using System.Diagnostics;
using System.Globalization;
using Server.MirDatabase;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>[LiveDb] settings as the engine sees them (production: <see cref="FromSettings"/>; tests: plain values).</summary>
    internal sealed class LiveDbOptions
    {
        public bool Enabled = true;
        public int BackupEveryMinutes = 10;
        public int SweepPerTick = 512;
        public int MaxBatchRows = 2000;
        public int MaxPushRows = 500;

        /// <summary>How long the live step waits for the game thread to pick it up (never abandoned once started).</summary>
        public int GameThreadTimeoutMs = 30000;

        public static LiveDbOptions FromSettings()
        {
            return new LiveDbOptions
            {
                Enabled = Settings.LiveDbEnabled,
                BackupEveryMinutes = Math.Max(0, Settings.LiveDbBackupEveryMinutes),
                SweepPerTick = Math.Max(1, Settings.LiveDbSweepPerTick),
                MaxBatchRows = Math.Max(1, Settings.LiveDbMaxBatchRows),
                MaxPushRows = Math.Max(1, Settings.LiveDbMaxPushRows),
                GameThreadTimeoutMs = GameDataSave.GameThreadStartTimeoutMs,
            };
        }
    }

    /// <summary>What a successful save reports.</summary>
    internal sealed class LiveDbSaveOutcome
    {
        public string BackupFile;

        /// <summary>Areas of other (non-LiveDb) unsaved changes the save also wrote (flushUnsaved).</summary>
        public IReadOnlyList<string> Areas = Array.Empty<string>();

        /// <summary>The saved spells (Edit snapshot) for the magic sync; null = no sync.</summary>
        public List<MagicInfo> Magic;
    }

    /// <summary>Writes the editor copy to Server.MirDB (production: GameDataSave.SaveEdit via RunFileIo).</summary>
    internal interface ILiveDbPersistence
    {
        /// <summary>Throws (409) when saving could truncate Server.MirDB.</summary>
        void EnsureSavable();

        /// <summary>Throws on failure (the file on disk is unchanged then).</summary>
        LiveDbSaveOutcome Save();
    }

    /// <summary>Everything the engine touches, so it runs in tests without Envir / Settings (production: <see cref="LiveDbProduction"/>).</summary>
    internal sealed class LiveDbEnvironment
    {
        public object EditLock = new object();

        /// <summary>The editor copy (called under <see cref="EditLock"/>).</summary>
        public Func<GameDataContext> Edit;

        /// <summary>The running world's system data (called on the game thread only).</summary>
        public Func<GameDataContext> Main;

        /// <summary>"running" | "stopped" | "starting" | "stopping".</summary>
        public Func<string> WorldState = () => "stopped";

        public Func<int> EditorFormsOpen = () => 0;

        public GameDataUnsaved Unsaved = new GameDataUnsaved();

        public ILiveDbPersistence Persistence;

        /// <summary>
        /// Runs the action on the game thread and waits: true when it ran (its exception propagates), false when it could not be queued
        /// or was dropped before it ran; throws <see cref="GameThreadTimeoutException"/> when it was not started within the timeout.
        /// </summary>
        public Func<Action, int, bool> RunOnGameThread = (a, t) => false;

        public ILiveDbHooks Hooks;
        public LiveDbJournal Journal = new LiveDbJournal(null);
        public LiveDbState State = new LiveDbState();
        public LiveDbOptions Options = new LiveDbOptions();

        /// <summary>Audit sink: (action, target, details, reason).</summary>
        public Action<string, string, object, string> Audit = (a, t, d, r) => { };

        /// <summary>Pending-reboot sink: (area, description).</summary>
        public Action<string, string> Pending = (a, d) => { };

        /// <summary>The saved spells reached Main (clear the pending magic snapshot).</summary>
        public Action<List<MagicInfo>> MagicSynced = m => { };

        public Action<string> Log = s => { };
        public Action<Exception, string> LogError = (e, s) => { };
        public Func<DateTime> Clock = () => DateTime.UtcNow;

        /// <summary>Column classification (tests may switch a table on to exercise later-batch paths).</summary>
        public Func<string, string, LiveColumnRule> Classify = LiveDbClassification.Effective;

        public Func<string, bool> InsertLive = LiveDbClassification.InsertLive;

        public Func<string, bool> DeleteLive = LiveDbClassification.DeleteLive;
    }

    /// <summary>
    /// The LiveDb pipeline for one batch (pure over <see cref="LiveDbEnvironment"/>; call it from a worker thread, never the game thread):
    /// <list type="number">
    /// <item>checks: id, size, world state (starting / stopping → 503), WinForms DB editor open → 409, other unsaved edits → 409 unless
    /// flushUnsaved, a repeated id → the stored outcome;</item>
    /// <item>validate-all: <see cref="GameDataEngine.Apply"/> / <see cref="GameDataEngine.Delete"/> as dry runs for every table; any
    /// conflict or validation problem rejects the whole batch (nothing changes);</item>
    /// <item>apply to Edit with before-images (entity values, row lists, index counters);</item>
    /// <item>save through <see cref="ILiveDbPersistence"/>; a failure restores Edit (500, nothing changed);</item>
    /// <item>one game-thread live step (<see cref="LiveDbMainApplier"/>) when the world runs; a failure there restores Main and keeps the
    /// save ("saved; takes effect after restart", pending-reboot entry);</item>
    /// <item>journal + audit (db.update / db.insert / db.delete with <c>batch</c>, plus db.livedb-batch) + pending entry.</item>
    /// </list>
    /// </summary>
    internal sealed class LiveDbEngine
    {
        private readonly LiveDbEnvironment _env;

        public LiveDbEngine(LiveDbEnvironment env)
        {
            _env = env ?? throw new ArgumentNullException(nameof(env));
        }

        private sealed class TablePlan
        {
            public GameDataTable Table;
            public LiveDbTableOps Ops;
            public GameDataWriteResult Dry;
            public GameDataDeleteResult DryDelete;
            public GameDataWriteResult Real;
            public GameDataDeleteResult RealDelete;
            public readonly Dictionary<int, string> RevBefore = new Dictionary<int, string>();

            /// <summary>Position-keyed child tables: every touched row's typed values before the batch (Main must still hold them).</summary>
            public readonly Dictionary<int, List<KeyValuePair<GameDataColumn, object>>> Expected = new Dictionary<int, List<KeyValuePair<GameDataColumn, object>>>();

            /// <summary>Position-keyed child tables: the length of every parent's list before the batch.</summary>
            public readonly Dictionary<int, int> ParentCounts = new Dictionary<int, int>();

            public bool Positional => Table.PositionKeyed && Table.IsChild;
        }

        public LiveDbBatchResult Run(LiveDbBatchRequest req)
        {
            if (req == null) throw new WebAdminException(400, "Missing batch");
            if (!LiveDbBatchRequest.IsValidId(req.Id)) throw new WebAdminException(400, "Invalid batch id");
            if (req.Tables.Count == 0 || req.OpCount == 0) throw new WebAdminException(400, "The batch contains no changes");
            if (req.OpCount > _env.Options.MaxBatchRows)
                throw new WebAdminException(413, "Too many rows in one batch (max " + _env.Options.MaxBatchRows.ToString(CultureInfo.InvariantCulture) +
                                                 ", [LiveDb] MaxBatchRows)");

            if (!req.DryRun)
            {
                var prior = _env.Journal.Find(req.Id);
                if (prior != null) return AsDuplicate(prior);
            }

            var world = _env.WorldState() ?? "stopped";
            if (world == "starting" || world == "stopping")
                throw new WebAdminException(503, "The world is " + world + "; try again in a moment", new { world });

            var total = Stopwatch.StartNew();
            lock (_env.EditLock)
            {
                if (!req.DryRun)
                {
                    var prior = _env.Journal.Find(req.Id); // a concurrent request with the same id finished while we waited
                    if (prior != null) return AsDuplicate(prior);
                }

                var result = new LiveDbBatchResult
                {
                    Id = req.Id,
                    Utc = _env.Clock(),
                    Admin = req.Admin,
                    Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
                    Source = req.Source,
                    InverseOf = string.IsNullOrEmpty(req.InverseOf) ? null : req.InverseOf,
                    World = world,
                    UnsavedChanges = _env.Unsaved.Changes,
                    EditorFormsOpen = _env.EditorFormsOpen(),
                };

                if (!req.DryRun)
                {
                    if (result.EditorFormsOpen > 0)
                        throw new WebAdminException(409, "A database editor window is open on the server (WinForms). Close it first: it edits the same data and saves it when it closes.",
                            new { editorFormsOpen = result.EditorFormsOpen });

                    if (result.UnsavedChanges > 0 && !req.FlushUnsaved)
                        throw new WebAdminException(409, "The editor copy has " + result.UnsavedChanges.ToString(CultureInfo.InvariantCulture) +
                                                         " other unsaved change(s). Save or discard them first, or send flushUnsaved:true to save them with this batch.",
                            new { unsavedChanges = result.UnsavedChanges });

                    _env.Persistence.EnsureSavable();
                }

                var ctx = _env.Edit();
                var plans = Resolve(req, ctx);

                // ---- 1. validate everything (dry run) -----------------------------------------------------------------------
                var t0 = Stopwatch.StartNew();
                foreach (var p in plans)
                {
                    CaptureRevs(ctx, p);
                    CaptureExpected(ctx, p);
                    p.Dry = GameDataEngine.Apply(ctx, p.Table, p.Ops.Rows, true, p.Ops.NoRev);
                    p.DryDelete = GameDataEngine.Delete(ctx, p.Table, p.Ops.Deletes, true);
                    Collect(result, p, p.Dry, p.DryDelete);
                    CheckDeleteRevs(result, p);
                }

                result.TimingsMs["validate"] = t0.ElapsedMilliseconds;

                if (result.Conflicts.Count > 0 || result.Validation.Count > 0)
                {
                    result.Status = "rejected";
                    result.HttpStatus = result.Validation.Count > 0 ? 422 : 409;
                    result.Message = "Nothing was changed: " + Plural(result.Conflicts.Count, "conflict") + ", " +
                                     Plural(result.Validation.Count, "validation problem") + ". The batch applies completely or not at all.";
                    _env.State.NoteBatch(result);
                    return result;
                }

                var running = world == "running";

                if (req.DryRun)
                {
                    foreach (var p in plans) Preview(result, p, running);
                    PreviewPushLimit(result);
                    result.Status = "dry-run";
                    result.Live.Outcome = "dry-run";
                    result.Live.RestartFields = result.RestartFields.Count;
                    result.Message = "Dry run: " + Plural(result.Applied, "row") + " would change" +
                                     (result.Deleted > 0 ? ", " + Plural(result.Deleted, "row") + " would be deleted" : string.Empty) + ". Nothing has been changed yet.";
                    AddNotes(result, req);
                    return result;
                }

                // ---- 2. apply to Edit, with before-images ------------------------------------------------------------------
                t0.Restart();
                var snapshot = EditSnapshot.Capture(ctx, plans.Select(p => (p.Table, p.Ops)).ToList());
                try
                {
                    foreach (var p in plans)
                    {
                        p.Real = GameDataEngine.Apply(ctx, p.Table, p.Ops.Rows, false, p.Ops.NoRev);
                        if (p.Real.Conflicts.Count > 0 || p.Real.Validation.Count > 0 || p.Real.Applied != p.Dry.Applied)
                            throw new InvalidOperationException("[LiveDb] " + p.Table.Endpoint + ": the validated batch did not apply identically");

                        p.RealDelete = GameDataEngine.Delete(ctx, p.Table, p.Ops.Deletes, false);
                        if (p.RealDelete.Rejects.Count > 0 || p.RealDelete.Deleted != p.DryDelete.Deleted)
                            throw new InvalidOperationException("[LiveDb] " + p.Table.Endpoint + ": the validated deletes did not apply identically");
                    }

                    ctx.Invalidate();
                }
                catch (Exception ex)
                {
                    snapshot.Restore(ctx);
                    _env.LogError(ex, "[LiveDb] batch " + req.Id + ": applying to the editor copy failed; restored");
                    throw new WebAdminException(500, "Applying the batch failed, see the server log. Nothing was changed.");
                }

                result.TimingsMs["apply"] = t0.ElapsedMilliseconds;

                // ---- 2b. NPC placement: a moved / new NPC must stand on a walkable cell of the running world's map ------------
                if (world == "running" && !CheckNpcPlacement(ctx, plans, result))
                {
                    snapshot.Restore(ctx);
                    result.Status = "rejected";
                    result.HttpStatus = 422;
                    result.Message = "Nothing was changed: " + Plural(result.Validation.Count, "validation problem") + ". The batch applies completely or not at all.";
                    _env.State.NoteBatch(result);
                    return result;
                }

                // ---- 3. save ---------------------------------------------------------------------------------------------------
                t0.Restart();
                LiveDbSaveOutcome saved;
                try
                {
                    saved = _env.Persistence.Save() ?? new LiveDbSaveOutcome();
                }
                catch (WebAdminException)
                {
                    snapshot.Restore(ctx);
                    throw;
                }
                catch (Exception ex)
                {
                    snapshot.Restore(ctx);
                    _env.LogError(ex, "[LiveDb] batch " + req.Id + ": saving Server.MirDB failed; the editor copy was restored");
                    throw new WebAdminException(500, "Saving the game database failed, see the server log. Nothing was changed.");
                }

                result.TimingsMs["save"] = t0.ElapsedMilliseconds;
                result.Saved = true;
                result.BackupFile = saved.BackupFile;
                if (saved.Areas != null) result.FlushedAreas.AddRange(saved.Areas);

                // ---- 4. live step --------------------------------------------------------------------------------------------------
                t0.Restart();
                var plan = new LiveDbLivePlan();
                var structural = new List<string>();
                foreach (var p in plans) Outcomes(result, ctx, p, plan, structural);
                ApplyPushLimit(result, plan);

                var worldNow = _env.WorldState() ?? "stopped";
                string notLiveReason = null;
                var liveApplied = false;
                LiveDbApplyResult applied = null;
                if (worldNow != "running")
                {
                    notLiveReason = "the world is not running; it loads the saved data when it starts";
                    result.Live.Outcome = "world-stopped";
                }
                else
                {
                    if (saved.Magic != null && saved.Magic.Count > 0) plan.MagicSnapshot = saved.Magic;

                    if (!plan.HasWork)
                    {
                        result.Live.Outcome = "none";
                    }
                    else
                    {
                        Exception failure = null;
                        bool ran;
                        try
                        {
                            ran = _env.RunOnGameThread(() =>
                            {
                                try
                                {
                                    applied = LiveDbMainApplier.Apply(plan, _env.Main(), _env.Hooks);
                                }
                                catch (Exception ex)
                                {
                                    failure = ex; // Main already restored by the applier
                                }
                            }, _env.Options.GameThreadTimeoutMs);
                        }
                        catch (GameThreadTimeoutException)
                        {
                            ran = false;
                            result.Live.Outcome = "timeout";
                            notLiveReason = "the game thread did not pick the change up in time; it takes effect after a restart";
                        }

                        if (failure != null)
                        {
                            result.Live.Outcome = "rolled-back";
                            notLiveReason = "applying it to the running world failed (the world was left unchanged); it takes effect after a restart";
                            _env.LogError(failure, "[LiveDb] batch " + req.Id + ": the live step failed; Main was restored, the save is kept");
                        }
                        else if (!ran)
                        {
                            if (result.Live.Outcome != "timeout")
                            {
                                result.Live.Outcome = "world-stopped";
                                notLiveReason = "the world stopped before the change could be applied; it loads the saved data when it starts";
                            }
                        }
                        else if (applied != null)
                        {
                            liveApplied = true;
                            result.Live.AppliedFields = applied.AppliedFields;
                            result.Live.MonstersQueued = applied.MonstersQueued;
                            result.Live.InsertedLive = applied.Inserted;
                            result.Live.DropsReloaded = applied.DropsReloaded;
                            result.Live.MagicSynced = applied.MagicSynced;
                            result.Live.ItemHoldersRefreshed = applied.ItemHoldersRefreshed;
                            result.Live.DeletedLive = applied.DeletedLive;
                            result.Live.RefusedLive = applied.Refused;
                            result.Live.PushSkipped = applied.PushSkipped;
                            result.Live.PushFailed = applied.PushFailed;
                            if (applied.Push != null)
                            {
                                result.Live.PushConnections = applied.Push.Connections;
                                result.Live.PushPackets = applied.Push.Packets;
                                result.Live.PushRows = applied.Push.Rows;
                            }
                            if (plan.MagicSnapshot != null) _env.MagicSynced(plan.MagicSnapshot);
                        }
                    }
                }

                FinishOutcomes(result, plan, notLiveReason, worldNow == "running");
                var anyRowNotLive = result.Rows.Any(r => r.RowLive == false); // e.g. an item delete (restart-only) of an undo
                if (liveApplied) result.Live.Outcome = result.RestartFields.Count > 0 || anyRowNotLive ? "partial" : "applied";
                else if (result.Live.Outcome == "none" && (result.RestartFields.Count > 0 || anyRowNotLive)) result.Live.Outcome = "partial";
                result.TimingsMs["live"] = t0.ElapsedMilliseconds;

                // Structural changes of position-keyed lists that did not reach Main: positions no longer line up (see LiveDbState).
                if (worldNow == "running")
                {
                    foreach (var key in structural) _env.State.MarkDiverged(key);
                    foreach (var ins in plan.Inserts)
                        if (ins.Table.PositionKeyed && ins.Table.IsChild && !(liveApplied && ins.Applied))
                            _env.State.MarkDiverged(LiveDbState.ParentKey(ins.Table.ParentEndpoint, ins.ParentIndex));
                    foreach (var d in plan.Deletes)
                        if (d.Table.PositionKeyed && d.Table.IsChild && !(liveApplied && d.Applied))
                            _env.State.MarkDiverged(LiveDbState.ParentKey(d.Table.ParentEndpoint, d.ParentIndex));
                    if (applied != null)
                        foreach (var key in applied.DivergedParents) _env.State.MarkDiverged(key);
                }

                // ---- 5. record -----------------------------------------------------------------------------------------------
                result.Status = "applied";
                result.Live.RestartFields = result.RestartFields.Count;
                var pendingParts = new List<string>();
                if (result.RestartFields.Count > 0) pendingParts.Add("restart-only fields " + string.Join(", ", result.RestartFields.Take(12)) + (result.RestartFields.Count > 12 ? ", ..." : string.Empty));
                var rowsNotLive = result.Rows.Count(r => r.RowLive == false);
                if (rowsNotLive > 0) pendingParts.Add(Plural(rowsNotLive, "added / deleted row"));
                if (notLiveReason != null && result.Rows.Count > 0) pendingParts.Add("live step: " + result.Live.Outcome);
                if (result.FlushedAreas.Count > 0) pendingParts.Add("other saved changes (" + string.Join(", ", result.FlushedAreas) + ")");
                if (pendingParts.Count > 0)
                {
                    _env.Pending("gamedata", "LiveDb batch " + req.Id + ": " + string.Join("; ", pendingParts));
                    result.PendingReboot = true;
                }

                result.Message = Message(result);
                AddNotes(result, req);
                result.TimingsMs["total"] = total.ElapsedMilliseconds;

                _env.Journal.Append(result);
                AuditRows(result, req);
                _env.State.NoteBatch(result);
                _env.Log("[LiveDb] batch " + req.Id + ": " + result.Rows.Count.ToString(CultureInfo.InvariantCulture) + " row(s), live " + result.Live.Outcome +
                         "; validate " + Ms(result, "validate") + " / apply " + Ms(result, "apply") + " / save " + Ms(result, "save") + " / live " + Ms(result, "live") + " ms");
                return result;
            }
        }

        // ================================================================== resolution / validation

        private static List<TablePlan> Resolve(LiveDbBatchRequest req, GameDataContext ctx)
        {
            var plans = new List<TablePlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ops in req.Tables)
            {
                var table = GameDataRegistry.Get(ops.Endpoint);
                if (table == null) throw new WebAdminException(404, "Unknown game-data table '" + (ops.Endpoint ?? string.Empty) + "'");
                if (!seen.Add(table.Endpoint)) throw new WebAdminException(400, "Table '" + table.Endpoint + "' appears twice in the batch; merge its rows");
                if (ops.Deletes.Count > 0 && !table.Deletable) throw new WebAdminException(400, table.Label + " rows cannot be deleted");

                // One op per row: a second edit of the same row would fail its __rev after the first one applied (no partial batches).
                var keys = new HashSet<int>();
                foreach (var row in ops.Rows)
                {
                    if (row.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (GameDataCodec.TryGetIndex(row, "Index", out var index) && index > 0 && !keys.Add(index))
                        throw new WebAdminException(400, table.Label + " #" + index.ToString(CultureInfo.InvariantCulture) + " appears twice in the batch");
                }

                foreach (var d in ops.Deletes)
                    if (!keys.Add(d)) throw new WebAdminException(400, table.Label + " #" + d.ToString(CultureInfo.InvariantCulture) + " is edited and deleted (or deleted twice) in the same batch");

                plans.Add(new TablePlan { Table = table, Ops = ops });
            }

            return plans;
        }

        private static void CaptureRevs(GameDataContext ctx, TablePlan p)
        {
            var wanted = new HashSet<int>();
            foreach (var row in p.Ops.Rows)
                if (row.ValueKind == System.Text.Json.JsonValueKind.Object && GameDataCodec.TryGetIndex(row, "Index", out var i) && i > 0) wanted.Add(i);
            foreach (var d in p.Ops.Deletes) wanted.Add(d);
            if (wanted.Count == 0) return;

            foreach (var row in p.Table.Rows(ctx))
                if (wanted.Contains(row.Index) && !p.RevBefore.ContainsKey(row.Index)) p.RevBefore[row.Index] = p.Table.Rev(row);
        }

        private static void Collect(LiveDbBatchResult result, TablePlan p, GameDataWriteResult w, GameDataDeleteResult d)
        {
            var ep = p.Table.Endpoint;
            foreach (var c in w.Conflicts) result.Conflicts.Add(Tag(ep, c));
            foreach (var v in w.Validation) result.Validation.Add(Tag(ep, v));
            foreach (var f in w.IgnoredFields) result.IgnoredFields.Add(ep + "." + f);
            foreach (var r in d.Rejects)
            {
                var code = GetProp(r, "code") as string;
                if (code == "not-found") result.Conflicts.Add(Tag(ep, r));
                else result.Validation.Add(Tag(ep, r));
            }
        }

        /// <summary>
        /// Rev-guarded deletes (<see cref="LiveDbTableOps.DeleteRevs"/>, sent by undo): a row whose current <c>__rev</c> differs from the
        /// one the request names was changed since (or, for position-keyed rows, another row now holds the key): a conflict.
        /// </summary>
        private static void CheckDeleteRevs(LiveDbBatchResult result, TablePlan p)
        {
            if (p.Ops.DeleteRevs == null || p.Ops.DeleteRevs.Count == 0) return;
            foreach (var pair in p.Ops.DeleteRevs)
            {
                if (string.IsNullOrEmpty(pair.Value) || !p.Ops.Deletes.Contains(pair.Key)) continue;
                if (!p.RevBefore.TryGetValue(pair.Key, out var current)) continue; // missing rows are reported by the delete itself
                if (string.Equals(current, pair.Value, StringComparison.Ordinal)) continue;
                result.Conflicts.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["endpoint"] = p.Table.Endpoint,
                    ["index"] = pair.Key,
                    ["message"] = "changed since (or another row now holds this index); it is not deleted",
                });
            }
        }

        private static void AddNotes(LiveDbBatchResult result, LiveDbBatchRequest req)
        {
            if (req.Notes == null || req.Notes.Count == 0) return;
            result.Notes.AddRange(req.Notes);
            result.Message = (result.Message ?? string.Empty) + " " + string.Join(" ", req.Notes);
        }

        private static object Tag(string endpoint, object entry)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal) { ["endpoint"] = endpoint };
            if (entry == null) return dict;
            foreach (var prop in entry.GetType().GetProperties())
                dict[prop.Name] = prop.GetValue(entry);
            return dict;
        }

        private static object GetProp(object entry, string name) => entry?.GetType().GetProperty(name)?.GetValue(entry);

        // ================================================================== outcomes

        private LiveDbFieldOutcome Field(string endpoint, GameDataChange change)
        {
            var rule = _env.Classify(endpoint, change.Field) ?? LiveColumnRule.Restart;
            return new LiveDbFieldOutcome
            {
                Field = change.Field,
                Before = change.Before,
                After = change.After,
                LiveClass = rule.Class.ToString(),
                LiveTiming = rule.Timing,
            };
        }

        private string DivergenceKey(TablePlan p, int parentIndex)
        {
            return p.Positional ? LiveDbState.ParentKey(p.Table.ParentEndpoint, parentIndex) : null;
        }

        private static int ParentOf(GameDataTable table, int index) => table.IsChild && table.PositionKeyed ? index / GameDataRegistry.Stride : 0;

        private const string DivergedReason = "the parent's rows changed structure since the world loaded them; saved only until a restart";

        /// <summary>
        /// Position-keyed child tables (safe zones, movements, mine zones; conquest children): the typed values of every row the batch
        /// updates or deletes and the length of every parent's list, as the editor copy holds them BEFORE the batch. The live step
        /// compares Main against them, so a list that no longer lines up is never changed by position. Worker thread, under EditLock.
        /// </summary>
        private static void CaptureExpected(GameDataContext ctx, TablePlan p)
        {
            if (!p.Positional) return;
            var wanted = new HashSet<int>();
            foreach (var row in p.Ops.Rows)
                if (row.ValueKind == System.Text.Json.JsonValueKind.Object && GameDataCodec.TryGetIndex(row, "Index", out var i) && i > 0) wanted.Add(i);
            foreach (var d in p.Ops.Deletes) wanted.Add(d);

            foreach (var row in p.Table.Rows(ctx))
            {
                p.ParentCounts.TryGetValue(row.ParentIndex, out var n);
                p.ParentCounts[row.ParentIndex] = n + 1;
                if (!wanted.Contains(row.Index) || p.Expected.ContainsKey(row.Index)) continue;

                var values = new List<KeyValuePair<GameDataColumn, object>>();
                foreach (var c in p.Table.Columns)
                    if (c.Get != null && !c.IsParentField) values.Add(new KeyValuePair<GameDataColumn, object>(c, c.Get(row)));
                p.Expected[row.Index] = values;
            }
        }

        /// <summary>
        /// After the editor copy was changed: every NPC the batch moves or adds must stand on a walkable cell of its map in the running
        /// world (one game-thread round trip). A cell that is not walkable rejects the whole batch (422, the editor copy is restored);
        /// a map the running world has not loaded is allowed (the NPC moves after a restart). True when the batch may continue.
        /// </summary>
        private bool CheckNpcPlacement(GameDataContext ctx, List<TablePlan> plans, LiveDbBatchResult result)
        {
            if (_env.Hooks == null) return true;
            var checks = new List<(int Index, int Map, System.Drawing.Point Location)>();
            foreach (var p in plans)
            {
                if (p.Table.Endpoint != "npcs" || p.Real == null || _env.Classify("npcs", "Location_X")?.IsLive != true) continue;
                foreach (var r in p.Real.Rows)
                {
                    var moves = r.Kind == "insert" && _env.InsertLive("npcs");
                    if (!moves)
                        foreach (var c in r.Changes)
                            if (LiveDbMainApplier.IsNpcPlacementColumn(c.Field)) moves = true;
                    if (!moves) continue;

                    var row = p.Table.Find(ctx, r.NewIndex);
                    if (row?.Entity is NPCInfo npc) checks.Add((r.NewIndex, npc.MapIndex, npc.Location));
                }
            }

            if (checks.Count == 0) return true;

            var verdicts = new LiveDbPlacement[checks.Count];
            bool ran;
            try
            {
                ran = _env.RunOnGameThread(() =>
                {
                    for (var i = 0; i < checks.Count; i++) verdicts[i] = _env.Hooks.CheckNpcPlacement(checks[i].Map, checks[i].Location);
                }, _env.Options.GameThreadTimeoutMs);
            }
            catch (GameThreadTimeoutException)
            {
                ran = false;
            }

            if (!ran) return true; // the live step checks again and refuses the move (saved only)

            for (var i = 0; i < checks.Count; i++)
            {
                if (verdicts[i] != LiveDbPlacement.Invalid) continue;
                result.Validation.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["endpoint"] = "npcs",
                    ["index"] = checks[i].Index,
                    ["field"] = "Location_X",
                    ["message"] = "(" + checks[i].Location.X.ToString(CultureInfo.InvariantCulture) + ", " + checks[i].Location.Y.ToString(CultureInfo.InvariantCulture) +
                                  ") is not a walkable cell of map " + checks[i].Map.ToString(CultureInfo.InvariantCulture) + " in the running world",
                });
            }

            return result.Validation.Count == 0;
        }

        /// <summary>Dry run: what would happen (fields that are live now, restart-only, refused by divergence).</summary>
        private void Preview(LiveDbBatchResult result, TablePlan p, bool running)
        {
            var ep = p.Table.Endpoint;
            foreach (var r in p.Dry.Rows)
            {
                var row = new LiveDbRowOutcome { Endpoint = ep, Index = r.Index, NewIndex = r.NewIndex, Kind = r.Kind, Name = r.Name };
                p.RevBefore.TryGetValue(r.Index, out var rev);
                row.RevBefore = rev;
                var parent = r.Kind == "insert" ? InsertParent(p, r) : ParentOf(p.Table, r.Index);
                var diverged = _env.State.IsDiverged(DivergenceKey(p, parent));
                foreach (var c in r.Changes)
                {
                    var f = Field(ep, c);
                    bool live;
                    string reason = null;
                    if (r.Kind == "insert")
                    {
                        live = _env.InsertLive(ep);
                        if (!live) reason = "new " + p.Table.Label + " rows take effect after a restart";
                        else if (diverged)
                        {
                            live = false;
                            reason = DivergedReason;
                        }
                    }
                    else
                    {
                        live = f.LiveClass != nameof(LiveClass.RestartOnly);
                        if (!live) reason = "restart-only field";
                        else if (diverged)
                        {
                            live = false;
                            reason = DivergedReason;
                        }
                    }

                    if (live && !running)
                    {
                        live = false;
                        reason = "the world is not running; it loads the saved data when it starts";
                    }

                    f.Live = live;
                    f.Reason = reason;
                    if (!live) AddRestart(result, ep, c.Field);
                    row.Changes.Add(f);
                }

                if (r.Kind == "insert")
                {
                    row.RowLive = _env.InsertLive(ep) && running && !diverged;
                    row.RowReason = row.RowLive == true ? null
                        : !running ? "the world is not running"
                        : !_env.InsertLive(ep) ? "new " + p.Table.Label + " rows take effect after a restart"
                        : DivergedReason;
                }

                result.Rows.Add(row);
                result.Applied++;
            }

            foreach (var removed in p.DryDelete.Removed)
            {
                var diverged = _env.State.IsDiverged(DivergenceKey(p, ParentOf(p.Table, removed.Key)));
                var live = _env.DeleteLive(ep) && running && !diverged;
                result.Rows.Add(new LiveDbRowOutcome
                {
                    Endpoint = ep, Index = removed.Key, NewIndex = removed.Key, Kind = "delete", Name = NameOf(p.Table, removed.Value),
                    RowLive = live,
                    RowReason = live ? null
                        : !running ? "the world is not running"
                        : !_env.DeleteLive(ep) ? "deletes take effect after a restart"
                        : DivergedReason,
                });
                result.Deleted++;
            }

            result.Renames.AddRange(p.Dry.Renames);
        }

        /// <summary>The parent index a dry-run insert names (child tables: the parent field of the sent row), else 0.</summary>
        private static int InsertParent(TablePlan p, GameDataRowResult r)
        {
            if (!p.Table.IsChild || string.IsNullOrEmpty(p.Table.ParentField)) return 0;
            foreach (var row in p.Ops.Rows)
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Object || !GameDataCodec.TryGetIndex(row, "Index", out var index) || index != r.Index) continue;
                return GameDataCodec.TryGetIndex(row, p.Table.ParentField, out var parent) ? parent : 0;
            }

            return 0;
        }

        /// <summary>After the save: row outcomes (revs, images), the live plan, and structural changes of position-keyed lists.</summary>
        private void Outcomes(LiveDbBatchResult result, GameDataContext ctx, TablePlan p, LiveDbLivePlan plan, List<string> structural)
        {
            var ep = p.Table.Endpoint;
            var byIndex = new Dictionary<int, GameDataRow>();
            foreach (var row in p.Table.Rows(ctx))
                if (!byIndex.ContainsKey(row.Index)) byIndex[row.Index] = row;

            var touchedParents = new HashSet<int>();

            foreach (var r in p.Real.Rows)
            {
                byIndex.TryGetValue(r.NewIndex, out var current);
                var row = new LiveDbRowOutcome { Endpoint = ep, Index = r.Index, NewIndex = r.NewIndex, Kind = r.Kind, Name = r.Name };
                p.RevBefore.TryGetValue(r.Index, out var revBefore);
                row.RevBefore = revBefore;
                row.RevAfter = current != null ? p.Table.Rev(current) : null;
                row.BeforeImage = new Dictionary<string, object>(StringComparer.Ordinal);
                row.AfterImage = r.Kind == "insert" && current != null ? p.Table.ToWire(current) : new Dictionary<string, object>(StringComparer.Ordinal);

                if (r.Kind == "insert")
                {
                    foreach (var c in r.Changes) row.Changes.Add(Field(ep, c));
                    var parent = current?.ParentIndex ?? 0;
                    var diverged = _env.State.IsDiverged(DivergenceKey(p, parent));
                    object clone = null;
                    if (_env.InsertLive(ep) && current != null && !diverged) clone = LiveDbMainApplier.PrepareInsert(p.Table, current.Entity);
                    if (clone != null)
                    {
                        plan.Inserts.Add(new LiveDbInsert { Table = p.Table, Clone = clone, ParentIndex = parent, Row = row });
                        if (p.Positional) touchedParents.Add(parent);
                    }
                    else
                    {
                        row.RowLive = false;
                        row.RowReason = diverged ? DivergedReason : "new " + p.Table.Label + " rows take effect after a restart";
                        foreach (var c in r.Changes) AddRestart(result, ep, c.Field);
                        if (current != null && p.Positional) structural.Add(LiveDbState.ParentKey(p.Table.ParentEndpoint, current.ParentIndex));
                    }
                }
                else
                {
                    var parent = ParentOf(p.Table, r.Index);
                    var diverged = _env.State.IsDiverged(DivergenceKey(p, parent));
                    var update = new LiveDbUpdate { Table = p.Table, Index = r.NewIndex, ParentIndex = current?.ParentIndex ?? parent, Row = row };
                    if (p.Positional && p.Expected.TryGetValue(r.Index, out var expected)) update.Expected = expected;
                    var typedByName = new Dictionary<string, KeyValuePair<GameDataColumn, object>>(StringComparer.Ordinal);
                    foreach (var t in r.Typed) typedByName[t.Key.Name] = t;

                    foreach (var c in r.Changes)
                    {
                        row.BeforeImage[c.Field] = c.Before;
                        row.AfterImage[c.Field] = c.After;
                        var f = Field(ep, c);
                        row.Changes.Add(f);
                        if (f.LiveClass == nameof(LiveClass.RestartOnly))
                        {
                            f.Live = false;
                            f.Reason = "restart-only field";
                            AddRestart(result, ep, c.Field);
                            continue;
                        }

                        if (diverged)
                        {
                            f.Live = false;
                            f.Reason = DivergedReason;
                            AddRestart(result, ep, c.Field);
                            continue;
                        }

                        if (typedByName.TryGetValue(c.Field, out var typed)) update.Values.Add(typed);
                    }

                    if (update.Values.Count > 0)
                    {
                        plan.Updates.Add(update);
                        if (p.Positional) touchedParents.Add(update.ParentIndex);
                    }
                }

                result.Rows.Add(row);
                result.Applied++;
            }

            foreach (var removed in p.RealDelete.Removed)
            {
                var parent = ParentOf(p.Table, removed.Key);
                if (p.Table.IsChild && !p.Table.PositionKeyed && removed.Value != null && removed.Value.TryGetValue(p.Table.ParentField ?? string.Empty, out var pv) &&
                    pv != null && int.TryParse(GameDataCodec.Canonical(pv), NumberStyles.Integer, CultureInfo.InvariantCulture, out var named))
                    parent = named;
                var diverged = _env.State.IsDiverged(DivergenceKey(p, parent));
                var row = new LiveDbRowOutcome
                {
                    Endpoint = ep, Index = removed.Key, NewIndex = removed.Key, Kind = "delete", Name = NameOf(p.Table, removed.Value),
                    RevBefore = p.RevBefore.TryGetValue(removed.Key, out var rb) ? rb : null,
                    BeforeImage = removed.Value,
                };

                if (_env.DeleteLive(ep) && !diverged)
                {
                    var d = new LiveDbDelete { Table = p.Table, Index = removed.Key, ParentIndex = parent, Row = row };
                    if (p.Positional && p.Expected.TryGetValue(removed.Key, out var expected)) d.Expected = expected;
                    plan.Deletes.Add(d);
                    if (p.Positional) touchedParents.Add(parent);
                }
                else
                {
                    row.RowLive = false;
                    row.RowReason = diverged ? DivergedReason : "deletes take effect after a restart";
                    if (p.Positional) structural.Add(LiveDbState.ParentKey(p.Table.ParentEndpoint, parent));
                }

                result.Rows.Add(row);
                result.Deleted++;
            }

            // Main's list of every touched parent must have the editor copy's pre-batch length (else nothing is changed by position).
            foreach (var parent in touchedParents)
            {
                p.ParentCounts.TryGetValue(parent, out var count);
                plan.ParentChecks.Add(new LiveDbParentCheck { Table = p.Table, ParentIndex = parent, ExpectedCount = count });
            }

            result.Renames.AddRange(p.Real.Renames);
        }

        /// <summary>
        /// Tables whose changed rows go to connected clients (S.GameDataChanged); they count against [LiveDb] MaxPushRows (items,
        /// monsters, spells; NPCs, quests and game-shop rows since Batch C).
        /// </summary>
        internal static bool IsPushTable(string endpoint) =>
            endpoint == "items" || endpoint == "monsters" || endpoint == "magic" || endpoint == "npcs" || endpoint == "quests" || endpoint == "gameshop";

        internal const string PushLimitReason = "the batch changes more client-visible rows than [LiveDb] MaxPushRows, so connected clients cannot be " +
                                                "updated; item changes take effect after a restart (split the batch to apply them live)";

        /// <summary>
        /// [LiveDb] MaxPushRows: a batch that changes more client-visible rows (items, monsters, spells, NPCs, quests, game-shop rows,
        /// added / deleted game-shop rows) than the limit is not pushed. Item updates are then refused live (the server and the clients
        /// would compute different stats and tooltips); the other changes still apply to the running world and reach clients at their
        /// next login.
        /// </summary>
        private void ApplyPushLimit(LiveDbBatchResult result, LiveDbLivePlan plan)
        {
            var rows = 0;
            foreach (var u in plan.Updates)
                if (IsPushTable(u.Table.Endpoint)) rows++;
            foreach (var ins in plan.Inserts)
                if (ins.Table.Endpoint == "gameshop") rows++;
            foreach (var d in plan.Deletes)
                if (d.Table.Endpoint == "gameshop") rows++;
            if (rows <= _env.Options.MaxPushRows) return;

            plan.SkipPush = true;
            for (var i = plan.Updates.Count - 1; i >= 0; i--)
            {
                var u = plan.Updates[i];
                if (u.Table.Endpoint != "items") continue;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in u.Values) names.Add(v.Key.Name);
                foreach (var f in u.Row.Changes)
                {
                    if (!names.Contains(f.Field)) continue;
                    f.Live = false;
                    f.Reason = PushLimitReason;
                    AddRestart(result, u.Row.Endpoint, f.Field);
                }

                plan.Updates.RemoveAt(i);
            }
        }

        /// <summary>The dry-run prediction of <see cref="ApplyPushLimit"/>.</summary>
        private void PreviewPushLimit(LiveDbBatchResult result)
        {
            var rows = 0;
            foreach (var r in result.Rows)
            {
                if (r.Kind == "update" && IsPushTable(r.Endpoint) && r.Changes.Any(c => c.Live)) rows++;
                else if (r.Kind != "update" && r.Endpoint == "gameshop" && r.RowLive == true) rows++;
            }

            if (rows <= _env.Options.MaxPushRows) return;

            foreach (var r in result.Rows)
            {
                if (r.Kind != "update" || r.Endpoint != "items") continue;
                foreach (var f in r.Changes)
                {
                    if (!f.Live) continue;
                    f.Live = false;
                    f.Reason = PushLimitReason;
                    AddRestart(result, r.Endpoint, f.Field);
                }
            }
        }

        /// <summary>Marks every planned live field / insert / delete with the live step's outcome (refusals of the running world included).</summary>
        private static void FinishOutcomes(LiveDbBatchResult result, LiveDbLivePlan plan, string notLiveReason, bool running)
        {
            foreach (var u in plan.Updates)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var v in u.Values) names.Add(v.Key.Name);
                foreach (var f in u.Row.Changes)
                {
                    if (!names.Contains(f.Field)) continue;
                    string reason = null;
                    if (notLiveReason != null) reason = notLiveReason;
                    else if (u.RowRefusal != null) reason = u.RowRefusal;
                    else if (!u.Found) reason = LiveDbMainApplier.NoRowReason;
                    else if (u.FieldRefusals != null && u.FieldRefusals.TryGetValue(f.Field, out var refused)) reason = refused;

                    f.Live = reason == null;
                    f.Reason = reason;
                    if (reason != null) AddRestart(result, u.Row.Endpoint, f.Field);
                }
            }

            foreach (var ins in plan.Inserts)
            {
                var reason = notLiveReason ?? (!running ? "the world is not running" : ins.Applied ? null : ins.Refusal ?? "not applied to the running world");
                var live = reason == null;
                ins.Row.RowLive = live;
                ins.Row.RowReason = live ? ins.Note : reason;
                foreach (var f in ins.Row.Changes)
                {
                    f.Live = live;
                    f.Reason = live ? null : reason;
                }

                if (!live)
                    foreach (var f in ins.Row.Changes)
                        AddRestart(result, ins.Row.Endpoint, f.Field);
            }

            foreach (var d in plan.Deletes)
            {
                var reason = notLiveReason ?? (!running ? "the world is not running" : d.Applied ? null : d.Refusal ?? "not applied to the running world");
                d.Row.RowLive = reason == null;
                d.Row.RowReason = reason;
            }
        }

        private static void AddRestart(LiveDbBatchResult result, string endpoint, string field)
        {
            var key = endpoint + "." + field;
            if (!result.RestartFields.Contains(key)) result.RestartFields.Add(key);
        }

        private static string NameOf(GameDataTable table, Dictionary<string, object> wire)
        {
            if (table.NameColumn != null && wire != null && wire.TryGetValue(table.NameColumn, out var v) && v is string s && !string.IsNullOrWhiteSpace(s)) return s;
            return "#" + (wire != null && wire.TryGetValue("Index", out var i) ? GameDataCodec.Canonical(i) : "?");
        }

        // ================================================================== recording

        private void AuditRows(LiveDbBatchResult result, LiveDbBatchRequest req)
        {
            foreach (var row in result.Rows)
            {
                var action = row.Kind == "insert" ? "db.insert" : row.Kind == "delete" ? "db.delete" : "db.update";
                object details;
                if (row.Kind == "delete")
                {
                    details = new { endpoint = row.Endpoint, index = row.NewIndex, before = row.BeforeImage, batch = result.Id, inverseOf = result.InverseOf, live = row.RowLive == true };
                }
                else
                {
                    details = new
                    {
                        endpoint = row.Endpoint,
                        index = row.NewIndex,
                        name = row.Name,
                        changes = row.Changes.Select(c => new { field = c.Field, before = c.Before, after = c.After }).ToList(),
                        batch = result.Id,
                        inverseOf = result.InverseOf,
                        liveFields = row.Changes.Where(c => c.Live).Select(c => c.Field).ToList(),
                        restartFields = row.Changes.Where(c => !c.Live).Select(c => c.Field).ToList(),
                        noRev = req.Tables.Any(t => t.NoRev && string.Equals(t.Endpoint, row.Endpoint, StringComparison.OrdinalIgnoreCase)) ? true : (bool?)null,
                    };
                }

                _env.Audit(action, row.Endpoint + "#" + row.NewIndex.ToString(CultureInfo.InvariantCulture), details, result.Reason);
            }

            _env.Audit("db.livedb-batch", "batch:" + result.Id, new
            {
                id = result.Id,
                source = result.Source,
                inverseOf = result.InverseOf,
                rows = result.Rows.Count,
                applied = result.Applied,
                deleted = result.Deleted,
                saved = result.Saved,
                backupFile = result.BackupFile,
                live = result.Live.Outcome,
                liveFields = result.Live.AppliedFields,
                restartFields = result.RestartFields,
                flushedAreas = result.FlushedAreas,
            }, result.Reason);
        }

        private static string Message(LiveDbBatchResult r)
        {
            var parts = new List<string>
            {
                "Saved " + Plural(r.Applied, "row") + (r.Deleted > 0 ? " and deleted " + Plural(r.Deleted, "row") : string.Empty) + " to Server.MirDB" +
                (string.IsNullOrEmpty(r.BackupFile) ? "." : " (previous file: Back Up\\Database\\" + r.BackupFile + ")."),
            };

            switch (r.Live.Outcome)
            {
                case "world-stopped":
                    parts.Add("The world is not running: it loads the changes when it starts.");
                    break;
                case "rolled-back":
                    parts.Add("Applying to the running world failed and was undone; the changes take effect after a restart.");
                    break;
                case "timeout":
                    parts.Add("The game thread did not respond in time; the changes take effect after a restart.");
                    break;
                default:
                    if (r.Live.AppliedFields > 0 || r.Live.InsertedLive > 0 || r.Live.DeletedLive > 0)
                        parts.Add("Live: " + Plural(r.Live.AppliedFields, "field") + " applied to the running world" +
                                  (r.Live.MonstersQueued > 0 ? ", spawned monsters of " + Plural(r.Live.MonstersQueued, "kind") + " refreshing" : string.Empty) +
                                  (r.Live.InsertedLive > 0 ? ", " + Plural(r.Live.InsertedLive, "new row") + " added" : string.Empty) +
                                  (r.Live.DeletedLive > 0 ? ", " + Plural(r.Live.DeletedLive, "row") + " removed" : string.Empty) +
                                  (r.Live.ItemHoldersRefreshed > 0 ? ", " + Plural(r.Live.ItemHoldersRefreshed, "item holder") + " refreshed" : string.Empty) +
                                  (r.Live.PushConnections > 0 ? ", " + Plural(r.Live.PushConnections, "connected client") + " updated" : string.Empty) + ".");
                    if (r.Live.RefusedLive > 0) parts.Add("The running world refused " + Plural(r.Live.RefusedLive, "change") + " (saved; see the reasons per field / row).");
                    if (r.Live.PushSkipped) parts.Add("Connected clients were not updated (more than [LiveDb] MaxPushRows client-visible rows); they see the change at their next login.");
                    if (r.Live.PushFailed) parts.Add("Updating connected clients failed (see the server log); they see the change at their next login.");
                    break;
            }

            if (r.RestartFields.Count > 0) parts.Add("Restart needed for: " + string.Join(", ", r.RestartFields.Take(10)) + (r.RestartFields.Count > 10 ? ", ..." : string.Empty) + ".");
            return string.Join(" ", parts);
        }

        private static LiveDbBatchResult AsDuplicate(LiveDbBatchResult prior)
        {
            var copy = prior.ShallowCopy(); // the stored record stays as it was
            copy.Duplicate = true;
            copy.HttpStatus = 200;
            return copy;
        }

        private static string Plural(int n, string what) => n.ToString(CultureInfo.InvariantCulture) + " " + what + (n == 1 ? string.Empty : "s");

        private static string Ms(LiveDbBatchResult r, string key) => r.TimingsMs.TryGetValue(key, out var v) ? v.ToString(CultureInfo.InvariantCulture) : "-";

        // ================================================================== Edit before-images

        /// <summary>
        /// Before-images of the editor copy for one batch: clones of every row the batch updates (values restored through the columns),
        /// copies of every list a table with inserts / deletes keeps its rows in, and the index counters. Restoring puts Edit back exactly
        /// as it was before the batch (an allocated index stays allocated only if the context cannot capture counters).
        /// </summary>
        private sealed class EditSnapshot
        {
            private readonly List<KeyValuePair<GameDataTable, KeyValuePair<GameDataRow, GameDataRow>>> _rows =
                new List<KeyValuePair<GameDataTable, KeyValuePair<GameDataRow, GameDataRow>>>();
            private readonly List<KeyValuePair<System.Collections.IList, object[]>> _lists = new List<KeyValuePair<System.Collections.IList, object[]>>();
            private object _counters;

            public static EditSnapshot Capture(GameDataContext ctx, List<(GameDataTable Table, LiveDbTableOps Ops)> tables)
            {
                var s = new EditSnapshot { _counters = ctx.CaptureCounters?.Invoke() };
                foreach (var (table, ops) in tables)
                {
                    var wanted = new HashSet<int>();
                    var structural = ops.Deletes.Count > 0;
                    foreach (var row in ops.Rows)
                    {
                        if (row.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (GameDataCodec.TryGetIndex(row, "Index", out var i) && i > 0) wanted.Add(i);
                        else structural = true; // insert
                    }

                    if (wanted.Count > 0)
                        foreach (var row in table.Rows(ctx))
                            if (wanted.Remove(row.Index)) s._rows.Add(new KeyValuePair<GameDataTable, KeyValuePair<GameDataRow, GameDataRow>>(table,
                                new KeyValuePair<GameDataRow, GameDataRow>(row, GameDataClone.CloneRow(row))));

                    if (structural && table.Lists != null)
                        foreach (var list in table.Lists(ctx))
                        {
                            var copy = new object[list.Count];
                            list.CopyTo(copy, 0);
                            s._lists.Add(new KeyValuePair<System.Collections.IList, object[]>(list, copy));
                        }
                }

                return s;
            }

            public void Restore(GameDataContext ctx)
            {
                foreach (var l in _lists)
                {
                    l.Key.Clear();
                    foreach (var o in l.Value) l.Key.Add(o);
                }

                foreach (var entry in _rows)
                {
                    var table = entry.Key;
                    var live = entry.Value.Key;
                    var before = entry.Value.Value;
                    foreach (var c in table.Columns)
                    {
                        if (c.Set == null || c.ReadOnly || c.IsParentField) continue;
                        c.Set(live, c.Get(before));
                    }

                    table.AfterApply?.Invoke(ctx, live);
                }

                if (_counters != null) ctx.RestoreCounters?.Invoke(_counters);
                ctx.Invalidate();
            }
        }
    }
}
