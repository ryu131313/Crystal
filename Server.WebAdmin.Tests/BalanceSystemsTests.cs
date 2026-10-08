using System.Reflection;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// B7 Balance + Systems: pure parsing / validation / catalogue logic. Nothing here touches Settings statics (only metadata
    /// reflection over typeof(Settings)), Envir or the game thread.
    /// </summary>
    public class BalanceSystemsTests
    {
        private static JsonElement Json(string json) => WebAdminJson.Parse(json);

        // ------------------------------------------------------------------------------------------------ strict JSON readers

        [Theory]
        [InlineData("5", true, 5L)]
        [InlineData("\"12\"", true, 12L)]
        [InlineData("5.0", true, 5L)]
        [InlineData("5.5", false, 0L)]
        [InlineData("\"abc\"", false, 0L)]
        [InlineData("true", false, 0L)]
        public void TryInteger_IsStrict(string json, bool ok, long expected)
        {
            Assert.Equal(ok, BsJson.TryInteger(Json(json), out var value));
            if (ok) Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("true", true, true)]
        [InlineData("false", true, false)]
        [InlineData("1", true, true)]
        [InlineData("\"false\"", true, false)]
        [InlineData("2", false, false)]
        [InlineData("\"yes\"", false, false)]
        public void TryBool_IsStrict(string json, bool ok, bool expected)
        {
            Assert.Equal(ok, BsJson.TryBool(Json(json), out var value));
            if (ok) Assert.Equal(expected, value);
        }

        [Fact]
        public void TryEnum_AcceptsNamesAndDefinedNumbersOnly()
        {
            Assert.True(BsJson.TryEnum(Json("\"maxac\""), out Stat byName));
            Assert.Equal(Stat.MaxAC, byName);

            Assert.True(BsJson.TryEnum(Json("12"), out Stat byNumber));
            Assert.Equal(Stat.HP, byNumber);

            Assert.False(BsJson.TryEnum(Json("25"), out Stat _));          // gap in the enum
            Assert.False(BsJson.TryEnum(Json("\"MaxAC,MinAC\""), out Stat _));
            Assert.False(BsJson.TryEnum(Json("\"Nope\""), out Stat _));
            Assert.False(BsJson.TryEnum(Json("-1"), out Stat _));
        }

        [Fact]
        public void ReadInt_ReportsRangeAndMissing()
        {
            var body = Json("{\"a\":300,\"b\":\"x\"}");
            var v = new BsValidation();

            Assert.False(BsJson.ReadInt(body, "a", 0, 255, v, "a", true, out _));
            Assert.False(BsJson.ReadInt(body, "b", 0, 255, v, "b", true, out _));
            Assert.False(BsJson.ReadInt(body, "c", 0, 255, v, "c", true, out _));
            Assert.False(BsJson.ReadInt(body, "d", 0, 255, v, "d", false, out _));

            Assert.Equal(new[] { "a", "b", "c" }, v.Errors.Select(e => e.Field).ToArray());
        }

        [Fact]
        public void Validation_ThrowsA422WithTheErrorList()
        {
            var v = new BsValidation();
            v.Error("stats[0].gain", "must be a number");
            v.Warn("careful");

            var ex = Assert.Throws<WebAdminException>(() => v.ThrowIfInvalid());
            Assert.Equal(422, ex.Status);
            Assert.Contains("stats[0].gain", ex.Message);

            var payload = WebAdminJson.ToElement(ex.Payload);
            Assert.Equal(1, payload.GetProperty("errors").GetArrayLength());
            Assert.Equal("careful", payload.GetProperty("warnings")[0].GetString());

            new BsValidation().ThrowIfInvalid(); // no errors → no throw
        }

        // ------------------------------------------------------------------------------------------------ revision tokens

        [Fact]
        public void Rev_IsStableAndDetectsChanges()
        {
            var a = BsRev.Of(new { x = 1, y = new[] { 1, 2 } });
            var b = BsRev.Of(new { x = 1, y = new[] { 1, 2 } });
            var c = BsRev.Of(new { x = 1, y = new[] { 2, 1 } });

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(16, a.Length);
        }

        [Fact]
        public void RevCheck_ConflictsOnlyOnAMismatch()
        {
            BsRev.Check(Json("{}"), "abc");
            BsRev.Check(Json("{\"rev\":\"\"}"), "abc");
            BsRev.Check(Json("{\"rev\":\"ABC\"}"), "abc");

            var ex = Assert.Throws<WebAdminException>(() => BsRev.Check(Json("{\"rev\":\"old\"}"), "abc"));
            Assert.Equal(409, ex.Status);
        }

        // ------------------------------------------------------------------------------------------------ name lookups

        [Fact]
        public void NameIndex_ItemsResolveToTheSpaceStrippedName()
        {
            var index = new BsNameIndex(new[] { "Gold Ore", "WoomaHorn", "  ", null }, BsNameMode.ItemStripped);

            Assert.Equal("GoldOre", index.Resolve("gold ore"));
            Assert.Equal("GoldOre", index.Resolve("GOLDORE"));
            Assert.Equal("WoomaHorn", index.Resolve("woomahorn"));
            Assert.Null(index.Resolve("Silver Ore"));
            Assert.Null(index.Resolve(""));
            Assert.Equal(2, index.Count);
        }

        [Fact]
        public void NameIndex_MonstersKeepTheirNameAndMatchWithoutSpaces()
        {
            var index = new BsNameIndex(new[] { "Giant Keratoid", "Hen" }, BsNameMode.Monster);

            Assert.Equal("Giant Keratoid", index.Resolve("giant keratoid"));
            Assert.Equal("Giant Keratoid", index.Resolve("GiantKeratoid"));
            Assert.Equal("Hen", index.Resolve("HEN"));
            Assert.Null(index.Resolve("Deer"));
        }

        [Fact]
        public void NameIndex_ExactIsCaseSensitive()
        {
            var index = new BsNameIndex(new[] { "BlackIronOre" }, BsNameMode.Exact);

            Assert.Equal("BlackIronOre", index.Resolve("BlackIronOre"));
            Assert.Null(index.Resolve("blackironore"));
        }

        [Fact]
        public void RefineOre_MustPassBothTheFriendlyNameAndTheStartupLookup()
        {
            var names = BsNameIndex.RefineOreCandidates(new[]
            {
                ("BlackIronOre", "BlackIronOre"),       // ok
                ("Black Iron Ore", "Black Iron Ore"),   // start-up GetItemInfo("Black Iron Ore") fails (stripped name has no spaces)
                ("GoldOre2", "GoldOre"),                // no item's stripped name is "GoldOre"
                ("SilverOre", "SilverOre"),
                ("SilverOre1", "SilverOre"),            // duplicate friendly name collapses
            });

            Assert.Equal(new[] { "BlackIronOre", "SilverOre" }, names);
        }

        // ------------------------------------------------------------------------------------------------ base stats

        private const string ValidBaseStats = @"{
            ""stats"": [
                { ""stat"": ""HP"", ""formula"": ""Health"", ""base"": 14, ""gain"": 4, ""gainRate"": 4.5, ""max"": 0 },
                { ""stat"": ""MaxDC"", ""formula"": ""Stat"", ""base"": 0, ""gain"": 5 }
            ],
            ""caps"": [ { ""stat"": ""CriticalRate"", ""value"": 18 } ]
        }";

        [Fact]
        public void BaseStats_ParseValidBody()
        {
            var v = new BsValidation();
            var stats = BalanceModel.Parse(MirClass.Warrior, Json(ValidBaseStats), v);

            Assert.True(v.Ok);
            Assert.Equal(MirClass.Warrior, stats.Job);
            Assert.Equal(2, stats.Stats.Count);

            var hp = stats.Stats[0];
            Assert.Equal(Stat.HP, hp.Type);
            Assert.Equal(StatFormula.Health, hp.FormulaType);
            Assert.Equal(14, hp.Base);
            Assert.Equal(4f, hp.Gain);
            Assert.Equal(4.5f, hp.GainRate);

            Assert.Equal(0f, stats.Stats[1].GainRate);
            Assert.Equal(18, stats.Caps[Stat.CriticalRate]);
            Assert.Single(stats.Caps.Values);
        }

        [Fact]
        public void BaseStats_RejectDuplicatesUnknownsAndNegativeLimits()
        {
            var v = new BsValidation();
            BalanceModel.Parse(MirClass.Wizard, Json(@"{
                ""stats"": [
                    { ""stat"": ""HP"", ""formula"": ""Health"", ""base"": 1, ""gain"": 1 },
                    { ""stat"": ""hp"", ""formula"": ""Health"", ""base"": 1, ""gain"": 1 },
                    { ""stat"": ""Unknown"", ""formula"": ""Stat"", ""base"": 1, ""gain"": 1 },
                    { ""stat"": ""MP"", ""formula"": ""Magic"", ""base"": 1, ""gain"": 1 },
                    { ""stat"": ""MaxAC"", ""formula"": ""Stat"", ""base"": 1.5, ""gain"": 1 },
                    { ""stat"": ""MaxMC"", ""formula"": ""Stat"", ""base"": 1, ""gain"": ""x"" },
                    { ""stat"": ""MaxSC"", ""formula"": ""Stat"", ""base"": 1, ""gain"": 1, ""max"": -5 }
                ],
                ""caps"": [ { ""stat"": ""Luck"", ""value"": 1 }, { ""stat"": ""Luck"", ""value"": 2 }, { ""stat"": ""Agility"", ""value"": -1 } ]
            }"), v);

            var fields = v.Errors.Select(e => e.Field).ToList();
            Assert.Contains("stats[1].stat", fields);
            Assert.Contains("stats[2].stat", fields);
            Assert.Contains("stats[3].formula", fields);
            Assert.Contains("stats[4].base", fields);
            Assert.Contains("stats[5].gain", fields);
            Assert.Contains("stats[6].max", fields);
            Assert.Contains("caps[1].stat", fields);
            Assert.Contains("caps[2].value", fields);
            Assert.DoesNotContain("stats[0].stat", fields);
        }

        [Fact]
        public void BaseStats_RequireArrays()
        {
            var v = new BsValidation();
            BalanceModel.Parse(MirClass.Taoist, Json("{\"stats\":{}}"), v);
            Assert.Contains(v.Errors, e => e.Field == "stats");

            v = new BsValidation();
            BalanceModel.Parse(MirClass.Taoist, Json("[1]"), v);
            Assert.Contains(v.Errors, e => e.Field == "body");
        }

        [Fact]
        public void BaseStats_DtoRoundTripsAndRevTracksContent()
        {
            var defaults = new BaseStats(MirClass.Archer);
            var dto = BalanceModel.ToDto(defaults, hero: true);

            Assert.Equal("Archer", dto.Job);
            Assert.True(dto.Hero);
            Assert.Equal(defaults.Stats.Count, dto.Stats.Count);
            Assert.Equal(defaults.Caps.Values.Count, dto.Caps.Count);

            // Feed the DTO back through the parser: identical content, identical rev.
            var v = new BsValidation();
            var parsed = BalanceModel.Parse(MirClass.Archer, WebAdminJson.ToElement(new { stats = dto.Stats, caps = dto.Caps }), v);
            Assert.True(v.Ok, string.Join("; ", v.Errors.Select(e => e.Field + " " + e.Message)));
            Assert.Equal(dto.Rev, BalanceModel.ToDto(parsed, true).Rev);

            parsed.Stats[0].Base++;
            Assert.NotEqual(dto.Rev, BalanceModel.ToDto(parsed, true).Rev);
        }

        [Fact]
        public void BaseStats_ClassRouteValue()
        {
            Assert.True(BalanceModel.TryParseClass("assassin", out var a));
            Assert.Equal(MirClass.Assassin, a);
            Assert.True(BalanceModel.TryParseClass("4", out var b));
            Assert.Equal(MirClass.Archer, b);
            Assert.False(BalanceModel.TryParseClass("5", out _));
            Assert.False(BalanceModel.TryParseClass("Monk", out _));
        }

        [Fact]
        public void Preview_MatchesBaseStatCalculateWithTheCap()
        {
            var stat = new BaseStat(Stat.HP) { FormulaType = StatFormula.Health, Base = 14, Gain = 4f, GainRate = 4.5f, Max = 0 };
            var values = BalanceModel.Preview(MirClass.Warrior, StatFormula.Health, 14, 4f, 4.5f, 0, 0, 1, 50);

            Assert.Equal(50, values.Count);
            for (var level = 1; level <= 50; level++)
                Assert.Equal(stat.Calculate(MirClass.Warrior, level), values[level - 1]);

            var capped = BalanceModel.Preview(MirClass.Warrior, StatFormula.Health, 14, 4f, 4.5f, 0, 100, 1, 50);
            Assert.All(capped, x => Assert.True(x <= 100));
            Assert.Equal(100, capped[49]);
        }

        [Fact]
        public void Preview_ZeroGainIsTheBaseAndLevelsAreClamped()
        {
            var values = BalanceModel.Preview(MirClass.Wizard, StatFormula.Stat, 7, 0f, 0f, 0, 0, -5, 3);
            Assert.Equal(new[] { 7, 7, 7 }, values);

            var many = BalanceModel.Preview(MirClass.Wizard, StatFormula.Stat, 1, 2f, 0f, 0, 0, 1, 100000);
            Assert.Equal(500, many.Count);
        }

        // ------------------------------------------------------------------------------------------------ catalogues vs Settings

        private static FieldInfo SettingsField(string name) => typeof(Settings).GetField(name, BindingFlags.Public | BindingFlags.Static);

        [Fact]
        public void Catalogue_EveryFieldExistsOnSettingsWithItsType()
        {
            foreach (var group in BsCatalog.Groups.Concat(new[] { BsCatalog.ItemWeights }))
            {
                foreach (var def in group.Fields)
                {
                    var fi = SettingsField(def.Field);
                    Assert.True(fi != null, group.Key + ": Settings." + def.Field + " does not exist");

                    if (def.ArrayIndex >= 0)
                    {
                        Assert.Equal(typeof(bool[]), fi.FieldType);
                        Assert.InRange(def.ArrayIndex, 0, Enum.GetNames(typeof(MirClass)).Length - 1);
                        Assert.Equal(((MirClass)def.ArrayIndex).ToString(), def.ArrayLabel);
                    }
                    else
                    {
                        Assert.True(def.ClrType == fi.FieldType, group.Key + ": Settings." + def.Field + " is " + fi.FieldType.Name + ", catalogue says " + def.Kind);
                    }

                    if (def.Kind != "bool" && def.Kind != "string" && def.Kind != "float")
                    {
                        var clrMax = Convert.ToDecimal(def.ClrType.GetField("MaxValue").GetValue(null));
                        var clrMin = Convert.ToDecimal(def.ClrType.GetField("MinValue").GetValue(null));
                        Assert.True(def.Max <= clrMax && def.Min >= clrMin, def.Key + " range exceeds " + def.ClrType.Name);
                        Assert.True(def.Min <= def.Max, def.Key + " min > max");
                    }
                }

                var save = typeof(Settings).GetMethod(group.SaveMethod, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                Assert.True(save != null, "Settings." + group.SaveMethod + "() does not exist");

                Assert.Equal(group.Fields.Count, group.Fields.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }

        [Fact]
        public void Catalogue_CoversTheSystemInfoFormGroups()
        {
            var keys = BsCatalog.Groups.Select(g => g.Key).ToArray();
            Assert.Equal(new[] { "fishing", "mail", "goods", "refine", "marriage", "mentor", "gem", "heroes" }, keys);
            Assert.Null(BsCatalog.FindGroup("guilds"));
            Assert.Same(BsCatalog.Groups[0], BsCatalog.FindGroup("FISHING"));
        }

        [Fact]
        public void ListSettingsUsedByTheRoutesExist()
        {
            Assert.Equal(typeof(BaseStats[]), SettingsField("ClassBaseStats").FieldType);
            Assert.Equal(typeof(BaseStats[]), SettingsField("HeroBaseStats").FieldType);
            Assert.Equal(typeof(List<RandomItemStat>), SettingsField("RandomItemStatsList").FieldType);
            Assert.Equal(typeof(List<MineSet>), SettingsField("MineSetList").FieldType);
            Assert.Equal(typeof(List<GuildBuffInfo>), SettingsField("Guild_BuffList").FieldType);
            Assert.Equal(typeof(List<long>), SettingsField("Guild_ExperienceList").FieldType);
            Assert.Equal(typeof(List<int>), SettingsField("Guild_MembercapList").FieldType);
            Assert.Equal(typeof(float), SettingsField("Guild_ExpRate").FieldType);

            foreach (var method in new[] { "SaveBaseStats", "SaveHeroBaseStats", "SaveRandomItemStats", "SaveMines", "SaveGuildSettings", "Save" })
                Assert.NotNull(typeof(Settings).GetMethod(method, BindingFlags.Public | BindingFlags.Static));
        }

        [Fact]
        public void ItemWeights_DivisorsMustBeAtLeastOne()
        {
            var group = BsCatalog.ItemWeights;
            var v = new BsValidation();
            var values = BsCatalog.ParseValues(group, Json(@"{ ""values"": {
                ""CriticalDamageWeight"": 0, ""HealthRegenWeight"": 0, ""ManaRegenWeight"": 256,
                ""MagicResistWeight"": 0, ""PvpCanFreeze"": true, ""Bogus"": 1 } }"), v);

            var fields = v.Errors.Select(e => e.Field).ToList();
            Assert.Contains("CriticalDamageWeight", fields);
            Assert.Contains("HealthRegenWeight", fields);
            Assert.Contains("ManaRegenWeight", fields);
            Assert.Contains("Bogus", fields);

            Assert.Equal((byte)0, values["MagicResistWeight"]);
            Assert.Equal(true, values["PvpCanFreeze"]);
        }

        [Fact]
        public void GroupValues_AreTypedForTheSettingsField()
        {
            var fishing = BsCatalog.FindGroup("fishing");
            var v = new BsValidation();
            var values = BsCatalog.ParseValues(fishing, Json(@"{ ""values"": { ""FishingAttempts"": 9, ""FishingDelay"": ""2500"", ""FishingMonster"": "" Hen "" } }"), v);

            Assert.Contains(v.Errors, e => e.Field == "FishingAttempts"); // SystemInfoForm: 10-100
            Assert.IsType<long>(values["FishingDelay"]);
            Assert.Equal(2500L, values["FishingDelay"]);
            Assert.Equal("Hen", values["FishingMonster"]);

            var mail = BsCatalog.FindGroup("mail");
            v = new BsValidation();
            values = BsCatalog.ParseValues(mail, Json(@"{ ""values"": { ""MailCostPer1KGold"": 1000, ""MailItemInsurancePercentage"": 101 } }"), v);
            Assert.IsType<uint>(values["MailCostPer1KGold"]);
            Assert.Contains(v.Errors, e => e.Field == "MailItemInsurancePercentage");

            v = new BsValidation();
            BsCatalog.ParseValues(mail, Json("{\"other\":1}"), v);
            Assert.Contains(v.Errors, e => e.Field == "values");
        }

        [Fact]
        public void Heroes_ClassFlagsUseArrayKeysAndHeroCountNeedsOneWhileAllowed()
        {
            var heroes = BsCatalog.FindGroup("heroes");
            Assert.NotNull(heroes.Find("Hero_CanCreateClass.Archer"));

            var v = new BsValidation();
            var values = BsCatalog.ParseValues(heroes, Json(@"{ ""values"": { ""Hero_CanCreateClass.Wizard"": false, ""MaximumHeroCount"": 0 } }"), v);
            Assert.True(v.Ok);
            Assert.Equal(false, values["Hero_CanCreateClass.Wizard"]);

            var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["AllowNewHero"] = true, ["MaximumHeroCount"] = (byte)0 };
            BsCatalog.CrossValidate(heroes, merged, v);
            Assert.Contains(v.Errors, e => e.Field == "MaximumHeroCount");

            v = new BsValidation();
            merged["AllowNewHero"] = false;
            BsCatalog.CrossValidate(heroes, merged, v);
            Assert.True(v.Ok);
        }

        [Fact]
        public void StringFields_RejectMultiLineAndOverlongText()
        {
            var def = BsCatalog.FindGroup("refine").Find("RefineOreName");

            def.Parse(Json("\"Black\\nIron\""), out var error);
            Assert.NotNull(error);

            def.Parse(Json("\"" + new string('x', 51) + "\""), out error);
            Assert.NotNull(error);

            def.Parse(Json("\"   \""), out error);
            Assert.NotNull(error); // required (min length 1)

            Assert.Equal("BlackIronOre", def.Parse(Json("\"BlackIronOre\""), out error));
            Assert.Null(error);
        }

        // ------------------------------------------------------------------------------------------------ random item stats

        [Fact]
        public void RandomItemStats_CatalogueCoversEveryByteField()
        {
            var fields = typeof(RandomItemStat).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(byte)).Select(f => f.Name).OrderBy(n => n).ToArray();
            var catalogue = RandomItemStatCatalog.FieldNames().OrderBy(n => n).ToArray();

            Assert.Equal(fields, catalogue);
            Assert.All(catalogue, n => Assert.NotNull(RandomItemStatCatalog.Field(n)));
        }

        [Fact]
        public void RandomItemStats_ValuesApplyAndClone()
        {
            var weapon = new RandomItemStat(ItemType.Weapon);
            var values = RandomItemStatCatalog.ToValues(weapon);

            Assert.Equal(15, values["MaxDcChance"]);
            Assert.Equal(3, values["AttackSpeedMaxStat"]);

            var copy = RandomItemStatCatalog.Clone(weapon);
            Assert.Equal(values, RandomItemStatCatalog.ToValues(copy));

            RandomItemStatCatalog.Apply(copy, new Dictionary<string, int> { ["LuckChance"] = 40 });
            Assert.Equal(40, copy.LuckChance);
            Assert.Equal(0, weapon.LuckChance);
        }

        [Fact]
        public void RandomItemStats_ParseMergesAndValidates()
        {
            var current = RandomItemStatCatalog.ToValues(new RandomItemStat(ItemType.Ring));
            var v = new BsValidation();
            var merged = RandomItemStatCatalog.Parse(Json(@"{ ""values"": { ""luckchance"": 10, ""LuckMaxStat"": 2 } }"), current, v);

            Assert.True(v.Ok, string.Join("; ", v.Errors.Select(e => e.Field + " " + e.Message)));
            Assert.Equal(10, merged["LuckChance"]);
            Assert.Equal(2, merged["LuckMaxStat"]);
            Assert.Equal(current["MaxAcChance"], merged["MaxAcChance"]);

            v = new BsValidation();
            RandomItemStatCatalog.Parse(Json(@"{ ""values"": {
                ""MaxDuraChance"": 255, ""HpChance"": 5, ""HpMaxStat"": 0, ""SlotChance"": 1, ""SlotMaxStat"": 13,
                ""CurseChance"": 300, ""NotAField"": 1 } }"), current, v);

            var fields = v.Errors.Select(e => e.Field).ToList();
            Assert.Contains("MaxDuraChance", fields);
            Assert.Contains("HpMaxStat", fields);
            Assert.Contains("SlotMaxStat", fields);
            Assert.Contains("CurseChance", fields);
            Assert.Contains("NotAField", fields);
        }

        [Fact]
        public void RandomItemStats_UnusedGroupsMayKeepAZeroMaxStat()
        {
            var v = new BsValidation();
            RandomItemStatCatalog.Validate(RandomItemStatCatalog.ToValues(new RandomItemStat()), v);
            Assert.True(v.Ok);
        }

        // ------------------------------------------------------------------------------------------------ guild settings

        private static string GuildBody(string costs = null, string levels = null, string buffs = null)
        {
            return @"{
                ""requiredLevel"": 22, ""pointPerLevel"": 1, ""expRate"": 0.01, ""newbieGuildBuffEnabled"": true, ""newbieGuildExpBuff"": 5,
                ""warTime"": 180, ""warCost"": 3000, ""buyGTGold"": 10000000, ""extendGT"": 1000000, ""gtDays"": 30,
                ""creationCosts"": " + (costs ?? @"[ { ""itemName"": """", ""amount"": 1000000 }, { ""itemName"": ""wooma horn"", ""amount"": 1 } ]") + @",
                ""levels"": " + (levels ?? @"[ { ""experience"": 0, ""memberCap"": 10 }, { ""experience"": 5000, ""memberCap"": 20 } ]") + @",
                ""buffs"": " + (buffs ?? @"[ { ""id"": 3, ""name"": ""Rage"", ""levelRequirement"": 2, ""pointsRequirement"": 1, ""timeLimit"": 60, ""activationCost"": 500, ""stats"": { ""MaxDC"": 5, ""HP"": 1000 } },
                                            { ""name"": ""Fresh"", ""stats"": {} } ]") + @"
            }";
        }

        private static string ResolveItem(string name) => new BsNameIndex(new[] { "Wooma Horn", "Gold Ore" }, BsNameMode.ItemStripped).Resolve(name);

        [Fact]
        public void Guilds_ParseValidSettings()
        {
            var v = new BsValidation();
            var dto = GuildSettingsModel.Parse(Json(GuildBody()), ResolveItem, true, v);

            Assert.True(v.Ok, string.Join("; ", v.Errors.Select(e => e.Field + " " + e.Message)));
            Assert.Equal(22, dto.RequiredLevel);
            Assert.Equal(0.01f, dto.ExpRate);
            Assert.Equal(2, dto.CreationCosts.Count);
            Assert.Equal(string.Empty, dto.CreationCosts[0].ItemName);
            Assert.Equal("WoomaHorn", dto.CreationCosts[1].ItemName);
            Assert.Equal(new long[] { 0, 5000 }, dto.Levels.Select(l => l.Experience).ToArray());
            Assert.Equal(new[] { 0, 1 }, dto.Levels.Select(l => l.Level).ToArray());

            Assert.Equal(2, dto.Buffs.Count);
            Assert.Equal(3, dto.Buffs[0].Id);
            Assert.Equal(4, dto.Buffs[1].Id); // new buff → max id + 1
            Assert.Equal(5, dto.Buffs[0].Stats["MaxDC"]);
            Assert.Equal(1000, dto.Buffs[0].Stats["HP"]);
            Assert.Equal(GuildSettingsModel.BuffStats.Count, dto.Buffs[1].Stats.Count);
        }

        [Fact]
        public void Guilds_BuffIdZeroIsKeptAndOnlyMissingIdsAreNew()
        {
            var v = new BsValidation();
            var dto = GuildSettingsModel.Parse(Json(GuildBody(buffs: @"[ { ""id"": 0, ""name"": ""Old"" }, { ""id"": null, ""name"": ""New"" }, { ""name"": ""Newer"" } ]")), ResolveItem, true, v);

            Assert.True(v.Ok, string.Join("; ", v.Errors.Select(e => e.Field + " " + e.Message)));
            Assert.Equal(new[] { 0, 1, 2 }, dto.Buffs.Select(b => b.Id).ToArray());
        }

        [Fact]
        public void Guilds_RejectLoaderSentinelsAndBadRows()
        {
            var v = new BsValidation();
            GuildSettingsModel.Parse(Json(GuildBody(
                costs: @"[ { ""itemName"": """", ""amount"": 0 }, { ""itemName"": ""Dragon Egg"", ""amount"": 1 } ]",
                levels: @"[ { ""experience"": -1, ""memberCap"": 10 }, { ""experience"": 10, ""memberCap"": -1 } ]",
                buffs: @"[ { ""id"": 1, ""name"": """" }, { ""id"": 1, ""name"": ""Twin"" }, { ""id"": 2, ""name"": ""ThisNameIsWayTooLongForAGuildBuff"" },
                           { ""id"": 5, ""name"": ""Big"", ""stats"": { ""MaxAC"": 256, ""Luck"": 1 } } ]")), ResolveItem, true, v);

            var fields = v.Errors.Select(e => e.Field).ToList();
            Assert.Contains("creationCosts[0].amount", fields);
            Assert.Contains("creationCosts[1].itemName", fields);
            Assert.Contains("levels[0].experience", fields);
            Assert.Contains("levels[1].memberCap", fields);
            Assert.Contains("buffs[0].name", fields);
            Assert.Contains("buffs[1].id", fields);
            Assert.Contains("buffs[2].name", fields);
            Assert.Contains("buffs[3].stats.MaxAC", fields);
            Assert.Contains("buffs[3].stats.Luck", fields);
        }

        [Fact]
        public void Guilds_UnloadedItemDatabaseOnlyWarns()
        {
            var v = new BsValidation();
            var dto = GuildSettingsModel.Parse(Json(GuildBody()), _ => null, false, v);

            Assert.True(v.Ok);
            Assert.NotEmpty(v.Warnings);
            Assert.Equal("woomahorn", dto.CreationCosts[1].ItemName);
        }

        [Fact]
        public void Guilds_BuffDtoRoundTrip()
        {
            var info = new GuildBuffInfo { Id = 7, Name = "Shield", Icon = 3, LevelRequirement = 4, PointsRequirement = 2, TimeLimit = 30, ActivationCost = 99 };
            info.Stats[Stat.MaxAC] = 5;
            info.Stats[Stat.MP] = 300;
            info.Stats[Stat.Luck] = 9; // not a guild buff stat: not part of the editable set

            var dto = GuildSettingsModel.ToDto(info);
            Assert.Equal(5, dto.Stats["MaxAC"]);
            Assert.Equal(300, dto.Stats["MP"]);
            Assert.False(dto.Stats.ContainsKey("Luck"));

            var target = new GuildBuffInfo();
            dto.Stats["MaxAC"] = 0;
            GuildSettingsModel.ApplyBuff(dto, target);

            Assert.Equal(7, target.Id);
            Assert.Equal("Shield", target.Name);
            Assert.Equal((byte)4, target.LevelRequirement);
            Assert.Equal(0, target.Stats[Stat.MaxAC]);
            Assert.Equal(300, target.Stats[Stat.MP]);
        }

        // ------------------------------------------------------------------------------------------------ mining

        private const string MineDrop = @"{ ""itemName"": ""gold ore"", ""minSlot"": 1, ""maxSlot"": 2, ""minDura"": 3, ""maxDura"": 16, ""bonusChance"": 20, ""maxBonusDura"": 10 }";

        private static string MineSetJson(string name, string drops = null, int regen = 5) =>
            @"{ ""name"": """ + name + @""", ""spotRegenRate"": " + regen + @", ""maxStones"": 80, ""hitRate"": 25, ""dropRate"": 10, ""totalSlots"": 100, ""drops"": [" + (drops ?? MineDrop) + "] }";

        [Fact]
        public void Mining_ParseValidSets()
        {
            var v = new BsValidation();
            var sets = MiningModel.Parse(Json("{\"sets\":[" + MineSetJson("Iron") + "," + MineSetJson("Gems", "") + "]}"), 2, null, ResolveItem, true, v);

            Assert.True(v.Ok, string.Join("; ", v.Errors.Select(e => e.Field + " " + e.Message)));
            Assert.Equal(2, sets.Count);
            Assert.Equal(1, sets[0].Index);
            Assert.Equal(2, sets[1].Index);
            Assert.Equal("GoldOre", sets[0].Drops[0].ItemName);
            Assert.Empty(sets[1].Drops);

            var drop = MiningModel.ToDrop(sets[0].Drops[0]);
            Assert.Equal((byte)16, drop.MaxDura);
            Assert.Equal("GoldOre", drop.ItemName);
        }

        [Fact]
        public void Mining_RejectSentinelsDisorderAndUnknownItems()
        {
            var badDrops = @"{ ""itemName"": ""Unobtainium"", ""minSlot"": 1, ""maxSlot"": 2, ""minDura"": 1, ""maxDura"": 1, ""bonusChance"": 0, ""maxBonusDura"": 0 },
                             { ""itemName"": ""Gold Ore"", ""minSlot"": 255, ""maxSlot"": 255, ""minDura"": 1, ""maxDura"": 1, ""bonusChance"": 0, ""maxBonusDura"": 0 },
                             { ""itemName"": ""Gold Ore"", ""minSlot"": 9, ""maxSlot"": 3, ""minDura"": 1, ""maxDura"": 1, ""bonusChance"": 0, ""maxBonusDura"": 0 },
                             { ""itemName"": ""Gold Ore"", ""minSlot"": 1, ""maxSlot"": 3, ""minDura"": 9, ""maxDura"": 2, ""bonusChance"": 0, ""maxBonusDura"": 0 }";

            var v = new BsValidation();
            MiningModel.Parse(Json("{\"sets\":[" + MineSetJson("Bad", badDrops, regen: 255) + "]}"), 1, null, ResolveItem, true, v);

            var fields = v.Errors.Select(e => e.Field).ToList();
            Assert.Contains("sets[0].spotRegenRate", fields);
            Assert.Contains("sets[0].drops[0].itemName", fields);
            Assert.Contains("sets[0].drops[1].minSlot", fields);
            Assert.Contains("sets[0].drops[2].maxSlot", fields);
            Assert.Contains("sets[0].drops[3].maxDura", fields);
        }

        [Fact]
        public void Mining_SetsUsedByMapsCannotBeRemoved()
        {
            var usage = new Dictionary<int, List<string>> { [2] = new List<string> { "Mine (D401)" } };

            var v = new BsValidation();
            MiningModel.Parse(Json("{\"sets\":[" + MineSetJson("Only") + "]}"), 3, usage, ResolveItem, true, v);
            var error = Assert.Single(v.Errors);
            Assert.Equal("sets", error.Field);
            Assert.Contains("mine 2", error.Message);

            v = new BsValidation();
            MiningModel.Parse(Json("{\"sets\":[" + MineSetJson("One") + "," + MineSetJson("Two") + "]}"), 3, usage, ResolveItem, true, v);
            Assert.True(v.Ok); // only the unused 3rd set goes
        }

        [Fact]
        public void Mining_WarnsAboutUnreachableDrops()
        {
            var drop = @"{ ""itemName"": ""Gold Ore"", ""minSlot"": 150, ""maxSlot"": 160, ""minDura"": 1, ""maxDura"": 1, ""bonusChance"": 0, ""maxBonusDura"": 0 }";
            var v = new BsValidation();
            MiningModel.Parse(Json("{\"sets\":[" + MineSetJson("Far", drop) + "]}"), 1, null, ResolveItem, true, v);

            Assert.True(v.Ok);
            Assert.Contains(v.Warnings, w => w.Contains("never drops"));
        }

        [Fact]
        public void Mining_DtoProjection()
        {
            var set = new MineSet(1) { Name = "Bichon" };
            var dto = MiningModel.ToDto(set, 1, null);

            Assert.Equal("Bichon", dto.Name);
            Assert.Equal(120, dto.TotalSlots);
            Assert.Equal(4, dto.Drops.Count);
            Assert.Equal("GoldOre", dto.Drops[0].ItemName);
            Assert.Empty(dto.UsedBy);
        }

        // ------------------------------------------------------------------------------------------------ spawn tick

        [Fact]
        public void SpawnTick_ParseAndValidate()
        {
            var v = new BsValidation();
            var dto = SpawnTickModel.Parse(Json(@"{ ""baseSpawnRate"": 20, ""options"": [ { ""userCount"": 0, ""delayLoss"": 1 }, { ""userCount"": 50, ""delayLoss"": 0.5 } ] }"), v);

            Assert.True(v.Ok);
            Assert.Equal(20, dto.BaseSpawnRate);
            Assert.Equal(0.5, dto.Options[1].DelayLoss);

            v = new BsValidation();
            SpawnTickModel.Parse(Json(@"{ ""baseSpawnRate"": 0, ""options"": [ { ""userCount"": -1, ""delayLoss"": 0.5 }, { ""userCount"": 5, ""delayLoss"": 0 }, { ""userCount"": 6, ""delayLoss"": 1.5 } ] }"), v);

            var fields = v.Errors.Select(e => e.Field).ToList();
            Assert.Contains("baseSpawnRate", fields);
            Assert.Contains("options[0].userCount", fields);
            Assert.Contains("options[1].delayLoss", fields);
            Assert.Contains("options[2].delayLoss", fields);
        }

        [Fact]
        public void SpawnTick_MultiplierMatchesRespawnTimerRules()
        {
            var options = new[]
            {
                new SpawnOptionDto { UserCount = 0, DelayLoss = 1.0 },
                new SpawnOptionDto { UserCount = 20, DelayLoss = 0.8 },
                new SpawnOptionDto { UserCount = 50, DelayLoss = 0.5 },
            };

            Assert.Equal(1.0, SpawnTickModel.Multiplier(options, 0));
            Assert.Equal(0.8, SpawnTickModel.Multiplier(options, 49));
            Assert.Equal(0.5, SpawnTickModel.Multiplier(options, 500));
            Assert.Equal(1.0, SpawnTickModel.Multiplier(Array.Empty<SpawnOptionDto>(), 10));
        }

        [Fact]
        public void SpawnTick_DtoFromATimer()
        {
            var timer = new RespawnTimer { BaseSpawnRate = 15 };
            timer.Respawn.Add(new RespawnTickOption { UserCount = 10, DelayLoss = 0.75 });

            var dto = SpawnTickModel.ToDto(timer);
            Assert.Equal(15, dto.BaseSpawnRate);
            Assert.Equal(2, dto.Options.Count); // the constructor's default {0 users, 1.0} + ours
            Assert.Equal(0.75, dto.Options[1].DelayLoss);
        }

        // ------------------------------------------------------------------------------------------------ routes

        [Fact]
        public void Routes_RegisterWithoutClashesAndLiteralGroupsWin()
        {
            var router = new WebAdminRouter();
            new BalanceRoutes().Register(router);
            new SystemsRoutes().Register(router);

            Assert.Equal("/api/admin/systems/guilds", router.Match("GET", "/api/admin/systems/guilds").Route.Pattern);
            Assert.Equal("/api/admin/systems/spawntick", router.Match("PUT", "/api/admin/systems/spawntick").Route.Pattern);
            Assert.Equal("/api/admin/systems/names", router.Match("GET", "/api/admin/systems/names").Route.Pattern);

            var generic = router.Match("PUT", "/api/admin/systems/fishing");
            Assert.Equal("/api/admin/systems/{group}", generic.Route.Pattern);
            Assert.Equal("fishing", generic.Values["group"]);

            var hero = router.Match("PUT", "/api/admin/balance/herobasestats/Taoist");
            Assert.Equal("Taoist", hero.Values["class"]);

            Assert.True(router.Match("DELETE", "/api/admin/balance/randomitemstats/3").Success);
            Assert.False(router.Match("DELETE", "/api/admin/balance/randomitemstats/x").Success);

            Assert.All(router.Routes, r => Assert.Equal(RouteAuth.Admin, r.Auth));
        }
    }
}
