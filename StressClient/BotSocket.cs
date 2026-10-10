using System.Collections.Concurrent;
using System.Net.Sockets;

namespace StressClient;

/// <summary>
/// One bot's TCP connection. The receive loop splits the stream into frames on the 2-byte length prefix and
/// decodes each frame with the real Shared codec (<see cref="Packet.ReceivePacket"/>); decoded packets are queued
/// for the bot loop. Sending serialises packets with <see cref="Packet.GetPacketBytes"/>. No packet bytes are
/// built by hand here.
/// </summary>
internal sealed class BotSocket : IDisposable
{
    private const int HeaderSize = 4;            // | UInt16 total length | Int16 packet id |

    private readonly Metrics _m;
    private TcpClient _client;
    private NetworkStream _stream;
    private int _closed; // 0 open, 1 closed

    public readonly ConcurrentQueue<Packet> Received = new();
    public string CloseReason { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public BotSocket(Metrics metrics) => _m = metrics;

    public enum ConnectResult { Ok, Refused, Timeout, Failed }

    public async Task<ConnectResult> ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        _client = new TcpClient { NoDelay = true, ReceiveBufferSize = 64 * 1024, SendBufferSize = 16 * 1024 };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await _client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            _stream = _client.GetStream();
            return ConnectResult.Ok;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
        {
            Close("connect refused");
            return ConnectResult.Refused;
        }
        catch (OperationCanceledException)
        {
            Close(ct.IsCancellationRequested ? "cancelled" : "connect timeout");
            return ConnectResult.Timeout;
        }
        catch (Exception ex)
        {
            Close("connect failed: " + ex.GetType().Name);
            return ConnectResult.Failed;
        }
    }

    public void StartReceiving(CancellationToken ct) => _ = Task.Run(() => ReceiveLoopAsync(ct));

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[128 * 1024]; // > the largest frame (64 KB), so a compacted partial frame always fits
        int filled = 0;
        try
        {
            while (!IsClosed && !ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    Close("server closed the connection");
                    return;
                }
                filled += read;
                Metrics.Add(ref _m.BytesReceived, read);

                int offset = 0;
                while (filled - offset >= HeaderSize)
                {
                    int length = BitConverter.ToUInt16(buffer, offset);
                    if (length < HeaderSize)
                    {
                        Metrics.Inc(ref _m.DecodeErrors);
                        Close($"bad frame length {length}");
                        return;
                    }
                    if (filled - offset < length) break;

                    Decode(buffer.AsSpan(offset, length).ToArray());
                    offset += length;
                }

                if (offset > 0)
                {
                    Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                    filled -= offset;
                }
            }
        }
        catch (OperationCanceledException) { Close("cancelled"); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close("receive: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    private void Decode(byte[] frame)
    {
        Packet p;
        try
        {
            p = Packet.ReceivePacket(frame, out _);
        }
        catch (InvalidDataException)
        {
            // A server packet the Shared codec could not read: a codec/version mismatch, which the client would crash on.
            Metrics.Inc(ref _m.DecodeErrors);
            return;
        }

        if (p == null)
        {
            Metrics.Inc(ref _m.UnknownPackets); // id without a GetServerPacket case: the real client would drop its whole buffer
            return;
        }
        Metrics.Inc(ref _m.PacketsReceived);
        if (p is ServerPackets.KeepAlive ka)
        {
            // Measured on arrival (not on the next bot tick) so the RTT is the server's, not the bot loop's.
            long rtt = MonoClock.Now - ka.Time;
            if (rtt >= 0 && rtt < 60_000)
            {
                Metrics.Add(ref _m.KeepAliveRttTotalMs, rtt);
                Metrics.Inc(ref _m.KeepAliveRttCount);
            }
            return;
        }
        Received.Enqueue(p);
    }

    /// <summary>Writes all packets in one socket write (the server's MaxPacket counts receives, not packets).</summary>
    public async Task<bool> SendAsync(List<Packet> packets, CancellationToken ct)
    {
        if (IsClosed || packets.Count == 0) return false;
        var bytes = new List<byte>(64 * packets.Count);
        foreach (var p in packets) bytes.AddRange(p.GetPacketBytes());
        try
        {
            await _stream.WriteAsync(bytes.ToArray(), ct).ConfigureAwait(false);
            Metrics.Add(ref _m.PacketsSent, packets.Count);
            Metrics.Add(ref _m.BytesSent, bytes.Count);
            Metrics.Inc(ref _m.Sends);
            return true;
        }
        catch (OperationCanceledException) { Close("cancelled"); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Close("send: " + (ex.InnerException?.Message ?? ex.Message));
        }
        return false;
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason = reason;
        try { _client?.Client?.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
        try { _client?.Close(); } catch { /* already gone */ }
    }

    public void Dispose() => Close("disposed");
}
