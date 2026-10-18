using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;
using Client.MirScenes.Modern.Windows;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 2 area "chat / map" (worker B): the chat HUD (tabs, name menu, filters, input, announcement ticker), the minimap, the
    /// quest tracker and the big map window. Components live in Client\MirScenes\Modern\Hud\ (Chat*, Minimap*, Tracker*) and
    /// Client\MirScenes\Modern\Windows\ (BigMap*). <see cref="MapData"/> holds the map records both maps read; it is built with
    /// the window catalog, before the HUD, so the world map setup sent after login is kept.
    /// </summary>
    public sealed partial class ModernUi
    {
        /// <summary>The chat box (null before Build).</summary>
        public ChatHud Chat { get; private set; }
        public MinimapHud Minimap { get; private set; }
        public TrackerHud Tracker { get; private set; }
        /// <summary>Map records (S.NewMapInfo), the world map setup and the known-maps list for the minimap and the big map.</summary>
        public BigMapData MapData { get; private set; }

        partial void BuildChatHud()
        {
            Tracker = new TrackerHud(MapData);
            Minimap = new MinimapHud(MapData);
            Chat = new ChatHud();
        }

        partial void RegisterChatWindows()
        {
            MapData?.Dispose();
            MapData = new BigMapData();
            Root.Wm.Register(BigMapWindow.WindowKey, () => new BigMapWindow(MapData));
        }

        private partial bool ChatKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Minimap:
                    Minimap?.Toggle();
                    return true;
                case KeybindOptions.Bigmap:
                    Root.Wm.Toggle(BigMapWindow.WindowKey);
                    return true;
            }
            return false;
        }

        /// <summary>Shift + right-click on an item cell: link the item into the chat input.</summary>
        public void ItemChatLink(ItemSlotRef cell) => Chat?.LinkItem(cell);
    }
}
