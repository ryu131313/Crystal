using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.8 allow-toggle echo (2026-10-16): S.AllowToggles { ushort Flags }, C.SetAllowToggle { AllowToggle, bool }
/// and the existing C.ChangeTrade { bool } the web client now encodes. Pins the layouts WebClient/src/protocol decodes.
/// </summary>
public class AllowTogglePacketTests
{
    [Fact]
    public void Ids_are_appended_after_the_friend_packets_and_known_to_the_factory()
    {
        Assert.Equal(299, (int)ServerPacketIds.FriendRequestResult);
        Assert.Equal(300, (int)ServerPacketIds.AllowToggles);
        Assert.Equal(171, (int)ClientPacketIds.FriendRequestReply);
        Assert.Equal(172, (int)ClientPacketIds.SetAllowToggle);

        Assert.IsType<ServerPackets.AllowToggles>(GetServerPacket((short)ServerPacketIds.AllowToggles));
        Assert.IsType<ClientPackets.SetAllowToggle>(GetClientPacket((short)ClientPacketIds.SetAllowToggle));
        Assert.IsType<ClientPackets.ChangeTrade>(GetClientPacket((short)ClientPacketIds.ChangeTrade));
    }

    [Fact]
    public void AllowToggle_values_are_fixed_and_fit_a_ushort_bit_index()
    {
        Assert.Equal(0, (byte)AllowToggle.Group);
        Assert.Equal(1, (byte)AllowToggle.Trade);
        Assert.Equal(2, (byte)AllowToggle.Observe);
        Assert.Equal(3, (byte)AllowToggle.GuildInvite);
        Assert.Equal(4, (byte)AllowToggle.GroupRecall);
        Assert.Equal(5, (byte)AllowToggle.Marriage);
        Assert.Equal(6, (byte)AllowToggle.LoverRecall);
        Assert.Equal(7, (byte)AllowToggle.Mentor);
        Assert.Equal(8, (byte)AllowToggle.FriendRequests);
        Assert.Equal(9, Enum.GetValues<AllowToggle>().Length);
        foreach (var t in Enum.GetValues<AllowToggle>()) Assert.True((byte)t < 16);
    }

    [Fact]
    public void AllowToggles_is_private()
    {
        Assert.False(new ServerPackets.AllowToggles().Observable);
    }

    [Fact]
    public void AllowToggles_Set_and_Get_use_one_bit_per_toggle()
    {
        var p = new ServerPackets.AllowToggles();
        foreach (var t in Enum.GetValues<AllowToggle>())
        {
            p.Flags = 0;
            p.Set(t, true);
            Assert.Equal((ushort)(1 << (int)t), p.Flags);
            Assert.True(p.Get(t));
            p.Set(t, false);
            Assert.Equal(0, p.Flags);
            Assert.False(p.Get(t));
        }
    }

    [Fact]
    public void AllowToggles_round_trips_as_a_little_endian_ushort()
    {
        var sent = new ServerPackets.AllowToggles();
        sent.Set(AllowToggle.Group, true);
        sent.Set(AllowToggle.Trade, true);
        sent.Set(AllowToggle.FriendRequests, true);
        var bytes = Write(sent);
        Assert.Equal(new byte[] { 0x03, 0x01 }, bytes);

        var read = new ServerPackets.AllowToggles();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal((ushort)0x0103, read.Flags);
        Assert.True(read.Get(AllowToggle.Group));
        Assert.True(read.Get(AllowToggle.Trade));
        Assert.False(read.Get(AllowToggle.Observe));
        Assert.True(read.Get(AllowToggle.FriendRequests));
    }

    [Fact]
    public void SetAllowToggle_round_trips_byte_then_bool()
    {
        foreach (var t in Enum.GetValues<AllowToggle>())
        foreach (bool value in new[] { true, false })
        {
            var bytes = Write(new ClientPackets.SetAllowToggle { Toggle = t, Value = value });
            Assert.Equal(new byte[] { (byte)t, (byte)(value ? 1 : 0) }, bytes);
            var read = new ClientPackets.SetAllowToggle();
            Assert.Equal(bytes.Length, Read(read, bytes));
            Assert.Equal(t, read.Toggle);
            Assert.Equal(value, read.Value);
        }
    }

    [Fact]
    public void SetAllowToggle_reads_an_undefined_toggle_without_validating()
    {
        // ReadPacket does not validate; MirConnection.SetAllowToggle rejects it with Enum.IsDefined.
        var read = new ClientPackets.SetAllowToggle();
        Assert.Equal(2, Read(read, new byte[] { 200, 1 }));
        Assert.Equal((AllowToggle)200, read.Toggle);
        Assert.False(Enum.IsDefined(read.Toggle));
    }

    [Fact]
    public void ChangeTrade_round_trips_one_bool()
    {
        foreach (bool value in new[] { true, false })
        {
            var bytes = Write(new ClientPackets.ChangeTrade { AllowTrade = value });
            Assert.Equal(new byte[] { (byte)(value ? 1 : 0) }, bytes);
            var read = new ClientPackets.ChangeTrade();
            Assert.Equal(1, Read(read, bytes));
            Assert.Equal(value, read.AllowTrade);
        }
    }
}
