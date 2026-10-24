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
/// Packet bytes are otherwise moved untouched. Each pump is the only writer of its side, except that a public relay
/// (<see cref="PlayerGuard"/>) may answer an over-quota C.NewAccount / C.NewCharacter itself: browser sends take _send.
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
    private readonly string _ip;
    private readonly PlayerGuard? _guard;
    private readonly SemaphoreSlim _send = new(1, 1);
    // Public relay (PlayerGuard): this session's login in flight (0/1) and the slots its forwarded creation requests hold,
    // in request order (the server answers each connection in order). Both pumps touch them.
    private int _loginPending;
    private readonly System.Collections.Concurrent.ConcurrentQueue<DateTime> _accountSlots = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<DateTime> _characterSlots = new();

    public RelaySession(int id, WebSocket ws, RelayOptions options, byte[]? versionHash, string ip, PlayerGuard? guard)
    {
        _id = id;
        _ws = ws;
        _options = options;
        _versionHash = versionHash;
        _ip = ip;
        _guard = guard;
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
        // An unanswered login stops counting; unanswered creation slots stay taken until they expire (an hour).
        if (_guard != null && Interlocked.Exchange(ref _loginPending, 0) == 1) _guard.EndLogin(_ip);
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
                    await SendToBrowserAsync(buffer.AsMemory(offset, length), ct).ConfigureAwait(false);
                    if (_guard != null && ObserveServerFrame(buffer, offset, length)) return "too many failed logins";
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

                if (_guard != null && id == (short)ClientPacketIds.NewAccount)
                {
                    if (!_guard.TryReserveAccount(_ip, out var slot))
                    {
                        await SendToBrowserAsync(new ServerPackets.NewAccount { Result = 0 }.GetPacketBytes().ToArray(), ct).ConfigureAwait(false);
                        continue;
                    }
                    _accountSlots.Enqueue(slot);
                }
                if (_guard != null && id == (short)ClientPacketIds.NewCharacter)
                {
                    if (!_guard.TryReserveCharacter(_ip, out var slot))
                    {
                        await SendToBrowserAsync(new ServerPackets.NewCharacter { Result = 0 }.GetPacketBytes().ToArray(), ct).ConfigureAwait(false);
                        continue;
                    }
                    _characterSlots.Enqueue(slot);
                }
                if (_guard != null && id == (short)ClientPacketIds.Login)
                {
                    // One login at a time per session, as the server does (MirConnection.LoginPending drops the rest unanswered).
                    if (Interlocked.CompareExchange(ref _loginPending, 1, 0) != 0) continue;
                    if (!_guard.TryBeginLogin(_ip))
                    {
                        Volatile.Write(ref _loginPending, 0);
                        return "too many failed logins";
                    }
                }
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

    /// <summary>
    /// Public relay: feeds the server's account answers to <see cref="PlayerGuard"/> (packet id + first payload byte only).
    /// True when this address just hit the failed-login limit.
    /// </summary>
    private bool ObserveServerFrame(byte[] frame, int offset, int length)
    {
        short id = BitConverter.ToInt16(frame, offset + 2);
        byte result = length > HeaderSize ? frame[offset + HeaderSize] : (byte)255;
        if ((id == (short)ServerPacketIds.Login || id == (short)ServerPacketIds.LoginSuccess || id == (short)ServerPacketIds.LoginBanned)
            && Interlocked.Exchange(ref _loginPending, 0) == 1)
            _guard!.EndLogin(_ip);
        if (id == (short)ServerPacketIds.Login && result is 3 or 4) return _guard!.FailedLogin(_ip); // no account / wrong password
        if (id == (short)ServerPacketIds.NewAccount && _accountSlots.TryDequeue(out var account))
        {
            if (result != 8) _guard!.ReleaseAccount(_ip, account); // refused: the slot is given back
        }
        else if (id == (short)ServerPacketIds.NewCharacterSuccess) _characterSlots.TryDequeue(out _);
        else if (id == (short)ServerPacketIds.NewCharacter && _characterSlots.TryDequeue(out var character))
            _guard!.ReleaseCharacter(_ip, character); // S.NewCharacter is only sent for a refusal
        return false;
    }

    private async Task SendToBrowserAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
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
