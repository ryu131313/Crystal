namespace Server.WebAdmin.Tests
{
    public class RateLimiterTests
    {
        private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

        [Fact]
        public void AllowsUpToLimit_ThenRejects_WithRetryAfter()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now);

            for (var i = 0; i < 3; i++)
                Assert.True(limiter.TryAcquire("login|1.2.3.4", 3, Minute, out _));

            Assert.False(limiter.TryAcquire("login|1.2.3.4", 3, Minute, out var retry));
            Assert.InRange(retry, 1, 60);
        }

        [Fact]
        public void NewWindow_ResetsTheCount()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now);

            Assert.True(limiter.TryAcquire("k", 1, Minute, out _));
            Assert.False(limiter.TryAcquire("k", 1, Minute, out _));

            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.False(limiter.TryAcquire("k", 1, Minute, out var retry));
            Assert.InRange(retry, 29, 31);

            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.True(limiter.TryAcquire("k", 1, Minute, out _));
        }

        [Fact]
        public void Keys_AreIndependent()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now);

            Assert.True(limiter.TryAcquire("login|a", 1, Minute, out _));
            Assert.False(limiter.TryAcquire("login|a", 1, Minute, out _));
            Assert.True(limiter.TryAcquire("login|b", 1, Minute, out _));
        }

        [Fact]
        public void IsExhausted_PeeksWithoutCounting_AndHitCounts()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now);
            var window = TimeSpan.FromMinutes(15);

            Assert.False(limiter.IsExhausted("fail|gm", 2, window, out _));
            Assert.False(limiter.IsExhausted("fail|gm", 2, window, out _));

            limiter.Hit("fail|gm", window);
            Assert.False(limiter.IsExhausted("fail|gm", 2, window, out _));

            limiter.Hit("fail|gm", window);
            Assert.True(limiter.IsExhausted("fail|gm", 2, window, out var retry));
            Assert.True(retry > 0);

            limiter.Reset("fail|gm");
            Assert.False(limiter.IsExhausted("fail|gm", 2, window, out _));
        }

        [Fact]
        public void IsExhausted_ClearsWhenWindowPasses()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now);

            limiter.Hit("x", Minute);
            Assert.True(limiter.IsExhausted("x", 1, Minute, out _));

            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.False(limiter.IsExhausted("x", 1, Minute, out _));
        }

        [Fact]
        public void ZeroLimit_MeansUnlimited()
        {
            var limiter = new WebAdminRateLimiter(new TestClock().Now);
            for (var i = 0; i < 100; i++)
                Assert.True(limiter.TryAcquire("k", 0, Minute, out _));
        }

        [Fact]
        public void Sweep_RemovesStaleCounters()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now);

            limiter.TryAcquire("a", 5, Minute, out _);
            limiter.TryAcquire("b", 5, Minute, out _);
            Assert.Equal(2, limiter.KeyCount);

            clock.Advance(TimeSpan.FromHours(2));
            limiter.Sweep(TimeSpan.FromHours(1));
            Assert.Equal(0, limiter.KeyCount);
        }

        [Fact]
        public void FullKeyTable_FailsClosed()
        {
            var clock = new TestClock();
            var limiter = new WebAdminRateLimiter(clock.Now, maxKeys: 16);

            for (var i = 0; i < 16; i++)
                Assert.True(limiter.TryAcquire("k" + i, 5, Minute, out _));

            Assert.False(limiter.TryAcquire("overflow", 5, Minute, out _));
        }
    }
}
