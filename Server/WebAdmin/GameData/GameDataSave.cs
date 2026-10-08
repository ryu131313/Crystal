using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// The one way the web admin writes Server.MirDB from the editor copy (Envir.Edit) or re-reads it into that copy.
    /// <para>
    /// Server.MirDB has another writer: Main's periodic SaveDB() on the game thread (File.Copy + File.Create with FileShare.None), which
    /// stops the world when it throws. Any open handle at that moment (an Edit.SaveDB / Edit.LoadDB on an HTTP thread) makes it throw, and
    /// Main's save can likewise make an Edit save fail. So the file IO is serialised with Main:
    /// </para>
    /// <list type="bullet">
    /// <item>game loop running and Main still saves (!SystemDataStale): the IO runs ON the game thread, between ticks;</item>
    /// <item>otherwise (stopped, starting, stopping, or Main skips its saves because SystemDataStale): inline, under Envir.LoadLock,
    /// which also serialises it with Main.LoadDB() during a world start.</item>
    /// </list>
    /// Callers hold <see cref="WebAdminGame.EditLock"/> on a worker thread (never on the game thread). Lock order: EditLock, LoadLock.
    /// The work posted to the game thread takes no web admin lock, so it never waits for the caller.
    /// </summary>
    internal static class GameDataSave
    {
        /// <summary>How long to wait for the game thread to pick the IO up before cancelling it (it is never abandoned once started).</summary>
        internal static int GameThreadStartTimeoutMs = 30000;

        /// <summary>Refuses a save that could truncate Server.MirDB (half-reloaded or never-loaded editor copy). Caller holds EditLock.</summary>
        public static void EnsureSavable()
        {
            if (GameDataState.EditReloadFailed)
                throw new WebAdminException(409, "The last reload of the editor copy failed part-way, so it may be incomplete and saving could truncate Server.MirDB. Reload it successfully first (Discard unsaved).");

            if (!EditLooksLoaded())
                throw new WebAdminException(409, "The editor copy of the game data is not loaded, so saving would empty Server.MirDB. Reload it first (Discard unsaved).");
        }

        /// <summary>True when Envir.Edit holds data, or when there is nothing on disk worth protecting yet.</summary>
        public static bool EditLooksLoaded()
        {
            var edit = Envir.Edit;
            if (edit.ItemInfoList.Count > 0 || edit.MonsterInfoList.Count > 0 || edit.MapInfoList.Count > 0) return true;
            try
            {
                return !File.Exists(Envir.DatabasePath) || new FileInfo(Envir.DatabasePath).Length < 1024;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes Envir.Edit to Server.MirDB (serialised with Main, see the class summary), then does the Game Data bookkeeping: clears
        /// the unsaved-change tracker and leaves the saved spells pending for Main (<see cref="GameDataMagicSync"/>).
        /// Caller holds EditLock and has called <see cref="EnsureSavable"/>. Exceptions from SaveDB propagate (nothing is marked saved).
        /// </summary>
        public static SaveOutcome SaveEdit()
        {
            RunFileIo(() =>
            {
                Envir.Edit.SaveDB();
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

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var w = WebAdminGame.World;

                if (w.QueueOpen && !Envir.SystemDataStale)
                {
                    if (TryRunOnGameThread(w, io, out var result)) return result;
                    continue; // dropped: the loop stopped meanwhile, re-evaluate
                }

                lock (Envir.LoadLock)
                {
                    // The loop may have opened (or Main stopped skipping saves) since the check: then go through the game thread.
                    if (w.QueueOpen && !Envir.SystemDataStale) continue;
                    return io();
                }
            }

            throw new WebAdminException(409, "The world is changing state; try again in a moment", new { world = WebAdminGame.WorldState });
        }

        private static bool TryRunOnGameThread<T>(IWebAdminWorld w, Func<T> io, out T result)
        {
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

            if (!done.Wait(GameThreadStartTimeoutMs))
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
