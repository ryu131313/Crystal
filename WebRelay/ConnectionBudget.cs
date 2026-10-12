namespace WebRelay;

/// <summary>
/// Per-origin connection budget — the relay's own re-application of the per-IP
/// defences the browser would otherwise share one upstream address through
/// (WebClient.md §2.1 "Connection budget" / §4 "Relay as attack surface").
/// Fails closed: the check and the claim are one atomic operation.
/// </summary>
/// <remarks>
/// One global lock rather than per-IP locks plus a lock-free total. Claims happen
/// once per browser connection — serially, from the accept loop — and releases once
/// per session end, so contention is nil at the configured scale (≤
/// MaxConnectionsTotal live sessions), and pruning must be atomic with claiming: an
/// entry removed from the dictionary while a concurrent claim was completing on it
/// would orphan that claim outside its per-IP cap.
///
/// Entries are pruned on release (nothing pinned, no in-window connect attempt left)
/// and swept periodically, so <c>_byIp</c> no longer grows one <c>IpBudget</c> per
/// distinct source IP for the process lifetime — harmless on loopback, unbounded once
/// the relay is public.
/// </remarks>
public sealed class ConnectionBudget
{
    public static readonly TimeSpan DefaultConnectWindow = TimeSpan.FromMinutes(1);

    private readonly int _maxPerIp;
    private readonly int _maxTotal;
    private readonly int _perMinute;
    private readonly TimeSpan _connectWindow;
    private readonly object _lock = new();
    private readonly Dictionary<string, IpBudget> _byIp = new(StringComparer.Ordinal);
    private int _total;

    public ConnectionBudget(int maxPerIp, int maxTotal, int connectsPerMinutePerIp)
        : this(maxPerIp, maxTotal, connectsPerMinutePerIp, DefaultConnectWindow) { }

    /// <summary>Test constructor: the per-minute window is adjustable so the eviction
    /// path can be exercised without waiting out a real minute.</summary>
    internal ConnectionBudget(int maxPerIp, int maxTotal, int connectsPerMinutePerIp, TimeSpan connectWindow)
    {
        _maxPerIp = maxPerIp;
        _maxTotal = maxTotal;
        _perMinute = connectsPerMinutePerIp;
        _connectWindow = connectWindow;
    }

    /// <summary>Live budget entries (internal: selftest assertions).</summary>
    internal int TrackedIpsForTesting { get { lock (_lock) return _byIp.Count; } }

    /// <summary>Sum of live claims (internal: selftest assertions).</summary>
    internal int ClaimedTotalForTesting { get { lock (_lock) return _total; } }

    public string? TryClaim(string ip)
    {
        ip = IpKey.Normalize(ip); // S2: an IPv6 client is budgeted by its /64
        lock (_lock)
        {
            if (_total >= _maxTotal)
                return $"relay at total capacity ({_maxTotal})";
            if (!_byIp.TryGetValue(ip, out IpBudget? budget))
            {
                budget = new IpBudget();
                _byIp[ip] = budget;
            }
            string? reason = budget.TryClaim(_maxPerIp, _perMinute, _connectWindow);
            if (reason != null)
                return reason;
            _total++;
            return null;
        }
    }

    public void Release(string ip)
    {
        ip = IpKey.Normalize(ip);
        lock (_lock)
        {
            _total--;
            if (!_byIp.TryGetValue(ip, out IpBudget? budget))
                return; // already pruned; exactly one release per successful claim
            budget.Release();
            // An expired timestamp would be dropped by the next claim anyway, so it
            // does not keep the entry alive — drop it here and prune when nothing
            // in-window remains. Refused claims never enqueue a timestamp, so the
            // per-minute budget of a live IP is untouched by this.
            budget.DropExpired(DateTime.UtcNow.Subtract(_connectWindow));
            if (budget.IsIdle)
                _byIp.Remove(ip);
        }
    }

    /// <summary>Drop expired connect-history entries and prune IPs that hold nothing.
    /// Called periodically by the server so an IP that stops connecting (rather than
    /// being released) cannot leave a lingering entry behind.</summary>
    public void PruneExpired()
    {
        lock (_lock)
        {
            DateTime cutoff = DateTime.UtcNow.Subtract(_connectWindow);
            List<string>? dead = null;
            foreach (KeyValuePair<string, IpBudget> entry in _byIp)
            {
                entry.Value.DropExpired(cutoff);
                if (entry.Value.IsIdle)
                    (dead ??= new List<string>()).Add(entry.Key);
            }
            if (dead != null)
                foreach (string ip in dead)
                    _byIp.Remove(ip);
        }
    }

    private sealed class IpBudget
    {
        private int _concurrent;
        private readonly Queue<DateTime> _connectTimes = new();

        /// <summary>Caller holds the budget-wide lock.</summary>
        public string? TryClaim(int maxConcurrent, int perMinute, TimeSpan window)
        {
            if (_concurrent >= maxConcurrent)
                return $"too many concurrent connections from your address ({maxConcurrent} max)";
            DateTime cutoff = DateTime.UtcNow.Subtract(window);
            DropExpired(cutoff);
            if (_connectTimes.Count >= perMinute)
                return $"too many connection attempts per minute from your address ({perMinute} max)";
            _connectTimes.Enqueue(DateTime.UtcNow);
            _concurrent++;
            return null;
        }

        public void Release() => _concurrent--;

        /// <summary>Expired entries are irrelevant to the rate limit (the next claim
        /// would discard them) — they do not keep the IP in the dictionary.</summary>
        public void DropExpired(DateTime cutoff)
        {
            while (_connectTimes.Count > 0 && _connectTimes.Peek() < cutoff) _connectTimes.Dequeue();
        }

        /// <summary>Nothing pinned and no in-window connect attempt — safe to prune.</summary>
        public bool IsIdle => _concurrent == 0 && _connectTimes.Count == 0;
    }
}
