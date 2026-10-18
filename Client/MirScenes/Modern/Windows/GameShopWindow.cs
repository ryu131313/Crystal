// Ported from WebClient/src/ui/modern/windows/gameShop.ts (GameShopWindow): the new UI's game shop over GameScene.GameShopInfoList
// (S.GameShopInfo at login, S.GameShopStock, S.GameDataChanged shop edits) and the wallets GameScene.Credit / GameScene.Gold.
// Classic reference: GameShopDialog + MirGameShopCell (Client/MirScenes/Dialogs/GameshopDialog.cs, Classic/MirGameShopCell.cs).
//  - sections All / Top sellers (TopItem) / Deals (Deal) / New (dated within 7 days, shown once one arrived: GameShopNewItems)
//    / Wishlist, then the categories of what is left with counts; the class filter starts on the user's class on the first
//    open (GameShopDialog.Show) and matches GameShopItem.Class or "All"; the search matches the friendly name or category;
//    sort Featured / Name / Credits / Gold; 12 cards a page;
//  - DEAL ribbon from Deal, the struck-through "was" price from DealCreditPrice / DealGoldPrice (0 = none);
//  - stock: GameShopItem.Stock is what is LEFT (0 = unlimited); S.GameShopStock 0 removes the item (GameScene);
//  - quantity 1-99, at most 5 stacks (Quantity x Count / StackSize ≤ 5) and within stock (MirGameShopCell.quantityUp, Shift = 10);
//  - Buy asks with the desktop's confirm wording and sends C.GameshopBuy; Gift sends C.GameshopGift to a friend picked from the
//    friend list. SERVER AUTHORITATIVE: nothing is deducted locally. The buttons stay locked until the wallet drops
//    (S.LoseCredit / S.LoseGold) or 5 s pass (refusals are chat lines only); a charged purchase goes into this session's list;
//  - the carousel (top items and deals) turns every 5 s while the window is open, not while hovered or a product is open.
// Desktop: the wishlist is kept for this session (the web keeps it in the browser's storage); no confetti; the narrow layout
// (900 px) below a 1040 px wide screen. HIDDEN (no system): top up, preview on the character, purchase history beyond the session.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class GameShopWindow : ModernWindow
    {
        public const string WindowKey = "gameShop";
        private const int Page = 12, CarouselMs = 5000, FeaturedMax = 8, AnswerMs = 5000, MaxStacks = 5, MaxQuantity = 99, HistoryMax = 20, H = 630;
        private const byte PayCredit = 0, PayGold = 1;
        private const string AllClasses = "All";

        private enum Section { All, Top, Deals, New, Wish }
        private enum SortBy { Featured, Name, CreditUp, CreditDown, GoldUp, GoldDown }

        private static readonly (Section S, ClientTextKeys Name, int Icon)[] Sections =
        {
            (Section.All, ClientTextKeys.ModernEcoShopAll, ModernIcons.Square), (Section.Top, ClientTextKeys.ModernEcoShopTop, ModernIcons.Star),
            (Section.Deals, ClientTextKeys.ModernEcoShopDeals, ModernIcons.Tag), (Section.New, ClientTextKeys.ModernEcoShopNew, ModernIcons.Spark),
            (Section.Wish, ClientTextKeys.ModernEcoShopWishlist, ModernIcons.HeartFull),
        };
        private static readonly ClientTextKeys[] SortNames =
        {
            ClientTextKeys.ModernEcoShopSortFeatured, ClientTextKeys.ModernEcoShopSortName, ClientTextKeys.ModernEcoShopSortCreditUp,
            ClientTextKeys.ModernEcoShopSortCreditDown, ClientTextKeys.ModernEcoShopSortGoldUp, ClientTextKeys.ModernEcoShopSortGoldDown,
        };

        /// <summary>GameShopDialog.New.Visible: a new item arrived (GameShopNewItems). Reset when the modern UI is built.</summary>
        internal static bool HasNew;
        /// <summary>The wishlist (ItemInfo indexes), kept for the session.</summary>
        private static readonly HashSet<int> Wish = new HashSet<int>();

        private Section _section = Section.All;
        private string _category;
        private string _cls = string.Empty; // GameShopItem.Class to show ("" = every class)
        private string _query = string.Empty;
        private SortBy _sort = SortBy.Featured;
        private int _page;
        private bool _classSet;
        private int _slide;
        private long _nextSlide;

        // product view
        private int _viewG = -1;
        private GameShopItem _viewBuiltFor;
        private byte _pay = PayCredit;
        private int _qty = 1;
        private bool _giftOpen;
        private string _giftTo = string.Empty, _note = string.Empty, _friendsKey = string.Empty;
        private bool _noteBad;

        private sealed class Pending
        {
            public int GIndex, Qty;
            public ItemInfo Info;
            public ushort Count;
            public string Name, To;
            public long Cost, Before, Until;
            public byte Pay;
        }
        private Pending _pending;

        private sealed class HistoryRow
        {
            public int Qty;
            public ItemInfo Info;
            public ushort Count;
            public string Name, Paid, To;
            public DateTime At;
        }
        private readonly List<HistoryRow> _history = new List<HistoryRow>();
        private bool _showHist;

        // layout / controls
        private bool _narrow;
        private int _width, _navW, _heroH;
        private PaintBox _crWal, _gdWal;
        private uint _lastCredit = uint.MaxValue, _lastGold = uint.MaxValue;
        private ModernButton _sortBtn, _classBtn, _histBtn, _prevBtn, _nextBtn;
        private ModernPanel _hero, _grid, _modal, _hist;
        private ScrollView _nav;
        private ModernLabel _sum, _pageLbl;
        private PaintBox _toast;
        private string _toastText = string.Empty;
        private bool _toastBad;
        private long _toastUntil;
        private string _heroKey = string.Empty, _navKey = string.Empty, _gridKey = string.Empty, _histKey = string.Empty, _viewKey = string.Empty;
        private bool _hoverHero;
        private bool _dirty = true;
        private long _sig = long.MinValue, _nextSig;

        // product view parts
        private PaintBox _pvCredit, _pvGold, _pvTotal;
        private ModernLabel _pvQty, _pvNote, _pvStock;
        private ModernButton _pvBuy, _pvGift, _pvWish;
        private ModernPanel _pvGiftRow;
        private string _pvTotalText = string.Empty, _pvWasText = string.Empty;
        private bool _pvTotalBad;

        private readonly Action _friendsChanged;

        public GameShopWindow() : base(WindowKey, W.T(ClientTextKeys.ModernEcoShop), 12, 20)
        {
            _friendsChanged = () => { if (_viewG >= 0) _dirty = true; };
        }

        // test hooks
        internal int ViewShown => _viewG;
        internal int Quantity => _qty;
        internal bool GiftOpen => _giftOpen;
        internal string NoteShown => _note;
        internal bool PendingShown => _pending != null;
        internal int HistoryCount => _history.Count;
        internal ModernPanel Grid => _grid;
        internal ModernPanel Modal => _modal;
        internal ModernButton BuyButton => _pvBuy;
        internal ModernButton GiftButton => _pvGift;
        internal string SectionShown => _section.ToString();
        internal string ClassShown => _cls;

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            _width = _narrow ? 900 : 1000;
            _navW = _narrow ? 140 : 172;
            _heroH = _narrow ? 112 : 140;
            ContentWidth = _width;
            ContentHeight = H;
            MirControl m = new ModernPanel { Parent = this, Location = new Point(0, S(TitleHeight)), Size = new Size(S(_width), S(H)) };

            // ---- top bar: wallets, search, sort, class, purchases
            _crWal = new PaintBox((g, b) => PaintWallet(g, b, true)) { Parent = m, Location = new Point(S(14), S(9)), Size = new Size(S(_narrow ? 110 : 150), S(28)), NotControl = true };
            _gdWal = new PaintBox((g, b) => PaintWallet(g, b, false)) { Parent = m, Location = new Point(S(_narrow ? 130 : 170), S(9)), Size = new Size(S(_narrow ? 110 : 130), S(28)), NotControl = true };
            _histBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoShopPurchases)) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernEcoShopPurchasesHint) };
            _histBtn.Size = new Size(S(_narrow ? 96 : 110), S(28));
            _histBtn.Location = new Point(S(_width - 14) - _histBtn.Size.Width, S(9));
            _histBtn.Click += (o, e) => { _showHist = !_showHist; _histKey = string.Empty; _dirty = true; };
            _classBtn = new ModernButton(string.Empty) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernEcoShopClassHint) };
            _classBtn.Size = new Size(S(_narrow ? 96 : 110), S(28));
            _classBtn.Location = new Point(_histBtn.Location.X - S(6) - _classBtn.Size.Width, S(9));
            _classBtn.Click += (o, e) => CycleClass(CMain.Shift ? -1 : 1);
            _sortBtn = new ModernButton(string.Empty) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernEcoShopSortHint) };
            _sortBtn.Size = new Size(S(_narrow ? 120 : 150), S(28));
            _sortBtn.Location = new Point(_classBtn.Location.X - S(6) - _sortBtn.Size.Width, S(9));
            _sortBtn.Click += (o, e) => { _sort = (SortBy)(((int)_sort + (CMain.Shift ? SortNames.Length - 1 : 1)) % SortNames.Length); _page = 0; _dirty = true; };
            int sw = _narrow ? 160 : 210;
            var search = new SearchBox(sw, W.T(ClientTextKeys.ModernEcoShopSearch)) { Parent = m };
            search.Location = new Point(_sortBtn.Location.X - S(8) - search.Size.Width, S(11));
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); _page = 0; _dirty = true; };
            W.Divider(m, 0, 46, _width);

            // ---- carousel
            _hero = new ModernPanel { Parent = m, Location = new Point(S(14), S(56)), Size = new Size(S(_width - 28), S(_heroH)) };

            // ---- body: nav | grid + pager
            int bodyY = 56 + _heroH + 10;
            _nav = new ScrollView(_navW - 22, H - bodyY - 10) { Parent = m, Location = new Point(S(14), S(bodyY)), Background = W.Page };
            _nav.Framed = false;
            int gx = _navW, gw = _width - _navW - 14;
            _grid = new ModernPanel { Parent = m, Location = new Point(S(gx), S(bodyY)), Size = new Size(S(gw), S(H - bodyY - 44)) };
            _sum = W.Label(m, string.Empty, gx, H - 32, 200, 22, 11, false, ModernTheme.Dim);
            _nextBtn = new ModernButton("›") { Parent = m, Size = new Size(S(26), S(22)), Location = new Point(S(_width - 14 - 26), S(H - 32)), Tooltip = W.T(ClientTextKeys.ModernWinNextPage) };
            _nextBtn.Click += (o, e) => { _page++; _dirty = true; };
            _pageLbl = W.Label(m, string.Empty, _width - 14 - 26 - 66, H - 32, 62, 22, 11, false, ModernTheme.Dim, StringAlignment.Center);
            _prevBtn = new ModernButton("‹") { Parent = m, Size = new Size(S(26), S(22)), Location = new Point(S(_width - 14 - 26 - 66 - 30), S(H - 32)), Tooltip = W.T(ClientTextKeys.ModernEcoPrevPage) };
            _prevBtn.Click += (o, e) => { if (_page > 0) { _page--; _dirty = true; } };

            // ---- overlays: purchases, product view, toast (created last: drawn and hit first)
            _hist = new ModernPanel { Parent = m, Location = new Point(S(_width - 14 - 310), S(50)), Size = new Size(S(310), S(400)), Visible = false };
            _modal = new ModernPanel { Parent = m, Location = Point.Empty, Size = new Size(S(_width), S(H)), Visible = false };
            _toast = new PaintBox(PaintToast) { Parent = m, Size = new Size(S(_width - 200), S(36)), Location = new Point(S(100), S(H - 72)), Visible = false, NotControl = true };

            _heroKey = _navKey = _gridKey = _histKey = _viewKey = string.Empty;
            _viewBuiltFor = null;
            _lastCredit = _lastGold = uint.MaxValue;
            _dirty = true;
            Render();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            GameScene.Models.Friends.Changed.On(_friendsChanged);
            if (!_classSet && GameScene.User != null)
            {
                _classSet = true; // GameShopDialog.Show: the class filter starts on the user's class
                _cls = GameScene.User.Class.ToString();
            }
            _nextSlide = CMain.Time + CarouselMs;
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            GameScene.Models.Friends.Changed.Off(_friendsChanged);
            ModernTooltip.HideAll();
            CloseView(false);
            _showHist = false;
            _hoverHero = false;
        }

        public override bool HandleKey(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape || e.Shift) return false;
            if (_viewG >= 0) { CloseView(true); return true; }
            if (_showHist) { _showHist = false; _dirty = true; return true; }
            return false;
        }

        public override void Process()
        {
            base.Process();
            long now = CMain.Time;
            Pending p = _pending;
            if (p != null)
            {
                // done only when the wallet dropped by the expected cost (not any drop: a repair or a market buy meanwhile);
                // income meanwhile raises the level the drop is measured from
                long have = p.Pay == PayCredit ? GameScene.Credit : GameScene.Gold;
                if (have > p.Before) p.Before = have;
                if (p.Cost > 0 && p.Before - have >= p.Cost) Finish(true);
                else if (now >= p.Until) Finish(false);
            }
            if (GameScene.Credit != _lastCredit || GameScene.Gold != _lastGold)
            {
                _lastCredit = GameScene.Credit;
                _lastGold = GameScene.Gold;
                _crWal?.Invalidate();
                _gdWal?.Invalidate();
                if (_viewG >= 0) _dirty = true;
            }
            if (now >= _nextSig) // S.GameDataChanged edits the list without a seam call: a cheap signature poll
            {
                _nextSig = now + 500;
                long sig = GameScene.GameShopInfoList.Count;
                foreach (GameShopItem it in GameScene.GameShopInfoList) sig = sig * 31 + it.GIndex * 7 + it.Stock + it.GoldPrice + it.CreditPrice;
                if (sig != _sig) { _sig = sig; _dirty = true; _viewBuiltFor = null; }
            }
            if (_hero != null)
            {
                bool hover = _hero.Visible && _hero.DisplayRectangle.Contains(CMain.MPoint);
                if (hover != _hoverHero) _hoverHero = hover;
                if (now >= _nextSlide)
                {
                    _nextSlide = now + CarouselMs;
                    if (!_hoverHero && _viewG < 0) { _slide++; _heroKey = string.Empty; _dirty = true; }
                }
            }
            if (_toast != null && _toast.Visible && now >= _toastUntil) _toast.Visible = false;
            if (_dirty) Render();
        }

        /// <summary>GameShopNewItems / GameShopStockChanged: the catalogue changed.</summary>
        internal void ShopChanged()
        {
            _dirty = true;
            _viewBuiltFor = null;
        }

        /// <summary>Opens the shop on one item (by GIndex); false when it is not listed.</summary>
        public bool OpenItem(int gIndex)
        {
            if (Find(gIndex) == null) return false;
            Open();
            OpenView(gIndex);
            return true;
        }

        // ------------------------------------------------------------------ rules

        private static GameShopItem Find(int gIndex)
        {
            foreach (GameShopItem it in GameScene.GameShopInfoList) if (it.GIndex == gIndex) return it;
            return null;
        }

        private static string ShopName(GameShopItem it) => it.Info?.FriendlyName ?? W.T(ClientTextKeys.ModernEcoItemN, it.ItemIndex);
        private static bool CanPay(GameShopItem it, byte pay) => pay == PayCredit ? it.CanBuyCredit : it.CanBuyGold;
        private static long PriceOf(GameShopItem it, byte pay) => pay == PayCredit ? it.CreditPrice : it.GoldPrice;
        private static long WasOf(GameShopItem it, byte pay)
        {
            long was = pay == PayCredit ? it.DealCreditPrice : it.DealGoldPrice;
            return was > PriceOf(it, pay) ? was : 0;
        }
        private static byte LeadPay(GameShopItem it) => it.CanBuyCredit || !it.CanBuyGold ? PayCredit : PayGold;
        private static bool IsNew(GameShopItem it) => it.Date > CMain.Now.AddDays(-7);
        private static bool HasNewItems
        {
            get
            {
                if (!HasNew) return false;
                foreach (GameShopItem it in GameScene.GameShopInfoList) if (IsNew(it)) return true;
                return false;
            }
        }

        /// <summary>MirGameShopCell.quantityUp's limits: 99, at most 5 stacks, the stock left (0 = unlimited). 0 = the pack alone is
        /// over 5 stacks.</summary>
        private static int MaxQty(GameShopItem it)
        {
            int stack = Math.Max(1, (int)(it.Info?.StackSize ?? 1)), count = Math.Max(1, (int)it.Count);
            int q = Math.Min(MaxQuantity, MaxStacks * stack / count);
            if (it.Stock > 0) q = Math.Min(q, it.Stock);
            return Math.Max(0, q);
        }

        private static string StockText(GameShopItem it)
        {
            if (it.Stock <= 0) return string.Empty;
            return it.iStock ? W.T(ClientTextKeys.ModernEcoShopLeftForYou, W.N(it.Stock)) : W.T(ClientTextKeys.ModernEcoShopOnlyLeft, W.N(it.Stock));
        }

        private bool MatchesBase(GameShopItem it)
        {
            if (_cls.Length > 0 && it.Class != _cls && it.Class != AllClasses) return false;
            if (_query.Length == 0) return true;
            return ShopName(it).ToLowerInvariant().Contains(_query) || (it.Category ?? string.Empty).ToLowerInvariant().Contains(_query);
        }

        private static bool InSection(GameShopItem it, Section s)
        {
            switch (s)
            {
                case Section.Top: return it.TopItem;
                case Section.Deals: return it.Deal;
                case Section.New: return IsNew(it);
                case Section.Wish: return it.Info != null && Wish.Contains(it.Info.Index);
            }
            return true;
        }

        private void Sorted(List<GameShopItem> list)
        {
            int ByName(GameShopItem a, GameShopItem b) => string.Compare(ShopName(a), ShopName(b), StringComparison.CurrentCultureIgnoreCase);
            Comparison<GameShopItem> ByPrice(byte pay, int dir) => (a, b) =>
            {
                bool ca = CanPay(a, pay), cb = CanPay(b, pay);
                if (ca != cb) return ca ? -1 : 1; // items not sold for that currency go last
                int c = PriceOf(a, pay).CompareTo(PriceOf(b, pay)) * dir;
                return c != 0 ? c : ByName(a, b);
            };
            switch (_sort)
            {
                case SortBy.Name: list.Sort(ByName); break;
                case SortBy.CreditUp: list.Sort(ByPrice(PayCredit, 1)); break;
                case SortBy.CreditDown: list.Sort(ByPrice(PayCredit, -1)); break;
                case SortBy.GoldUp: list.Sort(ByPrice(PayGold, 1)); break;
                case SortBy.GoldDown: list.Sort(ByPrice(PayGold, -1)); break;
                default:
                    list.Sort((a, b) =>
                    {
                        int c = b.TopItem.CompareTo(a.TopItem);
                        if (c == 0) c = b.Deal.CompareTo(a.Deal);
                        return c != 0 ? c : ByName(a, b);
                    });
                    break;
            }
        }

        private void CycleClass(int step)
        {
            string[] classes = { string.Empty, nameof(MirClass.Warrior), nameof(MirClass.Wizard), nameof(MirClass.Taoist), nameof(MirClass.Assassin), nameof(MirClass.Archer) };
            int i = Array.IndexOf(classes, _cls);
            _cls = classes[((i < 0 ? 0 : i) + step + classes.Length) % classes.Length];
            _category = null;
            _page = 0;
            _dirty = true;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_grid == null || _grid.IsDisposed) return;
            _sortBtn.Text = W.T(ClientTextKeys.ModernEcoShopSortBy, W.T(SortNames[(int)_sort]));
            _classBtn.Text = _cls.Length == 0 ? W.T(ClientTextKeys.ModernEcoShopAllClasses) : ClassName(_cls);
            var all = new List<GameShopItem>();
            foreach (GameShopItem it in GameScene.GameShopInfoList) if (MatchesBase(it)) all.Add(it);
            if (_section == Section.New && !HasNewItems) _section = Section.All;
            RenderNav(all);
            var inSection = all.FindAll(it => InSection(it, _section));
            if (_category != null && !inSection.Exists(it => it.Category == _category)) _category = null;
            var list = _category == null ? inSection : inSection.FindAll(it => it.Category == _category);
            Sorted(list);
            RenderGrid(list);
            RenderHero();
            RenderView();
            RenderHistory();
        }

        private static string ClassName(string cls) =>
            Enum.TryParse(cls, out MirClass c) ? c.ToLocalizedString() : cls;

        private static string JoinParts(string sep, params string[] parts)
        {
            string r = string.Empty;
            foreach (string p in parts)
                if (!string.IsNullOrEmpty(p)) r = r.Length == 0 ? p : r + sep + p;
            return r;
        }

        private static string Pretty(string s) => string.IsNullOrEmpty(s) ? string.Empty : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private void PaintWallet(Graphics g, PaintBox b, bool credit)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f))
            {
                if (credit)
                {
                    using var br = new LinearGradientBrush(r, W.Over(ModernTheme.Accent, 0.4f), W.Over(ModernTheme.Accent, 0.15f), LinearGradientMode.Horizontal);
                    g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, ModernTheme.Accent);
                }
                else
                {
                    using var br = new SolidBrush(W.Over(ModernTheme.Gold, 0.08f));
                    g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, W.Over(ModernTheme.Gold, 0.4f));
                }
            }
            float cx = 18 * k, cy = b.Size.Height / 2f;
            if (credit) Gem(g, cx, cy, 6 * k);
            else Coin(g, cx, cy, 6 * k);
            string n = W.N(credit ? GameScene.Credit : GameScene.Gold);
            Font f = ModernSkin.Font(13, true);
            float nw = ModernSkin.Measure(n, f);
            ModernSkin.Text(g, n, f, credit ? Color.White : ModernTheme.Gold, new RectangleF(30 * k, 0, b.Size.Width - 34 * k, b.Size.Height));
            if (credit && !_narrow)
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernEcoShopCredits), ModernSkin.Font(11, true), ModernTheme.Dim, new RectangleF(30 * k + nw + 6 * k, 0, b.Size.Width, b.Size.Height));
        }

        /// <summary>The credit gem (.gem: a rotated square, white to accent).</summary>
        private static void Gem(Graphics g, float cx, float cy, float r)
        {
            PointF[] pts = { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) };
            using var br = new LinearGradientBrush(new RectangleF(cx - r, cy - r, 2 * r, 2 * r), Color.White, ModernTheme.Accent, 45f);
            g.FillPolygon(br, pts);
        }

        /// <summary>The gold coin (.coin: a radial gold disc).</summary>
        private static void Coin(Graphics g, float cx, float cy, float r)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
            using var br = new PathGradientBrush(path) { CenterColor = ModernTheme.Hex("#ffe9a0"), SurroundColors = new[] { ModernTheme.Hex("#c9921f") }, CenterPoint = new PointF(cx - r * 0.3f, cy - r * 0.3f) };
            g.FillPath(br, path);
        }

        /// <summary>A price: the currency glyph, a struck "was" price, the price (prices.ts priceKids).</summary>
        private static float Price(Graphics g, float x, float y, float h, GameShopItem it, byte pay, int mult, float size, bool compact, Color colour)
        {
            float k = ModernRoot.K;
            if (pay == PayCredit) Gem(g, x + 5 * k, y + h / 2f, 5 * k);
            else Coin(g, x + 5 * k, y + h / 2f, 5 * k);
            x += 14 * k;
            long was = WasOf(it, pay);
            Font f = ModernSkin.Font(size, true);
            if (was > 0)
            {
                string ws = compact ? W.Compact(was * mult) : W.N(was * mult);
                Font sf = ModernSkin.Font(size * 0.75f, true);
                float ww = ModernSkin.Measure(ws, sf);
                ModernSkin.Text(g, ws, sf, ModernTheme.Dim, new RectangleF(x, y, ww + 2, h), StringAlignment.Near, StringAlignment.Center, false);
                using (var pen = new Pen(ModernTheme.Dim, 1f)) g.DrawLine(pen, x, y + h / 2f, x + ww, y + h / 2f);
                x += ww + 5 * k;
            }
            string ps = compact ? W.Compact(PriceOf(it, pay) * mult) : W.N(PriceOf(it, pay) * mult);
            float pw = ModernSkin.Measure(ps, f);
            ModernSkin.Text(g, ps, f, colour, new RectangleF(x, y, pw + 2, h), StringAlignment.Near, StringAlignment.Center, false);
            return x + pw + 8 * k;
        }

        private void RenderNav(List<GameShopItem> all)
        {
            int[] counts = new int[Sections.Length];
            var cats = new List<string>();
            var catCounts = new Dictionary<string, int>();
            foreach (GameShopItem it in all)
            {
                for (int i = 0; i < Sections.Length; i++) if (InSection(it, Sections[i].S)) counts[i]++;
                string c = it.Category ?? string.Empty;
                if (c.Length > 0 && InSection(it, _section))
                {
                    if (!catCounts.ContainsKey(c)) { catCounts[c] = 0; cats.Add(c); }
                    catCounts[c]++;
                }
            }
            bool hasNew = HasNewItems;
            var sb = new System.Text.StringBuilder().Append(_section).Append('|').Append(_category).Append('|').Append(hasNew).Append('|').Append(string.Join(",", counts)).Append('|');
            foreach (string c in cats) sb.Append(c).Append(':').Append(catCounts[c]).Append(',');
            string key = sb.ToString();
            if (key == _navKey) return;
            _navKey = key;
            for (int i = _nav.Content.Controls.Count - 1; i >= 0; i--) _nav.Content.Controls[i].Dispose();
            int y = 0, w = _nav.InnerWidth;
            void Entry(string label, int icon, int n, bool on, Action click)
            {
                bool narrow = _narrow;
                var b = new PaintBox((g, pb) => PaintNav(g, pb, label, n, on, icon >= 0, narrow)) { Parent = _nav.Content, Location = new Point(0, y), Size = new Size(w, S(28)), Hint = label };
                if (icon >= 0) b.AfterPaint = pb => W.DrawIcon(icon, new Rectangle(pb.DisplayLocation.X + S(9), pb.DisplayLocation.Y + S(7), S(14), S(14)), on ? ModernTheme.Accent : ModernTheme.Dim);
                b.Click += (o, e) => click();
                y += S(30);
            }
            for (int i = 0; i < Sections.Length; i++)
            {
                Section s = Sections[i].S;
                if (s == Section.New && !hasNew) continue; // GameShopDialog.New.Visible
                Entry(W.T(Sections[i].Name), Sections[i].Icon, counts[i], _section == s && _category == null, () => { _section = s; _category = null; _page = 0; _dirty = true; });
            }
            if (cats.Count > 0)
            {
                W.Divider(_nav.Content, 4, (int)(y / ModernRoot.K) + 3, (int)(w / ModernRoot.K) - 8);
                y += S(10);
            }
            foreach (string c in cats)
            {
                string cat = c;
                Entry(Pretty(cat), -1, catCounts[cat], _category == cat, () => { _category = _category == cat ? null : cat; _page = 0; _dirty = true; });
            }
            _nav.FitContent(0);
        }

        private static void PaintNav(Graphics g, PaintBox b, string label, int n, bool on, bool icon, bool narrow)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                if (on || b.Hovered)
                    using (var br = new SolidBrush(W.Over(ModernTheme.Accent, on ? 0.16f : 0.08f)))
                        g.FillPath(br, p);
                if (on) ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.6f));
            }
            if (on)
                using (var br = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(br, 1, 2 * k, 3 * k, b.Size.Height - 4 * k);
            float x = (icon ? 30 : 10) * k;
            string ns = W.N(n);
            float nw = ModernSkin.Measure(ns, ModernSkin.Font(10)) + 4;
            ModernSkin.Text(g, label, ModernSkin.Font(narrow ? 11 : 12, true), on ? Color.White : b.Hovered ? ModernTheme.Text : ModernTheme.Dim, new RectangleF(x, 0, b.Size.Width - x - nw - 10 * k, b.Size.Height));
            ModernSkin.Text(g, ns, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(b.Size.Width - nw - 8 * k, 0, nw, b.Size.Height), StringAlignment.Far);
        }

        private void RenderGrid(List<GameShopItem> list)
        {
            int pages = Math.Max(1, (list.Count + Page - 1) / Page);
            _page = Math.Clamp(_page, 0, pages - 1);
            _pageLbl.Text = (_page + 1) + " / " + pages;
            _sum.Text = W.T(list.Count == 1 ? ClientTextKeys.ModernEcoOneItem : ClientTextKeys.ModernEcoNItems, list.Count);
            _prevBtn.SetDisabled(_page == 0);
            _nextBtn.SetDisabled(_page >= pages - 1);
            int from = _page * Page, to = Math.Min(list.Count, from + Page);
            var sb = new System.Text.StringBuilder().Append(_section).Append('|').Append(_category).Append('|');
            for (int i = from; i < to; i++)
            {
                GameShopItem it = list[i];
                sb.Append(it.GIndex).Append(':').Append(it.Stock).Append(':').Append(it.GoldPrice).Append(':').Append(it.CreditPrice).Append(':').Append(it.DealGoldPrice).Append(':')
                  .Append(it.DealCreditPrice).Append(':').Append(it.Deal ? 1 : 0).Append(it.TopItem ? 1 : 0).Append(':').Append(it.Info != null && Wish.Contains(it.Info.Index) ? 1 : 0).Append(';');
            }
            string key = sb.ToString();
            if (key == _gridKey) return;
            _gridKey = key;
            ModernTooltip.HideAll();
            for (int i = _grid.Controls.Count - 1; i >= 0; i--) _grid.Controls[i].Dispose();
            if (from >= to)
            {
                ClientTextKeys text = _section == Section.Wish && _category == null && _query.Length == 0 ? ClientTextKeys.ModernEcoShopWishEmpty
                    : GameScene.GameShopInfoList.Count > 0 ? ClientTextKeys.ModernEcoNoMatch : ClientTextKeys.ModernEcoShopEmpty;
                W.Label(_grid, W.T(text), 0, 0, (int)(_grid.Size.Width / ModernRoot.K), (int)(_grid.Size.Height / ModernRoot.K), 12, false, ModernTheme.Dim, StringAlignment.Center);
                return;
            }
            float gap = S(8);
            float cw = (_grid.Size.Width - 3 * gap) / 4f, ch = (_grid.Size.Height - 2 * gap) / 3f;
            for (int i = from; i < to; i++)
            {
                int n = i - from;
                Card(list[i], new Rectangle((int)(n % 4 * (cw + gap)), (int)(n / 4 * (ch + gap)), (int)cw, (int)ch));
            }
        }

        private void Card(GameShopItem it, Rectangle r)
        {
            int g0 = it.GIndex;
            bool wished = it.Info != null && Wish.Contains(it.Info.Index);
            var card = new PaintBox((g, b) => PaintCard(g, b, it)) { Parent = _grid, Location = r.Location, Size = r.Size, Hint = ShopName(it) };
            card.Click += (o, e) => OpenView(g0);
            int metaH = S(54);
            var art = new EcoItemView(10, () => Eco.Show(it.Info, it.Count)) { Parent = card, Well = false, Scale = 1.6f, ShowCount = false };
            art.Size = new Size(r.Width - S(40), r.Height - metaH - S(8));
            art.Location = new Point(S(20), S(6));
            art.Sync();
            art.Clicked += _ => OpenView(g0);
            var heart = new PaintBox((g, b) => PaintHeart(g, b, wished)) { Parent = card, Size = new Size(S(22), S(22)), Location = new Point(r.Width - S(28), S(6)), Hint = W.T(wished ? ClientTextKeys.ModernEcoShopWishRemove : ClientTextKeys.ModernEcoShopWishAdd) };
            heart.Click += (o, e) => ToggleWish(it);
        }

        private static void PaintHeart(Graphics g, PaintBox b, bool on)
        {
            float k = ModernRoot.K;
            using (var br = new SolidBrush(W.Over(Color.Black, 0.35f))) g.FillEllipse(br, 0, 0, b.Size.Width - 1, b.Size.Height - 1);
            Color c = on ? ModernTheme.Hex("#ff6a8a") : b.Hovered ? Color.White : ModernTheme.Dim;
            float cx = b.Size.Width / 2f, cy = b.Size.Height / 2f + 1 * k, s = 5 * k;
            using var path = new GraphicsPath();
            path.AddBezier(cx, cy + s, cx - s * 2.2f, cy - s * 0.3f, cx - s * 0.9f, cy - s * 1.9f, cx, cy - s * 0.7f);
            path.AddBezier(cx, cy - s * 0.7f, cx + s * 0.9f, cy - s * 1.9f, cx + s * 2.2f, cy - s * 0.3f, cx, cy + s);
            if (on) using (var br = new SolidBrush(c)) g.FillPath(br, path);
            using var pen = new Pen(c, Math.Max(1f, 1.4f * k));
            g.DrawPath(pen, path);
        }

        private static void PaintCard(Graphics g, PaintBox b, GameShopItem it)
        {
            float k = ModernRoot.K, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, rad))
            {
                ModernSkin.FillVertical(g, p, r, new[] { W.Over(ModernTheme.FrameLight, 0.08f), W.Over(ModernTheme.PanelB, 0.7f) });
                RectangleF art = new RectangleF(r.X, r.Y, r.Width, r.Height - 54 * k);
                using (var glow = new GraphicsPath())
                {
                    glow.AddEllipse(art.X + art.Width * 0.2f, art.Y + art.Height * 0.05f, art.Width * 0.6f, art.Height * 0.95f);
                    using var br = new PathGradientBrush(glow) { CenterColor = W.Over(ModernTheme.Accent, 0.2f), SurroundColors = new[] { W.Over(ModernTheme.PanelB, 0.6f) } };
                    Region old = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    g.FillPath(br, glow);
                    g.Clip = old;
                    old.Dispose();
                }
                ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.6f));
            }
            float metaY = r.Bottom - 54 * k;
            if (it.Count > 1) Outlined(g, "x" + W.N(it.Count), ModernSkin.Font(11, true), Color.White, new RectangleF(r.X, metaY - 18 * k, r.Width - 8 * k, 16 * k), StringAlignment.Far);
            string stock = StockText(it);
            if (stock.Length > 0)
            {
                Font sf = ModernSkin.Font(9, true);
                float sw = ModernSkin.Measure(stock, sf) + 12 * k;
                RectangleF pill = new RectangleF(6 * k, metaY - 18 * k, sw, 14 * k);
                using (GraphicsPath pp = ModernSkin.RoundRect(pill, 6 * k))
                using (var br = new SolidBrush(ModernTheme.Mix(ModernTheme.Bad, Color.Black, 0.75f))) g.FillPath(br, pp);
                ModernSkin.Text(g, stock, sf, Color.White, pill, StringAlignment.Center);
            }
            // meta: name, category · class, prices
            float x = 9 * k, w = r.Width - 18 * k;
            ModernSkin.Text(g, ShopName(it), ModernSkin.Font(12, true), Eco.GradeColour(it.Info), new RectangleF(x, metaY + 4 * k, w, 16 * k));
            string sub = JoinParts(" · ", Pretty(it.Category), it.Class != null && it.Class != AllClasses ? it.Class : string.Empty);
            ModernSkin.Text(g, sub.Length > 0 ? sub : " ", ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, metaY + 19 * k, w, 13 * k));
            float py = metaY + 33 * k, px = x;
            if (it.CanBuyCredit) px = Price(g, px, py, 16 * k, it, PayCredit, 1, 12, false, Color.White);
            if (it.CanBuyGold) Price(g, px, py, 16 * k, it, PayGold, 1, 11, true, ModernTheme.Gold);
            if (!it.CanBuyCredit && !it.CanBuyGold) ModernSkin.Text(g, W.T(ClientTextKeys.ModernEcoShopNotForSale), ModernSkin.Font(11, true), ModernTheme.Dim, new RectangleF(x, py, w, 16 * k));
            // badges: DEAL ribbon, top seller star, NEW
            if (it.Deal)
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(20 * k, 22 * k);
                g.RotateTransform(-38f);
                RectangleF rib = new RectangleF(-46 * k, -7 * k, 92 * k, 14 * k);
                using (var br = new LinearGradientBrush(rib, ModernTheme.Mix(ModernTheme.Bad, Color.Black, 0.8f), ModernTheme.Bad, LinearGradientMode.Horizontal)) g.FillRectangle(br, rib);
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernEcoShopDealRibbon), ModernSkin.Font(9, true), Color.White, rib, StringAlignment.Center);
                g.Restore(st);
            }
            if (it.TopItem) Star(g, 14 * k, (it.Deal ? 38 : 14) * k, 7 * k, ModernTheme.Warn);
            if (IsNew(it))
            {
                string nt = W.T(ClientTextKeys.ModernEcoShopNewBadge);
                Font nf = ModernSkin.Font(8, true);
                float nw = ModernSkin.Measure(nt, nf) + 12 * k;
                RectangleF pill = new RectangleF((r.Width - nw) / 2f, 5 * k, nw, 13 * k);
                using (GraphicsPath pp = ModernSkin.RoundRect(pill, 6 * k))
                using (var br = new SolidBrush(ModernTheme.Ok)) g.FillPath(br, pp);
                ModernSkin.Text(g, nt, nf, Color.Black, pill, StringAlignment.Center);
            }
        }

        private static void Star(Graphics g, float cx, float cy, float r, Color c)
        {
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 5;
                float rr = i % 2 == 0 ? r : r * 0.45f;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * rr, cy + (float)Math.Sin(a) * rr);
            }
            using (var br = new SolidBrush(c)) g.FillPolygon(br, pts);
            using var pen = new Pen(Color.Black, 1f);
            g.DrawPolygon(pen, pts);
        }

        private static void Outlined(Graphics g, string text, Font f, Color c, RectangleF r, StringAlignment h)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        ModernSkin.Text(g, text, f, Color.FromArgb(200, 0, 0, 0), new RectangleF(r.X + dx, r.Y + dy, r.Width, r.Height), h, StringAlignment.Center, false);
            ModernSkin.Text(g, text, f, c, r, h, StringAlignment.Center, false);
        }

        private List<GameShopItem> Featured()
        {
            var out1 = new List<GameShopItem>();
            foreach (GameShopItem it in GameScene.GameShopInfoList) if ((it.TopItem || it.Deal) && MatchesBase(it)) out1.Add(it);
            out1.Sort((a, b) =>
            {
                int c = b.Deal.CompareTo(a.Deal);
                if (c == 0) c = b.TopItem.CompareTo(a.TopItem);
                return c != 0 ? c : string.Compare(ShopName(a), ShopName(b), StringComparison.CurrentCultureIgnoreCase);
            });
            if (out1.Count > FeaturedMax) out1.RemoveRange(FeaturedMax, out1.Count - FeaturedMax);
            return out1;
        }

        private void RenderHero()
        {
            List<GameShopItem> items = Featured();
            _hero.Visible = items.Count > 0;
            if (items.Count == 0) { _heroKey = string.Empty; return; }
            int at = ((_slide % items.Count) + items.Count) % items.Count;
            GameShopItem it = items[at];
            byte pay = LeadPay(it);
            string key = at + "|" + string.Join(",", items.ConvertAll(x => x.GIndex)) + "|" + it.Stock + "|" + PriceOf(it, pay) + "|" + WasOf(it, pay);
            if (key == _heroKey) return;
            _heroKey = key;
            for (int i = _hero.Controls.Count - 1; i >= 0; i--) _hero.Controls[i].Dispose();
            int w = (int)(_hero.Size.Width / ModernRoot.K), h = _heroH;
            long was = WasOf(it, pay), price = PriceOf(it, pay);
            string kick = it.Deal ? (was > 0 ? W.T(ClientTextKeys.ModernEcoShopDealSave, (int)Math.Round((1 - price / (double)was) * 100)) : W.T(ClientTextKeys.ModernEcoShopDeals)) : W.T(ClientTextKeys.ModernEcoShopTopSeller);
            string sub = JoinParts(" · ", Pretty(it.Category), it.Count > 1 ? W.T(ClientTextKeys.ModernEcoShopPackOf, W.N(it.Count)) : string.Empty, StockText(it));
            int g0 = it.GIndex;
            int bigW = _narrow ? 80 : 100;
            new PaintBox((g, b) => PaintHero(g, b, it, kick, sub, pay, bigW)) { Parent = _hero, Size = _hero.Size, NotControl = true };
            var big = new EcoItemView(bigW, () => Eco.Show(it.Info, it.Count)) { Parent = _hero, Well = false, Scale = _narrow ? 2.2f : 2.6f, ShowCount = false, Location = new Point(S(44), S((h - bigW) / 2)) };
            big.Sync();
            big.Clicked += _ => OpenView(g0);
            var view = new ModernButton(W.T(it.Deal ? ClientTextKeys.ModernEcoShopGrabDeal : ClientTextKeys.ModernEcoShopView), ButtonKind.Main) { Parent = _hero };
            view.Size = new Size(Math.Max(S(90), view.Size.Width), S(30));
            view.AutoWidth(90);
            view.Location = new Point(S(w - 44) - view.Size.Width, S(h / 2 + 6));
            view.Click += (o, e) => OpenView(g0);
            var left = new ModernButton("‹") { Parent = _hero, Size = new Size(S(26), S(26)), Location = new Point(S(8), S(h / 2 - 13)), Tooltip = W.T(ClientTextKeys.ModernEcoPrevPage) };
            left.Click += (o, e) => { _slide--; _heroKey = string.Empty; _nextSlide = CMain.Time + CarouselMs; _dirty = true; };
            var right = new ModernButton("›") { Parent = _hero, Size = new Size(S(26), S(26)), Location = new Point(S(w - 34), S(h / 2 - 13)), Tooltip = W.T(ClientTextKeys.ModernWinNextPage) };
            right.Click += (o, e) => { _slide++; _heroKey = string.Empty; _nextSlide = CMain.Time + CarouselMs; _dirty = true; };
            int dotsW = items.Count * 13 + 14;
            for (int i = 0; i < items.Count; i++)
            {
                int idx = i;
                bool on = i == at;
                var dot = new PaintBox((g, b) => PaintDot(g, b, on)) { Parent = _hero, Size = new Size(S(on ? 20 : 9), S(9)), Hint = ShopName(items[i]) };
                dot.Location = new Point(S(w / 2 - dotsW / 2 + i * 13 + (i > at ? 11 : 0)), S(h - 14));
                dot.Click += (o, e) => { _slide = idx; _heroKey = string.Empty; _nextSlide = CMain.Time + CarouselMs; _dirty = true; };
            }
        }

        private static void PaintDot(Graphics g, PaintBox b, bool on)
        {
            RectangleF r = new RectangleF(0.5f, 1, b.Size.Width - 1f, b.Size.Height - 2f);
            using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
            using var br = new SolidBrush(on ? ModernTheme.Accent : W.Over(Color.White, 0.3f));
            g.FillPath(br, p);
        }

        private void PaintHero(Graphics g, PaintBox b, GameShopItem it, string kick, string sub, byte pay, int bigW)
        {
            float k = ModernRoot.K, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, rad))
            {
                using (var br = new LinearGradientBrush(r, ModernSkin.Opaque(ModernTheme.PanelA), ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Accent), ModernSkin.Opaque(ModernTheme.PanelB), 0.18f), 20f))
                    g.FillPath(br, p);
                using (var glow = new GraphicsPath())
                {
                    glow.AddEllipse(r.X - r.Width * 0.32f, r.Y - r.Height * 0.7f, r.Width, r.Height * 2.4f);
                    using var br = new PathGradientBrush(glow) { CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.35f), SurroundColors = new[] { Color.FromArgb(0, ModernTheme.Accent) } };
                    br.CenterPoint = new PointF(r.X + r.Width * 0.18f, r.Y + r.Height / 2f);
                    Region old = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    g.FillPath(br, glow);
                    g.Clip = old;
                    old.Dispose();
                }
                ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.5f));
            }
            float tx = (44 + bigW + 24) * k, tw = b.Size.Width - tx - 200 * k;
            float cy = b.Size.Height / 2f;
            ModernSkin.SpacedText(g, kick.ToUpperInvariant(), ModernSkin.Font(10, true), ModernTheme.Accent, new RectangleF(tx, cy - 40 * k, Math.Min(tw, ModernSkin.Measure(kick, ModernSkin.Font(10, true)) * 1.6f), 14 * k), 2 * k);
            ModernSkin.Text(g, ShopName(it), ModernSkin.Font(_narrow ? 19 : 24, true), Eco.GradeColour(it.Info), new RectangleF(tx, cy - 26 * k, tw, 32 * k));
            ModernSkin.Text(g, sub, ModernSkin.Font(12), ModernTheme.Mix(ModernTheme.Text, ModernTheme.Dim, 0.8f), new RectangleF(tx, cy + 8 * k, tw, 16 * k));
            // price, right side above the button
            float pw = 190 * k;
            Price(g, b.Size.Width - 44 * k - pw, cy - 30 * k, 28 * k, it, pay, 1, 20, false, Color.White);
        }

        // ------------------------------------------------------------------ product view

        private void OpenView(int gIndex)
        {
            GameShopItem it = Find(gIndex);
            if (it == null) return;
            _viewG = gIndex;
            _viewBuiltFor = null;
            _qty = 1;
            _giftOpen = false;
            _note = string.Empty;
            _noteBad = false;
            _pay = LeadPay(it);
            _showHist = false;
            _dirty = true;
        }

        private void CloseView(bool render)
        {
            if (_viewG < 0) return;
            _viewG = -1;
            _viewBuiltFor = null;
            ModernTooltip.HideAll();
            if (_modal != null && !_modal.IsDisposed)
            {
                _modal.Visible = false;
                for (int i = _modal.Controls.Count - 1; i >= 0; i--) _modal.Controls[i].Dispose();
            }
            if (render) _dirty = true;
        }

        private void RenderView()
        {
            if (_viewG < 0) { _modal.Visible = false; return; }
            GameShopItem it = Find(_viewG);
            if (it == null)
            {
                CloseView(false);
                Toast(W.T(ClientTextKeys.ModernEcoShopGone), true);
                return;
            }
            if (_viewBuiltFor != it) BuildView(it);
            SyncView(it);
        }

        private void BuildView(GameShopItem it)
        {
            _viewBuiltFor = it;
            _friendsKey = string.Empty;
            for (int i = _modal.Controls.Count - 1; i >= 0; i--) _modal.Controls[i].Dispose();
            _modal.Visible = true;
            int mw = (int)(_modal.Size.Width / ModernRoot.K), mh = H;
            // the scrim (rgba(0,0,0,.7)) is opaque here: translucent pixels in the window's target would show the world through
            new PaintBox((g, b) => { using var br = new SolidBrush(ModernTheme.Mix(W.Page, Color.Black, 0.35f)); g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height); })
            { Parent = _modal, Size = _modal.Size };
            _modal.Controls[0].Click += (o, e) => CloseView(true); // a press on the scrim closes the view
            int cw = Math.Min(640, mw - 24), ch = 300, cx = (mw - cw) / 2, cy = (mh - ch) / 2;
            var card = new PaintBox((g, b) => PaintViewCard(g, b)) { Parent = _modal, Location = new Point(S(cx), S(cy)), Size = new Size(S(cw), S(ch)) };
            var close = new ModernButton("×") { Parent = card, Size = new Size(S(22), S(22)), Location = new Point(S(cw - 30), S(6)), Tooltip = W.T(ClientTextKeys.ModernEcoShopBack), FontSize = 14 };
            close.Click += (o, e) => CloseView(true);
            new PaintBox(PaintStage) { Parent = card, Location = new Point(S(20), S(20)), Size = new Size(S(200), S(ch - 40)), NotControl = true };
            var big = new EcoItemView(140, () => Eco.Show(it.Info, it.Count)) { Parent = card, Well = false, Scale = 3f, ShowCount = false, Location = new Point(S(50), S(20 + (ch - 40 - 140) / 2)) };
            big.Sync();
            int ix = 240, iw = cw - ix - 20, y = 20;
            W.Label(card, ShopName(it), ix, y, iw - 24, 26, 20, true, Eco.GradeColour(it.Info));
            y += 30;
            var tags = new List<(string, int)>(); // 0 normal, 1 hot, 2 new
            if (!string.IsNullOrEmpty(it.Category)) tags.Add((Pretty(it.Category), 0));
            if (!string.IsNullOrEmpty(it.Class) && it.Class != AllClasses) tags.Add((it.Class, 0));
            if (it.Deal) tags.Add((W.T(ClientTextKeys.ModernEcoShopDeals), 1));
            if (it.TopItem) tags.Add((W.T(ClientTextKeys.ModernEcoShopTopSeller), 0));
            if (IsNew(it)) tags.Add((W.T(ClientTextKeys.ModernEcoShopNew), 2));
            int tx = ix;
            foreach (var (text, kind) in tags)
            {
                int tw = (int)(ModernSkin.Measure(text.ToUpperInvariant(), ModernSkin.Font(9, true)) / ModernRoot.K) + 16;
                string t = text;
                new PaintBox((g, b) => PaintTag(g, b, t, kind)) { Parent = card, Location = new Point(S(tx), S(y)), Size = new Size(S(tw), S(16)), NotControl = true };
                tx += tw + 5;
            }
            _pvStock = W.Label(card, string.Empty, tx, y, ix + iw - tx, 16, 9, true, ModernTheme.BadSoft);
            y += 24;
            W.Label(card, (it.Count > 1 ? W.T(ClientTextKeys.ModernEcoShopPackDesc, W.N(it.Count)) + " " : string.Empty) + W.T(ClientTextKeys.ModernEcoShopByMail) + " " + W.T(ClientTextKeys.ModernEcoShopHoverHint),
                ix, y, iw, 30, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            y += 34;
            int pw = (iw - 6) / 2;
            _pvCredit = new PaintBox((g, b) => PaintPay(g, b, it, PayCredit)) { Parent = card, Location = new Point(S(ix), S(y)), Size = new Size(S(pw), S(36)), Hint = W.T(it.CanBuyCredit ? ClientTextKeys.ModernEcoShopPayCredits : ClientTextKeys.ModernEcoShopNoCredits) };
            _pvCredit.Click += (o, e) => { if (CanPay(it, PayCredit) && _pending == null) { _pay = PayCredit; _dirty = true; } };
            _pvGold = new PaintBox((g, b) => PaintPay(g, b, it, PayGold)) { Parent = card, Location = new Point(S(ix + pw + 6), S(y)), Size = new Size(S(pw), S(36)), Hint = W.T(it.CanBuyGold ? ClientTextKeys.ModernEcoShopPayGold : ClientTextKeys.ModernEcoShopNoGold) };
            _pvGold.Click += (o, e) => { if (CanPay(it, PayGold) && _pending == null) { _pay = PayGold; _dirty = true; } };
            y += 44;
            W.Label(card, W.T(ClientTextKeys.ModernEcoShopQuantity), ix, y, 70, 26, 12, false, ModernTheme.Dim);
            var minus = new ModernButton("−") { Parent = card, Size = new Size(S(26), S(26)), Location = new Point(S(ix + 70), S(y)), Tooltip = W.T(ClientTextKeys.ModernEcoShopFewer) };
            minus.Click += (o, e) => SetQty(_qty - (CMain.Shift ? 10 : 1));
            _pvQty = W.Label(card, string.Empty, ix + 98, y, 34, 26, 13, true, Color.White, StringAlignment.Center);
            var plus = new ModernButton("+") { Parent = card, Size = new Size(S(26), S(26)), Location = new Point(S(ix + 134), S(y)), Tooltip = W.T(ClientTextKeys.ModernEcoShopMore) };
            plus.Click += (o, e) => SetQty(_qty + (CMain.Shift ? 10 : 1));
            _pvTotal = new PaintBox(PaintTotal) { Parent = card, Location = new Point(S(ix + 166), S(y - 2)), Size = new Size(S(iw - 166), S(30)), NotControl = true };
            y += 34;
            _pvGiftRow = new ModernPanel { Parent = card, Location = new Point(S(ix), S(y)), Size = new Size(S(iw), S(30)), Visible = false };
            _pvNote = W.Label(card, string.Empty, ix, ch - 64, iw, 16, 11, false, ModernTheme.Dim);
            _pvBuy = new ModernButton(W.T(ClientTextKeys.ModernEcoShopBuy), ButtonKind.Main) { Parent = card, Location = new Point(S(ix), S(ch - 44)) };
            _pvBuy.Size = new Size(S(iw - 200), S(32));
            _pvBuy.Click += (o, e) => Purchase();
            _pvGift = new ModernButton(W.T(ClientTextKeys.ModernEcoShopGift)) { Parent = card, Location = new Point(S(ix + iw - 194), S(ch - 44)), Tooltip = W.T(ClientTextKeys.ModernEcoShopGiftHint) };
            _pvGift.Size = new Size(S(96), S(32));
            _pvGift.Click += (o, e) =>
            {
                _giftOpen = !_giftOpen;
                if (_giftOpen) Network.Enqueue(new C.RefreshFriends()); // online states for the picker
                _friendsKey = string.Empty;
                _dirty = true;
            };
            _pvWish = new ModernButton(string.Empty) { Parent = card, Location = new Point(S(ix + iw - 92), S(ch - 44)) };
            _pvWish.Size = new Size(S(92), S(32));
            _pvWish.Click += (o, e) => ToggleWish(it);
            _viewKey = string.Empty;
        }

        private static void PaintViewCard(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath black = ModernSkin.RoundRect(RectangleF.Inflate(r, 0, 0), rad + 1)) ModernSkin.Stroke(g, black, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(r, -1, -1), rad);
            ModernSkin.FillVertical(g, p, r, ModernSkin.PageColours(), ModernSkin.PagePositions);
            ModernSkin.Stroke(g, p, ModernTheme.Accent);
        }

        private static void PaintStage(Graphics g, PaintBox b)
        {
            float rad = ModernTheme.Radius * ModernRoot.K;
            RectangleF r = new RectangleF(0, 0, b.Size.Width, b.Size.Height);
            using GraphicsPath p = ModernSkin.RoundRect(r, rad);
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.8f))) g.FillPath(br, p);
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(r.X - r.Width * 0.15f, r.Y + r.Height * 0.1f, r.Width * 1.3f, r.Height);
                using var br = new PathGradientBrush(glow) { CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.3f), SurroundColors = new[] { Color.FromArgb(0, ModernTheme.Accent) } };
                br.CenterPoint = new PointF(r.Width / 2f, r.Height * 0.6f);
                Region old = g.Clip;
                g.SetClip(p, CombineMode.Intersect);
                g.FillPath(br, glow);
                g.Clip = old;
                old.Dispose();
            }
        }

        private static void PaintTag(Graphics g, PaintBox b, string text, int kind)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, 7 * k);
            Color fg = ModernTheme.AccentDim;
            if (kind == 1)
            {
                using (var br = new SolidBrush(ModernTheme.Mix(ModernTheme.Bad, Color.Black, 0.75f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ModernTheme.Bad);
                fg = Color.White;
            }
            else if (kind == 2)
            {
                using (var br = new SolidBrush(ModernTheme.Ok)) g.FillPath(br, p);
                fg = Color.Black;
            }
            else ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.4f));
            ModernSkin.Text(g, text.ToUpperInvariant(), ModernSkin.Font(9, true), fg, r, StringAlignment.Center);
        }

        private void PaintPay(Graphics g, PaintBox b, GameShopItem it, byte pay)
        {
            float k = ModernRoot.K;
            bool can = CanPay(it, pay), on = can && _pay == pay;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var br = new SolidBrush(on ? ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Accent), ModernSkin.Opaque(ModernTheme.PanelB), 0.22f) : ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.6f)))
                g.FillPath(br, p);
            ModernSkin.Stroke(g, p, on ? ModernTheme.Accent : ModernTheme.Frame);
            float w = 160 * k;
            Price(g, (b.Size.Width - w) / 2f + 10 * k, 0, b.Size.Height, it, pay, 1, 14, false, !can ? ModernSkin.Dim(ModernTheme.Dim, 0.5f) : on ? Color.White : ModernTheme.Dim);
        }

        private void PaintTotal(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(17, true);
            float tw = ModernSkin.Measure(_pvTotalText, f) + 2;
            float x = b.Size.Width - tw;
            ModernSkin.Text(g, _pvTotalText, f, _pvTotalBad ? ModernTheme.BadSoft : Color.White, new RectangleF(x, 0, tw, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
            if (_pvWasText.Length > 0)
            {
                Font sf = ModernSkin.Font(12, true);
                float ww = ModernSkin.Measure(_pvWasText, sf) + 2;
                x -= ww + 6 * k;
                ModernSkin.Text(g, _pvWasText, sf, ModernTheme.Dim, new RectangleF(x, 0, ww, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                using (var pen = new Pen(ModernTheme.Dim, 1f)) g.DrawLine(pen, x, b.Size.Height / 2f, x + ww - 2, b.Size.Height / 2f);
            }
            string lbl = W.T(ClientTextKeys.ModernEcoShopTotal);
            float lw = ModernSkin.Measure(lbl, ModernSkin.Font(12)) + 2;
            ModernSkin.Text(g, lbl, ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(x - lw - 6 * k, 0, lw, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
        }

        /// <summary>The friends a gift can go to (not blocked), online first.</summary>
        private static List<ClientFriend> GiftFriends()
        {
            var list = GameScene.Models.Friends.Friends.FindAll(f => !f.Blocked && !string.IsNullOrEmpty(f.Name));
            list.Sort((a, b) => a.Online != b.Online ? (a.Online ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        private void SyncView(GameShopItem it)
        {
            if (!CanPay(it, _pay)) _pay = CanPay(it, PayCredit) ? PayCredit : PayGold;
            int max = MaxQty(it);
            _qty = Math.Clamp(_qty, 1, Math.Max(1, max));
            bool anyPay = it.CanBuyCredit || it.CanBuyGold;
            long price = PriceOf(it, _pay), was = WasOf(it, _pay), total = price * _qty, have = _pay == PayCredit ? GameScene.Credit : GameScene.Gold;

            List<ClientFriend> friends = GiftFriends();
            string fkey = string.Join(",", friends.ConvertAll(f => f.Name + ":" + (f.Online ? 1 : 0)));
            if (!friends.Exists(f => f.Name == _giftTo)) _giftTo = friends.Count > 0 ? friends[0].Name : string.Empty;
            if (friends.Count == 0) _giftOpen = false;
            string why = _pending != null ? W.T(ClientTextKeys.ModernEcoShopWaiting)
                : !anyPay ? W.T(ClientTextKeys.ModernEcoShopNotForSaleLong)
                : max == 0 ? W.T(ClientTextKeys.ModernEcoShopTooBig)
                : total > have ? W.T(ClientTextKeys.YouCantAffordSelectedItem)
                : _giftOpen && _giftTo.Length == 0 ? W.T(ClientTextKeys.ModernEcoShopNoFriends)
                : string.Empty;
            string note = _note.Length > 0 ? _note : max == 0 ? W.T(ClientTextKeys.ModernEcoShopTooBig) : max < MaxQuantity && _qty == max ? W.T(ClientTextKeys.ModernEcoShopAtMost, W.N(max)) : string.Empty;
            string key = it.GIndex + "|" + _pay + "|" + _qty + "|" + total + "|" + have + "|" + why + "|" + note + "|" + _noteBad + "|" + _giftOpen + "|" + _giftTo + "|" + fkey + "|" + (it.Info != null && Wish.Contains(it.Info.Index)) + "|" + it.Stock;
            if (key == _viewKey) return;
            _viewKey = key;

            _pvCredit.Invalidate();
            _pvGold.Invalidate();
            _pvQty.Text = _qty.ToString();
            _pvStock.Text = StockText(it).ToUpperInvariant();
            _pvTotalText = anyPay ? W.N(total) + " " + W.T(_pay == PayCredit ? ClientTextKeys.ModernEcoShopCredits : ClientTextKeys.ModernEcoGoldWord) : "-";
            _pvWasText = was > 0 ? W.N(was * _qty) : string.Empty;
            _pvTotalBad = anyPay && total > have;
            _pvTotal.Invalidate();

            // gift picker (rebuilt only when the friends or the pick changed)
            _pvGiftRow.Visible = _giftOpen;
            if (fkey + "|" + _giftTo != _friendsKey)
            {
                _friendsKey = fkey + "|" + _giftTo;
                for (int i = _pvGiftRow.Controls.Count - 1; i >= 0; i--) _pvGiftRow.Controls[i].Dispose();
                int gw = (int)(_pvGiftRow.Size.Width / ModernRoot.K);
                W.Label(_pvGiftRow, W.T(ClientTextKeys.ModernEcoShopGiftTo), 0, 0, 52, 22, 12, false, ModernTheme.Dim);
                var choices = new List<Choice<string>>();
                foreach (ClientFriend f in friends) choices.Add(new Choice<string>(f.Name, f.Online ? W.T(ClientTextKeys.ModernEcoShopFriendOnline, f.Name) : f.Name));
                var chips = new Chips<string>(choices, new[] { _giftTo }, gw - 56, false, false) { Parent = _pvGiftRow, Location = new Point(S(56), S(1)) };
                chips.Changed += sel => { foreach (string s in sel) { _giftTo = s; _dirty = true; } };
            }

            _pvBuy.Text = _pending != null ? W.T(ClientTextKeys.ModernEcoShopWaiting)
                : total > have && anyPay ? W.T(ClientTextKeys.ModernEcoShopNotEnough)
                : _giftOpen ? W.T(ClientTextKeys.ModernEcoShopSendGiftTo, _giftTo) : W.T(ClientTextKeys.ModernEcoShopBuy);
            _pvBuy.SetDisabled(why.Length > 0, why);
            _pvGift.Text = W.T(_giftOpen ? ClientTextKeys.ModernEcoShopBuyForMe : ClientTextKeys.ModernEcoShopGift);
            _pvGift.SetDisabled(_pending != null || (!_giftOpen && friends.Count == 0), _pending != null ? W.T(ClientTextKeys.ModernEcoShopWaiting) : W.T(ClientTextKeys.ModernEcoShopNoFriends));
            _pvWish.Text = W.T(it.Info != null && Wish.Contains(it.Info.Index) ? ClientTextKeys.ModernEcoShopWishlisted : ClientTextKeys.ModernEcoShopWishlist);
            _pvNote.Text = note;
            _pvNote.Colour = _noteBad || max == 0 ? ModernTheme.BadSoft : ModernTheme.Dim;
        }

        private void SetQty(int q)
        {
            if (_pending != null || _viewG < 0) return;
            _qty = q;
            _note = string.Empty;
            _noteBad = false;
            _dirty = true;
        }

        // ------------------------------------------------------------------ purchase (server authoritative)

        /// <summary>MirGameShopCell.BuyProduct: the desktop's confirm, then C.GameshopBuy (or C.GameshopGift to the picked friend).</summary>
        private void Purchase()
        {
            if (_pending != null || _viewG < 0) return;
            int gIndex = _viewG;
            GameShopItem it = Find(gIndex);
            Popups pop = ModernRoot.Instance?.Popups;
            if (it == null || pop == null) return;
            byte pay = _pay;
            int qty = _qty;
            string to = _giftOpen ? _giftTo : string.Empty;
            if (!CanPay(it, pay) || qty < 1 || qty > MaxQty(it)) return;
            long cost = PriceOf(it, pay) * qty;
            if (cost > (pay == PayCredit ? GameScene.Credit : GameScene.Gold)) { SetNote(W.T(ClientTextKeys.YouCantAffordSelectedItem), true); return; }
            if (_giftOpen && to.Length == 0) { SetNote(W.T(ClientTextKeys.ModernEcoShopNoFriends), true); return; }
            string name = ShopName(it);
            string text = W.T(pay == PayCredit ? ClientTextKeys.ConfirmBuyItemCredits : ClientTextKeys.ConfirmPurchaseItemGold, name, qty, cost, it.Count);
            text += "\n" + (to.Length > 0 ? W.T(ClientTextKeys.ModernEcoShopGiftDelivered, to) : W.T(ClientTextKeys.ModernEcoShopDeliveredByMail));
            pop.Ask(text, ok =>
            {
                if (!ok || _pending != null || IsDisposed) return;
                GameShopItem now = Find(gIndex); // the shop may have changed while the box was up
                if (now == null || !CanPay(now, pay) || qty > MaxQty(now)) { SetNote(W.T(ClientTextKeys.ModernEcoShopGone), true); return; }
                long cost2 = PriceOf(now, pay) * qty, before = pay == PayCredit ? GameScene.Credit : GameScene.Gold;
                if (cost2 > before) { SetNote(W.T(ClientTextKeys.YouCantAffordSelectedItem), true); return; }
                if (to.Length > 0) Network.Enqueue(new C.GameshopGift { GIndex = now.GIndex, Quantity = qty, PType = pay, Recipient = to });
                else Network.Enqueue(new C.GameshopBuy { GIndex = now.GIndex, Quantity = (byte)qty, PType = pay });
                _pending = new Pending { GIndex = gIndex, Name = name, Info = now.Info, Count = now.Count, Qty = qty, Cost = cost2, Pay = pay, To = to, Before = before, Until = CMain.Time + AnswerMs };
                _note = string.Empty;
                _noteBad = false;
                _dirty = true;
            }, false, PopupKind.Question, W.T(to.Length > 0 ? ClientTextKeys.ModernEcoShopSendGift : ClientTextKeys.ModernEcoShop));
        }

        /// <summary>The server charged (true) or nothing came within the wait (false: refused with a chat line, or a free item).</summary>
        private void Finish(bool charged)
        {
            Pending p = _pending;
            if (p == null) return;
            _pending = null;
            string paid = W.N(p.Cost) + " " + W.T(p.Pay == PayCredit ? ClientTextKeys.ModernEcoShopCredits : ClientTextKeys.ModernEcoGoldWord);
            if (charged || p.Cost == 0)
            {
                _history.Insert(0, new HistoryRow { Info = p.Info, Name = p.Name, Qty = p.Qty, Count = p.Count, Paid = paid, To = p.To, At = CMain.Now });
                if (_history.Count > HistoryMax) _history.RemoveRange(HistoryMax, _history.Count - HistoryMax);
                _histBtn.Text = W.T(ClientTextKeys.ModernEcoShopPurchasesN, _history.Count);
                string what = p.Qty + " x " + p.Name;
                string msg = p.Cost == 0 && !charged ? W.T(ClientTextKeys.ModernEcoShopOrdered, what)
                    : p.To.Length > 0 ? W.T(ClientTextKeys.ModernEcoShopSentGift, what, p.To) : W.T(ClientTextKeys.ModernEcoShopBought, what);
                Toast(msg, false);
                _qty = 1;
                _note = msg;
                _noteBad = false;
            }
            else
            {
                _note = W.T(ClientTextKeys.ModernEcoShopNoAnswer);
                _noteBad = true;
                Toast(_note, true);
            }
            _histKey = string.Empty;
            _dirty = true;
        }

        private void SetNote(string text, bool bad)
        {
            _note = text;
            _noteBad = bad;
            _dirty = true;
        }

        private void ToggleWish(GameShopItem it)
        {
            if (it.Info == null) return;
            if (!Wish.Remove(it.Info.Index)) Wish.Add(it.Info.Index);
            _dirty = true;
        }

        private void RenderHistory()
        {
            _hist.Visible = _showHist;
            if (!_showHist) return;
            string key = _history.Count + "|" + (_history.Count > 0 ? _history[0].At.Ticks : 0);
            if (key == _histKey) return;
            _histKey = key;
            for (int i = _hist.Controls.Count - 1; i >= 0; i--) _hist.Controls[i].Dispose();
            new PaintBox(PaintViewCard) { Parent = _hist, Size = _hist.Size, NotControl = true };
            W.Caption(_hist, W.T(ClientTextKeys.ModernEcoShopBoughtSession), 12, 10, 280);
            int y = 30;
            if (_history.Count == 0) W.Label(_hist, W.T(ClientTextKeys.ModernEcoShopNothingBought), 12, y, 286, 18, 11, false, ModernTheme.Dim);
            foreach (HistoryRow x in _history)
            {
                if (y > 400 - 44) break;
                HistoryRow row = x;
                new EcoItemView(34, () => Eco.Show(row.Info, row.Count)) { Parent = _hist, Location = new Point(S(10), S(y)), Well = false, ShowCount = false }.Sync();
                W.Label(_hist, row.Qty + " x " + row.Name + (row.Count > 1 ? " (" + W.N(row.Count) + ")" : string.Empty), 52, y, 160, 17, 11, true, Color.White);
                W.Label(_hist, row.To.Length > 0 ? W.T(ClientTextKeys.ModernEcoShopGiftToName, row.To) : W.T(ClientTextKeys.ModernEcoShopForYou), 52, y + 17, 160, 15, 10, false, ModernTheme.Dim);
                W.Label(_hist, row.Paid + " · " + row.At.ToString("t"), 190, y + 8, 110, 16, 10, false, ModernTheme.Dim, StringAlignment.Far);
                y += 42;
            }
        }

        private void PaintToast(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(13, true);
            float w = Math.Min(b.Size.Width - 2, ModernSkin.Measure(_toastText, f) + 40 * k);
            RectangleF r = new RectangleF((b.Size.Width - w) / 2f, 0.5f, w, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
            Color edge = _toastBad ? ModernTheme.Bad : ModernTheme.Accent;
            using (var br = new LinearGradientBrush(r, ModernTheme.Mix(ModernSkin.Opaque(edge), Color.Black, 0.55f), ModernTheme.Mix(ModernSkin.Opaque(edge), Color.Black, 0.3f), LinearGradientMode.Horizontal))
                g.FillPath(br, p);
            ModernSkin.Stroke(g, p, edge);
            ModernSkin.Text(g, _toastText, f, Color.White, r, StringAlignment.Center);
        }

        private void Toast(string text, bool bad)
        {
            if (_toast == null || _toast.IsDisposed) return;
            _toastText = text ?? string.Empty;
            _toastBad = bad;
            _toast.Invalidate();
            _toast.Visible = true;
            _toastUntil = CMain.Time + 3500;
        }
    }
}
