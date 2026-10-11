using System.Globalization;
using System.Text.Json;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>
    /// LiveDb batch journal: one JSON line per applied batch in <c>Logs\WebAdmin\livedb\batches-yyyy-MM-dd.jsonl</c> (next to the exe,
    /// UTF-8 without BOM, like <see cref="WebAdminAuditLog"/>), an in-memory ring of recent records, and an LRU of the last
    /// <see cref="LruCapacity"/> batch ids → outcome so a retried request (same client id) gets the first answer. An id that fell out of
    /// the LRU is looked up in the journal files (last <see cref="LookupDays"/> days). Thread-safe; never throws on IO.
    /// </summary>
    internal sealed class LiveDbJournal
    {
        public const int LruCapacity = 500;
        public const int LookupDays = 14;

        private readonly object _gate = new object();
        private readonly object _fileGate = new object();
        private readonly LinkedList<LiveDbBatchResult> _ring = new LinkedList<LiveDbBatchResult>();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, LiveDbBatchResult>>> _lruIndex =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, LiveDbBatchResult>>>(StringComparer.Ordinal);
        private readonly LinkedList<KeyValuePair<string, LiveDbBatchResult>> _lru = new LinkedList<KeyValuePair<string, LiveDbBatchResult>>();
        private readonly Func<DateTime> _clock;

        public LiveDbJournal(string directory, int ringCapacity = 200, Func<DateTime> clock = null)
        {
            Directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
            RingCapacity = Math.Max(10, ringCapacity);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Folder for the JSONL files; null = memory only (tests).</summary>
        public string Directory { get; }

        public int RingCapacity { get; }

        public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "WebAdmin", "livedb");

        private static LiveDbJournal _current;

        public static LiveDbJournal Current
        {
            get => _current ??= new LiveDbJournal(DefaultDirectory);
            set => _current = value;
        }

        internal string FilePath(DateTime utc)
        {
            return Path.Combine(Directory, "batches-" + utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        }

        /// <summary>Records an applied batch (ring, LRU, file).</summary>
        public void Append(LiveDbBatchResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.Id)) return;

            lock (_gate)
            {
                _ring.AddLast(result);
                while (_ring.Count > RingCapacity) _ring.RemoveFirst();
                Remember(result.Id, result);
            }

            if (Directory == null) return;

            try
            {
                var line = JsonSerializer.Serialize(ToRecord(result), WebAdminJson.Options);
                lock (_fileGate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(FilePath(result.Utc == default ? _clock() : result.Utc), line + "\n", WebAdminJson.Utf8NoBom);
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Failed to write the LiveDb journal");
            }
        }

        private void Remember(string id, LiveDbBatchResult result)
        {
            if (_lruIndex.TryGetValue(id, out var existing))
            {
                _lru.Remove(existing);
                _lruIndex.Remove(id);
            }

            var node = _lru.AddLast(new KeyValuePair<string, LiveDbBatchResult>(id, result));
            _lruIndex[id] = node;
            while (_lru.Count > LruCapacity)
            {
                var first = _lru.First;
                _lruIndex.Remove(first.Value.Key);
                _lru.RemoveFirst();
            }
        }

        /// <summary>The stored outcome of an earlier batch with this id (LRU first, then the journal files), else null.</summary>
        public LiveDbBatchResult Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            lock (_gate)
            {
                if (_lruIndex.TryGetValue(id, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddLast(node);
                    return node.Value.Value;
                }
            }

            if (Directory == null) return null;

            var now = _clock();
            for (var d = 0; d <= LookupDays; d++)
            {
                var path = FilePath(now.AddDays(-d));
                if (!File.Exists(path)) continue;

                foreach (var record in ReadFile(path))
                {
                    if (!string.Equals(record.Id, id, StringComparison.Ordinal)) continue;
                    lock (_gate) Remember(id, record);
                    return record;
                }
            }

            return null;
        }

        /// <summary>Newest first: the ring, then the files (summaries only, no row images).</summary>
        public IReadOnlyList<LiveDbBatchResult> Recent(int limit, int days = 7)
        {
            if (limit <= 0) limit = 50;
            limit = Math.Min(limit, 1000);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<LiveDbBatchResult>();

            lock (_gate)
            {
                for (var node = _ring.Last; node != null && list.Count < limit; node = node.Previous)
                    if (seen.Add(node.Value.Id)) list.Add(node.Value);
            }

            if (Directory != null && list.Count < limit)
            {
                var now = _clock();
                for (var d = 0; d <= Math.Clamp(days, 0, 366) && list.Count < limit; d++)
                {
                    var path = FilePath(now.AddDays(-d));
                    if (!File.Exists(path)) continue;
                    var records = ReadFile(path);
                    for (var i = records.Count - 1; i >= 0 && list.Count < limit; i--)
                        if (seen.Add(records[i].Id)) list.Add(records[i]);
                }
            }

            list.Sort((a, b) => b.Utc.CompareTo(a.Utc));
            return list;
        }

        private static List<LiveDbBatchResult> ReadFile(string path)
        {
            var list = new List<LiveDbBatchResult>();
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
                            var record = Parse(line);
                            if (record != null && !string.IsNullOrEmpty(record.Id)) list.Add(record);
                        }
                        catch (JsonException)
                        {
                            // skip a torn / corrupt line
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Failed to read " + path);
            }

            return list;
        }

        /// <summary>
        /// One journal line back into a result, including the per-row before / after images (internal fields the serializer skips;
        /// undo needs them after a restart). Values come back as <see cref="JsonElement"/>s. Throws <see cref="JsonException"/>.
        /// </summary>
        internal static LiveDbBatchResult Parse(string line)
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;
                var record = root.Deserialize<LiveDbBatchResult>(WebAdminJson.Options);
                if (record == null) return null;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    var i = 0;
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (i >= record.Rows.Count) break;
                        var outcome = record.Rows[i++];
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        outcome.BeforeImage = Image(row, "beforeImage");
                        outcome.AfterImage = Image(row, "afterImage");
                    }
                }

                return record;
            }
        }

        private static Dictionary<string, object> Image(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var image) || image.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in image.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            return dict;
        }

        /// <summary>The journal line: the outcome plus the before / after images of every row (for history and undo).</summary>
        internal static object ToRecord(LiveDbBatchResult r)
        {
            return new
            {
                id = r.Id,
                status = r.Status,
                saved = r.Saved,
                backupFile = r.BackupFile,
                utc = r.Utc,
                admin = r.Admin,
                reason = r.Reason,
                source = r.Source,
                inverseOf = r.InverseOf,
                notes = r.Notes,
                world = r.World,
                applied = r.Applied,
                deleted = r.Deleted,
                rows = r.Rows.Select(row => new
                {
                    endpoint = row.Endpoint,
                    index = row.Index,
                    newIndex = row.NewIndex,
                    kind = row.Kind,
                    name = row.Name,
                    revBefore = row.RevBefore,
                    revAfter = row.RevAfter,
                    rowLive = row.RowLive,
                    rowReason = row.RowReason,
                    changes = row.Changes,
                    beforeImage = row.BeforeImage,
                    afterImage = row.AfterImage,
                }).ToList(),
                restartFields = r.RestartFields,
                live = r.Live,
                pendingReboot = r.PendingReboot,
                flushedAreas = r.FlushedAreas,
                timingsMs = r.TimingsMs,
                message = r.Message,
            };
        }
    }
}
