using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 3 area "economy" (worker C): trust merchant (+ MarketSearchEx, MarketPriceHistory), game shop (+ GameshopGift),
    /// mail (+ RequestSentMail, SentMail, HideSentMail). Windows live in Client\MirScenes\Modern\Windows\ (Trust*, GameShop*, Mail*).
    /// The trust merchant and the parcel composer open only when the server opens them (S.NPCMarket, S.MailSendRequest).
    /// </summary>
    public sealed partial class ModernUi
    {
        partial void RegisterEconomyWindows()
        {
            GameShopWindow.HasNew = false;
            Root.Wm.Register(TrustWindow.WindowKey, () => new TrustWindow());
            Root.Wm.Register(GameShopWindow.WindowKey, () => new GameShopWindow());
            Root.Wm.Register(MailWindow.WindowKey, () => new MailWindow());
        }

        /// <summary>KeybindOptions.GameShop toggles the shop (GameScene_KeyDown: GameShopDialog). The desktop has no mail or trust key.</summary>
        private partial bool EconomyKeyBind(KeybindOptions function)
        {
            if (function != KeybindOptions.GameShop) return false;
            Root.Wm.Toggle(GameShopWindow.WindowKey);
            return true;
        }

        private TrustWindow OpenTrust => Root != null && !Root.IsDisposed && Root.Wm.IsOpen(TrustWindow.WindowKey) ? Root.Wm.Get(TrustWindow.WindowKey) as TrustWindow : null;
        private T Built<T>(string key) where T : ModernWindow => Root != null && !Root.IsDisposed ? Root.Wm.Get(key) as T : null;

        /// <summary>S.NPCMarket, before the listings (TrustMerchantDialog.Show: a fresh open picks the Market panel).</summary>
        public void MarketOpening()
        {
            if (Root == null || Root.IsDisposed || Root.Wm.IsOpen(TrustWindow.WindowKey)) return;
            Root.Wm.Open(TrustWindow.WindowKey);
        }

        public void MarketListed(int pages) => OpenTrust?.Listed(pages);

        public bool IsMarketOpen() => OpenTrust != null;

        public void MarketPageAdded() => OpenTrust?.PageAdded();

        /// <summary>S.ReceiveMail, after User.Mail and NewMail were updated (the HUD badge reads them itself).</summary>
        public void MailReceived()
        {
            if (Root != null && !Root.IsDisposed && Root.Wm.IsOpen(MailWindow.WindowKey)) Built<MailWindow>(MailWindow.WindowKey)?.MailChanged();
        }

        /// <summary>S.MailSendRequest (a post office page): the parcel composer with the bag.</summary>
        public void MailSendRequest() => Built<MailWindow>(MailWindow.WindowKey)?.ComposeParcel();

        /// <summary>S.ParcelCollected: -1 / 0 also get the desktop's message box (the classic MailListDialog shows them).</summary>
        public void ParcelCollected(sbyte result)
        {
            if (Root == null || Root.IsDisposed) return;
            if (Root.Wm.IsOpen(MailWindow.WindowKey)) Built<MailWindow>(MailWindow.WindowKey)?.ParcelCollected(result);
            if (result == -1 || result == 0)
                Root.Popups.Confirm(GameLanguage.ClientTextMap.GetLocalization(result == -1 ? ClientTextKeys.NoParcelsToCollect : ClientTextKeys.AllParcelsCollected),
                    PopupButtons.Ok, PopupKind.Info, null, false, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernEcoMail));
        }

        public void MailCost(uint cost)
        {
            if (Root != null && !Root.IsDisposed && Root.Wm.IsOpen(MailWindow.WindowKey)) Built<MailWindow>(MailWindow.WindowKey)?.CostArrived(cost);
        }

        /// <summary>S.GameShopInfo dated within 7 days (GameShopDialog.New.Visible).</summary>
        public void GameShopNewItems()
        {
            GameShopWindow.HasNew = true;
            if (Root != null && !Root.IsDisposed && Root.Wm.IsOpen(GameShopWindow.WindowKey)) Built<GameShopWindow>(GameShopWindow.WindowKey)?.ShopChanged();
        }

        public void GameShopStockChanged()
        {
            if (Root != null && !Root.IsDisposed && Root.Wm.IsOpen(GameShopWindow.WindowKey)) Built<GameShopWindow>(GameShopWindow.WindowKey)?.ShopChanged();
        }

        /// <summary>ItemMoves "To Mail" attached a bag item to a parcel cell: ask for the postage again.</summary>
        public void ItemMailPostageChanged()
        {
            if (Root != null && !Root.IsDisposed && Root.Wm.IsOpen(MailWindow.WindowKey)) Built<MailWindow>(MailWindow.WindowKey)?.PostageChanged();
        }

        /// <summary>S.MailSent, after GameScene unlocked the bag cells (MailComposeParcelDialog.Hide).</summary>
        public void ItemMailSent(sbyte result)
        {
            if (Root != null && !Root.IsDisposed && Root.Wm.IsOpen(MailWindow.WindowKey)) Built<MailWindow>(MailWindow.WindowKey)?.SentResult(result);
        }

        /// <summary>S.MarketSuccess / S.MarketFail text: the trust window logs it, and a popup shows it (classic: a message box).</summary>
        public void MarketMessage(string text, bool fail)
        {
            if (Root == null || Root.IsDisposed || string.IsNullOrEmpty(text)) return;
            if (Root.Wm.IsOpen(TrustWindow.WindowKey)) Built<TrustWindow>(TrustWindow.WindowKey)?.ServerMessage(text, fail);
            Root.Popups.Confirm(text, Core.PopupButtons.Ok, fail ? Core.PopupKind.Warning : Core.PopupKind.Info, null);
        }
    }
}
