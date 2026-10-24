using S = ServerPackets;

namespace StressClient.Tests;

/// <summary>The bots' V2 quest decisions: which offer to take, which monsters to hunt, which rewards to pick, when to give up.</summary>
public class QuestLogicTests
{
    private static S.QuestDefinition Def(int quest, int levelHint = 0, params S.QuestDefinition.Objective[] objectives)
    {
        var def = new S.QuestDefinition { Quest = quest, LevelHint = levelHint, AbandonAllowed = true };
        def.Steps.Add(new S.QuestDefinition.Step { Index = 1, Order = 1, NextStep = -1 });
        foreach (var o in objectives)
        {
            if (o.Step == 0) o.Step = 1;
            def.Objectives.Add(o);
        }
        return def;
    }

    private static S.QuestDefinition.Objective Kill(int index, int monster, int count = 1) =>
        new() { Index = index, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.Monster, TargetId = monster, Count = count };

    private static S.QuestDefinition.Objective KillGroup(int index, string label, int count = 1) =>
        new() { Index = index, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 900, TargetName = label, Count = count };

    private static S.QuestDefinition.Objective Of(int index, QuestObjectiveType type, QuestTargetKind kind, bool optional = false) =>
        new() { Index = index, Type = type, TargetKind = kind, TargetId = 1, Count = 1, Optional = optional };

    private static Dictionary<int, S.QuestDefinition> Defs(params S.QuestDefinition[] defs) => defs.ToDictionary(d => d.Quest);

    private static readonly Dictionary<int, QuestRun> NoRuns = new();
    private static readonly Dictionary<int, long> NoPending = new();

    [Fact]
    public void CanDo_KillCollectAndLevel_NotTalkOrFlag()
    {
        Assert.True(QuestLogic.CanDo(Kill(1, 5)));
        Assert.True(QuestLogic.CanDo(KillGroup(1, "Hen")));
        Assert.True(QuestLogic.CanDo(Of(1, QuestObjectiveType.Collect, QuestTargetKind.Item)));
        Assert.True(QuestLogic.CanDo(Of(1, QuestObjectiveType.ReachLevel, QuestTargetKind.None)));
        Assert.False(QuestLogic.CanDo(Of(1, QuestObjectiveType.Talk, QuestTargetKind.Npc)));
        Assert.False(QuestLogic.CanDo(Of(1, QuestObjectiveType.Flag, QuestTargetKind.Flag)));
        Assert.False(QuestLogic.CanDo(Of(1, QuestObjectiveType.Kill, QuestTargetKind.Npc)));
    }

    [Fact]
    public void IsDoable_HonoursOptionalAndAnySteps()
    {
        Assert.True(QuestLogic.IsDoable(Def(1, 0, Kill(1, 5), Of(2, QuestObjectiveType.Flag, QuestTargetKind.Flag, optional: true))));
        Assert.False(QuestLogic.IsDoable(Def(1, 0, Kill(1, 5), Of(2, QuestObjectiveType.Flag, QuestTargetKind.Flag))));

        var any = Def(2, 0, Kill(1, 5), Of(2, QuestObjectiveType.Talk, QuestTargetKind.Npc));
        any.Steps[0].CompletionMode = QuestStepCompletion.Any;
        Assert.True(QuestLogic.IsDoable(any));

        Assert.False(QuestLogic.IsDoable(new S.QuestDefinition { Quest = 3 })); // no steps
    }

