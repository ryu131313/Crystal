// Ported from WebClient/src/ui/modern/windows/hero.ts (HeroWindow): the new UI's hero window, a modern take on the desktop's hero
// dialogs (HeroDialogs.cs: HeroManageDialog, HeroInventoryDialog, HeroBehaviourPanel; NewCharacterDialog as hero creation).
// Left: the roster (the server's manage data: GameScene.CurrentHeroInfo, HeroStorage up to MaximumHeroCount - 1; "make active"
// asks, then C.ChangeHero { ListIndex = index + 1 } and waits for S.ChangeHero) and the New hero panel while S.HeroCreateRequest
// stands (classes from CanCreateClass, the name popup, C.NewHero; the S.NewHero result is a popup, ModernUi.NewHeroResult).
// Centre: name, level / class, state chip, the hero's buffs, the paperdoll between the equipment cells (HeroEquipment), HP / MP /
// EXP bars and Summon / Dismiss (C.Chat "@SUMMONHERO", locked until the spawn state changes). Right: Tactics (behaviour cards
// → C.SetHeroBehaviour, shown when S.SetHeroBehaviour answers; auto potion: locked until S.UnlockHeroAutoPot, the HP / MP potion
// cells (HeroHPItem / HeroMPItem) and 0-99 % thresholds → C.SetAutoPotValue once the slider rests), Bag (HeroInventory after the
// two belt cells, cells past Inventory.Length locked), Skills (Hero.Magics with key and auto-cast; set in the Skills window) and
// Stats.
// Desktop changes: the hero belt cells stay on the HUD (the HUD belt's cells 9 / 0), a line here names their keys; item moves are
// ModernItemCell clicks through ItemMoves (no drag); the window polls the hero every 250 ms and repaints only what changed
// (UserHeroObject has no change events); the hero's behaviour comes from CompanionState (ModernEvents.HeroBehaviour since the
// game started). Hotkeys: HeroInventory → Bag, HeroEquipment → Stats, HeroSkills → Skills (ModernUi.Companion.cs).
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
    public sealed class HeroWindow : ModernWindow
    {
        public const string WindowKey = "hero";
        private const int WidthHud = 900, HeightHud = 554, RosterW = 200, MidX = 213, MidW = 326, SideX = 562, SideW = 328, WaitMs = 2500, BagCells = 40;

        private static readonly (EquipmentSlot Slot, ClientTextKeys Name, int Icon)[] LeftSlots =
        {
            (EquipmentSlot.Weapon, ClientTextKeys.ModernWinSlotWeapon, ModernIcons.Sword), (EquipmentSlot.Armour, ClientTextKeys.ModernWinSlotArmour, ModernIcons.Shield),
            (EquipmentSlot.Torch, ClientTextKeys.ModernWinSlotTorch, ModernIcons.Sun), (EquipmentSlot.BraceletL, ClientTextKeys.ModernWinSlotBracelet, ModernIcons.Ring),
            (EquipmentSlot.RingL, ClientTextKeys.ModernWinSlotRing, ModernIcons.Ring),
        };
        private static readonly (EquipmentSlot Slot, ClientTextKeys Name, int Icon)[] RightSlots =
        {
            (EquipmentSlot.Helmet, ClientTextKeys.ModernWinSlotHelmet, ModernIcons.Cap), (EquipmentSlot.Necklace, ClientTextKeys.ModernWinSlotNecklace, ModernIcons.Gem),
            (EquipmentSlot.Amulet, ClientTextKeys.ModernWinSlotAmulet, ModernIcons.Tag), (EquipmentSlot.BraceletR, ClientTextKeys.ModernWinSlotBracelet, ModernIcons.Ring),
            (EquipmentSlot.RingR, ClientTextKeys.ModernWinSlotRing, ModernIcons.Ring),
        };
        private static readonly (EquipmentSlot Slot, ClientTextKeys Name, int Icon)[] BottomSlots =
        {
            (EquipmentSlot.Belt, ClientTextKeys.ModernWinSlotBelt, ModernIcons.Pouch), (EquipmentSlot.Boots, ClientTextKeys.ModernWinSlotBoots, ModernIcons.Walk),
            (EquipmentSlot.Stone, ClientTextKeys.ModernWinSlotStone, ModernIcons.Gem), (EquipmentSlot.Mount, ClientTextKeys.ModernWinSlotMount, ModernIcons.Horse),
        };

        /// <summary>[behaviour, description, icon] (hero.ts BEHAVIOURS).</summary>
        private static readonly (HeroBehaviour Value, ClientTextKeys Text, int Icon)[] Behaviours =
        {
            (HeroBehaviour.Attack, ClientTextKeys.ModernCompBhvAttackText, ModernIcons.Sword), (HeroBehaviour.CounterAttack, ClientTextKeys.ModernCompBhvCounterText, ModernIcons.Shield),
            (HeroBehaviour.Follow, ClientTextKeys.ModernCompBhvFollowText, ModernIcons.Walk), (HeroBehaviour.Custom, ClientTextKeys.ModernCompBhvCustomText, ModernIcons.Gear),
        };

        /// <summary>The Stats tab (CharacterDialog STATS rows, hero values); null = a section break.</summary>
        private static readonly ClientTextKeys?[] StatLabels =
        {
            ClientTextKeys.ModernWinStatAC, ClientTextKeys.ModernWinStatMAC, ClientTextKeys.ModernWinStatDC, ClientTextKeys.ModernWinStatMC, ClientTextKeys.ModernWinStatSC,
            ClientTextKeys.ModernWinStatHP, ClientTextKeys.ModernWinStatMP, null,
            ClientTextKeys.ModernWinStatAccuracy, ClientTextKeys.ModernWinStatAgility, ClientTextKeys.ModernWinStatAttackSpeed, ClientTextKeys.ModernWinStatLuck,
            ClientTextKeys.ModernWinStatCritRate, ClientTextKeys.ModernWinStatCritDamage, ClientTextKeys.ModernWinStatMagicResist, ClientTextKeys.ModernWinStatPoisonResist,
            ClientTextKeys.ModernWinStatHealthRecovery, ClientTextKeys.ModernWinStatManaRecovery, ClientTextKeys.ModernWinStatPoisonRecovery, ClientTextKeys.ModernWinStatHoly, null,
            ClientTextKeys.ModernWinStatBagWeight, ClientTextKeys.ModernWinStatWearWeight, ClientTextKeys.ModernWinStatHandWeight,
        };

        private static string T(ClientTextKeys key) => W.T(key);
        private static string T(ClientTextKeys key, params object[] args) => W.T(key, args);

        private int _tab;

        // roster
        private ScrollView _roster;
        private long _rosterSig = long.MinValue;
        private Segmented<MirClass> _clsSeg;
        private MirClass _createCls = (MirClass)255;
        private MirGender _createGender = MirGender.Male;
        private bool _changing, _creating;
        private long _changeUntil, _createUntil;

        // centre
        private CompNote _noHero;
        private ModernPanel _mid;
        private ModernLabel _name;
        private PaintBox _line, _chip;
        private string _lineA = string.Empty, _lineB = string.Empty, _chipText = string.Empty;
        private Color _lineColour;
        private int _chipState;
        private ModernPanel _buffRow;
        private readonly List<BuffCell> _buffs = new List<BuffCell>();
        private long _buffSig = long.MinValue, _nextBuffTick;
        private Paperdoll _doll;
        private ModernLabel _ghost;
        private readonly List<(ModernItemCell Cell, CellDecor Decor, string Name)> _slots = new List<(ModernItemCell, CellDecor, string)>();
        private CompMeter _hp, _mp, _xp;
        private ModernButton _summon;
        private ModernLabel _midNote;
        private int _summonFrom = -1;
        private long _summonUntil;

        // side
        private Tabs _tabs;
        private readonly ModernPanel[] _panes = new ModernPanel[4];
        private readonly CompNote[] _needNotes = new CompNote[4];
        private readonly ModernPanel[] _needBodies = new ModernPanel[4];
        private ModernLabel _bhvLabel;
        private readonly PaintBox[] _bhvCards = new PaintBox[4];
        private int _asked = -1, _bhvSig = -1;
        private long _askedUntil;
        private PaintBox _lockBadge;
        private bool _unlocked;
        private readonly PotRow[] _pots = new PotRow[2];
        private readonly List<ModernItemCell> _bagCells = new List<ModernItemCell>();
        private readonly List<PaintBox> _bagLocks = new List<PaintBox>();
        private ModernLabel _bagCount, _weightText;
        private CompMeter _weight;
        private ModernLabel _skillCount;
        private ScrollView _skillList;
        private long _skillSig = long.MinValue;
        private readonly List<PaintBox> _statCells = new List<PaintBox>();
        private readonly string[] _statText = new string[StatLabels.Length];
        private ModernLabel _beltNote;

        private long _nextPoll, _sig = long.MinValue;
        private readonly GridWatch _eqWatch = new GridWatch(), _bagWatch = new GridWatch();
        private readonly Action _poke;
        private readonly Action<bool> _pokeBool;
        private readonly Action<HeroSpawnState> _onSpawn;
        private readonly Action<HeroBehaviour> _onBehaviour;

        private sealed class PotRow
        {
            public PaintBox Box;
            public ModernItemCell Cell;
            public CellDecor Decor;
            public ModernLabel Title, Name, Pc;
            public Slider Slider;
            public int Shown = -1, Pending = -1, Draft = -1;
            public long PendingUntil, SendAt;
        }

        public HeroWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinHero), 40, 50)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _poke = () => _nextPoll = 0;
            _pokeBool = _ => _nextPoll = 0;
            _onSpawn = s =>
            {
                if (_summonFrom >= 0 && (int)s != _summonFrom) _summonFrom = -1;
                _nextPoll = 0;
            };
            _onBehaviour = b =>
            {
                if (_asked == (int)b) _asked = -1;
                _nextPoll = 0;
            };
        }

        internal int TabShown => _tab;
        internal ModernButton SummonButton => _summon;
        internal ScrollView Roster => _roster;
        internal PaintBox BehaviourCard(int i) => i >= 0 && i < 4 ? _bhvCards[i] : null;
        internal Slider PotSlider(int i) => i >= 0 && i < 2 ? _pots[i]?.Slider : null;
        internal bool Changing => _changing;
        internal bool Creating => _creating;

        private static GameScene Scene => GameScene.Scene;
        private static UserHeroObject Hero => GameScene.Hero;
        private static HeroSpawnState Spawn => Scene?.HeroSpawnState ?? HeroSpawnState.None;
        private static bool HasHero => Scene?.HasHero ?? false;
        /// <summary>A live hero: its details arrived (S.HeroInformation) and it is out (or fell while out).</summary>
        private static bool Live => Hero != null && Spawn >= HeroSpawnState.Summoned;
        private static bool Dead => Spawn == HeroSpawnState.Dead || (Live && Hero.Dead);

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            const int top = TitleHeight;

            _roster = new ScrollView(RosterW, HeightHud) { Parent = this, Location = new Point(0, S(top)), Background = W.Page };
            _roster.Framed = false;
            _rosterSig = long.MinValue;
            W.VDivider(this, RosterW, top, HeightHud);
            W.VDivider(this, SideX - 10, top, HeightHud);

            BuildMid(top);
            BuildSide(top);

            _sig = _skillSig = _buffSig = long.MinValue;
            _eqWatch.Reset();
            _bagWatch.Reset();
            _nextPoll = 0;
        }

        private void BuildMid(int top)
        {
            _noHero = W.At(new CompNote(MidW, 70), this, MidX, top + 200);
            const int ox = RosterW + 1, x0 = MidX - ox;
            _mid = new ModernPanel { Parent = this, Location = new Point(S(ox), S(top)), Size = new Size(S(SideX - 10 - ox), S(HeightHud)) };
            int y = 10;
            _name = W.Label(_mid, string.Empty, x0, y, MidW - 90, 24, 18, true, Color.White);
            _line = new PaintBox(PaintLine) { Parent = _mid, Location = new Point(S(x0), S(y + 24)), Size = new Size(S(MidW - 90), S(16)), NotControl = true };
            _chip = new PaintBox(PaintStateChip) { Parent = _mid, Location = new Point(S(x0 + MidW - 84), S(y + 10)), Size = new Size(S(84), S(20)), NotControl = true };

            _buffRow = new ModernPanel { Parent = _mid, Location = new Point(S(x0), S(y + 46)), Size = new Size(S(MidW), S(BuffCell.H)) };
            _buffs.Clear();

            int dy = y + 92;
            _doll = new Paperdoll { Parent = _mid, Location = new Point(S(x0 + 50), S(dy)), Size = new Size(S(MidW - 100), S(228)), Bleed = S(12) };
            _ghost = W.Label(_mid, T(ClientTextKeys.ModernCompFallenGhost), x0 + 50, dy + 100, MidW - 100, 24, 12, true, ModernTheme.Bad, StringAlignment.Center);
            _slots.Clear();
            for (int i = 0; i < LeftSlots.Length; i++) AddSlot(LeftSlots[i], x0, dy + i * 47);
            for (int i = 0; i < RightSlots.Length; i++) AddSlot(RightSlots[i], x0 + MidW - 40, dy + i * 47);
            int bx = x0 + (MidW - (4 * 40 + 3 * 6)) / 2;
            for (int i = 0; i < BottomSlots.Length; i++) AddSlot(BottomSlots[i], bx + i * 46, dy + 235);

            int by = dy + 235 + 48;
            _hp = W.At(new CompMeter(MidW, 15, ModernTheme.Hex("#e04848"), ModernTheme.Hex("#8a1616")), _mid, x0, by);
            _mp = W.At(new CompMeter(MidW, 15, ModernTheme.Hex("#4a8cff"), ModernTheme.Hex("#16388a")), _mid, x0, by + 19);
            _xp = W.At(new CompMeter(MidW, 15, ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.5f), ModernTheme.Accent, true), _mid, x0, by + 38);
            _xp.NotControl = false;

            _summon = W.At(new ModernButton(T(ClientTextKeys.ModernCompSummon), ButtonKind.Main, true), _mid, x0, by + 62);
            _summon.Size = new Size(S(MidW), _summon.Size.Height);
            _summon.Tooltip = T(ClientTextKeys.ModernCompSummonHint);
            _summon.Click += (o, e) => Summon();
            _midNote = W.Label(_mid, string.Empty, x0, by + 98, MidW, 30, 10, false, ModernTheme.Dim, StringAlignment.Center, true);
        }

        private void AddSlot((EquipmentSlot Slot, ClientTextKeys Name, int Icon) d, int x, int y)
        {
            var c = new ModernItemCell(MirGridType.HeroEquipment, (int)d.Slot, 40) { Parent = _mid, Location = new Point(S(x), S(y)) };
            _slots.Add((c, new CellDecor(c, d.Icon), T(d.Name)));
        }

        private void BuildSide(int top)
        {
            string[] names = { T(ClientTextKeys.ModernCompTabTactics), T(ClientTextKeys.ModernWinBag), T(ClientTextKeys.ModernWinSkills), T(ClientTextKeys.ModernWinStats) };
            _tabs = new Tabs(names, _tab, SideW) { Parent = this, Location = new Point(S(SideX), S(top + 8)) };
            _tabs.Changed += SelectTab;
            int py = top + 44;
            for (int i = 0; i < 4; i++)
            {
                _panes[i] = new ModernPanel { Parent = this, Location = new Point(S(SideX), S(py)), Size = new Size(S(SideW), S(HeightHud - 44 - 8)), Visible = i == _tab };
                _needNotes[i] = new CompNote(SideW, 44) { Parent = _panes[i], Location = Point.Empty };
                _needBodies[i] = new ModernPanel { Parent = _panes[i], Location = Point.Empty, Size = _panes[i].Size };
            }
            BuildTactics(_needBodies[0]);
            BuildBag(_needBodies[1]);
            BuildSkills(_needBodies[2]);
            BuildStats(_needBodies[3]);
        }

        private void BuildTactics(MirControl p)
        {
            W.Caption(p, T(ClientTextKeys.ModernCompBehaviour), 0, 0, 160);
            _bhvLabel = W.Label(p, string.Empty, 160, 0, SideW - 160, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            int cw = (SideW - 6) / 2;
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var card = new PaintBox(PaintBehaviour) { Parent = p, Location = new Point(S(i % 2 * (cw + 6)), S(20 + i / 2 * 64)), Size = new Size(S(cw), S(58)), Index = i };
                card.AfterPaint = b => W.DrawIcon(Behaviours[b.Index].Icon, new Rectangle(b.DisplayLocation.X + S(8), b.DisplayLocation.Y + S(8), S(22), S(22)),
                    CompanionState.Behaviour == Behaviours[b.Index].Value ? ModernTheme.Accent : ModernTheme.Dim);
                card.Click += (o, e) => SetBehaviour(Behaviours[idx].Value);
                _bhvCards[i] = card;
            }

            int y = 20 + 2 * 64 + 8;
            W.Caption(p, T(ClientTextKeys.ModernCompAutoPotion), 0, y, 160);
            _lockBadge = new PaintBox(PaintLockBadge) { Parent = p, Location = new Point(S(SideW - 110), S(y - 1)), Size = new Size(S(110), S(16)) };
            _lockBadge.AfterPaint = b => W.DrawIcon(_unlocked ? ModernIcons.Unlock : ModernIcons.Lock,
                new Rectangle(b.DisplayLocation.X + b.Size.Width - S(14) - (int)Math.Ceiling(ModernSkin.Measure(LockText, ModernSkin.Font(10))) - S(4), b.DisplayLocation.Y + S(2), S(12), S(12)),
                _unlocked ? ModernTheme.Accent : ModernTheme.Dim);
            y += 20;
            for (int k = 0; k < 2; k++)
            {
                var row = new PotRow();
                int kk = k;
                row.Box = new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = p, Location = new Point(0, S(y + k * 60)), Size = new Size(S(SideW), S(54)) };
                row.Cell = new ModernItemCell(k == 0 ? MirGridType.HeroHPItem : MirGridType.HeroMPItem, 0, 40) { Parent = row.Box, Location = new Point(S(7), S(7)) };
                row.Decor = new CellDecor(row.Cell, ModernIcons.Drop);
                row.Cell.Hint = T(ClientTextKeys.ModernCompPotDropHint, k == 0 ? "HP" : "MP");
                row.Title = W.Label(row.Box, string.Empty, 56, 5, SideW - 56 - 52, 15, 11, true, Color.White);
                row.Name = W.Label(row.Box, string.Empty, 56, 19, SideW - 56 - 52, 14, 10, false, ModernTheme.Dim);
                row.Slider = W.At(new Slider(0, 99, 0, SideW - 56 - 56, 1, false), row.Box, 52, 32);
                row.Slider.Changed += v => PotDragged(kk, v);
                row.Pc = W.Label(row.Box, "0%", SideW - 52, 0, 44, 54, 14, true, ModernTheme.Accent, StringAlignment.Far);
                _pots[k] = row;
            }
            y += 2 * 60;
            W.Label(p, T(ClientTextKeys.ModernCompPotHint), 0, y, SideW, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.Caption(p, T(ClientTextKeys.ModernCompHeroBelt), 0, y + 36, 200);
            _beltNote = W.Label(p, string.Empty, 0, y + 52, SideW, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        private string LockText => T(_unlocked ? ClientTextKeys.ModernCompUnlocked : ClientTextKeys.ModernCompLocked);

        private void BuildBag(MirControl p)
        {
            W.Caption(p, T(ClientTextKeys.ModernWinHeroBag), 0, 0, 200);
            _bagCount = W.Label(p, string.Empty, 200, 0, SideW - 200, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            _bagCells.Clear();
            _bagLocks.Clear();
            int gx = (SideW - (8 * 36 + 7 * 3)) / 2, belt = GameScene.User?.HeroBeltIdx ?? 2;
            for (int i = 0; i < BagCells; i++)
            {
                int x = gx + i % 8 * 39, y = 20 + i / 8 * 39;
                var lk = new PaintBox(PaintLockedCell) { Parent = p, Location = new Point(S(x), S(y)), Size = new Size(S(36), S(36)), Visible = false, Hint = T(ClientTextKeys.ModernCompLocked) };
                lk.AfterPaint = b => W.DrawIcon(ModernIcons.Lock, new Rectangle(b.DisplayLocation.X + S(12), b.DisplayLocation.Y + S(12), S(12), S(12)), ModernSkin.Dim(ModernTheme.Dim, 0.5f));
                var c = new ModernItemCell(MirGridType.HeroInventory, belt + i, 36) { Parent = p, Location = new Point(S(x), S(y)) };
                _bagCells.Add(c);
                _bagLocks.Add(lk);
            }
            int wy = 20 + 5 * 39 + 6;
            W.Label(p, T(ClientTextKeys.ModernWinWeight), 0, wy, 60, 16, 11, false, ModernTheme.Dim);
            _weightText = W.Label(p, string.Empty, 60, wy, 90, 16, 11, true, ModernTheme.Text);
            _weight = W.At(new CompMeter(SideW - 160, 7, ModernTheme.Accent, ModernTheme.Accent), p, 160, wy + 5);
            W.Label(p, T(ClientTextKeys.ModernCompBagHint), 0, wy + 24, SideW, 30, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        private void BuildSkills(MirControl p)
        {
            W.Caption(p, T(ClientTextKeys.ModernCompHeroSkills), 0, 0, 200);
            _skillCount = W.Label(p, string.Empty, 200, 0, SideW - 200, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            _skillList = new ScrollView(SideW, 380) { Parent = p, Location = new Point(0, S(20)), Background = W.Page };
            _skillList.Framed = false;
            var open = W.At(new ModernButton(T(ClientTextKeys.ModernCompSetKeys)), p, 0, 408);
            open.Size = new Size(S(SideW), open.Size.Height);
            open.Click += (o, e) => ModernRoot.Instance?.Wm.Open(SkillsWindow.WindowKey);
            W.Label(p, T(ClientTextKeys.ModernCompSetKeysHint), 0, 436, SideW, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        private void BuildStats(MirControl p)
        {
            _statCells.Clear();
            int cw = (SideW - 4) / 2, col = 0, y = 0;
            for (int i = 0; i < StatLabels.Length; i++)
            {
                if (StatLabels[i] == null)
                {
                    if (col == 1) { col = 0; y += 28; }
                    y += 6;
                    continue;
                }
                int idx = i;
                string label = T(StatLabels[i].Value);
                var cell = new PaintBox((g, b) => PaintStat(g, b, label, _statText[idx])) { Parent = p, Location = new Point(S(col * (cw + 4)), S(y)), Size = new Size(S(cw), S(24)), NotControl = true, Index = i };
                _statCells.Add(cell);
                if (++col == 2) { col = 0; y += 28; }
            }
        }

        // ------------------------------------------------------------------ painters

        private void PaintLine(Graphics g, PaintBox b)
        {
            Font f = ModernSkin.Font(11, true), f2 = ModernSkin.Font(11);
            float w = ModernSkin.Measure(_lineA, f);
            ModernSkin.Text(g, _lineA, f, _lineColour, new RectangleF(0, 0, b.Size.Width, b.Size.Height));
            ModernSkin.Text(g, _lineB, f2, ModernTheme.Dim, new RectangleF(w, 0, Math.Max(1, b.Size.Width - w), b.Size.Height));
        }

        private void PaintStateChip(Graphics g, PaintBox b)
        {
            if (_chipText.Length == 0) return;
            CompKit.StateColours(_chipState, out Color fg, out Color border, out Color fill);
            int w = Math.Min(b.Size.Width - 1, CompKit.ChipWidth(_chipText, 10) + S(6));
            CompKit.Chip(g, new RectangleF(b.Size.Width - w - 0.5f, 0.5f, w, b.Size.Height - 1f), _chipText, fg, border, fill, 10);
        }

        private void PaintBehaviour(Graphics g, PaintBox b)
        {
            var d = Behaviours[b.Index];
            bool on = CompanionState.Behaviour == d.Value, wait = _asked == (int)d.Value, can = CanSetBehaviour;
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                using (var br = new SolidBrush(on ? W.Over(ModernTheme.Accent, 0.14f) : W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                Color border = on ? ModernTheme.Accent : b.Hovered && can ? W.Over(ModernTheme.Accent, 0.55f) : W.Over(ModernTheme.Frame, 0.55f);
                using var pen = new Pen(border, 1f);
                if (wait) pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, p);
            }
            float a = can ? 1f : 0.5f;
            string label = d.Value == HeroBehaviour.CounterAttack ? T(ClientTextKeys.ModernHudHeroCounter) : d.Value.ToLocalizedString();
            ModernSkin.Text(g, label, ModernSkin.Font(12, true), ModernSkin.Dim(Color.White, a), new RectangleF(38 * k, 6 * k, b.Size.Width - 44 * k, 16 * k));
            ModernSkin.Text(g, T(d.Text), ModernSkin.Font(10), ModernSkin.Dim(ModernTheme.Dim, a), new RectangleF(38 * k, 22 * k, b.Size.Width - 44 * k, b.Size.Height - 24 * k),
                StringAlignment.Near, StringAlignment.Near, false, true);
        }

        private void PaintLockBadge(Graphics g, PaintBox b)
        {
            ModernSkin.Text(g, LockText, ModernSkin.Font(10), _unlocked ? ModernTheme.Accent : ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width, b.Size.Height), StringAlignment.Far);
        }

        private static void PaintLockedCell(Graphics g, PaintBox b)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * ModernRoot.K);
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.6f))) g.FillPath(br, p);
            using var pen = new Pen(ModernTheme.Frame, 1f) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, p);
        }

        private static void PaintStat(Graphics g, PaintBox b, string label, string value)
        {
            float k = ModernRoot.K;
            W.Card(g, b.Size, W.Over(ModernTheme.Frame, 0.45f));
            RectangleF r = new RectangleF(8 * k, 0, b.Size.Width - 16 * k, b.Size.Height);
            ModernSkin.Text(g, label, ModernSkin.Font(11), ModernTheme.Dim, r);
            ModernSkin.Text(g, value ?? string.Empty, ModernSkin.Font(11, true), Color.White, r, StringAlignment.Far, StringAlignment.Center, false);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            ModernEvents.HasHero.On(_pokeBool);
            ModernEvents.HeroInformation.On(_poke);
            ModernEvents.HeroInfo.On(_poke);
            ModernEvents.HeroAutoPot.On(_poke);
            ModernEvents.HeroSpawnState.On(_onSpawn);
            ModernEvents.HeroBehaviour.On(_onBehaviour);
            ModernEvents.SkillBars.On(_poke);
            GameScene.Models?.HeroBuffs.Changed.On(_poke);
            _sig = _skillSig = _buffSig = _rosterSig = long.MinValue;
            _nextPoll = 0;
        }

        protected override void Closed()
        {
            ModernEvents.HasHero.Off(_pokeBool);
            ModernEvents.HeroInformation.Off(_poke);
            ModernEvents.HeroInfo.Off(_poke);
            ModernEvents.HeroAutoPot.Off(_poke);
            ModernEvents.HeroSpawnState.Off(_onSpawn);
            ModernEvents.HeroBehaviour.Off(_onBehaviour);
            ModernEvents.SkillBars.Off(_poke);
            GameScene.Models?.HeroBuffs.Changed.Off(_poke);
        }

        /// <summary>Opens the window on a side tab (the hero hotkeys); a second press on the shown tab closes it.</summary>
        internal void ToggleTab(int tab)
        {
            if (IsOpen && _tab == tab)
            {
                Close();
                return;
            }
            Open();
            SelectTab(tab);
            _tabs?.Select(tab);
        }

        internal void SelectTab(int i)
        {
            if (i < 0 || i > 3) return;
            _tab = i;
            for (int k = 0; k < 4; k++)
                if (_panes[k] != null && _panes[k].Visible != (k == i)) _panes[k].Visible = k == i;
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>S.ChangeHero answered (ModernUi.ChangeHero): the roster unlocks and redraws.</summary>
        internal void ChangeAnswered()
        {
            _changing = false;
            _rosterSig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>S.NewHero answered (ModernUi.NewHeroResult shows the box).</summary>
        internal void NewHeroAnswered()
        {
            _creating = false;
            _rosterSig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>The manage data or the create request changed while open.</summary>
        internal void Poke()
        {
            _rosterSig = long.MinValue;
            _nextPoll = 0;
        }

        public override void Process()
        {
            base.Process();
            long now = CMain.Time;
            // the slider sends its value once it rests (hero.ts: on "change", i.e. release)
            for (int k = 0; k < 2; k++)
            {
                PotRow r = _pots[k];
                if (r == null) continue;
                if (r.Draft >= 0 && now >= r.SendAt && !r.Slider.Moving) SendThreshold(k, r.Draft);
                if (r.Pending >= 0 && now >= r.PendingUntil) { r.Pending = -1; _sig = long.MinValue; _nextPoll = 0; }
            }
            if (_summonFrom >= 0 && now >= _summonUntil) { _summonFrom = -1; _nextPoll = 0; }
            if (_asked >= 0 && now >= _askedUntil) { _asked = -1; _nextPoll = 0; }
            if (_changing && now >= _changeUntil) { _changing = false; _rosterSig = long.MinValue; _nextPoll = 0; }
            if (_creating && now >= _createUntil) { _creating = false; _rosterSig = long.MinValue; _nextPoll = 0; }
            if (now >= _nextBuffTick && _buffs.Count > 0)
            {
                _nextBuffTick = now + 250;
                for (int i = 0; i < _buffs.Count; i++) { _buffs[i].UpdateTime(); _buffs[i].Blink(); }
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 250;
            if (_name == null || _name.IsDisposed) return;

            RefreshRoster();
            RefreshBuffs();
            if (Live)
            {
                if (_eqWatch.Poll(MirGridType.HeroEquipment, Hero.Equipment, 0, int.MaxValue, true))
                    for (int i = 0; i < _slots.Count; i++) { _slots[i].Cell.Refresh(false); _slots[i].Decor.Sync(); }
                if (_bagWatch.Poll(MirGridType.HeroInventory, Hero.Inventory, 0, int.MaxValue, true))
                    for (int i = 0; i < _bagCells.Count; i++) _bagCells[i].Refresh(false);
                if (_tab == 2) RefreshSkills();
            }
            Refresh();
        }

        // ------------------------------------------------------------------ requests

        /// <summary>MainDialog.HeroSummonButton: C.Chat "@SUMMONHERO" (the server summons or dismisses); locked until the spawn
        /// state changes (a refusal only prints a chat line) or 2.5 s.</summary>
        private void Summon()
        {
            if (!HasHero || _summonFrom >= 0) return;
            _summonFrom = (int)Spawn;
            _summonUntil = CMain.Time + WaitMs;
            Network.Enqueue(new C.Chat { Message = "@SUMMONHERO" });
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        private bool CanSetBehaviour => Live && !Dead && Spawn == HeroSpawnState.Summoned;

        /// <summary>HeroBehaviourPanel: C.SetHeroBehaviour (the server ignores it unless the hero is out); shown when it answers.</summary>
        private void SetBehaviour(HeroBehaviour b)
        {
            if (!CanSetBehaviour || CompanionState.Behaviour == b || _asked >= 0) return;
            _asked = (int)b;
            _askedUntil = CMain.Time + WaitMs;
            Network.Enqueue(new C.SetHeroBehaviour { Behaviour = b });
            for (int i = 0; i < 4; i++) _bhvCards[i]?.Invalidate();
            _sig = long.MinValue;
        }

        private void PotDragged(int k, int v)
        {
            PotRow r = _pots[k];
            if (r == null || Hero == null || !Hero.AutoPot) return;
            r.Draft = v;
            r.SendAt = CMain.Time + 350;
            r.Pc.Text = v + "%";
            r.Title.Text = T(ClientTextKeys.ModernCompPotBelow, k == 0 ? "HP" : "MP", v);
        }

        /// <summary>HeroInventoryDialog HP / MP button: C.SetAutoPotValue (0-99); shown pending until S.SetAutoPotValue.</summary>
        private void SendThreshold(int k, int value)
        {
            PotRow r = _pots[k];
            r.Draft = -1;
            if (Hero == null || !Hero.AutoPot) return;
            uint v = (uint)Math.Clamp(value, 0, 99);
            uint server = k == 0 ? Hero.AutoHPPercent : Hero.AutoMPPercent;
            if (v == server) { r.Pending = -1; _sig = long.MinValue; _nextPoll = 0; return; }
            r.Pending = (int)v;
            r.PendingUntil = CMain.Time + WaitMs;
            Network.Enqueue(new C.SetAutoPotValue { Stat = k == 0 ? Stat.HP : Stat.MP, Value = v });
            _sig = long.MinValue;
            _nextPoll = 0;
        }

        /// <summary>HeroManageAvatar click: ClientTextKeys.MakeActiveHero, then C.ChangeHero { ListIndex = index + 1 }.</summary>
        private void AskChange(int index)
        {
            if (_changing || index < 0 || index >= GameScene.HeroStorage.Length) return;
            ClientHeroInformation info = GameScene.HeroStorage[index];
            if (info == null) return;
            ModernRoot.Instance?.Popups.Ask(T(ClientTextKeys.MakeActiveHero, info.Name), yes =>
            {
                if (!yes || _changing || IsDisposed || GameScene.HeroStorage[index] != info) return;
                _changing = true;
                _changeUntil = CMain.Time + WaitMs;
                Network.Enqueue(new C.ChangeHero { ListIndex = index + 1 });
                _rosterSig = long.MinValue;
                _nextPoll = 0;
            }, false, PopupKind.Question, T(ClientTextKeys.ModernHudWinHero));
        }

        /// <summary>NewHeroDialog OK: the name popup, then C.NewHero (the result arrives as S.NewHero).</summary>
        private void Create()
        {
            bool[] req = CompanionState.CreateRequest;
            if (_creating || req == null || (int)_createCls >= req.Length || !req[(int)_createCls]) return;
            MirClass cls = _createCls;
            MirGender gender = _createGender;
            ModernRoot.Instance?.Popups.Input(T(ClientTextKeys.ModernCompNewHero), string.Empty, Globals.MaxCharacterNameLength, name =>
            {
                if (name == null || IsDisposed || CompanionState.CreateRequest != req) return;
                if (!ValidName(name))
                {
                    ModernRoot.Instance?.Popups.Confirm(T(ClientTextKeys.ModernCompHeroNameRule, Globals.MinCharacterNameLength, Globals.MaxCharacterNameLength),
                        PopupButtons.Ok, PopupKind.Warning, null, false, T(ClientTextKeys.ModernCompNewHero));
                    return;
                }
                _creating = true;
                _createUntil = CMain.Time + WaitMs * 2; // Envir.CanCreateHero refusals send no S.NewHero
                Network.Enqueue(new C.NewHero { Name = name, Gender = gender, Class = cls });
                _rosterSig = long.MinValue;
                _nextPoll = 0;
            }, T(ClientTextKeys.ModernCompNameHero, cls.ToLocalizedString()));
        }

        /// <summary>A character name: Globals' length, letters and digits only (NewCharacterDialog / the server's rule).</summary>
        internal static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < Globals.MinCharacterNameLength || name.Length > Globals.MaxCharacterNameLength) return false;
            for (int i = 0; i < name.Length; i++)
                if (!char.IsLetterOrDigit(name[i])) return false;
            return true;
        }

        // ------------------------------------------------------------------ roster

        private long RosterSignature()
        {
            long h = 17;
            ClientHeroInformation cur = GameScene.CurrentHeroInfo;
            h = h * 31 + (cur?.Index ?? -1) + (cur?.Level ?? 0) * 7L;
            for (int i = 0; i < GameScene.HeroStorage.Length; i++) h = h * 31 + (GameScene.HeroStorage[i]?.Index ?? -1) * 3L + (GameScene.HeroStorage[i]?.Level ?? 0);
            h = h * 31 + GameScene.MaximumHeroCount;
            UserHeroObject x = Live ? Hero : null;
            h = h * 31 + (x == null ? -1 : x.Level * 11L + (int)x.Class * 3 + (int)x.Gender + (x.Name?.GetHashCode() ?? 0));
            h = h * 31 + (HasHero ? 1 : 0) + (Dead ? 2 : 0) + (Spawn == HeroSpawnState.Summoned ? 4 : 0) + (_changing ? 8 : 0) + (_creating ? 16 : 0);
            bool[] req = ShownRequest;
            if (req != null) for (int i = 0; i < req.Length; i++) h = h * 3 + (req[i] ? 1 : 0);
            h = h * 31 + (req == null ? 0 : req.GetHashCode());
            h = h * 31 + (int)_createCls * 5 + (int)_createGender;
            return h;
        }

        private static bool[] ShownRequest => CompanionState.CreateRequest != null && CompanionState.CreateRequest != CompanionState.DismissedRequest ? CompanionState.CreateRequest : null;

        private void RefreshRoster()
        {
            long sig = RosterSignature();
            if (sig == _rosterSig) return;
            _rosterSig = sig;
            ScrollView v = _roster;
            for (int i = v.Content.Controls.Count - 1; i >= 0; i--) v.Content.Controls[i].Dispose();
            _clsSeg = null;
            int w = (int)Math.Round(v.InnerWidth / ModernRoot.K) - 18, x = 10, y = 10;
            UserHeroObject live = Live ? Hero : null;

            W.Caption(v.Content, T(ClientTextKeys.ModernCompActiveHero), x, y, w);
            y += 18;
            int chip = Dead ? 2 : Spawn == HeroSpawnState.Summoned ? 1 : 0;
            string chipText = T(chip == 2 ? ClientTextKeys.ModernCompFallen : chip == 1 ? ClientTextKeys.ModernCompSummoned : ClientTextKeys.ModernCompResting);
            ClientHeroInformation cur = GameScene.CurrentHeroInfo;
            if (cur != null) y = Card(v.Content, x, y, w, cur.Name, live != null && live.Name == cur.Name ? live.Level : cur.Level, cur.Class, cur.Gender, chipText, chip, true, -1);
            else if (live != null) y = Card(v.Content, x, y, w, live.Name, live.Level, live.Class, live.Gender, chipText, chip, true, -1);
            else y = EmptyCard(v.Content, x, y, w, T(HasHero ? ClientTextKeys.ModernCompSummonToSee : ClientTextKeys.ModernCompNoHeroShort), null);
            y += 8;

            int slots = Math.Min(GameScene.HeroStorage.Length, Math.Max(0, GameScene.MaximumHeroCount - 1));
            if (GameScene.MaximumHeroCount > 0)
            {
                int used = 0;
                for (int i = 0; i < slots; i++) if (GameScene.HeroStorage[i] != null) used++;
                W.Caption(v.Content, T(ClientTextKeys.ModernCompStoredHeroes), x, y, w - 40);
                W.Label(v.Content, used + " / " + slots, x + w - 40, y, 40, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
                y += 18;
                for (int i = 0; i < slots; i++)
                {
                    ClientHeroInformation s = GameScene.HeroStorage[i];
                    if (s != null) y = Card(v.Content, x, y, w, s.Name, s.Level, s.Class, s.Gender, T(ClientTextKeys.ModernCompStored), 0, false, i);
                    else y = EmptyCard(v.Content, x, y, w, T(ClientTextKeys.ModernCompEmptySlot), T(ClientTextKeys.ModernCompEmptySlotHint));
                }
                if (slots == 0) { W.Label(v.Content, T(ClientTextKeys.ModernCompNoStoreSlots), x, y, w, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true); y += 30; }
            }
            else
            {
                W.Caption(v.Content, T(ClientTextKeys.ModernCompStoredHeroes), x, y, w);
                y += 18;
                W.At(new CompNote(w, 58) { Text = T(ClientTextKeys.ModernCompSeeManager) }, v.Content, x, y);
                y += 64;
            }

            bool[] req = ShownRequest;
            if (req != null) y = NewHeroPanel(v.Content, x, y + 4, w, req);
            v.ContentHeight = S(y + 10);
        }

        private int Card(MirControl parent, int x, int y, int w, string name, int level, MirClass cls, MirGender gender, string chip, int chipState, bool active, int storeIndex)
        {
            bool button = storeIndex >= 0;
            bool waiting = button && _changing;
            var card = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                {
                    using (var br = new SolidBrush(active ? W.Over(ModernTheme.Accent, 0.12f) : W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                    Color border = active ? ModernTheme.Accent : button && b.Hovered && !waiting ? W.Over(ModernTheme.Accent, 0.55f) : W.Over(ModernTheme.Frame, 0.55f);
                    ModernSkin.Stroke(g, p, border);
                }
                if (active) using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, 1, 2 * k, 3 * k, b.Size.Height - 4 * k);
                // the avatar well and its class-colour underline
                RectangleF av = new RectangleF(6 * k, 6 * k, 40 * k, 40 * k);
                using (GraphicsPath p = ModernSkin.RoundRect(av, ModernTheme.Radius * k))
                {
                    using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelC), ModernSkin.Opaque(ModernTheme.PanelB), 0.5f))) g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, ModernTheme.Frame);
                }
                using (var br = new SolidBrush(ModernTheme.Class(cls))) g.FillRectangle(br, av.X + 1, av.Bottom - 3 * k, av.Width - 2, 3 * k);
                int cw = CompKit.ChipWidth(chip);
                float tw = b.Size.Width - 54 * k - cw - 6 * k;
                Color dim = waiting ? ModernSkin.Dim(Color.White, 0.45f) : Color.White;
                ModernSkin.Text(g, name ?? string.Empty, ModernSkin.Font(12, true), dim, new RectangleF(52 * k, 8 * k, tw, 18 * k));
                ModernSkin.Text(g, T(ClientTextKeys.ModernCompLvClass, level, cls.ToLocalizedString()), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(52 * k, 26 * k, b.Size.Width - 58 * k, 16 * k));
                CompKit.StateColours(chipState, out Color fg, out Color bd, out Color fill);
                CompKit.Chip(g, new RectangleF(b.Size.Width - cw - 6 * k, 9 * k, cw, 13 * k), chip, fg, bd, fill);
            })
            { Parent = parent, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(52)), NotControl = !button, Index = storeIndex };
            card.AfterPaint = b =>
            {
                // GameScene.HeroAvatar + 370 (HeroManageAvatar's larger picture)
                int index = 1400 + (byte)cls + 10 * (byte)gender + 370;
                CompKit.Fit(Libraries.Prguse, index, new RectangleF(b.DisplayLocation.X + S(7), b.DisplayLocation.Y + S(7), S(38), S(35)));
            };
            if (button)
            {
                card.Hint = waiting ? T(ClientTextKeys.ModernWinWaitingServer) : T(ClientTextKeys.ModernCompMakeActiveTip, name);
                card.Click += (o, e) => AskChange(storeIndex);
            }
            return y + 58;
        }

        private static int EmptyCard(MirControl parent, int x, int y, int w, string text, string hint)
        {
            var card = new PaintBox((g, b) =>
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * ModernRoot.K);
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.55f), 1f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, p);
                ModernSkin.Text(g, text, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(4, 0, b.Size.Width - 8, b.Size.Height), StringAlignment.Center);
            })
            { Parent = parent, Location = new Point(ModernRoot.S(x), ModernRoot.S(y)), Size = new Size(ModernRoot.S(w), ModernRoot.S(40)), NotControl = hint == null, Hint = hint, Index = -1 };
            return y + 46;
        }

        private int NewHeroPanel(MirControl parent, int x, int y, int w, bool[] req)
        {
            var classes = new List<Choice<MirClass>>();
            MirClass[] order = { MirClass.Warrior, MirClass.Wizard, MirClass.Taoist, MirClass.Assassin, MirClass.Archer };
            for (int i = 0; i < order.Length; i++)
                if ((int)order[i] < req.Length && req[(int)order[i]]) classes.Add(new Choice<MirClass>(order[i], HudKit.ClassTag(order[i]), null, order[i].ToLocalizedString()));
            bool known = false;
            for (int i = 0; i < classes.Count; i++) if (classes[i].Value == _createCls) known = true;
            if (!known) _createCls = classes.Count > 0 ? classes[0].Value : (MirClass)255;

            int h = 150;
            new PaintBox((g, b) =>
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * ModernRoot.K);
                using (var br = new SolidBrush(W.Over(ModernTheme.Gold, 0.08f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ModernTheme.Gold);
            })
            { Parent = parent, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(h)), NotControl = true };
            W.Caption(parent, T(ClientTextKeys.ModernCompNewHero), x + 8, y + 8, w - 16, ModernTheme.Gold);
            if (classes.Count > 0)
            {
                _clsSeg = W.At(new Segmented<MirClass>(classes, _createCls, w - 16), parent, x + 8, y + 28);
                _clsSeg.Changed += v => { _createCls = v; };
            }
            else W.Label(parent, T(ClientTextKeys.ModernCompNoClass), x + 8, y + 28, w - 16, 24, 10, false, ModernTheme.Dim);
            var genders = new[] { new Choice<MirGender>(MirGender.Male, T(ClientTextKeys.ModernCompMale)), new Choice<MirGender>(MirGender.Female, T(ClientTextKeys.ModernCompFemale)) };
            var gen = W.At(new Segmented<MirGender>(genders, _createGender, w - 16), parent, x + 8, y + 58);
            gen.Changed += v => _createGender = v;
            int bw = (w - 16 - 5) / 2;
            var ok = W.At(new ModernButton(T(_creating ? ClientTextKeys.ModernCompCreating : ClientTextKeys.ModernCompCreateHero), ButtonKind.Main), parent, x + 8, y + 92);
            ok.Size = new Size(S(bw), ok.Size.Height);
            ok.SetDisabled(_creating || classes.Count == 0, _creating ? T(ClientTextKeys.ModernWinWaitingServer) : T(ClientTextKeys.ModernCompNoClass));
            ok.Click += (o, e) => Create();
            var cancel = W.At(new ModernButton(T(ClientTextKeys.ModernCancel)), parent, x + 8 + bw + 5, y + 92);
            cancel.Size = new Size(S(bw), cancel.Size.Height);
            cancel.Click += (o, e) => { CompanionState.DismissedRequest = req; _rosterSig = long.MinValue; _nextPoll = 0; };
            W.Label(parent, T(ClientTextKeys.ModernCompNewHeroHint), x + 8, y + 120, w - 16, 26, 9, false, ModernTheme.Dim, StringAlignment.Near, true);
            return y + h + 6;
        }

        // ------------------------------------------------------------------ values

        private void RefreshBuffs()
        {
            List<ClientBuff> list = Live ? GameScene.Models?.HeroBuffs.Items : null;
            long sig = 7;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    sig = sig * 31 + (int)list[i].Type * 7 + (list[i].Infinite ? 1 : 0) + (list[i].Paused ? 2 : 0) + list[i].ExpireTime;
            if (sig == _buffSig) return;
            _buffSig = sig;
            for (int i = _buffs.Count - 1; i >= 0; i--) _buffs[i].Dispose();
            _buffs.Clear();
            if (list == null) return;
            int max = Math.Max(1, (int)(MidW / (BuffCell.W + 3)));
            for (int i = 0; i < list.Count && i < max; i++)
                _buffs.Add(new BuffCell(list[i]) { Parent = _buffRow, Location = new Point(S(i * (BuffCell.W + 3)), 0) });
        }

        private void RefreshSkills()
        {
            UserHeroObject hero = Hero;
            List<ClientMagic> list = hero?.Magics;
            long sig = 3;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    sig = sig * 31 + (int)list[i].Spell * 7 + list[i].Level * 3 + list[i].Key + (SkillsWindow.HeroAutoCast(list[i].Spell) ? 1 : 0);
            for (int i = 0; i < CMain.InputKeys.Keylist.Count; i++)
            {
                KeyBind kb = CMain.InputKeys.Keylist[i];
                if (kb.function >= KeybindOptions.HeroSkill1 && kb.function <= KeybindOptions.HeroSkill8) sig = sig * 31 + (int)kb.Key + kb.RequireShift * 3 + kb.RequireCtrl * 5 + kb.RequireAlt * 7;
            }
            if (sig == _skillSig) return;
            _skillSig = sig;
            ScrollView v = _skillList;
            for (int i = v.Content.Controls.Count - 1; i >= 0; i--) v.Content.Controls[i].Dispose();
            int n = list?.Count ?? 0;
            _skillCount.Text = n.ToString();
            int w = (int)Math.Round(v.InnerWidth / ModernRoot.K);
            if (n == 0)
            {
                W.Label(v.Content, T(ClientTextKeys.ModernWinHeroNoSkills), 4, 8, w - 8, 30, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
                v.ContentHeight = S(40);
                return;
            }
            for (int i = 0; i < n; i++)
            {
                ClientMagic m = list[i];
                bool passive = SkillsWindow.IsPassive(m.Spell);
                bool auto = !passive && SkillsWindow.HeroAutoCast(m.Spell);
                string keyText = HeroKeyText(m.Key);
                string badge = passive ? string.Empty : T(auto ? ClientTextKeys.ModernCompAuto : ClientTextKeys.ModernCompManual);
                string sub = T(ClientTextKeys.ModernCompSkillLevel, m.Level) + (passive ? T(ClientTextKeys.ModernCompPassive) : string.Empty);
                bool odd = i % 2 == 0;
                var row = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    if (odd) using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k)) g.FillPath(br, p);
                    RectangleF ic = new RectangleF(6 * k, 5 * k, 30 * k, 30 * k);
                    using (GraphicsPath p = ModernSkin.RoundRect(ic, ModernTheme.Radius * 0.8f * k))
                    {
                        using (var br = new SolidBrush(Color.Black)) g.FillPath(br, p);
                        ModernSkin.Stroke(g, p, ModernTheme.Frame);
                    }
                    float right = b.Size.Width - 6 * k;
                    if (badge.Length > 0)
                    {
                        int bw = CompKit.ChipWidth(badge, 9);
                        right -= bw;
                        CompKit.Chip(g, new RectangleF(right, 13 * k, bw, 14 * k), badge, auto ? ModernTheme.Accent : ModernTheme.Dim, auto ? ModernTheme.Accent : ModernTheme.Frame, W.Page, 9);
                        right -= 6 * k;
                    }
                    if (keyText.Length > 0)
                    {
                        float kw = ModernSkin.Measure(keyText, ModernSkin.Font(10, true)) + 2;
                        right -= kw;
                        ModernSkin.Text(g, keyText, ModernSkin.Font(10, true), ModernTheme.Accent, new RectangleF(right, 0, kw, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                        right -= 6 * k;
                    }
                    float tw = Math.Max(1, right - 44 * k);
                    ModernSkin.Text(g, m.Name ?? m.Spell.ToString(), ModernSkin.Font(12), Color.White, new RectangleF(44 * k, 4 * k, tw, 17 * k));
                    ModernSkin.Text(g, sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(44 * k, 20 * k, tw, 15 * k));
                })
                { Parent = v.Content, Location = new Point(0, S(i * 42)), Size = new Size(S(w), S(40)) };
                row.Hint = T(passive ? ClientTextKeys.ModernCompPassiveSkill : auto ? ClientTextKeys.ModernWinHeroCastsAlone : ClientTextKeys.ModernCompAutoOff);
                int icon = m.Icon * 2;
                row.AfterPaint = b => CompKit.Fit(Libraries.MagIcon2, icon, new RectangleF(b.DisplayLocation.X + S(7), b.DisplayLocation.Y + S(6), S(28), S(28)), 1f * ModernRoot.K);
            }
            v.ContentHeight = S(n * 42 + 4);
        }

        /// <summary>The binding label of a hero magic key (17-24 = HeroSkill1-8), or the desktop default.</summary>
        private static string HeroKeyText(byte key)
        {
            int n = key - 16;
            if (n < 1 || n > 8) return string.Empty;
            string label = W.CapLabel(W.Key(KeybindOptions.HeroSkill1 + n - 1));
            return label.Length > 0 ? label : T(ClientTextKeys.ModernWinShiftCombo, "F" + n);
        }

        private long Signature()
        {
            long h = (int)Spawn * 7 + (HasHero ? 1 : 0) + (_summonFrom >= 0 ? 4 : 0) + _asked * 13L + (int)CompanionState.Behaviour * 101L + _tab * 3001L;
            ClientHeroInformation cur = GameScene.CurrentHeroInfo;
            h = h * 31 + (cur?.Index ?? -1) + (cur?.Level ?? 0);
            h = h * 31 + (CompanionState.CreateRequest != null ? 1 : 0);
            UserHeroObject a = Live ? Hero : null;
            if (a != null)
            {
                h = h * 1000003 + a.Level * 31L + (int)a.Class * 7 + (int)a.Gender * 3 + a.Hair + a.WingEffect * 101L + a.HP * 13L + a.MP * 17L + (a.Dead ? 5 : 0);
                h = h * 1000003 + a.Experience + a.MaxExperience * 3;
                h = h * 1000003 + a.CurrentBagWeight + a.CurrentWearWeight * 7L + a.CurrentHandWeight * 11L + (a.Inventory?.Length ?? 0) * 13L;
                h = h * 31 + (a.AutoPot ? 1 : 0) + a.AutoHPPercent * 3L + a.AutoMPPercent * 7L + (a.HPItem[0]?.ItemIndex ?? 0) * 11L + (a.MPItem[0]?.ItemIndex ?? 0) * 13L;
                if (a.Stats != null)
                    for (int s = 0; s <= (int)Stat.Holy; s++) h = h * 31 + a.Stats[(Stat)s];
                if (a.Equipment != null)
                    for (int i = 0; i < a.Equipment.Length; i++) h = h * 31 + (long)(a.Equipment[i]?.UniqueID ?? 0);
                if (a.Inventory != null)
                    for (int i = 0; i < a.Inventory.Length; i++) h = h * 31 + (a.Inventory[i] == null ? 0 : a.Inventory[i].ItemIndex + a.Inventory[i].Count * 3L);
            }
            for (int k = 0; k < 2; k++) h = h * 31 + (_pots[k]?.Pending ?? -1);
            return h;
        }

        private void Refresh()
        {
            long sig = Signature();
            if (sig == _sig) return;
            _sig = sig;
            UserHeroObject x = Live ? Hero : null;
            ClientHeroInformation cur = GameScene.CurrentHeroInfo;
            bool none = !HasHero && x == null;

            // centre
            if (_mid.Visible == none) _mid.Visible = !none;
            if (_noHero.Visible != none) _noHero.Visible = none;
            _noHero.Text = T(CompanionState.CreateRequest != null ? ClientTextKeys.ModernCompNoHeroYetCreate : ClientTextKeys.ModernCompNoHeroManager);
            string name = x?.Name ?? cur?.Name;
            int level = x != null ? x.Level : cur?.Level ?? 0;
            MirClass cls = x?.Class ?? cur?.Class ?? MirClass.Warrior;
            MirGender gender = x?.Gender ?? cur?.Gender ?? MirGender.Male;
            bool known = name != null;
            _name.Text = known ? name : T(ClientTextKeys.ModernCompYourHero);
            string a = known ? T(ClientTextKeys.ModernWinLevelClass, level, cls.ToLocalizedString()) : string.Empty;
            string bText = known ? " · " + T(gender == MirGender.Female ? ClientTextKeys.ModernCompFemale : ClientTextKeys.ModernCompMale) : T(ClientTextKeys.ModernCompSummonToSeeHere);
            Color lc = known ? ModernTheme.Class(cls) : ModernTheme.Accent;
            if (a != _lineA || bText != _lineB || lc != _lineColour) { _lineA = a; _lineB = bText; _lineColour = lc; _line.Invalidate(); }
            int state = Dead ? 2 : Spawn == HeroSpawnState.Summoned ? 1 : 0;
            string chip = HasHero || x != null ? T(state == 2 ? ClientTextKeys.ModernCompFallen : state == 1 ? ClientTextKeys.ModernCompSummoned : ClientTextKeys.ModernCompResting) : string.Empty;
            if (chip != _chipText || state != _chipState) { _chipText = chip; _chipState = state; _chip.Invalidate(); }

            int maxHp = x?.Stats?[Stat.HP] ?? 0, maxMp = x?.Stats?[Stat.MP] ?? 0;
            _hp.Set(x != null && maxHp > 0 ? x.HP / (float)maxHp : 0, "HP", x != null ? W.N(x.HP) + " / " + W.N(maxHp) : "—");
            _mp.Set(x != null && maxMp > 0 ? x.MP / (float)maxMp : 0, "MP", x != null ? W.N(x.MP) + " / " + W.N(maxMp) : "—");
            double xp = x != null && x.MaxExperience > 0 ? x.Experience / (double)x.MaxExperience : 0;
            _xp.Set((float)xp, "EXP", x != null ? (Math.Floor(xp * 10000) / 100).ToString("0.00") + "%" : "—");
            string xpHint = x != null ? "EXP " + W.N(x.Experience) + " / " + W.N(x.MaxExperience) : string.Empty;
            if (_xp.Hint != xpHint) _xp.Hint = xpHint;

            bool summoned = Spawn == HeroSpawnState.Summoned || Spawn == HeroSpawnState.Dead;
            _summon.Text = T(summoned ? ClientTextKeys.ModernCompDismiss : ClientTextKeys.ModernCompSummon);
            _summon.Kind = summoned ? ButtonKind.Normal : ButtonKind.Main;
            _summon.SetDisabled(!HasHero || _summonFrom >= 0, _summonFrom >= 0 ? T(ClientTextKeys.ModernWinWaitingServer) : T(ClientTextKeys.ModernCompNoHeroShort));
            _midNote.Text = Dead ? T(ClientTextKeys.ModernCompHeroFallen) : !summoned ? T(ClientTextKeys.ModernCompSummonToSeeGear) : x == null ? T(ClientTextKeys.ModernCompWaitingDetails) : string.Empty;

            if (_doll.Visible != (x != null)) _doll.Visible = x != null;
            if (x != null) _doll.Set(DollLook.Of(x.Equipment, x.Level, x.Class, x.Gender, x.Hair, x.WingEffect, x.Gender));
            _doll.Opacity = Dead ? 0.45f : 1f;
            if (_ghost.Visible != Dead) _ghost.Visible = Dead;
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                s.Decor.Sync();
                string hint = x != null && s.Cell.Item == null ? T(ClientTextKeys.ModernWinSlotEmpty, s.Name) : string.Empty;
                if (s.Cell.Hint != hint) s.Cell.Hint = hint;
            }

            // side: the panes need a live hero
            string note = Dead ? T(ClientTextKeys.ModernCompHeroFallen) : !HasHero ? T(ClientTextKeys.ModernCompNoHeroShort) : T(ClientTextKeys.ModernCompSummonToSeeThis);
            for (int i = 0; i < 4; i++)
            {
                bool showBody = x != null;
                if (_needBodies[i].Visible != showBody) _needBodies[i].Visible = showBody;
                if (_needNotes[i].Visible == showBody) _needNotes[i].Visible = !showBody;
                _needNotes[i].Text = note;
            }
            RefreshTactics(x);
            if (x == null) return;
            if (_tab == 1) RefreshBag(x);
            else if (_tab == 3) RefreshStats(x);
        }

        private void RefreshTactics(UserHeroObject x)
        {
            HeroBehaviour cur = CompanionState.Behaviour;
            _bhvLabel.Text = cur == HeroBehaviour.CounterAttack ? T(ClientTextKeys.ModernHudHeroCounter) : cur.ToLocalizedString();
            string why = !CanSetBehaviour ? T(ClientTextKeys.ModernCompBhvSummonFirst) : _asked >= 0 ? T(ClientTextKeys.ModernWinWaitingServer) : string.Empty;
            int bsig = (int)cur * 100 + (_asked + 1) * 10 + (CanSetBehaviour ? 1 : 0);
            for (int i = 0; i < 4; i++)
            {
                if (bsig != _bhvSig) _bhvCards[i].Invalidate();
                if (_bhvCards[i].Hint != why) _bhvCards[i].Hint = why;
            }
            _bhvSig = bsig;
            string k1 = HudKit.ShortKey(HudKit.KeyLabel(KeybindOptions.HeroBelt1)), k2 = HudKit.ShortKey(HudKit.KeyLabel(KeybindOptions.HeroBelt2));
            _beltNote.Text = T(ClientTextKeys.ModernCompBeltOnHud, k1.Length > 0 ? k1 : "-", k2.Length > 0 ? k2 : "-");
            if (x == null) return;

            bool unlocked = x.AutoPot;
            if (unlocked != _unlocked)
            {
                _unlocked = unlocked;
                _lockBadge.Invalidate();
            }
            string lockHint = T(unlocked ? ClientTextKeys.ModernCompAutoPotUnlockedHint : ClientTextKeys.ModernCompAutoPotLockedHint);
            if (_lockBadge.Hint != lockHint) _lockBadge.Hint = lockHint;
            for (int k = 0; k < 2; k++)
            {
                PotRow r = _pots[k];
                string stat = k == 0 ? "HP" : "MP";
                int server = (int)(k == 0 ? x.AutoHPPercent : x.AutoMPPercent);
                if (r.Pending == server) r.Pending = -1;
                int v = r.Draft >= 0 ? r.Draft : r.Pending >= 0 ? r.Pending : server;
                if (r.Shown != v || r.Draft >= 0)
                {
                    r.Shown = v;
                    if (r.Draft < 0) r.Slider.Set(v);
                    r.Pc.Text = v + "%";
                    r.Title.Text = T(ClientTextKeys.ModernCompPotBelow, stat, v);
                }
                r.Pc.Colour = r.Pending >= 0 ? ModernSkin.Dim(ModernTheme.Accent, 0.45f) : ModernTheme.Accent;
                if (r.Slider.Enabled != unlocked) r.Slider.Enabled = unlocked;
                float op = unlocked ? 1f : 0.45f;
                if (r.Box.Opacity != op) r.Box.Opacity = op;
                UserItem item = k == 0 ? x.HPItem[0] : x.MPItem[0];
                ItemInfo info = item?.Info;
                string text = !unlocked ? T(ClientTextKeys.ModernCompLocked) : item != null ? (info != null ? info.FriendlyName : "…") : T(ClientTextKeys.ModernCompNoPotChosen, stat);
                if (unlocked && info != null) text += T(ClientTextKeys.ModernCompPotLeft, W.N(CountHeroItem(x, info.Index)));
                r.Name.Text = text;
                bool show = unlocked;
                if (r.Cell.Visible != show) r.Cell.Visible = show;
                r.Cell.Refresh(false); // S.SetAutoPotItem replaces HPItem / MPItem without an ItemMoves.SlotChanged
                r.Decor.Sync();
            }
        }

        private static int CountHeroItem(UserHeroObject x, int itemIndex)
        {
            int n = 0;
            if (x.Inventory == null) return 0;
            for (int i = 0; i < x.Inventory.Length; i++)
                if (x.Inventory[i] != null && x.Inventory[i].ItemIndex == itemIndex) n += x.Inventory[i].Count;
            return n;
        }

        private void RefreshBag(UserHeroObject x)
        {
            int len = x.Inventory?.Length ?? 0, belt = GameScene.User?.HeroBeltIdx ?? 2, used = 0;
            for (int i = 0; i < _bagCells.Count; i++)
            {
                bool open = belt + i < len;
                if (_bagCells[i].Visible != open) _bagCells[i].Visible = open;
                if (_bagLocks[i].Visible == open) _bagLocks[i].Visible = !open;
                if (open && x.Inventory[belt + i] != null) used++;
            }
            _bagCount.Text = used + " / " + Math.Max(0, Math.Min(BagCells, len - belt));
            int max = x.Stats?[Stat.BagWeight] ?? 0;
            _weightText.Text = W.N(x.CurrentBagWeight) + " / " + W.N(max);
            _weight.Set(max > 0 ? x.CurrentBagWeight / (float)max : 0, null, null);
        }

        private void RefreshStats(UserHeroObject x)
        {
            Stats st = x.Stats;
            if (st == null) return;
            string[] v =
            {
                st[Stat.MinAC] + "-" + st[Stat.MaxAC], st[Stat.MinMAC] + "-" + st[Stat.MaxMAC], st[Stat.MinDC] + "-" + st[Stat.MaxDC],
                st[Stat.MinMC] + "-" + st[Stat.MaxMC], st[Stat.MinSC] + "-" + st[Stat.MaxSC], W.N(x.HP) + "/" + W.N(st[Stat.HP]), W.N(x.MP) + "/" + W.N(st[Stat.MP]),
                "+" + st[Stat.Accuracy], "+" + st[Stat.Agility], st[Stat.AttackSpeed].ToString(), st[Stat.Luck].ToString(), st[Stat.CriticalRate] + "%",
                st[Stat.CriticalDamage].ToString(), "+" + st[Stat.MagicResist], "+" + st[Stat.PoisonResist], "+" + st[Stat.HealthRecovery], "+" + st[Stat.SpellRecovery],
                "+" + st[Stat.PoisonRecovery], "+" + st[Stat.Holy],
                x.CurrentBagWeight + "/" + st[Stat.BagWeight], x.CurrentWearWeight + "/" + st[Stat.WearWeight], x.CurrentHandWeight + "/" + st[Stat.HandWeight],
            };
            for (int i = 0; i < _statCells.Count && i < v.Length; i++)
            {
                int idx = _statCells[i].Index;
                if (_statText[idx] == v[i]) continue;
                _statText[idx] = v[i];
                _statCells[i].Invalidate();
            }
        }
    }
}
