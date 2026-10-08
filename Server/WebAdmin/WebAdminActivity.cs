using Server.MirObjects;

namespace Server.WebAdmin
{
    /// <summary>One dashboard activity-feed entry. Seq is monotonically increasing (process-local).</summary>
    public sealed class ActivityEvent
    {
        public long Seq { get; set; }
        public DateTime Utc { get; set; }

        /// <summary>login | logout | login-failed | world | admin</summary>
        public string Kind { get; set; }

        public string Text { get; set; }

        /// <summary>Character name (login/logout), null otherwise.</summary>
        public string Character { get; set; }

        /// <summary>Game account id (login/logout/login-failed) or the admin account (admin), null otherwise.</summary>
        public string Account { get; set; }
    }

    /// <summary>Fixed-capacity ring of activity events with sequence ids; safe for concurrent writers/readers.</summary>
    public sealed class WebAdminActivityRing
    {
        private readonly ActivityEvent[] _items;
        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;
        private long _lastSeq;
        private int _start;
        private int _count;

        public WebAdminActivityRing(int capacity, Func<DateTime> clock = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new ActivityEvent[capacity];
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Capacity => _items.Length;

        public long LastSeq
        {
            get
            {
                lock (_gate) return _lastSeq;
            }
        }

        public int Count
        {
            get
            {
                lock (_gate) return _count;
            }
        }

        /// <summary>Appends an event (Utc = clock now unless <paramref name="utc"/> is given). Returns its Seq.</summary>
        public long Add(string kind, string text, string character = null, string account = null, DateTime? utc = null)
        {
            lock (_gate)
            {
                var e = new ActivityEvent
                {
                    Seq = ++_lastSeq,
                    Utc = utc.HasValue ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc) : _clock(),
                    Kind = string.IsNullOrWhiteSpace(kind) ? "admin" : kind,
                    Text = text ?? string.Empty,
                    Character = string.IsNullOrEmpty(character) ? null : character,
                    Account = string.IsNullOrEmpty(account) ? null : account,
                };

                if (_count < _items.Length)
                {
                    _items[(_start + _count) % _items.Length] = e;
                    _count++;
                }
                else
                {
                    _items[_start] = e;
                    _start = (_start + 1) % _items.Length;
                }

                return e.Seq;
            }
        }

        /// <summary>
        /// afterSeq &lt;= 0: the newest <paramref name="max"/> events (ascending). afterSeq &gt; 0: events with Seq &gt; afterSeq, oldest first, up
        /// to max (an afterSeq newer than anything known — e.g. after a process restart — is treated as 0).
        /// last = the Seq of the last returned event, or the ring's latest Seq when nothing was returned.
        /// </summary>
        public (IReadOnlyList<ActivityEvent> events, long last) Get(long afterSeq, int max = 200)
        {
            if (max < 1) max = 1;

            lock (_gate)
            {
                if (afterSeq > _lastSeq) afterSeq = 0;

                var result = new List<ActivityEvent>(Math.Min(max, _count));

                if (afterSeq <= 0)
                {
                    var take = Math.Min(max, _count);
                    for (var i = _count - take; i < _count; i++)
                        result.Add(_items[(_start + i) % _items.Length]);
                }
                else
                {
                    for (var i = 0; i < _count && result.Count < max; i++)
                    {
                        var e = _items[(_start + i) % _items.Length];
                        if (e.Seq > afterSeq) result.Add(e);
                    }
                }

                var last = result.Count > 0 ? result[result.Count - 1].Seq : _lastSeq;
                return (result, last);
            }
        }
    }

    /// <summary>
    /// Remembers which audit entries were already turned into activity events: entries newer than <see cref="Since"/>, or at exactly
    /// that timestamp but with an unseen id, are new. Pure (no I/O); unit tested.
    /// </summary>
    public sealed class WebAdminAuditCursor
    {
        private readonly HashSet<string> _idsAtSince = new HashSet<string>(StringComparer.Ordinal);

        public WebAdminAuditCursor(DateTime sinceUtc)
        {
            Since = sinceUtc;
        }

        public DateTime Since { get; private set; }

        /// <summary>Returns the entries not seen before, oldest first, and advances the cursor past them.</summary>
        public IReadOnlyList<WebAdminAuditEntry> TakeNew(IEnumerable<WebAdminAuditEntry> entries)
        {
            var fresh = new List<WebAdminAuditEntry>();
            if (entries == null) return fresh;

            foreach (var e in entries)
            {
                if (e == null) continue;
                if (e.Ts < Since) continue;
                if (e.Ts == Since && e.Id != null && _idsAtSince.Contains(e.Id)) continue;
                fresh.Add(e);
            }

            fresh.Sort((a, b) => a.Ts.CompareTo(b.Ts));

            // De-duplicate ids within this batch (ring + file reads may both return an entry).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            fresh.RemoveAll(e => e.Id != null && !seen.Add(e.Id));

            foreach (var e in fresh)
            {
                if (e.Ts > Since)
                {
                    Since = e.Ts;
                    _idsAtSince.Clear();
                }

                if (e.Id != null) _idsAtSince.Add(e.Id);
            }

            return fresh;
        }

