// Ported from WebClient/src/ui/modern/windows/trade.ts (TradeWindow + TradeSession): the new UI's trade window. The server
// opens it (S.TradeAccept → ModernUi.TradeAccepted); the incoming request is ModernUi.TradeRequest's toast and the Trade key
// (KeybindOptions.Trade, GameScene) sends C.TradeRequest to the player you face. Top: the partner card (class / level / guild
// from the partner in view, else the friends list), the steps Offer → Confirm → Done, Whisper (prefills "/name "). Offers: your
// side = MirGridType.Trade cells (ItemMoves: the classic pick / place, C.DepositTradeItem / C.RetrieveTradeItem / C.MergeItem)
// + "Add gold" (the classic GoldLabel: an amount up to your gold, C.TradeGold, add-only: the gold leaves you at once and comes
// back on a cancel); their side = MirGridType.GuestTrade cells (read-only, Models.Trade); a fairness meter on the estimated
// worth (ItemInfo.Price × count + gold). Your bag beside them (the classic TradeAccept opens the bag next to the trade): a click
// on a bag item offers it (onto a matching stack with room, else the first free slot), a double-click on an offered item takes
// it back; DontTrade items are striped and refused. Confirm = C.TradeConfirm with User.TradeLocked flipped (any change on
// either side unlocks you, as the server does); a refused room check (S.TradeCancel Unlock) shows its note. S.TradeConfirm shows
// a summary of what changed hands (the offers as they stood when you confirmed). EVERY close path (the cross, Esc, Close all)
// cancels an open trade with C.TradeCancel like the classic close button (log-out: the server cancels it itself);
// S.TradeCancel closes the window with the classic "must face" box when the cancel was not yours.
// HIDDEN as on the web: the partner's "confirmed" pill (no packet) and a trade chat thread (use whispers).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class TradeWindow : ModernWindow
    {
        public const string WindowKey = "trade";
        private const int WidthHud = 600, Slots = 10, Cell = 40, Gap = 4;

        /// <summary>Set by ModernUi.DisposeSocial at log-out: closing then sends nothing (the server cancels the trade).</summary>
        internal static bool Ending;

        // the session (trade.ts TradeSession)
        private bool _active, _selfCancel;
        private string _partner = string.Empty, _roomNote = string.Empty, _note = string.Empty;
        private (List<UserItem> Items, uint Gold) _mine, _guest;
        private Result _result, _resultShown;

        private sealed class Result
        {
            public string Partner;
            public List<UserItem> GaveItems, GotItems;
            public uint GaveGold, GotGold;
        }

        private PaintBox _top, _steps, _mineBox, _theirBox, _mid, _bagBox, _done;
        private ModernButton _whisper, _addGold, _cancel, _confirm, _doneClose;
        private ModernLabel _noteLbl;
        private readonly List<ModernItemCell> _mineCells = new List<ModernItemCell>(), _theirCells = new List<ModernItemCell>();
        private BagColumn _bag;
        private readonly GridWatch _tradeWatch = new GridWatch(), _bagWatch = new GridWatch();
        private bool _dirty = true;
        private long _nextTick;
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action<UserItem> _itemInfo;

        // painted values
        private string _partnerName = string.Empty, _partnerSub = string.Empty, _partnerTag = "?";
        private Color _partnerRing = Color.Empty;
        private int _mineCount, _theirCount;
        private long _mineWorth, _theirWorth;
        private string _freeText = string.Empty, _weightText = string.Empty;
        private bool _freeBad, _weightBad;

        public TradeWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinTrade), 60, 40)
        {
            ContentWidth = WidthHud;
            ContentHeight = 500;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Trade || k.Grid == MirGridType.GuestTrade || k.Grid == MirGridType.Inventory || k.Grid == MirGridType.None) _dirty = true; };
            _itemInfo = _ => _dirty = true;
        }

        private static UserObject User => GameScene.User;
        private static TradeState Model => GameScene.Models.Trade;
        private static int Belt => GameScene.User?.BeltIdx ?? UserObject.BeltSize;

        // ---- for the UI test
        internal bool Active => _active;
        internal string NoteShown => _noteLbl?.Text ?? string.Empty;
        internal bool SummaryShown => _done != null && _done.Visible;
        internal ModernButton ConfirmButton => _confirm;
        internal ModernButton CancelButton => _cancel;
        internal ModernButton AddGoldButton => _addGold;
        internal ModernButton SummaryClose => _doneClose;
        internal IReadOnlyList<ModernItemCell> BagCells => _bag?.Cells;
        internal IReadOnlyList<ModernItemCell> MineCells => _mineCells;

        // ------------------------------------------------------------------ session (called by ModernUi.Social)

        /// <summary>S.TradeAccept: a new trade with Models.Trade.GuestName.</summary>
        internal void Start()
        {
            _active = true;
            _selfCancel = false;
            _partner = Model.GuestName ?? string.Empty;
            _roomNote = _note = string.Empty;
            _result = null;
            _mine = (new List<UserItem>(), 0);
            _guest = (new List<UserItem>(), 0);
            _dirty = true;
        }

        /// <summary>S.TradeItem / S.TradeGold: the guest's offer changed (User.TradeLocked already cleared).</summary>
        internal void GuestChanged()
        {
            BindGuest();
            _roomNote = string.Empty;
            _dirty = true;
        }

        /// <summary>S.TradeConfirm: the trade went through (both sides already reset): the summary.</summary>
        internal void Confirmed()
        {
            if (!_active) return;
            _active = false;
            _roomNote = string.Empty;
            _result = new Result { Partner = _partner, GaveItems = _mine.Items, GaveGold = _mine.Gold, GotItems = _guest.Items, GotGold = _guest.Gold };
            _dirty = true;
        }

        /// <summary>S.TradeCancel. Unlock = the room check failed (both unlocked); else the trade ended: close. Returns whether the
        /// "must face" box should show (the cancel was not yours).</summary>
        internal bool Cancelled(bool unlock)
        {
            if (unlock)
            {
                _roomNote = W.T(ClientTextKeys.ModernSocialTradeRoomCheck);
                _dirty = true;
                return false;
            }
            bool wasActive = _active, self = _selfCancel;
            _active = false;
            _selfCancel = false;
            _roomNote = string.Empty;
            _result = null;
            Close();
            return wasActive && !self;
        }

        /// <summary>A trade item move came back (classic: TradeDialog.ChangeLockState(false)).</summary>
        internal void ItemReturned()
        {
            if (User != null) User.TradeLocked = false;
            _dirty = true;
        }

        // ------------------------------------------------------------------ layout

        protected override void Build()
        {
            int top = TitleHeight;
            _top = new PaintBox(PaintTop) { Parent = this, Location = new Point(S(1), S(top)), Size = new Size(S(WidthHud) - 2, S(60)), NotControl = true };
            W.Divider(this, 0, top + 60, WidthHud);
            _steps = new PaintBox(PaintSteps) { Parent = this, Location = new Point(S(WidthHud - 14 - 96 - 10 - 230), S(top + 19)), Size = new Size(S(230), S(22)), NotControl = true };
            _whisper = W.At(new ModernButton(W.T(ClientTextKeys.ModernWinWhisper)) { Tooltip = W.T(ClientTextKeys.ModernSocialTradeWhisperHint) }, this, WidthHud - 14 - 96, top + 17);
            _whisper.Size = new Size(S(96), S(26));
            _whisper.Click += (o, e) => { if (_partner.Length > 0) Soc.Prefill("/" + _partner + " "); };

            // offers
            int oy = top + 72, sideW = (WidthHud - 28 - 52) / 2, sideH = 186;
            _mineBox = new PaintBox((g, b) => PaintSide(g, b, true)) { Parent = this, Location = new Point(S(14), S(oy)), Size = new Size(S(sideW), S(sideH)), NotControl = true };
            _theirBox = new PaintBox((g, b) => PaintSide(g, b, false)) { Parent = this, Location = new Point(S(14 + sideW + 52), S(oy)), Size = new Size(S(sideW), S(sideH)), NotControl = true };
            _mid = new PaintBox(PaintMid) { Parent = this, Location = new Point(S(14 + sideW), S(oy)), Size = new Size(S(52), S(sideH)), Hint = W.T(ClientTextKeys.ModernSocialTradeWorthHint) };
            _mid.AfterPaint = b =>
            {
                float k = ModernRoot.K;
                int s = S(18);
                W.DrawIcon(ModernIcons.Swap, new Rectangle(b.DisplayLocation.X + (b.Size.Width - s) / 2, b.DisplayLocation.Y + S(sideH / 2 - 26) + (S(34) - s) / 2, s, s), ModernTheme.Accent);
            };
            _mineCells.Clear();
            _theirCells.Clear();
            int gx = (sideW - (5 * Cell + 4 * Gap)) / 2;
            for (int i = 0; i < Slots; i++)
            {
                var mc = new ModernItemCell(MirGridType.Trade, i, Cell) { Parent = this, Location = new Point(S(14 + gx + i % 5 * (Cell + Gap)), S(oy + 34 + i / 5 * (Cell + Gap))) };
                mc.Click += OnTradeCellClick;
                _mineCells.Add(mc);
                var tc = new ModernItemCell(MirGridType.GuestTrade, i, Cell) { Parent = this, Location = new Point(S(14 + sideW + 52 + gx + i % 5 * (Cell + Gap)), S(oy + 34 + i / 5 * (Cell + Gap))), ReadOnly = true };
                _theirCells.Add(tc);
            }
            _addGold = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialAddGold)) { Tooltip = W.T(ClientTextKeys.ModernSocialAddGoldHint) }, this, 14 + 10 + 20, oy + 34 + 2 * (Cell + Gap) + 6);
            _addGold.Size = new Size(S(84), S(22));
            _addGold.Click += (o, e) => AddGold();

            // the bag
            int by = oy + sideH + 10, bagH = 132;
            _bagBox = new PaintBox(PaintBag) { Parent = this, Location = new Point(S(14), S(by)), Size = new Size(S(WidthHud - 28), S(bagH + 38)), NotControl = true };
            _bag = new BagColumn(this, 24, by + 30, 12, Cell, bagH, Offer, it => it.Info == null || !it.Info.Bind.HasFlag(BindMode.DontTrade));
            _bagWatch.Reset();

            // footer
            int fy = by + bagH + 38 + 12;
            W.Divider(this, 0, fy, WidthHud);
            _noteLbl = W.Label(this, string.Empty, 14, fy + 8, WidthHud - 28 - 150 - 110 - 16, 36, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            _cancel = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialTradeCancel)) { Tooltip = W.T(ClientTextKeys.ModernSocialTradeCancelHint) }, this, WidthHud - 14 - 150 - 8 - 110, fy + 11);
            _cancel.Size = new Size(S(110), S(30));
            _cancel.Click += (o, e) => Close();
            _confirm = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialTradeConfirm), ButtonKind.Main), this, WidthHud - 14 - 150, fy + 11);
            _confirm.Size = new Size(S(150), S(30));
            _confirm.Click += (o, e) => Confirm();
            ContentHeight = fy + 52 - top;

            // the summary card (over everything below the title)
            _done = new PaintBox(PaintDone) { Parent = this, Location = new Point(S(1), S(top)), Size = new Size(S(WidthHud) - 2, S(ContentHeight) - 1), Visible = false };
            _doneClose = new ModernButton(W.T(ClientTextKeys.ModernClose), ButtonKind.Main) { Parent = _done };
            _doneClose.Size = new Size(S(110), S(28));
            _doneClose.Click += (o, e) => Close();
            _resultShown = null;
            _tradeWatch.Reset();
            _primed = false;
            _dirty = true;
            Render();
        }

        protected override void Shown()
        {
            Model.Changed.On(_changed);
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.ItemInfo.On(_itemInfo);
            _note = string.Empty;
            _dirty = true;
        }

        /// <summary>Every close path: an open trade is cancelled (TradeDialog's close button: C.TradeCancel).</summary>
        protected override void Closed()
        {
            Model.Changed.Off(_changed);
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.ItemInfo.Off(_itemInfo);
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && (pick.GridType == MirGridType.Trade || pick.GridType == MirGridType.GuestTrade)) ItemMoves.Selected = null;
            _result = null;
            if (!_active) return;
            _active = false;
            _selfCancel = true;
            if (Ending || GameScene.Scene == null) return; // log-out: the server cancels the trade itself (PlayerObject.StopGame)
            Network.Enqueue(new C.TradeCancel());
        }

        public override void Process()
        {
            base.Process();
            // a deposit / retrieve reply changes the trade grid without a seam call here (ModernUi.ItemTradeUnlocked is not
            // this area's): any change of the offer unlocks you, as the server does (classic TradeDialog.ChangeLockState(false))
            if (_tradeWatch.Poll(MirGridType.Trade, User?.Trade))
            {
                if (_primed && User != null && User.TradeLocked && _active) User.TradeLocked = false;
                _primed = true;
                _dirty = true;
            }
            if (_bagWatch.Poll(MirGridType.Inventory, User?.Inventory)) _dirty = true;
            if (_dirty) Render();
            else if (CMain.Time >= _nextTick) { _nextTick = CMain.Time + 1000; PartnerCard(); }
        }

        private bool _primed;

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_top == null || _top.IsDisposed || User == null) return;
            BindGuest();
            // S.TradeItem replaces the guest array and the trade resets clear both without ItemMoves.SlotChanged: re-read the cells
            for (int i = 0; i < _mineCells.Count; i++) _mineCells[i].Refresh(false);
            for (int i = 0; i < _theirCells.Count; i++) _theirCells[i].Refresh(false);
            UserObject u = User;
            TradeState t = Model;
            _mineCount = Count(u.Trade);
            _theirCount = Count(t.GuestItems);
            _mineWorth = Worth(u.Trade, u.TradeGoldAmount);
            _theirWorth = Worth(t.GuestItems, t.GuestGold);
            _mineBox.Invalidate();
            _theirBox.Invalidate();
            _mid.Invalidate();
            _steps.Invalidate();
            PartnerCard();
            RenderBag();

            _addGold.SetDisabled(!_active || GameScene.Gold == 0, !_active ? W.T(ClientTextKeys.ModernSocialTradeOver) : W.T(ClientTextKeys.ModernSocialNoGold));
            _cancel.Visible = _active;
            _confirm.Visible = _active;
            _confirm.Text = u.TradeLocked ? W.T(ClientTextKeys.ModernSocialTradeConfirmedUndo) : W.T(ClientTextKeys.ModernSocialTradeConfirm);
            _confirm.Kind = u.TradeLocked ? ButtonKind.Normal : ButtonKind.Main;
            string warn = _roomNote.Length > 0 ? _roomNote : _note;
            _noteLbl.Text = warn.Length > 0 ? warn : !_active ? (_result != null ? string.Empty : W.T(ClientTextKeys.ModernSocialTradeOverDot))
                : u.TradeLocked ? W.T(ClientTextKeys.ModernSocialTradeWaiting, _partner) : W.T(ClientTextKeys.ModernSocialTradeBothConfirm);
            _noteLbl.Colour = warn.Length > 0 ? ModernTheme.Warn : ModernTheme.Dim;

            if (_result != _resultShown)
            {
                _resultShown = _result;
                _done.Visible = _result != null;
                if (_result != null)
                {
                    _done.Invalidate();
                    _doneClose.Location = new Point((_done.Size.Width - _doneClose.Size.Width) / 2, (int)DoneCard().Bottom - S(16) - _doneClose.Size.Height);
                }
            }
        }

        private static int Count(UserItem[] items)
        {
            int n = 0;
            if (items != null) for (int i = 0; i < items.Length; i++) if (items[i] != null) n++;
            return n;
        }

        /// <summary>Estimated worth: ItemInfo.Price × count (unknown infos count 0) plus gold.</summary>
        private static long Worth(UserItem[] items, uint gold)
        {
            long w = gold;
            if (items != null)
                for (int i = 0; i < items.Length; i++)
                    if (items[i]?.Info != null) w += (long)items[i].Info.Price * items[i].Count;
            return w;
        }

        private static void BindGuest()
        {
            UserItem[] g = GameScene.Models?.Trade.GuestItems;
            if (g == null) return;
            for (int i = 0; i < g.Length; i++) if (g[i] != null && g[i].Info == null) GameScene.Bind(g[i]);
        }

        /// <summary>The partner card: class / level / guild from the partner in view, else the friends list (1 s tick while open).</summary>
        private void PartnerCard()
        {
            string name = _partner;
            PlayerObject ob = Soc.InView(name);
            ClientFriend friend = null;
            foreach (ClientFriend f in GameScene.Models.Friends.Friends) if (f != null && f.Name == name) { friend = f; break; }
            int cls = ob != null ? (int)ob.Class : friend != null ? (int)friend.Class : -1;
            int level = ob != null && ob.Level > 0 ? ob.Level : friend?.Level ?? 0;
            string guild = !string.IsNullOrEmpty(ob?.GuildName) ? ob.GuildName : friend?.GuildName ?? string.Empty;
            var bits = new List<string>();
            if (cls >= 0) bits.Add(Soc.ClassName((MirClass)cls) + (level > 0 ? " " + level : string.Empty));
            if (guild.Length > 0) bits.Add(guild);
            if (friend != null) bits.Add(W.T(ClientTextKeys.ModernSocialFriendTag));
            if (ob == null) bits.Add(W.T(ClientTextKeys.ModernSocialNotInView));
            string sub = string.Join(" · ", bits), tag = cls >= 0 ? Soc.Tag((MirClass)cls) : "?";
            string shownName = name.Length > 0 ? name : W.T(ClientTextKeys.ModernWinTrade);
            Color ring = cls >= 0 ? ModernTheme.Class((MirClass)cls) : Color.Empty;
            _whisper.Visible = name.Length > 0;
            if (shownName == _partnerName && sub == _partnerSub && tag == _partnerTag && ring == _partnerRing) return;
            _partnerName = shownName;
            _partnerSub = sub;
            _partnerTag = tag;
            _partnerRing = ring;
            _top.Invalidate();
        }

        private void RenderBag()
        {
            _bag.Sync(); // DontTrade items dimmed (BagColumn's "useful" veil); a click on one is refused in Offer
            // after the trade (a client estimate; the server decides when both have confirmed): free cells of the whole
            // inventory with the partner's items in, and the bag weight with them
            UserObject u = User;
            int free = 0;
            for (int i = 0; i < u.Inventory.Length; i++) if (u.Inventory[i] == null) free++;
            int gotItems = 0, gotWeight = 0;
            UserItem[] guest = Model.GuestItems;
            for (int i = 0; i < guest.Length; i++)
            {
                if (guest[i] == null) continue;
                gotItems++;
                if (guest[i].Info != null) gotWeight += guest[i].Info.Type == ItemType.Amulet || guest[i].Info.Type == ItemType.Bait ? guest[i].Info.Weight : guest[i].Info.Weight * guest[i].Count;
            }
            int after = free - gotItems;
            _freeText = W.T(after == 1 ? ClientTextKeys.ModernSocialFreeCell : ClientTextKeys.ModernSocialFreeCells, Math.Max(0, after));
            _freeBad = after < 0;
            int max = u.Stats[Stat.BagWeight], weight = u.CurrentBagWeight + gotWeight;
            _weightText = max > 0 ? W.T(ClientTextKeys.ModernSocialWeightOf, W.N(weight), W.N(max)) : string.Empty;
            _weightBad = max > 0 && weight > max;
            _bagBox.Invalidate();
        }

        // ------------------------------------------------------------------ painting

        private void PaintTop(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            using (var br = new LinearGradientBrush(new RectangleF(0, 0, b.Size.Width * 0.45f + 1, b.Size.Height), W.Over(ModernTheme.Accent, 0.14f), Color.FromArgb(0, W.Page), 0f))
                g.FillRectangle(br, 0, 0, b.Size.Width * 0.45f, b.Size.Height);
            RectangleF av = new RectangleF(14 * k, 10 * k, 40 * k, 40 * k);
            using (var glow = new Pen(ModernTheme.Alpha(ModernTheme.Accent, 0.3f), 4 * k)) g.DrawEllipse(glow, RectangleF.Inflate(av, 1, 1));
            Soc.Medal(g, av, _partnerTag, ModernTheme.Accent, -1, false, 2f, 11, _partnerRing.IsEmpty ? ModernTheme.Text : _partnerRing);
            float x = 66 * k, w = 230 * k;
            ModernSkin.Text(g, _partnerName, ModernSkin.Font(15, true), Color.White, new RectangleF(x, 12 * k, w, 20 * k));
            ModernSkin.Text(g, _partnerSub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(x, 32 * k, w, 16 * k));
        }

        private void PaintSteps(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            int step = _result != null ? 2 : User?.TradeLocked == true ? 1 : 0;
            string[] labels = { W.T(ClientTextKeys.ModernSocialStepOffer), W.T(ClientTextKeys.ModernSocialStepConfirm), W.T(ClientTextKeys.ModernSocialStepDone) };
            Font f = ModernSkin.Font(10), nf = ModernSkin.Font(9, true);
            float[] widths = new float[3];
            float total = 0;
            for (int i = 0; i < 3; i++) { widths[i] = 18 * k + 5 * k + ModernSkin.Measure(labels[i], f) + 2; total += widths[i]; }
            total += 2 * (14 * k + 12 * k);
            float x = b.Size.Width - total, cy = b.Size.Height / 2f;
            for (int i = 0; i < 3; i++)
            {
                bool on = i == step, ok = i < step;
                RectangleF d = new RectangleF(x, cy - 9 * k, 18 * k, 18 * k);
                if (on) using (var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.4f))) g.FillEllipse(br, d);
                using (var pen = new Pen(on || ok ? ModernTheme.Accent : ModernTheme.Frame, 1f)) g.DrawEllipse(pen, d);
                ModernSkin.Text(g, ok ? "✓" : (i + 1).ToString(), nf, on ? Color.White : ok ? ModernTheme.Accent : ModernTheme.Dim, d, StringAlignment.Center, StringAlignment.Center, false);
                ModernSkin.Text(g, labels[i], f, on ? Color.White : ModernTheme.Dim, new RectangleF(x + 23 * k, 0, widths[i] - 23 * k + 2, b.Size.Height));
                x += widths[i];
                if (i < 2)
                {
                    using var pen = new Pen(W.Over(ModernTheme.Frame, 0.8f), 1f);
                    g.DrawLine(pen, x + 6 * k, cy, x + 20 * k, cy);
                    x += 26 * k;
                }
            }
        }

        private void PaintSide(Graphics g, PaintBox b, bool mine)
        {
            float k = ModernRoot.K;
            UserObject u = User;
            bool locked = mine && u != null && u.TradeLocked && _active;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                using (var br = new SolidBrush(locked ? W.Over(ModernTheme.Accent, 0.1f) : W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, locked ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.6f), locked ? 2f : 1f);
            }
            string title = mine ? W.T(ClientTextKeys.ModernSocialYourOffer) : _partner.Length > 0 ? W.T(ClientTextKeys.ModernSocialTheirOfferOf, _partner) : W.T(ClientTextKeys.ModernSocialTheirOffer);
            float pw = 0;
            if (mine && u != null) pw = Soc.Pill(g, b.Size.Width - 10 * k, 9 * k, u.TradeLocked ? W.T(ClientTextKeys.ModernSocialConfirmed) : W.T(ClientTextKeys.ModernSocialNotConfirmed), locked ? ModernTheme.Accent : ModernTheme.Dim, 9, locked, true, 16);
            ModernSkin.Text(g, title, ModernSkin.Font(12, true), Color.White, new RectangleF(10 * k, 8 * k, b.Size.Width - 26 * k - pw, 18 * k));
            // gold row (under the grid)
            float gy = (34 + 2 * Cell + Gap + 8) * k;
            RectangleF coin = new RectangleF(10 * k, gy + 4 * k, 14 * k, 14 * k);
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(coin);
                using var cb = new PathGradientBrush(p) { CenterColor = Color.FromArgb(0xff, 0xe9, 0xa0), SurroundColors = new[] { Color.FromArgb(0xc9, 0x92, 0x1f) } };
                cb.CenterPoint = new PointF(coin.X + coin.Width * 0.35f, coin.Y + coin.Height * 0.35f);
                g.FillEllipse(cb, coin);
            }
            uint gold = mine ? u?.TradeGoldAmount ?? 0 : Model.GuestGold;
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialGoldAmount, W.N(gold)), ModernSkin.Font(13, true), ModernTheme.Gold, new RectangleF(10 * k, gy, b.Size.Width - 20 * k, 22 * k), StringAlignment.Far);
            float ey = gy + 30 * k;
            Font ef = ModernSkin.Font(10);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialSlotsOf, mine ? _mineCount : _theirCount, Slots), ef, ModernTheme.Dim, new RectangleF(10 * k, ey, b.Size.Width / 2f, 14 * k));
            string worth = W.N(mine ? _mineWorth : _theirWorth);
            float ww = ModernSkin.Measure(worth, ModernSkin.Font(10, true)) + 2;
            ModernSkin.Text(g, worth, ModernSkin.Font(10, true), ModernTheme.Text, new RectangleF(b.Size.Width - 10 * k - ww, ey, ww, 14 * k));
            string wl = W.T(ClientTextKeys.ModernSocialWorth);
            float wlw = ModernSkin.Measure(wl, ef) + 2;
            ModernSkin.Text(g, wl, ef, ModernTheme.Dim, new RectangleF(b.Size.Width - 10 * k - ww - wlw - 2 * k, ey, wlw, 14 * k));
        }

        private void PaintMid(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, cx = b.Size.Width / 2f;
            RectangleF swap = new RectangleF(cx - 17 * k, b.Size.Height / 2f - 26 * k, 34 * k, 34 * k);
            using (var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.12f))) g.FillEllipse(br, swap);
            using (var pen = new Pen(ModernTheme.Accent, 1f)) g.DrawEllipse(pen, swap);
            long total = _mineWorth + _theirWorth;
            float share = total > 0 ? _theirWorth / (float)total : 0.5f;
            RectangleF m = new RectangleF(4 * k, swap.Bottom + 10 * k, b.Size.Width - 8 * k, 5 * k);
            using (GraphicsPath p = ModernSkin.RoundRect(m, 2.5f * k))
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Frame), Color.Black, 0.6f))) g.FillPath(br, p);
            if (share > 0)
                using (var lg = new LinearGradientBrush(new RectangleF(m.X - 1, m.Y, m.Width + 2, m.Height), ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f), ModernTheme.Accent, 0f))
                    g.FillRectangle(lg, m.X, m.Y, m.Width * share, m.Height);
            using (var pen = new Pen(Color.White, 1f)) g.DrawLine(pen, cx, m.Y - 2 * k, cx, m.Bottom + 2 * k);
            string verdict = total == 0 ? W.T(ClientTextKeys.ModernSocialFairEmpty) : Math.Abs(share - 0.5f) < 0.08f ? W.T(ClientTextKeys.ModernSocialFair)
                : share > 0.5f ? W.T(ClientTextKeys.ModernSocialYourFavour) : W.T(ClientTextKeys.ModernSocialTheirFavour);
            ModernSkin.Text(g, verdict.ToUpperInvariant(), ModernSkin.Font(7.5f, true), ModernTheme.Dim, new RectangleF(0, m.Bottom + 4 * k, b.Size.Width, 22 * k), StringAlignment.Center, StringAlignment.Near, false, true);
        }

        private void PaintBag(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            W.Card(g, b.Size, W.Over(ModernTheme.Frame, 0.5f), 0.04f);
            Font lf = ModernSkin.Font(10, true), vf = ModernSkin.Font(10, true);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialBagOffer).ToUpperInvariant(), lf, ModernTheme.Dim, new RectangleF(10 * k, 8 * k, b.Size.Width * 0.55f, 16 * k));
            // "After the trade: n free cells · w / m weight" on the right
            float x = b.Size.Width - 10 * k;
            if (_weightText.Length > 0)
            {
                float w = ModernSkin.Measure(_weightText, vf) + 2;
                x -= w;
                ModernSkin.Text(g, _weightText, vf, _weightBad ? ModernTheme.BadSoft : ModernTheme.Text, new RectangleF(x, 8 * k, w, 16 * k));
                float dw = ModernSkin.Measure(" · ", lf) + 2;
                x -= dw;
                ModernSkin.Text(g, " · ", lf, ModernTheme.Dim, new RectangleF(x, 8 * k, dw, 16 * k));
            }
            float fw = ModernSkin.Measure(_freeText, vf) + 2;
            x -= fw;
            ModernSkin.Text(g, _freeText, vf, _freeBad ? ModernTheme.BadSoft : ModernTheme.Text, new RectangleF(x, 8 * k, fw, 16 * k));
            string after = W.T(ClientTextKeys.ModernSocialAfterTrade).ToUpperInvariant();
            float aw = ModernSkin.Measure(after, lf) + 4 * k;
            ModernSkin.Text(g, after, lf, ModernTheme.Dim, new RectangleF(x - aw, 8 * k, aw, 16 * k));
        }

        private RectangleF DoneCard()
        {
            float k = ModernRoot.K;
            int rows = 1;
            if (_result != null) rows = Math.Max(Lines(_result.GotItems, _result.GotGold), Lines(_result.GaveItems, _result.GaveGold));
            float w = 420 * k, h = (18 + 22 + 16 + 12 + 18 + rows * 16 + 16 + 28 + 16) * k;
            return new RectangleF((_done.Size.Width - w) / 2f, Math.Max(20 * k, (_done.Size.Height - h) / 2f), w, h);
        }

        private static int Lines(List<UserItem> items, uint gold)
        {
            int n = gold > 0 ? 1 : 0;
            foreach (UserItem it in items) if (it != null) n++;
            return Math.Max(1, n);
        }

        private void PaintDone(Graphics g, PaintBox b)
        {
            if (_result == null) return;
            float k = ModernRoot.K;
            // rgba(0,0,0,.65) over the offers (the window's opaque panel is under it, so the veil may be translucent)
            using (var br = new SolidBrush(Color.FromArgb(166, 0, 0, 0))) g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height);
            RectangleF card = DoneCard();
            ModernSkin.Glow(g, card, ModernTheme.Radius * k, ModernTheme.GlowColour, 10 * k);
            using (GraphicsPath p = ModernSkin.RoundRect(card, ModernTheme.Radius * k))
            {
                using (var br = new SolidBrush(W.Page)) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ModernTheme.Accent);
            }
            float y = card.Y + 18 * k;
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialTradeComplete), ModernSkin.Font(17, true), Color.White, new RectangleF(card.X, y, card.Width, 22 * k), StringAlignment.Center);
            y += 24 * k;
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialWithName, _result.Partner), ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(card.X, y, card.Width, 16 * k), StringAlignment.Center);
            y += 28 * k;
            float colW = (card.Width - 40 * k - 12 * k) / 2f;
            Column(g, card.X + 20 * k, y, colW, W.T(ClientTextKeys.ModernSocialYouReceived), _result.GotItems, _result.GotGold);
            Column(g, card.X + 20 * k + colW + 12 * k, y, colW, W.T(ClientTextKeys.ModernSocialYouGave), _result.GaveItems, _result.GaveGold);
        }

        private static void Column(Graphics g, float x, float y, float w, string title, List<UserItem> items, uint gold)
        {
            float k = ModernRoot.K;
            ModernSkin.Text(g, title.ToUpperInvariant(), ModernSkin.Font(10, true), ModernTheme.AccentDim, new RectangleF(x, y, w, 16 * k));
            y += 18 * k;
            Font f = ModernSkin.Font(11);
            int n = 0;
            foreach (UserItem it in items)
            {
                if (it == null) continue;
                string name = (it.Info?.FriendlyName ?? it.Info?.Name ?? W.T(ClientTextKeys.ModernSocialItem)) + (it.Count > 1 ? " x" + W.N(it.Count) : string.Empty);
                ModernSkin.Text(g, name, f, ModernTheme.Text, new RectangleF(x, y, w, 16 * k));
                y += 16 * k;
                n++;
            }
            if (gold > 0) { ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialGoldAmount, W.N(gold)), f, ModernTheme.Gold, new RectangleF(x, y, w, 16 * k)); n++; }
            if (n == 0) ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialNothing), f, ModernTheme.Dim, new RectangleF(x, y, w, 16 * k));
        }

        // ------------------------------------------------------------------ actions (through ItemMoves: nothing moves until the server answers)

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            _dirty = true;
        }

        /// <summary>A click on a bag item (BagColumn): onto a matching offered stack with room, else the first free trade slot,
        /// through the classic "To Trade" logic. True = handled (the item is not picked up).</summary>
        private bool Offer(int bagSlot)
        {
            UserObject u = User;
            if (!_active || u == null) return false;
            UserItem item = u.Inventory[bagSlot];
            if (item?.Info == null) return false;
            if (item.Info.Bind.HasFlag(BindMode.DontTrade))
            {
                Say(W.T(ClientTextKeys.ModernSocialCantTrade, item.Info.FriendlyName ?? item.Info.Name));
                return true;
            }
            int to = -1;
            for (int i = 0; i < u.Trade.Length && to < 0; i++)
            {
                UserItem t = u.Trade[i];
                if (t != null && t.Info == item.Info && item.Info.StackSize > 1 && t.Count < item.Info.StackSize && !ItemMoves.IsLocked(MirGridType.Trade, i)) to = i;
            }
            for (int i = 0; i < u.Trade.Length && to < 0; i++) if (u.Trade[i] == null && !ItemMoves.IsLocked(MirGridType.Trade, i)) to = i;
            if (to < 0) { Say(W.T(ClientTextKeys.ModernSocialTradeSlotsFull)); return true; }
            _note = string.Empty;
            ItemMoves.Select(MirGridType.Inventory, bagSlot);
            var e = new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0);
            ItemMoves.Click(ItemMoves.Slot(MirGridType.Trade, to), e);
            if (ItemMoves.IsSelected(MirGridType.Inventory, bagSlot)) ItemMoves.Selected = null; // refused: nothing stays picked
            return true;
        }

        /// <summary>A double-click on an offered item (the first click picked it): onto a matching bag stack with room, else the
        /// first free bag cell, through the classic "From Trade" logic.</summary>
        private void OnTradeCellClick(object sender, EventArgs e)
        {
            if (!(sender is ModernItemCell cell) || !(e is MouseEventArgs me) || me.Button != MouseButtons.Left || me.Clicks < 2) return;
            if (!_active || CMain.Shift || CMain.Ctrl || CMain.Alt || !ItemMoves.IsSelected(MirGridType.Trade, cell.Slot)) return;
            UserObject u = User;
            UserItem item = u?.Trade[cell.Slot];
            if (item?.Info == null) return;
            int to = -1, belt = Belt;
            for (int i = belt; i < u.Inventory.Length && to < 0; i++)
            {
                UserItem b = u.Inventory[i];
                if (b != null && b.Info == item.Info && item.Info.StackSize > 1 && b.Count < item.Info.StackSize && !ItemMoves.IsLocked(MirGridType.Inventory, i)) to = i;
            }
            for (int i = belt; i < u.Inventory.Length && to < 0; i++) if (u.Inventory[i] == null && !ItemMoves.IsLocked(MirGridType.Inventory, i)) to = i;
            if (to < 0) { Say(W.T(ClientTextKeys.ModernSocialBagFull)); return; }
            _note = string.Empty;
            ItemMoves.Click(ItemMoves.Slot(MirGridType.Inventory, to), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        }

        /// <summary>TradeDialog.GoldLabel: an amount up to your gold, then C.TradeGold (add-only: taken at once).</summary>
        private void AddGold()
        {
            if (!_active || GameScene.Gold == 0 || ItemMoves.Selected != null) return;
            ModernRoot.Instance?.Popups.Amount(W.T(ClientTextKeys.ModernSocialAddGoldTitle), -1, GameScene.Gold, amount =>
            {
                if (amount == 0 || !_active || User == null) return;
                amount = Math.Min(amount, GameScene.Gold);
                User.TradeGoldAmount += amount;
                User.TradeLocked = false; // the server unlocks both sides on any change (PlayerObject.TradeGold → TradeUnlock)
                Network.Enqueue(new C.TradeGold { Amount = amount });
                _note = string.Empty;
                _dirty = true;
            }, 0);
        }

        /// <summary>TradeDialog.ConfirmButton: C.TradeConfirm with the lock flipped (the server checks room when both locked).</summary>
        private void Confirm()
        {
            UserObject u = User;
            if (!_active || u == null) return;
            u.TradeLocked = !u.TradeLocked;
            Network.Enqueue(new C.TradeConfirm { Locked = u.TradeLocked });
            if (u.TradeLocked)
            {
                // the offers are final while you are confirmed (any change unlocks you): keep them for the summary
                _mine = (Snapshot(u.Trade), u.TradeGoldAmount);
                _guest = (Snapshot(Model.GuestItems), Model.GuestGold);
            }
            _note = string.Empty;
            _dirty = true;
        }

        private static List<UserItem> Snapshot(UserItem[] items)
        {
            var list = new List<UserItem>();
            if (items != null) for (int i = 0; i < items.Length; i++) if (items[i] != null) list.Add(items[i]);
            return list;
        }
    }
}
