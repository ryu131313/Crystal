using System.Drawing;
using Microsoft.Data.Sqlite;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Quest system V2 phase 1 (UserData-and-Quests-Plan.md, Part B): the definition tables round-trip through SystemData.db, an older store
/// gets the new tables, columns and counters (after its pre-migration copy), ShapeHash / Revision follow the shape only, and validation
/// reports what the spec lists.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestDefinitionTests
{
    /// <summary>A small world with one quest carrying a definition in every V2 table.</summary>
    internal static Envir World()
    {
        var e = new Envir();
        e.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
        e.ItemInfoList.Add(new ItemInfo { Index = 1, Name = "WoodenSword", StackSize = 1 });
        e.ItemInfoList.Add(new ItemInfo { Index = 2, Name = "Pelt", StackSize = 10 });
        e.MonsterInfoList.Add(new MonsterInfo { Index = 1, Name = "Wolf" });
        e.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Guide", Name = "Guide", Location = new Point(10, 10) });
        e.MapIndex = 1; e.ItemIndex = 2; e.MonsterIndex = 1; e.NPCIndex = 1;

        var q = new QuestInfo { Index = 1, Name = "Wolves", FileName = "Wolves", RequiredMinLevel = 3, RequiredMaxLevel = ushort.MaxValue };
        e.QuestInfoList.Add(q);
        e.QuestIndex = 1;
        var d = q.Definition;
        d.AuthorKey = "q1"; d.NameKey = "q1.name"; d.Category = QuestCategory.Side; d.AcceptCondition = 1; d.Flags = QuestFlags.HiddenInLog;
        d.StorylineIndex = 1; d.PoolIndex = 1; d.GroupName = "Bichon";
        d.Steps.Add(new QuestStepInfo { Index = 1, Order = 1, TextKey = "q1.task", NextStep = 2, LegacySource = "step" });
        d.Steps.Add(new QuestStepInfo { Index = 2, Order = 2, NextStep = -1, CompletionMode = QuestStepCompletion.NOf, NRequired = 1 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 1, Step = 1, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 1, Count = 5 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 2, Step = 2, Order = 1, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = 2, Count = 3, Params = "" });
        d.Branches.Add(new QuestBranchInfo { Step = 1, ChoiceKey = "q1.task", NextStep = 2, Outcome = 1 });
        d.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 1, LegacySource = "script Guide" });
        d.Rewards.Add(new QuestRewardInfo { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 5_000_000_000L });
        d.Rewards.Add(new QuestRewardInfo { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Item, TargetId = 1, Amount = 1, Condition = 2, ChoiceGroup = 1 });
        d.Vars.Add(new QuestVarInfo { Index = 1, AuthorKey = "count", Default = 2 });

        e.QuestConditionList.Add(new QuestConditionInfo { Index = 1, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 3, Cmp = QuestCompare.Ge, OwnerQuest = 1 });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 2, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Gender, Arg1 = 1, OwnerQuest = 1 });
        e.QuestTextList.Add(new QuestTextInfo { Index = 1, TextKey = "q1.name", Body = "Wolves" });
        e.QuestTextList.Add(new QuestTextInfo { Index = 2, TextKey = "q1.task", Body = "Kill {5/Red} wolves.\nThen come back." });
        e.StorylineList.Add(new StorylineInfo { Index = 1, AuthorKey = "bichon", NameKey = "q1.name", Order = 1 });
        var g = new QuestGroupInfo { Index = 1, AuthorKey = "q1.kill.Wolf", Kind = QuestGroupKind.Monster };
        g.Members.Add(new QuestGroupMemberInfo { TargetId = 1 });
        e.QuestGroupList.Add(g);
        var p = new QuestPoolInfo { Index = 1, AuthorKey = "daily", Picks = 1, RotatePolicy = QuestRotatePolicy.Weekly };
        p.Members.Add(new QuestPoolMemberInfo { Quest = 1, Weight = 3 });
        e.QuestPoolList.Add(p);
        e.QuestStepIndex = 2; e.QuestObjectiveIndex = 2; e.QuestConditionIndex = 2; e.QuestGroupIndex = 1; e.QuestTextIndex = 2; e.QuestVarIndex = 1;
        e.StorylineIndex = 1; e.QuestPoolIndex = 1;
        return e;
    }

    [Fact]
    public void Definition_tables_round_trip_through_the_store()
    {
        var dir = NewTempFolder("quests-roundtrip");
        InFolder(dir, () =>
        {
            var e = World();
            var rows = SqliteWorldCodec.Encode(e, 0);
            var db = Path.Combine(dir, "SystemData.db");
            SqliteWorldStore.Create(db, rows, null);

            WorldRows back;
            using (var conn = SqliteWorldFile.Open(db, create: false)) back = SqliteWorldFile.ReadAll(conn);
            Assert.Null(SqliteWorldStore.FirstDifference(rows, back));

            var loaded = new Envir();
            lock (Envir.LoadLock) SqliteWorldCodec.Decode(back, loaded);
            var q = Assert.Single(loaded.QuestInfoList);
            Assert.Equal(2, q.Definition.Steps.Count);
            Assert.Equal(5_000_000_000L, q.Definition.Rewards[0].Amount);
            Assert.Equal(QuestFlags.HiddenInLog, q.Definition.Flags);
            Assert.Equal("Kill {5/Red} wolves.\nThen come back.", loaded.QuestTextList[1].Body);
            Assert.Equal(3, loaded.QuestPoolList[0].Members[0].Weight);
            Assert.Equal(2, loaded.QuestObjectiveIndex);
            Assert.Equal(1, loaded.QuestPoolIndex);
            Assert.Null(SqliteWorldStore.FirstDifference(rows, SqliteWorldCodec.Encode(loaded, 0)));

            // JSON export / import of the quest tables gives identical rows.
            var json = QuestDefinitionJson.Export(back);
            WorldRows fresh;
            using (var conn = SqliteWorldFile.Open(db, create: false)) fresh = SqliteWorldFile.ReadAll(conn);
            foreach (var name in QuestDefinitionJson.Tables) fresh.Tables[name] = new WorldRows.Table { Name = name };
            foreach (var name in SqliteWorldCodec.MigratedCounters.Select(c => c.Counter).Append("QuestIndex")) fresh.Counters[name] = 0;
            QuestDefinitionJson.Import(json, fresh);
            Assert.Null(SqliteWorldStore.FirstDifference(back, fresh));
            return 0;
        });
    }

    [Fact]
    public void An_older_store_gets_the_quest_tables_columns_and_counters_after_a_copy()
    {
        var dir = NewTempFolder("quests-migrate");
        InFolder(dir, () =>
        {
            var e = World();
            var db = Path.Combine(dir, "old.db");
            SqliteWorldStore.Create(db, SqliteWorldCodec.Encode(e, 0), null);

            using (var conn = SqliteWorldFile.Open(db, create: false))
            {
                // A store written before quest V2: no new tables, no V2 quests columns, no new counters (but quest rows; their keys
                // stay, and the counters start at the highest key of their tables, 0 for a new table).
                var dropColumns = string.Join("; ", Server.WebAdmin.GameData.GameDataRegistry.QuestV2Columns.Select(c => "ALTER TABLE quests DROP COLUMN \"" + c + "\""));
                Exec(conn, dropColumns);
                foreach (var t in new[] { "queststeps", "questobjectives", "questbranches", "questendpoints", "questrewards", "questvars", "conditions", "texts",
                             "storylines", "questgroupmembers", "questgroups", "questpoolmembers", "questpools" })
                    Exec(conn, "DROP TABLE " + t);
                Exec(conn, "DELETE FROM index_counters WHERE name LIKE 'Quest%' AND name <> 'QuestIndex' OR name = 'StorylineIndex'");
                Assert.Throws<InvalidDataException>(() => SqliteWorldFile.ReadAll(conn));

                var log = new List<string>();
                var backup = Path.Combine(dir, "Back Up", "Pre-migrate.bak");
                var added = SqliteWorldFile.Migrate(conn, log.Add, backup);
                Assert.Equal(13 + Server.WebAdmin.GameData.GameDataRegistry.QuestV2Columns.Count + 8, added);
                Assert.True(File.Exists(backup));
                Assert.Contains(log, l => l.Contains("Added index counter QuestObjectiveIndex = 0"));
                using (var copy = SqliteWorldFile.Open(backup, create: false))
                    Assert.Throws<InvalidDataException>(() => SqliteWorldFile.ReadAll(copy)); // the copy is the pre-migration file

                var rows = SqliteWorldFile.ReadAll(conn);
                var loaded = new Envir();
                lock (Envir.LoadLock) SqliteWorldCodec.Decode(rows, loaded);
                Assert.Equal("Wolves", loaded.QuestInfoList[0].Name);
                Assert.False(loaded.QuestInfoList[0].HasDefinition);
                Assert.True(loaded.QuestInfoList[0].Definition.AbandonAllowed); // the fresh model's default
                Assert.Equal(0, loaded.QuestStepIndex);
                Assert.Equal(0, SqliteWorldFile.Migrate(conn, log.Add, backup)); // idempotent
            }

            // A counter's table that already has rows starts the counter at its highest key.
            using (var conn = SqliteWorldFile.Open(db, create: false))
            {
                Exec(conn, "INSERT INTO texts (parent_position, position, key, \"TextKey\", \"Lang\", \"Body\") VALUES (0, 0, 41, 'x', '', 'y')");
                Exec(conn, "DELETE FROM index_counters WHERE name = 'QuestTextIndex'");
                Assert.Equal(1, SqliteWorldFile.Migrate(conn, _ => { }));
                Assert.Equal(41L, Convert.ToInt64(Scalar(conn, "SELECT value FROM index_counters WHERE name = 'QuestTextIndex'")));
            }

            return 0;
        });
    }

    [Fact]
    public void Revision_follows_the_shape_hash_only()
    {
        var e = World();
        var d = e.QuestInfoList[0].Definition;
        QuestDefinitions.PrepareForSave(e);
        Assert.Equal(1, d.Revision);
        var hash = d.ShapeHash;
        Assert.NotEqual(0, hash);

        QuestDefinitions.PrepareForSave(e);
        Assert.Equal(1, d.Revision); // unchanged shape: no bump

        e.QuestTextList[1].Body = "Other text";
        d.Rewards[0].Amount = 1;
        QuestDefinitions.PrepareForSave(e);
        Assert.Equal(1, d.Revision); // text and rewards are not shape

        d.Objectives[0].Count = 6;
        QuestDefinitions.PrepareForSave(e);
        Assert.Equal(2, d.Revision);
        Assert.NotEqual(hash, d.ShapeHash);

        var legacyOnly = new QuestInfo { Index = 2, Name = "Legacy" };
        e.QuestInfoList.Add(legacyOnly);
        QuestDefinitions.PrepareForSave(e);
        Assert.Equal(0, legacyOnly.Definition.Revision); // no V2 rows: left alone
        Assert.Equal(0, legacyOnly.Definition.ShapeHash);
    }

    [Fact]
    public void Validation_reports_the_listed_problems()
    {
        var e = World();
        Assert.DoesNotContain(QuestDefinitions.Validate(e), p => !p.Warning);

        var d = e.QuestInfoList[0].Definition;
        d.Objectives[0].TargetKind = QuestTargetKind.Item;                                    // Kill on an item
        d.Objectives.Add(new QuestObjectiveInfo { Index = 3, Step = 1, Type = QuestObjectiveType.Flag, TargetKind = QuestTargetKind.Flag, TargetId = QuestDefinitions.MaxFlagNumber + 1 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 2, Step = 9, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = 77 }); // reused id, bad step, missing item
        d.Steps[0].NextStep = 5;                                                             // not a step of the quest
        d.NameKey = "q1.missing";
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 3, Op = QuestConditionOp.Not, OwnerQuest = 1 });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 4, Op = QuestConditionOp.Leaf, Parent = 3, Leaf = QuestConditionLeaf.Level, OwnerQuest = 1 });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 5, Op = QuestConditionOp.Leaf, Parent = 3, Leaf = QuestConditionLeaf.Level, OwnerQuest = 1 });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 6, Op = QuestConditionOp.And, Parent = 7, OwnerQuest = 2 });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 7, Op = QuestConditionOp.And, Parent = 6, OwnerQuest = 2 });
        d.VisibleCondition = 6;                                                               // owned by another quest
        e.QuestObjectiveIndex = 3;

        var problems = QuestDefinitions.Validate(e).Where(p => !p.Warning).Select(p => p.Message).ToList();
        Assert.Contains(problems, m => m.Contains("(Kill) needs a Monster or MonsterGroup target"));
        Assert.Contains(problems, m => m.Contains("names flag " + (QuestDefinitions.MaxFlagNumber + 1)));
        Assert.Contains(problems, m => m.Contains("objective 2 id is used twice"));
        Assert.Contains(problems, m => m.Contains("names step 9"));
        Assert.Contains(problems, m => m.Contains("names a missing item 77"));
        Assert.Contains(problems, m => m.Contains("NextStep 5 is not a step"));
        Assert.Contains(problems, m => m.Contains("names a missing text q1.missing"));
        Assert.Contains(problems, m => m.Contains("(Not) has 2 children"));
        Assert.Contains(problems, m => m.Contains("parent cycle"));
        Assert.Contains(problems, m => m.Contains("owned by quest 2"));

        e.QuestInfoList.Add(new QuestInfo { Index = 2, Name = "Pelts too" });
        var other = e.QuestInfoList[1].Definition;
        other.NameKey = "q1.name";
        other.Steps.Add(new QuestStepInfo { Index = 2, Order = 1, NextStep = -1 });
        other.Objectives.Add(new QuestObjectiveInfo { Index = 3, Step = 2, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = 2 });
        Assert.Contains(QuestDefinitions.Validate(e), p => p.Warning && p.Message.Contains("all collect item 2"));
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
