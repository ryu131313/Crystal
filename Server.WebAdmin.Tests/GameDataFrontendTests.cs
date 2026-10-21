using System.Text.RegularExpressions;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Static contract checks for the Game Data section module (B9, wwwroot\admin\admin-gamedata.js / .css): the module
    /// replaces the foundation stub, talks only to the routes of the game-data / server-control contract, never uses a raw
    /// fetch (portalApi carries the CSRF header), never writes data through innerHTML, and keeps its CSS scoped.
    /// </summary>
    public class GameDataFrontendTests
    {
        private static readonly string[] AllowedApi =
        {
            "/api/admin/db/",                   // + {endpoint}, ?dryRun / ?live (PUT)
            "/api/admin/db/tables",
            "/api/admin/db/dragon",
            "/api/admin/db/rename-propagate",
            "/api/admin/database/status",
            "/api/admin/database/save",
            "/api/admin/database/reload-edit",
            "/api/admin/server/reboot",
            "/api/admin/server/reload/drops",
            "/api/admin/server/reload/npcs",
            "/api/admin/livedb/status",         // LiveDb ([LiveDb] Enabled): batch save + live apply
            "/api/admin/livedb/batch",          // + ?dryRun=1
            "/api/admin/gm/action",             // Maps row right-click: randomteleport on the live map (2026-10-21)
        };

        private static string AdminDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Server.MirForms", "wwwroot", "admin");
                if (File.Exists(Path.Combine(candidate, "admin-gamedata.js"))) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Server.MirForms\\wwwroot\\admin\\admin-gamedata.js not found above " + AppContext.BaseDirectory);
        }

        private static string Js() => File.ReadAllText(Path.Combine(AdminDir(), "admin-gamedata.js"));

        private static string Css() => File.ReadAllText(Path.Combine(AdminDir(), "admin-gamedata.css"));

        [Fact]
        public void Module_ReplacesStub_AndRegistersTheSection()
        {
            var js = Js();
            Assert.DoesNotContain("AdminSections.placeholder", js);
            Assert.Contains("var SECTION = 'gamedata';", js);
            Assert.Contains("global.AdminSections.register(SECTION,", js);
            Assert.Contains("global.portalRegisterDeepLink(SECTION,", js);
        }

        [Theory]
        [InlineData("items")]
        [InlineData("monsters")]
        [InlineData("magic")]
        [InlineData("npcs")]
        [InlineData("quests")]
        [InlineData("maps")]
        [InlineData("gameshop")]
        [InlineData("conquests")]
        [InlineData("dragon")]
        public void SubNavigation_HasEveryTable(string id)
        {
            Assert.Matches(new Regex(@"\{ id: '" + Regex.Escape(id) + @"', label: '[^']+', kind: '(grid|master|form)'"), Js());
        }

        [Theory]
        [InlineData("mapsafezones")]
        [InlineData("maprespawns")]
        [InlineData("mapmovements")]
        [InlineData("mapminezones")]
        [InlineData("conquestguards")]
        [InlineData("conquestgates")]
        [InlineData("conquestwalls")]
        [InlineData("conquestsieges")]
        [InlineData("conquestflags")]
        [InlineData("conquestcontrolpoints")]
        public void MasterTabs_HaveEveryChildTable(string endpoint)
        {
            Assert.Contains("{ endpoint: '" + endpoint + "', label: ", Js());
        }

        [Fact]
        public void ApiCalls_StayWithinTheContract()
        {
            var urls = Regex.Matches(Js(), @"'(/api/[^']*)'").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(urls);
            var unknown = urls.Where(u => !AllowedApi.Contains(u)).ToList();
            Assert.True(unknown.Count == 0, "Routes outside the B8/B1 contract: " + string.Join(", ", unknown));
        }

        [Fact]
        public void SaveFlow_UsesDryRunLiveAndDatabaseSave()
        {
            var js = Js();
            Assert.Contains("q.push('dryRun=1')", js);
            Assert.Contains("q.push('live=1')", js);
            Assert.Contains("method: 'PUT'", js);
            Assert.Contains("'/api/admin/database/save'", js);
            Assert.Contains("persistLegacy:", js);
        }

        [Fact]
        public void NoRawFetch_EveryRequestGoesThroughPortalApi()
        {
            // portalApi adds the session cookie and the X-MirCraft-CSRF header; a bare fetch( would skip CSRF on mutations.
            var bare = Regex.Matches(Js(), @"(?<![\w.$])fetch\s*\(");
            Assert.Empty(bare);
        }

        [Fact]
        public void InnerHtml_IsOnlyEverCleared()
        {
            var assignments = Regex.Matches(Js(), @"innerHTML\s*=\s*([^;]+);").Select(m => m.Groups[1].Value.Trim()).ToList();
            Assert.All(assignments, a => Assert.Equal("''", a));
            Assert.DoesNotContain("insertAdjacentHTML", Js());
            Assert.DoesNotContain("outerHTML", Js());
        }

        // ---- Drops tab (admin-drops.js, drops plan phase 2) ----

        private static readonly string[] DropsAllowedApi =
        {
            "/api/admin/drops",                          // + /lists, /monster?index=, /list?id=, /save, /create, /copy, /delete-disabled
            "/api/admin/scripts/drops/class-toggle",     // the class drop tool (all lists), moved here from the Scripts editor
        };

        private static string DropsJs() => File.ReadAllText(Path.Combine(AdminDir(), "admin-drops.js"));

        [Fact]
        public void Drops_TabIsWiredToTheDropsModule()
        {
            var js = Js();
            Assert.Contains("{ id: 'drops', label: 'Drops', kind: 'drops' }", js);
            Assert.Contains("global.DropsEditor.mount(pane)", js);
            Assert.Contains("global.DropsEditor = {", DropsJs());
            Assert.Contains("<script src=\"admin-drops.js\"></script>", File.ReadAllText(Path.Combine(AdminDir(), "index.html")));
        }

        [Fact]
        public void Drops_ApiCallsStayWithinTheContract()
        {
            var urls = Regex.Matches(DropsJs(), @"'(/api/[^']*)'").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(urls);
            var unknown = urls.Where(u => !DropsAllowedApi.Contains(u)).ToList();
            Assert.True(unknown.Count == 0, "Routes outside the drops contract: " + string.Join(", ", unknown));
        }

        [Fact]
        public void Drops_WritesUseDryRunAndPortalApi()
        {
            var js = DropsJs();
            Assert.Contains("dryRun: true", js);
            Assert.Contains("global.portalApi(url, { method: 'POST'", js);
            Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
        }

        [Fact]
        public void Drops_InnerHtml_IsNeverUsed()
        {
            var js = DropsJs();
            Assert.DoesNotContain("innerHTML", js);
            Assert.DoesNotContain("insertAdjacentHTML", js);
            Assert.DoesNotContain("outerHTML", js);
        }

        [Fact]
        public void Css_IsScopedToTheSection()
        {
            var css = Regex.Replace(Css(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            var selectors = Regex.Matches(css, @"([^{}]+)\{[^{}]*\}")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("@", StringComparison.Ordinal))
                .SelectMany(s => s.Split(','))
                .Select(s => s.Trim())
                .ToList();
            Assert.NotEmpty(selectors);
            var unscoped = selectors.Where(s => !s.StartsWith(".gd-", StringComparison.Ordinal) && !s.StartsWith("#section-gamedata", StringComparison.Ordinal)).ToList();
            Assert.True(unscoped.Count == 0, "Unscoped selectors: " + string.Join(" | ", unscoped));
        }
    }
}
