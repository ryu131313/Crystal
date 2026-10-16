using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.6 detail packets (2026-10-16): game shop gifts, extended market search, market price history, the sent mail
/// folder. Pins the populated layouts the web client encodes / decodes (WebClient/src/protocol/server.ts, client.ts).
/// </summary>
public class MarketMailPacketTests
{
    [Fact]
    public void Ids_are_appended_after_PlayerProfile_and_SetProfile_and_known_to_the_factory()
    {
        Assert.Equal(295, (int)ServerPacketIds.PlayerProfile);
        Assert.Equal(296, (int)ServerPacketIds.MarketPriceHistory);
        Assert.Equal(297, (int)ServerPacketIds.SentMail);
        Assert.Equal(165, (int)ClientPacketIds.SetProfile);
        Assert.Equal(166, (int)ClientPacketIds.GameshopGift);
        Assert.Equal(167, (int)ClientPacketIds.MarketSearchEx);
        Assert.Equal(168, (int)ClientPacketIds.MarketPriceHistory);
        Assert.Equal(169, (int)ClientPacketIds.RequestSentMail);
        Assert.Equal(170, (int)ClientPacketIds.HideSentMail);

        Assert.IsType<ServerPackets.MarketPriceHistory>(GetServerPacket((short)ServerPacketIds.MarketPriceHistory));
        Assert.IsType<ServerPackets.SentMail>(GetServerPacket((short)ServerPacketIds.SentMail));
        Assert.IsType<ClientPackets.GameshopGift>(GetClientPacket((short)ClientPacketIds.GameshopGift));
        Assert.IsType<ClientPackets.MarketSearchEx>(GetClientPacket((short)ClientPacketIds.MarketSearchEx));
        Assert.IsType<ClientPackets.MarketPriceHistory>(GetClientPacket((short)ClientPacketIds.MarketPriceHistory));
        Assert.IsType<ClientPackets.RequestSentMail>(GetClientPacket((short)ClientPacketIds.RequestSentMail));
        Assert.IsType<ClientPackets.HideSentMail>(GetClientPacket((short)ClientPacketIds.HideSentMail));
    }

    [Fact]
    public void Observable_flags()
    {
        Assert.True(new ServerPackets.MarketPriceHistory().Observable);
        Assert.False(new ServerPackets.SentMail().Observable);
    }

