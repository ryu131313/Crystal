using System.Net.Sockets;
using System.Net.WebSockets;

namespace WebRelay;

/// <summary>
/// One browser tab: a WebSocket bridged to its own upstream TCP connection to the Crystal server.
/// Crystal frame: | UInt16 total length (incl. header) | Int16 packet id | payload |, little-endian.
///   server -> browser: the TCP stream is split on the length prefix (as StressClient's BotSocket) and each packet is
///                      sent as ONE binary WS message, so the browser can skip ids it has not ported.
///   browser -> server: each WS message must be exactly one well-formed frame with a known ClientPacketIds id,
///                      otherwise the session is closed (the server would IP-block a malformed sender for 24 h).
///                      C.ClientVersion is the one frame rewritten: its hash becomes the MD5 of --version-path,
///                      so the browser never has to (and cannot) supply a client hash.
/// Packet bytes are otherwise moved untouched. Each pump is the only writer of its side, so no locks.
/// </summary>
internal sealed class RelaySession
{
    private const int HeaderSize = 4;
    private const int MaxFrame = ushort.MaxValue;
    private static readonly short MaxClientPacketId = Enum.GetValues<ClientPacketIds>().Max(v => (short)v);

    private readonly int _id;
    private readonly WebSocket _ws;
    private readonly RelayOptions _options;
    private readonly byte[]? _versionHash;

    public RelaySession(int id, WebSocket ws, RelayOptions options, byte[]? versionHash)
    {
        _id = id;
        _ws = ws;
        _options = options;
        _versionHash = versionHash;
    }

    public async Task<string> RunAsync(CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        using var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 64 * 1024 };
        string reason;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(_options.UpstreamHost, _options.UpstreamPort, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CloseWsAsync(WebSocketCloseStatus.EndpointUnavailable, "game server unavailable").ConfigureAwait(false);
            return "upstream connect failed: " + ex.Message;
        }

        var stream = tcp.GetStream();
        var toBrowser = PumpServerToBrowserAsync(stream, cts.Token);
        var toServer = PumpBrowserToServerAsync(stream, cts.Token);
        var first = await Task.WhenAny(toBrowser, toServer).ConfigureAwait(false);
        reason = first.IsFaulted ? first.Exception!.InnerException!.Message : await first.ConfigureAwait(false);
        cts.Cancel();
        try { tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
        await CloseWsAsync(WebSocketCloseStatus.NormalClosure, Trim(reason)).ConfigureAwait(false);
        try { await Task.WhenAll(toBrowser, toServer).ConfigureAwait(false); } catch { /* teardown */ }
        _ws.Dispose();
        return reason;
    }

    private async Task<string> PumpServerToBrowserAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[2 * MaxFrame + 2]; // > 2 frames: a compacted partial frame always fits
        int filled = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                if (read == 0) return "server closed the connection";
                filled += read;

                int offset = 0;
                while (filled - offset >= HeaderSize)
                {
                    int length = BitConverter.ToUInt16(buffer, offset);
                    if (length < HeaderSize) return $"bad server frame length {length}";
                    if (filled - offset < length) break;
                    await _ws.SendAsync(buffer.AsMemory(offset, length), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    offset += length;
                }
                if (offset > 0)
                {
                    Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                    filled -= offset;
                }
            }
            return "cancelled";
        }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or ObjectDisposedException)
        {
            return "server->browser: " + (ex.InnerException?.Message ?? ex.Message);
        }
    }

    private async Task<string> PumpBrowserToServerAsync(NetworkStream stream, CancellationToken ct)
    {
        var message = new byte[MaxFrame];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (count == message.Length) return "browser message too large";
                    r = await _ws.ReceiveAsync(message.AsMemory(count), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return "browser closed";
                    if (r.MessageType != WebSocketMessageType.Binary) return "text frames are not accepted";
                    count += r.Count;
                } while (!r.EndOfMessage);

                if (count < HeaderSize) return "browser frame too short";
                int length = BitConverter.ToUInt16(message, 0);
                short id = BitConverter.ToInt16(message, 2);
                if (length != count) return $"browser frame length {length} != message {count}";
                if (id < 0 || id > MaxClientPacketId) return $"unknown client packet id {id}";

                if (id == (short)ClientPacketIds.ClientVersion && _versionHash != null)
                {
                    await stream.WriteAsync(ClientVersionFrame(_versionHash), ct).ConfigureAwait(false);
                    continue;
                }
                await stream.WriteAsync(message.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            return "cancelled";
        }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or ObjectDisposedException)
        {
            return "browser->server: " + (ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>C.ClientVersion { byte[] VersionHash } = int32 count + bytes, built with the real codec.</summary>
    private static byte[] ClientVersionFrame(byte[] hash) =>
        new ClientPackets.ClientVersion { VersionHash = hash }.GetPacketBytes().ToArray();

    private async Task CloseWsAsync(WebSocketCloseStatus status, string description)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(status, description, t.Token).ConfigureAwait(false);
            }
        }
        catch { /* already gone */ }
    }

    /// <summary>A close reason is at most 123 UTF-8 bytes.</summary>
    private static string Trim(string s) => s.Length > 100 ? s[..100] : s;

    public override string ToString() => $"session {_id}";
}
