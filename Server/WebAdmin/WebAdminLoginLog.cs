using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Server.WebAdmin
{
    /// <summary>One login attempt (game client or web panel).</summary>
    public sealed class WebAdminLoginEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("utc")]
        public DateTime Utc { get; set; }

        [JsonPropertyName("accountId")]
        public string AccountId { get; set; }

        [JsonPropertyName("ip")]
        public string Ip { get; set; }

        /// <summary>"ok", "bad-password", "banned", "no-account", "refused:&lt;reason&gt;" (game) or web results (see <see cref="WebAdminLoginLog"/>).</summary>
        [JsonPropertyName("result")]
        public string Result { get; set; }

        /// <summary>"game" (client login) or "web" (admin panel sign-in).</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }

        [JsonIgnore]
        public bool IsSuccess => string.Equals(Result, "ok", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Failed attempts grouped by IP (security overview).</summary>
    public sealed class WebAdminLoginFailureGroup
    {
        public string Ip { get; set; }
        public int Count { get; set; }
        public List<string> Accounts { get; set; } = new List<string>();
        public DateTime Last { get; set; }
    }

    /// <summary>
    /// Login log: in-memory ring (newest kept) + optional daily JSONL files (logins-yyyy-MM-dd.jsonl, UTF-8 without BOM).
    /// <see cref="Record"/> is cheap and safe to call from the game thread: file appends are done by a background flusher.
    /// </summary>
    public sealed class WebAdminLoginLogStore
    {
        public const int DefaultCapacity = 5000;

        private readonly LinkedList<WebAdminLoginEntry> _ring = new LinkedList<WebAdminLoginEntry>();
        private readonly object _gate = new object();
        private readonly object _fileGate = new object();
        private readonly ConcurrentQueue<WebAdminLoginEntry> _pending = new ConcurrentQueue<WebAdminLoginEntry>();
        private readonly Func<DateTime> _clock;
        private readonly string _nonce = WebAdminSessionStore.NewToken(8);
        private long _seq;
        private int _flushing;

        // Parsed JSONL files keyed by path; reused while the file's length and write time are unchanged (past days never change),
        // so a polled query does not re-parse every daily file. Entries unused for FileCacheIdle are dropped.
        private static readonly TimeSpan FileCacheIdle = TimeSpan.FromMinutes(10);
        private readonly Dictionary<string, CachedFile> _fileCache = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheGate = new object();

        private sealed class CachedFile
        {
            public long Length;
            public DateTime WriteUtc;
            public DateTime LastUsedUtc;
            public List<WebAdminLoginEntry> Entries;
        }

        public WebAdminLoginLogStore(string directory, int capacity = DefaultCapacity, Func<DateTime> clock = null)
        {
            Directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
            Capacity = Math.Max(10, capacity);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Folder for JSONL files; null = memory only.</summary>
        public string Directory { get; }

        public int Capacity { get; }

        /// <summary>When false, file appends happen synchronously inside <see cref="Record"/> (tests).</summary>
        internal bool BackgroundWrites { get; set; } = true;

        public int Count
        {
            get
            {
                lock (_gate) return _ring.Count;
            }
        }

        public WebAdminLoginEntry Record(string accountId, string ip, string result, string source = "game")
        {
            var entry = new WebAdminLoginEntry
            {
                Id = _nonce + "-" + Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture),
                Utc = DateTime.SpecifyKind(_clock(), DateTimeKind.Utc),
                AccountId = Clip(accountId, 64) ?? string.Empty,
                Ip = Clip(ip, 64) ?? string.Empty,
                Result = Clip(string.IsNullOrWhiteSpace(result) ? "unknown" : result.Trim(), 120),
                Source = string.IsNullOrWhiteSpace(source) ? "game" : source,
            };

            lock (_gate)
            {
                _ring.AddLast(entry);
                while (_ring.Count > Capacity) _ring.RemoveFirst();
            }

            if (Directory != null)
            {
                _pending.Enqueue(entry);
                if (BackgroundWrites)
                {
                    if (Interlocked.CompareExchange(ref _flushing, 1, 0) == 0)
                        Task.Run(FlushLoop);
                }
                else
                {
                    Flush();
                }
            }

            return entry;
        }

        /// <summary>Writes every queued entry to its daily file (thread-safe).</summary>
        public void Flush()
        {
            if (Directory == null) return;

            lock (_fileGate)
            {
                var byFile = new Dictionary<string, System.Text.StringBuilder>(StringComparer.OrdinalIgnoreCase);

                while (_pending.TryDequeue(out var e))
                {
                    var path = FilePath(e.Utc);
                    if (!byFile.TryGetValue(path, out var sb)) byFile[path] = sb = new System.Text.StringBuilder();
                    sb.Append(JsonSerializer.Serialize(e, WebAdminJson.Options)).Append('\n');
                }

                if (byFile.Count == 0) return;

                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    foreach (var pair in byFile)
                        File.AppendAllText(pair.Key, pair.Value.ToString(), WebAdminJson.Utf8NoBom);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Failed to write the web admin login log");
                }
            }
        }

        private void FlushLoop()
        {
            try
            {
                while (true)
                {
                    Flush();
                    Interlocked.Exchange(ref _flushing, 0);

                    // Something queued after the last drain but before the flag was released: take the flag back and drain again.
                    if (_pending.IsEmpty || Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _flushing, 0);
                WebAdminLog.Error(ex, "Login log flusher failed");
            }
        }

        internal string FilePath(DateTime utc)
        {
            return Path.Combine(Directory, "logins-" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        /// <summary>
        /// Newest first. <paramref name="q"/>: case-insensitive substring of account id or IP (an exact IP match also works);
        /// <paramref name="result"/>: null/"all", "ok", "failed" (anything but ok), or an exact result / prefix ("refused" matches "refused:x");
        /// <paramref name="since"/>: only entries at or after this UTC time. Reads the ring plus the JSONL files (when the ring may not cover the window).
        /// </summary>
        public IReadOnlyList<WebAdminLoginEntry> Query(string q, string result, DateTime since, int limit, string source = null)
        {
            if (limit <= 0) limit = 200;
            limit = Math.Min(limit, 20000);

            List<WebAdminLoginEntry> ringItems;
            lock (_gate) ringItems = _ring.Reverse().ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<WebAdminLoginEntry>();
            // The ring holds everything newer than its oldest entry, so when that entry predates the window the files add nothing.
            var ringCoversWindow = ringItems.Count > 0 && ringItems[ringItems.Count - 1].Utc <= since;

            foreach (var e in ringItems)
            {
                if (e.Id != null) seen.Add(e.Id);
                if (Matches(e, q, result, since, source)) results.Add(e);
            }

            if (Directory != null && results.Count < limit && !ringCoversWindow)
            {
                Flush();
                foreach (var e in ReadFiles(since))
                {
                    if (e.Id != null && !seen.Add(e.Id)) continue;
                    if (Matches(e, q, result, since, source)) results.Add(e);
                }
            }

            return results.OrderByDescending(e => e.Utc).Take(limit).ToList();
        }

        private IEnumerable<WebAdminLoginEntry> ReadFiles(DateTime since)
        {
            var now = _clock();
            var days = (int)Math.Ceiling(Math.Max(0, (now - since).TotalDays));
            days = Math.Min(days, 366);
            var list = new List<WebAdminLoginEntry>();

            var used = DateTime.UtcNow;

            for (var d = 0; d <= days; d++)
            {
                var path = FilePath(now.AddDays(-d));

                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists) continue;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Failed to read " + path);
                    continue;
                }

                var length = info.Length;
                var writeUtc = info.LastWriteTimeUtc;

                lock (_cacheGate)
                {
                    if (_fileCache.TryGetValue(path, out var hit) && hit.Length == length && hit.WriteUtc == writeUtc)
                    {
                        hit.LastUsedUtc = used;
                        list.AddRange(hit.Entries);
                        continue;
                    }
                }

                var entries = ParseFile(path, out var complete);
                list.AddRange(entries);

                if (complete)
                {
                    lock (_cacheGate)
                        _fileCache[path] = new CachedFile { Length = length, WriteUtc = writeUtc, LastUsedUtc = used, Entries = entries };
                }
            }

            lock (_cacheGate)
            {
                foreach (var stale in _fileCache.Where(p => used - p.Value.LastUsedUtc > FileCacheIdle).Select(p => p.Key).ToList())
                    _fileCache.Remove(stale);
            }

            return list;
        }

        private static List<WebAdminLoginEntry> ParseFile(string path, out bool complete)
        {
            var list = new List<WebAdminLoginEntry>();
            complete = false;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, WebAdminJson.Utf8NoBom))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var e = JsonSerializer.Deserialize<WebAdminLoginEntry>(line, WebAdminJson.Options);
                            if (e != null) list.Add(e);
                        }
                        catch (JsonException)
                        {
                            // skip a torn/corrupt line
                        }
                    }
                }

                complete = true;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Failed to read " + path);
            }

            return list;
        }

        internal static bool Matches(WebAdminLoginEntry e, string q, string result, DateTime since, string source)
        {
            if (e == null || e.Utc < since) return false;

            if (!string.IsNullOrWhiteSpace(source) && !string.Equals(source.Trim(), "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Source ?? "game", source.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!MatchesResult(e.Result, result)) return false;

            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                if (!Contains(e.AccountId, q) && !Contains(e.Ip, q)) return false;
            }

            return true;
        }

        internal static bool MatchesResult(string entryResult, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;

            filter = filter.Trim();
            var r = entryResult ?? string.Empty;

            if (filter.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
            if (filter.Equals("ok", StringComparison.OrdinalIgnoreCase)) return r.Equals("ok", StringComparison.OrdinalIgnoreCase);
            if (filter.Equals("failed", StringComparison.OrdinalIgnoreCase)) return !r.Equals("ok", StringComparison.OrdinalIgnoreCase);

            return r.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                   r.StartsWith(filter + ":", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Failed attempts grouped by IP, most failures first (ties: most recent first).</summary>
        public static List<WebAdminLoginFailureGroup> GroupFailuresByIp(IEnumerable<WebAdminLoginEntry> entries, int max = 50)
        {
            var groups = new Dictionary<string, WebAdminLoginFailureGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries ?? Enumerable.Empty<WebAdminLoginEntry>())
            {
                if (e == null || e.IsSuccess) continue;

                var ip = string.IsNullOrEmpty(e.Ip) ? "unknown" : e.Ip;
                if (!groups.TryGetValue(ip, out var g)) groups[ip] = g = new WebAdminLoginFailureGroup { Ip = ip, Last = e.Utc };

                g.Count++;
                if (e.Utc > g.Last) g.Last = e.Utc;
                if (!string.IsNullOrEmpty(e.AccountId) && !g.Accounts.Contains(e.AccountId, StringComparer.OrdinalIgnoreCase) && g.Accounts.Count < 20)
                    g.Accounts.Add(e.AccountId);
            }

            return groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.Last).Take(Math.Max(1, max)).ToList();
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Clip(string s, int max)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    /// <summary>
    /// Process-wide login log fed by <see cref="WebAdminHooks.LoginAttempt"/> (game client logins).
    /// Files: Logs\WebAdmin\logins-yyyy-MM-dd.jsonl next to the exe. Web panel sign-ins live in the audit log (auth.*) and are merged
    /// into <see cref="Query"/> results with source "web".
    /// </summary>
    public static class WebAdminLoginLog
    {
        public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "WebAdmin");

        private static WebAdminLoginLogStore _store;
        private static int _hooked;

        internal static WebAdminLoginLogStore Store
        {
            get => _store ??= new WebAdminLoginLogStore(DefaultDirectory);
            set => _store = value;
        }

        /// <summary>Subscribes the LoginAttempt hook once (idempotent).</summary>
        internal static void EnsureHooks()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
            WebAdminHooks.LoginAttempt += OnLoginAttempt;
        }

        private static void OnLoginAttempt(string accountId, string ip, string result)
        {
            Record(accountId, ip, result, "game");
        }

        public static void Record(string accountId, string ip, string result, string source = "game")
        {
            try
            {
                Store.Record(accountId, ip, result, source);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Login log record failed");
            }
        }

        /// <summary>
        /// Game logins from the store plus (unless <paramref name="source"/> is "game") web panel sign-ins from the audit log, newest first.
        /// </summary>
        public static IReadOnlyList<WebAdminLoginEntry> Query(string q, string result, int days, int limit, string source = null, DateTime? nowUtc = null)
        {
            days = Math.Clamp(days, 1, 366);
            limit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000);
            var now = nowUtc ?? DateTime.UtcNow;
            return QueryWindow(q, result, now.AddDays(-days), days, limit, source);
        }

        /// <summary>Same as <see cref="Query"/> for an explicit UTC window start (e.g. "last N hours").</summary>
        public static IReadOnlyList<WebAdminLoginEntry> QuerySince(string q, string result, DateTime sinceUtc, int limit, string source = null)
        {
            var days = (int)Math.Ceiling(Math.Max(1, (DateTime.UtcNow - sinceUtc).TotalDays));
            return QueryWindow(q, result, sinceUtc, days, Math.Clamp(limit <= 0 ? 200 : limit, 1, 5000), source);
        }

        private static IReadOnlyList<WebAdminLoginEntry> QueryWindow(string q, string result, DateTime since, int days, int limit, string source)
        {
            var list = new List<WebAdminLoginEntry>();
            var src = string.IsNullOrWhiteSpace(source) ? "all" : source.Trim().ToLowerInvariant();

            try
            {
                if (src == "all" || src == "game")
                    list.AddRange(Store.Query(q, result, since, limit, "game"));
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Login log query failed");
            }

            if (src == "all" || src == "web")
            {
                foreach (var a in QueryAuthAudit(Math.Min(limit * 2, 10000), q, days + 1))
                {
                    var e = FromAudit(a);
                    if (e == null) continue;
                    if (WebAdminLoginLogStore.Matches(e, q, result, since, "web")) list.Add(e);
                }
            }

            return list.OrderByDescending(e => e.Utc).Take(limit).ToList();
        }

        private static readonly object AuthAuditCacheGate = new object();
        private static readonly TimeSpan AuthAuditCacheTtl = TimeSpan.FromMinutes(2);
        private static object _authAuditCacheLog;
        private static string _authAuditCacheKey;
        private static DateTime _authAuditCacheAt;
        private static IReadOnlyList<WebAdminAuditEntry> _authAuditCacheRows;

        /// <summary>
        /// auth.* audit entries for the web sign-in part of the login log. The overview polls every 30 s, so the result is reused while the
        /// audit files of the window (length + write time) and the audit ring are unchanged, instead of re-reading up to days+1 daily files.
        /// </summary>
        private static IReadOnlyList<WebAdminAuditEntry> QueryAuthAudit(int limit, string q, int days)
        {
            var log = WebAdminAudit.Log;
            string key = null;

            try
            {
                if (log.Directory != null)
                {
                    var now = DateTime.UtcNow;
                    var sb = new System.Text.StringBuilder();
                    sb.Append(limit).Append('|').Append(days).Append('|').Append(q ?? string.Empty).Append('|').Append(log.Count);

                    var span = Math.Min(Math.Max(days, 0), 366);
                    for (var d = 0; d <= span; d++)
                    {
                        var info = new FileInfo(log.FilePath(now.AddDays(-d)));
                        if (!info.Exists) continue;
                        sb.Append('|').Append(d).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
                    }

                    key = sb.ToString();

                    lock (AuthAuditCacheGate)
                    {
                        if (ReferenceEquals(_authAuditCacheLog, log) && _authAuditCacheKey == key && now - _authAuditCacheAt < AuthAuditCacheTtl)
                            return _authAuditCacheRows;
                    }
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Login log: audit cache key failed");
                key = null;
            }

            var rows = WebAdminAudit.Query(limit, "auth", null, q, days);

            if (key != null)
            {
                lock (AuthAuditCacheGate)
                {
                    _authAuditCacheLog = log;
                    _authAuditCacheKey = key;
                    _authAuditCacheAt = DateTime.UtcNow;
                    _authAuditCacheRows = rows;
                }
            }

            return rows;
        }

        /// <summary>Maps a web sign-in audit entry (auth.login / auth.login-failed / auth.login-throttled / auth.login-cross-site) to a login entry; null for others.</summary>
        internal static WebAdminLoginEntry FromAudit(WebAdminAuditEntry a)
        {
            if (a == null || string.IsNullOrEmpty(a.Action)) return null;

            string result;
            switch (a.Action.ToLowerInvariant())
            {
                case "auth.login":
                    result = "ok";
                    break;
                case "auth.login-failed":
                    var reason = AuditReason(a);
                    result = string.IsNullOrEmpty(reason) ? "failed" : reason;
                    break;
                case "auth.login-throttled":
                    result = "throttled";
                    break;
                case "auth.login-cross-site":
                    result = "refused:cross-site";
                    break;
                default:
                    return null;
            }

            return new WebAdminLoginEntry
            {
                Id = "audit-" + a.Id,
                Utc = DateTime.SpecifyKind(a.Ts, DateTimeKind.Utc),
                AccountId = a.Target ?? a.Admin ?? string.Empty,
                Ip = a.Ip ?? string.Empty,
                Result = result,
                Source = "web",
            };
        }

        private static string AuditReason(WebAdminAuditEntry a)
        {
            if (!a.Details.HasValue) return null;
            var d = a.Details.Value;
            if (d.ValueKind != JsonValueKind.Object) return null;
            return d.GetString("reason");
        }
    }
}
