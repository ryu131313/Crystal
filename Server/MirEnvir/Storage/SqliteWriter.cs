using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// The one thread that owns a SQLite file (roadmap P7 phase 7.2). All reads, writes and backups of that file run here, in order.
    /// <para>Rules: the writer never takes EditLock, LoadLock or AccountLock and never posts to the game thread, so a caller may wait for
    /// it while holding any of them. The game thread never waits: it only <see cref="Post"/>s. Other threads (web workers, the WinForms
    /// UI, startup) may <see cref="Run{T}"/> and wait for the result.</para>
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

        public SqliteWriter(string path, string threadName)
        {
            Path = System.IO.Path.GetFullPath(path); // pinned: the working directory may change later (tests, tools)
            _thread = new Thread(Loop) { IsBackground = true, Name = threadName };
            _thread.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }

        public bool IsWriterThread => Thread.CurrentThread == _thread;

        /// <summary>Runs <paramref name="job"/> on the writer thread and waits for it; its exception is rethrown here.</summary>
        public T Run<T>(Func<SqliteWriter, T> job)
        {
            if (IsWriterThread) return job(this);

            T result = default;
            Exception error = null;
            using var done = new ManualResetEventSlim(false);
            if (!TryAdd(() =>
                {
                    try { result = job(this); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                }))
                throw new IOException("[Store] the writer for " + Path + " has shut down");

            done.Wait();
            if (error != null) throw new IOException("[Store] " + error.Message, error);
            return result;
        }

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
                _conn = SqliteWorldFile.Open(Path, create: false);
                try
                {
                    SqliteWorldFile.CheckIntegrity(_conn);
                    SqliteWorldFile.CheckSchema(_conn);
                }
                catch
                {
                    CloseConnection();
                    throw;
                }

                return _conn;
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
