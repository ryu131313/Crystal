namespace Client.MirScenes.Modern
{
    public sealed partial class ModernUi
    {
        public bool IsNpcDropOpen => false;
        public void ItemNpcDropQuickSell(MouseEventArgs e) => NotPorted(nameof(ItemNpcDropQuickSell));
        public void ItemAskAmount(string title, int image, uint max, Action<uint> ok) => Root?.Popups.Amount(title, image, max, ok);
        public void ItemAskAmountMessage(string title, int image, string message, Action ok) =>
            Root?.Popups.Confirm(message, Core.PopupButtons.OkCancel, Core.PopupKind.Question, (a, _) => { if (a == Core.PopupAnswer.Ok) ok?.Invoke(); }, false, title);
        public void ItemAskYesNo(string message, Action yes) => Root?.Popups.Ask(message, ok => { if (ok) yes?.Invoke(); });
        public void ItemMessage(string message) => Root?.Popups.Confirm(message, Core.PopupButtons.Ok, Core.PopupKind.Info, null);
        public void ItemShowSockets(MirGridType grid, UserItem item) => NotPorted(nameof(ItemShowSockets));

        public int ItemFreeSocketSlot()
        {
            NotPorted(nameof(ItemFreeSocketSlot));
            return -1;
        }

        public void ItemAwakeItemChanged() => NotPorted(nameof(ItemAwakeItemChanged));
        public void ItemAwakeTypeSelect(int index) => NotPorted(nameof(ItemAwakeTypeSelect));
        public void ItemMailPostageChanged() => NotPorted(nameof(ItemMailPostageChanged));

        public void ItemTradeUnlocked() => NotPorted(nameof(ItemTradeUnlocked));
        public void ItemDuraRefresh() => ModernEvents.Durability.Emit(null);
        public void ItemDuraUpdate(UserItem item) => ModernEvents.Durability.Emit(item);
        public void ItemAwakeningClosing() => NotPorted(nameof(ItemAwakeningClosing));
        public void ItemAwakeningResult(int result) => NotPorted(nameof(ItemAwakeningResult));
        public void ItemMailSent() => NotPorted(nameof(ItemMailSent));
        public void ItemRentalDeposited() => NotPorted(nameof(ItemRentalDeposited));
    }
}
