using Server.MirDatabase;
using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Roadmap P7 phase 7.1: the post-load bindings moved out of the binary LoadDB into WorldBinder. The fixture oracles prove
/// the loaded state is unchanged; these pin the two bindings the fixtures don't exercise.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class WorldBinderTests
{
    [Fact]
    public void Unbound_game_shop_items_are_dropped_and_the_rest_keep_their_order()
    {
        var dir = NewTempFolder("p7-binder-shop");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            var now = Envir.Main.GameShopList[0].Date;
            Envir.Main.GameShopList.Add(new GameShopItem { GIndex = 2, ItemIndex = 99999, Class = "All", Category = "Lost", Date = now });
            Envir.Main.GameShopList.Add(new GameShopItem { GIndex = 3, ItemIndex = 1, Class = "All", Category = "Weapons", Date = now });
            SaveAll();

            Assert.True(LoadAll());

            foreach (var e in new[] { Envir.Edit, Envir.Main })
            {
                Assert.Equal(new[] { 1, 3 }, e.GameShopList.Select(x => x.GIndex).ToArray());
                Assert.All(e.GameShopList, x => Assert.Same(Envir.Edit.ItemInfoList.Single(i => i.Index == x.ItemIndex), x.Info));
            }
            return 0;
        });
    }

    [Fact]
    public void Missing_default_spells_are_filled_after_load()
    {
        var dir = NewTempFolder("p7-binder-magic");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            Envir.Main.MagicInfoList.Clear();
            SaveAll();

            Envir.Edit.MagicInfoList.Clear();
            Assert.True(Envir.Edit.LoadDB());

            Assert.Contains(Envir.Edit.MagicInfoList, m => m.Spell == Spell.Fencing);
            Assert.Equal(Envir.Edit.MagicInfoList.Count, Envir.Edit.MagicInfoList.Select(m => m.Spell).Distinct().Count());
            return 0;
        });
    }
}
