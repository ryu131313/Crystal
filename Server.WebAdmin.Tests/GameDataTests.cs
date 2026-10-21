using System.Drawing;
using System.Text;
using System.Text.Json;
using Server.MirDatabase;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.Tests
{
    /// <summary>Game-data grid engine (B8): codec, rev, registry/schema, apply, delete, lookups. Pure: no Envir, no Settings.</summary>
    public class GameDataTests
    {
        // ------------------------------------------------------------------ helpers

        private static JsonElement Json(string json)
        {
            using (var doc = JsonDocument.Parse(json))
                return doc.RootElement.Clone();
        }

        private static List<JsonElement> Rows(string json) => Json(json).EnumerateArray().ToList();

        private static GameDataContext Context()
        {
            var ctx = new GameDataContext
            {
                MineSetCount = () => 2,
                MineSetNames = () => new[] { "Iron", "Gold" },
            };

            var sword = new ItemInfo { Index = 1, Name = "WoodenSword", Type = ItemType.Weapon, Price = 100 };
            sword.Stats[Stat.MinDC] = 2;
            ctx.Items.Add(sword);
            ctx.Items.Add(new ItemInfo { Index = 2, Name = "HPPotion", Type = ItemType.Potion, Price = 30, StackSize = 50 });
            ctx.Items.Add(new ItemInfo { Index = 3, Name = "Unused", Type = ItemType.Nothing });

            ctx.Monsters.Add(new MonsterInfo { Index = 1, Name = "Hen", Level = 1 });
            ctx.Monsters.Add(new MonsterInfo { Index = 2, Name = "Deer", Level = 3 });
            ctx.Monsters.Add(new MonsterInfo { Index = 3, Name = "Lonely", Level = 9 });

            var bichon = new MapInfo { Index = 1, FileName = "0", Title = "BichonProvince" };
            bichon.SafeZones.Add(new SafeZoneInfo { Info = bichon, Location = new Point(10, 10), Size = 5, StartPoint = true });
            bichon.SafeZones.Add(new SafeZoneInfo { Info = bichon, Location = new Point(20, 20), Size = 6 });
            bichon.Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = new Point(50, 50), Count = 10, RespawnIndex = 7 });
            bichon.Movements.Add(new MovementInfo { MapIndex = 2, Source = new Point(1, 1), Destination = new Point(2, 2) });
            ctx.Maps.Add(bichon);
            ctx.Maps.Add(new MapInfo { Index = 2, FileName = "1", Title = "Woomyon" });

            ctx.Quests.Add(new QuestInfo { Index = 1, Name = "First" });
            ctx.Quests.Add(new QuestInfo { Index = 2, Name = "Second", RequiredQuest = 1 });

            ctx.Npcs.Add(new NPCInfo { Index = 1, Name = "Merchant", FileName = "Bichon\\Merchant", MapIndex = 1 });
            ctx.Npcs[0].CollectQuestIndexes.Add(2);

            ctx.GameShop.Add(new GameShopItem { GIndex = 1, ItemIndex = 2, Count = 1, Info = ctx.Items[1] });

            var conquest = new ConquestInfo { Index = 1, Name = "Sabuk", MapIndex = 2 };
            conquest.ConquestGuards.Add(new ConquestArcherInfo { Index = 1, Name = "Guard", MobIndex = 2 });
            conquest.GuardIndex = 1;
            ctx.Conquests.Add(conquest);

            ctx.Magics.Add(new MagicInfo { Name = "Fencing", Spell = Spell.Fencing, Level1 = 7 });
            var dragon = new DragonInfo();
            ctx.Dragon = () => dragon;
            return ctx;
        }

        private static GameDataTable T(string endpoint) => GameDataRegistry.Get(endpoint);

        private static Dictionary<string, object> WireRow(GameDataContext ctx, string endpoint, int index)
        {
            var table = T(endpoint);
            return table.ToWire(table.Rows(ctx).First(r => r.Index == index));
        }

        // ------------------------------------------------------------------ codec

        [Fact]
        public void Convert_Integers_EnforceClrRange_AndWholeNumbers()
        {
            Assert.True(GameDataCodec.TryConvert(Json("255"), typeof(byte), false, out var b, out _));
            Assert.Equal((byte)255, b);
            Assert.False(GameDataCodec.TryConvert(Json("256"), typeof(byte), false, out _, out var err));
            Assert.Contains("0 and 255", err);
            Assert.False(GameDataCodec.TryConvert(Json("1.5"), typeof(int), false, out _, out err));
            Assert.Contains("whole", err);
            Assert.True(GameDataCodec.TryConvert(Json("\"42\""), typeof(ushort), false, out var u, out _));
            Assert.Equal((ushort)42, u);
            Assert.False(GameDataCodec.TryConvert(Json("-1"), typeof(uint), false, out _, out _));
            Assert.False(GameDataCodec.TryConvert(Json("\"abc\""), typeof(int), false, out _, out _));
        }

        [Fact]
        public void Convert_Enums_ByNameOrNumber_RejectNonMembers()
        {
            Assert.True(GameDataCodec.TryConvert(Json("\"weapon\""), typeof(ItemType), false, out var t, out _));
            Assert.Equal(ItemType.Weapon, t);
            Assert.True(GameDataCodec.TryConvert(Json("4"), typeof(ItemType), false, out t, out _));
            Assert.Equal(ItemType.Helmet, t);
            Assert.False(GameDataCodec.TryConvert(Json("3"), typeof(ItemType), false, out _, out var err)); // ItemType has no 3
            Assert.Contains("not a member", err);
            Assert.False(GameDataCodec.TryConvert(Json("\"Sword\""), typeof(ItemType), false, out _, out _));
        }

        [Fact]
        public void Convert_Flags_UnsignedWire_NamesAndUnknownBits()
        {
            // BindMode is a short: NoHero = -32768 travels as 32768 so the grid's bit editor sees a positive bit.
            var bind = BindMode.NoHero | BindMode.DontDrop;
            var wire = GameDataCodec.ToWire(typeof(BindMode), bind, true);
            Assert.Equal(32770L, wire);
            Assert.True(GameDataCodec.TryConvert(Json("32770"), typeof(BindMode), true, out var back, out _));
            Assert.Equal(bind, back);
            Assert.True(GameDataCodec.TryConvert(Json("-32766"), typeof(BindMode), true, out back, out _));
            Assert.Equal(bind, back);

            Assert.True(GameDataCodec.TryConvert(Json("\"Warrior, Wizard\""), typeof(RequiredClass), true, out var cls, out _));
            Assert.Equal(RequiredClass.Warrior | RequiredClass.Wizard, cls);
            Assert.False(GameDataCodec.TryConvert(Json("4"), typeof(RequiredGender), true, out _, out var err));
            Assert.Contains("not members", err);
            Assert.False(GameDataCodec.TryConvert(Json("\"Warrior, Knight\""), typeof(RequiredClass), true, out _, out _));
        }

        [Fact]
        public void Convert_ListsBoolsStringsDates()
        {
            Assert.True(GameDataCodec.TryConvert(Json("\"1, 2 3;4\""), typeof(List<int>), false, out var list, out _));
            Assert.Equal(new[] { 1, 2, 3, 4 }, (List<int>)list);
            Assert.True(GameDataCodec.TryConvert(Json("[5,6]"), typeof(List<int>), false, out list, out _));
            Assert.Equal(new[] { 5, 6 }, (List<int>)list);
            Assert.True(GameDataCodec.TryConvert(Json("\"\""), typeof(List<int>), false, out list, out _));
            Assert.Empty((List<int>)list);
            Assert.False(GameDataCodec.TryConvert(Json("\"1, x\""), typeof(List<int>), false, out _, out _));
            Assert.Equal("1, 2", GameDataCodec.ToWire(typeof(List<int>), new List<int> { 1, 2 }, false));

            Assert.True(GameDataCodec.TryConvert(Json("\"1\""), typeof(bool), false, out var flag, out _));
            Assert.Equal(true, flag);
            Assert.False(GameDataCodec.TryConvert(Json("\"maybe\""), typeof(bool), false, out _, out _));

            Assert.True(GameDataCodec.TryConvert(Json("null"), typeof(string), false, out var s, out _));
            Assert.Equal(string.Empty, s);

            Assert.True(GameDataCodec.TryConvert(Json("\"2024-05-06T07:08:09\""), typeof(DateTime), false, out var dt, out _));
            Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), dt);
            Assert.Equal("2024-05-06T07:08:09", GameDataCodec.ToWire(typeof(DateTime), dt, false));
        }

        [Fact]
        public void Rev_IsOrderIndependent_IgnoresMeta_AndTracksValues()
        {
            var a = new Dictionary<string, object> { ["Index"] = 1, ["Name"] = "Hen", ["Level"] = (ushort)3 };
            var b = new Dictionary<string, object> { ["Level"] = (ushort)3, ["__rev"] = "x", ["Name"] = "Hen", ["Index"] = 1 };
            Assert.Equal(GameDataCodec.Rev(a), GameDataCodec.Rev(b));
            Assert.Equal(16, GameDataCodec.Rev(a).Length);

            b["Level"] = (ushort)4;
            Assert.NotEqual(GameDataCodec.Rev(a), GameDataCodec.Rev(b));
        }

        // ------------------------------------------------------------------ registry / schema

        [Fact]
        public void Registry_HasEveryContractEndpoint_WithParents()
        {
            var expected = new[]
            {
                "items", "monsters", "magic", "npcs", "quests", "maps", "mapsafezones", "maprespawns", "mapmovements", "mapminezones",
                "gameshop", "conquests", "conquestguards", "conquestgates", "conquestwalls", "conquestsieges", "conquestflags",
                "conquestcontrolpoints", "dragon",
            };
            Assert.Equal(expected.OrderBy(x => x), GameDataRegistry.Tables.Select(t => t.Endpoint).OrderBy(x => x));

            Assert.Equal("MapIndex", T("maprespawns").ParentField);
            Assert.Equal("maps", T("mapminezones").ParentEndpoint);
            Assert.Equal("ConquestIndex", T("conquestcontrolpoints").ParentField);
            Assert.False(T("magic").Insertable);
            Assert.False(T("magic").Deletable);
            Assert.False(T("dragon").Insertable);
            Assert.Null(GameDataRegistry.Get("accounts"));
        }

        [Fact]
        public void Columns_FlattenStatsPointsListsArrays_AndSkipRuntimeFields()
        {
            var items = T("items");
            Assert.NotNull(items.Column("Stat_MinDC"));
            Assert.Null(items.Column("Stat_Unknown"));
            Assert.Null(items.Column("RandomStats"));
            Assert.Null(items.Column("Index"));
            Assert.True(items.Column("Bind").IsFlags);
            Assert.True(items.Column("RequiredClass").IsFlags);
            Assert.Equal(GameDataKinds.Number, items.Column("Light").Kind);

            var monsters = T("monsters");
            Assert.Null(monsters.Column("Drops"));
            Assert.Null(monsters.Column("HasSpawnScript"));
            Assert.True(monsters.Column("Level").LiveSafe);
            Assert.True(monsters.Column("Stat_HP").LiveSafe);
            Assert.False(monsters.Column("Name").LiveSafe);
            Assert.True(monsters.Column("DropPath").LiveSafe);    // LiveDb: hooked (the drop file is reloaded)
            Assert.False(monsters.Column("AI").LiveSafe);         // LiveDb: restart-only

            var npcs = T("npcs");
            Assert.NotNull(npcs.Column("Location_X"));
            Assert.NotNull(npcs.Column("Location_Y"));
            Assert.True(npcs.Column("CollectQuestIndexes").IsList);
            Assert.Null(npcs.Column("Colour"));
            Assert.Null(npcs.Column("Sabuk"));

            var movements = T("mapmovements");
            Assert.True(movements.Column("MapIndex").IsParentField);
            Assert.Equal("maps", movements.Column("DestinationMapIndex").Lookup);

            Assert.True(T("maps").Column("WeatherParticles").IsFlags);
            Assert.NotNull(T("dragon").Column("Exps_12"));
            Assert.Null(T("dragon").Column("Level"));
            Assert.True(T("conquestguards").Column("LocalIndex").ReadOnly);
            Assert.Null(T("quests").Column("GoldReward")); // parsed from the quest txt, not persisted
        }

        [Fact]
        public void Schema_PublishesKindsMembersLookupsAndPresets()
        {
            var json = JsonSerializer.Serialize(GameDataSchema.Build(T("items")), WebAdminJson.Options);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("items", root.GetProperty("endpoint").GetString());
            Assert.True(root.GetProperty("insertable").GetBoolean());

            var props = root.GetProperty("properties").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString(), p => p);
            Assert.True(props["Index"].GetProperty("readOnly").GetBoolean());
            Assert.Equal("enum", props["Type"].GetProperty("kind").GetString());
            var helmet = props["Type"].GetProperty("members").EnumerateArray().First(m => m.GetProperty("name").GetString() == "Helmet");
            Assert.Equal(4, helmet.GetProperty("value").GetInt32());
            Assert.True(props["Bind"].GetProperty("flags").GetBoolean());
            var noHero = props["Bind"].GetProperty("members").EnumerateArray().First(m => m.GetProperty("name").GetString() == "NoHero");
            Assert.Equal(32768, noHero.GetProperty("value").GetInt32());
            Assert.Equal("Stats", props["Stat_MaxAC"].GetProperty("group").GetString());
            Assert.Equal("bool", props["CanMine"].GetProperty("kind").GetString());
            Assert.True(props["ToolTip"].GetProperty("multiline").GetBoolean());

            var presets = root.GetProperty("presets");
            Assert.Contains(presets.GetProperty("stats").EnumerateArray(), e => e.GetString() == "Stat_MinDC");
            Assert.DoesNotContain(presets.GetProperty("basic").EnumerateArray(), e => e.GetString() == "Stat_MinDC");

            var npcSchema = JsonSerializer.Serialize(GameDataSchema.Build(T("npcs")), WebAdminJson.Options);
            using var npcDoc = JsonDocument.Parse(npcSchema);
            var npcProps = npcDoc.RootElement.GetProperty("properties").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString(), p => p);
            Assert.Equal("reference", npcProps["MapIndex"].GetProperty("kind").GetString());
            Assert.Equal("maps", npcProps["MapIndex"].GetProperty("lookup").GetString());
            Assert.Equal("quests", npcProps["FinishQuestIndexes"].GetProperty("listLookup").GetString());
        }

        [Fact]
        public void Rows_UseDocumentedKeys()
        {
            var ctx = Context();
            Assert.Equal(new[] { 10000, 10001 }, T("mapsafezones").Rows(ctx).Select(r => r.Index));
            Assert.Equal(new[] { 7 }, T("maprespawns").Rows(ctx).Select(r => r.Index));
            Assert.Equal(new[] { (int)Spell.Fencing }, T("magic").Rows(ctx).Select(r => r.Index));
            Assert.Equal(new[] { 1 }, T("dragon").Rows(ctx).Select(r => r.Index));
            Assert.Equal(new[] { 10000 }, T("conquestguards").Rows(ctx).Select(r => r.Index));

            var zone = WireRow(ctx, "mapsafezones", 10001);
            Assert.Equal(1, zone["MapIndex"]);
            Assert.Equal(20, zone["Location_X"]);
            Assert.True(zone.ContainsKey("__rev"));

            var move = WireRow(ctx, "mapmovements", 10000);
            Assert.Equal(1, move["MapIndex"]);
            Assert.Equal(2, move["DestinationMapIndex"]);
        }

        [Fact]
        public void Query_FiltersSortsAndPages()
        {
            var ctx = Context();
            var rows = GameDataEngine.Query(ctx, T("items"), new[] { new KeyValuePair<string, string>("type", "1") }, null, null, null, 1, 0, out var total);
            Assert.Equal(1, total);
            Assert.Equal("WoodenSword", rows[0]["Name"]);

            rows = GameDataEngine.Query(ctx, T("items"), null, "potion", null, null, 1, 0, out total);
            Assert.Equal(1, total);

            rows = GameDataEngine.Query(ctx, T("items"), null, null, "Price", "desc", 1, 2, out total);
            Assert.Equal(3, total);
            Assert.Equal(2, rows.Count);
            Assert.Equal("WoodenSword", rows[0]["Name"]);

            rows = GameDataEngine.Query(ctx, T("mapsafezones"), new[] { new KeyValuePair<string, string>("MapIndex", "2") }, null, null, null, 1, 0, out total);
            Assert.Equal(0, total);
            Assert.Empty(rows);
        }

        // ------------------------------------------------------------------ apply

        [Fact]
        public void Apply_UpdatesChangedFields_WithRevCheck()
        {
            var ctx = Context();
            var row = WireRow(ctx, "items", 1);
            var rev = (string)row["__rev"];

            var result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":1,\"Price\":250,\"Stat_MinDC\":0,\"Stat_MaxDC\":5,\"__rev\":\"" + rev + "\"}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Empty(result.Conflicts);
            Assert.Equal(250u, ctx.Items[0].Price);
            Assert.False(ctx.Items[0].Stats.Values.ContainsKey(Stat.MinDC)); // 0 removes the stat
            Assert.Equal(5, ctx.Items[0].Stats[Stat.MaxDC]);
            var changes = result.Rows[0].Changes.ToDictionary(c => c.Field);
            Assert.Equal(100u, changes["Price"].Before);
            Assert.Equal(250u, changes["Price"].After);
            Assert.Equal(3, changes.Count);

            // The old rev is now stale.
            result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":1,\"Price\":300,\"__rev\":\"" + rev + "\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Single(result.Conflicts);
            Assert.Equal(250u, ctx.Items[0].Price);

            // noRev overrides the check (import).
            result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":1,\"Price\":300,\"__rev\":\"" + rev + "\"}]"), false, true);
            Assert.Equal(1, result.Applied);
            Assert.Equal(300u, ctx.Items[0].Price);
        }

        [Fact]
        public void Apply_DryRun_ChangesNothing_ButReportsPreview()
        {
            var ctx = Context();
            var result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":2,\"Price\":31},{\"Index\":0,\"Name\":\"NewThing\"}]"), true, false);
            Assert.True(result.DryRun);
            Assert.Equal(2, result.Applied);
            Assert.Equal(30u, ctx.Items[1].Price);
            Assert.Equal(3, ctx.Items.Count);
            Assert.Equal("insert", result.Rows[1].Kind);
            Assert.Equal(0, result.Rows[1].NewIndex);
        }

        [Fact]
        public void Apply_Validation_SkipsBadRows_ReportsIgnoredFields()
        {
            var ctx = Context();
            var result = GameDataEngine.Apply(ctx, T("items"), Rows(
                "[{\"Index\":1,\"Price\":-5},{\"Index\":2,\"Type\":3},{\"Index\":3,\"Price\":7,\"ItemName\":\"x\",\"Weight\":12}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal(2, result.Validation.Count);
            Assert.Contains("ItemName", result.IgnoredFields);
            Assert.Equal(100u, ctx.Items[0].Price);
            Assert.Equal(ItemType.Potion, ctx.Items[1].Type);
            Assert.Equal(7u, ctx.Items[2].Price);
            Assert.Equal((byte)12, ctx.Items[2].Weight);
        }

        [Fact]
        public void Apply_Insert_AllocatesIndex_AndEnforcesUniqueNames()
        {
            var ctx = Context();
            var result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":0,\"Name\":\"IronSword\",\"Type\":1},{\"Index\":-1,\"Name\":\"hppotion\"}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Single(result.Validation);
            var created = ctx.Items.Single(i => i.Name == "IronSword");
            Assert.Equal(4, created.Index);
            Assert.Equal(255, created.RandomStatsId);
            Assert.Equal(4, result.Rows[0].NewIndex);
            Assert.Equal("insert", result.Rows[0].Kind);
        }

        [Fact]
        public void Apply_NameChange_ReportsRename()
        {
            var ctx = Context();
            var result = GameDataEngine.Apply(ctx, T("monsters"), Rows("[{\"Index\":1,\"Name\":\"Chicken\"}]"), false, false);
            Assert.Equal(1, result.Applied);
            var json = JsonSerializer.Serialize(result.Renames, WebAdminJson.Options);
            Assert.Contains("\"old\":\"Hen\"", json);
            Assert.Contains("\"new\":\"Chicken\"", json);

            // Renaming to another monster's name is refused.
            result = GameDataEngine.Apply(ctx, T("monsters"), Rows("[{\"Index\":2,\"Name\":\"chicken\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Single(result.Validation);
        }

        [Fact]
        public void Apply_ForeignKeys_AreChecked_AndGameShopRebinds()
        {
            var ctx = Context();
            var shop = T("gameshop");
            var result = GameDataEngine.Apply(ctx, shop, Rows("[{\"Index\":0,\"ItemIndex\":99}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Contains("does not exist", JsonSerializer.Serialize(result.Validation, WebAdminJson.Options));

            result = GameDataEngine.Apply(ctx, shop, Rows("[{\"Index\":0,\"ItemIndex\":1,\"Count\":2}]"), false, false);
            Assert.Equal(1, result.Applied);
            var entry = ctx.GameShop.Last();
            Assert.Equal(2, entry.GIndex);
            Assert.Same(ctx.Items[0], entry.Info);

            result = GameDataEngine.Apply(ctx, shop, Rows("[{\"Index\":1,\"ItemIndex\":3}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Same(ctx.Items[2], ctx.GameShop[0].Info);

            result = GameDataEngine.Apply(ctx, shop, Rows("[{\"Index\":0}]"), false, false);
            Assert.Equal(0, result.Applied); // ItemIndex is required on insert

            result = GameDataEngine.Apply(ctx, T("npcs"), Rows("[{\"Index\":1,\"FinishQuestIndexes\":\"1, 5\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            result = GameDataEngine.Apply(ctx, T("npcs"), Rows("[{\"Index\":1,\"FinishQuestIndexes\":\"1, 2\"}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal(new[] { 1, 2 }, ctx.Npcs[0].FinishQuestIndexes);

            result = GameDataEngine.Apply(ctx, T("mapminezones"), Rows("[{\"Index\":0,\"MapIndex\":2,\"Mine\":3}]"), false, false);
            Assert.Equal(0, result.Applied); // only 2 mine sets
            result = GameDataEngine.Apply(ctx, T("mapminezones"), Rows("[{\"Index\":0,\"MapIndex\":2,\"Mine\":2}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal(20000, result.Rows[0].NewIndex);
        }

        [Fact]
        public void Apply_ChildRows_InsertIntoParent_AllocateRespawnIndex_ParentIsFixed()
        {
            var ctx = Context();
            var respawns = T("maprespawns");

            var result = GameDataEngine.Apply(ctx, respawns, Rows("[{\"Index\":0,\"MonsterIndex\":2,\"Count\":3}]"), false, false);
            Assert.Equal(0, result.Applied); // MapIndex required for a new child row

            result = GameDataEngine.Apply(ctx, respawns, Rows("[{\"Index\":0,\"MapIndex\":2,\"MonsterIndex\":2,\"Count\":3,\"Location_X\":40}]"), false, false);
            Assert.Equal(1, result.Applied);
            var added = ctx.Maps[1].Respawns.Single();
            Assert.Equal(8, added.RespawnIndex);
            Assert.Equal(40, added.Location.X);
            Assert.Equal(8, result.Rows[0].NewIndex);

            result = GameDataEngine.Apply(ctx, respawns, Rows("[{\"Index\":7,\"MapIndex\":2,\"Count\":1}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Contains("cannot be changed", JsonSerializer.Serialize(result.Validation, WebAdminJson.Options));

            result = GameDataEngine.Apply(ctx, T("mapsafezones"), Rows("[{\"Index\":10001,\"Size\":9,\"MapIndex\":1}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal((ushort)9, ctx.Maps[0].SafeZones[1].Size);

            result = GameDataEngine.Apply(ctx, T("conquestguards"), Rows("[{\"Index\":0,\"ConquestIndex\":1,\"MobIndex\":1}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal(2, ctx.Conquests[0].ConquestGuards[1].Index);
            Assert.Equal(2, ctx.Conquests[0].GuardIndex);
            Assert.Equal(10001, result.Rows[0].NewIndex);
        }

        [Fact]
        public void Apply_ReadOnlyAndNotInsertable()
        {
            var ctx = Context();
            var result = GameDataEngine.Apply(ctx, T("magic"), Rows("[{\"Index\":0,\"Name\":\"X\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Single(result.Validation);

            result = GameDataEngine.Apply(ctx, T("magic"), Rows("[{\"Index\":" + (int)Spell.Fencing + ",\"Spell\":5,\"Level1\":8}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Contains("Spell", result.IgnoredFields);
            Assert.Equal(Spell.Fencing, ctx.Magics[0].Spell);
            Assert.Equal((byte)8, ctx.Magics[0].Level1);

            result = GameDataEngine.Apply(ctx, T("dragon"), Rows("[{\"Index\":1,\"Exps_2\":12345,\"Location_Y\":99}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal(12345, ctx.Dragon().Exps[1]);
            Assert.Equal(99, ctx.Dragon().Location.Y);
        }

        [Fact]
        public void Apply_ValidatingOnScratchCopy_NeverLeaksIntoTheRecord()
        {
            var ctx = Context();
            // Stat change is valid but the name collides: nothing of the row may apply.
            var result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":1,\"Stat_MaxAC\":9,\"Name\":\"HPPotion\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Equal(0, ctx.Items[0].Stats[Stat.MaxAC]);
            Assert.Equal("WoodenSword", ctx.Items[0].Name);
        }

        // ------------------------------------------------------------------ delete

        [Fact]
        public void Delete_GuardsReferences_AndSupportsDryRun()
        {
            var ctx = Context();
            var result = GameDataEngine.Delete(ctx, T("items"), new[] { 2, 3, 99 }, true);
            Assert.Equal(1, result.Deleted);
            Assert.Equal(2, result.Rejects.Count);
            Assert.Equal(3, ctx.Items.Count); // dry run
            var json = JsonSerializer.Serialize(result.Rejects, WebAdminJson.Options);
            Assert.Contains("in-use", json);
            Assert.Contains("not-found", json);

            result = GameDataEngine.Delete(ctx, T("items"), new[] { 3 }, false);
            Assert.Equal(1, result.Deleted);
            Assert.DoesNotContain(ctx.Items, i => i.Index == 3);

            Assert.Single(GameDataEngine.Delete(ctx, T("monsters"), new[] { 1 }, false).Rejects); // respawn uses Hen
            Assert.Single(GameDataEngine.Delete(ctx, T("monsters"), new[] { 2 }, false).Rejects); // conquest guard uses Deer
            Assert.Equal(1, GameDataEngine.Delete(ctx, T("monsters"), new[] { 3 }, false).Deleted);

            Assert.Single(GameDataEngine.Delete(ctx, T("maps"), new[] { 1 }, false).Rejects);  // NPC on map 1
            Assert.Single(GameDataEngine.Delete(ctx, T("maps"), new[] { 2 }, false).Rejects);  // movement + conquest
            Assert.Single(GameDataEngine.Delete(ctx, T("quests"), new[] { 1 }, false).Rejects); // quest 2 requires it
            Assert.Single(GameDataEngine.Delete(ctx, T("quests"), new[] { 2 }, false).Rejects); // NPC lists it
        }

        [Fact]
        public void Delete_PositionKeys_ResolveBeforeRemoving()
        {
            var ctx = Context();
            var result = GameDataEngine.Delete(ctx, T("mapsafezones"), new[] { 10000, 10001 }, false);
            Assert.Equal(2, result.Deleted);
            Assert.Empty(ctx.Maps[0].SafeZones);

            var shop = GameDataEngine.Delete(ctx, T("gameshop"), new[] { 1 }, false);
            Assert.Equal(1, shop.Deleted);
            Assert.Empty(ctx.GameShop);
        }

        // ------------------------------------------------------------------ lookups / state

        [Fact]
        public void Lookups_EmptyFirst_SortedAndDisambiguated()
        {
            var options = GameDataLookups.Finish(new[]
            {
                new GameDataLookupItem { Value = 5, Label = "Zeta" },
                new GameDataLookupItem { Value = 2, Label = "alpha" },
                new GameDataLookupItem { Value = 9, Label = "Alpha", Detail = "boss" },
                new GameDataLookupItem { Value = 3, Label = "" },
            });

            Assert.Equal(0, options[0]["value"]);
            Assert.Equal(string.Empty, options[0]["label"]);
            Assert.Equal(new object[] { "#3", "2 — alpha", "9 — Alpha", "Zeta" }, options.Skip(1).Select(o => o["label"]).ToArray());
            Assert.Equal("boss", options[3]["detail"]);

            var ctx = Context();
            Assert.Equal(new[] { "Iron", "Gold" }, GameDataLookups.Raw(ctx, "minesets").Select(o => o.Label));
            Assert.Equal(new[] { 1, 2 }, GameDataLookups.Raw(ctx, "minesets").Select(o => o.Value));
            Assert.Equal("0 — BichonProvince", GameDataLookups.Raw(ctx, "maps")[0].Label);
            Assert.Null(GameDataLookups.Raw(ctx, "guilds"));
            Assert.True(GameDataLookups.IsKnown("Magics"));
        }

        [Fact]
        public void Unsaved_TracksAreasUntilSavedOrDiscarded()
        {
            var clock = new TestClock();
            var unsaved = new GameDataUnsaved(clock.Now);
            unsaved.Note("items", 2);
            unsaved.Note("monsters", 1);
            unsaved.Note("items", 1);
            Assert.Equal(4, unsaved.Changes);

            var areas = unsaved.MarkSaved();
            Assert.Equal(new[] { "items", "monsters" }, areas);
            Assert.Equal(0, unsaved.Changes);
            Assert.Equal(clock.UtcNow, unsaved.Snapshot(false)["lastSavedUtc"]);

            unsaved.Note("maps", 1);
            unsaved.Discard();
            Assert.Equal(0, unsaved.Changes);
        }

        [Fact]
        public void SafeRelativeScriptName_RejectsEscapes()
        {
            Assert.True(GameDataRegistry.IsSafeRelativeScriptName("Bichon\\Merchant"));
            Assert.True(GameDataRegistry.IsSafeRelativeScriptName(""));
            Assert.False(GameDataRegistry.IsSafeRelativeScriptName("..\\..\\Configs\\Setup"));
            Assert.False(GameDataRegistry.IsSafeRelativeScriptName("C:\\Windows\\x"));
            Assert.False(GameDataRegistry.IsSafeRelativeScriptName("\\abs"));
        }

        // ------------------------------------------------------------------ review fixes

        [Fact]
        public void Apply_DeletedRowWithRev_IsAConflict_NotAReinsert()
        {
            var ctx = Context();
            var rev = (string)WireRow(ctx, "items", 3)["__rev"];
            ctx.Items.RemoveAt(2); // another admin deleted item #3

            var result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":3,\"Name\":\"Unused\",\"Price\":9,\"__rev\":\"" + rev + "\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Single(result.Conflicts);
            Assert.Equal(2, ctx.Items.Count);

            // Position-keyed child rows: map 1 safe zone 10001 disappears when zone 10000 is deleted.
            var zoneRev = (string)WireRow(ctx, "mapsafezones", 10001)["__rev"];
            ctx.Maps[0].SafeZones.RemoveAt(0);
            result = GameDataEngine.Apply(ctx, T("mapsafezones"), Rows("[{\"Index\":10001,\"MapIndex\":1,\"Size\":9,\"__rev\":\"" + zoneRev + "\"}]"), false, false);
            Assert.Equal(0, result.Applied);
            Assert.Single(result.Conflicts);
            Assert.Single(ctx.Maps[0].SafeZones);

            // Without a __rev (import) an unknown positive Index is still a new row.
            result = GameDataEngine.Apply(ctx, T("items"), Rows("[{\"Index\":3,\"Name\":\"Imported\"}]"), false, false);
            Assert.Equal(1, result.Applied);
            Assert.Equal("insert", result.Rows[0].Kind);
        }

        [Fact]
        public void Schema_Defaults_AreTheServersNewRowValues()
        {
            var ctx = Context();
            var items = GameDataSchema.Defaults(T("items"), ctx);
            Assert.Equal(255, Convert.ToInt32(items["RandomStatsId"]));

            var monster = new MonsterInfo();
            var monsters = GameDataSchema.Defaults(T("monsters"), ctx);
            Assert.Equal(Convert.ToInt64(monster.AttackSpeed), Convert.ToInt64(monsters["AttackSpeed"]));
            Assert.Equal(Convert.ToInt64(monster.MoveSpeed), Convert.ToInt64(monsters["MoveSpeed"]));

            var zones = GameDataSchema.Defaults(T("mapsafezones"), ctx);
            Assert.Equal(10, Convert.ToInt32(zones["Size"]));
            Assert.False(zones.ContainsKey("MapIndex"));

            Assert.Null(GameDataSchema.Defaults(T("magic"), ctx));

            var json = JsonSerializer.Serialize(GameDataSchema.GetWithDefaults("items", ctx), WebAdminJson.Options);
            Assert.Contains("\"defaults\"", json);
        }
    }

    /// <summary>Rename propagation (item / monster names in drop, recipe, quest and NPC files).</summary>
    public class GameDataRenameTests
    {
        private static List<string> L(params string[] lines) => lines.ToList();

        [Fact]
        public void Recipe_SkipsRecipeKeys_RewritesIngredientsAndTools()
        {
            var lines = L("[Recipe]", "WoodenSword 1", "[Ingredients]", "WoodenSword 2", "Other 1", "[Tools]", "woodensword");
            Assert.Equal(2, GameDataRename.RewriteRecipe(lines, "WoodenSword", "OakSword"));
            Assert.Equal("WoodenSword 1", lines[1]);
            Assert.Equal("OakSword 2", lines[3]);
            Assert.Equal("OakSword", lines[6]);
        }

        [Fact]
        public void Quest_ItemAndKillSections()
        {
            var lines = L("[@DESCRIPTION]", "Bring a Hen feather", "[@ITEMTASKS]", "HenFeather 5 \"Hen feathers\"", "", "HenFeather 1",
                "[@KILLTASKS]", "Hen 10", "//", "Hen 3");
            Assert.Equal(2, GameDataRename.RewriteQuest(lines, "HenFeather", "Feather", false));
            Assert.Equal("Feather 5 \"Hen feathers\"", lines[3]);
            Assert.Equal("Feather 1", lines[5]);
            Assert.Equal(1, GameDataRename.RewriteQuest(lines, "Hen", "Chicken", true));
            Assert.Equal("Chicken 10", lines[7]);
            Assert.Equal("Hen 3", lines[9]);
            Assert.Equal("Bring a Hen feather", lines[1]);
        }

        [Fact]
        public void Npc_TradeAndRecipeSections()
        {
            var lines = L("[@MAIN]", "WoodenSword", "[Trade]", "WoodenSword", "HPPotion 5", "[@BUY]", "WoodenSword", "[RECIPE]", "WoodenSword");
            Assert.Equal(2, GameDataRename.RewriteNpc(lines, "WoodenSword", "OakSword"));
            Assert.Equal("WoodenSword", lines[1]);
            Assert.Equal("OakSword", lines[3]);
            Assert.Equal("WoodenSword", lines[6]);
            Assert.Equal("OakSword", lines[8]);
        }

        [Fact]
        public void TextFile_RoundTripsEndingsAndBom()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a\r\nb\nc")).ToArray();
            var file = GameDataTextFile.Parse(bytes);
            Assert.Equal(new[] { "a", "b", "c" }, file.Lines);
            Assert.Equal(bytes, file.Serialize());

            var latin = new byte[] { 0x41, 0xE9, 0x0D, 0x0A }; // "Aé" in Latin-1: invalid UTF-8, must round-trip byte for byte
            Assert.Equal(latin, GameDataTextFile.Parse(latin).Serialize());
        }

        [Fact]
        public void CheckName_RejectsEmptyAndSpaces()
        {
            Assert.Null(GameDataRename.CheckName("OakSword"));
            Assert.NotNull(GameDataRename.CheckName(" "));
            Assert.NotNull(GameDataRename.CheckName("Oak Sword"));
        }

        [Fact]
        public void PlanAndExecute_RewritesFiles_RenamesRecipe_WithBackups()
        {
            var root = Path.Combine(Path.GetTempPath(), "mircraft-gamedata-" + Guid.NewGuid().ToString("N"));
            try
            {
                var drops = Path.Combine(root, "Drops");
                var recipes = Path.Combine(root, "Recipe");
                var quests = Path.Combine(root, "Quests");
                var npcs = Path.Combine(root, "NPCs", "Town");
                Directory.CreateDirectory(drops);
                Directory.CreateDirectory(recipes);
                Directory.CreateDirectory(quests);
                Directory.CreateDirectory(npcs);
                File.WriteAllText(Path.Combine(drops, "Hen.txt"), "1/10 WoodenSword\r\n1/2 Other\r\n");
                File.WriteAllText(Path.Combine(recipes, "WoodenSword.txt"), "[Recipe]\nAmount 1\n[Ingredients]\nLog 2\n");
                File.WriteAllText(Path.Combine(quests, "Q1.txt"), "[@FIXEDREWARDS]\nWoodenSword 1\n");
                File.WriteAllText(Path.Combine(npcs, "Smith.txt"), "[TRADE]\nWoodenSword\n");
                File.WriteAllText(Path.Combine(quests, "Untouched.txt"), "[@KILLTASKS]\nHen 1\n");

                var roots = new Dictionary<string, string>
                {
                    ["recipes"] = recipes,
                    ["quests"] = quests,
                    ["npcs"] = Path.Combine(root, "NPCs"),
                };

                var warnings = new List<string>();
                var plan = GameDataRename.Plan("items", "WoodenSword", "OakSword", roots, warnings);
                Assert.Empty(warnings);
                Assert.Equal(3, plan.Count); // recipe, quest, NPC: drops follow renames by Index (Drops-Tables-Plan phase 2)
                Assert.Contains(plan, f => f.Root == "npcs" && f.Path == "Town/Smith.txt" && f.Replacements == 1);
                var recipe = plan.Single(f => f.Root == "recipes");
                Assert.Equal("OakSword.txt", recipe.RenameTo);
                Assert.Equal(0, recipe.Replacements);

                // Dry run: nothing written.
                Assert.Equal("1/10 WoodenSword\r\n1/2 Other\r\n", File.ReadAllText(Path.Combine(drops, "Hen.txt")));

                var backups = Path.Combine(root, "Back Up");
                GameDataRename.Execute(plan, backups, new DateTime(2026, 1, 2, 3, 4, 5), warnings);
                Assert.Empty(warnings);
                Assert.Equal("1/10 WoodenSword\r\n1/2 Other\r\n", File.ReadAllText(Path.Combine(drops, "Hen.txt"))); // untouched
                Assert.False(File.Exists(Path.Combine(recipes, "WoodenSword.txt")));
                Assert.True(File.Exists(Path.Combine(recipes, "OakSword.txt")));
                Assert.Equal("[TRADE]\nOakSword\n", File.ReadAllText(Path.Combine(npcs, "Smith.txt")));
                Assert.True(File.Exists(Path.Combine(backups, "npcs", "Town", "Smith.txt.20260102-030405.bak")));

                // Monster rename: quest kill tasks only (its drop list is bound to its Index).
                warnings.Clear();
                plan = GameDataRename.Plan("monsters", "Hen", "Chicken", roots, warnings);
                Assert.Contains(plan, f => f.Root == "quests" && f.Path == "Untouched.txt");
                Assert.DoesNotContain(plan, f => f.Root == "drops");
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [Fact]
        public void MagicSync_CopiesSavedValuesInPlace_BySpell()
        {
            var live = new MagicInfo { Name = "Fencing", Spell = Spell.Fencing, Level1 = 7, MPowerBase = 1 };
            var liveList = new List<MagicInfo> { live };
            var saved = GameDataMagicSync.Snapshot(new[] { new MagicInfo { Name = "Fencing2", Spell = Spell.Fencing, Level1 = 9, MPowerBase = 4 } });

            Assert.Equal(1, GameDataMagicSync.CopyInto(saved, liveList));
            Assert.Same(live, liveList[0]);
            Assert.Equal((byte)9, live.Level1);
            Assert.Equal((ushort)4, live.MPowerBase);
            Assert.Equal("Fencing2", live.Name);
            Assert.Equal(0, GameDataMagicSync.CopyInto(saved, liveList));
        }

        [Fact]
        public void Quest_HeadersMatchParseFileExactly_AndGenderRewardsAreCounted()
        {
            var lines = L("[@KillTasks] ;old", "Hen 1", "[@KILLTASKS]", "Hen 2");
            Assert.Equal(1, GameDataRename.RewriteQuest(lines, "Hen", "Chicken", true));
            Assert.Equal("Hen 1", lines[1]);
            Assert.Equal("Chicken 2", lines[3]);

            Assert.Equal("Sword", GameDataRename.GenderBaseName("Sword(M)"));
            Assert.Null(GameDataRename.GenderBaseName("Sword"));
            Assert.Equal(1, GameDataRename.CountGenderRewardRefs(L("[@FIXEDREWARDS]", "Sword 1", "[@ITEMTASKS]", "Sword 1"), "Sword"));
        }

        [Fact]
        public void LiveNothingApplied_AlwaysCarriesTheLiveShape()
        {
            var noop = JsonDocument.Parse(WebAdminJson.Serialize(new { live = Server.WebAdmin.Routes.GameDataRoutes.LiveNothingApplied(new GameDataWriteResult()) })).RootElement.GetProperty("live");
            Assert.Equal(0, noop.GetProperty("applied").GetInt32());
            Assert.Equal(0, noop.GetProperty("refreshedMonsters").GetInt32());
            Assert.Equal(0, noop.GetProperty("skippedFields").GetArrayLength());
            Assert.Equal("No changes to apply.", noop.GetProperty("message").GetString());

            var rejected = new GameDataWriteResult();
            rejected.Conflicts.Add(new { index = 1 });
            var r = JsonDocument.Parse(WebAdminJson.Serialize(Server.WebAdmin.Routes.GameDataRoutes.LiveNothingApplied(rejected))).RootElement;
            Assert.Equal(0, r.GetProperty("applied").GetInt32());
            Assert.Contains("Nothing was saved", r.GetProperty("message").GetString());
        }
    }
}
