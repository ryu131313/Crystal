// Ported from WebClient/src/ui/modern/windows/fishing.ts (FishingWindow): one window for the desktop's FishingDialog (the rod and
// its parts) and FishingStatusDialog (chance / progress bars, the fish button, auto cast, "Esc exit"). Left: the rod (the Weapon
// equipment cell) and its five parts (Hook / Float / Bait / Finder / Reel), the catch chance. Right: the water scene
// (FishingScene: the float out while the user fishes, bobbing while the progress fills, dipping on a bite), the status line,
// the progress bar, Cast / Reel in, the auto-cast and Esc-exit switches.
//  - every state comes from S.FishingUpdate (CompanionState.Fishing: Fishing, ChancePercent, ProgressPercent, FoundFish);
//  - the parts are ModernItemCell on the Fishing grid (index = FishingSlot): pick / drop / right-click through ItemMoves (the
//    cell locks until the server answers);
//  - a click on a part row opens a picker of the bag items of that type (usable first, best marked): Fit = ItemMoves.Use on the
//    bag slot (UseSlotItem), Swap = take the old part off (ItemMoves.Use on the fishing slot), then fit the new one once the slot
//    is free; same-kind bait tops the stack up (the server merges);
//  - Cast = what the classic map click does (MapControl.CanFish facing the user's own direction, then User.FishingTime and
//    C.FishingCast CastOut = true); Reel in = C.FishingCast CastOut = false (FishButton / FishingStatusDialog.Cancel); auto
//    cast = C.FishingChangeAutocast (needs a Reel in the rod; losing it turns it off, FishingStatusDialog_BeforeDraw); Esc exit:
//    closing the window reels in while it is ticked;
//  - opens only while holding a rod (FishingDialog.Show / ClientTextKeys.NoFishingRod) and closes when the rod goes
//    (CloseFishingWindow); it opens by itself when a cast starts or a fish bites (ModernUi.FishingUpdated);
//  - each cast's result is worked out client-side (FishingSession: caught / got away / bag full / missed bite / empty line) and
//    shown as a card on the water, with a per-session tally.
// Desktop changes: a caught item is the new bag item between the last update and the reel-in (the desktop has no S.GainedItem
// hook for the UI); the scene is GDI+ on a 40 / 125 ms repaint gate (FishingScene).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>
    /// The fishing state the window shows, kept while it is closed (CompanionState.Fishing): the last S.FishingUpdate, the
    /// classic status dialog's switches, and the cast tracking (fishing.ts track / finish) with the session tally.
    /// </summary>
    internal sealed class FishingSession
    {
        public bool Fishing, FoundFish;
        public int Chance, Progress;
        public bool AutoCast, EscExit;
        public int Casts, Caught, Lost;
        // the cast in progress
        private bool _line, _bit, _slipped, _gotAway, _bagFull, _wasFishing, _wasBite;
        private int _maxProgress;
        /// <summary>The bag at the last update: UniqueID → count (a catch may merge into a stack: GameScene.AddItem).</summary>
        private readonly Dictionary<ulong, ushort> _bag = new Dictionary<ulong, ushort>();
        private bool _bagWasFull;
        // what the window has not shown yet
        public int ResultKind = -1; // 0 line in, 1 catch, 2 lost
        public string ResultTitle = string.Empty, ResultSub = string.Empty;
        public int ResultImage = -1;
        public long ResultUntil;
        public int SceneOutcome = -1; // FishingScene.Outcomes
        public bool SceneSlip;
        public int Version;

        private static string T(ClientTextKeys key) => W.T(key);
        private static string T(ClientTextKeys key, params object[] args) => W.T(key, args);

        private void SnapBag()
        {
            _bag.Clear();
            _bagWasFull = false;
            UserItem[] inv = GameScene.User?.Inventory;
            if (inv == null) return;
            bool full = true;
            for (int i = 0; i < inv.Length; i++)
            {
                if (inv[i] == null) { if (i >= GameScene.User.BeltIdx) full = false; continue; }
                _bag[inv[i].UniqueID] = inv[i].Count;
            }
            _bagWasFull = full;
        }

        /// <summary>The first bag item that is new or grew (a stacked catch) since the last update.</summary>
        private UserItem Gained(out int extra)
        {
            extra = 0;
            UserItem first = null;
            UserItem[] inv = GameScene.User?.Inventory;
            if (inv == null) return null;
            for (int i = 0; i < inv.Length; i++)
            {
                if (inv[i] == null) continue;
                if (_bag.TryGetValue(inv[i].UniqueID, out ushort before) && inv[i].Count <= before) continue;
                if (first == null) first = inv[i];
                else extra++;
            }
            return first;
        }

        /// <summary>The server's chat lines for a reel-in (ServerTextKeys FishGotAway, NoBagSpace) when the server speaks English; Finish
        /// does not need them (a bite reeled in with no gain is "got away", or "bag full" when the bag had no free cell).</summary>
        public void Chat(string text)
        {
            if (!_line || string.IsNullOrEmpty(text)) return;
            if (text == "Your fish got away!") _gotAway = true;
            else if (text == "You do not have enough space.") _bagFull = true;
        }

        /// <summary>S.FishingUpdate for the user (PlayerObject.FishingCast / UpdateFish): a cast gets at most one bite; it lasts ~3 s,
        /// then FoundFish drops ("slipped"); reel-in on a bite brings a new bag item (caught), "Your fish got away!" or "You do not
        /// have enough space.", then Fishing = false; a reel-in without a bite says nothing.</summary>
        public void Update(ClientPackets_FishingView p)
        {
            if (p.Fishing && !_wasFishing)
            {
                _line = true;
                _bit = _slipped = _gotAway = _bagFull = false;
                _maxProgress = 0;
                Casts++;
            }
            else if (!p.Fishing && _wasFishing && _line)
            {
                Finish();
                _line = false;
            }
            if (_line && p.Fishing)
            {
                _maxProgress = Math.Max(_maxProgress, p.Progress);
                if (p.FoundFish) _bit = true;
                else if (_wasBite && !_slipped)
                {
                    _slipped = true;
                    SceneSlip = true;
                    Show(2, T(ClientTextKeys.ModernCompFishSlipped), T(ClientTextKeys.ModernCompFishSlippedSub), -1);
                }
                // only what arrives between the last update and the reel-in's update belongs to the reel-in
                _gotAway = _bagFull = false;
                SnapBag();
            }
            _wasFishing = p.Fishing;
            _wasBite = p.Fishing && p.FoundFish;
            Fishing = p.Fishing;
            FoundFish = p.FoundFish;
            Chance = p.Chance;
            Progress = p.Progress;
            Version++;
        }

        private void Finish()
        {
            UserItem item = Gained(out int extra);
            bool onBite = _wasBite; // Finish runs before _wasBite takes the reel-in update: the line was on a bite when reeled in
            if (item == null && onBite && !_gotAway && !_bagFull)
            {
                if (_bagWasFull) _bagFull = true;
                else _gotAway = true;
            }
            if (item != null)
            {
                Caught++;
                SceneOutcome = (int)FishingScene.Outcomes.Catch;
                string name = item.Info != null ? item.Info.FriendlyName : T(ClientTextKeys.ModernCompAFish);
                Show(1, T(ClientTextKeys.ModernCompCaught, name), extra > 0 ? T(ClientTextKeys.ModernCompAndMore, extra) : T(ClientTextKeys.ModernCompAddedToBag), item.Info?.Image ?? -1);
            }
            else if (_bagFull)
            {
                Lost++;
                SceneOutcome = (int)FishingScene.Outcomes.Lost;
                Show(2, T(ClientTextKeys.ModernCompBagFullCatch), T(ClientTextKeys.ModernCompBagFullCatchSub), -1);
            }
            else if (_gotAway)
            {
                Lost++;
                SceneOutcome = (int)FishingScene.Outcomes.Lost;
                Show(2, T(ClientTextKeys.ModernCompGotAway), T(ClientTextKeys.ModernCompGotAwaySub), -1);
            }
            else if (_slipped)
            {
                Lost++;
                Show(2, T(ClientTextKeys.ModernCompNothingCaught), T(ClientTextKeys.ModernCompNothingCaughtSub), -1);
            }
            else if (_maxProgress >= 100) Show(0, T(ClientTextKeys.ModernCompNothingBit), T(ClientTextKeys.ModernCompNothingBitSub), -1);
            else Show(0, T(ClientTextKeys.ModernCompReeledEmpty), T(ClientTextKeys.ModernCompReeledEmptySub), -1);
        }

        private void Show(int kind, string title, string sub, int image)
        {
            ResultKind = kind;
            ResultTitle = title;
            ResultSub = sub;
            ResultImage = image;
            ResultUntil = CMain.Time + (kind == 1 ? 7000 : 5000);
            Version++;
        }
    }

    /// <summary>The fields of S.FishingUpdate the session reads (a copy: packet objects are not kept past their handler).</summary>
    internal struct ClientPackets_FishingView
    {
        public bool Fishing, FoundFish;
        public int Chance, Progress;
    }

    public sealed class FishingWindow : ModernWindow
    {
        public const string WindowKey = "fishing";
        private const int WidthHud = 660, HeightHud = 440, RodW = 230, SceneX = 231, SceneW = 429;

        /// <summary>[FishingSlot, the ItemType that fits it, name, what it is for, empty-slot icon].</summary>
        private static readonly (FishingSlot Slot, ItemType Type, ClientTextKeys Name, ClientTextKeys Hint, int Icon)[] Parts =
        {
            (FishingSlot.Hook, ItemType.Hook, ClientTextKeys.ModernCompHook, ClientTextKeys.ModernCompHookHint, ModernIcons.Fish),
            (FishingSlot.Float, ItemType.Float, ClientTextKeys.ModernCompFloat, ClientTextKeys.ModernCompFloatHint, ModernIcons.Drop),
            (FishingSlot.Bait, ItemType.Bait, ClientTextKeys.ModernCompBait, ClientTextKeys.ModernCompBaitHint, ModernIcons.Spark),
            (FishingSlot.Finder, ItemType.Finder, ClientTextKeys.ModernCompFinder, ClientTextKeys.ModernCompFinderHint, ModernIcons.Search),
            (FishingSlot.Reel, ItemType.Reel, ClientTextKeys.ModernCompReel, ClientTextKeys.ModernCompReelHint, ModernIcons.Refresh),
        };

        private static string T(ClientTextKeys key) => W.T(key);
        private static string T(ClientTextKeys key, params object[] args) => W.T(key, args);
        private static FishingSession F => CompanionState.Fishing;

        private ModernLabel _rodName, _rodSub;
        private readonly PaintBox[] _rows = new PaintBox[5];
        private readonly ModernItemCell[] _cells = new ModernItemCell[5];
        private readonly string[] _rowName = new string[5], _rowLine = new string[5];
        private readonly Color[] _rowColour = new Color[5], _lineColour = new Color[5];
        private readonly float[] _rowDura = new float[5];
        private ModernLabel _chanceText;
        private CompMeter _chance;
        private FishingScene _scene;
        private ModernLabel _status, _tally;
        private CompMeter _progress;
        private ModernButton _cast, _reel, _bait;
        private ToggleSwitch _autoCast, _escExit;
        private PaintBox _result;
        private int _resultShown = -1;
        private PaintBox _picker;
        private int _pickSlot = -1;
        private long _pickSig = long.MinValue;
        private (int Slot, ulong From, ulong To)? _swap;
        private long _nextPoll, _sig = long.MinValue, _nextCast;
        private int _seenVersion = -1;
        private readonly GridWatch _bagWatch = new GridWatch(), _partWatch = new GridWatch();

        public FishingWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinFishing), -1, 120)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
        }

        internal ModernButton CastButton => _cast;
        internal ModernButton ReelButton => _reel;
        internal ToggleSwitch AutoCastSwitch => _autoCast;
        internal ToggleSwitch EscExitSwitch => _escExit;
        internal PaintBox PartRow(int i) => i >= 0 && i < 5 ? _rows[i] : null;
        internal PaintBox Picker => _picker;
        internal int PickerSlot => _pickSlot;
        internal FishingScene Scene => _scene;

        private static UserObject User => GameScene.User;
        private static UserItem Rod => User != null && User.HasFishingRod && User.Equipment != null ? User.Equipment[(int)EquipmentSlot.Weapon] : null;

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            const int top = TitleHeight;
            int x = 12, w = RodW - 24, y = top + 12;

            new PaintBox((g, b) =>
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * ModernRoot.K);
                using (var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.1f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.45f));
            })
            { Parent = this, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(56)), NotControl = true };
            new ModernItemCell(MirGridType.Equipment, (int)EquipmentSlot.Weapon, 40) { Parent = this, Location = new Point(S(x + 8), S(y + 8)), ReadOnly = true };
            _rodName = W.Label(this, string.Empty, x + 58, y + 10, w - 64, 18, 13, true, Color.White);
            _rodSub = W.Label(this, string.Empty, x + 58, y + 29, w - 64, 15, 10, false, ModernTheme.Dim);
            y += 64;
            W.Caption(this, T(ClientTextKeys.ModernCompParts), x, y, w);
            y += 18;
            for (int i = 0; i < Parts.Length; i++)
            {
                int idx = i;
                var row = new PaintBox(PaintPart) { Parent = this, Location = new Point(S(x), S(y + i * 50)), Size = new Size(S(w), S(46)), Index = i };
                row.Click += (o, e) => RowClicked(idx);
                var cell = new ModernItemCell(MirGridType.Fishing, (int)Parts[i].Slot, 38) { Parent = row, Location = new Point(S(4), S(4)) };
                new CellDecor(cell, Parts[i].Icon);
                _rows[i] = row;
                _cells[i] = cell;
                _rowName[i] = _rowLine[i] = string.Empty;
                _rowDura[i] = -1;
            }
            int cy = top + HeightHud - 62;
            W.Caption(this, T(ClientTextKeys.ModernCompCatchChance), x, cy, w - 40);
            _chanceText = W.Label(this, "-", x + w - 40, cy, 40, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            _chance = W.At(new CompMeter(w, 8, ModernTheme.Hex("#2f9e5a"), ModernTheme.Hex("#4fe07a"), true), this, x, cy + 18);
            W.Label(this, T(ClientTextKeys.ModernCompChanceHint), x, cy + 32, w, 14, 10, false, ModernTheme.Dim);
            W.VDivider(this, RodW, top, HeightHud);

            // the water scene and its overlay
            _scene = new FishingScene { Parent = this, Location = new Point(S(SceneX), S(top)), Size = new Size(S(SceneW) - S(2), S(HeightHud) - S(2)) };
            var water = new PaintBox((g, b) => { }) { Parent = this, Location = _scene.Location, Size = _scene.Size };
            water.Click += (o, e) => WaterClicked();
            int ox = SceneX + 16, ow = SceneW - 32, oy = top + HeightHud - 12;
            _result = new PaintBox(PaintResult) { Parent = this, Location = new Point(S(SceneX + 30), S(top + 14)), Size = new Size(S(SceneW - 60), S(58)), Visible = false, NotControl = true };
            _result.AfterPaint = DrawResultIcon;
            oy -= 22;
            _autoCast = new ToggleSwitch(F.AutoCast, true);
            _escExit = new ToggleSwitch(F.EscExit, true) { Tooltip = T(ClientTextKeys.ModernCompEscExitHint) };
            int optW = 64 + 30 + 6;
            W.At(_autoCast, this, ox + ow / 2 - optW - 8, oy);
            W.Label(this, T(ClientTextKeys.ModernCompAutoCast), ox + ow / 2 - optW - 8 + 36, oy, 64, 16, 11, false, ModernTheme.Hex("#dbe6f5"));
            W.At(_escExit, this, ox + ow / 2 + 8, oy);
            W.Label(this, T(ClientTextKeys.ModernCompEscExit), ox + ow / 2 + 8 + 36, oy, 64, 16, 11, false, ModernTheme.Hex("#dbe6f5"));
            _autoCast.Changed += on => ToggleAutoCast(on);
            _escExit.Changed += on => F.EscExit = on;
            oy -= 38;
            _cast = W.At(new ModernButton(T(ClientTextKeys.ModernCompCast), ButtonKind.Main, true), this, ox + 40, oy);
            _cast.Size = new Size(S(130), _cast.Size.Height);
            _cast.Click += (o, e) => Cast();
            _reel = W.At(new ModernButton(T(ClientTextKeys.ModernCompReelUp), ButtonKind.Normal, true), this, ox + 40, oy);
            _reel.Size = new Size(S(130), _reel.Size.Height);
            _reel.Click += (o, e) => ReelIn();
            _bait = W.At(new ModernButton(string.Empty), this, ox + 40 + 138, oy + 4);
            _bait.Size = new Size(S(110), _bait.Size.Height);
            _bait.Click += (o, e) => { int i = Array.FindIndex(Parts, p => p.Slot == FishingSlot.Bait); if (i >= 0) RowClicked(i); };
            var help = W.At(new ModernButton("?", ButtonKind.Normal, true), this, ox + 40 + 138 + 116, oy);
            help.Size = new Size(S(30), help.Size.Height);
            help.Tooltip = T(ClientTextKeys.ModernCompHelpFishing);
            help.Click += (o, e) => (ModernRoot.Instance?.Wm.Get(HelpWindow.WindowKey) as HelpWindow)?.Show("fishing");
            oy -= 16;
            _progress = W.At(new CompMeter(ow, 8, ModernTheme.Hex("#1d63a8"), ModernTheme.Hex("#59c2ff"), true), this, ox, oy);
            oy -= 22;
            _tally = W.Label(this, string.Empty, ox, oy, ow, 16, 10, false, ModernTheme.Hex("#b8c6da"), StringAlignment.Center);
            oy -= 26;
            _status = W.Label(this, string.Empty, ox, oy, ow, 24, 14, true, ModernTheme.Hex("#e8f1ff"), StringAlignment.Center);

            _picker = null;
            _pickSlot = -1;
            _sig = _pickSig = long.MinValue;
            _resultShown = -1;
            _bagWatch.Reset();
            _partWatch.Reset();
            _nextPoll = 0;
        }

        // ------------------------------------------------------------------ painters

        private void PaintPart(Graphics g, PaintBox b)
        {
            int i = b.Index;
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                if (b.Hovered || _pickSlot == i) ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.55f));
            }
            float tx = 50 * k, tw = b.Size.Width - tx - 6 * k;
            ModernSkin.Text(g, _rowName[i], ModernSkin.Font(12, true), _rowColour[i], new RectangleF(tx, 4 * k, tw, 17 * k));
            ModernSkin.Text(g, _rowLine[i], ModernSkin.Font(10), _lineColour[i], new RectangleF(tx, 20 * k, tw, 15 * k));
            if (_rowDura[i] >= 0)
            {
                RectangleF bar = new RectangleF(tx, 37 * k, tw, 3 * k);
                using (var br = new SolidBrush(Color.FromArgb(4, 6, 10))) g.FillRectangle(br, bar);
                float d = _rowDura[i];
                Color c = d <= 0 ? ModernTheme.Bad : d < 0.2f ? ModernTheme.Warn : ModernTheme.Hex("#4fe07a");
                if (d > 0) using (var br = new SolidBrush(c)) g.FillRectangle(br, bar.X, bar.Y, Math.Max(1, bar.Width * d), bar.Height);
            }
        }

        private void PaintResult(Graphics g, PaintBox b)
        {
            FishingSession f = F;
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            bool caught = f.ResultKind == 1, lost = f.ResultKind == 2;
            using (GraphicsPath p = ModernSkin.RoundRect(r, 10 * k))
            {
                using (var br = new LinearGradientBrush(new RectangleF(0, -1, b.Size.Width, b.Size.Height + 2), Color.FromArgb(240, 14, 24, 42), Color.FromArgb(240, 4, 9, 18), LinearGradientMode.Vertical)) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, caught ? ModernTheme.Alpha(ModernTheme.Gold, 0.7f) : Color.FromArgb(64, 150, 190, 255));
            }
            bool icon = f.ResultImage >= 0;
            float tx = (icon ? 58 : 14) * k;
            if (icon)
                using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(8 * k, 9 * k, 40 * k, 40 * k), 8 * k))
                using (var br = new SolidBrush(caught ? ModernTheme.Mix(ModernTheme.Gold, Color.Black, 0.18f) : Color.FromArgb(90, 0, 0, 0))) g.FillPath(br, p);
            string eyebrow = T(caught ? ClientTextKeys.ModernCompEyebrowCatch : lost ? ClientTextKeys.ModernCompEyebrowMissed : ClientTextKeys.ModernCompEyebrowLineIn).ToUpperInvariant();
            ModernSkin.Text(g, eyebrow, ModernSkin.Font(9, true), caught ? ModernTheme.Gold : lost ? ModernTheme.Bad : ModernTheme.Dim, new RectangleF(tx, 5 * k, b.Size.Width - tx - 8 * k, 13 * k));
            ModernSkin.Text(g, f.ResultTitle, ModernSkin.Font(13, true), caught ? ModernTheme.Gold : Color.White, new RectangleF(tx, 18 * k, b.Size.Width - tx - 8 * k, 19 * k));
            ModernSkin.Text(g, f.ResultSub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(tx, 37 * k, b.Size.Width - tx - 8 * k, 15 * k));
        }

        private void DrawResultIcon(PaintBox b)
        {
            int img = F.ResultImage;
            if (img < 0) return;
            CompKit.Fit(Libraries.Items, img, new RectangleF(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(11), S(36), S(36)), ModernRoot.K);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            // FishingDialog.Show: refused with NoFishingRod without a rod
            if (User == null || !User.HasFishingRod)
            {
                Close();
                ModernRoot.Instance?.Popups.Confirm(T(ClientTextKeys.NoFishingRod), PopupButtons.Ok, PopupKind.Info, null, false, T(ClientTextKeys.ModernHudWinFishing));
                return;
            }
            _scene.SetPhase(PhaseNow());
            _seenVersion = -1;
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        protected override void Closed()
        {
            _scene?.Snap(); // reopened later: jump to the then-current pose instead of replaying what was missed
            ClosePicker();
            // "Esc exit": every close reels in while the line is out (FishingStatusDialog.Cancel on Closeall)
            if (F.EscExit && F.Fishing && User != null && User.HasFishingRod) Network.Enqueue(new C.FishingCast { CastOut = false });
        }

        private static FishingScene.Phase PhaseNow() => !F.Fishing ? FishingScene.Phase.Idle : F.FoundFish ? FishingScene.Phase.Bite : FishingScene.Phase.Wait;

        public override void Process()
        {
            base.Process();
            long now = CMain.Time;
            FishingSession f = F;
            if (f.Version != _seenVersion)
            {
                _seenVersion = f.Version;
                if (f.SceneOutcome >= 0) { _scene.Outcome((FishingScene.Outcomes)f.SceneOutcome); f.SceneOutcome = -1; }
                if (f.SceneSlip) { _scene.Slip(); f.SceneSlip = false; }
                _scene.SetPhase(PhaseNow());
                _sig = long.MinValue;
                _nextPoll = 0;
            }
            _scene.Tick(now);
            bool showResult = f.ResultKind >= 0 && now < f.ResultUntil;
            if (_result.Visible != showResult) _result.Visible = showResult;
            if (showResult && _resultShown != f.Version) { _resultShown = f.Version; _result.Invalidate(); }
            if (now < _nextPoll) return;
            _nextPoll = now + 200;
            if (_rodName == null || _rodName.IsDisposed) return;
            if (User == null || !User.HasFishingRod) { Close(); return; }
            UserItem rod = Rod;
            if (rod?.Slots != null && _partWatch.Poll(MirGridType.Fishing, rod.Slots, 0, int.MaxValue, true))
            {
                for (int i = 0; i < _cells.Length; i++) _cells[i].Refresh(false);
                _sig = _pickSig = long.MinValue;
                StepSwap();
            }
            if (_bagWatch.Poll(MirGridType.Inventory, User.Inventory, 0, int.MaxValue, true)) { _sig = _pickSig = long.MinValue; StepSwap(); }
            Refresh();
            RenderCast();
            if (_pickSlot >= 0) RenderPicker();
        }

        // ------------------------------------------------------------------ casting

        /// <summary>MapControl.CanFish for the user's own direction (GameScene's cast throttle), guarded against the map edge.</summary>
        private static bool CanFish()
        {
            UserObject u = User;
            MapControl map = GameScene.Scene?.MapControl;
            if (u == null || map == null || F.Fishing) return false;
            Point p = Functions.PointMove(u.CurrentLocation, u.Direction, 3);
            if (p.X < 0 || p.Y < 0 || p.X >= map.Width || p.Y >= map.Height) return false;
            return map.CanFish(u.Direction);
        }

        /// <summary>Why the user cannot cast now ("" = can).</summary>
        private static string CastRefusal()
        {
            if (User == null) return T(ClientTextKeys.ModernCompNotInGame);
            if (F.Fishing) return T(ClientTextKeys.ModernCompLineOut);
            if (CanFish()) return string.Empty;
            return T(User.HasFishingRod ? ClientTextKeys.ModernCompFaceWater : ClientTextKeys.NoFishingRod);
        }

        /// <summary>MapControl.CheckInput's cast, facing the user's own direction: then FishingTime and C.FishingCast CastOut = true.</summary>
        private void Cast()
        {
            if (!CanFish() || CMain.Time < _nextCast) return;
            _nextCast = CMain.Time + 500;
            User.FishingTime = CMain.Time;
            Network.Enqueue(new C.FishingCast { CastOut = true });
            _nextPoll = 0;
        }

        /// <summary>FishButton (a bite) / FishingStatusDialog.Cancel (waiting): C.FishingCast CastOut = false.</summary>
        private void ReelIn()
        {
            if (!F.Fishing || CMain.Time < _nextCast) return;
            _nextCast = CMain.Time + 500;
            Network.Enqueue(new C.FishingCast { CastOut = false });
        }

        private void WaterClicked()
        {
            if (F.Fishing && F.FoundFish) ReelIn();
            else if (!F.Fishing) Cast();
        }

        private static bool CanAutoCast()
        {
            UserItem rod = Rod;
            return rod?.Slots != null && rod.Slots.Length >= 5 && rod.Slots[(int)FishingSlot.Reel] != null;
        }

        /// <summary>AutoCastButton: C.FishingChangeAutocast (only with a Reel in the rod).</summary>
        private void ToggleAutoCast(bool on)
        {
            if (!CanAutoCast()) { _autoCast.Set(false); return; }
            F.AutoCast = on;
            Network.Enqueue(new C.FishingChangeAutocast { AutoCast = on });
        }

        private void RenderCast()
        {
            string why = CastRefusal();
            _cast.SetDisabled(why.Length > 0, why);
            if (!F.Fishing) _status.Text = T(why.Length > 0 ? ClientTextKeys.ModernCompFaceWaterToCast : ClientTextKeys.ModernCompCastYourLine).ToUpperInvariant();
            // FishingStatusDialog_BeforeDraw: losing the reel turns auto cast off on the server
            bool can = CanAutoCast();
            if (F.AutoCast && !can)
            {
                F.AutoCast = false;
                Network.Enqueue(new C.FishingChangeAutocast { AutoCast = false });
            }
            _autoCast.Set(F.AutoCast);
            _autoCast.Lock(!can, T(ClientTextKeys.ModernCompAutoCastNeedsReel));
            _escExit.Set(F.EscExit);
        }

        // ------------------------------------------------------------------ values

        private static string NameOf(UserItem item) =>
            item.Info != null ? (string.IsNullOrEmpty(item.Info.FriendlyName) ? item.Info.Name : item.Info.FriendlyName) + (item.Count > 1 ? " (" + item.Count + ")" : string.Empty) : "#" + item.ItemIndex;

        /// <summary>A part's stats in fishing terms (PlayerObject.FishingCast: Hook CriticalRate = flexibility, Float AC = bite chance,
        /// Finder AC = increase after a miss, other parts MaxAC = catch chance, MaxMAC = auto reel); score ranks parts of one type.</summary>
        private static string PartStats(UserItem item, out float score)
        {
            score = 0;
            ItemInfo info = item.Info;
            if (info == null) return string.Empty;
            int St(Stat k) => info.Stats?[k] ?? 0;
            int Add(Stat k) => item.AddedStats?[k] ?? 0;
            string s = string.Empty;
            int flex = St(Stat.CriticalRate) + Add(Stat.CriticalRate);
            if (flex != 0) { s = T(ClientTextKeys.ModernCompFlexibility, flex); score += flex; }
            int minAc = St(Stat.MinAC), maxAc = St(Stat.MaxAC) + Add(Stat.MaxAC);
            if (minAc != 0 || maxAc != 0)
            {
                string part = info.Type == ItemType.Float ? T(ClientTextKeys.ModernCompBiteChance, minAc, maxAc) : info.Type == ItemType.Finder ? T(ClientTextKeys.ModernCompAfterMiss, minAc, maxAc) : T(ClientTextKeys.ModernCompCatchPlus, maxAc);
                s = s.Length > 0 ? s + " · " + part : part;
                score += info.Type == ItemType.Float || info.Type == ItemType.Finder ? (minAc + maxAc) / 2f : maxAc;
            }
            int reel = St(Stat.MaxMAC) + Add(Stat.MaxMAC);
            if (reel != 0) { string part = T(ClientTextKeys.ModernCompAutoReel, reel); s = s.Length > 0 ? s + " · " + part : part; score += reel; }
            return s;
        }

        /// <summary>Durability as a ratio (null for stacks / none).</summary>
        private static float Dura(UserItem item) => item.Info == null || item.Info.StackSize > 1 || item.MaxDura <= 0 ? -1 : Math.Clamp(item.CurrentDura / (float)item.MaxDura, 0f, 1f);

        private static int Spares(ItemType type)
        {
            int n = 0;
            UserItem[] inv = User?.Inventory;
            if (inv != null) for (int i = 0; i < inv.Length; i++) if (inv[i]?.Info != null && inv[i].Info.Type == type) n++;
            return n;
        }

        private long Signature()
        {
            FishingSession f = F;
            long h = f.Version * 7L + (f.AutoCast ? 1 : 0) + (f.EscExit ? 2 : 0) + _pickSlot * 13L;
            UserItem rod = Rod;
            if (rod != null)
            {
                h = h * 31 + (long)rod.UniqueID + (rod.Info != null ? 1 : 0);
                if (rod.Slots != null) for (int i = 0; i < rod.Slots.Length; i++) h = h * 31 + (long)(rod.Slots[i]?.UniqueID ?? 0) + (rod.Slots[i]?.Count ?? 0) * 3 + (rod.Slots[i]?.CurrentDura ?? 0);
            }
            return h;
        }

        private void Refresh()
        {
            long sig = Signature();
            if (sig == _sig) return;
            _sig = sig;
            FishingSession f = F;
            UserItem rod = Rod;
            ItemInfo info = rod?.Info;
            UserItem[] slots = rod?.Slots ?? Array.Empty<UserItem>();
            _rodName.Text = rod != null ? NameOf(rod) : T(ClientTextKeys.ModernCompNoRod);
            _rodName.Colour = info != null && info.Grade != ItemGrade.None ? ModernTheme.Grade(info.Grade) : Color.White;
            _rodSub.Text = rod != null ? T(ClientTextKeys.ModernCompRodSub, slots.Length) : T(ClientTextKeys.ModernCompHoldRod);
            for (int i = 0; i < Parts.Length; i++)
            {
                var d = Parts[i];
                int slot = (int)d.Slot;
                bool show = slot < slots.Length;
                if (_rows[i].Visible != show) _rows[i].Visible = show;
                if (!show) continue;
                UserItem item = slots[slot];
                if (item != null && item.Info == null) GameScene.RequestItemInfo(item.ItemIndex);
                int spare = Spares(d.Type);
                float dura = item != null ? Dura(item) : -1;
                bool worn = dura >= 0 && item.CurrentDura <= 0; // DamagedFishingItem deletes a part at 0 on its next use
                string name = item != null ? NameOf(item) : T(ClientTextKeys.ModernCompNoPart, T(d.Name).ToLowerInvariant());
                string line;
                Color lc = ModernTheme.Dim;
                if (item == null)
                {
                    line = spare > 0 ? T(ClientTextKeys.ModernCompSpareInBag, spare) : T(ClientTextKeys.ModernCompNoneInBagHint, T(d.Hint).ToLowerInvariant());
                    if (spare > 0) lc = ModernTheme.Accent;
                }
                else if (worn) { line = T(ClientTextKeys.ModernCompWornOut); lc = ModernTheme.Bad; }
                else
                {
                    string stats = PartStats(item, out _);
                    line = d.Slot == FishingSlot.Bait ? T(ClientTextKeys.ModernCompBaitLeft, W.N(item.Count)) + (stats.Length > 0 ? " · " + stats : string.Empty) : stats.Length > 0 ? stats : T(d.Hint);
                }
                Color nc = item == null ? ModernTheme.Dim : item.Info != null && item.Info.Grade != ItemGrade.None ? ModernTheme.Grade(item.Info.Grade) : ModernTheme.Text;
                if (name != _rowName[i] || line != _rowLine[i] || nc != _rowColour[i] || lc != _lineColour[i] || dura != _rowDura[i])
                {
                    _rowName[i] = name; _rowLine[i] = line; _rowColour[i] = nc; _lineColour[i] = lc; _rowDura[i] = dura;
                    _rows[i].Invalidate();
                }
                string hint = T(d.Name) + ": " + T(d.Hint) + "\n" + T(ClientTextKeys.ModernCompPartRowHint) + (dura >= 0 ? "\n" + T(ClientTextKeys.ModernCompDurability, item.CurrentDura / 1000, item.MaxDura / 1000) : string.Empty);
                if (_rows[i].Hint != hint) _rows[i].Hint = hint;
            }

            // casts left (the fitted bait only)
            int bait = slots.Length > (int)FishingSlot.Bait ? slots[(int)FishingSlot.Bait]?.Count ?? 0 : 0;
            bool baitShown = rod != null && slots.Length > (int)FishingSlot.Bait;
            if (_bait.Visible != baitShown) _bait.Visible = baitShown;
            _bait.Text = bait > 0 ? T(bait == 1 ? ClientTextKeys.ModernCompCastLeft : ClientTextKeys.ModernCompCastsLeft, W.N(bait)) : T(ClientTextKeys.ModernCompNoBait);
            _bait.Kind = bait <= 0 ? ButtonKind.Warn : ButtonKind.Normal;
            _bait.Tooltip = T(bait > 0 ? ClientTextKeys.ModernCompBaitHintChip : ClientTextKeys.ModernCompFitBait);

            // FishingStatusDialog: chance, progress, phase
            bool fishing = f.Fishing;
            _chanceText.Text = fishing ? f.Chance + "%" : "-";
            _chance.Set(fishing ? Math.Clamp(f.Chance, 0, 100) / 100f : 0, null, null);
            _progress.Set(fishing ? Math.Clamp(f.Progress, 0, 100) / 100f : 0, null, null);
            bool bite = fishing && f.FoundFish;
            _progress.SetColours(bite ? ModernTheme.Hex("#a8661a") : ModernTheme.Hex("#1d63a8"), bite ? ModernTheme.Gold : ModernTheme.Hex("#59c2ff"));
            _progress.Opacity = fishing ? 1f : 0.35f;
            string tally = f.Casts > 0 ? T(f.Casts == 1 ? ClientTextKeys.ModernCompTallyOne : ClientTextKeys.ModernCompTally, f.Casts, f.Caught, f.Lost) : string.Empty;
            _tally.Text = tally;
            if (fishing)
            {
                _status.Text = T(bite ? ClientTextKeys.ModernCompBiting : ClientTextKeys.ModernCompWaitingBite).ToUpperInvariant();
                _status.Colour = bite ? ModernTheme.Gold : ModernTheme.Hex("#e8f1ff");
                _status.FontSize = bite ? 16 : 14;
            }
            else
            {
                _status.Colour = ModernTheme.Hex("#e8f1ff");
                _status.FontSize = 14;
            }
            _reel.Text = T(bite ? ClientTextKeys.ModernCompReelInBang : ClientTextKeys.ModernCompReelUp);
            _reel.Kind = bite ? ButtonKind.Main : ButtonKind.Normal;
            if (_cast.Visible == fishing) _cast.Visible = !fishing;
            if (_reel.Visible != fishing) _reel.Visible = fishing;
        }

        // ------------------------------------------------------------------ part picker

        private void RowClicked(int i)
        {
            if (ItemMoves.Selected != null) return; // a picked-up item: the cell click places it
            if (_pickSlot == i) ClosePicker();
            else OpenPicker(i);
        }

        private void OpenPicker(int i)
        {
            ClosePicker();
            _pickSlot = i;
            _pickSig = long.MinValue;
            RenderPicker();
            for (int k = 0; k < 5; k++) _rows[k].Invalidate();
        }

        private void ClosePicker()
        {
            _picker?.Dispose();
            _picker = null;
            if (_pickSlot >= 0 && _rows[_pickSlot] != null && !_rows[_pickSlot].IsDisposed) _rows[_pickSlot].Invalidate();
            _pickSlot = -1;
            _pickSig = long.MinValue;
        }

        private struct Candidate
        {
            public int Index;
            public UserItem Item;
            public string Why;
            public float Score;
        }

        /// <summary>Bag items of a type, the usable ones first, best first.</summary>
        private static List<Candidate> Candidates(ItemType type)
        {
            var list = new List<Candidate>();
            UserItem[] inv = User?.Inventory;
            if (inv == null) return list;
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                if (it?.Info == null || it.Info.Type != type) continue;
                PartStats(it, out float score);
                float d = Dura(it);
                list.Add(new Candidate { Index = i, Item = it, Why = ItemMoves.IsLocked(MirGridType.Inventory, i) ? T(ClientTextKeys.ModernWinWaitingServer) : string.Empty, Score = score + (d >= 0 ? d / 100f : 0) });
            }
            list.Sort((a, b) => a.Why.Length > 0 != b.Why.Length > 0 ? (a.Why.Length > 0 ? 1 : -1) : b.Score.CompareTo(a.Score));
            return list;
        }

        private void RenderPicker()
        {
            int i = _pickSlot;
            UserItem rod = Rod;
            if (i < 0 || rod?.Slots == null || (int)Parts[i].Slot >= rod.Slots.Length) { ClosePicker(); return; }
            var d = Parts[i];
            int slot = (int)d.Slot;
            UserItem cur = rod.Slots[slot];
            List<Candidate> list = Candidates(d.Type);
            bool fishing = F.Fishing, freeBag = false;
            UserItem[] inv = User.Inventory;
            for (int k = 0; k < inv.Length; k++) if (inv[k] == null) { freeBag = true; break; }
            bool busy = _swap != null || ItemMoves.IsLocked(MirGridType.Fishing, slot);
            long sig = slot * 7L + (long)(cur?.UniqueID ?? 0) + (cur?.Count ?? 0) * 3 + (cur?.CurrentDura ?? 0) + (fishing ? 11 : 0) + (freeBag ? 13 : 0) + (busy ? 17 : 0);
            for (int k = 0; k < list.Count; k++) sig = sig * 31 + (long)list[k].Item.UniqueID + list[k].Item.Count * 3 + list[k].Why.Length;
            if (_picker != null && sig == _pickSig) return;
            _pickSig = sig;
            _picker?.Dispose();

            const int pw = 290;
            int rows = (cur != null ? 1 : 0) + Math.Max(1, list.Count);
            int ph = Math.Min(330, 40 + rows * 46 + 6);
            int top = TitleHeight;
            int rowY = (int)Math.Round(_rows[i].Location.Y / ModernRoot.K);
            int y = Math.Max(top + 6, Math.Min(rowY, top + HeightHud - ph - 6));
            string title = cur != null ? T(ClientTextKeys.ModernCompChangePart, T(d.Name).ToLowerInvariant()) : T(ClientTextKeys.ModernCompChoosePart, T(d.Name).ToLowerInvariant());
            string sub = list.Count > 0 ? T(ClientTextKeys.ModernCompSpareInBagShort, list.Count) : T(ClientTextKeys.ModernCompNoneOfThese, T(d.Name).ToLowerInvariant());
            _picker = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                {
                    using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.97f))) g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, ModernTheme.Frame);
                }
                ModernSkin.Text(g, title, ModernSkin.Font(12, true), Color.White, new RectangleF(10 * k, 6 * k, b.Size.Width - 20 * k, 16 * k));
                ModernSkin.Text(g, sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(10 * k, 21 * k, b.Size.Width - 20 * k, 14 * k));
                using var pen = new Pen(W.Over(ModernTheme.Frame, 0.6f), 1f);
                g.DrawLine(pen, 0, 37 * k, b.Size.Width, 37 * k);
            })
            { Parent = this, Location = new Point(S(RodW + 6), S(y)), Size = new Size(S(pw), S(ph)) };
            var view = new ScrollView(pw - 8, ph - 44) { Parent = _picker, Location = new Point(S(4), S(40)), Background = ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.97f) };
            view.Framed = false;
            int ry = 0, rw = (int)Math.Round(view.InnerWidth / ModernRoot.K);
            if (cur != null)
            {
                string why = busy ? T(ClientTextKeys.ModernWinWaitingServer) : fishing ? T(ClientTextKeys.ModernCompReelInFirst) : !freeBag ? T(ClientTextKeys.ModernCompBagFull) : string.Empty;
                PickRow(view.Content, ry, rw, -1, T(ClientTextKeys.ModernCompTakeOffPart, NameOf(cur)), T(ClientTextKeys.ModernCompBackToBag), T(ClientTextKeys.ModernCompRemove), why, false,
                    () => { ClosePicker(); ItemMoves.Use(MirGridType.Fishing, slot); });
                ry += 46;
            }
            bool best = list.Count > 1 && list[0].Why.Length == 0;
            for (int k = 0; k < list.Count; k++)
            {
                Candidate c = list[k];
                bool merge = cur != null && d.Type == ItemType.Bait && cur.ItemIndex == c.Item.ItemIndex && cur.Count < (c.Item.Info?.StackSize ?? 1);
                string act = T(cur == null ? ClientTextKeys.ModernCompFit : merge ? ClientTextKeys.ModernCompAdd : ClientTextKeys.ModernCompSwap);
                string why = c.Why.Length > 0 ? c.Why : busy ? T(ClientTextKeys.ModernWinWaitingServer) : cur != null && !merge && fishing ? T(ClientTextKeys.ModernCompReelInFirst) : cur != null && !merge && !freeBag ? T(ClientTextKeys.ModernCompBagFull) : string.Empty;
                float dura = Dura(c.Item);
                string stats = PartStats(c.Item, out _);
                string line = (d.Type == ItemType.Bait ? T(ClientTextKeys.ModernCompInStack, W.N(c.Item.Count)) : string.Empty);
                if (stats.Length > 0) line = line.Length > 0 ? line + " · " + stats : stats;
                if (dura >= 0) line = (line.Length > 0 ? line + " · " : string.Empty) + T(ClientTextKeys.ModernCompDurability, c.Item.CurrentDura / 1000, c.Item.MaxDura / 1000);
                ulong uid = c.Item.UniqueID;
                PickRow(view.Content, ry, rw, c.Index, NameOf(c.Item), line, act, why, best && k == 0, () => { ClosePicker(); Choose(slot, uid, merge); });
                ry += 46;
            }
            if (list.Count == 0)
                W.Label(view.Content, T(ClientTextKeys.ModernCompGetPartsHint, T(d.Name).ToLowerInvariant()), 8, ry + 8, rw - 16, 30, 10, false, ModernTheme.Dim, StringAlignment.Center, true);
            view.ContentHeight = S(Math.Max(ry, 44) + 2);
            _picker.BringToFront();
        }

        private static void PickRow(MirControl parent, int y, int w, int bagSlot, string title, string sub, string act, string why, bool best, Action run)
        {
            bool can = why.Length == 0;
            UserItem item = bagSlot >= 0 ? ItemMoves.GetItem(MirGridType.Inventory, bagSlot) : null;
            Color gc = item?.Info != null && item.Info.Grade != ItemGrade.None ? ModernTheme.Grade(item.Info.Grade) : ModernTheme.Text;
            var row = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                {
                    using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                    if (can && b.Hovered) ModernSkin.Stroke(g, p, ModernTheme.Accent);
                }
                float a = can ? 1f : 0.5f, tx = 46 * k;
                float aw = ModernSkin.Measure(act.ToUpperInvariant(), ModernSkin.Font(10, true)) + 4;
                float tw = b.Size.Width - tx - aw - 10 * k;
                Font tf = ModernSkin.Font(11, true);
                ModernSkin.Text(g, title, tf, ModernSkin.Dim(gc, a), new RectangleF(tx, 4 * k, tw - (best ? 36 * k : 0), 15 * k));
                if (best)
                {
                    float bx = tx + Math.Min(tw - 36 * k, ModernSkin.Measure(title, tf) + 5 * k);
                    using (var br = new SolidBrush(ModernTheme.Gold)) using (GraphicsPath bp = ModernSkin.RoundRect(new RectangleF(bx, 6 * k, 30 * k, 11 * k), 3 * k)) g.FillPath(br, bp);
                    ModernSkin.Text(g, W.T(ClientTextKeys.ModernCompBest), ModernSkin.Font(8, true), Color.Black, new RectangleF(bx, 6 * k, 30 * k, 11 * k), StringAlignment.Center, StringAlignment.Center, false);
                }
                ModernSkin.Text(g, sub, ModernSkin.Font(9), ModernSkin.Dim(ModernTheme.Dim, a), new RectangleF(tx, 19 * k, tw, 13 * k));
                if (!can) ModernSkin.Text(g, why, ModernSkin.Font(9), ModernTheme.Bad, new RectangleF(tx, 31 * k, tw, 13 * k));
                ModernSkin.Text(g, act.ToUpperInvariant(), ModernSkin.Font(10, true), can ? ModernTheme.Accent : ModernTheme.Dim, new RectangleF(b.Size.Width - aw - 6 * k, 0, aw, b.Size.Height), StringAlignment.Far);
            })
            { Parent = parent, Location = new Point(0, ModernRoot.S(y)), Size = new Size(ModernRoot.S(w), ModernRoot.S(43)), Hint = can ? null : why };
            if (bagSlot >= 0) new ModernItemCell(MirGridType.Inventory, bagSlot, 34) { Parent = row, Location = new Point(ModernRoot.S(5), ModernRoot.S(4)), ReadOnly = true };
            else row.AfterPaint = b => W.DrawIcon(ModernIcons.ChevronDown, new Rectangle(b.DisplayLocation.X + ModernRoot.S(14), b.DisplayLocation.Y + ModernRoot.S(13), ModernRoot.S(16), ModernRoot.S(16)), ModernTheme.Dim);
            if (can) row.Click += (o, e) => run();
        }

        /// <summary>Fit a bag item into a slot: straight in when the slot is empty (or same-kind bait tops up the stack); otherwise a
        /// swap: take the old part off first (the server fits nothing over a part), then the new one once the slot is free.</summary>
        private void Choose(int slot, ulong uid, bool merge)
        {
            UserItem rod = Rod;
            if (rod?.Slots == null || slot >= rod.Slots.Length) return;
            UserItem cur = rod.Slots[slot];
            if (cur == null || merge)
            {
                UseBag(uid);
                return;
            }
            _swap = (slot, cur.UniqueID, uid);
            ItemMoves.Use(MirGridType.Fishing, slot);
            StepSwap(); // refused before sending (fishing, bag full): the old part is still there and unlocked
        }

        private static void UseBag(ulong uid)
        {
            ItemSlotRef at = ItemMoves.Find(MirGridType.Inventory, uid);
            if (at == null || at.Locked) return;
            ItemMoves.Use(MirGridType.Inventory, at.ItemSlot);
        }

        /// <summary>Advances a swap: fits the new part once the old one has left the slot; drops the swap when the removal was
        /// refused or the new part is gone.</summary>
        private void StepSwap()
        {
            if (_swap == null) return;
            var s = _swap.Value;
            UserItem rod = Rod;
            if (rod?.Slots == null || s.Slot >= rod.Slots.Length) { _swap = null; return; }
            UserItem now = rod.Slots[s.Slot];
            if (now != null)
            {
                if (now.UniqueID != s.From || !ItemMoves.IsLocked(MirGridType.Fishing, s.Slot)) _swap = null;
                return;
            }
            if (ItemMoves.IsLocked(MirGridType.Fishing, s.Slot)) return;
            _swap = null;
            UseBag(s.To);
        }
    }
}
