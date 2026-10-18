namespace Client.MirScenes.Classic
{
    public sealed partial class ClassicUi
    {
        public bool ItemDeleteModeClick(ItemSlotRef cell, MouseButtons button) => _scene.ClassicItemDeleteModeClick(cell, button);
        public void ItemChatLink(ItemSlotRef cell) => _scene.ClassicItemChatLink(cell);
        public bool IsNpcDropOpen => _scene.ClassicIsNpcDropOpen;
        public void ItemNpcDropQuickSell(MouseEventArgs e) => _scene.ClassicItemNpcDropQuickSell(e);
        public void ItemAskAmount(string title, int image, uint max, Action<uint> ok) => _scene.ClassicItemAskAmount(title, image, max, ok);
        public void ItemAskAmountMessage(string title, int image, string message, Action ok) => _scene.ClassicItemAskAmountMessage(title, image, message, ok);
        public void ItemAskYesNo(string message, Action yes) => _scene.ClassicItemAskYesNo(message, yes);
        public void ItemMessage(string message) => _scene.ClassicItemMessage(message);
        public void ItemShowSockets(MirGridType grid, UserItem item) => _scene.ClassicItemShowSockets(grid, item);
        public int ItemFreeSocketSlot() => _scene.ClassicItemFreeSocketSlot();
        public void ItemAwakeItemChanged() => _scene.ClassicItemAwakeItemChanged();
        public void ItemAwakeTypeSelect(int index) => _scene.ClassicItemAwakeTypeSelect(index);
        public void ItemMailPostageChanged() => _scene.ClassicItemMailPostageChanged();

        public void ItemTradeUnlocked() => _scene.ClassicItemTradeUnlocked();
        public void ItemDuraRefresh() => _scene.ClassicItemDuraRefresh();
        public void ItemDuraUpdate(UserItem item) => _scene.ClassicItemDuraUpdate(item);
        public void ItemAwakeningClosing() => _scene.ClassicItemAwakeningClosing();
        public void ItemAwakeningResult(int result) => _scene.ClassicItemAwakeningResult(result);
        public void ItemMailSent(sbyte result) => _scene.ClassicItemMailSent(); // classic closes either way
        public void ItemRentalDeposited() => _scene.ClassicItemRentalDeposited();
    }
}
