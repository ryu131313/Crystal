using System.Drawing;
using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.4 detail packets (2026-10-16). The generic round trip only fills empty lists, so these pin the populated
/// layouts the web client decodes (WebClient/src/protocol/server.ts, client.ts).
/// </summary>
public class DetailPacketTests
{
    [Fact]
    public void Ids_are_appended_after_ServerRules_and_SortItems_and_known_to_the_factory()
    {
        Assert.Equal(281, (int)ServerPacketIds.ServerRules);
        Assert.Equal(282, (int)ServerPacketIds.PoisonStatus);
        Assert.Equal(283, (int)ServerPacketIds.GroupMemberStatus);
        Assert.Equal(284, (int)ServerPacketIds.HeroAutoCast);
        Assert.Equal(285, (int)ServerPacketIds.MapDetails);
        Assert.Equal(286, (int)ServerPacketIds.LoginFailed);
        Assert.Equal(287, (int)ServerPacketIds.SelectDetails);
        Assert.Equal(157, (int)ClientPacketIds.SortItems);
        Assert.Equal(158, (int)ClientPacketIds.HeroSpellToggle);
        Assert.Equal(159, (int)ClientPacketIds.RequestMapDetails);

        Assert.IsType<ServerPackets.PoisonStatus>(GetServerPacket((short)ServerPacketIds.PoisonStatus));
        Assert.IsType<ServerPackets.GroupMemberStatus>(GetServerPacket((short)ServerPacketIds.GroupMemberStatus));
        Assert.IsType<ServerPackets.HeroAutoCast>(GetServerPacket((short)ServerPacketIds.HeroAutoCast));
        Assert.IsType<ServerPackets.MapDetails>(GetServerPacket((short)ServerPacketIds.MapDetails));
        Assert.IsType<ServerPackets.LoginFailed>(GetServerPacket((short)ServerPacketIds.LoginFailed));
        Assert.IsType<ServerPackets.SelectDetails>(GetServerPacket((short)ServerPacketIds.SelectDetails));
        Assert.IsType<ClientPackets.HeroSpellToggle>(GetClientPacket((short)ClientPacketIds.HeroSpellToggle));
        Assert.IsType<ClientPackets.RequestMapDetails>(GetClientPacket((short)ClientPacketIds.RequestMapDetails));
    }

    [Fact]
    public void Observable_and_Compressed_flags()
    {
        Assert.True(new ServerPackets.PoisonStatus().Observable);
        Assert.True(new ServerPackets.GroupMemberStatus().Observable);
        Assert.True(new ServerPackets.MapDetails().Observable);
        Assert.True(new ServerPackets.MapDetails().Compressed);
        Assert.False(new ServerPackets.HeroAutoCast().Observable);
        Assert.False(new ServerPackets.LoginFailed().Observable);
        Assert.False(new ServerPackets.SelectDetails().Observable);
    }

    [Fact]
    public void PoisonStatus_round_trips_and_clamps_negative_time()
    {
        var sent = new ServerPackets.PoisonStatus();
        sent.Poisons.Add(new ClientPoisonStatus { Type = PoisonType.Green, TimeLeft = 4500, Value = 7 });
        sent.Poisons.Add(new ClientPoisonStatus { Type = PoisonType.DelayedExplosion, TimeLeft = -10, Value = 0 });
        var bytes = Write(sent);
        Assert.Equal(4 + 2 * (2 + 4 + 4), bytes.Length); // int32 count, then (u16 type, int32 ms, int32 value)

        var read = new ServerPackets.PoisonStatus();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(2, read.Poisons.Count);
        Assert.Equal(PoisonType.Green, read.Poisons[0].Type);
        Assert.Equal(4500, read.Poisons[0].TimeLeft);
        Assert.Equal(7, read.Poisons[0].Value);
        Assert.Equal(PoisonType.DelayedExplosion, read.Poisons[1].Type);
        Assert.Equal(0, read.Poisons[1].TimeLeft);
    }

    [Fact]
    public void GroupMemberStatus_round_trips_and_caps_buffs()
    {
        var sent = new ServerPackets.GroupMemberStatus
        {
            Name = "Healer", Class = MirClass.Taoist, Level = 42, HPPercent = 100, MPPercent = 3,
        };
        for (int i = 0; i < 25; i++) sent.Buffs.Add(BuffType.Haste);
        sent.Buffs[0] = BuffType.SoulShield;
        var bytes = Write(sent);
        // string (1 length byte + 6), class, u16 level, HP %, MP %, int32 count, capped buff bytes
        Assert.Equal(1 + 6 + 1 + 2 + 1 + 1 + 4 + ServerPackets.GroupMemberStatus.MaxBuffs, bytes.Length);

        var read = new ServerPackets.GroupMemberStatus();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("Healer", read.Name);
        Assert.Equal(MirClass.Taoist, read.Class);
        Assert.Equal(42, read.Level);
        Assert.Equal(100, read.HPPercent);
        Assert.Equal(3, read.MPPercent);
        Assert.Equal(ServerPackets.GroupMemberStatus.MaxBuffs, read.Buffs.Count);
        Assert.Equal(BuffType.SoulShield, read.Buffs[0]);
    }

