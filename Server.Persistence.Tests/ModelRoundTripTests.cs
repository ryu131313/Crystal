using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>
/// Roadmap P1 background: shared models that serialise to BOTH the server DB and the wire. Each must re-save identically
/// when read back at the current DB version AND with the wire default (version = int.MaxValue), so a field added to one
/// side only shows up here first.
/// </summary>
public class ModelRoundTripTests
{
    private static byte[] Bytes(Action<BinaryWriter> save)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) save(w);
        return ms.ToArray();
    }

    private static BinaryReader Reader(byte[] b) => new BinaryReader(new MemoryStream(b));

    private static ItemInfo NewItemInfo()
    {
        var info = new ItemInfo { Index = 7, Name = "Test Sword", Type = ItemType.Weapon, Shape = 3, Durability = 5000, Price = 1234, StackSize = 1, ToolTip = "tip", Image = 42 };
        info.Stats[Stat.MinDC] = 2; info.Stats[Stat.MaxDC] = 9; info.Stats[Stat.Accuracy] = 1;
        return info;
    }

    [Fact]
    public void Stats_round_trip_db_and_wire()
    {
        var s = new Stats(); s[Stat.MinAC] = 1; s[Stat.MaxAC] = 4; s[Stat.HP] = 100; s[Stat.CriticalRate] = 3;
        var b = Bytes(s.Save);
        Assert.Equal(b, Bytes(new Stats(Reader(b), Envir.Version, Envir.CustomVersion).Save));
        Assert.Equal(b, Bytes(new Stats(Reader(b)).Save));
    }

    [Fact]
    public void ItemInfo_round_trip_db_and_wire()
    {
        var b = Bytes(NewItemInfo().Save);
        Assert.Equal(b, Bytes(new ItemInfo(Reader(b), Envir.Version, Envir.CustomVersion).Save));
        Assert.Equal(b, Bytes(new ItemInfo(Reader(b)).Save));
    }

    [Fact]
    public void UserItem_round_trip_db_and_wire()
    {
        var info = NewItemInfo();
        var item = new UserItem(info) { UniqueID = 99, ItemIndex = info.Index, Count = 1, CurrentDura = 1000, MaxDura = 5000, RefinedValue = RefinedValue.DC, RefineAdded = 2 };
        item.AddedStats[Stat.MaxDC] = 3;
        var b = Bytes(item.Save);
        Assert.Equal(b, Bytes(new UserItem(Reader(b), Envir.Version, Envir.CustomVersion).Save));
        Assert.Equal(b, Bytes(new UserItem(Reader(b)).Save));
    }

    [Fact]
    public void GameShopItem_round_trip_db_and_packet_layouts()
    {
        var g = new GameShopItem { GIndex = 5, ItemIndex = 7, Info = NewItemInfo(), GoldPrice = 100, CreditPrice = 2, Count = 3, Class = "All", Category = "Weapons", Stock = 10, iStock = true, Date = new DateTime(2030, 1, 2), CanBuyGold = true };
        var db = Bytes(w => g.Save(w));
        Assert.Equal(db, Bytes(w => new GameShopItem(Reader(db), Envir.Version, Envir.CustomVersion).Save(w)));

        var wire = Bytes(w => g.Save(w, true)); // GameShopItem has a separate packet layout: test it on its own ctor
        var back = new GameShopItem(Reader(wire), true);
        Assert.Equal(wire, Bytes(w => back.Save(w, true)));
    }
}
