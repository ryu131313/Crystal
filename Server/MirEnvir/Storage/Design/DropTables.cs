using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Server.MirDatabase;

namespace Server.MirEnvir.Storage
{
    /// <summary>The <c>kind</c> of a <c>drops</c> row.</summary>
    internal static class DropKind
    {
        public const string Item = "item", Gold = "gold", Group = "group", Include = "include", Note = "note", Disabled = "disabled";
    }

    /// <summary>One row of a drop list (<c>drops</c> table), in line order.</summary>
    internal sealed class DropRow
    {
        public long Id;
        /// <summary>Index of the parent group row in <see cref="DropList.Rows"/>; -1 at the top level.</summary>
        public int Parent = -1;
        /// <summary>Position within the parent (or the top level).</summary>
        public int Position;
        public string Kind;
        public int Chance;
        public long? ItemIndex;
        /// <summary>The item as written; the rendered line keeps it until the item is renamed.</summary>
        public string ItemName;
        public bool Quest;
        public uint Gold;
        /// <summary>"", "*" (random) or "^" (first).</summary>
        public string GroupMode;
        public byte? DragonLevel;
        public long? IncludeListId;
        /// <summary>The line as written (every row has it in phase 1; the editors still read and write text).</summary>
        public string Text;
    }

    /// <summary>One drop list (<c>drop_lists</c> table): a former Envir\Drops file and its owner.</summary>
    internal sealed class DropList
    {
        public const string Monster = "monster", Named = "named", Fishing = "fishing", Awakening = "awakening", Strongbox = "strongbox",
            Blackstone = "blackstone", Dragon = "dragon";

        public long Id;
        public string Kind;
        /// <summary>The owning monster (kind <see cref="Monster"/>): a monster without DropPath; the list follows its renames.</summary>
        public long? MonsterIndex;
        /// <summary>The file name relative to the drop folder ('/'-separated), as last written.</summary>
        public string Name;
        public int? FishingType;
        public DateTime UpdatedUtc;
        public TextLayout Layout;
        public DropRow[] Rows;

        public bool IsDragon => Kind == Dragon;
    }

    /// <summary>
    /// Drops as tables (Drops-Tables-Plan phase 1): <c>drop_lists</c> (one per former file, with its owner) and <c>drops</c> (one row per
    /// line: typed item / gold / group rows with Index links, groups nested by <c>parent_id</c>, #INSERT lines as <c>include</c> rows,
    /// comments and blanks as <c>note</c> rows, lines the game can't load as <c>disabled</c> rows with their text).
    /// <list type="bullet">
    /// <item><b>The game reads rows:</b> <see cref="LoadInto"/> / <see cref="LoadDragon"/> build the lists the text loaders built, with the
    /// same order, types, groups, includes and bad-line log, without parsing text (only a disabled row's text is tried again, as the game
    /// tried every line: an item created since then loads).</item>
    /// <item><b>The editors still see text:</b> <see cref="DesignTexts"/> routes the drops kind here; every row keeps its line, so a list
    /// renders back exactly (a renamed item under its new name), and saved text is parsed into rows the way the game reads it.</item>
    /// <item><b>Cache:</b> every list is in memory once loaded (an immutable snapshot swapped under a short lock, no I/O inside it), so the
    /// game thread never queries the store. Its writes (a missing list's default) update the cache and are posted; other threads write
    /// the store first, then the cache.</item>
    /// </list>
    /// </summary>
    internal static partial class DropTables
    {
        /// <summary>meta key: set once the drop_lines table was converted (or the drops were imported straight into these tables).</summary>
        public const string ConvertedMarker = "drops_tables_utc";

        private const string ImportMarkerKind = "drops";
        private const int MaxIncludedLists = 256;

        private static readonly Regex IncludePattern = new Regex(@"#INSERT \[(.*?)\]", RegexOptions.IgnoreCase); // as DropInfo.ParseInsert

        public static DesignTextFormat Format => DesignTexts.FormatOf("drops");

        public static IEnumerable<(string Table, string Ddl)> Tables()
        {
            yield return ("drop_lists", "CREATE TABLE drop_lists (id INTEGER PRIMARY KEY, kind TEXT NOT NULL, monster_index INTEGER, name TEXT NOT NULL, " +
                                        "fishing_type INTEGER, updated_utc TEXT NOT NULL, newline TEXT NOT NULL, final_newline INTEGER NOT NULL, " +
                                        "bom INTEGER NOT NULL) STRICT");
            yield return ("drops", "CREATE TABLE drops (id INTEGER PRIMARY KEY, list_id INTEGER NOT NULL, parent_id INTEGER, position INTEGER NOT NULL, " +
                                   "kind TEXT NOT NULL, chance INTEGER, item_index INTEGER, item_name TEXT, quest INTEGER NOT NULL DEFAULT 0, gold INTEGER, " +
                                   "group_mode TEXT, dragon_level INTEGER, include_list_id INTEGER, text TEXT) STRICT");
        }

        // ------------------------------------------------------------------ cache

        private sealed class State
        {
            public Dictionary<long, DropList> ById;
            /// <summary>File name (as the game builds it, case-insensitive) → list. One list per name.</summary>
            public Dictionary<string, DropList> ByName;
            /// <summary>Monster Index → the monster's own list (kind monster): what a monster without DropPath loads (phase 2).</summary>
            public Dictionary<long, DropList> ByMonster;
        }

        private static volatile State _state;
        private static readonly object Gate = new object();
        private static long _nextListId, _nextRowId;

        /// <summary>Bumped by every reset and by a write that found no cache: a load that started before it is thrown away and redone.</summary>
        private static int _generation;

        /// <summary>Monster Index → (name, DropPath), from the store's last saved data (refreshed with the item names): the owner of a new list.</summary>
        private static volatile Dictionary<long, (string Name, string DropPath)> _monsters = new Dictionary<long, (string, string)>();

        public static void ResetCache()
        {
            lock (Gate)
            {
                _state = null;
                _generation++;
            }
        }

        public static void EnsureLoaded() => Loaded();

        private static State Loaded()
        {
            while (true)
            {
                var s = _state;
                if (s != null) return s;
                int generation;
                lock (Gate) generation = _generation;
                var lists = DesignStore.Run(conn => LoadAll(conn));
                lock (Gate)
                {
                    if (_state != null) return _state;
                    if (generation != _generation) continue; // reset, or written to, while loading: the rows read may be stale
                    _state = BuildState(lists);
                    return _state;
                }
            }
        }

