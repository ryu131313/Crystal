using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.5 detail packets (2026-10-16): guild member details, wars, log, ranking details, guild board, mentor finder,
/// territory actions and profiles. Pins the populated layouts the web client decodes (WebClient/src/protocol/server.ts, client.ts).
/// </summary>
public class GuildSocialPacketTests
{
    [Fact]
    public void Ids_are_appended_after_SelectDetails_and_RequestMapDetails_and_known_to_the_factory()
    {
        Assert.Equal(288, (int)ServerPacketIds.GuildMemberDetails);
        Assert.Equal(289, (int)ServerPacketIds.GuildWarList);
        Assert.Equal(290, (int)ServerPacketIds.GuildLog);
        Assert.Equal(291, (int)ServerPacketIds.RankingsDetail);
        Assert.Equal(292, (int)ServerPacketIds.GuildRankings);
        Assert.Equal(293, (int)ServerPacketIds.MentorDetails);
        Assert.Equal(294, (int)ServerPacketIds.MentorCandidates);
        Assert.Equal(295, (int)ServerPacketIds.PlayerProfile);
        Assert.Equal(160, (int)ClientPacketIds.RequestGuildMemberDetails);
        Assert.Equal(161, (int)ClientPacketIds.RequestGuildLog);
        Assert.Equal(162, (int)ClientPacketIds.GetGuildRanking);
        Assert.Equal(163, (int)ClientPacketIds.FindMentor);
        Assert.Equal(164, (int)ClientPacketIds.GuildTerritoryAction);
        Assert.Equal(165, (int)ClientPacketIds.SetProfile);

        Assert.IsType<ServerPackets.GuildMemberDetails>(GetServerPacket((short)ServerPacketIds.GuildMemberDetails));
        Assert.IsType<ServerPackets.GuildWarList>(GetServerPacket((short)ServerPacketIds.GuildWarList));
        Assert.IsType<ServerPackets.GuildLog>(GetServerPacket((short)ServerPacketIds.GuildLog));
        Assert.IsType<ServerPackets.RankingsDetail>(GetServerPacket((short)ServerPacketIds.RankingsDetail));
        Assert.IsType<ServerPackets.GuildRankings>(GetServerPacket((short)ServerPacketIds.GuildRankings));
        Assert.IsType<ServerPackets.MentorDetails>(GetServerPacket((short)ServerPacketIds.MentorDetails));
        Assert.IsType<ServerPackets.MentorCandidates>(GetServerPacket((short)ServerPacketIds.MentorCandidates));
        Assert.IsType<ServerPackets.PlayerProfile>(GetServerPacket((short)ServerPacketIds.PlayerProfile));
        Assert.IsType<ClientPackets.RequestGuildMemberDetails>(GetClientPacket((short)ClientPacketIds.RequestGuildMemberDetails));
        Assert.IsType<ClientPackets.RequestGuildLog>(GetClientPacket((short)ClientPacketIds.RequestGuildLog));
        Assert.IsType<ClientPackets.GetGuildRanking>(GetClientPacket((short)ClientPacketIds.GetGuildRanking));
        Assert.IsType<ClientPackets.FindMentor>(GetClientPacket((short)ClientPacketIds.FindMentor));
        Assert.IsType<ClientPackets.GuildTerritoryAction>(GetClientPacket((short)ClientPacketIds.GuildTerritoryAction));
        Assert.IsType<ClientPackets.SetProfile>(GetClientPacket((short)ClientPacketIds.SetProfile));
    }

    [Fact]
    public void Observable_flags_private_replies_are_false()
    {
        Assert.False(new ServerPackets.GuildMemberDetails().Observable);
        Assert.False(new ServerPackets.GuildWarList().Observable);
        Assert.False(new ServerPackets.GuildLog().Observable);
        Assert.False(new ServerPackets.MentorDetails().Observable);
        Assert.False(new ServerPackets.MentorCandidates().Observable);
        Assert.True(new ServerPackets.RankingsDetail().Observable);
        Assert.True(new ServerPackets.GuildRankings().Observable);
        Assert.True(new ServerPackets.PlayerProfile().Observable);
    }

