namespace Client.MirScenes.Modern
{
    public sealed partial class ModernUi
    {
        public void ItemAskAmount(string title, int image, uint max, Action<uint> ok) => Root?.Popups.Amount(title, image, max, ok);
        public void ItemAskAmountMessage(string title, int image, string message, Action ok) =>
            Root?.Popups.Confirm(message, Core.PopupButtons.OkCancel, Core.PopupKind.Question, (a, _) => { if (a == Core.PopupAnswer.Ok) ok?.Invoke(); }, false, title);
        public void ItemAskYesNo(string message, Action yes) => Root?.Popups.Ask(message, ok => { if (ok) yes?.Invoke(); });
        public void ItemMessage(string message) => Root?.Popups.Confirm(message, Core.PopupButtons.Ok, Core.PopupKind.Info, null);



        /// <summary>A trade item came back to the bag: the user's side unlocks (classic: TradeDialog.ChangeLockState(false)).</summary>
        public void ItemTradeUnlocked()
        {
            if (GameScene.User != null) GameScene.User.TradeLocked = false;
            SocialTradeItemReturned();
        }
        public void ItemDuraRefresh() => ModernEvents.Durability.Emit(null);
        public void ItemDuraUpdate(UserItem item) => ModernEvents.Durability.Emit(item);
    }
}
