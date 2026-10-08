using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Server.WebAdmin
{
    public sealed class WebAdminAuditEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("ts")]
        public DateTime Ts { get; set; }

        [JsonPropertyName("admin")]
        public string Admin { get; set; }

        [JsonPropertyName("ip")]
        public string Ip { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; }

        [JsonPropertyName("target")]
        public string Target { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; }

        /// <summary>Detached JSON snapshot of the details object (null when none).</summary>
        [JsonPropertyName("details")]
        public JsonElement? Details { get; set; }
    }

    /// <summary>
    /// Audit log: in-memory ring + optional daily JSONL files (audit-yyyy-MM-dd.jsonl, one JSON object per line, UTF-8 without BOM).
    /// </summary>
    public sealed class WebAdminAuditLog
    {
        private readonly LinkedList<WebAdminAuditEntry> _ring = new LinkedList<WebAdminAuditEntry>();
        private readonly object _gate = new object();
        private readonly object _fileGate = new object();
        private readonly Func<DateTime> _clock;

        public WebAdminAuditLog(string directory, int capacity = 2000, Func<DateTime> clock = null)
        {
            Directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
            Capacity = Math.Max(10, capacity);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Folder for JSONL files; null = memory only.</summary>
        public string Directory { get; }

        public int Capacity { get; }

        public int Count
        {
            get
            {
                lock (_gate) return _ring.Count;
            }
        }

        public WebAdminAuditEntry Record(string admin, string ip, string action, string target, object details = null, string reason = null)
        {
            var entry = new WebAdminAuditEntry
            {
                Id = WebAdminSessionStore.NewToken(8),
                Ts = DateTime.SpecifyKind(_clock(), DateTimeKind.Utc),
                Admin = string.IsNullOrWhiteSpace(admin) ? "system" : admin,
                Ip = ip,
                Action = string.IsNullOrWhiteSpace(action) ? "unknown" : action.Trim(),
                Target = target,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                Details = SafeElement(details),
            };

            lock (_gate)
            {
                _ring.AddLast(entry);
                while (_ring.Count > Capacity) _ring.RemoveFirst();
            }

            if (Directory != null)
            {
                try
                {
                    var line = JsonSerializer.Serialize(entry, WebAdminJson.Options);
                    var path = FilePath(entry.Ts);

                    lock (_fileGate)
                    {
                        System.IO.Directory.CreateDirectory(Directory);
                        File.AppendAllText(path, line + "\n", WebAdminJson.Utf8NoBom);
                    }
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Failed to write the web admin audit log");
                }
            }

            return entry;
        }

        /// <summary>
        /// Newest first. <paramref name="action"/>: exact (case-insensitive) or a prefix group ("accounts" matches "accounts.ban");
        /// <paramref name="admin"/>: exact (case-insensitive); <paramref name="q"/>: substring of admin/ip/action/target/reason/details;
        /// <paramref name="days"/>: only entries newer than now - days (≤ 0 = no limit). Reads the ring plus the JSONL files (if any).
        /// </summary>
        public IReadOnlyList<WebAdminAuditEntry> Query(int limit, string action = null, string admin = null, string q = null, int days = 7)
        {
            if (limit <= 0) limit = 100;
            limit = Math.Min(limit, 10000);

            var now = _clock();
            var since = days > 0 ? now.AddDays(-days) : DateTime.MinValue;

            List<WebAdminAuditEntry> ringItems;
            // Newest first, so that the stable OrderByDescending below keeps insertion order (newest first) for equal timestamps.
            lock (_gate) ringItems = _ring.Reverse().ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<WebAdminAuditEntry>();

            foreach (var e in ringItems)
            {
                if (e.Id != null) seen.Add(e.Id);
                if (Matches(e, since, action, admin, q)) results.Add(e);
            }

            if (Directory != null && results.Count < limit)
            {
                foreach (var e in ReadFiles(now, days))
                {
                    if (e.Id != null && !seen.Add(e.Id)) continue;
                    if (Matches(e, since, action, admin, q)) results.Add(e);
                }
            }

            return results.OrderByDescending(e => e.Ts).Take(limit).ToList();
        }

        internal string FilePath(DateTime utc)
        {
            return Path.Combine(Directory, "audit-" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        private IEnumerable<WebAdminAuditEntry> ReadFiles(DateTime now, int days)
        {
            var span = days > 0 ? Math.Min(days, 366) : 366;
            var list = new List<WebAdminAuditEntry>();

            for (var d = 0; d <= span; d++)
            {
                var path = FilePath(now.AddDays(-d));
                if (!File.Exists(path)) continue;

                var fileEntries = new List<WebAdminAuditEntry>();

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
                                var e = JsonSerializer.Deserialize<WebAdminAuditEntry>(line, WebAdminJson.Options);
                                if (e != null) fileEntries.Add(e);
                            }
                            catch (JsonException)
                            {
                                // skip a torn/corrupt line
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Failed to read " + path);
                }

                // Lines are appended chronologically; add newest first (days are already walked newest first).
                fileEntries.Reverse();
                list.AddRange(fileEntries);
            }

            return list;
        }

        private static bool Matches(WebAdminAuditEntry e, DateTime since, string action, string admin, string q)
        {
            if (e.Ts < since) return false;

            if (!string.IsNullOrWhiteSpace(action))
            {
                action = action.Trim();
                var a = e.Action ?? string.Empty;
                if (!string.Equals(a, action, StringComparison.OrdinalIgnoreCase) && !a.StartsWith(action + ".", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(admin) && !string.Equals(e.Admin, admin.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(q))
            {
                q = q.Trim();
                var details = e.Details.HasValue && e.Details.Value.ValueKind != JsonValueKind.Undefined ? e.Details.Value.GetRawText() : null;

                if (!Contains(e.Admin, q) && !Contains(e.Ip, q) && !Contains(e.Action, q) && !Contains(e.Target, q) && !Contains(e.Reason, q) && !Contains(details, q))
                    return false;
            }

            return true;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JsonElement? SafeElement(object details)
        {
            if (details == null) return null;

            try
            {
                var element = WebAdminJson.ToElement(details);
                return element.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : element;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Audit details could not be serialized");
                return WebAdminJson.ToElement(new { error = "details not serializable" });
            }
        }
    }

    /// <summary>
    /// Audit trail for web admin actions. Call <see cref="Record(WebAdminRequest, string, string, object, string)"/> for EVERY successful
    /// mutation (and for refused sensitive actions where useful). Never record passwords or hashes. Never throws.
    /// Files: Logs\WebAdmin\audit-yyyy-MM-dd.jsonl next to the exe.
    /// </summary>
    public static class WebAdminAudit
    {
        public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "WebAdmin");

        private static WebAdminAuditLog _log;

        internal static WebAdminAuditLog Log
        {
            get => _log ??= new WebAdminAuditLog(DefaultDirectory);
            set => _log = value;
        }

        public static void Record(WebAdminRequest req, string action, string target, object details = null, string reason = null)
        {
            Record(req?.AdminName ?? "system", req?.ClientIp, action, target, details, reason);
        }

        public static void Record(string admin, string ip, string action, string target, object details = null, string reason = null)
        {
            try
            {
                Log.Record(admin, ip, action, target, details, reason);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Audit record failed");
            }
        }

        public static IReadOnlyList<WebAdminAuditEntry> Query(int limit, string action = null, string admin = null, string q = null, int days = 7)
        {
            try
            {
                return Log.Query(limit, action, admin, q, days);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Audit query failed");
                return Array.Empty<WebAdminAuditEntry>();
            }
        }
    }
}