    [Fact]
    public void GuildTerritoryActionKind_values_are_fixed()
    {
        Assert.Equal(0, (byte)GuildTerritoryActionKind.Rent);
        Assert.Equal(1, (byte)GuildTerritoryActionKind.Extend);
        Assert.Equal(2, (byte)GuildTerritoryActionKind.ListForSale);
        Assert.Equal(3, (byte)GuildTerritoryActionKind.CancelSale);
        Assert.Equal(4, (byte)GuildTerritoryActionKind.Teleport);
    }

    [Fact]
    public void GuildMemberDetails_round_trips_and_a_worst_case_part_fits_one_frame()
    {
        long seen = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Local).ToBinary();
        var sent = new ServerPackets.GuildMemberDetails { Part = 1, PartCount = 2 };
        sent.Members.Add(new ClientGuildMemberDetail { Name = "龙门剑客", Class = MirClass.Assassin, Level = 65, LastLogin = seen, Online = true, RankIndex = 0 });
        sent.Members.Add(new ClientGuildMemberDetail { Name = null, Class = MirClass.Archer, Level = ushort.MaxValue, LastLogin = 0, Online = false, RankIndex = 3 });
        var bytes = Write(sent);

        var read = new ServerPackets.GuildMemberDetails();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(1, read.Part);
        Assert.Equal(2, read.PartCount);
        Assert.Equal(2, read.Members.Count);
        Assert.Equal("龙门剑客", read.Members[0].Name);
        Assert.Equal(MirClass.Assassin, read.Members[0].Class);
        Assert.Equal(65, read.Members[0].Level);
        Assert.Equal(seen, read.Members[0].LastLogin);
        Assert.True(read.Members[0].Online);
        Assert.Equal(string.Empty, read.Members[1].Name);
        Assert.Equal(ushort.MaxValue, read.Members[1].Level);
        Assert.Equal(3, read.Members[1].RankIndex);

