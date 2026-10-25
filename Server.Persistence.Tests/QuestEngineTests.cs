using System.Drawing;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.MirObjects;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Quest V2 engine (UserData-and-Quests-Plan.md, lane F): the differential test against the legacy oracle (QuestProgressInfo with
/// Owner = null for progress and completion, LegacyQuestRewards for the reward selection incl. the rate multipliers), plus unit tests
/// for conditions, the revision migration, repeat policies / ResetClock boundaries and the per-member party drop copies. Runs on
/// Envir.Main (the engine and the legacy classes read Main), so it restores Main's lists afterwards; no server, no randomness.
///
/// Expected differences from legacy, asserted as such:
/// 1. Gender rewards: legacy gives both the (M) and the (F) item of a pair; V2 gives the player's gender only (deliberate fix).
/// 2. Flag objectives: legacy completion stays once a flag task was seen set; V2 is Ready only while the flag is set (deliberate).
/// Party fan-out is compared by feeding each member's events, since CheckGroupQuestKill / CheckGroupQuestItem are replaced.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestEngineTests
{
    private const int Wolves = 1, Letter = 2;

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
        e.QuestInfoList.Add(new QuestInfo { Index = Wolves, Name = "Wolves", FileName = "Wolves", RequiredMaxLevel = ushort.MaxValue, Type = QuestType.Daily, TimeLimitInSeconds = 600 });
        e.QuestInfoList.Add(new QuestInfo { Index = Letter, Name = "Letter", FileName = "Letter", RequiredMaxLevel = ushort.MaxValue, RequiredQuest = Wolves, Type = QuestType.Story });
        e.MapIndex = 1; e.ItemIndex = items.Length; e.MonsterIndex = monsters.Length; e.NPCIndex = 1; e.QuestIndex = 2;

        var quests = Path.Combine("Envir", "Quests");
        var npcs = Path.Combine("Envir", "NPCs");
        Directory.CreateDirectory(quests);
        Directory.CreateDirectory(npcs);
        // Kill tasks in this order give non-overlapping groups: WolfKing = {WolfKing}, Wolf = {Wolf, Wolf2} (legacy credits the first
        // task whose name prefixes the monster's).
        File.WriteAllLines(Path.Combine(quests, "Wolves.txt"), new[]
        {
            "[@KILLTASKS]", "WolfKing 1", "Wolf 2",
            "[@ITEMTASKS]", "Pelt 2",
            "[@FLAGTASKS]", "12",
            "[@FIXEDREWARDS]", "BaseDress", "WoodenSword 2",
            "[@SELECTREWARDS]", "Medal 1",
            "[@EXPREWARD]", "100",
            "[@GOLDREWARD]", "50",
        });
        File.WriteAllLines(Path.Combine(quests, "Letter.txt"), new[] { "[@ITEMTASKS]", "QuestLetter 1", "[@CREDITREWARD]", "7" });
        File.WriteAllLines(Path.Combine(npcs, "Hunter.txt"), new[] { "[@MAIN]", "#SAY", "Hello", "[QUESTS]", "1", "-1", "2", "-2" });

        foreach (var q in e.QuestInfoList) q.LoadInfo();
        QuestLegacyImporter.Import(e);
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

    /// <summary>One event fed to both runtimes; returns (legacy complete, V2 ready).</summary>
    private sealed class Pair
    {
        public readonly QuestProgressInfo Legacy;
        public readonly PlayerObject Player;
        public readonly int Quest;
        public readonly UserItem[] LegacyBag = new UserItem[40];
        public readonly bool[] LegacyFlags = new bool[Globals.FlagIndexCount];

        public Pair(PlayerObject player, int quest)
        {
            Player = player;
            Quest = quest;
            Legacy = new QuestProgressInfo(quest); // Owner = null: the oracle never touches a player
            Assert.True(player.Quests.AdminStart(quest));
        }

        public void Kill(string monster, bool direct = true)
        {
            var m = Monster(monster);
            if (Legacy.NeedKill(m)) Legacy.ProcessKill(m);
            Player.Quests.MonsterKilled(m, direct);
        }

        public void Gain(string item, ushort count)
        {
            var info = Item(item);
            var legacyNeeds = Legacy.NeedItem(info);
            Assert.Equal(legacyNeeds, Player.Quests.NeedsQuestItem(info));
            if (!legacyNeeds) return;
            AddToBag(LegacyBag, info, count);
            Legacy.ProcessItem(LegacyBag);
            AddToBag(Player.Info.QuestInventory, info, count);
            Player.Quests.QuestBagChanged(info);
        }

        public void Flag(int flag, bool on)
        {
            LegacyFlags[flag] = on;
            Legacy.ProcessFlag(LegacyFlags);
            Player.Info.Flags[flag] = on;
            Player.Quests.FlagChanged(flag);
        }

        public void AssertSame(string step)
        {
            var legacy = Legacy.CheckCompleted();
            var v2 = Ready(Player, Quest);
            Assert.True(legacy == v2, step + ": legacy complete " + legacy + ", V2 ready " + v2);
        }
    }

    [Fact]
    public void Differential_progress_and_completion_match_the_legacy_oracle()
    {
        InWorld(() =>
        {
            var pair = new Pair(NewPlayer("Diff"), Wolves);
            pair.AssertSame("start");
            pair.Kill("Hen"); pair.AssertSame("unrelated kill");
            pair.Kill("Wolf"); pair.AssertSame("wolf");
            pair.Kill("Wolf2"); pair.AssertSame("wolf2 (same group as wolf)");
            pair.Kill("Wolf"); pair.AssertSame("wolf over the count");
            pair.Kill("WolfKing"); pair.AssertSame("wolfking (own group, prefix overlap with wolf)");
            pair.Gain("Pelt", 1); pair.AssertSame("one pelt");
            pair.Gain("Medal", 1); pair.AssertSame("an item no task needs");
            pair.Gain("Pelt", 1); pair.AssertSame("two pelts");
            Assert.False(Ready(pair.Player, Wolves));
            pair.Flag(12, true); pair.AssertSame("flag set");
            Assert.True(Ready(pair.Player, Wolves));

            // Expected difference 2: clearing the flag keeps legacy complete, V2 is no longer Ready (Flag objectives are read live).
            pair.Flag(12, false);
            Assert.True(pair.Legacy.CheckCompleted());
            Assert.False(Ready(pair.Player, Wolves));
            pair.Flag(12, true);
            Assert.True(Ready(pair.Player, Wolves));

            // The chained quest: the accept condition reads the V2 history (legacy RequiredQuest).
            var p = pair.Player;
            Assert.False(p.Quests.CanTake(Runtime(Letter)));
            p.Quests.AdminComplete(Wolves); // TurnIn gives exp and items, which need a spawned player: the scratch run covers it
            Assert.Null(p.Quests.GetRun(Wolves));
            Assert.Equal(1, p.Quests.TimesCompleted(Wolves));
            Assert.True(p.Quests.CanTake(Runtime(Letter)));

            var letter = new Pair(p, Letter);
            letter.AssertSame("letter start");
            letter.Gain("QuestLetter", 1); letter.AssertSame("letter collected");
            Assert.True(Ready(p, Letter));
        });
    }

    [Fact]
    public void Differential_rewards_match_the_legacy_selection_except_the_gender_fix()
    {
        InWorld(() =>
        {
            Settings.DropRate = 2F;
            Settings.ExpRate = 3F;
            var legacyInfo = Envir.Main.QuestInfoList.Single(q => q.Index == Wolves);
            var legacy = LegacyQuestRewards.Select(legacyInfo, 0);
            Assert.Equal(100u, legacy.Gold);
            Assert.Equal(300u, legacy.Exp);

            foreach (var gender in new[] { MirGender.Male, MirGender.Female })
            {
                var p = NewPlayer("Rw" + gender, gender);
                var q = Runtime(Wolves);
                var filtered = p.Quests.FilteredRewards(q);
                var medal = filtered.FindIndex(r => r.Kind == QuestRewardKind.Item && r.TargetId == Item("Medal").Index);
                Assert.True(medal >= 0);
                var preview = p.Quests.PreviewRewards(q, new List<int> { medal });

                // Expected difference 1: legacy gives both BaseDress items; V2 only the player's gender.
                var other = gender == MirGender.Male ? "BaseDress(F)" : "BaseDress(M)";
                var expected = legacy.Items.Where(x => x.Item.Name != other).Select(x => (x.Item.Index, (long)x.Count)).OrderBy(x => x).ToList();
                Assert.Contains(legacy.Items, x => x.Item.Name == other);
                Assert.Equal(expected, preview.Items.OrderBy(x => x).ToList());
                Assert.Equal(legacy.Gold, preview.Gold);
                Assert.Equal(legacy.Exp, preview.Exp);
                Assert.Equal(legacy.Credit, preview.Credit);

                // No pick: legacy without a selected item gives no select reward, as V2 with no pick for the group.
                var noPick = p.Quests.PreviewRewards(q, null);
                Assert.Equal(LegacyQuestRewards.Select(legacyInfo).Items.Count - 1, noPick.Items.Count);
            }

            var letterLegacy = LegacyQuestRewards.Select(Envir.Main.QuestInfoList.Single(q => q.Index == Letter));
            var lp = NewPlayer("RwLetter");
            Assert.Equal(letterLegacy.Credit, lp.Quests.PreviewRewards(Runtime(Letter), null).Credit);
        });
    }

    [Fact]
    public void Differential_party_kill_credit_by_feeding_each_member()
    {
        InWorld(() =>
        {
            var killer = new Pair(NewPlayer("Killer", index: 1), Wolves);
            var member = new Pair(NewPlayer("Member", index: 2), Wolves);
            foreach (var m in new[] { "Wolf", "WolfKing", "Wolf2" })
            {
                killer.Kill(m, direct: true);
                member.Kill(m, direct: false); // PartyCredit.Each: party members in range get the same credit
                killer.AssertSame("killer " + m);
                member.AssertSame("member " + m);
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

    [Fact]
    public void Importer_requires_the_previous_quest_completed_at_least_once()
    {
        InWorld(() =>
        {
            var root = Runtime(Letter).Def.AcceptCondition;
            var leaf = Envir.Main.QuestConditionList.Single(x => x.Index == root); // the only part: the leaf is the root
            Assert.Equal(QuestConditionLeaf.QuestCompletedCount, leaf.Leaf);
            Assert.Equal(Wolves, leaf.Arg1);
            Assert.Equal(1, leaf.Arg2);
            Assert.Equal(QuestCompare.Ge, leaf.Cmp);
        });
    }
    // ------------------------------------------------------------------ wave 2 review fixes

    /// <summary>A hand-built quest on Main: steps in order, each with the given objectives; rewards as given.</summary>
    private static QuestRuntime AddQuest(int index, (int Step, QuestObjectiveInfo[] Objectives)[] steps, params QuestRewardInfo[] rewards)
    {
        var info = new QuestInfo { Index = index, Name = "Q" + index, FileName = "none", RequiredMaxLevel = ushort.MaxValue };
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
}
