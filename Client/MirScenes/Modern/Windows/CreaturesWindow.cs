// Ported from WebClient/src/ui/modern/windows/creatures.ts (CreaturesWindow): the new UI's intelligent creature window over
// GameScene.User.IntelligentCreatures (UserInformation, S.UpdateIntelligentCreatureList, S.NewIntelligentCreature,
// S.IntelligentCreatureEnableRename). Desktop counterpart: IntelligentCreatureDialog + its Options / Grade dialogs; its rules
// are kept:
//  - showing the window asks the server for live updates (C.RequestIntelligentCreatureUpdates true), closing stops them;
//  - opening selects the summoned creature; the selection is a PetType resolved per refresh (never a stale object);
//  - Summon is off while another creature is out, Release is off while this one is out and asks for the creature's name again
//    (case-insensitive; a mismatch is the "Verification Failed!!" chat line);
//  - Rename only after S.IntelligentCreatureEnableRename (CompanionState.CreatureRenameEnabled) and once per grant, the name rule
//    of IntelligentCreatureDialog.CreatureNameReg;
//  - the pickup mode switches only to a mode the creature's rules allow; the item filter follows SetItemFilter; the minimum grade
//    is Filter.PickupGrade.
// Summon / Dismiss / Release go out as C.UpdateIntelligentCreature and lock the actions until the next list
// (IntelligentCreaturesChanged) or 4 s. A plain update (name, mode, filter, grade) gets no answer, so the model entry is written
// before sending, like the classic dialog. Feed = a creature food (ItemType.Pets, Shape 23) from the bag through ItemMoves.Use,
// only for the creature that is out. The blackstone ring is BlackstoneTime / 3 h.
// Desktop changes: the sprites are Prguse2 frames drawn at render time (the default loop; the extra loop every 8 s on the
// stage), a frame step redraws the window (100 ms gate, only while shown); the grade is a chip row instead of a select; the
// web's "rare finds" count is left out (S.IntelligentCreaturePickup reaches no UI member); the new-creature name prompt lives
// in ModernUi.NewIntelligentCreature (works while the window is closed).
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
    public sealed class CreaturesWindow : ModernWindow
    {
        public const string WindowKey = "creatures";
        private const int WidthHud = 920, HeightHud = 530, ListW = 200, StageX = 201, StageW = 300, CfgX = 515, CfgW = 391;
        /// <summary>IntelligentCreatureObject: Fullness runs 0..10000.</summary>
        public const int MaxFull = 10000;
        /// <summary>PlayerObject UseItem ItemType.Pets: Shape 23 = fullness food.</summary>
        public const int FoodShape = 23;
        public const long BlackstoneProduceTime = 10800;
        private const int PendingMs = 4000, ExtraEvery = 8000;

        private static readonly ClientTextKeys[] FilterNames =
        {
            ClientTextKeys.AllItems, ClientTextKeys.Gold, ClientTextKeys.Weapons, ClientTextKeys.Armours, ClientTextKeys.Helmets, ClientTextKeys.Boots,
            ClientTextKeys.Belts, ClientTextKeys.Jewelry, ClientTextKeys.Others,
        };
        private static readonly ItemGrade[] Grades = { ItemGrade.None, ItemGrade.Common, ItemGrade.Rare, ItemGrade.Legendary, ItemGrade.Mythical, ItemGrade.Heroic };

        private static string T(ClientTextKeys key) => W.T(key);
        private static string T(ClientTextKeys key, params object[] args) => W.T(key, args);

        private IntelligentCreatureType _sel = IntelligentCreatureType.None;
        private IntelligentCreatureType _pending = IntelligentCreatureType.None;
        private long _pendingUntil;
        private bool _hasPending;

        private ScrollView _list;
        private readonly List<(PaintBox Card, IntelligentCreatureType Type)> _cards = new List<(PaintBox, IntelligentCreatureType)>();
        private readonly List<int> _cardFrame = new List<int>();
        private long _listSig = long.MinValue;
        private PaintBox _pearls;
        private int _pearlCount = -1;
        private ModernPanel _main;
        private CompNote _none;
        private PaintBox _stage;
        private int _stageFrame = -1;
        private bool _extra;
        private long _extraStart, _nextExtra, _nextAnim;
        private ModernLabel _name, _sub, _stageNote;
        private ModernButton _rename, _summon, _release, _feed;
        private ProgressRing _fullRing, _stoneRing;
        private ModernLabel _fullHead, _fullText, _stoneHead, _stoneText;
        private Segmented<IntelligentCreaturePickupMode> _mode;
        private ModernLabel _modeHint;
        private readonly ModernLabel[] _rules = new ModernLabel[8];
        private Chips<int> _filters;
        private ModernLabel _filterCount;
        private Chips<ItemGrade> _grade;
        private ModernPanel _cfg;
        private long _nextPoll, _sig = long.MinValue;
        private readonly GridWatch _bagWatch = new GridWatch();

        public CreaturesWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinCreatures), 42, 30)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
        }

        internal ModernButton SummonButton => _summon;
        internal ModernButton ReleaseButton => _release;
        internal ModernButton RenameButton => _rename;
        internal ModernButton FeedButton => _feed;
        internal Chips<int> FilterChips => _filters;
        internal Chips<ItemGrade> GradeChips => _grade;
        internal Segmented<IntelligentCreaturePickupMode> ModeControl => _mode;
        internal PaintBox Card(int i) => i >= 0 && i < _cards.Count ? _cards[i].Card : null;
        internal IntelligentCreatureType Selected => _sel;
        internal bool Waiting => _hasPending;

        private static UserObject User => GameScene.User;
        private static List<ClientIntelligentCreature> Creatures => GameScene.User?.IntelligentCreatures;

        private static ClientIntelligentCreature ByType(IntelligentCreatureType t)
        {
            List<ClientIntelligentCreature> list = Creatures;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].PetType == t) return list[i];
            return null;
        }

        private static bool IsOut(ClientIntelligentCreature c) => c != null && User != null && User.CreatureSummoned && User.SummonedCreatureType == c.PetType;

        /// <summary>The selected creature (falls back to the first).</summary>
        private ClientIntelligentCreature Cur()
        {
            ClientIntelligentCreature c = ByType(_sel);
            if (c == null && Creatures != null && Creatures.Count > 0)
            {
                c = Creatures[0];
                Select(c.PetType);
            }
            return c;
        }

        private void Select(IntelligentCreatureType t)
        {
            if (t == _sel) return;
            _sel = t;
            _extra = false;
            _stageFrame = -1;
            _nextExtra = CMain.Time + 3000;
            _listSig = _sig = long.MinValue;
        }

        public static string TypeName(IntelligentCreatureType t) => RegexFunctions.SeperateCamelCase(t.ToString());

        /// <summary>The expiry text: DateTime.MinValue = never (PlayerObject.RefreshCreaturesTimeLeft).</summary>
        private static string Expiry(ClientIntelligentCreature c, out bool never)
        {
            never = c.Expire == DateTime.MinValue;
            if (never) return T(ClientTextKeys.ModernCompPermanent);
            double left = (c.Expire - CMain.Now).TotalSeconds;
            return left > 0 ? T(ClientTextKeys.ModernCompTimeLeft, HudKit.FmtDuration(left)) : T(ClientTextKeys.ModernCompExpiring);
        }

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            const int top = TitleHeight;

            _list = new ScrollView(ListW, HeightHud - 76) { Parent = this, Location = new Point(0, S(top)), Background = W.Page };
            _list.Framed = false;
            _pearls = new PaintBox(PaintPearls) { Parent = this, Location = new Point(S(10), S(top + HeightHud - 66)), Size = new Size(S(ListW - 20), S(56)), Hint = T(ClientTextKeys.ModernCompPearlsHint) };
            W.VDivider(this, ListW, top, HeightHud);
            _listSig = long.MinValue;
            _cards.Clear();
            _cardFrame.Clear();

            _none = new CompNote(WidthHud - ListW - 80, 80, false) { Parent = this, Location = new Point(S(ListW + 40), S(top + 200)) };
            _none.Text = T(ClientTextKeys.NoCreatures);
            _main = new ModernPanel { Parent = this, Location = new Point(S(StageX), S(top)), Size = new Size(S(WidthHud - StageX), S(HeightHud)) };

            // stage
            int sx = 12, w = StageW - 24;
            _stage = new PaintBox(PaintStage) { Parent = _main, Location = new Point(S(sx + (w - 240) / 2), S(12)), Size = new Size(S(240), S(190)), NotControl = true };
            _stage.AfterPaint = DrawStageSprite;
            _name = W.Label(_main, string.Empty, sx, 210, w - 92, 26, 19, true, Color.White);
            _rename = W.At(new ModernButton(T(ClientTextKeys.ModernCompRename)), _main, sx + w - 86, 212);
            _rename.Size = new Size(S(86), _rename.Size.Height);
            _rename.Tooltip = T(ClientTextKeys.ModernCompRenameHint);
            _rename.Click += (o, e) => Rename();
            _sub = W.Label(_main, string.Empty, sx, 238, w, 16, 11, false, ModernTheme.Dim, StringAlignment.Center);
            int bw = (w - 6) / 2;
            _summon = W.At(new ModernButton(T(ClientTextKeys.ModernCompSummon), ButtonKind.Main, true), _main, sx, 262);
            _summon.Size = new Size(S(bw), _summon.Size.Height);
            _summon.Click += (o, e) => Summon();
            _release = W.At(new ModernButton(T(ClientTextKeys.ModernCompRelease), ButtonKind.Warn, true), _main, sx + bw + 6, 262);
            _release.Size = new Size(S(bw), _release.Size.Height);
            _release.Tooltip = T(ClientTextKeys.ModernCompReleaseHint);
            _release.Click += (o, e) => Release();
            _stageNote = W.Label(_main, string.Empty, sx, 300, w, 34, 11, false, ModernTheme.Dim, StringAlignment.Center, true);
            W.VDivider(_main, StageW, 0, HeightHud);

            // settings
            int cx = CfgX - StageX, cw = CfgW;
            _cfg = new ModernPanel { Parent = _main, Location = new Point(S(cx), S(12)), Size = new Size(S(cw), S(HeightHud - 24)) };
            int gw = (cw - 10) / 2;
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = _cfg, Location = Point.Empty, Size = new Size(S(gw), S(96)), NotControl = true };
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = _cfg, Location = new Point(S(gw + 10), 0), Size = new Size(S(gw), S(96)), NotControl = true };
            _fullRing = W.At(new ProgressRing(66, 7), _cfg, 10, 15);
            _fullHead = W.Label(_cfg, string.Empty, 86, 10, gw - 92, 18, 14, true, Color.White);
            _fullText = W.Label(_cfg, string.Empty, 86, 28, gw - 92, 30, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            _feed = W.At(new ModernButton(T(ClientTextKeys.ModernCompFeed), ButtonKind.Main), _cfg, 86, 64);
            _feed.Size = new Size(S(gw - 96), _feed.Size.Height);
            _feed.Click += (o, e) => Feed();
            _stoneRing = W.At(new ProgressRing(66, 7) { Colour = ModernTheme.Hex("#b48cff") }, _cfg, gw + 20, 15);
            _stoneHead = W.Label(_cfg, string.Empty, gw + 96, 10, gw - 92, 18, 14, true, Color.White);
            _stoneText = W.Label(_cfg, string.Empty, gw + 96, 28, gw - 92, 60, 10, false, ModernTheme.Dim, StringAlignment.Near, true);

            int y = 110;
            W.Caption(_cfg, T(ClientTextKeys.ModernCompPickup), 0, y, cw);
            var modes = new[]
            {
                new Choice<IntelligentCreaturePickupMode>(IntelligentCreaturePickupMode.Automatic, T(ClientTextKeys.ModernCompAutomatic), null, T(ClientTextKeys.ModernCompAutomaticHint)),
                new Choice<IntelligentCreaturePickupMode>(IntelligentCreaturePickupMode.SemiAutomatic, T(ClientTextKeys.ModernCompSemiAutomatic), null, T(ClientTextKeys.ModernCompSemiAutomaticHint)),
            };
            _mode = W.At(new Segmented<IntelligentCreaturePickupMode>(modes, IntelligentCreaturePickupMode.SemiAutomatic, cw), _cfg, 0, y + 18);
            _mode.Changed += SetMode;
            _modeHint = W.Label(_cfg, string.Empty, 0, y + 46, cw, 30, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            int rw = (cw - 6) / 2;
            for (int i = 0; i < 4; i++)
            {
                int rx = i % 2 * (rw + 6), ry = y + 80 + i / 2 * 40;
                new PaintBox((g, b) => W.Card(g, b.Size, W.Over(ModernTheme.Frame, 0.45f), 0f)) { Parent = _cfg, Location = new Point(S(rx), S(ry)), Size = new Size(S(rw), S(34)), NotControl = true };
                _rules[i * 2] = W.Label(_cfg, string.Empty, rx + 8, ry + 2, rw - 16, 17, 13, true, ModernTheme.Text);
                _rules[i * 2 + 1] = W.Label(_cfg, string.Empty, rx + 8, ry + 18, rw - 16, 14, 10, false, ModernTheme.Dim);
            }

            y += 80 + 2 * 40 + 8;
            W.Caption(_cfg, T(ClientTextKeys.ModernCompCollect), 0, y, cw - 100);
            _filterCount = W.Label(_cfg, string.Empty, cw - 100, y, 100, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            var filters = new List<Choice<int>>();
            for (int i = 0; i < FilterNames.Length; i++) filters.Add(new Choice<int>(i, T(FilterNames[i])));
            _filters = W.At(new Chips<int>(filters, new[] { 0 }, cw, true, true), _cfg, 0, y + 18);
            _filters.Changed += ToggleFilter;
            int gy = y + 18 + (int)Math.Ceiling(_filters.Size.Height / ModernRoot.K) + 8;
            W.Label(_cfg, T(ClientTextKeys.ModernCompMinGrade), 0, gy, cw, 14, 11, false, ModernTheme.Dim);
            var grades = new List<Choice<ItemGrade>>();
            for (int i = 0; i < Grades.Length; i++)
                grades.Add(new Choice<ItemGrade>(Grades[i], Grades[i] == ItemGrade.None ? T(ClientTextKeys.ModernCompAnyGrade) : T(ClientTextKeys.ModernCompGradeUp, GradeName(Grades[i]))));
            _grade = W.At(new Chips<ItemGrade>(grades, new[] { ItemGrade.None }, cw, false, false), _cfg, 0, gy + 18);
            _grade.Changed += sel => { foreach (ItemGrade g in sel) SetGrade(g); };
            int hy = gy + 18 + (int)Math.Ceiling(_grade.Size.Height / ModernRoot.K) + 6;
            W.Label(_cfg, T(ClientTextKeys.ModernCompFilterHint), 0, hy, cw, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);

            _sig = long.MinValue;
            _bagWatch.Reset();
            _nextPoll = 0;
        }

        private static string GradeName(ItemGrade g) => g switch
        {
            ItemGrade.Common => T(ClientTextKeys.Common),
            ItemGrade.Rare => T(ClientTextKeys.Rare),
            ItemGrade.Legendary => T(ClientTextKeys.Legendary),
            ItemGrade.Mythical => T(ClientTextKeys.Mythical),
            ItemGrade.Heroic => T(ClientTextKeys.Heroic),
            _ => T(ClientTextKeys.All),
        };

        // ------------------------------------------------------------------ painters

        private void PaintPearls(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            W.Card(g, b.Size, Color.Empty, 0.05f);
            ModernSkin.Text(g, "◈ " + W.N(Math.Max(0, _pearlCount)), ModernSkin.Font(16, true), ModernTheme.Hex("#e8f4ff"), new RectangleF(0, 6 * k, b.Size.Width, 24 * k), StringAlignment.Center);
            ModernSkin.Text(g, T(ClientTextKeys.ModernCompPearls), ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(0, 30 * k, b.Size.Width, 18 * k), StringAlignment.Center);
        }

        private static void PaintStage(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.75f))) g.FillPath(br, p);
            Region old = g.Clip;
            g.SetClip(p, CombineMode.Intersect);
            // radial-gradient(circle at 50% 62%, accent 28%, transparent 62%)
            using (var gp = new GraphicsPath())
            {
                float rad = Math.Max(b.Size.Width, b.Size.Height) * 0.62f;
                PointF c = new PointF(b.Size.Width / 2f, b.Size.Height * 0.62f);
                gp.AddEllipse(c.X - rad, c.Y - rad, rad * 2, rad * 2);
                using var br = new PathGradientBrush(gp) { CenterPoint = c, CenterColor = W.Over(ModernTheme.Accent, 0.28f), SurroundColors = new[] { ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.75f) } };
                g.FillPath(br, gp);
            }
            // the pedestal glow (::before) and the vignette (::after)
            using (var gp = new GraphicsPath())
            {
                RectangleF e = new RectangleF(b.Size.Width / 2f - 75 * k, b.Size.Height - 52 * k, 150 * k, 26 * k);
                gp.AddEllipse(e);
                using var br = new PathGradientBrush(gp) { CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.5f), SurroundColors = new[] { Color.FromArgb(0, ModernTheme.Accent) } };
                g.FillPath(br, gp);
            }
            using (var gp = new GraphicsPath())
            {
                float rad = (float)Math.Sqrt(b.Size.Width * b.Size.Width + b.Size.Height * b.Size.Height) / 2f;
                gp.AddEllipse(b.Size.Width / 2f - rad, b.Size.Height / 2f - rad, rad * 2, rad * 2);
                using var br = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(150, 0, 0, 0) } };
                br.FocusScales = new PointF(0.55f, 0.55f);
                g.FillPath(br, gp);
            }
            g.Clip = old;
            old.Dispose();
        }

        /// <summary>CreatureButton.DrawCreatureAnimation, 2x on the pedestal (centre 70 % down, like .sprite).</summary>
        private void DrawStageSprite(PaintBox b)
        {
            if (_stageFrame < 0 || Libraries.Prguse2 == null) return;
            Size s = Libraries.Prguse2.GetSize(_stageFrame);
            if (s.IsEmpty) return;
            float k = 2f * ModernRoot.K;
            float w = s.Width * k, h = s.Height * k;
            float cx = b.DisplayLocation.X + b.Size.Width / 2f, cy = b.DisplayLocation.Y + b.Size.Height * 0.62f;
            HudKit.DrawScaled(Libraries.Prguse2, _stageFrame, new RectangleF(cx - w / 2f, cy - h * 0.7f, w, h), Color.White);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            Network.Enqueue(new C.RequestIntelligentCreatureUpdates { Update = true });
            // IntelligentCreatureDialog.Show -> RefreshDialog: the summoned creature's card is selected
            if (User != null && User.CreatureSummoned && ByType(User.SummonedCreatureType) != null) Select(User.SummonedCreatureType);
            _listSig = _sig = long.MinValue;
            _nextExtra = CMain.Time + 3000;
            _nextPoll = 0;
        }

        protected override void Closed()
        {
            Network.Enqueue(new C.RequestIntelligentCreatureUpdates { Update = false });
        }

        /// <summary>A list arrived (ModernUi.IntelligentCreaturesChanged / NewIntelligentCreature / EnableRename): any list answers a
        /// pending Summon / Dismiss / Release.</summary>
        internal void ListChanged(bool answer)
        {
            if (answer) _hasPending = false;
            _listSig = _sig = long.MinValue;
            _nextPoll = 0;
        }

        public override void Process()
        {
            base.Process();
            long now = CMain.Time;
            if (_hasPending && now >= _pendingUntil) { _hasPending = false; _sig = long.MinValue; _nextPoll = 0; }
            if (now >= _nextAnim)
            {
                _nextAnim = now + 100;
                Animate(now);
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 250;
            if (_name == null || _name.IsDisposed) return;
            RefreshList();
            if (_bagWatch.Poll(MirGridType.Inventory, User?.Inventory, 0, int.MaxValue, true)) _sig = long.MinValue;
            Refresh();
        }

        // ------------------------------------------------------------------ actions

        private void Lock(IntelligentCreatureType t)
        {
            _pending = t;
            _hasPending = true;
            _pendingUntil = CMain.Time + PendingMs;
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>SummonButton / DismissButton: locked until the server's list update.</summary>
        private void Summon()
        {
            ClientIntelligentCreature c = Cur();
            if (c == null || _hasPending || User == null) return;
            bool on = IsOut(c);
            if (!on && User.CreatureSummoned) return; // another creature is out (the server refuses)
            Network.Enqueue(new C.UpdateIntelligentCreature { Creature = c, SummonMe = !on, UnSummonMe = on });
            Lock(c.PetType);
        }

        /// <summary>ReleaseButton: type the creature's name again (case-insensitive), then ReleaseMe.</summary>
        private void Release()
        {
            ClientIntelligentCreature c = Cur();
            if (c == null || _hasPending || IsOut(c)) return;
            IntelligentCreatureType type = c.PetType;
            string text = T(ClientTextKeys.ModernCompReleaseText, c.CustomName) + "\n" + T(ClientTextKeys.EnterCreatureNameForVerification);
            ModernRoot.Instance?.Popups.Input(T(ClientTextKeys.ModernCompReleaseTitle), string.Empty, Globals.MaxCharacterNameLength, v =>
            {
                if (v == null || IsDisposed) return;
                ClientIntelligentCreature now = ByType(type);
                if (now == null || _hasPending || IsOut(now)) return;
                if (string.Compare(v, now.CustomName, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    GameScene.Models?.Chat.Add(T(ClientTextKeys.VerificationFailed), ChatType.System);
                    return;
                }
                Network.Enqueue(new C.UpdateIntelligentCreature { Creature = now, ReleaseMe = true });
                Lock(type);
            }, text);
        }

        /// <summary>The creature name rule (IntelligentCreatureDialog.CreatureNameReg: letters and digits, Globals' length).</summary>
        public static bool ValidName(string v) => HeroWindow.ValidName(v);

        /// <summary>CreatureRenameButton (after S.IntelligentCreatureEnableRename; once per grant).</summary>
        private void Rename()
        {
            ClientIntelligentCreature c = Cur();
            if (c == null || !CompanionState.CreatureRenameEnabled) return;
            IntelligentCreatureType type = c.PetType;
            CompanionState.CreatureRenameEnabled = false; // the classic button hides on click
            _sig = long.MinValue;
            ModernRoot.Instance?.Popups.Input(T(ClientTextKeys.ModernCompRenameTitle), c.CustomName, Globals.MaxCharacterNameLength, v =>
            {
                if (v == null || IsDisposed) return;
                if (!ValidName(v))
                {
                    ModernRoot.Instance?.Popups.Confirm(T(ClientTextKeys.CreatureNameLengthRange, Globals.MinCharacterNameLength, Globals.MaxCharacterNameLength), PopupButtons.Ok, PopupKind.Warning, null);
                    return;
                }
                ClientIntelligentCreature now = ByType(type);
                if (now == null) return;
                now.CustomName = v; // no answer for a plain update
                Network.Enqueue(new C.UpdateIntelligentCreature { Creature = now });
                _listSig = _sig = long.MinValue;
                _nextPoll = 0;
            }, T(ClientTextKeys.PleaseEnterCreatureNewName));
        }

        /// <summary>Semi-auto / Automatic: only to a mode the creature's rules allow.</summary>
        private void SetMode(IntelligentCreaturePickupMode v)
        {
            ClientIntelligentCreature c = Cur();
            IntelligentCreatureRules r = c?.CreatureRules;
            if (c == null || r == null || c.petMode == v || _hasPending
                || (v == IntelligentCreaturePickupMode.Automatic && !r.AutoPickupEnabled) || (v == IntelligentCreaturePickupMode.SemiAutomatic && !r.SemiAutoPickupEnabled))
            {
                if (c != null) _mode.Set(c.petMode);
                if (c != null && r != null && c.petMode != v)
                    GameScene.Models?.Chat.Add(T(v == IntelligentCreaturePickupMode.Automatic ? ClientTextKeys.ModernCompNoAutoPickup : ClientTextKeys.ModernCompNoSemiPickup), ChatType.Hint);
                return;
            }
            c.petMode = v;
            Network.Enqueue(new C.UpdateIntelligentCreature { Creature = c });
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        private static int FilterBits(IntelligentCreatureItemFilter f) =>
            (f.PetPickupAll ? 1 : 0) | (f.PetPickupGold ? 2 : 0) | (f.PetPickupWeapons ? 4 : 0) | (f.PetPickupArmours ? 8 : 0) | (f.PetPickupHelmets ? 16 : 0)
            | (f.PetPickupBoots ? 32 : 0) | (f.PetPickupBelts ? 64 : 0) | (f.PetPickupAccessories ? 128 : 0) | (f.PetPickupOthers ? 256 : 0);

        private static bool[] FilterFlags(IntelligentCreatureItemFilter f) => new[]
        {
            f.PetPickupAll, f.PetPickupGold, f.PetPickupWeapons, f.PetPickupArmours, f.PetPickupHelmets, f.PetPickupBoots, f.PetPickupBelts, f.PetPickupAccessories, f.PetPickupOthers,
        };

        /// <summary>IntelligentCreatureOptionsDialog CheckBoxClick: the chip that changed goes through SetItemFilter, sent at once.</summary>
        private void ToggleFilter(HashSet<int> next)
        {
            ClientIntelligentCreature c = Cur();
            if (c?.Filter == null) return;
            bool[] flags = FilterFlags(c.Filter);
            int idx = -1;
            for (int i = 0; i < flags.Length; i++) if (flags[i] != next.Contains(i)) { idx = i; break; }
            if (idx < 0 || _hasPending)
            {
                SyncFilters(c);
                return;
            }
            c.Filter.SetItemFilter(idx);
            Network.Enqueue(new C.UpdateIntelligentCreature { Creature = c });
            SyncFilters(c);
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>IntelligentCreatureOptionsGradeDialog → Filter.PickupGrade.</summary>
        private void SetGrade(ItemGrade g)
        {
            ClientIntelligentCreature c = Cur();
            if (c?.Filter == null) return;
            if (c.Filter.PickupGrade == g || _hasPending)
            {
                _grade.Set(new[] { c.Filter.PickupGrade });
                return;
            }
            c.Filter.PickupGrade = g;
            Network.Enqueue(new C.UpdateIntelligentCreature { Creature = c });
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>The first creature food in the bag (ItemType.Pets, Shape 23), not locked.</summary>
        private static int Food(out ItemInfo info)
        {
            info = null;
            UserItem[] inv = User?.Inventory;
            if (inv == null) return -1;
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                if (it?.Info == null || ItemMoves.IsLocked(MirGridType.Inventory, i)) continue;
                if (it.Info.Type == ItemType.Pets && it.Info.Shape == FoodShape) { info = it.Info; return i; }
            }
            return -1;
        }

        /// <summary>Feed: C.UseItem of a creature food through ItemMoves (the cell locks until the server answers).</summary>
        private void Feed()
        {
            ClientIntelligentCreature c = Cur();
            if (c == null || !IsOut(c) || c.Fullness >= MaxFull) return;
            int i = Food(out _);
            if (i >= 0) ItemMoves.Use(MirGridType.Inventory, i);
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        // ------------------------------------------------------------------ list

        private long ListSignature()
        {
            long h = 11;
            List<ClientIntelligentCreature> list = Creatures;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    h = h * 31 + (int)list[i].PetType * 7 + (list[i].CustomName?.GetHashCode() ?? 0);
            h = h * 31 + (User != null && User.CreatureSummoned ? (int)User.SummonedCreatureType : -1) * 3 + (int)_sel;
            return h;
        }

        private void RefreshList()
        {
            long sig = ListSignature();
            if (sig == _listSig) return;
            _listSig = sig;
            ScrollView v = _list;
            for (int i = v.Content.Controls.Count - 1; i >= 0; i--) v.Content.Controls[i].Dispose();
            _cards.Clear();
            _cardFrame.Clear();
            List<ClientIntelligentCreature> list = Creatures;
            int w = (int)Math.Round(v.InnerWidth / ModernRoot.K) - 18, y = 10;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    ClientIntelligentCreature x = list[i];
                    IntelligentCreatureType type = x.PetType;
                    var card = new PaintBox((g, b) => PaintCard(g, b, type)) { Parent = v.Content, Location = new Point(S(10), S(y)), Size = new Size(S(w), S(60)), Hint = x.CustomName };
                    card.Selected = type == _sel;
                    card.Index = i;
                    card.AfterPaint = DrawCardSprite;
                    card.Click += (o, e) => { if (_sel != type) { Select(type); _nextPoll = 0; } };
                    _cards.Add((card, type));
                    _cardFrame.Add(-1);
                    y += 66;
                }
            v.ContentHeight = S(y + 4);
        }

        private void PaintCard(Graphics g, PaintBox b, IntelligentCreatureType type)
        {
            ClientIntelligentCreature x = ByType(type);
            if (x == null) return;
            float k = ModernRoot.K;
            bool sel = type == _sel;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                using (var br = new SolidBrush(sel ? W.Over(ModernTheme.Accent, 0.12f) : W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, sel ? ModernTheme.Accent : b.Hovered ? W.Over(ModernTheme.Accent, 0.5f) : W.Over(ModernTheme.Frame, 0.55f));
            }
            if (sel) using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, 1, 2 * k, 3 * k, b.Size.Height - 4 * k);
            // the round mini portrait
            RectangleF mini = new RectangleF(7 * k, 7 * k, 46 * k, 46 * k);
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(mini);
                using var br = new PathGradientBrush(gp) { CenterPoint = new PointF(mini.X + mini.Width / 2, mini.Y + mini.Height * 0.6f), CenterColor = W.Over(ModernTheme.Accent, 0.2f), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } };
                g.FillPath(br, gp);
                using var pen = new Pen(ModernTheme.Frame, 1f);
                g.DrawEllipse(pen, mini);
            }
            float tx = 61 * k, tw = b.Size.Width - tx - 6 * k;
            bool on = IsOut(x);
            if (on)
            {
                string tag = T(ClientTextKeys.ModernCompOut);
                int cw = CompKit.ChipWidth(tag);
                CompKit.Chip(g, new RectangleF(b.Size.Width - cw - 5 * k, 5 * k, cw, 12 * k), tag, Color.White, ModernTheme.Accent, ModernSkin.Opaque(ModernTheme.Accent));
                tw -= cw + 4 * k;
            }
            ModernSkin.Text(g, x.CustomName ?? string.Empty, ModernSkin.Font(13, true), Color.White, new RectangleF(tx, 8 * k, tw, 18 * k));
            ModernSkin.Text(g, TypeName(x.PetType) + " · " + Expiry(x, out _), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(tx, 26 * k, b.Size.Width - tx - 6 * k, 15 * k));
            RectangleF bar = new RectangleF(tx, 45 * k, b.Size.Width - tx - 8 * k, 4 * k);
            using (var br = new SolidBrush(Color.FromArgb(4, 6, 10))) g.FillRectangle(br, bar);
            float f = Math.Clamp(x.Fullness / (float)MaxFull, 0f, 1f);
            bool hungry = x.CreatureRules != null && x.Fullness < x.CreatureRules.MinimalFullness;
            if (f > 0)
            {
                RectangleF fill = new RectangleF(bar.X, bar.Y, Math.Max(1, bar.Width * f), bar.Height);
                if (hungry) using (var br = new SolidBrush(ModernTheme.Bad)) g.FillRectangle(br, fill);
                else using (var br = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f), ModernTheme.Accent, LinearGradientMode.Horizontal)) g.FillRectangle(br, fill);
            }
        }

        private void DrawCardSprite(PaintBox b)
        {
            int i = b.Index;
            if (i < 0 || i >= _cards.Count || _cardFrame[i] < 0 || Libraries.Prguse2 == null) return;
            CompKit.Fit(Libraries.Prguse2, _cardFrame[i], new RectangleF(b.DisplayLocation.X + S(9), b.DisplayLocation.Y + S(9), S(42), S(42)), ModernRoot.K);
        }

        // ------------------------------------------------------------------ animation

        /// <summary>CreatureButton.SetCreatureFrames: (default index, count, delay, extra index, count, delay).</summary>
        public static (int DefIdx, int DefCount, int DefDelay, int ExIdx, int ExCount, int ExDelay) Frames(IntelligentCreatureType t) => t switch
        {
            IntelligentCreatureType.BabyPig => (540, 6, 200, 550, 5, 300),
            IntelligentCreatureType.Chick => (570, 4, 350, 580, 10, 200),
            IntelligentCreatureType.Kitten => (600, 6, 250, 610, 10, 200),
            IntelligentCreatureType.BabySkeleton => (630, 11, 200, 650, 7, 250),
            IntelligentCreatureType.Baekdon => (660, 6, 250, 670, 8, 250),
            IntelligentCreatureType.Wimaen => (690, 4, 350, 700, 6, 300),
            IntelligentCreatureType.BlackKitten => (720, 6, 250, 730, 10, 200),
            IntelligentCreatureType.BabyDragon => (750, 6, 300, 760, 7, 250),
            IntelligentCreatureType.OlympicFlame => (780, 6, 300, 790, 10, 200),
            IntelligentCreatureType.BabySnowMan => (810, 6, 300, 820, 6, 300),
            IntelligentCreatureType.Frog => (840, 6, 300, 850, 6, 300),
            IntelligentCreatureType.BabyMonkey => (870, 6, 300, 880, 9, 300),
            IntelligentCreatureType.AngryBird => (1400, 12, 300, 1332, 12, 300),
            IntelligentCreatureType.Foxey => (1430, 9, 300, 1439, 8, 300),
            IntelligentCreatureType.MedicalRat => (1550, 8, 300, 1560, 16, 300),
            IntelligentCreatureType.None => (539, 1, 0, 539, 1, 0),
            _ => (540, 6, 400, 550, 5, 400),
        };

        /// <summary>CreatureButton.DrawCreatureAnimation: the default loop, the extra loop once every 8 s; redraws only on a frame change.</summary>
        private void Animate(long now)
        {
            ClientIntelligentCreature c = ByType(_sel);
            if (c != null && _stage != null)
            {
                var f = Frames(c.PetType);
                int idx;
                if (!_extra && now >= _nextExtra && f.ExCount > 1) { _extra = true; _extraStart = now; }
                if (_extra)
                {
                    long step = (now - _extraStart) / Math.Max(1, f.ExDelay);
                    if (step >= f.ExCount) { _extra = false; _nextExtra = now + ExtraEvery; idx = f.DefIdx; }
                    else idx = f.ExIdx + (int)step;
                }
                else idx = f.DefIdx + (int)(now / Math.Max(1, f.DefDelay) % Math.Max(1, f.DefCount));
                if (idx != _stageFrame)
                {
                    _stageFrame = idx;
                    _stage.Redraw();
                }
            }
            for (int i = 0; i < _cards.Count; i++)
            {
                var f = Frames(_cards[i].Type);
                int idx = f.DefIdx + (int)(now / Math.Max(1, f.DefDelay) % Math.Max(1, f.DefCount));
                if (idx == _cardFrame[i]) continue;
                _cardFrame[i] = idx;
                _cards[i].Card.Redraw();
            }
        }

        // ------------------------------------------------------------------ values

        private long Signature()
        {
            long h = (int)_sel * 7L + (_hasPending ? 3 : 0) + (int)_pending * 11L + (CompanionState.CreatureRenameEnabled ? 5 : 0);
            UserObject u = User;
            if (u == null) return h;
            h = h * 31 + u.PearlCount + (u.CreatureSummoned ? 1 : 0) + (int)u.SummonedCreatureType * 13L;
            List<ClientIntelligentCreature> list = u.IntelligentCreatures;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    ClientIntelligentCreature x = list[i];
                    h = h * 31 + x.Fullness + x.BlackstoneTime * 7 + (int)x.petMode * 3 + x.Expire.Ticks / 10000000 / 60;
                    if (x.Filter != null) h = h * 1031 + FilterBits(x.Filter) * 7 + (int)x.Filter.PickupGrade;
                }
            h = h * 31 + (long)(CMain.Now.Ticks / 10000000); // the expiry and countdown texts follow the clock (1 s steps)
            return h;
        }

        private void Refresh()
        {
            long sig = Signature();
            if (sig == _sig) return;
            _sig = sig;
            UserObject u = User;
            List<ClientIntelligentCreature> list = Creatures;
            bool has = list != null && list.Count > 0;
            if (_main.Visible != has) _main.Visible = has;
            if (_none.Visible == has) _none.Visible = !has;
            int pearls = u?.PearlCount ?? 0;
            if (pearls != _pearlCount) { _pearlCount = pearls; _pearls.Invalidate(); }
            for (int i = 0; i < _cards.Count; i++)
            {
                _cards[i].Card.Selected = _cards[i].Type == _sel;
                _cards[i].Card.Invalidate();
            }
            ClientIntelligentCreature c = Cur();
            if (c == null) return;
            IntelligentCreatureRules r = c.CreatureRules ?? new IntelligentCreatureRules();
            bool on = IsOut(c), busy = _hasPending;
            _name.Text = c.CustomName ?? string.Empty;
            if (_rename.Visible != CompanionState.CreatureRenameEnabled) _rename.Visible = CompanionState.CreatureRenameEnabled;
            string ex = Expiry(c, out bool never);
            _sub.Text = TypeName(c.PetType) + " · " + (never ? T(ClientTextKeys.ModernCompNeverExpires) : ex) + (on ? T(ClientTextKeys.ModernCompFollowing) : string.Empty);

            // Summon / Dismiss / Release (IntelligentCreatureDialog.RefreshUI)
            ClientIntelligentCreature other = u.CreatureSummoned && !on ? ByType(u.SummonedCreatureType) : null;
            _summon.Text = busy && _pending == c.PetType ? T(ClientTextKeys.ModernCompWaiting) : T(on ? ClientTextKeys.ModernCompDismiss : ClientTextKeys.ModernCompSummon);
            _summon.Kind = on ? ButtonKind.Normal : ButtonKind.Main;
            if (busy) _summon.SetDisabled(true, T(ClientTextKeys.ModernWinWaitingServer));
            else if (u.CreatureSummoned && !on) _summon.SetDisabled(true, T(ClientTextKeys.ModernCompDismissFirst, other?.CustomName ?? T(ClientTextKeys.ModernCompOtherCreature)));
            else _summon.SetDisabled(false);
            if (busy) _release.SetDisabled(true, T(ClientTextKeys.ModernWinWaitingServer));
            else if (on) _release.SetDisabled(true, T(ClientTextKeys.ModernCompDismissBeforeRelease));
            else _release.SetDisabled(false);
            bool hungry = c.Fullness < r.MinimalFullness;
            _stageNote.Text = on ? T(hungry ? ClientTextKeys.ModernCompTooHungry : ClientTextKeys.ModernCompCollecting)
                : u.CreatureSummoned ? T(ClientTextKeys.ModernCompOtherOut, other?.CustomName ?? T(ClientTextKeys.ModernCompOtherCreature)) : T(ClientTextKeys.ModernCompResting2);

            // fullness ring (MinimalFullness = what it needs to work)
            _fullRing.Colour = hungry ? ModernTheme.Bad : ModernTheme.Accent;
            _fullRing.Set(c.Fullness / (float)MaxFull, (c.Fullness / 100) + "%");
            _fullHead.Text = T(hungry ? ClientTextKeys.ModernCompHungry : ClientTextKeys.ModernCompFullness);
            _fullHead.Colour = hungry ? ModernTheme.Bad : Color.White;
            _fullText.Text = T(ClientTextKeys.ModernCompFullnessLine, W.N(c.Fullness), W.N(MaxFull), W.N(r.MinimalFullness));
            int food = Food(out ItemInfo foodInfo);
            if (_feed.Visible != on) _feed.Visible = on;
            if (on)
            {
                if (c.Fullness >= MaxFull) _feed.SetDisabled(true, T(ClientTextKeys.ModernCompItIsFull));
                else if (food < 0) _feed.SetDisabled(true, T(ClientTextKeys.ModernCompNoFood));
                else { _feed.SetDisabled(false); _feed.Tooltip = T(ClientTextKeys.ModernCompFeedHint, foodInfo.FriendlyName, W.N(foodInfo.Effect * 100)); }
            }

            // blackstone production (only while out; the server awards the stone itself)
            if (!r.CanProduceBlackStone)
            {
                _stoneRing.Set(0, "–");
                _stoneHead.Text = T(ClientTextKeys.ModernCompBlackstones);
                _stoneText.Text = T(ClientTextKeys.NoProduction);
            }
            else
            {
                long done = Math.Min(BlackstoneProduceTime, Math.Max(0, c.BlackstoneTime));
                _stoneRing.Set(done / (float)BlackstoneProduceTime, (done * 100 / BlackstoneProduceTime) + "%");
                _stoneHead.Text = T(ClientTextKeys.ModernCompBlackstone);
                _stoneText.Text = T(ClientTextKeys.ModernCompNextStone, Functions.PrintTimeSpanFromSeconds(BlackstoneProduceTime - done)) + (on ? string.Empty : T(ClientTextKeys.ModernCompPausedAtHome))
                    + T(ClientTextKeys.ModernCompAlsoPearls);
            }

            // pickup mode, ranges, filter, grade
            _cfg.Opacity = busy ? 0.55f : 1f;
            _mode.Set(c.petMode);
            string mouseKey = W.CapLabel(W.Key(KeybindOptions.CreaturePickup)), areaKey = W.CapLabel(W.Key(KeybindOptions.CreatureAutoPickup));
            _modeHint.Text = c.petMode == IntelligentCreaturePickupMode.Automatic ? T(ClientTextKeys.ModernCompAutoModeText)
                : T(ClientTextKeys.ModernCompSemiModeText, areaKey.Length > 0 ? areaKey : T(ClientTextKeys.CreatureAutoPickup), mouseKey.Length > 0 ? mouseKey : T(ClientTextKeys.CreatureItemPickup));
            SetRule(0, r.AutoPickupEnabled, T(ClientTextKeys.ModernCompCells, r.AutoPickupRange), T(ClientTextKeys.ModernCompAutoRange));
            SetRule(1, r.SemiAutoPickupEnabled, T(ClientTextKeys.ModernCompCells, r.SemiAutoPickupRange), T(ClientTextKeys.ModernCompSemiRange));
            SetRule(2, r.MousePickupEnabled, T(ClientTextKeys.ModernCompCells, r.MousePickupRange), T(ClientTextKeys.ModernCompMouseRange));
            SetRule(3, true, W.N(r.MinimalFullness), T(ClientTextKeys.ModernCompNeedsFullness));
            if (c.Filter != null)
            {
                SyncFilters(c);
                _grade.Set(new[] { c.Filter.PickupGrade });
            }
        }

        private void SetRule(int i, bool on, string value, string what)
        {
            _rules[i * 2].Text = on ? value : "—";
            _rules[i * 2].Colour = on ? ModernTheme.Text : ModernTheme.Dim;
            _rules[i * 2 + 1].Text = what;
        }

        private void SyncFilters(ClientIntelligentCreature c)
        {
            bool[] flags = FilterFlags(c.Filter);
            var on = new List<int>();
            int n = 0;
            for (int i = 0; i < flags.Length; i++) if (flags[i]) { on.Add(i); n++; }
            bool same = on.Count == _filters.Selected.Count;
            if (same) for (int i = 0; i < on.Count; i++) if (!_filters.IsOn(on[i])) same = false;
            if (!same) _filters.Set(on);
            _filterCount.Text = flags[0] ? T(ClientTextKeys.ModernCompEverything) : T(n == 1 ? ClientTextKeys.ModernCompOneKind : ClientTextKeys.ModernCompKinds, n);
        }
    }
}
