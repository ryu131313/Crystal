namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// Tracks Edit-envir changes that were applied by the web admin but not yet written with Envir.Edit.SaveDB()
    /// (GET /api/admin/database/status). Thread-safe; cleared by a save or a reload of the Edit envir.
    /// </summary>
    internal sealed class GameDataUnsaved
    {
        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;
        private readonly SortedDictionary<string, int> _areas = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _changes;
        private DateTime? _lastSavedUtc;
        private DateTime? _firstChangeUtc;

        public GameDataUnsaved(Func<DateTime> clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Records <paramref name="count"/> changed rows of an area (endpoint).</summary>
        public void Note(string area, int count)
        {
            if (count <= 0) return;
            lock (_gate)
            {
                _changes += count;
                var key = string.IsNullOrWhiteSpace(area) ? "gamedata" : area.Trim();
                _areas[key] = (_areas.TryGetValue(key, out var n) ? n : 0) + count;
                _firstChangeUtc ??= _clock();
            }
        }

        /// <summary>The areas changed since the last save (for the pending-reboot description); clears the counters.</summary>
        public IReadOnlyList<string> MarkSaved()
        {
            lock (_gate)
            {
                var areas = _areas.Keys.ToList();
                _areas.Clear();
                _changes = 0;
                _firstChangeUtc = null;
                _lastSavedUtc = _clock();
                return areas;
            }
        }

        /// <summary>Unsaved changes were thrown away (Edit envir reloaded from disk).</summary>
        public void Discard()
        {
            lock (_gate)
            {
                _areas.Clear();
                _changes = 0;
                _firstChangeUtc = null;
            }
        }

        public int Changes
        {
            get
            {
                lock (_gate) return _changes;
            }
        }

        /// <summary>{unsavedChanges, unsavedAreas:[{area, rows}], firstUnsavedUtc, lastSavedUtc, systemDataStale} (callers may add keys).</summary>
        public Dictionary<string, object> Snapshot(bool systemDataStale)
        {
            lock (_gate)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["unsavedChanges"] = _changes,
                    ["unsavedAreas"] = _areas.Select(kv => new { area = kv.Key, rows = kv.Value }).ToList(),
                    ["firstUnsavedUtc"] = _firstChangeUtc,
                    ["lastSavedUtc"] = _lastSavedUtc,
                    ["systemDataStale"] = systemDataStale,
                };
            }
        }
    }

    /// <summary>Process-wide unsaved-change tracker for the Edit envir.</summary>
    internal static class GameDataState
    {
        public static GameDataUnsaved Unsaved { get; } = new GameDataUnsaved();

        private static volatile bool _editReloadFailed;

        /// <summary>
        /// True after a database/reload-edit whose Envir.Edit.LoadDB() failed part-way: the Edit lists may be half refilled (maps loaded,
        /// items / monsters empty), so database/save refuses until a reload succeeds.
        /// </summary>
        public static bool EditReloadFailed
        {
            get => _editReloadFailed;
            set => _editReloadFailed = value;
        }
    }
}
