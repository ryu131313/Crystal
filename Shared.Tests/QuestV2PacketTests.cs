using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Quest V2 wire contract (wave 2 kick-off, Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md Part B "Wire protocol"):
/// ids appended at the end, factory cases, private, every field round-trips byte-identically, and the changed packets
/// (S.ObjectNPC without QuestIDs). Pins the layouts WebClient/src/protocol decodes.
/// </summary>
public class QuestV2PacketTests
{
    [Fact]
    public void Ids_are_appended_at_the_end_and_known_to_the_factory()
    {
        Assert.Equal(302, (int)ServerPacketIds.DungeonMap);
        Assert.Equal(303, (int)ServerPacketIds.QuestDefinition);
        Assert.Equal(304, (int)ServerPacketIds.QuestStates);
        Assert.Equal(305, (int)ServerPacketIds.QuestProgress);
        Assert.Equal(306, (int)ServerPacketIds.QuestHistory);
        Assert.Equal(307, (int)ServerPacketIds.QuestAvailability);
        Assert.Equal(308, (int)ServerPacketIds.QuestOffer);
        Assert.Equal(309, (int)ServerPacketIds.QuestRefused);
        Assert.Equal(172, (int)ClientPacketIds.SetAllowToggle);
        Assert.Equal(173, (int)ClientPacketIds.QuestAccept);
        Assert.Equal(174, (int)ClientPacketIds.QuestTurnIn);
        Assert.Equal(175, (int)ClientPacketIds.QuestAbandon);
        Assert.Equal(176, (int)ClientPacketIds.QuestShare);
        Assert.Equal(177, (int)ClientPacketIds.QuestChoose);
        Assert.Equal(178, (int)ClientPacketIds.QuestDefinitionRequest);

        Assert.IsType<ServerPackets.QuestDefinition>(GetServerPacket((short)ServerPacketIds.QuestDefinition));
        Assert.IsType<ServerPackets.QuestStates>(GetServerPacket((short)ServerPacketIds.QuestStates));
        Assert.IsType<ServerPackets.QuestProgress>(GetServerPacket((short)ServerPacketIds.QuestProgress));
        Assert.IsType<ServerPackets.QuestHistory>(GetServerPacket((short)ServerPacketIds.QuestHistory));
        Assert.IsType<ServerPackets.QuestAvailability>(GetServerPacket((short)ServerPacketIds.QuestAvailability));
        Assert.IsType<ServerPackets.QuestOffer>(GetServerPacket((short)ServerPacketIds.QuestOffer));
        Assert.IsType<ServerPackets.QuestRefused>(GetServerPacket((short)ServerPacketIds.QuestRefused));
        Assert.IsType<ClientPackets.QuestAccept>(GetClientPacket((short)ClientPacketIds.QuestAccept));
        Assert.IsType<ClientPackets.QuestTurnIn>(GetClientPacket((short)ClientPacketIds.QuestTurnIn));
        Assert.IsType<ClientPackets.QuestAbandon>(GetClientPacket((short)ClientPacketIds.QuestAbandon));
        Assert.IsType<ClientPackets.QuestShare>(GetClientPacket((short)ClientPacketIds.QuestShare));
        Assert.IsType<ClientPackets.QuestChoose>(GetClientPacket((short)ClientPacketIds.QuestChoose));
        Assert.IsType<ClientPackets.QuestDefinitionRequest>(GetClientPacket((short)ClientPacketIds.QuestDefinitionRequest));
    }

    [Fact]
    public void QuestRunStatus_values_are_fixed()
    {
        Assert.Equal(0, (byte)QuestRunStatus.None);
        Assert.Equal(1, (byte)QuestRunStatus.Active);
        Assert.Equal(2, (byte)QuestRunStatus.Ready);
        Assert.Equal(3, (byte)QuestRunStatus.Failed);
        Assert.Equal(4, (byte)QuestRunStatus.Completed);
        Assert.Equal(5, Enum.GetValues<QuestRunStatus>().Length);
    }

