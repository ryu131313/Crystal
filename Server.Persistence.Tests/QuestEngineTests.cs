using System.Drawing;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.MirObjects;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Quest V2 engine (UserData-and-Quests-Plan.md, lane F): progress and completion of a hand-built quest pair (what the legacy importer
/// used to produce; the differential test against the legacy QuestProgressInfo oracle went with it in wave 4), rewards incl. the rate
/// multipliers and the gender fix, conditions, the revision migration, repeat policies / ResetClock boundaries and the per-member party
/// drop copies. Runs on Envir.Main (the engine reads Main), so it restores Main's lists afterwards; no server, no randomness.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestEngineTests
{
    private const int Wolves = 1, Letter = 2;

    /// <summary>
    /// Wolves (Daily, share Offer, 10 min): kill 1 of group {WolfKing} and 2 of group {Wolf, Wolf2}, collect 2 Pelt, flag 12; rewards
    /// BaseDress(M)/(F) by gender, 2 WoodenSword, a Medal pick (choice group 1), 100 exp, 50 gold. Letter (Story, Once, needs Wolves done
    /// once): collect 1 QuestLetter; 7 credit. Both given and taken by the Hunter NPC.
    /// </summary>
    private static void BuildWorld()
    {
        ResetMain();
        var e = Envir.Main;
        e.QuestConditionList.Clear(); e.QuestTextList.Clear(); e.QuestGroupList.Clear(); e.QuestPoolList.Clear(); e.StorylineList.Clear();
        e.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
        string[] items = { "Pelt", "QuestLetter", "BaseDress(M)", "BaseDress(F)", "WoodenSword", "Medal" };
        for (var i = 0; i < items.Length; i++) e.ItemInfoList.Add(new ItemInfo { Index = i + 1, Name = items[i], StackSize = 10 });
        string[] monsters = { "Wolf", "WolfKing", "Wolf2", "Hen" };
        for (var i = 0; i < monsters.Length; i++) e.MonsterInfoList.Add(new MonsterInfo { Index = i + 1, Name = monsters[i] });
        e.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Hunter", Name = "Hunter", Location = new Point(1, 1) });
        e.MapIndex = 1; e.ItemIndex = items.Length; e.MonsterIndex = monsters.Length; e.NPCIndex = 1; e.QuestIndex = 2;

        int ItemId(string name) => e.ItemInfoList.Single(x => x.Name == name).Index;
        int MonsterId(string name) => e.MonsterInfoList.Single(x => x.Name == name).Index;

        e.QuestGroupList.Add(new QuestGroupInfo { Index = 1, AuthorKey = "q1.kill.WolfKing", Kind = QuestGroupKind.Monster, Members = { new QuestGroupMemberInfo { TargetId = MonsterId("WolfKing") } } });
        e.QuestGroupList.Add(new QuestGroupInfo
        {
            Index = 2, AuthorKey = "q1.kill.Wolf", Kind = QuestGroupKind.Monster,
            Members = { new QuestGroupMemberInfo { TargetId = MonsterId("Wolf") }, new QuestGroupMemberInfo { TargetId = MonsterId("Wolf2") } },
        });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 1, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Gender, Arg1 = (int)MirGender.Male, OwnerQuest = Wolves });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 2, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Gender, Arg1 = (int)MirGender.Female, OwnerQuest = Wolves });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 3, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.QuestCompletedCount, Arg1 = Wolves, Arg2 = 1, Cmp = QuestCompare.Ge, OwnerQuest = Letter });

        var wolves = new QuestInfo { Index = Wolves, Name = "Wolves" };
        var w = wolves.Definition;
        w.Category = QuestCategory.Daily; w.RepeatPolicy = QuestRepeatPolicy.Daily; w.ShareMode = QuestShareMode.Offer; w.TimeLimitSeconds = 600;
        w.Steps.Add(new QuestStepInfo { Index = 100, Order = 1, NextStep = -1 });
        w.Objectives.Add(new QuestObjectiveInfo { Index = 1001, Step = 100, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 1, Count = 1 });
        w.Objectives.Add(new QuestObjectiveInfo { Index = 1002, Step = 100, Order = 2, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 2, Count = 2 });
        w.Objectives.Add(new QuestObjectiveInfo { Index = 1003, Step = 100, Order = 3, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = ItemId("Pelt"), Count = 2 });
        w.Objectives.Add(new QuestObjectiveInfo { Index = 1004, Step = 100, Order = 4, Type = QuestObjectiveType.Flag, TargetKind = QuestTargetKind.Flag, TargetId = 12, Count = 1 });
        w.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        w.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Finish, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        w.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = ItemId("BaseDress(M)"), Amount = 1, Condition = 1 });
        w.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = ItemId("BaseDress(F)"), Amount = 1, Condition = 2 });
        w.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = ItemId("WoodenSword"), Amount = 2 });
        w.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = ItemId("Medal"), Amount = 1, ChoiceGroup = 1 });
        w.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Exp, Amount = 100 });
        w.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Gold, Amount = 50 });
        e.QuestInfoList.Add(wolves);

        var letter = new QuestInfo { Index = Letter, Name = "Letter" };
        var l = letter.Definition;
        l.Category = QuestCategory.Story; l.RepeatPolicy = QuestRepeatPolicy.Once; l.ShareMode = QuestShareMode.Offer; l.AcceptCondition = 3;
        l.Steps.Add(new QuestStepInfo { Index = 200, Order = 1, NextStep = -1 });
        l.Objectives.Add(new QuestObjectiveInfo { Index = 2001, Step = 200, Order = 1, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = ItemId("QuestLetter"), Count = 1 });
        l.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        l.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Finish, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        l.Rewards.Add(new QuestRewardInfo { Kind = QuestRewardKind.Credit, Amount = 7 });
        e.QuestInfoList.Add(letter);

        e.QuestStepIndex = 200; e.QuestObjectiveIndex = 2001; e.QuestConditionIndex = 3; e.QuestGroupIndex = 2;
        e.InvalidateQuestCatalog();
    }

    private static void InWorld(Action body)
    {
        var dir = NewTempFolder("quest-engine");
        InFolder(dir, () =>
        {
            var dropRate = Settings.DropRate;
            var expRate = Settings.ExpRate;
            try
            {
                BuildWorld();
                body();
            }
            finally
            {
                Settings.DropRate = dropRate;
                Settings.ExpRate = expRate;
                var e = Envir.Main;
                e.QuestConditionList.Clear(); e.QuestTextList.Clear(); e.QuestGroupList.Clear();
                ResetMain();
                e.InvalidateQuestCatalog();
            }

            return 0;
        });
    }

    private static PlayerObject NewPlayer(string name, MirGender gender = MirGender.Male, ushort level = 10, int index = 1)
    {
        var p = new PlayerObject();
        p.Info = new CharacterInfo { Index = index, Name = name, Level = level, Class = MirClass.Warrior, Gender = gender };
        p.Report = new Reporting(p);
        p.Quests.Login();
        return p;
    }

    private static MonsterInfo Monster(string name) => Envir.Main.MonsterInfoList.Single(m => m.Name == name);
    private static ItemInfo Item(string name) => Envir.Main.ItemInfoList.Single(i => i.Name == name);
    private static QuestRuntime Runtime(int quest) => Envir.Main.QuestCatalog.Get(quest);

    private static bool Ready(PlayerObject p, int quest) => p.Quests.StatusOf(p.Quests.GetRun(quest)) == QuestRunStatus.Ready;

    private static void AddToBag(UserItem[] bag, ItemInfo info, ushort count)
    {
        for (var i = 0; i < bag.Length; i++)
        {
            if (bag[i] != null) continue;
            bag[i] = new UserItem(info) { Count = count, UniqueID = ++Envir.Main.NextUserItemID };
            return;
        }
    }

    private static void Kill(PlayerObject p, string monster, bool direct = true) => p.Quests.MonsterKilled(Monster(monster), direct);

    private static void Gain(PlayerObject p, string item, ushort count)
    {
        var info = Item(item);
        if (!p.Quests.NeedsQuestItem(info)) return;
        AddToBag(p.Info.QuestInventory, info, count);
        p.Quests.QuestBagChanged(info);
    }

    private static void Flag(PlayerObject p, int flag, bool on)
    {
        p.Info.Flags[flag] = on;
        p.Quests.FlagChanged(flag);
    }

    [Fact]
    public void Progress_and_completion_follow_the_objectives()
    {
        InWorld(() =>
        {
            var p = NewPlayer("Prog");
            Assert.True(p.Quests.AdminStart(Wolves));
            Assert.False(Ready(p, Wolves));
            Kill(p, "Hen"); // unrelated
            Kill(p, "Wolf");
            Kill(p, "Wolf2"); // same group as Wolf: 2/2
            Kill(p, "Wolf"); // over the count
            Assert.False(Ready(p, Wolves));
            Kill(p, "WolfKing"); // own group
            Assert.False(p.Quests.NeedsQuestItem(Item("Medal"))); // no objective needs it
            Gain(p, "Pelt", 1);
            Gain(p, "Pelt", 1);
            Assert.False(Ready(p, Wolves));
            Flag(p, 12, true);
            Assert.True(Ready(p, Wolves));

            // Flag objectives are read live (deliberate V2 change): clearing the flag makes the quest not Ready again.
            Flag(p, 12, false);
            Assert.False(Ready(p, Wolves));
            Flag(p, 12, true);
            Assert.True(Ready(p, Wolves));

            // The chained quest: the accept condition reads the V2 history.
            Assert.False(p.Quests.CanTake(Runtime(Letter)));
            p.Quests.AdminComplete(Wolves); // TurnIn gives exp and items, which need a spawned player: the scratch run covers it
            Assert.Null(p.Quests.GetRun(Wolves));
            Assert.Equal(1, p.Quests.TimesCompleted(Wolves));
            Assert.True(p.Quests.CanTake(Runtime(Letter)));

            Assert.True(p.Quests.AdminStart(Letter));
            Assert.False(Ready(p, Letter));
            Gain(p, "QuestLetter", 1);
            Assert.True(Ready(p, Letter));
        });
    }

    [Fact]
    public void Rewards_apply_the_rates_and_give_the_players_gender_only()
    {
        InWorld(() =>
        {
            Settings.DropRate = 2F;
            Settings.ExpRate = 3F;
            foreach (var gender in new[] { MirGender.Male, MirGender.Female })
            {
                var p = NewPlayer("Rw" + gender, gender);
                var q = Runtime(Wolves);
                var filtered = p.Quests.FilteredRewards(q);
                var medal = filtered.FindIndex(r => r.Kind == QuestRewardKind.Item && r.TargetId == Item("Medal").Index);
                Assert.True(medal >= 0);
                var preview = p.Quests.PreviewRewards(q, new List<int> { medal });

                var dress = gender == MirGender.Male ? "BaseDress(M)" : "BaseDress(F)";
                var expected = new[] { (Item(dress).Index, 1L), (Item("WoodenSword").Index, 2L), (Item("Medal").Index, 1L) }.OrderBy(x => x).ToList();
                Assert.Equal(expected, preview.Items.OrderBy(x => x).ToList());
                Assert.Equal(100u, preview.Gold); // 50 × DropRate 2
                Assert.Equal(300u, preview.Exp); // 100 × ExpRate 3

                // No pick: nothing from the choice group.
                Assert.Equal(expected.Count - 1, p.Quests.PreviewRewards(q, null).Items.Count);
            }

            Assert.Equal(7u, NewPlayer("RwLetter").Quests.PreviewRewards(Runtime(Letter), null).Credit);
        });
    }

    [Fact]
    public void Party_kill_credit_counts_for_each_member()
    {
        InWorld(() =>
        {
            var killer = NewPlayer("Killer", index: 1);
            var member = NewPlayer("Member", index: 2);
            foreach (var p in new[] { killer, member }) Assert.True(p.Quests.AdminStart(Wolves));
            foreach (var m in new[] { "Wolf", "WolfKing", "Wolf2" })
            {
                Kill(killer, m, direct: true);
                Kill(member, m, direct: false); // PartyCredit.Each: party members in range get the same credit
            }

            foreach (var p in new[] { killer, member })
            {
                var q = Runtime(Wolves);
                var run = p.Quests.GetRun(Wolves);
                Assert.Equal(1, p.Quests.ValueOf(q, run, q.ObjectivesById[1001]));
                Assert.Equal(2, p.Quests.ValueOf(q, run, q.ObjectivesById[1002]));
            }
        });
    }

    [Fact]
    public void Party_quest_drop_gives_each_member_their_own_copy()
    {
        InWorld(() =>
        {
            var a = NewPlayer("PartyA", index: 1);
            var b = NewPlayer("PartyB", index: 2);
            var c = NewPlayer("PartyC", index: 3); // in the party, but not on the quest
            foreach (var p in new[] { a, b }) { p.Quests.AdminComplete(Wolves); Assert.True(p.Quests.AdminStart(Letter)); }
            var group = new List<PlayerObject> { a, b, c };
            var list = new LinkedList<MapObject>();
            foreach (var p in group) { p.GroupMembers = group; p.Node = list.AddLast(p); }

            var drop = Envir.Main.CreateFreshItem(Item("QuestLetter"));
            Assert.True(a.CheckGroupQuestItem(drop));

            var itemA = a.Info.QuestInventory.Single(i => i != null);
            var itemB = b.Info.QuestInventory.Single(i => i != null);
            Assert.Same(drop, itemA);
            Assert.NotSame(itemA, itemB);
            Assert.NotEqual(itemA.UniqueID, itemB.UniqueID);
            Assert.All(c.Info.QuestInventory, i => Assert.Null(i));
            Assert.True(Ready(a, Letter));
            Assert.True(Ready(b, Letter));

            // Nobody needs a second one: it is not taken.
            Assert.False(a.CheckGroupQuestItem(Envir.Main.CreateFreshItem(Item("QuestLetter"))));
        });
    }

    [Fact]
    public void Conditions_evaluate_the_tree_with_live_player_state()
    {
        InWorld(() =>
        {
            var c = Envir.Main.QuestConditionList;
            void Add(int index, QuestConditionOp op, int parent, int order, QuestConditionLeaf leaf = QuestConditionLeaf.None, int a1 = 0, int a2 = 0,
                QuestCompare cmp = QuestCompare.Eq) =>
                c.Add(new QuestConditionInfo { Index = index, Op = op, Parent = parent, Order = order, Leaf = leaf, Arg1 = a1, Arg2 = a2, Cmp = cmp });
            Add(9001, QuestConditionOp.And, 0, 0);
            Add(9002, QuestConditionOp.Leaf, 9001, 0, QuestConditionLeaf.Level, 5, 0, QuestCompare.Ge);
            Add(9003, QuestConditionOp.Or, 9001, 1);
            Add(9004, QuestConditionOp.Leaf, 9003, 0, QuestConditionLeaf.Class, (int)MirClass.Taoist);
            Add(9005, QuestConditionOp.Leaf, 9003, 1, QuestConditionLeaf.Class, (int)MirClass.Warrior);
            Add(9006, QuestConditionOp.Not, 0, 0);
            Add(9007, QuestConditionOp.Leaf, 9006, 0, QuestConditionLeaf.Gender, (int)MirGender.Female);
            Add(9008, QuestConditionOp.Leaf, 0, 0, QuestConditionLeaf.QuestStatus, Wolves, (int)QuestRunStatus.Completed);
            Add(9009, QuestConditionOp.Or, 0, 0); // empty Or: nobody
            Add(9010, QuestConditionOp.Leaf, 0, 0, QuestConditionLeaf.QuestStatus, Wolves, (int)QuestRunStatus.Active);
            Envir.Main.InvalidateQuestCatalog();

            var p = NewPlayer("Cond", level: 4);
            Assert.True(p.Quests.EvaluateCondition(0));
            Assert.False(p.Quests.EvaluateCondition(9001)); // level 4 < 5
            p.Info.Level = 5;
            Assert.True(p.Quests.EvaluateCondition(9001));
            Assert.True(p.Quests.EvaluateCondition(9006));
            Assert.False(NewPlayer("CondF", MirGender.Female).Quests.EvaluateCondition(9006));
            Assert.False(p.Quests.EvaluateCondition(9009));
            Assert.False(p.Quests.EvaluateCondition(123456)); // a missing node fails closed

            Assert.False(p.Quests.EvaluateCondition(9010));
            p.Quests.AdminStart(Wolves);
            Assert.True(p.Quests.EvaluateCondition(9010));
            Assert.False(p.Quests.EvaluateCondition(9008));
            p.Quests.AdminComplete(Wolves);
            Assert.True(p.Quests.EvaluateCondition(9008));
            Assert.False(p.Quests.EvaluateCondition(9010));
        });
    }

    [Fact]
    public void Migration_drops_removed_objectives_moves_off_removed_steps_and_clamps()
    {
        var def = new QuestDefinition { Revision = 3 };
        def.Steps.Add(new QuestStepInfo { Index = 20, Order = 2, TimeLimitSeconds = 60 });
        def.Steps.Add(new QuestStepInfo { Index = 10, Order = 1 });
        def.Objectives.Add(new QuestObjectiveInfo { Index = 100, Step = 10, Type = QuestObjectiveType.Kill, Count = 3 });

        var run = new QuestRunState { Quest = 7, Step = 99, Revision = 1 };
        var rows = new List<QuestObjectiveState>
        {
            new QuestObjectiveState { Quest = 7, Objective = 100, Value = 9 },  // clamped to 3
            new QuestObjectiveState { Quest = 7, Objective = 101, Value = 2 },  // removed objective: dropped
            new QuestObjectiveState { Quest = 8, Objective = 555, Value = 1 },  // another quest: untouched
        };
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(PlayerQuests.MigrateRecords(def, run, rows, now));
        Assert.Equal(10, run.Step); // first remaining step by Order
        Assert.Equal(0, run.StepExpiresAt);
        Assert.Equal(3, run.Revision);
        Assert.Equal(new[] { (7, 100, 3), (8, 555, 1) }, rows.Select(r => (r.Quest, r.Objective, r.Value)));

        Assert.False(PlayerQuests.MigrateRecords(def, run, rows, now)); // nothing left to change

        def.Steps.Clear();
        def.Revision = 4;
        Assert.True(PlayerQuests.MigrateRecords(def, run, rows, now));
        Assert.Equal(-1, run.Step); // no step left: turn-in pending
        Assert.DoesNotContain(rows, r => r.Quest == 7);
    }

    [Fact]
    public void Reset_clock_boundaries_and_repeat_policies()
    {
        var mon = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc); // a Monday
        Assert.Equal(DayOfWeek.Monday, mon.DayOfWeek);
        Assert.Equal(mon, ResetClock.LastDailyReset(mon, 0));
        Assert.Equal(mon, ResetClock.LastDailyReset(mon.AddHours(23.9), 0));
        Assert.Equal(mon.AddDays(-1).AddHours(6), ResetClock.LastDailyReset(mon.AddHours(5), 6));
        Assert.Equal(mon.AddHours(6), ResetClock.LastDailyReset(mon.AddHours(6), 6));
        Assert.Equal(mon, ResetClock.LastWeeklyReset(mon.AddDays(6).AddHours(23), 0, DayOfWeek.Monday));
        Assert.Equal(mon.AddDays(7), ResetClock.LastWeeklyReset(mon.AddDays(7), 0, DayOfWeek.Monday));
        Assert.Equal(mon.AddDays(-4), ResetClock.LastWeeklyReset(mon, 0, DayOfWeek.Thursday));

        InWorld(() =>
        {
            var p = NewPlayer("Repeat");
            var q = Runtime(Wolves);
            Assert.Equal(QuestRepeatPolicy.Daily, q.Def.RepeatPolicy);
            var now = Envir.Main.Now;
            Assert.True(p.Quests.RepeatAllows(q, now));
            p.Quests.AdminComplete(Wolves);
            Assert.False(p.Quests.RepeatAllows(q, now));
            Assert.True(p.Quests.RepeatAllows(q, ResetClock.NextDailyReset(now)));

            q.Def.RepeatPolicy = QuestRepeatPolicy.Cooldown;
            q.Def.CooldownSeconds = 3600;
            Assert.False(p.Quests.RepeatAllows(q, now.AddMinutes(59)));
            Assert.True(p.Quests.RepeatAllows(q, now.AddMinutes(61)));

            q.Def.RepeatPolicy = QuestRepeatPolicy.Unlimited;
            q.Def.MaxCompletions = 1;
            Assert.False(p.Quests.RepeatAllows(q, now)); // MaxCompletions caps repeatable policies

            Assert.True(p.Quests.RepeatAllows(Runtime(Letter), now)); // never completed: allowed
            p.Quests.AdminComplete(Letter);
            Assert.False(p.Quests.RepeatAllows(Runtime(Letter), now.AddYears(1))); // Once
        });
    }

    // ------------------------------------------------------------------ wave 2 review fixes

    /// <summary>A hand-built quest on Main: steps in order, each with the given objectives; rewards as given.</summary>
    private static QuestRuntime AddQuest(int index, (int Step, QuestObjectiveInfo[] Objectives)[] steps, params QuestRewardInfo[] rewards)
    {
        var info = new QuestInfo { Index = index, Name = "Q" + index };
        var d = info.Definition;
        d.RepeatPolicy = QuestRepeatPolicy.Unlimited;
        for (var i = 0; i < steps.Length; i++)
        {
            d.Steps.Add(new QuestStepInfo { Index = steps[i].Step, Order = i + 1 });
            foreach (var o in steps[i].Objectives) { o.Step = steps[i].Step; d.Objectives.Add(o); }
        }

        d.Rewards.AddRange(rewards);
        Envir.Main.QuestInfoList.Add(info);
        Envir.Main.InvalidateQuestCatalog();
        return Runtime(index);
    }

    private static QuestObjectiveInfo Counter(int index) => new QuestObjectiveInfo { Index = index, Type = QuestObjectiveType.Counter, Count = 1 };

    [Fact]
    public void Step_rewards_are_paid_once_per_cycle_even_after_abandon_and_retake()
    {
        InWorld(() =>
        {
            AddQuest(30, new[] { (300, new[] { Counter(3001) }), (301, new[] { Counter(3011) }) },
                new QuestRewardInfo { Scope = QuestRewardScope.Step, ScopeId = 300, Kind = QuestRewardKind.Gold, Amount = 5 });
            var p = NewPlayer("Farm");
            p.Account = new AccountInfo();

            Assert.True(p.Quests.AdminStart(30));
            Assert.True(p.Quests.ScriptCounter("30", "3001", "1")); // step 300 done → step reward
            Assert.Equal(301, p.Quests.GetRun(30).Step);
            Assert.Equal(5u, p.Account.Gold);

            p.Quests.Abandon(30);
            Assert.Single(p.Info.QuestRewardsPaid); // an abandon keeps the record
            Assert.True(p.Quests.AdminStart(30));
            p.Quests.ScriptCounter("30", "3001", "1");
            Assert.Equal(301, p.Quests.GetRun(30).Step);
            Assert.Equal(5u, p.Account.Gold); // not paid again

            p.Quests.ScriptCounter("30", "3011", "1");
            Assert.True(Ready(p, 30));
            p.Quests.AdminComplete(30); // completion starts a new cycle (TurnIn does the same)
            Assert.Empty(p.Info.QuestRewardsPaid);
            Assert.True(p.Quests.AdminStart(30));
            p.Quests.ScriptCounter("30", "3001", "1");
            Assert.Equal(10u, p.Account.Gold);
        });
    }

    [Fact]
    public void Reach_level_objectives_are_seeded_on_entering_the_step()
    {
        InWorld(() =>
        {
            AddQuest(31, new[] { (310, new[] { new QuestObjectiveInfo { Index = 3101, Type = QuestObjectiveType.ReachLevel, Count = 8 } }) });
            AddQuest(32, new[] { (320, new[] { new QuestObjectiveInfo { Index = 3201, Type = QuestObjectiveType.ReachLevel, Count = 12 } }) });
            var p = NewPlayer("Lvl", level: 10);
            Assert.True(p.Quests.AdminStart(31));
            Assert.True(Ready(p, 31)); // level 10 ≥ 8 already
            Assert.True(p.Quests.AdminStart(32));
            Assert.False(Ready(p, 32));
            Assert.Equal(10, p.Quests.ValueOf(Runtime(32), p.Quests.GetRun(32), Runtime(32).ObjectivesById[3201]));
            p.Info.Level = 12;
            p.Quests.LevelChanged();
            Assert.True(Ready(p, 32));
        });
    }

    [Fact]
    public void Deliver_items_of_a_non_final_step_are_taken_when_the_step_completes()
    {
        InWorld(() =>
        {
            var pelt = Item("Pelt");
            AddQuest(33, new[]
            {
                (330, new[] { new QuestObjectiveInfo { Index = 3301, Type = QuestObjectiveType.Deliver, TargetKind = QuestTargetKind.Item, TargetId = pelt.Index, Count = 2 } }),
                (331, new[] { Counter(3311) }),
            });
            var p = NewPlayer("Deliver");
            p.Info.Inventory[0] = new UserItem(pelt) { Count = 3, UniqueID = ++Envir.Main.NextUserItemID };

            Assert.True(p.Quests.AdminStart(33));
            Assert.Equal(331, p.Quests.GetRun(33).Step); // step 330 met at once and completed
            Assert.Equal(1, p.Info.Inventory[0].Count); // its 2 pelts taken, the third kept
            p.Quests.ScriptCounter("33", "3311", "1");
            Assert.True(Ready(p, 33));
            Assert.Equal(1, p.Info.Inventory[0].Count); // nothing more taken until turn-in
        });
    }

    [Fact]
    public void Reward_picks_resolve_against_the_definition_the_client_was_sent()
    {
        InWorld(() =>
        {
            var e = Envir.Main;
            e.QuestConditionList.Add(new QuestConditionInfo { Index = 9101, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 20, Cmp = QuestCompare.Ge });
            e.QuestConditionList.Add(new QuestConditionInfo { Index = 9102, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 10, Cmp = QuestCompare.Le });
            var sword = Item("WoodenSword").Index;
            var medal = Item("Medal").Index;
            var dress = Item("BaseDress(M)").Index;
            var q = AddQuest(34, new[] { (340, new[] { Counter(3401) }) },
                new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = sword, Amount = 1, ChoiceGroup = 1, Condition = 9101 }, // level ≥ 20 only
                new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = dress, Amount = 1, ChoiceGroup = 1, Condition = 9102 }, // level ≤ 10 only
                new QuestRewardInfo { Kind = QuestRewardKind.Item, TargetId = medal, Amount = 1, ChoiceGroup = 1 });

            var p = NewPlayer("Picks", level: 10);
            var sent = p.Quests.BuildDefinitionPacket(q);
            Assert.Equal(new[] { dress, medal }, sent.Rewards.Select(r => r.TargetId));

            // The player picked the medal (position 1 of what they saw), then reached level 20: the filter now lists the sword first.
            p.Info.Level = 20;
            Assert.Equal(new[] { (medal, 1L) }, p.Quests.PreviewRewards(q, new List<int> { 1 }).Items);
            // A picked reward whose condition no longer holds (the dress, level ≤ 10) gives nothing, never a neighbour.
            Assert.Empty(p.Quests.PreviewRewards(q, new List<int> { 0 }).Items);

            // After a re-send the positions follow the new list.
            sent = p.Quests.BuildDefinitionPacket(q);
            Assert.Equal(new[] { sword, medal }, sent.Rewards.Select(r => r.TargetId));
            Assert.Equal(new[] { (sword, 1L) }, p.Quests.PreviewRewards(q, new List<int> { 0 }).Items);
        });
    }

    /// <summary>Records what the engine sends (no connection in these tests).</summary>
    private sealed class RecordingPlayer : PlayerObject
    {
        public readonly List<Packet> Sent = new List<Packet>();
        public override void Enqueue(Packet p) => Sent.Add(p);

        public ServerPackets.QuestRefused LastRefusal() => Sent.OfType<ServerPackets.QuestRefused>().LastOrDefault();
    }

    private static RecordingPlayer NewRecordingPlayer(string name, int index = 1)
    {
        var p = new RecordingPlayer();
        p.Info = new CharacterInfo { Index = index, Name = name, Level = 10, Class = MirClass.Warrior, Gender = MirGender.Male };
        p.Report = new Reporting(p);
        p.Quests.Login();
        return p;
    }

    private static void AssertRefused(RecordingPlayer p, int quest, QuestRequestKind request, QuestRefusal reason)
    {
        var r = p.LastRefusal();
        Assert.NotNull(r);
        Assert.Equal(quest, r.Quest);
        Assert.Equal(request, r.Request);
        Assert.Equal(reason, r.Reason);
        p.Sent.Clear();
    }

    [Fact]
    public void Refused_requests_answer_with_QuestRefused_and_the_reason()
    {
        InWorld(() =>
        {
            var q = AddQuest(35, new[] { (350, new[] { Counter(3501) }) });
            q.Def.AbandonAllowed = false;
            var p = NewRecordingPlayer("Refused");

            p.Quests.Accept(999, 0);
            AssertRefused(p, 999, QuestRequestKind.Accept, QuestRefusal.UnknownQuest);
            p.Quests.Accept(35, 0); // no pending offer and no NPC
            AssertRefused(p, 35, QuestRequestKind.Accept, QuestRefusal.NotAvailable);
            p.Quests.Accept(35, 12345); // no such NPC in reach
            AssertRefused(p, 35, QuestRequestKind.Accept, QuestRefusal.NotAtNpc);

            p.Quests.TurnIn(35, 0, null);
            AssertRefused(p, 35, QuestRequestKind.TurnIn, QuestRefusal.NotAvailable);
            Assert.True(p.Quests.AdminStart(35));
            p.Sent.Clear();
            p.Quests.Accept(35, 0);
            AssertRefused(p, 35, QuestRequestKind.Accept, QuestRefusal.AlreadyActive);
            p.Quests.TurnIn(35, 0, null);
            AssertRefused(p, 35, QuestRequestKind.TurnIn, QuestRefusal.NotReady);
            p.Quests.Abandon(35);
            AssertRefused(p, 35, QuestRequestKind.Abandon, QuestRefusal.AbandonNotAllowed);
            p.Quests.Choose(35, 0, "");
            AssertRefused(p, 35, QuestRequestKind.Choose, QuestRefusal.InvalidPick);

            // Share: ShareMode None is refused before the group check; Offer without a group is NotInGroup.
            q.Def.ShareMode = QuestShareMode.None;
            p.Quests.Share(35);
            AssertRefused(p, 35, QuestRequestKind.Share, QuestRefusal.ShareNotAllowed);
            q.Def.ShareMode = QuestShareMode.Offer;
            p.Quests.Share(35);
            AssertRefused(p, 35, QuestRequestKind.Share, QuestRefusal.NotInGroup);

            // Ready but not at a finish NPC.
            p.Quests.ScriptCounter("35", "3501", "1");
            Assert.True(Ready(p, 35));
            p.Sent.Clear();
            p.Quests.TurnIn(35, 0, null);
            AssertRefused(p, 35, QuestRequestKind.TurnIn, QuestRefusal.NotAtNpc);
            Assert.True(Ready(p, 35));
        });
    }

    [Fact]
    public void Definition_carries_share_mode_and_group_members()
    {
        InWorld(() =>
        {
            var p = NewPlayer("Members");
            var sent = p.Quests.BuildDefinitionPacket(Runtime(Wolves));
            Assert.Equal(QuestShareMode.Offer, sent.ShareMode);
            var wolf = sent.Objectives.Single(o => o.TargetKind == QuestTargetKind.MonsterGroup && o.TargetMembers.Count == 2);
            Assert.Equal(new[] { Monster("Wolf").Index, Monster("Wolf2").Index }, wolf.TargetMembers.OrderBy(i => i));
            Assert.All(sent.Objectives.Where(o => o.TargetKind != QuestTargetKind.MonsterGroup && o.TargetKind != QuestTargetKind.ItemGroup), o => Assert.Empty(o.TargetMembers));
        });
    }
}
