using Server.MirDatabase;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// The post-load bindings of system data, run by <see cref="Envir.LoadDB"/> after any store's
    /// <see cref="IWorldStore.LoadDesign"/>, under LoadLock (roadmap P7 phase 7.1). Moved out of the binary LoadDB unchanged.
    /// <para>Order today:</para>
    /// <list type="number">
    /// <item>Startup: <c>Settings.Load</c> reads RandomItemStats.ini and Mines.ini before any Envir loads, so
    /// <c>Settings.RandomItemStatsList</c> is ready here.</item>
    /// <item>Edit.LoadDB when SMain opens (or the web admin's world start), then Main.LoadDB in <c>StartEnvir</c>
    /// (BindGameShop binds to Edit's items, so Edit must load first). StartEnvir then does the drop, quest, recipe and
    /// dragon .txt loads; those stay there and run for every backend.</item>
    /// <item>Here: item random stats, default and legacy magic, game-shop binding, guild creation items.</item>
    /// </list>
    /// <para>Traps kept on purpose: <see cref="Envir.BindGameShop"/> always binds to <c>Envir.Edit.ItemInfoList</c>, even for
    /// Main (server.md §3.4 Known debt), and UpdateMagicInfo is gated on the static <c>Envir.LoadVersion</c>, so a non-binary
    /// store must set LoadVersion.</para>
    /// <para>When a load throws part-way nothing is bound (before the seam, the sections read so far were). Either way the
    /// envir is half-filled and unusable until a load succeeds (the web admin's EditReloadFailed blocks saves).</para>
    /// </summary>
    internal static class WorldBinder
    {
        public static void Bind(Envir e)
        {
            for (var i = 0; i < e.ItemInfoList.Count; i++)
            {
                var item = e.ItemInfoList[i];
                if (item != null && item.RandomStatsId < Settings.RandomItemStatsList.Count)
                    item.RandomStats = Settings.RandomItemStatsList[item.RandomStatsId];
            }

            e.FillMagicInfoList();
            if (Envir.LoadVersion <= 70)
                e.UpdateMagicInfo();

            // Files older than 63 have no game shop section and the list is left as it was, as before the seam.
            if (Envir.LoadVersion >= 63)
            {
                for (var i = e.GameShopList.Count - 1; i >= 0; i--)
                {
                    if (Envir.Main.BindGameShop(e.GameShopList[i])) continue;
                    e.GameShopList.RemoveAt(i);
                }
            }

            Settings.LinkGuildCreationItems(e.ItemInfoList);
        }
    }
}
