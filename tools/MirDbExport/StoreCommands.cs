using System.Globalization;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace MirDbExport
{
    /// <summary>
    /// Offline commands for the server's SQLite system-data store (roadmap P7 phase 7.2), using the server's own codec:
    /// <c>import</c> (Server.MirDB → SystemData.db), <c>export-binary</c> (SystemData.db → Server.MirDB, the rollback) and <c>roundtrip</c>
    /// (SQLite → binary must give the bytes of the binary file the store was imported from). Sources are only read.
    /// </summary>
    internal static class StoreCommands
    {
        private static readonly object Gate = new object();

        public static int Import(string sourceFolder, string outPath)
        {
            outPath = Path.GetFullPath(outPath);
            if (File.Exists(outPath)) throw new ToolException(outPath + " already exists (refusing to overwrite a store).");

            WorldLoader.WithLoadedEdit(sourceFolder, source =>
            {
                var rows = SqliteWorldCodec.Encode(Envir.Edit, (long)Envir.Edit.RespawnTick.CurrentTickcounter);
                SqliteWorldStore.Create(outPath, rows, new Dictionary<string, object>
                {
                    ["source"] = "import",
                    ["source_file"] = source.Path,
                    ["source_sha256"] = source.Sha256.ToUpperInvariant(),
                    ["source_envir_version"] = (long)Envir.LoadVersion,
                    ["tool"] = "MirDbExport",
                });
                Console.WriteLine("Imported " + source.Path + " (version " + Envir.LoadVersion.ToString(CultureInfo.InvariantCulture) + ") into " + outPath + ": " +
                                  rows.Tables.Values.Sum(t => t.Rows.Count).ToString(CultureInfo.InvariantCulture) + " rows.");
                return 0;
            });
            return 0;
        }

        public static int ExportBinary(string dbPath, string outFolder, bool force)
        {
            var target = Path.Combine(Path.GetFullPath(outFolder), WorldLoader.DbFileName);
            if (File.Exists(target) && !force) throw new ToolException(target + " already exists (pass --force to overwrite it).");

            var bytes = ToBinary(dbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);
            Console.WriteLine("Wrote " + target + " (" + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes, version " + Envir.Version.ToString(CultureInfo.InvariantCulture) + ").");
            return 0;
        }

        /// <summary>
        /// Loads <paramref name="dbPath"/> (default: &lt;folder&gt;\SystemData.db), writes it as binary and compares the bytes with
        /// whichever of &lt;folder&gt;\Server.MirDB and &lt;folder&gt;\Back Up\Database\*.bak has meta.source_sha256.
        /// </summary>
        public static int Roundtrip(string folder, string dbPath)
        {
            folder = Path.GetFullPath(folder);
            dbPath = Path.GetFullPath(dbPath ?? Path.Combine(folder, "SystemData.db"));
            var sha = ReadMeta(dbPath, "source_sha256") as string
                      ?? throw new ToolException(dbPath + " has no meta.source_sha256 (only imports record their source).");

            var candidates = new List<string> { Path.Combine(folder, WorldLoader.DbFileName) };
            var backups = Path.Combine(folder, "Back Up", "Database");
            if (Directory.Exists(backups)) candidates.AddRange(Directory.GetFiles(backups, "*.bak"));
            var source = candidates.FirstOrDefault(p => File.Exists(p) && string.Equals(WorldLoader.Sha256Of(p), sha, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ToolException("No binary file with SHA-256 " + sha + " in " + folder + " or its Back Up\\Database (it may have been pruned; save again).");

            var expected = File.ReadAllBytes(source);
            var actual = ToBinary(dbPath);
            var diff = FirstDifference(expected, actual);
            if (diff < 0)
            {
                Console.WriteLine("ROUNDTRIP OK: " + dbPath + " gives the " + expected.Length.ToString(CultureInfo.InvariantCulture) + " bytes of " + source);
                return 0;
            }

            Console.Error.WriteLine("ROUNDTRIP FAILED: first difference at byte " + diff.ToString(CultureInfo.InvariantCulture) + " of " + source +
                                    " (expected " + expected.Length.ToString(CultureInfo.InvariantCulture) + " bytes, got " + actual.Length.ToString(CultureInfo.InvariantCulture) +
                                    "). Run 'verify' on that file to see the table, row and column.");
            return 1;
        }

        /// <summary>Decodes the store into Envir.Edit in a temp folder and returns the Server.MirDB bytes the binary store writes for it.</summary>
        internal static byte[] ToBinary(string dbPath)
        {
            dbPath = Path.GetFullPath(dbPath);
            lock (Gate)
            {
                WorldRows rows;
                // Reads a copy: Migrate may add columns, and the source is never written.
                var copy = Path.Combine(Path.GetTempPath(), "mirdbexport", Guid.NewGuid().ToString("N") + ".db");
                Directory.CreateDirectory(Path.GetDirectoryName(copy));
                File.Copy(dbPath, copy);
                try
                {
                    using var conn = SqliteWorldFile.Open(copy, create: false);
                    SqliteWorldFile.CheckIntegrity(conn);
                    SqliteWorldFile.CheckSchema(conn);
                    SqliteWorldFile.Migrate(conn, Console.WriteLine);
                    rows = SqliteWorldFile.ReadAll(conn);
                }
                finally
                {
                    File.Delete(copy);
                }

                var work = Path.Combine(Path.GetTempPath(), "mirdbexport", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                var old = Environment.CurrentDirectory;
                try
                {
                    Directory.SetCurrentDirectory(work);
                    lock (Envir.LoadLock)
                    {
                        Envir.LoadVersion = Envir.Version;
                        Envir.LoadCustomVersion = Envir.CustomVersion;
                        Envir.Edit.MagicInfoList.Clear(); // Decode keeps spells already present, as the binary load does
                        SqliteWorldCodec.Decode(rows, Envir.Edit);
                    }

                    new BinaryWorldStore().SaveDesign(Envir.Edit, null);
                    return File.ReadAllBytes(Path.Combine(work, WorldLoader.DbFileName));
                }
                finally
                {
                    Directory.SetCurrentDirectory(old);
                    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static object ReadMeta(string dbPath, string name)
        {
            using var conn = SqliteWorldFile.Open(dbPath, create: false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE name = $n";
            cmd.Parameters.AddWithValue("$n", name);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }

        private static long FirstDifference(byte[] a, byte[] b)
        {
            var n = Math.Min(a.Length, b.Length);
            for (var i = 0; i < n; i++) if (a[i] != b[i]) return i;
            return a.Length == b.Length ? -1 : n;
        }
    }
}
