using System.Globalization;

namespace Server.MirEnvir.Storage
{
    /// <summary>One row of a grid save (the whole list, in display order). New rows have ids ≤ 0.</summary>
    internal sealed class DropEdit
    {
        public long Id;
        /// <summary>Id of a group row earlier in the save, or null for the top level.</summary>
        public long? ParentId;
        public string Kind;
        public int Chance;
        public long? ItemIndex;
        public bool Quest;
        public uint Gold;
        public string GroupMode;
        public byte? DragonLevel;
    }

    internal sealed class DropEditError
    {
        /// <summary>Position of the row in the save.</summary>
        public int Row;
        public string Field;
        public string Message;
    }

    /// <summary>
    /// Row-level edits of a list (Drops-Tables-Plan phase 2: the web grid). Rows the grid didn't change keep their line as written;
    /// changed and new rows get the canonical line (<see cref="Canonical"/>). Every write here is an editor write (HTTP or UI thread,
    /// never the game thread): the store first, then the cache, all under <see cref="EditGate"/> so two saves can't interleave.
    /// </summary>
    internal static partial class DropTables
    {
        /// <summary>Held by the drop editors around read-check-write (never by the game thread; the writer never takes it).</summary>
        public static readonly object EditGate = new object();

        /// <summary>
        /// Builds a list's new rows from a grid save, or returns null with <paramref name="errors"/>. Notes, includes and disabled rows
        /// come back by id and stay as written (a disabled row may become an item); new rows are item, gold or group. A group made or
        /// kept complete gets its <c>{</c> / <c>}</c> lines around its members; an old group without them (whose members the game
        /// ignores) is kept as it is, and nothing but notes may follow it (the game reads everything after it as part of it).
        /// </summary>
        /// <param name="itemName">Item Index → its name as the game matches it (spaces removed), or null when the item doesn't exist.</param>
        /// <param name="names">The same items as name → Index lookups: the saved rows are rendered, read back the way the game reads text,
        /// and must come out the same (a save the text would read differently is refused, so text tools and backups stay true).</param>
        public static DropRow[] ApplyEdits(DropList list, IReadOnlyList<DropEdit> edits, Func<long, string> itemName, DesignNames names, List<DropEditError> errors, out int changed)
        {
            changed = 0;
            var firstError = errors.Count; // the caller's list may already hold errors
            if (edits.Count > MaxRows)
            {
                errors.Add(new DropEditError { Row = MaxRows, Field = "id", Message = "A drop list holds at most " + MaxRows.ToString(CultureInfo.InvariantCulture) + " rows." });
                return null;
            }

            var depth = new int[edits.Count];
            var old = new Dictionary<long, DropRow>();
            foreach (var r in list.Rows) old[r.Id] = r;
            var nodes = new Node[edits.Count];
            var byId = new Dictionary<long, int>();
            var top = new List<Node>();

            for (var i = 0; i < edits.Count; i++)
            {
                var row = i;
                void Err(string field, string message) => errors.Add(new DropEditError { Row = row, Field = field, Message = message });
                var e = edits[i];
                if (e == null) { Err("id", "Empty row."); continue; }
                if (!byId.TryAdd(e.Id, i)) { Err("id", "The row appears twice."); continue; }
                DropRow before = null;
                if (e.Id > 0 && !old.TryGetValue(e.Id, out before)) { Err("id", "This row no longer exists; reload the list."); continue; }

                Node parent = null;
                if (e.ParentId is long pid)
                {
                    if (!byId.TryGetValue(pid, out var pi) || nodes[pi] == null || nodes[pi].Row.Kind != DropKind.Group)
                    {
                        Err("parentId", "A row's group must be a group row above it.");
                        continue;
                    }

                    parent = nodes[pi];
                    depth[i] = depth[pi] + 1;
                    if (depth[i] > MaxDepth) { Err("parentId", "Groups nest at most " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " deep."); continue; }
                }

                // A row that moves to another group gets a fresh line (its old indentation can read differently there).
                var sameParent = before == null || (before.Parent >= 0 ? list.Rows[before.Parent].Id : (long?)null) == e.ParentId;
                var made = MakeRow(e, before, sameParent, list.IsDragon, itemName, Err, ref changed);
                if (made == null) continue;
                var node = new Node { Row = made, Before = before, EditRow = i };
                nodes[i] = node;
                (parent?.Kids ?? top).Add(node);
            }

            if (errors.Count > firstError) return null;

            foreach (var id in old.Keys) if (!byId.ContainsKey(id)) changed++; // deleted

            // Groups: braces around the members of every new or complete group.
            var openGroups = new HashSet<DropRow>();
            foreach (var node in nodes)
                if (node.Row.Kind == DropKind.Group && !NormaliseBraces(node, list.Rows) && !(node.Kids.Count > 0 && IsBrace(node.Kids[node.Kids.Count - 1].Row, "}")))
                    openGroups.Add(node.Row); // no closing "}": the text loader reads every later line into it

            var flat = new List<DropRow>(edits.Count + 4);
            var editRowOf = new List<int>(edits.Count + 4);
            Emit(top, -1, flat, editRowOf);

            // An old group without its closing "}" takes every later line in the text: only notes may follow it.
            for (var g = 0; g < flat.Count; g++)
            {
                if (!openGroups.Contains(flat[g])) continue;
                var end = g + 1;
                while (end < flat.Count && IsInside(flat, end, g)) end++;
                for (var k = end; k < flat.Count; k++)
                {
                    if (flat[k].Kind == DropKind.Note) continue;
                    errors.Add(new DropEditError
                    {
                        Row = editRowOf[g], Field = "kind",
                        Message = "This group has no { } around its members, so the game reads every line after it as part of it: move it to the end, or delete it.",
                    });
                    return null;
                }
            }

            // The rows must read back the same from their text (braces, old groups without them, stray lines, item names).
            var rendered = new List<string>(flat.Count);
            foreach (var r in flat) rendered.Add(Render(r, (kind, index) => kind == "I" ? itemName(index) : null));
            var reread = Parse(rendered, names, list.IsDragon, out _);
            for (var i = 0; i < flat.Count; i++)
            {
                var why = Differs(flat[i], reread[i]);
                if (why == null) continue;
                errors.Add(new DropEditError
                {
                    Row = editRowOf[i], Field = "kind",
                    Message = "As text (\"" + rendered[i].Trim() + "\") the game would read this row differently (" + why + "): move it, change it or delete it.",
                });
                return null;
            }

            if (changed == 0 && !SameShape(list.Rows, flat)) changed = 1; // only moved
            return flat.ToArray();
        }