        /// <summary>
        /// Lists keep their file name (the text facade, DropPath lists, the special lists and NPC DROP lists are found by it). A monster
        /// without DropPath finds its own list by Index first (<see cref="ForMonster"/>), so a rename, also one the running world hasn't
        /// loaded yet, keeps its drops; a list other monsters load by name (a DropPath equal to the owner's old name) stays where it is.
        /// </summary>
        private static State BuildState(Dictionary<long, DropList> byId)
        {
            var byName = new Dictionary<string, DropList>(StringComparer.OrdinalIgnoreCase);
            var byMonster = new Dictionary<long, DropList>();
            foreach (var l in byId.Values.OrderBy(l => l.Id))
            {
                byName[l.Name] = l;
                if (l.Kind == DropList.Monster && l.MonsterIndex is long m) byMonster[m] = l;
            }

            return new State { ById = byId, ByName = byName, ByMonster = byMonster };
        }

        /// <summary>The list file name a monster loads by name: DropPath (or, without one, Name) + ".txt", relative to the drop folder.</summary>
        public static string MonsterFileName(string name, string dropPath)
        {
            var file = (string.IsNullOrEmpty(dropPath) ? name : dropPath) + ".txt";
            return TryMap(Path.Combine(Settings.DropPath, file), out var listName) ? listName : file.Replace('\\', '/'); // as Envir.ReloadDrops built the path
        }

        /// <summary>
        /// The list a monster loads (phase 2, D1): with a DropPath the shared list of that name; without, its own list (bound to its Index),
        /// else the list named after it (a list created before the binding, or one shared by name). Null when there is none.
        /// </summary>
        public static DropList ForMonster(long index, string name, string dropPath)
        {
            var s = Loaded();
            if (string.IsNullOrEmpty(dropPath) && s.ByMonster.TryGetValue(index, out var own)) return own;
            if (name == null && string.IsNullOrEmpty(dropPath)) return null;
            if (!s.ByName.TryGetValue(MonsterFileName(name, dropPath), out var l)) return null;
            // A monster without DropPath doesn't take over another monster's own list that still carries its old name (that monster was
            // renamed): it gets a list of its own. A DropPath list, or one bound to a monster of the same name (duplicates), is shared.
            if (string.IsNullOrEmpty(dropPath) && l.Kind == DropList.Monster && l.MonsterIndex is long other && other != index &&
                _monsters.TryGetValue(other, out var owner) && owner.Name != null && !string.Equals(owner.Name, name, StringComparison.OrdinalIgnoreCase))
                return null;
            return l;
        }

        /// <summary>A free list name for a monster's own new list: Name.txt, or Name_Index.txt when another list holds that name.</summary>
        public static string FreeMonsterListName(string listName, long index)
        {
            var s = Loaded();
            if (!s.ByName.ContainsKey(listName)) return listName;
            var stem = listName.Substring(0, listName.Length - 4) + "_" + index.ToString(CultureInfo.InvariantCulture);
            var name = stem + ".txt";
            for (var n = 2; s.ByName.ContainsKey(name); n++) name = stem + "_" + n.ToString(CultureInfo.InvariantCulture) + ".txt";
            return name;
        }

        /// <summary>The owner of a new list from its name, never a monster that already has its own list (it keeps that one).</summary>
        /// <param name="listId">The list being renamed (its own monster may keep it), or null for a new list.</param>
        public static (string Kind, long? Monster, int? Fishing) OwnerForNew(string name, IEnumerable<(long Index, string Name, string DropPath)> monsters, long? listId = null)
        {
            var s = Loaded();
            return OwnerOf(name, monsters.Where(m => !s.ByMonster.TryGetValue(m.Index, out var own) || own.Id == listId));
        }

        public static DropList Get(long id) => Loaded().ById.TryGetValue(id, out var l) ? l : null;

        /// <summary>Every list (a snapshot; lists and rows are never changed in place).</summary>
        public static IReadOnlyCollection<DropList> Lists() => Loaded().ById.Values;

        /// <summary>The list's revision for optimistic concurrency (changes with every write).</summary>
        public static string Rev(DropList l) => l.UpdatedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + l.Rows.Length.ToString(CultureInfo.InvariantCulture);

