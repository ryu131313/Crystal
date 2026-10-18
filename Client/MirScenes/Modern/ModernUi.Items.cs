namespace Client.MirScenes.Modern
{
    public sealed partial class ModernUi
    {
        public bool ItemDeleteModeClick(ItemSlotRef cell, MouseButtons button) => false;
        public void ItemChatLink(ItemSlotRef cell) => NotPorted(nameof(ItemChatLink));
        public bool IsNpcDropOpen => false;
        public void ItemNpcDropQuickSell(MouseEventArgs e) => NotPorted(nameof(ItemNpcDropQuickSell));
        public void ItemAskAmount(string title, int image, uint max, Action<uint> ok) => NotPorted(nameof(ItemAskAmount));
        public void ItemAskAmountMessage(string title, int image, string message, Action ok) => NotPorted(nameof(ItemAskAmountMessage));
        public void ItemAskYesNo(string message, Action yes) => NotPorted(nameof(ItemAskYesNo));
        public void ItemMessage(string message) => NotPorted(nameof(ItemMessage));
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
        public void ItemDuraRefresh() => NotPorted(nameof(ItemDuraRefresh));
        public void ItemDuraUpdate(UserItem item) => NotPorted(nameof(ItemDuraUpdate));
        public void ItemAwakeningClosing() => NotPorted(nameof(ItemAwakeningClosing));
        public void ItemAwakeningResult(int result) => NotPorted(nameof(ItemAwakeningResult));
        public void ItemMailSent() => NotPorted(nameof(ItemMailSent));
        public void ItemRentalDeposited() => NotPorted(nameof(ItemRentalDeposited));
    }
}