    [Fact]
    public void GameshopGift_round_trips()
    {
        var sent = new ClientPackets.GameshopGift { GIndex = 70000, Quantity = 99, PType = 1, Recipient = "龙门" };
        var bytes = Write(sent);
        Assert.Equal(4 + 4 + 1 + 1 + 6, bytes.Length); // int32, int32, u8, string (1 length byte + 2 x 3 UTF-8 bytes)

        var read = new ClientPackets.GameshopGift();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(70000, read.GIndex);
        Assert.Equal(99, read.Quantity);
        Assert.Equal(1, read.PType);
        Assert.Equal("龙门", read.Recipient);

        // A null recipient writes as an empty string.
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Write(new ClientPackets.GameshopGift { Recipient = null }));
    }

    [Fact]
    public void MarketSearchEx_round_trips_and_keeps_MarketSearch_defaults()
    {
        var fresh = new ClientPackets.MarketSearchEx();
        Assert.Equal(0, fresh.MinShape);
        Assert.Equal(5000, fresh.MaxShape);
        Assert.Equal(MarketPanelType.Market, fresh.MarketType);

        var sent = new ClientPackets.MarketSearchEx
        {
            Match = "Sword", Type = ItemType.Weapon, Usermode = true, MinShape = 3, MaxShape = 4999,
            MarketType = MarketPanelType.Auction, Sort = ClientPackets.MarketSearchEx.SortEndingSoonest,
            PriceMin = 100, PriceMax = uint.MaxValue - 1, MinGrade = (byte)ItemGrade.Rare,
        };
        var bytes = Write(sent);
        // string (1 + 5), type, bool, 2 x int16, panel, sort, 2 x uint32, grade
        Assert.Equal(1 + 5 + 1 + 1 + 2 + 2 + 1 + 1 + 4 + 4 + 1, bytes.Length);

        var read = new ClientPackets.MarketSearchEx();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("Sword", read.Match);
        Assert.Equal(ItemType.Weapon, read.Type);
        Assert.True(read.Usermode);
        Assert.Equal(3, read.MinShape);
        Assert.Equal(4999, read.MaxShape);
        Assert.Equal(MarketPanelType.Auction, read.MarketType);
        Assert.Equal(4, read.Sort);
        Assert.Equal(100u, read.PriceMin);
        Assert.Equal(uint.MaxValue - 1, read.PriceMax);
        Assert.Equal((byte)ItemGrade.Rare, read.MinGrade);
        Assert.Equal(bytes, Write(read));
    }

    [Fact]
    public void Small_client_requests_round_trip()
    {
        var history = new ClientPackets.MarketPriceHistory { ItemIndex = 70000 };
        var readHistory = new ClientPackets.MarketPriceHistory();
        Assert.Equal(4, Read(readHistory, Write(history)));
        Assert.Equal(70000, readHistory.ItemIndex);

        Assert.Empty(Write(new ClientPackets.RequestSentMail()));

        var hide = new ClientPackets.HideSentMail { MailID = ulong.MaxValue - 5 };
        var readHide = new ClientPackets.HideSentMail();
        Assert.Equal(8, Read(readHide, Write(hide)));
        Assert.Equal(ulong.MaxValue - 5, readHide.MailID);
    }

    [Fact]
    public void MarketPriceHistory_round_trips_and_keeps_the_newest_rows()
    {
        long day = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Local).ToBinary();
        var sent = new ServerPackets.MarketPriceHistory { ItemIndex = 42 };
        sent.Sales.Add(new ClientMarketSale { Price = 1500, Date = day });
        sent.Sales.Add(new ClientMarketSale { Price = uint.MaxValue, Date = day + 1 });
        var bytes = Write(sent);
        Assert.Equal(4 + 4 + 2 * (4 + 8), bytes.Length);

        var read = new ServerPackets.MarketPriceHistory();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(42, read.ItemIndex);
        Assert.Equal(2, read.Sales.Count);
        Assert.Equal(1500u, read.Sales[0].Price);
        Assert.Equal(day, read.Sales[0].Date);
        Assert.Equal(uint.MaxValue, read.Sales[1].Price);

        // Over the cap: the oldest rows are dropped, the newest MaxSales are written in order.
        var many = new ServerPackets.MarketPriceHistory { ItemIndex = 1 };
        for (int i = 0; i < ServerPackets.MarketPriceHistory.MaxSales + 3; i++)
            many.Sales.Add(new ClientMarketSale { Price = (uint)i, Date = i });
        var readMany = new ServerPackets.MarketPriceHistory();
        Read(readMany, Write(many));
        Assert.Equal(ServerPackets.MarketPriceHistory.MaxSales, readMany.Sales.Count);
        Assert.Equal(3u, readMany.Sales[0].Price);
        Assert.Equal((uint)(ServerPackets.MarketPriceHistory.MaxSales + 2), readMany.Sales[^1].Price);
    }

    [Fact]
    public void MarketPriceHistory_rejects_a_count_over_the_cap()
    {
        var bytes = Write(new ServerPackets.MarketPriceHistory());
        BitConverter.GetBytes(ServerPackets.MarketPriceHistory.MaxSales + 1).CopyTo(bytes, 4);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.MarketPriceHistory(), bytes));
        BitConverter.GetBytes(-1).CopyTo(bytes, 4);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.MarketPriceHistory(), bytes));
    }

    [Fact]
    public void SentMail_round_trips_and_writes_null_strings_as_empty()
    {
        long sentAt = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Local).ToBinary();
        var sent = new ServerPackets.SentMail();
        sent.Mails.Add(new ClientSentMail
        {
            MailID = ulong.MaxValue, RecipientName = "龙门", Message = "Hello", Gold = uint.MaxValue,
            ItemCount = 5, DateSent = sentAt, Opened = true, Collected = false,
        });
        sent.Mails.Add(new ClientSentMail { MailID = 2, RecipientName = null, Message = null });
        var bytes = Write(sent);

        var read = new ServerPackets.SentMail();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(2, read.Mails.Count);
        var a = read.Mails[0];
        Assert.Equal(ulong.MaxValue, a.MailID);
        Assert.Equal("龙门", a.RecipientName);
        Assert.Equal("Hello", a.Message);
        Assert.Equal(uint.MaxValue, a.Gold);
        Assert.Equal(5, a.ItemCount);
        Assert.Equal(sentAt, a.DateSent);
        Assert.True(a.Opened);
        Assert.False(a.Collected);
        Assert.Equal(2ul, read.Mails[1].MailID);
        Assert.Equal(string.Empty, read.Mails[1].RecipientName);
        Assert.Equal(string.Empty, read.Mails[1].Message);
        Assert.Equal(bytes, Write(read));
    }

    [Fact]
    public void SentMail_cuts_long_text_without_splitting_a_surrogate_pair()
    {
        int max = ClientSentMail.MaxMessageLength;
        // An emoji (surrogate pair) straddling the cut: the high surrogate at max - 1 is dropped with its pair.
        string message = new string('x', max - 1) + "\U0001F600" + "tail";
        var sent = new ServerPackets.SentMail();
        sent.Mails.Add(new ClientSentMail { RecipientName = new string('n', 40), Message = message });

        var read = new ServerPackets.SentMail();
        Read(read, Write(sent));
        Assert.Equal(ClientSentMail.MaxNameLength, read.Mails[0].RecipientName.Length);
        Assert.Equal(new string('x', max - 1), read.Mails[0].Message);

        var plain = new ServerPackets.SentMail();
        plain.Mails.Add(new ClientSentMail { Message = new string('y', max + 50) });
        var readPlain = new ServerPackets.SentMail();
        Read(readPlain, Write(plain));
        Assert.Equal(max, readPlain.Mails[0].Message.Length);
    }

    [Fact]
    public void SentMail_writes_at_most_MaxRows_and_the_worst_case_fits_one_frame()
    {
        var sent = new ServerPackets.SentMail();
        // Worst case: every string at its cap in 3-byte UTF-8 characters, plus more rows than the cap.
        for (int i = 0; i < ServerPackets.SentMail.MaxRows + 10; i++)
            sent.Mails.Add(new ClientSentMail
            {
                MailID = (ulong)i,
                RecipientName = new string('龙', ClientSentMail.MaxNameLength + 5),
                Message = new string('门', ClientSentMail.MaxMessageLength + 100),
            });
        var bytes = Write(sent);
        var read = new ServerPackets.SentMail();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(ServerPackets.SentMail.MaxRows, read.Mails.Count);
        Assert.Equal((ulong)(ServerPackets.SentMail.MaxRows - 1), read.Mails[^1].MailID);

        // 4-byte frame header + payload stays under the 65 535-byte frame limit.
        Assert.True(bytes.Length + 4 <= ushort.MaxValue, $"worst case {bytes.Length} bytes");
    }

    [Fact]
    public void SentMail_rejects_a_count_over_the_cap()
    {
        var bytes = Write(new ServerPackets.SentMail());
        BitConverter.GetBytes(ServerPackets.SentMail.MaxRows + 1).CopyTo(bytes, 0);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.SentMail(), bytes));
    }
}
