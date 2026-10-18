using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 2 area "core windows" (worker C): inventory (+ sort, delete bin, unlock row), character (+ hero tab, stats panel),
    /// storage (+ sort, password session), inspect (+ profile), skills (incl. hero tab, auto-cast), options (theme, scale,
    /// opacity, plates, interface style, game settings), keybinds, help. Windows live in Client\MirScenes\Modern\Windows\.
    /// </summary>
    public sealed partial class ModernUi
    {
        /// <summary>The NPC storage session (storage.ts StorageSession): the server may open the storage before the window exists.</summary>
        internal StorageSession Storage { get; private set; }

        partial void RegisterCoreWindows()
        {
            Storage?.Dispose();
            Storage = new StorageSession();
            StorageSession session = Storage;
            Root.Wm.Register(InventoryWindow.WindowKey, () => new InventoryWindow());
            Root.Wm.Register(CharacterWindow.WindowKey, () => new CharacterWindow());
            Root.Wm.Register(StorageWindow.WindowKey, () => new StorageWindow(session));
            Root.Wm.Register(InspectWindow.WindowKey, () => new InspectWindow());
            Root.Wm.Register(SkillsWindow.WindowKey, () => new SkillsWindow());
            Root.Wm.Register(OptionsWindow.WindowKey, () => new OptionsWindow());
            Root.Wm.Register(KeybindsWindow.WindowKey, () => new KeybindsWindow());
            Root.Wm.Register(HelpWindow.WindowKey, () => new HelpWindow());
        }

        /// <summary>The window toggles (MENU_WINDOWS defaults): the classic GameScene_KeyDown cases for these binds.</summary>
        private partial bool WindowKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Inventory:
                case KeybindOptions.Inventory2:
                    Root.Wm.Toggle(InventoryWindow.WindowKey);
                    return true;
                case KeybindOptions.Equipment:
                case KeybindOptions.Equipment2:
                    Root.Wm.Toggle(CharacterWindow.WindowKey);
                    return true;
                case KeybindOptions.Skills:
                case KeybindOptions.Skills2:
                    Root.Wm.Toggle(SkillsWindow.WindowKey);
                    return true;
                case KeybindOptions.Options:
                case KeybindOptions.Options2:
                    Root.Wm.Toggle(OptionsWindow.WindowKey);
                    return true;
                case KeybindOptions.Keybind:
                    Root.Wm.Toggle(KeybindsWindow.WindowKey);
                    return true;
                case KeybindOptions.Help:
                    Root.Wm.Toggle(HelpWindow.WindowKey);
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The click hook ItemMoves.BeginClick asks first (classic: InventoryDialog delete mode); true = consumed. Here: the
        /// inventory's delete mode, then a click on a bag cell of the storage window's side column (deposit).
        /// </summary>
        public bool ItemDeleteModeClick(ItemSlotRef cell, MouseButtons button)
        {
            if (Root == null || Root.IsDisposed || cell == null) return false;
            if (Root.Wm.IsOpen(InventoryWindow.WindowKey) && Root.Wm.Get(InventoryWindow.WindowKey) is InventoryWindow inv && inv.DeleteModeClick(cell, button)) return true;
            if (Root.Wm.IsOpen(StorageWindow.WindowKey) && Root.Wm.Get(StorageWindow.WindowKey) is StorageWindow store && store.SideClick(cell, button)) return true;
            return false;
        }

        /// <summary>S.PlayerInspect (GameScene.PlayerInspect → InspectDialog.Show): the inspect window shows the new player.</summary>
        public void PlayerInspected(bool isHero)
        {
            if (Root == null || Root.IsDisposed) return;
            if (Root.Wm.Get(InspectWindow.WindowKey) is InspectWindow w)
            {
                w.Received(isHero);
                w.Open();
            }
        }

        /// <summary>S.NPCStorage (classic: only while the NPC dialog is open; the modern NPC window is a later phase).</summary>
        public void NpcStorage() => Storage?.Opened();

        public void StorageUnlockResult(S.StorageUnlockResult p) => Storage?.UnlockResult(p);

        public void StoragePasswordResult(S.StoragePasswordResult p) => Storage?.PasswordResult(p);
    }
}