        /// <summary>True when another list includes <paramref name="l"/> (by link or by name): its owners' drops change too.</summary>
        public static bool IsIncluded(DropList l)
        {
            foreach (var other in Loaded().ById.Values)
            {
                if (other.Id == l.Id || other.IsDragon) continue;
                foreach (var row in other.Rows)
                {
                    if (row.IncludeListId == l.Id) return true;
                    var m = IncludePattern.Match(row.Text ?? string.Empty);
                    if (m.Success && string.Equals(IncludeName(m.Groups[1].Value), l.Name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        /// <summary>Replaces (or with <paramref name="removeId"/> removes) lists in the cache; a list replaces any other of the same name.</summary>
        private static void Commit(DropList put, long? removeId = null)
        {
            lock (Gate)
            {
                var s = _state;
                if (s == null)
                {
                    _generation++; // not loaded: a load in flight may predate this write, so it reloads; the store has the change
                    return;
                }

                var byId = new Dictionary<long, DropList>(s.ById);
                if (removeId != null) byId.Remove(removeId.Value);
                if (put != null)
                {
                    if (s.ByName.TryGetValue(put.Name, out var same) && same.Id != put.Id) byId.Remove(same.Id);
                    byId[put.Id] = put;
                }

                _state = BuildState(byId);
            }
        }

        /// <summary>Monster names and DropPaths for the owner of a monster list (after every design save and load).</summary>
        public static void RefreshMonsters(WorldRows rows)
        {
            var spec = SqliteWorldCodec.Specs.First(s => s.Name == "monsters");
            var cols = SqliteWorldCodec.ColumnNames(spec);
            var name = cols.IndexOf("Name");
            var dropPath = cols.IndexOf("DropPath");
            var map = new Dictionary<long, (string, string)>();
            foreach (var cells in rows.Tables["monsters"].Rows)
                if (cells[2] is long key) map[key] = (cells[name] as string, dropPath >= 0 ? cells[dropPath] as string : null);
            _monsters = map;
        }

        // ------------------------------------------------------------------ the text facade (DesignTexts routes the drops kind here)

        /// <summary>The list name of a path under the drop folder while the store is active.</summary>
        public static bool TryMap(string path, out string listName)
        {
            listName = null;
            return DesignFiles.TryMap(path, out var f, out listName) && f.Kind == "drops";
        }

        public static IEnumerable<string> List() => Loaded().ByName.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        public static bool Exists(string name) => Loaded().ByName.ContainsKey(name);

        public static List<string> Read(string name, Func<string, long, string> nameOf = null) =>
            Loaded().ByName.TryGetValue(name, out var l) ? Render(l, nameOf) : null;

        public static byte[] ReadBytes(string name) => Loaded().ByName.TryGetValue(name, out var l) ? l.Layout.Encode(Render(l, null)) : null;

        public static DateTime? UpdatedUtc(string name) => Loaded().ByName.TryGetValue(name, out var l) ? l.UpdatedUtc : null;

        public static TextLayout? LayoutOf(string name) => Loaded().ByName.TryGetValue(name, out var l) ? l.Layout : null;

        private static List<string> Render(DropList l, Func<string, long, string> nameOf)
        {
            var lines = new List<string>(l.Rows.Length);
            for (var i = 0; i < l.Rows.Length; i++) lines.Add(Render(l.Rows[i], nameOf));
            return lines;
        }

        /// <summary>The row's line: as written, with a renamed item under its current name.</summary>
        internal static string Render(DropRow row, Func<string, long, string> nameOf)
        {
            if (row.Text == null) return Canonical(row, nameOf);
            var text = row.Text;
            if (row.ItemIndex == null || row.ItemName == null) return text;
            var current = DesignTexts.CurrentName("I", row.ItemIndex.Value, row.ItemName, nameOf);
            if (string.Equals(current, row.ItemName, StringComparison.Ordinal)) return text;
            var at = SecondTokenStart(text);
            if (at < 0 || string.CompareOrdinal(text, at, row.ItemName, 0, row.ItemName.Length) != 0) return text;
            return text.Substring(0, at) + current + text.Substring(at + row.ItemName.Length);
        }

        /// <summary>
        /// The line for a row written by the grid editor (no text of its own): <c>1/N Item[ Q][ level]</c>, <c>1/N Gold amount[ level]</c>,
        /// <c>1/N GROUP[*|^]</c>. The game reads it back as the same drop (items under their current, spaceless names).
        /// </summary>
        private static string Canonical(DropRow row, Func<string, long, string> nameOf)
        {
            var chance = "1/" + row.Chance.ToString(CultureInfo.InvariantCulture);
            var level = row.DragonLevel.HasValue ? " " + row.DragonLevel.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            switch (row.Kind)
            {
                case DropKind.Item:
                    var name = row.ItemIndex is long i ? DesignTexts.CurrentName("I", i, row.ItemName ?? string.Empty, nameOf) : row.ItemName;
                    return chance + " " + name + (row.Quest ? " Q" : string.Empty) + level;
                case DropKind.Gold:
                    return chance + " Gold " + row.Gold.ToString(CultureInfo.InvariantCulture) + level;
                case DropKind.Group:
                    return chance + " GROUP" + (row.GroupMode ?? string.Empty);
                default:
                    return string.Empty;
            }
        }

        /// <summary>Where the item token starts: after leading white space, the chance token and its spaces (the game splits on ' ').</summary>
        private static int SecondTokenStart(string text)
        {
            var i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && text[i] != ' ') i++;
            while (i < text.Length && text[i] == ' ') i++;
            return i < text.Length ? i : -1;
        }

        /// <summary>
        /// Parses and stores a list (replacing it). Returns the number of item lines whose item doesn't exist (stored as disabled rows).
        /// On the game thread (a loader creating a missing default list) the cache is updated at once and the store write is posted.
        /// </summary>
        public static int Write(string name, IList<string> lines, TextLayout? layout = null, (string Kind, long? Monster, int? Fishing)? owner = null)
        {
            if (DesignStore.OnGameThread) return WriteCore(name, lines, layout, owner);
            lock (EditGate) return WriteCore(name, lines, layout, owner); // text writes (restore, class toggle) serialise with the grid's saves
        }

        private static int WriteCore(string name, IList<string> lines, TextLayout? layout, (string Kind, long? Monster, int? Fishing)? owner)
        {
            var s = Loaded();
            var lay = layout ?? TextLayout.WriteAllLines;
            DropList list;
            if (s.ByName.TryGetValue(name, out var existing))
                list = new DropList { Id = existing.Id, Kind = existing.Kind, MonsterIndex = existing.MonsterIndex, Name = existing.Name, FishingType = existing.FishingType };
            else
            {
                var (kind, monster, fishing) = owner ?? OwnerForNew(name, MonsterView());
                list = new DropList { Id = Interlocked.Increment(ref _nextListId), Kind = kind, MonsterIndex = monster, Name = name, FishingType = fishing };
            }

            list.Layout = lay;
            list.UpdatedUtc = DateTime.UtcNow;
            int unresolved;
            if (DesignStore.OnGameThread)
            {
                list.Rows = Parse(lines, DesignTexts.ViewNames(), list.IsDragon, out unresolved);
                Finish(list, s);
                Commit(list);
                DesignStore.Post(conn =>
                {
                    using var tx = conn.BeginTransaction();
                    Save(conn, tx, list);
                    tx.Commit();
                }, "drop list save (" + list.Name + ")");
                return unresolved;
            }

            var parseNames = DesignTexts.ParseNames;
            unresolved = DesignStore.Run(conn =>
            {
                list.Rows = Parse(lines, parseNames ?? DesignNames.FromStore(conn), list.IsDragon, out var n);
                Finish(list, s);
                using var tx = conn.BeginTransaction();
                Save(conn, tx, list);
                tx.Commit();
                return n;
            });
            Commit(list);
            return unresolved;
        }

        /// <summary>Renames a list in one transaction (editors only, never the game thread). Its owner follows the new name (a monster
        /// renamed through the web's rename propagation keeps its drops this way); a rename into or out of DragonItem.txt re-reads its lines
        /// the other way.</summary>
        public static void Rename(string from, string to)
        {
            lock (EditGate) RenameCore(from, to);
        }

        private static void RenameCore(string from, string to)
        {
            var s = Loaded();
            if (!s.ByName.TryGetValue(from, out var old)) throw new FileNotFoundException("[Store] " + from + " is not in the drops store");
            var list = new DropList { Id = old.Id, Name = to, Layout = old.Layout, UpdatedUtc = DateTime.UtcNow, Rows = old.Rows };
            (list.Kind, list.MonsterIndex, list.FishingType) = OwnerForNew(to, MonsterView(), old.Id);

            var reparse = list.IsDragon != old.IsDragon;
            var lines = reparse ? Render(old, null) : null;
            var parseNames = DesignTexts.ParseNames;
            DesignStore.Run(conn =>
            {
                if (reparse)
                {
                    list.Rows = Parse(lines, parseNames ?? DesignNames.FromStore(conn), list.IsDragon, out _);
                    Finish(list, s);
                }

                using var tx = conn.BeginTransaction();
                if (reparse) Save(conn, tx, list);
                else
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE drop_lists SET kind = $k, monster_index = $m, name = $n, fishing_type = $f, updated_utc = $t WHERE id = $id";
                    cmd.Parameters.AddWithValue("$k", list.Kind);
                    cmd.Parameters.AddWithValue("$m", (object)list.MonsterIndex ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$n", list.Name);
                    cmd.Parameters.AddWithValue("$f", (object)list.FishingType ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$t", Iso(list.UpdatedUtc));
                    cmd.Parameters.AddWithValue("$id", list.Id);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return 0;
            });
            Commit(list);
        }

        public static void Delete(string name)
        {
            if (DesignStore.OnGameThread) DeleteCore(name);
            else lock (EditGate) DeleteCore(name);
        }

        private static void DeleteCore(string name)
        {
            var s = Loaded();
            if (!s.ByName.TryGetValue(name, out var l)) return;
            if (DesignStore.OnGameThread)
            {
                Commit(null, l.Id);
                DesignStore.Post(conn =>
                {
                    using var tx = conn.BeginTransaction();
                    DeleteList(conn, tx, l.Id);
                    tx.Commit();
                }, "drop list delete (" + l.Name + ")");
                return;
            }

            DesignStore.Run(conn =>
            {
                using var tx = conn.BeginTransaction();
                DeleteList(conn, tx, l.Id);
                tx.Commit();
                return 0;
            });
            Commit(null, l.Id);
        }

        /// <summary>Row ids and include links of a list about to be written (the include targets resolve against <paramref name="s"/>).</summary>
        private static void Finish(DropList list, State s)
        {
            foreach (var row in list.Rows) row.Id = Interlocked.Increment(ref _nextRowId);
            LinkIncludes(list, name => string.Equals(name, list.Name, StringComparison.OrdinalIgnoreCase) ? list : s.ByName.TryGetValue(name, out var t) ? t : null);
        }

        private static IEnumerable<(long Index, string Name, string DropPath)> MonsterView() =>
            _monsters.OrderBy(m => m.Key).Select(m => (m.Key, m.Value.Name, m.Value.DropPath));

        // ------------------------------------------------------------------ owners and parsing

        /// <summary>Who a list belongs to, from its file name (as Envir.ReloadDrops, DragonInfo.LoadDrops and the monsters build them).</summary>
        internal static (string Kind, long? Monster, int? Fishing) OwnerOf(string name, IEnumerable<(long Index, string Name, string DropPath)> monsters)
        {
            bool Is(string file) => string.Equals(name, file, StringComparison.OrdinalIgnoreCase);
            if (Is("DragonItem.txt")) return (DropList.Dragon, null, null);
            for (var i = 0; i < 19; i++)
                if (Is(Settings.FishingDropFilename.Replace("00", i.ToString("D2", CultureInfo.InvariantCulture)) + ".txt")) return (DropList.Fishing, null, i);
            if (Is(Settings.AwakeningDropFilename + ".txt")) return (DropList.Awakening, null, null);
            if (Is(Settings.StrongboxDropFilename + ".txt")) return (DropList.Strongbox, null, null);
            if (Is(Settings.BlackstoneDropFilename + ".txt")) return (DropList.Blackstone, null, null);
            foreach (var m in monsters)
                if (string.IsNullOrEmpty(m.DropPath) && m.Name != null && Is(m.Name + ".txt")) return (DropList.Monster, m.Index, null);
            return (DropList.Named, null, null);
        }

        /// <summary>
        /// Rows for a list's lines, classified exactly as the game reads them (DropInfo.Load / ParseGroup / ParseInsert, or DragonInfo for
        /// <paramref name="dragon"/>). One row per line, in line order. A typed line naming an item that doesn't exist, or a line the game
        /// can't read (it logs those), becomes a disabled row; <paramref name="unresolved"/> counts the missing items.
        /// </summary>
        internal static DropRow[] Parse(IList<string> lines, DesignNames names, bool dragon, out int unresolved)
        {
            var rows = new DropRow[lines.Count];
            var missing = 0;
            if (dragon)
            {
                for (var i = 0; i < lines.Count; i++) rows[i] = DragonRow(lines[i] ?? string.Empty, names, ref missing);
            }
            else
            {
                var top = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    if (rows[i] != null) continue; // taken by a group above
                    var line = lines[i] ?? string.Empty;
                    var row = IsIncludeLine(line) ? new DropRow { Kind = DropKind.Include, Text = line }
                        : line.StartsWith(";") || string.IsNullOrWhiteSpace(line) ? new DropRow { Kind = DropKind.Note, Text = line }
                        : TypedRow(line, line, names, ref missing);
                    row.Position = top++;
                    rows[i] = row;
                    if (row.Kind == DropKind.Group) ParseGroup(lines, rows, i, names, ref missing);
                }
            }

            unresolved = missing;
            return rows;
        }

        /// <summary>As DropInfo.ParseGroup: every following line up to the first "}" belongs to the group (trimmed when read).</summary>
        private static void ParseGroup(IList<string> lines, DropRow[] rows, int group, DesignNames names, ref int missing)
        {
            var position = 0;
            for (var j = group + 1; j < lines.Count; j++)
            {
                if (rows[j] != null) continue; // a nested group's line
                var text = lines[j] ?? string.Empty;
                DropRow row;
                if (IsIncludeLine(text)) row = new DropRow { Kind = DropKind.Include, Text = text }; // the game removed these before grouping
                else
                {
                    var line = text.Trim();
                    row = line == "{" || line == "}" || line.StartsWith(";") || string.IsNullOrWhiteSpace(line)
                        ? new DropRow { Kind = DropKind.Note, Text = text }
                        : TypedRow(text, line, names, ref missing);
                }

                row.Parent = group;
                row.Position = position++;
                rows[j] = row;
                if (row.Kind == DropKind.Group) ParseGroup(lines, rows, j, names, ref missing);
                if (row.Kind == DropKind.Note && text.Trim() == "}") return;
            }
        }

        private static bool IsIncludeLine(string line) => line.ToUpper().StartsWith("#INSERT");

        private static DropRow TypedRow(string text, string line, DesignNames names, ref int missing)
        {
            var row = new DropRow { Text = text, Kind = DropKind.Disabled };
            bool ok;
            DropInfo.LineParts p;
            try { ok = DropInfo.TryParseLine(line, out p); }
            catch (ArgumentOutOfRangeException) { ok = false; p = default; } // a first token shorter than "1/": the text loader throws on it
            if (!ok) return row;

            row.Chance = p.Chance;
            if (p.IsGold)
            {
                row.Kind = DropKind.Gold;
                row.Gold = p.Gold;
            }
            else if (p.IsGroup)
            {
                row.Kind = DropKind.Group;
                row.GroupMode = p.Random ? "*" : p.First ? "^" : string.Empty;
            }
            else
            {
                var item = names.Item(p.ItemName);
                if (item == null)
                {
                    missing++;
                    row.Chance = 0;
                    return row;
                }

                row.Kind = DropKind.Item;
                row.ItemIndex = item;
                row.ItemName = p.ItemName;
                row.Quest = p.Quest;
            }

            return row;
        }

        private static DropRow DragonRow(string line, DesignNames names, ref int missing)
        {
            if (line.StartsWith(";") || string.IsNullOrWhiteSpace(line)) return new DropRow { Kind = DropKind.Note, Text = line };
            var row = new DropRow { Text = line, Kind = DropKind.Disabled };
            bool ok;
            int chance;
            uint gold;
            string itemName;
            byte level;
            try { ok = DragonInfo.DropInfo.TryParseLine(line, out chance, out gold, out itemName, out level); }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException) { return row; } // the text loader throws on these
            if (!ok) return row;

            if (itemName != null)
            {
                var item = names.Item(itemName);
                if (item == null)
                {
                    missing++;
                    return row;
                }

                row.Kind = DropKind.Item;
                row.ItemIndex = item;
                row.ItemName = itemName;
            }
            else
            {
                row.Kind = DropKind.Gold;
                row.Gold = gold;
            }

            row.Chance = chance;
            row.DragonLevel = level;
            return row;
        }

        /// <summary>The list an #INSERT names ("sub" relative to the drop folder, as Path.Combine(Settings.DropPath, sub)), or null.</summary>
        internal static string IncludeName(string sub)
        {
            try
            {
                var root = Path.GetFullPath(Format.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(Format.Root, sub));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return full.Substring(root.Length).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private static void LinkIncludes(DropList list, Func<string, DropList> find)
        {
            if (list.IsDragon) return; // DragonItem.txt has no #INSERT
            foreach (var row in list.Rows)
            {
                var m = IncludePattern.Match(row.Text ?? string.Empty);
                if (!m.Success) continue;
                var name = IncludeName(m.Groups[1].Value);
                row.IncludeListId = name == null ? null : find(name)?.Id;
            }
        }

        // ------------------------------------------------------------------ the game's loaders

        /// <summary>
        /// Adds the list stored as <paramref name="listName"/> to <paramref name="list"/> exactly as DropInfo.Load added the file's drops
        /// (top-level drops get <paramref name="type"/>, then the whole list is sorted). False when there is no such list.
        /// </summary>
        public static bool LoadInto(List<DropInfo> list, string name, string listName, byte type, Action<string, string> badLine)
        {
            var s = Loaded();
            if (!s.ByName.TryGetValue(listName, out var root)) return false;
            LoadInto(list, name, root, type, badLine);
            return true;
        }

        /// <summary>As <see cref="LoadInto(List{DropInfo}, string, string, byte, Action{string, string})"/> for a list already found (a monster's own list).</summary>
        public static void LoadInto(List<DropInfo> list, string name, DropList root, byte type, Action<string, string> badLine)
        {
            var s = Loaded();
            // #INSERT: every included list's drops go after the including list's, transitively, in the order the text loader appended them.
            var segments = new List<(DropList List, int From)> { (root, -1) };
            for (var k = 0; k < segments.Count; k++)
            {
                var seg = segments[k].List;
                for (var r = 0; r < seg.Rows.Length; r++)
                {
                    var row = seg.Rows[r];
                    // The name in the line first, as the text loader read it; the stored link when no list has that name (any more).
                    var m = IncludePattern.Match(Render(row, null));
                    if (!m.Success) continue;
                    var sub = m.Groups[1].Value;
                    var includeName = IncludeName(sub);
                    DropList target = null;
                    if (includeName != null) s.ByName.TryGetValue(includeName, out target);
                    if (target == null && row.IncludeListId is long id) s.ById.TryGetValue(id, out target);

                    if (target == null)
                    {
                        MessageQueue.Instance.Enqueue("[Drops] " + name + ": #INSERT [" + sub + "] names a drop list that doesn't exist; skipped.");
                        continue;
                    }

                    var cycle = false;
                    for (var c = k; c >= 0; c = segments[c].From) cycle |= segments[c].List == target;
                    if (cycle || segments.Count >= MaxIncludedLists)
                    {
                        MessageQueue.Instance.Enqueue("[Drops] " + name + ": #INSERT of " + target.Name + (cycle ? " includes itself" : " goes past " + MaxIncludedLists + " lists") + "; skipped.");
                        continue;
                    }

                    segments.Add((target, k));
                }
            }

            // A list with includes is assembled from all its lists' lines, as the text loader appended them: a group left open at the end
            // of one list takes the next list's lines, and an included DragonItem.txt is read the monster way. (No live list has an #INSERT.)
            var rows = root.Rows;
            if (segments.Count > 1 || root.IsDragon)
            {
                var all = new List<string>();
                for (var k = 0; k < segments.Count; k++) all.AddRange(Render(segments[k].List, null));
                rows = Parse(all, DesignTexts.ViewNames(), false, out _);
            }

            for (var r = 0; r < rows.Length; r++)
            {
                if (rows[r].Parent != -1) continue;
                var drop = Build(rows, r, name, false, badLine);
                if (drop == null) continue;
                drop.Type = type;
                list.Add(drop);
            }

            list.Sort(DropInfo.CompareForList);
        }

        /// <summary>One drop from a row (null for notes, includes and lines the game can't load, which are logged as the text loader did).</summary>
        private static DropInfo Build(DropRow[] rows, int index, string name, bool inGroup, Action<string, string> badLine)
        {
            var row = rows[index];
            switch (row.Kind)
            {
                case DropKind.Item:
                    var item = Envir.Main.GetItemInfo((int)row.ItemIndex.Value);
                    if (item != null) return new DropInfo { Chance = row.Chance, Item = item, QuestRequired = row.Quest };
                    return Retry(Render(row, null), inGroup, name, badLine); // the item is gone from this world: read the line by name
                case DropKind.Gold:
                    return new DropInfo { Chance = row.Chance, Gold = row.Gold };
                case DropKind.Group:
                    var drop = new DropInfo { Chance = row.Chance, GroupedDrop = new GroupDropInfo { Random = row.GroupMode == "*", First = row.GroupMode == "^" } };
                    var members = new List<DropInfo>();
                    bool start = false, finish = false;
                    for (var j = index + 1; j < rows.Length && rows[j].Parent >= index; j++)
                    {
                        if (rows[j].Parent != index) continue;
                        var child = rows[j];
                        if (child.Kind == DropKind.Note)
                        {
                            var t = (child.Text ?? string.Empty).Trim();
                            if (t == "{") start = true;
                            else if (t == "}") finish = true;
                            continue;
                        }

                        var member = Build(rows, j, name, true, badLine);
                        if (member != null) members.Add(member);
                    }

                    if (start && finish) drop.GroupedDrop.AddRange(members);
                    return drop;
                case DropKind.Disabled:
                    return Retry(row.Text ?? string.Empty, inGroup, name, badLine);
                default:
                    return null;
            }
        }

        /// <summary>A line read by name as the text loader read it (a disabled line whose item may exist by now, or an item that's gone).</summary>
        private static DropInfo Retry(string text, bool inGroup, string name, Action<string, string> badLine)
        {
            var line = inGroup ? text.Trim() : text;
            DropInfo drop = null;
            try { drop = DropInfo.FromLine(line); }
            catch (ArgumentOutOfRangeException) { } // the text loader threw here; logged instead
            if (drop == null || drop.GroupedDrop != null)
            {
                badLine(name, line);
                return null;
            }

            return drop;
        }

        /// <summary>
        /// Fills the dragon's level lists from DragonItem.txt's rows exactly as DragonInfo.LoadDrops did (a level outside 1..MaxDragonLevel
        /// stops the load unsorted). False when there is no such list.
        /// </summary>
        public static bool LoadDragon(List<DragonInfo.DropInfo>[] drops, string listName, Action<string> badLine)
        {
            var s = Loaded();
            if (!s.ByName.TryGetValue(listName, out var l)) return false;
            var rows = l.IsDragon ? l.Rows : Parse(Render(l, null), DesignTexts.ViewNames(), true, out _);
            foreach (var row in rows)
            {
                DragonInfo.DropInfo drop = null;
                switch (row.Kind)
                {
                    case DropKind.Item:
                        var item = Envir.Main.GetItemInfo((int)row.ItemIndex.Value);
                        drop = item != null
                            ? new DragonInfo.DropInfo { Chance = row.Chance, Item = item, level = row.DragonLevel ?? 0 }
                            : RetryDragon(Render(row, null), badLine);
                        break;
                    case DropKind.Gold:
                        drop = new DragonInfo.DropInfo { Chance = row.Chance, Gold = row.Gold, level = row.DragonLevel ?? 0 };
                        break;
                    case DropKind.Disabled:
                        drop = RetryDragon(row.Text ?? string.Empty, badLine);
                        break;
                }

                if (drop == null) continue;
                if (drop.level <= 0 || drop.level > Globals.MaxDragonLevel) return true;
                drops[drop.level - 1].Add(drop);
            }

            for (var i = 0; i < Globals.MaxDragonLevel; i++)
            {
                drops[i].Sort((drop1, drop2) =>
                {
                    if (drop1.Gold > 0 && drop2.Gold == 0) return 1;
                    if (drop1.Gold == 0 && drop2.Gold > 0) return -1;
                    if (drop1.Item == null || drop2.Item == null) return 0; // two gold rows: the text loader threw here
                    return drop1.Item.Type.CompareTo(drop2.Item.Type);
                });
            }

            return true;
        }

        private static DragonInfo.DropInfo RetryDragon(string line, Action<string> badLine)
        {
            DragonInfo.DropInfo drop = null;
            try { drop = DragonInfo.DropInfo.FromLine(line); }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException) { } // the text loader threw; logged instead
            if (drop == null) badLine(line);
            return drop;
        }

        // ------------------------------------------------------------------ store (writer thread)

        private static Dictionary<long, DropList> LoadAll(SqliteConnection conn)
        {
            if (!IsImported(conn)) Import(conn);
            else if (Scalar(conn, "SELECT value FROM meta WHERE name = '" + ConvertedMarker + "'") == null)
                ConvertLines(conn, MessageQueue.Instance.Enqueue, null); // opened without the writer's open steps (should not happen)
            Exec(conn, null, "CREATE INDEX IF NOT EXISTS drops_list ON drops (list_id, id)");

            var lists = new Dictionary<long, DropList>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, kind, monster_index, name, fishing_type, updated_utc, newline, final_newline, bom FROM drop_lists";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    lists[r.GetInt64(0)] = new DropList
                    {
                        Id = r.GetInt64(0),
                        Kind = r.GetString(1),
                        MonsterIndex = r.IsDBNull(2) ? null : r.GetInt64(2),
                        Name = r.GetString(3),
                        FishingType = r.IsDBNull(4) ? null : (int)r.GetInt64(4),
                        UpdatedUtc = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        Layout = new TextLayout(r.GetString(6), r.GetInt64(7) != 0, r.GetInt64(8) != 0),
                    };
            }

            var rows = lists.Keys.ToDictionary(k => k, _ => new List<DropRow>());
            var parents = new Dictionary<long, long?>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, list_id, parent_id, position, kind, chance, item_index, item_name, quest, gold, group_mode, dragon_level, " +
                                  "include_list_id, text FROM drops ORDER BY list_id, id";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!rows.TryGetValue(r.GetInt64(1), out var list)) continue; // an orphan row (its list is gone)
                    var row = new DropRow
                    {
                        Id = r.GetInt64(0),
                        Position = (int)r.GetInt64(3),
                        Kind = r.GetString(4),
                        Chance = r.IsDBNull(5) ? 0 : (int)r.GetInt64(5),
                        ItemIndex = r.IsDBNull(6) ? null : r.GetInt64(6),
                        ItemName = r.IsDBNull(7) ? null : r.GetString(7),
                        Quest = r.GetInt64(8) != 0,
                        Gold = r.IsDBNull(9) ? 0 : (uint)r.GetInt64(9),
                        GroupMode = r.IsDBNull(10) ? null : r.GetString(10),
                        DragonLevel = r.IsDBNull(11) ? null : (byte)r.GetInt64(11),
                        IncludeListId = r.IsDBNull(12) ? null : r.GetInt64(12),
                        Text = r.IsDBNull(13) ? null : r.GetString(13),
                    };
                    parents[row.Id] = r.IsDBNull(2) ? null : r.GetInt64(2);
                    list.Add(row);
                }
            }

            long maxRow = 0;
            foreach (var l in lists.Values)
            {
                var list = rows[l.Id];
                var at = new Dictionary<long, int>(list.Count);
                for (var i = 0; i < list.Count; i++) at[list[i].Id] = i;
                for (var i = 0; i < list.Count; i++)
                {
                    list[i].Parent = parents[list[i].Id] is long p && at.TryGetValue(p, out var pi) ? pi : -1;
                    maxRow = Math.Max(maxRow, list[i].Id);
                }

                l.Rows = list.ToArray();
            }

            InterlockedMax(ref _nextListId, lists.Count == 0 ? 0 : lists.Keys.Max());
            InterlockedMax(ref _nextRowId, maxRow);
            return lists;
        }

        private static void InterlockedMax(ref long field, long value)
        {
            long seen;
            while ((seen = Interlocked.Read(ref field)) < value && Interlocked.CompareExchange(ref field, value, seen) != seen) { }
        }

        private static bool IsImported(SqliteConnection conn) =>
            Scalar(conn, "SELECT 1 FROM design_files WHERE kind = '" + ImportMarkerKind + "' AND name = ''") != null;

        /// <summary>First use on a store that never had drops: imports every file under the drop folder straight into the tables.</summary>
        private static void Import(SqliteConnection conn)
        {
            var (names, _) = StoreNames(conn);
            var monsters = StoreMonsters(conn);
            var root = Path.GetFullPath(Format.Root);
            var lists = new List<DropList>();
            var unresolved = 0;
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var (lines, layout) = TextLayout.Decode(File.ReadAllBytes(path));
                    lists.Add(NewList(Path.GetRelativePath(root, path).Replace('\\', '/'), lines, layout, File.GetLastWriteTimeUtc(path), names, monsters, ref unresolved));
                }
            }

