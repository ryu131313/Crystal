using System.Drawing;
using System.Reflection;
using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// LiveDb push packet (S.GameDataChanged, Batch B): every section populated round-trips byte-identically, the factory knows it, it is
/// private, and ItemInfo.CopyFrom (the client's in-place update) copies every field.
/// </summary>
public class GameDataChangedTests
{
    private static ItemInfo Item(int index)
    {
        var item = new ItemInfo
        {
            Index = index, Name = "Sword" + index, Type = ItemType.Weapon, Grade = ItemGrade.Rare, Shape = 3, Weight = 9, Light = 2,
            Image = 44, Durability = 100, Price = 1234, StackSize = 1, Effect = 1, NeedIdentify = true, GlobalDropNotify = true,
            Bind = BindMode.DontDrop, Unique = SpecialItemMode.Paralize, RandomStatsId = 255, CanFastRun = true, Slots = 2, ToolTip = "tip",
        };
        item.Stats[Stat.MaxDC] = 7;
        item.Stats[Stat.HP] = 20;
        return item;
    }

    private static ServerPackets.GameDataChanged Populated()
    {
        var monster = new ClientMonsterInfo { Index = 3, Name = "Hen", GameName = "Hen", Image = Monster.Hen, Level = 2, AttackSpeed = 2500 };
        monster.Stats[Stat.HP] = 15;
        var quest = new ClientQuestInfo { Index = 4, Name = "Q", Group = "G", Description = { "d" }, TaskDescription = { "t" } };
        quest.RewardsFixedItem.Add(new QuestItemReward { Item = Item(9), Count = 1 });

        return new ServerPackets.GameDataChanged
        {
            Items = { Item(1), Item(2) },
            Monsters = { monster },
            Npcs = { new ClientNPCInfo { Index = 5, Name = "Npc", FileName = "f", MapIndex = 1, Location = new Point(3, 4), Image = 7, CanTeleportTo = true } },
            Quests = { quest },
            Magics = { new ClientMagic { Name = "Fencing", Spell = Spell.Fencing, Level1 = 7, Need1 = 100, Range = 1, Delay = 500 } },
            HeroMagics = { new ClientMagic { Name = "Slaying", Spell = Spell.Slaying, Level1 = 9 } },
            GameShop = { new ServerPackets.GameDataChanged.ShopEntry { Item = new GameShopItem { GIndex = 6, ItemIndex = 1, Info = Item(1), GoldPrice = 5, Class = "All", Category = "Weapon", Date = new DateTime(2026, 10, 10) }, StockLevel = 3 } },
            GameShopRemoved = { 8, 9 },
            MapInfoInvalidated = { 1, 2 },
            Last = true,
        };
    }

    [Fact]
    public void Every_section_round_trips_byte_identically()
    {
        var p1 = Populated();
        var b1 = Write(p1);
        var p2 = new ServerPackets.GameDataChanged();
        Assert.Equal(b1.Length, Read(p2, b1));
        Assert.Equal(b1, Write(p2));

        Assert.Equal(2, p2.Items.Count);
        Assert.Equal(7, p2.Items[0].Stats[Stat.MaxDC]);
        Assert.Equal(Monster.Hen, p2.Monsters.Single().Image);
        Assert.Equal(new Point(3, 4), p2.Npcs.Single().Location);
        Assert.Equal("Q", p2.Quests.Single().Name);
        Assert.Equal(Spell.Fencing, p2.Magics.Single().Spell);
        Assert.Equal(Spell.Slaying, p2.HeroMagics.Single().Spell);
        Assert.Equal(3, p2.GameShop.Single().StockLevel);
        Assert.Equal("Sword1", p2.GameShop.Single().Item.Info.Name);
        Assert.Equal(new[] { 8, 9 }, p2.GameShopRemoved);
        Assert.Equal(new[] { 1, 2 }, p2.MapInfoInvalidated);
        Assert.True(p2.Last);
    }

    [Fact]
    public void Empty_packet_is_the_fixed_overhead()
    {
        var p = new ServerPackets.GameDataChanged();
        Assert.Equal(ServerPackets.GameDataChanged.FixedOverhead, ((byte[])p.GetPacketBytes()).Length);
    }

    [Fact]
    public void Is_private_and_known_to_the_factory()
    {
        var p = GetServerPacket((short)ServerPacketIds.GameDataChanged);
        Assert.IsType<ServerPackets.GameDataChanged>(p);
        Assert.False(p.Observable);
        Assert.Equal(ServerPacketIds.GameDataChanged, Enum.GetValues<ServerPacketIds>().Max()); // appended at the end
    }

    [Fact]
    public void Oversized_section_count_is_rejected()
    {
        var bytes = BitConverter.GetBytes(0x7FFFFFF0);
        Assert.Throws<InvalidDataException>(() => Read(new ServerPackets.GameDataChanged(), bytes));
    }

    [Fact]
    public void ItemInfo_CopyFrom_copies_every_field_and_keeps_its_own_Stats()
    {
        var source = Item(1);
        source.RandomStats = new RandomItemStat();
        var target = new ItemInfo { Index = 1, Name = "Old" };
        var targetBefore = target;

        target.CopyFrom(source);

        Assert.Same(targetBefore, target);
        foreach (var f in typeof(ItemInfo).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (f.FieldType == typeof(Stats)) continue;
            Assert.True(Equals(f.GetValue(source), f.GetValue(target)), "ItemInfo." + f.Name + " was not copied by CopyFrom");
        }

        Assert.NotSame(source.Stats, target.Stats);
        Assert.Equal(source.Stats.Values, target.Stats.Values);
        Assert.Equal(Write(new ServerPackets.NewItemInfo { Info = source }), Write(new ServerPackets.NewItemInfo { Info = target }));
    }
}
