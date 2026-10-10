namespace StressClient;

/// <summary>
/// Per-bot send limiter. The server counts socket receives per connection in 5 s windows
/// (MirConnection.ReceiveData: _dataCounter vs MaxPacket / [LoadTest] LoopbackMaxPacket) and blocks the IP for 24 h
/// when the count is exceeded. One bot flush = one socket write, so the bucket counts writes, not packets.
/// Single-threaded: only the owning bot's loop uses it.
/// </summary>
internal sealed class TokenBucket
{
    private readonly double _ratePerMs;
    private readonly double _capacity;
    private double _tokens;
    private long _last;

    /// <param name="serverMaxPacket">The server's limit per 5 s window.</param>
    /// <param name="fraction">Share of that limit this bot may use (0..1).</param>
    public TokenBucket(int serverMaxPacket, double fraction, long nowMs)
    {
        double perWindow = Math.Max(1, serverMaxPacket * fraction);
        _ratePerMs = perWindow / 5000.0;
        // A burst of at most a fifth of the window budget keeps any 5 s window under the limit.
        _capacity = Math.Max(1, perWindow / 5);
        _tokens = _capacity;
        _last = nowMs;
    }

    public bool TryTake(long nowMs)
    {
        if (nowMs > _last)
        {
            _tokens = Math.Min(_capacity, _tokens + (nowMs - _last) * _ratePerMs);
            _last = nowMs;
        }
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}
