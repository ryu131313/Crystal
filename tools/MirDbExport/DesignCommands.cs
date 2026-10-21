using System.Globalization;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace MirDbExport
{
    /// <summary>
    /// Offline commands for the design files in SystemData.db (roadmap P7 phase 7.4):
    /// <c>check-files</c> imports a server folder's design files into a scratch store and proves every one renders back byte for byte;
    /// <c>export-files</c> writes a store's ini and text design files back out (the rollback). Sources are only read.
    /// </summary>
    internal static class DesignCommands
    {
        /// <summary>The ini design files under Configs (Setup.ini's design sections are handled separately).</summary>
        private static readonly string[] IniPatterns = { "*.ini" };

        public static int CheckFiles(string folder)
        {
            folder = Path.GetFullPath(folder);
            var work = Path.Combine(Path.GetTempPath(), "mirdbexport", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var db = Path.Combine(work, "SystemData.db");
            try
            {
                // A scratch store from the folder's Server.MirDB (items and monsters resolve the names).
                StoreCommands.Import(folder, db);
                return WithStore(db, folder, () =>
                {
                    var failures = 0;
                    var files = 0;
                    DesignTexts.ResetCache();
                    var rows = DesignStore.Run(conn => SqliteWorldFile.ReadAll(conn));
                    DesignTexts.RefreshNames(rows);
                    DesignTexts.EnsureLoaded();

                    foreach (var f in DesignTexts.Formats)
                    {
                        var root = Path.GetFullPath(f.Root);
                        var typed = DesignStore.Run(conn => Count(conn, "SELECT count(*) FROM " + f.Table + " WHERE text IS NULL"));
                        var text = DesignStore.Run(conn => Count(conn, "SELECT count(*) FROM " + f.Table + " WHERE text IS NOT NULL"));
                        var n = 0;
                        if (Directory.Exists(root))
                        {
                            foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
                            {
                                n++;
                                var expected = File.ReadAllBytes(path);
                                var actual = DesignFiles.ReadAllBytes(path); // the file's own line endings, final newline and BOM
                                if (expected.AsSpan().SequenceEqual(actual)) continue;
                                failures++;
                                var i = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(x => expected[x] != actual[x], Math.Min(expected.Length, actual.Length));
                                Console.Error.WriteLine("  MISMATCH " + f.Kind + " " + Path.GetRelativePath(root, path) + " at byte " + i.ToString(CultureInfo.InvariantCulture));
                            }
                        }

                        files += n;
                        Console.WriteLine("  " + f.Kind.PadRight(8) + n.ToString(CultureInfo.InvariantCulture).PadLeft(6) + " files " +
                                          typed.ToString(CultureInfo.InvariantCulture).PadLeft(8) + " typed lines " +
                                          text.ToString(CultureInfo.InvariantCulture).PadLeft(8) + " kept as text");
                    }

                    var configs = Path.Combine(folder, "Configs");
                    var inis = 0;
                    if (Directory.Exists(configs))
                    {
                        foreach (var path in Directory.EnumerateFiles(configs, "*.ini").Where(p => !string.Equals(Path.GetFileName(p), "Setup.ini", StringComparison.OrdinalIgnoreCase)))
                        {
                            inis++;
                            var reader = DesignIni.Open(path); // imports it
                            var stored = DesignStore.Run(conn => DesignIni.Read(conn, DesignStore.Key(path))).Lines;
                            if (File.ReadAllLines(path).SequenceEqual(stored)) continue;
                            failures++;
                            Console.Error.WriteLine("  MISMATCH ini " + Path.GetFileName(path));
                        }
                    }

                    Console.WriteLine("  ini     " + inis.ToString(CultureInfo.InvariantCulture).PadLeft(6) + " files");
                    if (failures > 0)
                    {
                        Console.Error.WriteLine("CHECK FAILED: " + failures.ToString(CultureInfo.InvariantCulture) + " file(s) don't render back identically.");
                        return 1;
                    }

                    Console.WriteLine("CHECK OK: " + (files + inis).ToString(CultureInfo.InvariantCulture) + " design files render back byte for byte (text files compared as bytes, ini files as lines).");
                    return 0;
                });
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Imports a server folder's design files (ini + text) into an existing store (what the server does on first use).</summary>
        public static int ImportFiles(string dbPath, string folder)
        {
            dbPath = Path.GetFullPath(dbPath);
            folder = Path.GetFullPath(folder);
            if (!File.Exists(dbPath)) throw new ToolException(dbPath + " does not exist (run import first).");
            return WithStore(dbPath, folder, () =>
            {
                DesignTexts.ResetCache();
                DesignTexts.RefreshNames(DesignStore.Run(conn => SqliteWorldFile.ReadAll(conn)));
                DesignTexts.EnsureLoaded();
                var configs = Path.Combine(folder, "Configs");
                var inis = 0;
                if (Directory.Exists(configs))
                {
                    foreach (var path in Directory.EnumerateFiles(configs, "*.ini"))
                    {
                        if (string.Equals(Path.GetFileName(path), "Setup.ini", StringComparison.OrdinalIgnoreCase))
                            new SetupIniReader(path).LoadDesignSections();
                        else DesignIni.Open(path);
                        inis++;
                    }
                }

                Console.WriteLine("Imported the design files of " + folder + " (" + inis.ToString(CultureInfo.InvariantCulture) + " ini file(s)) into " + dbPath);
                return 0;
            });
        }

        public static int ExportFiles(string dbPath, string outFolder, bool force)
        {
            dbPath = Path.GetFullPath(dbPath);
            outFolder = Path.GetFullPath(outFolder);
            if (Directory.Exists(outFolder) && Directory.EnumerateFileSystemEntries(outFolder).Any() && !force)
                throw new ToolException(outFolder + " is not empty (pass --force to write into it).");

            // Work on a copy: opening the store may add tables (Migrate), and the source is never written.
            var copy = Path.Combine(Path.GetTempPath(), "mirdbexport", Guid.NewGuid().ToString("N") + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.Copy(dbPath, copy);
            try
            {
                Directory.CreateDirectory(outFolder);
                return WithStore(copy, outFolder, () =>
                {
                    var written = 0;
                    DesignTexts.ResetCache();
                    var rows = DesignStore.Run(conn => SqliteWorldFile.ReadAll(conn));
                    DesignTexts.RefreshNames(rows);
                    foreach (var f in DesignTexts.Formats)
                    {
                        // Only kinds the store has imported: an unimported kind still lives in the original folder's files.
                        if (!DesignStore.Run(conn => Count(conn, "SELECT count(*) FROM design_files WHERE kind = '" + f.Kind + "' AND name = ''") > 0)) continue;
                        foreach (var name in DesignTexts.List(f))
                        {
                            var target = Path.Combine(Path.GetFullPath(f.Root), name.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllBytes(target, DesignTexts.ReadBytes(f, name)); // the file's own line endings, final newline and BOM
                            written++;
                        }
                    }

                    var inis = DesignStore.Run(conn =>
                    {
                        var list = new List<(string Key, List<string> Lines)>();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT file FROM ini_files WHERE present = 1";
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) list.Add((r.GetString(0), null));
                        return list.Select(x => (x.Key, DesignIni.ReadLines(conn, x.Key))).ToList();
                    });

                    foreach (var (key, lines) in inis)
                    {
                        if (key.EndsWith("#design", StringComparison.Ordinal))
                        {
                            // Setup.ini's [Game] / [Items]: merged into the folder's Setup.ini if there is one, else written alone.
                            var setup = Path.GetFullPath(key.Substring(0, key.Length - "#design".Length));
                            Directory.CreateDirectory(Path.GetDirectoryName(setup));
                            var rest = File.Exists(setup) ? DesignIni.Split(File.ReadAllLines(setup), DesignIni.SetupDesignSections).Outside : new List<string>();
                            if (rest.Count > 0 && rest[rest.Count - 1].Length > 0) rest.Add(string.Empty);
                            File.WriteAllLines(setup, rest.Concat(lines));
                        }
                        else
                        {
                            var target = Path.GetFullPath(key);
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllLines(target, lines);
                        }

                        written++;
                    }

                    Console.WriteLine("Wrote " + written.ToString(CultureInfo.InvariantCulture) + " design file(s) under " + outFolder);
                    return 0;
                });
            }
            finally
            {
                File.Delete(copy);
            }
        }

        /// <summary>Points the design layer at <paramref name="db"/> with the working directory <paramref name="folder"/> (paths resolve there).</summary>
        private static int WithStore(string db, string folder, Func<int> body)
        {
            var old = Environment.CurrentDirectory;
            var writer = new SqliteWriter(db, "MirDbExport design writer");
            try
            {
                Directory.SetCurrentDirectory(folder);
                DesignStore.WriterOverride = writer;
                DesignStore.Disabled = false;
                return body();
            }
            finally
            {
                DesignStore.Disabled = true;
                DesignStore.WriterOverride = null;
                writer.Shutdown();
                DesignTexts.ResetCache();
                Directory.SetCurrentDirectory(old);
            }
        }

        private static long Count(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
