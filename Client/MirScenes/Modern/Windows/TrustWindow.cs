// Ported from WebClient/src/ui/modern/windows/trust.ts (TrustWindow): the new UI's Trust Merchant (market / auction house) over
// GameScene.Models.Market (listings, UserMode, MarketType, SearchTime / MarketTime, the sell item, the price history).
// Classic reference: TrustMerchantDialog (Client/MirScenes/Dialogs/TrustMerchantDialog.cs) + GameScene.NPCMarket /
// NPCMarketPage / MarketFail / MarketSuccess / ConsignItem. Server rules (Globals): consign for ConsignmentCost at
// MinConsignment .. MaxConsignment, auctions for AuctionCost with a starting bid of MinStartingBid .. MaxStartingBid, listed for
// ConsignmentLength days; sales pay the seller less Globals.Commission; every action is gated by the server on the market page.
//  - opens only when the server opens it (S.NPCMarket → ModernUi.MarketOpening) and closes with the NPC session (a new NPC
//    page: Models.Npc.PageChanged). A fresh open picks the Market panel (C.MarketSearch, as TrustMerchantDialog.Show).
//    Closing is TrustMerchantDialog.Hide (listings cleared, the sell cell unlocks);
//  - Browse = S.NPCMarket pages (10 rows, more via C.MarketPage): the desktop's category filters, a name search and the
//    extended search C.MarketSearchEx (server-side sort, price range, minimum grade). "Buy now / Auction" and "Usable by me"
//    filter the LOADED page only;
//  - Buy now and bids are C.MarketBuy (BidPrice 0 / the bid) after a popup; quick bids +5 / +10 / +25 %. Nothing changes
//    locally: the row waits until the answer (MarketTime reset by S.MarketSuccess / S.MarketFail) or MarketTime passes, then
//    the view is searched again;
//  - the price history and the suggested price come from C.MarketPriceHistory → Models.Market.PriceHistory (one a second);
//  - My listings = the user-mode search (Consign or Auction panel): status from the server's Seller label (For Sale / No Bid
//    / Bid Met / Sold / Expired); Collect / Take back / Cancel = C.MarketGetBack (an unsold item asks first), Sell now =
//    C.MarketSellNow, Collect sold = C.MarketGetBack (Mode Sold) + C.MarketRefresh;
//  - Sell: a click on a bag item in the side bag makes it the sell item (Models.Market.SellItem, its bag cell locked, as
//    ItemCell_Click), then C.ConsignItem; the bag cell stays locked until S.ConsignItem (GameScene unlocks it);
//  - Watchlist: AuctionIDs with a snapshot, kept for this session (the web keeps them in the browser); Messages: this
//    session's market actions and answers.
// HIDDEN (ClientAuction has no data): the bid count, the leading bidder. The answer's text (S.MarketSuccess / S.MarketFail)
// shows in GameScene's message box; the log records that the answer came. Narrow layout (740 px) below a 1040 px wide screen:
// the listing detail opens over the table (Back closes it).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class TrustWindow : ModernWindow
    {
        public const string WindowKey = "trust";
        private const int Rows = 10, SearchGapMs = (int)Globals.SearchDelay + 60, MarketWaitMs = 3100, MarketDelayMs = 3000, PriceHistoryDelayMs = 1000,
            WatchMax = 50, LogMax = 60, H = 590, BarH = 37;

        private enum Tab { Browse, Mine, Sell, Watch, Log }
        private enum Kind { All, Consign, Auction }

        private sealed class Filter
        {
            public int Index;
            public ClientTextKeys Title;
            public ItemType? Type;
            public short MinShape, MaxShape = 5000;
            public Filter[] Sub = Array.Empty<Filter>();
        }

        /// <summary>TrustMerchantDialog.SetupFilters.</summary>
        private static readonly Filter[] Filters =
        {
            new Filter { Index = 0, Title = ClientTextKeys.ShowAllItems, Type = ItemType.Nothing },
            new Filter { Index = 1, Title = ClientTextKeys.WeaponItems, Type = ItemType.Weapon },
            new Filter { Index = 2, Title = ClientTextKeys.DraperyItems, Sub = new[] {
                new Filter { Index = 201, Title = ClientTextKeys.Armour, Type = ItemType.Armour }, new Filter { Index = 202, Title = ClientTextKeys.Helmet, Type = ItemType.Helmet },
                new Filter { Index = 203, Title = ClientTextKeys.Belt, Type = ItemType.Belt }, new Filter { Index = 204, Title = ClientTextKeys.Boots, Type = ItemType.Boots },
                new Filter { Index = 205, Title = ClientTextKeys.Stone, Type = ItemType.Stone } } },
            new Filter { Index = 3, Title = ClientTextKeys.AccessoryItems, Sub = new[] {
                new Filter { Index = 301, Title = ClientTextKeys.Necklaces, Type = ItemType.Necklace }, new Filter { Index = 302, Title = ClientTextKeys.Bracelets, Type = ItemType.Bracelet },
                new Filter { Index = 303, Title = ClientTextKeys.Rings, Type = ItemType.Ring } } },
            new Filter { Index = 4, Title = ClientTextKeys.ConsumableItems, Sub = new[] {
                new Filter { Index = 401, Title = ClientTextKeys.RecoveryPots, Type = ItemType.Potion, MaxShape = 2 },
                new Filter { Index = 402, Title = ClientTextKeys.BuffPots, Type = ItemType.Potion, MinShape = 3, MaxShape = 4 },
                new Filter { Index = 403, Title = ClientTextKeys.ScrollsOils, Type = ItemType.Scroll }, new Filter { Index = 404, Title = ClientTextKeys.MiscItems, Type = ItemType.Script } } },
            new Filter { Index = 5, Title = ClientTextKeys.Enhancement, Sub = new[] {
                new Filter { Index = 501, Title = ClientTextKeys.Gems, Type = ItemType.Potion, MinShape = 3, MaxShape = 3 },
                new Filter { Index = 502, Title = ClientTextKeys.Orbs, Type = ItemType.Potion, MinShape = 4, MaxShape = 4 } } },
            new Filter { Index = 6, Title = ClientTextKeys.Books, Sub = new[] {
                new Filter { Index = 601, Title = ClientTextKeys.Warrior, Type = ItemType.Book, MaxShape = 30 }, new Filter { Index = 602, Title = ClientTextKeys.Wizard, Type = ItemType.Book, MinShape = 31, MaxShape = 60 },
                new Filter { Index = 603, Title = ClientTextKeys.Taoist, Type = ItemType.Book, MinShape = 61, MaxShape = 90 }, new Filter { Index = 604, Title = ClientTextKeys.Assassin, Type = ItemType.Book, MinShape = 91, MaxShape = 120 },
                new Filter { Index = 605, Title = ClientTextKeys.Archer, Type = ItemType.Book, MinShape = 121, MaxShape = 150 } } },
            new Filter { Index = 7, Title = ClientTextKeys.CraftItems, Sub = new[] {
                new Filter { Index = 701, Title = ClientTextKeys.Materials, Type = ItemType.CraftingMaterial }, new Filter { Index = 703, Title = ClientTextKeys.Meat, Type = ItemType.Meat },
                new Filter { Index = 704, Title = ClientTextKeys.Ore, Type = ItemType.Ore } } },
        };

        private static readonly ClientTextKeys[] SortNames =
        {
            ClientTextKeys.ModernEcoTrustSortDefault, ClientTextKeys.ModernEcoTrustSortPriceUp, ClientTextKeys.ModernEcoTrustSortPriceDown,
            ClientTextKeys.ModernEcoTrustSortNewest, ClientTextKeys.ModernEcoTrustSortEnding,
        };

        /// <summary>A watched listing: the AuctionID and what it looked like when starred (session only).</summary>
        private sealed class WatchEntry
        {
            public ulong Id;
            public int Item;
            public string Name;
            public ushort Count;
            public uint Price;
            public MarketItemType Type;
            public string Seller;
            public DateTime Date;
            public long Added;
        }
        private static readonly List<WatchEntry> Watch = new List<WatchEntry>();
        private static long _watchSeq;

        private sealed class LogLine
        {
            public string Time, Text;
            public bool Err;
        }
        private readonly List<LogLine> _log = new List<LogLine>();

        private Tab _tab = Tab.Browse;
        private MarketPanelType _ownKind = MarketPanelType.Consign;
        private string _query = string.Empty;
        private byte _sort;
        private uint _priceMin, _priceMax;
        private ItemGrade _minGrade = ItemGrade.None;
        private Kind _kind = Kind.All;
        private bool _usableOnly;
        private int _filterIndex = 0, _subFilterIndex = -1;
        private int _page, _pageCount;
        private ClientAuction _selected;
        private ulong _pending;
        private bool _hasPending;
        private long _pendingUntil, _lastSearch = -100000, _queuedAt, _historyTime, _historyRetryAt, _nextTick;
        private Func<bool> _queued;
        private int _historyRetryItem = -1;
        private readonly HashSet<int> _asked = new HashSet<int>();
        private bool _detView;
        private string _msg = string.Empty;
        private bool _msgErr;
        private bool _dirty = true;
        private string _catsKey = string.Empty, _detKey = string.Empty, _sparkKey = string.Empty, _sellKey = string.Empty, _watchKey = string.Empty, _logKey = string.Empty, _mineKey = string.Empty, _browseKey = string.Empty;

        // layout
        private bool _narrow;
        private int _width, _catsW, _listW, _detX;
        private const int DetW = 290;

        // top
        private Tabs _tabs;
        private ModernLabel _msgLbl, _goldLbl;
        private ModernPanel[] _views;

        // browse
        private ScrollView _cats;
        private ModernTextField _search, _minIn, _maxIn;
        private ModernButton _sortBtn, _gradeBtn;
        private Segmented<Kind> _kindSeg;
        private ToggleSwitch _usableTog;
        private readonly PaintBox[] _bRows = new PaintBox[Rows];
        private readonly ClientAuction[] _bList = new ClientAuction[Rows];
        private readonly EcoItemView[] _bCells = new EcoItemView[Rows];
        private readonly PaintBox[] _bStars = new PaintBox[Rows];
        private readonly ModernLabel[] _bTimes = new ModernLabel[Rows];
        private ModernLabel _bNone, _bPagerNote, _bPagerText;
        private ModernButton _bPrev, _bNext;
        private readonly List<ClientAuction> _shown = new List<ClientAuction>();

        // detail
        private ModernPanel _det, _detBody;
        private ModernLabel _detEmpty, _detName, _detSub, _histTitle, _histAvg, _histLo, _histHi, _histNote, _buyBig, _buyTime, _buyAfter, _bidBig, _bidTime, _bidMin, _detNote;
        private EcoItemView _selCell;
        private PaintBox _spark;
        private List<ClientMarketSale> _sparkSales;
        private ModernPanel _buyBox, _bidBox;
        private ModernButton _buyBtn, _bidBtn, _watchBtn, _whisperBtn, _backBtn;
        private ModernTextField _bidIn;

        // mine
        private Segmented<MarketPanelType> _mineSeg;
        private ModernLabel _mineNote, _mNone, _mPagerNote, _mPagerText;
        private ModernButton _collectBtn, _mPrev, _mNext;
        private readonly PaintBox[] _mRows = new PaintBox[Rows];
        private readonly ClientAuction[] _mList = new ClientAuction[Rows];
        private readonly EcoItemView[] _mCells = new EcoItemView[Rows];
        private readonly ModernLabel[] _mTimes = new ModernLabel[Rows];
        private readonly ModernPanel[] _mActs = new ModernPanel[Rows];
        private readonly string[] _mActsKey = new string[Rows];

        // sell
        private Segmented<MarketPanelType> _sellSeg;
        private ScrollView _sellBag;
        private readonly List<EcoItemView> _bagCells = new List<EcoItemView>();
        private int _bagBuilt = -1;
        private EcoItemView _sellCell;
        private ModernLabel _sellName, _sellSub, _sellHist, _priceLbl, _feeEl, _commEl, _recvLbl, _recvEl, _sellNote, _sellRules;
        private ModernTextField _priceIn;
        private ModernButton _useBtn, _listBtn;

        // watch / log
        private ScrollView _watchList, _logList;

        private readonly Action _npcPage, _modelChanged;
        private readonly Action<int> _historyChanged;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly GridWatch _bagWatch = new GridWatch();
        private uint _goldShown = uint.MaxValue;

        public TrustWindow() : base(WindowKey, W.T(ClientTextKeys.ModernEcoTrust), 12, 20)
        {
            _npcPage = () => { if (IsOpen) Close(); }; // GameScene.NPCResponse (a new NPC page) hides the trust merchant
            _modelChanged = () => _dirty = true;
            _historyChanged = _ => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.TrustMerchant || k.Grid == MirGridType.None) _dirty = true; };
        }

        private static MarketState Market => GameScene.Models.Market;
        private static UserObject User => GameScene.User;
        private bool Ready => CMain.Time >= Market.MarketTime;

        // test hooks
        internal string TabShown => _tab.ToString();
        internal int PageShown => _page;
        internal int PageCount => _pageCount;
        internal ClientAuction SelectedListing => _selected;
        internal string Message => _msg;
        internal bool IsPending => _hasPending;
        internal int WatchCount => Watch.Count;
        internal PaintBox BrowseRow(int i) => _bRows[i];
        internal PaintBox Star(int i) => _bStars[i];
        internal ModernButton BuyButton => _buyBtn;
        internal ModernButton BidButton => _bidBtn;
        internal ModernTextField BidField => _bidIn;
        internal ModernTextField SearchField => _search;
        internal ModernTextField PriceField => _priceIn;
        internal ModernButton ListButton => _listBtn;
        internal ModernButton CollectButton => _collectBtn;
        internal ModernPanel MineActions(int i) => _mActs[i];
        internal IReadOnlyList<EcoItemView> BagCells => _bagCells;
        internal ScrollView Categories => _cats;

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            _width = _narrow ? 740 : 1000;
            _catsW = _narrow ? 156 : 178;
            _listW = _width - _catsW - (_narrow ? 0 : DetW);
            _detX = _width - DetW;
            _cols = null;
            ContentWidth = _width;
            ContentHeight = H;
            MirControl m = new ModernPanel { Parent = this, Location = new Point(0, S(TitleHeight)), Size = new Size(S(_width), S(H)) };

            // ---- top: tabs, last message, gold
            string[] names = { W.T(ClientTextKeys.ModernEcoTrustBrowse), W.T(ClientTextKeys.ModernEcoTrustMine), W.T(ClientTextKeys.ModernEcoTrustSell), W.T(ClientTextKeys.ModernEcoTrustWatch), W.T(ClientTextKeys.ModernEcoTrustMessages) };
            _tabs = W.At(new Tabs(names, (int)_tab, _narrow ? 380 : 460), m, 12, 6);
            _tabs.Changed += i => EnterTab((Tab)i);
            _goldLbl = W.Label(m, string.Empty, _width - 170, 8, 158, 22, 12, true, ModernTheme.Gold, StringAlignment.Far);
            _msgLbl = W.Label(m, string.Empty, W.Right(_tabs) + 10, 8, _width - 170 - W.Right(_tabs) - 20, 22, 11, false, ModernTheme.Ok, StringAlignment.Far);
            W.Divider(m, 0, BarH - 1, _width);
            _views = new ModernPanel[5];
            for (int i = 0; i < 5; i++)
                _views[i] = new ModernPanel { Parent = m, Location = new Point(0, S(BarH)), Size = new Size(S(_width), S(H - BarH)), Visible = i == (int)_tab };

            BuildBrowse(_views[0]);
            BuildMine(_views[1]);
            BuildSell(_views[2]);
            _watchList = new ScrollView(_width - 24, H - BarH - 12) { Parent = _views[3], Location = new Point(S(12), S(6)), Background = W.Page };
            _watchList.Framed = false;
            _logList = new ScrollView(_width - 24, H - BarH - 12) { Parent = _views[4], Location = new Point(S(12), S(6)), Background = W.Page };
            _logList.Framed = false;

            _catsKey = _detKey = _sparkKey = _sellKey = _watchKey = _logKey = _mineKey = _browseKey = string.Empty;
            for (int i = 0; i < Rows; i++) _mActsKey[i] = string.Empty;
            _bagBuilt = -1;
            _goldShown = uint.MaxValue;
            _dirty = true;
            Render();
        }

        private void BuildBrowse(MirControl v)
        {
            int vh = H - BarH;
            // categories
            _cats = new ScrollView(_catsW - 12, vh - 12) { Parent = v, Location = new Point(S(6), S(6)), Background = W.Page };
            _cats.Framed = false;
            W.VDivider(v, _catsW, 0, vh);

            // the list column: filter strip (2 rows), header, rows, pager
            int x0 = _catsW + 10, lw = _listW - 20;
            _search = W.At(new ModernTextField(_narrow ? 150 : 132, 24) { MaxLength = 20, Placeholder = W.T(ClientTextKeys.ModernEcoTrustItemName) }, v, x0, 8);
            _search.Submitted += _ => FindClick();
            var find = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustFind)) { Parent = v, Tooltip = W.T(ClientTextKeys.ModernEcoTrustFindHint) };
            find.AutoWidth(44);
            find.Location = new Point(_search.Location.X + _search.Size.Width + S(4), S(9));
            find.Click += (o, e) => FindClick();
            _sortBtn = new ModernButton(string.Empty) { Parent = v, Tooltip = W.T(ClientTextKeys.ModernEcoTrustSortHint) };
            _sortBtn.Size = new Size(S(_narrow ? 150 : 132), S(22));
            _sortBtn.Location = new Point(find.Location.X + find.Size.Width + S(6), S(9));
            _sortBtn.Click += (o, e) => { _sort = (byte)((_sort + (CMain.Shift ? SortNames.Length - 1 : 1)) % SortNames.Length); _dirty = true; BrowseSearch(); };
            _gradeBtn = new ModernButton(string.Empty) { Parent = v, Tooltip = W.T(ClientTextKeys.ModernEcoTrustGradeHint) };
            _gradeBtn.Size = new Size(S(_narrow ? 130 : 124), S(22));
            _gradeBtn.Location = new Point(_sortBtn.Location.X + _sortBtn.Size.Width + S(6), S(9));
            _gradeBtn.Click += (o, e) => { _minGrade = (ItemGrade)(((int)_minGrade + (CMain.Shift ? 5 : 1)) % 6); _dirty = true; BrowseSearch(); };

            _minIn = W.At(new ModernTextField(80, 24) { MaxLength = 13, Placeholder = W.T(ClientTextKeys.ModernEcoTrustMin) }, v, x0, 38);
            _minIn.Submitted += _ => ApplyPrice();
            W.Label(v, "–", x0 + 82, 38, 10, 24, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _maxIn = W.At(new ModernTextField(80, 24) { MaxLength = 13, Placeholder = W.T(ClientTextKeys.ModernEcoTrustMax) }, v, x0 + 94, 38);
            _maxIn.Submitted += _ => ApplyPrice();
            var apply = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustApplyPrice)) { Parent = v, Location = new Point(S(x0 + 180), S(39)) };
            apply.AutoWidth(60);
            apply.Click += (o, e) => ApplyPrice();
            var kinds = new List<Choice<Kind>> { new Choice<Kind>(Kind.All, W.T(ClientTextKeys.ModernWinCatAll)), new Choice<Kind>(Kind.Consign, W.T(ClientTextKeys.ModernEcoTrustBuyNow)), new Choice<Kind>(Kind.Auction, W.T(ClientTextKeys.ModernEcoTrustAuction)) };
            int segX = W.Right(apply) + 8;
            _kindSeg = W.At(new Segmented<Kind>(kinds, _kind, 172), v, segX, 39);
            _kindSeg.Hint = W.T(ClientTextKeys.ModernEcoTrustPageOnly);
            _kindSeg.Changed += k => { _kind = k; _dirty = true; };
            int togX = W.Right(_kindSeg) + 8;
            _usableTog = W.At(new ToggleSwitch(_usableOnly, true), v, togX, 42);
            _usableTog.Hint = W.T(ClientTextKeys.ModernEcoTrustUsableHint);
            _usableTog.Changed += on => { _usableOnly = on; _dirty = true; };
            W.Label(v, W.T(ClientTextKeys.ModernEcoTrustUsable), togX + 34, 38, Math.Max(40, _catsW + _listW - 10 - togX - 34), 24, 11, false, ModernTheme.Text);
            var refresh = new ModernButton("⟳") { Parent = v, Size = new Size(S(26), S(22)), Location = new Point(S(_catsW + _listW - 36), S(9)), Tooltip = W.T(ClientTextKeys.ModernEcoTrustRefreshHint) };
            refresh.Click += (o, e) => RefreshView();

            int hy = 70;
            new PaintBox((g, b) => PaintHeader(g, b)) { Parent = v, Location = new Point(S(_catsW + 1), S(hy)), Size = new Size(S(_listW - 1), S(28)), NotControl = true };
            for (int i = 0; i < Rows; i++)
            {
                int idx = i;
                _bRows[i] = new PaintBox((g, b) => PaintBrowseRow(g, b, idx)) { Parent = v, Location = new Point(S(_catsW + 1), S(hy + 28 + i * 40)), Size = new Size(S(_listW - 1), S(40)), Visible = false };
                _bRows[i].Click += (o, e) => { if (_bList[idx] != null) Select(_bList[idx]); };
                _bCells[i] = new EcoItemView(32, () => _bList[idx]?.Item) { Parent = _bRows[i], Location = new Point(S(10), S(4)), Locked = () => Busy(_bList[idx]) };
                _bCells[i].Clicked += _ => { if (_bList[idx] != null) Select(_bList[idx]); };
                int[] col = Cols();
                _bStars[i] = new PaintBox((g, b) => PaintStar(g, b, _bList[idx] != null && Watched(_bList[idx].AuctionID))) { Parent = _bRows[i], Size = new Size(S(14), S(14)), Location = new Point(S(col[1] + col[2] - 18), S(21)), Hint = W.T(ClientTextKeys.ModernEcoTrustWatchHint) };
                _bStars[i].Click += (o, e) => { if (_bList[idx] != null) ToggleWatch(_bList[idx]); };
                _bTimes[i] = W.Label(_bRows[i], string.Empty, col[5], 0, col[6], 40, 11, false, ModernTheme.Dim);
            }
            _bNone = W.Label(v, string.Empty, _catsW + 10, hy + 70, _listW - 20, 40, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
            int py = vh - 32;
            W.Divider(v, _catsW + 1, py - 2, _listW - 1);
            _bPagerNote = W.Label(v, string.Empty, _catsW + 10, py, _listW - 140, 26, 11, false, ModernTheme.Dim);
            Pager(v, _catsW + _listW - 10, py, out _bPrev, out _bPagerText, out _bNext);

            // detail pane (built last: in the narrow layout it lies over the table)
            BuildDetail(v, vh);
        }

        /// <summary>Column x positions / widths in HUD px within a row: [x0, nameX, nameW, lvX, lvW, kindX..]: cell, name, lv, kind, time, price.</summary>
        private int[] _cols;

        private int[] Cols() => _cols ??= MakeCols();

        private int[] MakeCols()
        {
            int gap = _narrow ? 6 : 8, lv = _narrow ? 34 : 40, kind = _narrow ? 62 : 66, time = _narrow ? 70 : 78, price = _narrow ? 96 : 112;
            int nameX = 10 + 34 + gap;
            int nameW = _listW - 1 - 20 - 34 - lv - kind - time - price - gap * 5;
            int lvX = nameX + nameW + gap, kindX = lvX + lv + gap, timeX = kindX + kind + gap, priceX = timeX + time + gap;
            return new[] { 10, nameX, nameW, lvX, lv, timeX, time, kindX, kind, priceX, price };
        }

        private void Pager(MirControl v, int right, int y, out ModernButton prev, out ModernLabel text, out ModernButton next)
        {
            next = new ModernButton("›") { Parent = v, Size = new Size(S(26), S(22)), Location = new Point(S(right - 26), S(y + 2)), Tooltip = W.T(ClientTextKeys.ModernWinNextPage) };
            next.Click += (o, e) => NextPage();
            text = W.Label(v, string.Empty, right - 26 - 64, y, 60, 26, 11, false, ModernTheme.Dim, StringAlignment.Center);
            prev = new ModernButton("‹") { Parent = v, Size = new Size(S(26), S(22)), Location = new Point(S(right - 26 - 64 - 30), S(y + 2)), Tooltip = W.T(ClientTextKeys.ModernEcoPrevPage) };
            prev.Click += (o, e) => { if (_page > 0) { _page--; _dirty = true; } };
        }

        private void BuildDetail(MirControl v, int vh)
        {
            _det = new ModernPanel { Parent = v, Location = new Point(S(_detX), 0), Size = new Size(S(DetW), S(vh)) };
            new PaintBox((g, b) =>
            {
                using (var br = new SolidBrush(W.Page)) g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height);
                using var pen = new Pen(W.Over(ModernTheme.Frame, 0.6f), 1f);
                g.DrawLine(pen, 0.5f, 0, 0.5f, b.Size.Height);
            }) { Parent = _det, Size = _det.Size, NotControl = true };
            int x = 12, w = DetW - 24;
            _detEmpty = W.Label(_det, W.T(ClientTextKeys.ModernEcoTrustPickListing), x, 20, w, 40, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            _detBody = new ModernPanel { Parent = _det, Location = Point.Empty, Size = _det.Size, Visible = false };
            MirControl d = _detBody;
            int y = 12;
            if (_narrow)
            {
                _backBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoBack)) { Parent = d, Location = new Point(S(x), S(y)) };
                _backBtn.AutoWidth(50);
                _backBtn.Click += (o, e) => { _detView = false; _dirty = true; };
                y += 28;
            }
            _selCell = new EcoItemView(52, () => _selected?.Item) { Parent = d, Location = new Point(S(x), S(y)) };
            _detName = W.Label(d, string.Empty, x + 62, y + 6, w - 62, 20, 15, true, Color.White);
            _detSub = W.Label(d, string.Empty, x + 62, y + 28, w - 62, 16, 10, false, ModernTheme.Dim);
            y += 62;
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = d, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(118)), NotControl = true };
            _histTitle = W.Label(d, string.Empty, x + 10, y + 8, w - 110, 16, 12, false, ModernTheme.Dim);
            _histAvg = W.Label(d, string.Empty, x + w - 110, y + 8, 100, 16, 12, true, ModernTheme.Gold, StringAlignment.Far);
            _spark = new PaintBox(PaintSpark) { Parent = d, Location = new Point(S(x + 10), S(y + 28)), Size = new Size(S(w - 20), S(46)), NotControl = true };
            _histLo = W.Label(d, string.Empty, x + 10, y + 76, (w - 20) / 2, 14, 11, false, ModernTheme.Dim);
            _histHi = W.Label(d, string.Empty, x + w / 2, y + 76, (w - 20) / 2, 14, 11, false, ModernTheme.Dim, StringAlignment.Far);
            _histNote = W.Label(d, string.Empty, x + 10, y + 94, w - 20, 16, 11, false, ModernTheme.Dim);
            y += 126;

            // buy now box
            _buyBox = new ModernPanel { Parent = d, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(136)) };
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = _buyBox, Size = _buyBox.Size, NotControl = true };
            _buyBig = W.Label(_buyBox, string.Empty, 10, 6, w - 20, 32, 24, true, ModernTheme.Gold);
            W.Label(_buyBox, W.T(ClientTextKeys.ModernEcoTrustTimeLeft), 10, 42, 120, 16, 12, false, ModernTheme.Dim);
            _buyTime = W.Label(_buyBox, string.Empty, w - 130, 42, 120, 16, 12, true, Color.White, StringAlignment.Far);
            W.Label(_buyBox, W.T(ClientTextKeys.ModernEcoTrustGoldAfter), 10, 62, 120, 16, 12, false, ModernTheme.Dim);
            _buyAfter = W.Label(_buyBox, string.Empty, w - 130, 62, 120, 16, 12, true, ModernTheme.Ok, StringAlignment.Far);
            _buyBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustBuyNow), ButtonKind.Main, true) { Parent = _buyBox, Location = new Point(S(10), S(92)) };
            _buyBtn.Size = new Size(S(w - 20), _buyBtn.Size.Height);
            _buyBtn.Click += (o, e) => BuyNow();

            // bid box
            _bidBox = new ModernPanel { Parent = d, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(214)), Visible = false };
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = _bidBox, Size = _bidBox.Size, NotControl = true };
            _bidBig = W.Label(_bidBox, string.Empty, 10, 6, w - 20, 32, 24, true, ModernTheme.Gold);
            W.Label(_bidBox, W.T(ClientTextKeys.ModernEcoTrustEndsIn), 10, 40, 120, 16, 12, false, ModernTheme.Dim);
            _bidTime = W.Label(_bidBox, string.Empty, w - 130, 40, 120, 16, 12, true, Color.White, StringAlignment.Far);
            _bidMin = W.Label(_bidBox, string.Empty, 10, 58, w - 20, 16, 11, false, ModernTheme.Dim);
            _bidIn = W.At(new ModernTextField(w - 20, 30, 15, 8, 8, true) { MaxLength = 13 }, _bidBox, 10, 78);
            _bidIn.TextChanged += _ => { _detKey = string.Empty; _dirty = true; };
            _bidIn.Submitted += _ => PlaceBid();
            int qw = (w - 20 - 12) / 3;
            int[] pcts = { 5, 10, 25 };
            for (int i = 0; i < 3; i++)
            {
                int pct = pcts[i];
                var q = new ModernButton("+" + pct + "%") { Parent = _bidBox, Location = new Point(S(10 + i * (qw + 6)), S(114)), Tooltip = W.T(ClientTextKeys.ModernEcoTrustQuickBidHint, pct) };
                q.Size = new Size(S(qw), q.Size.Height);
                q.Click += (o, e) => QuickBid(pct);
            }
            _bidBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustPlaceBid), ButtonKind.Main, true) { Parent = _bidBox, Location = new Point(S(10), S(142)) };
            _bidBtn.Size = new Size(S(w - 20), _bidBtn.Size.Height);
            _bidBtn.Click += (o, e) => PlaceBid();
            W.Label(_bidBox, W.T(ClientTextKeys.ModernEcoTrustBidNote), 10, 176, w - 20, 32, 10, false, ModernTheme.Dim, StringAlignment.Near, true);

            _detNote = W.Label(d, string.Empty, x, vh - 74, w, 30, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            int bw = (w - 6) / 2;
            _watchBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustWatchBtn)) { Parent = d, Location = new Point(S(x), S(vh - 38)) };
            _watchBtn.Size = new Size(S(bw), _watchBtn.Size.Height);
            _watchBtn.Click += (o, e) => { if (_selected != null) ToggleWatch(_selected); };
            _whisperBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustWhisper)) { Parent = d, Location = new Point(S(x + bw + 6), S(vh - 38)) };
            _whisperBtn.Size = new Size(S(bw), _whisperBtn.Size.Height);
            _whisperBtn.Click += (o, e) => Whisper();
        }

        private void BuildMine(MirControl v)
        {
            int vh = H - BarH;
            var choices = new List<Choice<MarketPanelType>> { new Choice<MarketPanelType>(MarketPanelType.Consign, W.T(ClientTextKeys.ModernEcoTrustBuyNow)), new Choice<MarketPanelType>(MarketPanelType.Auction, W.T(ClientTextKeys.ModernEcoTrustAuctions)) };
            _mineSeg = W.At(new Segmented<MarketPanelType>(choices, _ownKind, 220), v, 12, 10);
            _mineSeg.Changed += SetOwnKind;
            _collectBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoTrustCollectSold), ButtonKind.Main) { Parent = v, Tooltip = W.T(ClientTextKeys.ModernEcoTrustCollectSoldHint) };
            _collectBtn.AutoWidth(150);
            _collectBtn.Location = new Point(S(_width - 12) - _collectBtn.Size.Width, S(9));
            _collectBtn.Click += (o, e) => CollectSold();
            _mineNote = W.Label(v, string.Empty, 244, 8, (int)(_collectBtn.Location.X / ModernRoot.K) - 254, 28, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.Divider(v, 0, 42, _width);
            int aw = _narrow ? 200 : 230;
            for (int i = 0; i < Rows; i++)
            {
                int idx = i;
                _mRows[i] = new PaintBox((g, b) => PaintMineRow(g, b, idx)) { Parent = v, Location = new Point(0, S(44 + i * 44)), Size = new Size(S(_width), S(44)), Visible = false }; // not NotControl: the action buttons inside take clicks
                _mCells[i] = new EcoItemView(32, () => _mList[idx]?.Item) { Parent = _mRows[i], Location = new Point(S(12), S(6)), Locked = () => Busy(_mList[idx]) };
                _mTimes[i] = W.Label(_mRows[i], string.Empty, _width - 12 - aw - 10 - 84, 0, 84, 44, 11, false, ModernTheme.Dim);
                _mActs[i] = new ModernPanel { Parent = _mRows[i], Location = new Point(S(_width - 12 - aw), S(10)), Size = new Size(S(aw), S(24)) };
                _mActsKey[i] = string.Empty;
            }
            _mNone = W.Label(v, string.Empty, 12, 100, _width - 24, 40, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
            int py = vh - 32;
            W.Divider(v, 0, py - 2, _width);
            _mPagerNote = W.Label(v, string.Empty, 12, py, _width - 160, 26, 11, false, ModernTheme.Dim);
            Pager(v, _width - 12, py, out _mPrev, out _mPagerText, out _mNext);
        }

        private void BuildSell(MirControl v)
        {
            int vh = H - BarH, bagW = _narrow ? 250 : 300, cols = _narrow ? 5 : 6;
            W.Caption(v, W.T(ClientTextKeys.ModernEcoTrustHowToSell), 12, 10, bagW - 24);
            var choices = new List<Choice<MarketPanelType>>
            {
                new Choice<MarketPanelType>(MarketPanelType.Consign, W.T(ClientTextKeys.ModernEcoTrustBuyNow), null, W.T(ClientTextKeys.ModernEcoTrustConsignTip, W.N(Globals.MinConsignment), W.N(Globals.MaxConsignment), W.N(Globals.ConsignmentCost))),
                new Choice<MarketPanelType>(MarketPanelType.Auction, W.T(ClientTextKeys.ModernEcoTrustAuction), null, W.T(ClientTextKeys.ModernEcoTrustAuctionTip, W.N(Globals.MinStartingBid), W.N(Globals.MaxStartingBid), W.N(Globals.AuctionCost))),
            };
            _sellSeg = W.At(new Segmented<MarketPanelType>(choices, _ownKind, bagW - 24), v, 12, 28);
            _sellSeg.Changed += SetOwnKind;
            W.Caption(v, W.T(ClientTextKeys.ModernEcoTrustYourBag), 12, 62, bagW - 24);
            _sellBag = new ScrollView(cols * 44 + 10, vh - 92) { Parent = v, Location = new Point(S(12), S(80)), Background = W.Page };
            _sellBag.Framed = false;
            W.VDivider(v, bagW, 0, vh);

            int x = bagW + 18, w = _width - x - 18;
            _sellCell = new EcoItemView(52, () => Market.SellItem) { Parent = v, Location = new Point(S(x), S(14)), Selected = true };
            _sellCell.Clicked += b => { if (b == MouseButtons.Right) { ReleaseSellItem(); _dirty = true; } };
            _sellName = W.Label(v, string.Empty, x + 64, 18, w - 64, 20, 15, true, Color.White);
            _sellSub = W.Label(v, string.Empty, x + 64, 40, w - 64, 16, 11, false, ModernTheme.Dim);
            _sellHist = W.Label(v, string.Empty, x, 76, w, 16, 11, false, ModernTheme.Dim);
            _priceLbl = W.Caption(v, string.Empty, x, 100, 200);
            _priceIn = W.At(new ModernTextField(w - 160, 30, 15, 8, 8, true) { MaxLength = 13 }, v, x, 118);
            _priceIn.TextChanged += _ => { _sellKey = string.Empty; _dirty = true; };
            _priceIn.Submitted += _ => ListItem();
            _useBtn = new ModernButton(string.Empty) { Parent = v, Location = new Point(S(x + w - 150), S(120)), Tooltip = W.T(ClientTextKeys.ModernEcoTrustSuggestedHint) };
            _useBtn.Size = new Size(S(150), S(26));
            _useBtn.Click += (o, e) => UseSuggested();
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = v, Location = new Point(S(x), S(158)), Size = new Size(S(w), S(96)), NotControl = true };
            W.Label(v, W.T(ClientTextKeys.ModernEcoTrustFee), x + 10, 166, w - 140, 18, 12, false, ModernTheme.Dim);
            _feeEl = W.Label(v, string.Empty, x + w - 130, 166, 120, 18, 12, true, ModernTheme.Gold, StringAlignment.Far);
            W.Label(v, W.T(ClientTextKeys.ModernEcoTrustCommission, (int)Math.Round(Globals.Commission * 100)), x + 10, 186, w - 140, 18, 12, false, ModernTheme.Dim);
            _commEl = W.Label(v, string.Empty, x + w - 130, 186, 120, 18, 12, true, Color.White, StringAlignment.Far);
            _recvLbl = W.Label(v, string.Empty, x + 10, 206, w - 140, 18, 12, false, ModernTheme.Dim);
            _recvEl = W.Label(v, string.Empty, x + w - 130, 206, 120, 18, 12, true, ModernTheme.Ok, StringAlignment.Far);
            W.Label(v, W.T(ClientTextKeys.ModernEcoTrustListedFor), x + 10, 226, w - 140, 18, 12, false, ModernTheme.Dim);
            W.Label(v, W.T(ClientTextKeys.ModernEcoTrustDays, Globals.ConsignmentLength), x + w - 130, 226, 120, 18, 12, true, Color.White, StringAlignment.Far);
            _listBtn = new ModernButton(string.Empty, ButtonKind.Main, true) { Parent = v, Location = new Point(S(x), S(266)) };
            _listBtn.Size = new Size(S(w), _listBtn.Size.Height);
            _listBtn.Click += (o, e) => ListItem();
            _sellNote = W.Label(v, string.Empty, x, 304, w, 32, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            _sellRules = W.Label(v, string.Empty, x, 340, w, 60, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            // TrustMerchantDialog.Show: the Market panel, filters from the top (DrawFilters(0, -1)), the bag beside it.
            GameScene.Models.Npc.PageChanged.On(_npcPage);
            Market.Changed.On(_modelChanged);
            Market.PriceHistoryChanged.On(_historyChanged);
            ItemMoves.SlotChanged.On(_slotChanged);
            _tab = Tab.Browse;
            _tabs?.Select(0);
            _query = string.Empty;
            if (_search != null) _search.Text = string.Empty;
            _sort = 0;
            _minGrade = ItemGrade.None;
            _priceMin = _priceMax = 0;
            if (_minIn != null) { _minIn.Text = string.Empty; _maxIn.Text = string.Empty; }
            _hasPending = false;
            _asked.Clear();
            _selected = null;
            _detView = false;
            _filterIndex = 0;
            _subFilterIndex = -1;
            _page = 0;
            _catsKey = _detKey = _sparkKey = _sellKey = _browseKey = _mineKey = string.Empty;
            SetTab(MarketPanelType.Market);
            _lastSearch = CMain.Time;
            ModernRoot.Instance?.Wm.Open(InventoryWindow.WindowKey);
            Open(); // keep the trust merchant in front of the bag
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            // TrustMerchantDialog.Hide: listings and selection go, the sell cell unlocks.
            GameScene.Models.Npc.PageChanged.Off(_npcPage);
            Market.Changed.Off(_modelChanged);
            Market.PriceHistoryChanged.Off(_historyChanged);
            ItemMoves.SlotChanged.Off(_slotChanged);
            _queued = null;
            _historyRetryItem = -1;
            _hasPending = false;
            Market.Listings.Clear();
            _selected = null;
            ReleaseSellItem();
            _search?.Blur();
            _bidIn?.Blur();
            _priceIn?.Blur();
        }

        public override void Process()
        {
            base.Process();
            if (User == null) return;
            long now = CMain.Time;
            if (_queued != null && now >= _queuedAt) RunQueued();
            if (_historyRetryItem >= 0 && now >= _historyRetryAt)
            {
                int item = _historyRetryItem;
                _historyRetryItem = -1;
                AskHistory(item);
            }
            if (_hasPending)
            {
                if (Market.MarketTime == 0) Answered(); // S.MarketSuccess / S.MarketFail reset MarketTime
                else if (now >= _pendingUntil) { _hasPending = false; _dirty = true; }
            }
            if (GameScene.Gold != _goldShown) { _goldShown = GameScene.Gold; _goldLbl.Text = W.T(ClientTextKeys.ModernEcoTrustGold, W.N(GameScene.Gold)); _detKey = _sellKey = string.Empty; _dirty = true; }
            if (_tab == Tab.Sell && _bagWatch.Poll(MirGridType.Inventory, User.Inventory)) _dirty = true;
            if (now >= _nextTick)
            {
                _nextTick = now + 1000;
                TickTimes();
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ seam (ModernUi.Economy)

        /// <summary>S.NPCMarket after the listings: page 0 of <paramref name="pages"/>.</summary>
        internal void Listed(int pages)
        {
            _page = 0;
            _pageCount = pages;
            _dirty = true;
        }

        /// <summary>S.NPCMarketPage: the appended page is shown.</summary>
        internal void PageAdded()
        {
            _page = Math.Max(0, (Market.Listings.Count - 1) / Rows);
            _dirty = true;
        }

        // ------------------------------------------------------------------ tabs and searches

        private void EnterTab(Tab t)
        {
            _tab = t;
            _detView = false;
            if (t == Tab.Browse)
            {
                ReleaseSellItem(); // the desktop's Market tab unlocks the sell cell
                if (Market.UserMode) BrowseSearch();
            }
            else if (t == Tab.Mine || t == Tab.Sell)
            {
                if (!Market.UserMode || Market.MarketType != _ownKind) Queue(() => { SetTab(_ownKind); return true; });
            }
            _dirty = true;
        }

        private void SetOwnKind(MarketPanelType v)
        {
            _ownKind = v;
            _mineSeg?.Set(v);
            _sellSeg?.Set(v);
            if (_priceIn != null) _priceIn.Text = string.Empty;
            Queue(() => { SetTab(v); return true; });
            _sellKey = _mineKey = string.Empty;
            _dirty = true;
        }

        /// <summary>TrustMerchantDialog.TMerchantDialog: switch the panel and search the user's (Consign / Auction) or all listings.</summary>
        private static void SetTab(MarketPanelType type)
        {
            Market.MarketType = type;
            Network.Enqueue(new C.MarketSearch { Match = string.Empty, Type = ItemType.Nothing, Usermode = type != MarketPanelType.Market, MinShape = 0, MaxShape = 5000, MarketType = type });
        }

        /// <summary>Runs a search-type request at least SearchGap after the last one; a newer one replaces a waiting one.</summary>
        private void Queue(Func<bool> run)
        {
            _queued = run;
            _queuedAt = Math.Max(CMain.Time, _lastSearch + SearchGapMs);
            if (CMain.Time >= _queuedAt) RunQueued();
        }

        private void RunQueued()
        {
            Func<bool> run = _queued;
            _queued = null;
            if (run == null || !IsOpen) return;
            if (run()) _lastSearch = CMain.Time;
        }

        /// <summary>The selected category (a sub-filter, or a top filter without subs; null = a top filter that only expands).</summary>
        private Filter Target()
        {
            Filter top = Array.Find(Filters, f => f.Index == _filterIndex) ?? Filters[0];
            if (top.Sub.Length == 0) return top;
            return Array.Find(top.Sub, s => s.Index == _subFilterIndex);
        }

        /// <summary>C.MarketSearchEx with the category, name, order, price range and grade, under the SearchTime gate.</summary>
        private void BrowseSearch()
        {
            Filter t = Target() ?? Filters[0];
            Queue(() =>
            {
                if (!SearchAllowed()) return false;
                Market.SearchTime = CMain.Time + Globals.SearchDelay;
                Market.MarketType = MarketPanelType.Market;
                Network.Enqueue(new C.MarketSearchEx
                {
                    Match = _query, Type = t.Type ?? ItemType.Nothing, Usermode = false, MinShape = t.MinShape, MaxShape = t.MaxShape,
                    MarketType = MarketPanelType.Market, Sort = _sort, PriceMin = _priceMin, PriceMax = _priceMax, MinGrade = (byte)_minGrade,
                });
                return true;
            });
        }

        /// <summary>SearchTime not yet passed: the "search again after" line (as the classic dialog), false.</summary>
        private static bool SearchAllowed()
        {
            long t = CMain.Time;
            if (t >= Market.SearchTime) return true;
            GameScene.Models.Chat.Add(W.T(ClientTextKeys.YouCanSearchAgain, Math.Ceiling((Market.SearchTime - t) / 1000D)), ChatType.System);
            return false;
        }

        private void FindClick()
        {
            _query = (_search.Text ?? string.Empty).Trim().Replace(" ", string.Empty);
            BrowseSearch();
        }

        private void ApplyPrice()
        {
            _priceMin = Eco.Amount(_minIn.Text, out uint lo) ? lo : 0;
            _priceMax = Eco.Amount(_maxIn.Text, out uint hi) ? hi : 0;
            if (_priceMax > 0 && _priceMax < _priceMin) (_priceMin, _priceMax) = (_priceMax, _priceMin);
            _minIn.Text = _priceMin > 0 ? W.N(_priceMin) : string.Empty;
            _maxIn.Text = _priceMax > 0 ? W.N(_priceMax) : string.Empty;
            BrowseSearch();
        }

        /// <summary>A category click (TrustMerchantDialog filter buttons): expand; search when it has a type.</summary>
        private void CatClick(Filter item, Filter sub)
        {
            _filterIndex = item.Index;
            _subFilterIndex = sub?.Index ?? -1;
            _catsKey = string.Empty;
            _dirty = true;
            if (sub == null && item.Sub.Length > 0) return;
            BrowseSearch();
        }

        private void RefreshView()
        {
            if (_tab == Tab.Mine || _tab == Tab.Sell) Queue(() => { SetTab(_ownKind); return true; });
            else BrowseSearch();
        }

        /// <summary>NextButton: a cached page at once, else C.MarketPage (a search-type request).</summary>
        private void NextPage()
        {
            if (_page >= _pageCount - 1) return;
            if (_page < (Market.Listings.Count - 1) / Rows)
            {
                _page++;
                _dirty = true;
                return;
            }
            int next = _page + 1;
            Queue(() => { Network.Enqueue(new C.MarketPage { Page = next }); return true; });
        }

        // ------------------------------------------------------------------ actions (nothing changes until the server answers)

        private bool Busy(ClientAuction l) => l != null && _hasPending && _pending == l.AuctionID && !Ready;

        private bool CanAct()
        {
            if (!IsOpen) { Say(W.T(ClientTextKeys.ModernEcoTrustSpeakFirst), true); return false; }
            if (!Ready) { Say(W.T(ClientTextKeys.ModernEcoTrustStillAnswering), true); return false; }
            return true;
        }

        private void Wait(ClientAuction l)
        {
            _pending = l.AuctionID;
            _hasPending = true;
            _pendingUntil = CMain.Time + MarketWaitMs;
            _dirty = true;
        }

        private static bool Live(ClientAuction l) => Market.Listings.Contains(l);

        private static uint BidFloor(ClientAuction l) => l.Price == uint.MaxValue ? uint.MaxValue : l.Price + 1;

        private void BuyNow()
        {
            ClientAuction l = _selected;
            Popups pop = ModernRoot.Instance?.Popups;
            if (l == null || Market.UserMode || l.ItemType != MarketItemType.Consign || !CanAct() || pop == null) return;
            pop.Ask(W.T(ClientTextKeys.ConfirmBuyItemWithPrice, Eco.Name(l.Item), l.Price, W.T(ClientTextKeys.ModernEcoGoldWord)), ok =>
            {
                if (!ok || !IsOpen) return;
                if (!Live(l)) { Say(W.T(ClientTextKeys.ModernEcoTrustChanged), true); return; }
                if (!CanAct()) return;
                Market.MarketTime = CMain.Time + MarketDelayMs;
                Network.Enqueue(new C.MarketBuy { AuctionID = l.AuctionID, BidPrice = 0 });
                AddLog(W.T(ClientTextKeys.ModernEcoTrustLogBuy, Eco.Name(l.Item), W.N(l.Price)), false);
                Wait(l);
            }, true, PopupKind.Question, W.T(ClientTextKeys.ModernEcoTrustBuyNow));
        }

        private void QuickBid(int pct)
        {
            ClientAuction l = _selected;
            if (l == null) return;
            double v = Math.Ceiling(l.Price * (1 + pct / 100.0) / 100.0) * 100;
            uint bid = (uint)Math.Min(uint.MaxValue, Math.Max(BidFloor(l), v));
            _bidIn.Text = W.N(bid);
        }

        private void PlaceBid()
        {
            ClientAuction l = _selected;
            Popups pop = ModernRoot.Instance?.Popups;
            if (l == null || Market.UserMode || l.ItemType != MarketItemType.Auction || pop == null) return;
            uint min = BidFloor(l);
            if (!Eco.Amount(_bidIn.Text, out uint v) || v < min) { Say(W.T(ClientTextKeys.ModernEcoTrustBidAtLeast, W.N(min)), true); return; }
            if (v > GameScene.Gold) { Say(W.T(ClientTextKeys.LowGold), true); return; }
            if (!CanAct()) return;
            pop.Ask(W.T(ClientTextKeys.ConfirmBidGoldForItem, v, Eco.Name(l.Item)), ok =>
            {
                if (!ok || !IsOpen) return;
                if (!Live(l)) { Say(W.T(ClientTextKeys.ModernEcoTrustChanged), true); return; }
                if (!CanAct()) return;
                Market.MarketTime = CMain.Time + MarketDelayMs;
                Network.Enqueue(new C.MarketBuy { AuctionID = l.AuctionID, BidPrice = v });
                AddLog(W.T(ClientTextKeys.ModernEcoTrustLogBid, W.N(v), Eco.Name(l.Item)), false);
                _bidIn.Text = string.Empty;
                Wait(l);
            }, true, PopupKind.Question, W.T(ClientTextKeys.ModernEcoTrustPlaceBid));
        }

        /// <summary>BuyButton in user mode: C.MarketGetBack (an unsold listing asks first). Sold = collect the gold.</summary>
        private void GetBack(ClientAuction l)
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (!Market.UserMode || !CanAct() || pop == null) return;
            string name = Eco.Name(l.Item);
            void Go()
            {
                if (!IsOpen || !Live(l) || !CanAct()) return;
                Market.MarketTime = CMain.Time + MarketDelayMs;
                Network.Enqueue(new C.MarketGetBack { Mode = MarketCollectionMode.Any, AuctionID = l.AuctionID });
                AddLog(W.T(ClientTextKeys.ModernEcoTrustLogGetBack, name), false);
                Wait(l);
            }
            if (l.Seller == "For Sale" || l.Seller == "No Bid")
                pop.Ask(W.T(ClientTextKeys.ItemNotSoldGetBack, name), ok => { if (ok) Go(); }, true, PopupKind.Question, W.T(ClientTextKeys.ModernEcoTrustCancelListing));
            else if (l.Seller == "Bid Met")
                pop.Ask(W.T(ClientTextKeys.ModernEcoTrustCancelBidAsk, name, W.N(l.Price)), ok => { if (ok) Go(); }, true, PopupKind.Warning, W.T(ClientTextKeys.ModernEcoTrustCancelAuction));
            else Go();
        }

        /// <summary>SellNowButton (an auction with a bid: "Bid Met").</summary>
        private void SellNow(ClientAuction l)
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (!Market.UserMode || l.Seller != "Bid Met" || !CanAct() || pop == null) return;
            pop.Ask(W.T(ClientTextKeys.ModernEcoTrustSellNowAsk, Eco.Name(l.Item), W.N(l.Price), W.N(Net(l.Price))), ok =>
            {
                if (!ok || !IsOpen || !Live(l) || !CanAct()) return;
                _selected = l;
                Market.MarketTime = CMain.Time + MarketDelayMs;
                Network.Enqueue(new C.MarketSellNow { AuctionID = l.AuctionID });
                AddLog(W.T(ClientTextKeys.ModernEcoTrustLogSellNow, Eco.Name(l.Item), W.N(l.Price)), false);
                Wait(l);
            }, true, PopupKind.Question, W.T(ClientTextKeys.ModernEcoTrustSellNow));
        }

        /// <summary>CollectSoldButton: every sold listing's gold (C.MarketGetBack Mode Sold), then a refresh.</summary>
        private void CollectSold()
        {
            if (!Market.UserMode || !CanAct()) return;
            Queue(() =>
            {
                if (!Ready || !Market.UserMode) return false;
                Market.MarketTime = CMain.Time + MarketDelayMs;
                Network.Enqueue(new C.MarketGetBack { Mode = MarketCollectionMode.Sold, AuctionID = 0 });
                Network.Enqueue(new C.MarketRefresh());
                AddLog(W.T(ClientTextKeys.ModernEcoTrustLogCollect), false);
                return true;
            });
        }

        /// <summary>The server's answer text (S.MarketSuccess / S.MarketFail through IGameUi.MarketMessage): into the log.</summary>
        internal void ServerMessage(string text, bool fail)
        {
            AddLog(text, fail);
            _dirty = true;
        }

        /// <summary>The server answered (S.MarketSuccess / S.MarketFail; the text arrives through ServerMessage): search the view again.</summary>
        private void Answered()
        {
            _hasPending = false;
            AddLog(W.T(ClientTextKeys.ModernEcoTrustAnswered), false);
            Say(W.T(ClientTextKeys.ModernEcoTrustAnswered), false);
            if (IsOpen) RefreshView();
            _dirty = true;
        }

        private static uint Net(uint cost) => (uint)Math.Max(0, Math.Truncate(cost - cost * Globals.Commission));

        private void Say(string text, bool err)
        {
            _msg = text ?? string.Empty;
            _msgErr = err;
            if (_msgLbl != null && !_msgLbl.IsDisposed)
            {
                _msgLbl.Text = _msg;
                _msgLbl.Colour = err ? ModernTheme.BadSoft : ModernTheme.Ok;
            }
            ModernLabel note = _tab == Tab.Sell ? _sellNote : _tab == Tab.Browse || _tab == Tab.Watch ? _detNote : _mineNote;
            if (note != null && !note.IsDisposed)
            {
                note.Text = _msg;
                note.Colour = err ? ModernTheme.BadSoft : ModernTheme.Dim;
            }
        }

        private void AddLog(string text, bool err)
        {
            _log.Insert(0, new LogLine { Time = CMain.Now.ToString("T"), Text = text, Err = err });
            if (_log.Count > LogMax) _log.RemoveRange(LogMax, _log.Count - LogMax);
            _logKey = string.Empty;
        }

        private void Select(ClientAuction l)
        {
            if (_selected != l)
            {
                if (_bidIn != null) _bidIn.Text = string.Empty;
                if (_detNote != null) _detNote.Text = string.Empty;
            }
            _selected = l;
            _detView = true;
            _detKey = string.Empty;
            AskHistory(l.Item?.ItemIndex ?? -1);
            _dirty = true;
        }

        private void Whisper()
        {
            ClientAuction l = _selected;
            if (l == null || Market.UserMode || string.IsNullOrEmpty(l.Seller)) return;
            ModernUi ui = GameScene.Ui as ModernUi;
            ui?.Chat?.Prefill("/" + l.Seller + " "); // the HUD chat input (chat.ts prefill)
        }

        /// <summary>C.MarketPriceHistory once per item while open (one a second: a refused ask retries).</summary>
        private void AskHistory(int itemIndex)
        {
            if (itemIndex < 0 || _asked.Contains(itemIndex) || !IsOpen) return;
            long now = CMain.Time;
            if (now >= _historyTime)
            {
                _historyTime = now + PriceHistoryDelayMs;
                _asked.Add(itemIndex);
                Network.Enqueue(new C.MarketPriceHistory { ItemIndex = itemIndex });
                return;
            }
            _historyRetryItem = itemIndex;
            _historyRetryAt = _historyTime;
        }

        // ------------------------------------------------------------------ sell

        /// <summary>NPCDropDialog Consign's checks (DontStore / DontSell, Durability &lt; 0 as ItemCell_Click), a locked cell. "" = fine.</summary>
        private static string SellRefusal(int slot, UserItem item)
        {
            if (item?.Info == null) return string.Empty;
            if (item.Info.Bind.HasFlag(BindMode.DontStore) || item.Info.Bind.HasFlag(BindMode.DontSell) || item.Info.Durability < 0) return W.T(ClientTextKeys.ModernEcoTrustCannotConsign);
            if (ItemMoves.IsLocked(MirGridType.Inventory, slot)) return W.T(ClientTextKeys.ModernWinWaitingServer);
            return string.Empty;
        }

        /// <summary>ItemCell_Click: the bag item becomes the sell item, its bag cell locked.</summary>
        private void PickSell(int slot)
        {
            UserItem item = slot >= 0 && slot < User.Inventory.Length ? User.Inventory[slot] : null;
            if (item == null) return;
            if (Market.SellItem == item) return;
            string why = SellRefusal(slot, item);
            if (why.Length > 0) { Say(why, true); return; }
            if (Market.MarketType != _ownKind || !Market.UserMode) Queue(() => { SetTab(_ownKind); return true; });
            ReleaseSellItem();
            Market.SellItem = item;
            ItemMoves.SetLocked(MirGridType.Inventory, slot, true);
            ItemMoves.SlotChanged.Emit(new ItemSlotKey(MirGridType.TrustMerchant, 0));
            _priceIn.Text = string.Empty;
            _sellKey = string.Empty;
            _sellNote.Text = string.Empty;
            AskHistory(item.ItemIndex);
            _priceIn.Focus();
            _dirty = true;
        }

        /// <summary>The tempCell unlock (Market tab, a right-click on the sell cell, Hide).</summary>
        private static void ReleaseSellItem()
        {
            UserItem item = Market.SellItem;
            if (item == null) return;
            Market.SellItem = null;
            ItemSlotRef cell = ItemMoves.Find(MirGridType.Inventory, item.UniqueID);
            if (cell != null) cell.Locked = false;
            ItemMoves.SlotChanged.Emit(new ItemSlotKey(MirGridType.TrustMerchant, 0));
        }

        private (uint Lo, uint Hi, uint Fee) Limits() => _ownKind == MarketPanelType.Auction
            ? (Globals.MinStartingBid, Globals.MaxStartingBid, Globals.AuctionCost)
            : (Globals.MinConsignment, Globals.MaxConsignment, Globals.ConsignmentCost);

        private sealed class SaleStats
        {
            public int N;
            public double Avg, Median;
            public uint Lo, Hi, Last;
        }

        private static SaleStats Stats(List<ClientMarketSale> sales)
        {
            if (sales == null || sales.Count == 0) return null;
            var p = new uint[sales.Count];
            double sum = 0;
            for (int i = 0; i < p.Length; i++) { p[i] = sales[i].Price; sum += p[i]; }
            var sorted = (uint[])p.Clone();
            Array.Sort(sorted);
            int mid = sorted.Length / 2;
            return new SaleStats
            {
                N = p.Length, Avg = sum / p.Length, Lo = sorted[0], Hi = sorted[^1], Last = p[^1],
                Median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + (double)sorted[mid]) / 2,
            };
        }

        private static List<ClientMarketSale> History(int itemIndex) => Market.PriceHistory.TryGetValue(itemIndex, out List<ClientMarketSale> s) ? s : null;

        private uint? Suggested()
        {
            UserItem item = Market.SellItem;
            SaleStats st = item != null ? Stats(History(item.ItemIndex)) : null;
            if (st == null) return null;
            var (lo, hi, _) = Limits();
            double v = Math.Round(st.Median / 100) * 100;
            return (uint)Math.Clamp(v, lo, hi);
        }

        private void UseSuggested()
        {
            uint? s = Suggested();
            if (s == null) return;
            _priceIn.Text = W.N(s.Value);
        }

        /// <summary>SellItemButton: the limits, the fee, a confirm, then C.ConsignItem (the bag cell stays locked until S.ConsignItem).</summary>
        private void ListItem()
        {
            UserItem item = Market.SellItem;
            Popups pop = ModernRoot.Instance?.Popups;
            if (item == null) { Say(W.T(ClientTextKeys.ModernEcoTrustPickFirst), true); return; }
            var (lo, hi, fee) = Limits();
            bool auction = _ownKind == MarketPanelType.Auction;
            if (!Eco.Amount(_priceIn.Text, out uint v) || v < lo || v > hi)
            {
                Say(W.T(auction ? ClientTextKeys.ModernEcoTrustBidRange : ClientTextKeys.ModernEcoTrustPriceRange, W.N(lo), W.N(hi)), true);
                return;
            }
            if (GameScene.Gold < fee) { Say(W.T(ClientTextKeys.ModernEcoTrustNeedFee, W.N(fee)), true); return; }
            if (!IsOpen || pop == null) { Say(W.T(ClientTextKeys.ModernEcoTrustSpeakFirst), true); return; }
            int pct = (int)Math.Round(Globals.Commission * 100);
            string text = W.T(auction ? ClientTextKeys.ModernEcoTrustAuctionAsk : ClientTextKeys.ModernEcoTrustConsignAsk, Eco.Name(item), W.N(v)) + "\n" + W.T(ClientTextKeys.ModernEcoTrustFeeAsk, W.N(fee), pct);
            MarketPanelType kind = _ownKind;
            pop.Ask(text, ok =>
            {
                if (!ok || !IsOpen || Market.SellItem != item) return;
                Market.MarketType = kind;
                Network.Enqueue(new C.ConsignItem { UniqueID = item.UniqueID, Price = v, Type = kind });
                Market.SellItem = null; // SellItemSlot = null; the bag cell stays locked until S.ConsignItem
                ItemMoves.SlotChanged.Emit(new ItemSlotKey(MirGridType.TrustMerchant, 0));
                _priceIn.Text = string.Empty;
                Say(W.T(ClientTextKeys.ModernEcoTrustSent, Eco.Name(item)), false);
                AddLog(W.T(ClientTextKeys.ModernEcoTrustLogConsign, Eco.Name(item), W.N(v)), false);
                Queue(() => { SetTab(kind); return true; });
                _dirty = true;
            }, true, PopupKind.Question, W.T(auction ? ClientTextKeys.ModernEcoTrustStartAuction : ClientTextKeys.ModernEcoTrustConsignItem));
        }

        // ------------------------------------------------------------------ watchlist (session)

        private static bool Watched(ulong id) => Watch.Exists(e => e.Id == id);

        private void ToggleWatch(ClientAuction l)
        {
            int i = Watch.FindIndex(e => e.Id == l.AuctionID);
            if (i >= 0) Watch.RemoveAt(i);
            else
            {
                if (Watch.Count >= WatchMax) { Say(W.T(ClientTextKeys.ModernEcoTrustWatchMax, WatchMax), true); return; }
                Watch.Add(new WatchEntry
                {
                    Id = l.AuctionID, Item = l.Item?.ItemIndex ?? 0, Name = l.Item?.Info?.FriendlyName ?? W.T(ClientTextKeys.ModernEcoItemN, l.Item?.ItemIndex ?? 0),
                    Count = l.Item?.Count ?? 1, Price = l.Price, Type = l.ItemType, Seller = l.Seller, Date = l.ConsignmentDate, Added = ++_watchSeq,
                });
            }
            for (int r = 0; r < Rows; r++) _bStars[r]?.Invalidate();
            _watchKey = _detKey = string.Empty;
            _dirty = true;
        }

        private void FindWatched(WatchEntry e)
        {
            ItemInfo info = GameScene.ItemInfoList.Find(x => x.Index == e.Item);
            _query = (info != null ? info.Name : e.Name).Replace(" ", string.Empty);
            _search.Text = _query;
            _filterIndex = 0;
            _subFilterIndex = -1;
            _catsKey = string.Empty;
            _tab = Tab.Browse;
            _tabs.Select(0);
            BrowseSearch();
            _dirty = true;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_tabs == null || _tabs.IsDisposed || User == null) return;
            for (int i = 0; i < _views.Length; i++) _views[i].Visible = i == (int)_tab;
            _tabs.Badge(3, Watch.Count);
            int ended = 0;
            if (Market.UserMode) foreach (ClientAuction l in Market.Listings) if (l.Seller == "Sold" || l.Seller == "Expired") ended++;
            _tabs.Badge(1, ended);
            _sortBtn.Text = W.T(SortNames[_sort]);
            _gradeBtn.Text = _minGrade == ItemGrade.None ? W.T(ClientTextKeys.ModernEcoTrustAnyGrade) : W.T(ClientTextKeys.ModernEcoTrustGradeOrBetter, Eco.GradeName(_minGrade));
            switch (_tab)
            {
                case Tab.Browse: RenderCats(); RenderBrowse(); RenderDetail(); break;
                case Tab.Mine: RenderMine(); break;
                case Tab.Sell: RenderSell(); break;
                case Tab.Watch: RenderWatch(); break;
                case Tab.Log: RenderLog(); break;
            }
        }

        private void RenderCats()
        {
            string key = _filterIndex + "|" + _subFilterIndex;
            if (key == _catsKey) return;
            _catsKey = key;
            for (int i = _cats.Content.Controls.Count - 1; i >= 0; i--) _cats.Content.Controls[i].Dispose();
            int y = 0, w = _cats.InnerWidth;
            void Btn(Filter f, Filter sub)
            {
                bool on = sub != null ? sub.Index == _subFilterIndex : f.Index == _filterIndex && (f.Sub.Length == 0 || _subFilterIndex < 0);
                string text = W.T((sub ?? f).Title) + (sub == null && f.Sub.Length > 0 ? (f.Index == _filterIndex ? " ▾" : " ▸") : string.Empty);
                bool isSub = sub != null;
                var b = new PaintBox((g, pb) => PaintCat(g, pb, text, on, isSub)) { Parent = _cats.Content, Location = new Point(0, y), Size = new Size(w, S(isSub ? 22 : 24)) };
                Filter ff = f, ss = sub;
                b.Click += (o, e) => CatClick(ff, ss);
                y += b.Size.Height + S(1);
            }
            foreach (Filter f in Filters)
            {
                Btn(f, null);
                if (f.Index == _filterIndex) foreach (Filter s in f.Sub) Btn(f, s);
            }
            _cats.FitContent(0);
        }

        private static void PaintCat(Graphics g, PaintBox b, string text, bool on, bool sub)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            if (on || b.Hovered)
                using (GraphicsPath p = ModernSkin.RoundRect(r, 4 * k))
                using (var br = new SolidBrush(on ? W.Over(ModernTheme.Accent, 0.18f) : W.Over(ModernTheme.FrameLight, 0.06f)))
                    g.FillPath(br, p);
            if (on)
                using (var br = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(br, 0, 0, 3 * k, b.Size.Height);
            ModernSkin.Text(g, text, ModernSkin.Font(sub ? 11 : 12), on || b.Hovered ? Color.White : sub ? ModernTheme.Dim : ModernTheme.Text, new RectangleF((sub ? 20 : 8) * k, 0, b.Size.Width - (sub ? 24 : 12) * k, b.Size.Height));
        }

        private void PaintHeader(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            int[] c = Cols();
            Font f = ModernSkin.Font(9, true);
            Color dim = ModernTheme.Dim;
            void T(string s, int x, int w, StringAlignment a = StringAlignment.Near) => ModernSkin.Text(g, s.ToUpperInvariant(), f, dim, new RectangleF(x * k, 0, w * k, b.Size.Height), a);
            T(W.T(ClientTextKeys.Item), c[1], c[2]);
            T(W.T(ClientTextKeys.ModernEcoTrustLv), c[3], c[4]);
            T(W.T(ClientTextKeys.ModernEcoTrustType), c[7], c[8]);
            T(W.T(ClientTextKeys.ModernEcoTrustTimeLeft), c[5], c[6]);
            T(W.T(ClientTextKeys.ModernEcoTrustPriceBid), c[9], c[10], StringAlignment.Far);
            using var pen = new Pen(W.Over(ModernTheme.Frame, 0.5f), 1f);
            g.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
        }

        /// <summary>Whether the user meets the item's class, gender and level / stat requirement.</summary>
        private static bool Usable(UserItem item)
        {
            ItemInfo info = item?.Info;
            UserObject u = User;
            if (info == null || u == null) return true;
            if (info.RequiredClass != RequiredClass.None && ((int)info.RequiredClass & (1 << (int)u.Class)) == 0) return false;
            if (info.RequiredGender != RequiredGender.None && ((int)info.RequiredGender & (1 << (int)u.Gender)) == 0) return false;
            int a = info.RequiredAmount;
            if (a <= 0) return true;
            switch (info.RequiredType)
            {
                case RequiredType.Level: return u.Level >= a;
                case RequiredType.MaxLevel: return u.Level <= a;
                case RequiredType.MaxAC: return u.Stats[Stat.MaxAC] >= a;
                case RequiredType.MaxMAC: return u.Stats[Stat.MaxMAC] >= a;
                case RequiredType.MaxDC: return u.Stats[Stat.MaxDC] >= a;
                case RequiredType.MaxMC: return u.Stats[Stat.MaxMC] >= a;
                case RequiredType.MaxSC: return u.Stats[Stat.MaxSC] >= a;
                case RequiredType.MinAC: return u.Stats[Stat.MinAC] >= a;
                case RequiredType.MinMAC: return u.Stats[Stat.MinMAC] >= a;
                case RequiredType.MinDC: return u.Stats[Stat.MinDC] >= a;
                case RequiredType.MinMC: return u.Stats[Stat.MinMC] >= a;
                case RequiredType.MinSC: return u.Stats[Stat.MinSC] >= a;
            }
            return true;
        }

        /// <summary>The current page's rows (UpdateInterface); a selection not on it is dropped.</summary>
        private List<ClientAuction> PageRows()
        {
            var rows = new List<ClientAuction>();
            List<ClientAuction> all = Market.Listings;
            for (int i = _page * Rows; i < Math.Min(all.Count, _page * Rows + Rows); i++) rows.Add(all[i]);
            if (_selected != null && !rows.Contains(_selected)) _selected = null;
            return rows;
        }

        private static DateTime EndsAt(DateTime consigned) => consigned.AddDays(Globals.ConsignmentLength);

        private static string TimeLeft(DateTime consigned, out bool soon)
        {
            double left = (EndsAt(consigned) - CMain.Now).TotalSeconds;
            soon = left > 0 && left < 3600;
            return left > 0 ? W.Duration((long)left) : W.T(ClientTextKeys.ModernEcoTrustEnded);
        }

        private void RenderBrowse()
        {
            bool loaded = !Market.UserMode;
            List<ClientAuction> page = loaded ? PageRows() : new List<ClientAuction>();
            _shown.Clear();
            foreach (ClientAuction l in page)
            {
                if (_kind == Kind.Consign && l.ItemType != MarketItemType.Consign) continue;
                if (_kind == Kind.Auction && l.ItemType != MarketItemType.Auction) continue;
                if (_usableOnly && !Usable(l.Item)) continue;
                _shown.Add(l);
            }
            var sb = new System.Text.StringBuilder().Append(loaded).Append('|').Append(_selected?.AuctionID ?? 0).Append('|').Append(_hasPending ? _pending : 0).Append('|').Append(Ready).Append('|');
            foreach (ClientAuction l in _shown) sb.Append(l.AuctionID).Append(':').Append(l.Price).Append(':').Append(l.Item?.Info?.Index ?? -1).Append(Watched(l.AuctionID) ? "w" : "").Append(',');
            string key = sb.ToString();
            int hidden = page.Count - _shown.Count;
            if (key != _browseKey)
            {
                _browseKey = key;
                for (int i = 0; i < Rows; i++)
                {
                    ClientAuction l = i < _shown.Count ? _shown[i] : null;
                    _bList[i] = l;
                    _bRows[i].Visible = l != null;
                    _bRows[i].Selected = l != null && l == _selected;
                    _bRows[i].Invalidate();
                    _bStars[i].Invalidate();
                    _bCells[i].Sync();
                }
                TickTimes();
            }
            _bNone.Visible = _shown.Count == 0;
            _bNone.Text = W.T(!loaded ? ClientTextKeys.ModernEcoLoading : page.Count > 0 ? ClientTextKeys.ModernEcoTrustPageFiltered : ClientTextKeys.ModernEcoTrustNoListings);
            RenderPager(_bPagerText, _bPrev, _bNext, loaded);
            _bPagerNote.Text = hidden > 0 ? W.T(ClientTextKeys.ModernEcoTrustHiddenN, hidden) : string.Empty;
        }

        private void RenderPager(ModernLabel text, ModernButton prev, ModernButton next, bool loaded)
        {
            text.Text = loaded && _pageCount > 0 ? (_page + 1) + " / " + _pageCount : "–";
            prev.SetDisabled(!loaded || _page <= 0);
            next.SetDisabled(!loaded || _page >= _pageCount - 1);
        }

        private void PaintBrowseRow(Graphics g, PaintBox b, int i)
        {
            ClientAuction l = _bList[i];
            if (l == null) return;
            float k = ModernRoot.K;
            int[] c = Cols();
            RectangleF r = new RectangleF(0, 0, b.Size.Width, b.Size.Height);
            if (b.Selected)
            {
                using (var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.14f))) g.FillRectangle(br, r);
                using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, 0, 0, 3 * k, r.Height);
            }
            else if (b.Hovered)
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.06f))) g.FillRectangle(br, r);
            using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.2f), 1f)) g.DrawLine(pen, 0, r.Height - 0.5f, r.Width, r.Height - 0.5f);
            ItemInfo info = l.Item?.Info;
            ModernSkin.Text(g, Eco.Name(l.Item), ModernSkin.Font(12, true), Eco.GradeColour(info), new RectangleF(c[1] * k, 4 * k, c[2] * k, 17 * k));
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernEcoTrustBy, l.Seller), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(c[1] * k, 21 * k, (c[2] - 20) * k, 14 * k));
            int lvReq = info != null && info.RequiredType == RequiredType.Level && info.RequiredAmount > 0 ? info.RequiredAmount : 0;
            ModernSkin.Text(g, lvReq > 0 ? lvReq.ToString() : "—", ModernSkin.Font(12), Usable(l.Item) ? ModernTheme.Text : ModernTheme.BadSoft, new RectangleF(c[3] * k, 0, c[4] * k, r.Height));
            bool auction = l.ItemType == MarketItemType.Auction;
            string kind = W.T(auction ? ClientTextKeys.ModernEcoTrustAuction : ClientTextKeys.ModernEcoTrustBuyNow).ToUpperInvariant();
            Font kf = ModernSkin.Font(9, true);
            float kw = Math.Min(c[8] * k, ModernSkin.Measure(kind, kf) + 12 * k);
            RectangleF pill = new RectangleF(c[7] * k, r.Height / 2f - 7 * k, kw, 14 * k);
            Color kc = auction ? ModernTheme.Gold : ModernTheme.Ok;
            using (GraphicsPath pp = ModernSkin.RoundRect(pill, 6 * k)) ModernSkin.Stroke(g, pp, ModernTheme.Mix(kc, Color.Black, 0.55f));
            ModernSkin.Text(g, kind, kf, kc, pill, StringAlignment.Center);
            ModernSkin.Text(g, W.N(l.Price), ModernSkin.Font(12, true), ModernTheme.Gold, new RectangleF(c[9] * k, 4 * k, c[10] * k, 17 * k), StringAlignment.Far);
            string sub = auction ? W.T(ClientTextKeys.ModernEcoTrustBidToBeat) : l.Item != null && l.Item.Count > 1 ? W.T(ClientTextKeys.ModernEcoTrustEach, W.N(l.Price / l.Item.Count)) : W.T(ClientTextKeys.ModernEcoTrustBuyNow);
            ModernSkin.Text(g, sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(c[9] * k, 21 * k, c[10] * k, 14 * k), StringAlignment.Far);
        }

        private static void PaintStar(Graphics g, PaintBox b, bool on)
        {
            float k = ModernRoot.K, cx = b.Size.Width / 2f, cy = b.Size.Height / 2f, r = 6 * k;
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 5;
                float rr = i % 2 == 0 ? r : r * 0.45f;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * rr, cy + (float)Math.Sin(a) * rr);
            }
            Color c = on ? ModernTheme.Gold : b.Hovered ? Color.White : ModernTheme.Dim;
            if (on) using (var br = new SolidBrush(c)) g.FillPolygon(br, pts);
            using var pen = new Pen(c, 1f);
            g.DrawPolygon(pen, pts);
        }

        /// <summary>The countdowns of the visible rows / detail (every second while shown; labels repaint only on change).</summary>
        private void TickTimes()
        {
            if (_tabs == null || _tabs.IsDisposed) return;
            if (_tab == Tab.Browse)
            {
                for (int i = 0; i < Rows; i++)
                {
                    ClientAuction l = _bList[i];
                    if (l == null) continue;
                    _bTimes[i].Text = TimeLeft(l.ConsignmentDate, out bool soon);
                    _bTimes[i].Colour = soon ? ModernTheme.Warn : ModernTheme.Dim;
                }
                if (_selected != null)
                {
                    string t = TimeLeft(_selected.ConsignmentDate, out _);
                    _buyTime.Text = t;
                    _bidTime.Text = t;
                }
            }
            else if (_tab == Tab.Mine)
            {
                for (int i = 0; i < Rows; i++)
                {
                    ClientAuction l = _mList[i];
                    if (l == null) continue;
                    bool active = l.Seller == "For Sale" || l.Seller == "No Bid" || l.Seller == "Bid Met";
                    _mTimes[i].Text = active ? TimeLeft(l.ConsignmentDate, out bool soon) : string.Empty;
                }
            }
            else if (_tab == Tab.Watch) _dirty = true; // the "left" column: the watch key holds the minute, so rows rebuild once a minute
        }

        private void RenderDetail()
        {
            ClientAuction l = _selected;
            _det.Visible = !_narrow || (l != null && _detView);
            _detEmpty.Visible = l == null;
            _detBody.Visible = l != null;
            if (l == null) { _detView = false; return; }
            ItemInfo info = l.Item?.Info;
            uint gold = GameScene.Gold;
            bool ready = Ready, busy = Busy(l), auction = l.ItemType == MarketItemType.Auction, watched = Watched(l.AuctionID);
            RenderSpark(l.Item?.ItemIndex ?? -1);
            string key = l.AuctionID + "|" + l.Price + "|" + gold + "|" + ready + "|" + busy + "|" + (info?.Index ?? -1) + "|" + _bidIn.Text + "|" + watched + "|" + Market.UserMode;
            if (key == _detKey) return;
            _detKey = key;
            _selCell.Sync();
            _detName.Text = Eco.Name(l.Item);
            _detName.Colour = Eco.GradeColour(info);
            _detSub.Text = (info != null && info.Grade != ItemGrade.None ? Eco.GradeName(info.Grade) + " · " : string.Empty)
                + W.T(auction ? ClientTextKeys.ModernEcoTrustAuction : ClientTextKeys.ModernEcoTrustBuyNow) + " · " + W.T(ClientTextKeys.ModernEcoTrustSeller, l.Seller);
            _buyBox.Visible = !auction;
            _bidBox.Visible = auction;
            string waitWhy = busy ? W.T(ClientTextKeys.ModernEcoTrustWaiting) : !ready ? W.T(ClientTextKeys.ModernEcoTrustStillAnswering) : Market.UserMode ? W.T(ClientTextKeys.ModernEcoTrustSwitchBrowse) : string.Empty;
            string t = TimeLeft(l.ConsignmentDate, out _);
            if (!auction)
            {
                _buyBig.Text = W.N(l.Price);
                _buyTime.Text = t;
                long after = (long)gold - l.Price;
                _buyAfter.Text = W.N(after);
                _buyAfter.Colour = after < 0 ? ModernTheme.BadSoft : ModernTheme.Ok;
                _buyBtn.Text = W.T(after < 0 ? ClientTextKeys.LowGold : busy ? ClientTextKeys.ModernEcoTrustBuying : ClientTextKeys.ModernEcoTrustBuyNow);
                _buyBtn.SetDisabled(after < 0 || waitWhy.Length > 0, after < 0 ? W.T(ClientTextKeys.LowGold) : waitWhy);
            }
            else
            {
                _bidBig.Text = W.N(l.Price);
                _bidTime.Text = t;
                uint min = BidFloor(l);
                _bidMin.Text = W.T(ClientTextKeys.ModernEcoTrustPriceToBeat, W.N(l.Price), W.N(min));
                _bidIn.Placeholder = W.T(ClientTextKeys.ModernEcoTrustOrMore, W.N(min));
                bool bad = !Eco.Amount(_bidIn.Text, out uint v) || v < min;
                bool poor = !bad && v > gold;
                _bidBtn.Text = W.T(busy ? ClientTextKeys.ModernEcoTrustBidding : poor ? ClientTextKeys.LowGold : ClientTextKeys.ModernEcoTrustPlaceBid);
                _bidBtn.SetDisabled(bad || poor || waitWhy.Length > 0, bad ? W.T(ClientTextKeys.ModernEcoTrustEnterAtLeast, W.N(min)) : poor ? W.T(ClientTextKeys.LowGold) : waitWhy);
            }
            _watchBtn.Text = W.T(watched ? ClientTextKeys.ModernEcoTrustWatching : ClientTextKeys.ModernEcoTrustWatchBtn);
            _watchBtn.Kind = watched ? ButtonKind.Main : ButtonKind.Normal;
            _whisperBtn.SetDisabled(Market.UserMode);
        }

        /// <summary>The recent sales line and its figures (S.MarketPriceHistory; repainted only when the sales change).</summary>
        private void RenderSpark(int itemIndex)
        {
            List<ClientMarketSale> sales = itemIndex >= 0 ? History(itemIndex) : null;
            var sb = new System.Text.StringBuilder().Append(itemIndex).Append('|');
            if (sales == null) sb.Append('?');
            else foreach (ClientMarketSale s in sales) sb.Append(s.Price).Append(',');
            string key = sb.ToString();
            if (key == _sparkKey) return;
            _sparkKey = key;
            SaleStats st = Stats(sales);
            _histTitle.Text = st != null ? W.T(ClientTextKeys.ModernEcoTrustRecentN, st.N) : W.T(ClientTextKeys.ModernEcoTrustRecent);
            _histAvg.Text = st != null ? W.T(ClientTextKeys.ModernEcoTrustAvg, W.N((long)st.Avg)) : string.Empty;
            _histLo.Text = st != null ? W.T(ClientTextKeys.ModernEcoTrustLow, W.N(st.Lo)) : string.Empty;
            _histHi.Text = st != null ? W.T(ClientTextKeys.ModernEcoTrustHigh, W.N(st.Hi)) : string.Empty;
            _histNote.Text = sales == null ? W.T(ClientTextKeys.ModernEcoTrustAsking) : st != null ? W.T(ClientTextKeys.ModernEcoTrustLastSale, W.N(st.Last)) : W.T(ClientTextKeys.ModernEcoTrustNoSales);
            _sparkSales = st != null && st.N > 1 ? sales : null;
            _spark.Invalidate();
        }

        private void PaintSpark(Graphics g, PaintBox b)
        {
            List<ClientMarketSale> sales = _sparkSales;
            if (sales == null || sales.Count < 2) return;
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            uint lo = uint.MaxValue, hi = 0;
            foreach (ClientMarketSale s in sales) { lo = Math.Min(lo, s.Price); hi = Math.Max(hi, s.Price); }
            int n = sales.Count;
            float X(int i) => i / (float)(n - 1) * (w - 6 * k) + 3 * k;
            float Y(uint p) => h - 4 * k - (p - lo) / (float)Math.Max(1, hi - lo) * (h - 10 * k);
            var pts = new PointF[n];
            for (int i = 0; i < n; i++) pts[i] = new PointF(X(i), Y(sales[i].Price));
            using (var area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[n - 1], new PointF(X(n - 1), h));
                area.AddLine(new PointF(X(n - 1), h), new PointF(X(0), h));
                area.CloseFigure();
                using var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.18f));
                g.FillPath(br, area);
            }
            using (var pen = new Pen(ModernTheme.Accent, Math.Max(1.2f, 1.6f * k)) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
            using (var br = new SolidBrush(ModernTheme.Gold))
                g.FillEllipse(br, pts[n - 1].X - 3 * k, pts[n - 1].Y - 3 * k, 6 * k, 6 * k);
        }

        private static (ClientTextKeys Text, Color Colour, bool Active) StatusOf(string seller)
        {
            switch (seller)
            {
                case "For Sale": return (ClientTextKeys.ModernEcoTrustStListed, ModernTheme.Accent, true);
                case "No Bid": return (ClientTextKeys.ModernEcoTrustStNoBids, ModernTheme.Accent, true);
                case "Bid Met": return (ClientTextKeys.ModernEcoTrustStBid, ModernTheme.Gold, true);
                case "Sold": return (ClientTextKeys.ModernEcoTrustStSold, ModernTheme.Ok, false);
                case "Expired": return (ClientTextKeys.ModernEcoTrustStExpired, ModernTheme.Warn, false);
            }
            return (ClientTextKeys.ModernEcoTrustStListed, ModernTheme.Accent, true);
        }

        private void RenderMine()
        {
            bool loaded = Market.UserMode && Market.MarketType == _ownKind;
            List<ClientAuction> rows = loaded ? PageRows() : new List<ClientAuction>();
            int sold = 0;
            if (loaded) foreach (ClientAuction l in Market.Listings) if (l.Seller == "Sold") sold++;
            _collectBtn.Text = sold > 0 ? W.T(ClientTextKeys.ModernEcoTrustCollectSoldN, sold) : W.T(ClientTextKeys.ModernEcoTrustCollectSold);
            _collectBtn.SetDisabled(!loaded || sold == 0 || !Ready, !loaded ? W.T(ClientTextKeys.ModernEcoLoading) : sold == 0 ? W.T(ClientTextKeys.ModernEcoTrustNothingSold) : W.T(ClientTextKeys.ModernEcoTrustStillAnswering));
            bool ready = Ready;
            for (int i = 0; i < Rows; i++)
            {
                ClientAuction l = i < rows.Count ? rows[i] : null;
                if (_mList[i] != l) { _mList[i] = l; _mRows[i].Invalidate(); }
                _mRows[i].Visible = l != null;
                _mCells[i].Sync();
                if (l == null) continue;
                bool busy = Busy(l);
                string key = l.AuctionID + "|" + l.Seller + "|" + l.Price + "|" + busy + "|" + ready;
                if (key == _mActsKey[i]) continue;
                _mActsKey[i] = key;
                _mRows[i].Invalidate();
                ModernPanel acts = _mActs[i];
                for (int j = acts.Controls.Count - 1; j >= 0; j--) acts.Controls[j].Dispose();
                int right = acts.Size.Width;
                ClientAuction row = l;
                void Add(string label, Action run, ButtonKind kind)
                {
                    var b = new ModernButton(label, kind) { Parent = acts };
                    b.AutoWidth(56);
                    right -= b.Size.Width;
                    b.Location = new Point(right, S(1));
                    right -= S(4);
                    if (busy || !ready) b.SetDisabled(true, busy ? W.T(ClientTextKeys.ModernEcoTrustWaiting) : W.T(ClientTextKeys.ModernEcoTrustStillAnswering));
                    b.Click += (o, e) => run();
                }
                if (l.Seller == "Sold") Add(W.T(ClientTextKeys.ModernEcoTrustCollectN, W.N(Net(l.Price))), () => GetBack(row), ButtonKind.Main);
                else if (l.Seller == "Expired") Add(W.T(ClientTextKeys.ModernEcoTrustTakeBack), () => GetBack(row), ButtonKind.Normal);
                else
                {
                    Add(W.T(ClientTextKeys.ModernEcoTrustCancel), () => GetBack(row), ButtonKind.Normal);
                    if (l.Seller == "Bid Met") Add(W.T(ClientTextKeys.ModernEcoTrustSellNowN, W.N(l.Price)), () => SellNow(row), ButtonKind.Main);
                }
            }
            TickTimes();
            _mNone.Visible = rows.Count == 0;
            _mNone.Text = !loaded ? W.T(ClientTextKeys.ModernEcoLoading) : W.T(_ownKind == MarketPanelType.Auction ? ClientTextKeys.ModernEcoTrustNoAuctions : ClientTextKeys.ModernEcoTrustNoConsigns);
            RenderPager(_mPagerText, _mPrev, _mNext, loaded);
            _mPagerNote.Text = W.T(ClientTextKeys.ModernEcoTrustMineNote, (int)Math.Round(Globals.Commission * 100));
        }

        private void PaintMineRow(Graphics g, PaintBox b, int i)
        {
            ClientAuction l = _mList[i];
            if (l == null) return;
            float k = ModernRoot.K;
            int aw = _narrow ? 200 : 230;
            using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.2f), 1f)) g.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
            ItemInfo info = l.Item?.Info;
            bool auction = l.ItemType == MarketItemType.Auction;
            float nx = 56 * k, statusX = (_width - 12 - aw - 10 - 84 - 10 - 100) * k, nw = statusX - nx - 10 * k;
            ModernSkin.Text(g, Eco.Name(l.Item), ModernSkin.Font(12, true), Eco.GradeColour(info), new RectangleF(nx, 5 * k, nw, 17 * k));
            string line = auction
                ? W.T(l.Seller == "Bid Met" ? ClientTextKeys.ModernEcoTrustLineHighest : ClientTextKeys.ModernEcoTrustLineStarting, W.N(l.Price))
                : W.T(ClientTextKeys.ModernEcoTrustLineBuyNow, W.N(l.Price));
            ModernSkin.Text(g, line, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(nx, 23 * k, nw, 15 * k));
            var (text, colour, _) = StatusOf(l.Seller);
            ModernSkin.SpacedText(g, W.T(text).ToUpperInvariant(), ModernSkin.Font(10, true), colour, new RectangleF(statusX, 0, 100 * k, b.Size.Height), 0.8f * k);
        }

        private void RenderSell()
        {
            UserObject u = User;
            int belt = u.BeltIdx, n = Math.Max(0, u.Inventory.Length - belt), cols = _narrow ? 5 : 6;
            if (_bagBuilt != n)
            {
                for (int i = 0; i < _bagCells.Count; i++) _bagCells[i].Dispose();
                _bagCells.Clear();
                for (int i = 0; i < n; i++)
                {
                    int slot = belt + i;
                    var c = new EcoItemView(40, () => slot < User.Inventory.Length ? User.Inventory[slot] : null)
                    { Parent = _sellBag.Content, Location = new Point(S(i % cols * 44), S(i / cols * 44)), Locked = () => ItemMoves.IsLocked(MirGridType.Inventory, slot) };
                    c.Clicked += btn => { if (btn == MouseButtons.Left) PickSell(slot); };
                    _bagCells.Add(c);
                }
                _sellBag.FitContent(0);
                _bagBuilt = n;
            }
            for (int i = 0; i < _bagCells.Count; i++)
            {
                _bagCells[i].Sync();
                _bagCells[i].Selected = Market.SellItem != null && _bagCells[i].Item == Market.SellItem;
            }
            _sellCell.Sync();
            UserItem item = Market.SellItem;
            var (lo, hi, fee) = Limits();
            bool auction = _ownKind == MarketPanelType.Auction;
            ItemInfo info = item?.Info;
            List<ClientMarketSale> sales = item != null ? History(item.ItemIndex) : null;
            SaleStats st = Stats(sales);
            bool valid = Eco.Amount(_priceIn.Text, out uint v) && v >= lo && v <= hi;
            string key = (item?.UniqueID ?? 0) + "|" + (info?.Index ?? -1) + "|" + _ownKind + "|" + (st != null ? st.N + ":" + st.Median : sales == null ? "?" : "0") + "|" + _priceIn.Text + "|" + (GameScene.Gold >= fee) + "|" + IsOpen;
            if (key == _sellKey) return;
            _sellKey = key;
            _sellName.Text = item != null ? Eco.Name(item) : W.T(ClientTextKeys.ModernEcoTrustNoItem);
            _sellName.Colour = Eco.GradeColour(info);
            _sellSub.Text = W.T(item != null ? ClientTextKeys.ModernEcoTrustPutBackHint : ClientTextKeys.ModernEcoTrustPickHint);
            _sellHist.Text = item == null ? string.Empty : sales == null ? W.T(ClientTextKeys.ModernEcoTrustAskingSales)
                : st != null ? W.T(ClientTextKeys.ModernEcoTrustSalesLine, st.N, W.N((long)st.Median), W.N(st.Lo), W.N(st.Hi)) : W.T(ClientTextKeys.ModernEcoTrustNoSales);
            uint? sug = Suggested();
            _useBtn.Text = sug == null ? W.T(ClientTextKeys.ModernEcoTrustNoSuggestion) : W.T(ClientTextKeys.ModernEcoTrustUseN, W.N(sug.Value));
            _useBtn.SetDisabled(sug == null, W.T(ClientTextKeys.ModernEcoTrustNoSalesSuggest));
            _priceLbl.Text = W.T(auction ? ClientTextKeys.StartingBid : ClientTextKeys.Price).ToUpperInvariant();
            _priceIn.Placeholder = W.N(lo) + " – " + W.N(hi);
            uint p = valid ? v : 0;
            _feeEl.Text = W.N(fee);
            _commEl.Text = W.N((long)(p * Globals.Commission));
            _recvLbl.Text = W.T(auction ? ClientTextKeys.ModernEcoTrustReceiveAtStart : ClientTextKeys.ModernEcoTrustReceive);
            _recvEl.Text = W.N(Net(p));
            string why = !IsOpen ? W.T(ClientTextKeys.ModernEcoTrustSpeakFirst) : item == null ? W.T(ClientTextKeys.ModernEcoTrustPickFirst)
                : !valid ? W.T(ClientTextKeys.ModernEcoTrustEnterRange, W.N(lo), W.N(hi)) : GameScene.Gold < fee ? W.T(ClientTextKeys.ModernEcoTrustNeedFee, W.N(fee)) : string.Empty;
            _listBtn.Text = W.T(auction ? ClientTextKeys.ModernEcoTrustStartAuctionFee : ClientTextKeys.ModernEcoTrustListFee, W.N(fee));
            _listBtn.SetDisabled(why.Length > 0, why);
            int pct = (int)Math.Round(Globals.Commission * 100);
            _sellRules.Text = auction
                ? W.T(ClientTextKeys.ModernEcoTrustAuctionRules, W.N(Globals.AuctionCost), W.N(Globals.MinStartingBid), W.N(Globals.MaxStartingBid), Globals.ConsignmentLength, pct)
                : W.T(ClientTextKeys.ModernEcoTrustConsignRules, W.N(Globals.ConsignmentCost), W.N(Globals.MinConsignment), W.N(Globals.MaxConsignment), Globals.ConsignmentLength, pct);
        }

        private void RenderWatch()
        {
            var live = new Dictionary<ulong, ClientAuction>();
            if (!Market.UserMode) foreach (ClientAuction l in Market.Listings) live[l.AuctionID] = l;
            var entries = new List<WatchEntry>(Watch);
            entries.Sort((a, b) => b.Added.CompareTo(a.Added));
            var sb = new System.Text.StringBuilder();
            foreach (WatchEntry e in entries) sb.Append(e.Id).Append(':').Append(live.TryGetValue(e.Id, out ClientAuction x) ? x.Price.ToString() : "-").Append(',');
            sb.Append('|').Append(CMain.Now.Minute); // the "left" column moves once a minute at most
            string key = sb.ToString();
            if (key == _watchKey) return;
            _watchKey = key;
            for (int i = _watchList.Content.Controls.Count - 1; i >= 0; i--) _watchList.Content.Controls[i].Dispose();
            int w = (int)(_watchList.InnerWidth / ModernRoot.K), y = 0;
            if (entries.Count == 0)
            {
                W.Label(_watchList.Content, W.T(ClientTextKeys.ModernEcoTrustWatchEmpty), 0, 40, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
                _watchList.FitContent(0);
                return;
            }
            W.Label(_watchList.Content, W.T(entries.Count == 1 ? ClientTextKeys.ModernEcoTrustWatchedOne : ClientTextKeys.ModernEcoTrustWatchedN, entries.Count), 0, 0, w, 18, 11, false, ModernTheme.Dim);
            y = 24;
            foreach (WatchEntry e in entries)
            {
                WatchEntry entry = e;
                ItemInfo info = GameScene.ItemInfoList.Find(i => i.Index == e.Item);
                live.TryGetValue(e.Id, out ClientAuction l);
                string name = (info != null ? info.FriendlyName : e.Name) + (e.Count > 1 ? " (" + e.Count + ")" : string.Empty);
                string sub = W.T(e.Type == MarketItemType.Auction ? ClientTextKeys.ModernEcoTrustAuction : ClientTextKeys.ModernEcoTrustBuyNow) + " · " + W.T(ClientTextKeys.ModernEcoTrustBy, e.Seller);
                string state = l != null ? W.T(ClientTextKeys.ModernEcoTrustLiveN, W.N(l.Price)) : W.T(ClientTextKeys.ModernEcoTrustSeenAt, W.N(e.Price));
                double left = (EndsAt(e.Date) - CMain.Now).TotalSeconds;
                string time = e.Date.Year > 1 ? (left > 0 ? W.T(ClientTextKeys.ModernEcoTrustLeftN, W.Duration((long)left)) : W.T(ClientTextKeys.ModernEcoTrustEnded)) : string.Empty;
                Color nameColour = Eco.GradeColour(info);
                bool isLive = l != null;
                var row = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.25f), 1f)) g.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
                    float aw = 170 * k, cw = b.Size.Width - aw;
                    ModernSkin.Text(g, name, ModernSkin.Font(12, true), nameColour, new RectangleF(4 * k, 4 * k, cw * 0.45f, 17 * k));
                    ModernSkin.Text(g, sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(4 * k, 21 * k, cw * 0.45f, 14 * k));
                    ModernSkin.Text(g, state, ModernSkin.Font(11), isLive ? ModernTheme.Ok : ModernTheme.Dim, new RectangleF(cw * 0.47f, 0, cw * 0.3f, b.Size.Height));
                    ModernSkin.Text(g, time, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(cw * 0.78f, 0, cw * 0.22f, b.Size.Height));
                }) { Parent = _watchList.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(42)) };
                var act = new ModernButton(W.T(l != null ? ClientTextKeys.ModernEcoTrustShow : ClientTextKeys.ModernEcoTrustFind), l != null ? ButtonKind.Main : ButtonKind.Normal) { Parent = row };
                act.AutoWidth(60);
                act.Location = new Point(row.Size.Width - S(40) - act.Size.Width, S(10));
                if (l != null) { ClientAuction la = l; act.Click += (o, ev) => { _tab = Tab.Browse; _tabs.Select(0); Select(la); }; }
                else
                {
                    act.Tooltip = W.T(ClientTextKeys.ModernEcoTrustFindHint2);
                    act.Click += (o, ev) => FindWatched(entry);
                }
                var del = new ModernButton("×") { Parent = row, Size = new Size(S(26), S(22)), Location = new Point(row.Size.Width - S(32), S(10)), Tooltip = W.T(ClientTextKeys.ModernEcoTrustStopWatching) };
                del.Click += (o, ev) => { Watch.Remove(entry); _watchKey = string.Empty; _dirty = true; };
                y += 44;
            }
            _watchList.FitContent(0);
        }

        private void RenderLog()
        {
            string key = _log.Count + "|" + (_log.Count > 0 ? _log[0].Time + _log[0].Text : string.Empty);
            if (key == _logKey) return;
            _logKey = key;
            for (int i = _logList.Content.Controls.Count - 1; i >= 0; i--) _logList.Content.Controls[i].Dispose();
            int w = (int)(_logList.InnerWidth / ModernRoot.K), y = 0;
            if (_log.Count == 0) W.Label(_logList.Content, W.T(ClientTextKeys.ModernEcoTrustLogEmpty), 0, 0, w, 18, 11, false, ModernTheme.Dim);
            foreach (LogLine line in _log)
            {
                LogLine l = line;
                new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    ModernSkin.Text(g, l.Time, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(0, 0, 70 * k, b.Size.Height));
                    ModernSkin.Text(g, l.Text, ModernSkin.Font(12), l.Err ? ModernTheme.BadSoft : ModernTheme.Text, new RectangleF(76 * k, 0, b.Size.Width - 76 * k, b.Size.Height));
                    using var pen = new Pen(W.Over(ModernTheme.Frame, 0.25f), 1f);
                    g.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
                }) { Parent = _logList.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(26)), NotControl = true };
                y += 27;
            }
            _logList.FitContent(0);
        }
    }
}
