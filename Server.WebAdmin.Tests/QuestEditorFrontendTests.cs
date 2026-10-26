using System.Text.RegularExpressions;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Static contract checks for the Quests page (wwwroot\admin\admin-quests.js / .css): wired into the shell, talks only to the quest
    /// editor routes (pickers go through DbLookups), never uses a raw fetch (portalApi carries the CSRF header), never uses innerHTML,
    /// and keeps its CSS scoped to the section.
    /// </summary>
    public class QuestEditorFrontendTests
    {
        private static string AdminDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Server.MirForms", "wwwroot", "admin");
                if (File.Exists(Path.Combine(candidate, "admin-quests.js"))) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Server.MirForms\\wwwroot\\admin\\admin-quests.js not found above " + AppContext.BaseDirectory);
        }

        private static string Js() => File.ReadAllText(Path.Combine(AdminDir(), "admin-quests.js"));

        [Fact]
        public void Page_is_registered_in_the_shell()
        {
            var index = File.ReadAllText(Path.Combine(AdminDir(), "index.html"));
            Assert.Contains("data-section=\"quests\"", index);
            Assert.Contains("<section id=\"section-quests\" class=\"admin-section hidden\"></section>", index);
            Assert.Contains("<script src=\"admin-quests.js\"></script>", index);
            Assert.Contains("<link rel=\"stylesheet\" href=\"admin-quests.css\" />", index);
            Assert.True(index.IndexOf("admin-quests.js", StringComparison.Ordinal) < index.IndexOf("app.js", StringComparison.Ordinal));
            Assert.Contains("quests: { label: 'Quests', group: 'Game Data' }", File.ReadAllText(Path.Combine(AdminDir(), "admin-shell.js")));
            Assert.Contains("AdminSections.register('quests'", Js());
        }

        [Fact]
        public void Api_calls_stay_within_the_quest_editor_routes()
        {
            var js = Js();
            var urls = Regex.Matches(js, @"'(/api/[^']*)'").Select(m => m.Groups[1].Value).Distinct().ToList();
            // The page also calls the characters' quest edit / reset routes for the Test tab.
            Assert.Equal(new[] { "/api/admin/characters/", "/api/admin/players-online", "/api/admin/quests" }, urls.OrderBy(u => u, StringComparer.Ordinal).ToArray());
            Assert.Contains("DbLookups.fetch", js);
        }

        [Fact]
        public void Wizard_is_loaded_after_the_page_and_never_calls_the_api()
        {
            var index = File.ReadAllText(Path.Combine(AdminDir(), "index.html"));
            var page = index.IndexOf("<script src=\"admin-quests.js\"></script>", StringComparison.Ordinal);
            var wizard = index.IndexOf("<script src=\"admin-quests-wizard.js\"></script>", StringComparison.Ordinal);
            var app = index.IndexOf("app.js", StringComparison.Ordinal);
            Assert.True(page >= 0 && wizard > page && wizard < app);

            var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-quests-wizard.js"));
            Assert.DoesNotContain("/api/", js);
            Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
            Assert.DoesNotContain("innerHTML", js);
            Assert.DoesNotContain("insertAdjacentHTML", js);
            Assert.DoesNotContain("localStorage", js);
            Assert.Contains("QuestsPage.openDraft", js);
        }

        [Fact]
        public void Assist_is_loaded_between_the_page_and_the_wizard_and_uses_only_known_routes()
        {
            var index = File.ReadAllText(Path.Combine(AdminDir(), "index.html"));
            int At(string s) => index.IndexOf(s, StringComparison.Ordinal);
            Assert.True(At("admin-quests.js\"") < At("admin-quests-assist.js\"") && At("admin-quests-assist.js\"") < At("admin-quests-wizard.js\"") && At("admin-quests-wizard.js\"") < At("app.js"));

            var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-quests-assist.js"));
            var urls = Regex.Matches(js, @"'(/api/[^']*)'").Select(m => m.Groups[1].Value).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "/api/admin/gm/action", "/api/admin/mapeditor/maps/", "/api/admin/players-online", "/api/admin/quests" }, urls);
            Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
            Assert.DoesNotContain("innerHTML", js);
            Assert.DoesNotContain("insertAdjacentHTML", js);
            Assert.DoesNotContain("localStorage", js);
        }

        [Fact]
        public void Writes_go_through_portalApi_and_no_data_html()
        {
            var js = Js();
            Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
            Assert.Contains("OpsUI.post(API + '/save'", js);
            Assert.DoesNotContain("innerHTML", js);
            Assert.DoesNotContain("insertAdjacentHTML", js);
            Assert.DoesNotContain("outerHTML", js);
            Assert.DoesNotContain("localStorage", js);
        }

        [Fact]
        public void Css_is_scoped_to_the_section()
        {
            var css = Regex.Replace(File.ReadAllText(Path.Combine(AdminDir(), "admin-quests.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            var selectors = Regex.Matches(css, @"([^{}]+)\{[^{}]*\}")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("@", StringComparison.Ordinal))
                .SelectMany(s => s.Split(','))
                .Select(s => s.Trim())
                .ToList();
            Assert.NotEmpty(selectors);
            var unscoped = selectors.Where(s => !s.StartsWith("#section-quests", StringComparison.Ordinal)).ToList();
            Assert.True(unscoped.Count == 0, "Unscoped selectors: " + string.Join(" | ", unscoped));
        }
    }
}
