using Server.MirEnvir.Storage;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// P9-12 (offline account cache, review H1): every web admin write invalidates every cached account, on the one path all of
    /// them take. A mutating admin route runs with <see cref="WebAdminGame.InMutatingRequest"/> set
    /// (<see cref="WebAdminHost.InvokeRouteAsync"/>), and each delegate it runs through RunAsync / RunOrInlineAsync / Post bumps
    /// <see cref="AccountSaveCache.Epoch"/> right after it ran, on the thread that mutated.
    /// </summary>
    [Collection(WebAdminGlobalsCollection.Name)]
    public class AccountCacheHookTests : IDisposable
    {
        public void Dispose()
        {
            WebAdminGame.World = null;
        }

        private static Task Request(bool mutating, Func<Task> handler, RouteAuth auth = RouteAuth.Admin) =>
            WebAdminHost.InvokeRouteAsync(auth, mutating, handler);

        [Fact]
        public async Task Admin_write_bumps_the_epoch_after_each_game_thread_delegate()
        {
            WebAdminGame.World = FakeWorld.RunningWorld();
            var before = AccountSaveCache.Epoch;
            var seenInside = -1;

            await Request(true, async () =>
            {
                await WebAdminGame.RunAsync(() => { seenInside = AccountSaveCache.Epoch; });
                await WebAdminGame.RunOrInlineAsync(() => 1);
                Assert.True(WebAdminGame.Post(() => { }));
            });

            Assert.Equal(before, seenInside); // after the mutation, never before it
            Assert.Equal(before + 3, AccountSaveCache.Epoch);
            Assert.False(WebAdminGame.InMutatingRequest); // scoped to the request
        }

        [Fact]
        public async Task Admin_write_bumps_inline_while_stopped_and_when_the_delegate_throws()
        {
            WebAdminGame.World = FakeWorld.Stopped();
            var before = AccountSaveCache.Epoch;

            await Request(true, async () =>
            {
                await WebAdminGame.RunOrInlineAsync(() => 1);
                await Assert.ThrowsAsync<InvalidOperationException>(() => WebAdminGame.RunOrInlineAsync<int>(() => throw new InvalidOperationException()));
            });
            Assert.Equal(before + 2, AccountSaveCache.Epoch);

            WebAdminGame.World = FakeWorld.RunningWorld();
            before = AccountSaveCache.Epoch;
            await Request(true, () => Assert.ThrowsAsync<InvalidOperationException>(() => WebAdminGame.RunAsync<int>(() => throw new InvalidOperationException())));
            Assert.Equal(before + 1, AccountSaveCache.Epoch);
        }

        [Fact]
        public async Task Reads_and_anonymous_posts_do_not_bump()
        {
            WebAdminGame.World = FakeWorld.RunningWorld();
            var before = AccountSaveCache.Epoch;

            await Request(false, async () =>
            {
                await WebAdminGame.RunAsync(() => 1);
                await WebAdminGame.RunOrInlineAsync(() => 1);
                WebAdminGame.Post(() => { });
            });
            await Request(true, () => WebAdminGame.RunAsync(() => 1), RouteAuth.Anonymous);
            await WebAdminGame.RunAsync(() => 1); // no request at all (sampler, hooks)

            Assert.Equal(before, AccountSaveCache.Epoch);
        }

        /// <summary>
        /// Review L1: a timer or Task.Run started inside an admin write captures the request's context; once the request has
        /// ended its callbacks (restart / maintenance countdown ticks) no longer count as admin writes.
        /// </summary>
        [Fact]
        public async Task Callbacks_started_by_an_admin_write_do_not_bump_after_the_request()
        {
            WebAdminGame.World = FakeWorld.RunningWorld();
            using var release = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            System.Threading.Timer timer = null;
            Task later = null;
            var insideDuring = false;
            var insideTimer = true;
            var insideTask = true;

            await Request(true, () =>
            {
                insideDuring = WebAdminGame.InMutatingRequest;
                timer = new System.Threading.Timer(_ =>
                {
                    insideTimer = WebAdminGame.InMutatingRequest;
                    WebAdminGame.Post(() => { });
                    done.Set();
                }, null, Timeout.Infinite, Timeout.Infinite);
                later = Task.Run(() => { release.Wait(5000); insideTask = WebAdminGame.InMutatingRequest; });
                return Task.CompletedTask;
            });

            var before = AccountSaveCache.Epoch;
            timer.Change(0, Timeout.Infinite);
            Assert.True(done.Wait(5000));
            release.Set();
            await later;
            timer.Dispose();

            Assert.True(insideDuring);
            Assert.False(insideTimer);
            Assert.False(insideTask);
            Assert.Equal(before, AccountSaveCache.Epoch);
        }

        /// <summary>
        /// Every route that can write user data is an admin route with a mutating method, so it runs with the flag: the only
        /// mutating routes that are not admin routes are the sign-in/out endpoints, which read accounts only.
        /// </summary>
        [Fact]
        public void Every_mutating_route_is_an_admin_route_except_sign_in()
        {
            var router = RouteRegistrationTests.RegisterEveryModule();
            var mutating = router.Routes.Where(r => r.Method is "POST" or "PUT" or "PATCH" or "DELETE").ToList();
            Assert.Contains(mutating, r => r.Pattern.StartsWith("/api/admin/accounts", StringComparison.Ordinal));
            Assert.Contains(mutating, r => r.Pattern.StartsWith("/api/admin/characters", StringComparison.Ordinal));

            var notAdmin = mutating.Where(r => r.Auth != RouteAuth.Admin).Select(r => r.Method + " " + r.Pattern).OrderBy(s => s).ToList();
            Assert.Equal(new[] { "POST /api/auth/login", "POST /api/auth/logout" }, notAdmin);
        }

        /// <summary>The host dispatches every matched route through <see cref="WebAdminHost.InvokeRouteAsync"/> (source check).</summary>
        [Fact]
        public void Host_dispatches_every_route_through_InvokeRouteAsync()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string source = null;
            for (var i = 0; i < 10 && dir != null && source == null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Server", "WebAdmin", "WebAdminHost.cs");
                if (File.Exists(candidate)) source = candidate;
            }
            if (source == null) return; // no source tree next to the test output

            var text = File.ReadAllText(source);
            Assert.Equal(1, CountOf(text, ".Handler("));
            Assert.Contains("await InvokeRouteAsync(match.Route.Auth, req.IsMutating, () => match.Route.Handler(req))", text);
        }

        private static int CountOf(string text, string value)
        {
            var n = 0;
            for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
