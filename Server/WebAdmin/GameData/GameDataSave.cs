using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// The one way the web admin writes the system data (SystemData.db, roadmap P7) from the editor copy (Envir.Edit) or re-reads it into
    /// that copy. Every file access goes through the store's writer thread, and Main never writes design data, so the IO runs inline on
    /// the caller's thread under Envir.LoadLock (which serialises it with Main.LoadDB() during a world start).
    /// Callers hold <see cref="WebAdminGame.EditLock"/> on a worker thread (never on the game thread). Lock order: EditLock, LoadLock.
    /// <see cref="TryRunOnGameThread"/> is LiveDb's way onto the game thread.
    /// </summary>
    internal static class GameDataSave
    {
        /// <summary>How long to wait for the game thread to pick the IO up before cancelling it (it is never abandoned once started).</summary>
        internal static int GameThreadStartTimeoutMs = 30000;

        /// <summary>Refuses a save that could truncate the game data (half-reloaded or never-loaded editor copy). Caller holds EditLock.</summary>
        public static void EnsureSavable()
        {
            if (GameDataState.EditReloadFailed)
                throw new WebAdminException(409, "The last reload of the editor copy failed part-way, so it may be incomplete and saving could truncate the game data. Reload it successfully first (Discard unsaved).");

            if (!EditLooksLoaded())
                throw new WebAdminException(409, "The editor copy of the game data is not loaded, so saving would empty the game data. Reload it first (Discard unsaved).");
        }

        /// <summary>True when Envir.Edit holds data, or when there is nothing on disk worth protecting yet.</summary>
        public static bool EditLooksLoaded()
        {
            var edit = Envir.Edit;
            if (edit.ItemInfoList.Count > 0 || edit.MonsterInfoList.Count > 0 || edit.MapInfoList.Count > 0) return true;
            try
            {
                // SQLite store (roadmap P7): protected as soon as it holds any item, monster or map.
                if (Envir.Store is SqliteWorldStore store) return !store.HasDesignRows();
                return !File.Exists(Envir.DatabasePath) || new FileInfo(Envir.DatabasePath).Length < 1024;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes Envir.Edit to the game data store (see the class summary), then does the Game Data bookkeeping: clears
        /// the unsaved-change tracker and leaves the saved spells pending for Main (<see cref="GameDataMagicSync"/>).
        /// Caller holds EditLock and has called <see cref="EnsureSavable"/>. Exceptions from SaveDB propagate (nothing is marked saved).
        /// </summary>
        public static SaveOutcome SaveEdit()
        {
            // F3: Envir.Edit's clock never advances, so SaveDB() alone would overwrite one rolling backup; pick the name from the wall
            // clock (a timestamped backup at most every [LiveDb] BackupEveryMinutes, the rolling one in between).
            var now = DateTime.Now;
            var name = LiveDb.LiveDbBackupPolicy.Next(now, Settings.LiveDbBackupEveryMinutes);
            var outcome = SaveEdit(name);
            LiveDb.LiveDbBackupPolicy.Saved(name, now);
            return outcome;
        }

        /// <summary>
        /// As <see cref="SaveEdit()"/>, with the file name the previous Server.MirDB gets in Back Up\Database (null = Envir's own
        /// timestamp, which for Envir.Edit never advances). Caller holds EditLock and has called <see cref="EnsureSavable"/>.
        /// </summary>
        public static SaveOutcome SaveEdit(string backupFileName)
        {
            RunFileIo(() =>
            {
                Envir.Edit.SaveDB(backupFileName);
                return true;
            });

            return AfterFileChanged(true);
        }

        /// <summary>
        /// Server.MirDB now holds Envir.Edit's data (after a save, or after the editor copy was reloaded from a changed file, e.g. a
        /// restore). Main never reloads spells, so the editor copy's spells replace any older pending snapshot. Caller holds EditLock.
        /// </summary>
        public static SaveOutcome AfterFileChanged(bool markSaved)
        {
            var areas = markSaved ? GameDataState.Unsaved.MarkSaved() : (IReadOnlyList<string>)Array.Empty<string>();
            var magic = GameDataMagicSync.Snapshot(Envir.Edit.MagicInfoList);
            GameDataMagicSync.SetPending(magic);
            return new SaveOutcome(areas, magic);
        }

        /// <summary>
        /// Copies the saved spells onto Main's MagicInfo objects now when the world is running or fully stopped; otherwise they stay
        /// pending for the WorldStarted hook. Call WITHOUT holding EditLock.
        /// </summary>
        public static async Task<(int Changed, bool Synced)> SyncMagicAsync(List<MagicInfo> magic)
        {
            try
            {
                var changed = await WebAdminGame.RunOrInlineAsync(() => GameDataMagicSync.CopyInto(magic, Envir.Main.MagicInfoList), 10000).ConfigureAwait(false);
                GameDataMagicSync.ClearPending(magic);
                return (changed, true);
            }
            catch (WebAdminException ex) when (ex is WorldNotRunningException || ex is GameThreadTimeoutException)
            {
                // Still pending: applied by the WorldStarted hook (game thread, after Main.LoadDB) the next time the world starts.
                return (0, false);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Applying saved magic changes to the running world failed");
                return (0, false);
            }
        }

        /// <summary>
        /// Runs file IO on Server.MirDB so that it never overlaps Main's periodic SaveDB (see the class summary). Caller holds EditLock on
        /// a worker thread. Exceptions thrown by <paramref name="io"/> propagate.
        /// </summary>
        public static T RunFileIo<T>(Func<T> io)
        {
            if (io == null) throw new ArgumentNullException(nameof(io));

            // Main never writes design data and every file access goes through the store's writer thread (roadmap P7): running here
            // keeps the game thread from waiting on the writer.
            lock (Envir.LoadLock) return io();
        }

        /// <summary>
        /// Queues <paramref name="io"/> on the game thread and blocks until it finished (worker threads only, never the game thread; the
        /// caller may hold EditLock because the queued work takes no web admin lock). Returns false when it could not be queued or was
        /// dropped before it ran (the loop stopped); throws <see cref="GameThreadTimeoutException"/> (Started = false, cancelled) when the
        /// game thread did not pick it up within <paramref name="startTimeoutMs"/>; once started it is always waited for. Exceptions
        /// thrown by <paramref name="io"/> propagate. Shared by the file IO here and the LiveDb live step.
        /// </summary>
        internal static bool TryRunOnGameThread<T>(IWebAdminWorld w, Func<T> io, int startTimeoutMs, out T result)
        {
            if (startTimeoutMs <= 0) startTimeoutMs = GameThreadStartTimeoutMs;
            var state = 0; // 0 pending, 1 started, 2 cancelled / dropped
            var done = new ManualResetEventSlim(false); // not disposed: a late callback may still Set it
            var value = default(T);
            Exception error = null;

            var queued = w.TryQueue(() =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try
                {
                    value = io();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            }, _ =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0) done.Set();
            });

            result = default(T);
            if (!queued) return false;

            if (!done.Wait(startTimeoutMs))
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                    throw new GameThreadTimeoutException(false);

                // Started: the caller's EditLock must stay held until the IO finished (it reads/writes Envir.Edit).
                done.Wait();
            }

            if (Volatile.Read(ref state) == 2) return false; // dropped before it ran

            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            result = value;
            return true;
        }

        internal readonly struct SaveOutcome
        {
            public SaveOutcome(IReadOnlyList<string> areas, List<MagicInfo> magic)
            {
                Areas = areas;
                Magic = magic;
            }

            public IReadOnlyList<string> Areas { get; }
            public List<MagicInfo> Magic { get; }
        }
    }
}