        public const int MaxRows = 5000, MaxDepth = 16;

        /// <summary>Why a saved row and the row its text reads back as differ, or null. A disabled row may read back as an item (its item exists by now: the game loads it too).</summary>
        private static string Differs(DropRow a, DropRow b)
        {
            if (a.Parent != b.Parent) return b.Parent < 0 ? "outside its group" : "inside another group";
            if (a.Kind != b.Kind) return a.Kind == DropKind.Disabled && b.Kind == DropKind.Item ? null : "as a " + b.Kind + " line";
            if (a.Kind != DropKind.Item && a.Kind != DropKind.Gold && a.Kind != DropKind.Group) return null;
            if (a.ItemIndex != b.ItemIndex) return "as another item with the same name";
            if (a.Chance != b.Chance || a.Quest != b.Quest || a.Gold != b.Gold || (a.GroupMode ?? string.Empty) != (b.GroupMode ?? string.Empty) || a.DragonLevel != b.DragonLevel)
                return "with other values";
            return null;
        }

        private sealed class Node
        {
            public DropRow Row;
            public DropRow Before;
            public int EditRow;
            public List<Node> Kids = new List<Node>();
        }

        private static DropRow MakeRow(DropEdit e, DropRow before, bool sameParent, bool dragon, Func<long, string> itemName, Action<string, string> err, ref int changed)
        {
            var kind = e.Kind ?? string.Empty;
            if (kind == DropKind.Note || kind == DropKind.Include || kind == DropKind.Disabled)
            {
                if (before == null) { err("kind", "New rows are items, gold or groups."); return null; }
                if (before.Kind != kind) { err("kind", "A " + before.Kind + " row can't become a " + kind + " row."); return null; }
                return Copy(before);
            }

            if (kind != DropKind.Item && kind != DropKind.Gold && kind != DropKind.Group) { err("kind", "Unknown kind \"" + kind + "\"."); return null; }
            if (before != null && (before.Kind == DropKind.Note || before.Kind == DropKind.Include)) { err("kind", "A " + before.Kind + " row can't be changed into a drop."); return null; }

            var ok = true;
            void Bad(string field, string message) { err(field, message); ok = false; }
            if (e.Chance < 1) Bad("chance", "The chance is 1/N with N of 1 or more.");
            string name = null;
            // An item that no longer exists is fine on a row the save doesn't change (the game skips it, the grid warns); a new pick must exist.
            if (kind == DropKind.Item && (e.ItemIndex is not long ii || (name = itemName(ii) ?? (before?.ItemIndex == ii ? before.ItemName : null)) == null))
                Bad("itemIndex", "Pick an item that exists.");
            if (kind == DropKind.Gold && e.Gold < 1) Bad("gold", "Gold must be 1 or more.");
            var mode = e.GroupMode ?? string.Empty;
            if (kind == DropKind.Group && mode != string.Empty && mode != "*" && mode != "^") Bad("groupMode", "A group is normal, * (random) or ^ (first).");
            if (dragon)
            {
                if (kind == DropKind.Group) Bad("kind", "DragonItem.txt has no groups.");
                if (e.DragonLevel is not byte lv || lv < 1 || lv > Globals.MaxDragonLevel)
                    Bad("dragonLevel", "The dragon level is 1 to " + Globals.MaxDragonLevel.ToString(CultureInfo.InvariantCulture) + ".");
                if (e.Quest) Bad("quest", "DragonItem.txt has no quest drops.");
            }
            else if (e.DragonLevel != null) Bad("dragonLevel", "Only DragonItem.txt has dragon levels.");

            if (!ok) return null;

            var row = new DropRow
            {
                Kind = kind,
                Chance = e.Chance,
                ItemIndex = kind == DropKind.Item ? e.ItemIndex : null,
                ItemName = kind == DropKind.Item ? name : null,
                Quest = kind == DropKind.Item && e.Quest,
                Gold = kind == DropKind.Gold ? e.Gold : 0,
                GroupMode = kind == DropKind.Group ? mode : null,
                DragonLevel = dragon ? e.DragonLevel : null,
            };

            if (before != null && sameParent && before.Kind == kind && before.Chance == row.Chance && before.ItemIndex == row.ItemIndex && before.Quest == row.Quest &&
                before.Gold == row.Gold && (before.GroupMode ?? string.Empty) == (row.GroupMode ?? string.Empty) && before.DragonLevel == row.DragonLevel)
            {
                row.Text = before.Text; // unchanged: the line as written
                row.ItemName = before.ItemName;
                row.IncludeListId = before.IncludeListId;
                row.Id = before.Id;
            }
            else changed++;

            return row;
        }

