using System.Globalization;
using System.Security.Cryptography;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// System data in SQLite, <c>[Database] Backend=Sqlite</c> (roadmap P7 phase 7.2; switched on at the flip, 7.3).
    /// <list type="bullet">
    /// <item>One <see cref="SqliteWriter"/> thread owns <see cref="DatabasePath"/>. Loads and saves are encoded / decoded on the calling
    /// thread (startup, the WinForms UI, a web worker under EditLock) and only rows cross to the writer.</item>
    /// <item>Envir.Edit is the only writer of design data. Main's save writes only runtime_state (the respawn tick counter), posted
    /// without waiting, so the game thread never blocks and Main can't overwrite newer design data.</item>
    /// <item>Before each design save the previous data is copied with VACUUM INTO to Back Up\Database, named as the binary backups
    /// ("Database yyyy-MM-dd HH-mm-ss.bak", or the caller's name), so BackupRetention prunes them unchanged.</item>
    /// <item>A missing database is imported from Server.MirDB (left untouched as the rollback), unless there is evidence of an earlier
    /// SQLite database (a SQLite backup, or a .tmp): then the load is refused, so a lost database never silently re-imports stale data.</item>
    /// </list>
    /// </summary>
    internal sealed class SqliteWorldStore : IWorldStore
    {
        public static readonly string DatabasePath = Path.Combine(".", "SystemData.db");

        private static MessageQueue MessageQueue => MessageQueue.Instance;

        private readonly BinaryWorldStore _binary = new BinaryWorldStore();
        private readonly Lazy<SqliteWriter> _writer;

        public SqliteWorldStore()
        {
            _writer = new Lazy<SqliteWriter>(() => new SqliteWriter(DatabasePath, "SystemData writer"));
        }

        internal SqliteWriter Writer => _writer.Value;

        public bool LoadDesign(Envir e)
        {
            try
            {
                if (!File.Exists(DatabasePath))
                {
                    if (!CreateDatabase(e)) return false;
                }
                else if (File.Exists(Envir.DatabasePath) && File.GetLastWriteTimeUtc(Envir.DatabasePath) > File.GetLastWriteTimeUtc(DatabasePath))
                {
                    // Only the binary store writes Server.MirDB: a newer one means the server ran with Backend=Binary since this store
                    // was last written (a rollback). Loading the store would silently drop those edits, and the next save would bury them.
                    MessageQueue.Enqueue("[Store] Server.MirDB is newer than " + DatabasePath + " (the server ran with Backend=Binary since). " +
                                         "Refusing to load stale data: move " + DatabasePath + " away to import Server.MirDB again, or touch it to keep it (Knowledgebase/Runbook.md).");
                    return false;
                }

                var rows = Writer.Run(w => SqliteWorldFile.ReadAll(w.Connection));
                Envir.LoadVersion = Envir.Version;
                Envir.LoadCustomVersion = Envir.CustomVersion;
                SqliteWorldCodec.Decode(rows, e);
                return true;
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue("[Store] Loading system data from " + DatabasePath + " failed: " + ex.Message);
                MessageQueue.Enqueue(ex);
                return false;
            }
        }

        public void SaveDesign(Envir e, string backupFileName)
        {
            if (e == Envir.Main)
            {
                // Main never saves design data in this mode (Edit is the only writer); its periodic save keeps the respawn counter.
                var counter = (long)e.RespawnTick.CurrentTickcounter;
                Writer.Post(w => SqliteWorldFile.WriteRuntime(w.Connection, counter), "runtime save");
                return;
            }

            var rows = SqliteWorldCodec.Encode(e, CurrentRespawnCounter(e));
            var backupName = !string.IsNullOrWhiteSpace(backupFileName)
                ? Path.GetFileName(backupFileName)
                : $"Database {e.Now.Year:0000}-{e.Now.Month:00}-{e.Now.Day:00} {e.Now.Hour:00}-{e.Now.Minute:00}-{e.Now.Second:00}.bak";

            Writer.Run(w =>
            {
                if (!Directory.Exists(Envir.DatabaseBackUpPath)) Directory.CreateDirectory(Envir.DatabaseBackUpPath);
                SqliteWorldFile.BackupTo(w.Connection, Path.Combine(Envir.DatabaseBackUpPath, backupName));
                SqliteWorldFile.WriteAll(w.Connection, rows, includeRuntime: false, new Dictionary<string, object> { ["source"] = "save" });
                return 0;
            });
        }

        /// <summary>
        /// Takes a VACUUM INTO copy of the store (web admin backups). Runs on the writer, so it never sees a half-written save.
        /// </summary>
        public void BackupTo(string path) => Writer.Run(w => { SqliteWorldFile.BackupTo(w.Connection, path); return 0; });

        /// <summary>Main's published counter when Edit saves (F4), else the envir's own.</summary>
        internal static long CurrentRespawnCounter(Envir e)
        {
            var published = Envir.PublishedRespawnTick;
            return e == Envir.Edit && published >= 0 ? published : (long)e.RespawnTick.CurrentTickcounter;
        }

        /// <summary>
        /// First boot: import Server.MirDB when there is one (a new server gets an empty database). Refuses when an earlier SQLite
        /// database evidently existed. The import is written to a .tmp, checked, read back and compared before it is moved into place.
        /// </summary>
        private bool CreateDatabase(Envir e)
        {
            if (HasPriorSqliteEvidence())
            {
                MessageQueue.Enqueue("[Store] " + DatabasePath + " is missing but a SQLite backup in Back Up\\Database or " + DatabasePath +
                                     ".tmp exists. Refusing to import Server.MirDB over it: restore the newest backup (Knowledgebase/Runbook.md).");
                return false;
            }

            var meta = new Dictionary<string, object>();
            if (File.Exists(Envir.DatabasePath))
            {
                if (!_binary.LoadDesign(e)) return false;
                if (Envir.LoadVersion <= 70) e.UpdateMagicInfo(); // WorldBinder won't: after this load LoadVersion is the current version
                meta["source"] = "import";
                meta["source_file"] = Path.GetFullPath(Envir.DatabasePath);
                meta["source_envir_version"] = (long)Envir.LoadVersion;
                meta["source_sha256"] = Sha256(Envir.DatabasePath);
                MessageQueue.Enqueue("[Store] Importing " + Envir.DatabasePath + " (version " + Envir.LoadVersion.ToString(CultureInfo.InvariantCulture) + ") into " + DatabasePath);
            }
            else meta["source"] = "new";

            var rows = SqliteWorldCodec.Encode(e, (long)e.RespawnTick.CurrentTickcounter);
            Create(DatabasePath, rows, meta);
            MessageQueue.Enqueue("[Store] Created " + DatabasePath + " (" + meta["source"] + "); Server.MirDB is left as it was.");
            return true;
        }

        /// <summary>Writes <paramref name="rows"/> to a new database at <paramref name="path"/> (via .tmp, integrity check and read-back).</summary>
        internal static void Create(string path, WorldRows rows, IDictionary<string, object> meta)
        {
            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            var ok = false;
            try
            {
                using (var conn = SqliteWorldFile.Open(tmp, create: true))
                {
                    SqliteWorldFile.CreateSchema(conn);
                    SqliteWorldFile.WriteAll(conn, rows, includeRuntime: true, meta);
                    SqliteWorldFile.CheckIntegrity(conn);
                    var back = SqliteWorldFile.ReadAll(conn);
                    var diff = FirstDifference(rows, back);
                    if (diff != null) throw new InvalidDataException("[Store] read-back differs from what was written: " + diff);
                }

                File.Move(tmp, path, overwrite: false);
                ok = true;
            }
            finally
            {
                if (!ok && File.Exists(tmp)) File.Delete(tmp);
            }
        }

        /// <summary>
        /// A SQLite backup in Back Up\Database means a store existed and was lost. A leftover .tmp alone is a crashed first import
        /// (only <see cref="Create"/> writes one, and only while no store exists): it is logged and replaced, not treated as evidence.
        /// </summary>
        internal static bool HasPriorSqliteEvidence()
        {
            var backups = Directory.Exists(Envir.DatabaseBackUpPath) &&
                          Directory.EnumerateFiles(Envir.DatabaseBackUpPath, "*.bak").Any(SqliteWorldFile.LooksLikeSqlite);
            if (!backups && File.Exists(DatabasePath + ".tmp"))
                MessageQueue.Enqueue("[Store] Replacing " + DatabasePath + ".tmp left by an interrupted import.");
            return backups;
        }

        internal static string Sha256(string path)
        {
            using var f = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(f));
        }

        /// <summary>Null when equal, else the first differing table / row / cell.</summary>
        internal static string FirstDifference(WorldRows a, WorldRows b)
        {
            foreach (var name in SqliteWorldCodec.CounterNames)
                if (a.Counters[name] != b.Counters[name]) return "index_counters." + name;
            if (a.BaseSpawnRate != b.BaseSpawnRate) return "respawn_tick.BaseSpawnRate";
            if (a.RespawnTickCounter != b.RespawnTickCounter) return "runtime_state.RespawnTickCounter";
            if (!a.RespawnOptions.SequenceEqual(b.RespawnOptions)) return "respawn_tick_options";

            foreach (var spec in SqliteWorldCodec.Specs)
            {
                var ra = a.Tables[spec.Name].Rows;
                var rb = b.Tables[spec.Name].Rows;
                if (ra.Count != rb.Count) return spec.Name + ": " + ra.Count + " rows vs " + rb.Count;
                var names = SqliteWorldCodec.ColumnNames(spec);
                for (var i = 0; i < ra.Count; i++)
                    for (var c = 0; c < names.Count; c++)
                        if (!Equals(ra[i][c], rb[i][c]))
                            return spec.Name + " row " + i.ToString(CultureInfo.InvariantCulture) + " column " + names[c] + ": " + (ra[i][c] ?? "NULL") + " vs " + (rb[i][c] ?? "NULL");
            }

            return null;
        }
    }
}
