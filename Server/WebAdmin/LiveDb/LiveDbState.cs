namespace Server.WebAdmin.LiveDb
{
    /// <summary>
    /// Process-wide LiveDb bookkeeping: counters for /api/admin/livedb/status and the divergence set. Thread-safe.
    /// <para>
    /// Divergence: a position-keyed child row (safe zone, movement, mine zone, conquest child) is matched to Main's row by its position
    /// in the parent's list. When a structural change of such a list (insert / delete) is saved WITHOUT reaching Main (a restart-only
    /// table, a failed or refused live step, a list the live step found out of step), positions in Edit and Main no longer line up, so
    /// the parent ("maps#3") is marked diverged and live edits, inserts and deletes of its children are refused (saved only) until the
    /// world restarts and Main reloads the data. The live step also checks each touched list itself (length, and the values of every
    /// touched row), so a list changed behind LiveDb's back (the legacy delete route, a WinForms editor) is never changed by position.
    /// </para>
    /// </summary>
    internal sealed class LiveDbState
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _diverged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long _batches;
        private long _rejected;
        private long _liveFailures;
        private string _lastBatchId;
        private DateTime? _lastBatchUtc;
        private string _lastOutcome;
        private bool _lastSaved;
        private string _lastInverseOf;
        private long _undos;

        public static string ParentKey(string parentEndpoint, int parentIndex) => (parentEndpoint ?? "?") + "#" + parentIndex;

        public void MarkDiverged(string parentKey)
        {
            if (string.IsNullOrEmpty(parentKey)) return;
            lock (_gate) _diverged.Add(parentKey);
        }

        public bool IsDiverged(string parentKey)
        {
            if (string.IsNullOrEmpty(parentKey)) return false;
            lock (_gate) return _diverged.Contains(parentKey);
        }

        public IReadOnlyList<string> Diverged()
        {
            lock (_gate)
            {
                var list = new List<string>(_diverged);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }
        }

        /// <summary>Main reloaded its data (world start): Edit and Main agree again.</summary>
        public void ClearDivergence()
        {
            lock (_gate) _diverged.Clear();
        }

        public void NoteBatch(LiveDbBatchResult result)
        {
            if (result == null) return;
            lock (_gate)
            {
                if (result.Status == "rejected")
                {
                    _rejected++;
                    return;
                }

                if (result.Status != "applied") return;
                _batches++;
                _lastBatchId = result.Id;
                _lastBatchUtc = result.Utc;
                _lastOutcome = result.Live?.Outcome;
                _lastSaved = result.Saved;
                _lastInverseOf = result.InverseOf;
                if (!string.IsNullOrEmpty(result.InverseOf)) _undos++;
                if (result.Live != null && (result.Live.Outcome == "rolled-back" || result.Live.Outcome == "timeout")) _liveFailures++;
            }
        }

        public Dictionary<string, object> Snapshot()
        {
            lock (_gate)
            {
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["batches"] = _batches,
                    ["rejected"] = _rejected,
                    ["liveFailures"] = _liveFailures,
                    ["lastBatchId"] = _lastBatchId,
                    ["lastBatchUtc"] = _lastBatchUtc,
                    ["lastLiveOutcome"] = _lastOutcome,
                    ["lastSaved"] = _lastSaved,
                    ["lastInverseOf"] = _lastInverseOf,
                    ["undos"] = _undos,
                    ["diverged"] = new List<string>(_diverged),
                };
            }
        }

        // ================================================================== process-wide instance + world hooks

        private static int _hooked;

        public static LiveDbState Current { get; } = new LiveDbState();

        /// <summary>
        /// WorldStarted: Main reloaded Server.MirDB, so divergence is gone. WorldStopped: the sweeper's queue refers to monster objects
        /// of the stopped world, so it is dropped (Main's objects are replaced on Reboot; never cached across WorldStarted).
        /// Idempotent (called from LiveDbRoutes.Register).
        /// </summary>
        public static void EnsureHooks()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
            WebAdminHooks.WorldStarted += () =>
            {
                Current.ClearDivergence();
                LiveDbSweeper.Drop();
            };
            WebAdminHooks.WorldStopped += () =>
            {
                LiveDbSweeper.Drop();
                Current.ClearDivergence();
            };
        }
    }
}
