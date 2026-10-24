using Server.MirDatabase;
using Server.MirEnvir.Quests;
using Server.WebAdmin.GameData;
using Server.WebAdmin.LiveDb;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Quest system V2 phase 1 (UserData-and-Quests-Plan.md, Part B): the new tables are read-only grids, the quest V2 columns read-only
    /// in the quests grid, and every new table and column is RestartOnly in LiveDb while legacy quests are the runtime.
    /// </summary>
    public class QuestDefinitionRegistryTests
    {
        [Fact]
        public void New_tables_are_read_only_grids_and_restart_only()
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
                    Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective(endpoint, c.Name).Class);
                    Assert.NotNull(LiveDbClassification.Planned(endpoint, c.Name));
                }
            }
        }

        [Fact]
        public void Quest_v2_columns_are_read_only_restart_only_and_restored_through_the_definition()
        {
            var quests = GameDataRegistry.Get("quests");
            Assert.Contains("Category", GameDataRegistry.QuestV2Columns);
            Assert.DoesNotContain("Steps", GameDataRegistry.QuestV2Columns); // lists are child tables, not columns
            foreach (var name in GameDataRegistry.QuestV2Columns)
            {
                var c = quests.Column(name);
                Assert.True(c.ReadOnly && c.Set == null && c.StoreSet != null, name);
                Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("quests", name).Class);
            }

            Assert.NotNull(quests.Column("Name").Set); // the legacy columns stay editable
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
