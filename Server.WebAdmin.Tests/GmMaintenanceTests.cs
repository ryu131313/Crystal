using Server.MirDatabase;

namespace Server.WebAdmin.Tests
{
    internal sealed class FakeMaintenanceWorld : IMaintenanceWorld
    {
        public bool IsRunning { get; set; } = true;
        public List<string> Announcements { get; } = new List<string>();
        public int KickCalls { get; private set; }
        public int KickResult { get; set; } = 3;

        public void Announce(string text) => Announcements.Add(text);

        public void KickNonAdmins(Action<int> done)
        {
            KickCalls++;
            done?.Invoke(KickResult);
        }
    }

    public class GmMaintenanceTests
    {
        private static (MaintenanceController controller, FakeMaintenanceWorld world, TestClock clock, Func<Func<AccountInfo, string>> gate) Create()
        {
            var world = new FakeMaintenanceWorld();
            var clock = new TestClock();
            Func<AccountInfo, string> installed = null;
            var controller = new MaintenanceController(world, g => installed = g, clock.Now, useTimer: false);
            return (controller, world, clock, () => installed);
        }

        [Fact]
        public void Gate_RefusesNonAdminsOnlyWhileActive()
        {
            var (c, _, _, gate) = Create();
            var player = new AccountInfo { AccountID = "player1" };
            var admin = new AccountInfo { AccountID = "gm1", AdminAccount = true };

            Assert.Null(c.Gate(player));
            Assert.Null(gate());

            c.Start("gm1", "Patching", false, 0);

            Assert.NotNull(gate());
            Assert.Equal("Patching", gate()(player));
            Assert.Null(gate()(admin));
            Assert.Null(c.Gate(null));
            Assert.Equal(1, c.Snapshot().RefusedLogins);

            c.Lift("gm1");
            Assert.Null(gate());
            Assert.Null(c.Gate(player));
        }

        [Fact]
        public void Start_WithoutKick_AnnouncesAndKeepsPlayers()
        {
            var (c, world, clock, _) = Create();

            var s = c.Start("gm1", null, false, 30);

            Assert.True(s.Active);
            Assert.Equal(MaintenanceController.DefaultMessage, s.Message);
            Assert.Equal("gm1", s.By);
            Assert.Equal(clock.UtcNow, s.Since);
            Assert.Null(s.KickScheduledUtc); // minutes only matter with a kick
            Assert.Single(world.Announcements);
            Assert.Equal(0, world.KickCalls);
        }