        // 15 three-byte chars = the longest name the server accepts; one row must be exactly MaxWireSize.
        var worst = new ClientGuildMemberDetail { Name = new string('龙', 15) };
        var row = new MemoryStream();
        using (var w = new BinaryWriter(row)) worst.Save(w);
        Assert.Equal(ClientGuildMemberDetail.MaxWireSize, row.ToArray().Length);
        int full = 4 + 1 + 1 + 4 + ServerPackets.GuildMemberDetails.MaxRowsPerPart * ClientGuildMemberDetail.MaxWireSize;
        Assert.True(full <= ushort.MaxValue, $"full part {full} bytes");
    }

    [Fact]
    public void GuildMemberDetails_rejects_a_count_over_the_cap()
    {
        var bytes = Write(new ServerPackets.GuildMemberDetails());
        BitConverter.GetBytes(ServerPackets.GuildMemberDetails.MaxRowsPerPart + 1).CopyTo(bytes, bytes.Length - 4);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.GuildMemberDetails(), bytes));
    }

    [Fact]
    public void GuildWarList_round_trips_and_clamps_negative_time()
    {
        var sent = new ServerPackets.GuildWarList();
        sent.Wars.Add(new ClientGuildWar { GuildName = "Hens", MillisecondsLeft = 3_600_000 });
        sent.Wars.Add(new ClientGuildWar { GuildName = null, MillisecondsLeft = -5 });
        var bytes = Write(sent);
        Assert.Equal(4 + (1 + 4 + 8) + (1 + 8), bytes.Length);

        var read = new ServerPackets.GuildWarList();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("Hens", read.Wars[0].GuildName);
        Assert.Equal(3_600_000, read.Wars[0].MillisecondsLeft);
        Assert.Equal(string.Empty, read.Wars[1].GuildName);
        Assert.Equal(0, read.Wars[1].MillisecondsLeft);

        var empty = Write(new ServerPackets.GuildWarList());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, empty);
        BitConverter.GetBytes(ServerPackets.GuildWarList.MaxWars + 1).CopyTo(empty, 0);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.GuildWarList(), empty));
    }

    [Fact]
    public void GuildLog_keeps_the_newest_entries_truncates_text_and_fits_one_frame()
    {
        var sent = new ServerPackets.GuildLog();
        long t = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Local).ToBinary();
        for (int i = 0; i < ServerPackets.GuildLog.MaxEntries + 3; i++)
            sent.Entries.Add(new ClientGuildLogEntry { Time = t, Kind = 8, Text = "e" + i });
        sent.Entries[^1].Text = new string('龙', ClientGuildLogEntry.MaxTextLength + 10);
        var bytes = Write(sent);

        var read = new ServerPackets.GuildLog();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(ServerPackets.GuildLog.MaxEntries, read.Entries.Count);
        Assert.Equal("e3", read.Entries[0].Text);
        Assert.Equal(t, read.Entries[0].Time);
        Assert.Equal(8, read.Entries[0].Kind);
        Assert.Equal(ClientGuildLogEntry.MaxTextLength, read.Entries[^1].Text.Length);

        // worst case: every entry carries MaxTextLength three-byte chars (2-byte length prefix)
        int worst = 4 + 4 + ServerPackets.GuildLog.MaxEntries * (8 + 1 + 2 + ClientGuildLogEntry.MaxTextLength * 3);
        Assert.True(worst <= ushort.MaxValue, $"worst log {worst} bytes");

        BitConverter.GetBytes(ServerPackets.GuildLog.MaxEntries + 1).CopyTo(bytes, 0);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.GuildLog(), bytes));
    }

    [Fact]
    public void RankingsDetail_round_trips()
    {
        var sent = new ServerPackets.RankingsDetail { RankType = 3 };
        sent.Rows.Add(new ClientRankingDetail { PlayerId = long.MaxValue, GuildName = "Hens", Online = true, ExpPerTenThousand = 10000 });
        sent.Rows.Add(new ClientRankingDetail { PlayerId = 7, GuildName = null, Online = false, ExpPerTenThousand = 0 });
        var bytes = Write(sent);
        Assert.Equal(1 + 4 + (8 + 1 + 4 + 1 + 2) + (8 + 1 + 1 + 2), bytes.Length);

        var read = new ServerPackets.RankingsDetail();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(3, read.RankType);
        Assert.Equal(long.MaxValue, read.Rows[0].PlayerId);
        Assert.Equal("Hens", read.Rows[0].GuildName);
        Assert.True(read.Rows[0].Online);
        Assert.Equal(10000, read.Rows[0].ExpPerTenThousand);
        Assert.Equal(string.Empty, read.Rows[1].GuildName);
        Assert.Equal(bytes, Write(read));
    }

    [Fact]
    public void GuildRankings_round_trips_and_caps_a_page()
    {
        var sent = new ServerPackets.GuildRankings { Page = 2, Total = 57 };
        for (int i = 0; i < ServerPackets.GuildRankings.PageSize + 4; i++)
            sent.Guilds.Add(new ClientGuildRanking { Name = "G" + i, Level = (ushort)(30 - i), ExpPerTenThousand = 5000, Members = i, Leader = "L" + i });
        var bytes = Write(sent);

        var read = new ServerPackets.GuildRankings();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(2, read.Page);
        Assert.Equal(57, read.Total);
        Assert.Equal(ServerPackets.GuildRankings.PageSize, read.Guilds.Count);
        Assert.Equal("G0", read.Guilds[0].Name);
        Assert.Equal(30, read.Guilds[0].Level);
        Assert.Equal(5000, read.Guilds[0].ExpPerTenThousand);
        Assert.Equal("L19", read.Guilds[^1].Leader);
        Assert.Equal(19, read.Guilds[^1].Members);

        var request = new ClientPackets.GetGuildRanking { Page = -1 };
        var readRequest = new ClientPackets.GetGuildRanking();
        Assert.Equal(4, Read(readRequest, Write(request)));
        Assert.Equal(-1, readRequest.Page);
    }

    [Fact]
    public void MentorDetails_and_MentorCandidates_round_trip()
    {
        long start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local).ToBinary();
        long end = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Local).ToBinary();
        var details = new ServerPackets.MentorDetails { PartnerClass = MirClass.Wizard, MentorDate = start, EndsAt = end };
        var detailBytes = Write(details);
        Assert.Equal(1 + 8 + 8, detailBytes.Length);
        var readDetails = new ServerPackets.MentorDetails();
        Assert.Equal(detailBytes.Length, Read(readDetails, detailBytes));
        Assert.Equal(MirClass.Wizard, readDetails.PartnerClass);
        Assert.Equal(start, readDetails.MentorDate);
        Assert.Equal(end, readDetails.EndsAt);

        var sent = new ServerPackets.MentorCandidates();
        for (int i = 0; i < ServerPackets.MentorCandidates.MaxRows + 2; i++)
            sent.Candidates.Add(new ClientMentorCandidate { Name = "M" + i, Class = MirClass.Taoist, Level = (ushort)(40 + i) });
        var bytes = Write(sent);
        var read = new ServerPackets.MentorCandidates();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(ServerPackets.MentorCandidates.MaxRows, read.Candidates.Count);
        Assert.Equal("M0", read.Candidates[0].Name);
        Assert.Equal(MirClass.Taoist, read.Candidates[0].Class);
        Assert.Equal(40, read.Candidates[0].Level);
        BitConverter.GetBytes(ServerPackets.MentorCandidates.MaxRows + 1).CopyTo(bytes, 0);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.MentorCandidates(), bytes));

        var find = new ClientPackets.FindMentor { MinLevel = 0, MaxLevel = ushort.MaxValue };
        var findBytes = Write(find);
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0xFF }, findBytes);
        var readFind = new ClientPackets.FindMentor();
        Assert.Equal(4, Read(readFind, findBytes));
        Assert.Equal(ushort.MaxValue, readFind.MaxLevel);
    }

    [Fact]
    public void GuildTerritoryAction_round_trips_a_price_above_int_max()
    {
        var sent = new ClientPackets.GuildTerritoryAction { Kind = GuildTerritoryActionKind.ListForSale, TerritoryIndex = -1, Price = uint.MaxValue };
        var bytes = Write(sent);
        Assert.Equal(new byte[] { 2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, bytes);
        var read = new ClientPackets.GuildTerritoryAction();
        Assert.Equal(9, Read(read, bytes));
        Assert.Equal(GuildTerritoryActionKind.ListForSale, read.Kind);
        Assert.Equal(-1, read.TerritoryIndex);
        Assert.Equal(uint.MaxValue, read.Price);

        // an out-of-range kind decodes unchecked; the server handler must reject it
        bytes[0] = 200;
        var bad = new ClientPackets.GuildTerritoryAction();
        Read(bad, bytes);
        Assert.False(Enum.IsDefined(bad.Kind));
    }

    [Fact]
    public void SetProfile_PlayerProfile_and_the_empty_requests_round_trip()
    {
        var set = new ClientPackets.SetProfile { Text = "招募 mentees" };
        var readSet = new ClientPackets.SetProfile();
        var setBytes = Write(set);
        Assert.Equal(setBytes.Length, Read(readSet, setBytes));
        Assert.Equal("招募 mentees", readSet.Text);
        Assert.Equal(new byte[] { 0 }, Write(new ClientPackets.SetProfile { Text = null }));

        var profile = new ServerPackets.PlayerProfile { Name = "Healer", Text = null };
        var bytes = Write(profile);
        Assert.Equal(new byte[] { 6, (byte)'H', (byte)'e', (byte)'a', (byte)'l', (byte)'e', (byte)'r', 0 }, bytes);
        var read = new ServerPackets.PlayerProfile();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("Healer", read.Name);
        Assert.Equal(string.Empty, read.Text);

        Assert.Empty(Write(new ClientPackets.RequestGuildMemberDetails()));
        Assert.Empty(Write(new ClientPackets.RequestGuildLog()));
    }
}
