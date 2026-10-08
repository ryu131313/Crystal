namespace Server.WebAdmin.Tests
{
    public class RouterTests
    {
        private static Task Noop(WebAdminRequest req) => Task.CompletedTask;

        [Fact]
        public void LiteralRoute_Matches_CaseInsensitive()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/status", Noop);

            var match = router.Match("GET", "/API/Admin/Status");

            Assert.True(match.Success);
            Assert.Equal("/api/admin/status", match.Route.Pattern);
        }

        [Fact]
        public void AnyParameter_CapturesDecodedSegment()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/characters/{name}", Noop);

            var match = router.Match("GET", "/api/admin/characters/Bob%20The%2FGreat");

            Assert.True(match.Success);
            Assert.Equal("Bob The/Great", match.Values["name"]);
            Assert.Equal("Bob The/Great", match.Values["NAME"]);
            Assert.Null(match.Values["missing"]);
        }

        [Fact]
        public void IntParameter_OnlyMatchesIntegers()
        {
            var router = new WebAdminRouter();
            router.Post("/api/admin/accounts/{index:int}/ban", Noop);

            var ok = router.Match("POST", "/api/admin/accounts/42/ban");
            Assert.True(ok.Success);
            Assert.Equal("42", ok.Values["index"]);

            var negative = router.Match("POST", "/api/admin/accounts/-3/ban");
            Assert.True(negative.Success);

            var bad = router.Match("POST", "/api/admin/accounts/abc/ban");
            Assert.False(bad.Success);
            Assert.False(bad.MethodNotAllowed);

            var overflow = router.Match("POST", "/api/admin/accounts/99999999999/ban");
            Assert.False(overflow.Success);
        }

        [Fact]
        public void MethodMismatch_ReportsMethodNotAllowed()
        {
            var router = new WebAdminRouter();
            router.Post("/api/admin/server/start", Noop);
            router.Put("/api/admin/server/start", Noop);

            var match = router.Match("GET", "/api/admin/server/start");

            Assert.False(match.Success);
            Assert.True(match.MethodNotAllowed);
            Assert.Contains("POST", match.AllowedMethods);
            Assert.Contains("PUT", match.AllowedMethods);
        }

        [Fact]
        public void UnknownPath_IsNotFound_NotMethodNotAllowed()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/status", Noop);

            var match = router.Match("GET", "/api/admin/nothing-here");

            Assert.False(match.Success);
            Assert.False(match.MethodNotAllowed);
        }

        [Theory]
        [InlineData("/api/admin/status/")]
        [InlineData("/api/admin/status")]
        [InlineData("/api/admin/status?x=1")]
        public void TrailingSlashAndQuery_AreIgnored(string path)
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/status/", Noop);

            Assert.True(router.Match("GET", path).Success);
        }

        [Fact]
        public void SegmentCountMustMatch()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/accounts/{index:int}", Noop);

            Assert.False(router.Match("GET", "/api/admin/accounts").Success);
            Assert.False(router.Match("GET", "/api/admin/accounts/1/extra").Success);
        }

        [Fact]
        public void LiteralWinsOverParameter_RegardlessOfOrder()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/db/{endpoint}", Noop);
            router.Get("/api/admin/db/tables", Noop);

            Assert.Equal("/api/admin/db/tables", router.Match("GET", "/api/admin/db/tables").Route.Pattern);
            Assert.Equal("/api/admin/db/{endpoint}", router.Match("GET", "/api/admin/db/items").Route.Pattern);
        }

        [Fact]
        public void IntWinsOverAny()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/x/{name}", Noop);
            router.Get("/api/admin/x/{id:int}", Noop);

            Assert.Equal("/api/admin/x/{id:int}", router.Match("GET", "/api/admin/x/5").Route.Pattern);
            Assert.Equal("/api/admin/x/{name}", router.Match("GET", "/api/admin/x/five").Route.Pattern);
        }

        [Fact]
        public void DuplicateRegistration_Throws()
        {
            var router = new WebAdminRouter();
            router.Get("/api/admin/accounts/{index:int}", Noop);

            Assert.Throws<InvalidOperationException>(() => router.Get("/api/admin/accounts/{other:int}", Noop));

            // Same shape but a different method is fine.
            router.Post("/api/admin/accounts/{index:int}", Noop);
        }

        [Fact]
        public void MalformedPatterns_Throw()
        {
            var router = new WebAdminRouter();
            Assert.Throws<ArgumentException>(() => router.Get("no-leading-slash", Noop));
            Assert.Throws<ArgumentException>(() => router.Get("/api/{x:guid}", Noop));
            Assert.Throws<ArgumentException>(() => router.Get("/api/{x}/{x}", Noop));
            Assert.Throws<ArgumentException>(() => router.Get("/api/a{b", Noop));
        }

        [Fact]
        public void DefaultAuth_AdminUnlessAuthPrefix()
        {
            var router = new WebAdminRouter();
            var admin = router.Get("/api/admin/status", Noop);
            var auth = router.Post("/api/auth/login", Noop);
            var explicitAnon = router.Get("/api/admin/public-thing", Noop, RouteAuth.Anonymous);

            Assert.Equal(RouteAuth.Admin, admin.Auth);
            Assert.Equal(RouteAuth.Anonymous, auth.Auth);
            Assert.Equal(RouteAuth.Anonymous, explicitAnon.Auth);
        }

        [Fact]
        public void RegisterAll_DiscoversFoundationRoutes()
        {
            var router = new WebAdminRouter();
            var count = WebAdminRouter.RegisterAll(router, typeof(WebAdminRouter).Assembly);

            Assert.True(count >= 2);
            Assert.True(router.Match("GET", "/api/admin/status").Success);
            Assert.True(router.Match("POST", "/api/auth/login").Success);
            Assert.True(router.Match("GET", "/api/auth/me").Success);
            Assert.Equal(RouteAuth.Anonymous, router.Match("GET", "/api/auth/me").Route.Auth);
        }

        [Fact]
        public void QueryParsing_DecodesAndIsCaseInsensitive()
        {
            var q = WebAdminQuery.Parse("?a=1&B=x%20y&c=hello+world&flag&n=abc&multi=1&multi=2");

            Assert.Equal(1, q.Int("a", 0));
            Assert.Equal("x y", q["b"]);
            Assert.Equal("hello world", q["C"]);
            Assert.True(q.Bool("flag", false));
            Assert.Equal(7, q.Int("n", 7));
            Assert.Equal(9, q.Int("absent", 9));
            Assert.Null(q["absent"]);
            Assert.Equal(new[] { "1", "2" }, q.GetAll("multi").ToArray());
            Assert.False(WebAdminQuery.Parse("x=0").Bool("x", true));
        }
    }
}
