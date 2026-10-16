using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.1 batch / safety packets (2026-10-16). The generic round trip only fills empty lists,
/// so these pin the populated wire layouts the web client encodes (WebClient/src/protocol/client.ts).
/// </summary>
public class BatchItemPacketTests
{
    [Fact]
    public void Ids_are_appended_after_RemoveStoragePassword_and_known_to_the_factory()
    {
        Assert.Equal(152, (int)ClientPacketIds.RemoveStoragePassword);
        Assert.Equal(153, (int)ClientPacketIds.SellItems);
        Assert.Equal(154, (int)ClientPacketIds.RepairItems);
        Assert.Equal(155, (int)ClientPacketIds.CollectAllParcels);
        Assert.Equal(156, (int)ClientPacketIds.ReadMails);
        Assert.Equal(157, (int)ClientPacketIds.SortItems);
        Assert.IsType<ClientPackets.SellItems>(GetClientPacket((short)ClientPacketIds.SellItems));
        Assert.IsType<ClientPackets.RepairItems>(GetClientPacket((short)ClientPacketIds.RepairItems));
        Assert.IsType<ClientPackets.CollectAllParcels>(GetClientPacket((short)ClientPacketIds.CollectAllParcels));
        Assert.IsType<ClientPackets.ReadMails>(GetClientPacket((short)ClientPacketIds.ReadMails));
        Assert.IsType<ClientPackets.SortItems>(GetClientPacket((short)ClientPacketIds.SortItems));
        // Known defect fixed 2026-10-16: the server used to discard the receive buffer.
        Assert.IsType<ClientPackets.PurchaseGuildTerritory>(GetClientPacket((short)ClientPacketIds.PurchaseGuildTerritory));
    }

    [Fact]
    public void SellItems_round_trips_a_populated_list()
    {
        var sent = new ClientPackets.SellItems();
        sent.Items.Add((1UL, (ushort)5));
        sent.Items.Add((ulong.MaxValue, ushort.MaxValue));
        var bytes = Write(sent);
        Assert.Equal(4 + 2 * (8 + 2), bytes.Length); // int32 count, then (u64, u16) pairs
        var read = new ClientPackets.SellItems();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(sent.Items, read.Items);
    }

    [Fact]
    public void RepairItems_round_trips_a_populated_list()
    {
        var sent = new ClientPackets.RepairItems { Special = true };
        sent.UniqueIDs.Add(7UL);
        sent.UniqueIDs.Add(ulong.MaxValue);
        var bytes = Write(sent);
        Assert.Equal(1 + 4 + 2 * 8, bytes.Length); // bool, int32 count, u64s
        var read = new ClientPackets.RepairItems();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.True(read.Special);
        Assert.Equal(sent.UniqueIDs, read.UniqueIDs);
    }

    [Fact]
    public void ReadMails_round_trips_a_populated_list()
    {
        var sent = new ClientPackets.ReadMails();
        sent.MailIDs.Add(3UL);
        sent.MailIDs.Add(4UL);
        sent.MailIDs.Add(5UL);
        var bytes = Write(sent);
        Assert.Equal(4 + 3 * 8, bytes.Length);
        var read = new ClientPackets.ReadMails();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(sent.MailIDs, read.MailIDs);
    }

    [Fact]
    public void CollectAllParcels_is_empty_and_SortItems_is_two_bytes()
    {
        Assert.Empty(Write(new ClientPackets.CollectAllParcels()));

        var sent = new ClientPackets.SortItems { Grid = MirGridType.Storage, Mode = 1 };
        var bytes = Write(sent);
        Assert.Equal(new byte[] { (byte)MirGridType.Storage, 1 }, bytes);
        var read = new ClientPackets.SortItems();
        Assert.Equal(2, Read(read, bytes));
        Assert.Equal(MirGridType.Storage, read.Grid);
        Assert.Equal(1, read.Mode);
    }

    [Fact]
    public void Truncated_list_throws_instead_of_reading_past_the_payload()
    {
        // count says 3 but only one id follows: the reader must fail (the server drops the connection), not invent data
        var bytes = BitConverter.GetBytes(3).Concat(BitConverter.GetBytes(9UL)).ToArray();
        Assert.Throws<EndOfStreamException>(() => Read(new ClientPackets.ReadMails(), bytes));
    }
}
