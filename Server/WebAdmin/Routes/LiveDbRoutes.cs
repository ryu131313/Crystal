using System.Globalization;
using Server.MirEnvir;
using Server.WebAdmin.GameData;
using Server.WebAdmin.LiveDb;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// LiveDb: live game-data edits (see <see cref="LiveDbEngine"/>).
    /// <list type="bullet">
    /// <item>GET  /api/admin/livedb/status → {enabled, world, editorFormsOpen, unsavedChanges, liveTables, sweeper, diverged, ...}</item>
    /// <item>GET  /api/admin/livedb/schema/{endpoint} → per column {name, liveClass, liveTiming, note, plannedClass}</item>
    /// <item>POST /api/admin/livedb/batch[?dryRun=1] {id, reason?, flushUnsaved?, tables:[{endpoint, rows, deletes}]} → the batch outcome</item>
    /// <item>GET  /api/admin/livedb/history[?limit=&amp;offset=&amp;days=&amp;table=&amp;admin=&amp;from=&amp;to=] → recent batches (journal), newest
    /// first, with live counters, <c>inverseOf</c> / <c>undoneBy</c></item>
    /// <item>GET  /api/admin/livedb/history/{id} → one batch with every row's before / after values and whether it can be undone</item>
    /// <item>POST /api/admin/livedb/undo/{id}[?dryRun=1] {reason?, flushUnsaved?} → the inverse batch's outcome (<see cref="LiveDbUndo"/>)</item>
    /// </list>
    /// The batch runs on a <c>Task.Run</c> worker that holds <see cref="WebAdminGame.EditLock"/> while the save and the live step may
    /// wait for the game thread (the queued work takes no web admin lock). CSRF + admin auth as every mutating admin route.
    /// </summary>
    public sealed class LiveDbRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            LiveDbState.EnsureHooks();
            router.Get("/api/admin/livedb/status", GetStatus);
            router.Get("/api/admin/livedb/schema/{endpoint}", GetSchema);
            router.Post("/api/admin/livedb/batch", PostBatch);
            router.Get("/api/admin/livedb/history", GetHistory);
            router.Get("/api/admin/livedb/history/{id}", GetHistoryItem);
            router.Post("/api/admin/livedb/undo/{id}", PostUndo);
        }

        /// <summary>The compact LiveDb block of GET /api/admin/status (dashboard tile): cheap reads only.</summary>
        internal static object StatusBlock(bool enabled)
        {
            var state = LiveDbState.Current.Snapshot();
            var diverged = state["diverged"] as List<string> ?? new List<string>();
            return new
            {
                enabled,
                batches = state["batches"],
                rejected = state["rejected"],
                liveFailures = state["liveFailures"],
                undos = state["undos"],
                lastBatchId = state["lastBatchId"],
                lastBatchUtc = state["lastBatchUtc"],
                lastLiveOutcome = state["lastLiveOutcome"],
                lastSaved = state["lastSaved"],
                lastInverseOf = state["lastInverseOf"],
                sweeperActive = LiveDbSweeper.HasWork,
                sweeperPending = LiveDbSweeper.PendingKinds,
                divergedCount = diverged.Count,
                diverged = diverged.Count > 20 ? diverged.GetRange(0, 20) : diverged,
            };
        }

        private static Task GetStatus(WebAdminRequest req)
        {
            var state = LiveDbState.Current.Snapshot();
            return req.Json(new
            {
                enabled = Settings.LiveDbEnabled,
                world = WebAdminGame.WorldState,
                editorFormsOpen = Envir.EditorFormsOpen,
                unsavedChanges = GameDataState.Unsaved.Changes,
                systemDataStale = Envir.SystemDataStale,
                liveTables = LiveDbClassification.LiveTables(),
                sweeper = new { active = LiveDbSweeper.HasWork, pendingKinds = LiveDbSweeper.PendingKinds, refreshedTotal = LiveDbSweeper.RefreshedTotal },
                batches = state["batches"],
                rejected = state["rejected"],
                liveFailures = state["liveFailures"],
                lastBatchId = state["lastBatchId"],
                lastBatchUtc = state["lastBatchUtc"],
                lastLiveOutcome = state["lastLiveOutcome"],
                lastSaved = state["lastSaved"],
                lastInverseOf = state["lastInverseOf"],
                undos = state["undos"],
                diverged = state["diverged"],
                options = new
                {
                    backupEveryMinutes = Settings.LiveDbBackupEveryMinutes,
                    sweepPerTick = Settings.LiveDbSweepPerTick,
                    maxBatchRows = Settings.LiveDbMaxBatchRows,
                    maxPushRows = Settings.LiveDbMaxPushRows,
                },
            });
        }

        private static Task GetSchema(WebAdminRequest req)
        {
            var endpoint = req.Route["endpoint"];
            var table = GameDataRegistry.Get(endpoint);
            if (table == null) return req.Error(404, "Unknown game-data table '" + (endpoint ?? string.Empty) + "'");
            return req.Json(Schema(table));
        }

        internal static object Schema(GameDataTable table)
        {
            var rule = LiveDbClassification.Table(table.Endpoint);
            return new
            {
                endpoint = table.Endpoint,
                live = LiveDbClassification.IsLiveTable(table.Endpoint),
                insertLive = LiveDbClassification.InsertLive(table.Endpoint),
                deleteLive = LiveDbClassification.DeleteLive(table.Endpoint),
                plannedBatch = rule?.PlannedBatch,
                positionKeyed = table.PositionKeyed,
                columns = table.Columns.Where(c => !c.IsParentField).Select(c =>
                {
                    var eff = LiveDbClassification.Effective(table.Endpoint, c.Name);
                    var planned = LiveDbClassification.Planned(table.Endpoint, c.Name);
                    return new
                    {
                        name = c.Name,
                        liveClass = eff.Class.ToString(),
                        liveTiming = eff.Timing,
                        note = (planned ?? eff).Note,
                        plannedClass = (planned ?? LiveColumnRule.Restart).Class.ToString(),
                        plannedTiming = (planned ?? LiveColumnRule.Restart).Timing,
                    };
                }).ToList(),
            };
        }

        private static async Task PostBatch(WebAdminRequest req)
        {
            if (!Settings.LiveDbEnabled)
                throw new WebAdminException(409, "LiveDb is disabled ([LiveDb] Enabled=False): use the grid's Apply + Save DB instead.");

            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var batch = LiveDbBatchRequest.Parse(body); // parse on the HTTP thread
            batch.DryRun = batch.DryRun || req.QueryBool("dryRun");
            batch.Admin = req.AdminName;
            batch.Ip = req.ClientIp;
            batch.Source = "livedb";

            var result = await RunAsync(batch).ConfigureAwait(false);
            await req.Json(Reply(result), result.HttpStatus).ConfigureAwait(false);
        }

        /// <summary>Runs a batch on a worker (EditLock is held there while the save / live step wait for the game thread).</summary>
        internal static Task<LiveDbBatchResult> RunAsync(LiveDbBatchRequest batch)
        {
            return Task.Run(() =>
            {
                var engine = new LiveDbEngine(LiveDbProduction.Environment(batch.Admin, batch.Ip));
                try
                {
                    return engine.Run(batch);
                }
                catch (WebAdminException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] batch " + batch.Id + " failed");
                    throw new WebAdminException(500, "The LiveDb batch failed, see the server log");
                }
            });
        }

        internal static object Reply(LiveDbBatchResult r)
        {
            return new
            {
                ok = r.Status != "rejected",
                message = r.Message,
                id = r.Id,
                status = r.Status,
                duplicate = r.Duplicate,
                inverseOf = r.InverseOf,
                notes = r.Notes,
                dryRun = r.Status == "dry-run",
                saved = r.Saved,
                backupFile = r.BackupFile,
                world = r.World,
                applied = r.Applied,
                deleted = r.Deleted,
                rows = r.Rows,
                conflicts = r.Conflicts,
                validation = r.Validation,
                ignoredFields = r.IgnoredFields,
                renames = r.Renames,
                restartFields = r.RestartFields,
                live = r.Live,
                pendingReboot = r.PendingReboot,
                flushedAreas = r.FlushedAreas,
                unsavedChanges = r.UnsavedChanges,
                editorFormsOpen = r.EditorFormsOpen,
                timingsMs = r.TimingsMs,
            };
        }

        // ================================================================== history

        /// <summary>Most recent batches scanned for the history list (the journal ring + files of the requested days).</summary>
        internal const int HistoryScanLimit = 1000;

        private static Task GetHistory(WebAdminRequest req)
        {
            var limit = Math.Clamp(req.QueryInt("limit", 50), 1, 500);
            var offset = Math.Max(0, req.QueryInt("offset", 0));
            var days = Math.Clamp(req.QueryInt("days", 7), 0, 366);
            var filter = new HistoryFilter
            {
                Table = Trim(req.Query["table"]),
                Admin = Trim(req.Query["admin"]),
                From = ParseDate(req.Query["from"], false),
                To = ParseDate(req.Query["to"], true),
            };
            if (filter.From.HasValue)
                days = Math.Clamp((int)Math.Ceiling((DateTime.UtcNow - filter.From.Value).TotalDays) + 1, 0, 366);

            return req.Json(History(LiveDbJournal.Current.Recent(HistoryScanLimit, days), filter, offset, limit));
        }

        internal sealed class HistoryFilter
        {
            public string Table;
            public string Admin;
            public DateTime? From;
            public DateTime? To;

            public bool Matches(LiveDbBatchResult r)
            {
                if (From.HasValue && r.Utc < From.Value) return false;
                if (To.HasValue && r.Utc > To.Value) return false;
                if (!string.IsNullOrEmpty(Admin) && (r.Admin ?? string.Empty).IndexOf(Admin, StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (!string.IsNullOrEmpty(Table) && !r.Rows.Any(x => string.Equals(x.Endpoint, Table, StringComparison.OrdinalIgnoreCase))) return false;
                return true;
            }
        }

        /// <summary>The history list (pure over the scanned records, newest first): filtered, paged, with <c>undoneBy</c> per batch.</summary>
        internal static object History(IReadOnlyList<LiveDbBatchResult> scanned, HistoryFilter filter, int offset, int limit)
        {
            var undoneBy = UndoneBy(scanned);
            var matching = scanned.Where(r => filter == null || filter.Matches(r)).ToList();
            var page = matching.Skip(offset).Take(limit).Select(r => new
            {
                id = r.Id,
                utc = r.Utc,
                admin = r.Admin,
                reason = r.Reason,
                source = r.Source,
                world = r.World,
                inverseOf = r.InverseOf,
                undoneBy = undoneBy.TryGetValue(r.Id, out var u) ? u : null,
                applied = r.Applied,
                deleted = r.Deleted,
                saved = r.Saved,
                backupFile = r.BackupFile,
                live = r.Live?.Outcome,
                liveFields = r.Live?.AppliedFields ?? 0,
                counters = Counters(r.Live),
                restartFields = r.RestartFields,
                tables = r.Rows.Select(x => x.Endpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                undoable = r.Rows.Count > 0 && r.Rows.All(x => x.Kind != "delete"),
                rows = r.Rows.Select(x => new { endpoint = x.Endpoint, index = x.NewIndex, kind = x.Kind, name = x.Name, fields = x.Changes.Select(c => c.Field).ToList() }).ToList(),
                message = r.Message,
            }).ToList();

            return new
            {
                items = page,
                total = matching.Count,
                offset,
                limit,
                scanned = scanned.Count,
                truncated = scanned.Count >= HistoryScanLimit,
            };
        }

        /// <summary>batch id → the id of the applied undo that currently reverses it (an undo that was itself undone does not count).</summary>
        internal static Dictionary<string, string> UndoneBy(IReadOnlyList<LiveDbBatchResult> records)
        {
            var undoers = new Dictionary<string, List<LiveDbBatchResult>>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (string.IsNullOrEmpty(r.InverseOf) || r.Status != "applied") continue;
                if (!undoers.TryGetValue(r.InverseOf, out var list)) undoers[r.InverseOf] = list = new List<LiveDbBatchResult>();
                list.Add(r);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var memo = new Dictionary<string, string>(StringComparer.Ordinal);

            string Resolve(string id, int depth)
            {
                if (memo.TryGetValue(id, out var known)) return known;
                string by = null;
                if (depth < 32 && undoers.TryGetValue(id, out var list))
                {
                    foreach (var u in list.OrderByDescending(x => x.Utc))
                    {
                        if (Resolve(u.Id, depth + 1) != null) continue; // that undo was undone again (redo)
                        by = u.Id;
                        break;
                    }
                }

                memo[id] = by;
                return by;
            }

            foreach (var r in records)
            {
                var by = Resolve(r.Id, 0);
                if (by != null) result[r.Id] = by;
            }

            return result;
        }

        private static object Counters(LiveDbLiveOutcome live)
        {
            if (live == null) return null;
            return new
            {
                appliedFields = live.AppliedFields,
                restartFields = live.RestartFields,
                insertedLive = live.InsertedLive,
                deletedLive = live.DeletedLive,
                refusedLive = live.RefusedLive,
                monstersQueued = live.MonstersQueued,
                dropsReloaded = live.DropsReloaded,
                magicSynced = live.MagicSynced,
                itemHoldersRefreshed = live.ItemHoldersRefreshed,
                pushConnections = live.PushConnections,
                pushPackets = live.PushPackets,
                pushRows = live.PushRows,
                pushSkipped = live.PushSkipped,
                pushFailed = live.PushFailed,
            };
        }

        private static Task GetHistoryItem(WebAdminRequest req)
        {
            var id = (req.Route["id"] ?? string.Empty).Trim();
            if (!LiveDbBatchRequest.IsValidId(id)) return req.Error(400, "Invalid batch id");
            var journal = LiveDbJournal.Current;
            var r = journal.Find(id);
            if (r == null) return req.Error(404, "Batch '" + id + "' is not in the LiveDb journal (the last " + LiveDbJournal.LookupDays.ToString(CultureInfo.InvariantCulture) + " days)");
            return req.Json(Detail(journal, r));
        }

        /// <summary>One batch for the history view: rows with before / after values, live counters, undo state (no Edit access).</summary>
        internal static object Detail(LiveDbJournal journal, LiveDbBatchResult r)
        {
            var (undoId, prior) = LiveDbUndo.ResolveId(journal, r.Id);
            var plan = LiveDbUndo.Plan(r, undoId);
            return new
            {
                id = r.Id,
                utc = r.Utc,
                admin = r.Admin,
                reason = r.Reason,
                source = r.Source,
                world = r.World,
                inverseOf = r.InverseOf,
                notes = r.Notes,
                saved = r.Saved,
                backupFile = r.BackupFile,
                applied = r.Applied,
                deleted = r.Deleted,
                live = r.Live,
                restartFields = r.RestartFields,
                flushedAreas = r.FlushedAreas,
                pendingReboot = r.PendingReboot,
                message = r.Message,
                timingsMs = r.TimingsMs,
                rows = r.Rows.Select(x => new
                {
                    endpoint = x.Endpoint,
                    index = x.NewIndex,
                    kind = x.Kind,
                    name = x.Name,
                    revBefore = x.RevBefore,
                    revAfter = x.RevAfter,
                    rowLive = x.RowLive,
                    rowReason = x.RowReason,
                    changes = x.Changes.Select(c => new { field = c.Field, before = c.Before, after = c.After, live = c.Live, liveClass = c.LiveClass, reason = c.Reason }).ToList(),
                    image = x.Kind == "delete" ? x.BeforeImage : x.Kind == "insert" ? x.AfterImage : null,
                }).ToList(),
                undo = new
                {
                    undoId,
                    undoneBy = prior?.Id,
                    undoable = prior == null && plan.Undoable,
                    refused = plan.Refused,
                    notes = plan.Notes,
                    updates = plan.Updates,
                    deletes = plan.Deletes,
                },
            };
        }

        // ================================================================== undo

        private static async Task PostUndo(WebAdminRequest req)
        {
            if (!Settings.LiveDbEnabled)
                throw new WebAdminException(409, "LiveDb is disabled ([LiveDb] Enabled=False): undo needs it.");

            var id = (req.Route["id"] ?? string.Empty).Trim();
            if (!LiveDbBatchRequest.IsValidId(id)) throw new WebAdminException(400, "Invalid batch id");

            string reason = null;
            var flush = req.QueryBool("flushUnsaved");
            var dryRun = req.QueryBool("dryRun");
            var body = await req.ReadJsonAsync().ConfigureAwait(false); // parse on the HTTP thread (empty body → Undefined)
            if (body.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                reason = body.GetString("reason");
                flush = flush || body.GetBool("flushUnsaved");
                dryRun = dryRun || body.GetBool("dryRun");
            }

            var admin = req.AdminName;
            var ip = req.ClientIp;
            var outcome = await Task.Run(() =>
            {
                var env = LiveDbProduction.Environment(admin, ip);
                try
                {
                    return LiveDbUndo.Run(env, id, dryRun, admin, ip, reason, flush);
                }
                catch (WebAdminException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] undo of batch " + id + " failed");
                    throw new WebAdminException(500, "The LiveDb undo failed, see the server log");
                }
            }).ConfigureAwait(false);

            await req.Json(UndoReply(outcome), outcome.Result.HttpStatus).ConfigureAwait(false);
        }

        /// <summary>The batch reply plus {target, undoId, alreadyUndone, undoNotes}.</summary>
        internal static Dictionary<string, object> UndoReply(LiveDbUndoOutcome o)
        {
            var r = o.Result;
            var reply = new Dictionary<string, object>(StringComparer.Ordinal);
            var batch = Reply(r);
            foreach (var p in batch.GetType().GetProperties()) reply[p.Name] = p.GetValue(batch);
            reply["target"] = o.TargetId;
            reply["undoId"] = o.UndoId;
            reply["alreadyUndone"] = o.AlreadyUndone;
            if (o.AlreadyUndone) reply["message"] = "Batch " + o.TargetId + " is already undone by " + o.UndoId + ". " + r.Message;
            reply["undoNotes"] = o.Plan?.Notes ?? new List<string>();
            return reply;
        }

        private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>An ISO date (<c>2026-10-10</c>, UTC; <paramref name="endOfDay"/> → its last tick) or date-time; null when absent / invalid.</summary>
        internal static DateTime? ParseDate(string s, bool endOfDay)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day))
                return endOfDay ? day.AddDays(1).AddTicks(-1) : day;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return t;
            return null;
        }
    }
}
