using System.Text;
using System.Text.Json;
using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    public class GmTests
    {
        private static JsonElement Body(string json) => WebAdminJson.Parse(json);

        private static GmPlan Prepare(string json)
        {
            var body = Body(json);
            var args = new GmArgReader(body);
            var verb = GmCatalog.Find(body.GetString("action"));
            Assert.NotNull(verb);
            return GmActions.Prepare(verb, args);
        }

        // ---------------------------------------------------------------------------------------------- catalog

        [Fact]
        public void Catalog_HasEveryContractVerb()
        {
            var required = new[]
            {
                "kick", "kill", "killpets", "revive", "safezone", "teleport", "message", "givegold", "givecredit", "giveitem", "setlevel",
                "giveskill", "setpk", "chatban", "unchatban", "clearbuffs", "spawnmob", "clearmobs", "startconquest",
            };

            foreach (var action in required)
                Assert.True(GmCatalog.Find(action) != null, "missing verb " + action);

            Assert.Equal(GmCatalog.Verbs.Count, GmCatalog.Verbs.Select(v => v.Action).Distinct().Count());
            Assert.All(GmCatalog.Verbs, v => Assert.Contains(v.Group, GmCatalog.Groups));
            Assert.NotNull(GmCatalog.Find("KICK"));
            Assert.Null(GmCatalog.Find("nope"));
            Assert.Null(GmCatalog.Find(null));
        }

        [Fact]
        public void Catalog_OnlineAndOfflineFlagsMatchTheContract()
        {
            foreach (var a in new[] { "chatban", "unchatban", "setlevel", "setpk" })
            {
                var v = GmCatalog.Find(a);
                Assert.True(v.WorksOffline, a);
                Assert.False(v.NeedsOnline, a);
            }

            foreach (var a in new[] { "kick", "kill", "teleport", "message", "givegold", "giveitem", "giveskill", "revive", "killpets", "clearbuffs", "safezone" })
                Assert.True(GmCatalog.Find(a).NeedsOnline, a);

            foreach (var a in new[] { "spawnmob", "clearmobs", "startconquest" })
            {
                Assert.Equal("world", GmCatalog.Find(a).Target);
                Assert.False(GmCatalog.Find(a).WorksOffline);
            }
        }

        [Fact]
        public void Catalog_SerializesToTheCapsShape()
        {
            var json = WebAdminJson.Serialize(new { verbs = GmCatalog.Verbs });
            using var doc = JsonDocument.Parse(json);
            var verbs = doc.RootElement.GetProperty("verbs");
            var teleport = verbs.EnumerateArray().First(v => v.GetProperty("action").GetString() == "teleport");

            Assert.Equal("Movement", teleport.GetProperty("group").GetString());
            Assert.True(teleport.GetProperty("needsOnline").GetBoolean());
            var map = teleport.GetProperty("args").EnumerateArray().First(a => a.GetProperty("name").GetString() == "map");
            Assert.True(map.GetProperty("required").GetBoolean());
            Assert.Equal("map", map.GetProperty("type").GetString());

            var spell = GmCatalog.Find("giveskill").Args.First(a => a.Name == "spell");
            Assert.DoesNotContain(spell.Options, o => o.Value == "None");
            Assert.Contains(spell.Options, o => o.Value == "FireBall");
        }

        // ---------------------------------------------------------------------------------------------- rules

        [Theory]
        [InlineData("Fireball", true)]
        [InlineData("fireball", true)]
        [InlineData(" Thunderbolt ", true)]
        [InlineData("None", false)]
        [InlineData("0", false)]
        [InlineData("NotASpell", false)]
        [InlineData("", false)]
        [InlineData("999", false)]
        public void TryParseSpell(string text, bool ok)
        {
            Assert.Equal(ok, GmRules.TryParseSpell(text, out var spell));
            if (ok) Assert.NotEqual(Spell.None, spell);
        }

        [Fact]
        public void TryParseSpell_AcceptsNumbers()
        {
            Assert.True(GmRules.TryParseSpell(((int)Spell.FireBall).ToString(), out var s));
            Assert.Equal(Spell.FireBall, s);
        }

        [Fact]
        public void SplitStacks_RespectsStackSize()
        {
            Assert.Equal(new ushort[] { 1, 1, 1 }, GmRules.SplitStacks(3, 1));
            Assert.Equal(new ushort[] { 100, 100, 50 }, GmRules.SplitStacks(250, 100));
            Assert.Equal(new ushort[] { 7 }, GmRules.SplitStacks(7, 100));
            Assert.Empty(GmRules.SplitStacks(0, 10));
            Assert.Equal(new ushort[] { 1, 1 }, GmRules.SplitStacks(2, 0)); // bad stack size behaves as 1
            Assert.Equal(10000, GmRules.SplitStacks(10000, 3).Sum(x => (int)x));
        }

        [Fact]
        public void ClampGain_NeverOverflows()
        {
            Assert.Equal(100u, GmRules.ClampGain(0, 100));
            Assert.Equal(5u, GmRules.ClampGain(uint.MaxValue - 5, 100));
            Assert.Equal(0u, GmRules.ClampGain(uint.MaxValue, 1));
            Assert.Equal(0u, GmRules.ClampGain(10, 0));
            Assert.Equal(0u, GmRules.ClampGain(10, -5));
            Assert.Equal(uint.MaxValue, GmRules.ClampGain(0, long.MaxValue));
        }

        [Fact]
        public void ChatBanUntil_MinutesUntilAndIndefinite()
        {
            var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(now.AddMinutes(90), GmRules.ChatBanUntil(new GmArgReader(Body("{\"minutes\":90}")), now));
            Assert.Equal(now.AddMinutes(15), GmRules.ChatBanUntil(new GmArgReader(Body("{\"minutes\":\"15\"}")), now));
            Assert.Equal(now.AddYears(100), GmRules.ChatBanUntil(new GmArgReader(Body("{\"indefinite\":true}")), now));
            Assert.Equal(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                GmRules.ChatBanUntil(new GmArgReader(Body("{\"until\":\"2026-05-02T00:00:00Z\"}")), now));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"minutes\":0}")]
        [InlineData("{\"minutes\":-5}")]
        [InlineData("{\"minutes\":\"abc\"}")]
        [InlineData("{\"until\":\"2020-01-01T00:00:00Z\"}")]
        [InlineData("{\"until\":\"not a date\"}")]
        public void ChatBanUntil_RejectsMissingOrInvalid(string json)
        {
            var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            var ex = Assert.Throws<WebAdminException>(() => GmRules.ChatBanUntil(new GmArgReader(Body(json)), now));
            Assert.Equal(400, ex.Status);
        }

        [Fact]
        public void DescribeMinutes_IsReadable()
        {
            Assert.Equal("15 minutes", GmRules.DescribeMinutes(15));
            Assert.Equal("1 hour", GmRules.DescribeMinutes(60));
            Assert.Equal("1 day", GmRules.DescribeMinutes(1440));
            Assert.Equal("1 day 2 hours", GmRules.DescribeMinutes(1440 + 120 + 5));
            Assert.Equal("indefinitely", GmRules.DescribeMinutes(60.0 * 24 * 365 * 100));
        }

        [Fact]
        public void ParseMapRef_SplitsIndexAndName()
        {
            // A plain number is a file name first (Crystal maps are "0", "0100", ...); the index is only a fallback.
            var three = GmRules.ParseMapRef(" 3 ");
            Assert.Equal(3, three.index);
            Assert.Equal("3", three.name);

            var zero = GmRules.ParseMapRef("0");
            Assert.Equal(0, zero.index);
            Assert.Equal("0", zero.name);

            var leadingZero = GmRules.ParseMapRef("0100");
            Assert.Equal("0100", leadingZero.name);
            Assert.Equal(100, leadingZero.index);

            // "#12" is an explicit index and never a file name.
            var explicitIndex = GmRules.ParseMapRef(" #12 ");
            Assert.Equal(12, explicitIndex.index);
            Assert.Null(explicitIndex.name);

            var badHash = GmRules.ParseMapRef("#abc");
            Assert.Null(badHash.index);
            Assert.Equal("#abc", badHash.name);

            var named = GmRules.ParseMapRef("BichonProvince");
            Assert.Null(named.index);
            Assert.Equal("BichonProvince", named.name);

            var blank = GmRules.ParseMapRef("  ");
            Assert.Null(blank.index);
            Assert.Null(blank.name);
            Assert.Equal("woodensword", GmRules.NameKey(" Wooden Sword "));
        }

        // ---------------------------------------------------------------------------------------------- argument reader / prepare

        [Fact]
        public void ArgReader_TypedAccessAndErrors()
        {
            var r = new GmArgReader(Body("{\"a\":5,\"b\":\"1,000\",\"c\":\"  hi  \",\"d\":null,\"e\":\"\",\"f\":2.0,\"g\":2.5,\"h\":true}"));

            Assert.Equal(5L, r.Long("a", true, 0, 10));
            Assert.Equal(1000L, r.Long("b", true, 0, 5000));
            Assert.Equal("hi", r.String("c", true, 10));
            Assert.False(r.Has("d"));
            Assert.False(r.Has("e"));
            Assert.Null(r.Long("d", false, 0, 1));
            Assert.Equal(2L, r.Long("f", true, 0, 10));
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => r.Long("g", true, 0, 10)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => r.Long("a", true, 6, 10)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => r.String("missing", true, 10)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => r.String("c", true, 1)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => r.Long("h", true, 0, 1)).Status);
            Assert.True(r.Bool("h", false));
        }

        [Fact]
        public void Prepare_CharacterVerbsNeedATarget()
        {
            var ex = Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"kick\"}"));
            Assert.Equal(400, ex.Status);

            var byName = Prepare("{\"action\":\"kick\",\"character\":\"Bob\",\"reason\":\"spam\"}");
            Assert.Equal("Bob", byName.CharacterName);
            Assert.Equal("spam", byName.Reason);

            var byIndex = Prepare("{\"action\":\"kick\",\"characterIndex\":\"42\"}");
            Assert.Equal(42, byIndex.CharacterIndex);

            // world verbs do not
            var spawn = Prepare("{\"action\":\"spawnmob\",\"monster\":\"Hen\",\"map\":\"0\",\"x\":10,\"y\":12}");
            Assert.Equal(1, spawn.Count);
            Assert.Equal(10, spawn.X);
            Assert.Equal(12, spawn.Y);
        }

        [Fact]
        public void RandomTeleport_PreparesMapAndInstance()
        {
            var verb = GmCatalog.Find("randomteleport");
            Assert.Equal("world", verb.Target);
            Assert.Equal(GmCatalog.Movement, verb.Group);
            Assert.True(verb.Danger);

            var plan = Prepare("{\"action\":\"randomteleport\",\"map\":\"#3\"}");
            Assert.Equal("#3", plan.MapRef);
            Assert.Equal(1, plan.Instance);
            Assert.Equal(2, Prepare("{\"action\":\"randomteleport\",\"map\":\"0\",\"instance\":2}").Instance);

            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"randomteleport\"}")).Status);
        }

        [Fact]
        public void TeleportAll_PreparesAndScopes()
        {
            var verb = GmCatalog.Find("teleportall");
            Assert.Equal("world", verb.Target);
            Assert.Equal(GmCatalog.Movement, verb.Group);
            Assert.Equal(GmRules.MassTeleportScopes, verb.Args.First(a => a.Name == "who").Options.Select(o => o.Value));

            var all = Prepare("{\"action\":\"teleportall\",\"map\":\"#3\",\"x\":10,\"y\":12}");
            Assert.Equal("all", all.Who);
            Assert.Equal("#3", all.MapRef);
            Assert.Equal(0, all.Spread);

            var gather = Prepare("{\"action\":\"teleportall\",\"who\":\"MAP\",\"map\":\"0\",\"x\":1,\"y\":2,\"spread\":3}");
            Assert.Equal("map", gather.Who);
            Assert.Equal(3, gather.Spread);

            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"teleportall\",\"who\":\"nope\",\"map\":\"0\",\"x\":1,\"y\":1}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"teleportall\",\"map\":\"0\",\"x\":1}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"teleportall\",\"map\":\"0\",\"x\":1,\"y\":1,\"spread\":21}")).Status);

            Assert.True(GmRules.MassTeleportIncludes("all", false, false));
            Assert.True(GmRules.MassTeleportIncludes("gms", true, false));
            Assert.False(GmRules.MassTeleportIncludes("gms", false, true));
            Assert.True(GmRules.MassTeleportIncludes("players", false, false));
            Assert.False(GmRules.MassTeleportIncludes("players", true, false));
            Assert.True(GmRules.MassTeleportIncludes("map", true, true));
            Assert.False(GmRules.MassTeleportIncludes("map", false, false));
            Assert.False(GmRules.MassTeleportIncludes("nope", true, true));
        }

        [Fact]
        public void Prepare_ValidatesVerbArguments()
        {
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"teleport\",\"character\":\"Bob\"}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"teleport\",\"character\":\"Bob\",\"map\":\"0\",\"x\":5}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"givegold\",\"character\":\"Bob\",\"amount\":0}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"givegold\",\"character\":\"Bob\",\"amount\":4294967296}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"giveskill\",\"character\":\"Bob\",\"spell\":\"Nope\"}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"giveskill\",\"character\":\"Bob\",\"spell\":\"Fireball\",\"level\":4}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"setlevel\",\"character\":\"Bob\",\"level\":0}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"chatban\",\"character\":\"Bob\"}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"message\",\"character\":\"Bob\",\"text\":\"  \"}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"spawnmob\",\"monster\":\"Hen\",\"map\":\"0\",\"x\":1,\"y\":1,\"count\":101}")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare("{\"action\":\"startconquest\"}")).Status);

            var tp = Prepare("{\"action\":\"teleport\",\"character\":\"Bob\",\"map\":\"Bichon\",\"x\":5,\"y\":6,\"instance\":2}");
            Assert.Equal("Bichon", tp.MapRef);
            Assert.Equal(2, tp.Instance);

            var gi = Prepare("{\"action\":\"giveitem\",\"character\":\"Bob\",\"item\":\"Wooden Sword\"}");
            Assert.Equal(1, gi.Count);
            Assert.Equal("Wooden Sword", gi.ItemRef);

            var sk = Prepare("{\"action\":\"giveskill\",\"character\":\"Bob\",\"spell\":\"fireball\",\"level\":3}");
            Assert.Equal(Spell.FireBall, sk.Spell);
            Assert.Equal(3, sk.SpellLevel);

            var cb = Prepare("{\"action\":\"chatban\",\"character\":\"Bob\",\"minutes\":60,\"reason\":\"flood\"}");
            Assert.Equal("flood", cb.Reason);

            var sc = Prepare("{\"action\":\"startconquest\",\"conquestIndex\":2,\"guild\":\"Knights\"}");
            Assert.Equal(2, sc.ConquestIndex);
            Assert.Equal("Knights", sc.Guild);
        }

        [Fact]
        public void Prepare_RejectsAnOverlongReason()
        {
            var json = "{\"action\":\"kick\",\"character\":\"Bob\",\"reason\":\"" + new string('x', 501) + "\"}";
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Prepare(json)).Status);
        }

        // ---------------------------------------------------------------------------------------------- broadcast helpers

        [Theory]
        [InlineData("announcement", ChatType.Announcement)]
        [InlineData("SHOUT", ChatType.Shout3)]
        [InlineData("system", ChatType.System)]
        [InlineData("hint", ChatType.Hint)]
        [InlineData("linemessage", ChatType.LineMessage)]
        [InlineData(null, ChatType.Announcement)]
        public void Broadcast_ParseType(string name, ChatType expected)
        {
            Assert.Equal(expected, BroadcastRoutes.ParseType(name));
        }

        [Fact]
        public void Broadcast_ParseType_RejectsUnknown()
        {
            Assert.Null(BroadcastRoutes.ParseType("whisper"));
        }

        [Fact]
        public void Broadcast_SplitLines_DropsBlanksAndControlChars()
        {
            var lines = BroadcastRoutes.SplitLines("Hello\r\n\r\n  World\t!  \nBye\u0007");
            Assert.Equal(new[] { "Hello", "World !", "Bye" }, lines);
            Assert.Empty(BroadcastRoutes.SplitLines("  \r\n "));
            Assert.Equal(BroadcastRoutes.MaxMessageLength, BroadcastRoutes.SplitLines(new string('a', 900))[0].Length);
        }

        [Fact]
        public void Notice_EtagAndDecoding()
        {
            var a = BroadcastRoutes.ComputeEtag(Encoding.UTF8.GetBytes("TITLE=Hi\r\nWelcome"));
            var b = BroadcastRoutes.ComputeEtag(Encoding.UTF8.GetBytes("TITLE=Hi\r\nWelcome!"));
            Assert.NotEqual(a, b);
            Assert.Equal(a, BroadcastRoutes.ComputeEtag(Encoding.UTF8.GetBytes("TITLE=Hi\r\nWelcome")));
            Assert.Equal(24, a.Length);

            var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("héllo")).ToArray();
            Assert.Equal("héllo", BroadcastRoutes.DecodeText(withBom, out var bom));
            Assert.True(bom);

            Assert.Equal("café", BroadcastRoutes.DecodeText(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, out bom)); // not UTF-8 → Latin-1
            Assert.False(bom);
        }
    }
}
