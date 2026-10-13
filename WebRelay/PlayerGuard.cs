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

    public bool CanCreateAccount(string ip) => UnderQuota(ip, e => e.Accounts, _accountsPerHour);
    public bool CanCreateCharacter(string ip) => UnderQuota(ip, e => e.Characters, _charactersPerHour);
    public void AccountCreated(string ip) => Record(ip, e => e.Accounts);
    public void CharacterCreated(string ip) => Record(ip, e => e.Characters);

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
                if (e.FailedLogins.Count == 0 && e.Accounts.Count == 0 && e.Characters.Count == 0 && e.BlockedUntil <= now)
                    (dead ??= new()).Add(key);
            }
            if (dead != null) foreach (var key in dead) _byIp.Remove(key);
        }
    }

    private bool UnderQuota(string ip, Func<Entry, List<DateTime>> list, int max)
    {
        lock (_lock)
        {
            if (!_byIp.TryGetValue(IpKey.Normalize(ip), out var e)) return true;
            var times = list(e);
            Drop(times, DateTime.UtcNow - CreationWindow);
            return times.Count < max;
        }
    }

    private void Record(string ip, Func<Entry, List<DateTime>> list)
    {
        lock (_lock) list(Get(ip)).Add(DateTime.UtcNow);
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
    }
}
