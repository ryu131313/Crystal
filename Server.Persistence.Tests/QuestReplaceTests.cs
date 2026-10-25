using System.Drawing;
using System.Runtime.CompilerServices;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.MirNetwork;
using Server.MirObjects;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// The wave 3 LiveDb quest-replace hook (UserData-and-Quests-Plan.md, Part B "Live edits"): <see cref="QuestReplace.Take"/> copies the
/// changed quests from an editor copy, <see cref="QuestReplace.Apply"/> swaps fresh graphs into Main (before-images, rollback), rebuilds the
/// catalog and refreshes online players (revision migration, sent definitions, states). Runs on Envir.Main as the engine does; the "editor
/// copy" is a detached Envir built from copies of Main's rows, edited per test.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestReplaceTests
{
    private const int Hunt = 40, Pack = 41;

    private static void BuildWorld()
    {
        ResetMain();
        var e = Envir.Main;
        e.QuestConditionList.Clear(); e.QuestTextList.Clear(); e.QuestGroupList.Clear(); e.QuestPoolList.Clear(); e.StorylineList.Clear();
        e.MapInfoList.Add(new MapInfo { Index = 1, FileName = "0", Title = "Bichon" });
        e.ItemInfoList.Add(new ItemInfo { Index = 1, Name = "Pelt", StackSize = 10 });
        e.MonsterInfoList.Add(new MonsterInfo { Index = 1, Name = "Wolf" });
        e.MonsterInfoList.Add(new MonsterInfo { Index = 2, Name = "WolfKing" });
        e.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Hunter", Name = "Hunter", Location = new Point(1, 1) });

        // Hunt: two steps of counters (editor quest: no legacy body).
        var hunt = new QuestInfo { Index = Hunt, Name = "Hunt" };
        var d = hunt.Definition;
        d.NameKey = "hunt.name";
        d.RepeatPolicy = QuestRepeatPolicy.Unlimited;
        d.Revision = 1;
        d.Steps.Add(new QuestStepInfo { Index = 400, Order = 1 });
        d.Steps.Add(new QuestStepInfo { Index = 401, Order = 2 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 4001, Step = 400, Order = 1, Type = QuestObjectiveType.Counter, Count = 5 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 4002, Step = 400, Order = 2, Type = QuestObjectiveType.Counter, Count = 3 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 4011, Step = 401, Order = 1, Type = QuestObjectiveType.Counter, Count = 1 });
        d.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        e.QuestInfoList.Add(hunt);

        // Pack: kill any of the "wolves" group (shared row), with a level condition it owns.
        var pack = new QuestInfo { Index = Pack, Name = "Pack" };
        var p = pack.Definition;
        p.NameKey = "pack.name";
        p.RepeatPolicy = QuestRepeatPolicy.Unlimited;
        p.Revision = 1;
        p.AcceptCondition = 900;
        p.Steps.Add(new QuestStepInfo { Index = 410, Order = 1 });
        p.Objectives.Add(new QuestObjectiveInfo { Index = 4101, Step = 410, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 1, Count = 3 });
        e.QuestInfoList.Add(pack);
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 900, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 1, Cmp = QuestCompare.Ge, OwnerQuest = Pack });

        var wolves = new QuestGroupInfo { Index = 1, AuthorKey = "wolves", Kind = QuestGroupKind.Monster };
        wolves.Members.Add(new QuestGroupMemberInfo { TargetId = 1 });
        e.QuestGroupList.Add(wolves);
        e.QuestTextList.Add(new QuestTextInfo { Index = 1, TextKey = "hunt.name", Body = "Hunt" });
        e.QuestTextList.Add(new QuestTextInfo { Index = 2, TextKey = "pack.name", Body = "Pack" });
        e.InvalidateQuestCatalog();
    }

    private static void InWorld(Action body)
    {
        InFolder(NewTempFolder("quest-replace"), () =>
        {
            try
            {
                BuildWorld();
                body();
            }
            finally
            {
                var e = Envir.Main;
                e.QuestConditionList.Clear(); e.QuestTextList.Clear(); e.QuestGroupList.Clear(); e.StorylineList.Clear(); e.QuestPoolList.Clear();
                ResetMain();
                e.InvalidateQuestCatalog();
            }

            return 0;
        });
    }

    /// <summary>A detached "editor copy": copies of Main's quest rows (as Envir.Edit holds its own objects); edit it, then Take from it.</summary>
    private static Envir EditCopy()
    {
        var main = Envir.Main;
        var e = new Envir();
        foreach (var q in main.QuestInfoList)
            e.QuestInfoList.Add(new QuestInfo { Index = q.Index, Name = q.Name, Group = q.Group, Definition = q.Definition.DeepCopy() });
        foreach (var c in main.QuestConditionList) e.QuestConditionList.Add(c.Copy());
        foreach (var t in main.QuestTextList) e.QuestTextList.Add(t.Copy());
        foreach (var g in main.QuestGroupList) e.QuestGroupList.Add(g.Copy());
        return e;
    }

    private static QuestDefinition Def(Envir e, int quest) => e.QuestInfoList.Single(q => q.Index == quest).Definition;

    /// <summary>Records what the engine sends; holds a connection object only for its sent sets (no socket).</summary>
    private sealed class RecordingPlayer : PlayerObject
    {
        public readonly List<Packet> Sent = new List<Packet>();
        public override void Enqueue(Packet p) => Sent.Add(p);
        public IEnumerable<T> Of<T>() where T : Packet => Sent.OfType<T>();
    }

    /// <summary>A MirConnection without a socket: its collection fields are created, nothing else (the engine only reads the sent sets).</summary>
    private static MirConnection FakeConnection()
    {
        var c = (MirConnection)RuntimeHelpers.GetUninitializedObject(typeof(MirConnection));
        foreach (var f in typeof(MirConnection).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            if (f.IsInitOnly || f.FieldType.IsValueType || f.FieldType.IsAbstract || f.FieldType.IsInterface || f.GetValue(c) != null) continue;
            if (!f.FieldType.IsGenericType) continue;
            var def = f.FieldType.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(Dictionary<,>)) f.SetValue(c, Activator.CreateInstance(f.FieldType));
        }

        return c;
    }

    private static RecordingPlayer NewPlayer(string name, int index = 1, bool connection = true)
    {
        var p = new RecordingPlayer();
        p.Info = new CharacterInfo { Index = index, Name = name, Level = 10, Class = MirClass.Warrior, Gender = MirGender.Male };
        p.Report = new Reporting(p);
        if (connection) p.Connection = FakeConnection();
        p.Quests.Login();
        return p;
    }

    private static QuestReplaceResult Replace(Envir edit, IReadOnlyCollection<int> quests, params PlayerObject[] players) =>
        QuestReplace.Apply(Envir.Main, QuestReplace.Take(edit, quests, "test"), players);

    [Fact]
    public void A_raised_count_takes_a_Ready_run_back_to_Active_until_the_next_kill()
    {
        InWorld(() =>
        {
            // Pack needs 1 wolf to start with; the player is Ready at 1/1.
            Def(Envir.Main, Pack).Objectives[0].Count = 1;
            Envir.Main.InvalidateQuestCatalog();
            var wolf = Envir.Main.MonsterInfoList.Single(m => m.Name == "Wolf");
            var p = NewPlayer("Raise");
            Assert.True(p.Quests.AdminStart(Pack));
            p.Quests.MonsterKilled(wolf, true);
            Assert.Equal(QuestRunStatus.Ready, p.Quests.StatusOf(p.Quests.GetRun(Pack)));

            // The Quests page raises the count to 2 (a new revision): the online run migrates, keeps its 1 and is Active again.
            var edit = EditCopy();
            Def(edit, Pack).Objectives[0].Count = 2;
            Def(edit, Pack).Revision = 2;
            p.Sent.Clear();
            var result = Replace(edit, new[] { Pack }, p);
            Assert.Empty(result.Failed);
            var q = Envir.Main.QuestCatalog.Get(Pack);
            var run = p.Quests.GetRun(Pack);
            Assert.Equal(2, run.Revision);
            Assert.Equal(QuestRunStatus.Active, p.Quests.StatusOf(run));
            Assert.Equal(1, p.Quests.ValueOf(q, run, q.ObjectivesById[4101]));
            p.Quests.Process(); // states go out coalesced
            Assert.Contains(p.Of<ServerPackets.QuestStates>().SelectMany(x => x.States), x => x.Quest == Pack && x.Status == QuestRunStatus.Active);

            // A second kill makes it Ready (2/2), and it completes into the history (AdminComplete: TurnIn needs a spawned player at an NPC,
            // which the scratch run covers).
            p.Quests.MonsterKilled(wolf, true);
            Assert.Equal(QuestRunStatus.Ready, p.Quests.StatusOf(p.Quests.GetRun(Pack)));
            Assert.Equal(2, p.Quests.ValueOf(q, p.Quests.GetRun(Pack), q.ObjectivesById[4101]));
            p.Quests.AdminComplete(Pack);
            Assert.Null(p.Quests.GetRun(Pack));
            Assert.Equal(1, p.Quests.TimesCompleted(Pack));
        });
    }

    [Fact]
    public void A_replace_changes_mains_definition_and_migrates_an_online_run()
    {
        InWorld(() =>
        {
            var p = NewPlayer("Hunter1");
            Assert.True(p.Quests.AdminStart(Hunt));
            p.Quests.ScriptCounter("40", "4001", "4");
            p.Quests.ScriptCounter("40", "4002", "2");
            Assert.Contains(Hunt, p.Connection.SentQuestDefinitions);
            var mainInfo = Envir.Main.QuestInfoList.Single(q => q.Index == Hunt);
            var oldCatalog = Envir.Main.QuestCatalog;

            // The edit: objective 4001's count lowered to 2, objective 4002 removed, renamed; the shape changed, so the revision went up.
            var edit = EditCopy();
            var d = Def(edit, Hunt);
            d.Objectives.Single(o => o.Index == 4001).Count = 2;
            d.Objectives.RemoveAll(o => o.Index == 4002);
            d.Revision = 2;
            edit.QuestTextList.Single(t => t.TextKey == "hunt.name").Body = "Wolf hunt";
            p.Sent.Clear();

            var result = Replace(edit, new[] { Hunt }, p);

            Assert.Equal(new[] { Hunt }, result.Replaced);
            Assert.Empty(result.Failed);
            Assert.False(result.RolledBack);
            Assert.Equal(1, result.Players);

            // Main: the same QuestInfo row with a fresh graph, sharing no row with the editor copy.
            Assert.Same(mainInfo, Envir.Main.QuestInfoList.Single(q => q.Index == Hunt));
            Assert.NotSame(d, mainInfo.Definition);
            Assert.NotSame(d.Objectives[0], mainInfo.Definition.Objectives[0]);
            Assert.Equal(2, mainInfo.Definition.Objectives.Single(o => o.Index == 4001).Count);
            Assert.DoesNotContain(mainInfo.Definition.Objectives, o => o.Index == 4002);
            Assert.NotSame(oldCatalog, Envir.Main.QuestCatalog);
            Assert.Equal("Wolf hunt", Envir.Main.QuestCatalog.Text("hunt.name"));

            // The run: 4002's row dropped, 4001 clamped to 2, which completes step 400 (step completion re-run), revision set.
            var run = p.Quests.GetRun(Hunt);
            Assert.Equal(2, run.Revision);
            Assert.Equal(401, run.Step);
            Assert.DoesNotContain(p.Info.QuestObjectives, o => o.Objective == 4002);

            // The client: the new definition at once, the migrated state with the next flush.
            var def = Assert.Single(p.Of<ServerPackets.QuestDefinition>());
            Assert.Equal(Hunt, def.Quest);
            Assert.DoesNotContain(def.Objectives, o => o.Index == 4002);
            p.Quests.Process();
            Assert.Contains(p.Of<ServerPackets.QuestStates>().SelectMany(s => s.States), s => s.Quest == Hunt && s.Step == 401);
        });
    }

    [Fact]
    public void A_removed_step_moves_the_run_to_the_first_remaining_step()
    {
        InWorld(() =>
        {
            var p = NewPlayer("Hunter2");
            Assert.True(p.Quests.AdminStart(Hunt));
            Assert.True(p.Quests.AdminSetStep(Hunt, 401));

            var edit = EditCopy();
            var d = Def(edit, Hunt);
            d.Steps.RemoveAll(s => s.Index == 401);
            d.Objectives.RemoveAll(o => o.Step == 401);
            d.Revision = 2;
            Replace(edit, new[] { Hunt }, p);

            Assert.Equal(400, p.Quests.GetRun(Hunt).Step);
            Assert.Equal(2, p.Quests.GetRun(Hunt).Revision);
        });
    }

    [Fact]
    public void A_missing_reference_keeps_the_old_graph_and_an_exception_rolls_the_batch_back()
    {
        InWorld(() =>
        {
            var main = Envir.Main;
            var hunt = main.QuestInfoList.Single(q => q.Index == Hunt);
            var pack = main.QuestInfoList.Single(q => q.Index == Pack);
            var huntDef = hunt.Definition;
            var packDef = pack.Definition;

            // Pack names a monster the running world does not have: Pack keeps its graph, Hunt is replaced.
            var edit = EditCopy();
            Def(edit, Hunt).LevelHint = 9;
            var o = Def(edit, Pack).Objectives[0];
            o.TargetKind = QuestTargetKind.Monster;
            o.TargetId = 999;
            var result = Replace(edit, new[] { Hunt, Pack });
            Assert.Equal(new[] { Hunt }, result.Replaced);
            var failed = Assert.Single(result.Failed);
            Assert.Equal(Pack, failed.Quest);
            Assert.Contains("monster 999", failed.Reason);
            Assert.Same(packDef, pack.Definition);
            Assert.Equal(9, hunt.Definition.LevelHint);
            Assert.Contains(main.QuestConditionList, c => c.Index == 900); // the kept quest's owned condition stays

            // An exception during the swap restores every list and row of the batch.
            huntDef = hunt.Definition;
            var conditions = main.QuestConditionList.ToList();
            var texts = main.QuestTextList.ToList();
            var groups = main.QuestGroupList.ToList();
            var quests = main.QuestInfoList.ToList();
            var edit2 = EditCopy();
            Def(edit2, Hunt).LevelHint = 33;
            Def(edit2, Pack).LevelHint = 33;
            edit2.QuestTextList.Add(new QuestTextInfo { Index = 9, TextKey = "new.text", Body = "x" });
            var rolled = QuestReplace.Apply(main, QuestReplace.Take(edit2, new[] { Hunt, Pack }, "test"), Array.Empty<PlayerObject>(),
                () => throw new InvalidOperationException("boom"));

            Assert.True(rolled.RolledBack);
            Assert.Empty(rolled.Replaced);
            Assert.Equal(2, rolled.Failed.Count);
            Assert.Same(huntDef, hunt.Definition);
            Assert.Same(packDef, pack.Definition);
            Assert.Equal(quests, main.QuestInfoList);
            Assert.Equal(conditions, main.QuestConditionList);
            Assert.Equal(texts, main.QuestTextList);
            Assert.Equal(groups, main.QuestGroupList);
            Assert.NotNull(main.QuestCatalog.Get(Hunt)); // the catalog rebuilds from the restored lists
        });
    }

    [Fact]
    public void A_deleted_quest_drops_online_runs_and_keeps_history()
    {
        InWorld(() =>
        {
            var p = NewPlayer("Hunter3");
            Assert.True(p.Quests.AdminComplete(Hunt));
            Assert.True(p.Quests.AdminStart(Hunt)); // repeatable: a run and a history row
            p.Quests.Process();
            p.Sent.Clear();

            var edit = EditCopy();
            edit.QuestInfoList.RemoveAll(q => q.Index == Hunt);
            var result = Replace(edit, new[] { Hunt }, p);

            Assert.Equal(new[] { Hunt }, result.Deleted);
            Assert.DoesNotContain(Envir.Main.QuestInfoList, q => q.Index == Hunt);
            Assert.Null(Envir.Main.QuestCatalog.Get(Hunt));
            Assert.Null(p.Quests.GetRun(Hunt));
            Assert.Equal(1, p.Quests.TimesCompleted(Hunt)); // history kept
            p.Quests.Process();
            Assert.Contains(p.Of<ServerPackets.QuestStates>().SelectMany(s => s.States), s => s.Quest == Hunt && s.Status == QuestRunStatus.None);
        });
    }

    [Fact]
    public void A_shared_group_edit_replaces_the_quests_that_use_it()
    {
        InWorld(() =>
        {
            var p = NewPlayer("Hunter4");
            Assert.True(p.Quests.AdminStart(Pack));
            var wolfKing = Envir.Main.MonsterInfoList.Single(m => m.Name == "WolfKing");
            p.Quests.MonsterKilled(wolfKing, true);
            Assert.Equal(0, p.Quests.ValueOf(Envir.Main.QuestCatalog.Get(Pack), p.Quests.GetRun(Pack), Envir.Main.QuestCatalog.Get(Pack).ObjectivesById[4101]));

            // The group gains WolfKing. No quest index is passed: the reverse index finds Pack through the changed group.
            var edit = EditCopy();
            edit.QuestGroupList.Single(g => g.Index == 1).Members.Add(new QuestGroupMemberInfo { TargetId = wolfKing.Index });
            var result = Replace(edit, Array.Empty<int>(), p);

            Assert.Equal(new[] { Pack }, result.Affected);
            Assert.Equal(2, Envir.Main.QuestGroupList.Single(g => g.Index == 1).Members.Count);
            p.Quests.MonsterKilled(wolfKing, true); // the player's index was rebuilt over the new group
            var q = Envir.Main.QuestCatalog.Get(Pack);
            Assert.Equal(1, p.Quests.ValueOf(q, p.Quests.GetRun(Pack), q.ObjectivesById[4101]));
        });
    }

    [Fact]
    public void The_sent_set_is_cleared_and_the_definition_resent_only_where_it_was_held()
    {
        InWorld(() =>
        {
            var holder = NewPlayer("Holder", 1);
            var other = NewPlayer("Other", 2);
            holder.Quests.EnsureDefinition(Envir.Main.QuestCatalog.Get(Pack)); // seen at a giver, no run
            Assert.Contains(Pack, holder.Connection.SentQuestDefinitions);
            holder.Sent.Clear();
            other.Sent.Clear();

            var edit = EditCopy();
            edit.QuestTextList.Single(t => t.TextKey == "pack.name").Body = "Wolf pack";
            var result = Replace(edit, new[] { Pack }, holder, other);

            Assert.Equal(new[] { Pack }, result.Replaced);
            Assert.Single(holder.Of<ServerPackets.QuestDefinition>(), d => d.Quest == Pack);
            Assert.Contains(Pack, holder.Connection.SentQuestDefinitions);
            Assert.Empty(other.Of<ServerPackets.QuestDefinition>());
            Assert.DoesNotContain(Pack, other.Connection.SentQuestDefinitions);
        });
    }

    [Fact]
    public void A_definition_over_the_packet_limit_is_never_sent()
    {
        InWorld(() =>
        {
            // Review S5: a ushort packet length would wrap; the engine logs and skips instead.
            Envir.Main.QuestTextList.Single(t => t.TextKey == "pack.name").Body = new string('x', 70000);
            Envir.Main.InvalidateQuestCatalog();
            var p = NewPlayer("Big");
            p.Sent.Clear();
            p.Quests.EnsureDefinition(Envir.Main.QuestCatalog.Get(Pack));
            Assert.Empty(p.Of<ServerPackets.QuestDefinition>());

            p.Quests.EnsureDefinition(Envir.Main.QuestCatalog.Get(Hunt)); // a normal one still goes out
            Assert.Single(p.Of<ServerPackets.QuestDefinition>(), d => d.Quest == Hunt);
        });
    }
}
