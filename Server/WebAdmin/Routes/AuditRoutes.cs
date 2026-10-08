using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// B11 — Audit Trail + game-data history.
    /// GET /api/admin/audit?limit=&amp;action=&amp;admin=&amp;q=&amp;days=&amp;before=   → {ok, entries:[...], next}
    /// GET /api/admin/audit/actions?days=                                    → {ok, actions:[{action, count}], groups:[...], admins:[...]}
    /// GET /api/admin/audit/export?action=&amp;admin=&amp;q=&amp;days=&amp;limit=        → CSV download
    /// GET /api/admin/db/history?endpoint=&amp;index=&amp;limit=&amp;days=&amp;before=   → {ok, items:[...], next}
    /// Paging: <c>before</c> is the opaque cursor returned as <c>next</c> ("&lt;ISO ts&gt;|&lt;id&gt;"); a bare ISO timestamp also works.
    /// </summary>
    public sealed class AuditRoutes : IWebAdminRoutes
    {
        public const int DefaultLimit = 200;
        public const int MaxLimit = 1000;
        public const int MaxExport = 10000;

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/audit", GetAudit);
            router.Get("/api/admin/audit/actions", GetActions);
            router.Get("/api/admin/audit/export", Export);
            router.Get("/api/admin/db/history", GetDbHistory);
        }

        private static AuditFilter FilterFrom(WebAdminRequest req, int defaultDays)
        {
            return new AuditFilter
            {
                Action = Trimmed(req.Query["action"]),
                Admin = Trimmed(req.Query["admin"]),
                Q = Trimmed(req.Query["q"]),
                Days = Math.Clamp(req.QueryInt("days", defaultDays), 0, 366),
            };
        }

        private static string Trimmed(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static AuditCursor CursorFrom(WebAdminRequest req)
        {
            var raw = req.Query["before"];
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!AuditCursor.TryParse(raw, out var cursor))
                throw new WebAdminException(400, "Invalid 'before' cursor");
            return cursor;
        }

        private static Task GetAudit(WebAdminRequest req)
        {
            var filter = FilterFrom(req, 7);
            var limit = Math.Clamp(req.QueryInt("limit", DefaultLimit), 1, MaxLimit);
            var page = AuditPager.Page(WebAdminAudit.Log, filter, limit, CursorFrom(req), DateTime.UtcNow);

            return req.Json(new
            {
                ok = true,
                entries = page.Entries,
                next = page.Next?.Format(),
                limit,
                days = filter.Days,
            });
        }

        private static Task GetActions(WebAdminRequest req)
        {
            var days = Math.Clamp(req.QueryInt("days", 30), 0, 366);
            var summary = AuditPager.Summarize(WebAdminAudit.Log, days, DateTime.UtcNow);

            return req.Json(new
            {
                ok = true,
                days,
                scanned = summary.Scanned,
                actions = summary.Actions.Select(p => new { action = p.Key, count = p.Value }).ToList(),
                groups = summary.Groups.Select(p => new { group = p.Key, count = p.Value }).ToList(),
                admins = summary.Admins.Select(p => new { admin = p.Key, count = p.Value }).ToList(),
            });
        }

        private static Task Export(WebAdminRequest req)
        {
            var filter = FilterFrom(req, 7);
            var limit = Math.Clamp(req.QueryInt("limit", MaxExport), 1, MaxExport);
            var page = AuditPager.Page(WebAdminAudit.Log, filter, limit, CursorFrom(req), DateTime.UtcNow);

            var csv = AuditCsv.Build(page.Entries);
            var preamble = Encoding.UTF8.GetPreamble();
            var body = Encoding.UTF8.GetBytes(csv);
            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            var name = "mircraft-audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

            return req.Bytes(bytes, "text/csv; charset=utf-8", name);
        }

        private static Task GetDbHistory(WebAdminRequest req)
        {
            var endpoint = Trimmed(req.Query["endpoint"]);
            int? index = null;
            if (req.Query.Has("index") && !string.IsNullOrWhiteSpace(req.Query["index"]))
            {
                if (!int.TryParse(req.Query["index"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    throw new WebAdminException(400, "Invalid index");
                index = i;
            }

            var filter = new AuditFilter
            {
                Action = "db",
                Admin = Trimmed(req.Query["admin"]),
                Q = Trimmed(req.Query["q"]),
                Days = Math.Clamp(req.QueryInt("days", 30), 0, 366),
            };

            if (endpoint != null || index.HasValue)
                filter.Predicate = e => AuditDbHistory.Normalize(e).Any(item => AuditDbHistory.ItemMatches(item, endpoint, index));

            var limit = Math.Clamp(req.QueryInt("limit", DefaultLimit), 1, MaxLimit);
            var page = AuditPager.Page(WebAdminAudit.Log, filter, limit, CursorFrom(req), DateTime.UtcNow);

            var items = new List<AuditDbHistoryItem>();
            foreach (var entry in page.Entries)
            {
                foreach (var item in AuditDbHistory.Normalize(entry))
                {
                    if (AuditDbHistory.ItemMatches(item, endpoint, index)) items.Add(item);
                }
            }

            return req.Json(new
            {
                ok = true,
                items,
                entryCount = page.Entries.Count,
                next = page.Next?.Format(),
                days = filter.Days,
            });
        }
    }

    // ------------------------------------------------------------------------------------------------ paging

    /// <summary>Total order cursor: entries are ordered by Ts descending, then Id descending (ordinal).</summary>
    public sealed class AuditCursor
    {
        public DateTime Ts { get; set; }

        public string Id { get; set; }

        public static AuditCursor From(WebAdminAuditEntry e)
        {
            return new AuditCursor { Ts = ToUtc(e.Ts), Id = e.Id ?? string.Empty };
        }

        public string Format()
        {
            return ToUtc(Ts).ToString("o", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(Id) ? string.Empty : "|" + Id);
        }

        public static bool TryParse(string raw, out AuditCursor cursor)
        {
            cursor = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            raw = raw.Trim();
            var bar = raw.IndexOf('|');
            var tsText = bar >= 0 ? raw.Substring(0, bar) : raw;
            var id = bar >= 0 ? raw.Substring(bar + 1) : null;

            if (!DateTime.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
                return false;

            cursor = new AuditCursor { Ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc), Id = id };
            return true;
        }

        /// <summary>True when <paramref name="e"/> comes strictly after this cursor in newest-first order (i.e. it is older).</summary>
        public bool IsBefore(WebAdminAuditEntry e)
        {
            var ts = ToUtc(e.Ts);
            if (ts < Ts) return true;
            if (ts > Ts) return false;
            if (Id == null) return false; // bare timestamp cursor: exclusive
            return string.CompareOrdinal(e.Id ?? string.Empty, Id) < 0;
        }

        internal static DateTime ToUtc(DateTime t)
        {
            if (t.Kind == DateTimeKind.Local) return t.ToUniversalTime();
            return DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }
    }

    public sealed class AuditFilter
    {
        /// <summary>Exact (case-insensitive) or a dotted prefix group: "gm", "gm." and "gm.*" all match "gm.kick".</summary>
        public string Action { get; set; }

        public string Admin { get; set; }

        public string Q { get; set; }

        /// <summary>≤ 0 = no age limit (files are still only scanned 366 days back).</summary>
        public int Days { get; set; } = 7;

        /// <summary>Extra predicate (e.g. game-data history endpoint/index filter).</summary>
        public Func<WebAdminAuditEntry, bool> Predicate { get; set; }

        public bool Matches(WebAdminAuditEntry e, DateTime sinceUtc)
        {
            if (e == null) return false;
            if (AuditCursor.ToUtc(e.Ts) < sinceUtc) return false;
            if (!ActionMatches(e.Action, Action)) return false;

            if (Admin != null && !string.Equals(e.Admin, Admin, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Q != null)
            {
                var details = e.Details.HasValue && e.Details.Value.ValueKind != JsonValueKind.Undefined ? e.Details.Value.GetRawText() : null;
                if (!Contains(e.Admin, Q) && !Contains(e.Ip, Q) && !Contains(e.Action, Q) && !Contains(e.Target, Q) && !Contains(e.Reason, Q) && !Contains(details, Q))
                    return false;
            }

            return Predicate == null || Predicate(e);
        }

        public static bool ActionMatches(string action, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;

            filter = filter.Trim();
            if (filter.EndsWith("*", StringComparison.Ordinal)) filter = filter.Substring(0, filter.Length - 1);
            filter = filter.TrimEnd('.');
            if (filter.Length == 0) return true;

            var a = action ?? string.Empty;
            return string.Equals(a, filter, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(filter + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class AuditPage
    {
        public List<WebAdminAuditEntry> Entries { get; set; } = new List<WebAdminAuditEntry>();

        /// <summary>Cursor for the next (older) page; null when there is nothing more.</summary>
        public AuditCursor Next { get; set; }
    }

    public sealed class AuditSummary
    {
        public int Scanned { get; set; }

        public List<KeyValuePair<string, int>> Actions { get; set; } = new List<KeyValuePair<string, int>>();

        public List<KeyValuePair<string, int>> Groups { get; set; } = new List<KeyValuePair<string, int>>();

        public List<KeyValuePair<string, int>> Admins { get; set; } = new List<KeyValuePair<string, int>>();
    }

    /// <summary>
    /// Cursor paging over an audit log. With a directory, the daily JSONL files are the source of truth (every record is appended there
    /// synchronously); day files are walked newest first and the walk stops once a page is full. Memory-only logs page the ring.
    /// </summary>
    public static class AuditPager
    {
        public const int MaxFileDays = 366;
        public const int MaxSummaryEntries = 250000;

        public static int CompareNewestFirst(WebAdminAuditEntry a, WebAdminAuditEntry b)
        {
            var c = AuditCursor.ToUtc(b.Ts).CompareTo(AuditCursor.ToUtc(a.Ts));
            if (c != 0) return c;
            return string.CompareOrdinal(b.Id ?? string.Empty, a.Id ?? string.Empty);
        }

        public static AuditPage Page(WebAdminAuditLog log, AuditFilter filter, int limit, AuditCursor before, DateTime nowUtc)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            filter ??= new AuditFilter();
            limit = Math.Max(1, limit);
            nowUtc = AuditCursor.ToUtc(nowUtc);

            var since = filter.Days > 0 ? nowUtc.AddDays(-filter.Days) : DateTime.MinValue;
            var found = new List<WebAdminAuditEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            bool Accept(WebAdminAuditEntry e)
            {
                if (e == null) return false;
                if (before != null && !before.IsBefore(e)) return false;
                if (!filter.Matches(e, since)) return false;
                if (!string.IsNullOrEmpty(e.Id) && !seen.Add(e.Id)) return false;
                return true;
            }

            if (log.Directory == null)
            {
                foreach (var e in log.Query(log.Capacity, null, null, null, filter.Days))
                {
                    if (Accept(e)) found.Add(e);
                }
            }
            else
            {
                foreach (var day in Days(nowUtc, since, before, filter.Days))
                {
                    foreach (var e in ReadDay(log, day))
                    {
                        if (Accept(e)) found.Add(e);
                    }

                    // Day files hold entries of that UTC day only, so once a page is overfull, older days cannot change it.
                    if (found.Count > limit) break;
                }
            }

            found.Sort(CompareNewestFirst);

            var page = new AuditPage();
            page.Entries = found.Take(limit).ToList();
            if (found.Count > limit && page.Entries.Count > 0)
                page.Next = AuditCursor.From(page.Entries[page.Entries.Count - 1]);

            return page;
        }

        public static AuditSummary Summarize(WebAdminAuditLog log, int days, DateTime nowUtc)
        {
            nowUtc = AuditCursor.ToUtc(nowUtc);
            var since = days > 0 ? nowUtc.AddDays(-days) : DateTime.MinValue;
            var actions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var admins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;

            IEnumerable<WebAdminAuditEntry> source;
            if (log.Directory == null)
                source = log.Query(log.Capacity, null, null, null, days);
            else
                source = Days(nowUtc, since, null, days).SelectMany(d => ReadDay(log, d));

            foreach (var e in source)
            {
                if (e == null || AuditCursor.ToUtc(e.Ts) < since) continue;
                if (++scanned > MaxSummaryEntries) break;

                var action = string.IsNullOrWhiteSpace(e.Action) ? "unknown" : e.Action;
                Bump(actions, action);
                Bump(groups, GroupOf(action));
                Bump(admins, string.IsNullOrWhiteSpace(e.Admin) ? "system" : e.Admin);
            }

            return new AuditSummary
            {
                Scanned = Math.Min(scanned, MaxSummaryEntries),
                Actions = actions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                Groups = groups.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                Admins = admins.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }

        public static string GroupOf(string action)
        {
            if (string.IsNullOrEmpty(action)) return "unknown";
            var dot = action.IndexOf('.');
            return dot > 0 ? action.Substring(0, dot) : action;
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out var n);
            map[key] = n + 1;
        }

        /// <summary>UTC days to scan, newest first.</summary>
        internal static IEnumerable<DateTime> Days(DateTime nowUtc, DateTime sinceUtc, AuditCursor before, int days)
        {
            var newest = nowUtc.Date.AddDays(1); // tolerate small clock skew of the writer
            if (before != null && before.Ts.Date < newest) newest = before.Ts.Date;

            var oldest = days > 0 ? sinceUtc.Date : nowUtc.Date.AddDays(-MaxFileDays);
            if (oldest < nowUtc.Date.AddDays(-MaxFileDays)) oldest = nowUtc.Date.AddDays(-MaxFileDays);

            for (var d = newest; d >= oldest; d = d.AddDays(-1))
                yield return d;
        }

        /// <summary>Entries of one daily file (file order; torn/corrupt lines skipped). Missing file → empty.</summary>
        internal static List<WebAdminAuditEntry> ReadDay(WebAdminAuditLog log, DateTime dayUtc)
        {
            var list = new List<WebAdminAuditEntry>();
            if (log.Directory == null) return list;

            var path = log.FilePath(dayUtc);
            if (!File.Exists(path)) return list;

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
                            if (e != null)
                            {
                                e.Ts = AuditCursor.ToUtc(e.Ts);
                                list.Add(e);
                            }
                        }
                        catch (JsonException)
                        {
                            // torn / corrupt line
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Failed to read audit file " + path);
            }

            return list;
        }
    }

    // ------------------------------------------------------------------------------------------------ CSV

    public static class AuditCsv
    {
        public static readonly string[] Header = { "ts", "admin", "ip", "action", "target", "reason", "details", "id" };

        public static string Build(IEnumerable<WebAdminAuditEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Header.Select(Cell))).Append("\r\n");

            foreach (var e in entries ?? Enumerable.Empty<WebAdminAuditEntry>())
            {
                var details = e.Details.HasValue && e.Details.Value.ValueKind != JsonValueKind.Undefined ? e.Details.Value.GetRawText() : string.Empty;
                var cells = new[]
                {
                    AuditCursor.ToUtc(e.Ts).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z",
                    e.Admin, e.Ip, e.Action, e.Target, e.Reason, details, e.Id,
                };
                sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>Quoted CSV cell; values that a spreadsheet would treat as a formula are prefixed with an apostrophe.</summary>
        public static string Cell(string value)
        {
            value ??= string.Empty;
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    // ------------------------------------------------------------------------------------------------ game-data history

    public sealed class AuditDbHistoryChange
    {
        public string Field { get; set; }

        /// <summary>Recorded value before the change (JSON null when it was null; absent (C# null) when the audit did not record it).</summary>
        public JsonElement? Before { get; set; }

        public JsonElement? After { get; set; }
    }

    public sealed class AuditDbHistoryItem
    {
        public string Id { get; set; }
        public DateTime Ts { get; set; }
        public string Admin { get; set; }
        public string Ip { get; set; }
        public string Action { get; set; }

        /// <summary>"update" | "insert" | "delete" | other verb (e.g. "save").</summary>
        public string Kind { get; set; }

        public string Endpoint { get; set; }
        public int? Index { get; set; }
        public string Reason { get; set; }
        public string Target { get; set; }
        public bool Revertible { get; set; }

        /// <summary>
        /// The endpoint keys rows by position in a parent's list (map safe zones / movements / mine zones, conquest children): the Index
        /// may belong to a different record now, so the entry is never revertible and its history can mix records.
        /// </summary>
        public bool PositionKeyed { get; set; }

        public List<AuditDbHistoryChange> Changes { get; set; } = new List<AuditDbHistoryChange>();
    }

    /// <summary>
    /// Turns <c>db.*</c> audit entries into per-row field changes. Tolerant of several detail shapes:
    /// <c>{endpoint, index, changes:[{field, before, after}]}</c>, <c>{changes:{Field:{before, after}}}</c>, <c>{Field:[before, after]}</c>
    /// maps, <c>{before:{...}, after:{...}}</c> row snapshots and multi-row <c>{rows:[{index, changes}]}</c>; endpoint/index fall back to
    /// the target ("items#12", "items/12", "items:12", "items[12]").
    /// </summary>
    public static class AuditDbHistory
    {
        private static readonly string[] EndpointKeys = { "endpoint", "table", "collection" };
        private static readonly string[] IndexKeys = { "index", "rowIndex", "row", "id" };
        private static readonly string[] RowListKeys = { "rows", "items", "edits" };
        private static readonly string[] ChangeKeys = { "changes", "fields", "diff", "changed" };
        private static readonly string[] FieldKeys = { "field", "name", "column", "key", "property" };
        private static readonly string[] BeforeKeys = { "before", "old", "oldValue", "from", "was" };
        private static readonly string[] AfterKeys = { "after", "new", "newValue", "to", "value" };
        private static readonly string[] KindKeys = { "op", "kind", "operation" };

        private static readonly Regex TargetPattern = new Regex(
            @"^\s*(?:db[.:/])?(?<ep>[A-Za-z][A-Za-z0-9_\-]*)\s*(?:(?:#|/|:|\[|\s)\s*#?(?<idx>-?\d+)\s*\]?)?\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool ItemMatches(AuditDbHistoryItem item, string endpoint, int? index)
        {
            if (endpoint != null && !string.Equals(item.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)) return false;
            if (index.HasValue && item.Index != index) return false;
            return true;
        }

        public static string KindOf(string action, JsonElement details)
        {
            foreach (var k in KindKeys)
            {
                var s = details.ValueKind == JsonValueKind.Object ? details.GetString(k) : null;
                if (!string.IsNullOrWhiteSpace(s)) return NormalizeVerb(s);
            }

            var a = action ?? string.Empty;
            var dot = a.LastIndexOf('.');
            return NormalizeVerb(dot >= 0 ? a.Substring(dot + 1) : a);
        }

        public static string NormalizeVerb(string verb)
        {
            var v = (verb ?? string.Empty).Trim().ToLowerInvariant();
            switch (v)
            {
                case "update":
                case "updated":
                case "edit":
                case "set":
                case "modify":
                case "put":
                case "change":
                case "revert":
                    return "update";
                case "insert":
                case "inserted":
                case "create":
                case "created":
                case "add":
                case "new":
                    return "insert";
                case "delete":
                case "deleted":
                case "remove":
                case "removed":
                    return "delete";
                default:
                    return v.Length == 0 ? "other" : v;
            }
        }

        public static bool TryParseTarget(string target, out string endpoint, out int? index)
        {
            endpoint = null;
            index = null;
            if (string.IsNullOrWhiteSpace(target)) return false;

            var m = TargetPattern.Match(target);
            if (!m.Success) return false;

            endpoint = m.Groups["ep"].Value;
            if (m.Groups["idx"].Success && int.TryParse(m.Groups["idx"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
                index = i;
            return true;
        }

        public static List<AuditDbHistoryItem> Normalize(WebAdminAuditEntry e)
        {
            var result = new List<AuditDbHistoryItem>();
            if (e == null) return result;

            var details = e.Details.HasValue ? e.Details.Value : default;
            var kind = KindOf(e.Action, details);

            TryParseTarget(e.Target, out var targetEndpoint, out var targetIndex);
            var actionEndpoint = EndpointFromAction(e.Action);
            var detailEndpoint = FirstString(details, EndpointKeys);
            var detailIndex = FirstInt(details, IndexKeys);

            var rows = RowList(details);
            var multi = rows.Count > 0;
            if (!multi) rows.Add(details);

            foreach (var row in rows)
            {
                var item = new AuditDbHistoryItem
                {
                    Id = e.Id,
                    Ts = AuditCursor.ToUtc(e.Ts),
                    Admin = e.Admin,
                    Ip = e.Ip,
                    Action = e.Action,
                    Kind = kind,
                    Reason = e.Reason,
                    Target = e.Target,
                };

                var rowKind = FirstString(row, KindKeys);
                if (!string.IsNullOrWhiteSpace(rowKind)) item.Kind = NormalizeVerb(rowKind);

                item.Endpoint = FirstString(row, EndpointKeys) ?? detailEndpoint ?? targetEndpoint ?? actionEndpoint;
                var ownIndex = FirstInt(row, IndexKeys);
                if (!multi) item.Index = detailIndex ?? targetIndex;
                else item.Index = ownIndex ?? (rows.Count == 1 ? detailIndex ?? targetIndex : null);

                item.Changes = ParseChanges(row);
                item.PositionKeyed = IsPositionKeyed(item.Endpoint);
                item.Revertible = item.Kind == "update" && !string.IsNullOrEmpty(item.Endpoint) && item.Index.HasValue
                    && item.Changes.Count > 0 && item.Changes.All(c => c.Before.HasValue) && !item.PositionKeyed;

                result.Add(item);
            }

            return result;
        }

        private static bool IsPositionKeyed(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            try
            {
                return global::Server.WebAdmin.GameData.GameDataRegistry.Get(endpoint)?.PositionKeyed ?? false;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Looking up game-data table " + endpoint + " for the history failed");
                return false;
            }
        }

        /// <summary>"db.items.update" → "items" (only when the action has three or more segments).</summary>
        private static string EndpointFromAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return null;
            var parts = action.Split('.');
            return parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
        }

        private static List<JsonElement> RowList(JsonElement details)
        {
            var list = new List<JsonElement>();
            if (details.ValueKind != JsonValueKind.Object) return list;

            foreach (var key in RowListKeys)
            {
                if (!details.TryGetPropertyIgnoreCase(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Object) list.Add(el);
                }

                if (list.Count > 0) return list;
            }

            return list;
        }

        public static List<AuditDbHistoryChange> ParseChanges(JsonElement row)
        {
            var changes = new List<AuditDbHistoryChange>();
            if (row.ValueKind != JsonValueKind.Object) return changes;

            foreach (var key in ChangeKeys)
            {
                if (!row.TryGetPropertyIgnoreCase(key, out var c)) continue;

                if (c.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in c.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        var field = FirstString(el, FieldKeys);
                        if (string.IsNullOrWhiteSpace(field)) continue;
                        Add(changes, field, FirstElement(el, BeforeKeys), FirstElement(el, AfterKeys), true);
                    }
                }
                else if (c.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in c.EnumerateObject())
                        AddFromValue(changes, p.Name, p.Value);
                }

                if (changes.Count > 0) return changes;
            }

            var before = FirstElement(row, BeforeKeys);
            var after = FirstElement(row, AfterKeys);

            // A single flattened change: {field:"Name", before:"a", after:"b"}.
            var single = FirstString(row, FieldKeys);
            if (single != null && (before.HasValue || after.HasValue)
                && (!before.HasValue || before.Value.ValueKind != JsonValueKind.Object)
                && (!after.HasValue || after.Value.ValueKind != JsonValueKind.Object))
            {
                Add(changes, single, before, after, true);
                return changes;
            }

            // Row snapshots: {before:{...}, after:{...}} (either may be missing for inserts / deletes).
            var beforeObj = before.HasValue && before.Value.ValueKind == JsonValueKind.Object ? before : null;
            var afterObj = after.HasValue && after.Value.ValueKind == JsonValueKind.Object ? after : null;

            if (beforeObj.HasValue || afterObj.HasValue)
            {
                var names = new List<string>();
                if (beforeObj.HasValue) names.AddRange(beforeObj.Value.EnumerateObject().Select(p => p.Name));
                if (afterObj.HasValue) names.AddRange(afterObj.Value.EnumerateObject().Select(p => p.Name));

                foreach (var name in names.Distinct(StringComparer.Ordinal))
                {
                    JsonElement? b = null, a = null;
                    if (beforeObj.HasValue && beforeObj.Value.TryGetProperty(name, out var bv)) b = bv.Clone();
                    if (afterObj.HasValue && afterObj.Value.TryGetProperty(name, out var av)) a = av.Clone();

                    if (b.HasValue && a.HasValue && b.Value.GetRawText() == a.Value.GetRawText()) continue;
                    Add(changes, name, b, a, beforeObj.HasValue);
                }
            }

            return changes;
        }

        private static void AddFromValue(List<AuditDbHistoryChange> changes, string field, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var b = FirstElement(value, BeforeKeys);
                var a = FirstElement(value, AfterKeys);
                if (b.HasValue || a.HasValue)
                {
                    Add(changes, field, b, a, true);
                    return;
                }
            }

            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2)
            {
                Add(changes, field, value[0].Clone(), value[1].Clone(), true);
                return;
            }

            Add(changes, field, null, value.Clone(), false);
        }

        private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

        /// <summary>
        /// <paramref name="beforeKnown"/>: the audit recorded this field's previous value; an absent value then means null (WebAdminJson omits
        /// null members). Unknown previous values stay absent, which makes the change non-revertible.
        /// </summary>
        private static void Add(List<AuditDbHistoryChange> changes, string field, JsonElement? before, JsonElement? after, bool beforeKnown)
        {
            if (string.IsNullOrWhiteSpace(field) || field == "__rev") return;
            if (!before.HasValue && beforeKnown) before = NullElement;
            changes.Add(new AuditDbHistoryChange { Field = field, Before = before, After = after });
        }

        private static string FirstString(JsonElement obj, string[] keys)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            foreach (var k in keys)
            {
                if (!obj.TryGetPropertyIgnoreCase(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString().Trim();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }

            return null;
        }

        private static int? FirstInt(JsonElement obj, string[] keys)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            foreach (var k in keys)
            {
                if (!obj.TryGetPropertyIgnoreCase(k, out var v)) continue;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out i)) return i;
            }

            return null;
        }

        private static JsonElement? FirstElement(JsonElement obj, string[] keys)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;

            foreach (var k in keys)
            {
                if (obj.TryGetPropertyIgnoreCase(k, out var v)) return v.Clone();
            }

            return null;
        }
    }
}
