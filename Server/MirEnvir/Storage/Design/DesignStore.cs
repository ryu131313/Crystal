using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// The design data that used to live in loose files (roadmap P7 phase 7.4): ini settings, drops, recipes and routes,
    /// stored in SystemData.db next to the Server.MirDB tables and written by the same <see cref="SqliteWriter"/>.
    /// <list type="bullet">
    /// <item><b>Active</b> once SystemData.db exists and the server runs on the SQLite store. Before the first import, and in tests and
    /// offline tools that select <see cref="BinaryWorldStore"/>, every reader falls back to the files.</item>
    /// <item><b>Lazy import per file / section:</b> the first read of something not yet in the store imports it from its file (and records
    /// that in the section's own table), so a store created before 7.4 picks the files up on its first use. The files are never deleted;
    /// they are the rollback (<c>MirDbExport export-files</c> writes the store back).</item>
    /// <item><b>No Envir here:</b> <c>Settings.Load</c> reads through this class at process start, before any Envir exists, and Envir's
    /// field initialisers read Settings; touching Envir from here would size them from unloaded settings.</item>
    /// </list>
    /// </summary>
    internal static class DesignStore
    {
        /// <summary>Set when Envir.Store is not the SQLite store (tests and tools on Server.MirDB): readers use the files.</summary>
        internal static volatile bool Disabled;

        /// <summary>Tests: a private writer (its file decides <see cref="Active"/>); null = the server's shared writer.</summary>
        internal static SqliteWriter WriterOverride;

        public static bool Active => !Disabled && Broken == null && File.Exists(WriterOverride?.Path ?? SqliteWorldStore.DatabasePath);

        /// <summary>
        /// Set when the store couldn't be opened for the design data (corrupt, newer schema): readers fall back to the files, and the
        /// world start (SqliteWorldStore.LoadDesign) refuses with the same error. Logged once.
        /// </summary>
        internal static volatile string Broken;

        /// <summary>Set after SystemData.db was restored while the server runs: Settings in memory are older than the store until a restart,
        /// so design settings aren't saved over the restored ones (logged).</summary>
        internal static volatile bool SavesBlockedUntilRestart;

        /// <summary>Runs <paramref name="job"/> and turns a failure to open the store into <see cref="Broken"/> (falls back to <paramref name="fallback"/>).</summary>
        public static T Guard<T>(Func<T> job, Func<T> fallback)
        {
            try { return job(); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is Microsoft.Data.Sqlite.SqliteException)
            {
                if (Broken == null)
                {
                    Broken = ex.Message;
                    MessageQueue.Instance.Enqueue("[Store] SystemData.db can't be used for the design files (" + ex.Message + "); reading the files instead. The world won't start until it is fixed (Knowledgebase/Runbook.md §10).");
                }

                return fallback();
            }
        }

        /// <summary>Main's WorkLoop thread (set when it starts): design writes from it are posted, never waited for.</summary>
        internal static volatile int GameThreadId = -1;

        public static bool OnGameThread => Environment.CurrentManagedThreadId == GameThreadId;

        /// <summary>Queues <paramref name="job"/> on the writer without waiting (failures are logged and counted by the writer).</summary>
        public static void Post(Action<SqliteConnection> job, string what) => (WriterOverride ?? SqliteWorldStore.SharedWriter).Post(w => job(w.Connection), what);

        /// <summary>Runs <paramref name="job"/> on the store's writer thread and waits (never call from the game thread at runtime).</summary>
        public static T Run<T>(Func<SqliteConnection, T> job) => (WriterOverride ?? SqliteWorldStore.SharedWriter).Run(w => job(w.Connection));

        /// <summary>Relative, '/'-separated key of a file under the working directory ("Configs/Mines.ini", "Envir/Drops/Hen.txt").</summary>
        public static string Key(string path) =>
            Path.GetRelativePath(Path.GetFullPath("."), Path.GetFullPath(path)).Replace('\\', '/');

        /// <summary>CREATE statements of every design table (added to new and existing stores by <see cref="SqliteWorldFile.Migrate"/>).</summary>
        public static IEnumerable<(string Table, string Ddl)> Tables()
        {
            foreach (var t in DesignIni.Tables()) yield return t;
            foreach (var t in DesignTexts.Tables()) yield return t;
            foreach (var t in DropTables.Tables()) yield return t;
        }
    }
}
