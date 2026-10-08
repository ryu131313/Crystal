using System.Text.Json;
using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Scripts &amp; Files editor (B10): text files under Envir (NPC scripts, drops, quests, recipes, routes, values, Envir\*.txt,
    /// SystemScripts) — tree, read, save with etag + backup, create / rename / delete, search, backups + restore, validation,
    /// quest reload and the ConfigForm drop class tool. All file work is confined by <see cref="ScriptFileStore"/>.
    /// </summary>
    public sealed class ScriptRoutes : IWebAdminRoutes
    {
        private const int MaxToggleReport = 500;

        public void Register(WebAdminRouter router)
        {
            // Never touch Settings here (RouterTests instantiates every module): the store is built on first request.
            router.Get("/api/admin/scripts/roots", GetRoots);
            router.Get("/api/admin/scripts/tree", GetTree);
            router.Get("/api/admin/scripts/file", GetFile);
            router.Put("/api/admin/scripts/file", PutFile);
            router.Post("/api/admin/scripts/create", PostCreate);
            router.Post("/api/admin/scripts/delete", PostDelete);
            router.Post("/api/admin/scripts/rename", PostRename);
            router.Get("/api/admin/scripts/search", GetSearch);
            router.Get("/api/admin/scripts/backups", GetBackups);
            router.Get("/api/admin/scripts/backup", GetBackup);
            router.Post("/api/admin/scripts/restore", PostRestore);
            router.Post("/api/admin/scripts/validate", PostValidate);
            router.Post("/api/admin/scripts/reload-quests", PostReloadQuests);
            router.Post("/api/admin/scripts/drops/class-toggle", PostDropClassToggle);
        }

        private static ScriptFileStore Store => ScriptFileStore.Current;

        private static string Target(ScriptRoot root, string path) => root.Id + "/" + path;

        /// <summary>Runs a file operation, turning OS file errors into clean API errors (details go to the server log only).</summary>
        private static T Io<T>(Func<T> fn, string what)
        {
            try
            {
                return fn();
            }
            catch (WebAdminException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                WebAdminLog.Error(ex, "scripts: " + what);
                throw new WebAdminException(500, "Access to the file was denied while trying to " + what + ". Check the folder permissions.");
            }
            catch (IOException ex)
            {
                WebAdminLog.Error(ex, "scripts: " + what);
                throw new WebAdminException(409, "The file is in use or could not be written while trying to " + what + ". Try again in a moment.");
            }
        }

        private static string RequireString(JsonElement body, string name, string label)
        {
            var value = body.GetString(name);
            if (string.IsNullOrWhiteSpace(value)) throw new WebAdminException(400, label + " is required.");
            return value;
        }

        private static string ReadContent(JsonElement body)
        {
            if (!body.TryGetPropertyIgnoreCase("content", out var v) || v.ValueKind != JsonValueKind.String)
                throw new WebAdminException(400, "content (string) is required.");
            return v.GetString();
        }

        private static string Reason(JsonElement body)
        {
            var reason = body.GetString("reason");
            if (string.IsNullOrWhiteSpace(reason)) return null;
            reason = reason.Trim();
            return reason.Length > 500 ? reason.Substring(0, 500) : reason;
        }

        // ------------------------------------------------------------------ read-only

        private static Task GetRoots(WebAdminRequest req)
        {
            var roots = Store.Roots.Select(r => new
            {
                id = r.Id,
                label = r.Label,
                description = r.Description,
                directory = r.Directory,
                exists = r.Exists,
                flat = r.Flat,
                reload = ScriptFileStore.ReloadKindFor(r.Id, null),
                validator = ScriptFileStore.ValidatorFor(r.Id),
            }).ToList();

            return req.Ok(new { roots, backupDirectory = Store.BackupBase });
        }

        private static Task GetTree(WebAdminRequest req)
        {
            var root = Store.GetRoot(req.Query["root"]);
            var files = Store.ListFiles(root, out var truncated);

            return req.Ok(new
            {
                root = root.Id,
                exists = root.Exists,
                flat = root.Flat,
                files = files.Select(f => new { path = f.Path, size = f.Size, mtimeUtc = f.MtimeUtc }).ToList(),
                truncated,
            });
        }

        private static Task GetFile(WebAdminRequest req)
        {
            var root = Store.GetRoot(req.Query["root"]);
            var path = req.Query["path"];
            var file = Store.Read(root, path);
            Store.ResolveFile(root, path, out var normalized);
            var readOnlyReason = EditLimitReason(file.Content);

            return req.Ok(new
            {
                root = root.Id,
                path = normalized,
                content = file.Content,
                readOnly = readOnlyReason != null,
                readOnlyReason,
                etag = file.Etag,
                encoding = file.Encoding,
                lineEnding = file.LineEnding,
                mixedLineEndings = file.MixedLineEndings,
                size = file.Size,
                mtimeUtc = file.MtimeUtc,
                reload = ScriptFileStore.ReloadKindFor(root.Id, normalized),
                effect = ScriptFileStore.EffectNote(root.Id, normalized),
                validator = ScriptFileStore.ValidatorFor(root.Id),
            });
        }

        private static readonly JsonSerializerOptions EditSizeJson = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Saving or validating sends the whole text in one JSON body, which is capped by WebAdmin MaxBodyKB. A file whose
        /// body would not fit opens read-only (with this reason) instead of failing later with a bare HTTP 413.
        /// </summary>
        internal static string EditLimitReason(string content)
        {
            var limit = (WebAdminOptions.Current ?? new WebAdminOptions()).MaxBodyBytes;
            const int envelope = 4096; // root, path, etag, reason and the JSON punctuation around the content
            long needed = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(content ?? string.Empty, EditSizeJson)) + envelope;
            if (needed <= limit) return null;
            return "This file is too large to save from the browser (about " + (needed / 1024) + " KB to send; the request limit is " +
                (limit / 1024) + " KB, WebAdmin MaxBodyKB in Setup.ini). It is shown read-only: edit it on disk, or raise MaxBodyKB and restart the panel.";
        }

        private static Task GetSearch(WebAdminRequest req)
        {
            var root = Store.GetRoot(req.Query["root"]);
            var result = Store.Search(root, req.Query["q"], req.QueryBool("regex"), req.QueryInt("max", ScriptFileStore.MaxSearchHits));

            return req.Ok(new
            {
                root = root.Id,
                hits = result.Hits.Select(h => new { path = h.Path, line = h.Line, column = h.Column, text = h.Text }).ToList(),
                truncated = result.Truncated,
                filesScanned = result.FilesScanned,
                filesSkipped = result.FilesSkipped,
            });
        }

        private static Task GetBackups(WebAdminRequest req)
        {
            var root = Store.GetRoot(req.Query["root"]);
            var path = req.Query["path"];
            var list = Store.ListBackups(root, path);
            var full = Store.ResolveFile(root, path, out var normalized);

            return req.Ok(new
            {
                root = root.Id,
                path = normalized,
                exists = File.Exists(full),
                backups = list.Select(b => new { name = b.Name, size = b.Size, mtimeUtc = b.MtimeUtc, stamp = b.Stamp }).ToList(),
            });
        }

        private static Task GetBackup(WebAdminRequest req)
        {
            var root = Store.GetRoot(req.Query["root"]);
            var file = Store.ReadBackup(root, req.Query["path"], req.Query["name"]);

            return req.Ok(new
            {
                name = req.Query["name"],
                content = file.Content,
                encoding = file.Encoding,
                lineEnding = file.LineEnding,
                size = file.Size,
                mtimeUtc = file.MtimeUtc,
            });
        }

        // ------------------------------------------------------------------ mutations

        private static async Task PutFile(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var root = Store.GetRoot(RequireString(body, "root", "root"));
            var path = RequireString(body, "path", "path");
            var content = ReadContent(body);
            var force = body.GetBool("force");

            var result = Io(() => Store.Save(root, path, content, body.GetString("etag"), force), "save the file");

            if (!result.Unchanged)
            {
                WebAdminAudit.Record(req, "script.save", Target(root, result.Path), new
                {
                    root = root.Id,
                    path = result.Path,
                    size = result.Size,
                    backup = result.Backup,
                    force,
                }, Reason(body));
            }

            await req.Ok(new
            {
                root = root.Id,
                path = result.Path,
                etag = result.Etag,
                size = result.Size,
                mtimeUtc = result.MtimeUtc,
                backup = result.Backup,
                unchanged = result.Unchanged,
                encoding = result.Encoding,
                lineEnding = result.LineEnding,
                reload = ScriptFileStore.ReloadKindFor(root.Id, result.Path),
                effect = ScriptFileStore.EffectNote(root.Id, result.Path),
            }, result.Unchanged ? "No changes to save." : "Saved " + result.Path + ".").ConfigureAwait(false);
        }

        private static async Task PostCreate(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var root = Store.GetRoot(RequireString(body, "root", "root"));
            var path = RequireString(body, "path", "path");
            string content = null;
            if (body.TryGetPropertyIgnoreCase("content", out var c) && c.ValueKind == JsonValueKind.String) content = c.GetString();

            var result = Io(() => Store.Create(root, path, content), "create the file");
            WebAdminAudit.Record(req, "script.create", Target(root, result.Path), new { root = root.Id, path = result.Path, size = result.Size }, Reason(body));

            await req.Ok(new
            {
                root = root.Id,
                path = result.Path,
                etag = result.Etag,
                size = result.Size,
                mtimeUtc = result.MtimeUtc,
            }, "Created " + result.Path + ".").ConfigureAwait(false);
        }

        private static async Task PostDelete(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var root = Store.GetRoot(RequireString(body, "root", "root"));
            var path = RequireString(body, "path", "path");
            Store.ResolveFile(root, path, out var normalized);

            var backup = Io(() => Store.Delete(root, path), "delete the file");
            WebAdminAudit.Record(req, "script.delete", Target(root, normalized), new { root = root.Id, path = normalized, backup }, Reason(body));

            await req.Ok(new { root = root.Id, path = normalized, backup }, "Deleted " + normalized + " (kept as backup " + backup + ").").ConfigureAwait(false);
        }

        private static async Task PostRename(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var root = Store.GetRoot(RequireString(body, "root", "root"));
            var path = RequireString(body, "path", "path");
            var newPath = RequireString(body, "newPath", "newPath");
            Store.ResolveFile(root, path, out var normalized);

            var renamed = Io(() => Store.Rename(root, path, newPath), "rename the file");
            WebAdminAudit.Record(req, "script.rename", Target(root, normalized), new { root = root.Id, path = normalized, newPath = renamed }, Reason(body));

            await req.Ok(new { root = root.Id, path = renamed, oldPath = normalized }, "Renamed to " + renamed + ".").ConfigureAwait(false);
        }

        private static async Task PostRestore(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var root = Store.GetRoot(RequireString(body, "root", "root"));
            var path = RequireString(body, "path", "path");
            var backup = RequireString(body, "backup", "backup");

            var result = Io(() => Store.Restore(root, path, backup), "restore the backup");
            WebAdminAudit.Record(req, "script.restore", Target(root, result.Path), new
            {
                root = root.Id,
                path = result.Path,
                restored = backup,
                previousBackup = result.Backup,
            }, Reason(body));

            await req.Ok(new
            {
                root = root.Id,
                path = result.Path,
                etag = result.Etag,
                size = result.Size,
                mtimeUtc = result.MtimeUtc,
                previousBackup = result.Backup,
                reload = ScriptFileStore.ReloadKindFor(root.Id, result.Path),
                effect = ScriptFileStore.EffectNote(root.Id, result.Path),
            }, "Restored " + result.Path + " from " + backup + ".").ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ validation

        private static async Task PostValidate(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var root = Store.GetRoot(RequireString(body, "root", "root"));
            var path = RequireString(body, "path", "path");
            var content = ReadContent(body);
            Store.ResolveFile(root, path, out var normalized);

            var validator = ScriptFileStore.ValidatorFor(root.Id);
            if (validator == null)
            {
                await req.Ok(new { validator = (string)null, diagnostics = new List<ScriptDiagnostic>() }, "There is no validator for this kind of file.").ConfigureAwait(false);
                return;
            }

            var ctx = await BuildContextAsync(Store, normalized).ConfigureAwait(false);
            var diagnostics = ScriptValidation.Validate(ScriptValidation.KindFor(validator, normalized), content, ctx);

            await req.Ok(new
            {
                validator,
                diagnostics = diagnostics.Select(d => new { line = d.Line, severity = d.Severity, message = d.Message }).ToList(),
                errors = diagnostics.Count(d => d.Severity == ScriptDiagnostic.Error),
                warnings = diagnostics.Count(d => d.Severity == ScriptDiagnostic.Warning),
            }).ConfigureAwait(false);
        }

        private sealed class NameLists
        {
            public List<string> Items;
            public List<string> Monsters;
        }

        /// <summary>Item / monster names from the Edit database (what the next reboot loads), else from the running world.</summary>
        private static async Task<NameLists> LoadNamesAsync()
        {
            var names = new NameLists();

            lock (WebAdminGame.EditLock)
            {
                names.Items = SnapshotNames(Envir.Edit.ItemInfoList, i => i?.Name);
                names.Monsters = SnapshotNames(Envir.Edit.MonsterInfoList, m => m?.Name);
            }

            if ((names.Items == null || names.Monsters == null) && WebAdminGame.IsRunning)
            {
                try
                {
                    var live = await WebAdminGame.RunAsync(() => new NameLists
                    {
                        Items = SnapshotNames(Envir.Main.ItemInfoList, i => i?.Name),
                        Monsters = SnapshotNames(Envir.Main.MonsterInfoList, m => m?.Name),
                    }).ConfigureAwait(false);

                    names.Items ??= live.Items;
                    names.Monsters ??= live.Monsters;
                }
                catch (WebAdminException)
                {
                    // world stopped / busy: validate without the name lists
                }
            }

            return names;
        }

        /// <summary>Copies names by index (tolerates a concurrent WinForms editor); null when the list is empty or unreadable.</summary>
        private static List<string> SnapshotNames<T>(List<T> list, Func<T, string> name)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var result = new List<string>(list.Count);
                    for (var i = 0; i < list.Count; i++)
                    {
                        var n = name(list[i]);
                        if (!string.IsNullOrEmpty(n)) result.Add(n);
                    }
                    return result.Count > 0 ? result : null;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            return null;
        }

        private static async Task<ScriptValidationContext> BuildContextAsync(ScriptFileStore store, string normalized)
        {
            var names = await LoadNamesAsync().ConfigureAwait(false);
            var envir = store.Roots.FirstOrDefault(r => r.Id == "envir")?.Directory;
            var drops = store.Roots.FirstOrDefault(r => r.Id == "drops")?.Directory;
            var npcs = store.Roots.FirstOrDefault(r => r.Id == "npcs")?.Directory;

            return new ScriptValidationContext
            {
                ItemNames = names.Items,
                MonsterNames = names.Monsters,
                FileName = Path.GetFileNameWithoutExtension(normalized),
                ReadDropFile = sub => ReadConfined(drops, envir, sub),
                ReadEnvirFile = sub => ReadConfined(envir, envir, sub),
                NpcScriptExists = name =>
                {
                    var full = ConfinedPath(npcs, envir, name + ".txt");
                    return full != null && File.Exists(full);
                },
            };
        }

        /// <summary>Resolves a script-relative path (as the game would) but only inside the Envir folder; null otherwise.</summary>
        private static string ConfinedPath(string baseDir, string envirDir, string relative)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(envirDir) || string.IsNullOrWhiteSpace(relative)) return null;
            if (relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0) return null;

            try
            {
                var full = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                var envirRoot = Path.GetFullPath(envirDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(envirRoot, StringComparison.OrdinalIgnoreCase)) return null;
                if (ScriptPaths.CrossesReparsePoint(envirDir, full)) return null;
                return full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string[] ReadConfined(string baseDir, string envirDir, string relative)
        {
            var full = ConfinedPath(baseDir, envirDir, relative);
            return full == null ? null : ScriptFileStore.TryReadLines(full);
        }

        // ------------------------------------------------------------------ quests reload

        private static string QuestKey(string fileName)
        {
            var key = (fileName ?? string.Empty).Trim().Replace('\\', '/');
            if (key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) key = key.Substring(0, key.Length - 4);
            return key;
        }

        private static async Task PostReloadQuests(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var path = RequireString(body, "path", "path");
            var normalized = ScriptPaths.Normalize(path);
            if (!ScriptPaths.IsTextFile(normalized)) throw new WebAdminException(400, "Only .txt quest files can be reloaded.");
            var key = QuestKey(normalized);

            var outcome = await WebAdminGame.RunAsync(() =>
            {
                var names = new List<string>();
                var refused = new List<string>();
                var quests = Envir.Main.QuestInfoList;

                for (var i = 0; i < quests.Count; i++)
                {
                    var quest = quests[i];
                    if (quest == null || !string.Equals(QuestKey(quest.FileName), key, StringComparison.OrdinalIgnoreCase)) continue;

                    var snapshot = QuestSnapshot.Take(quest);

                    // QuestInfo.ClearInfo leaves these lists alone, so LoadInfo(true) alone would duplicate their lines.
                    quest.TaskDescription.Clear();
                    quest.ReturnDescription.Clear();
                    quest.CompletionDescription.Clear();
                    quest.CarryItems.Clear();
                    try
                    {
                        quest.LoadInfo(true);
                    }
                    catch (Exception ex)
                    {
                        snapshot.Restore(quest);
                        refused.Add(quest.Index + " " + quest.Name + ": the file could not be read (" + ex.Message + ")");
                        continue;
                    }

                    // Every QuestProgressInfo (online or not) was sized from the old task lists, and UpdateKillTasks /
                    // UpdateItemTasks / UpdateFlagTasks index Info.*Tasks[i] by the progress counts. A changed task
                    // shape would throw on the game thread, so roll back and refuse it: that needs a reboot.
                    var mismatch = snapshot.TaskShapeMismatch(quest);
                    if (mismatch != null)
                    {
                        snapshot.Restore(quest);
                        refused.Add(quest.Index + " " + quest.Name + ": " + mismatch);
                        continue;
                    }

                    snapshot.RebindProgress(quest, Envir.Main.CharacterList);
                    names.Add(quest.Index + " " + quest.Name);
                }

                return (names: names, refused: refused);
            }, 10000).ConfigureAwait(false);

            var reloaded = outcome.names;
            if (outcome.refused.Count > 0)
            {
                if (reloaded.Count > 0)
                    WebAdminAudit.Record(req, "script.reload-quests", "quests/" + normalized, new { path = normalized, quests = reloaded, refused = outcome.refused }, Reason(body));
                throw new WebAdminException(409, "Not reloaded: " + string.Join("; ", outcome.refused) +
                    ". Players' quest progress is sized from the old kill, item and flag tasks, so adding, removing or changing a task needs a server reboot." +
                    " The file is saved; the running quest was left unchanged." + (reloaded.Count > 0 ? " Reloaded: " + string.Join(", ", reloaded) + "." : string.Empty));
            }

            if (reloaded.Count == 0)
                throw new WebAdminException(404, "No quest in the running world uses " + normalized + " (QuestInfo.FileName). New quests need a reboot.");

            WebAdminAudit.Record(req, "script.reload-quests", "quests/" + normalized, new { path = normalized, quests = reloaded }, Reason(body));
            await req.Ok(new { path = normalized, reloaded = reloaded.Count, quests = reloaded },
                "Reloaded " + reloaded.Count + " quest(s) from " + normalized + ".").ConfigureAwait(false);
        }

        /// <summary>The file-loaded parts of a live QuestInfo, so a refused reload can be rolled back exactly.</summary>
        private sealed class QuestSnapshot
        {
            private List<string> description, taskDescription, returnDescription, completionDescription;
            private List<Server.MirDatabase.QuestItemTask> carryItems, itemTasks;
            private List<Server.MirDatabase.QuestKillTask> killTasks;
            private List<Server.MirDatabase.QuestFlagTask> flagTasks;
            private List<QuestItemReward> fixedRewards, selectRewards;
            private uint expReward, goldReward, creditReward;

            public static QuestSnapshot Take(Server.MirDatabase.QuestInfo q) => new QuestSnapshot
            {
                description = new List<string>(q.Description),
                taskDescription = new List<string>(q.TaskDescription),
                returnDescription = new List<string>(q.ReturnDescription),
                completionDescription = new List<string>(q.CompletionDescription),
                carryItems = new List<Server.MirDatabase.QuestItemTask>(q.CarryItems),
                killTasks = q.KillTasks,
                itemTasks = q.ItemTasks,
                flagTasks = q.FlagTasks,
                fixedRewards = q.FixedRewards,
                selectRewards = q.SelectRewards,
                expReward = q.ExpReward,
                goldReward = q.GoldReward,
                creditReward = q.CreditReward,
            };

            public void Restore(Server.MirDatabase.QuestInfo q)
            {
                q.Description.Clear(); q.Description.AddRange(description);
                q.TaskDescription.Clear(); q.TaskDescription.AddRange(taskDescription);
                q.ReturnDescription.Clear(); q.ReturnDescription.AddRange(returnDescription);
                q.CompletionDescription.Clear(); q.CompletionDescription.AddRange(completionDescription);
                q.CarryItems.Clear(); q.CarryItems.AddRange(carryItems);
                q.KillTasks = killTasks;
                q.ItemTasks = itemTasks;
                q.FlagTasks = flagTasks;
                q.FixedRewards = fixedRewards;
                q.SelectRewards = selectRewards;
                q.ExpReward = expReward;
                q.GoldReward = goldReward;
                q.CreditReward = creditReward;
            }

            /// <summary>Null when the kill / item / flag tasks still line up one-to-one (same count, same monster / item / flag per slot).</summary>
            public string TaskShapeMismatch(Server.MirDatabase.QuestInfo q)
            {
                if (q.KillTasks.Count != killTasks.Count) return "kill tasks " + killTasks.Count + " -> " + q.KillTasks.Count;
                for (var i = 0; i < killTasks.Count; i++)
                    if (q.KillTasks[i].Monster?.Index != killTasks[i].Monster?.Index) return "kill task " + (i + 1) + " monster changed";
                if (q.ItemTasks.Count != itemTasks.Count) return "item tasks " + itemTasks.Count + " -> " + q.ItemTasks.Count;
                for (var i = 0; i < itemTasks.Count; i++)
                    if (q.ItemTasks[i].Item?.Index != itemTasks[i].Item?.Index) return "item task " + (i + 1) + " item changed";
                if (q.FlagTasks.Count != flagTasks.Count) return "flag tasks " + flagTasks.Count + " -> " + q.FlagTasks.Count;
                for (var i = 0; i < flagTasks.Count; i++)
                    if (q.FlagTasks[i].Number != flagTasks[i].Number) return "flag task " + (i + 1) + " number changed";
                return null;
            }

            /// <summary>
            /// Points every character's progress entries at the new task objects (so new required counts and messages apply),
            /// then re-sends the task list to online owners. Only called when <see cref="TaskShapeMismatch"/> is null.
            /// </summary>
            public void RebindProgress(Server.MirDatabase.QuestInfo q, List<Server.MirDatabase.CharacterInfo> characters)
            {
                for (var c = 0; c < characters.Count; c++)
                {
                    var character = characters[c];
                    if (character?.CurrentQuests == null) continue;

                    for (var p = 0; p < character.CurrentQuests.Count; p++)
                    {
                        var progress = character.CurrentQuests[p];
                        if (progress == null || progress.Info != q) continue;

                        foreach (var k in progress.KillTaskCount)
                        {
                            var slot = k.Info == null ? -1 : killTasks.IndexOf(k.Info);
                            if (slot >= 0) k.Info = q.KillTasks[slot];
                        }
                        foreach (var it in progress.ItemTaskCount)
                        {
                            var slot = it.Info == null ? -1 : itemTasks.IndexOf(it.Info);
                            if (slot >= 0) it.Info = q.ItemTasks[slot];
                        }
                        foreach (var f in progress.FlagTaskSet)
                        {
                            var slot = f.Info == null ? -1 : flagTasks.IndexOf(f.Info);
                            if (slot >= 0) f.Info = q.FlagTasks[slot];
                        }

                        var player = character.Player;
                        if (player != null && player.Info == character && player.Connection != null)
                            player.SendUpdateQuest(progress, QuestState.Update);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ drop class tool

        private static async Task PostDropClassToggle(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var className = RequireString(body, "class", "class");
            if (!DropClassToggle.TryParseClass(className, out var target))
                throw new WebAdminException(400, "class must be Assassin or Archer.");
            if (!body.TryGetPropertyIgnoreCase("enable", out var enableEl) || (enableEl.ValueKind != JsonValueKind.True && enableEl.ValueKind != JsonValueKind.False))
                throw new WebAdminException(400, "enable (true = re-add the drops, false = remove them) is required.");

            var enable = enableEl.GetBoolean();
            var dryRun = body.GetBool("dryRun", true);

            var store = Store;
            var root = store.GetRoot("drops");
            if (!root.Exists) throw new WebAdminException(404, "The Drops folder does not exist: " + root.Directory);

            Dictionary<string, RequiredClass> classes;
            lock (WebAdminGame.EditLock)
            {
                classes = SnapshotClasses(Envir.Edit.ItemInfoList);
            }

            if (classes == null || classes.Count == 0)
                throw new WebAdminException(409, "The item database is not loaded, so item classes are unknown.");

            RequiredClass? ClassOf(string token) => classes.TryGetValue(token, out var cls) ? cls : (RequiredClass?)null;

            var report = new List<object>();
            int filesChanged = 0, linesChanged = 0;
            var backups = new List<string>();
            var failed = new List<string>();

            lock (store.WriteLock)
            {
                foreach (var entry in store.ListFiles(root, out _))
                {
                    var full = store.ResolveFile(root, entry.Path, out var normalized);
                    byte[] bytes;
                    try
                    {
                        bytes = ScriptFileStore.ReadAllBytesShared(full);
                    }
                    catch (Exception ex) when (ex is WebAdminException || ex is IOException || ex is UnauthorizedAccessException)
                    {
                        failed.Add(entry.Path);
                        continue;
                    }

                    var decoded = ScriptText.Decode(bytes);
                    var updated = DropClassToggle.Apply(decoded.Content, ClassOf, target, !enable, out var changed);
                    if (changed.Count == 0) continue;

                    filesChanged++;
                    linesChanged += changed.Count;
                    if (report.Count < MaxToggleReport) report.Add(new { path = normalized, lines = changed });

                    if (dryRun) continue;

                    try
                    {
                        var newBytes = ScriptText.Encode(updated, decoded.Encoding, decoded.LineEnding);
                        backups.Add(store.Backup(root, normalized, bytes));
                        ScriptFileStore.WriteAtomic(full, newBytes);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        WebAdminLog.Error(ex, "scripts: drop class toggle could not write " + normalized);
                        failed.Add(normalized);
                        filesChanged--;
                        linesChanged -= changed.Count;
                    }
                }
            }

            if (!dryRun && filesChanged > 0)
            {
                WebAdminAudit.Record(req, "script.drops-class-toggle", "drops", new
                {
                    @class = target.ToString(),
                    enable,
                    filesChanged,
                    linesChanged,
                    failed,
                }, Reason(body));
            }

            var verb = enable ? "re-enable" : "comment out";
            var message = dryRun
                ? "Would " + verb + " " + linesChanged + " drop line(s) in " + filesChanged + " file(s)."
                : (filesChanged == 0 ? "Nothing to change." : (enable ? "Re-enabled " : "Commented out ") + linesChanged + " drop line(s) in " + filesChanged + " file(s). Reload Drops to apply.");

            await req.Ok(new
            {
                dryRun,
                @class = target.ToString(),
                enable,
                filesChanged,
                linesChanged,
                files = report,
                truncated = filesChanged > report.Count,
                failed,
                reload = "drops",
            }, message).ConfigureAwait(false);
        }

        private static Dictionary<string, RequiredClass> SnapshotClasses(List<ItemInfo> items)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var map = new Dictionary<string, RequiredClass>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (item?.Name == null) continue;
                        var key = item.Name.Replace(" ", string.Empty);
                        if (!map.ContainsKey(key)) map[key] = item.RequiredClass;
                    }
                    return map;
                }
                catch (ArgumentOutOfRangeException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            return null;
        }
    }
}