        /// <summary>Feed text for an audit entry: "admin: action target (reason)".</summary>
        public static string Describe(WebAdminAuditEntry e)
        {
            if (e == null) return string.Empty;

            var text = (string.IsNullOrWhiteSpace(e.Admin) ? "system" : e.Admin) + ": " + (e.Action ?? "unknown");
            if (!string.IsNullOrWhiteSpace(e.Target)) text += " " + e.Target;
            if (!string.IsNullOrWhiteSpace(e.Reason)) text += " (" + e.Reason + ")";
            return text;
        }
    }

    /// <summary>
    /// Dashboard activity feed: logins/logouts (PlayerEntered/PlayerLeft), failed game logins (LoginAttempt), world start/stop and
    /// admin actions (new audit records, picked up by <see cref="PollAudit"/> on the status sampler tick). Ring of 2000.
    /// </summary>
    public static class WebAdminActivity
    {
        public const int Capacity = 2000;

        internal static WebAdminActivityRing Ring { get; } = new WebAdminActivityRing(Capacity);

        private static readonly object AuditGate = new object();
        private static WebAdminAuditCursor _auditCursor;
        private static int _lastAuditCount = -1;
        private static string _lastAuditNewestId;
        private static int _hooked;

        /// <summary>Subscribes the WebAdminHooks events once (idempotent). Handlers are O(1) and never throw.</summary>
        internal static void EnsureHooks()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;

            lock (AuditGate)
                _auditCursor ??= new WebAdminAuditCursor(DateTime.UtcNow);

            WebAdminHooks.PlayerEntered += OnPlayerEntered;
            WebAdminHooks.PlayerLeft += OnPlayerLeft;
            WebAdminHooks.LoginAttempt += OnLoginAttempt;
            WebAdminHooks.WorldStarted += OnWorldStarted;
            WebAdminHooks.WorldStopped += OnWorldStopped;
        }

        public static (IReadOnlyList<ActivityEvent> events, long last) Get(long afterSeq, int max = 200) => Ring.Get(afterSeq, max);

        public static void Add(string kind, string text, string character = null, string account = null)
        {
            try
            {
                Ring.Add(kind, text, character, account);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Activity feed append failed");
            }
        }

        private static void OnPlayerEntered(PlayerObject player)
        {
            if (player == null) return;
            var (name, account) = Describe(player);
            Add("login", name + " entered the game", name, account);
        }

        private static void OnPlayerLeft(PlayerObject player)
        {
            if (player == null) return;
            var (name, account) = Describe(player);
            Add("logout", name + " left the game", name, account);
        }

        private static (string name, string account) Describe(PlayerObject player)
        {
            string name = null, account = null;

            try
            {
                name = player.Info?.Name ?? player.Name;
                account = player.Info?.AccountInfo?.AccountID;
            }
            catch (Exception)
            {
                // a half-initialised player: keep whatever was read
            }

            return (string.IsNullOrEmpty(name) ? "(unknown)" : name, account);
        }

        private static void OnLoginAttempt(string accountId, string ip, string result)
        {
            if (string.Equals(result, "ok", StringComparison.Ordinal)) return;
            Add("login-failed", LoginFailureText(accountId, ip, result), null, accountId);
        }

        /// <summary>"Login refused for 'bob' from 1.2.3.4: wrong password" (pure; tested).</summary>
        internal static string LoginFailureText(string accountId, string ip, string result)
        {
            string why;
            var r = result ?? string.Empty;

            if (r == "bad-password") why = "wrong password";
            else if (r == "banned") why = "account banned";
            else if (r == "no-account") why = "no such account";
            else if (r.StartsWith("refused:", StringComparison.Ordinal)) why = "refused (" + r.Substring("refused:".Length).Trim() + ")";
            else why = string.IsNullOrEmpty(r) ? "failed" : r;

            var who = string.IsNullOrEmpty(accountId) ? "(blank)" : "'" + accountId + "'";
            var from = string.IsNullOrEmpty(ip) ? string.Empty : " from " + ip;
            return "Login failed for " + who + from + ": " + why;
        }

        private static void OnWorldStarted()
        {
            Add("world", "World started");
        }

        private static void OnWorldStopped()
        {
            Add("world", "World stopped");
        }

        /// <summary>
        /// Turns audit records written since the last call into "admin" events. Called from the status sampler tick (thread pool).
        /// Change detection is the ring's count plus its newest id (works once the ring is full). Every record the cursor has not
        /// seen is still in the in-memory ring (the cursor starts at "now"), so the whole ring is read with <c>days: 0</c> and
        /// limit = ring size: that never falls through to the JSONL files and never truncates a burst (up to the ring's capacity).
        /// </summary>
        internal static void PollAudit()
        {
            try
            {
                lock (AuditGate)
                {
                    _auditCursor ??= new WebAdminAuditCursor(DateTime.UtcNow);

                    var log = WebAdminAudit.Log;
                    var count = log.Count;
                    if (count == 0) return; // Query would fall through to the files

                    var newest = WebAdminAudit.Query(1, days: 0);
                    var newestId = newest.Count > 0 ? newest[0].Id : null;
                    if (count == _lastAuditCount && string.Equals(newestId, _lastAuditNewestId, StringComparison.Ordinal)) return;
                    _lastAuditCount = count;
                    _lastAuditNewestId = newestId;

                    var entries = WebAdminAudit.Query(Math.Max(count, log.Count), days: 0);
                    foreach (var e in _auditCursor.TakeNew(entries))
                        Ring.Add("admin", WebAdminAuditCursor.Describe(e), null, e.Admin, e.Ts);
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Activity feed audit poll failed");
            }
        }
    }
}
