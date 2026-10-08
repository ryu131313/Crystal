using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Game-data grid engine over the Edit envir (contract §3.4, feature B8):
    /// <list type="bullet">
    /// <item>GET  /api/admin/db/tables → {tables:[{endpoint, label, group, count, insertable, deletable, parent?}]}</item>
    /// <item>GET  /api/admin/db/schema/{endpoint} → schema (admin-schema.js shape)</item>
    /// <item>GET  /api/admin/db/{endpoint}[?Field=v&amp;q=&amp;sort=&amp;dir=&amp;page=&amp;pageSize=] → {rows, total}</item>
    /// <item>PUT  /api/admin/db/{endpoint}[?dryRun=1&amp;noRev=1&amp;live=1] {rows, reason?} → {ok, applied, conflicts, validation, ignoredFields, pendingReboot, ...}</item>
    /// <item>POST /api/admin/db/{endpoint}/delete {indexes, reason?, dryRun?} → {ok, deleted, rejects}</item>
    /// <item>POST /api/admin/db/rename-propagate {endpoint, old, new, dryRun} → {files:[{root, path, replacements, renameTo?}]}</item>
    /// <item>POST /api/admin/database/save · GET /api/admin/database/status · POST /api/admin/database/reload-edit</item>
    /// </list>
    /// Edits change <c>Envir.Edit</c> under <see cref="WebAdminGame.EditLock"/> and stay in memory until database/save
    /// (Edit.SaveDB() + a pending-reboot entry). Every mutation is audited (<c>db.*</c> actions).
    /// </summary>
    public sealed class GameDataRoutes : IWebAdminRoutes
    {
        private static readonly HashSet<string> ReservedQueryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "q", "sort", "dir", "page", "pageSize", "_", "live", "dryRun", "noRev", "meta",
        };

        private static readonly object RenameLock = new object();

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/db/tables", GetTables);
            router.Get("/api/admin/db/schema/{endpoint}", GetSchema);
            router.Get("/api/admin/db/{endpoint}", GetRows);
            router.Put("/api/admin/db/{endpoint}", PutRows);
            router.Post("/api/admin/db/{endpoint}/delete", DeleteRows);
            router.Post("/api/admin/db/rename-propagate", RenamePropagate);
            router.Post("/api/admin/database/save", SaveDatabase);
            router.Get("/api/admin/database/status", GetDatabaseStatus);
            router.Post("/api/admin/database/reload-edit", ReloadEdit);
        }

        // ================================================================== helpers

        internal static T WithEdit<T>(Func<GameDataContext, T> fn)
        {
            lock (WebAdminGame.EditLock)
            {
                return fn(GameDataContext.FromEnvir(Envir.Edit));
            }
        }

        private static GameDataTable RequireTable(WebAdminRequest req)
        {
            var endpoint = req.Route["endpoint"];
            var table = GameDataRegistry.Get(endpoint);
            if (table == null) throw new WebAdminException(404, "Unknown game-data table '" + (endpoint ?? string.Empty) + "'");
            return table;
        }

        /// <summary>
        /// False when the Edit envir holds no system data although Server.MirDB exists: saving it would wipe the database
        /// (the host normally loads Edit at start-up, see SMain_Load).
        /// </summary>
        private static bool EditLooksLoaded() => GameDataSave.EditLooksLoaded();

        private static List<JsonElement> ReadRows(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body {rows:[...]}");
            if (!body.TryGetPropertyIgnoreCase("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new WebAdminException(400, "Missing 'rows' array");
            var list = rows.EnumerateArray().ToList();
            if (list.Count > GameDataEngine.MaxRowsPerWrite)
                throw new WebAdminException(413, "Too many rows in one request (max " + GameDataEngine.MaxRowsPerWrite + ")");
            return list;
        }

        // ================================================================== reads

        private static Task GetTables(WebAdminRequest req)
        {
            var tables = WithEdit(ctx => GameDataRegistry.Tables.Select(t =>
            {
                int count;
                try
                {
                    count = t.Rows(ctx).Count();
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Counting game-data table " + t.Endpoint + " failed");
                    count = -1;
                }

                return new
                {
                    endpoint = t.Endpoint,
                    label = t.Label,
                    group = t.Group,
                    type = t.EntityType.Name,
                    count,
                    insertable = t.Insertable,
                    deletable = t.Deletable,
                    parent = t.IsChild ? new { endpoint = t.ParentEndpoint, field = t.ParentField } : null,
                    positionKeyed = t.PositionKeyed,
                };
            }).ToList());

            return req.Json(new { tables });
        }

        private static Task GetSchema(WebAdminRequest req)
        {
            var endpoint = req.Route["endpoint"];
            var schema = WithEdit(ctx => GameDataSchema.GetWithDefaults(endpoint, ctx));
            if (schema == null) return req.Error(404, "Unknown game-data table '" + (endpoint ?? string.Empty) + "'");
            return req.Json(schema);
        }

        private static Task GetRows(WebAdminRequest req)
        {
            var table = RequireTable(req);
            var filters = req.Query.Keys
                .Where(k => !ReservedQueryKeys.Contains(k))
                .Select(k => new KeyValuePair<string, string>(k, req.Query[k]))
                .ToList();

            var q = req.Query["q"];
            var sort = req.Query["sort"];
            var dir = req.Query["dir"];
            var page = req.QueryInt("page", 1);
            var pageSize = Math.Clamp(req.QueryInt("pageSize", 0), 0, 100000);

            var (rows, total) = WithEdit(ctx =>
            {
                var list = GameDataEngine.Query(ctx, table, filters, q, sort, dir, page, pageSize, out var count);
                return (list, count);
            });

            return req.Json(new { endpoint = table.Endpoint, rows, total });
        }

        // ================================================================== PUT

        private static async Task PutRows(WebAdminRequest req)
        {
            var table = RequireTable(req);
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var rows = ReadRows(body);

            var dryRun = req.QueryBool("dryRun") || body.GetBool("dryRun");
            var noRev = req.QueryBool("noRev") || body.GetBool("noRev");
            var live = req.QueryBool("live") || body.GetBool("live");
            var reason = body.GetString("reason");

            var result = WithEdit(ctx => GameDataEngine.Apply(ctx, table, rows, dryRun, noRev));

            if (!dryRun && result.Applied > 0)
            {
                GameDataState.Unsaved.Note(table.Endpoint, result.Applied);
                foreach (var row in result.Rows)
                {
                    WebAdminAudit.Record(req, row.Kind == "insert" ? "db.insert" : "db.update", table.Endpoint + "#" + row.NewIndex, new
                    {
                        endpoint = table.Endpoint,
                        index = row.NewIndex,
                        name = row.Name,
                        changes = row.Changes.Select(c => new { field = c.Field, before = c.Before, after = c.After }).ToList(),
                        noRev = noRev ? true : (bool?)null,
                    }, reason);
                }
            }

            // ?live=1 on monsters always answers with a live block (FEATURES: live:{applied, refreshedMonsters}), even when the
            // write changed nothing, so the client can tell "nothing to push" from "live apply not attempted".
            object liveResult = null;
            if (live && !dryRun && string.Equals(table.Endpoint, "monsters", StringComparison.OrdinalIgnoreCase))
                liveResult = result.Applied > 0
                    ? await ApplyLiveMonsters(req, result, reason).ConfigureAwait(false)
                    : LiveNothingApplied(result);

            var status = 200;
            if (!dryRun && result.Applied == 0)
            {
                if (result.Validation.Count > 0) status = 422;
                else if (result.Conflicts.Count > 0) status = 409;
            }

            await req.Json(new
            {
                ok = result.Conflicts.Count == 0 && result.Validation.Count == 0,
                dryRun,
                applied = result.Applied,
                inserted = result.Rows.Where(r => r.Kind == "insert").Select(r => new { index = r.Index, newIndex = r.NewIndex }).ToList(),
                conflicts = result.Conflicts,
                validation = result.Validation,
                ignoredFields = result.IgnoredFields,
                renames = result.Renames,
                preview = dryRun
                    ? result.Rows.Select(r => new
                    {
                        index = r.Index,
                        kind = r.Kind,
                        name = r.Name,
                        changes = r.Changes.Select(c => new { field = c.Field, before = c.Before, after = c.After }).ToList(),
                    }).ToList()
                    : null,
                pendingReboot = !dryRun && result.Applied > 0,
                unsavedChanges = GameDataState.Unsaved.Changes,
                live = liveResult,
            }, status).ConfigureAwait(false);
        }

        /// <summary>Live block for a ?live=1 monsters write that saved no rows (no-op edit, or rejected by conflicts / validation).</summary>
        internal static object LiveNothingApplied(GameDataWriteResult result)
        {
            var rejected = result != null && (result.Conflicts.Count > 0 || result.Validation.Count > 0);
            return new
            {
                applied = 0,
                refreshedMonsters = 0,
                skippedFields = new List<string>(),
                newMonstersNeedRestart = 0,
                message = rejected
                    ? "Nothing was saved, so nothing was applied to the running world."
                    : "No changes to apply.",
            };
        }

        /// <summary>
        /// ?live=1 on monsters: pushes the changed live-safe fields (scalars + Stat_*, not Name / DropPath) of EXISTING monsters onto
        /// Envir.Main's MonsterInfo on the game thread and refreshes every live MonsterObject of those infos (MonsterTunerForm behaviour).
        /// The Edit envir already holds the change, so the next save + restart keeps it.
        /// </summary>
        private static async Task<object> ApplyLiveMonsters(WebAdminRequest req, GameDataWriteResult result, string reason)
        {
            var updates = result.Rows
                .Where(r => r.Kind == "update")
                .Select(r => new
                {
                    index = r.NewIndex,
                    values = r.Typed.Where(t => t.Key.LiveSafe).ToList(),
                    skipped = r.Typed.Where(t => !t.Key.LiveSafe).Select(t => t.Key.Name).ToList(),
                })
                .ToList();

            var skippedFields = updates.SelectMany(u => u.skipped).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var work = updates.Where(u => u.values.Count > 0).ToList();
            var insertedCount = result.Rows.Count(r => r.Kind == "insert");

            if (work.Count == 0)
                return new { applied = 0, refreshedMonsters = 0, skippedFields, newMonstersNeedRestart = insertedCount, message = "No live-safe changes." };

            if (!WebAdminGame.IsRunning)
                return new { applied = 0, refreshedMonsters = 0, skippedFields, newMonstersNeedRestart = insertedCount, message = "The world is not running; the changes apply when it starts." };

            try
            {
                var outcome = await WebAdminGame.RunAsync(() =>
                {
                    var touched = new HashSet<MonsterInfo>();
                    var applied = 0;
                    var list = Envir.Main.MonsterInfoList;

                    foreach (var u in work)
                    {
                        MonsterInfo info = null;
                        for (var i = 0; i < list.Count; i++)
                        {
                            if (list[i] != null && list[i].Index == u.index)
                            {
                                info = list[i];
                                break;
                            }
                        }

                        if (info == null) continue;

                        var row = new GameDataRow { Index = u.index, Entity = info };
                        foreach (var kv in u.values) kv.Key.Set(row, kv.Value);
                        touched.Add(info);
                        applied++;
                    }

                    var refreshed = 0;
                    if (touched.Count > 0)
                    {
                        foreach (var obj in Envir.Main.Objects)
                        {
                            if (!(obj is MonsterObject mob) || mob.Info == null || !touched.Contains(mob.Info)) continue;

                            try
                            {
                                mob.RefreshAll();
                                refreshed++;
                            }
                            catch (Exception ex)
                            {
                                WebAdminLog.Error(ex, "Refreshing monster " + mob.Info.Name + " after a live edit failed");
                            }
                        }
                    }

                    return (applied, refreshed);
                }, 10000).ConfigureAwait(false);

                WebAdminAudit.Record(req, "db.live-apply", "monsters", new
                {
                    endpoint = "monsters",
                    indexes = work.Select(u => u.index).ToList(),
                    fields = work.SelectMany(u => u.values.Select(v => v.Key.Name)).Distinct().ToList(),
                    applied = outcome.applied,
                    refreshedMonsters = outcome.refreshed,
                }, reason);

                return new
                {
                    applied = outcome.applied,
                    refreshedMonsters = outcome.refreshed,
                    skippedFields,
                    newMonstersNeedRestart = insertedCount,
                    message = skippedFields.Count > 0 ? "Name / DropPath changes take effect after a restart." : null,
                };
            }
            catch (WebAdminException ex) when (ex is WorldNotRunningException || ex is GameThreadTimeoutException)
            {
                return new { applied = 0, refreshedMonsters = 0, skippedFields, newMonstersNeedRestart = insertedCount, message = "Live apply failed: " + ex.Message + ". The edit is saved in the editor copy." };
            }
        }

        // ================================================================== delete

        private static async Task DeleteRows(WebAdminRequest req)
        {
            var table = RequireTable(req);
            if (!table.Deletable) throw new WebAdminException(400, table.Label + " rows cannot be deleted");

            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body {indexes:[...]}");
            if (!body.TryGetPropertyIgnoreCase("indexes", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new WebAdminException(400, "Missing 'indexes' array");

            var indexes = new List<int>();
            foreach (var el in arr.EnumerateArray())
            {
                if (!GameDataCodec.TryGetInteger(el, out var n, out _) || n < int.MinValue || n > int.MaxValue)
                    throw new WebAdminException(400, "'indexes' must contain whole numbers");
                indexes.Add((int)n);
            }

            if (indexes.Count > GameDataEngine.MaxRowsPerWrite)
                throw new WebAdminException(413, "Too many rows in one request (max " + GameDataEngine.MaxRowsPerWrite + ")");

            var dryRun = req.QueryBool("dryRun") || body.GetBool("dryRun");
            var reason = body.GetString("reason");

            var result = WithEdit(ctx => GameDataEngine.Delete(ctx, table, indexes, dryRun));

            if (!dryRun && result.Deleted > 0)
            {
                GameDataState.Unsaved.Note(table.Endpoint, result.Deleted);
                foreach (var removed in result.Removed)
                    WebAdminAudit.Record(req, "db.delete", table.Endpoint + "#" + removed.Key, new { endpoint = table.Endpoint, index = removed.Key, before = removed.Value }, reason);
            }

            var status = !dryRun && result.Deleted == 0 && result.Rejects.Count > 0 ? 409 : 200;
            await req.Json(new
            {
                ok = result.Deleted > 0 || result.Rejects.Count == 0,
                dryRun,
                deleted = result.Deleted,
                rejects = result.Rejects,
                rows = result.Removed.Select(r => new { index = r.Key, name = NameOf(table, r.Value) }).ToList(),
                pendingReboot = !dryRun && result.Deleted > 0,
                unsavedChanges = GameDataState.Unsaved.Changes,
            }, status).ConfigureAwait(false);
        }

        private static string NameOf(GameDataTable table, Dictionary<string, object> wire)
        {
            if (table.NameColumn != null && wire.TryGetValue(table.NameColumn, out var v) && v is string s && !string.IsNullOrWhiteSpace(s)) return s;
            return "#" + (wire.TryGetValue("Index", out var i) ? GameDataCodec.Canonical(i) : "?");
        }

        // ================================================================== rename propagation

        private static async Task RenamePropagate(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body {endpoint, old, new}");

            var endpoint = (body.GetString("endpoint") ?? string.Empty).Trim().ToLowerInvariant();
            if (endpoint != "items" && endpoint != "monsters") throw new WebAdminException(400, "'endpoint' must be items or monsters");

            var oldName = (body.GetString("old") ?? string.Empty).Trim();
            var newName = (body.GetString("new") ?? string.Empty).Trim();
            var problem = GameDataRename.CheckName(oldName);
            if (problem != null) throw new WebAdminException(400, "'old' " + problem);
            problem = GameDataRename.CheckName(newName);
            if (problem != null) throw new WebAdminException(400, "'new' " + problem);
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) throw new WebAdminException(400, "'old' and 'new' are the same");

            var dryRun = req.QueryBool("dryRun") || body.GetBool("dryRun");
            var reason = body.GetString("reason");

            // A monster without DropPath loads Drops\<Name>.txt: move that file too, unless another monster still loads it.
            var renameDropFile = endpoint == "monsters" && WithEdit(ctx =>
            {
                var renamed = ctx.Monsters.Where(m => m != null && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (renamed.Count == 0 || renamed.Any(m => !string.IsNullOrEmpty(m.DropPath))) return false;
                return !ctx.Monsters.Any(m => m != null &&
                                              ((string.IsNullOrEmpty(m.DropPath) && string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)) ||
                                               string.Equals(m.DropPath, oldName, StringComparison.OrdinalIgnoreCase)));
            });

            var roots = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["drops"] = Path.GetFullPath(Settings.DropPath),
                ["recipes"] = Path.GetFullPath(Settings.RecipePath),
                ["quests"] = Path.GetFullPath(Settings.QuestPath),
                ["npcs"] = Path.GetFullPath(Settings.NPCPath),
            };

            var warnings = new List<string>();
            List<GameDataRenameFile> plan;
            lock (RenameLock)
            {
                plan = GameDataRename.Plan(endpoint, oldName, newName, roots, renameDropFile, warnings);
                if (!dryRun && plan.Count > 0)
                    GameDataRename.Execute(plan, Path.GetFullPath(Path.Combine(Envir.BackUpPath, "WebAdmin", "Scripts")), DateTime.Now, warnings);
            }

            var files = plan.Select(f => new { root = f.Root, path = f.Path, replacements = f.Replacements, renameTo = f.RenameTo }).ToList();

            if (!dryRun && plan.Count > 0)
            {
                WebAdminAudit.Record(req, "db.rename-propagate", endpoint + ":" + oldName, new
                {
                    endpoint,
                    old = oldName,
                    @new = newName,
                    files,
                    warnings,
                }, reason);
            }

            // The rename itself only exists in Envir.Edit until database/save + a world restart (Name is not live-safe), so the live
            // world's ItemInfo/MonsterInfo still carry the OLD name. Hot-reloading drops / NPCs / recipes now would resolve the rewritten
            // files against those old names and drop every line that mentions the item (and a monster without DropPath would lose its
            // moved Drops\<old>.txt). So: no hot reloads; save the database and restart the world (Reboot reloads all of these).
            await req.Json(new
            {
                ok = true,
                dryRun,
                endpoint,
                old = oldName,
                @new = newName,
                files,
                replacements = plan.Sum(f => f.Replacements),
                warnings,
                suggestedReloads = Array.Empty<string>(),
                restartRequired = plan.Count > 0,
                message = plan.Count == 0
                    ? "No drop, recipe, quest or NPC file refers to \"" + oldName + "\"."
                    : (dryRun ? "Dry run: nothing was written." : "Updated " + plan.Count + " file(s); backups are in Back Up\\WebAdmin\\Scripts.") +
                      " The running world still uses the old name until you save the database and restart the world, which also reloads these files;" +
                      " do not reload drops, NPCs or recipes (or use @RELOADDROPS) before that restart.",
            }).ConfigureAwait(false);
        }

        // ================================================================== save / status / reload

        private static async Task SaveDatabase(WebAdminRequest req)
        {
            // EditLock is held on this worker thread while the file IO may run on the game thread (GameDataSave.RunFileIo):
            // Task.Run keeps the blocking wait off the request's continuation and Monitor on one thread.
            var saved = await Task.Run(() =>
            {
                lock (WebAdminGame.EditLock)
                {
                    GameDataSave.EnsureSavable();

                    try
                    {
                        return GameDataSave.SaveEdit();
                    }
                    catch (WebAdminException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "Saving the game database (Envir.Edit.SaveDB) failed");
                        throw new WebAdminException(500, "Saving the game database failed, see the server log");
                    }
                }
            }).ConfigureAwait(false);

            var areas = saved.Areas;
            var magic = saved.Magic;

            var description = areas.Count > 0
                ? "Game data saved (" + string.Join(", ", areas) + ")"
                : "Game data saved";
            WebAdminPending.Add("gamedata", description, req.AdminName);
            WebAdminAudit.Record(req, "db.save", "Server.MirDB", new { areas });

            // Main never reloads MagicInfo (not even on Reboot) and would write its old spells back with its next periodic save:
            // push the saved spell values onto Main's MagicInfo objects now (see GameDataMagicSync).
            var (magicChanged, magicSynced) = await GameDataSave.SyncMagicAsync(magic).ConfigureAwait(false);

            var message = "Saved Server.MirDB (the previous file is in Back Up\\Database). Changes take effect after the next world restart.";
            if (magicChanged > 0)
                message += " Spell changes (" + magicChanged + ") were also applied to the running world now, because the server never reloads spells.";
            else if (!magicSynced)
                message += " Spell changes will be applied when the world next starts.";

            await req.Ok(new
            {
                areas,
                systemDataStale = Envir.SystemDataStale,
                pendingReboot = true,
                magicApplied = magicChanged,
                magicPending = !magicSynced && GameDataMagicSync.HasPending,
            }, message).ConfigureAwait(false);
        }

        private static Task GetDatabaseStatus(WebAdminRequest req)
        {
            bool loaded;
            lock (WebAdminGame.EditLock) loaded = EditLooksLoaded();

            var status = GameDataState.Unsaved.Snapshot(Envir.SystemDataStale);
            status["editLoaded"] = loaded;
            status["editReloadFailed"] = GameDataState.EditReloadFailed;
            status["magicPending"] = GameDataMagicSync.HasPending;
            status["world"] = WebAdminGame.WorldState;
            status["pendingReboot"] = WebAdminPending.Count;
            return req.Json(status);
        }

        private static Task ReloadEdit(WebAdminRequest req)
        {
            int discarded;
            lock (WebAdminGame.EditLock)
            {
                var edit = Envir.Edit;
                discarded = GameDataState.Unsaved.Changes;

                // LoadDB clears and refills every list except MagicInfoList (it only adds missing spells): clear it so edits are dropped.
                var magicBackup = edit.MagicInfoList.ToList();
                bool ok;
                try
                {
                    edit.MagicInfoList.Clear();
                    // Serialised with Main's periodic SaveDB (an open read handle would make it throw and stop the world).
                    ok = GameDataSave.RunFileIo(() => edit.LoadDB()); // LoadDB takes Envir.LoadLock itself (re-entrant)
                }
                catch (WebAdminException ex) when ((ex is GameThreadTimeoutException t && !t.Started) || ex.Status == 409)
                {
                    // Cancelled before LoadDB ran: the editor copy is untouched.
                    if (edit.MagicInfoList.Count == 0) edit.MagicInfoList.AddRange(magicBackup);
                    throw;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Reloading the editor copy (Envir.Edit.LoadDB) failed");
                    ok = false;
                }

                if (!ok)
                {
                    if (edit.MagicInfoList.Count == 0) edit.MagicInfoList.AddRange(magicBackup);
                    // LoadDB may have stopped after clearing and refilling only some lists: block database/save until a reload succeeds.
                    GameDataState.EditReloadFailed = true;
                    throw new WebAdminException(500, "Reloading the game data from disk failed, see the server log");
                }

                GameDataState.EditReloadFailed = false;
                GameDataState.Unsaved.Discard();
            }

            // Edit.LoadDB re-links the guild creation items to the editor copy's ItemInfo objects; point them back at the live world's.
            if (WebAdminGame.IsRunning)
                WebAdminGame.Post(() => Settings.LinkGuildCreationItems(Envir.Main.ItemInfoList));

            WebAdminAudit.Record(req, "db.reload-edit", "Server.MirDB", new { discardedChanges = discarded });
            return req.Ok(new { discardedChanges = discarded }, "Reloaded the game data from disk; " + discarded + " unsaved change(s) discarded.");
        }
    }
}
