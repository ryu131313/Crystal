using Client.MirControls;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 2 area "core windows" steps (modern only): inventory (search, chips, unlock row + wave, sort, gold, delete bin),
    /// character (+ stats panel, hero tab), storage (lock screen, password unlock, pages, side deposit, sort), inspect (tabs,
    /// profile), skills (key assignment), options (pages, a toggle, search), keybinds (rebind + reset), help (topics, pages,
    /// shortcuts, search), and every item route of WebClient core/dragRoutes.ts these windows offer, through ModernItemCell clicks
    /// and the server replies: bag ↔ bag, bag ↔ belt, bag ↔ equipment, bag ↔ storage, storage ↔ storage, storage ↔ equipment,
    /// bag ↔ hero bag, hero bag ↔ hero equipment, hero bag ↔ hero bag, bag → ground. Starts from a fresh game (EnterGame) so the
    /// slots are known, and closes everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddWindowSteps(List<Step> steps)
        {
            WStep(steps, "win_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            WStep(steps, "win_enter", () => { EnterGame(); SetScale(1f, false); }, 1500);

            // ---- inventory: open, search, chips, bag routes
            WStep(steps, "inv_open", () => PressBind(KeybindOptions.Inventory));
            WStep(steps, "inv_search_potion", () => SetSearch(Win<InventoryWindow>(InventoryWindow.WindowKey), "potion"), 700);
            WStep(steps, "inv_chip_gear", () => { SetSearch(Win<InventoryWindow>(InventoryWindow.WindowKey), ""); ClickAt(ChipCentre(Find<Chips<string>>(Win<InventoryWindow>(InventoryWindow.WindowKey)), 0)); });
            WStep(steps, "inv_chip_clear", () => ClickAt(ChipCentre(Find<Chips<string>>(Win<InventoryWindow>(InventoryWindow.WindowKey)), 0)));
            WStep(steps, "route_bag_bag", () => { ClickCell(MirGridType.Inventory, 8, Inv); ClickCell(MirGridType.Inventory, 15, Inv); });
            WStep(steps, "route_bag_bag_reply", () => Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 8, To = 15, Success = true }));
            WStep(steps, "route_bag_belt", () => { ClickCell(MirGridType.Inventory, 15, Inv); ClickCell(MirGridType.Inventory, 1, Hud); });
            WStep(steps, "route_bag_belt_reply", () => Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 15, To = 1, Success = true }));
            WStep(steps, "route_belt_bag", () => { ClickCell(MirGridType.Inventory, 1, Hud); ClickCell(MirGridType.Inventory, 16, Inv); });
            WStep(steps, "route_belt_bag_reply", () => Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 1, To = 16, Success = true }));
            WStep(steps, "inv_quest_tab", () => ClickTabOf(Win<InventoryWindow>(InventoryWindow.WindowKey), 1));
            WStep(steps, "inv_bag_tab", () => ClickTabOf(Win<InventoryWindow>(InventoryWindow.WindowKey), 0));

            // ---- character: equip / unequip, stats panel
            WStep(steps, "char_open", () => PressBind(KeybindOptions.Equipment), 800);
            WStep(steps, "route_bag_equip", () => { ClickCell(MirGridType.Inventory, 9, Inv); ClickCell(MirGridType.Equipment, (int)EquipmentSlot.Weapon, Chr); });
            WStep(steps, "route_bag_equip_reply", () => Feed(new S.EquipItem { Grid = MirGridType.Inventory, UniqueID = UidAt(MirGridType.Inventory, 9), To = (int)EquipmentSlot.Weapon, Success = true }), 800);
            WStep(steps, "route_equip_bag", () => { ClickCell(MirGridType.Equipment, (int)EquipmentSlot.Armour, Chr); ClickCell(MirGridType.Inventory, 20, Inv); });
            WStep(steps, "route_equip_bag_reply", () => Feed(new S.RemoveItem { Grid = MirGridType.Inventory, UniqueID = UidAt(MirGridType.Equipment, (int)EquipmentSlot.Armour), To = 20, Success = true }), 800);
            WStep(steps, "route_bag_equip_armour", () => { ClickCell(MirGridType.Inventory, 20, Inv); ClickCell(MirGridType.Equipment, (int)EquipmentSlot.Armour, Chr); });
            WStep(steps, "route_bag_equip_armour_reply", () => Feed(new S.EquipItem { Grid = MirGridType.Inventory, UniqueID = UidAt(MirGridType.Inventory, 20), To = (int)EquipmentSlot.Armour, Success = true }), 800);
            WStep(steps, "char_stats_open", () => ClickAt(Centre(FindByHint(Win<CharacterWindow>(CharacterWindow.WindowKey), W(ClientTextKeys.ModernWinStats)))), 800);
            WStep(steps, "char_stats_close", () => ClickAt(Centre(FindByHint(Win<CharacterWindow>(CharacterWindow.WindowKey), W(ClientTextKeys.ModernWinStats)))));

            // ---- storage: lock screen, unlock, routes, side deposit, pages, sort
            WStep(steps, "storage_lock_screen", () =>
            {
                PressBind(KeybindOptions.Equipment); // close the character window
                GameScene.User.RequireStoragePassword = true;
                GameScene.User.HasStoragePassword = true;
                UserItem[] storage = new UserItem[80];
                storage[0] = Item("UiTestRing");
                storage[1] = Item("UiTestPotion", 10);
                storage[3] = Item("UiTestScroll", 2);
                Feed(new S.UserStorage { Storage = storage });
                Feed(new S.NPCStorage());
            }, 800);
            WStep(steps, "storage_bad_password", () => { var w = Win<StorageWindow>(StorageWindow.WindowKey); if (w?.LockField != null) { w.LockField.Text = "ab"; ClickAt(Centre(w.LockButton)); } });
            WStep(steps, "storage_unlock", () => { var w = Win<StorageWindow>(StorageWindow.WindowKey); if (w?.LockField != null) { w.LockField.Text = "secret1"; ClickAt(Centre(w.LockButton)); } });
            WStep(steps, "storage_unlock_reply", () => Feed(new S.StorageUnlockResult { Result = 0, HasPassword = true }), 900);
            WStep(steps, "route_bag_storage", () => { ClickCell(MirGridType.Inventory, 10, Inv); ClickCell(MirGridType.Storage, 5, Sto); });
            WStep(steps, "route_bag_storage_reply", () => Feed(new S.StoreItem { From = 10, To = 5, Success = true }));
            WStep(steps, "route_storage_bag", () => { ClickCell(MirGridType.Storage, 0, Sto); ClickCell(MirGridType.Inventory, 21, Inv); });
            WStep(steps, "route_storage_bag_reply", () => Feed(new S.TakeBackItem { From = 0, To = 21, Success = true }));
            WStep(steps, "route_storage_storage", () => { ClickCell(MirGridType.Storage, 3, Sto); ClickCell(MirGridType.Storage, 12, Sto); });
            WStep(steps, "route_storage_storage_reply", () => Feed(new S.MoveItem { Grid = MirGridType.Storage, From = 3, To = 12, Success = true }));
            WStep(steps, "storage_side_deposit", () => { MergeFrom = UidAt(MirGridType.Inventory, 11); MergeTo = UidAt(MirGridType.Storage, 12); ClickCell(MirGridType.Inventory, 11, Sto, true); });
            WStep(steps, "storage_side_deposit_reply", () => Feed(new S.MergeItem { GridFrom = MirGridType.Inventory, GridTo = MirGridType.Storage, IDFrom = MergeFrom, IDTo = MergeTo, Success = true }));
            WStep(steps, "storage_char_open", () => PressBind(KeybindOptions.Equipment), 800);
            WStep(steps, "route_storage_equip", () => { ClickCell(MirGridType.Storage, 5, Sto); ClickCell(MirGridType.Equipment, (int)EquipmentSlot.RingL, Chr); });
            WStep(steps, "route_storage_equip_reply", () => Feed(new S.EquipItem { Grid = MirGridType.Storage, UniqueID = UidAt(MirGridType.Storage, 5), To = (int)EquipmentSlot.RingL, Success = true }), 800);
            WStep(steps, "route_equip_storage", () => { ClickCell(MirGridType.Equipment, (int)EquipmentSlot.RingL, Chr); ClickCell(MirGridType.Storage, 7, Sto); });
            WStep(steps, "route_equip_storage_reply", () => Feed(new S.RemoveItem { Grid = MirGridType.Storage, UniqueID = UidAt(MirGridType.Equipment, (int)EquipmentSlot.RingL), To = 7, Success = true }), 800);
            WStep(steps, "storage_search_chip", () =>
            {
                PressBind(KeybindOptions.Equipment);
                var w = Win<StorageWindow>(StorageWindow.WindowKey);
                SetSearch(w, "ring");
            }, 700);
            WStep(steps, "storage_page2", () => { SetSearch(Win<StorageWindow>(StorageWindow.WindowKey), ""); ClickAt(PageButton(1)); });
            WStep(steps, "storage_page1_sort", () => { ClickAt(PageButton(0)); ClickAt(Centre(FindButton(Win<StorageWindow>(StorageWindow.WindowKey), W(ClientTextKeys.ModernWinSort)))); });
            WStep(steps, "storage_sort_reply", () => Feed(new S.UserStorage { Storage = SortedStorage() }));
            WStep(steps, "storage_close", () => Key(Keys.Escape));

            // ---- hero: bag ↔ hero bag, hero bag ↔ hero equipment, hero bag ↔ hero bag
            WStep(steps, "hero_info", () =>
            {
                UserItem[] inv = new UserItem[10];
                inv[2] = Item("UiTestPotion", 3);
                inv[3] = Item("UiTestRing");
                Feed(new S.HeroInformation
                {
                    ObjectID = 77, RealId = 77, Name = "UiHero", NameColour = Color.White, Class = MirClass.Taoist, Gender = MirGender.Female, Level = 22,
                    Location = GameScene.User.CurrentLocation, Direction = MirDirection.Down, HP = 120, MP = 80, Experience = 5, MaxExperience = 500,
                    Inventory = inv, Equipment = new UserItem[Enum.GetNames(typeof(EquipmentSlot)).Length], QuestInventory = new UserItem[40],
                });
                Feed(new S.UpdateHeroSpawnState { State = HeroSpawnState.Summoned });
                PressBind(KeybindOptions.Equipment);
            }, 900);
            WStep(steps, "hero_tab", () => ClickTabOf(Win<CharacterWindow>(CharacterWindow.WindowKey), 1), 900);
            WStep(steps, "route_bag_hero", () => { ClickCell(MirGridType.Inventory, 12, Inv); ClickCell(MirGridType.HeroInventory, 5, Chr); });
            WStep(steps, "route_bag_hero_reply", () => Feed(new S.TransferHeroItem { From = 12, To = 5, Success = true }));
            WStep(steps, "route_hero_bag", () => { ClickCell(MirGridType.HeroInventory, 2, Chr); ClickCell(MirGridType.Inventory, 22, Inv); });
            WStep(steps, "route_hero_bag_reply", () => Feed(new S.TakeBackHeroItem { From = 2, To = 22, Success = true }));
            WStep(steps, "route_hero_hero", () => { ClickCell(MirGridType.HeroInventory, 5, Chr); ClickCell(MirGridType.HeroInventory, 7, Chr); });
            WStep(steps, "route_hero_hero_reply", () => Feed(new S.MoveItem { Grid = MirGridType.HeroInventory, From = 5, To = 7, Success = true }));
            WStep(steps, "route_hero_equip", () => { ClickCell(MirGridType.HeroInventory, 3, Chr); ClickCell(MirGridType.HeroEquipment, (int)EquipmentSlot.RingR, Chr); });
            WStep(steps, "route_hero_equip_reply", () => Feed(new S.EquipItem { Grid = MirGridType.HeroInventory, UniqueID = UidAt(MirGridType.HeroInventory, 3), To = (int)EquipmentSlot.RingR, Success = true }), 800);
            WStep(steps, "route_hero_unequip", () => { ClickCell(MirGridType.HeroEquipment, (int)EquipmentSlot.RingR, Chr); ClickCell(MirGridType.HeroInventory, 8, Chr); });
            WStep(steps, "route_hero_unequip_reply", () => Feed(new S.RemoveItem { Grid = MirGridType.HeroInventory, UniqueID = UidAt(MirGridType.HeroEquipment, (int)EquipmentSlot.RingR), To = 8, Success = true }), 800);
            WStep(steps, "hero_char_tab", () => ClickTabOf(Win<CharacterWindow>(CharacterWindow.WindowKey), 0));

            // ---- ground, gold, delete bin, unlock row, sort
            WStep(steps, "route_bag_ground", () => { PressBind(KeybindOptions.Equipment); ClickCell(MirGridType.Inventory, 16, Inv); ClickAt(MapPoint()); });
            WStep(steps, "route_bag_ground_yes", () => { Key(Keys.Enter); MapControl.MapButtons = MouseButtons.None; });
            WStep(steps, "route_bag_ground_reply", () => Feed(new S.DropItem { UniqueID = DroppedId, Count = 20, Success = true }));
            WStep(steps, "inv_gold_pick", () => ClickAt(Centre(FindByHint(Win<InventoryWindow>(InventoryWindow.WindowKey), W(ClientTextKeys.ModernWinGoldHint)))));
            WStep(steps, "inv_gold_drop_ask", () => ClickAt(MapPoint()));
            WStep(steps, "inv_gold_drop_cancel", () => { Key(Keys.Escape); MapControl.MapButtons = MouseButtons.None; });
            WStep(steps, "inv_bin_pick", () => { ClickCell(MirGridType.Inventory, 22, Inv); ClickAt(Centre(FindByHintPrefix(Win<InventoryWindow>(InventoryWindow.WindowKey), "Delete"))); });
            WStep(steps, "inv_bin_amount_ok", () => ClickAt(Centre(Root.Popups.Card?.ButtonRow.Count > 0 ? Root.Popups.Card.ButtonRow[^1] : null)));
            WStep(steps, "inv_bin_hold", () => Press(Centre(Find<HoldButton>(Root.Popups.Card))), 1000);
            WStep(steps, "inv_bin_release", () => Release(CMain.MPoint));
            WStep(steps, "inv_delete_reply", () => Feed(new S.DeleteItem { UniqueID = DeleteId, Count = 3 }));
            WStep(steps, "inv_unlock_ask", () => ClickAt(Centre(FindByHintPrefix(Win<InventoryWindow>(InventoryWindow.WindowKey), "Unlock 2"))));
            WStep(steps, "inv_unlock_yes", () => Key(Keys.Y));
            WStep(steps, "inv_unlock_reply", () => Feed(new S.ResizeInventory { Size = 58 }), 500);
            WStep(steps, "inv_unlock_wave", () => { }, 700);
            WStep(steps, "inv_sort", () => ClickAt(Centre(FindButton(Win<InventoryWindow>(InventoryWindow.WindowKey), W(ClientTextKeys.ModernWinSort)))));
            WStep(steps, "inv_close", () => PressBind(KeybindOptions.Inventory));

            // ---- inspect
            WStep(steps, "inspect_open", () =>
            {
                UserItem[] eq = new UserItem[Enum.GetNames(typeof(EquipmentSlot)).Length];
                eq[(int)EquipmentSlot.Weapon] = Item("UiTestSword");
                eq[(int)EquipmentSlot.Armour] = Item("UiTestDress");
                eq[(int)EquipmentSlot.RingL] = Item("UiTestRing");
                eq[(int)EquipmentSlot.Weapon].AddedStats ??= new Stats();
                eq[(int)EquipmentSlot.Weapon].AddedStats[Stat.MaxDC] = 9;
                Feed(new S.PlayerInspect { Name = "UiAlly", GuildName = "UiGuild", GuildRank = "Member", Equipment = eq, Class = MirClass.Wizard, Gender = MirGender.Female, Hair = 2, Level = 41, LoverName = "UiLove" });
                Feed(new S.PlayerProfile { Name = "UiAlly", Text = "Hunting in the Bichon wall every evening. Whisper me for a group." });
            }, 900);
            WStep(steps, "inspect_compare", () => ClickTabOf(Win<InspectWindow>(InspectWindow.WindowKey), 1), 800);
            WStep(steps, "inspect_profile", () => ClickTabOf(Win<InspectWindow>(InspectWindow.WindowKey), 2));
            WStep(steps, "inspect_close", () => Key(Keys.Escape));

            // ---- skills
            WStep(steps, "skills_open", () =>
            {
                List<ClientMagic> m = GameScene.User.Magics;
                m.Clear();
                m.Add(new ClientMagic { Name = "Fencing", Spell = Spell.Fencing, Icon = 0, Level = 3, Key = 0, Level1 = 7, Level2 = 11, Level3 = 16, Need1 = 270, Need2 = 600, Need3 = 1300 });
                m.Add(new ClientMagic { Name = "Slaying", Spell = Spell.Slaying, Icon = 1, Level = 1, Key = 1, Level1 = 15, Level2 = 18, Level3 = 21, Need1 = 500, Need2 = 900, Need3 = 1500, Experience = 400 });
                m.Add(new ClientMagic { Name = "Thrusting", Spell = Spell.Thrusting, Icon = 2, Level = 0, Key = 0, Level1 = 22, Level2 = 24, Level3 = 26, Need1 = 1000, Need2 = 1800, Need3 = 2400, BaseCost = 3, LevelCost = 1, Experience = 120 });
                m.Add(new ClientMagic { Name = "Half Moon", Spell = Spell.HalfMoon, Icon = 25, Level = 2, Key = 9, Level1 = 26, Level2 = 28, Level3 = 31, Need1 = 1000, Need2 = 2000, Need3 = 3500, BaseCost = 3, LevelCost = 1, Delay = 4000, CastTime = CMain.Time, Range = 1 });
                if (GameScene.Hero != null)
                {
                    GameScene.Hero.Magics.Clear();
                    GameScene.Hero.Magics.Add(new ClientMagic { Name = "Soul Fire Ball", Spell = Spell.SoulFireBall, Icon = 9, Level = 1, Key = 17, Level1 = 9, Level2 = 13, Level3 = 19, Need1 = 300, Need2 = 600, Need3 = 1000, BaseCost = 5, LevelCost = 1, Experience = 90, Range = 9 });
                }
                ModernEvents.UserInformation.Emit();
                PressBind(KeybindOptions.Skills);
            }, 800);
            WStep(steps, "skills_pick_row", () =>
            {
                var w = Win<SkillsWindow>(SkillsWindow.WindowKey);
                ScrollView list = FindList(w);
                if (list != null && list.Content.Controls.Count > 2) ClickAt(Centre(list.Content.Controls[2]));
            });
            WStep(steps, "skills_assign_f3", () => ClickAt(Centre(FindButton(Win<SkillsWindow>(SkillsWindow.WindowKey), "F3"))));
            WStep(steps, "skills_filter_keyed", () => ClickAt(ChipCentre(Find<Chips<string>>(Win<SkillsWindow>(SkillsWindow.WindowKey)), 1)));
            WStep(steps, "skills_filter_all", () => ClickAt(ChipCentre(Find<Chips<string>>(Win<SkillsWindow>(SkillsWindow.WindowKey)), 0)));
            WStep(steps, "skills_hero_tab", () => ClickTabOf(Win<SkillsWindow>(SkillsWindow.WindowKey), 1));
            WStep(steps, "skills_hero_scroll", () => { ScrollView d = SecondScroll(Win<SkillsWindow>(SkillsWindow.WindowKey)); if (d != null) Wheel(Centre(d), -600); });
            WStep(steps, "skills_hero_autocast_off", () => ClickAt(Centre(Find<ToggleSwitch>(Win<SkillsWindow>(SkillsWindow.WindowKey)))));
            WStep(steps, "skills_close", () => PressBind(KeybindOptions.Skills));

            // ---- options
            WStep(steps, "options_open", () => PressBind(KeybindOptions.Options), 800);
            WStep(steps, "options_toggle_effect", () => ClickAt(Centre(Find<ToggleSwitch>(Win<OptionsWindow>(OptionsWindow.WindowKey)?.Page))));
            WStep(steps, "options_toggle_back", () => ClickAt(Centre(Find<ToggleSwitch>(Win<OptionsWindow>(OptionsWindow.WindowKey)?.Page))));
            WStep(steps, "options_interface", () => ClickAt(NavPoint(1)), 800);
            WStep(steps, "options_interface_scroll", () => Wheel(Centre(Win<OptionsWindow>(OptionsWindow.WindowKey)?.Page), -480), 700);
            WStep(steps, "options_social", () => ClickAt(NavPoint(5)));
            WStep(steps, "options_search", () => SetSearch(Win<OptionsWindow>(OptionsWindow.WindowKey), "chat"), 800);
            WStep(steps, "options_close", () => { SetSearch(Win<OptionsWindow>(OptionsWindow.WindowKey), ""); PressBind(KeybindOptions.Options); });

            // ---- keybinds
            WStep(steps, "keys_open", () => PressBind(KeybindOptions.Keybind), 900);
            WStep(steps, "keys_hover_board", () => { var b = Win<KeybindsWindow>(KeybindsWindow.WindowKey)?.Board; if (b != null) { Point c = b.KeyCentre("I"); _log.Add($"   board at {b.DisplayLocation} I at {c} keys={b.KeyCount}"); MouseAt(new Point(b.DisplayLocation.X + c.X, b.DisplayLocation.Y + c.Y)); } });
            WStep(steps, "keys_search_help", () => SetSearch(Win<KeybindsWindow>(KeybindsWindow.WindowKey), "help"));
            WStep(steps, "keys_wait", () => { var w = Win<KeybindsWindow>(KeybindsWindow.WindowKey); if (w != null && w.BindAreas.TryGetValue(KeybindOptions.Help, out PaintBox p)) ClickAt(Centre(p)); });
            WStep(steps, "keys_press_j", () => Key(Keys.J));
            WStep(steps, "keys_reset_help", () => { var w = Win<KeybindsWindow>(KeybindsWindow.WindowKey); if (w != null && w.ResetArrows.TryGetValue(KeybindOptions.Help, out PaintBox p)) ClickAt(Centre(p)); });
            WStep(steps, "keys_clear_search", () => SetSearch(Win<KeybindsWindow>(KeybindsWindow.WindowKey), ""));
            WStep(steps, "keys_layer_ctrl", () => ClickAt(Centre(FindByText(Win<KeybindsWindow>(KeybindsWindow.WindowKey)))));
            WStep(steps, "keys_close", () => PressBind(KeybindOptions.Keybind));

            // ---- help
            WStep(steps, "help_open", () => PressBind(KeybindOptions.Help), 800);
            WStep(steps, "help_topic_skills", () => Win<HelpWindow>(HelpWindow.WindowKey)?.Show("skills"), 900);
            WStep(steps, "help_next_page", () => ClickAt(Centre(FindByHint(Win<HelpWindow>(HelpWindow.WindowKey), W(ClientTextKeys.ModernWinNextPage)))), 800);
            WStep(steps, "help_arrow_key", () => Key(Keys.Right), 500);
            WStep(steps, "help_lightbox", () => ClickAt(Centre(FindByHint(Win<HelpWindow>(HelpWindow.WindowKey), W(ClientTextKeys.ModernWinNextPage))?.Parent)), 600);
            WStep(steps, "help_lightbox_esc", () => Key(Keys.Escape));
            WStep(steps, "help_shortcuts", () => Win<HelpWindow>(HelpWindow.WindowKey)?.Show("shortcuts"), 800);
            WStep(steps, "help_search", () => SetSearch(Win<HelpWindow>(HelpWindow.WindowKey), "trade"), 800);
            WStep(steps, "help_close", () => PressBind(KeybindOptions.Help));
            WStep(steps, "win_close_all", () => { Root?.Popups.CloseAll(); PressBind(KeybindOptions.Closeall); DumpWindows(); });
        }

        // ------------------------------------------------------------------ helpers

        private static string W(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);

        private static T Win<T>(string key) where T : ModernWindow => Root?.Wm.IsOpen(key) == true ? Root.Wm.Get(key) as T : null;

        private static void SetSearch(MirControl window, string text)
        {
            SearchBox s = Find<SearchBox>(window);
            s?.SetQuery(text);
        }

        /// <summary>A modern-only step that first logs area C's state after the previous step (the dump hook is another area's).</summary>
        private static void WStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { DumpWindowState(_log); act(); }, DelayMs = delayMs, ModernOnly = true });

        private static void ClickTabOf(MirControl window, int i)
        {
            Tabs t = Find<Tabs>(window);
            if (t == null) { _log.Add("   (no tabs)"); return; }
            Rectangle r = t.TabBounds(i);
            ClickAt(new Point(t.DisplayLocation.X + r.X + r.Width / 2, t.DisplayLocation.Y + r.Y + r.Height / 2));
        }

        private const string Inv = InventoryWindow.WindowKey, Chr = CharacterWindow.WindowKey, Sto = StorageWindow.WindowKey, Hud = "hud";

        /// <summary>The visible ModernItemCell showing (grid, slot) in one window (raised first, as a click on it would) or the HUD
        /// ("hud": the belt). <paramref name="side"/> = the storage window's side column; otherwise its side column is skipped.</summary>
        private static ModernItemCell CellOf(MirGridType grid, int slot, string where, bool side)
        {
            ModernRoot root = Root;
            if (root == null) return null;
            MirControl scope = root.Hud;
            if (where != Hud)
            {
                if (!root.Wm.IsOpen(where)) return null;
                root.Wm.Open(where); // raise it: the click lands on the front window
                scope = root.Wm.Get(where);
            }
            var store = scope as StorageWindow;
            if (side && store != null)
            {
                foreach (ModernItemCell c in store.BagCells) if (c.Grid == grid && c.Slot == slot && c.Visible) return c;
                return null;
            }
            ModernItemCell found = null;
            void Walk(MirControl c)
            {
                if (found != null || c == null || !c.Visible) return;
                if (c is ModernItemCell mc && mc.Grid == grid && mc.Slot == slot && !mc.ReadOnly)
                {
                    bool inSide = store != null && store.BagCells.Contains(mc);
                    if (!inSide) { found = mc; return; }
                }
                for (int i = c.Controls.Count - 1; i >= 0 && found == null; i--) Walk(c.Controls[i]);
            }
            Walk(scope);
            return found;
        }

        /// <summary>A click on the cell through the scene's routing, or (no such cell on screen) the same ItemMoves calls.</summary>
        private static void ClickCell(MirGridType grid, int slot, string where, bool side = false)
        {
            if (grid == MirGridType.Inventory && slot == 16) DroppedId = UidAt(grid, slot);
            if (grid == MirGridType.Inventory && slot == 22) DeleteId = UidAt(grid, slot);
            ModernItemCell c = CellOf(grid, slot, where, side);
            if (c != null)
            {
                ClickAt(Centre(c));
                return;
            }
            _log.Add($"   (no cell on screen for {grid}[{slot}] in {where}: ItemMoves directly)");
            var e = new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0);
            ItemSlotRef r = new ItemSlotRef(grid, slot);
            if (ItemMoves.BeginClick(r, e)) ItemMoves.Click(r, e);
        }

        private static ulong MergeFrom, MergeTo;
        private static ulong DroppedId, DeleteId;

        private static ulong UidAt(MirGridType grid, int slot)
        {
            try { return ItemMoves.GetItem(grid, slot)?.UniqueID ?? 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>A map point clear of the HUD and the windows (top middle-left).</summary>
        private static Point MapPoint() => new Point(Settings.ScreenWidth / 2 - ModernRoot.S(160), ModernRoot.S(330));

        private static MirControl FindByHint(MirControl root, string hint)
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                MirControl c = root.Controls[i];
                if (c.Visible && c.Hint == hint) return c;
                MirControl deep = FindByHint(c, hint);
                if (deep != null) return deep;
            }
            return null;
        }

        private static MirControl FindByHintPrefix(MirControl root, string prefix)
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                MirControl c = root.Controls[i];
                if (c.Visible && c.Hint != null && c.Hint.StartsWith(prefix, StringComparison.Ordinal)) return c;
                MirControl deep = FindByHintPrefix(c, prefix);
                if (deep != null) return deep;
            }
            return null;
        }

        /// <summary>The keybinds window's "Ctrl" layer chip (the third chip of the layer bar).</summary>
        private static MirControl FindByText(MirControl window)
        {
            if (window == null) return null;
            int n = 0;
            foreach (MirControl c in window.Controls)
                if (c.GetType().Name == "PaintBox" && c.Location.Y < ModernRoot.S(ModernWindow.TitleHeight + 34) && c.Size.Height == ModernRoot.S(22) && ++n == 3) return c;
            return null;
        }

        private static ScrollView FindList(MirControl window)
        {
            if (window == null) return null;
            foreach (MirControl c in window.Controls) if (c is ScrollView sv) return sv;
            return null;
        }

        private static ScrollView SecondScroll(MirControl window)
        {
            if (window == null) return null;
            int n = 0;
            foreach (MirControl c in window.Controls) if (c is ScrollView sv && n++ == 1) return sv;
            return null;
        }

        private static Point PageButton(int i)
        {
            var w = Win<StorageWindow>(StorageWindow.WindowKey);
            if (w == null) return new Point(-1000, -1000);
            MirControl main = null;
            foreach (MirControl c in w.Controls) if (c is ModernPanel p && p.Visible && p.Controls.Count > 20) main = p;
            if (main == null) return new Point(-1000, -1000);
            int n = 0;
            foreach (MirControl c in main.Controls)
                if (c.GetType().Name == "PaintBox" && c.Size.Width == ModernRoot.S(124) && n++ == i) return Centre(c);
            return new Point(-1000, -1000);
        }

        private static Point NavPoint(int cat)
        {
            var w = Win<OptionsWindow>(OptionsWindow.WindowKey);
            if (w == null) return new Point(-1000, -1000);
            int n = 0;
            foreach (MirControl c in w.Controls)
                if (c.GetType().Name == "PaintBox" && c.Size.Height == ModernRoot.S(32) && n++ == cat) return Centre(c);
            return new Point(-1000, -1000);
        }

        private static UserItem[] SortedStorage()
        {
            UserItem[] s = GameScene.Storage;
            var copy = new UserItem[s.Length];
            int j = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] != null) copy[j++] = s[i];
            return copy;
        }

        private static void DumpWindows()
        {
            var open = new List<string>();
            foreach (MirControl c in Root.Windows.Controls)
                if (c is ModernWindow w && w.IsOpen) open.Add(w.Key);
            _log.Add("   windows open after close all: [" + string.Join(",", open) + "]");
        }

        /// <summary>Area C's state lines (added to the modern dump through DumpModern's window list).</summary>
        private static void DumpWindowState(List<string> log)
        {
            ModernRoot root = Root;
            if (root == null || GameScene.Scene == null || GameScene.User == null) return;
            if (root.Wm.IsOpen(StorageWindow.WindowKey) && root.Wm.Get(StorageWindow.WindowKey) is StorageWindow s)
                log.Add($"   [area C] storage view={s.ViewNow} page={s.PageShown} note=\"{s.Note}\"");
            if (root.Wm.IsOpen(CharacterWindow.WindowKey) && root.Wm.Get(CharacterWindow.WindowKey) is CharacterWindow c)
                log.Add($"   [area C] character tab={c.ShownTab} stats={c.StatsOpen}");
            if (root.Wm.IsOpen(SkillsWindow.WindowKey) && root.Wm.Get(SkillsWindow.WindowKey) is SkillsWindow k)
                log.Add($"   [area C] skills tab={k.TabShown} shown={k.ShownMagic?.Name} keys=[{string.Join(",", GameScene.User.Magics.ConvertAll(m => m.Name + ":" + m.Key))}]");
            if (root.Wm.IsOpen(KeybindsWindow.WindowKey) && root.Wm.Get(KeybindsWindow.WindowKey) is KeybindsWindow kb)
                log.Add($"   [area C] keybinds waiting={kb.Waiting} conflicts={kb.ConflictCount} help=\"{CMain.InputKeys.GetKey(KeybindOptions.Help)}\" status=\"{kb.StatusText}\" {kb.InfoKeys}");
            if (root.Wm.IsOpen(HelpWindow.WindowKey) && root.Wm.Get(HelpWindow.WindowKey) is HelpWindow h)
                log.Add($"   [area C] help topic={h.TopicShown ?? "home"} page={h.PageNo} lightbox={h.LightboxOpen}");
            if (root.Wm.IsOpen(InspectWindow.WindowKey) && root.Wm.Get(InspectWindow.WindowKey) is InspectWindow iw)
                log.Add($"   [area C] inspect tab={iw.TabShown} hero={iw.IsHeroShown} name={GameScene.Models.Inspect.Name}");
            if (root.Wm.IsOpen(OptionsWindow.WindowKey) && root.Wm.Get(OptionsWindow.WindowKey) is OptionsWindow o)
                log.Add($"   [area C] options page={o.CategoryShown} effect={Settings.Effect}");
            log.Add($"   [area C] hero={(GameScene.Hero != null ? GameScene.Hero.Name + " inv " + Items(GameScene.Hero.Inventory) + " eq " + Items(GameScene.Hero.Equipment) : "none")} pickedGold={GameScene.PickedUpGold} invLen={GameScene.User?.Inventory.Length}");
        }
    }
}
