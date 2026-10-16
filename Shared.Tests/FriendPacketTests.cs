using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.7 friends (2026-10-16): online friend requests (S.FriendRequest, S.FriendRequestResult, C.FriendRequestReply)
/// and the richer wire-only ClientFriend inside S.FriendUpdate. Pins the layouts the web client decodes
/// (WebClient/src/protocol/server.ts, client.ts) and the desktop reads through Shared.dll.
/// </summary>
public class FriendPacketTests
{
    [Fact]
    public void Ids_are_appended_after_SentMail_and_HideSentMail_and_known_to_the_factory()
    {
        Assert.Equal(297, (int)ServerPacketIds.SentMail);
        Assert.Equal(298, (int)ServerPacketIds.FriendRequest);
        Assert.Equal(299, (int)ServerPacketIds.FriendRequestResult);
        Assert.Equal(170, (int)ClientPacketIds.HideSentMail);
        Assert.Equal(171, (int)ClientPacketIds.FriendRequestReply);

        Assert.IsType<ServerPackets.FriendRequest>(GetServerPacket((short)ServerPacketIds.FriendRequest));
        Assert.IsType<ServerPackets.FriendRequestResult>(GetServerPacket((short)ServerPacketIds.FriendRequestResult));
        Assert.IsType<ClientPackets.FriendRequestReply>(GetClientPacket((short)ClientPacketIds.FriendRequestReply));
    }

    [Fact]
    public void Observable_flags_private_replies_are_false()
    {
        Assert.False(new ServerPackets.FriendRequest().Observable);
        Assert.False(new ServerPackets.FriendRequestResult().Observable);
    }

    [Fact]
    public void FriendRequestResult_codes_are_fixed()
    {
        Assert.Equal(0, ServerPackets.FriendRequestResult.Accepted);
        Assert.Equal(1, ServerPackets.FriendRequestResult.Declined);
        Assert.Equal(2, ServerPackets.FriendRequestResult.Expired);
        Assert.Equal(3, ServerPackets.FriendRequestResult.Offline);
        Assert.Equal(4, ServerPackets.FriendRequestResult.ListFull);
        Assert.Equal(5, ServerPackets.FriendRequestResult.NotAccepting);
    }

    [Fact]
    public void FriendRequest_round_trips_with_exact_layout()
    {
        var sent = new ServerPackets.FriendRequest { Name = "龙门剑客", Class = MirClass.Archer, Level = ushort.MaxValue };
        var bytes = Write(sent);

        var read = new ServerPackets.FriendRequest();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("龙门剑客", read.Name);
        Assert.Equal(MirClass.Archer, read.Class);
        Assert.Equal(ushort.MaxValue, read.Level);

        // string (7-bit length + 12 UTF-8 bytes) + byte class + ushort level
        Assert.Equal(1 + 12 + 1 + 2, bytes.Length);
    }

    [Fact]
    public void FriendRequest_writes_null_name_as_empty()
    {
        var bytes = Write(new ServerPackets.FriendRequest { Name = null, Class = MirClass.Warrior, Level = 1 });
        var read = new ServerPackets.FriendRequest();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("", read.Name);
        Assert.Equal(4, bytes.Length);
    }

    [Fact]
    public void FriendRequestResult_round_trips()
    {
        var sent = new ServerPackets.FriendRequestResult { Name = "Bob", Result = ServerPackets.FriendRequestResult.NotAccepting };
        var bytes = Write(sent);
        var read = new ServerPackets.FriendRequestResult();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal("Bob", read.Name);
        Assert.Equal(ServerPackets.FriendRequestResult.NotAccepting, read.Result);
        Assert.Equal(1 + 3 + 1, bytes.Length);

        var nul = Write(new ServerPackets.FriendRequestResult { Name = null, Result = ServerPackets.FriendRequestResult.Expired });
        var readNull = new ServerPackets.FriendRequestResult();
        Assert.Equal(nul.Length, Read(readNull, nul));
        Assert.Equal("", readNull.Name);
        Assert.Equal(ServerPackets.FriendRequestResult.Expired, readNull.Result);
    }

    [Fact]
    public void FriendRequestReply_round_trips()
    {
        foreach (bool accept in new[] { true, false })
        {
            var bytes = Write(new ClientPackets.FriendRequestReply { Name = "Alice", Accept = accept });
            var read = new ClientPackets.FriendRequestReply();
            Assert.Equal(bytes.Length, Read(read, bytes));
            Assert.Equal("Alice", read.Name);
            Assert.Equal(accept, read.Accept);
            Assert.Equal(1 + 5 + 1, bytes.Length);
        }

        var nul = Write(new ClientPackets.FriendRequestReply { Name = null, Accept = true });
        var readNull = new ClientPackets.FriendRequestReply();
        Assert.Equal(nul.Length, Read(readNull, nul));
        Assert.Equal("", readNull.Name);
        Assert.True(readNull.Accept);
    }

    [Fact]
    public void FriendUpdate_round_trips_the_extended_ClientFriend()
    {
        long seen = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Local).ToBinary();
        var sent = new ServerPackets.FriendUpdate();
        sent.Friends.Add(new ClientFriend
        {
            Index = 7, Name = "龙门剑客", Memo = "memo", Blocked = false, Online = true,
            Class = MirClass.Assassin, Level = ushort.MaxValue, Gender = MirGender.Female, GuildName = "Crystal", LastSeen = 0,
        });
        sent.Friends.Add(new ClientFriend
        {
            Index = int.MaxValue, Name = "Blocked", Memo = "", Blocked = true, Online = false,
            Class = MirClass.Taoist, Level = 1, Gender = MirGender.Male, GuildName = null, LastSeen = seen,
        });
        var bytes = Write(sent);

