namespace Client.MirScenes
{
    // Item seam members (Desktop-Modern-UI-Plan seam step 3): the classic UI pieces of the item pick / drop / use logic
    // (ItemMoves.Logic.cs) and of GameScene's item reply handlers. Called on the UI thread only.
    public partial interface IGameUi
    {
        // ---- Item cells (ItemMoves) ----

        /// <summary>An item slot was clicked while the inventory may be in delete mode: true = the click was used for that.</summary>
        bool ItemDeleteModeClick(ItemSlotRef cell, MouseButtons button);
        /// <summary>Shift + right-click on an item: link it into the chat input.</summary>
        void ItemChatLink(ItemSlotRef cell);
        /// <summary>The NPC sell / repair / consign drop panel is open (Alt + click quick sell).</summary>
        bool IsNpcDropOpen { get; }
        /// <summary>Alt + click quick sell, after the item was picked up: drop it on the NPC panel and confirm.</summary>
        void ItemNpcDropQuickSell(MouseEventArgs e);
        /// <summary>Asks for an amount up to max; ok gets the amount when confirmed.</summary>
        void ItemAskAmount(string title, int image, uint max, Action<uint> ok);
        /// <summary>Asks to confirm with a message (purchase of one item); ok runs when confirmed.</summary>
        void ItemAskAmountMessage(string title, int image, string message, Action ok);
        /// <summary>A yes / no question; yes runs on Yes.</summary>
        void ItemAskYesNo(string message, Action yes);
        /// <summary>A message with an OK button.</summary>
        void ItemMessage(string message);
        /// <summary>Ctrl + right-click on an equipment / inventory item: show its sockets.</summary>
        void ItemShowSockets(MirGridType grid, UserItem item);
        /// <summary>The first free socket slot shown for GameScene.SelectedItem, or -1.</summary>
        int ItemFreeSocketSlot();
        /// <summary>The awakening base item slot changed (classic: NPCAwakeDialog.ItemCell_Click).</summary>
        void ItemAwakeItemChanged();
        /// <summary>A new awakening base item was placed: select awakening type index.</summary>
        void ItemAwakeTypeSelect(int index);
        /// <summary>A parcel item was attached to the mail being composed.</summary>
        void ItemMailPostageChanged();

        // ---- Item reply handlers ----

        /// <summary>A trade item move came back: the trade is unlocked again (classic: TradeDialog.ChangeLockState(false)).</summary>
        void ItemTradeUnlocked();
        /// <summary>Equipment changed: refresh the durability panel from the equipment.</summary>
        void ItemDuraRefresh();
        /// <summary>An item was equipped: update its durability panel entry.</summary>
        void ItemDuraUpdate(UserItem item);
        /// <summary>S.Awakening arrived, before the slot updates: close the awakening window.</summary>
        void ItemAwakeningClosing();
        /// <summary>S.Awakening, after the slot updates: show the result (negative = failure reason).</summary>
        void ItemAwakeningResult(int result);
        /// <summary>S.MailSent, after the inventory locks were cleared: close the parcel window.</summary>
        void ItemMailSent(sbyte result);
        /// <summary>S.DepositRentalItem succeeded: ask for the rental period if none is set.</summary>
        void ItemRentalDeposited();
    }
}
