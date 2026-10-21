using System.Globalization;
using System.Text.Json;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// The drops editor (Drops-Tables-Plan phase 2): drop lists as rows in the Game Data section, instead of text in the Scripts editor.
    /// Lists, a monster's list, one list's rows, a whole-list save (revision checked, before-text backed up in the Scripts backups,
    /// audited), create, copy and delete-all-disabled. A save writes SystemData.db at once (there is no unsaved Edit state for drops)
    /// and then reloads that list's owners in the running world (<see cref="Envir.ReloadDropList"/>, game thread).
    /// <para>Threads: HTTP only. Edit's monsters and items are read under EditLock; writes go through <see cref="DropTables"/> under its
    /// EditGate (the store writer never takes either lock).</para>
    /// </summary>
    public sealed class DropRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/drops/lists", GetLists);
            router.Get("/api/admin/drops/monster", GetMonster);
            router.Get("/api/admin/drops/list", GetList);
            router.Post("/api/admin/drops/save", PostSave);
            router.Post("/api/admin/drops/create", PostCreate);
            router.Post("/api/admin/drops/copy", PostCopy);
            router.Post("/api/admin/drops/delete-disabled", PostDeleteDisabled);
        }

        private sealed class EditView
        {
            public List<(int Index, string Name, string DropPath)> Monsters = new List<(int, string, string)>();
            /// <summary>Item Index → name as shown (with spaces); first wins, as the game's lookups.</summary>
            public Dictionary<long, string> Items = new Dictionary<long, string>();
            /// <summary>Name → Index lookups over Edit's items, as the game matches names (saves are read back through them).</summary>
            public DesignNames Names;

            public string ItemName(long index) => Items.TryGetValue(index, out var n) ? n.Replace(" ", string.Empty) : null;
        }

        private static EditView Snapshot()
        {
            var v = new EditView();
            lock (WebAdminGame.EditLock)
            {
                var monsters = Envir.Edit.MonsterInfoList;
                for (var i = 0; i < monsters.Count; i++) v.Monsters.Add((monsters[i].Index, monsters[i].Name, monsters[i].DropPath));
                var items = Envir.Edit.ItemInfoList;
                for (var i = 0; i < items.Count; i++) if (items[i]?.Name != null) v.Items.TryAdd(items[i].Index, items[i].Name);
                v.Names = DesignNames.FromEnvir(Envir.Edit);
            }

            return v;
        }

        private static void EnsureActive()
        {
            if (!DesignStore.Active) throw new WebAdminException(409, "Drops are edited in SystemData.db, which isn't active on this server.");
        }

        /// <summary>List id → the monsters that load it.</summary>
        private static Dictionary<long, List<object>> Users(EditView v)
        {
            var users = new Dictionary<long, List<object>>();
            foreach (var (index, name, dropPath) in v.Monsters)
            {
                var l = DropTables.ForMonster(index, name, dropPath);
                if (l == null) continue;
                if (!users.TryGetValue(l.Id, out var list)) users[l.Id] = list = new List<object>();
                list.Add(new { index, name });
            }

            return users;
        }

        private static bool Typed(DropRow r) => r.Kind == DropKind.Item || r.Kind == DropKind.Gold || r.Kind == DropKind.Group;

        private static Task GetLists(WebAdminRequest req)
        {
            EnsureActive();
            var users = Users(Snapshot());
            var lists = DropTables.Lists().OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Select(l => new
            {
                id = l.Id,
                name = l.Name,
                kind = l.Kind,
                monsterIndex = l.MonsterIndex,
                fishingType = l.FishingType,
                typed = l.Rows.Count(Typed),
                disabled = l.Rows.Count(r => r.Kind == DropKind.Disabled),
                notes = l.Rows.Count(r => r.Kind == DropKind.Note),
                users = users.TryGetValue(l.Id, out var u) ? u : new List<object>(),
                rev = DropTables.Rev(l),
            }).ToList();
            return req.Ok(new { lists });
        }

        private static Task GetMonster(WebAdminRequest req)
        {
            EnsureActive();
            var index = req.QueryInt("index", -1);
            var v = Snapshot();
            var m = v.Monsters.FirstOrDefault(x => x.Index == index);
            if (m.Name == null) throw new WebAdminException(404, "No monster with Index " + index.ToString(CultureInfo.InvariantCulture) + ".");
            var l = DropTables.ForMonster(m.Index, m.Name, m.DropPath);
            var users = l != null && Users(v).TryGetValue(l.Id, out var u) ? u : new List<object>();
            return req.Ok(new
            {
                monster = new { index = m.Index, name = m.Name, dropPath = m.DropPath },
                listId = l?.Id,
                listName = l?.Name ?? DropTables.MonsterFileName(m.Name, m.DropPath),
                shared = !string.IsNullOrEmpty(m.DropPath) || users.Count > 1,
                users,
            });
        }

        private static Task GetList(WebAdminRequest req)
        {
            EnsureActive();
            var l = DropTables.Get(req.QueryLong("id", -1)) ?? throw new WebAdminException(404, "No such drop list (it may have been deleted; reload).");
            var v = Snapshot();
            return req.Ok(ListBody(l, v, Users(v)));
        }

        private static object ListBody(DropList l, EditView v, Dictionary<long, List<object>> users) => new
        {
            id = l.Id,
            name = l.Name,
            kind = l.Kind,
            monsterIndex = l.MonsterIndex,
            fishingType = l.FishingType,
            dragon = l.IsDragon,
            rev = DropTables.Rev(l),
            users = users.TryGetValue(l.Id, out var u) ? u : new List<object>(),
            rows = Rows(l, v),
        };

        private static List<object> Rows(DropList l, EditView v)
        {
            var rows = new List<object>(l.Rows.Length);
            var depth = new int[l.Rows.Length];
            var ignored = new bool[l.Rows.Length]; // inside a group whose members the game ignores
            for (var i = 0; i < l.Rows.Length; i++)
            {
                var r = l.Rows[i];
                if (r.Parent >= 0)
                {
                    depth[i] = depth[r.Parent] + 1;
                    ignored[i] = ignored[r.Parent] || !DropTables.GroupIsComplete(l, r.Parent);
                }

                string warning = null;
                if (r.Kind == DropKind.Disabled) warning = "The game can't load this line (an item that doesn't exist, or bad syntax) and skips it.";
                else if (r.Kind == DropKind.Item && (r.ItemIndex is not long ii || !v.Items.ContainsKey(ii))) warning = "This item no longer exists; the game skips this drop.";
                else if (r.Kind == DropKind.Group && !DropTables.GroupIsComplete(l, i)) warning = "No { } around its members: the game ignores them.";
                else if (ignored[i] && Typed(r)) warning = "Inside a group without { }: the game ignores it.";

                rows.Add(new
                {
                    id = r.Id,
                    parentId = r.Parent >= 0 ? l.Rows[r.Parent].Id : (long?)null,
                    depth = depth[i],
                    kind = r.Kind,
                    chance = Typed(r) ? r.Chance : (int?)null,
                    itemIndex = r.ItemIndex,
                    itemName = r.ItemIndex is long ix && v.Items.TryGetValue(ix, out var n) ? n : r.ItemName,
                    quest = r.Quest,
                    gold = r.Kind == DropKind.Gold ? r.Gold : (uint?)null,
                    groupMode = r.GroupMode,
                    dragonLevel = r.DragonLevel,
                    includeListId = r.IncludeListId,
                    text = DropTables.Render(r, null),
                    warning,
                });
            }

            return rows;
        }

        // ------------------------------------------------------------------ writes

        private static DropList RequireList(long id) =>
            DropTables.Get(id) ?? throw new WebAdminException(404, "No such drop list (it may have been deleted; reload).");

        private static void CheckRev(DropList l, string rev)
        {
            if (!string.Equals(rev, DropTables.Rev(l), StringComparison.Ordinal))
                throw new WebAdminException(409, "The drop list " + l.Name + " changed since you loaded it; reload it and redo your change.");
        }

        private static string Reason(JsonElement body)
        {
            var reason = body.GetString("reason");
            return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        }

        /// <summary>Keeps the list's current text in the Scripts backups (drops/&lt;name&gt;), the undo for a grid save.</summary>
        private static void Backup(DropList l)
        {
            try
            {
                var store = ScriptFileStore.Current;
                store.Backup(store.GetRoot("drops"), l.Name, DropTables.ReadBytes(l.Name) ?? Array.Empty<byte>());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                WebAdminLog.Error(ex, "drops: backup of " + l.Name);
                throw new WebAdminException(409, "The backup of " + l.Name + " could not be written, so nothing was saved. Try again in a moment.");
            }
        }

        /// <summary>Reloads the list's owners in the running world (game thread); a stopped world loads it at the next start.</summary>
        private static async Task<string> ReloadInWorld(long listId)
        {
            if (!WebAdminGame.IsRunning) return "The world isn't running: the change loads at the next start.";
            try
            {
                var list = DropTables.Get(listId);
                var included = list != null && DropTables.IsIncluded(list); // here, not on the game thread
                var what = await WebAdminGame.RunAsync(() => Envir.Main.ReloadDropList(listId, included)).ConfigureAwait(false);
                return "Reloaded in the running world: " + what + ".";
            }
            catch (WebAdminException ex)
            {
                return "Saved, but the running world didn't reload it (" + ex.Message + "): use Reload Drops.";
            }
        }

        private static async Task Saved(WebAdminRequest req, DropList saved, int changed, string action, object details, string reason, string message)
        {
            WebAdminAudit.Record(req, action, "drops/" + saved.Name, details, reason);
            var reloaded = await ReloadInWorld(saved.Id).ConfigureAwait(false);
            var v = Snapshot();
            await req.Ok(new { rev = DropTables.Rev(saved), changed, rows = Rows(saved, v), reloaded }, message + " " + reloaded).ConfigureAwait(false);
        }

        private static async Task PostSave(WebAdminRequest req)
        {
            EnsureActive();
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var edits = ReadEdits(body);
            var dryRun = body.GetBool("dryRun");
            var v = Snapshot();

            DropList saved;
            int changed;
            lock (DropTables.EditGate)
            {
                var l = RequireList(body.GetLong("id", -1));
                CheckRev(l, body.GetString("rev"));
                var errors = new List<DropEditError>();
                var rows = DropTables.ApplyEdits(l, edits, v.ItemName, v.Names, errors, out changed);
                if (rows == null)
                    throw new WebAdminException(400, errors.Count.ToString(CultureInfo.InvariantCulture) + " problem(s): " + errors[0].Message,
                        new { errors = errors.Select(e => new { row = e.Row, field = e.Field, message = e.Message }) });
                if (dryRun || changed == 0)
                {
                    saved = null;
                }
                else
                {
                    Backup(l);
                    saved = DropTables.ReplaceRows(l, rows);
                }
            }

            if (saved == null)
            {
                var summary = changed == 0 ? "Nothing changed." : changed.ToString(CultureInfo.InvariantCulture) + " row change(s) would be saved.";
                await req.Ok(new { changed, summary }, summary).ConfigureAwait(false);
                return;
            }

            await Saved(req, saved, changed, "drops.save", new { changed }, Reason(body), "Saved " + changed.ToString(CultureInfo.InvariantCulture) + " row change(s) to " + saved.Name + ".").ConfigureAwait(false);
        }

        private static async Task PostCopy(WebAdminRequest req)
        {
            EnsureActive();
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var dryRun = body.GetBool("dryRun");
            DropList saved = null, source;
            int changed;
            lock (DropTables.EditGate)
            {
                source = RequireList(body.GetLong("fromId", -1));
                var target = RequireList(body.GetLong("toId", -1));
                if (source.Id == target.Id) throw new WebAdminException(400, "Pick another list to copy from.");
                CheckRev(target, body.GetString("rev"));
                var rows = DropTables.CopyRows(source, target, out var error) ?? throw new WebAdminException(400, error);
                changed = rows.Length;
                if (!dryRun)
                {
                    Backup(target);
                    saved = DropTables.ReplaceRows(target, rows);
                }
            }

            if (saved == null)
            {
                var summary = "Would replace the list with the " + changed.ToString(CultureInfo.InvariantCulture) + " line(s) of " + source.Name + ".";
                await req.Ok(new { changed, summary }, summary).ConfigureAwait(false);
                return;
            }

            await Saved(req, saved, changed, "drops.copy", new { from = source.Name, rows = changed }, Reason(body), "Copied " + source.Name + " into " + saved.Name + ".").ConfigureAwait(false);
        }

        private static async Task PostDeleteDisabled(WebAdminRequest req)
        {
            EnsureActive();
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var dryRun = body.GetBool("dryRun");
            var v = Snapshot();
            DropList saved = null;
            int removed;
            lock (DropTables.EditGate)
            {
                var l = RequireList(body.GetLong("id", -1));
                CheckRev(l, body.GetString("rev"));
                removed = l.Rows.Count(r => r.Kind == DropKind.Disabled);
                if (removed > 0 && !dryRun)
                {
                    var errors = new List<DropEditError>();
                    var rows = DropTables.ApplyEdits(l, DropTables.AsEdits(l).Where(e => e.Kind != DropKind.Disabled).ToList(), v.ItemName, v.Names, errors, out _);
                    if (rows == null) throw new WebAdminException(400, errors[0].Message);
                    Backup(l);
                    saved = DropTables.ReplaceRows(l, rows);
                }
            }

            if (saved == null)
            {
                var summary = removed == 0 ? "This list has no disabled lines." : "Would delete " + removed.ToString(CultureInfo.InvariantCulture) + " disabled line(s).";
                await req.Ok(new { changed = removed, summary }, summary).ConfigureAwait(false);
                return;
            }

            await Saved(req, saved, removed, "drops.delete-disabled", new { removed }, Reason(body), "Deleted " + removed.ToString(CultureInfo.InvariantCulture) + " disabled line(s) from " + saved.Name + ".").ConfigureAwait(false);
        }

        private static async Task PostCreate(WebAdminRequest req)
        {
            EnsureActive();
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var v = Snapshot();
            DropList created;
            var isNew = true;
            lock (DropTables.EditGate)
            {
                if (body.TryGetPropertyIgnoreCase("monsterIndex", out var mi) && mi.ValueKind == JsonValueKind.Number)
                {
                    var index = mi.GetInt32();
                    var m = v.Monsters.FirstOrDefault(x => x.Index == index);
                    if (m.Name == null) throw new WebAdminException(404, "No monster with Index " + index.ToString(CultureInfo.InvariantCulture) + ".");
                    var existing = DropTables.ForMonster(m.Index, m.Name, m.DropPath);
                    if (existing != null)
                    {
                        created = existing;
                        isNew = false;
                    }
                    else
                    {
                        var name = DropTables.MonsterFileName(m.Name, m.DropPath);
                        created = string.IsNullOrEmpty(m.DropPath)
                            ? DropTables.CreateList(DropTables.FreeMonsterListName(name, m.Index), DropList.Monster, m.Index, null)
                            : DropTables.CreateList(name, DropList.Named, null, null);
                    }
                }
                else
                {
                    var raw = body.GetString("name");
                    if (string.IsNullOrWhiteSpace(raw) || !raw.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || raw.Contains("..", StringComparison.Ordinal) ||
                        raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(raw))
                        throw new WebAdminException(400, "name must be a relative .txt name under the drop folder, e.g. Shared/Wolves.txt.");
                    if (!DropTables.TryMap(Path.Combine(Settings.DropPath, raw.Trim()), out var name)) throw new WebAdminException(400, "That name is outside the drop folder.");
                    if (DropTables.Exists(name)) throw new WebAdminException(409, name + " already exists.");
                    var (kind, monster, fishing) = DropTables.OwnerForNew(name, v.Monsters.Select(x => ((long)x.Index, x.Name, x.DropPath)));
                    created = DropTables.CreateList(name, kind, monster, fishing);
                }
            }

            if (isNew) WebAdminAudit.Record(req, "drops.create", "drops/" + created.Name, new { created.Kind, created.MonsterIndex }, Reason(body));
            await req.Ok(new { id = created.Id }, isNew ? "Created the drop list " + created.Name + "." : "The monster already loads " + created.Name + ".").ConfigureAwait(false);
        }

        /// <summary>The save's rows (unparsable numbers become invalid values, which the validation reports per row).</summary>
        private static List<DropEdit> ReadEdits(JsonElement body)
        {
            if (!body.TryGetPropertyIgnoreCase("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new WebAdminException(400, "rows (array) is required.");
            var edits = new List<DropEdit>(rows.GetArrayLength());
            foreach (var r in rows.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) { edits.Add(null); continue; }
                long? Num(string name) => r.TryGetPropertyIgnoreCase(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var n) ? n : null;
                var chance = Num("chance");
                var gold = Num("gold");
                var level = Num("dragonLevel");
                var item = Num("itemIndex");
                edits.Add(new DropEdit
                {
                    Id = Num("id") ?? 0,
                    ParentId = Num("parentId"),
                    Kind = r.GetString("kind"),
                    Chance = chance is long c && c >= 1 && c <= int.MaxValue ? (int)c : 0,
                    ItemIndex = item,
                    Quest = r.GetBool("quest"),
                    Gold = gold is long g && g >= 1 && g <= uint.MaxValue ? (uint)g : 0,
                    GroupMode = r.GetString("groupMode"),
                    DragonLevel = level is long lv ? (lv >= 1 && lv <= byte.MaxValue ? (byte)lv : (byte)0) : null,
                });
            }

            return edits;
        }
    }
}
