// Ported from WebClient/src/ui/modern/windows/rent.ts (RentSession + RentWindow): the new UI's item rental window. Desktop
// counterparts: ItemRentalDialog (the rented-out list), ItemRentingDialog / GuestItemRentingDialog (the item + period),
// ItemRentDialog / GuestItemRentDialog (the fee) and GameScene's rental handlers (MirScenes/Dialogs/ItemRent*.cs).
// Server rules (PlayerObject.ItemRentalRequest ... ConfirmItemRental): a rental is always started by the LENDER, facing the
// borrower (C.ItemRentalRequest pairs you with the player you face); a lender has at most 3 items out and a player rents one
// item at a time; the lender puts the item, sets 1 to 30 days and locks it; the borrower adds the fee in gold (each amount
// leaves the bag at once, back if the deal is cancelled) and locks it; then only the lender confirms.
//  - opens on S.ItemRentalRequest (ModernUi.ItemRentalRequest, with the bag) on the Deal tab; your side comes from the server
//    (Renting = you borrow); closing the window while a deal is open cancels it (C.CancelItemRental, the classic close
//    buttons); the server's cancel / confirm (ModernUi.ItemRentalReset) ends the Deal tab and resets the deal data like
//    ItemRentingDialog.Reset / ItemRentDialog.Reset (Models.Rental items, User.RentalGoldAmount, the period);
//  - every action is the classic request: the Renting cell is ModernItemCell(MirGridType.Renting) (click a bag item in the
//    deal's bag strip, or pick and drop: C.DepositRentalItem; Take back: C.RetrieveRentalItem through ItemMoves), the period
//    (slider + Set: C.ItemRentalPeriod; after a deposit with no period the amount box asks, as InputRentalPeroid), Lock item /
//    Lock fee (C.ItemRentalLockItem / LockFee), Confirm (C.ConfirmItemRental, enabled by S.CanConfirmItemRental);
//  - the fee only ADDS: C.ItemRentalFee carries the added amount (the server adds it and tells the partner the increment;
//    the classic dialog sent its running total, which the server added again), User.RentalGoldAmount keeps the total;
//  - Rented out = S.GetRentedItems (C.GetRentedItems at most once a minute) with the 3-item limit, plus your returned items
//    still rental-locked; Renting = your bag / worn items with rental information;
//  - KeybindOptions.Rental (no default key) toggles it (ModernUi.UpgradeKeyBind).
// HIDDEN (no data): the fee earned per rental, the simulated partner and the contract overlay.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    public enum RentMode
    {
        None,
        Lend,
        Borrow,
    }

    /// <summary>
    /// The rental deal and list outside the window (rent.ts RentSession + the web Rental model): the partner's side (the
    /// classic Guest* dialogs' fields), your period, the confirm switch and the rented-out list. One per modern UI (ModernUi.Rent).
    /// </summary>
    public sealed class RentSession
    {
        public const int MaxOut = 3, MinDays = 1, MaxDays = 30, ListEveryMs = 60000;

        public RentMode Mode;
        public string GuestName = string.Empty;
        public uint GuestFee, GuestPeriod;
        public bool GuestItemLocked, GuestGoldLocked, CanConfirm;
        /// <summary>The lender's period (ItemRentingDialog.RentalPeriod), 0 = none sent.</summary>
        public uint RentalPeriod;
        public readonly List<ItemRentalInformation> Rented = new List<ItemRentalInformation>();
        public bool Listed, SelfCancel;
        private long _nextList;
        // fee additions sent but not yet taken: the total grows only when the server's S.LoseGold for that amount shows in
        // GameScene.Gold (PlayerObject.SetItemRentalFee refuses silently: locked fee, too little gold, no partner)
        public const int FeeAnswerMs = 4000;
        private readonly Queue<(uint Amount, long Until)> _pendingFees = new Queue<(uint, long)>();
        private uint _feeBaseGold;

        /// <summary>The fee amounts sent and not yet confirmed by the server.</summary>
        public uint PendingFee
        {
            get
            {
                uint n = 0;
                foreach (var f in _pendingFees) n += f.Amount;
                return n;
            }
        }

        public event Action Changed;
        /// <summary>The deal ended (cancelled?, were we the lender?).</summary>
        public event Action<bool, bool> Ended;

        public void Emit() => Changed?.Invoke();

        /// <summary>S.ItemRentalRequest: a deal opens (Renting = we borrow).</summary>
        public void Opened(S.ItemRentalRequest p)
        {
            Mode = p.Renting ? RentMode.Borrow : RentMode.Lend;
            GuestName = p.Name ?? string.Empty;
            GuestFee = GuestPeriod = 0;
            GuestItemLocked = GuestGoldLocked = CanConfirm = false;
            RentalPeriod = 0;
            SelfCancel = false;
            Emit();
        }

        /// <summary>ItemRentingDialog.Reset + ItemRentDialog.Reset + the Guest dialogs' Reset (the data half).</summary>
        public void Reset(bool cancelled)
        {
            bool wasLend = Mode == RentMode.Lend;
            ItemMoves.SetItem(MirGridType.Renting, 0, null);
            ItemMoves.SetItem(MirGridType.GuestRenting, 0, null);
            if (GameScene.User != null) GameScene.User.RentalGoldAmount = 0;
            _pendingFees.Clear();
            RentalPeriod = 0;
            GuestFee = GuestPeriod = 0;
            GuestItemLocked = GuestGoldLocked = CanConfirm = false;
            Mode = RentMode.None;
            GameScene.Models?.Rental.Changed.Emit();
            Ended?.Invoke(cancelled, wasLend);
            Emit();
        }

        /// <summary>ItemRentalDialog.RequestRentedItems (at most once a minute).</summary>
        public void RequestList()
        {
            if (CMain.Time < _nextList) return;
            _nextList = CMain.Time + ListEveryMs;
            Network.Enqueue(new C.GetRentedItems());
        }

        public void List(List<ItemRentalInformation> items)
        {
            Rented.Clear();
            if (items != null)
                for (int i = 0; i < items.Count; i++) if (items[i] != null) Rented.Add(items[i]);
            Listed = true;
            Emit();
        }

        /// <summary>Set period: C.ItemRentalPeriod (1-30); false = refused.</summary>
        public bool SetPeriod(uint days)
        {
            if (Mode != RentMode.Lend || days < MinDays || days > MaxDays || (GameScene.User?.RentalItemLocked ?? false)) return false;
            RentalPeriod = days;
            Network.Enqueue(new C.ItemRentalPeriod { Days = days });
            Emit();
            return true;
        }

        /// <summary>
        /// Add to the fee: C.ItemRentalFee with the added amount. The running total in User.RentalGoldAmount grows when the server
        /// takes the gold (<see cref="Poll"/>), not on the click.
        /// </summary>
        public bool AddFee(uint amount)
        {
            if (Mode != RentMode.Borrow || amount == 0 || GameScene.User == null || GameScene.User.RentalGoldLocked) return false;
            uint pending = PendingFee;
            if ((ulong)amount + pending > GameScene.Gold) return false;
            if (_pendingFees.Count == 0) _feeBaseGold = GameScene.Gold;
            _pendingFees.Enqueue((amount, CMain.Time + FeeAnswerMs));
            Network.Enqueue(new C.ItemRentalFee { Amount = amount });
            Emit();
            return true;
        }

        /// <summary>
        /// Per frame from the rent window (cheap when nothing is pending): each pending addition is confirmed once GameScene.Gold
        /// has dropped by it since it was sent (S.LoseGold), in order; one with no drop within FeeAnswerMs is dropped.
        /// </summary>
        public void Poll()
        {
            if (_pendingFees.Count == 0) return;
            uint gold = GameScene.Gold;
            if (gold > _feeBaseGold) _feeBaseGold = gold; // gold came in meanwhile: measure the drop from the new level
            bool changed = false;
            while (_pendingFees.Count > 0)
            {
                var f = _pendingFees.Peek();
                if (_feeBaseGold - gold >= f.Amount)
                {
                    _pendingFees.Dequeue();
                    _feeBaseGold -= f.Amount;
                    if (GameScene.User != null) GameScene.User.RentalGoldAmount += f.Amount;
                    changed = true;
                    continue;
                }
                if (CMain.Time < f.Until) break;
                _pendingFees.Dequeue();
                changed = true;
            }
            if (changed) Emit();
        }

        public void Cancel()
        {
            if (Mode == RentMode.None) return;
            SelfCancel = true;
            Network.Enqueue(new C.CancelItemRental());
        }
    }

    public sealed class RentWindow : ModernWindow
    {
        public const string WindowKey = "rent";
        private const int WidthHud = 960, HeightHud = 560, RulesW = 230, MainW = WidthHud - RulesW, SideW = (MainW - 32 - 40) / 2;
        private static readonly uint[] FeeSteps = { 1000, 10000, 100000 };
        private static readonly (BindMode Flag, ClientTextKeys Label)[] Bound =
        {
            (BindMode.DontDrop, ClientTextKeys.ModernUpBoundDrop), (BindMode.DontStore, ClientTextKeys.ModernUpBoundStore), (BindMode.DontSell, ClientTextKeys.ModernUpBoundSell),
            (BindMode.DontTrade, ClientTextKeys.ModernUpBoundTrade), (BindMode.UnableToRent, ClientTextKeys.ModernUpBoundRent), (BindMode.DontUpgrade, ClientTextKeys.ModernUpBoundUpgrade),
            (BindMode.UnableToDisassemble, ClientTextKeys.ModernUpBoundDisassemble),
        };

        private readonly RentSession _session;
        private int _tab = 1; // 0 deal, 1 rented out, 2 renting
        private string _note = string.Empty;
        private bool _noteErr, _dirty = true;
        private string _listKey = string.Empty, _inKey = string.Empty;
        private long _nextTick, _nextPoll;
        private uint _gold;

        private Tabs _tabs;
        private ModernLabel _goldText;
        private ModernPanel _deskView, _outView, _inView;
        private ScrollView _outList, _inList;
        // deal
        private ModernLabel _roleText, _itemHead, _itemName, _itemSub, _bagCaption, _daysText, _backText, _feeHead, _feeText, _feeSub, _feeNote, _msg;
        private PaintBox _itemCard, _feeCard, _who;
        private ModernItemCell _rentCell, _guestCell;
        private BagColumn _bag;
        private Slider _range;
        private ModernButton _setPeriodBtn, _takeBackBtn, _lockItemBtn, _lockFeeBtn, _confirmBtn, _cancelBtn;
        private readonly List<ModernButton> _addBtns = new List<ModernButton>();
        private bool _itemLockedShown, _feeLockedShown;
        private readonly List<PaintBox> _tickRows = new List<PaintBox>();

        private readonly GridWatch _bagWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action<bool, bool> _ended;
        private readonly Action<UserItem> _itemInfo;

        public RentWindow(RentSession session) : base(WindowKey, W.T(ClientTextKeys.ModernUpRentTitle), 20, 24)
        {
            _session = session;
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.Renting || k.Grid == MirGridType.GuestRenting) _dirty = true; };
            _ended = DealEnded;
            _itemInfo = _ => { _listKey = _inKey = string.Empty; _dirty = true; };
            _session.Ended += _ended; // the window may be closed when the deal ends (it opens on the right tab next time)
        }

        internal int TabShown => _tab;
        internal BagColumn Bag => _bag;
        internal ModernItemCell RentCell => _rentCell;
        internal Slider Range => _range;
        internal ModernButton SetPeriodButton => _setPeriodBtn;
        internal ModernButton LockItemButton => _lockItemBtn;
        internal ModernButton LockFeeButton => _lockFeeBtn;
        internal ModernButton ConfirmButton => _confirmBtn;
        internal ModernButton CancelButton => _cancelBtn;
        internal IReadOnlyList<ModernButton> AddButtons => _addBtns;
        internal string Note => _note;

        protected override void Build()
        {
            const int top = TitleHeight;
            _tabs = new Tabs(new[] { W.T(ClientTextKeys.ModernUpDealTab), W.T(ClientTextKeys.ModernUpRentedOutTab), W.T(ClientTextKeys.ModernUpRentingTab) }, _tab, 420)
            { Parent = this, Location = new Point(S(12), S(top + 6)) };
            _tabs.Changed += i => { _tab = i; _dirty = true; };
            W.Label(this, W.T(ClientTextKeys.ModernUpGoldLabel), MainW - 40, top + 12, 50, 16, 12, false, ModernTheme.Dim, StringAlignment.Far);
            _goldText = W.Label(this, string.Empty, MainW + 12, top + 12, RulesW - 24, 16, 12, true, ModernTheme.Gold);
            int body = top + 38;
            W.Divider(this, 0, body, WidthHud);
            int bh = HeightHud - 38;

            // ---- deal
            _deskView = new ModernPanel { Parent = this, Location = new Point(0, S(body + 1)), Size = new Size(S(MainW), S(bh - 1)) };
            MirControl d = _deskView;
            W.Caption(d, W.T(ClientTextKeys.ModernUpDealWith), 16, 18, 80);
            _who = new PaintBox(PaintWho) { Parent = d, Location = new Point(S(96), S(8)), Size = new Size(S(170), S(36)), NotControl = true };
            _roleText = W.Label(d, string.Empty, 278, 10, MainW - 290, 32, 12, false, ModernTheme.Dim, StringAlignment.Near, true);
            int sy = 54, sh = bh - sy - 88;
            int ix = 16, fx = 16 + SideW + 40;
            _itemCard = new PaintBox((g, b) => PaintSide(g, b, _itemLockedShown)) { Parent = d, Location = new Point(S(ix), S(sy)), Size = new Size(S(SideW), S(sh)), NotControl = true };
            _feeCard = new PaintBox((g, b) => PaintSide(g, b, _feeLockedShown)) { Parent = d, Location = new Point(S(fx), S(sy)), Size = new Size(S(SideW), S(sh)), NotControl = true };
            W.Label(d, "⇄", 16 + SideW, sy + sh / 2 - 14, 40, 28, 22, false, ModernTheme.Accent, StringAlignment.Center);
            // item side
            _itemHead = W.Label(d, string.Empty, ix + 12, sy + 10, SideW - 24, 16, 12, true, ModernTheme.Accent);
            _rentCell = new ModernItemCell(MirGridType.Renting, 0, 56) { Parent = d, Location = new Point(S(ix + 12), S(sy + 34)) };
            _guestCell = new ModernItemCell(MirGridType.GuestRenting, 0, 56) { Parent = d, Location = new Point(S(ix + 12), S(sy + 34)), ReadOnly = true };
            _itemName = W.Label(d, string.Empty, ix + 78, sy + 40, SideW - 90, 20, 15, true, Color.White);
            _itemSub = W.Label(d, string.Empty, ix + 78, sy + 62, SideW - 90, 16, 11, false, ModernTheme.Dim);
            _bagCaption = W.Caption(d, W.T(ClientTextKeys.ModernUpFromYourBag), ix + 12, sy + 100, SideW - 24);
            _bag = new BagColumn(d, ix + 8, sy + 118, 7, 38, 116, Deposit, it => it.Info == null || !it.Info.Bind.HasFlag(BindMode.UnableToRent));
            _daysText = W.Label(d, string.Empty, ix + 12, sy + sh - 140, SideW - 24, 26, 20, true, Color.White);
            _backText = W.Label(d, string.Empty, ix + 12, sy + sh - 112, SideW - 24, 16, 11, false, ModernTheme.Dim);
            _range = new Slider(RentSession.MinDays, RentSession.MaxDays, 7, SideW - 24, 1, false) { Parent = d, Location = new Point(S(ix + 12), S(sy + sh - 88)) };
            _range.Changed += _ => _dirty = true;
            int bw = (SideW - 24 - 12) / 3;
            _setPeriodBtn = new ModernButton(W.T(ClientTextKeys.ModernUpSetPeriod)) { Parent = d, Location = new Point(S(ix + 12), S(sy + sh - 54)), Tooltip = W.T(ClientTextKeys.ModernUpSetPeriodHint) };
            _setPeriodBtn.Size = new Size(S(bw), S(30));
            _setPeriodBtn.Click += (o, e) => SetPeriod();
            _takeBackBtn = new ModernButton(W.T(ClientTextKeys.ModernUpTakeBack)) { Parent = d, Location = new Point(S(ix + 18 + bw), S(sy + sh - 54)), Tooltip = W.T(ClientTextKeys.ModernUpTakeBackRentHint) };
            _takeBackBtn.Size = new Size(S(bw), S(30));
            _takeBackBtn.Click += (o, e) => TakeBack();
            _lockItemBtn = new ModernButton(W.T(ClientTextKeys.ModernUpLockItem), ButtonKind.Main) { Parent = d, Location = new Point(S(ix + 24 + 2 * bw), S(sy + sh - 54)), Tooltip = W.T(ClientTextKeys.ModernUpLockItemHint) };
            _lockItemBtn.Size = new Size(S(bw), S(30));
            _lockItemBtn.Click += (o, e) => { if (GameScene.User != null && _session.Mode == RentMode.Lend && !GameScene.User.RentalItemLocked && _session.RentalPeriod >= RentSession.MinDays && _session.RentalPeriod <= RentSession.MaxDays) Network.Enqueue(new C.ItemRentalLockItem()); };
            // fee side
            _feeHead = W.Label(d, string.Empty, fx + 12, sy + 10, SideW - 24, 16, 12, true, ModernTheme.Accent);
            _feeText = W.Label(d, string.Empty, fx + 12, sy + 34, SideW - 24, 34, 26, true, ModernTheme.Gold);
            _feeSub = W.Label(d, string.Empty, fx + 12, sy + 70, SideW - 24, 16, 11, false, ModernTheme.Dim);
            int ax = fx + 12;
            for (int i = 0; i <= FeeSteps.Length; i++)
            {
                bool more = i == FeeSteps.Length;
                uint amount = more ? 0 : FeeSteps[i];
                var b = new ModernButton(more ? W.T(ClientTextKeys.ModernUpAddMore) : "+" + W.N(amount)) { Parent = d, Location = new Point(S(ax), S(sy + 98)), Tooltip = more ? W.T(ClientTextKeys.ModernUpAddMoreHint) : W.T(ClientTextKeys.ModernUpAddN, W.N(amount)) };
                b.AutoWidth(56);
                if (more) b.Click += (o, e) => AddFeeAsk();
                else b.Click += (o, e) => AddFee(amount);
                _addBtns.Add(b);
                ax = W.Right(b) + 4;
            }
            _feeNote = W.Label(d, W.T(ClientTextKeys.ModernUpFeeNote), fx + 12, sy + 130, SideW - 24, 46, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            _lockFeeBtn = new ModernButton(W.T(ClientTextKeys.ModernUpLockFee), ButtonKind.Main) { Parent = d, Location = new Point(S(fx + 12), S(sy + sh - 54)), Tooltip = W.T(ClientTextKeys.ModernUpLockFeeHint) };
            _lockFeeBtn.Size = new Size(S(SideW - 24), S(30));
            _lockFeeBtn.Click += (o, e) => { _session.Poll(); if (GameScene.User != null && _session.Mode == RentMode.Borrow && !GameScene.User.RentalGoldLocked && GameScene.User.RentalGoldAmount >= 1) Network.Enqueue(new C.ItemRentalLockFee()); };
            // confirm / cancel
            int cw = (MainW - 32 - 8) / 2;
            _confirmBtn = new ModernButton(W.T(ClientTextKeys.ModernUpConfirmRental), ButtonKind.Main) { Parent = d, Location = new Point(S(16), S(bh - 78)), Tooltip = W.T(ClientTextKeys.ModernUpConfirmRentalHint) };
            _confirmBtn.Size = new Size(S(cw), S(30));
            _confirmBtn.Click += (o, e) => { if (_session.Mode == RentMode.Lend && _session.CanConfirm) Network.Enqueue(new C.ConfirmItemRental()); };
            _cancelBtn = new ModernButton(W.T(ClientTextKeys.ModernUpCancelRental)) { Parent = d, Location = new Point(S(24 + cw), S(bh - 78)), Tooltip = W.T(ClientTextKeys.ModernUpCancelRentalHint) };
            _cancelBtn.Size = new Size(S(cw), S(30));
            _cancelBtn.Click += (o, e) => _session.Cancel();
            _msg = W.Label(d, string.Empty, 16, bh - 40, MainW - 32, 30, 12, false, ModernTheme.Dim, StringAlignment.Near, true);

            // ---- rented out / renting
            _outView = new ModernPanel { Parent = this, Location = new Point(0, S(body + 1)), Size = new Size(S(MainW), S(bh - 1)) };
            _outList = new ScrollView(MainW - 16, bh - 12) { Parent = _outView, Location = new Point(S(8), S(6)), Background = W.Page };
            _outList.Framed = false;
            _inView = new ModernPanel { Parent = this, Location = new Point(0, S(body + 1)), Size = new Size(S(MainW), S(bh - 1)) };
            _inList = new ScrollView(MainW - 16, bh - 12) { Parent = _inView, Location = new Point(S(8), S(6)), Background = W.Page };
            _inList.Framed = false;

            // ---- the rules column
            W.VDivider(this, MainW, body, bh);
            int ry = body + 12, rw = RulesW - 24;
            W.Caption(this, W.T(ClientTextKeys.ModernUpRentalRules), MainW + 12, ry, rw);
            ry += 20;
            string bound = string.Empty;
            for (int i = 0; i < Bound.Length; i++) bound += (i > 0 ? ", " : string.Empty) + W.T(Bound[i].Label).ToLowerInvariant();
            (ClientTextKeys Head, string Body)[] rules =
            {
                (ClientTextKeys.ModernUpRuleStartHead, W.T(ClientTextKeys.ModernUpRuleStart)),
                (ClientTextKeys.ModernUpRuleOutHead, W.T(ClientTextKeys.ModernUpRuleOut)),
                (ClientTextKeys.ModernUpRuleOneHead, W.T(ClientTextKeys.ModernUpRuleOne)),
                (ClientTextKeys.ModernUpRuleDaysHead, W.T(ClientTextKeys.ModernUpRuleDays)),
                (ClientTextKeys.ModernUpRuleBoundHead, bound + "."),
                (ClientTextKeys.ModernUpRuleBackHead, W.T(ClientTextKeys.ModernUpRuleBack)),
            };
            for (int i = 0; i < rules.Length; i++)
            {
                string head = W.T(rules[i].Head), text = rules[i].Body;
                Font f = ModernSkin.Font(11);
                int h = 14 + (int)Math.Ceiling(ModernSkin.MeasureWrapped(head + " " + text, f, S(rw - 16)).Height / ModernRoot.K);
                new PaintBox((g, b) => PaintRule(g, b, head, text)) { Parent = this, Location = new Point(S(MainW + 12), S(ry)), Size = new Size(S(rw), S(h)), NotControl = true };
                ry += h + 6;
            }

            _listKey = _inKey = string.Empty;
            _bagWatch.Reset();
            _dirty = true;
            Render();
        }

        // ------------------------------------------------------------------ open / close

        /// <summary>S.ItemRentalRequest: the Deal tab.</summary>
        public void ShowDeal()
        {
            _tab = 0;
            _note = string.Empty;
            _noteErr = false;
            if (_range != null) _range.Set(7);
            _dirty = true;
        }

        /// <summary>The deal ended (cancelled or done): back to the list it belongs to.</summary>
        private void DealEnded(bool cancelled, bool wasLend)
        {
            _tab = wasLend ? 1 : 2;
            _note = W.T(cancelled ? ClientTextKeys.ModernUpRentalCancelled : ClientTextKeys.ModernUpRentalDone);
            _noteErr = cancelled;
            if (!cancelled && wasLend) _session.RequestList();
            _listKey = string.Empty;
            _dirty = true;
        }

        /// <summary>ItemRentingDialog.InputRentalPeroid after a deposit with no period: the amount box (1-30 days).</summary>
        public void AskPeriod()
        {
            _tab = 0;
            _dirty = true;
            UserItem item = GameScene.Models?.Rental.RentalItem;
            Popups pop = Up.Pop;
            if (pop == null || item == null) return;
            pop.Amount(W.T(ClientTextKeys.RentItemDurationQuestion, item.FriendlyName, _session.GuestName), item.Image, RentSession.MaxDays, days =>
            {
                if (_session.SetPeriod(days) && _range != null) _range.Set((int)days);
                _dirty = true;
            }, (uint)Math.Clamp(_range?.Value ?? 7, RentSession.MinDays, RentSession.MaxDays), RentSession.MinDays);
        }

        protected override void Shown()
        {
            _session.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.ItemInfo.On(_itemInfo);
            ModernEvents.InventoryResized.On(_changed);
            GameScene.Models?.Rental.Changed.On(_changed);
            _session.RequestList(); // ItemRentalDialog.Toggle → RequestRentedItems
            if (_session.Mode != RentMode.None) _tab = 0;
            else if (_tab == 0) _tab = 1;
            _listKey = _inKey = string.Empty;
            _dirty = true;
        }

        /// <summary>Every close path while a deal is open cancels it (the deal dialogs' close buttons: C.CancelItemRental).</summary>
        protected override void Closed()
        {
            _session.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.ItemInfo.Off(_itemInfo);
            ModernEvents.InventoryResized.Off(_changed);
            GameScene.Models?.Rental.Changed.Off(_changed);
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Renting) ItemMoves.Selected = null;
            if (_session.Mode != RentMode.None) _session.Cancel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _session.Ended -= _ended;
            base.Dispose(disposing);
        }

        public override void Process()
        {
            base.Process();
            if (Up.User == null) return;
            _session.Poll();
            if (CMain.Time >= _nextPoll)
            {
                _nextPoll = CMain.Time + GridWatch.IntervalMs;
                if (_gold != GameScene.Gold) { _gold = GameScene.Gold; _dirty = true; }
                if (_bagWatch.Poll(MirGridType.Inventory, Up.User.Inventory, 0, int.MaxValue, true)) { _dirty = true; _inKey = string.Empty; }
                if (_lockSig != LockSig()) { _lockSig = LockSig(); _dirty = true; }
            }
            if (_dirty) Render();
            // countdowns: once a second, only while shown
            if (_tab != 0 && CMain.Time >= _nextTick)
            {
                _nextTick = CMain.Time + 1000;
                for (int i = 0; i < _tickRows.Count; i++) _tickRows[i].Invalidate();
            }
        }

        private int _lockSig = -1;
        private static int LockSig() => GameScene.User == null ? 0 : (GameScene.User.RentalGoldLocked ? 1 : 0) | (GameScene.User.RentalItemLocked ? 2 : 0);

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_tabs == null || _tabs.IsDisposed || Up.User == null) return;
            RentMode mode = _session.Mode;
            if (mode == RentMode.None && _tab == 0) _tab = 1;
            if (_tabs.Active != _tab) _tabs.Select(_tab);
            _tabs.Badge(1, _session.Rented.Count);
            _tabs.Badge(2, RentingItems().Count);
            Up.SetText(_goldText, W.N(GameScene.Gold));
            Up.Show(_deskView, _tab == 0);
            Up.Show(_outView, _tab == 1);
            Up.Show(_inView, _tab == 2);
            if (_tab == 0) RenderDeal(mode);
            else if (_tab == 1) RenderOut();
            else RenderIn();
        }

        private void RenderDeal(RentMode mode)
        {
            UserObject u = Up.User;
            var rental = GameScene.Models.Rental;
            bool lend = mode == RentMode.Lend;
            _who.Invalidate();
            Up.SetText(_roleText, W.T(lend ? ClientTextKeys.ModernUpRoleLend : ClientTextKeys.ModernUpRoleBorrow));

            // item side
            UserItem item = lend ? rental.RentalItem : rental.GuestLoanItem;
            if (!lend && item != null && item.Info == null) GameScene.Bind(item); // GuestItemRentingDialog.RefreshInterface
            ItemInfo info = item?.Info;
            bool itemLocked = lend ? u.RentalItemLocked : _session.GuestItemLocked;
            uint period = lend ? _session.RentalPeriod : _session.GuestPeriod;
            Up.SetText(_itemHead, lend ? W.T(ClientTextKeys.ModernUpYouLend) : W.T(ClientTextKeys.ModernUpGuestLends, _session.GuestName));
            if (_itemLockedShown != itemLocked) { _itemLockedShown = itemLocked; _itemCard.Invalidate(); }
            Up.Show(_rentCell, lend);
            Up.Show(_guestCell, !lend);
            _rentCell.Refresh(false); // S.UpdateRentalItem sets Models.Rental without ItemMoves.SlotChanged
            _guestCell.Refresh(false);
            Up.SetText(_itemName, item != null ? Up.Name(item) : W.T(lend ? ClientTextKeys.ModernUpPickItem : ClientTextKeys.ModernUpNoItemYet));
            _itemName.Colour = info != null ? Up.Grade(info.Grade) : Color.White;
            Up.SetText(_itemSub, info != null ? (Up.GradeName(info.Grade) + (item.Count > 1 ? " · " + W.N(item.Count) : string.Empty)).Trim(' ', '·')
                : W.T(lend ? ClientTextKeys.ModernUpPickItemHint : ClientTextKeys.ModernUpWaitingLender));
            bool pickBag = lend && !itemLocked && rental.RentalItem == null;
            Up.Show(_bagCaption, pickBag);
            Up.Show(_bag.View, pickBag);
            if (pickBag) _bag.Sync();
            Up.Show(_range, lend && !itemLocked);
            uint days = lend ? (itemLocked ? period : (uint)_range.Value) : period;
            if (days == 0)
            {
                Up.SetText(_daysText, W.T(ClientTextKeys.ModernUpNoPeriod));
                Up.SetText(_backText, lend ? W.T(ClientTextKeys.ModernUpOneTo30) : string.Empty);
            }
            else
            {
                bool set = !lend || days == _session.RentalPeriod;
                Up.SetText(_daysText, W.T(ClientTextKeys.ModernUpDaysN, days) + (set ? string.Empty : " " + W.T(ClientTextKeys.ModernUpNotSent)));
                Up.SetText(_backText, W.T(ClientTextKeys.ModernUpBackAround, CMain.Now.AddDays(days).ToString("d")));
            }
            Up.Show(_setPeriodBtn, lend);
            Up.Show(_takeBackBtn, lend);
            Up.Show(_lockItemBtn, lend);
            if (lend)
            {
                _setPeriodBtn.SetDisabled(itemLocked || days == _session.RentalPeriod, W.T(itemLocked ? ClientTextKeys.ModernUpItemIsLocked : ClientTextKeys.ModernUpPeriodSet));
                bool canTake = rental.RentalItem != null && !itemLocked && !ItemMoves.IsLocked(MirGridType.Renting, 0);
                _takeBackBtn.SetDisabled(!canTake, itemLocked ? W.T(ClientTextKeys.UnableRemoveLockedItem) : string.Empty);
                string lockWhy = itemLocked ? W.T(ClientTextKeys.ModernUpLocked) : rental.RentalItem == null ? W.T(ClientTextKeys.ModernUpPutItemFirst)
                    : _session.RentalPeriod < RentSession.MinDays ? W.T(ClientTextKeys.ModernUpSetPeriodFirst) : string.Empty;
                _lockItemBtn.SetDisabled(lockWhy.Length > 0, lockWhy);
                _lockItemBtn.Text = W.T(itemLocked ? ClientTextKeys.ModernUpItemLocked : ClientTextKeys.ModernUpLockItem);
            }

            // fee side
            uint fee = lend ? _session.GuestFee : u.RentalGoldAmount;
            bool feeLocked = lend ? _session.GuestGoldLocked : u.RentalGoldLocked;
            Up.SetText(_feeHead, lend ? W.T(ClientTextKeys.ModernUpGuestPays, _session.GuestName) : W.T(ClientTextKeys.ModernUpYouPay));
            if (_feeLockedShown != feeLocked) { _feeLockedShown = feeLocked; _feeCard.Invalidate(); }
            Up.SetText(_feeText, W.T(ClientTextKeys.ModernUpGoldN, W.N(fee)));
            Up.SetText(_feeSub, fee > 0 && period > 0 ? W.T(ClientTextKeys.ModernUpPerDay, W.N((long)Math.Round(fee / (double)period)))
                : W.T(lend ? ClientTextKeys.ModernUpWaitingOffer : ClientTextKeys.ModernUpAddGoldBelow));
            for (int i = 0; i < _addBtns.Count; i++) Up.Show(_addBtns[i], !lend);
            Up.Show(_feeNote, !lend);
            Up.Show(_lockFeeBtn, !lend);
            if (!lend)
            {
                for (int i = 0; i < FeeSteps.Length; i++)
                    _addBtns[i].SetDisabled(feeLocked || GameScene.Gold < FeeSteps[i], W.T(feeLocked ? ClientTextKeys.ModernUpFeeIsLocked : ClientTextKeys.LowGold));
                _addBtns[FeeSteps.Length].SetDisabled(feeLocked || GameScene.Gold == 0, W.T(feeLocked ? ClientTextKeys.ModernUpFeeIsLocked : ClientTextKeys.LowGold));
                string lockWhy = feeLocked ? W.T(ClientTextKeys.ModernUpLocked) : fee < 1 ? W.T(ClientTextKeys.ModernUpAddFeeFirst) : string.Empty;
                _lockFeeBtn.SetDisabled(lockWhy.Length > 0, lockWhy);
                _lockFeeBtn.Text = W.T(feeLocked ? ClientTextKeys.ModernUpFeeLocked : ClientTextKeys.ModernUpLockFee);
            }

            // confirm (the lender only, after S.CanConfirmItemRental)
            Up.Show(_confirmBtn, lend);
            _confirmBtn.SetDisabled(!_session.CanConfirm, W.T(ClientTextKeys.ModernUpBothLocked));
            string msg = _note;
            bool err = _noteErr;
            if (msg.Length == 0)
            {
                if (lend && _session.Listed && _session.Rented.Count >= RentSession.MaxOut) { msg = W.T(ClientTextKeys.ModernUpThreeOut, RentSession.MaxOut); err = true; }
                else if (!lend) msg = itemLocked && feeLocked ? W.T(ClientTextKeys.ModernUpWaitingConfirm, _session.GuestName) : W.T(ClientTextKeys.ModernUpOnlyLenderConfirms);
                else if (_session.CanConfirm) msg = W.T(ClientTextKeys.ModernUpBothLockedConfirm);
            }
            Up.SetText(_msg, msg);
            _msg.Colour = err ? ModernTheme.BadSoft : ModernTheme.Dim;
        }

        /// <summary>Rented out: S.GetRentedItems (3 slots) + your own items back from a rental and still rental-locked.</summary>
        private void RenderOut()
        {
            var list = _session.Rented;
            List<UserItem> back = LockedBack();
            string key = _session.Listed + "|" + _session.Mode + "|" + _note + "|";
            for (int i = 0; i < list.Count; i++) key += list[i].ItemId + ":" + list[i].ItemReturnDate.Ticks + ":" + list[i].RentingPlayerName + ",";
            for (int i = 0; i < back.Count; i++) key += back[i].UniqueID + ",";
            if (key == _listKey) return;
            _listKey = key;
            ClearList(_outList);
            MirControl c = _outList.Content;
            int w = MainW - 36, y = 4;
            int n = list.Count;
            W.Label(c, W.T(ClientTextKeys.ModernUpRentedOut), 4, y + 6, 90, 16, 12, false, ModernTheme.Dim);
            for (int i = 0; i < RentSession.MaxOut; i++)
            {
                bool on = i < n;
                new PaintBox((g, b) =>
                {
                    using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f), b.Size.Height / 2f);
                    using (var br = new SolidBrush(on ? ModernTheme.Accent : ModernTheme.Field)) g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, ModernTheme.Frame);
                }) { Parent = c, Location = new Point(S(96 + i * 52), S(y + 10)), Size = new Size(S(46), S(8)), NotControl = true };
            }
            W.Label(c, _session.Listed ? n + "/" + RentSession.MaxOut : "…", 256, y + 6, 40, 16, 12, true, ModernTheme.Text);
            var rentOut = new ModernButton(W.T(ClientTextKeys.ModernUpRentOut), ButtonKind.Main) { Parent = c, Tooltip = W.T(ClientTextKeys.ModernUpRentOutHint) };
            rentOut.AutoWidth(130);
            rentOut.Location = new Point(S(w) - rentOut.Size.Width, S(y));
            bool full = _session.Listed && n >= RentSession.MaxOut;
            rentOut.SetDisabled(_session.Mode != RentMode.None || full, _session.Mode != RentMode.None ? W.T(ClientTextKeys.ModernUpRentalOpen) : W.T(ClientTextKeys.ModernUpThreeOutShort, RentSession.MaxOut));
            rentOut.Click += (o, e) => Request();
            y += 34;
            if (_note.Length > 0)
            {
                W.Label(c, _note, 4, y, w, 16, 12, false, _noteErr ? ModernTheme.BadSoft : ModernTheme.Dim);
                y += 20;
            }
            W.Label(c, W.T(ClientTextKeys.ModernUpRentOutNote), 4, y, w, 30, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            y += 36;
            _tickRows.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                ItemRentalInformation r = list[i];
                Card(c, y, w, r.ItemName, Color.White, W.T(ClientTextKeys.ModernUpRentedTag), ModernTheme.Accent, W.T(ClientTextKeys.ModernUpToPlayer, r.RentingPlayerName), null,
                    r.ItemReturnDate, W.T(ClientTextKeys.ModernUpBackIn), W.T(ClientTextKeys.ModernUpDueBack), true);
                y += 66;
            }
            if (_session.Listed)
                for (int i = n; i < RentSession.MaxOut; i++) { EmptyCard(c, y, w, W.T(ClientTextKeys.ModernUpFreeSlot)); y += 50; }
            else { EmptyCard(c, y, w, W.T(ClientTextKeys.ModernUpAskingList)); y += 50; }
            if (back.Count > 0)
            {
                W.Caption(c, W.T(ClientTextKeys.ModernUpBackFromRental), 4, y + 4, w);
                y += 24;
                for (int i = 0; i < back.Count; i++)
                {
                    UserItem it = back[i];
                    Card(c, y, w, Up.Name(it), it.Info != null ? Up.Grade(it.Info.Grade) : Color.White, W.T(ClientTextKeys.ModernUpReturnedTag), ModernTheme.Warn, string.Empty, null,
                        it.RentalInformation.ExpiryDate, W.T(ClientTextKeys.ModernUpRentAgainIn), W.T(ClientTextKeys.ModernUpRentAgain), false);
                    y += 66;
                }
            }
            _outList.ContentHeight = S(y + 4);
        }

        /// <summary>Renting: your bag / worn items with rental information (not the rental-locked ones you own).</summary>
        private void RenderIn()
        {
            List<UserItem> items = RentingItems();
            string key = string.Empty;
            for (int i = 0; i < items.Count; i++) key += items[i].UniqueID + ":" + items[i].RentalInformation.ExpiryDate.Ticks + ":" + (int)items[i].RentalInformation.BindingFlags + ":" + (items[i].Info != null) + ",";
            if (key == _inKey) return;
            _inKey = key;
            ClearList(_inList);
            MirControl c = _inList.Content;
            int w = MainW - 36, y = 4;
            _tickRows.Clear();
            if (items.Count == 0) { EmptyCard(c, y, w, W.T(ClientTextKeys.ModernUpNotRenting)); y += 50; }
            for (int i = 0; i < items.Count; i++)
            {
                UserItem it = items[i];
                RentalInformation rent = it.RentalInformation;
                string chips = string.Empty;
                for (int j = 0; j < Bound.Length; j++) if (rent.BindingFlags.HasFlag(Bound[j].Flag)) chips += (chips.Length > 0 ? "|" : string.Empty) + W.T(Bound[j].Label);
                Card(c, y, w, Up.Name(it), it.Info != null ? Up.Grade(it.Info.Grade) : Color.White, string.Empty, Color.Empty, W.T(ClientTextKeys.ModernUpRentedFrom, rent.OwnerName), chips,
                    rent.ExpiryDate, string.Empty, W.T(ClientTextKeys.ModernUpRentalExpired), true);
                y += 86;
            }
            if (items.Count > 0) { W.Label(c, W.T(ClientTextKeys.ModernUpRentingNote), 4, y, w, 16, 11, false, ModernTheme.Dim); y += 20; }
            _inList.ContentHeight = S(y + 4);
        }

        private static void ClearList(ScrollView list)
        {
            for (int i = list.Content.Controls.Count - 1; i >= 0; i--) list.Content.Controls[i].Dispose();
        }

        private void EmptyCard(MirControl parent, int y, int w, string text)
        {
            new PaintBox((g, b) =>
            {
                W.Card(g, b.Size, null, 0.04f, true);
                ModernSkin.Text(g, text, ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width, b.Size.Height), StringAlignment.Center);
            }) { Parent = parent, Location = new Point(S(4), S(y)), Size = new Size(S(w), S(44)), NotControl = true };
        }

        /// <summary>A list card (.card): name + status tag, a sub line, binding chips, and a live countdown on the right.</summary>
        private void Card(MirControl parent, int y, int w, string name, Color nameColour, string tag, Color tagColour, string sub, string chips, DateTime until,
            string prefix, string done, bool returns)
        {
            int h = chips != null ? 80 : 60;
            var card = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                W.Card(g, b.Size, null, 0.04f);
                float rx = b.Size.Width - 230 * k;
                ModernSkin.Text(g, name, ModernSkin.Font(14, true), nameColour, new RectangleF(12 * k, 8 * k, rx - 20 * k, 18 * k));
                if (tag.Length > 0)
                {
                    float nw = Math.Min(rx - 90 * k, ModernSkin.Measure(name, ModernSkin.Font(14, true)));
                    Font tf = ModernSkin.Font(9, true);
                    float tw = ModernSkin.Measure(tag, tf) + 12 * k;
                    RectangleF tr = new RectangleF(18 * k + nw, 10 * k, tw, 14 * k);
                    using GraphicsPath tp = ModernSkin.RoundRect(tr, 7 * k);
                    ModernSkin.Stroke(g, tp, tagColour);
                    ModernSkin.Text(g, tag, tf, tagColour, tr, StringAlignment.Center);
                }
                if (sub.Length > 0) ModernSkin.Text(g, sub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(12 * k, 28 * k, rx - 20 * k, 14 * k));
                if (!string.IsNullOrEmpty(chips))
                {
                    float cx = 12 * k;
                    Font cf = ModernSkin.Font(10);
                    foreach (string chip in chips.Split('|'))
                    {
                        float cw = ModernSkin.Measure(chip, cf) + 16 * k;
                        if (cx + cw > rx - 8 * k) break;
                        RectangleF cr = new RectangleF(cx, 48 * k, cw, 18 * k);
                        using GraphicsPath cp = ModernSkin.RoundRect(cr, 9 * k);
                        using (var cb = new SolidBrush(W.Over(ModernTheme.Bad, 0.1f))) g.FillPath(cb, cp);
                        ModernSkin.Stroke(g, cp, W.Over(ModernTheme.Bad, 0.35f));
                        ModernSkin.Text(g, chip, cf, ModernTheme.Hex("#ffb3a6"), cr, StringAlignment.Center);
                        cx += cw + 5 * k;
                    }
                }
                double left = (until - CMain.Now).TotalSeconds;
                string countdown = left > 0 ? (prefix.Length > 0 ? string.Format(prefix, W.Duration((long)left)) : W.Duration((long)left)) : done;
                ModernSkin.Text(g, countdown, ModernSkin.Font(11), ModernTheme.Text, new RectangleF(rx, 10 * k, 220 * k, 16 * k));
                if (returns) ModernSkin.Text(g, W.T(ClientTextKeys.ModernUpReturns, until.ToString("g")), ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(rx, 28 * k, 220 * k, 16 * k));
            }) { Parent = parent, Location = new Point(S(4), S(y)), Size = new Size(S(w), S(h)), NotControl = true };
            _tickRows.Add(card);
        }

        /// <summary>Items you are renting (UserItem.RentalInformation with RentalLocked false).</summary>
        private static List<UserItem> RentingItems()
        {
            var list = new List<UserItem>();
            UserObject u = Up.User;
            if (u == null) return list;
            foreach (UserItem[] arr in new[] { u.Inventory, u.Equipment })
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                        if (arr[i]?.RentalInformation != null && !arr[i].RentalInformation.RentalLocked) list.Add(arr[i]);
            return list;
        }

        /// <summary>Your items back from a rental and still locked from renting (RentalLocked, expiry in the future).</summary>
        private static List<UserItem> LockedBack()
        {
            var list = new List<UserItem>();
            UserObject u = Up.User;
            if (u == null) return list;
            foreach (UserItem[] arr in new[] { u.Inventory, u.Equipment })
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                        if (arr[i]?.RentalInformation != null && arr[i].RentalInformation.RentalLocked && arr[i].RentalInformation.ExpiryDate > CMain.Now) list.Add(arr[i]);
            return list;
        }

        // ------------------------------------------------------------------ painting

        private void PaintWho(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
            using (var bg = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(bg, p);
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
            string name = _session.GuestName;
            RectangleF av = new RectangleF(6 * k, 5 * k, 26 * k, 26 * k);
            using (var pen = new Pen(ModernTheme.Accent, 1f)) g.DrawEllipse(pen, av);
            ModernSkin.Text(g, name.Length >= 2 ? name.Substring(0, 2).ToUpperInvariant() : name.ToUpperInvariant(), ModernSkin.Font(9, true), ModernTheme.Text, av, StringAlignment.Center);
            ModernSkin.Text(g, name, ModernSkin.Font(12, true), Color.White, new RectangleF(38 * k, 3 * k, b.Size.Width - 44 * k, 16 * k));
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernUpFacingYou), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(38 * k, 18 * k, b.Size.Width - 44 * k, 14 * k));
        }

        private static void PaintSide(Graphics g, PaintBox b, bool locked)
        {
            float k = ModernRoot.K;
            W.Card(g, b.Size, locked ? ModernTheme.Ok : (Color?)null, 0.04f);
            if (!locked) return;
            string tag = W.T(ClientTextKeys.ModernUpLockedTag);
            Font f = ModernSkin.Font(9, true);
            float tw = ModernSkin.Measure(tag, f) + 16 * k;
            RectangleF tr = new RectangleF(b.Size.Width - tw - 10 * k, 10 * k, tw, 16 * k);
            using GraphicsPath p = ModernSkin.RoundRect(tr, 8 * k);
            using (var br = new SolidBrush(ModernTheme.Ok)) g.FillPath(br, p);
            ModernSkin.Text(g, tag, f, Color.Black, tr, StringAlignment.Center);
        }

        private static void PaintRule(Graphics g, PaintBox b, string head, string text)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.4f));
            Font bold = ModernSkin.Font(11, true);
            float hw = ModernSkin.Measure(head + " ", bold);
            // the head in bold on the first line, the text wrapped after it
            ModernSkin.Text(g, head, bold, ModernTheme.Text, new RectangleF(8 * k, 6 * k, hw + 4 * k, 14 * k), StringAlignment.Near, StringAlignment.Near, false);
            string pad = new string(' ', Math.Max(1, (int)Math.Ceiling(hw / Math.Max(1f, ModernSkin.Measure(" ", ModernSkin.Font(11))))));
            ModernSkin.Text(g, pad + text, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(8 * k, 6 * k, b.Size.Width - 16 * k, b.Size.Height - 8 * k), StringAlignment.Near, StringAlignment.Near, false, true);
        }

        // ------------------------------------------------------------------ actions (nothing moves until the server answers)

        private void Say(string text, bool err = false)
        {
            _note = text ?? string.Empty;
            _noteErr = err;
            _listKey = string.Empty;
            _dirty = true;
        }

        /// <summary>"Rent out an item": C.ItemRentalRequest (the server pairs you with the player you face; refusals come as chat).</summary>
        private void Request()
        {
            if (_session.Mode != RentMode.None) return;
            if (_session.Listed && _session.Rented.Count >= RentSession.MaxOut) { Say(W.T(ClientTextKeys.ModernUpThreeOutShort, RentSession.MaxOut), true); return; }
            Network.Enqueue(new C.ItemRentalRequest());
            Say(W.T(ClientTextKeys.ModernUpAskedPlayer));
        }

        /// <summary>A click on a bag-strip cell: the Inventory → Renting route (C.DepositRentalItem; ItemMoves locks both cells).</summary>
        private bool Deposit(int bagSlot)
        {
            if (!IsOpen || _tab != 0 || _session.Mode != RentMode.Lend) return false;
            UserItem item = ItemMoves.GetItem(MirGridType.Inventory, bagSlot);
            if (item?.Info == null) return false;
            if (item.Info.Bind.HasFlag(BindMode.UnableToRent)) { Say(W.T(ClientTextKeys.ModernUpCantRentOut, Up.Name(item)), true); return true; }
            if (GameScene.Models.Rental.RentalItem != null || ItemMoves.IsLocked(MirGridType.Renting, 0)) return true;
            ItemMoves.Selected = new ItemSlotRef(MirGridType.Inventory, bagSlot);
            ItemMoves.Click(new ItemSlotRef(MirGridType.Renting, 0), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            if (ItemMoves.IsSelected(MirGridType.Inventory, bagSlot)) ItemMoves.Selected = null;
            _note = string.Empty;
            return true;
        }

        /// <summary>Take back: the Renting → Inventory route into the first free bag cell (C.RetrieveRentalItem).</summary>
        private void TakeBack()
        {
            UserItem[] inv = Up.User?.Inventory;
            if (inv == null || GameScene.Models.Rental.RentalItem == null) return;
            if (GameScene.User.RentalItemLocked) { Say(W.T(ClientTextKeys.UnableRemoveLockedItem), true); return; }
            int to = -1;
            for (int i = Up.Belt; i < inv.Length && to < 0; i++) if (inv[i] == null && !ItemMoves.IsLocked(MirGridType.Inventory, i)) to = i;
            if (to < 0) { Say(W.T(ClientTextKeys.ModernUpBagFull), true); return; }
            ItemMoves.Selected = new ItemSlotRef(MirGridType.Renting, 0);
            ItemMoves.Click(new ItemSlotRef(MirGridType.Inventory, to), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            if (ItemMoves.IsSelected(MirGridType.Renting, 0)) ItemMoves.Selected = null;
        }

        private void SetPeriod()
        {
            if (!_session.SetPeriod((uint)_range.Value)) Say(W.T(ClientTextKeys.ModernUpPeriodRange), true);
            else _note = string.Empty;
            _dirty = true;
        }

        private void AddFee(uint amount)
        {
            if (GameScene.User == null || GameScene.User.RentalGoldLocked) return;
            if ((ulong)amount + _session.PendingFee > GameScene.Gold) { Say(W.T(ClientTextKeys.LowGold), true); return; }
            _note = string.Empty;
            _session.AddFee(amount);
        }

        /// <summary>"Add…": the amount box up to your gold (ItemRentDialog's MirAmountBox).</summary>
        private void AddFeeAsk()
        {
            uint gold = GameScene.Gold;
            if (gold == 0 || GameScene.User == null || GameScene.User.RentalGoldLocked || ItemMoves.Selected != null) return;
            Up.Pop?.Amount(W.T(ClientTextKeys.RentalFee), -1, gold, n => { if (n > 0 && _session.Mode == RentMode.Borrow) AddFee(n); }, Math.Min(gold, 1000u));
        }
    }
}