    [Fact]
    public void QuestRequestKind_and_QuestRefusal_values_are_fixed()
    {
        Assert.Equal(0, (byte)QuestRequestKind.Accept);
        Assert.Equal(1, (byte)QuestRequestKind.TurnIn);
        Assert.Equal(2, (byte)QuestRequestKind.Abandon);
        Assert.Equal(3, (byte)QuestRequestKind.Share);
        Assert.Equal(4, (byte)QuestRequestKind.Choose);
        Assert.Equal(0, (byte)QuestRefusal.NotAvailable);
        Assert.Equal(4, (byte)QuestRefusal.NotReady);
        Assert.Equal(8, (byte)QuestRefusal.ShareNotAllowed);
        Assert.Equal(12, (byte)QuestRefusal.Other);
        Assert.Equal(16, (byte)QuestRefusal.NoMemberCouldTake);
        Assert.Equal(17, Enum.GetValues<QuestRefusal>().Length);
    }

    [Fact]
    public void Every_server_packet_is_private()
    {
        Assert.False(new ServerPackets.QuestDefinition().Observable);
        Assert.False(new ServerPackets.QuestStates().Observable);
        Assert.False(new ServerPackets.QuestProgress().Observable);
        Assert.False(new ServerPackets.QuestHistory().Observable);
        Assert.False(new ServerPackets.QuestAvailability().Observable);
        Assert.False(new ServerPackets.QuestOffer().Observable);
        Assert.False(new ServerPackets.QuestRefused().Observable);
    }

    private static void RoundTrip(Packet sent, Packet fresh)
    {
        var bytes = Write(sent);
        Assert.Equal(bytes.Length, Read(fresh, bytes));
        Assert.Equal(bytes, Write(fresh));
    }

