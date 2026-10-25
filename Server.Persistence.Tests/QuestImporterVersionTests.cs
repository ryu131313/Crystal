using System.Drawing;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Wave 3 importer fixes (UserData-and-Quests-Plan.md, "Wave 3 integration as built"): the legacy re-import owns legacy quests only (a
/// legacy body file name) and keeps everything an editor made; the importer version is recorded in SystemData.db and an older one triggers
/// one re-import at world start (the D2 start); quests without a legacy body load no body file.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestImporterVersionTests
{
    private const int Legacy = 1, Editor = 9;

    private static Envir World()
    {
        var e = new Envir();
        e.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
        e.ItemInfoList.Add(new ItemInfo { Index = 1, Name = "Pelt", StackSize = 10 });
        e.ItemInfoList.Add(new ItemInfo { Index = 2, Name = "Medal", StackSize = 1 });
        e.MonsterInfoList.Add(new MonsterInfo { Index = 1, Name = "Wolf" });
        e.MonsterInfoList.Add(new MonsterInfo { Index = 2, Name = "Hen" });
        e.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Hunter", Name = "Hunter", Location = new Point(1, 1) });
        e.QuestInfoList.Add(new QuestInfo { Index = Legacy, Name = "Wolves", FileName = "Wolves", RequiredMinLevel = 3, RequiredMaxLevel = ushort.MaxValue });
        e.MapIndex = 1; e.ItemIndex = 2; e.MonsterIndex = 2; e.NPCIndex = 1; e.QuestIndex = Legacy;
        return e;
    }

    private static void WriteLegacyFiles(string dir)
    {
        var quests = Path.Combine(dir, "Envir", "Quests");
        var npcs = Path.Combine(dir, "Envir", "NPCs");
        Directory.CreateDirectory(quests);
        Directory.CreateDirectory(npcs);
        File.WriteAllLines(Path.Combine(quests, "Wolves.txt"), new[] { "[@TASKDESCRIPTION]", "Kill wolves.", "[@KILLTASKS]", "Wolf 3", "[@GOLDREWARD]", "50" });
        File.WriteAllLines(Path.Combine(npcs, "Hunter.txt"), new[] { "[@MAIN]", "#SAY", "Hello", "[QUESTS]", "1", "-1" });
    }

    /// <summary>
    /// An editor-made quest (no FileName) whose keys look like importer keys on purpose (q9.name, a q9.kill group, owned conditions), so the
    /// test proves the importer goes by legacy quests, not by key shape.
    /// </summary>
    private static void AddEditorQuest(Envir e)
    {
        var q = new QuestInfo { Index = Editor, Name = "Editor quest", RequiredMaxLevel = ushort.MaxValue };
        var d = q.Definition;
        d.AuthorKey = "editor.quest";
        d.NameKey = "q9.name";
        d.ShareMode = QuestShareMode.None;
        d.AcceptCondition = ++e.QuestConditionIndex;
        e.QuestConditionList.Add(new QuestConditionInfo { Index = d.AcceptCondition, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 7, Cmp = QuestCompare.Ge, OwnerQuest = Editor });
        var step = ++e.QuestStepIndex;
        d.Steps.Add(new QuestStepInfo { Index = step, Order = 1, TitleKey = "q9.step" });
        var group = new QuestGroupInfo { Index = ++e.QuestGroupIndex, AuthorKey = "q9.kill.Hen", Kind = QuestGroupKind.Monster };
        group.Members.Add(new QuestGroupMemberInfo { TargetId = 2 });
        e.QuestGroupList.Add(group);
        d.Objectives.Add(new QuestObjectiveInfo { Index = ++e.QuestObjectiveIndex, Step = step, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = group.Index, Count = 2 });
        var rewardCondition = ++e.QuestConditionIndex;
        e.QuestConditionList.Add(new QuestConditionInfo { Index = rewardCondition, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Gender, Arg1 = 0, OwnerQuest = Editor });
        d.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = 2, Amount = 1, Condition = rewardCondition });
        d.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        e.QuestTextList.Add(new QuestTextInfo { Index = ++e.QuestTextIndex, TextKey = "q9.name", Body = "Editor quest" });
        e.QuestTextList.Add(new QuestTextInfo { Index = ++e.QuestTextIndex, TextKey = "q9.step", Body = "Hunt hens" });
        e.QuestInfoList.Add(q);
        e.QuestIndex = Editor;
    }

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };

    /// <summary>Everything that belongs to the editor quest, as text (definition, owned conditions, its texts and group).</summary>
    private static string EditorImage(Envir e) => JsonSerializer.Serialize(new
    {
        quest = e.QuestInfoList.Single(q => q.Index == Editor).Definition,
        conditions = e.QuestConditionList.Where(c => c.OwnerQuest == Editor).OrderBy(c => c.Index).ToList(),
        texts = e.QuestTextList.Where(t => t.TextKey.StartsWith("q9.", StringComparison.Ordinal)).OrderBy(t => t.Index).ToList(),
        groups = e.QuestGroupList.Where(g => g.AuthorKey.StartsWith("q9.", StringComparison.Ordinal)).ToList(),
    }, Json);

    [Fact]
    public void A_reimport_leaves_an_editor_made_quest_byte_identical()
    {
        var dir = NewTempFolder("quests-editor-kept");
        WriteLegacyFiles(dir);
        InFolder(dir, () =>
        {
            var e = World();
            QuestLegacyImporter.Import(e);
            AddEditorQuest(e);
            var rows = SqliteWorldCodec.Encode(e, 0); // the pre-save step stamps the new quest's ShapeHash / Revision, as any save does
            var before = EditorImage(e);

            var report = QuestLegacyImporter.Import(e);

            Assert.Equal(new[] { Legacy }, report.ImportedQuests);
            Assert.Equal(1, report.Quests);
            Assert.Equal(before, EditorImage(e));
            Assert.Null(SqliteWorldStore.FirstDifference(rows, SqliteWorldCodec.Encode(e, 0))); // unchanged legacy data: every row identical
            Assert.Equal(QuestShareMode.None, e.QuestInfoList.Single(q => q.Index == Editor).Definition.ShareMode);
            return 0;
        });
    }

    [Fact]
    public void A_reimport_keeps_the_rows_an_editor_added_to_a_legacy_quest()
    {
        var dir = NewTempFolder("quests-editor-rows");
        WriteLegacyFiles(dir);
        InFolder(dir, () =>
        {
            var e = World();
            QuestLegacyImporter.Import(e);
            var d = e.QuestInfoList.Single(q => q.Index == Legacy).Definition;
            var legacyStep = d.Steps.Single().Index;

            // Editor additions: an objective on the imported step with its own condition, a bonus reward, a second giver, a field the
            // legacy data has no value for, and an edit of a legacy-derived row (the gold reward), which a re-import puts back.
            var condition = ++e.QuestConditionIndex;
            e.QuestConditionList.Add(new QuestConditionInfo { Index = condition, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.TimeOfDay, Arg1 = 20, Cmp = QuestCompare.Ge, OwnerQuest = Legacy });
            var objective = new QuestObjectiveInfo { Index = ++e.QuestObjectiveIndex, Step = legacyStep, Order = 9, Type = QuestObjectiveType.Counter, Count = 1, Condition = condition, TextKey = "q1.extra" };
            d.Objectives.Add(objective);
            e.QuestTextList.Add(new QuestTextInfo { Index = ++e.QuestTextIndex, TextKey = "q1.extra", Body = "Light the beacon" });
            d.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = 2, Amount = 1 });
            d.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 7 });
            d.Flags = QuestFlags.AutoAccept;
            d.Rewards.Single(r => r.Kind == QuestRewardKind.Gold).Amount = 999;

            QuestLegacyImporter.Import(e);

            d = e.QuestInfoList.Single(q => q.Index == Legacy).Definition;
            Assert.Contains(d.Objectives, o => o.Index == objective.Index && o.Condition == condition);
            Assert.Contains(e.QuestConditionList, c => c.Index == condition);
            Assert.Equal("Light the beacon", e.QuestTextList.Single(t => t.TextKey == "q1.extra").Body);
            Assert.Contains(d.Rewards, r => r.Kind == QuestRewardKind.Item && r.TargetId == 2 && string.IsNullOrEmpty(r.LegacySource));
            Assert.Contains(d.Endpoints, x => x.TargetId == 7);
            Assert.Equal(QuestFlags.AutoAccept, d.Flags);
            Assert.Equal(50, d.Rewards.Single(r => r.Kind == QuestRewardKind.Gold).Amount); // legacy-derived: rebuilt from the body
            Assert.Equal(legacyStep, d.Steps.Single(s => s.LegacySource == "step").Index);
            return 0;
        });
    }

    [Fact]
    public void A_quest_without_a_legacy_body_loads_no_body_file()
    {
        var dir = NewTempFolder("quests-no-body");
        InFolder(dir, () =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "Envir", "Quests"));
            var q = new QuestInfo { Index = Editor, Name = "Editor quest" };
            q.LoadInfo(true); // no FileName: returns before looking for a file (no "file not found" line)
            Assert.Empty(q.KillTasks);
            Assert.False(QuestLegacyImporter.IsLegacy(q));
            Assert.True(QuestLegacyImporter.IsLegacy(new QuestInfo { FileName = "Wolves" }));
            return 0;
        });
    }

    /// <summary>
    /// The D2 start: a store whose V2 rows came from the D1 importer (no importer version recorded) is re-imported once, the version is
    /// recorded in meta, a second start does nothing, and the editor-made quest is untouched. Runs QuestImportRunner on Envir.Edit with a
    /// private SystemData.db writer in a temp folder.
    /// </summary>
    [Fact]
    public void An_older_importer_version_triggers_one_reimport_and_records_the_version()
    {
        var dir = NewTempFolder("quests-importer-version");
        WriteLegacyFiles(dir);
        lock (Gate)
        {
            var old = Environment.CurrentDirectory;
            var oldStore = Envir.Store;
            var oldDisabled = DesignStore.Disabled;
            Directory.SetCurrentDirectory(dir);
            var store = new SqliteWorldStore(privateWriter: true);
            try
            {
                Envir.Store = store;
                DesignStore.WriterOverride = store.Writer;
                DesignStore.Disabled = true; // design texts from the folder's files
                DesignTexts.ResetCache();
                EnsureBuffInfo();

                // The "D1" store: imported rows plus an editor quest, no quest_importer_version.
                var world = World();
                QuestLegacyImporter.Import(world);
                Def(world, Legacy).Objectives[0].Count = 77; // a stale D1 row the re-import must fix
                AddEditorQuest(world);
                SqliteWorldStore.Create(SqliteWorldStore.DatabasePath, SqliteWorldCodec.Encode(world, 0), null);
                Assert.Null(store.ReadMetaLong(QuestImportRunner.VersionMetaKey));

                Envir.Edit.MagicInfoList.Clear();
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(Envir.Edit));
                var before = EditorImage(Envir.Edit);

                var outcome = QuestImportRunner.Run("test start", onlyWhenEmpty: false, onlyWhenOlderVersion: true);
                Assert.True(outcome.Imported, outcome.Message);
                Assert.Equal(new[] { Legacy }, outcome.Report.ImportedQuests);
                Assert.Equal(QuestLegacyImporter.Version, store.ReadMetaLong(QuestImportRunner.VersionMetaKey));
                Assert.Equal(3, Def(Envir.Edit, Legacy).Objectives[0].Count);
                Assert.Equal(before, EditorImage(Envir.Edit));

                // Saved: a fresh load sees the fixed row and the untouched editor quest.
                var reloaded = new Envir();
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(reloaded));
                Assert.Equal(3, Def(reloaded, Legacy).Objectives[0].Count);
                Assert.Equal(before, EditorImage(reloaded));

                // The next start: up to date, nothing imported.
                var again = QuestImportRunner.Run("test start", onlyWhenEmpty: false, onlyWhenOlderVersion: true);
                Assert.False(again.Imported);
                Assert.Equal(QuestImportRunner.UpToDate, again.Message);
            }
            finally
            {
                store.Writer.Shutdown();
                DesignStore.WriterOverride = null;
                DesignStore.Disabled = oldDisabled;
                DesignTexts.ResetCache();
                Envir.Store = oldStore;
                var edit = Envir.Edit;
                edit.QuestInfoList.Clear(); edit.QuestConditionList.Clear(); edit.QuestTextList.Clear(); edit.QuestGroupList.Clear();
                edit.ItemInfoList.Clear(); edit.MonsterInfoList.Clear(); edit.MapInfoList.Clear(); edit.NPCInfoList.Clear();
                Directory.SetCurrentDirectory(old);
            }
        }
    }

    [Fact]
    public void A_legacy_quest_saved_in_the_editor_is_skipped_by_every_later_reimport()
    {
        var dir = NewTempFolder("quests-editor-owned");
        WriteLegacyFiles(dir);
        InFolder(dir, () =>
        {
            var e = World();
            QuestLegacyImporter.Import(e);
            var d = Def(e, Legacy);
            d.EditorOwned = true;                 // what a Quests page save sets (review S1)
            d.Objectives[0].Count = 9;            // an edit of an imported row
            d.Rewards.Single(r => r.Kind == QuestRewardKind.Gold).Amount = 999;
            var image = System.Text.Json.JsonSerializer.Serialize(d, Json);

            var report = QuestLegacyImporter.Import(e);

            Assert.Empty(report.ImportedQuests);
            Assert.Equal(image, System.Text.Json.JsonSerializer.Serialize(Def(e, Legacy), Json));
            Assert.Contains(e.QuestConditionList, c => c.OwnerQuest == Legacy); // its owned accept condition stays
            Assert.Contains(e.QuestTextList, t => t.TextKey == "q1.task");      // and its texts
            return 0;
        });
    }

    [Fact]
    public void Repeated_legacy_sources_are_reported_and_never_throw()
    {
        var dir = NewTempFolder("quests-dup-source");
        WriteLegacyFiles(dir);
        InFolder(dir, () =>
        {
            var e = World();
            QuestLegacyImporter.Import(e);
            var d = Def(e, Legacy);
            d.Steps.Add(new QuestStepInfo { Index = ++e.QuestStepIndex, Order = 2, LegacySource = "step" });          // hand-edited duplicate
            d.Objectives.Add(new QuestObjectiveInfo { Index = ++e.QuestObjectiveIndex, Step = d.Steps[0].Index, Type = QuestObjectiveType.Counter,
                Count = 1, LegacySource = d.Objectives[0].LegacySource });
            var firstStep = d.Steps[0].Index;
            var firstObjective = d.Objectives[0].Index;

            var report = QuestLegacyImporter.Import(e); // review S2: used to throw in ToDictionary at every start

            Assert.Equal(2, report.DuplicateLegacySources.Count);
            Assert.Equal(firstStep, Def(e, Legacy).Steps.Single(s => s.LegacySource == "step").Index);
            Assert.Equal(firstObjective, Def(e, Legacy).Objectives[0].Index);
            return 0;
        });
    }

    private static QuestDefinition Def(Envir e, int quest) => e.QuestInfoList.Single(q => q.Index == quest).Definition;
}
