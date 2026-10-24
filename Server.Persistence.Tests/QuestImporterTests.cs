using System.Drawing;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Quest system V2 phase 1 importer (UserData-and-Quests-Plan.md, Part B, "Importer field mapping"): legacy quest bodies and NPC
/// [QUESTS] sections become V2 rows, as the legacy loader reads them; a re-import keeps every id; the result round-trips through the
/// store and the JSON export. <see cref="Live_copy_dry_run"/> runs on a copy of a real SystemData.db when MIR2_QUEST_LIVE_COPY names
/// its folder (the copy, never the live file) and writes the dry-run report there.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestImporterTests
{
    private static Envir LegacyWorld()
    {
        var e = new Envir();
        e.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
        string[] items = { "Pelt", "WolfFang", "BaseDress(M)", "BaseDress(F)", "WoodenSword", "QuestLetter", "Medal" };
        for (var i = 0; i < items.Length; i++) e.ItemInfoList.Add(new ItemInfo { Index = i + 1, Name = items[i], StackSize = 10 });
        string[] monsters = { "Wolf", "WolfKing", "Wolf2", "Hen", "Deer" };
        for (var i = 0; i < monsters.Length; i++) e.MonsterInfoList.Add(new MonsterInfo { Index = i + 1, Name = monsters[i] });
        e.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Hunter", Name = "Hunter", Location = new Point(1, 1) });
        e.NPCInfoList.Add(new NPCInfo { Index = 2, MapIndex = 1, FileName = "Hunter", Name = "Hunter twin", Location = new Point(2, 2) });
        e.NPCInfoList.Add(new NPCInfo { Index = 3, MapIndex = 1, FileName = "Broken", Name = "Broken", Location = new Point(3, 3), FinishQuestIndexes = new List<int> { 2 } });
        e.NPCInfoList.Add(new NPCInfo { Index = 4, MapIndex = 1, FileName = "Nobody", Name = "No script", Location = new Point(4, 4) });
        e.QuestInfoList.Add(new QuestInfo
        {
            Index = 1, Name = "Wolves", Group = "Bichon", FileName = "Wolves", RequiredMinLevel = 5, RequiredMaxLevel = 20, RequiredQuest = 2,
            RequiredClass = RequiredClass.Warrior | RequiredClass.Taoist, Type = QuestType.Daily, KillMessage = "Hunt", TimeLimitInSeconds = 600,
        });
        e.QuestInfoList.Add(new QuestInfo { Index = 2, Name = "Letter", FileName = "Letter", RequiredMaxLevel = ushort.MaxValue, Type = QuestType.Story });
        e.QuestInfoList.Add(new QuestInfo { Index = 3, Name = "No body", FileName = "Missing", RequiredMaxLevel = ushort.MaxValue, RequiredClass = RequiredClass.Archer });
        e.MapIndex = 1; e.ItemIndex = items.Length; e.MonsterIndex = monsters.Length; e.NPCIndex = 4; e.QuestIndex = 3;
        return e;
    }

    private static void WriteLegacyFiles(string dir)
    {
        var quests = Path.Combine(dir, "Envir", "Quests");
        var npcs = Path.Combine(dir, "Envir", "NPCs");
        Directory.CreateDirectory(quests);
        Directory.CreateDirectory(npcs);
        File.WriteAllLines(Path.Combine(quests, "Wolves.txt"), new[]
        {
            "[@DESCRIPTION]", "Wolves are eating the hens.", "Help!", "", "[@TASKDESCRIPTION]", "Kill wolves.", "[@RETURNDESCRIPTION]", "Come back.",
            "[@COMPLETION]", "Thanks.",
            "[@KILLTASKS]", "Wolf 5 \"Wolves slain\"", "WolfKing 1", "NoSuchMonster 3", "Wolf 2",
            "[@ITEMTASKS]", "Pelt 3", "WolfFang x", "Pelt 1",
            "[@FLAGTASKS]", "12 \"Talked to the hunter\"", "5000", "abc",
            "[@CARRYITEMS]", "QuestLetter",
            "[@FIXEDREWARDS]", "BaseDress", "WoodenSword 2", "Nothing",
            "[@SELECTREWARDS]", "Medal 1",
            "[@EXPREWARD]", "1500",
            "[@GOLDREWARD]", "250",
            "//comment ends nothing here",
        });
        File.WriteAllLines(Path.Combine(quests, "Letter.txt"), new[] { "[@ITEMTASKS]", "QuestLetter 1", "[@CREDITREWARD]", "7" });
        File.WriteAllLines(Path.Combine(npcs, "Hunter.txt"), new[] { "[@MAIN]", "#SAY", "Hello", "#INSERT [NPCs/Shared.txt]", "[QUESTS]", "1", "-1", "", "[TRADE]", "[QUESTS]", "2" });
        File.WriteAllLines(Path.Combine(npcs, "Shared.txt"), new[] { "[@SHARED]", "#SAY", "Shared" });
        File.WriteAllLines(Path.Combine(npcs, "Broken.txt"), new[] { "[QUESTS]", "-2", "99", "1" });
        File.WriteAllLines(Path.Combine(npcs, "Flags.txt"), new[] { "[@CLEAR]", "#ACT", "SET [12] 0", "SET [13] 1" });
    }

    [Fact]
    public void Legacy_quests_import_as_the_legacy_loader_reads_them()
    {
        var dir = NewTempFolder("quests-import");
        WriteLegacyFiles(dir);
        InFolder(dir, () =>
        {
            var e = LegacyWorld();
            var report = QuestLegacyImporter.Import(e);
            var q = e.QuestInfoList[0];
            var d = q.Definition;

            Assert.Equal("q1", d.AuthorKey);
            Assert.Equal(QuestCategory.Daily, d.Category);
            Assert.Equal(QuestRepeatPolicy.Daily, d.RepeatPolicy);
            Assert.Equal(600, d.TimeLimitSeconds);
            Assert.Equal("Bichon", d.GroupName);
            Assert.Equal("Wolves are eating the hens.\nHelp!", Text(e, d.SummaryKey));
            var step = Assert.Single(d.Steps);
            Assert.Equal(-1, step.NextStep);
            Assert.Equal("Kill wolves.", Text(e, step.TextKey));
            Assert.Equal("Come back.", Text(e, step.ReadyTextKey));
            Assert.Equal("Thanks.", Text(e, d.CompleteKey));
            Assert.Equal("Hunt", Text(e, "q1.kill"));

            // Kill tasks: non-overlapping groups in file order (Wolf takes Wolf, WolfKing and Wolf2; the WolfKing task is left empty).
            var kills = d.Objectives.Where(o => o.Type == QuestObjectiveType.Kill).ToList();
            Assert.Equal(3, kills.Count);
            var wolfGroup = e.QuestGroupList.Single(g => g.Index == kills[0].TargetId);
            Assert.Equal(new[] { 1, 2, 3 }, wolfGroup.Members.Select(m => m.TargetId));
            Assert.Empty(e.QuestGroupList.Single(g => g.Index == kills[1].TargetId).Members);
            Assert.Equal(5, kills[0].Count);
            Assert.Equal("Wolves slain", Text(e, kills[0].TextKey));
            Assert.Contains(report.EmptyGroups, s => s.Contains("WolfKing"));
            Assert.Contains(report.OverlappingGroups, s => s.Contains("WolfKing matches kill tasks Wolf and WolfKing"));
            Assert.Contains(report.DuplicateTasks, s => s.Contains("two kill tasks on Wolf"));
            Assert.Contains(report.Unparsed, s => s.Contains("NoSuchMonster"));

            // Item tasks: "x" is no number, so the count is 0 (ushort.TryParse), as legacy; flags: 5000 is out of range, "abc" is flag 0.
            var collects = d.Objectives.Where(o => o.Type == QuestObjectiveType.Collect).ToList();
            Assert.Equal(new[] { 1, 2, 1 }, collects.Select(o => o.TargetId));
            Assert.Equal(new[] { 3, 0, 1 }, collects.Select(o => o.Count));
            var flags = d.Objectives.Where(o => o.Type == QuestObjectiveType.Flag).ToList();
            Assert.Equal(new[] { 12, 0 }, flags.Select(o => o.TargetId));
            Assert.Contains(report.Unparsed, s => s.Contains("flag 5000"));
            Assert.Equal(new List<string> { "Flags.txt" }, report.FlagClearedBy[12]);
            Assert.False(report.FlagClearedBy.ContainsKey(13));

            // Rewards: carry item on accept, the (M)/(F) pair with Gender conditions, select group 1, exp and gold.
            Assert.Contains(d.Rewards, r => r.Scope == QuestRewardScope.Accept && r.TargetId == 6);
            var male = d.Rewards.Single(r => r.TargetId == 3);
            var female = d.Rewards.Single(r => r.TargetId == 4);
            Assert.Equal(QuestConditionLeaf.Gender, e.QuestConditionList.Single(c => c.Index == male.Condition).Leaf);
            Assert.Equal((int)MirGender.Female, e.QuestConditionList.Single(c => c.Index == female.Condition).Arg1);
            Assert.Contains(report.GenderPairs, s => s.Contains("BaseDress"));
            Assert.Equal(2, d.Rewards.Single(r => r.TargetId == 5).Amount);
            Assert.Equal(1, d.Rewards.Single(r => r.TargetId == 7).ChoiceGroup);
            Assert.Equal(1500, d.Rewards.Single(r => r.Kind == QuestRewardKind.Exp).Amount);
            Assert.Equal(250, d.Rewards.Single(r => r.Kind == QuestRewardKind.Gold).Amount);
            Assert.Contains(report.Unparsed, s => s.Contains("Nothing"));

            // Accept condition: And(Level >= 5, Level <= 20, completed quest 2, Or(Warrior, Taoist)).
            var root = e.QuestConditionList.Single(c => c.Index == d.AcceptCondition);
            Assert.Equal(QuestConditionOp.And, root.Op);
            var parts = e.QuestConditionList.Where(c => c.Parent == root.Index).OrderBy(c => c.Order).ToList();
            Assert.Equal(4, parts.Count);
            Assert.Equal(QuestCompare.Le, parts[1].Cmp);
            Assert.Equal(QuestConditionLeaf.QuestCompletedCount, parts[2].Leaf);
            Assert.Equal(new[] { (int)MirClass.Warrior, (int)MirClass.Taoist },
                e.QuestConditionList.Where(c => c.Parent == parts[3].Index).Select(c => c.Arg1));
            var archer = e.QuestConditionList.Single(c => c.Index == e.QuestInfoList[2].Definition.AcceptCondition);
            Assert.Equal(QuestConditionLeaf.Class, archer.Leaf); // one part: the leaf is the root

            // Givers: both NPCs on the shared script; the first [QUESTS] section only; Broken stops at 99 but its -2 counts.
            Assert.Equal(new[] { 1, 2 }, d.Endpoints.Where(x => x.Role == QuestEndpointRole.Start).Select(x => x.TargetId));
            Assert.Equal(new[] { 1, 2 }, d.Endpoints.Where(x => x.Role == QuestEndpointRole.Finish).Select(x => x.TargetId));
            var letter = e.QuestInfoList[1].Definition;
            Assert.DoesNotContain(letter.Endpoints, x => x.Role == QuestEndpointRole.Start);
            Assert.Equal(3, Assert.Single(letter.Endpoints).TargetId); // from the script and NPCInfo.FinishQuestIndexes, once
            Assert.Contains(report.MultiGiver, s => s.StartsWith("1 "));
            Assert.Contains(report.NoGiver, s => s.StartsWith("2 "));
            Assert.Contains(report.CutShort, s => s.Contains("Broken: stops at unknown quest 99"));
            Assert.Contains(report.IgnoredQuestSections, s => s.StartsWith("Hunter"));
            Assert.Contains(report.MissingScripts, s => s.Contains("Nobody"));
            Assert.Contains(report.MissingBodies, s => s.StartsWith("3 "));

            // Validation runs (report only): the known data problems are listed.
            Assert.Contains(report.Problems, p => !p.Warning && p.Message.Contains("Count is 0"));
            Assert.Contains(report.Problems, p => p.Warning && p.Message.Contains("is empty"));
            Assert.NotEmpty(report.ToMarkdown());
            return 0;
        });
    }

    [Fact]
    public void A_second_import_gives_identical_rows_and_ids_and_round_trips()
    {
        var dir = NewTempFolder("quests-reimport");
        WriteLegacyFiles(dir);
        InFolder(dir, () =>
        {
            var e = LegacyWorld();
            QuestLegacyImporter.Import(e);
            var first = SqliteWorldCodec.Encode(e, 0);
            Assert.Equal(1, e.QuestInfoList[0].Definition.Revision);

            QuestLegacyImporter.Import(e);
            var second = SqliteWorldCodec.Encode(e, 0);
            Assert.Null(SqliteWorldStore.FirstDifference(first, second));

            // A legacy edit keeps the other ids: a new kill task gets a new objective id, the rest keep theirs, and the shape bumps the revision.
            var before = e.QuestInfoList[0].Definition.Objectives.ToDictionary(o => o.LegacySource, o => o.Index);
            var path = Path.Combine(dir, "Envir", "Quests", "Wolves.txt");
            var lines = File.ReadAllLines(path).ToList();
            lines.Insert(lines.IndexOf("[@KILLTASKS]") + 1, "Deer 2");
            File.WriteAllLines(path, lines);
            QuestLegacyImporter.Import(e);
            SqliteWorldCodec.Encode(e, 0);
            var after = e.QuestInfoList[0].Definition.Objectives;
            foreach (var o in after.Where(o => before.ContainsKey(o.LegacySource))) Assert.Equal(before[o.LegacySource], o.Index);
            Assert.True(after.Single(o => o.LegacySource == "[@KILLTASKS] Deer").Index > before.Values.Max());
            Assert.Equal(2, e.QuestInfoList[0].Definition.Revision);

            // Store round trip, then JSON export / import.
            var rows = SqliteWorldCodec.Encode(e, 0);
            var db = Path.Combine(dir, "SystemData.db");
            SqliteWorldStore.Create(db, rows, null);
            WorldRows back;
            using (var conn = SqliteWorldFile.Open(db, create: false)) back = SqliteWorldFile.ReadAll(conn);
            var loaded = new Envir();
            lock (Envir.LoadLock) SqliteWorldCodec.Decode(back, loaded);
            Assert.Null(SqliteWorldStore.FirstDifference(rows, SqliteWorldCodec.Encode(loaded, 0)));

            var json = QuestDefinitionJson.Export(back);
            WorldRows fresh;
            using (var conn = SqliteWorldFile.Open(db, create: false)) fresh = SqliteWorldFile.ReadAll(conn);
            foreach (var name in QuestDefinitionJson.Tables) fresh.Tables[name] = new WorldRows.Table { Name = name };
            QuestDefinitionJson.Import(json, fresh);
            Assert.Null(SqliteWorldStore.FirstDifference(back, fresh));
            return 0;
        });
    }

    /// <summary>
    /// The dry run on a copy of the live data (Part B phase 1): MIR2_QUEST_LIVE_COPY = a folder holding a VACUUM INTO copy of
    /// SystemData.db and a copy of the Envir folder (NPC scripts). Imports twice (identical rows), saves through the real store (pre-migration
    /// copy, writer), reloads, round-trips the JSON export, and writes quest-dry-run.md there. Skipped (passes) when the variable is unset.
    /// </summary>
    [Fact]
    public void Live_copy_dry_run()
    {
        var dir = Environment.GetEnvironmentVariable("MIR2_QUEST_LIVE_COPY");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Assert.False(Path.GetFullPath(dir).Contains(Path.Combine("Build", "Server"), StringComparison.OrdinalIgnoreCase), "never the live folder");
        Assert.True(File.Exists(Path.Combine(dir, "SystemData.db")));

        lock (Gate)
        {
            var old = Environment.CurrentDirectory;
            var oldStore = Envir.Store;
            Directory.SetCurrentDirectory(dir);
            var store = new SqliteWorldStore(privateWriter: true);
            try
            {
                Envir.Store = store;
                DesignStore.WriterOverride = store.Writer;
                DesignTexts.ResetCache();
                EnsureBuffInfo();

                Envir.Edit.MagicInfoList.Clear();
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(Envir.Edit));
                var report = QuestLegacyImporter.Import(Envir.Edit);
                var first = SqliteWorldCodec.Encode(Envir.Edit, 0);
                QuestLegacyImporter.Import(Envir.Edit);
                var second = SqliteWorldCodec.Encode(Envir.Edit, 0);
                Assert.Null(SqliteWorldStore.FirstDifference(first, second));

                store.SaveDesign(Envir.Edit, "quest-dry-run.bak");
                var reloaded = new Envir();
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(reloaded));
                var third = SqliteWorldCodec.Encode(reloaded, 0);
                Assert.Null(SqliteWorldStore.FirstDifference(second, third));

                var json = QuestDefinitionJson.Export(third);
                var fresh = SqliteWorldCodec.Encode(reloaded, 0);
                foreach (var name in QuestDefinitionJson.Tables) fresh.Tables[name] = new WorldRows.Table { Name = name };
                QuestDefinitionJson.Import(json, fresh);
                Assert.Null(SqliteWorldStore.FirstDifference(third, fresh));

                File.WriteAllText(Path.Combine(dir, "quest-dry-run.md"), report.ToMarkdown(25) + "\n\nAll problems:\n" + string.Join("\n", report.Problems) +
                                                                       "\n\nUnparsed:\n" + string.Join("\n", report.Unparsed));
                File.WriteAllText(Path.Combine(dir, "quest-dry-run.json"), json);
                File.WriteAllText(Path.Combine(dir, "quest-dry-run-full.md"), report.ToMarkdown(int.MaxValue));
            }
            finally
            {
                store.Writer.Shutdown();
                DesignStore.WriterOverride = null;
                DesignStore.Disabled = true;
                DesignTexts.ResetCache();
                Envir.Store = oldStore;
                Directory.SetCurrentDirectory(old);
            }
        }
    }

    private static string Text(Envir e, string key) => e.QuestTextList.Single(t => t.TextKey == key).Body;
}
