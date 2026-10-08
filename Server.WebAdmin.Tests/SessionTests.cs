namespace Server.WebAdmin.Tests
{
    public class SessionTests
    {
        private static (WebAdminSessionStore store, TestClock clock) NewStore(double hours = 8)
        {
            var clock = new TestClock();
            return (new WebAdminSessionStore(TimeSpan.FromHours(hours), clock.Now), clock);
        }

        [Fact]
        public void Create_ThenValidate_ReturnsSameSession()
        {
            var (store, _) = NewStore();
            var s = store.Create(7, "GameMaster", "127.0.0.1", "ua");

            var found = store.Validate(s.Token);

            Assert.Same(s, found);
            Assert.Equal(7, found.AccountIndex);
            Assert.Equal("GameMaster", found.AccountId);
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void Tokens_AreRandomHex_AndDistinct()
        {
            var (store, _) = NewStore();
            var a = store.Create(1, "a", "ip");
            var b = store.Create(1, "a", "ip");

            Assert.Equal(64, a.Token.Length);
            Assert.Equal(64, a.Csrf.Length);
            Assert.Matches("^[0-9a-f]+$", a.Token);
            Assert.NotEqual(a.Token, b.Token);
            Assert.NotEqual(a.Csrf, b.Csrf);
            Assert.NotEqual(a.SessionId, b.SessionId);
            Assert.NotEqual(a.Token, a.SessionId);
            Assert.NotEqual(a.Token, a.Csrf);
        }

        [Fact]
        public void UnknownOrEmptyToken_IsRejected()
        {
            var (store, _) = NewStore();
            store.Create(1, "a", "ip");

            Assert.Null(store.Validate(null));
            Assert.Null(store.Validate(string.Empty));
            Assert.Null(store.Validate("deadbeef"));
            Assert.Null(store.Validate(new string('a', 300)));
        }

        [Fact]
        public void Session_Expires_AfterLifetime()
        {
            var (store, clock) = NewStore(8);
            var s = store.Create(1, "a", "ip");

            clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromSeconds(1));

            Assert.Null(store.Validate(s.Token));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void Validate_SlidesExpiry()
        {
            var (store, clock) = NewStore(8);
            var s = store.Create(1, "a", "ip");

            clock.Advance(TimeSpan.FromHours(5));
            Assert.NotNull(store.Validate(s.Token));

            clock.Advance(TimeSpan.FromHours(5));
            Assert.NotNull(store.Validate(s.Token));

            // Without touching, it does not slide.
            clock.Advance(TimeSpan.FromHours(5));
            Assert.NotNull(store.Validate(s.Token, touch: false));
            clock.Advance(TimeSpan.FromHours(4));
            Assert.Null(store.Validate(s.Token));
        }

        [Fact]
        public void Session_HasAbsoluteMaximumAge_EvenWhenPolled()
        {
            var (store, clock) = NewStore(8);
            var s = store.Create(1, "a", "ip");

            // A tab polling every 5 minutes keeps sliding the expiry, but never past CreatedUtc + 3 x Lifetime (24h).
            for (var i = 0; i < 12 * 23; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(5));
                Assert.NotNull(store.Validate(s.Token));
            }

            Assert.True(s.ExpiresUtc <= s.CreatedUtc + TimeSpan.FromHours(24));

            clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
            Assert.Null(store.Validate(s.Token));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void Revoke_BySessionId_AndByAccount()
        {
            var (store, _) = NewStore();
            var a1 = store.Create(1, "a", "ip");
            var a2 = store.Create(1, "a", "ip");
            var b = store.Create(2, "b", "ip");

            Assert.True(store.Revoke(a1.SessionId));
            Assert.False(store.Revoke(a1.SessionId));
            Assert.Null(store.Validate(a1.Token));
            Assert.NotNull(store.Validate(a2.Token));

            Assert.Equal(1, store.RevokeAccount(1));
            Assert.Null(store.Validate(a2.Token));
            Assert.NotNull(store.Validate(b.Token));

            Assert.True(store.RevokeToken(b.Token));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void PerAccountSessions_AreCapped()
        {
            var (store, clock) = NewStore();
            var first = store.Create(1, "a", "ip");

            for (var i = 0; i < WebAdminSessionStore.MaxSessionsPerAccount + 3; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                store.Create(1, "a", "ip");
            }

            Assert.Equal(WebAdminSessionStore.MaxSessionsPerAccount, store.List().Count(s => s.AccountIndex == 1));
            Assert.Null(store.Validate(first.Token));
        }

        [Fact]
        public void Csrf_RequiresHeaderCookieAndSessionToAgree()
        {
            var (store, _) = NewStore();
            var s = store.Create(1, "a", "ip");
            var other = store.Create(2, "b", "ip");

            Assert.True(WebAdminSessionStore.ValidateCsrf(s, s.Csrf, s.Csrf));

            Assert.False(WebAdminSessionStore.ValidateCsrf(s, null, s.Csrf));
            Assert.False(WebAdminSessionStore.ValidateCsrf(s, s.Csrf, null));
            Assert.False(WebAdminSessionStore.ValidateCsrf(s, "", ""));
            Assert.False(WebAdminSessionStore.ValidateCsrf(s, s.Csrf, "x" + s.Csrf));
            Assert.False(WebAdminSessionStore.ValidateCsrf(s, other.Csrf, other.Csrf));
            Assert.False(WebAdminSessionStore.ValidateCsrf(null, s.Csrf, s.Csrf));
        }

        [Fact]
        public void Cookies_BuildAndParse()
        {
            var cookie = WebAdminSessions.BuildCookie(WebAdminSessions.CookieName, "abc", httpOnly: true, maxAgeSeconds: 60);
            Assert.StartsWith("MirCraftAdmin=abc; Path=/", cookie);
            Assert.Contains("HttpOnly", cookie);
            Assert.Contains("SameSite=Strict", cookie);
            Assert.Contains("Max-Age=60", cookie);

            var csrf = WebAdminSessions.BuildCookie(WebAdminSessions.CsrfCookieName, "def", httpOnly: false);
            Assert.DoesNotContain("HttpOnly", csrf);

            Assert.Contains("Max-Age=0", WebAdminSessions.ClearCookie(WebAdminSessions.CookieName, true));

            var header = "theme=dark; MirCraftAdminCsrf=c1; MirCraftAdmin=tok%20en; other=1";
            Assert.Equal("tok en", WebAdminSessions.GetCookie(header, "MirCraftAdmin"));
            Assert.Equal("c1", WebAdminSessions.GetCookie(header, "MirCraftAdminCsrf"));
            Assert.Null(WebAdminSessions.GetCookie(header, "missing"));
            Assert.Null(WebAdminSessions.GetCookie(null, "MirCraftAdmin"));
        }
    }
}
