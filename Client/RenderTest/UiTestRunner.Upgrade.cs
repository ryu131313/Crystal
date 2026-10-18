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
    /// Phase 3 area "upgrades / rental" steps (modern only): refine (the anvil from the bag column, the grid deposits, the odds,
    /// C.RefineItem, the smith taking it, C.RefineCancel on close; the check page: C.CheckRefine and a crit result), awaken (the
    /// gear list, a type chip, the server's materials, hold-to-awaken with the material fill, the result), sockets & gems (Ctrl +
    /// right-click open, socketing from the list, an orb combine and its answer) and item rental (lend: deposit, the period box,
    /// lock, the partner's fee / lock, confirm, the rented-out list; borrow: fee, lock, the lender's item, cancel; the renting tab).
    /// The NPC dialog is area A's: when it does not report itself open, the NPC-gated windows are opened directly (logged).
    /// Starts from a fresh game and closes everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddUpgradeSteps(List<Step> steps)
        {
            UStep(steps, "up_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            UStep(steps, "up_enter", () => { EnterGame(); SetScale(1f, false); UpItems(); UpRules(); }, 1500);

            // ---- refine
            UStep(steps, "refine_open", () =>
            {
                Feed(new S.NPCResponse { Page = new List<string> { "Welcome to the smithy.", "<Refine/@REFINE>" } });
                Feed(new S.NPCRefine { Rate = 1F, Refining = false });
                if (Win<RefineWindow>(RefineWindow.WindowKey) == null)
                {
                    _log.Add("   (NPC dialog not open: refine window opened directly)");
                    UpUi.Refine.Open(RefineMode.Refine);
                    Root.Wm.Open(RefineWindow.WindowKey);
                }
            }, 900);
            UStep(steps, "refine_anvil", () => UpClickBag(Win<RefineWindow>(RefineWindow.WindowKey)?.Bag, 9));
            UStep(steps, "refine_grid_necklace", () => UpClickBag(Win<RefineWindow>(RefineWindow.WindowKey)?.Bag, 20));
            UStep(steps, "refine_grid_necklace_reply", () => Feed(new S.DepositRefineItem { From = 20, To = 0, Success = true }));
            UStep(steps, "refine_grid_ore", () => UpClickBag(Win<RefineWindow>(RefineWindow.WindowKey)?.Bag, 21));
            UStep(steps, "refine_grid_ore_reply", () => Feed(new S.DepositRefineItem { From = 21, To = 1, Success = true }), 800);
            UStep(steps, "refine_go", () => ClickAt(Centre(Win<RefineWindow>(RefineWindow.WindowKey)?.GoButton)));
            UStep(steps, "refine_go_ok", () => UpPopupOk(), 700);
            UStep(steps, "refine_taken", () =>
            {
                Feed(new S.RepairItem { UniqueID = UpUid(9) });
                Feed(new S.NPCRefine { Rate = 1F, Refining = true });
                if (Root.Wm.IsOpen(RefineWindow.WindowKey)) Root.Wm.Close(RefineWindow.WindowKey);
            });
            UStep(steps, "refine_check_open", () =>
            {
                UserItem sword = GameScene.User.Inventory[9];
                if (sword != null) { sword.RefineAdded = 2; sword.RefinedValue = RefinedValue.DC; sword.RefineSuccessChance = 64; }
                Feed(new S.NPCResponse { Page = new List<string> { "Check your refine?" } });
                if (!UpUi.UpgradeDropPanel(PanelType.CheckRefine))
                {
                    _log.Add("   (NPC dialog not open: check window opened directly)");
                    UpUi.Refine.Open(RefineMode.Check);
                    Root.Wm.Open(RefineWindow.WindowKey);
                }
            }, 800);
            UStep(steps, "refine_check_anvil", () => UpClickBag(Win<RefineWindow>(RefineWindow.WindowKey)?.Bag, 9));
            UStep(steps, "refine_check_test", () => ClickAt(Centre(Win<RefineWindow>(RefineWindow.WindowKey)?.GoButton)));
            UStep(steps, "refine_check_ok", () => UpPopupOk());
            UStep(steps, "refine_check_crit", () =>
            {
                UserItem sword = GameScene.User.Inventory[9];
                if (sword == null) return;
                UserItem up = sword.Clone();
                up.AddedStats[Stat.MaxDC] = 5;
                up.RefineAdded = 0;
                Feed(new S.ItemUpgraded { Item = up });
            }, 900);
            UStep(steps, "refine_close", () => Key(Keys.Escape));

            // ---- awaken
            UStep(steps, "awaken_open", () => Feed(new S.NPCAwakening()), 900);
            UStep(steps, "awaken_pick_helmet", () =>
            {
                var w = Win<AwakenWindow>(AwakenWindow.WindowKey);
                if (w == null) return;
                foreach (var r in w.Rows) if (r.Slot == 22) ClickAt(Centre(r.Row));
            });
            UStep(steps, "awaken_type", () =>
            {
                var w = Win<AwakenWindow>(AwakenWindow.WindowKey);
                ModernButton b = w?.TypesBox.Controls.Count > 0 ? w.TypesBox.Controls[0] as ModernButton : null;
                ClickAt(Centre(b));
            });
            UStep(steps, "awaken_materials", () => Feed(new S.AwakeningNeedMaterials
            {
                Materials = new[] { Info("UiTestGlyph"), Info("UiTestSoul") },
                MaterialsCount = new byte[] { 2, 1 },
            }), 900);
            UStep(steps, "awaken_hold", () => Press(Centre(Win<AwakenWindow>(AwakenWindow.WindowKey)?.GoButton)), 1500);
            UStep(steps, "awaken_release", () => Release(CMain.MPoint));
            UStep(steps, "awaken_result", () => Feed(new S.Awakening { result = 1, removeID = -1 }), 900);
            UStep(steps, "awaken_result_ok", () => { UpPopupOk(); PressBind(KeybindOptions.Inventory); });

            // ---- sockets & gems
            UStep(steps, "socket_open", () => MirScenes.ItemMoves.OpenItem(new ItemSlotRef(MirGridType.Inventory, 23)), 900);
            UStep(steps, "socket_equip", () =>
            {
                var w = Win<SocketWindow>(SocketWindow.WindowKey);
                ScrollView list = w == null ? null : UpScrolls(w)[UpScrolls(w).Count - 1];
                if (list?.Content.Controls.Count > 0) ClickAt(Centre(list.Content.Controls[0]));
            });
            UStep(steps, "socket_equip_reply", () =>
            {
                ulong id = UpUid(24);
                Feed(new S.EquipSlotItem { Grid = MirGridType.Inventory, UniqueID = id, To = 0, GridTo = MirGridType.Socket, Success = true });
            }, 800);
            UStep(steps, "gems_tab", () => ClickTabOf(Win<SocketWindow>(SocketWindow.WindowKey), 0), 800);
            UStep(steps, "gems_pick_orb", () =>
            {
                var w = Win<SocketWindow>(SocketWindow.WindowKey);
                List<ScrollView> sv = w == null ? null : UpScrolls(w);
                if (sv == null || sv.Count < 2) return;
                ScrollView gear = sv[0], gems = sv[1];
                foreach (MirControl c in gear.Content.Controls) // the ring (slot 23)
                    if (c is PaintBox && c.Controls.Count > 0 && c.Controls[0] is ModernItemCell cell && cell.Slot == 23) ClickAt(Centre(c));
                if (gems.Content.Controls.Count > 0) ClickAt(Centre(gems.Content.Controls[0]));
            }, 800);
            UStep(steps, "gems_apply", () => ClickAt(Centre(Win<SocketWindow>(SocketWindow.WindowKey)?.GoButton)));
            UStep(steps, "gems_apply_yes", () => Key(Keys.Y), 900);
            UStep(steps, "gems_reply", () =>
            {
                UserItem ring = GameScene.User.Inventory[23];
                if (ring == null) return;
                UserItem up = ring.Clone();
                up.AddedStats[Stat.MaxDC] = 1;
                Feed(new S.ItemUpgraded { Item = up });
                Feed(new S.CombineItem { Grid = MirGridType.Inventory, IDFrom = UpUid(25), IDTo = ring.UniqueID, Success = true, Destroy = false });
            }, 900);
            UStep(steps, "socket_close", () => Key(Keys.Escape));

            // ---- item rental: lend
            UStep(steps, "rent_lend_open", () => Feed(new S.ItemRentalRequest { Name = "UiBorrower", Renting = false }), 900);
            UStep(steps, "rent_deposit", () => UpClickBag(Win<RentWindow>(RentWindow.WindowKey)?.Bag, 10));
            UStep(steps, "rent_deposit_reply", () => Feed(new S.DepositRentalItem { From = 10, To = 0, Success = true }), 800);
            UStep(steps, "rent_period_ok", () => UpPopupOk());
            UStep(steps, "rent_lock_item", () => ClickAt(Centre(Win<RentWindow>(RentWindow.WindowKey)?.LockItemButton)));
            UStep(steps, "rent_partner", () =>
            {
                Feed(new S.ItemRentalLock { Success = true, ItemLocked = true, GoldLocked = false });
                Feed(new S.ItemRentalFee { Amount = 5000 });
                Feed(new S.ItemRentalFee { Amount = 2500 });
                Feed(new S.ItemRentalPartnerLock { GoldLocked = true });
                Feed(new S.CanConfirmItemRental());
            }, 800);
            UStep(steps, "rent_confirm", () => ClickAt(Centre(Win<RentWindow>(RentWindow.WindowKey)?.ConfirmButton)));
            UStep(steps, "rent_confirmed", () =>
            {
                Feed(new S.ConfirmItemRental());
                Feed(new S.GetRentedItems
                {
                    RentedItems = new List<ItemRentalInformation> { new ItemRentalInformation { ItemId = 1, ItemName = "UiTestRing", RentingPlayerName = "UiBorrower", ItemReturnDate = DateTime.Now.AddDays(3).AddHours(4) } },
                });
            }, 900);

            // ---- item rental: borrow
            UStep(steps, "rent_borrow_open", () =>
            {
                Feed(new S.ItemRentalRequest { Name = "UiLender", Renting = true });
                Feed(new S.UpdateRentalItem { HasData = true, LoanItem = Item("UiTestSword") });
                Feed(new S.ItemRentalPeriod { Days = 5 });
                Feed(new S.ItemRentalPartnerLock { ItemLocked = true });
            }, 900);
            UStep(steps, "rent_fee", () =>
            {
                var w = Win<RentWindow>(RentWindow.WindowKey);
                if (w != null && w.AddButtons.Count > 1) ClickAt(Centre(w.AddButtons[1]));
            });
            // the fee total follows the server: it counts once S.LoseGold took the gold (a frame later), then the lock is offered
            UStep(steps, "rent_fee_taken", () => Feed(new S.LoseGold { Gold = 10000 }));
            UStep(steps, "rent_lock_fee", () => ClickAt(Centre(Win<RentWindow>(RentWindow.WindowKey)?.LockFeeButton)));
            UStep(steps, "rent_fee_locked", () => Feed(new S.ItemRentalLock { Success = true, GoldLocked = true, ItemLocked = false }), 800);
            UStep(steps, "rent_cancel", () => ClickAt(Centre(Win<RentWindow>(RentWindow.WindowKey)?.CancelButton)));
            UStep(steps, "rent_cancelled", () =>
            {
                Feed(new S.CancelItemRental());
                UserItem rented = GameScene.User.Inventory[26];
                if (rented != null) rented.RentalInformation = new RentalInformation { OwnerName = "UiLender", ExpiryDate = DateTime.Now.AddDays(2).AddHours(3), BindingFlags = BindMode.DontDrop | BindMode.DontSell | BindMode.DontUpgrade };
            }, 800);
            UStep(steps, "rent_renting_tab", () => ClickTabOf(Win<RentWindow>(RentWindow.WindowKey), 2), 900);
            UStep(steps, "rent_toggle_close", () => UpUi?.KeyBind(KeybindOptions.Rental)); // no default key: the bind's handler directly
            UStep(steps, "rent_toggle_open", () => UpUi?.KeyBind(KeybindOptions.Rental));
            UStep(steps, "up_close_all", () => { Root?.Popups.CloseAll(); PressBind(KeybindOptions.Closeall); });
        }

        private static ModernUi UpUi => GameScene.Ui as ModernUi;

        /// <summary>A modern-only step of this area.</summary>
        private static void UStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = act, DelayMs = delayMs, ModernOnly = true });

        private static ulong UpUid(int bagSlot) => GameScene.User?.Inventory != null && bagSlot < GameScene.User.Inventory.Length ? GameScene.User.Inventory[bagSlot]?.UniqueID ?? 0 : 0;

        /// <summary>A click on the bag column cell showing a bag slot.</summary>
        private static void UpClickBag(BagColumn bag, int slot)
        {
            if (bag == null) { _log.Add("   (no bag column)"); return; }
            foreach (ModernItemCell c in bag.Cells)
                if (c.Slot == slot) { ClickAt(Centre(c)); return; }
            _log.Add("   (bag slot " + slot + " not shown)");
        }

        /// <summary>The front popup's primary (last) button.</summary>
        private static void UpPopupOk()
        {
            PopupCard card = Root?.Popups.Card;
            if (card == null || card.ButtonRow.Count == 0) { _log.Add("   (no popup)"); return; }
            ClickAt(Centre(card.ButtonRow[card.ButtonRow.Count - 1]));
        }

        private static List<ScrollView> UpScrolls(MirControl root)
        {
            var list = new List<ScrollView>();
            void Walk(MirControl c)
            {
                if (c == null || !c.Visible) return;
                if (c is ScrollView sv) list.Add(sv);
                for (int i = 0; i < c.Controls.Count; i++) Walk(c.Controls[i]);
            }
            Walk(root);
            return list;
        }

        /// <summary>Upgrade test items: jewellery with DC, ore, a graded awakenable helmet, glyphs / souls, a ring with sockets,
        /// a socket item, an orb, a spare item for the renting tab.</summary>
        private static void UpItems()
        {
            Info("UiTestPotion"); // the default infos first
            if (!_infos.ContainsKey("UiTestNecklace"))
            {
                var dc = new Stats();
                dc[Stat.MinDC] = 1;
                dc[Stat.MaxDC] = 3;
                AddInfo(new ItemInfo { Index = 9101, Name = "UiTestNecklace", Type = ItemType.Necklace, Image = 130, Durability = 3000, Price = 300, RequiredAmount = 20, Stats = dc });
                AddInfo(new ItemInfo { Index = 9102, Name = "UiTestOre", Type = ItemType.Ore, Image = 100, Durability = 15000, Price = 10, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9103, Name = "UiTestHelmet", Type = ItemType.Helmet, Image = 80, Durability = 4000, Price = 600, Grade = ItemGrade.Rare, CanAwakening = true, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9104, Name = "UiTestGlyph", Type = ItemType.Awakening, Image = 300, StackSize = 50, Price = 10, Shape = 3, Grade = ItemGrade.Rare, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9105, Name = "UiTestSoul", Type = ItemType.Awakening, Image = 301, StackSize = 50, Price = 10, Shape = 100, Grade = ItemGrade.Rare, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9106, Name = "UiTestSocketRing", Type = ItemType.Ring, Image = 121, Durability = 3000, Price = 300, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9107, Name = "UiTestJewel", Type = ItemType.Socket, Image = 140, Price = 50, Shape = 3, Stats = new Stats() });
                var orb = new Stats();
                orb[Stat.MaxDC] = 1;
                orb[Stat.CriticalRate] = 80;
                orb[Stat.Reflect] = 10;
                orb[Stat.CriticalDamage] = 3;
                orb[Stat.HPDrainRatePercent] = 5;
                AddInfo(new ItemInfo { Index = 9108, Name = "UiTestOrb", Type = ItemType.Gem, Image = 150, StackSize = 20, Price = 50, Shape = 4, Unique = SpecialItemMode.Muscle, Stats = orb });
            }
            UserItem[] inv = GameScene.User.Inventory;
            inv[20] = Item("UiTestNecklace");
            UserItem ore = Item("UiTestOre");
            ore.CurrentDura = ore.MaxDura = 8000;
            inv[21] = ore;
            inv[22] = Item("UiTestHelmet");
            inv[23] = Item("UiTestSocketRing");
            inv[23].Slots = new UserItem[3];
            inv[24] = Item("UiTestJewel");
            inv[25] = Item("UiTestOrb", 3);
            inv[26] = Item("UiTestDress");
            inv[27] = Item("UiTestGlyph", 5);
            inv[28] = Item("UiTestSoul", 3);
            MirScenes.ItemMoves.GridChanged(MirGridType.Inventory);
        }

        private static void UpRules()
        {
            Feed(new S.ServerRules
            {
                OnlyRefineWeapon = true, RefineBaseChance = 20, RefineTime = 20, RefineIncrease = 1, RefineCritChance = 10, RefineCritIncrease = 2,
                RefineWepStatReduce = 6, RefineItemStatReduce = 15, RefineCost = 125, RefineOreName = "UiTestOre",
                AwakeSuccessRate = 70, AwakeHitRate = 70, Awake_WeaponRate = 1, Awake_HelmetRate = 1, Awake_ArmorRate = 5, MaxAwakeLevel = 5,
                AwakeChanceMax = new byte[] { 1, 2, 3, 4, 5 }, AwakeMaterialRate = new float[] { 1, 1, 1, 1, 1 },
                GemStatIndependent = false,
            });
        }
    }
}
