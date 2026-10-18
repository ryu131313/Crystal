// Ported from WebClient/src/ui/modern/windows/merchant.ts (MerchantWindow + MerchantSession, the session half is in NpcState): the
// new UI's NPC merchant window. Desktop counterparts: NPCGoodsDialog / NPCSubGoodsDialog (Buy, BuySub, pearl goods) and
// NPCDropDialog's Sell / Repair / SpecialRepair panels (Client/MirScenes/Dialogs/NPCDialogs.cs).
//  - opens only from the server's answers while the NPC dialog is open (S.NPCGoods Buy / BuySub, S.NPCPearlGoods, S.NPCSell,
//    S.NPCRepair, S.NPCSRepair) and closes with the NPC session (a new page that opens no merchant panel closes it after a short
//    grace). A tab is live only while its panel is the one the server opened; another service is reached through the current
//    page's own link (C.CallNPC, NpcState.Call);
//  - Buy: one row per ItemIndex for a normal shop with the used offers folded under it (NPCSubGoodsDialog), BuySub sorted by
//    price, pearl goods priced in pearls. A click runs NPCGoodsDialog.BuyItem's checks (gold, bag space = User.GetMaxGain, the
//    amount box for a stack) and adds to a cart; Buy re-checks gold and bag room for the whole cart and sends one C.BuyItem per
//    stack through the pacer. Used and buy-back entries are bought whole;
//  - Buy back: the S.NPCGoods list the server sends for [@BUYBACK] (NpcState.LastPageKey), labelled as such;
//  - Sell: the bag; picks by UniqueID, DontSell dimmed, Select junk marks outgrown Common gear and candles, Sell sends ONE
//    C.SellItems (the cells lock until each S.SellItem). A bag item picked in the bag window and dropped on the "add" cell is
//    picked too (NPCDropDialog's cell); Alt + click in the bag sells at once (ItemNpcDropQuickSell, the classic route);
//  - Repair: the damaged bag items, per item (C.RepairItems with one id) or Repair all (bag) as ONE C.RepairItems (Special = the
//    panel the server opened); prices RepairPrice() x NPCRate (x3 special), the normal repair's maximum loss (MaxDura - CurrentDura) / 30.
// HIDDEN (no system for them): repairing worn gear, a local wallet / sales list, local outcomes; pearls are the server's check.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirSounds;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class MerchantWindow : ModernWindow
    {
        public const string WindowKey = "merchant";
        // MaxCartSends: at most 10 C.BuyItem per checkout (the pacer's burst of 5 + under 2 s at 3 a second), so a cart never keeps
        // buying long after the player has moved on; the queued rest is also dropped once the goods panel closes (Checkout).
        private const int WidthHud = 860, HeadH = 60, TabsH = 34, BodyH = 420, SideW = 260, MaxCartSends = 10, QuickSellMs = 6000;
        private const int MainW = WidthHud - SideW;

        private static readonly MerchantTab[] AllTabs = { MerchantTab.Buy, MerchantTab.Sell, MerchantTab.Repair, MerchantTab.BuyBack };
        private static readonly Dictionary<MerchantTab, string[]> TabKeys = new Dictionary<MerchantTab, string[]>
        {
            [MerchantTab.Buy] = new[] { "@BUY", "@BUYSELL", "@BUYNEW", "@BUYSELLNEW", "@BUYUSED", "@PEARLBUY" },
            [MerchantTab.Sell] = new[] { "@SELL", "@BUYSELL", "@BUYSELLNEW" },
            [MerchantTab.Repair] = new[] { "@REPAIR", "@SREPAIR" },
            [MerchantTab.BuyBack] = new[] { "@BUYBACK" },
        };
        private static readonly (string Name, ClientTextKeys Label)[] Cats =
        {
            ("All", ClientTextKeys.ModernWinCatAll), ("Potions", ClientTextKeys.ModernWinCatPotions), ("Scrolls", ClientTextKeys.ModernWinCatScrolls),
            ("Weapons", ClientTextKeys.ModernWinCatWeapons), ("Armour", ClientTextKeys.ModernWinCatArmour), ("Jewellery", ClientTextKeys.ModernWinCatJewellery),
            ("Other", ClientTextKeys.ModernWinCatOther),
        };

        private sealed class CartLine
        {
            public UserItem Item;
            public int Count;
        }

        private readonly NpcState _state;
        private MerchantTab _tab = MerchantTab.Buy;
        private int _seenFocus = -1, _cartGen = -1;
        private string _note = string.Empty, _query = string.Empty, _cat = "All";
        private bool _dirty = true;
        private uint _gold = uint.MaxValue;
        private readonly List<CartLine> _cart = new List<CartLine>();
        private readonly HashSet<int> _unfolded = new HashSet<int>();
        private readonly HashSet<ulong> _picks = new HashSet<ulong>();
        // Alt + click quick sells waiting for S.SellItem: the server sends none when the NPC is out of range (PlayerObject.SellItem),
        // so the bag cell unlocks after QuickSellMs
        private readonly List<(ulong Id, long Until)> _quickSells = new List<(ulong, long)>();
        private long _nextQuickPoll;
        private string _goodsKey = string.Empty, _chipsKey = string.Empty, _cartKey = string.Empty, _pickKey = string.Empty, _repairKey = string.Empty, _offKey = string.Empty, _tabsKey = string.Empty;
        private int _sellBuilt = -1;

        private ModernLabel _npcName, _npcSub, _wallet;
        private readonly Dictionary<MerchantTab, PaintBox> _tabBtns = new Dictionary<MerchantTab, PaintBox>();
        private readonly Dictionary<MerchantTab, int> _badges = new Dictionary<MerchantTab, int>();
        private ModernPanel _offView, _buyView, _sellView, _repView;
        private ModernPanel _offRow;
        private ModernLabel _offText;
        // buy
        private ModernLabel _listTitle, _listHint, _cartWeight, _cartTotal, _buyNote;
        private ModernPanel _chipsBox;
        private ScrollView _list, _cartList;
        private ModernButton _buyBtn, _clearCartBtn;
        private readonly List<(PaintBox Row, UserItem Item, uint Price)> _rows = new List<(PaintBox, UserItem, uint)>();
        // sell
        private ScrollView _bagView, _pickList;
        private ModernLabel _pickCount, _sellTotal, _sellNote;
        private ModernButton _sellBtn;
        private NpcItemCell _addCell;
        private readonly List<NpcItemCell> _sellCells = new List<NpcItemCell>();
        // repair
        private ModernLabel _repTitle, _repExplain, _repSum, _repTotal, _repNote;
        private ScrollView _repList;
        private ModernPanel _repSwitch;
        private ModernButton _repAllBtn;
        private string _repSwitchKey = string.Empty;

        private readonly GridWatch _bagWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;

        public MerchantWindow(NpcState state) : base(WindowKey, W.T(ClientTextKeys.ModernNpcMerchant), 60, 40)
        {
            _state = state;
            ContentWidth = WidthHud;
            ContentHeight = HeadH + TabsH + BodyH;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.None) _dirty = true; };
        }

        internal MerchantTab TabShown => _tab;
        internal int CartLines => _cart.Count;
        internal int Picks => _picks.Count;
        internal string Note => _note;
        internal IReadOnlyList<(PaintBox Row, UserItem Item, uint Price)> GoodsRows => _rows;
        internal ModernButton BuyButton => _buyBtn;
        internal ModernButton SellButton => _sellBtn;
        internal ModernButton RepairAllButton => _repAllBtn;
        internal IReadOnlyList<NpcItemCell> SellCells => _sellCells;
        internal PaintBox TabButton(MerchantTab t) => _tabBtns.TryGetValue(t, out PaintBox b) ? b : null;
        internal ScrollView RepairList => _repList;

        private static UserObject User => GameScene.User;
        private static float Rate => GameScene.NPCRate;

        protected override void Build()
        {
            int top = TitleHeight;
            // ---- head: the NPC, the rate, the gold
            new PaintBox((g, b) => PaintNpcBadge(g, b)) { Parent = this, Location = new Point(S(14), S(top + 10)), Size = new Size(S(40), S(40)), NotControl = true }
                .AfterPaint = b => W.DrawIcon(ModernIcons.Shop, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(10), S(20), S(20)), ModernTheme.Accent);
            _npcName = W.Label(this, string.Empty, 66, top + 12, 400, 20, 15, true, Color.White);
            _npcSub = W.Label(this, string.Empty, 66, top + 33, 400, 14, 11, false, ModernTheme.Dim);
            var walletBox = new PaintBox((g, b) => PaintWallet(g, b)) { Parent = this, Location = new Point(S(WidthHud - 194), S(top + 16)), Size = new Size(S(180), S(28)), NotControl = true };
            walletBox.AfterPaint = b => W.DrawIcon(ModernIcons.Coin, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(7), S(14), S(14)), ModernTheme.Gold);
            _wallet = W.Label(walletBox, string.Empty, 30, 0, 140, 28, 13, true, ModernTheme.Gold, StringAlignment.Far);
            W.Divider(this, 0, top + HeadH, WidthHud);

            // ---- tabs
            _tabBtns.Clear();
            foreach (MerchantTab t in AllTabs)
            {
                MerchantTab tab = t;
                var b = new PaintBox((g, pb) => PaintTab(g, pb, tab)) { Parent = this, Location = new Point(S(14), S(top + HeadH + 8)), Size = new Size(S(100), S(26)), Sound = SoundList.ButtonA };
                b.Click += (o, e) => SelectTab(tab);
                _tabBtns[t] = b;
                _badges[t] = 0;
            }
            _tabsKey = string.Empty;
            W.Divider(this, 0, top + HeadH + TabsH, WidthHud);

            int by = top + HeadH + TabsH + 1;
            _offView = Panel(by);
            _offText = W.Label(_offView, string.Empty, 0, 170, WidthHud, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _offRow = new ModernPanel { Parent = _offView, Location = new Point(0, S(200)), Size = new Size(S(WidthHud), S(32)) };
            BuildBuy(Panel(by));
            BuildSell(Panel(by));
            BuildRepair(Panel(by));
            _goodsKey = _chipsKey = _cartKey = _pickKey = _repairKey = _offKey = _repSwitchKey = string.Empty;
            _sellBuilt = -1;
            _dirty = true;
            Render();
        }

        private ModernPanel Panel(int y) => new ModernPanel { Parent = this, Location = new Point(0, S(y)), Size = new Size(S(WidthHud), S(BodyH - 1)), Visible = false };

        private void BuildBuy(ModernPanel p)
        {
            _buyView = p;
            var search = W.At(new SearchBox(180, W.T(ClientTextKeys.ModernNpcSearchGoods)), p, 12, 10);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); _goodsKey = string.Empty; _dirty = true; };
            _chipsBox = new ModernPanel { Parent = p, Location = new Point(S(200), S(10)), Size = new Size(S(MainW - 212), S(26)) };
            _listTitle = W.Label(p, string.Empty, 12, 40, 260, 14, 11, false, ModernTheme.Dim);
            _listHint = W.Label(p, string.Empty, 272, 40, MainW - 284, 14, 11, false, ModernTheme.Dim, StringAlignment.Far);
            _list = new ScrollView(MainW - 24, BodyH - 70) { Parent = p, Location = new Point(S(12), S(58)), Background = W.Page };
            _list.Framed = false;
            W.VDivider(p, MainW, 0, BodyH);
            int sx = MainW + 12, sw = SideW - 24;
            W.Caption(p, W.T(ClientTextKeys.ModernNpcCart), sx, 10, 80);
            _cartWeight = W.Label(p, string.Empty, sx + 60, 10, sw - 60, 14, 10.5f, false, ModernTheme.Dim, StringAlignment.Far);
            _cartList = new ScrollView(sw, 220) { Parent = p, Location = new Point(S(sx), S(30)), Background = W.Page };
            _cartList.Framed = false;
            W.Divider(p, sx, 258, sw);
            W.Label(p, W.T(ClientTextKeys.ModernNpcTotal), sx, 264, 100, 20, 12, false, ModernTheme.Dim);
            _cartTotal = W.Label(p, string.Empty, sx + 100, 262, sw - 100, 22, 15, true, ModernTheme.Gold, StringAlignment.Far);
            _buyBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernNpcBuy), ButtonKind.Main, true), p, sx, 292);
            _buyBtn.Size = new Size(S(sw), _buyBtn.Size.Height);
            _buyBtn.Click += (o, e) => Checkout();
            _clearCartBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernNpcClear)) { Tooltip = W.T(ClientTextKeys.ModernNpcClearCartHint) }, p, sx, 330);
            _clearCartBtn.Size = new Size(S(sw), _clearCartBtn.Size.Height);
            _clearCartBtn.Click += (o, e) => { _cart.Clear(); _dirty = true; };
            _buyNote = W.Label(p, string.Empty, sx, 360, sw, 48, 11, false, ModernTheme.Accent, StringAlignment.Near, true);
        }

        private void BuildSell(ModernPanel p)
        {
            _sellView = p;
            W.Caption(p, W.T(ClientTextKeys.ModernNpcSellCaption), 12, 14, 300);
            var junk = new ModernButton(W.T(ClientTextKeys.ModernNpcSelectJunk)) { Parent = p, Tooltip = W.T(ClientTextKeys.ModernNpcSelectJunkHint) };
            junk.AutoWidth(80);
            var clear = new ModernButton(W.T(ClientTextKeys.ModernNpcClear)) { Parent = p };
            clear.AutoWidth(50);
            clear.Location = new Point(S(MainW - 12) - clear.Size.Width, S(10));
            junk.Location = new Point(clear.Location.X - S(6) - junk.Size.Width, S(10));
            junk.Click += (o, e) => SelectJunk();
            clear.Click += (o, e) => { _picks.Clear(); _dirty = true; };
            _bagView = new ScrollView(MainW - 24, BodyH - 80) { Parent = p, Location = new Point(S(12), S(40)), Background = W.Page };
            _bagView.Framed = false;
            W.Label(p, W.T(ClientTextKeys.ModernNpcSellHint), 12, BodyH - 34, MainW - 24, 28, 10.5f, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.VDivider(p, MainW, 0, BodyH);
            int sx = MainW + 12, sw = SideW - 24;
            W.Caption(p, W.T(ClientTextKeys.ModernNpcSelling), sx, 10, 100);
            _pickCount = W.Label(p, string.Empty, sx + 100, 10, sw - 100, 14, 11, true, Color.Empty, StringAlignment.Far);
            _pickList = new ScrollView(sw, 196) { Parent = p, Location = new Point(S(sx), S(30)), Background = W.Page };
            _pickList.Framed = false;
            _addCell = new NpcItemCell(34) { Parent = p, Location = new Point(S(sx), S(232)), EmptyIcon = ModernIcons.Plus, Hint = W.T(ClientTextKeys.ModernNpcAddCellHint) };
            _addCell.Click += (o, e) => AddCellClick();
            W.Label(p, W.T(ClientTextKeys.ModernNpcAddCellHint), sx + 42, 232, sw - 42, 34, 10.5f, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.Divider(p, sx, 274, sw);
            W.Label(p, W.T(ClientTextKeys.ModernNpcYouGet), sx, 280, 100, 20, 12, false, ModernTheme.Dim);
            _sellTotal = W.Label(p, string.Empty, sx + 100, 278, sw - 100, 22, 15, true, ModernTheme.Gold, StringAlignment.Far);
            _sellBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernNpcSell), ButtonKind.Main, true), p, sx, 308);
            _sellBtn.Size = new Size(S(sw), _sellBtn.Size.Height);
            _sellBtn.Click += (o, e) => SellPicked();
            _sellNote = W.Label(p, string.Empty, sx, 346, sw, 48, 11, false, ModernTheme.Accent, StringAlignment.Near, true);
            _sellCells.Clear();
            _sellBuilt = -1;
        }

        private void BuildRepair(ModernPanel p)
        {
            _repView = p;
            _repTitle = W.Label(p, string.Empty, 12, 10, MainW - 220, 16, 11.5f, true, ModernTheme.Text);
            W.Label(p, W.T(ClientTextKeys.ModernNpcRepairBagOnly), MainW - 212, 10, 200, 16, 10.5f, false, ModernTheme.Dim, StringAlignment.Far);
            _repList = new ScrollView(MainW - 24, BodyH - 44) { Parent = p, Location = new Point(S(12), S(34)), Background = W.Page };
            _repList.Framed = false;
            W.VDivider(p, MainW, 0, BodyH);
            int sx = MainW + 12, sw = SideW - 24;
            _repExplain = W.Label(p, string.Empty, sx, 10, sw, 64, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            _repSwitch = new ModernPanel { Parent = p, Location = new Point(S(sx), S(80)), Size = new Size(S(sw), S(24)) };
            _repSum = W.Label(p, string.Empty, sx, 220, sw, 36, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.Divider(p, sx, 260, sw);
            W.Label(p, W.T(ClientTextKeys.ModernNpcAllDamaged), sx, 266, 120, 20, 12, false, ModernTheme.Dim);
            _repTotal = W.Label(p, string.Empty, sx + 120, 264, sw - 120, 22, 15, true, ModernTheme.Gold, StringAlignment.Far);
            _repAllBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernNpcRepairAll), ButtonKind.Main, true), p, sx, 294);
            _repAllBtn.Size = new Size(S(sw), _repAllBtn.Size.Height);
            _repAllBtn.Click += (o, e) => RepairAll();
            _repNote = W.Label(p, string.Empty, sx, 332, sw, 48, 11, false, ModernTheme.Accent, StringAlignment.Near, true);
        }

        private static void PaintNpcBadge(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(1f * k, 1f * k, b.Size.Width - 2f * k, b.Size.Height - 2f * k);
            using var path = new GraphicsPath();
            path.AddEllipse(r);
            using (var br = new PathGradientBrush(path) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } })
                g.FillEllipse(br, r);
            using var pen = new Pen(ModernTheme.Accent, 2f * k);
            g.DrawEllipse(pen, r);
        }

        private static void PaintWallet(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, 14 * k);
            using (var br = new SolidBrush(W.Over(ModernTheme.Gold, 0.08f))) g.FillPath(br, p);
            ModernSkin.Stroke(g, p, W.Over(ModernTheme.Gold, 0.4f));
        }

        private void PaintTab(Graphics g, PaintBox b, MerchantTab t)
        {
            float k = ModernRoot.K;
            bool on = t == _tab, live = _state.MerchantLive(t);
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height);
            if (on)
            {
                using GraphicsPath p = ModernSkin.RoundTop(r, ModernTheme.Radius * k);
                using (var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.16f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.5f));
                using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, r.X + 2, r.Y, r.Width - 4, 2 * k);
            }
            int badge = _badges.TryGetValue(t, out int n) ? n : 0;
            string name = TabName(t);
            Font f = live ? ModernSkin.Font(12, true) : new Font(ModernSkin.Font(12, true), FontStyle.Bold | FontStyle.Italic);
            try
            {
                float tw = ModernSkin.Measure(name, f);
                float bw = badge > 0 ? ModernSkin.Measure(badge.ToString(), ModernSkin.Font(10, true)) + 10 * k : 0;
                float x = (b.Size.Width - tw - (badge > 0 ? bw + 5 * k : 0)) / 2f;
                ModernSkin.Text(g, name, f, on ? Color.White : b.Hovered ? ModernTheme.Text : ModernTheme.Dim, new RectangleF(x, 0, tw + 4, b.Size.Height));
                if (badge > 0)
                {
                    RectangleF br = new RectangleF(x + tw + 5 * k, (b.Size.Height - 14 * k) / 2f, bw, 14 * k);
                    using GraphicsPath bp = ModernSkin.RoundRect(br, 7 * k);
                    using (var bb = new SolidBrush(ModernTheme.Accent)) g.FillPath(bb, bp);
                    ModernSkin.Text(g, badge.ToString(), ModernSkin.Font(10, true), Color.White, br, StringAlignment.Center);
                }
            }
            finally
            {
                if (!live) f.Dispose();
            }
        }

        private static string TabName(MerchantTab t) => t switch
        {
            MerchantTab.Buy => W.T(ClientTextKeys.ModernNpcBuy),
            MerchantTab.Sell => W.T(ClientTextKeys.ModernNpcSell),
            MerchantTab.Repair => W.T(ClientTextKeys.ModernNpcSvcRepair),
            _ => W.T(ClientTextKeys.ModernNpcSvcBuyBack),
        };

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _state.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            _bagWatch.Reset();
            Say(string.Empty);
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            _state.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            _cart.Clear();
            _picks.Clear();
            _unfolded.Clear();
            _note = string.Empty;
            // Process stops with the window: quick sells still waiting unlock now (a late S.SellItem still applies)
            for (int i = 0; i < _quickSells.Count; i++) UnlockQuickSell(_quickSells[i].Id);
            _quickSells.Clear();
            // the merchant panels hide with the window (NPCGoodsDialog / NPCDropDialog.Hide): no Alt + click sell / repair while
            // nothing is shown, and buys still queued on the pacer drop out (NpcState.Gen bumps)
            _state.HideMerchant();
        }

        public override void Process()
        {
            base.Process();
            _state.Process();
            if (User == null) return;
            if (_bagWatch.Poll(MirGridType.Inventory, User.Inventory))
            {
                for (int i = 0; i < _sellCells.Count; i++) _sellCells[i].Refresh();
                _pickKey = _repairKey = string.Empty;
                _dirty = true;
            }
            if (_gold != GameScene.Gold) { _gold = GameScene.Gold; _dirty = true; }
            if (_quickSells.Count > 0 && CMain.Time >= _nextQuickPoll) { _nextQuickPoll = CMain.Time + 250; PollQuickSells(); }
            if (_dirty) Render();
        }

        /// <summary>Quick sells: answered (cell unlocked or item gone) leave the list; past QuickSellMs the cell unlocks.</summary>
        private void PollQuickSells()
        {
            for (int i = _quickSells.Count - 1; i >= 0; i--)
            {
                var q = _quickSells[i];
                UserItem item = NpcFmt.FindBag(q.Id, out int slot);
                if (item == null || !ItemMoves.IsLocked(MirGridType.Inventory, slot)) { _quickSells.RemoveAt(i); continue; }
                if (CMain.Time < q.Until) continue;
                ItemMoves.SetLocked(MirGridType.Inventory, slot, false);
                _quickSells.RemoveAt(i);
                _dirty = true;
            }
        }

        private static void UnlockQuickSell(ulong id)
        {
            if (NpcFmt.FindBag(id, out int slot) != null) ItemMoves.SetLocked(MirGridType.Inventory, slot, false);
        }

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            if (_buyNote == null) return;
            _buyNote.Text = _note;
            _sellNote.Text = _note;
            _repNote.Text = _note;
        }

        private void SelectTab(MerchantTab t)
        {
            if (_tab == t) return;
            _tab = t;
            Say(string.Empty);
            _dirty = true;
        }

        /// <summary>The current page's links to a tab's panels (merchant.ts links).</summary>
        private List<(string Key, string Text)> Links(MerchantTab t)
        {
            var all = _state.PageLinks();
            var output = new List<(string, string)>();
            foreach (string k in TabKeys[t]) if (all.TryGetValue(k, out var l)) output.Add(l);
            return output;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_buyView == null || _buyView.IsDisposed || User == null) return;
            if (_state.FocusSeq != _seenFocus) { _seenFocus = _state.FocusSeq; if (_state.Focus.HasValue) _tab = _state.Focus.Value; }
            if (_state.Gen != _cartGen) { _cartGen = _state.Gen; _cart.Clear(); _unfolded.Clear(); _picks.Clear(); _goodsKey = string.Empty; }

            var (name, place) = _state.Name;
            _npcName.Text = name;
            string sub = place;
            if (Math.Abs(Rate - 1f) > 0.001f) sub += (sub.Length > 0 ? " · " : string.Empty) + W.T(ClientTextKeys.ModernNpcPricesX, W.N1(Rate));
            _npcSub.Text = sub;
            _wallet.Text = W.N(GameScene.Gold);

            // tabs: shown when live or linked from the page
            var vis = new Dictionary<MerchantTab, List<(string Key, string Text)>>();
            string tk = _tab.ToString();
            foreach (MerchantTab t in AllTabs)
            {
                bool live = _state.MerchantLive(t);
                var links = live ? new List<(string, string)>() : Links(t);
                vis[t] = links;
                bool show = live || links.Count > 0;
                int badge = t == MerchantTab.Buy || t == MerchantTab.BuyBack ? (live ? _cart.Count : 0) : t == MerchantTab.Sell ? _picks.Count : 0;
                tk += "|" + t + show + live + badge;
                _badges[t] = badge;
            }
            if (!_tabBtns[_tab].Visible || !(_state.MerchantLive(_tab) || vis[_tab].Count > 0))
                foreach (MerchantTab t in AllTabs) if (_state.MerchantLive(t)) { _tab = t; break; }
            if (tk != _tabsKey)
            {
                _tabsKey = tk;
                int x = 14;
                foreach (MerchantTab t in AllTabs)
                {
                    PaintBox b = _tabBtns[t];
                    bool show = _state.MerchantLive(t) || vis[t].Count > 0;
                    b.Visible = show;
                    b.Hint = _state.MerchantLive(t) ? string.Empty : W.T(ClientTextKeys.ModernNpcTabOffHint);
                    if (!show) continue;
                    b.Location = new Point(S(x), b.Location.Y);
                    b.Invalidate();
                    x += 104;
                }
            }

            bool liveTab = _state.MerchantLive(_tab);
            _offView.Visible = !liveTab;
            _buyView.Visible = liveTab && (_tab == MerchantTab.Buy || _tab == MerchantTab.BuyBack);
            _sellView.Visible = liveTab && _tab == MerchantTab.Sell;
            _repView.Visible = liveTab && _tab == MerchantTab.Repair;
            if (!liveTab) RenderOff(vis[_tab]);
            else if (_buyView.Visible) RenderBuy();
            else if (_sellView.Visible) RenderSell();
            else RenderRepair();
        }

        private void RenderOff(List<(string Key, string Text)> links)
        {
            string npc = _state.Name.Name;
            string key = _tab + "|" + npc + "|" + string.Join(",", links);
            if (key == _offKey) return;
            _offKey = key;
            _offText.Text = links.Count > 0 ? W.T(ClientTextKeys.ModernNpcTabOnPage, TabName(_tab), npc) : W.T(ClientTextKeys.ModernNpcTabSpeakTo, npc, TabName(_tab));
            for (int i = _offRow.Controls.Count - 1; i >= 0; i--) _offRow.Controls[i].Dispose();
            int total = 0;
            var btns = new List<ModernButton>();
            foreach (var l in links)
            {
                string call = l.Key, text = l.Text;
                var b = new ModernButton(text.Length > 0 ? text : TabName(_tab), ButtonKind.Main) { Parent = _offRow };
                b.AutoWidth(80);
                b.Click += (o, e) => _state.Call(call, text);
                btns.Add(b);
                total += b.Size.Width + S(6);
            }
            int x = (_offRow.Size.Width - total) / 2;
            foreach (ModernButton b in btns) { b.Location = new Point(x, 0); x += b.Size.Width + S(6); }
        }

        // ------------------------------------------------------------------ buy

        private static string CatOf(ItemInfo info)
        {
            if (info == null) return "Other";
            switch (info.Type)
            {
                case ItemType.Potion: return "Potions";
                case ItemType.Scroll:
                case ItemType.Amulet: return "Scrolls";
                case ItemType.Weapon: return "Weapons";
                case ItemType.Armour:
                case ItemType.Helmet:
                case ItemType.Boots:
                case ItemType.Belt: return "Armour";
                case ItemType.Necklace:
                case ItemType.Bracelet:
                case ItemType.Ring: return "Jewellery";
            }
            return "Other";
        }

        /// <summary>NPCGoodsDialog.NewGoods: a normal shop shows one entry per ItemIndex; BuySub sorted by price.</summary>
        private List<UserItem> Display()
        {
            var list = new List<UserItem>(_state.Goods);
            if (_state.GoodsType == PanelType.BuySub) list.Sort((a, b) => a.Price().CompareTo(b.Price()));
            if (_state.GoodsType != PanelType.Buy || _state.UsePearls) return list;
            var seen = new HashSet<int>();
            var output = new List<UserItem>();
            foreach (UserItem it in list) if (seen.Add(it.ItemIndex)) output.Add(it);
            return output;
        }

        private void RenderBuy()
        {
            bool back = _state.GoodsKey == NpcState.BuyBackKey, pearls = _state.UsePearls;
            _listTitle.Text = W.T(back ? ClientTextKeys.ModernNpcBuyBackTitle : pearls ? ClientTextKeys.ModernNpcPearlShop : _state.GoodsType == PanelType.BuySub ? ClientTextKeys.ModernNpcUsedGoods : ClientTextKeys.ModernNpcGoods);
            _listHint.Text = W.T(back ? ClientTextKeys.ModernNpcBuyBackHint : ClientTextKeys.ModernNpcClickToCart);
            List<UserItem> display = Display();

            // chips
            int[] counts = new int[Cats.Length];
            counts[0] = display.Count;
            foreach (UserItem it in display)
            {
                string c = CatOf(it.Info);
                for (int j = 1; j < Cats.Length; j++) if (Cats[j].Name == c) counts[j]++;
            }
            bool catOk = _cat == "All";
            for (int j = 1; j < Cats.Length; j++) if (Cats[j].Name == _cat && counts[j] > 0) catOk = true;
            if (!catOk) _cat = "All";
            string ck = _cat + ":" + string.Join(",", counts);
            if (ck != _chipsKey)
            {
                _chipsKey = ck;
                for (int i = _chipsBox.Controls.Count - 1; i >= 0; i--) _chipsBox.Controls[i].Dispose();
                var choices = new List<Choice<string>>();
                int used = 0;
                for (int j = 0; j < Cats.Length; j++)
                    if (j == 0 || counts[j] > 0) { choices.Add(new Choice<string>(Cats[j].Name, W.T(Cats[j].Label) + " " + counts[j])); if (j > 0) used++; }
                if (used >= 2)
                {
                    var chips = new Chips<string>(choices, new[] { _cat }, MainW - 212, false, false) { Parent = _chipsBox, Location = Point.Empty };
                    chips.Changed += sel =>
                    {
                        foreach (string s in sel) { if (s != _cat) { _cat = s; _goodsKey = _chipsKey = string.Empty; _dirty = true; } return; }
                    };
                }
            }

            // rows
            string key = _state.Gen + "|" + display.Count + "|" + _query + "|" + _cat + "|" + string.Join(",", _unfolded) + "|" + Rate + "|" + GameScene.Gold + "|" + (User?.Level ?? 0);
            if (key != _goodsKey)
            {
                _goodsKey = key;
                float keep = _list.ScrollY;
                for (int i = _list.Content.Controls.Count - 1; i >= 0; i--) _list.Content.Controls[i].Dispose();
                _rows.Clear();
                int colW = ((int)Math.Round(_list.InnerWidth / ModernRoot.K) - 5) / 2, n = 0;
                void Place(MirControl c, bool full)
                {
                    c.Parent = _list.Content;
                    if (full && n % 2 == 1) n++;
                    int indent = c.Size.Width < S(colW) - 2 ? S(18) : 0;
                    c.Location = new Point(S((n % 2) * (colW + 5)) + indent, S((n / 2) * 49));
                    n++;
                }
                foreach (UserItem it in display)
                {
                    if (_cat != "All" && CatOf(it.Info) != _cat) continue;
                    if (_query.Length > 0 && !NpcFmt.ItemName(it).ToLowerInvariant().Contains(_query)) continue;
                    List<UserItem> offers = new List<UserItem>();
                    if (_state.GoodsType == PanelType.Buy && !pearls) { foreach (UserItem o in _state.Goods) if (o.ItemIndex == it.ItemIndex) offers.Add(o); }
                    else offers.Add(it);
                    if (offers.Count > 1)
                    {
                        bool open = _unfolded.Contains(it.ItemIndex);
                        Place(GoodsRow(it, colW, W.T(open ? ClientTextKeys.ModernNpcOffersFold : ClientTextKeys.ModernNpcOffersCompare, offers.Count), true, open, false), false);
                        if (open) foreach (UserItem o in offers) Place(GoodsRow(o, colW, string.Empty, false, false, true), false);
                    }
                    else Place(GoodsRow(it, colW, string.Empty, false, false, false), false);
                }
                if (n == 0)
                    W.Label(_list.Content, W.T(display.Count > 0 ? ClientTextKeys.ModernNpcNothingMatches : back ? ClientTextKeys.ModernNpcNoSales : ClientTextKeys.ModernNpcNothingForSale), 0, 24, colW * 2, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
                _list.ContentHeight = S(((n + 1) / 2) * 49);
                _list.ScrollTo((int)keep);
            }
            RenderCart(pearls);
        }

        /// <summary>One goods row: icon (hover = the item tooltip), name, requirement / stack, price; a group row folds the offers.</summary>
        private PaintBox GoodsRow(UserItem item, int w, string extra, bool group, bool open, bool variant)
        {
            ItemInfo info = item.Info;
            bool each = NpcFmt.IsStack(info) && item.IsShopItem;
            uint price = each ? NpcFmt.BuyPrice(item, 1, Rate) : NpcFmt.BuyPrice(item, item.Count, Rate);
            var bits = new List<string>();
            if (info != null && info.RequiredType == RequiredType.Level && info.RequiredAmount > 0) bits.Add(W.T(ClientTextKeys.ModernNpcLevelN, info.RequiredAmount));
            string used = W.T(_state.GoodsKey == NpcState.BuyBackKey ? ClientTextKeys.ModernNpcYouSoldThis : ClientTextKeys.ModernNpcUsed);
            if (!item.IsShopItem) bits.Add(item.MaxDura > 0 && info != null && info.Durability > 0 ? used + " · " + NpcFmt.Dura(item.CurrentDura) + "/" + NpcFmt.Dura(item.MaxDura) : used);
            if (each && info != null) bits.Add(W.T(ClientTextKeys.ModernNpcStacksTo, info.StackSize));
            if (!each && item.Count > 1) bits.Add("x" + W.N(item.Count));
            if (extra.Length > 0) bits.Add(extra);
            string req = bits.Count > 0 ? string.Join(" · ", bits) : item.IsShopItem && !variant ? W.T(ClientTextKeys.ModernNpcAnyLevel) : string.Empty;
            bool lowLevel = info != null && info.RequiredType == RequiredType.Level && (User?.Level ?? 0) < info.RequiredAmount;
            bool cant = !_state.UsePearls && price > GameScene.Gold;
            string priceText = _state.UsePearls ? W.T(price == 1 ? ClientTextKeys.ModernNpcPearl : ClientTextKeys.ModernNpcPearls, W.N(price)) : W.N(price);
            string nm = NpcFmt.ItemName(item);
            Color nameColour = info != null && info.Grade != ItemGrade.None ? ModernTheme.Grade(info.Grade) : ModernTheme.Text;
            int rw = variant ? w - 18 : w;
            var row = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using (var br = new SolidBrush(b.Hovered ? W.Over(ModernTheme.Accent, 0.09f) : W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                using (var pen = new Pen(b.Hovered ? ModernTheme.Accent : open ? W.Over(ModernTheme.Accent, 0.6f) : W.Over(ModernTheme.Frame, 0.45f), 1f))
                {
                    if (variant) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
                float tx = 48 * k, pw = 90 * k;
                ModernSkin.Text(g, nm, ModernSkin.Font(12, true), nameColour, new RectangleF(tx, 5 * k, r.Width - tx - pw, 18 * k));
                ModernSkin.Text(g, req, ModernSkin.Font(10), lowLevel ? ModernTheme.BadSoft : ModernTheme.Dim, new RectangleF(tx, 23 * k, r.Width - tx - pw, 16 * k));
                ModernSkin.Text(g, priceText, ModernSkin.Font(12, true), cant ? ModernTheme.BadSoft : ModernTheme.Gold, new RectangleF(r.Width - pw, 5 * k, pw - 8 * k, 18 * k), StringAlignment.Far);
                if (each) ModernSkin.Text(g, W.T(ClientTextKeys.ModernNpcEach), ModernSkin.Font(9, true), ModernTheme.Dim, new RectangleF(r.Width - pw, 23 * k, pw - 8 * k, 14 * k), StringAlignment.Far);
            })
            { Size = new Size(S(rw), S(44)), Sound = SoundList.ButtonA };
            var cell = new NpcItemCell(36, () => item) { Parent = row, Location = new Point(S(4), S(4)) };
            void Clicked()
            {
                if (group)
                {
                    if (!_unfolded.Remove(item.ItemIndex)) _unfolded.Add(item.ItemIndex);
                    _goodsKey = string.Empty;
                    _dirty = true;
                    return;
                }
                AddToCart(item);
            }
            row.Click += (o, e) => Clicked();
            cell.Click += (o, e) => Clicked();
            _rows.Add((row, item, price));
            return row;
        }

        /// <summary>Shop stacks are priced per C.BuyItem (StackSize at most each); everything else is count x the single price.</summary>
        private static uint LinePrice(CartLine l)
        {
            ItemInfo info = l.Item.Info;
            if (!l.Item.IsShopItem) return NpcFmt.BuyPrice(l.Item, l.Item.Count, Rate);
            if (!NpcFmt.IsStack(info)) return NpcFmt.BuyPrice(l.Item, 1, Rate) * (uint)l.Count;
            uint total = 0;
            for (int left = l.Count; left > 0; left -= info.StackSize) total += NpcFmt.BuyPrice(l.Item, (ushort)Math.Min(info.StackSize, left), Rate);
            return total;
        }

        private static int LineSends(CartLine l) => !l.Item.IsShopItem ? 1 : NpcFmt.IsStack(l.Item.Info) ? (l.Count + l.Item.Info.StackSize - 1) / l.Item.Info.StackSize : l.Count;

        /// <summary>The cart's total, weight, sends, and the room left for an item (GetMaxGain for every line together).</summary>
        private (uint Total, int Weight, int Sends, Dictionary<int, int> Partial, int Slots) Plan(CartLine skip = null)
        {
            int slots = 0;
            var partial = new Dictionary<int, int>();
            foreach (UserItem it in User.Inventory)
            {
                if (it == null) { slots++; continue; }
                if (NpcFmt.IsStack(it.Info) && it.Count < it.Info.StackSize)
                    partial[it.ItemIndex] = (partial.TryGetValue(it.ItemIndex, out int v) ? v : 0) + it.Info.StackSize - it.Count;
            }
            uint total = 0;
            int weight = 0, sends = 0;
            foreach (CartLine l in _cart)
            {
                if (l == skip) continue;
                ItemInfo info = l.Item.Info;
                total += LinePrice(l);
                weight += (info?.Weight ?? 0) * (NpcFmt.IsStack(info) ? 1 : l.Count);
                sends += LineSends(l);
                if (NpcFmt.IsStack(info))
                {
                    int s = info.StackSize, have = partial.TryGetValue(l.Item.ItemIndex, out int v) ? v : 0;
                    int need = Math.Max(0, l.Count - have), stacks = (need + s - 1) / s;
                    slots -= stacks;
                    partial[l.Item.ItemIndex] = have - Math.Min(have, l.Count) + (stacks * s - need);
                }
                else slots -= l.Item.IsShopItem ? l.Count : 1;
            }
            return (total, weight, sends, partial, slots);
        }

        private static int RoomFor((uint, int, int, Dictionary<int, int> Partial, int Slots) p, UserItem item)
        {
            if (!NpcFmt.IsStack(item.Info)) return Math.Max(0, p.Slots);
            return Math.Max(0, (p.Partial.TryGetValue(item.ItemIndex, out int v) ? v : 0) + Math.Max(0, p.Slots) * item.Info.StackSize);
        }

        private CartLine Line(UserItem item)
        {
            foreach (CartLine l in _cart) if (l.Item.UniqueID == item.UniqueID) return l;
            return null;
        }

        /// <summary>The most of an item the cart can hold: bag room (the other lines counted) and gold (pearls for a pearl shop) left.</summary>
        private int MaxFor(UserItem item)
        {
            CartLine line = Line(item);
            var p = Plan(line);
            ItemInfo info = item.Info;
            int max = Math.Min(RoomFor(p, item), NpcFmt.IsStack(info) ? info.StackSize * MaxCartSends : MaxCartSends);
            long left = Wallet - (long)p.Total;
            var probe = new CartLine { Item = item, Count = max };
            while (max > 0 && LinePrice(probe) > left) probe.Count = --max;
            return Math.Max(0, max);
        }

        /// <summary>What the cart is paid with: the pearls for a pearl shop, else the gold.</summary>
        private long Wallet => _state.UsePearls ? Math.Max(0, User?.PearlCount ?? 0) : GameScene.Gold;

        /// <summary>A goods click: NPCGoodsDialog.BuyItem's checks, then the cart (the amount box for a stack).</summary>
        private void AddToCart(UserItem item)
        {
            ItemInfo info = item?.Info;
            if (info == null || !_state.GoodsOpen) return;
            int gen = _state.Gen;
            CartLine line = Line(item);
            // NPCGoodsDialog.BuyItem's gold / pearl / space refusals
            if (!_state.UsePearls)
            {
                uint unit = NpcFmt.IsStack(info) ? Math.Max(1u, NpcFmt.BuyPrice(item, info.StackSize, Rate) / info.StackSize) : NpcFmt.BuyPrice(item, 1, Rate);
                if (unit > GameScene.Gold) { Say(W.T(ClientTextKeys.LowGold)); return; }
            }
            else if (User.PearlCount > 0 && NpcFmt.BuyPrice(item, 1, Rate) > User.PearlCount) { Say(W.T(ClientTextKeys.NotEnoughPearls)); return; }
            if (!item.IsShopItem)
            {
                if (line != null) { Say(W.T(ClientTextKeys.ModernNpcAlreadyInCart)); return; }
                if (RoomFor(Plan(), item) < (NpcFmt.IsStack(info) ? item.Count : 1)) { Say(W.T(ClientTextKeys.YouNoBagSpace)); return; }
                _cart.Add(new CartLine { Item = item, Count = item.Count });
                Say(string.Empty);
                _dirty = true;
                return;
            }
            int max = MaxFor(item);
            if (max <= 0) { Say(RoomFor(Plan(line), item) <= 0 ? W.T(ClientTextKeys.YouNoBagSpace) : W.T(ClientTextKeys.LowGold)); return; }
            if (!NpcFmt.IsStack(info)) { SetCount(item, Math.Min(max, (line?.Count ?? 0) + 1)); return; }
            ModernRoot.Instance?.Popups.Amount(W.T(ClientTextKeys.PurchaseAmount), info.Image, (uint)max, n =>
            {
                if (_state.Gen != gen || !IsOpen) return;
                SetCount(item, (int)n);
            }, (uint)Math.Min(max, line != null ? line.Count : info.StackSize));
        }

        private void SetCount(UserItem item, int n)
        {
            CartLine l = Line(item);
            if (n <= 0) { if (l != null) _cart.Remove(l); }
            else if (l != null) l.Count = n;
            else _cart.Add(new CartLine { Item = item, Count = n });
            Say(string.Empty);
            _dirty = true;
        }

        private void RenderCart(bool pearls)
        {
            var p = Plan();
            var sb = new System.Text.StringBuilder();
            foreach (CartLine l in _cart) sb.Append(l.Item.UniqueID).Append('x').Append(l.Count).Append(',');
            string key = sb + "|" + Rate;
            if (key != _cartKey)
            {
                _cartKey = key;
                for (int i = _cartList.Content.Controls.Count - 1; i >= 0; i--) _cartList.Content.Controls[i].Dispose();
                int w = (int)Math.Round(_cartList.InnerWidth / ModernRoot.K), y = 0;
                foreach (CartLine line in _cart)
                {
                    CartLine l = line;
                    uint price = LinePrice(l);
                    string nm = NpcFmt.ItemName(l.Item), pt = pearls ? W.T(ClientTextKeys.ModernNpcPearls, W.N(price)) : W.N(price);
                    var row = new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        using (GraphicsPath rp = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k))
                        using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, rp);
                        ModernSkin.Text(g, nm, ModernSkin.Font(11, true), ModernTheme.Text, new RectangleF(38 * k, 2 * k, b.Size.Width - 130 * k, 16 * k));
                        ModernSkin.Text(g, pt, ModernSkin.Font(11), ModernTheme.Gold, new RectangleF(38 * k, 18 * k, b.Size.Width - 130 * k, 16 * k));
                    })
                    { Parent = _cartList.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(36)), NotControl = true };
                    var cell = new NpcItemCell(30, () => l.Item) { Parent = row, Location = new Point(S(3), S(3)), CountOverride = (ushort)Math.Min(ushort.MaxValue, l.Count) };
                    cell.Refresh();
                    int x = w - 92;
                    if (!l.Item.IsShopItem) W.Label(row, W.T(ClientTextKeys.ModernNpcWhole), x, 0, 64, 36, 10.5f, false, ModernTheme.Dim, StringAlignment.Center);
                    else
                    {
                        int step = NpcFmt.IsStack(l.Item.Info) ? l.Item.Info.StackSize : 1;
                        var minus = new ModernButton("−") { Parent = row, Location = new Point(S(x), S(7)), Size = new Size(S(20), S(22)), Tooltip = W.T(ClientTextKeys.ModernNpcLessMore) };
                        minus.Click += (o, e) => SetCount(l.Item, l.Count - step);
                        W.Label(row, W.N(l.Count), x + 20, 0, 26, 36, 11, true, Color.White, StringAlignment.Center);
                        var plus = new ModernButton("+") { Parent = row, Location = new Point(S(x + 46), S(7)), Size = new Size(S(20), S(22)), Tooltip = W.T(ClientTextKeys.ModernNpcLessMore) };
                        plus.Click += (o, e) =>
                        {
                            int max = MaxFor(l.Item);
                            if (max <= l.Count) Say(W.T(ClientTextKeys.ModernNpcCantMore));
                            else SetCount(l.Item, Math.Min(max, l.Count + step));
                        };
                    }
                    var x2 = new ModernButton("×") { Parent = row, Location = new Point(S(w - 24), S(7)), Size = new Size(S(20), S(22)), Tooltip = W.T(ClientTextKeys.ModernNpcRemoveFromCart) };
                    x2.Click += (o, e) => SetCount(l.Item, 0);
                    y += 40;
                }
                if (_cart.Count == 0) W.Label(_cartList.Content, W.T(ClientTextKeys.ModernNpcCartEmpty), 0, 24, w, 20, 11.5f, false, ModernTheme.Dim, StringAlignment.Center);
                _cartList.ContentHeight = S(y);
            }
            int left = User.Stats[Stat.BagWeight] - User.CurrentBagWeight;
            _cartWeight.Text = _cart.Count > 0 ? W.T(ClientTextKeys.ModernNpcCartWeight, W.N(p.Weight), p.Sends) : string.Empty;
            _cartWeight.Colour = p.Weight > left ? ModernTheme.BadSoft : ModernTheme.Dim;
            _cartWeight.Hint = W.T(ClientTextKeys.ModernNpcBagWeightLeft, W.N(Math.Max(0, left)));
            bool shortGold = p.Total > Wallet;
            string shortText = W.T(pearls ? ClientTextKeys.NotEnoughPearls : ClientTextKeys.LowGold);
            _cartTotal.Text = pearls ? W.T(ClientTextKeys.ModernNpcPearls, W.N(p.Total)) : W.N(p.Total);
            _cartTotal.Colour = shortGold ? ModernTheme.BadSoft : ModernTheme.Gold;
            _buyBtn.Text = _cart.Count == 0 ? W.T(ClientTextKeys.ModernNpcBuy) : shortGold ? shortText : W.T(pearls ? ClientTextKeys.ModernNpcBuyForPearls : ClientTextKeys.ModernNpcBuyFor, W.N(p.Total));
            _buyBtn.SetDisabled(_cart.Count == 0 || shortGold || p.Sends > MaxCartSends, p.Sends > MaxCartSends ? W.T(ClientTextKeys.ModernNpcMaxSends, MaxCartSends) : shortGold ? shortText : string.Empty);
            _clearCartBtn.Visible = _cart.Count > 0;
        }

        /// <summary>Buy: re-checks the whole cart, then one C.BuyItem per stack through the pacer.</summary>
        private void Checkout()
        {
            if (!_state.GoodsOpen || _cart.Count == 0) return;
            var p = Plan();
            if (p.Total > Wallet) { Say(W.T(_state.UsePearls ? ClientTextKeys.NotEnoughPearls : ClientTextKeys.LowGold)); return; }
            if (p.Sends > MaxCartSends) { Say(W.T(ClientTextKeys.ModernNpcMaxSends, MaxCartSends)); return; }
            if (p.Slots < 0) { Say(W.T(ClientTextKeys.YouNoBagSpace)); return; }
            ModernUi ui = NpcFmt.Ui;
            if (ui == null) return;
            int sent = 0, kinds = _cart.Count, gen = _state.Gen;
            // queued buys go only while this goods panel is still the one the server opened (a new page, Exit, walking away or
            // closing the merchant bumps Gen / clears GoodsOpen)
            Func<bool> stillValid = () => _state.Open && _state.GoodsOpen && _state.Gen == gen;
            foreach (CartLine l in _cart)
            {
                UserItem item = l.Item;
                var counts = new List<int>();
                if (!item.IsShopItem) counts.Add(item.Count);
                else if (NpcFmt.IsStack(item.Info)) for (int left = l.Count; left > 0; left -= item.Info.StackSize) counts.Add(Math.Min(item.Info.StackSize, left));
                else for (int i = 0; i < l.Count; i++) counts.Add(1);
                foreach (int n in counts)
                {
                    ui.Pacer.Send(new C.BuyItem { ItemIndex = item.UniqueID, Count = (ushort)n, Type = PanelType.Buy }, stillValid);
                    sent++;
                }
            }
            _cart.Clear();
            Say(W.T(ClientTextKeys.ModernNpcBuying, kinds, sent));
            _dirty = true;
        }

        // ------------------------------------------------------------------ sell

        private static string SellRefusal(UserItem item, int slot)
        {
            if (item?.Info != null && item.Info.Bind.HasFlag(BindMode.DontSell)) return W.T(ClientTextKeys.CannotSellItem);
            if (slot >= 0 && ItemMoves.IsLocked(MirGridType.Inventory, slot)) return W.T(ClientTextKeys.ModernWinWaitingServer);
            return string.Empty;
        }

        private static int AddedCount(UserItem item)
        {
            int n = 0;
            if (item?.AddedStats == null) return 0;
            foreach (var kv in item.AddedStats.Values) n += Math.Abs(kv.Value);
            return n;
        }

        /// <summary>Junk: Common / ungraded gear 15+ levels under yours with no added stats, and candles. Never belt items.</summary>
        private static bool IsJunk(UserItem item, int index)
        {
            if (User == null || index < User.BeltIdx || item?.Info == null || SellRefusal(item, index).Length > 0) return false;
            ItemInfo info = item.Info;
            // English-only rule (it matches the item name, like the web's merchant.ts junk rule): candles in other languages'
            // item databases are simply not pre-selected
            if (info.Type == ItemType.Torch && info.Name.IndexOf("candle", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            bool gear = info.Type == ItemType.Weapon || info.Type == ItemType.Armour || info.Type == ItemType.Helmet || info.Type == ItemType.Necklace ||
                info.Type == ItemType.Bracelet || info.Type == ItemType.Ring || info.Type == ItemType.Belt || info.Type == ItemType.Boots;
            return gear && info.Grade <= ItemGrade.Common && AddedCount(item) == 0 && info.RequiredType == RequiredType.Level && info.RequiredAmount + 15 <= User.Level;
        }

        private void TogglePick(int slot, bool add = false)
        {
            if (!_state.SellOpen) return;
            UserItem item = User.Inventory[slot];
            if (item == null) return;
            string why = SellRefusal(item, slot);
            if (why.Length > 0) { Say(why); return; }
            if (_picks.Contains(item.UniqueID) && !add) _picks.Remove(item.UniqueID);
            else _picks.Add(item.UniqueID);
            Say(string.Empty);
            _dirty = true;
        }

        private void SelectJunk()
        {
            UserItem[] inv = User.Inventory;
            int n = 0;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != null && IsJunk(inv[i], i) && _picks.Add(inv[i].UniqueID)) n++;
            Say(n > 0 ? W.T(ClientTextKeys.ModernNpcPickedN, n) : W.T(ClientTextKeys.ModernNpcNoJunk));
            _dirty = true;
        }

        /// <summary>The "add" cell: a bag item picked in the bag window is picked for sale (NPCDropDialog's cell).</summary>
        private void AddCellClick()
        {
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick == null) return;
            ItemMoves.Selected = null;
            if (pick.GridType == MirGridType.Inventory) TogglePick(pick.ItemSlot, true);
        }

        private void RenderSell()
        {
            UserItem[] bag = User.Inventory;
            int belt = User.BeltIdx, n = Math.Max(0, bag.Length - belt);
            if (_sellBuilt != n)
            {
                for (int i = 0; i < _sellCells.Count; i++) _sellCells[i].Dispose();
                _sellCells.Clear();
                for (int i = 0; i < n; i++)
                {
                    var c = new NpcItemCell(40).Bag(belt + i);
                    c.Parent = _bagView.Content;
                    c.Location = new Point(S(i % 12 * 44), S(i / 12 * 44));
                    c.Click += (o, e) => TogglePick(c.BagSlot);
                    _sellCells.Add(c);
                }
                _bagView.FitContent(0);
                _sellBuilt = n;
            }
            // picks that left the bag drop out
            var present = new HashSet<ulong>();
            foreach (UserItem it in bag) if (it != null) present.Add(it.UniqueID);
            _picks.RemoveWhere(id => !present.Contains(id));
            foreach (NpcItemCell c in _sellCells)
            {
                UserItem it = c.Item;
                bool dont = it?.Info != null && it.Info.Bind.HasFlag(BindMode.DontSell);
                c.SetState(it != null && _picks.Contains(it.UniqueID), false, it != null && IsJunk(it, c.BagSlot) ? W.T(ClientTextKeys.ModernNpcJunk) : null, dont);
                if (dont) c.Hint = W.T(ClientTextKeys.CannotSellItem);
            }
            // the picks list
            uint total = 0;
            var picked = new List<(UserItem Item, int Slot)>();
            for (int i = 0; i < bag.Length; i++)
                if (bag[i] != null && _picks.Contains(bag[i].UniqueID)) { picked.Add((bag[i], i)); total += NpcFmt.SellPrice(bag[i]); }
            var sb = new System.Text.StringBuilder();
            foreach (var p in picked) sb.Append(p.Item.UniqueID).Append(':').Append(p.Slot).Append(':').Append(p.Item.Count).Append(ItemMoves.IsLocked(MirGridType.Inventory, p.Slot) ? "L," : ",");
            string key = sb.ToString();
            if (key != _pickKey)
            {
                _pickKey = key;
                for (int i = _pickList.Content.Controls.Count - 1; i >= 0; i--) _pickList.Content.Controls[i].Dispose();
                int w = (int)Math.Round(_pickList.InnerWidth / ModernRoot.K), y = 0;
                foreach (var pk in picked)
                {
                    var p = pk;
                    string nm = NpcFmt.ItemName(p.Item), pt = "+" + W.N(NpcFmt.SellPrice(p.Item));
                    var row = new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        using (GraphicsPath rp = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k))
                        using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, rp);
                        ModernSkin.Text(g, nm, ModernSkin.Font(11, true), ModernTheme.Text, new RectangleF(38 * k, 2 * k, b.Size.Width - 70 * k, 16 * k));
                        ModernSkin.Text(g, pt, ModernSkin.Font(11), ModernTheme.Gold, new RectangleF(38 * k, 18 * k, b.Size.Width - 70 * k, 16 * k));
                    })
                    { Parent = _pickList.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(36)), NotControl = true };
                    new NpcItemCell(30).Bag(p.Slot).Parent = row;
                    row.Controls[^1].Location = new Point(S(3), S(3));
                    var x = new ModernButton("×") { Parent = row, Location = new Point(S(w - 24), S(7)), Size = new Size(S(20), S(22)), Tooltip = W.T(ClientTextKeys.ModernNpcDontSell) };
                    ulong id = p.Item.UniqueID;
                    x.Click += (o, e) => { _picks.Remove(id); _dirty = true; };
                    y += 40;
                }
                if (picked.Count == 0) W.Label(_pickList.Content, W.T(ClientTextKeys.ModernNpcPickToSell), 0, 24, w, 20, 11.5f, false, ModernTheme.Dim, StringAlignment.Center);
                _pickList.ContentHeight = S(y);
            }
            _pickCount.Text = picked.Count.ToString();
            _sellTotal.Text = W.N(total);
            bool over = (ulong)GameScene.Gold + total > uint.MaxValue;
            _sellTotal.Colour = over ? ModernTheme.BadSoft : ModernTheme.Gold;
            _sellBtn.Text = picked.Count > 0 ? W.T(ClientTextKeys.ModernNpcSellFor, picked.Count, W.N(total)) : W.T(ClientTextKeys.ModernNpcSell);
            _sellBtn.SetDisabled(picked.Count == 0 || over, over ? W.T(ClientTextKeys.CannotCarryMoreGold) : string.Empty);
        }

        /// <summary>Sell: ONE C.SellItems for every pick (whole stacks); the cells lock until each S.SellItem.</summary>
        private void SellPicked()
        {
            if (!_state.SellOpen) return;
            UserItem[] bag = User.Inventory;
            var packet = new C.SellItems();
            var slots = new List<int>();
            uint total = 0;
            for (int i = 0; i < bag.Length; i++)
            {
                UserItem it = bag[i];
                if (it == null || !_picks.Contains(it.UniqueID) || SellRefusal(it, i).Length > 0) continue;
                packet.Items.Add((it.UniqueID, it.Count));
                slots.Add(i);
                total += NpcFmt.SellPrice(it);
            }
            if (packet.Items.Count == 0) return;
            if ((ulong)GameScene.Gold + total > uint.MaxValue) { Say(W.T(ClientTextKeys.CannotCarryMoreGold)); return; }
            Network.Enqueue(packet);
            foreach (int s in slots) ItemMoves.SetLocked(MirGridType.Inventory, s, true);
            _picks.Clear();
            Say(W.T(ClientTextKeys.ModernNpcSellingN, packet.Items.Count, W.N(total)));
            _dirty = true;
        }

        /// <summary>Alt + click in the bag (ItemNpcDropQuickSell): sell or repair that item at once, like the classic drop + confirm.</summary>
        internal void QuickSell(int slot)
        {
            if (!IsOpen || User == null || slot < 0 || slot >= User.Inventory.Length) return;
            UserItem item = User.Inventory[slot];
            if (item == null) return;
            if (_state.SellOpen && (_state.DropType == PanelType.Sell || _state.RepairType == NpcState.NoPanel))
            {
                string why = SellRefusal(item, slot);
                if (why.Length > 0) { NpcFmt.Chat(why); return; }
                if ((ulong)GameScene.Gold + NpcFmt.SellPrice(item) > uint.MaxValue) { NpcFmt.Chat(W.T(ClientTextKeys.CannotCarryMoreGold)); return; }
                Network.Enqueue(new C.SellItem { UniqueID = item.UniqueID, Count = item.Count });
                ItemMoves.SetLocked(MirGridType.Inventory, slot, true);
                _quickSells.Add((item.UniqueID, CMain.Time + QuickSellMs));
                _nextQuickPoll = CMain.Time + 250;
                _picks.Remove(item.UniqueID);
                _dirty = true;
                return;
            }
            if (_state.RepairType != NpcState.NoPanel) RepairOne(item.UniqueID, true);
        }

        // ------------------------------------------------------------------ repair

        private bool Special => _state.RepairType == PanelType.SpecialRepair;

        private string RepairRefusal(ItemInfo info)
        {
            if (info == null) return string.Empty;
            if (info.Bind.HasFlag(BindMode.DontRepair) || (Special && info.Bind.HasFlag(BindMode.NoSRepair))) return W.T(ClientTextKeys.CannotRepairItem);
            return string.Empty;
        }

        private static List<(UserItem Item, int Slot)> Damaged()
        {
            var output = new List<(UserItem, int)>();
            UserItem[] inv = User.Inventory;
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                if (it?.Info == null || it.Info.Durability == 0 || it.MaxDura == 0 || it.CurrentDura >= it.MaxDura) continue;
                output.Add((it, i));
            }
            return output;
        }

        private void RenderRepair()
        {
            bool special = Special;
            var rows = Damaged();
            _repTitle.Text = W.T(special ? ClientTextKeys.ModernNpcSRepairTitle : ClientTextKeys.ModernNpcRepairTitle);
            uint total = 0;
            int count = 0, lost = 0;
            var sb = new System.Text.StringBuilder().Append(special).Append('|').Append(Rate).Append('|').Append(GameScene.Gold).Append('|');
            foreach (var r in rows)
            {
                sb.Append(r.Item.UniqueID).Append(':').Append(r.Slot).Append(':').Append(r.Item.CurrentDura).Append(':').Append(r.Item.MaxDura).Append(ItemMoves.IsLocked(MirGridType.Inventory, r.Slot) ? "L," : ",");
                if (RepairRefusal(r.Item.Info).Length > 0) continue;
                total += NpcFmt.RepairFee(r.Item, Rate, special);
                count++;
                if (!special) lost += (r.Item.MaxDura - r.Item.CurrentDura) / 30;
            }
            string key = sb.ToString();
            if (key != _repairKey)
            {
                _repairKey = key;
                for (int i = _repList.Content.Controls.Count - 1; i >= 0; i--) _repList.Content.Controls[i].Dispose();
                int w = (int)Math.Round(_repList.InnerWidth / ModernRoot.K), y = 0;
                foreach (var rr in rows)
                {
                    var r = rr;
                    UserItem item = r.Item;
                    string why = RepairRefusal(item.Info);
                    uint cost = NpcFmt.RepairFee(item, Rate, special);
                    int loss = special ? 0 : (item.MaxDura - item.CurrentDura) / 30, newMax = item.MaxDura - loss;
                    bool locked = ItemMoves.IsLocked(MirGridType.Inventory, r.Slot);
                    string nm = NpcFmt.ItemName(item);
                    string where = W.T(r.Slot < User.BeltIdx ? ClientTextKeys.ModernNpcBelt : ClientTextKeys.ModernNpcBag) + " · " + NpcFmt.Dura(item.CurrentDura) + " / " + NpcFmt.Dura(item.MaxDura);
                    string lossText = why.Length > 0 ? string.Empty : loss > 0 ? W.T(ClientTextKeys.ModernNpcMaxLoss, NpcFmt.Dura(loss)) : W.T(ClientTextKeys.ModernNpcMaxKept);
                    float baseMax = Math.Max(1, Math.Max((int)item.Info.Durability, (int)item.MaxDura));
                    var row = new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        using (GraphicsPath rp = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k))
                        using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, why.Length > 0 ? 0.02f : 0.05f))) g.FillPath(br, rp);
                        float tx = 50 * k, tw = b.Size.Width - tx - 230 * k;
                        Font nf = ModernSkin.Font(12, true);
                        float nw = Math.Min(ModernSkin.Measure(nm, nf) + 4, tw * 0.6f);
                        ModernSkin.Text(g, nm, nf, why.Length > 0 ? ModernTheme.Dim : ModernTheme.Text, new RectangleF(tx, 5 * k, nw, 18 * k));
                        ModernSkin.Text(g, where, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(tx + nw + 4 * k, 6 * k, tw - nw, 16 * k));
                        ModernSkin.Text(g, lossText, ModernSkin.Font(10), loss > 0 ? ModernTheme.BadSoft : ModernTheme.Ok, new RectangleF(tx, 21 * k, tw, 14 * k));
                        // the durability bar: now, what the repair restores, what it wears off the maximum
                        RectangleF bar = new RectangleF(tx, 37 * k, tw, 6 * k);
                        using (var br = new SolidBrush(Color.FromArgb(255, 4, 6, 10))) g.FillRectangle(br, bar);
                        float now = bar.Width * item.CurrentDura / baseMax, max = bar.Width * newMax / baseMax, all = bar.Width * item.MaxDura / baseMax;
                        Color fill = item.CurrentDura * 5 < item.MaxDura ? ModernTheme.Bad : item.CurrentDura * 2 < item.MaxDura ? ModernTheme.Warn : ModernTheme.Ok;
                        using (var br = new SolidBrush(fill)) g.FillRectangle(br, bar.X, bar.Y, now, bar.Height);
                        if (why.Length == 0)
                            using (var br = new SolidBrush(W.Over(ModernTheme.Ok, 0.35f))) g.FillRectangle(br, bar.X + now, bar.Y, Math.Max(0, max - now), bar.Height);
                        if (loss > 0)
                            using (var br = new SolidBrush(W.Over(ModernTheme.Bad, 0.55f))) g.FillRectangle(br, bar.X + max, bar.Y, Math.Max(0, all - max), bar.Height);
                        if (why.Length > 0) ModernSkin.Text(g, why, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(b.Size.Width - 170 * k, 0, 160 * k, b.Size.Height), StringAlignment.Far);
                        else ModernSkin.Text(g, W.N(cost), ModernSkin.Font(12, true), cost > GameScene.Gold ? ModernTheme.BadSoft : ModernTheme.Gold, new RectangleF(b.Size.Width - 180 * k, 0, 84 * k, b.Size.Height), StringAlignment.Far);
                    })
                    { Parent = _repList.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(48)), NotControl = true };
                    new NpcItemCell(36).Bag(r.Slot).Parent = row;
                    row.Controls[^1].Location = new Point(S(6), S(6));
                    if (why.Length == 0)
                    {
                        var b2 = new ModernButton(W.T(locked ? ClientTextKeys.ModernNpcWaitingShort : ClientTextKeys.ModernNpcSvcRepair)) { Parent = row };
                        b2.AutoWidth(76);
                        b2.Location = new Point(S(w - 8) - b2.Size.Width, S(13));
                        b2.SetDisabled(locked || cost > GameScene.Gold, cost > GameScene.Gold ? W.T(ClientTextKeys.LowGold) : W.T(ClientTextKeys.ModernWinWaitingServer));
                        ulong id = item.UniqueID;
                        b2.Click += (o, e) => RepairOne(id, false);
                    }
                    y += 52;
                }
                if (rows.Count == 0) W.Label(_repList.Content, W.T(ClientTextKeys.ModernNpcNothingToRepair), 0, 24, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
                _repList.ContentHeight = S(y);
            }
            _repExplain.Text = W.T(special ? ClientTextKeys.ModernNpcSRepairExplain : ClientTextKeys.ModernNpcRepairExplain);
            // the other kind of repair, when the page links to it
            var other = new List<(string Key, string Text)>();
            if (_state.PageLinks().TryGetValue(special ? "@REPAIR" : "@SREPAIR", out var ol)) other.Add(ol);
            string sk = string.Join(",", other);
            if (sk != _repSwitchKey)
            {
                _repSwitchKey = sk;
                for (int i = _repSwitch.Controls.Count - 1; i >= 0; i--) _repSwitch.Controls[i].Dispose();
                int x = 0;
                foreach (var l in other)
                {
                    string call = l.Key, text = l.Text;
                    var b = new ModernButton(text.Length > 0 ? text : W.T(ClientTextKeys.ModernNpcSvcRepair)) { Parent = _repSwitch, Location = new Point(S(x), 0) };
                    b.AutoWidth(80);
                    b.Click += (o, e) => _state.Call(call, text);
                    x = W.Right(b) + 6;
                }
            }
            _repSum.Text = W.T(ClientTextKeys.ModernNpcToRepair, count) + (!special && lost > 0 ? "\n" + W.T(ClientTextKeys.ModernNpcMaxLost, NpcFmt.Dura(lost)) : string.Empty);
            _repTotal.Text = W.N(total);
            _repTotal.Colour = total > GameScene.Gold ? ModernTheme.BadSoft : ModernTheme.Gold;
            _repAllBtn.Text = total > GameScene.Gold ? W.T(ClientTextKeys.ModernNpcNotEnoughForAll) : count > 0 ? W.T(ClientTextKeys.ModernNpcRepairAllFor, W.N(total)) : W.T(ClientTextKeys.ModernNpcRepairAll);
            _repAllBtn.SetDisabled(count == 0 || total > GameScene.Gold, total > GameScene.Gold ? W.T(ClientTextKeys.ModernNpcRepairOneByOne) : string.Empty);
        }

        private void RepairOne(ulong id, bool chat)
        {
            if (_state.RepairType == NpcState.NoPanel) return;
            UserItem item = NpcFmt.FindBag(id, out int slot);
            if (item == null || ItemMoves.IsLocked(MirGridType.Inventory, slot)) return;
            string why = RepairRefusal(item.Info);
            if (why.Length > 0) { if (chat) NpcFmt.Chat(why); else Say(why); return; }
            if (NpcFmt.RepairFee(item, Rate, Special) > GameScene.Gold) { if (chat) NpcFmt.Chat(W.T(ClientTextKeys.LowGold)); else Say(W.T(ClientTextKeys.LowGold)); return; }
            var p = new C.RepairItems { Special = Special };
            p.UniqueIDs.Add(id);
            Network.Enqueue(p);
            ItemMoves.SetLocked(MirGridType.Inventory, slot, true);
            Say(W.T(ClientTextKeys.ModernNpcRepairing, NpcFmt.ItemName(item)));
            _dirty = true;
        }

        /// <summary>Repair all (bag): ONE C.RepairItems for every damaged item the merchant takes.</summary>
        private void RepairAll()
        {
            if (_state.RepairType == NpcState.NoPanel) return;
            var p = new C.RepairItems { Special = Special };
            var slots = new List<int>();
            uint total = 0;
            foreach (var r in Damaged())
            {
                if (RepairRefusal(r.Item.Info).Length > 0 || ItemMoves.IsLocked(MirGridType.Inventory, r.Slot)) continue;
                p.UniqueIDs.Add(r.Item.UniqueID);
                slots.Add(r.Slot);
                total += NpcFmt.RepairFee(r.Item, Rate, Special);
            }
            if (p.UniqueIDs.Count == 0) return;
            if (total > GameScene.Gold) { Say(W.T(ClientTextKeys.LowGold)); return; }
            Network.Enqueue(p);
            foreach (int s in slots) ItemMoves.SetLocked(MirGridType.Inventory, s, true);
            Say(W.T(ClientTextKeys.ModernNpcRepairingN, p.UniqueIDs.Count, W.N(total)));
            _dirty = true;
        }
    }
}
