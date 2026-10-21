using System.Globalization;
using Microsoft.Data.Sqlite;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace MirDbExport
{
    /// <summary>
    /// <c>check-drops</c> (Drops-Tables-Plan phase 1): proves the drop tables load every drop list exactly as the text loaders loaded the
    /// text. Works on a copy of the folder's SystemData.db: the reference is that copy's <c>drop_lines</c> as the game read them (or the
    /// folder's drop files when the store never imported them, or the store's own rendered rows once it is converted); then the copy is
    /// opened (which converts it) and every list is loaded both ways and compared structurally, with the lines each way logs.
    /// </summary>
    internal static class DropCommands
    {
        private const int MaxListed = 50;

        public static int CheckDrops(string folder)
        {
            folder = Path.GetFullPath(folder);
            var src = Path.Combine(folder, "SystemData.db");
            if (!File.Exists(src)) throw new ToolException("No SystemData.db in " + folder + " (run the server on it once, or MirDbExport import).");
            var work = Path.Combine(Path.GetTempPath(), "mirdbexport", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var db = Path.Combine(work, "SystemData.db");
            try
            {
                // A consistent copy even if a server has the file open (WAL); the source is only read.
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = src, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    source.Open();
                    using var cmd = source.CreateCommand();
                    cmd.CommandText = "VACUUM INTO $p";
                    cmd.Parameters.AddWithValue("$p", db);
                    cmd.ExecuteNonQuery();
                }

                var (reference, origin) = ReadReference(db);
                return DesignCommands.WithStore(db, folder, () =>
                {
                    DesignTexts.ResetCache();
                    var rows = DesignStore.Run(conn => SqliteWorldFile.ReadAll(conn)); // opening the writer converts drop_lines
                    DesignTexts.RefreshNames(rows);
                    DesignTexts.EnsureLoaded();
                    lock (Envir.LoadLock)
                    {
                        Envir.LoadVersion = Envir.Version;
                        Envir.LoadCustomVersion = Envir.CustomVersion;
                        var e = Envir.Main;
                        e.MapInfoList.Clear(); e.ItemInfoList.Clear(); e.MonsterInfoList.Clear(); e.NPCInfoList.Clear(); e.QuestInfoList.Clear();
                        e.MagicInfoList.Clear(); e.GameShopList.Clear(); e.ConquestInfoList.Clear();
                        SqliteWorldCodec.Decode(rows, e);
                    }

                    if (reference == null)
                    {
                        origin = "the store's own rows (already converted)";
                        reference = DropTables.List().ToDictionary(n => n, n => DropTables.Read(n), StringComparer.OrdinalIgnoreCase);
                    }

                    return Compare(reference, origin);
                });
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The drop files as the text loaders read them: drop_lines rendered with the store's item names, or the folder's files.</summary>
        private static (Dictionary<string, List<string>> Files, string Origin) ReadReference(string db)
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            bool Has(string sql)
            {
                using var c = conn.CreateCommand();
                c.CommandText = sql;
                return c.ExecuteScalar() != null;
            }

            if (Has("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta'") && Has("SELECT 1 FROM meta WHERE name = '" + DropTables.ConvertedMarker + "'"))
                return (null, null);

            var imported = Has("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'design_files'") && Has("SELECT 1 FROM design_files WHERE kind = 'drops' AND name = ''");
            var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!imported || !Has("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'drop_lines'"))
                return (null, "files"); // read in Compare, with the folder as the working directory

            var items = new Dictionary<long, string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT key, \"Name\" FROM items ORDER BY position";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(1)) items.TryAdd(r.GetInt64(0), r.GetString(1).Replace(" ", string.Empty));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM design_files WHERE kind = 'drops' AND name <> ''";
                using var r = cmd.ExecuteReader();
                while (r.Read()) files[r.GetString(0)] = new List<string>();
            }

            var format = DesignTexts.FormatOf("drops");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, " + string.Join(", ", format.Columns.Select(c => SqliteWorldCodec.Quote(c.Name))) + " FROM drop_lines ORDER BY name, position";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var cells = new object[format.Columns.Length];
                    for (var i = 0; i < cells.Length; i++) cells[i] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
                    if (files.TryGetValue(r.GetString(0), out var lines))
                        lines.Add(format.Render(cells, (kind, index) => kind == "I" && items.TryGetValue(index, out var n) ? n : null));
                }
            }

            return (files, "drop_lines (as the game read them before the conversion)");
        }

        private static int Compare(Dictionary<string, List<string>> reference, string origin)
        {
            if (origin == "files")
            {
                // The store never imported the drops: the reference is the folder's files (the copy imported them on open).
                var root = Path.GetFullPath(DropTables.Format.Root);
                reference = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(root))
                    foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
                        reference[Path.GetRelativePath(root, path).Replace('\\', '/')] = TextLayout.Decode(File.ReadAllBytes(path)).Lines;
                origin = "the drop files in " + root;
            }

            var differences = new List<string>();
            int lists = 0, textOnly = 0, oldThrows = 0, drops = 0, badLines = 0;
            foreach (var name in reference.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                lists++;
                if (!DropTables.Exists(name))
                {
                    differences.Add(name + ": no such list in the drop tables");
                    continue;
                }

                if (!reference[name].SequenceEqual(DropTables.Read(name))) textOnly++;
                var (kind, _, fishing) = DropTables.OwnerOf(name, Enumerable.Empty<(long, string, string)>());
                var oldBad = new List<string>();
                var newBad = new List<string>();
                try
                {
                    if (kind == DropList.Dragon)
                    {
                        var oldLevels = Levels();
                        DragonInfo.LoadFromLines(oldLevels, reference[name].ToArray(), oldBad.Add);
                        var newLevels = Levels();
                        DropTables.LoadDragon(newLevels, name, newBad.Add);
                        for (var i = 0; i < oldLevels.Length; i++)
                        {
                            drops += oldLevels[i].Count;
                            CompareDragon(name + " level " + (i + 1).ToString(CultureInfo.InvariantCulture), oldLevels[i], newLevels[i], differences);
                        }
                    }
                    else
                    {
                        var type = (byte)(fishing ?? 0);
                        var oldList = new List<DropInfo>();
                        DropInfo.LoadFromLines(oldList, name, reference[name].ToList(), type, sub => ReadInclude(reference, sub), (_, l) => oldBad.Add(l));
                        var newList = new List<DropInfo>();
                        DropTables.LoadInto(newList, name, name, type, (_, l) => newBad.Add(l));
                        drops += oldList.Count;
                        CompareList(name, oldList, newList, differences);
                    }
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    oldThrows++; // the text loader throws on this list (the game would have stopped); the tables load and log it
                    continue;
                }

                badLines += oldBad.Count;
                if (!oldBad.SequenceEqual(newBad))
                    differences.Add(name + ": logged lines differ (text loader " + oldBad.Count.ToString(CultureInfo.InvariantCulture) + ", tables " +
                                    newBad.Count.ToString(CultureInfo.InvariantCulture) + ")");
            }

            Console.WriteLine("  reference: " + origin);
            Console.WriteLine("  " + lists.ToString(CultureInfo.InvariantCulture) + " lists, " + drops.ToString(CultureInfo.InvariantCulture) + " top-level drops, " +
                              badLines.ToString(CultureInfo.InvariantCulture) + " logged lines");
            if (oldThrows > 0) Console.WriteLine("  " + oldThrows.ToString(CultureInfo.InvariantCulture) + " list(s) the text loader throws on (not compared)");
            if (textOnly > 0) differences.Add(textOnly.ToString(CultureInfo.InvariantCulture) + " list(s) don't render back to the reference text");
            if (differences.Count > 0)
            {
                Console.Error.WriteLine("CHECK FAILED: " + differences.Count.ToString(CultureInfo.InvariantCulture) + " difference(s).");
                foreach (var d in differences.Take(MaxListed)) Console.Error.WriteLine("  " + d);
                if (differences.Count > MaxListed) Console.Error.WriteLine("  ... and " + (differences.Count - MaxListed).ToString(CultureInfo.InvariantCulture) + " more");
                return 1;
            }

            Console.WriteLine("CHECK OK: 0 differences (every list loads the same from the tables as from its text, and renders back to it).");
            return 0;
        }

        private static string[] ReadInclude(Dictionary<string, List<string>> reference, string sub)
        {
            var name = DropTables.IncludeName(sub);
            if (name != null && reference.TryGetValue(name, out var lines)) return lines.ToArray();
            throw new FileNotFoundException("#INSERT [" + sub + "]"); // as DesignFiles.ReadAllLines for a missing file
        }

        private static List<DragonInfo.DropInfo>[] Levels()
        {
            var levels = new List<DragonInfo.DropInfo>[Globals.MaxDragonLevel];
            for (var i = 0; i < levels.Length; i++) levels[i] = new List<DragonInfo.DropInfo>();
            return levels;
        }

        private static void CompareList(string where, List<DropInfo> a, List<DropInfo> b, List<string> differences)
        {
            if (a.Count != b.Count)
            {
                differences.Add(where + ": " + a.Count.ToString(CultureInfo.InvariantCulture) + " drop(s) vs " + b.Count.ToString(CultureInfo.InvariantCulture));
                return;
            }

            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                var at = where + " #" + i.ToString(CultureInfo.InvariantCulture);
                if (x.Chance != y.Chance || x.Item?.Index != y.Item?.Index || x.Gold != y.Gold || x.Type != y.Type || x.QuestRequired != y.QuestRequired ||
                    (x.GroupedDrop == null) != (y.GroupedDrop == null))
                {
                    differences.Add(at + ": " + Describe(x) + " vs " + Describe(y));
                    continue;
                }

                if (x.GroupedDrop == null) continue;
                if (x.GroupedDrop.Random != y.GroupedDrop.Random || x.GroupedDrop.First != y.GroupedDrop.First)
                    differences.Add(at + ": group mode differs");
                CompareList(at + " group", x.GroupedDrop, y.GroupedDrop, differences);
            }
        }

        private static void CompareDragon(string where, List<DragonInfo.DropInfo> a, List<DragonInfo.DropInfo> b, List<string> differences)
        {
            if (a.Count != b.Count)
            {
                differences.Add(where + ": " + a.Count.ToString(CultureInfo.InvariantCulture) + " drop(s) vs " + b.Count.ToString(CultureInfo.InvariantCulture));
                return;
            }

            for (var i = 0; i < a.Count; i++)
                if (a[i].Chance != b[i].Chance || a[i].Item?.Index != b[i].Item?.Index || a[i].Gold != b[i].Gold || a[i].level != b[i].level)
                    differences.Add(where + " #" + i.ToString(CultureInfo.InvariantCulture) + " differs");
        }

        private static string Describe(DropInfo d) =>
            "1/" + d.Chance.ToString(CultureInfo.InvariantCulture) + " " + (d.GroupedDrop != null ? "group" : d.Item != null ? "item " + d.Item.Index.ToString(CultureInfo.InvariantCulture) : "gold " + d.Gold.ToString(CultureInfo.InvariantCulture)) +
            " type " + d.Type.ToString(CultureInfo.InvariantCulture) + (d.QuestRequired ? " Q" : string.Empty);
    }
}
