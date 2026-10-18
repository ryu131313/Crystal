// Ported from WebClient/src/ui/modern/windows/npcBox.ts (NpcBoxWindow): the new UI's NPC item box, a modern NPCDropDialog
// (Client/MirScenes/Dialogs/NPCDialogs.cs) for the NPC services that take ONE item from your bag: Disassemble, Downgrade
// (awakening), Reset (added stats), Replace wedding ring, Consign (a price popup, as the classic panel) and Check refine (the web
// moved it to refine.ts; the desktop keeps it with the classic drop panel until the refine window takes it). Opens ONLY from the
// server's answer (S.NPCDisassemble / NPCDowngrade / NPCReset / NPCReplaceWedRing / NPCConsign / NPCCheckRefine → NpcDropPanel)
// and closes with the NPC session. The altar is the NPCDropDialog cell (Models.Npc.DropTarget, MirGridType.DropPanel): click a
// bag item in the strip (or pick one in the bag window and click the altar) to put it there — its bag cell locks; right-click
// the altar to take it back. Ineligible items are dimmed with the reason (the classic / server checks). Prices are the game's
// (UserItem.DisassemblePrice / DowngradePrice / ResetPrice; the ring = RequiredAmount x 10 x NPCRate; Globals.ConsignmentCost).
// Disassemble, Downgrade and Reset are hold-to-confirm (destructive). NO local outcomes: the packet goes out as the classic
// Confirm sends it, and the result is read from the bag as the server changes it (the item leaves, its awakening / added stats
// change, the ring is worn); refusals only come as chat, so the wait gives up after 4 s.
// HIDDEN (no client data): the loot preview, the spark / shatter effects, the partner's name.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class NpcBoxWindow : ModernWindow
    {
        public const string WindowKey = "npcBox";
        private const int WidthHud = 640, AnswerMs = 4000, Cols = 12, Cell = 44, Gap = 4;

        /// <summary>The NPCDropDialog types this window serves.</summary>
        public static bool Serves(PanelType t) =>
            t == PanelType.Disassemble || t == PanelType.Downgrade || t == PanelType.Reset || t == PanelType.ReplaceWedRing ||
            t == PanelType.Consign; // CheckRefine: the refine window's check mode (ModernUi.NpcDropPanel → UpgradeDropPanel)

        private readonly NpcState _state;
        private PanelType _shownType = NpcState.NoPanel;
        private string _note = string.Empty, _previewKey = string.Empty, _result = string.Empty;
        private long _resultUntil;
        private bool _dirty = true;
        private uint _gold = uint.MaxValue;

        // the request out (what the window waits for)
        private ulong _pendId;
        private PanelType _pendType;
        private string _pendName = string.Empty;
        private int _pendAwake, _pendAdded;
        private long _pendUntil, _nextPoll;

        private ModernPanel _closedView, _body;
        private IconView _blurbIcon;
        private ModernLabel _blurb, _altarName, _altarType, _cost, _noteLabel, _bagCount;
        private PaintBox _altar, _card, _banner;
        private NpcItemCell _altarCell;
        private ModernButton _go;
        private ScrollView _bagView;
        private readonly List<NpcItemCell> _bagCells = new List<NpcItemCell>();
        private readonly List<(string Label, string Value, Color Colour)> _rows = new List<(string, string, Color)>();
        private string _warn = string.Empty, _hint = string.Empty;
        private readonly GridWatch _bagWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;

        public NpcBoxWindow(NpcState state) : base(WindowKey, W.T(ClientTextKeys.ModernNpcService), 200, 70)
        {
            _state = state;
            ContentWidth = WidthHud;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.DropPanel || k.Grid == MirGridType.None) _dirty = true; };
        }

        internal string Note => _note;
        internal string Result => _result;
        internal bool Waiting => _pendId != 0;
        internal ModernButton Confirm => _go;
        internal IReadOnlyList<NpcItemCell> BagCells => _bagCells;
        internal NpcItemCell Altar => _altarCell;

        private static UserObject User => GameScene.User;
        private PanelType Type => _state.DropType;
        private bool Live => _state.Open && Serves(_state.DropType);

        protected override void Build()
        {
            int top = TitleHeight, h = 520;
            _closedView = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(WidthHud), S(h)) };
            W.Label(_closedView, W.T(ClientTextKeys.ModernNpcNoService), 0, 180, WidthHud, 22, 14, true, Color.White, StringAlignment.Center);
            W.Label(_closedView, W.T(ClientTextKeys.ModernNpcNoServiceText), 0, 204, WidthHud, 18, 12, false, ModernTheme.Dim, StringAlignment.Center);

            _body = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(WidthHud), S(h)) };
            MirControl m = _body;
            _blurbIcon = new IconView(ModernIcons.Wrench, 16, ModernTheme.Accent) { Parent = m, Location = new Point(S(16), S(11)) };
            _blurb = W.Label(m, string.Empty, 40, 8, WidthHud - 56, 22, 11.5f, false, ModernTheme.Dim);

            // the altar
            _altar = new PaintBox((g, b) => PaintAltar(g, b)) { Parent = m, Location = new Point(S(14), S(40)), Size = new Size(S(220), S(220)), NotControl = true };
            _altarCell = new NpcItemCell(76, () => GameScene.Models.Npc.DropTarget) { Parent = _altar, Location = new Point(S(72), S(66)), Hint = W.T(ClientTextKeys.ModernNpcAltarHint), EmptyIcon = ModernIcons.Plus };
            _altar.NotControl = false;
            _altarCell.Click += (o, e) => AltarClick(e as MouseEventArgs);
            _altarName = W.Label(_altar, string.Empty, 4, 168, 212, 18, 13, true, Color.White, StringAlignment.Center);
            _altarType = W.Label(_altar, string.Empty, 4, 186, 212, 14, 10, true, ModernTheme.Dim, StringAlignment.Center);
            _banner = new PaintBox((g, b) => PaintBanner(g, b)) { Parent = _altar, Location = new Point(S(6), S(6)), Size = new Size(S(208), S(38)), NotControl = true, Visible = false };

            // what happens, the note, the price and the confirm
            int px = 248, pw = WidthHud - px - 14;
            _card = new PaintBox((g, b) => PaintPreview(g, b)) { Parent = m, Location = new Point(S(px), S(40)), Size = new Size(S(pw), S(150)), NotControl = true };
            _noteLabel = W.Label(m, string.Empty, px, 196, pw, 16, 11, false, ModernTheme.Warn);
            _cost = W.Label(m, string.Empty, px, 222, pw - 150, 32, 15, true, ModernTheme.Gold);
            _shownType = NpcState.NoPanel;

            // the bag strip
            W.Caption(m, W.T(ClientTextKeys.ModernWinYourBag), 16, 270, 200);
            _bagCount = W.Label(m, string.Empty, 220, 270, WidthHud - 236, 14, 11, true, Color.Empty, StringAlignment.Far);
            _bagView = new ScrollView(Cols * (Cell + Gap) + 14, 220) { Parent = m, Location = new Point(S(14), S(290)), Background = W.Page };
            _bagView.Framed = false;
            _bagCells.Clear();
            ContentHeight = h;
            _dirty = true;
            Render();
        }

        private void BuildGo()
        {
            _go?.Dispose();
            bool hold = Type == PanelType.Disassemble || Type == PanelType.Downgrade || Type == PanelType.Reset;
            string verb = Verb(Type);
            if (hold)
            {
                var hb = new HoldButton(W.T(ClientTextKeys.ModernNpcHoldTo, verb), true, true) { Parent = _body, HoldMs = 1100, Kind = ButtonKind.Warn };
                hb.Done += DoConfirm;
                _go = hb;
            }
            else
            {
                _go = new ModernButton(verb, ButtonKind.Main, true) { Parent = _body };
                _go.Click += (o, e) => DoConfirm();
            }
            _go.AutoWidth(150);
            _go.Location = new Point(S(WidthHud - 14) - _go.Size.Width, S(222));
        }

        private static string Verb(PanelType t) => t switch
        {
            PanelType.Disassemble => W.T(ClientTextKeys.ModernNpcSvcDisassemble),
            PanelType.Downgrade => W.T(ClientTextKeys.ModernNpcSvcDowngrade),
            PanelType.Reset => W.T(ClientTextKeys.ModernNpcResetVerb),
            PanelType.ReplaceWedRing => W.T(ClientTextKeys.ModernNpcReplaceRing),
            PanelType.Consign => W.T(ClientTextKeys.ModernNpcSvcConsign),
            PanelType.CheckRefine => W.T(ClientTextKeys.ModernNpcSvcRefineCheck),
            _ => W.T(ClientTextKeys.ModernOk),
        };

        private static (string Title, string Blurb, int Icon) Svc(PanelType t) => t switch
        {
            PanelType.Disassemble => (W.T(ClientTextKeys.ModernNpcSvcDisassemble), W.T(ClientTextKeys.ModernNpcBlurbDisassemble), ModernIcons.Swap),
            PanelType.Downgrade => (W.T(ClientTextKeys.ModernNpcDowngradeTitle), W.T(ClientTextKeys.ModernNpcBlurbDowngrade), ModernIcons.ChevronDown),
            PanelType.Reset => (W.T(ClientTextKeys.ModernNpcResetTitle), W.T(ClientTextKeys.ModernNpcBlurbReset), ModernIcons.Refresh),
            PanelType.ReplaceWedRing => (W.T(ClientTextKeys.ModernNpcReplaceRingTitle), W.T(ClientTextKeys.ModernNpcBlurbRing), ModernIcons.Ring),
            PanelType.Consign => (W.T(ClientTextKeys.ModernNpcSvcConsign), W.T(ClientTextKeys.ModernNpcBlurbConsign, W.N(Globals.MinConsignment), W.N(Globals.MaxConsignment)), ModernIcons.Tag),
            PanelType.CheckRefine => (W.T(ClientTextKeys.ModernNpcSvcRefineCheck), W.T(ClientTextKeys.ModernNpcBlurbCheckRefine), ModernIcons.Spark),
            _ => (W.T(ClientTextKeys.ModernNpcService), string.Empty, ModernIcons.Wrench),
        };

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _state.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            _note = string.Empty;
            _dirty = true;
            _bagWatch.Reset();
            Render();
        }

        /// <summary>Every close path (cross, Esc, Close all, the NPC session ending): NPCDropDialog.Hide unlocks the target.</summary>
        protected override void Closed()
        {
            _state.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            _pendId = 0;
            _result = string.Empty;
            if (_banner != null) _banner.Visible = false;
            if (Serves(_state.DropType)) _state.HideDrop();
        }

        public override void Process()
        {
            base.Process();
            if (User == null) return;
            if (_bagWatch.Poll(MirGridType.Inventory, User.Inventory))
            {
                for (int i = 0; i < _bagCells.Count; i++) _bagCells[i].Refresh();
                _altarCell?.Refresh();
                _dirty = true;
            }
            if (_gold != GameScene.Gold) { _gold = GameScene.Gold; _dirty = true; }
            if (_pendId != 0 && CMain.Time >= _nextPoll) { _nextPoll = CMain.Time + 250; PollAnswer(); }
            if (_resultUntil > 0 && CMain.Time >= _resultUntil) { _resultUntil = 0; _banner.Visible = false; }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_body == null || _body.IsDisposed || User == null) return;
            bool live = Live;
            _closedView.Visible = !live;
            _body.Visible = live;
            if (!live)
            {
                SetTitle(W.T(ClientTextKeys.ModernNpcService));
                _shownType = NpcState.NoPanel;
                return;
            }
            PanelType type = Type;
            var svc = Svc(type);
            if (_shownType != type)
            {
                _shownType = type;
                SetTitle(svc.Title);
                _blurbIcon.Set(svc.Icon, ModernTheme.Accent);
                BuildGo();
                _previewKey = string.Empty;
                _altar.Invalidate();
            }
            _blurb.Text = _state.Name.Name + " · " + svc.Blurb;
            _altarCell.Refresh();
            UserItem item = _state.DropItem;
            _altarName.Text = item != null ? NpcFmt.ItemName(item) : string.Empty;
            _altarName.Colour = item?.Info != null && item.Info.Grade != ItemGrade.None ? ModernTheme.Grade(item.Info.Grade) : Color.White;
            _altarType.Text = item?.Info != null ? ((item.Info.Grade != ItemGrade.None ? item.Info.Grade + " " : string.Empty) + NpcFmt.Pretty(item.Info.Type.ToString())) : string.Empty;
            RenderPreview(type, item);
            RenderPay(type, item);
            _noteLabel.Text = _note;
            RenderBag(type);
        }

        private void PaintAltar(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            float cx = b.Size.Width / 2f, cy = b.Size.Height * 0.48f;
            using (var path = new GraphicsPath())
            {
                RectangleF e = new RectangleF(cx - 110 * k, cy - 100 * k, 220 * k, 200 * k);
                path.AddEllipse(e);
                using var br = new PathGradientBrush(path) { CenterColor = W.Over(ModernTheme.Accent, 0.18f), SurroundColors = new[] { W.Page } };
                g.FillEllipse(br, e);
            }
            bool danger = _shownType == PanelType.Disassemble;
            using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.7f), 1f))
                g.DrawEllipse(pen, cx - 90 * k, cy - 90 * k, 180 * k, 180 * k);
            using (var pen = new Pen(danger ? W.Over(ModernTheme.Bad, 0.6f) : W.Over(ModernTheme.Accent, 0.45f), 1f) { DashStyle = DashStyle.Dash })
                g.DrawEllipse(pen, cx - 75 * k, cy - 75 * k, 150 * k, 150 * k);
            RectangleF slot = new RectangleF(cx - 39 * k, cy - 39 * k, 78 * k, 78 * k);
            using (var sp = new GraphicsPath())
            {
                sp.AddEllipse(slot);
                using var br = new PathGradientBrush(sp) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { Color.FromArgb(255, 5, 7, 11) } };
                g.FillEllipse(br, slot);
            }
            UserItem item = _state.DropItem;
            Color ring = item?.Info != null && item.Info.Grade != ItemGrade.None ? ModernTheme.Grade(item.Info.Grade) : item != null ? ModernTheme.Accent : ModernTheme.Frame;
            using (var pen = new Pen(ring, 2f * k)) g.DrawEllipse(pen, slot);
        }

        /// <summary>The result banner over the altar (the server's outcome).</summary>
        private void PaintBanner(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, 12 * k);
            using (var br = new SolidBrush(Color.FromArgb(255, 8, 12, 20))) g.FillPath(br, p);
            ModernSkin.Stroke(g, p, ModernTheme.Accent);
            ModernSkin.Text(g, _result, ModernSkin.Font(11, true), Color.White, new RectangleF(8 * k, 2 * k, r.Width - 16 * k, r.Height - 4 * k), StringAlignment.Center, StringAlignment.Center, true, true);
        }

        /// <summary>WHAT HAPPENS: built from the item's real fields (no rolls).</summary>
        private void RenderPreview(PanelType type, UserItem item)
        {
            UserItem worn = type == PanelType.ReplaceWedRing ? WornWeddingRing() : null;
            string key = type + "|" + (item?.UniqueID ?? 0) + "|" + (item?.Awake.GetAwakeLevel() ?? 0) + "|" + AddedSum(item) + "|" + (worn?.UniqueID ?? 0) + "|" + GameScene.NPCRate + "|" + _pendName + "|" + (item?.RefineAdded ?? 0);
            if (key == _previewKey) return;
            _previewKey = key;
            _rows.Clear();
            _warn = _hint = string.Empty;
            if (type == PanelType.ReplaceWedRing && worn == null) _warn = W.T(ClientTextKeys.ModernNpcNoWeddingRing);
            if (item?.Info == null)
            {
                _hint = _pendId != 0 ? W.T(ClientTextKeys.ModernNpcWaitingFor, _pendName) : W.T(ClientTextKeys.ModernNpcPutOnAltarHint);
                _card.Invalidate();
                return;
            }
            ItemInfo info = item.Info;
            switch (type)
            {
                case PanelType.Disassemble:
                    {
                        int quality = AddedCount(item) + item.Awake.GetAwakeLevel() + 1;
                        int most = Math.Max(1, (int)info.Grade * info.RequiredAmount / 10 + quality - 1);
                        _warn = NpcFmt.ItemName(item) + ": " + W.T(ClientTextKeys.ItemWillBeDestroyed).Trim();
                        _rows.Add((W.T(ClientTextKeys.ModernNpcYouGet), most > 1 ? W.T(ClientTextKeys.ModernNpcMaterialRange, W.N(most)) : W.T(ClientTextKeys.ModernNpcMaterialOne), Color.White));
                        _rows.Add((W.T(ClientTextKeys.ModernNpcGrade), info.Grade == ItemGrade.None ? "-" : info.Grade.ToString(), ModernTheme.Grade(info.Grade)));
                        _hint = W.T(ClientTextKeys.ModernNpcGradeOff);
                        break;
                    }
                case PanelType.Downgrade:
                    {
                        int lv = item.Awake.GetAwakeLevel();
                        _rows.Add((W.T(ClientTextKeys.ModernNpcAwakening), new string('★', Math.Max(0, lv - 1)) + "☆", ModernTheme.Accent));
                        _rows.Add((W.T(ClientTextKeys.ModernNpcLevel), lv + "  →  " + (lv - 1), Color.White));
                        _rows.Add((W.T(ClientTextKeys.ModernNpcRisk), W.T(ClientTextKeys.ModernNpcRiskDura), ModernTheme.Text));
                        break;
                    }
                case PanelType.Reset:
                    foreach (var kv in item.AddedStats.Values)
                        if (kv.Value != 0) _rows.Add((NpcFmt.Pretty(kv.Key.ToString()), "+" + W.N(kv.Value) + "  →  +0", Color.White));
                    if (item.Slots.Length > info.Slots) _rows.Add((W.T(ClientTextKeys.ModernNpcSockets), W.T(ClientTextKeys.ModernNpcSocketsKept, item.Slots.Length), Color.White));
                    _rows.Add((W.T(ClientTextKeys.ModernNpcRisk), W.T(ClientTextKeys.ModernNpcRiskDura), ModernTheme.Text));
                    _hint = W.T(ClientTextKeys.ModernNpcResetKeeps);
                    break;
                case PanelType.ReplaceWedRing:
                    _rows.Add((W.T(ClientTextKeys.ModernNpcBond), "→ " + NpcFmt.ItemName(item), Color.White));
                    if (worn != null) _rows.Add((W.T(ClientTextKeys.ModernNpcOldRing), NpcFmt.ItemName(worn) + " " + W.T(ClientTextKeys.ModernNpcToBag), Color.White));
                    _hint = W.T(ClientTextKeys.ModernNpcRingPrice, info.RequiredAmount, W.N1(GameScene.NPCRate));
                    break;
                case PanelType.Consign:
                    _rows.Add((W.T(ClientTextKeys.ModernNpcListingFee), W.T(ClientTextKeys.ModernNpcGoldAmount, W.N(Globals.ConsignmentCost)), Color.White));
                    _hint = W.T(ClientTextKeys.ModernNpcConsignHint);
                    break;
                case PanelType.CheckRefine:
                    _rows.Add((W.T(ClientTextKeys.ModernNpcRefined), item.RefineAdded > 0 ? "+" + item.RefineAdded : "-", Color.White));
                    _hint = W.T(ClientTextKeys.ModernNpcCheckRefineHint);
                    break;
            }
            _card.Invalidate();
        }

        private void PaintPreview(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            W.Card(g, b.Size, null, 0.03f);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernNpcWhatHappens), ModernSkin.Font(9.5f, true), ModernTheme.Accent, new RectangleF(12 * k, 8 * k, b.Size.Width - 24 * k, 14 * k));
            float y = 28 * k, w = b.Size.Width - 24 * k;
            if (_warn.Length > 0)
            {
                SizeF sz = ModernSkin.MeasureWrapped(_warn, ModernSkin.Font(11), w - 20 * k);
                RectangleF r = new RectangleF(12 * k, y, w, sz.Height + 10 * k);
                using (var br = new SolidBrush(ModernTheme.Mix(Color.FromArgb(120, 20, 20), W.Page, 0.45f))) g.FillRectangle(br, r);
                using (var pen = new Pen(Color.FromArgb(0xcc, 0x44, 0x44), 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                ModernSkin.Text(g, _warn, ModernSkin.Font(11), Color.FromArgb(0xff, 0xc8, 0xc0), new RectangleF(r.X + 10 * k, r.Y + 5 * k, w - 20 * k, sz.Height + 2), StringAlignment.Near, StringAlignment.Near, false, true);
                y += r.Height + 6 * k;
            }
            foreach (var row in _rows)
            {
                ModernSkin.Text(g, row.Label, ModernSkin.Font(11.5f), ModernTheme.Dim, new RectangleF(12 * k, y, 96 * k, 22 * k));
                ModernSkin.Text(g, row.Value, ModernSkin.Font(11.5f, true), row.Colour, new RectangleF(110 * k, y, w - 100 * k, 22 * k));
                y += 22 * k;
            }
            if (_hint.Length > 0)
                ModernSkin.Text(g, _hint, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(12 * k, y + 2 * k, w, b.Size.Height - y - 6 * k), StringAlignment.Near, StringAlignment.Near, false, true);
        }

        /// <summary>The price against the gold you have, and the confirm.</summary>
        private void RenderPay(PanelType type, UserItem item)
        {
            uint? price = item != null ? Price(type, item) : null;
            bool poor = price.HasValue && price.Value > GameScene.Gold;
            _cost.Text = !price.HasValue ? (item != null ? W.T(ClientTextKeys.ModernNpcFree) : "—") : W.N(price.Value) + "  / " + W.N(GameScene.Gold) + " " + W.T(ClientTextKeys.ModernNpcGoldWord);
            _cost.Colour = poor ? ModernTheme.BadSoft : ModernTheme.Gold;
            string why = string.Empty;
            if (_pendId != 0) why = W.T(ClientTextKeys.ModernWinWaitingServer);
            else if (item == null) why = W.T(ClientTextKeys.ModernNpcPutFirst);
            else if (poor) why = W.T(ClientTextKeys.LowGold);
            else if (type == PanelType.ReplaceWedRing && WornWeddingRing() == null) why = W.T(ClientTextKeys.ModernNpcNoWeddingRing);
            _go.SetDisabled(why.Length > 0, why);
        }

        /// <summary>The bag strip: every bag cell, dimmed with the reason when this service can't take it.</summary>
        private void RenderBag(PanelType type)
        {
            UserItem[] inv = User.Inventory;
            int belt = User.BeltIdx, n = Math.Max(0, inv.Length - belt);
            if (_bagCells.Count != n)
            {
                for (int i = 0; i < _bagCells.Count; i++) _bagCells[i].Dispose();
                _bagCells.Clear();
                for (int i = 0; i < n; i++)
                {
                    var c = new NpcItemCell(Cell).Bag(belt + i);
                    c.Parent = _bagView.Content;
                    c.Location = new Point(S(i % Cols * (Cell + Gap)), S(i / Cols * (Cell + Gap)));
                    c.Click += (o, e) => BagClick(c);
                    _bagCells.Add(c);
                }
                _bagView.FitContent(0);
            }
            int fit = 0, used = 0;
            for (int i = 0; i < _bagCells.Count; i++)
            {
                NpcItemCell c = _bagCells[i];
                c.Refresh();
                UserItem item = c.Item;
                string why = string.Empty;
                if (item != null)
                {
                    used++;
                    why = Refusal(type, item);
                    if (why.Length == 0) fit++;
                }
                c.SetState(false, false, null, item != null && why.Length > 0);
                if (c.Hint != why) c.Hint = why;
            }
            _bagCount.Text = W.T(ClientTextKeys.ModernNpcBagFits, fit, used, n);
        }

        // ------------------------------------------------------------------ rules

        private static int AddedCount(UserItem item)
        {
            int n = 0;
            if (item?.AddedStats == null) return 0;
            foreach (var kv in item.AddedStats.Values) n += Math.Abs(kv.Value);
            return n;
        }

        private static int AddedSum(UserItem item) => AddedCount(item);

        /// <summary>NPCDropDialog.ItemCell_Click's / the server's checks for the type: "" = it takes the item, else why not.</summary>
        internal static string Refusal(PanelType type, UserItem item)
        {
            ItemInfo info = item?.Info;
            if (info == null) return W.T(ClientTextKeys.ModernNpcUnknownItem);
            switch (type)
            {
                case PanelType.Disassemble:
                    if (info.Grade == ItemGrade.None || info.Type == ItemType.Awakening) return W.T(ClientTextKeys.ModernNpcOnlyGraded);
                    if (info.Bind.HasFlag(BindMode.UnableToDisassemble)) return W.T(ClientTextKeys.ModernNpcCantDisassemble);
                    if (item.RentalInformation != null && item.RentalInformation.BindingFlags.HasFlag(BindMode.UnableToDisassemble)) return W.T(ClientTextKeys.ModernNpcRentedFrom, item.RentalInformation.OwnerName);
                    return string.Empty;
                case PanelType.Downgrade:
                    if (item.Awake.GetAwakeLevel() == 0) return W.T(ClientTextKeys.ModernNpcNotAwakened);
                    if (item.RentalInformation != null) return W.T(ClientTextKeys.ModernNpcRentedFrom, item.RentalInformation.OwnerName);
                    return string.Empty;
                case PanelType.Reset:
                    if (!item.IsAdded) return W.T(ClientTextKeys.ModernNpcNoAdded);
                    if (info.NeedIdentify) return W.T(ClientTextKeys.ModernNpcUnidentified);
                    if (item.RentalInformation != null) return W.T(ClientTextKeys.ModernNpcRentedFrom, item.RentalInformation.OwnerName);
                    return string.Empty;
                case PanelType.ReplaceWedRing:
                    if (info.Type != ItemType.Ring) return W.T(ClientTextKeys.ModernNpcRingsOnly);
                    if (info.Bind.HasFlag(BindMode.NoWeddingRing)) return W.T(ClientTextKeys.ModernNpcNoWedding);
                    if (info.Durability == 0) return W.T(ClientTextKeys.ModernNpcCantUseHere);
                    return string.Empty;
                case PanelType.Consign:
                    if (info.Bind.HasFlag(BindMode.DontStore) || info.Bind.HasFlag(BindMode.DontSell)) return W.T(ClientTextKeys.CannotConsignItem);
                    return string.Empty;
                case PanelType.CheckRefine:
                    if (item.RefineAdded == 0) return W.T(ClientTextKeys.ItemHasntBeenRefinedNoChecking, NpcFmt.ItemName(item));
                    return string.Empty;
            }
            return W.T(ClientTextKeys.ModernNpcNotForService);
        }

        private static uint? Price(PanelType type, UserItem item)
        {
            switch (type)
            {
                case PanelType.Disassemble: return item.DisassemblePrice();
                case PanelType.Downgrade: return item.DowngradePrice();
                case PanelType.Reset: return item.ResetPrice();
                case PanelType.ReplaceWedRing: return item.Info == null ? null : (uint)(item.Info.RequiredAmount * 10 * GameScene.NPCRate);
                case PanelType.Consign: return Globals.ConsignmentCost;
            }
            return null;
        }

        /// <summary>PlayerObject.ReplaceWeddingRing: the ring worn on the left hand, when it is the wedding ring.</summary>
        private static UserItem WornWeddingRing()
        {
            UserItem r = User?.Equipment[(int)EquipmentSlot.RingL];
            return r != null && r.WeddingRing != -1 ? r : null;
        }

        // ------------------------------------------------------------------ actions

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            if (_noteLabel != null && !_noteLabel.IsDisposed) _noteLabel.Text = _note;
        }

        /// <summary>A bag cell of the strip: on the altar when the service takes it, else the reason.</summary>
        private void BagClick(NpcItemCell c)
        {
            if (!Live || c.Item == null) return;
            Put(c.BagSlot);
        }

        private void Put(int slot)
        {
            UserItem item = ItemMoves.GetItem(MirGridType.Inventory, slot);
            if (item == null) return;
            if (_pendId != 0) { Say(W.T(ClientTextKeys.ModernWinWaitingServer)); return; }
            string why = Refusal(Type, item);
            if (why.Length > 0) { Say(why); return; }
            if (ItemMoves.IsLocked(MirGridType.Inventory, slot)) { Say(W.T(ClientTextKeys.ModernWinWaitingServer)); return; }
            if (_state.TakeDrop(slot)) Say(string.Empty);
        }

        /// <summary>NPCDropDialog.ItemCell_Click: a bag item picked in the bag window goes on the altar; right-click takes it back.</summary>
        private void AltarClick(MouseEventArgs e)
        {
            if (!Live) return;
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null)
            {
                ItemMoves.Selected = null;
                if (pick.GridType == MirGridType.Inventory) Put(pick.ItemSlot);
                return;
            }
            if (_state.DropItem != null && (e == null || e.Button == MouseButtons.Right || e.Button == MouseButtons.Left))
            {
                _state.ReleaseDrop();
                _dirty = true;
            }
        }

        /// <summary>The Alt + click quick sell route (IGameUi.ItemNpcDropQuickSell): the picked bag item goes on the altar.</summary>
        internal void QuickPut(int slot) => Put(slot);

        /// <summary>NPCDropDialog.Confirm: the classic packet; the result is read from the bag (PollAnswer).</summary>
        private void DoConfirm()
        {
            UserItem item = _state.DropItem;
            PanelType type = Type;
            if (item == null || _pendId != 0 || !Live) return;
            string why = Refusal(type, item);
            if (why.Length > 0) { Say(why); return; }
            uint? price = Price(type, item);
            if (price.HasValue && price.Value > GameScene.Gold) { Say(W.T(ClientTextKeys.LowGold)); return; }
            if (type == PanelType.ReplaceWedRing && WornWeddingRing() == null) { Say(W.T(ClientTextKeys.ModernNpcNoWeddingRing)); return; }
            if (type == PanelType.Consign)
            {
                AskConsignPrice(item);
                return;
            }
            switch (type)
            {
                case PanelType.Disassemble: Network.Enqueue(new C.DisassembleItem { UniqueID = item.UniqueID }); break;
                case PanelType.Downgrade: Network.Enqueue(new C.DowngradeAwakening { UniqueID = item.UniqueID }); break;
                case PanelType.Reset: Network.Enqueue(new C.ResetAddedItem { UniqueID = item.UniqueID }); break;
                case PanelType.ReplaceWedRing: Network.Enqueue(new C.ReplaceWedRing { UniqueID = item.UniqueID }); break;
                case PanelType.CheckRefine: Network.Enqueue(new C.CheckRefine { UniqueID = item.UniqueID }); break;
                default: return;
            }
            Wait(item, type);
            _state.DropSent(false);
        }

        /// <summary>The price box (MirAmountBox ConsignmentPrice), then C.ConsignItem; the bag cell stays locked until S.ConsignItem.</summary>
        private void AskConsignPrice(UserItem item)
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null) return;
            ulong id = item.UniqueID;
            pop.Amount(W.T(ClientTextKeys.ConsignmentPrice), item.Info?.Image ?? -1, Globals.MaxConsignment, price =>
            {
                UserItem now = _state.DropItem;
                if (!IsOpen || _pendId != 0 || !Live || Type != PanelType.Consign || now == null || now.UniqueID != id) return;
                Network.Enqueue(new C.ConsignItem { UniqueID = id, Price = price, Type = MarketPanelType.Consign });
                Wait(now, PanelType.Consign);
                _state.DropSent(true);
            }, Globals.MinConsignment, Globals.MinConsignment);
        }

        private void Wait(UserItem item, PanelType type)
        {
            _pendId = item.UniqueID;
            _pendType = type;
            _pendName = NpcFmt.ItemName(item);
            _pendAwake = item.Awake.GetAwakeLevel();
            _pendAdded = AddedCount(item);
            _pendUntil = CMain.Time + AnswerMs;
            _nextPoll = CMain.Time + 250;
            _note = string.Empty;
            _dirty = true;
        }

        /// <summary>The server's answer as the bag shows it (the models applied the packets already).</summary>
        private void PollAnswer()
        {
            UserItem item = NpcFmt.FindBag(_pendId, out int slot);
            string done = string.Empty;
            switch (_pendType)
            {
                case PanelType.Disassemble:
                    if (item == null) done = W.T(ClientTextKeys.ModernNpcDoneDisassemble, _pendName);
                    break;
                case PanelType.Consign:
                    if (item == null) done = W.T(ClientTextKeys.ModernNpcDoneConsign, _pendName);
                    else if (!ItemMoves.IsLocked(MirGridType.Inventory, slot)) done = W.T(ClientTextKeys.ModernNpcFailConsign, _pendName);
                    break;
                case PanelType.Downgrade:
                    if (item != null && item.Awake.GetAwakeLevel() != _pendAwake) done = W.T(ClientTextKeys.ModernNpcDoneDowngrade, _pendName, item.Awake.GetAwakeLevel());
                    break;
                case PanelType.Reset:
                    if (item != null && AddedCount(item) != _pendAdded) done = W.T(ClientTextKeys.ModernNpcDoneReset, _pendName);
                    break;
                case PanelType.ReplaceWedRing:
                    if (User.Equipment[(int)EquipmentSlot.RingL]?.UniqueID == _pendId) done = W.T(ClientTextKeys.ModernNpcDoneRing, _pendName);
                    break;
            }
            if (done.Length == 0)
            {
                if (CMain.Time < _pendUntil) return;
                _pendId = 0;
                Say(W.T(ClientTextKeys.ModernNpcSeeChat));
                _previewKey = string.Empty;
                _dirty = true;
                return;
            }
            _pendId = 0;
            _previewKey = string.Empty;
            _result = done;
            _banner.Visible = true;
            _banner.Invalidate();
            _resultUntil = CMain.Time + 3300;
            _dirty = true;
        }
    }
}
