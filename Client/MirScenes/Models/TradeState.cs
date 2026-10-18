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
    }
}
