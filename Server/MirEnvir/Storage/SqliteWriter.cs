using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// The one thread that owns a SQLite file (roadmap P7 phase 7.2). All reads, writes and backups of that file run here, in order.
    /// <para>Rules: the writer never takes EditLock, LoadLock or AccountLock and never posts to the game thread, so a caller may wait for
    /// it while holding any of them. The game thread never waits: it only <see cref="Post"/>s (or <see cref="TryPost"/>s). Other threads
    /// (web workers, the WinForms UI, startup) may <see cref="Run{T}"/> and wait for the result. The one exception on the game thread is
    /// the UserData shutdown save's bounded wait (<see cref="TryRun{T}"/>, UserData-and-Quests-Plan.md Part A, Shutdown).</para>
    /// <para>One writer per file: SystemData.db (the default open) and UserData.db (its own open callback).</para>
    /// <para>The thread is a background thread (a foreground one would keep the process alive: the runtime waits for foreground threads
    /// before it raises ProcessExit), and the ProcessExit handler drains its queue (<see cref="Shutdown"/>), so a posted save isn't lost
    /// when the server closes.</para>
    /// </summary>
    internal sealed class SqliteWriter
    {
        private static MessageQueue MessageQueue => MessageQueue.Instance;

        private readonly BlockingCollection<Action> _jobs = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private SqliteConnection _conn;

        public string Path { get; }

        /// <summary>Failed posted jobs (a Run failure is thrown to its caller instead).</summary>
        public static long PostFailures => Interlocked.Read(ref _postFailures);
        private static long _postFailures;

        /// <summary>Opens and checks the file on first use (writer thread only); SystemData.db's checks and migrations unless given.</summary>
        private readonly Func<string, SqliteConnection> _open;

        public SqliteWriter(string path, string threadName) : this(path, threadName, null) { }

        /// <param name="open">Opens the existing file at the given full path and runs its checks (writer thread). Null = SystemData.db's
        /// open: integrity, schema, migrations (UserData.db passes its own, Part A of UserData-and-Quests-Plan.md).</param>
        public SqliteWriter(string path, string threadName, Func<string, SqliteConnection> open)
        {
            Path = System.IO.Path.GetFullPath(path); // pinned: the working directory may change later (tests, tools)
            _open = open ?? OpenSystemData;
            _thread = new Thread(Loop) { IsBackground = true, Name = threadName };
            _thread.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }

        public bool IsWriterThread => Thread.CurrentThread == _thread;

        /// <summary>Runs <paramref name="job"/> on the writer thread and waits for it; its exception is rethrown here.</summary>
        public T Run<T>(Func<SqliteWriter, T> job)
        {
            TryRun(job, Timeout.Infinite, out var result);
            return result;
        }

        /// <summary>
        /// <see cref="Run{T}"/> with a bounded wait: false when <paramref name="timeoutMs"/> passed first (the job stays queued and may
        /// still run). Only for the shutdown save, the documented exception to "the game thread only posts" (UserData plan, Shutdown).
        /// </summary>
        public bool TryRun<T>(Func<SqliteWriter, T> job, int timeoutMs, out T result)
        {
            result = default;
            if (IsWriterThread)
            {
                result = job(this);
                return true;
            }

            var box = new RunBox<T>();
            if (!TryAdd(() =>
                {
                    try { box.Result = job(this); }
                    catch (Exception ex) { box.Error = ex; }
                    finally { box.Done.Set(); }
                }))
                throw new IOException("[Store] the writer for " + Path + " has shut down");

            if (!box.Done.Wait(timeoutMs)) return false; // the event is left to the GC: the job may still set it
            box.Done.Dispose();
            if (box.Error != null) throw new IOException("[Store] " + box.Error.Message, box.Error);
            result = box.Result;
            return true;
        }

        private sealed class RunBox<T>
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public T Result;
            public Exception Error;
        }

        /// <summary>
        /// Queues <paramref name="job"/> without waiting. False (nothing queued, nothing logged) when the writer has shut down; the job
        /// handles its own failures. The UserData save uses it to set its failure flags when a post is refused.
        /// </summary>
        public bool TryPost(Action<SqliteWriter> job) => TryAdd(() => job(this));

        /// <summary>Queues <paramref name="job"/> without waiting (the game thread's only way in). Failures are logged and counted.</summary>
        public void Post(Action<SqliteWriter> job, string what)
        {
            if (TryAdd(() =>
                {
                    try { job(this); }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _postFailures);
                        MessageQueue.Enqueue("[Store] " + what + " failed for " + Path + ": " + ex.Message);
                        MessageQueue.Enqueue(ex);
                    }
                }))
                return;

            Interlocked.Increment(ref _postFailures);
            MessageQueue.Enqueue("[Store] " + what + " dropped: the writer for " + Path + " has shut down");
        }

        private bool TryAdd(Action a)
        {
            try { return !_jobs.IsAddingCompleted && _jobs.TryAdd(a); }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>The open connection (writer thread only). Opens the existing file on first use; never creates it.</summary>
        public SqliteConnection Connection
        {
            get
            {
                if (!IsWriterThread) throw new InvalidOperationException("[Store] connection used off the writer thread");
                if (_conn != null) return _conn;
                _conn = _open(Path);
                return _conn;
            }
        }

        /// <summary>The per-file open of SystemData.db: integrity, schema and the written migrations.</summary>
        private static SqliteConnection OpenSystemData(string path)
        {
            var conn = SqliteWorldFile.Open(path, create: false);
            try
            {
                SqliteWorldFile.CheckIntegrity(conn);
                SqliteWorldFile.CheckSchema(conn);
                SqliteWorldFile.Migrate(conn, MessageQueue.Enqueue, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), SqliteWorldStore.BackUpPath,
                    $"Pre-migrate {DateTime.Now:yyyy-MM-dd HH-mm-ss}.bak"));
                SqliteWorldFile.FixDuplicateRespawnIndexes(conn, MessageQueue.Enqueue, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), SqliteWorldStore.BackUpPath,
                    $"Pre-respawn-fix {DateTime.Now:yyyy-MM-dd HH-mm-ss}.bak"));
                DropTables.ConvertLines(conn, MessageQueue.Enqueue, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), SqliteWorldStore.BackUpPath,
                    $"Pre-drops {DateTime.Now:yyyy-MM-dd HH-mm-ss}.bak"));
                SqliteWorldFile.UpgradeSchemaVersion(conn);
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        /// <summary>Closes the connection (writer thread only) so the file can be replaced; the next use reopens it.</summary>
        public void CloseConnection()
        {
            _conn?.Dispose();
            _conn = null;
        }

        /// <summary>Stops accepting jobs, drains the queue and closes the file (waits up to 10 s).</summary>
        public void Shutdown()
        {
            try { _jobs.CompleteAdding(); }
            catch (ObjectDisposedException) { return; }
            if (!IsWriterThread) _thread.Join(10000);
        }

        private void Loop()
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
                job();

            CloseConnection();
        }
    }
}