        private static DropRow Copy(DropRow r) => new DropRow
        {
            Id = r.Id, Kind = r.Kind, Chance = r.Chance, ItemIndex = r.ItemIndex, ItemName = r.ItemName, Quest = r.Quest, Gold = r.Gold,
            GroupMode = r.GroupMode, DragonLevel = r.DragonLevel, IncludeListId = r.IncludeListId, Text = r.Text,
        };

        private static bool IsBrace(DropRow r, string brace) => r.Kind == DropKind.Note && (r.Text ?? string.Empty).Trim() == brace;

        /// <summary>
        /// A new group, or one that had its braces, gets exactly "{" first and "}" last (true); an old group without them stays as it was
        /// (false: the game ignores its members and reads every later line as part of it).
        /// </summary>
        private static bool NormaliseBraces(Node group, DropRow[] oldRows)
        {
            if (group.Before != null && group.Before.Kind == DropKind.Group)
            {
                var at = Array.FindIndex(oldRows, r => r.Id == group.Before.Id);
                if (at >= 0 && !IsComplete(oldRows, at)) return false;
            }

            group.Kids.RemoveAll(k => IsBrace(k.Row, "{") || IsBrace(k.Row, "}"));
            group.Kids.Insert(0, new Node { Row = new DropRow { Kind = DropKind.Note, Text = "{" }, EditRow = group.EditRow });
            group.Kids.Add(new Node { Row = new DropRow { Kind = DropKind.Note, Text = "}" }, EditRow = group.EditRow });
            return true;
        }

        /// <summary>The text loader's rule: a group's members count only when it has a "{" and ends with "}".</summary>
        private static bool IsComplete(IList<DropRow> rows, int group)
        {
            bool open = false, close = false;
            for (var j = group + 1; j < rows.Count && IsInside(rows, j, group); j++)
            {
                if (rows[j].Parent != group) continue;
                if (IsBrace(rows[j], "{")) open = true;
                close = IsBrace(rows[j], "}");
            }

            return open && close;
        }

