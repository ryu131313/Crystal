namespace WebRelay;

/// <summary>
/// The server's per-IP account protections, re-applied per real player address.
///
/// Every relay session reaches the Crystal server from the relay's own address, so on a public relay the server must run
/// with <c>[LoadTest] ExemptLoopback=True</c> (otherwise MaxIP caps ALL web players at 5, and one player's failed logins or
/// fourth account IP-blocks every web player). This class puts those limits back, keyed by <see cref="IpKey"/>:
///   - failed logins (S.Login Result 3 "no account" / 4 "wrong password", Envir.CountFailedLogin): after
///     <see cref="FailedLoginLimit"/> in a minute the address cannot open a new session for a minute;
///   - new accounts (S.NewAccount Result 8) and new characters (S.NewCharacterSuccess) per hour, the server's
///     "AccountsMade.Count &gt; 2" / "CharactersMade.Count &gt; 4" rules: an over-quota request is answered by the relay
///     with the server's own refusal (Result 0, "disabled") and never forwarded.
/// Slots are taken when a request is forwarded, not when the server answers (codebase review 2026-10-24 #10: a burst of
/// pipelined requests, or logins over several sessions, all passed the check before the first answer counted): a
/// forwarded C.NewAccount / C.NewCharacter reserves a creation slot that a refusal gives back, and a forwarded C.Login
/// counts as in flight, so failures plus logins in flight never pass <see cref="FailedLoginLimit"/>.
/// The relay only reads packet ids and the first payload byte; it still never decodes a packet.
/// Called from the session pumps and the accept path (pool threads): one lock, held for a few list operations.
/// </summary>
internal sealed class PlayerGuard
{
    public const int FailedLoginLimit = 5;
    private static readonly TimeSpan FailedLoginWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CreationWindow = TimeSpan.FromHours(1);

    private readonly int _accountsPerHour;
    private readonly int _charactersPerHour;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byIp = new(StringComparer.Ordinal);

    public PlayerGuard(int accountsPerHour, int charactersPerHour)
    {
        _accountsPerHour = accountsPerHour;
        _charactersPerHour = charactersPerHour;
    }

    /// <summary>Null when the address may open a session, else the refusal text.</summary>
    public string? CheckBlocked(string ip)
    {
        lock (_lock)
        {
            return _byIp.TryGetValue(IpKey.Normalize(ip), out var e) && e.BlockedUntil > DateTime.UtcNow
                ? "too many failed logins from your address, try again in a minute"
                : null;
        }
    }

    /// <summary>Counts a failed login; true when the address is now blocked (the session should close).</summary>
    public bool FailedLogin(string ip)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var e = Get(ip);
            Drop(e.FailedLogins, now - FailedLoginWindow);
            e.FailedLogins.Add(now);
            if (e.FailedLogins.Count < FailedLoginLimit) return false;
            e.FailedLogins.Clear();
            e.BlockedUntil = now + BlockTime;
            return true;
        }
    }

    /// <summary>Takes an account slot for a request about to be forwarded; false when the address is over quota.</summary>
    public bool TryReserveAccount(string ip, out DateTime stamp) => TryReserve(ip, e => e.Accounts, _accountsPerHour, out stamp);
    public bool TryReserveCharacter(string ip, out DateTime stamp) => TryReserve(ip, e => e.Characters, _charactersPerHour, out stamp);
    /// <summary>The server refused the request: its slot is given back.</summary>
    public void ReleaseAccount(string ip, DateTime stamp) => Release(ip, e => e.Accounts, stamp);
    public void ReleaseCharacter(string ip, DateTime stamp) => Release(ip, e => e.Characters, stamp);

    /// <summary>A C.Login about to be forwarded; false when the address is blocked or failures + logins in flight reach the limit.</summary>
    public bool TryBeginLogin(string ip)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var e = Get(ip);
            if (e.BlockedUntil > now) return false;
            Drop(e.FailedLogins, now - FailedLoginWindow);
            if (e.FailedLogins.Count + e.PendingLogins >= FailedLoginLimit) return false;
            e.PendingLogins++;
            return true;
        }
    }

    /// <summary>The server answered a forwarded login (or the session ended before it did).</summary>
    public void EndLogin(string ip)
    {
        lock (_lock)
        {
            if (_byIp.TryGetValue(IpKey.Normalize(ip), out var e) && e.PendingLogins > 0) e.PendingLogins--;
        }
    }

    /// <summary>Drops expired history and idle addresses (called once a minute).</summary>
    public void PruneExpired()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            List<string>? dead = null;
            foreach (var (key, e) in _byIp)
            {
                Drop(e.FailedLogins, now - FailedLoginWindow);
                Drop(e.Accounts, now - CreationWindow);
                Drop(e.Characters, now - CreationWindow);
                if (e.FailedLogins.Count == 0 && e.Accounts.Count == 0 && e.Characters.Count == 0 && e.PendingLogins == 0 && e.BlockedUntil <= now)
                    (dead ??= new()).Add(key);
            }
            if (dead != null) foreach (var key in dead) _byIp.Remove(key);
        }
    }

    private bool TryReserve(string ip, Func<Entry, List<DateTime>> list, int max, out DateTime stamp)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var times = list(Get(ip));
            Drop(times, now - CreationWindow);
            stamp = now;
            if (times.Count >= max) return false;
            times.Add(now);
            return true;
        }
    }

    private void Release(string ip, Func<Entry, List<DateTime>> list, DateTime stamp)
    {
        lock (_lock)
        {
            if (_byIp.TryGetValue(IpKey.Normalize(ip), out var e)) list(e).Remove(stamp);
        }
    }

    private Entry Get(string ip)
    {
        ip = IpKey.Normalize(ip);
        if (!_byIp.TryGetValue(ip, out var e)) _byIp[ip] = e = new Entry();
        return e;
    }

    private static void Drop(List<DateTime> times, DateTime cutoff) => times.RemoveAll(t => t < cutoff);

    private sealed class Entry
    {
        public readonly List<DateTime> FailedLogins = new();
        public readonly List<DateTime> Accounts = new();
        public readonly List<DateTime> Characters = new();
        public DateTime BlockedUntil;
        public int PendingLogins;
    }
}
