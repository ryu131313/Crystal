using Client.MirObjects;
namespace Client.MirScenes.Models
{
    /// <summary>
    /// The other side of a trade (port of the WebClient/src/game/trade.ts subset). Was GuestTradeDialog.GuestItems (static),
    /// GuestName and GuestGold. The user's own offer stays on UserObject (Trade, TradeLocked).
    /// </summary>
    public sealed class TradeState
    {
        public UserItem[] GuestItems = new UserItem[10];
        public string GuestName;
        public uint GuestGold;

        public readonly Emitter Changed = new Emitter();

        /// <summary>
        /// S.TradeConfirm / S.TradeCancel (not unlock): was the data half of TradeDialog.TradeReset + GuestTradeDialog.TradeReset.
        /// Clears the guest side and the user's offer (User.Trade, TradeGoldAmount, TradeLocked).
        /// </summary>
        public void Reset(UserObject user)
        {
            for (int i = 0; i < GuestItems.Length; i++)
                GuestItems[i] = null;
            GuestName = string.Empty;
            GuestGold = 0;
            if (user != null)
            {
                for (int i = 0; i < user.Trade.Length; i++)
                    user.Trade[i] = null;
                user.TradeGoldAmount = 0;
                user.TradeLocked = false;
            }
            Changed.Emit();
        }
    }
}
