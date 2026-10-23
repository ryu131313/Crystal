using System.Text.Json;
using Server.WebAdmin;
using Server.WebAdmin.Tools;
using Xunit;

namespace Server.WebAdmin.Tests
{
    /// <summary>Tools page: the bot run config (validation + StressClient arguments), repo lookup and the bind check.</summary>
    public class ToolsTests
    {
        private static StressBotConfig Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return StressBotConfig.FromJson(doc.RootElement.Clone());
        }

        [Fact]
        public void Defaults_BuildStdinControlledRunWithEveryBehaviour()
        {
            var c = Parse("{}");
            Assert.Equal(StressBotConfig.TargetScratch, c.Target);

            var args = c.BuildArgs("127.0.0.1", 17100, 400, new[] { @"C:\scratch\run\Maps" });
            Assert.Contains("--control-stdin", args);
            Assert.Contains("--status-json", args);
            Assert.Equal("0", args[args.IndexOf("--duration") + 1]);
            Assert.Equal("400", args[args.IndexOf("--max-packet") + 1]);
            Assert.Equal(@"C:\scratch\run\Maps", args[args.IndexOf("--map-folder") + 1]);
            Assert.DoesNotContain(args, a => a.StartsWith("--no-", StringComparison.Ordinal));
        }

        [Fact]
        public void BehaviourToggles_MapToNoFlags()
        {
            var c = Parse("{\"hunt\":false,\"town\":false,\"party\":false,\"director\":false,\"run\":false,\"reconnect\":false,\"durationMinutes\":3,\"class\":\"Mixed\",\"ramp\":2.5}");
            var args = c.BuildArgs("127.0.0.1", 7000, 50, null);

            foreach (var flag in new[] { "--no-hunt", "--no-town", "--no-party", "--no-director", "--no-run", "--no-reconnect" })
                Assert.Contains(flag, args);
            Assert.DoesNotContain("--no-loot", args);
            Assert.Equal("180", args[args.IndexOf("--duration") + 1]);
            Assert.Equal("mixed", args[args.IndexOf("--class") + 1]);
            Assert.Equal("2.5", args[args.IndexOf("--ramp") + 1]);
            Assert.DoesNotContain("--map-folder", args);
        }

        [Theory]
        [InlineData("{\"target\":\"live\"}")]
        [InlineData("{\"bots\":0}")]
        [InlineData("{\"bots\":10001}")]
        [InlineData("{\"bots\":2.5}")]
        [InlineData("{\"prefix\":\"too-long-prefix\"}")]
        [InlineData("{\"password\":\"abc\"}")]
        [InlineData("{\"password\":\"has space\"}")]
        [InlineData("{\"class\":\"monk\"}")]
        [InlineData("{\"firstIndex\":99990,\"bots\":20}")]
        [InlineData("{\"ramp\":0}")]
        [InlineData("[]")]
        public void BadInput_Is400(string json)
        {
            var ex = Assert.Throws<WebAdminException>(() => Parse(json));
            Assert.Equal(400, ex.Status);
        }

        [Fact]
        public void Dto_NeverCarriesThePassword()
        {
            var c = Parse("{\"password\":\"Secret123\",\"prefix\":\"ab\",\"firstIndex\":7,\"bots\":3}");
            var json = JsonSerializer.Serialize(c.ToDto());
            Assert.DoesNotContain("Secret123", json);
            Assert.Contains("ab00007..ab00009", json);
            Assert.Contains("\"customPassword\":true", json);
        }

        [Fact]
        public void ResolveRepo_SearchesUpwardForTheSolution()
        {
            var root = Path.Combine(Path.GetTempPath(), "mir2-tools-test-" + Guid.NewGuid().ToString("N"));
            var exeDir = Path.Combine(root, "Build", "Server", "Release");
            Directory.CreateDirectory(exeDir);
            try
            {
                Assert.Null(WebAdminTools.ResolveRepo(null, exeDir, out var source));
                Assert.Equal("search", source);

                File.WriteAllText(Path.Combine(root, "Legend of Mir.sln"), "");
                Assert.Equal(root, WebAdminTools.ResolveRepo(null, exeDir, out _));

                Assert.Equal(root, WebAdminTools.ResolveRepo(root, exeDir, out source));
                Assert.Equal("setting", source);
                Assert.Null(WebAdminTools.ResolveRepo(exeDir, exeDir, out _)); // a setting without the solution is not searched past
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("0.0.0.0", true)]
        [InlineData("::", true)]
        [InlineData("::1", true)]
        [InlineData("192.168.1.10", false)]
        [InlineData("", false)]
        [InlineData("not-an-ip", false)]
        public void SelfTarget_NeedsLoopbackOrAnyBind(string bind, bool ok)
        {
            Assert.Equal(ok, WebAdminTools.IsLoopbackOrAny(bind));
        }
    }
}
