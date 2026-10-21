namespace StressClient.Tests;

public class WorldModelItemTests
{
    private static ItemInfo Info(int index, ItemType type, uint price = 10, int hp = 0, int maxDc = 0)
    {
        var info = new ItemInfo { Index = index, Name = "item" + index, Type = type, Price = price, StackSize = 1 };
        info.Stats[Stat.HP] = hp;
        info.Stats[Stat.MaxDC] = maxDc;
        return info;
    }

    private static UserItem Item(ItemInfo info, ulong id, ushort count = 1)
    {
        var item = new UserItem(info) { UniqueID = id, Count = count };
        return item;
    }

    private static WorldModel World(params ItemInfo[] infos)
    {
        var world = new WorldModel();
        foreach (var info in infos) world.ItemInfos[info.Index] = info;
        world.SetItems(new UserItem[10], new UserItem[14]);
        return world;
    }

    [Fact]
    public void AddItem_FillsFirstFreeSlot()
    {
        var world = World(Info(1, ItemType.Ore));
        var item = Item(world.ItemInfos[1], 7);

        Assert.True(world.AddItem(item));
        Assert.Same(item, world.Inventory[0]);
        Assert.Same(item.Info, world.Inventory[0].Info);
        Assert.Equal(9, world.FreeSlots);
    }

    [Fact]
    public void AddItem_FailsWhenBagIsFull()
    {
        var world = World(Info(1, ItemType.Ore));
        for (int i = 0; i < world.Inventory.Length; i++) world.Inventory[i] = Item(world.ItemInfos[1], (ulong)i);

        Assert.False(world.AddItem(Item(world.ItemInfos[1], 99)));
        Assert.True(world.BagFull);
    }

    [Fact]
    public void RemoveItem_DecrementsStackOrClearsSlot()
    {
        var world = World(Info(1, ItemType.Potion));
        world.AddItem(Item(world.ItemInfos[1], 5, count: 3));

        Assert.True(world.RemoveItem(5, 1));
        Assert.Equal(2, world.Inventory[0].Count);

        Assert.True(world.RemoveItem(5, 2));
        Assert.Null(world.Inventory[0]);
    }

    [Fact]
    public void ApplyEquip_SwapsTheOldItemBackIntoTheBag()
    {
        var world = World(Info(1, ItemType.Weapon, maxDc: 5));
        var first = Item(world.ItemInfos[1], 1);
        var second = Item(world.ItemInfos[1], 2);
        world.AddItem(first);
        world.AddItem(second);

        Assert.True(world.ApplyEquip(1, (int)EquipmentSlot.Weapon));
        Assert.Same(first, world.Equipped(EquipmentSlot.Weapon));

        Assert.True(world.ApplyEquip(2, (int)EquipmentSlot.Weapon));
        Assert.Same(second, world.Equipped(EquipmentSlot.Weapon));
        Assert.Contains(first, world.Inventory);
    }

    [Fact]
    public void HasPotion_FindsOnlyHpPotions()
    {
        var world = World(Info(1, ItemType.Ore), Info(2, ItemType.Potion), Info(3, ItemType.Potion, hp: 30));
        world.AddItem(Item(world.ItemInfos[1], 1));
        world.AddItem(Item(world.ItemInfos[2], 2));
        world.AddItem(Item(world.ItemInfos[3], 3));

        Assert.True(world.HasPotion(out var potion));
        Assert.Equal(3UL, potion.UniqueID);
    }

    [Fact]
    public void PotionCount_SumsStacks()
    {
        var world = World(Info(1, ItemType.Potion, hp: 30));
        world.AddItem(Item(world.ItemInfos[1], 1, count: 3));
        world.AddItem(Item(world.ItemInfos[1], 2, count: 2));

        Assert.Equal(5, world.PotionCount());
    }

    [Fact]
    public void FirstJunk_NeverSellsTheEquippedWeapon()
    {
        var weak = Info(1, ItemType.Weapon, maxDc: 2);
        var strong = Info(2, ItemType.Weapon, maxDc: 9);
        var ore = Info(3, ItemType.Ore);
        var world = World(weak, strong, ore);

        world.AddItem(Item(weak, 1));
        world.AddItem(Item(strong, 2));
        world.AddItem(Item(ore, 3));
        Assert.True(world.ApplyEquip(2, (int)EquipmentSlot.Weapon));

        var junk = world.FirstJunk();
        Assert.NotNull(junk);
        Assert.NotEqual(2, junk.ItemIndex); // the worn weapon stays
    }

    [Fact]
    public void GearScore_RanksWeaponPower()
    {
        var weak = Item(Info(1, ItemType.Weapon, maxDc: 2), 1);
        var strong = Item(Info(2, ItemType.Weapon, maxDc: 9), 2);

        Assert.True(WorldModel.GearScore(strong) > WorldModel.GearScore(weak));
        Assert.Equal(0, WorldModel.GearScore(null));
    }
}
