namespace Server.WebAdmin.Tests
{
    /// <summary>Fake world: no Envir/Settings involved.</summary>
    internal sealed class FakeWorld : IWebAdminWorld
    {
        public bool Running { get; set; }
        public bool QueueOpen { get; set; }
        public bool WorkLoopAlive { get; set; }

        /// <summary>When true, queued work runs immediately (simulating a fast game tick); otherwise it waits in <see cref="Pending"/>.</summary>
        public bool RunImmediately { get; set; }

        public List<(Action Run, Action<Exception> Dropped)> Pending { get; } = new List<(Action Run, Action<Exception> Dropped)>();

        public object AccountLock { get; } = new object();

        public bool TryQueue(Action run, Action<Exception> dropped)
        {
            if (!QueueOpen || !Running) return false;

            if (RunImmediately) run();
            else Pending.Add((run, dropped));

            return true;
        }

        public static FakeWorld Stopped() => new FakeWorld();

        public static FakeWorld RunningWorld(bool runImmediately = true) =>
            new FakeWorld { Running = true, QueueOpen = true, WorkLoopAlive = true, RunImmediately = runImmediately };
    }

    /// <summary>Test classes that swap process-wide statics (WebAdminGame.World, the WebAdminAdmins snapshot) run serially.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class WebAdminGlobalsCollection
    {
        public const string Name = "WebAdmin globals";
    }

    // These tests swap the process-wide WebAdminGame.World, so they must not run in parallel with each other.
    [Collection(WebAdminGlobalsCollection.Name)]
    public class GameBridgeTests : IDisposable
    {
        public void Dispose()
        {
            WebAdminGame.World = null; // lazily recreated as the real Envir adapter
        }

        [Fact]
        public async Task RunAsync_Throws_WorldNotRunning_WhenStopped()
        {
            WebAdminGame.World = FakeWorld.Stopped();

            var ex = await Assert.ThrowsAsync<WorldNotRunningException>(() => WebAdminGame.RunAsync(() => 42));
            Assert.Equal(503, ex.Status);

            await Assert.ThrowsAsync<WorldNotRunningException>(() => WebAdminGame.RunAsync(() => { }));
        }

        [Fact]
        public async Task RunAsync_Throws_WorldNotRunning_WhileStarting()
        {
            WebAdminGame.World = new FakeWorld { Running = true, QueueOpen = false, WorkLoopAlive = true };

            Assert.Equal("starting", WebAdminGame.WorldState);
            await Assert.ThrowsAsync<WorldNotRunningException>(() => WebAdminGame.RunAsync(() => 1));
        }

        [Fact]
        public async Task RunAsync_ReturnsResult_FromGameThread()
        {
            WebAdminGame.World = FakeWorld.RunningWorld();

            Assert.Equal(42, await WebAdminGame.RunAsync(() => 42));
        }

        [Fact]
        public async Task RunAsync_PropagatesHandlerExceptions()
        {
            WebAdminGame.World = FakeWorld.RunningWorld();

            await Assert.ThrowsAsync<InvalidOperationException>(() => WebAdminGame.RunAsync<int>(() => throw new InvalidOperationException("boom")));
        }

        [Fact]
        public async Task RunAsync_TimesOut_AndCancelsWorkThatNeverStarted()
        {
            var world = FakeWorld.RunningWorld(runImmediately: false);
            WebAdminGame.World = world;
            var ran = false;

            var ex = await Assert.ThrowsAsync<GameThreadTimeoutException>(() => WebAdminGame.RunAsync(() => ran = true, 50));
            Assert.False(ex.Started);
            Assert.Equal(504, ex.Status);

            // The game thread picks it up late: it must not run any more.
            Assert.Single(world.Pending);
            world.Pending[0].Run();
            Assert.False(ran);
        }

        [Fact]
        public async Task PendingWork_FailsWithWorldNotRunning_WhenTheLoopExits()
        {
            var world = FakeWorld.RunningWorld(runImmediately: false);
            WebAdminGame.World = world;

            var task = WebAdminGame.RunAsync(() => 1, 5000);
            Assert.Single(world.Pending);

            world.Pending[0].Dropped(new WorldNotRunningException());

            await Assert.ThrowsAsync<WorldNotRunningException>(() => task);
        }

        [Fact]
        public async Task RunOrInline_RunsInline_WhenFullyStopped()
        {
            WebAdminGame.World = FakeWorld.Stopped();

            Assert.Equal("stopped", WebAdminGame.WorldState);
            Assert.Equal(7, await WebAdminGame.RunOrInlineAsync(() => 7));
        }

        [Fact]
        public async Task RunOrInline_Refuses_WhileStopping()
        {
            WebAdminGame.World = new FakeWorld { Running = false, QueueOpen = false, WorkLoopAlive = true };

            Assert.Equal("stopping", WebAdminGame.WorldState);
            await Assert.ThrowsAsync<WorldNotRunningException>(() => WebAdminGame.RunOrInlineAsync(() => 7));
        }

        [Fact]
        public async Task CrashedWorld_ReportsStopped_AndRunsInline()
        {
            // Inner WorkLoop exception: the thread exited but Envir.Running was never cleared.
            WebAdminGame.World = new FakeWorld { Running = true, QueueOpen = false, WorkLoopAlive = false };

            Assert.Equal("stopped", WebAdminGame.WorldState);
            Assert.True(WebAdminGame.WorldCrashed);
            Assert.False(WebAdminGame.IsRunning);
            await Assert.ThrowsAsync<WorldNotRunningException>(() => WebAdminGame.RunAsync(() => 1));
            Assert.Equal(5, await WebAdminGame.RunOrInlineAsync(() => 5));
        }

        [Fact]
        public async Task RunOrInline_UsesGameThread_WhenRunning()
        {
            WebAdminGame.World = FakeWorld.RunningWorld();

            Assert.Equal("running", WebAdminGame.WorldState);
            Assert.True(WebAdminGame.IsRunning);
            Assert.Equal(3, await WebAdminGame.RunOrInlineAsync(() => 3));
        }

        [Fact]
        public void Post_ReturnsFalse_WhenStopped_AndSwallowsNothingWhenRunning()
        {
            WebAdminGame.World = FakeWorld.Stopped();
            Assert.False(WebAdminGame.Post(() => { }));

            var world = FakeWorld.RunningWorld();
            WebAdminGame.World = world;
            var ran = false;
            Assert.True(WebAdminGame.Post(() => ran = true));
            Assert.True(ran);
        }
    }
}
