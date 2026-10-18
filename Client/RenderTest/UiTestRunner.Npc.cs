using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 3 area "NPC" steps (modern only): the NPC dialogue (page markup, typewriter, option cards, log, chips, info link card,
    /// the quests tab), the merchant (buy with the cart and the amount popup, sell picks → C.SellItems, repair all → C.RepairItems,
    /// buy back label), the item box (disassemble on the altar with the hold confirm, consign with the price popup), craft (recipe,
    /// C.CraftItem, the reply) and walking away from the NPC (S.ObjectRemove → CloseNpcDialog closes everything). Starts from a fresh
    /// game and closes everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        private const uint NpcObjectId = 500;

        static partial void AddNpcSteps(List<Step> steps)
        {
            NStep(steps, "npc_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            NStep(steps, "npc_enter", () => { EnterGame(); SetScale(1f, false); }, 1500);
            NStep(steps, "npc_page", () =>
            {
                Point at = GameScene.User.CurrentLocation;
                Feed(new S.ObjectNPC { ObjectID = NpcObjectId, Name = "BichonProvince_Merchant_Herbalist", NameColour = Color.Lime, Image = 1, Colour = Color.White, Location = new Point(at.X + 2, at.Y), Direction = MirDirection.DownLeft });
                GameScene.NPCID = NpcObjectId; // MapControl's click on the NPC sets it before C.CallNPC "[@Main]"
                Feed(new S.NPCResponse { Page = NpcPageLines() });
            }, 300);
            NStep(steps, "npc_typing_mid", () => { }, 400);
            NStep(steps, "npc_type_finish", () => Key(Keys.Space));
            NStep(steps, "npc_info_hover", () => HoverInfoLink(0), 700);
            NStep(steps, "npc_info_monster", () => HoverInfoLink(1), 700);
            NStep(steps, "npc_log_on", () => ClickAt(Centre(FindButton(Win<NpcWindow>(NpcWindow.WindowKey), W(ClientTextKeys.ModernNpcLog)))));
            NStep(steps, "npc_quests_tab", () => ClickTabOf(Win<NpcWindow>(NpcWindow.WindowKey), 1));
            NStep(steps, "npc_talk_tab", () => ClickTabOf(Win<NpcWindow>(NpcWindow.WindowKey), 0));
            NStep(steps, "npc_choose_buy", () => ClickOption(0));
            NStep(steps, "npc_goods", () =>
            {
                Feed(new S.NPCResponse { Page = NpcPageLines() }); // the server's answer to [@buy] (NPCTime clears)
                UserItem potion = Item("UiTestPotion", 1), sword = Item("UiTestSword"), used = Item("UiTestSword"), scroll = Item("UiTestScroll", 1);
                potion.IsShopItem = sword.IsShopItem = scroll.IsShopItem = true;
                used.CurrentDura = 2500;
                Feed(new S.NPCGoods { List = new List<UserItem> { potion, sword, used, scroll }, Rate = 1.2F, Type = PanelType.Buy });
            }, 900);
            NStep(steps, "merchant_unfold_swords", () => ClickGoods("UiTestSword", 0));
            NStep(steps, "merchant_add_sword", () => ClickGoods("UiTestSword", 1));
            NStep(steps, "merchant_add_potion", () => ClickGoods("UiTestPotion", 0));
            NStep(steps, "merchant_amount_ok", () => ClickAt(Centre(Root.Popups.Card?.ButtonRow.Count > 0 ? Root.Popups.Card.ButtonRow[^1] : null)));
            NStep(steps, "merchant_checkout", () => ClickAt(Centre(Win<MerchantWindow>(MerchantWindow.WindowKey)?.BuyButton)), 900);
            NStep(steps, "merchant_sell_panel", () => Feed(new S.NPCSell()));
            NStep(steps, "merchant_sell_tab", () => ClickAt(Centre(Win<MerchantWindow>(MerchantWindow.WindowKey)?.TabButton(MerchantTab.Sell))));
            NStep(steps, "merchant_pick_sell", () => { ClickSellCell(11); ClickSellCell(12); });
            NStep(steps, "merchant_sell", () => ClickAt(Centre(Win<MerchantWindow>(MerchantWindow.WindowKey)?.SellButton)));
            NStep(steps, "merchant_sell_reply", () =>
            {
                Feed(new S.SellItem { UniqueID = UidAt(MirGridType.Inventory, 11), Count = 5, Success = true });
                Feed(new S.SellItem { UniqueID = UidAt(MirGridType.Inventory, 12), Count = 7, Success = true });
            });
            NStep(steps, "merchant_repair_panel", () =>
            {
                UserItem sword = GameScene.User.Inventory[9];
                if (sword != null) sword.CurrentDura = 1200;
                Feed(new S.NPCRepair { Rate = 1F });
            }, 800);
            NStep(steps, "merchant_repair_tab", () => ClickAt(Centre(Win<MerchantWindow>(MerchantWindow.WindowKey)?.TabButton(MerchantTab.Repair))));
            NStep(steps, "merchant_repair_all", () => ClickAt(Centre(Win<MerchantWindow>(MerchantWindow.WindowKey)?.RepairAllButton)));
            NStep(steps, "merchant_repair_reply", () => Feed(new S.RepairItem { UniqueID = UidAt(MirGridType.Inventory, 9) }));
            NStep(steps, "merchant_inv_open", () => { Feed(new S.NPCSell()); PressBind(KeybindOptions.Inventory); });
            NStep(steps, "merchant_alt_click", () => { CMain.Alt = true; ClickCell(MirGridType.Inventory, 10, InventoryWindow.WindowKey); CMain.Alt = false; });
            NStep(steps, "merchant_alt_reply", () => { Feed(new S.SellItem { UniqueID = UidAt(MirGridType.Inventory, 10), Count = 1, Success = false }); PressBind(KeybindOptions.Inventory); });

            // ---- review fix 1: a bulk checkout's queued C.BuyItem stop once the NPC session closes (SendPacer stillValid)
            NStep(steps, "merchant_bulk_goods", () =>
            {
                Feed(new S.NPCResponse { Page = NpcPageLines() });
                UserItem sword = Item("UiTestSword");
                sword.IsShopItem = true;
                Feed(new S.NPCGoods { List = new List<UserItem> { sword }, Rate = 1F, Type = PanelType.Buy });
            }, 2500); // the pacer refills its burst meanwhile
            NStep(steps, "merchant_bulk_cart", () => { for (int i = 0; i < 8; i++) ClickGoods("UiTestSword", 0); });
            NStep(steps, "merchant_bulk_checkout_close", () =>
            {
                var w = Win<MerchantWindow>(MerchantWindow.WindowKey);
                int cart = w?.CartLines ?? 0;
                ClickAt(Centre(w?.BuyButton));
                _bulkQueued = Pacer?.Queued ?? -1;
                _log.Add($"   [fix1] cart lines={cart} queued after checkout={_bulkQueued}");
                NCheck(_bulkQueued > 0, "fix1: the checkout should leave buys queued on the pacer");
                (GameScene.Ui as ModernUi)?.CloseNpcDialog(); // Exit / walking away
                _log.Add($"   [fix1] queued after close (dropped at the next pacer spin)={Pacer?.Queued ?? -1}");
            }, 2500);
            NStep(steps, "merchant_bulk_check", () =>
            {
                int sentBuys = CountLastSent("BuyItem");
                _log.Add($"   [fix1] queued now={Pacer?.Queued ?? -1} buys sent by the checkout step={sentBuys} (only the burst before the close)");
                NCheck((Pacer?.Queued ?? -1) == 0, "fix1: the queued buys should be dropped after the NPC closed");
                NCheck(sentBuys > 0 && sentBuys <= 5, "fix1: only the pacer's immediate burst should have gone out");
            });

            // ---- review fix 2: a closed merchant hides its panels, so Alt + click sells nothing
            NStep(steps, "merchant_hide_sell_open", () => { Feed(new S.NPCResponse { Page = NpcPageLines() }); Feed(new S.NPCSell()); }, 800);
            NStep(steps, "merchant_hide_close", () =>
            {
                Root?.Wm.Close(MerchantWindow.WindowKey);
                bool dropOpen = (GameScene.Ui as ModernUi)?.IsNpcDropOpen ?? true;
                _log.Add($"   [fix2] merchant closed, IsNpcDropOpen={dropOpen}");
                NCheck(!dropOpen, "fix2: closing the merchant should close its sell panel");
                PressBind(KeybindOptions.Inventory);
            });
            NStep(steps, "merchant_hide_alt_click", () => { CMain.Alt = true; ClickCell(MirGridType.Inventory, 10, InventoryWindow.WindowKey); CMain.Alt = false; });
            NStep(steps, "merchant_hide_alt_check", () =>
            {
                int sells = CountLastSent("SellItem");
                bool locked = ItemMoves.IsLocked(MirGridType.Inventory, 10);
                _log.Add($"   [fix2] sells sent by the Alt + click={sells} cell 10 locked={locked}");
                NCheck(sells == 0 && !locked, "fix2: Alt + click with the merchant closed should send nothing");
                ItemMoves.Selected = null;
                PressBind(KeybindOptions.Inventory);
            });

            // ---- the item box: disassemble (hold), consign (price popup)
            NStep(steps, "box_new_page", () => Feed(new S.NPCResponse { Page = new List<string> { "Bring me something to break.", "<Disassemble/@disassemble>  <Consign/@consign>" } }), 700);
            NStep(steps, "box_disassemble_open", () =>
            {
                ItemInfo axe = NpcAxeInfo();
                GameScene.User.Inventory[20] = new UserItem(axe) { UniqueID = 777001, Count = 1, CurrentDura = axe.Durability, MaxDura = axe.Durability };
                Feed(new S.NPCDisassemble());
            }, 800);
            NStep(steps, "box_put_axe", () => ClickBoxCell(20));
            NStep(steps, "box_hold_press", () => Press(Centre(Win<NpcBoxWindow>(NpcBoxWindow.WindowKey)?.Confirm)), 1300);
            NStep(steps, "box_hold_release", () => Release(CMain.MPoint));
            NStep(steps, "box_disassemble_reply", () => Feed(new S.DeleteItem { UniqueID = 777001, Count = 1 }), 700);
            NStep(steps, "box_consign_open", () => { Feed(new S.NPCResponse { Page = new List<string> { "Consign something?", "<Consign/@consign>" } }); Feed(new S.NPCConsign()); }, 800);
            NStep(steps, "box_put_potion", () => ClickBoxCell(8));
            NStep(steps, "box_consign_click", () => ClickAt(Centre(Win<NpcBoxWindow>(NpcBoxWindow.WindowKey)?.Confirm)));
            NStep(steps, "box_consign_price_ok", () => ClickAt(Centre(Root.Popups.Card?.ButtonRow.Count > 0 ? Root.Popups.Card.ButtonRow[^1] : null)));
            NStep(steps, "box_consign_reply", () => Feed(new S.ConsignItem { UniqueID = UidAt(MirGridType.Inventory, 8), Success = true }), 700);

            // ---- craft
            NStep(steps, "craft_open", () =>
            {
                Feed(new S.NPCResponse { Page = new List<string> { "I can brew potions.", "<Craft/@craft>" } });
                UserItem goods = Item("UiTestPotion", 2);
                UserItem ing = Item("UiTestScroll", 2);
                GameScene.User.Inventory[25] = Item("UiTestScroll", 6);
                GameScene.RecipeInfoList.RemoveAll(r => r.Item != null && r.Item.ItemIndex == goods.ItemIndex);
                GameScene.RecipeInfoList.Add(new ClientRecipeInfo { Gold = 100, Chance = 100, Item = goods, Ingredients = new List<UserItem> { ing } });
                Feed(new S.NPCGoods { List = new List<UserItem> { goods }, Rate = 1F, Type = PanelType.Craft });
            }, 900);
            NStep(steps, "craft_max", () => ClickAt(Centre(Win<CraftWindow>(CraftWindow.WindowKey)?.MaxButton)));
            NStep(steps, "craft_click", () => ClickAt(Centre(Win<CraftWindow>(CraftWindow.WindowKey)?.CraftButton)));
            NStep(steps, "craft_reply", () =>
            {
                UserItem made = Item("UiTestPotion", 6);
                Feed(new S.GainedItem { Item = made });
                Feed(new S.CraftItem { Success = true });
            }, 800);

            // ---- walking away: the NPC leaves the view → CloseNpcDialog closes every NPC window
            NStep(steps, "npc_walk_away", () => Feed(new S.ObjectRemove { ObjectID = NpcObjectId }), 700);
            NStep(steps, "npc_close_all", () => { Root?.Popups.CloseAll(); PressBind(KeybindOptions.Closeall); DumpNpcState(_log); });
        }

        // ------------------------------------------------------------------ helpers

        private static int _bulkQueued;

        private static SendPacer Pacer => (GameScene.Ui as ModernUi)?.Pacer;

        /// <summary>A failed expectation: counted (the run fails) and logged with "!!".</summary>
        private static void NCheck(bool ok, string what)
        {
            if (ok) return;
            _failures++;
            _log.Add("!! " + what);
        }

        /// <summary>The "sent" lines of the last captured step that start with a packet name (the log block after its "== " line).</summary>
        private static int CountLastSent(string packet)
        {
            int n = 0;
            string prefix = "   sent " + packet + " ";
            for (int i = _log.Count - 1; i >= 0; i--)
            {
                if (_log[i].StartsWith("== ", StringComparison.Ordinal)) break;
                if (_log[i].StartsWith(prefix, StringComparison.Ordinal) || _log[i] == "   sent " + packet) n++;
            }
            return n;
        }

        private static List<string> NpcPageLines() => new List<string>
        {
            "Welcome, <$USERNAME>! I sell {herbs/LightGreen} and {potions/Orange}.",
            "Ask about [ITEM:9001] or the beast [MONSTER:7|Hen] nearby.",
            "",
            "<<Browse my wares/@buy>>",
            "<Buy/@buy>  <Sell/@sell>  <Repair/@repair>  <Storage/@storage>",
            "Visit (our site/https://example.org) for news.",
            "<Leave/@exit>",
        };

        private static ItemInfo NpcAxeInfo()
        {
            foreach (ItemInfo i in GameScene.ItemInfoList) if (i.Index == 9101) return i;
            var info = new ItemInfo { Index = 9101, Name = "UiNpcAxe", Type = ItemType.Weapon, Image = 41, Durability = 6000, Price = 900, Grade = ItemGrade.Rare, RequiredAmount = 30, Shape = 1, Stats = new Stats() };
            AddInfo(info);
            return info;
        }

        /// <summary>A modern-only step that first logs area A's state after the previous step.</summary>
        private static void NStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { DumpNpcState(_log); act(); }, DelayMs = delayMs, ModernOnly = true });

        private static void ClickOption(int i)
        {
            var w = Win<NpcWindow>(NpcWindow.WindowKey);
            if (w?.Options == null || i >= w.Options.Controls.Count) { _log.Add("   (no option " + i + ")"); return; }
            ClickAt(Centre(w.Options.Controls[i]));
        }

        /// <summary>Walks the second page line with the pointer and stops on the nth info link (0 = the item, 1 = the monster).</summary>
        private static void HoverInfoLink(int nth)
        {
            var w = Win<NpcWindow>(NpcWindow.WindowKey);
            NpcRichText say = w?.Say;
            if (say == null) return;
            int found = -1;
            bool on = false;
            for (int x = 0; x < say.Size.Width; x += 4)
            {
                Point p = new Point(say.DisplayLocation.X + x, say.DisplayLocation.Y + ModernRoot.S(13 * 1.6f * 1.5f));
                MouseAt(p);
                bool now = w.CardTitle.Length > 0 || (ModernTooltip.Shared?.IsShown ?? false);
                if (now && !on && ++found == nth) return;
                on = now;
            }
        }

        private static void ClickGoods(string name, int nth)
        {
            var w = Win<MerchantWindow>(MerchantWindow.WindowKey);
            if (w == null) { _log.Add("   (merchant not open)"); return; }
            int n = 0;
            foreach (var r in w.GoodsRows)
                if (r.Item.Info?.Name == name && n++ == nth) { ClickAt(Centre(r.Row)); return; }
            _log.Add($"   (no goods row {name}#{nth})");
        }

        private static void ClickSellCell(int slot)
        {
            var w = Win<MerchantWindow>(MerchantWindow.WindowKey);
            if (w == null) return;
            foreach (NpcItemCell c in w.SellCells) if (c.BagSlot == slot) { ClickAt(Centre(c)); return; }
            _log.Add("   (no sell cell " + slot + ")");
        }

        private static void ClickBoxCell(int slot)
        {
            var w = Win<NpcBoxWindow>(NpcBoxWindow.WindowKey);
            if (w == null) { _log.Add("   (box not open)"); return; }
            foreach (NpcItemCell c in w.BagCells) if (c.BagSlot == slot) { ClickAt(Centre(c)); return; }
            _log.Add("   (no box cell " + slot + ")");
        }

        /// <summary>Area A's state lines.</summary>
        private static void DumpNpcState(List<string> log)
        {
            ModernRoot root = Root;
            if (root == null || GameScene.Scene == null || GameScene.User == null || !(GameScene.Ui is ModernUi ui) || ui.Npc == null) return;
            NpcState s = ui.Npc;
            log.Add($"   [area A] npc open={s.Open} seq={s.Seq} key={s.LastPageKey} drop={(s.DropType == NpcState.NoPanel ? "none" : s.DropType.ToString())} target={s.DropItem?.Info?.Name ?? "none"} goods={(s.GoodsOpen ? s.Goods.Count.ToString() : "-")} sell={s.SellOpen} repair={(s.RepairType == NpcState.NoPanel ? "-" : s.RepairType.ToString())} craft={s.CraftGoods.Count} log={s.Log.Count} locked=[{LockedBag()}]");
            if (root.Wm.IsOpen(NpcWindow.WindowKey) && root.Wm.Get(NpcWindow.WindowKey) is NpcWindow nw)
                log.Add($"   [area A] dialog typing={nw.Typing} tab={nw.TabShown} choices={nw.ChoiceCount} log={nw.LogShown} card=\"{nw.CardTitle}\" title={nw.Title}");
            if (root.Wm.IsOpen(MerchantWindow.WindowKey) && root.Wm.Get(MerchantWindow.WindowKey) is MerchantWindow mw)
                log.Add($"   [area A] merchant tab={mw.TabShown} cart={mw.CartLines} picks={mw.Picks} rows={mw.GoodsRows.Count} note=\"{mw.Note}\"");
            if (root.Wm.IsOpen(NpcBoxWindow.WindowKey) && root.Wm.Get(NpcBoxWindow.WindowKey) is NpcBoxWindow bw)
                log.Add($"   [area A] box title={bw.Title} waiting={bw.Waiting} note=\"{bw.Note}\" result=\"{bw.Result}\"");
            if (root.Wm.IsOpen(CraftWindow.WindowKey) && root.Wm.Get(CraftWindow.WindowKey) is CraftWindow cw)
                log.Add($"   [area A] craft pending={cw.Pending} qty={cw.Quantity} result=\"{cw.ResultText}\" shadows={CountNonNull(GameScene.Models.Npc.CraftShadowItems)} slots={CountNonNull(GameScene.Models.Npc.CraftItems)}");
        }

        private static int CountNonNull(UserItem[] a)
        {
            int n = 0;
            foreach (UserItem i in a) if (i != null) n++;
            return n;
        }

        private static string LockedBag()
        {
            var list = new List<string>();
            UserItem[] inv = GameScene.User.Inventory;
            for (int i = 0; i < inv.Length; i++) if (ItemMoves.IsLocked(MirGridType.Inventory, i)) list.Add(i.ToString());
            return string.Join(",", list);
        }
    }
}