        private static bool IsInside(IList<DropRow> rows, int j, int group) => rows[j].Parent >= group && rows[j].Parent != -1;

        private static void Emit(List<Node> nodes, int parent, List<DropRow> flat, List<int> editRowOf)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var row = nodes[i].Row;
                row.Parent = parent;
                row.Position = i;
                flat.Add(row);
                editRowOf.Add(nodes[i].EditRow);
                Emit(nodes[i].Kids, flat.Count - 1, flat, editRowOf);
            }
        }

        private static bool SameShape(DropRow[] a, List<DropRow> b)
        {
            if (a.Length != b.Count) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i].Id != b[i].Id || a[i].Parent != b[i].Parent || !ReferenceEquals(a[i].Text, b[i].Text) && a[i].Text != b[i].Text) return false;
            return true;
        }

        /// <summary>The text loader's rule for the group at <paramref name="index"/>: are its members loaded?</summary>
        public static bool GroupIsComplete(DropList list, int index) => IsComplete(list.Rows, index);

        /// <summary>The list's rows as grid edits (every row by id): the base for delete-disabled and the tests.</summary>
        public static List<DropEdit> AsEdits(DropList list)
        {
            var edits = new List<DropEdit>(list.Rows.Length);
            foreach (var r in list.Rows)
                edits.Add(new DropEdit
                {
                    Id = r.Id, ParentId = r.Parent >= 0 ? list.Rows[r.Parent].Id : null, Kind = r.Kind, Chance = r.Chance, ItemIndex = r.ItemIndex,
                    Quest = r.Quest, Gold = r.Gold, GroupMode = r.GroupMode, DragonLevel = r.DragonLevel,
                });
            return edits;
        }

        /// <summary>Replaces a list's rows (store, then cache) and returns the saved list. Editors only; call under <see cref="EditGate"/>.</summary>
        public static DropList ReplaceRows(DropList old, DropRow[] rows)
        {
            if (DesignStore.OnGameThread) throw new InvalidOperationException("[Drops] list edits never run on the game thread");
            var s = Loaded();
            var now = DateTime.UtcNow;
            var list = new DropList
            {
                Id = old.Id, Kind = old.Kind, MonsterIndex = old.MonsterIndex, Name = old.Name, FishingType = old.FishingType, Layout = old.Layout,
                UpdatedUtc = now > old.UpdatedUtc ? now : old.UpdatedUtc.AddTicks(1), Rows = rows,
            };
            DesignStore.Run(conn =>
            {
                Finish(list, s);
                using var tx = conn.BeginTransaction();
                Save(conn, tx, list);
                tx.Commit();
                return 0;
            });
            Commit(list);
            return list;
        }

        /// <summary>A copy of another list's rows for <paramref name="target"/> (same lines, same groups).</summary>
        public static DropRow[] CopyRows(DropList source, DropList target, out string error)
        {
            error = null;
            if (source.IsDragon != target.IsDragon)
            {
                error = "DragonItem.txt and the other lists have different lines; copy between lists of the same kind.";
                return null;
            }

            var rows = new DropRow[source.Rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = Copy(source.Rows[i]);
                rows[i].Parent = source.Rows[i].Parent;
                rows[i].Position = source.Rows[i].Position;
            }

            return rows;
        }

        /// <summary>Creates an empty list (a monster's own, bound to its Index, or a named one). Editors only; under <see cref="EditGate"/>.</summary>
        public static DropList CreateList(string name, string kind, long? monsterIndex, int? fishingType)
        {
            if (DesignStore.OnGameThread) throw new InvalidOperationException("[Drops] list edits never run on the game thread");
            var s = Loaded();
            if (s.ByName.ContainsKey(name)) throw new IOException("[Drops] a list named " + name + " already exists");
            var list = new DropList
            {
                Id = Interlocked.Increment(ref _nextListId), Kind = kind, MonsterIndex = monsterIndex, Name = name, FishingType = fishingType,
                Layout = TextLayout.WriteAllLines, UpdatedUtc = DateTime.UtcNow, Rows = Array.Empty<DropRow>(),
            };
            DesignStore.Run(conn =>
            {
                using var tx = conn.BeginTransaction();
                Save(conn, tx, list);
                tx.Commit();
                return 0;
            });
            Commit(list);
            return list;
        }
    }
}
