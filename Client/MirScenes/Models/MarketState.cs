namespace Client.MirScenes.Models
{
    /// <summary>
    /// The trust merchant session (port of the WebClient/src/game/market.ts subset). Was the TrustMerchantDialog statics
    /// UserMode, MarketType, SearchTime, MarketTime and SellItemSlot (the MirGridType.TrustMerchant slot), and its listings.
    /// The selected row stays in the window (it is a control).
    /// </summary>
    public sealed class MarketState
    {
        public bool UserMode;
        public MarketPanelType MarketType;
        /// <summary>CMain.Time gates for search / page requests.</summary>
        public long SearchTime, MarketTime;
        public UserItem SellItem;
        public List<ClientAuction> Listings = new List<ClientAuction>();

        /// <summary>S.MarketPriceHistory: recent sales per item index (C.MarketPriceHistory asks; at most MaxSales each).</summary>
        public readonly Dictionary<int, List<ClientMarketSale>> PriceHistory = new Dictionary<int, List<ClientMarketSale>>();
        public readonly Emitter<int> PriceHistoryChanged = new Emitter<int>();

        public readonly Emitter Changed = new Emitter();
    }
}
