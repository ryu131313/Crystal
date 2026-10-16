using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Phase 2.2 S.ServerRules (2026-10-16). The generic round trip only fills empty arrays, so these pin the populated
/// AwakeChanceMax / AwakeMaterialRate / AwakeMaterials layout the web client decodes (WebClient/src/protocol/server.ts).
/// </summary>
public class ServerRulesPacketTests
{
    [Fact]
    public void Id_is_appended_after_MapAtmosphere_and_known_to_the_factory()
    {
        Assert.Equal(280, (int)ServerPacketIds.MapAtmosphere);
        Assert.Equal(281, (int)ServerPacketIds.ServerRules);
        Assert.IsType<ServerPackets.ServerRules>(GetServerPacket((short)ServerPacketIds.ServerRules));
        Assert.True(new ServerPackets.ServerRules().Observable);
    }

    [Fact]
    public void Empty_arrays_and_nulls_write_the_fixed_96_byte_layout()
    {
        var bytes = Write(new ServerPackets.ServerRules());
        // Refine 15 + "" 1, Awake 9 + three int32 counts 12, Mentor 6, Marriage 17, Mail 15, GT 12, Gem 1, Account 2, Features 6.
        Assert.Equal(96, bytes.Length);

        var nulls = new ServerPackets.ServerRules
        {
            RefineOreName = null,
            AwakeChanceMax = null,
            AwakeMaterialRate = null,
            AwakeMaterials = null,
        };
        Assert.Equal(bytes, Write(nulls));

        var read = new ServerPackets.ServerRules();
        Assert.Equal(bytes.Length, Read(read, bytes));
        Assert.Equal(string.Empty, read.RefineOreName);
        Assert.Empty(read.AwakeChanceMax);
        Assert.Empty(read.AwakeMaterialRate);
        Assert.Empty(read.AwakeMaterials);
    }

    [Fact]
    public void Populated_rules_round_trip()
    {
        var sent = new ServerPackets.ServerRules
        {
            OnlyRefineWeapon = true, RefineBaseChance = 80, RefineTime = 20, RefineIncrease = 1, RefineCritChance = 10,
            RefineCritIncrease = 2, RefineWepStatReduce = 6, RefineItemStatReduce = 15, RefineCost = 125, RefineOreName = "黑铁矿石",
            AwakeSuccessRate = 70, AwakeHitRate = 70, MaxAwakeLevel = 5, Awake_WeaponRate = 1, Awake_HelmetRate = 1, Awake_ArmorRate = 5,
            AwakeChanceMax = new byte[] { 1, 2, 3, 4, 5 },
            AwakeMaterialRate = new[] { 1f, 1.5f, 2f, 0.25f, 3f },
            MentorLevelGap = 10, MentorSkillBoost = true, MentorLength = 7, MentorDamageBoost = 10, MentorExpBoost = 10, MenteeExpBank = 1,
            LoverEXPBonus = 5, MarriageCooldown = 7, WeddingRingRecall = true, MarriageLevelRequired = 11, ReplaceWedRingCost = 125,
            MailAutoSendGold = true, MailAutoSendItems = false, MailFreeWithStamp = true,
            MailCostPer1KGold = 100, MailItemInsurancePercentage = 5, MailCapacity = uint.MaxValue,
            BuyGTGold = 1000000, ExtendGT = 500000, GTDays = 7,
            GemStatIndependent = true, AllowCreateAssassin = true, AllowCreateArcher = false,
            MapDetailsEnabled = true, FriendRequestsEnabled = false, MaxFriends = 100,
        };
        sent.AwakeMaterials.Add(new[] { new List<byte> { 1, 2, 3, 4, 5 }, new List<byte> { 5, 4, 3, 2, 1 } });
        sent.AwakeMaterials.Add(new[] { new List<byte>(), null });

        var bytes = Write(sent);
        var read = new ServerPackets.ServerRules();
        Assert.Equal(bytes.Length, Read(read, bytes));

        Assert.True(read.OnlyRefineWeapon);
        Assert.Equal(80, read.RefineBaseChance);
        Assert.Equal(20, read.RefineTime);
        Assert.Equal(15, read.RefineItemStatReduce);
        Assert.Equal(125, read.RefineCost);
        Assert.Equal("黑铁矿石", read.RefineOreName);
        Assert.Equal(5, read.MaxAwakeLevel);
        Assert.Equal(5, read.Awake_ArmorRate);
        Assert.Equal(sent.AwakeChanceMax, read.AwakeChanceMax);
        Assert.Equal(sent.AwakeMaterialRate, read.AwakeMaterialRate);
        Assert.Equal(2, read.AwakeMaterials.Count);
        Assert.Equal(sent.AwakeMaterials[0][0], read.AwakeMaterials[0][0]);
        Assert.Equal(sent.AwakeMaterials[0][1], read.AwakeMaterials[0][1]);
        Assert.Equal(2, read.AwakeMaterials[1].Length);
        Assert.Empty(read.AwakeMaterials[1][0]);
        Assert.Empty(read.AwakeMaterials[1][1]); // a null list is written as count 0
        Assert.Equal(7, read.MentorLength);
        Assert.Equal(1, read.MenteeExpBank);
        Assert.True(read.WeddingRingRecall);
        Assert.Equal(11, read.MarriageLevelRequired);
        Assert.Equal(125, read.ReplaceWedRingCost);
        Assert.True(read.MailFreeWithStamp);
        Assert.False(read.MailAutoSendItems);
        Assert.Equal(uint.MaxValue, read.MailCapacity);
        Assert.Equal(1000000, read.BuyGTGold);
        Assert.Equal(7, read.GTDays);
        Assert.True(read.GemStatIndependent);
        Assert.True(read.AllowCreateAssassin);
        Assert.False(read.AllowCreateArcher);
        Assert.True(read.MapDetailsEnabled);
        Assert.False(read.FriendRequestsEnabled);
        Assert.Equal(100, read.MaxFriends);

        // Re-encoding the decoded packet gives the same bytes (null list -> empty list).
        Assert.Equal(bytes, Write(read));
    }
}
