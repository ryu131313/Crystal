using System.Globalization;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// User data in SQLite, <c>UserData.db</c> (UserData-and-Quests-Plan.md Part A): accounts, the world head/tail, guilds, NPC goods and
    /// conquests as rows. One <see cref="SqliteWriter"/> thread owns the file; rows are encoded and decoded on the calling thread.
    /// <list type="bullet">
    /// <item>The game thread only posts saves (<see cref="TryPostSave"/>). The one exception is the shutdown save's bounded wait
    /// (<see cref="TryRunSave"/>).</item>
    /// <item>Loads, offline saves (world stopped), backups and restores wait on the writer from their own thread.</item>
    /// <item>Every copy of the live file is a VACUUM INTO on the writer, never a file copy (a plain copy of a live DELETE-journal file can be torn).</item>
    /// </list>
    /// The path is pinned when the store is created (the server's working directory; tests create one per folder).
    /// </summary>
    internal sealed class UserDataStore
    {
        public static readonly string DatabasePath = Path.Combine(".", "UserData.db");

        private static MessageQueue MessageQueue => MessageQueue.Instance;

        private readonly Lazy<SqliteWriter> _writer;

        public string FullPath { get; }

        public UserDataStore() : this(DatabasePath) { }

        public UserDataStore(string path)
        {
            FullPath = Path.GetFullPath(path);
            _writer = new Lazy<SqliteWriter>(() => new SqliteWriter(FullPath, "UserData writer", UserDataFile.Open));
        }

        internal SqliteWriter Writer => _writer.Value;

        public bool Exists => File.Exists(FullPath);

        /// <summary>The world row and the account rows (waits on the writer; never from the game thread while the world runs).</summary>
        public UserDataRows ReadWorld() => Writer.Run(w => UserDataFile.ReadWorld(w.Connection));

        /// <summary>The rows of guilds, goods or conquests (waits on the writer).</summary>
        public List<UserDataRow> ReadTable(string table) => Writer.Run(w => UserDataFile.ReadTable(w.Connection, table));

        /// <summary>meta.envir_version through the writer (null when the store does not exist or has none).</summary>
        public int? ReadEnvirVersion()
        {
            if (!Exists) return null;
            var v = Writer.Run(w => UserDataFile.ReadEnvirVersion(w.Connection));
            return v.HasValue ? (int)v.Value : null;
        }

        /// <summary>Waits until every job queued before this call has run (the buffer of an earlier save is free again).</summary>
        public void WaitIdle() => Writer.Run(_ => 0);

        /// <summary>
        /// Game thread: queues <paramref name="job"/>. <paramref name="completed"/> runs on the writer thread with true after the commit,
        /// false after a failure, before the job lets go of the buffer. Returns false when the writer refused the job (shut down);
        /// <paramref name="completed"/> is then not called.
        /// </summary>
        public bool TryPostSave(UserDataSaveJob job, Action<bool> completed)
        {
            try
            {
                return Writer.TryPost(w => completed(RunJob(w, job)));
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue("[UserData] the save could not be queued: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Runs <paramref name="job"/> and waits at most <paramref name="timeoutMs"/> (shutdown only). False on a timeout (the job may
        /// still run); otherwise <paramref name="ok"/> says whether it committed.
        /// </summary>
        public bool TryRunSave(UserDataSaveJob job, int timeoutMs, Action<bool> completed, out bool ok)
        {
            return Writer.TryRun(w =>
            {
                var result = RunJob(w, job);
                completed(result);
                return result;
            }, timeoutMs, out ok);
        }

        /// <summary>Writer thread: the job's transaction, then its backup. False (logged) when the transaction failed.</summary>
        private static bool RunJob(SqliteWriter w, UserDataSaveJob job)
        {
            try
            {
                UserDataFile.WriteSave(w.Connection, job);
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue("[UserData] save failed (the next save writes every row again): " + ex.Message);
                MessageQueue.Enqueue(ex);
                return false;
            }

            if (job.BackupPath != null)
            {
                try
                {
                    var dir = Path.GetDirectoryName(job.BackupPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    SqliteWorldFile.BackupTo(w.Connection, job.BackupPath);
                }
                catch (Exception ex)
                {
                    // The save itself committed: a failed backup is only logged (the next due backup tries again).
                    MessageQueue.Enqueue("[UserData] backup " + Path.GetFileName(job.BackupPath) + " failed: " + ex.Message);
                }
            }

            return true;
        }

        /// <summary>VACUUM INTO <paramref name="path"/> on the writer (manual and pre-restore copies).</summary>
        public void BackupTo(string path) => Writer.Run(w =>
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            SqliteWorldFile.BackupTo(w.Connection, path);
            return 0;
        });

        /// <summary>
        /// World stopped: copies the live store to <paramref name="preRestorePath"/> (when it exists), closes the writer's connection and
        /// swaps <paramref name="backupPath"/> in (via a checked .tmp). The next use reopens and checks the file. Throws when the backup is
        /// not a usable UserData.db; the live file is then unchanged.
        /// </summary>
        public void Restore(string backupPath, string preRestorePath)
        {
            Writer.Run(w =>
            {
                if (File.Exists(FullPath) && preRestorePath != null)
                {
                    var dir = Path.GetDirectoryName(preRestorePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    SqliteWorldFile.BackupTo(w.Connection, preRestorePath);
                }

                var tmp = FullPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.Copy(backupPath, tmp);
                try
                {
                    CheckCandidate(tmp);
                    w.CloseConnection();
                    File.Move(tmp, FullPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }

                return 0;
            });
        }

        /// <summary>Throws unless <paramref name="path"/> is a SQLite UserData.db with a consistent world row (restore candidates).</summary>
        public static void CheckCandidate(string path)
        {
            if (!SqliteWorldFile.LooksLikeSqlite(path))
                throw new InvalidDataException("[UserData] " + Path.GetFileName(path) + " is not a SQLite file");
            using var conn = UserDataFile.Open(path);
            UserDataFile.ReadWorld(conn);
        }

        /// <summary>
        /// Writes <paramref name="rows"/> to a new store at <paramref name="path"/> like SqliteWorldStore.Create: a .tmp, integrity check,
        /// read back and compared, then moved into place (never over an existing file). A crash before the move leaves only the .tmp.
        /// </summary>
        public static void Create(string path, UserDataRows rows, int envirVersion, IDictionary<string, object> meta)
        {
            var tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            var ok = false;
            try
            {
                using (var conn = SqliteWorldFile.Open(tmp, create: true))
                {
                    UserDataFile.CreateSchema(conn);
                    UserDataFile.WriteRows(conn, rows, envirVersion, meta);
                    SqliteWorldFile.CheckIntegrity(conn);
                    var back = UserDataFile.ReadAll(conn);
                    var diff = FirstDifference(rows, back);
                    if (diff != null) throw new InvalidDataException("[UserData] read-back differs from what was written: " + diff);
                }

                File.Move(tmp, path, overwrite: false);
                ok = true;
            }
            finally
            {
                if (!ok && File.Exists(tmp)) File.Delete(tmp);
            }
        }

        /// <summary>Null when equal (row order is by key on both sides), else the first difference.</summary>
        internal static string FirstDifference(UserDataRows a, UserDataRows b)
        {
            if (!a.Head.AsSpan().SequenceEqual(b.Head)) return "world.head";
            if (!a.Tail.AsSpan().SequenceEqual(b.Tail)) return "world.tail";
            return Diff("accounts", a.Accounts, b.Accounts) ?? Diff("guilds", a.Guilds, b.Guilds) ?? Diff("goods", a.Goods, b.Goods) ?? Diff("conquests", a.Conquests, b.Conquests);

            static string Diff(string table, List<UserDataRow> x, List<UserDataRow> y)
            {
                var xs = new List<UserDataRow>(x);
                xs.Sort((p, q) => p.Key.CompareTo(q.Key));
                if (xs.Count != y.Count) return table + ": " + xs.Count.ToString(CultureInfo.InvariantCulture) + " rows vs " + y.Count.ToString(CultureInfo.InvariantCulture);
                for (var i = 0; i < xs.Count; i++)
                {
                    if (xs[i].Key != y[i].Key || !string.Equals(xs[i].Id, y[i].Id, StringComparison.Ordinal) || !xs[i].Data.AsSpan().SequenceEqual(y[i].Data))
                        return table + " key " + xs[i].Key.ToString(CultureInfo.InvariantCulture);
                }
                return null;
            }
        }

        /// <summary>Stops the writer (tests; the server's writer is drained on ProcessExit).</summary>
        public void Shutdown()
        {
            if (_writer.IsValueCreated) _writer.Value.Shutdown();
        }
    }
}
