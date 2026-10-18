using Client.MirControls;
using Client.MirScenes;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 3 area "economy" steps (modern only): the mail window (folders, read, collect, mark all read, lock, the sent folder
    /// and remove-from-sent, a letter, a parcel from S.MailSendRequest with a bag item through ItemMoves' "To Mail", the postage
    /// quote, take out, send and S.MailSent), the game shop (catalogue, product view, quantity, buy and the charge, gift to a
    /// friend, sections) and the trust merchant (S.NPCMarket opens it, price history, buy now and the answer, a bid, the next
    /// page, the watchlist, a category search, my listings with collect / sell now / collect sold, consign from the side bag and
    /// S.ConsignItem, the NPC page closing it). Starts from a fresh game and closes everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddEconomySteps(List<Step> steps)
        {
            EStep(steps, "eco_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            EStep(steps, "eco_enter", () =>
            {
                EnterGame();
                SetScale(1f, false);
                Feed(new S.ServerRules { MailCostPer1KGold = 100, MailItemInsurancePercentage = 10, MailCapacity = 100, MailFreeWithStamp = true });
            }, 1500);

            // ---- mail
            EStep(steps, "mail_open", () =>
            {
                DateTime now = CMain.Now;
                var letter = new ClientMail { MailID = 11, SenderName = "Alice", Message = "Dinner at the Bichon inn?\r\nBring the potions, we leave at eight.", DateSent = now.AddMinutes(-5), CanReply = true };
                var parcel = new ClientMail { MailID = 12, SenderName = "Bob", Message = "Your share of the loot", DateSent = now.AddHours(-3), Gold = 25000, Collected = true, CanReply = true, Opened = true };
                parcel.Items.Add(Item("UiTestSword"));
                var waiting = new ClientMail { MailID = 13, SenderName = "Carol", Message = "Scrolls for the trip", DateSent = now.AddDays(-1), CanReply = true };
                waiting.Items.Add(Item("UiTestScroll", 4));
                var system = new ClientMail { MailID = 14, SenderName = "Game Master", Message = "Welcome to the new mail window.", DateSent = now.AddDays(-2), Locked = true, Opened = true };
                Feed(new S.ReceiveMail { Mail = new List<ClientMail> { letter, parcel, waiting, system } });
                Root.Wm.Open(MailWindow.WindowKey);
            }, 900);
            EStep(steps, "mail_read_letter", () => ClickAt(Centre(MailRow(0))), 700);
            EStep(steps, "mail_read_reply", () => Feed(new S.ReceiveMail { Mail = MailsWith(11, m => m.Opened = true) }), 700);
            EStep(steps, "mail_parcel", () => ClickAt(Centre(MailRow(1))), 700);
            EStep(steps, "mail_collect", () => ClickAt(Centre(FindButtonPrefix(MailWin?.ReadBar, "Collect"))));
            EStep(steps, "mail_collect_reply", () => { Feed(new S.ParcelCollected { Result = 1 }); Feed(new S.ReceiveMail { Mail = MailsWith(12, m => { m.Gold = 0; m.Items.Clear(); }) }); }, 700);
            EStep(steps, "mail_mark_all_read", () => ClickAt(Centre(FindButton(MailWin, W(ClientTextKeys.ModernEcoMailMarkRead)))));
            EStep(steps, "mail_mark_all_reply", () => Feed(new S.ReceiveMail { Mail = MailsWith(13, m => m.Opened = true) }));
            EStep(steps, "mail_locked_folder", () => ClickAt(Centre(MailWin?.FolderRow(3))), 700);
            EStep(steps, "mail_system_locked", () => ClickAt(Centre(MailRow(0))), 700);
            EStep(steps, "mail_unlock", () => ClickAt(Centre(FindButton(MailWin?.ReadBar, W(ClientTextKeys.ModernEcoMailUnlock)))));
            EStep(steps, "mail_sent_folder", () => ClickAt(Centre(MailWin?.FolderRow(4))), 700);
            EStep(steps, "mail_sent_reply", () =>
            {
                long now = CMain.Now.ToBinary();
                Feed(new S.SentMail { Mails = new List<ClientSentMail>
                {
                    new ClientSentMail { MailID = 21, RecipientName = "Dave", Message = "Here is the ring I promised.", Gold = 1000, ItemCount = 1, DateSent = now, Opened = true, Collected = true },
                    new ClientSentMail { MailID = 22, RecipientName = "Erin", Message = "See you tonight", DateSent = now },
                } });
            }, 700);
            EStep(steps, "mail_sent_pick", () => ClickAt(Centre(MailRow(0))), 700);
            EStep(steps, "mail_sent_hide_arm", () => ClickAt(Centre(Find<ConfirmTwiceButton>(MailWin?.ReadBar))));
            EStep(steps, "mail_sent_hide", () => ClickAt(Centre(Find<ConfirmTwiceButton>(MailWin?.ReadBar))), 700);
            EStep(steps, "mail_parcel_request", () => Feed(new S.MailSendRequest()), 900);
            EStep(steps, "mail_parcel_attach", () => { ClickCell(MirGridType.Inventory, 10, Inv); ClickCell(MirGridType.Mail, 0, MailWindow.WindowKey); }, 700);
            EStep(steps, "mail_parcel_cost", () => Feed(new S.MailCost { Cost = 30 }), 700);
            EStep(steps, "mail_parcel_take_out", () => ClickAt(Centre(MailWin?.SlotX(0))));
            EStep(steps, "mail_parcel_unlock_echo", () => Feed(new S.MailLockedItem { UniqueID = UidAt(MirGridType.Inventory, 10), Locked = false }));
            EStep(steps, "mail_parcel_attach_again", () => { ClickCell(MirGridType.Inventory, 10, Inv); ClickCell(MirGridType.Mail, 0, MailWindow.WindowKey); });
            EStep(steps, "mail_parcel_fill", () =>
            {
                Feed(new S.MailCost { Cost = 30 });
                MailWindow w = MailWin;
                if (w != null) { w.ToField.Text = "Bob"; w.BodyField.Text = "The ring, as promised."; }
            }, 700);
            EStep(steps, "mail_parcel_send", () => ClickAt(Centre(FindButton(MailWin, W(ClientTextKeys.ModernEcoMailSend)))), 700);
            EStep(steps, "mail_parcel_sent", () => Feed(new S.MailSent { Result = 1 }), 800);
            EStep(steps, "mail_send_cooldown", () => { }, 9600); // PlayerObject.NextMailTime: 10 s between sends
            EStep(steps, "mail_letter", () =>
            {
                ClickAt(Centre(FindButton(MailWin, W(ClientTextKeys.ModernEcoMailNew))));
                MailWindow w = MailWin;
                if (w != null) { w.ToField.Text = "Alice"; w.BodyField.Text = "Yes! See you at eight."; }
            }, 700);
            EStep(steps, "mail_letter_send", () => ClickAt(Centre(FindButton(MailWin, W(ClientTextKeys.ModernEcoMailSend)))), 700);
            EStep(steps, "mail_close", () => Root.Wm.Close(MailWindow.WindowKey));

            // ---- game shop
            EStep(steps, "shop_open", () =>
            {
                Feed(new S.GainedCredit { Credit = 5000 });
                Feed(new S.FriendUpdate { Friends = new List<ClientFriend> { new ClientFriend { Index = 2, Name = "UiAlly", Online = true }, new ClientFriend { Index = 3, Name = "Zed" } } });
                foreach (GameShopItem it in ShopItems()) Feed(new S.GameShopInfo { Item = it, StockLevel = it.Stock });
                PressBind(KeybindOptions.GameShop);
            }, 1000);
            EStep(steps, "shop_card", () => ClickAt(Centre(Find<PaintBox>(ShopWin?.Grid))), 800);
            EStep(steps, "shop_qty_up", () => { ClickAt(Centre(FindButton(ShopWin?.Modal, "+"))); ClickAt(Centre(FindButton(ShopWin?.Modal, "+"))); });
            EStep(steps, "shop_buy", () => ClickAt(Centre(ShopWin?.BuyButton)));
            EStep(steps, "shop_buy_yes", () => Key(Keys.Enter));
            EStep(steps, "shop_charged", () => Feed(new S.LoseCredit { Credit = 1200 }), 800); // 3 x 400 credits: the window counts the purchase only on the full cost
            EStep(steps, "shop_gift_open", () => ClickAt(Centre(ShopWin?.GiftButton)), 700);
            EStep(steps, "shop_gift_send", () => ClickAt(Centre(ShopWin?.BuyButton)));
            EStep(steps, "shop_gift_yes", () => Key(Keys.Enter), 700);
            EStep(steps, "shop_view_close", () => Key(Keys.Escape));
            EStep(steps, "shop_deals", () => ClickAt(Centre(FindByHint(ShopWin, W(ClientTextKeys.ModernEcoShopDeals)))), 800);
            EStep(steps, "shop_close", () => PressBind(KeybindOptions.GameShop));

            // ---- trust merchant
            EStep(steps, "trust_open", () => Feed(new S.NPCMarket { Listings = Listings(false), Pages = 2, UserMode = false }), 1000);
            EStep(steps, "trust_pick", () => ClickAt(Centre(TrustWin?.BrowseRow(0))), 700);
            EStep(steps, "trust_history", () => Feed(new S.MarketPriceHistory { ItemIndex = 9002, Sales = Sales() }), 700);
            EStep(steps, "trust_buy", () => ClickAt(Centre(TrustWin?.BuyButton)));
            EStep(steps, "trust_buy_yes", () => Key(Keys.Enter));
            EStep(steps, "trust_buy_answer", () => Feed(new S.MarketSuccess { Message = "You bought UiTestSword." }), 900);
            EStep(steps, "trust_answer_close", () => CloseAllPrompts());
            EStep(steps, "trust_pick_auction", () => ClickAt(Centre(TrustWin?.BrowseRow(1))), 700);
            EStep(steps, "trust_bid_quick", () => ClickAt(Centre(FindButton(TrustWin, "+10%"))));
            EStep(steps, "trust_bid", () => ClickAt(Centre(TrustWin?.BidButton)));
            EStep(steps, "trust_bid_yes", () => Key(Keys.Enter), 700);
            EStep(steps, "trust_star", () => ClickAt(Centre(TrustWin?.Star(2))));
            EStep(steps, "trust_detail_back", () => ClickAt(Centre(FindButton(TrustWin, W(ClientTextKeys.ModernEcoBack)))));
            EStep(steps, "trust_next_page", () => ClickAt(Centre(FindButton(TrustWin, "›"))), 800);
            EStep(steps, "trust_page_reply", () => Feed(new S.NPCMarketPage { Listings = Listings(false, 100) }), 700);
            EStep(steps, "trust_category", () => ClickAt(Centre(TrustWin?.Categories.Content.Controls.Count > 1 ? TrustWin.Categories.Content.Controls[1] : null)), 900);
            EStep(steps, "trust_watch_tab", () => ClickTabOf(TrustWin, 3), 700);
            EStep(steps, "trust_mine_tab", () => ClickTabOf(TrustWin, 1), 900);
            EStep(steps, "trust_mine_reply", () => Feed(new S.NPCMarket { Listings = Listings(true), Pages = 1, UserMode = true }), 800);
            EStep(steps, "trust_collect_one", () => ClickAt(Centre(FindButtonPrefix(TrustWin?.MineActions(0), "Collect"))));
            EStep(steps, "trust_collect_wait", () => { }, 3300); // Sold: no question; MarketTime runs out
            EStep(steps, "trust_sell_now", () => ClickAt(Centre(FindButtonPrefix(TrustWin?.MineActions(1), "Sell now"))));
            EStep(steps, "trust_sell_now_yes", () => Key(Keys.Enter), 3300);
            EStep(steps, "trust_collect_sold", () => ClickAt(Centre(TrustWin?.CollectButton)), 900);
            EStep(steps, "trust_sell_tab", () => ClickTabOf(TrustWin, 2), 900);
            EStep(steps, "trust_sell_pick", () => ClickAt(Centre(TrustWin?.BagCells.Count > 1 ? TrustWin.BagCells[1] : null)), 700);
            EStep(steps, "trust_sell_price", () => { if (TrustWin != null) TrustWin.PriceField.Text = "12000"; }, 700);
            EStep(steps, "trust_list", () => ClickAt(Centre(TrustWin?.ListButton)));
            EStep(steps, "trust_list_yes", () => Key(Keys.Enter), 700);
            EStep(steps, "trust_consign_reply", () => Feed(new S.ConsignItem { UniqueID = UidAt(MirGridType.Inventory, 9), Success = true }), 700);
            EStep(steps, "trust_log_tab", () => ClickTabOf(TrustWin, 4), 700);
            EStep(steps, "trust_npc_page_closes", () => Feed(new S.NPCResponse { Page = new List<string> { "Come back any time." } }), 800);
            EStep(steps, "eco_close_all", () => { Root?.Popups.CloseAll(); CloseAllPrompts(); PressBind(KeybindOptions.Closeall); DumpWindows(); });
        }

        // ------------------------------------------------------------------ helpers

        private static MailWindow MailWin => Win<MailWindow>(MailWindow.WindowKey);
        private static GameShopWindow ShopWin => Win<GameShopWindow>(GameShopWindow.WindowKey);
        private static TrustWindow TrustWin => Win<TrustWindow>(TrustWindow.WindowKey);

        private static void EStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { DumpEcoState(_log); act(); }, DelayMs = delayMs, ModernOnly = true });

        private static MirControl MailRow(int i)
        {
            ScrollView list = MailWin?.List;
            if (list == null) return null;
            int n = 0;
            foreach (MirControl c in list.Content.Controls)
                if (c is PaintBox && n++ == i) return c;
            return null;
        }

        /// <summary>The current mailbox with one mail changed (the server's fresh S.ReceiveMail).</summary>
        private static List<ClientMail> MailsWith(ulong id, Action<ClientMail> change)
        {
            var list = new List<ClientMail>(GameScene.User.Mail);
            foreach (ClientMail m in list) if (m.MailID == id) change(m);
            return list;
        }

        private static ModernButton FindButtonPrefix(MirControl root, string prefix)
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                if (root.Controls[i] is ModernButton b && b.Visible && b.Text.StartsWith(prefix, StringComparison.Ordinal)) return b;
                ModernButton deep = FindButtonPrefix(root.Controls[i], prefix);
                if (deep != null) return deep;
            }
            return null;
        }

        private static List<GameShopItem> ShopItems()
        {
            DateTime now = CMain.Now;
            GameShopItem Make(int g, string name, uint credit, uint gold, ushort count, string cat, bool deal, bool top, int stock, int daysOld, uint dealCredit = 0) => new GameShopItem
            {
                GIndex = g, ItemIndex = Info(name).Index, Info = Info(name), CreditPrice = credit, GoldPrice = gold, Count = count, Class = "All", Category = cat, Deal = deal, TopItem = top,
                Stock = stock, Date = now.AddDays(-daysOld), CanBuyCredit = credit > 0, CanBuyGold = gold > 0, DealCreditPrice = dealCredit,
            };
            return new List<GameShopItem>
            {
                Make(501, "UiTestPotion", 100, 2000, 10, "potions", true, false, 0, 2, 150),
                Make(502, "UiTestSword", 900, 0, 1, "weapons", false, true, 5, 30),
                Make(503, "UiTestRing", 400, 50000, 1, "jewellery", false, true, 0, 30),
                Make(504, "UiTestScroll", 0, 800, 5, "scrolls", false, false, 0, 30),
                Make(505, "UiTestDress", 650, 0, 1, "armour", true, false, 0, 40, 800),
            };
        }

        private static List<ClientAuction> Listings(bool mine, ulong idBase = 0)
        {
            DateTime now = CMain.Now;
            if (mine)
                return new List<ClientAuction>
                {
                    new ClientAuction { AuctionID = 301, Item = Item("UiTestRing"), Seller = "Sold", Price = 40000, ConsignmentDate = now.AddDays(-2), ItemType = MarketItemType.Consign },
                    new ClientAuction { AuctionID = 302, Item = Item("UiTestDress"), Seller = "Bid Met", Price = 22000, ConsignmentDate = now.AddDays(-1), ItemType = MarketItemType.Auction },
                    new ClientAuction { AuctionID = 303, Item = Item("UiTestPotion", 20), Seller = "For Sale", Price = 6000, ConsignmentDate = now.AddHours(-5), ItemType = MarketItemType.Consign },
                    new ClientAuction { AuctionID = 304, Item = Item("UiTestScroll", 3), Seller = "Expired", Price = 9000, ConsignmentDate = now.AddDays(-8), ItemType = MarketItemType.Consign },
                };
            var list = new List<ClientAuction>();
            string[] names = { "UiTestSword", "UiTestDress", "UiTestRing", "UiTestPotion", "UiTestScroll" };
            for (int i = 0; i < 10; i++)
            {
                string n = names[i % names.Length];
                list.Add(new ClientAuction
                {
                    AuctionID = idBase + 200 + (ulong)i, Item = Item(n, (ushort)(n == "UiTestPotion" ? 20 : 1)), Seller = "Seller" + i, Price = (uint)(15000 + i * 2500),
                    ConsignmentDate = now.AddDays(-6.98 + i * 0.2), ItemType = i % 2 == 1 ? MarketItemType.Auction : MarketItemType.Consign,
                });
            }
            return list;
        }

        private static List<ClientMarketSale> Sales()
        {
            uint[] p = { 14000, 15500, 13800, 16000, 17200, 15000, 16800, 18000, 17500, 16400 };
            var list = new List<ClientMarketSale>();
            for (int i = 0; i < p.Length; i++) list.Add(new ClientMarketSale { Price = p[i], Date = CMain.Now.AddDays(-p.Length + i).ToBinary() });
            return list;
        }

        private static string BagLocks()
        {
            UserItem[] inv = GameScene.User?.Inventory;
            if (inv == null) return string.Empty;
            var l = new List<string>();
            for (int i = 0; i < inv.Length; i++) if (inv[i] != null && ItemMoves.IsLocked(MirGridType.Inventory, i)) l.Add(i.ToString());
            return string.Join(",", l);
        }

        /// <summary>Area C (economy) state lines.</summary>
        private static void DumpEcoState(List<string> log)
        {
            ModernRoot root = Root;
            if (root == null || GameScene.Scene == null || GameScene.User == null) return;
            if (MailWin is MailWindow m)
                log.Add($"   [eco] mail folder={m.FolderShown} mode={m.ModeShown} note=\"{m.Note}\" err=\"{m.Error}\" cost={m.CostShown} gold={m.GoldShown} stamped={m.StampedShown} sending={m.Sending} parcel=[{Items(GameScene.Models.Mail.ParcelItems)}] idx=[{string.Join(",", GameScene.Models.Mail.ParcelItemsIdx)}] sent={GameScene.Models.Mail.Sent.Count} locks=[{BagLocks()}]");
            if (ShopWin is GameShopWindow s)
                log.Add($"   [eco] shop section={s.SectionShown} class=\"{s.ClassShown}\" view={s.ViewShown} qty={s.Quantity} gift={s.GiftOpen} pending={s.PendingShown} history={s.HistoryCount} note=\"{s.NoteShown}\" credit={GameScene.Credit} gold={GameScene.Gold}");
            if (TrustWin is TrustWindow t)
                log.Add($"   [eco] trust tab={t.TabShown} page={t.PageShown}/{t.PageCount} listings={GameScene.Models.Market.Listings.Count} userMode={GameScene.Models.Market.UserMode} type={GameScene.Models.Market.MarketType} selected={t.SelectedListing?.AuctionID} pending={t.IsPending} watch={t.WatchCount} sell={GameScene.Models.Market.SellItem?.Info?.Name} msg=\"{t.Message}\" locks=[{BagLocks()}]");
        }
    }
}
