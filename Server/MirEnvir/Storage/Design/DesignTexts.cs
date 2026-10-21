using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// One text design format (roadmap P7 phase 7.4): where its files live, its line table, and how a line becomes typed cells and back.
    /// Lines the format can't type (comments, blanks, unparsable or unresolved lines) are kept verbatim in the <c>text</c> column, so
    /// nothing is ever lost and a file renders back to text the game's own parser reads exactly as before.
    /// </summary>
    internal abstract class DesignTextFormat
    {
        /// <summary>"routes", "drops", "quests", "recipes".</summary>
        public abstract string Kind { get; }

        /// <summary>The folder the files live in (Settings.RoutePath, ...), read when used.</summary>
        public abstract string Root { get; }

        /// <summary>Line table name.</summary>
        public abstract string Table { get; }

        /// <summary>Typed columns after (name, position), in cell order; the last is always <c>text</c>.</summary>
        public abstract (string Name, string SqlType)[] Columns { get; }

        /// <summary>Typed cells for one line (long / string / null); unresolved names come back as a text row and are counted.</summary>
        public abstract object[] Parse(string line, DesignNames names, ParseState state);

        /// <summary>The line for typed cells; <paramref name="nameOf"/> gives the current name for an item ("I") or monster ("M") index.</summary>
        public abstract string Render(object[] cells, Func<string, long, string> nameOf);

        /// <summary>Per-file parse state (sections).</summary>
        internal sealed class ParseState
        {
            public string Section;
            public int Unresolved;
        }

        protected object[] TextRow(string line)
        {
            var cells = new object[Columns.Length];
            cells[cells.Length - 1] = line;
            return cells;
        }
    }

    /// <summary>Name → index lookups over the store's items and monsters, with the game's own matching rules.</summary>
    internal sealed class DesignNames
    {
        private readonly Dictionary<string, long> _items = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _monsters = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _monstersNoSpace = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static DesignNames FromStore(SqliteConnection conn)
        {
            var n = new DesignNames();
            // First match in list order wins, as Envir.GetItemInfo / GetMonsterInfo do.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT key, \"Name\" FROM items ORDER BY position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (!r.IsDBNull(1)) n._items.TryAdd(r.GetString(1).Replace(" ", string.Empty), r.GetInt64(0));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT key, \"Name\" FROM monsters ORDER BY position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(1)) continue;
                    var name = r.GetString(1);
                    n._monsters.TryAdd(name, r.GetInt64(0));
                    n._monstersNoSpace.TryAdd(name.Replace(" ", string.Empty), r.GetInt64(0));
                }
            }

            return n;
        }

        /// <summary>Lookups over an envir's item and monster lists (rename propagation parses with the editor copy's names).</summary>
        public static DesignNames FromEnvir(Envir e)
        {
            var n = new DesignNames();
            foreach (var i in e.ItemInfoList) if (i?.Name != null) n._items.TryAdd(i.Name.Replace(" ", string.Empty), i.Index);
            foreach (var m in e.MonsterInfoList)
            {
                if (m?.Name == null) continue;
                n._monsters.TryAdd(m.Name, m.Index);
                n._monstersNoSpace.TryAdd(m.Name.Replace(" ", string.Empty), m.Index);
            }

            return n;
        }

        /// <summary>Lookups from the in-memory names view (index → spaceless name), for parsing without a query.</summary>
        public static DesignNames FromView(IReadOnlyDictionary<(string, long), string> view)
        {
            var n = new DesignNames();
            foreach (var pair in view.OrderBy(p => p.Key.Item2))
            {
                if (pair.Key.Item1 == "I") n._items.TryAdd(pair.Value, pair.Key.Item2);
                else
                {
                    n._monsters.TryAdd(pair.Value, pair.Key.Item2);
                    n._monstersNoSpace.TryAdd(pair.Value, pair.Key.Item2);
                }
            }

            return n;
        }

        /// <summary>As Envir.GetItemInfo(name): case-insensitive against the item name with its spaces removed.</summary>
        public long? Item(string name) => _items.TryGetValue(name, out var i) ? i : null;

        /// <summary>As Envir.GetMonsterInfo(name): case-insensitive, also with spaces removed on both sides.</summary>
        public long? Monster(string name) =>
            _monsters.TryGetValue(name, out var i) ? i : _monstersNoSpace.TryGetValue(name.Replace(" ", string.Empty), out var j) ? j : null;
    }

    /// <summary>
    /// How a text file's lines were laid out as bytes (UTF-8): its line ending, whether it ends with one, and a BOM. Kept per file so the
    /// web Scripts editor and <c>export-files</c> give back the original bytes. <see cref="WriteAllLines"/> is File.WriteAllLines' layout.
    /// </summary>
    internal readonly record struct TextLayout(string Newline, bool FinalNewline, bool Bom)
    {
        public static readonly TextLayout WriteAllLines = new TextLayout("\r\n", true, false);

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        /// <summary>Lines as File.ReadAllLines reads them, and the layout that writes them back as <paramref name="bytes"/>.</summary>
        public static (List<string> Lines, TextLayout Layout) Decode(byte[] bytes)
        {
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = System.Text.Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : "\r\n";
            var final = text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);
            return (DesignFiles.SplitLines(text), new TextLayout(newline, final, bom));
        }

        public byte[] Encode(IList<string> lines)
        {
            var text = string.Join(Newline, lines) + (FinalNewline && lines.Count > 0 ? Newline : string.Empty);
            var body = System.Text.Encoding.UTF8.GetBytes(text);
            return Bom ? Utf8Bom.Concat(body).ToArray() : body;
        }
    }

    /// <summary>
    /// The text design files in SystemData.db: routes, drops (incl. the fishing / awakening / strongbox / blackstone and dragon lists),
    /// quest bodies and recipes (roadmap P7 phase 7.4). One <c>design_files</c> row per file plus the format's line table.
    /// <list type="bullet">
    /// <item><b>Import</b> of a whole kind on first use, from its folder (recorded in <c>design_files</c> with name <c>""</c>). Names are
    /// resolved against the store's items and monsters; a line whose name doesn't resolve is kept as text and counted in the log, as the
    /// game skips it today.</item>
    /// <item><b>Cache:</b> every file's rows are kept in memory once a kind is loaded, and every runtime read renders from the cache with
    /// the caller's current names (<see cref="DesignFiles"/>), so the game thread never queries the store. Writes go to the store first,
    /// then the cache.</item>
    /// </list>
    /// </summary>
    internal static class DesignTexts
    {
        public static readonly DesignTextFormat[] Formats = { new RouteFormat(), new DropFormat(), new QuestFormat(), new RecipeFormat() };

        /// <summary>Names to resolve written lines against instead of the store's (rename propagation: the editor copy's names).</summary>
        [ThreadStatic] private static DesignNames _parseNames;

        /// <summary>The names set by <see cref="WithParseNames"/> on this thread, or null.</summary>
        internal static DesignNames ParseNames => _parseNames;

        /// <summary>Runs <paramref name="body"/> with writes on this thread resolving names against <paramref name="names"/>.</summary>
        public static T WithParseNames<T>(DesignNames names, Func<T> body)
        {
            var old = _parseNames;
            _parseNames = names;
            try { return body(); }
            finally { _parseNames = old; }
        }

        private const string ImportMarker = "";

        internal sealed class FileRows
        {
            public string Name;
            public object[][] Rows;
            public DateTime UpdatedUtc;
            public TextLayout Layout;
        }

        // ------------------------------------------------------------------ names (rendering)

        /// <summary>Item and monster index → name (spaces removed, as the game's lookups compare), from the store's last saved data.</summary>
        private static volatile Dictionary<(string, long), string> _names = new Dictionary<(string, long), string>();

        /// <summary>The name to write for a reference: the current one if it differs from what the line said (a rename), else as written.</summary>
        public static string CurrentName(string kind, object index, string written, Func<string, long, string> nameOf)
        {
            if (index is not long i) return written;
            var current = (nameOf ?? DefaultName)(kind, i);
            return current == null || string.Equals(current, written, StringComparison.OrdinalIgnoreCase) ? written : current;
        }

        private static string DefaultName(string kind, long index) => _names.TryGetValue((kind, index), out var n) ? n : null;

        private static (Dictionary<(string, long), string> View, DesignNames Names) _viewNames;

        /// <summary>Lookups built from the names view, once per refresh (game-thread writes parse with these).</summary>
        internal static DesignNames ViewNames()
        {
            var cached = _viewNames;
            var view = _names;
            if (cached.View == view && cached.Names != null) return cached.Names;
            var names = DesignNames.FromView(view);
            _viewNames = (view, names);
            return names;
        }

        /// <summary>Refreshes the names from saved rows (after every design save and load; Envir.Edit is the only writer of names).</summary>
        public static void RefreshNames(WorldRows rows)
        {
            var map = new Dictionary<(string, long), string>();
            Add(map, rows, "items", "I");
            Add(map, rows, "monsters", "M");
            _names = map;
            DropTables.RefreshMonsters(rows);
        }

        private static void Add(Dictionary<(string, long), string> map, WorldRows rows, string table, string kind)
        {
            var spec = SqliteWorldCodec.Specs.First(s => s.Name == table);
            var col = SqliteWorldCodec.ColumnNames(spec).IndexOf("Name");
            foreach (var cells in rows.Tables[table].Rows)
                if (cells[col] is string name && cells[2] is long key) map.TryAdd((kind, key), name.Replace(" ", string.Empty));
        }

        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FileRows>> Cache =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, FileRows>>(StringComparer.Ordinal);

        public static IEnumerable<(string Table, string Ddl)> Tables()
        {
            yield return ("design_files", "CREATE TABLE design_files (kind TEXT NOT NULL, name TEXT NOT NULL, imported_utc TEXT NOT NULL, " +
                                          "updated_utc TEXT NOT NULL, newline TEXT NOT NULL, final_newline INTEGER NOT NULL, bom INTEGER NOT NULL, " +
                                          "PRIMARY KEY (kind, name)) STRICT");
            foreach (var f in Formats.Where(x => !(x is DropFormat))) // drops: DropTables.Tables (Drops-Tables-Plan phase 1)
                yield return (f.Table, "CREATE TABLE " + f.Table + " (name TEXT NOT NULL, position INTEGER NOT NULL, " +
                                       string.Join(", ", f.Columns.Select(c => SqliteWorldCodec.Quote(c.Name) + " " + c.SqlType)) +
                                       ", PRIMARY KEY (name, position)) STRICT");
        }

        public static DesignTextFormat FormatOf(string kind) => Formats.First(f => f.Kind == kind);

        /// <summary>Drops the cache (tests; a restored store).</summary>
        internal static void ResetCache()
        {
            Cache.Clear();
            DropTables.ResetCache();
        }

        /// <summary>Imports (on first use) and caches every kind. Called by the store's load, before any loader reads a file.</summary>
        public static void EnsureLoaded()
        {
            foreach (var f in Formats)
                if (f is DropFormat) DropTables.EnsureLoaded();
                else Files(f);
        }

        // ------------------------------------------------------------------ reads (cache)

        /// <summary>The files of a kind (name → rows), importing and loading them on first use.</summary>
        private static ConcurrentDictionary<string, FileRows> Files(DesignTextFormat f)
        {
            if (Cache.TryGetValue(f.Kind, out var files)) return files;
            var loaded = DesignStore.Run(conn => LoadKind(conn, f));
            return Cache.GetOrAdd(f.Kind, loaded);
        }

        /// <summary>Names of the stored files of a kind, relative to its folder ('/'-separated).</summary>
        public static IEnumerable<string> List(DesignTextFormat f) => f is DropFormat ? DropTables.List() : Files(f).Values.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        public static bool Exists(DesignTextFormat f, string name) => f is DropFormat ? DropTables.Exists(name) : Files(f).ContainsKey(name);

        /// <summary>The file's lines with the current names, or null when it doesn't exist.</summary>
        public static List<string> Read(DesignTextFormat f, string name, Func<string, long, string> nameOf = null)
        {
            if (f is DropFormat) return DropTables.Read(name, nameOf);
            if (!Files(f).TryGetValue(name, out var file)) return null;
            return file.Rows.Select(cells => f.Render(cells, nameOf)).ToList();
        }

        public static DateTime? UpdatedUtc(DesignTextFormat f, string name) => f is DropFormat ? DropTables.UpdatedUtc(name) : Files(f).TryGetValue(name, out var file) ? file.UpdatedUtc : null;

        /// <summary>The file as bytes in its own layout, or null when it doesn't exist.</summary>
        public static byte[] ReadBytes(DesignTextFormat f, string name)
        {
            if (f is DropFormat) return DropTables.ReadBytes(name);
            if (!Files(f).TryGetValue(name, out var file)) return null;
            return file.Layout.Encode(file.Rows.Select(cells => f.Render(cells, null)).ToList());
        }

        public static TextLayout? LayoutOf(DesignTextFormat f, string name) => f is DropFormat ? DropTables.LayoutOf(name) : Files(f).TryGetValue(name, out var file) ? file.Layout : null;

        // ------------------------------------------------------------------ writes (store, then cache)

        /// <summary>
        /// Parses and stores a file (replacing it). Returns the number of lines whose names didn't resolve (kept as text). On the game
        /// thread (a loader creating a missing default file at runtime) the cache is updated at once and the store write is posted.
        /// </summary>
        public static int Write(DesignTextFormat f, string name, IList<string> lines, TextLayout? layout = null)
        {
            if (f is DropFormat) return DropTables.Write(name, lines, layout);
            var files = Files(f);
            if (files.TryGetValue(name, out var existing)) name = existing.Name; // the stored casing: SQLite keys are case-sensitive
            var lay = layout ?? TextLayout.WriteAllLines;
            if (DesignStore.OnGameThread)
            {
                var quick = Parse(f, lines, ViewNames());
                files[name] = new FileRows { Name = name, Rows = quick.Rows, UpdatedUtc = DateTime.UtcNow, Layout = lay };
                DesignStore.Post(conn =>
                {
                    using var tx = conn.BeginTransaction();
                    WriteFile(conn, tx, f, name, quick.Rows, lay);
                    tx.Commit();
                }, "design file save (" + f.Kind + "/" + name + ")");
                return quick.Unresolved;
            }

            var parseNames = _parseNames;
            var (rows, unresolved) = DesignStore.Run(conn =>
            {
                var parsed = Parse(f, lines, parseNames ?? DesignNames.FromStore(conn));
                using var tx = conn.BeginTransaction();
                WriteFile(conn, tx, f, name, parsed.Rows, lay);
                tx.Commit();
                return parsed;
            });
            files[name] = new FileRows { Name = name, Rows = rows, UpdatedUtc = DateTime.UtcNow, Layout = lay };
            return unresolved;
        }

        /// <summary>Renames a stored file in one transaction, case-only renames included (editors only, never the game thread).</summary>
        public static void Rename(DesignTextFormat f, string from, string to)
        {
            if (f is DropFormat)
            {
                DropTables.Rename(from, to);
                return;
            }

            var files = Files(f);
            if (!files.TryGetValue(from, out var file)) throw new FileNotFoundException("[Store] " + from + " is not in the " + f.Kind + " store");
            DesignStore.Run(conn =>
            {
                using var tx = conn.BeginTransaction();
                foreach (var sql in new[] { "UPDATE design_files SET name = $to, updated_utc = $t WHERE kind = $k AND name = $from",
                                            "UPDATE " + f.Table + " SET name = $to WHERE name = $from" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$to", to);
                    cmd.Parameters.AddWithValue("$from", file.Name);
                    cmd.Parameters.AddWithValue("$k", f.Kind);
                    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return 0;
            });
            files.TryRemove(from, out _);
            files[to] = new FileRows { Name = to, Rows = file.Rows, UpdatedUtc = DateTime.UtcNow, Layout = file.Layout };
        }

        public static void Delete(DesignTextFormat f, string name)
        {
            if (f is DropFormat)
            {
                DropTables.Delete(name);
                return;
            }

            var files = Files(f);
            if (files.TryGetValue(name, out var existing)) name = existing.Name; // the stored casing
            if (DesignStore.OnGameThread)
            {
                files.TryRemove(name, out _);
                DesignStore.Post(conn =>
                {
                    using var tx = conn.BeginTransaction();
                    DeleteFile(conn, tx, f, name);
                    tx.Commit();
                }, "design file delete (" + f.Kind + "/" + name + ")");
                return;
            }

            DesignStore.Run(conn =>
            {
                using var tx = conn.BeginTransaction();
                DeleteFile(conn, tx, f, name);
                tx.Commit();
                return 0;
            });
            files.TryRemove(name, out _);
        }

        // ------------------------------------------------------------------ store (writer thread)

        private static ConcurrentDictionary<string, FileRows> LoadKind(SqliteConnection conn, DesignTextFormat f)
        {
            if (!IsImported(conn, f)) Import(conn, f);

            var files = new ConcurrentDictionary<string, FileRows>(StringComparer.OrdinalIgnoreCase);
            var rows = new Dictionary<string, List<object[]>>(StringComparer.OrdinalIgnoreCase);
            var meta = new Dictionary<string, (DateTime Updated, TextLayout Layout)>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, updated_utc, newline, final_newline, bom FROM design_files WHERE kind = $k AND name <> ''";
                cmd.Parameters.AddWithValue("$k", f.Kind);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows[r.GetString(0)] = new List<object[]>();
                    meta[r.GetString(0)] = (DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        new TextLayout(r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(4) != 0));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, " + string.Join(", ", f.Columns.Select(c => SqliteWorldCodec.Quote(c.Name))) + " FROM " + f.Table + " ORDER BY name, position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var cells = new object[f.Columns.Length];
                    for (var i = 0; i < cells.Length; i++) cells[i] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
                    if (rows.TryGetValue(r.GetString(0), out var list)) list.Add(cells);
                }
            }

            foreach (var pair in rows)
                files[pair.Key] = new FileRows { Name = pair.Key, Rows = pair.Value.ToArray(), UpdatedUtc = meta[pair.Key].Updated, Layout = meta[pair.Key].Layout };

            ReportDiskChanges(f, files);
            return files;
        }

        /// <summary>Logs (once) how many of the kind's files were changed on disk after the store last wrote them: the store is the record.</summary>
        private static void ReportDiskChanges(DesignTextFormat f, ConcurrentDictionary<string, FileRows> files)
        {
            try
            {
                var root = Path.GetFullPath(f.Root);
                if (!Directory.Exists(root)) return;
                var changed = 0;
                foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
                {
                    var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!files.TryGetValue(name, out var file) || File.GetLastWriteTimeUtc(path) > file.UpdatedUtc) changed++;
                }

                if (changed > 0)
                    MessageQueue.Instance.Enqueue("[Store] " + changed.ToString(CultureInfo.InvariantCulture) + " " + f.Kind + " file(s) in " + root +
                                                  " are newer than the store or not in it; the server uses the store. Edit them in the web admin, or re-import (Knowledgebase/Runbook.md §10).");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsImported(SqliteConnection conn, DesignTextFormat f)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM design_files WHERE kind = $k AND name = ''";
            cmd.Parameters.AddWithValue("$k", f.Kind);
            return cmd.ExecuteScalar() != null;
        }

        /// <summary>Imports every file under the kind's folder (recursively, *.txt) in one transaction.</summary>
        private static void Import(SqliteConnection conn, DesignTextFormat f)
        {
            var names = DesignNames.FromStore(conn);
            var root = Path.GetFullPath(f.Root);
            var files = 0;
            var unresolved = 0;
            using var tx = conn.BeginTransaction();
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                    var (lines, layout) = TextLayout.Decode(File.ReadAllBytes(path));
                    var parsed = Parse(f, lines, names);
                    WriteFile(conn, tx, f, name, parsed.Rows, layout);
                    unresolved += parsed.Unresolved;
                    files++;
                }
            }

            InsertFileRow(conn, tx, f, ImportMarker, TextLayout.WriteAllLines);
            tx.Commit();
            MessageQueue.Instance.Enqueue("[Store] Imported " + files.ToString(CultureInfo.InvariantCulture) + " " + f.Kind + " file(s) from " + root +
                                          (unresolved > 0 ? "; " + unresolved.ToString(CultureInfo.InvariantCulture) + " line(s) name an item or monster that doesn't exist (kept as text, skipped by the game as before)" : string.Empty));
        }

        /// <summary>
        /// Types every line the format can; a typed line whose rendering (with the names as written) isn't the line itself is kept verbatim
        /// instead, so a file always renders back exactly as it was, until something it names is renamed.
        /// </summary>
        internal static (object[][] Rows, int Unresolved) Parse(DesignTextFormat f, IList<string> lines, DesignNames names)
        {
            var state = new DesignTextFormat.ParseState();
            var rows = new object[lines.Count][];
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? string.Empty;
                var cells = f.Parse(line, names, state);
                if (cells[cells.Length - 1] == null && f.Render(cells, (_, _) => null) != line)
                {
                    cells = new object[f.Columns.Length];
                    cells[cells.Length - 1] = line;
                }

                rows[i] = cells;
            }

            return (rows, state.Unresolved);
        }

        private static void WriteFile(SqliteConnection conn, SqliteTransaction tx, DesignTextFormat f, string name, object[][] rows, TextLayout layout)
        {
            DeleteFile(conn, tx, f, name);
            InsertFileRow(conn, tx, f, name, layout);
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO " + f.Table + " (name, position, " + string.Join(", ", f.Columns.Select(c => SqliteWorldCodec.Quote(c.Name))) +
                              ") VALUES ($n, $p, " + string.Join(", ", f.Columns.Select((_, i) => "$c" + i.ToString(CultureInfo.InvariantCulture))) + ")";
            ins.Parameters.AddWithValue("$n", name);
            var pos = ins.Parameters.Add("$p", SqliteType.Integer);
            var cols = f.Columns.Select((_, i) => ins.Parameters.Add("$c" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Text)).ToArray();
            for (var r = 0; r < rows.Length; r++)
            {
                pos.Value = (long)r;
                for (var c = 0; c < cols.Length; c++) cols[c].Value = rows[r][c] ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
        }

        private static void DeleteFile(SqliteConnection conn, SqliteTransaction tx, DesignTextFormat f, string name)
        {
            foreach (var sql in new[] { "DELETE FROM " + f.Table + " WHERE name = $n", "DELETE FROM design_files WHERE kind = $k AND name = $n" })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$k", f.Kind);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertFileRow(SqliteConnection conn, SqliteTransaction tx, DesignTextFormat f, string name, TextLayout layout)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO design_files (kind, name, imported_utc, updated_utc, newline, final_newline, bom) VALUES ($k, $n, $t, $t, $nl, $fn, $b) " +
                              "ON CONFLICT(kind, name) DO UPDATE SET updated_utc = excluded.updated_utc, newline = excluded.newline, " +
                              "final_newline = excluded.final_newline, bom = excluded.bom";
            cmd.Parameters.AddWithValue("$k", f.Kind);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$nl", layout.Newline ?? "\r\n");
            cmd.Parameters.AddWithValue("$fn", layout.FinalNewline ? 1L : 0L);
            cmd.Parameters.AddWithValue("$b", layout.Bom ? 1L : 0L);
            cmd.ExecuteNonQuery();
        }
    }
}
