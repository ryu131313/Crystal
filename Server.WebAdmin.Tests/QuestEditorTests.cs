using System.Drawing;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir.Quests;
using Server.WebAdmin.GameData;
using Server.WebAdmin.Quests;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// The web admin Quests editor (UserData-and-Quests-Plan.md, Part B, "Authoring"): whole-graph save / read round trip, ids from the
    /// counters and never reused, validation that blocks an editor save, the JSON export / import preview + apply, "where used" and the
    /// generic delete refusal. On a detached <see cref="QuestWorld"/>: no Envir, no Settings.
    /// </summary>
    public class QuestEditorTests
    {
        private const int T = QuestGraphDto.TempIdLimit; // -1000; new rows count down from here

        private static QuestWorld World()
        {
            var w = new QuestWorld();
            w.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
            w.ItemInfoList.Add(new ItemInfo { Index = 1, Name = "WoodenSword", StackSize = 1 });
            w.ItemInfoList.Add(new ItemInfo { Index = 2, Name = "Pelt", StackSize = 10 });
            w.ItemInfoList.Add(new ItemInfo { Index = 3, Name = "Unused", StackSize = 1 });
            w.MonsterInfoList.Add(new MonsterInfo { Index = 1, Name = "Wolf" });
            w.MonsterInfoList.Add(new MonsterInfo { Index = 2, Name = "WolfKing" });
            w.MonsterInfoList.Add(new MonsterInfo { Index = 3, Name = "Hen" });
            w.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Guide", Name = "Guide", Location = new Point(10, 10) });
            w.NPCInfoList.Add(new NPCInfo { Index = 2, MapIndex = 1, FileName = "Smith", Name = "Smith", Location = new Point(12, 10) });
            var g = new QuestGroupInfo { Index = 1, AuthorKey = "wolves", Kind = QuestGroupKind.Monster };
            g.Members.Add(new QuestGroupMemberInfo { TargetId = 1 });
            g.Members.Add(new QuestGroupMemberInfo { TargetId = 2 });
            w.QuestGroupList.Add(g);
            w.QuestGroupIndex = 1;
            w.QuestConditionList.Add(new QuestConditionInfo { Index = 1, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 5, Cmp = QuestCompare.Ge });
            w.QuestConditionIndex = 1;
            return w;
        }

        /// <summary>A full new quest: two steps, objectives of three kinds, a branch, endpoints, rewards of every scope, a variable and a condition tree.</summary>
        private static QuestGraphDto FullQuest()
        {
            var q = new QuestGraphDto
            {
                AuthorKey = "bichon.wolves",
                NameKey = "bichon.wolves.name",
                SummaryKey = "bichon.wolves.summary",
                Category = (int)QuestCategory.Side,
                LevelHint = 7,
                AcceptCondition = T - 1,       // owned And
                VisibleCondition = 1,          // shared
                RepeatPolicy = (int)QuestRepeatPolicy.Once,
                ShareMode = (int)QuestShareMode.Offer,
                Flags = (int)QuestFlags.HiddenInLog,
                GroupName = "Bichon",
            };
            q.Steps.Add(new QuestStepDto { Index = T - 10, Order = 1, TitleKey = "bichon.wolves.s1", NextStep = T - 11 });
            q.Steps.Add(new QuestStepDto { Index = T - 11, Order = 2, TitleKey = "bichon.wolves.s2", NextStep = -1, CompletionMode = (int)QuestStepCompletion.Any });
            q.Objectives.Add(new QuestObjectiveDto { Index = T - 20, Step = T - 10, Order = 1, Type = (int)QuestObjectiveType.Kill, TargetKind = (int)QuestTargetKind.Monster, TargetId = 1, Count = 5, Condition = T - 2 });
            q.Objectives.Add(new QuestObjectiveDto { Index = T - 21, Step = T - 10, Order = 2, Type = (int)QuestObjectiveType.Kill, TargetKind = (int)QuestTargetKind.MonsterGroup, TargetId = 1, Count = 2, Optional = true });
            q.Objectives.Add(new QuestObjectiveDto { Index = T - 22, Step = T - 11, Order = 1, Type = (int)QuestObjectiveType.Talk, TargetKind = (int)QuestTargetKind.Npc, TargetId = 2, Count = 1 });
            q.Branches.Add(new QuestBranchDto { Step = T - 11, ChoiceKey = "bichon.wolves.s2", NextStep = -1, Outcome = 1 });
            q.Endpoints.Add(new QuestEndpointDto { Role = (int)QuestEndpointRole.Start, Kind = (int)QuestEndpointKind.Npc, TargetId = 1 });
            q.Endpoints.Add(new QuestEndpointDto { Role = (int)QuestEndpointRole.Finish, Kind = (int)QuestEndpointKind.Npc, TargetId = 2 });
            q.Rewards.Add(new QuestRewardDto { Scope = (int)QuestRewardScope.Quest, Kind = (int)QuestRewardKind.Exp, Amount = 5_000_000_000L });
            q.Rewards.Add(new QuestRewardDto { Scope = (int)QuestRewardScope.Quest, Kind = (int)QuestRewardKind.Item, TargetId = 1, Amount = 1, ChoiceGroup = 1, Condition = T - 3 });
            q.Rewards.Add(new QuestRewardDto { Scope = (int)QuestRewardScope.Step, ScopeId = T - 10, Kind = (int)QuestRewardKind.Gold, Amount = 100 });
            q.Rewards.Add(new QuestRewardDto { Scope = (int)QuestRewardScope.Objective, ScopeId = T - 21, Kind = (int)QuestRewardKind.Item, TargetId = 2, Amount = 3, FirstTimeOnly = true });
            q.Vars.Add(new QuestVarDto { Index = T - 30, AuthorKey = "count", Default = 2 });
            q.Conditions.Add(new QuestConditionDto { Index = T - 1, Op = (int)QuestConditionOp.And });
            q.Conditions.Add(new QuestConditionDto { Index = T - 4, Op = (int)QuestConditionOp.Leaf, Parent = T - 1, Order = 1, Leaf = (int)QuestConditionLeaf.Level, Arg1 = 7, Cmp = (int)QuestCompare.Ge });
            q.Conditions.Add(new QuestConditionDto { Index = T - 5, Op = (int)QuestConditionOp.Leaf, Parent = T - 1, Order = 2, Leaf = (int)QuestConditionLeaf.QuestVar, Arg1 = T - 30, Arg2 = 0, Cmp = (int)QuestCompare.Ge });
            q.Conditions.Add(new QuestConditionDto { Index = T - 2, Op = (int)QuestConditionOp.Leaf, Leaf = (int)QuestConditionLeaf.TimeOfDay, Arg1 = 20, Cmp = (int)QuestCompare.Ge });
            q.Conditions.Add(new QuestConditionDto { Index = T - 3, Op = (int)QuestConditionOp.Leaf, Leaf = (int)QuestConditionLeaf.Gender, Arg1 = 1 });
            q.Texts["bichon.wolves.name"] = "Wolves of Bichon";
            q.Texts["bichon.wolves.summary"] = "The wolves are back.\nThin them out.";
            q.Texts["bichon.wolves.s1"] = "Kill wolves";
            q.Texts["bichon.wolves.s2"] = "Report to the smith";
            return q;
        }

        private sealed class Persist
        {
            public int Calls;
            public bool Fail;
            public void Run()
            {
                if (Fail) throw new WebAdminException(500, "disk full");
                Calls++;
            }
        }

        private static (QuestEditorService Svc, Persist Persist) Editor(QuestWorld w)
        {
            var p = new Persist();
            return (new QuestEditorService(w, p.Run), p);
        }

        private static List<QuestProblemDto> ProblemsOf(WebAdminException ex)
        {
            var json = JsonSerializer.Serialize(ex.Payload, WebAdminJson.Options);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("problems").EnumerateArray()
                .Select(p => new QuestProblemDto { Quest = p.GetProperty("quest").GetInt32(), Warning = p.TryGetProperty("warning", out var w) && w.GetBoolean(), Message = p.GetProperty("message").GetString() })
                .ToList();
        }

        // ------------------------------------------------------------------ round trip

        [Fact]
        public void Full_graph_saves_with_counter_ids_and_reads_back_identically()
        {
            var w = World();
            var (svc, persist) = Editor(w);
            var result = svc.Save(FullQuest(), dryRun: false);

            Assert.True(result.Created);
            Assert.Equal(1, result.Index);
            Assert.Equal(1, persist.Calls);
            Assert.DoesNotContain(result.Problems, p => !p.Warning);
            Assert.Equal(new[] { 1 }, result.ChangedQuests);
            Assert.False(result.SharedTablesChanged);

            var q = Assert.Single(w.QuestInfoList);
            Assert.Equal("Wolves of Bichon", q.Name); // legacy name from the name text
            Assert.Equal(new[] { 1, 2 }, q.Definition.Steps.Select(s => s.Index));
            Assert.Equal(2, q.Definition.Steps[0].NextStep);
            Assert.Equal(new[] { 1, 2, 3 }, q.Definition.Objectives.Select(o => o.Index));
            Assert.Equal(1, q.Definition.Rewards[2].ScopeId);        // step reward → step 1
            Assert.Equal(2, q.Definition.Rewards[3].ScopeId);        // objective reward → objective 2
            Assert.Equal(1, q.Definition.Vars.Single().Index);
            Assert.Equal(5_000_000_000L, q.Definition.Rewards[0].Amount);
            var owned = w.QuestConditionList.Where(c => c.OwnerQuest == 1).OrderBy(c => c.Index).ToList();
            Assert.Equal(new[] { 2, 3, 4, 5, 6 }, owned.Select(c => c.Index));
            var and = owned.Single(c => c.Op == QuestConditionOp.And);
            Assert.Equal(and.Index, q.Definition.AcceptCondition);
            Assert.Equal(2, owned.Count(c => c.Parent == and.Index));
            Assert.Equal(1, owned.Single(c => c.Leaf == QuestConditionLeaf.QuestVar).Arg1); // temp variable id mapped
            Assert.Equal(1, q.Definition.VisibleCondition);                                 // shared stays shared
            Assert.Equal(4, w.QuestTextList.Count);
            Assert.All(w.QuestTextList, t => Assert.Equal(string.Empty, t.Lang));
            Assert.Equal((1, 2, 3, 6, 1, 4), (w.QuestIndex, w.QuestStepIndex, w.QuestObjectiveIndex, w.QuestConditionIndex, w.QuestVarIndex, w.QuestTextIndex));

            // Read → JSON → save unchanged: same ids, nothing new allocated, the rev still matches.
            var read = svc.Read(1);
            var json = JsonSerializer.Serialize(read, WebAdminJson.Options);
            var back = JsonSerializer.Deserialize<QuestGraphDto>(json, WebAdminJson.Options);
            Assert.Equal(read.Rev, QuestEditorService.RevOf(back));
            Assert.Equal("The wolves are back.\nThin them out.", back.Texts["bichon.wolves.summary"]);
            var again = svc.Save(back, dryRun: false);
            Assert.False(again.Created);
            Assert.Equal(read.Rev, again.Rev);
            Assert.Equal(2, persist.Calls);
            Assert.Equal((1, 2, 3, 6, 1, 4), (w.QuestIndex, w.QuestStepIndex, w.QuestObjectiveIndex, w.QuestConditionIndex, w.QuestVarIndex, w.QuestTextIndex));
            Assert.Equal(JsonSerializer.Serialize(read, WebAdminJson.Options), JsonSerializer.Serialize(svc.Read(1), WebAdminJson.Options));
        }

        [Fact]
        public void Stale_rev_is_refused_and_a_dry_run_changes_nothing()
        {
            var w = World();
            var (svc, persist) = Editor(w);
            svc.Save(FullQuest(), false);
            var read = svc.Read(1);
            read.LevelHint = 9;
            var dry = svc.Save(read, dryRun: true);
            Assert.True(dry.DryRun);
            Assert.Equal(7, w.QuestInfoList[0].Definition.LevelHint);
            Assert.Equal(1, persist.Calls);

            svc.Save(read, false);
            read.LevelHint = 11; // read.Rev is the pre-save token now
            var ex = Assert.Throws<WebAdminException>(() => svc.Save(read, false));
            Assert.Equal(409, ex.Status);
        }

        // ------------------------------------------------------------------ ids

        [Fact]
        public void Deleted_ids_are_never_reused()
        {
            var w = World();
            var (svc, _) = Editor(w);
            svc.Save(FullQuest(), false);

            // Remove objective 3 (and step 2 with it): the counters stay; a new objective gets 4, not 3.
            var read = svc.Read(1);
            read.Objectives.RemoveAll(o => o.Index == 3);
            read.Branches.Clear();
            read.Steps.RemoveAll(s => s.Index == 2);
            read.Steps[0].NextStep = -1;
            read.Objectives.Add(new QuestObjectiveDto { Index = T - 1, Step = 1, Order = 3, Type = (int)QuestObjectiveType.Kill, TargetKind = (int)QuestTargetKind.Monster, TargetId = 3, Count = 1 });
            svc.Save(read, false);
            Assert.Equal(new[] { 1, 2, 4 }, w.QuestInfoList[0].Definition.Objectives.Select(o => o.Index));
            Assert.Equal(4, w.QuestObjectiveIndex);

            // Sending a retired id back is refused.
            read = svc.Read(1);
            read.Objectives.Add(new QuestObjectiveDto { Index = 3, Step = 1, Order = 9, Type = (int)QuestObjectiveType.Kill, TargetKind = (int)QuestTargetKind.Monster, TargetId = 3, Count = 1 });
            var ex = Assert.Throws<WebAdminException>(() => svc.Save(read, false));
            Assert.Equal(422, ex.Status);
            Assert.Contains(ProblemsOf(ex), p => p.Message.Contains("objective id 3 is not one of this quest's"));

            // A deleted quest's index is retired too, with its owned conditions and its texts.
            svc.Delete(1, svc.Read(1).Rev);
            Assert.Empty(w.QuestInfoList);
            Assert.DoesNotContain(w.QuestConditionList, c => c.OwnerQuest == 1);
            Assert.Empty(w.QuestTextList);
            Assert.Single(w.QuestConditionList); // the shared one stays
            var next = FullQuest();
            next.AuthorKey = "again";
            Assert.Equal(2, svc.Save(next, false).Index);
        }

        // ------------------------------------------------------------------ validation

        [Fact]
        public void Validation_errors_block_an_editor_save_and_list_every_error()
        {
            var w = World();
            var (svc, persist) = Editor(w);
            var q = FullQuest();
            q.Objectives[0].TargetId = 99;                       // missing monster
            q.Objectives[2].TargetKind = (int)QuestTargetKind.Item; // Talk needs an Npc target
            q.Texts.Remove("bichon.wolves.s1");                 // a key with no text

            var ex = Assert.Throws<WebAdminException>(() => svc.Save(q, false));
            Assert.Equal(422, ex.Status);
            var problems = ProblemsOf(ex).Where(p => !p.Warning).ToList();
            Assert.Contains(problems, p => p.Message.Contains("missing monster 99"));
            Assert.Contains(problems, p => p.Message.Contains("(Talk) needs an Npc target"));
            Assert.Contains(problems, p => p.Message.Contains("missing text bichon.wolves.s1"));

            // Nothing changed: no quest, no rows, counters back, nothing written.
            Assert.Empty(w.QuestInfoList);
            Assert.Single(w.QuestConditionList);
            Assert.Empty(w.QuestTextList);
            Assert.Equal((0, 0, 0, 1), (w.QuestIndex, w.QuestStepIndex, w.QuestObjectiveIndex, w.QuestConditionIndex));
            Assert.Equal(0, persist.Calls);
        }

        [Fact]
        public void Errors_of_other_quests_do_not_block_and_a_failed_write_rolls_back()
        {
            var w = World();
            var legacy = new QuestInfo { Index = 5, Name = "Broken import" };
            legacy.Definition.AuthorKey = "q5";
            legacy.Definition.NameKey = "q5.name"; // the text is missing: an error on quest 5
            legacy.Definition.Steps.Add(new QuestStepInfo { Index = 1 });
            w.QuestInfoList.Add(legacy);
            w.QuestIndex = 5; w.QuestStepIndex = 1;
            var (svc, persist) = Editor(w);

            var result = svc.Save(FullQuest(), false);
            Assert.Equal(6, result.Index);
            Assert.All(result.Problems, p => Assert.Equal(6, p.Quest));

            persist.Fail = true;
            var read = svc.Read(6);
            read.LevelHint = 50;
            Assert.Throws<WebAdminException>(() => svc.Save(read, false));
            Assert.Equal(7, w.QuestInfoList.Single(x => x.Index == 6).Definition.LevelHint);
        }

        [Fact]
        public void Structural_errors_are_refused_before_anything_changes()
        {
            var w = World();
            var (svc, _) = Editor(w);
            var q = FullQuest();
            q.Category = 99;
            q.Steps[0].Index = -5;                 // neither an existing id nor a temporary one
            q.Conditions[1].Parent = 77;           // not one of the quest's
            var ex = Assert.Throws<WebAdminException>(() => svc.Save(q, false));
            Assert.Equal(422, ex.Status);
            var messages = ProblemsOf(ex).Select(p => p.Message).ToList();
            Assert.Contains(messages, m => m.Contains("unknown Category 99"));
            Assert.Contains(messages, m => m.Contains("step id -5 is invalid"));
            Assert.Empty(w.QuestInfoList);
        }

        // ------------------------------------------------------------------ export / import

        [Fact]
        public void Import_previews_a_diff_then_applies_with_the_token()
        {
            var w = World();
            var (svc, persist) = Editor(w);
            svc.Save(FullQuest(), false);
            var file = svc.Export(1, new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("Wolf", file.Refs["monster"]["1"]);
            Assert.Equal("Smith", file.Refs["npc"]["2"]);
            Assert.Equal("wolves", Assert.Single(file.Groups).AuthorKey);

            file.Quest.Objectives[0].Count = 8;
            file.Quest.Texts["bichon.wolves.s1"] = "Kill more wolves";
            file.Quest.Rewards.RemoveAt(2);
            var json = JsonSerializer.Serialize(file, WebAdminJson.Options);

            var preview = svc.Import(json, apply: false, token: null);
            Assert.Equal("update", preview.Mode);
            Assert.True(preview.DryRun);
            Assert.Contains(preview.Diff, l => l == "objective 1 Count: 5 → 8");
            Assert.Contains(preview.Diff, l => l.StartsWith("text bichon.wolves.s1: ", StringComparison.Ordinal));
            Assert.Contains(preview.Diff, l => l == "reward #4 removed");
            Assert.NotNull(preview.Token);
            Assert.Equal(5, w.QuestInfoList[0].Definition.Objectives[0].Count); // nothing applied yet
            Assert.Equal(1, persist.Calls);

            var bad = Assert.Throws<WebAdminException>(() => svc.Import(json, apply: true, token: "nope"));
            Assert.Equal(409, bad.Status);

            var applied = svc.Import(json, apply: true, token: preview.Token);
            Assert.Equal(1, applied.Index);
            Assert.Equal(8, w.QuestInfoList[0].Definition.Objectives[0].Count);
            Assert.Equal(3, w.QuestInfoList[0].Definition.Rewards.Count);
            Assert.Equal("Kill more wolves", w.QuestTextList.Single(t => t.TextKey == "bichon.wolves.s1").Body);
            Assert.Equal(2, persist.Calls);

            // The preview token is spent: the quest changed since.
            Assert.Equal(409, Assert.Throws<WebAdminException>(() => svc.Import(json, apply: true, token: preview.Token)).Status);
        }

        [Fact]
        public void Import_into_another_world_creates_the_quest_and_its_group_with_fresh_ids()
        {
            var source = World();
            var (from, _) = Editor(source);
            from.Save(FullQuest(), false);
            var json = JsonSerializer.Serialize(from.Export(1, DateTime.UtcNow), WebAdminJson.Options);

            var target = World();
            target.QuestGroupList.Clear();             // no "wolves" group here
            target.QuestIndex = 40; target.QuestObjectiveIndex = 100; target.QuestGroupIndex = 7;
            var (to, _) = Editor(target);
            var preview = to.Import(json, false, null);
            Assert.Equal("create", preview.Mode);
            Assert.Contains(preview.Problems, p => p.Warning && p.Message.Contains("the import creates it"));
            Assert.DoesNotContain(preview.Problems, p => !p.Warning);
            Assert.Empty(target.QuestInfoList);

            var applied = to.Import(json, true, preview.Token);
            Assert.Equal(41, applied.Index);
            Assert.True(applied.SharedTablesChanged);
            var q = Assert.Single(target.QuestInfoList);
            Assert.Equal(new[] { 101, 102, 103 }, q.Definition.Objectives.Select(o => o.Index));
            var group = Assert.Single(target.QuestGroupList);
            Assert.Equal(8, group.Index);
            Assert.Equal(8, q.Definition.Objectives[1].TargetId);
        }

        // ------------------------------------------------------------------ where used / delete refusal

        [Fact]
        public void Where_used_finds_objectives_groups_endpoints_rewards_and_conditions()
        {
            var w = World();
            var (svc, _) = Editor(w);
            svc.Save(FullQuest(), false);

            var wolf = svc.WhereUsed(QuestRefKind.Monster, 1);
            Assert.Contains(wolf, u => u.Quest == 1 && u.Where.StartsWith("objective 1", StringComparison.Ordinal));
            Assert.Contains(wolf, u => u.Quest == 0 && u.Where.Contains("quest group 1"));
            Assert.Contains(wolf, u => u.Quest == 1 && u.Where.Contains("through group 1"));
            Assert.Contains(svc.WhereUsed(QuestRefKind.Npc, 1), u => u.Where == "Start endpoint (Npc)");
            Assert.Contains(svc.WhereUsed(QuestRefKind.Item, 2), u => u.Where.Contains("reward (item)"));
            Assert.Contains(svc.WhereUsed(QuestRefKind.Group, 1), u => u.Quest == 1);
            Assert.Contains(svc.WhereUsed(QuestRefKind.Condition, 1), u => u.Where == "visible condition");
            Assert.Empty(svc.WhereUsed(QuestRefKind.Monster, 3));
            Assert.Empty(svc.WhereUsed(QuestRefKind.Item, 3));
            Assert.Equal("Wolves of Bichon", wolf.First(u => u.Quest == 1).QuestLabel);
        }

        [Fact]
        public void Generic_delete_of_a_referenced_target_is_refused_with_where_used()
        {
            var w = World();
            var (svc, _) = Editor(w);
            svc.Save(FullQuest(), false);
            var ctx = new GameDataContext
            {
                Items = w.ItemInfoList, Monsters = w.MonsterInfoList, Npcs = w.NPCInfoList, Maps = w.MapInfoList, Quests = w.QuestInfoList,
                QuestConditions = w.QuestConditionList, QuestGroups = w.QuestGroupList, QuestPools = w.QuestPoolList, QuestTexts = w.QuestTextList,
            };

            var monsters = GameDataRegistry.Get("monsters");
            var refused = GameDataEngine.Delete(ctx, monsters, new[] { 2 }, false); // only in the group the quest uses
            Assert.Equal(0, refused.Deleted);
            var reject = JsonSerializer.Serialize(Assert.Single(refused.Rejects), WebAdminJson.Options);
            Assert.Contains("in-use", reject);
            Assert.Contains("Used by quest definitions", reject);
            Assert.Equal(1, GameDataEngine.Delete(ctx, monsters, new[] { 3 }, false).Deleted); // Hen is not used

            // Both refusals are listed: the table's own guard (a respawn) and the quest references.
            w.MapInfoList[0].Respawns.Add(new RespawnInfo { MonsterIndex = 1, RespawnIndex = 1 });
            var both = JsonSerializer.Serialize(Assert.Single(GameDataEngine.Delete(ctx, monsters, new[] { 1 }, true).Rejects), WebAdminJson.Options);
            Assert.Contains("respawn #1", both);
            Assert.Contains("Used by quest definitions", both);

            Assert.Single(GameDataEngine.Delete(ctx, GameDataRegistry.Get("npcs"), new[] { 2 }, true).Rejects);
            Assert.Single(GameDataEngine.Delete(ctx, GameDataRegistry.Get("items"), new[] { 1 }, true).Rejects);
            Assert.Empty(GameDataEngine.Delete(ctx, GameDataRegistry.Get("items"), new[] { 3 }, true).Rejects);
            Assert.Single(GameDataEngine.Delete(ctx, GameDataRegistry.Get("maps"), new[] { 1 }, true).Rejects);
            Assert.NotNull(QuestWhereUsed.DeleteGuard(ctx, "questgroups", 1));
            Assert.NotNull(QuestWhereUsed.DeleteGuard(ctx, "conditions", 1));
        }

        [Fact]
        public void A_quest_still_referenced_cannot_be_deleted_from_the_editor()
        {
            var w = World();
            var (svc, _) = Editor(w);
            svc.Save(FullQuest(), false);
            var second = FullQuest();
            second.AuthorKey = "followup";
            second.Conditions.Add(new QuestConditionDto { Index = T - 50, Op = (int)QuestConditionOp.Leaf, Parent = T - 1, Order = 3, Leaf = (int)QuestConditionLeaf.QuestCompletedCount, Arg1 = 1, Arg2 = 1, Cmp = (int)QuestCompare.Ge });
            svc.Save(second, false);

            var ex = Assert.Throws<WebAdminException>(() => svc.Delete(1, svc.Read(1).Rev));
            Assert.Equal(409, ex.Status);
            Assert.Equal(2, w.QuestInfoList.Count);
        }

        [Fact]
        public void Groups_are_created_validated_and_refused_while_used()
        {
            var w = World();
            var (svc, _) = Editor(w);
            var created = svc.SaveGroup(new QuestGroupDto { AuthorKey = "ore", Kind = (int)QuestGroupKind.Item, Members = new List<int> { 2, 3 } });
            Assert.Equal(2, created.Index);
            Assert.True(created.SharedTablesChanged);
            Assert.Equal(422, Assert.Throws<WebAdminException>(() => svc.SaveGroup(new QuestGroupDto { AuthorKey = "bad", Kind = (int)QuestGroupKind.Monster, Members = new List<int> { 99 } })).Status);

            svc.Save(FullQuest(), false);
            Assert.Equal(409, Assert.Throws<WebAdminException>(() => svc.DeleteGroup(1)).Status);
            var renamed = svc.SaveGroup(new QuestGroupDto { Index = 1, AuthorKey = "wolves", Kind = (int)QuestGroupKind.Monster, Members = new List<int> { 1 } });
            Assert.Equal(new[] { 1 }, renamed.ChangedQuests);
            svc.DeleteGroup(2);
            Assert.Single(w.QuestGroupList);
        }

        // ------------------------------------------------------------------ routes

        [Theory]
        [InlineData("GET", "/api/admin/quests", "/api/admin/quests")]
        [InlineData("GET", "/api/admin/quests/meta", "/api/admin/quests/meta")]
        [InlineData("GET", "/api/admin/quests/where-used", "/api/admin/quests/where-used")]
        [InlineData("GET", "/api/admin/quests/12", "/api/admin/quests/{index:int}")]
        [InlineData("GET", "/api/admin/quests/12/export", "/api/admin/quests/{index:int}/export")]
        [InlineData("POST", "/api/admin/quests/save", "/api/admin/quests/save")]
        [InlineData("POST", "/api/admin/quests/12/delete", "/api/admin/quests/{index:int}/delete")]
        [InlineData("POST", "/api/admin/quests/import", "/api/admin/quests/import")]
        [InlineData("POST", "/api/admin/quests/groups/save", "/api/admin/quests/groups/save")]
        [InlineData("POST", "/api/admin/quests/groups/3/delete", "/api/admin/quests/groups/{index:int}/delete")]
        [InlineData("POST", "/api/admin/quests/reimport", "/api/admin/quests/reimport")]
        [InlineData("GET", "/api/admin/quests/validation", "/api/admin/quests/validation")]
        public void Routes_are_admin_only_and_writes_are_csrf_checked_posts(string method, string path, string pattern)
        {
            var router = new WebAdminRouter();
            new QuestEditorRoutes().Register(router);
            new Routes.QuestDefinitionRoutes().Register(router);
            var match = router.Match(method, path);
            Assert.True(match.Success, method + " " + path);
            Assert.Equal(pattern, match.Route.Pattern);
            // RouteAuth.Admin: the host checks the session and, for POST (mutating), the X-MirCraft-CSRF header (AccountCacheHookTests).
            Assert.Equal(RouteAuth.Admin, match.Route.Auth);
            Assert.Equal(method, match.Route.Method);
        }
    }

    [Collection(WebAdminGlobalsCollection.Name)]
    public class QuestEditNotifierTests
    {
        [Fact]
        public void Notifier_marks_a_restart_pending_while_the_world_is_not_running()
        {
            // The live path (snapshot + quest replace on the game thread) is covered by Server.Persistence.Tests/QuestReplaceTests, which
            // can use Envir; here only the "world stopped" branch, with a fake world (no Envir, no Settings).
            var sink = WebAdminLog.Sink;
            var lines = new List<string>();
            WebAdminLog.Sink = lines.Add;
            WebAdminPending.Clear();
            WebAdminGame.World = FakeWorld.Stopped();
            try
            {
                var outcome = QuestEditNotifier.QuestsChanged(new[] { 3, 7 }, false);
                Assert.False(outcome.Applied);
                Assert.Contains("next world start", outcome.Message);
                var item = Assert.Single(WebAdminPending.Snapshot());
                Assert.Equal("quests", item.Area);
                Assert.Contains("3, 7", item.Description);
                Assert.Contains(lines, l => l.Contains("[Quests]"));

                Assert.True(QuestEditNotifier.QuestsChanged(Array.Empty<int>(), false).Applied); // nothing changed: nothing pending
                Assert.Single(WebAdminPending.Snapshot());
            }
            finally
            {
                WebAdminGame.World = null;
                WebAdminPending.Clear();
                WebAdminLog.Sink = sink;
            }
        }
    }
}