    [Fact]
    public void GroupMemberStatus_rejects_a_buff_count_over_the_cap()
    {
        var bytes = Write(new ServerPackets.GroupMemberStatus { Name = "" });
        // The int32 buff count is the last 4 bytes of a packet with no buffs.
        BitConverter.GetBytes(ServerPackets.GroupMemberStatus.MaxBuffs + 1).CopyTo(bytes, bytes.Length - 4);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.GroupMemberStatus(), bytes));
    }

    [Fact]
    public void HeroAutoCast_and_HeroSpellToggle_round_trip()
    {
        var sent = new ServerPackets.HeroAutoCast();
        sent.Disabled.Add(Spell.Fencing);
        sent.Disabled.Add(Spell.Thrusting);
        var bytes = Write(sent);
        Assert.Equal(new byte[] { 2, 0, 0, 0, (byte)Spell.Fencing, (byte)Spell.Thrusting }, bytes);
        var read = new ServerPackets.HeroAutoCast();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(sent.Disabled, read.Disabled);

        var toggle = new ClientPackets.HeroSpellToggle { Spell = Spell.Thrusting, AutoCast = true };
        var toggleBytes = Write(toggle);
        Assert.Equal(new byte[] { (byte)Spell.Thrusting, 1 }, toggleBytes);
        var readToggle = new ClientPackets.HeroSpellToggle();
        Assert.Equal(2, Read(readToggle, toggleBytes));
        Assert.Equal(Spell.Thrusting, readToggle.Spell);
        Assert.True(readToggle.AutoCast);
    }

    [Fact]
    public void MapDetails_round_trips_and_a_full_part_fits_one_frame()
    {
        var sent = new ServerPackets.MapDetails { MapIndex = 12, Part = 1, PartCount = 3 };
        sent.Respawns.Add(new ClientMapRespawn { MonsterIndex = 99, Location = new Point(120, 340), Spread = 15, Count = 8 });
        sent.SafeZones.Add(new ClientMapSafeZone { Location = new Point(300, 301), Size = 10, StartPoint = true });
        var bytes = Write(sent);
        Assert.Equal(4 + 1 + 1 + 4 + ClientMapRespawn.WireSize + 4 + ClientMapSafeZone.WireSize, bytes.Length);

        var read = new ServerPackets.MapDetails();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(12, read.MapIndex);
        Assert.Equal(1, read.Part);
        Assert.Equal(3, read.PartCount);
        Assert.Equal(99, read.Respawns[0].MonsterIndex);
        Assert.Equal(new Point(120, 340), read.Respawns[0].Location);
        Assert.Equal(15, read.Respawns[0].Spread);
        Assert.Equal(8, read.Respawns[0].Count);
        Assert.Equal(new Point(300, 301), read.SafeZones[0].Location);
        Assert.Equal(10, read.SafeZones[0].Size);
        Assert.True(read.SafeZones[0].StartPoint);

        // A worst-case part, uncompressed and with a 4-byte frame header, still fits the 65 535-byte frame limit.
        int full = 4 + 4 + 1 + 1
            + 4 + ServerPackets.MapDetails.MaxRespawnsPerPart * ClientMapRespawn.WireSize
            + 4 + ServerPackets.MapDetails.MaxSafeZonesPerPart * ClientMapSafeZone.WireSize;
        Assert.True(full <= ushort.MaxValue, $"full part {full} bytes");

        var request = new ClientPackets.RequestMapDetails { MapIndex = 12 };
        var readRequest = new ClientPackets.RequestMapDetails();
        Assert.Equal(4, Read(readRequest, Write(request)));
        Assert.Equal(12, readRequest.MapIndex);
    }

    [Fact]
    public void LoginFailed_round_trips_a_DateTime_binary()
    {
        long locked = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Local).ToBinary();
        var sent = new ServerPackets.LoginFailed { Result = 4, AccountTriesLeft = 2, ConnectionTriesLeft = -1, LockedUntil = locked };
        var bytes = Write(sent);
        Assert.Equal(1 + 4 + 4 + 8, bytes.Length);
        var read = new ServerPackets.LoginFailed();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(4, read.Result);
        Assert.Equal(2, read.AccountTriesLeft);
        Assert.Equal(-1, read.ConnectionTriesLeft);
        Assert.Equal(locked, read.LockedUntil);
    }

    [Fact]
    public void SelectDetails_round_trips_and_writes_null_strings_as_empty()
    {
        var sent = new ServerPackets.SelectDetails { AccountGold = uint.MaxValue };
        sent.Characters.Add(new ClientSelectDetail { Index = 5, GuildName = "龙门", MapTitle = "Bichon Province", PlayTime = 86400L * 400 });
        sent.Characters.Add(new ClientSelectDetail { Index = 6, GuildName = null, MapTitle = null, PlayTime = 0 });
        var bytes = Write(sent);

        var read = new ServerPackets.SelectDetails();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(uint.MaxValue, read.AccountGold);
        Assert.Equal(2, read.Characters.Count);
        Assert.Equal(5, read.Characters[0].Index);
        Assert.Equal("龙门", read.Characters[0].GuildName);
        Assert.Equal("Bichon Province", read.Characters[0].MapTitle);
        Assert.Equal(86400L * 400, read.Characters[0].PlayTime);
        Assert.Equal(string.Empty, read.Characters[1].GuildName);
        Assert.Equal(string.Empty, read.Characters[1].MapTitle);
        Assert.Equal(bytes, Write(read));
    }
}