    [Fact]
    public void ChooseOffer_SkipsUnknownUndoableTooHighAndTaken_PrefersLowestHint()
    {
        var defs = Defs(
            Def(10, 8, Kill(1, 5)),
            Def(11, 2, Kill(1, 5)),
            Def(12, 1, Of(1, QuestObjectiveType.Talk, QuestTargetKind.Npc)),  // undoable
            Def(13, 20, Kill(1, 5)));                                         // far above level 3
        var offered = new[] { 10, 11, 12, 13, 14 };                          // 14: no definition yet

        Assert.Equal(11, QuestLogic.ChooseOffer(offered, defs, NoRuns, NoPending, level: 3, maxActive: 3));

        var runs = new Dictionary<int, QuestRun> { [11] = new QuestRun { Quest = 11, Status = QuestRunStatus.Active } };
        Assert.Equal(10, QuestLogic.ChooseOffer(offered, defs, runs, NoPending, 3, 3));

        var pending = new Dictionary<int, long> { [10] = 0 };
        Assert.Equal(0, QuestLogic.ChooseOffer(offered, defs, runs, pending, 3, 3));
        Assert.Equal(0, QuestLogic.ChooseOffer(offered, defs, runs, NoPending, 3, maxActive: 1)); // at the cap
    }

    [Fact]
    public void KillTargets_CurrentStepUnmetKillsOnly_GroupsByLabel()
    {
        var def = Def(1, 0, Kill(1, 5, count: 2), KillGroup(2, "Hen"), Kill(3, 7));
        def.Steps.Add(new S.QuestDefinition.Step { Index = 2, Order = 2, NextStep = -1 });
        def.Objectives.Add(new S.QuestDefinition.Objective { Index = 4, Step = 2, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.Monster, TargetId = 8, Count = 1 });

        var run = new QuestRun { Quest = 1, Status = QuestRunStatus.Active, Step = 1 };
        run.Values[3] = 1; // objective 3 met
        var names = new Dictionary<int, string> { [5] = "Oma", [7] = "Deer", [8] = "Wolf", [20] = "Hen", [21] = "Hen" };

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = new HashSet<int>();
        QuestLogic.KillTargets(Defs(def), new[] { run }, names, found, indexes);

        Assert.Equal(new[] { "Hen", "Oma" }, found.OrderBy(n => n));
        Assert.Equal(new[] { 5, 20, 21 }, indexes.OrderBy(i => i));

        run.Status = QuestRunStatus.Ready; // ready runs need no more kills
        found.Clear();
        QuestLogic.KillTargets(Defs(def), new[] { run }, names, found, null);
        Assert.Empty(found);
    }