        var read = new ServerPackets.FriendUpdate();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(2, read.Friends.Count);

        var a = read.Friends[0];
        Assert.Equal(7, a.Index);
        Assert.Equal("龙门剑客", a.Name);
        Assert.Equal("memo", a.Memo);
        Assert.False(a.Blocked);
        Assert.True(a.Online);
        Assert.Equal(MirClass.Assassin, a.Class);
        Assert.Equal(ushort.MaxValue, a.Level);
        Assert.Equal(MirGender.Female, a.Gender);
        Assert.Equal("Crystal", a.GuildName);
        Assert.Equal(0, a.LastSeen);

        var b = read.Friends[1];
        Assert.Equal(int.MaxValue, b.Index);
        Assert.Equal("Blocked", b.Name);
        Assert.True(b.Blocked);
        Assert.False(b.Online);
        Assert.Equal(MirClass.Taoist, b.Class);
        Assert.Equal(1, b.Level);
        Assert.Equal(MirGender.Male, b.Gender);
        Assert.Equal("", b.GuildName); // null is written as ""
        Assert.Equal(seen, b.LastSeen);
        Assert.Equal(new DateTime(2026, 10, 16, 9, 30, 0), DateTime.FromBinary(b.LastSeen));
    }

    [Fact]
    public void ClientFriend_new_fields_follow_Online_in_fixed_order()
    {
        // One entry: int Index, string Name, string Memo, bool Blocked, bool Online,
        // then byte Class, ushort Level, byte Gender, string GuildName, long LastSeen (little-endian).
        var f = new ClientFriend
        {
            Index = 1, Name = "A", Memo = "", Blocked = false, Online = false,
            Class = MirClass.Wizard, Level = 0x0102, Gender = MirGender.Female, GuildName = "G", LastSeen = 0x0807060504030201,
        };
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) f.Save(w);
        var bytes = ms.ToArray();

        byte[] expected =
        {
            1, 0, 0, 0,             // Index
            1, (byte)'A',           // Name
            0,                      // Memo
            0,                      // Blocked
            0,                      // Online
            (byte)MirClass.Wizard,  // Class
            0x02, 0x01,             // Level
            (byte)MirGender.Female, // Gender
            1, (byte)'G',           // GuildName
            1, 2, 3, 4, 5, 6, 7, 8, // LastSeen
        };
        Assert.Equal(expected, bytes);

        using var rs = new MemoryStream(bytes);
        using var r = new BinaryReader(rs);
        var back = new ClientFriend(r);
        Assert.Equal(bytes.Length, rs.Position);
        Assert.Equal(MirClass.Wizard, back.Class);
        Assert.Equal(0x0102, back.Level);
        Assert.Equal(MirGender.Female, back.Gender);
        Assert.Equal("G", back.GuildName);
        Assert.Equal(f.LastSeen, back.LastSeen);
    }

    private static ClientFriend WorstCaseFriend(int i, Random random)
    {
        // 200-char memo (AddMemo's limit) of random CJK, 15-char CJK name, long CJK guild name: about 700 UTF-8 bytes
        static string Cjk(Random r, int n)
        {
            var c = new char[n];
            for (int k = 0; k < n; k++) c[k] = (char)r.Next(0x4E00, 0x9FA5);
            return new string(c);
        }

        return new ClientFriend
        {
            Index = i, Name = Cjk(random, 15), Memo = Cjk(random, 200), Blocked = false, Online = i % 2 == 0,
            Class = MirClass.Warrior, Level = ushort.MaxValue, Gender = MirGender.Female, GuildName = Cjk(random, 20), LastSeen = long.MaxValue,
        };
    }

    [Fact]
    public void FriendUpdate_is_compressed_and_the_default_worst_case_fits_one_frame()
    {
        Assert.True(new ServerPackets.FriendUpdate().Compressed);

        var random = new Random(2707);
        var p = new ServerPackets.FriendUpdate();
        for (int i = 0; i < 100; i++) p.Friends.Add(WorstCaseFriend(i, random)); // Settings.MaxFriends default

        Assert.True(Write(p).Length > ushort.MaxValue); // over the frame limit before gzip
        Assert.True(p.FrameLength() <= ServerPackets.FriendUpdate.MaxFrameLength);
        Assert.True(p.FitFrame());
        Assert.Equal(100, p.Friends.Count);
    }

    [Fact]
    public void FriendUpdate_FitFrame_bounds_the_largest_configured_list()
    {
        var random = new Random(1000);
        var p = new ServerPackets.FriendUpdate();
        for (int i = 0; i < 1000; i++) p.Friends.Add(WorstCaseFriend(i, random)); // Settings.MaxFriends clamp

        Assert.True(p.FrameLength() > ushort.MaxValue);
        Assert.False(p.FitFrame());
        Assert.True(p.FrameLength() <= ServerPackets.FriendUpdate.MaxFrameLength);
        Assert.NotEmpty(p.Friends);
        Assert.Equal(0, p.Friends[0].Index); // trimmed from the end, order kept
    }
}