        [Fact]
        public void Start_TwiceThrows()
        {
            var (c, _, _, _) = Create();
            c.Start("gm1", "x", false, 0);
            Assert.Throws<InvalidOperationException>(() => c.Start("gm2", "y", false, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Create().controller.Start("gm", "m", true, 61));
        }

        [Fact]
        public void Start_ImmediateKick()
        {
            var (c, world, clock, _) = Create();

            var s = c.Start("gm1", "Now", true, 0);

            Assert.Equal(1, world.KickCalls);
            Assert.Equal(3, s.KickedCount);
            Assert.Equal(clock.UtcNow, s.KickedUtc);
            Assert.Null(s.KickScheduledUtc);
        }

        [Fact]
        public void Countdown_AnnouncesDuePointsThenKicks()
        {
            var (c, world, clock, _) = Create();

            var s = c.Start("gm1", "Patch", true, 5);
            Assert.Equal(clock.UtcNow.AddMinutes(5), s.KickScheduledUtc);
            Assert.Equal(300, s.SecondsUntilKick);
            Assert.Single(world.Announcements); // "begins in 5 minutes"
            Assert.Contains("5 minutes", world.Announcements[0]);

            c.Tick();
            Assert.Single(world.Announcements); // 300 s point skipped: it equals the start

            clock.Advance(TimeSpan.FromSeconds(61));     // 239 s left → 240 s point due
            c.Tick();
            Assert.Equal(2, world.Announcements.Count);
            Assert.Contains("4 minutes", world.Announcements[1]);

            clock.Advance(TimeSpan.FromSeconds(200));    // 39 s left → 180/120/60 due at once: only the latest (60 s) is announced
            c.Tick();
            Assert.Equal(3, world.Announcements.Count);
            Assert.Contains("1 minute", world.Announcements[2]);

            c.Tick();
            Assert.Equal(3, world.Announcements.Count);  // nothing new
            Assert.Equal(0, world.KickCalls);

            clock.Advance(TimeSpan.FromSeconds(40));
            c.Tick();
            Assert.Equal(1, world.KickCalls);
            Assert.Null(c.Snapshot().KickScheduledUtc);
            Assert.Equal(3, c.Snapshot().KickedCount);
            Assert.True(c.Snapshot().Active); // the gate stays until lifted

            c.Tick();
            Assert.Equal(1, world.KickCalls);
        }

        [Fact]
        public void Lift_CancelsThePendingKick()
        {
            var (c, world, clock, _) = Create();

            c.Start("gm1", "Patch", true, 10);
            var lifted = c.Lift("gm2");

            Assert.False(lifted.Active);
            Assert.Null(lifted.KickScheduledUtc);
            Assert.Equal("gm2", lifted.LiftedBy);
            Assert.Equal(clock.UtcNow, lifted.LiftedUtc);
            Assert.Contains("over", world.Announcements.Last());

            clock.Advance(TimeSpan.FromMinutes(11));
            c.Tick();
            Assert.Equal(0, world.KickCalls);

            var count = world.Announcements.Count;
            c.Lift("gm2"); // idempotent, no second announcement
            Assert.Equal(count, world.Announcements.Count);
        }

        [Fact]
        public void Countdown_WithAnnounceOff_KicksSilently()
        {
            var (c, world, clock, _) = Create();

            c.Start("gm1", "Patch", true, 10, announce: false);
            Assert.Empty(world.Announcements);

            for (var i = 0; i < 20; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                c.Tick();
            }

            Assert.Empty(world.Announcements);  // no countdown points and no final "has started" message
            Assert.Equal(1, world.KickCalls);   // the disconnect still happens
            Assert.True(c.Snapshot().Active);
        }

        [Fact]
        public void WorldStopped_NoAnnouncementsOrKicks()
        {
            var (c, world, clock, _) = Create();
            world.IsRunning = false;

            c.Start("gm1", "Patch", true, 1);
            clock.Advance(TimeSpan.FromMinutes(2));
            c.Tick();

            Assert.Empty(world.Announcements);
            Assert.Equal(0, world.KickCalls);
            Assert.NotNull(c.Snapshot().KickedUtc);
            Assert.True(c.Snapshot().Active);
        }

        [Theory]
        [InlineData(3600, "1 hour")]
        [InlineData(7200, "2 hours")]
        [InlineData(2700, "45 minutes")]
        [InlineData(60, "1 minute")]
        [InlineData(30, "30 seconds")]
        [InlineData(1, "1 second")]
        public void DescribeSeconds(int seconds, string expected)
        {
            Assert.Equal(expected, MaintenanceController.DescribeSeconds(seconds));
        }
    }

    [Collection(WebAdminGlobalsCollection.Name)]
    public class GmMaintenanceGlobalsTests : IDisposable
    {
        private readonly Func<AccountInfo, string> _savedGate = WebAdminHooks.LoginGate;
        private readonly MaintenanceController _savedController = WebAdminMaintenance.Controller;

        public void Dispose()
        {
            WebAdminMaintenance.Controller = _savedController;
            WebAdminHooks.LoginGate = _savedGate;
        }

        [Fact]
        public void Facade_InstallsAndRemovesOnlyItsOwnLoginGate()
        {
            var world = new FakeMaintenanceWorld { IsRunning = false };
            var controller = WebAdminMaintenance.CreateController(world, null, useTimer: false);
            WebAdminMaintenance.Controller = controller;
            WebAdminHooks.LoginGate = null;

            WebAdminMaintenance.Start("gm1", "Down", false, 0);
            Assert.Same(controller.GateFunction, WebAdminHooks.LoginGate);
            Assert.Equal("Down", WebAdminHooks.CheckLoginGate(new AccountInfo { AccountID = "p" }));
            Assert.Null(WebAdminHooks.CheckLoginGate(new AccountInfo { AccountID = "a", AdminAccount = true }));

            WebAdminMaintenance.Lift("gm1");
            Assert.Null(WebAdminHooks.LoginGate);

            // A gate someone else installed is left alone by Lift.
            Func<AccountInfo, string> other = _ => "other";
            WebAdminHooks.LoginGate = other;
            WebAdminMaintenance.Lift("gm1");
            Assert.Same(other, WebAdminHooks.LoginGate);
        }
    }
}