    [Fact]
    public void RewardPicks_OnePerChoiceGroup_ItemsAndLargerAmountsWin()
    {
        var def = Def(1);
        def.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 100 });            // 0 fixed
        def.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Gold, Amount = 999, ChoiceGroup = 1 }); // 1
        def.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Item, Amount = 1, ChoiceGroup = 1 });   // 2
        def.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Accept, Kind = QuestRewardKind.Item, Amount = 1, ChoiceGroup = 2 });  // 3 not Quest scope
        def.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Gold, Amount = 10, ChoiceGroup = 3 });  // 4
        def.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Gold, Amount = 50, ChoiceGroup = 3 });  // 5

        Assert.Equal(new[] { 2, 5 }, QuestLogic.RewardPicks(def));
        Assert.Empty(QuestLogic.RewardPicks(Def(2)));
        Assert.Empty(QuestLogic.RewardPicks(null));
    }

    [Fact]
    public void StuckRun_FailedAtOnce_ActiveAfterTimeout_NeverReadyOrForbidden()
    {
        var defs = Defs(Def(1, 0, Kill(1, 5)), Def(2, 0, Kill(1, 5)), Def(3, 0, Of(1, QuestObjectiveType.Talk, QuestTargetKind.Npc)));
        var fresh = new QuestRun { Quest = 1, Status = QuestRunStatus.Active, Step = 1, LastProgressAt = 1000 };
        Assert.Equal(0, QuestLogic.StuckRun(defs, new[] { fresh }, now: 2000, stuckMs: 5000));
        Assert.Equal(1, QuestLogic.StuckRun(defs, new[] { fresh }, now: 7000, stuckMs: 5000));

        var ready = new QuestRun { Quest = 1, Status = QuestRunStatus.Ready, Step = 1, LastProgressAt = 0 };
        Assert.Equal(0, QuestLogic.StuckRun(defs, new[] { ready }, 99_999, 5000));

        var failed = new QuestRun { Quest = 2, Status = QuestRunStatus.Failed, Step = 1, LastProgressAt = 1000 };
        Assert.Equal(2, QuestLogic.StuckRun(defs, new[] { failed }, 1000, 5000));
        failed.AbandonSent = true;
        Assert.Equal(0, QuestLogic.StuckRun(defs, new[] { failed }, 1000, 5000));

        var undoable = new QuestRun { Quest = 3, Status = QuestRunStatus.Active, Step = 1, LastProgressAt = 1000 };
        Assert.Equal(3, QuestLogic.StuckRun(defs, new[] { undoable }, 1000, 5000));
        defs[3].AbandonAllowed = false; // story quest: the server would refuse
        Assert.Equal(0, QuestLogic.StuckRun(defs, new[] { undoable }, 99_999, 5000));
    }

    [Fact]
    public void QuestBook_StatesProgressAndAvailability()
    {
        var world = new WorldModel { MapIndex = 4 };
        world.AddNpc(new NpcState { Id = 77, Name = "Trader", Location = new System.Drawing.Point(10, 12) });
        var book = world.Quests;
        book.ApplyDefinition(Def(1, 0, Kill(1, 5)));
        book.NoteMonsterInfo(new ClientMonsterInfo { Index = 5, Name = "Oma1", GameName = "Oma" });

        book.ApplyAvailability(new S.QuestAvailability { Npcs = { new S.QuestAvailability.Entry { ObjectID = 77, Offered = { 1 } } } }, world);
        Assert.Equal(1, book.ChooseOfferAt(77, level: 1));
        Assert.True(book.AnyOfferInView(1));
        Assert.Equal(new System.Drawing.Point(10, 12), book.FinishSpots[1].Location); // no finisher seen yet: the giver

        book.PendingAccepts[1] = 0;
        var accepted = new List<int>();
        book.ApplyStates(new S.QuestStates { States = { new S.QuestStates.Entry { Quest = 1, Status = QuestRunStatus.Active, Step = 1 } } }, 100, accepted, null, null);
        Assert.Equal(new[] { 1 }, accepted);
        Assert.True(book.IsTarget("oma"));
        Assert.Equal(new[] { 5 }, book.TargetMonsterIndexes);

        book.ApplyProgress(new S.QuestProgress { Progress = { new S.QuestProgress.Entry { Quest = 1, Objective = 1, Value = 1 } } }, 200);
        Assert.False(book.IsTarget("Oma"));
        Assert.Equal(200, book.Runs[1].LastProgressAt);

        book.ApplyStates(new S.QuestStates { States = { new S.QuestStates.Entry { Quest = 1, Status = QuestRunStatus.Ready, Step = 1 } } }, 300, null, null, null);
        book.ApplyAvailability(new S.QuestAvailability { Npcs = { new S.QuestAvailability.Entry { ObjectID = 77, Finishing = { 1 } } } }, world);
        Assert.True(book.AnyReady);
        Assert.Equal(1, book.ReadyAt(77));

        book.PendingTurnIns[1] = 300;
        Assert.Equal(0, book.ReadyAt(77));
        var done = new List<int>();
        book.ApplyStates(new S.QuestStates { States = { new S.QuestStates.Entry { Quest = 1, Status = QuestRunStatus.None } } }, 400, null, done, null);
        Assert.Equal(new[] { 1 }, done);
        Assert.Empty(book.Runs);

        world.Remove(77);
        Assert.Equal(0, book.ReadyAt(77));
    }

    [Fact]
    public void QuestBook_ExpirePending_AllowsRetry()
    {
        var book = new QuestBook();
        book.PendingAccepts[1] = 1000;
        book.PendingTurnIns[2] = 9000;
        book.ExpirePending(now: 10_000, afterMs: 5000);
        Assert.False(book.PendingAccepts.ContainsKey(1));
        Assert.True(book.PendingTurnIns.ContainsKey(2));
    }
}
