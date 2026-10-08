using System.Diagnostics;
using Server.MirEnvir;

namespace Server.WebAdmin
{
    /// <summary>Abstraction over Envir.Main's lifecycle + action queue (lets tests run without constructing Envir/Settings).</summary>
    public interface IWebAdminWorld
    {
        /// <summary>Envir.Main.Running.</summary>
        bool Running { get; }

        /// <summary>The game loop is draining admin actions.</summary>
        bool QueueOpen { get; }

        /// <summary>The WorkLoop thread exists (starting / running / stopping).</summary>
        bool WorkLoopAlive { get; }

        /// <summary>Queues work for the game thread; false when the loop is not running. <paramref name="dropped"/> runs if the loop exits first.</summary>
        bool TryQueue(Action run, Action<Exception> dropped);

        /// <summary>Lock used for inline account work while the world is stopped (Envir.AccountLock).</summary>
        object AccountLock { get; }
    }

    internal sealed class EnvirWorld : IWebAdminWorld
    {
        public bool Running => Envir.Main.Running;
        public bool QueueOpen => Envir.Main.AdminQueueOpen;
        public bool WorkLoopAlive => Envir.Main.WorkLoopAlive;
        public bool TryQueue(Action run, Action<Exception> dropped) => Envir.Main.QueueAdminAction(run, dropped);
        public object AccountLock => Envir.AccountLock;
    }

    /// <summary>
    /// Game-thread bridge. HTTP threads must never mutate live game state directly: run that work through
    /// <see cref="RunAsync{T}(Func{T}, int)"/> (snapshot DTOs inside the delegate, never return live objects).
    /// </summary>
    public static class WebAdminGame
    {
        /// <summary>Hold while editing Envir.Edit (system data) and calling Envir.Edit.SaveDB().</summary>
        public static readonly object EditLock = new object();

        private static IWebAdminWorld _world;
        private static long _worldStartedTimestamp;
        private static int _hooked;

        internal static IWebAdminWorld World
        {
            get => _world ??= new EnvirWorld();
            set => _world = value;
        }

        /// <summary>
        /// "running" | "stopped" | "starting" | "stopping". A crashed world (see <see cref="WorldCrashed"/>) reports "stopped".
        /// </summary>
        public static string WorldState
        {
            get
            {
                var w = World;
                if (!w.Running) return w.WorkLoopAlive ? "stopping" : "stopped";
                if (IsCrashed(w)) return "stopped";
                return w.QueueOpen ? "running" : "starting";
            }
        }

        /// <summary>
        /// True when the WorkLoop thread died (inner game-loop exception) without Envir.Stop(): Envir.Running is still true but no loop
        /// exists. Nothing runs; Envir.Start() is a no-op in this state, so a "start world" action must call Envir.Main.Stop() first
        /// (safe: the loop thread is already gone).
        /// </summary>
        public static bool WorldCrashed => IsCrashed(World);

        internal static bool IsCrashed(IWebAdminWorld w) => w.Running && !w.WorkLoopAlive && !w.QueueOpen;

        /// <summary>No game loop exists (stopped or crashed): account data may be touched inline under AccountLock.</summary>
        internal static bool IsFullyStopped(IWebAdminWorld w) => !w.WorkLoopAlive && !w.QueueOpen;

        /// <summary>Envir.Main.Running (false when the loop crashed, see <see cref="WorldCrashed"/>).</summary>
        public static bool IsRunning
        {
            get
            {
                var w = World;
                return w.Running && !IsCrashed(w);
            }
        }

        /// <summary>Milliseconds since the world finished starting (0 when not running).</summary>
        public static long WorldUptimeMs
        {
            get
            {
                var started = Interlocked.Read(ref _worldStartedTimestamp);
                if (started == 0 || !World.QueueOpen) return 0;
                return (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        /// <summary>Subscribes the world start/stop bookkeeping (idempotent; called at host start).</summary>
        internal static void EnsureHooks()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;

            WebAdminHooks.WorldStarted += () => Interlocked.Exchange(ref _worldStartedTimestamp, Stopwatch.GetTimestamp());
            WebAdminHooks.WorldStopped += () => Interlocked.Exchange(ref _worldStartedTimestamp, 0);

            // The host may start while the world is already running.
            if (World.QueueOpen) Interlocked.CompareExchange(ref _worldStartedTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        private sealed class WorkState
        {
            public int Value; // 0 pending, 1 running/done, 2 cancelled
        }

        /// <summary>
        /// Runs <paramref name="fn"/> on the game thread. Throws <see cref="WorldNotRunningException"/> (503) when the world loop is not
        /// running (or stops before the work ran) and <see cref="GameThreadTimeoutException"/> (504) on timeout. Exceptions thrown by
        /// <paramref name="fn"/> propagate.
        /// </summary>
        public static async Task<T> RunAsync<T>(Func<T> fn, int timeoutMs = 5000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = new WorkState();

            var queued = World.TryQueue(() =>
            {
                if (Interlocked.CompareExchange(ref state.Value, 1, 0) != 0) return; // cancelled by a timed-out caller

                try
                {
                    tcs.TrySetResult(fn());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, ex => tcs.TrySetException(ex ?? new WorldNotRunningException()));

            if (!queued) throw new WorldNotRunningException(WorldState);

            if (timeoutMs <= 0) timeoutMs = 5000;

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!tcs.Task.IsCompleted)
            {
                var started = Interlocked.CompareExchange(ref state.Value, 2, 0) != 0;
                throw new GameThreadTimeoutException(started);
            }
        }

        public static Task RunAsync(Action fn, int timeoutMs = 5000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));

            return RunAsync(() =>
            {
                fn();
                return true;
            }, timeoutMs);
        }

        /// <summary>
        /// Game thread when the world is running; inline under lock(Envir.AccountLock) when it is fully stopped.
        /// While starting/stopping → <see cref="WorldNotRunningException"/> (503) since account data is in flux.
        /// </summary>
        public static async Task<T> RunOrInlineAsync<T>(Func<T> fn, int timeoutMs = 5000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));

            var w = World;

            if (w.QueueOpen)
            {
                try
                {
                    return await RunAsync(fn, timeoutMs).ConfigureAwait(false);
                }
                catch (WorldNotRunningException)
                {
                    // raced with a stop: fall through to the inline path if the loop is fully gone
                }
            }

            if (!IsFullyStopped(w))
                throw new WorldNotRunningException(WorldState);

            lock (w.AccountLock)
                return fn();
        }

        public static Task RunOrInlineAsync(Action fn, int timeoutMs = 5000)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn));

            return RunOrInlineAsync(() =>
            {
                fn();
                return true;
            }, timeoutMs);
        }

        /// <summary>Queues fire-and-forget work on the game thread; false when the world is not running.</summary>
        public static bool Post(Action fn)
        {
            if (fn == null) return false;

            return World.TryQueue(() =>
            {
                try
                {
                    fn();
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Queued web admin action failed");
                }
            }, null);
        }
    }
}
