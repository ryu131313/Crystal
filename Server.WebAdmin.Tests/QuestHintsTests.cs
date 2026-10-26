using System.Drawing;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir.Quests;
using Server.MirEnvir.Storage;
using Server.WebAdmin.Quests;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Quest editor picker hints (Phase E): spawns per map, an NPC's place, a map's respawns with monster names, and the item drop
    /// lookup that follows included lists once. On a detached <see cref="QuestWorld"/>: no Envir, no Settings, no drop store.
    /// </summary>
    public class QuestHintsTests
    {
        private static QuestWorld World()
        {
            var w = new QuestWorld();
            w.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
            w.ItemInfoList.Add(new ItemInfo { Index = 1, Name = "WoodenSword" });
            w.ItemInfoList.Add(new ItemInfo { Index = 2, Name = "Pelt", StackSize = 10 });
            w.MonsterInfoList.Add(new MonsterInfo { Index = 1, Name = "Wolf", Level = 12, Experience = 100 });
            w.MonsterInfoList.Add(new MonsterInfo { Index = 2, Name = "WolfKing", Level = 30, Experience = 900, IsBoss = true });
            w.MonsterInfoList.Add(new MonsterInfo { Index = 3, Name = "Hen", Level = 2, Experience = 5 });
            w.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Guide", Name = "Guide", Location = new Point(10, 10) });
            w.MapInfoList[0].Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = new Point(20, 20), Count = 5, Spread = 3 });
            w.MapInfoList[0].Respawns.Add(new RespawnInfo { MonsterIndex = 2, Location = new Point(60, 60), Count = 1, Spread = 0 });
            return w;
        }

        private static JsonDocument Serialize(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value, WebAdminJson.Options));

        [Fact]
        public void Monster_hint_lists_where_it_spawns()
        {
            var w = World();
            using var doc = Serialize(QuestHints.Monster(w, 1));
            var root = doc.RootElement;

            Assert.Equal("monster", root.GetProperty("kind").GetString());
            Assert.Equal(12, root.GetProperty("level").GetInt32());
            var spawns = root.GetProperty("spawns");
            Assert.Equal(1, spawns.GetArrayLength());
            Assert.Equal(1, spawns[0].GetProperty("map").GetInt32());
            Assert.Equal(5, spawns[0].GetProperty("count").GetInt32());
        }

        [Fact]
        public void Npc_hint_gives_map_and_cell()
        {
            var w = World();
            using var doc = Serialize(QuestHints.Npc(w, 1));
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("map").GetInt32());
            Assert.Equal(10, root.GetProperty("x").GetInt32());
            Assert.Equal(10, root.GetProperty("y").GetInt32());
            Assert.Equal("Bichon", root.GetProperty("mapTitle").GetString());
        }

        [Fact]
        public void Map_hint_lists_respawns_with_monster_names()
        {
            var w = World();
            using var doc = Serialize(QuestHints.Map(w, 1));
            var respawns = doc.RootElement.GetProperty("respawns");

            Assert.Equal(2, respawns.GetArrayLength());
            Assert.Equal("Wolf", respawns[0].GetProperty("name").GetString());
            Assert.Equal("WolfKing", respawns[1].GetProperty("name").GetString());
            Assert.True(respawns[1].GetProperty("boss").GetBoolean());
        }

        [Fact]
        public void Item_hint_has_the_picker_fields()
        {
            var w = World();
            using var doc = Serialize(QuestHints.Item(w, 2));
            var root = doc.RootElement;

            Assert.Equal("item", root.GetProperty("kind").GetString());
            Assert.Equal("Pelt", root.GetProperty("name").GetString());
            Assert.Equal(0, root.GetProperty("droppedBy").GetArrayLength());
            Assert.False(root.GetProperty("dropsKnown").GetBoolean());
        }

        [Fact]
        public void DroppedBy_follows_includes_once_keeps_the_best_chance_and_marks_quest_drops()
        {
            var a = new DropList
            {
                Id = 1,
                Rows = new[]
                {
                    new DropRow { Kind = DropKind.Item, ItemIndex = 2, Chance = 20 },
                    new DropRow { Kind = DropKind.Include, IncludeListId = 2 },
                },
            };
            var b = new DropList
            {
                Id = 2,
                Rows = new[]
                {
                    new DropRow { Kind = DropKind.Item, ItemIndex = 2, Chance = 5 },
                    new DropRow { Kind = DropKind.Include, IncludeListId = 1 },
                },
            };
            var c = new DropList { Id = 3, Rows = new[] { new DropRow { Kind = DropKind.Item, ItemIndex = 2, Chance = 3, Quest = true } } };

            var monsters = new[] { (1, "Wolf", string.Empty), (3, "Hen", string.Empty) };
            var hints = QuestHints.DroppedBy(2, monsters, (i, n, p) => i == 1 ? a : i == 3 ? c : null,
                id => id == 1 ? a : id == 2 ? b : id == 3 ? c : null);

            Assert.Equal(2, hints.Count);
            Assert.Equal(3, hints[0].monster);
            Assert.Equal(3, hints[0].chance);
            Assert.True(hints[0].quest);
            Assert.Equal(1, hints[1].monster);
            Assert.Equal(5, hints[1].chance);
            Assert.False(hints[1].quest);
        }
    }
}
