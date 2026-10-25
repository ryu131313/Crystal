using Server.MirDatabase;
using Server.MirEnvir.Quests;
using Server.WebAdmin.GameData;
using Server.WebAdmin.LiveDb;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Quest system V2 (UserData-and-Quests-Plan.md, Part B): the new tables are read-only grids, the quest V2 columns read-only in the
    /// quests grid, and every new table and quest V2 column is QuestReplace in LiveDb (wave 3): the generic engine never applies them row by
    /// row; the Quests page and the quest-replace hook do. The legacy quests columns were dropped in wave 4.
    /// </summary>
    public class QuestDefinitionRegistryTests
    {
        [Fact]
        public void New_tables_are_read_only_grids_and_go_through_the_quest_replace_hook()
        {
            foreach (var endpoint in LiveDbClassification.QuestV2Tables)
            {
                var t = GameDataRegistry.Get(endpoint);
                Assert.NotNull(t);
                Assert.False(t.Insertable, endpoint);
                Assert.False(t.Deletable, endpoint);
                Assert.Equal("quests", t.Group);
                Assert.False(LiveDbClassification.IsLiveTable(endpoint));
                foreach (var c in t.Columns)
                {
                    Assert.True(c.ReadOnly && c.Set == null, endpoint + "." + c.Name);
                    if (c.IsParentField) continue;
                    var rule = LiveDbClassification.Effective(endpoint, c.Name);
                    Assert.Equal(LiveClass.QuestReplace, rule.Class);
                    Assert.Equal(LiveTiming.QuestReplace, rule.Timing);
                    Assert.False(rule.IsLive, endpoint + "." + c.Name); // never applied row by row by the generic engine
                    Assert.NotNull(LiveDbClassification.Planned(endpoint, c.Name));
                }
            }
        }

        [Fact]
        public void Quest_v2_columns_are_read_only_quest_replace_and_restored_through_the_definition()
        {
            var quests = GameDataRegistry.Get("quests");
            Assert.Contains("Category", GameDataRegistry.QuestV2Columns);
            Assert.DoesNotContain("Steps", GameDataRegistry.QuestV2Columns); // lists are child tables, not columns
            foreach (var name in GameDataRegistry.QuestV2Columns)
            {
                var c = quests.Column(name);
                Assert.True(c.ReadOnly && c.Set == null && c.StoreSet != null, name);
                Assert.Equal(LiveClass.QuestReplace, LiveDbClassification.Effective("quests", name).Class);
            }

            // Name and Group: read only, applied by the quest-replace hook, restored by the store. The legacy columns are gone (wave 4).
            foreach (var name in new[] { "Name", "Group" })
            {
                var c = quests.Column(name);
                Assert.True(c.ReadOnly && c.Set == null && c.StoreSet != null, name);
                Assert.Equal(LiveClass.QuestReplace, LiveDbClassification.Effective("quests", name).Class);
            }

            foreach (var name in new[] { "FileName", "RequiredMinLevel", "RequiredClass", "Type", "EditorOwned" })
                Assert.Null(quests.Column(name));
            foreach (var t in new[] { "queststeps", "questobjectives", "questendpoints", "questrewards" })
                Assert.Null(GameDataRegistry.Get(t).Column("LegacySource"));

            Assert.False(quests.Insertable);
            Assert.False(quests.Deletable);
            var legacy = new QuestInfo();
            quests.Column("Name").StoreSet(new GameDataRow { Entity = legacy }, "Restored");
            Assert.Equal("Restored", legacy.Name);
            var q = new QuestInfo();
            var row = new GameDataRow { Entity = q };
            quests.Column("Category").StoreSet(row, QuestCategory.Daily);
            Assert.Equal(QuestCategory.Daily, q.Definition.Category);
            Assert.Equal(QuestCategory.Daily, quests.Column("Category").Get(row));
        }

        [Fact]
        public void Child_rows_are_keyed_by_their_stable_ids_or_quest_position()
        {
            var q = new QuestInfo { Index = 7 };
            q.Definition.Steps.Add(new QuestStepInfo { Index = 41 });
            q.Definition.Rewards.Add(new QuestRewardInfo());
            q.Definition.Rewards.Add(new QuestRewardInfo());
            var ctx = new GameDataContext();
            ctx.Quests.Add(q);

            Assert.Equal(41, Assert.Single(GameDataRegistry.Get("queststeps").Rows(ctx)).Index);
            Assert.Equal(new[] { 7 * GameDataRegistry.Stride, 7 * GameDataRegistry.Stride + 1 }, GameDataRegistry.Get("questrewards").Rows(ctx).Select(r => r.Index));
            Assert.Equal(7, GameDataRegistry.Get("questrewards").Rows(ctx).First().ParentIndex);
        }
    }
}