            using var tx = conn.BeginTransaction();
            SaveAll(conn, tx, lists);
            Exec(conn, tx, "INSERT INTO design_files (kind, name, imported_utc, updated_utc, newline, final_newline, bom) VALUES ('" + ImportMarkerKind + "', '', $t, $t, '\r\n', 1, 0)",
                ("$t", Iso(DateTime.UtcNow)));
            Upsert(conn, tx, ConvertedMarker, Iso(DateTime.UtcNow));
            tx.Commit();
            MessageQueue.Instance.Enqueue("[Store] Imported " + lists.Count.ToString(CultureInfo.InvariantCulture) + " drop file(s) from " + root + " into the drop tables" +
                                          (unresolved > 0 ? "; " + unresolved.ToString(CultureInfo.InvariantCulture) + " line(s) name an item that doesn't exist (kept as disabled rows, skipped by the game as before)" : string.Empty));
        }

        /// <summary>
        /// Converts the phase 7.4 <c>drop_lines</c> table into the drop tables (on store open, after Migrate; idempotent through the meta
        /// marker). Every file is rendered as the game read it (current item names) and parsed into rows; then drop_lines and the drop
        /// files' design_files rows go. With <paramref name="backupPath"/> the store is copied there first. Returns the lists converted.
        /// </summary>
        public static int ConvertLines(SqliteConnection conn, Action<string> log, string backupPath)
        {
            if (Scalar(conn, "SELECT value FROM meta WHERE name = '" + ConvertedMarker + "'") != null) return 0;
            var hasLines = Scalar(conn, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'drop_lines'") != null;
            if (!hasLines || !IsImported(conn))
            {
                // Nothing to convert: drops come straight from the files on first use.
                if (hasLines) Exec(conn, null, "DROP TABLE drop_lines");
                return 0;
            }

            if (backupPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath)));
                SqliteWorldFile.BackupTo(conn, backupPath);
            }

            var (names, itemNames) = StoreNames(conn);
            var monsters = StoreMonsters(conn);
            var format = DesignTexts.FormatOf("drops");
            var files = new List<(string Name, DateTime Updated, TextLayout Layout)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, updated_utc, newline, final_newline, bom FROM design_files WHERE kind = '" + ImportMarkerKind + "' AND name <> '' ORDER BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    files.Add((r.GetString(0), DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        new TextLayout(r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(4) != 0)));
            }

            var lines = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, " + string.Join(", ", format.Columns.Select(c => SqliteWorldCodec.Quote(c.Name))) + " FROM drop_lines ORDER BY name, position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var cells = new object[format.Columns.Length];
                    for (var i = 0; i < cells.Length; i++) cells[i] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
                    if (!lines.TryGetValue(r.GetString(0), out var list)) lines[r.GetString(0)] = list = new List<string>();
                    list.Add(format.Render(cells, (kind, index) => kind == "I" && itemNames.TryGetValue(index, out var n) ? n : null));
                }
            }

            var lists = new List<DropList>();
            var unresolved = 0;
            foreach (var (name, updated, layout) in files)
                lists.Add(NewList(name, lines.TryGetValue(name, out var l) ? l : new List<string>(), layout, updated, names, monsters, ref unresolved));

            using var tx = conn.BeginTransaction();
            SaveAll(conn, tx, lists);
            Exec(conn, tx, "DELETE FROM design_files WHERE kind = '" + ImportMarkerKind + "' AND name <> ''");
            Exec(conn, tx, "DROP TABLE drop_lines");
            Upsert(conn, tx, ConvertedMarker, Iso(DateTime.UtcNow));
            tx.Commit();

            var byKind = lists.SelectMany(x => x.Rows).GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture));
            var owners = lists.GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture));
            log("[Store] Converted " + lists.Count.ToString(CultureInfo.InvariantCulture) + " drop file(s) into the drop tables (lists: " + string.Join(", ", owners) +
                "; rows: " + string.Join(", ", byKind) + ")" + (backupPath != null ? "; the store was copied to " + backupPath + " first" : string.Empty) + ".");
            return lists.Count;
        }

        private static DropList NewList(string name, IList<string> lines, TextLayout layout, DateTime updated, DesignNames names,
            List<(long Index, string Name, string DropPath)> monsters, ref int unresolved)
        {
            var (kind, monster, fishing) = OwnerOf(name, monsters);
            var list = new DropList { Kind = kind, MonsterIndex = monster, Name = name, FishingType = fishing, Layout = layout, UpdatedUtc = updated };
            list.Rows = Parse(lines, names, list.IsDragon, out var n);
            unresolved += n;
            return list;
        }

        /// <summary>Gives a batch of new lists ids, links their includes to each other and inserts them.</summary>
        private static void SaveAll(SqliteConnection conn, SqliteTransaction tx, List<DropList> lists)
        {
            var nextList = Convert.ToInt64(Scalar(conn, "SELECT coalesce(max(id), 0) FROM drop_lists", tx), CultureInfo.InvariantCulture);
            var nextRow = Convert.ToInt64(Scalar(conn, "SELECT coalesce(max(id), 0) FROM drops", tx), CultureInfo.InvariantCulture);
            var byName = new Dictionary<string, DropList>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lists)
            {
                l.Id = ++nextList;
                foreach (var row in l.Rows) row.Id = ++nextRow;
                byName[l.Name] = l;
            }

            foreach (var l in lists) LinkIncludes(l, n => byName.TryGetValue(n, out var t) ? t : null);
            foreach (var l in lists) Save(conn, tx, l);
        }

        /// <summary>Replaces one list and its rows.</summary>
        private static void Save(SqliteConnection conn, SqliteTransaction tx, DropList l)
        {
            DeleteList(conn, tx, l.Id);
            Exec(conn, tx, "DELETE FROM drops WHERE list_id IN (SELECT id FROM drop_lists WHERE name = $n COLLATE NOCASE)", ("$n", l.Name));
            Exec(conn, tx, "DELETE FROM drop_lists WHERE name = $n COLLATE NOCASE", ("$n", l.Name));
            Exec(conn, tx, "INSERT INTO drop_lists (id, kind, monster_index, name, fishing_type, updated_utc, newline, final_newline, bom) " +
                           "VALUES ($id, $k, $m, $n, $f, $t, $nl, $fn, $b)",
                ("$id", l.Id), ("$k", l.Kind), ("$m", l.MonsterIndex), ("$n", l.Name), ("$f", l.FishingType), ("$t", Iso(l.UpdatedUtc)),
                ("$nl", l.Layout.Newline ?? "\r\n"), ("$fn", l.Layout.FinalNewline ? 1L : 0L), ("$b", l.Layout.Bom ? 1L : 0L));

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO drops (id, list_id, parent_id, position, kind, chance, item_index, item_name, quest, gold, group_mode, dragon_level, include_list_id, text) " +
                              "VALUES ($id, $l, $p, $pos, $k, $c, $i, $in, $q, $g, $gm, $dl, $inc, $t)";
            var names = new[] { "$id", "$l", "$p", "$pos", "$k", "$c", "$i", "$in", "$q", "$g", "$gm", "$dl", "$inc", "$t" };
            var ps = names.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var row in l.Rows)
            {
                var typed = row.Kind == DropKind.Item || row.Kind == DropKind.Gold || row.Kind == DropKind.Group;
                object[] values =
                {
                    row.Id, l.Id, row.Parent >= 0 ? l.Rows[row.Parent].Id : null, (long)row.Position, row.Kind,
                    typed ? (long)row.Chance : null, row.ItemIndex, row.ItemName, row.Quest ? 1L : 0L,
                    row.Kind == DropKind.Gold ? (long)row.Gold : null, row.Kind == DropKind.Group ? row.GroupMode : null,
                    row.DragonLevel.HasValue ? (long)row.DragonLevel.Value : null, row.IncludeListId, row.Text,
                };
                for (var i = 0; i < ps.Length; i++) ps[i].Value = values[i] ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
        }

        private static void DeleteList(SqliteConnection conn, SqliteTransaction tx, long id)
        {
            Exec(conn, tx, "DELETE FROM drops WHERE list_id = $id", ("$id", id));
            Exec(conn, tx, "DELETE FROM drop_lists WHERE id = $id", ("$id", id));
        }

        /// <summary>Item name → Index lookups (as the game matches names) and Index → name as written, spaces removed (rendering).</summary>
        private static (DesignNames Names, Dictionary<long, string> ItemNames) StoreNames(SqliteConnection conn)
        {
            var itemNames = new Dictionary<long, string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT key, \"Name\" FROM items ORDER BY position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (!r.IsDBNull(1)) itemNames.TryAdd(r.GetInt64(0), r.GetString(1).Replace(" ", string.Empty));
            }

            return (DesignNames.FromStore(conn), itemNames);
        }

        private static List<(long Index, string Name, string DropPath)> StoreMonsters(SqliteConnection conn)
        {
            var list = new List<(long, string, string)>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, \"Name\", \"DropPath\" FROM monsters ORDER BY position";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return list;
        }

        private static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

        private static object Scalar(SqliteConnection conn, string sql, SqliteTransaction tx = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void Upsert(SqliteConnection conn, SqliteTransaction tx, string name, string value) =>
            Exec(conn, tx, "INSERT INTO meta (name, value) VALUES ($n, $v) ON CONFLICT(name) DO UPDATE SET value = excluded.value", ("$n", name), ("$v", value));
    }
}