    public static ServerPackets.QuestDefinition SampleDefinition() => new ServerPackets.QuestDefinition
    {
        Quest = 42, Name = "Hen Trouble", Summary = "Thin the hens.", CompleteText = "Thanks!", Category = QuestCategory.Side,
        StorylineIndex = 3, LevelHint = 7, RepeatPolicy = QuestRepeatPolicy.Daily, CooldownSeconds = 60, TimeLimitSeconds = 3600,
        FailPolicy = QuestFailPolicy.Fail, AbandonAllowed = true, Flags = QuestFlags.AutoComplete | QuestFlags.HiddenInLog, GroupName = "Bichon",
        ShareMode = QuestShareMode.AutoParty,
        Steps =
        {
            new ServerPackets.QuestDefinition.Step { Index = 1, Order = 1, Title = "Hunt", Text = "Kill hens", ReadyText = "Done", CompletionMode = QuestStepCompletion.NOf, NRequired = 2, NextStep = -1, TimeLimitSeconds = 600 },
        },
        Objectives =
        {
            new ServerPackets.QuestDefinition.Objective { Index = 5, Step = 1, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 9, TargetName = "Hens", Count = 10, Map = 1, X = 300, Y = 400, Radius = 12, Optional = true, Hidden = false, Text = "", TargetMembers = { 3, 4_000_000 } },
            new ServerPackets.QuestDefinition.Objective { Index = 6, Step = 1, Order = 2, Type = QuestObjectiveType.Flag, TargetKind = QuestTargetKind.Flag, TargetId = 521, TargetName = "Seal", Count = 1, Hidden = true, Text = "Break the seal" },
        },
        Rewards =
        {
            new ServerPackets.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, ScopeId = 0, Kind = QuestRewardKind.Exp, TargetId = 0, Amount = 5_000_000_000L, ChoiceGroup = 0, FirstTimeOnly = true },
            new ServerPackets.QuestDefinition.Reward { Scope = QuestRewardScope.Step, ScopeId = 1, Kind = QuestRewardKind.Item, TargetId = 77, Amount = 2, ChoiceGroup = 1 },
        },
        Endpoints =
        {
            new ServerPackets.QuestDefinition.Endpoint { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 33, Name = "Guard", Map = 1 },
            new ServerPackets.QuestDefinition.Endpoint { Role = QuestEndpointRole.Finish, Kind = QuestEndpointKind.ItemUse, TargetId = 12, Name = "", Map = 0 },
        },
        Branches =
        {
            new ServerPackets.QuestDefinition.Branch { Step = 1, ChoiceKey = "spare", Text = "Spare them", Outcome = 2 },
        },
    };

    [Fact]
    public void QuestDefinition_round_trips_every_field()
    {
        var sent = SampleDefinition();
        var read = new ServerPackets.QuestDefinition();
        RoundTrip(sent, read);

        Assert.Equal(42, read.Quest);
        Assert.Equal("Hen Trouble", read.Name);
        Assert.Equal(QuestCategory.Side, read.Category);
        Assert.Equal(QuestRepeatPolicy.Daily, read.RepeatPolicy);
        Assert.Equal(QuestFailPolicy.Fail, read.FailPolicy);
        Assert.Equal(QuestFlags.AutoComplete | QuestFlags.HiddenInLog, read.Flags);
        Assert.Equal("Bichon", read.GroupName);
        Assert.Equal(QuestShareMode.AutoParty, read.ShareMode);
        Assert.Equal(QuestStepCompletion.NOf, read.Steps.Single().CompletionMode);
        Assert.Equal(-1, read.Steps.Single().NextStep);
        Assert.Equal(2, read.Objectives.Count);
        Assert.Equal(QuestTargetKind.MonsterGroup, read.Objectives[0].TargetKind);
        Assert.Equal(12, read.Objectives[0].Radius);
        Assert.Equal(new[] { 3, 4_000_000 }, read.Objectives[0].TargetMembers);
        Assert.Empty(read.Objectives[1].TargetMembers);
        Assert.True(read.Objectives[0].Optional);
        Assert.True(read.Objectives[1].Hidden);
        Assert.Equal("Break the seal", read.Objectives[1].Text);
        Assert.Equal(5_000_000_000L, read.Rewards[0].Amount);
        Assert.True(read.Rewards[0].FirstTimeOnly);
        Assert.Equal(QuestRewardKind.Item, read.Rewards[1].Kind);
        Assert.Equal(1, read.Rewards[1].ChoiceGroup);
        Assert.Equal(QuestEndpointKind.ItemUse, read.Endpoints[1].Kind);
        Assert.Equal("Guard", read.Endpoints[0].Name);
        Assert.Equal("spare", read.Branches.Single().ChoiceKey);
        Assert.Equal(2, read.Branches.Single().Outcome);
    }

    [Fact]
    public void Empty_QuestDefinition_writes_empty_strings_and_lists()
    {
        var read = new ServerPackets.QuestDefinition();
        RoundTrip(new ServerPackets.QuestDefinition { Name = null, Summary = null, CompleteText = null, GroupName = null }, read);
        Assert.Equal(string.Empty, read.Name);
        Assert.Empty(read.Steps);
        Assert.Empty(read.Branches);
    }

    [Fact]
    public void QuestStates_Progress_History_round_trip()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc).ToBinary();
        var states = new ServerPackets.QuestStates
        {
            Full = true,
            States =
            {
                new ServerPackets.QuestStates.Entry { Quest = 1, Status = QuestRunStatus.Ready, Step = -1, ExpiresAt = now, StepExpiresAt = 0, TrackHint = true },
                new ServerPackets.QuestStates.Entry { Quest = 2, Status = QuestRunStatus.None },
            },
        };
        var s2 = new ServerPackets.QuestStates();
        RoundTrip(states, s2);
        Assert.True(s2.Full);
        Assert.Equal(QuestRunStatus.Ready, s2.States[0].Status);
        Assert.Equal(now, s2.States[0].ExpiresAt);
        Assert.True(s2.States[0].TrackHint);
        Assert.Equal(QuestRunStatus.None, s2.States[1].Status);

        var progress = new ServerPackets.QuestProgress { Progress = { new ServerPackets.QuestProgress.Entry { Quest = 1, Objective = 5, Value = 7 } } };
        var p2 = new ServerPackets.QuestProgress();
        RoundTrip(progress, p2);
        Assert.Equal(7, p2.Progress.Single().Value);
        Assert.Equal(4 + 12, Write(progress).Length);

        var history = new ServerPackets.QuestHistory { Full = false, History = { new ServerPackets.QuestHistory.Entry { Quest = 3, Name = "Old", Times = 4, LastAt = now, AvailableAt = 0 } } };
        var h2 = new ServerPackets.QuestHistory();
        RoundTrip(history, h2);
        Assert.False(h2.Full);
        Assert.Equal("Old", h2.History.Single().Name);
        Assert.Equal(4, h2.History.Single().Times);
    }

    [Fact]
    public void QuestAvailability_and_QuestOffer_round_trip()
    {
        var avail = new ServerPackets.QuestAvailability
        {
            Npcs =
            {
                new ServerPackets.QuestAvailability.Entry { ObjectID = 88, Icon = QuestIcon.ExclamationYellow, Offered = { 1, 2 }, Finishing = { 3 }, Choosing = { 7 } },
                new ServerPackets.QuestAvailability.Entry { ObjectID = 89, Icon = QuestIcon.None },
            },
        };
        var a2 = new ServerPackets.QuestAvailability();
        RoundTrip(avail, a2);
        Assert.Equal(new[] { 1, 2 }, a2.Npcs[0].Offered);
        Assert.Equal(new[] { 3 }, a2.Npcs[0].Finishing);
        Assert.Equal(new[] { 7 }, a2.Npcs[0].Choosing);
        Assert.Empty(a2.Npcs[1].Choosing);
        Assert.Equal(QuestIcon.None, a2.Npcs[1].Icon);
        Assert.Empty(a2.Npcs[1].Offered);

        var offer = new ServerPackets.QuestOffer { Quest = 9, Kind = QuestEndpointKind.Auto, SharedBy = "Ann" };
        var o2 = new ServerPackets.QuestOffer();
        RoundTrip(offer, o2);
        Assert.Equal(QuestEndpointKind.Auto, o2.Kind);
        Assert.Equal("Ann", o2.SharedBy);

        var refused = new ServerPackets.QuestRefused { Quest = 9, Request = QuestRequestKind.TurnIn, Reason = QuestRefusal.NotReady };
        var r2 = new ServerPackets.QuestRefused();
        RoundTrip(refused, r2);
        Assert.Equal(9, r2.Quest);
        Assert.Equal(QuestRequestKind.TurnIn, r2.Request);
        Assert.Equal(QuestRefusal.NotReady, r2.Reason);
        Assert.Equal(4 + 1 + 1, Write(refused).Length);
    }

    [Fact]
    public void Client_packets_round_trip()
    {
        var accept = new ClientPackets.QuestAccept { Quest = 1, NpcObjectID = 88 };
        var a2 = new ClientPackets.QuestAccept();
        RoundTrip(accept, a2);
        Assert.Equal(88u, a2.NpcObjectID);
        Assert.Equal(8, Write(accept).Length);

        var turnIn = new ClientPackets.QuestTurnIn { Quest = 1, NpcObjectID = 88, RewardPicks = { 2, 5 } };
        var t2 = new ClientPackets.QuestTurnIn();
        RoundTrip(turnIn, t2);
        Assert.Equal(new[] { 2, 5 }, t2.RewardPicks);

        var choose = new ClientPackets.QuestChoose { Quest = 1, NpcObjectID = 88, ChoiceKey = "spare" };
        var c2 = new ClientPackets.QuestChoose();
        RoundTrip(choose, c2);
        Assert.Equal("spare", c2.ChoiceKey);

        RoundTrip(new ClientPackets.QuestAbandon { Quest = 4 }, new ClientPackets.QuestAbandon());
        RoundTrip(new ClientPackets.QuestShare { Quest = 4 }, new ClientPackets.QuestShare());
        var d2 = new ClientPackets.QuestDefinitionRequest();
        RoundTrip(new ClientPackets.QuestDefinitionRequest { Quest = 4 }, d2);
        Assert.Equal(4, d2.Quest);
    }

    [Fact]
    public void Oversized_counts_are_rejected()
    {
        var bytes = BitConverter.GetBytes(0x7FFFFFF0);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.QuestProgress(), bytes));
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.QuestAvailability(), bytes));
        Assert.Throws<InvalidDataException>(() => Read(new ClientPackets.QuestTurnIn(), new byte[8].Concat(bytes).ToArray()));
    }

    [Fact]
    public void ObjectNPC_has_no_quest_list_any_more()
    {
        Assert.Null(typeof(ServerPackets.ObjectNPC).GetField("QuestIDs"));
        Assert.Null(typeof(ServerPackets.GameDataChanged).GetField("Quests"));

        var sent = new ServerPackets.ObjectNPC { ObjectID = 88, Name = "Shopkeeper", Image = 14, Direction = MirDirection.Down };
        var bytes = Write(sent);
        var read = new ServerPackets.ObjectNPC();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(bytes, Write(read));
        // uint + string ("Shopkeeper" = 1 + 10) + int + ushort + int + 2 ints + byte, and nothing after the direction
        Assert.Equal(4 + 11 + 4 + 2 + 4 + 8 + 1, bytes.Length);
    }
}
