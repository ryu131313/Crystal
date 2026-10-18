// Ported from WebClient/src/ui/modern/windows/inspect.ts (InspectWindow): the new UI's inspect window over the Inspect model
// (GameScene.Models.Inspect), opened when S.PlayerInspect arrives (ModernUi.PlayerInspected). A banner (class badge, name,
// level, guild + rank, lover), the inspected equipment as read-only cells around the paperdoll, and tabs: Stats (their gear
// stats beside yours with a versus bar), Compare (slot by slot, the main stat changes as chips), Profile (S.PlayerProfile's
// text; the tab hides while it is empty). Buttons: Whisper, Invite (C.AddMember), Mail (only when a mail window is
// registered), Add friend / Friend request (C.AddFriend; hidden for a friend), Trade (C.TradeRequest); none for a hero.
// Desktop changes: Whisper asks the line in a popup and sends "/name text" (the chat input is the chat area's: a prefill seam
// is asked for); the gear stats are item + added stats (the web runs the whole RefreshStats gear part: sets, sockets and
// awakening are left out here); the cells' tooltip is the shared ModernTooltip (your level / class, not the inspected one's).
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class InspectWindow : ModernWindow
    {
        public const string WindowKey = "inspect";
        private const int WidthHud = 860, HeightHud = 530, DollW = 300;

        private static readonly EquipmentSlot[] LeftSlots = { EquipmentSlot.Weapon, EquipmentSlot.Armour, EquipmentSlot.Torch, EquipmentSlot.BraceletL, EquipmentSlot.RingL };
        private static readonly EquipmentSlot[] RightSlots = { EquipmentSlot.Helmet, EquipmentSlot.Necklace, EquipmentSlot.Amulet, EquipmentSlot.BraceletR, EquipmentSlot.RingR };
        private static readonly EquipmentSlot[] BottomSlots = { EquipmentSlot.Belt, EquipmentSlot.Boots, EquipmentSlot.Stone, EquipmentSlot.Mount };
        private static readonly (ClientTextKeys Label, Stat Min, Stat Max)[] Ranges =
        {
            (ClientTextKeys.ModernWinStatAC, Stat.MinAC, Stat.MaxAC), (ClientTextKeys.ModernWinStatMAC, Stat.MinMAC, Stat.MaxMAC), (ClientTextKeys.ModernWinStatDC, Stat.MinDC, Stat.MaxDC),
            (ClientTextKeys.ModernWinStatMC, Stat.MinMC, Stat.MaxMC), (ClientTextKeys.ModernWinStatSC, Stat.MinSC, Stat.MaxSC),
        };
        private static readonly (ClientTextKeys Label, Stat Stat)[] Extra =
        {
            (ClientTextKeys.ModernWinStatAccuracy, Stat.Accuracy), (ClientTextKeys.ModernWinStatAgility, Stat.Agility), (ClientTextKeys.ModernWinStatAttackSpeed, Stat.AttackSpeed),
            (ClientTextKeys.ModernWinStatHP, Stat.HP), (ClientTextKeys.ModernWinStatMP, Stat.MP), (ClientTextKeys.ModernWinStatHoly, Stat.Holy), (ClientTextKeys.ModernWinStatLuck, Stat.Luck),
            (ClientTextKeys.ModernWinStatMagicResist, Stat.MagicResist), (ClientTextKeys.ModernWinStatPoisonResist, Stat.PoisonResist), (ClientTextKeys.ModernWinStatReflect, Stat.Reflect),
            (ClientTextKeys.ModernWinStatCritRate, Stat.CriticalRate), (ClientTextKeys.ModernWinStatCritDamage, Stat.CriticalDamage), (ClientTextKeys.ModernWinStatFreeze, Stat.Freezing),
            (ClientTextKeys.ModernWinStatPoisonAttack, Stat.PoisonAttack), (ClientTextKeys.ModernWinStatHPDrain, Stat.HPDrainRatePercent),
        };
        private static readonly (Stat Stat, ClientTextKeys Label)[] Chips =
        {
            (Stat.MaxDC, ClientTextKeys.ModernWinStatDC), (Stat.MaxMC, ClientTextKeys.ModernWinStatMC), (Stat.MaxSC, ClientTextKeys.ModernWinStatSC),
            (Stat.MaxAC, ClientTextKeys.ModernWinStatAC), (Stat.MaxMAC, ClientTextKeys.ModernWinStatMAC), (Stat.Accuracy, ClientTextKeys.ModernWinChipAcc),
            (Stat.Agility, ClientTextKeys.ModernWinChipAgi), (Stat.AttackSpeed, ClientTextKeys.ModernWinChipASpd), (Stat.HP, ClientTextKeys.ModernWinStatHP),
            (Stat.MP, ClientTextKeys.ModernWinStatMP), (Stat.Luck, ClientTextKeys.ModernWinStatLuck),
        };

        private enum Tab { Stats, Compare, Profile }

        private Tab _tab = Tab.Stats;
        private bool _isHero;
        private string _lastName = string.Empty;

        private ModernLabel _badge, _name, _line, _guild;
        private PaintBox _badgeBox;
        private Paperdoll _doll;
        private readonly List<ModernItemCell> _cells = new List<ModernItemCell>();
        private readonly List<CellDecor> _decor = new List<CellDecor>();
        private Tabs _tabs;
        private string _tabKey = string.Empty;
        private readonly List<Tab> _tabList = new List<Tab>();
        private ScrollView _pane;
        private ModernPanel _acts;
        private ModernButton _whisper, _group, _mail, _friend, _trade;

        private bool _dirty = true;
        private readonly Action _changed, _profile;
        private readonly Action<ItemSlotKey> _slotChanged;

        public InspectWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinInspect), 50, 40)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _profile = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Equipment && (_tab == Tab.Stats || _tab == Tab.Compare)) _dirty = true; };
        }

        private static InspectState Model => GameScene.Models.Inspect;

        internal bool IsHeroShown => _isHero;
        internal string TabShown => _tab.ToString();
        internal IReadOnlyList<ModernItemCell> Cells => _cells;

        /// <summary>S.PlayerInspect arrived (ModernUi.PlayerInspected): show the new player on the Stats tab.</summary>
        internal void Received(bool isHero)
        {
            _isHero = isHero;
            if (Model.Name != _lastName)
            {
                _lastName = Model.Name ?? string.Empty;
                _tab = Tab.Stats;
            }
            for (int i = 0; i < _cells.Count; i++) _cells[i].Refresh(true);
            _dirty = true;
        }

        protected override void Build()
        {
            const int top = TitleHeight;
            // banner
            new PaintBox((g, b) =>
            {
                using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 0, b.Size.Width * 0.6f + 1, b.Size.Height), W.Over(ModernTheme.Accent, 0.16f), Color.FromArgb(0, W.Page), 0f);
                g.FillRectangle(br, 0, 0, b.Size.Width * 0.6f, b.Size.Height);
            }) { Parent = this, Location = new Point(S(1), S(top)), Size = new Size(S(WidthHud) - 2, S(66)), NotControl = true };
            W.Divider(this, 0, top + 66, WidthHud);
            _badgeBox = new PaintBox(PaintBadge) { Parent = this, Location = new Point(S(16), S(top + 10)), Size = new Size(S(46), S(46)), NotControl = true };
            _badge = W.Label(_badgeBox, string.Empty, 0, 0, 46, 46, 12, true, Color.White, StringAlignment.Center);
            _name = W.Label(this, string.Empty, 76, top + 10, 600, 24, 20, true, Color.White);
            _line = W.Label(this, string.Empty, 76, top + 34, 600, 14, 11, false, ModernTheme.Dim);
            _guild = W.Label(this, string.Empty, 76, top + 48, 600, 14, 11, false, ModernTheme.Accent);

            // doll column
            int body = top + 67;
            W.VDivider(this, DollW, body, HeightHud - 67);
            int drowY = body + 10, figX = 10 + 48;
            _doll = new Paperdoll { Parent = this, Location = new Point(S(figX), S(drowY)), Size = new Size(S(184), S(216)) };
            _cells.Clear();
            _decor.Clear();
            for (int i = 0; i < LeftSlots.Length; i++) AddCell(LeftSlots[i], 10, drowY + i * 44);
            for (int i = 0; i < RightSlots.Length; i++) AddCell(RightSlots[i], 10 + 240, drowY + i * 44);
            int bx = (DollW - (4 * 40 + 3 * 4)) / 2;
            for (int i = 0; i < BottomSlots.Length; i++) AddCell(BottomSlots[i], bx + i * 44, drowY + 224);

            // main: tabs, pane, buttons
            _tabKey = string.Empty;
            _tabs = null;
            _pane = new ScrollView(WidthHud - DollW - 24, HeightHud - 67 - 38 - 46) { Parent = this, Location = new Point(S(DollW + 12), S(body + 38)), Background = W.Page };
            _pane.Framed = false;
            W.Divider(this, DollW, top + HeightHud - 46, WidthHud - DollW);
            _acts = new ModernPanel { Parent = this, Location = new Point(S(DollW + 12), S(top + HeightHud - 38)), Size = new Size(S(WidthHud - DollW - 24), S(30)) };
            int bw = (WidthHud - DollW - 24 - 4 * 6) / 5;
            ModernButton Act(ClientTextKeys text, ButtonKind kind, int i, Action run, ClientTextKeys? hint = null)
            {
                var b = new ModernButton(W.T(text), kind) { Parent = _acts, Location = new Point(S(i * (bw + 6)), S(4)) };
                b.Size = new Size(S(bw), b.Size.Height);
                if (hint.HasValue) b.Tooltip = W.T(hint.Value);
                b.Click += (o, e) => run();
                return b;
            }
            _whisper = Act(ClientTextKeys.ModernWinWhisper, ButtonKind.Main, 0, Whisper, ClientTextKeys.ModernWinWhisperHint);
            _group = Act(ClientTextKeys.ModernWinInvite, ButtonKind.Normal, 1, Invite, ClientTextKeys.ModernWinInviteHint);
            _mail = Act(ClientTextKeys.ModernWinMail, ButtonKind.Normal, 2, () => ModernRoot.Instance?.Wm.Open("mail"), ClientTextKeys.ModernWinMailHint);
            _friend = Act(ClientTextKeys.ModernWinAddFriend, ButtonKind.Normal, 3, () => Network.Enqueue(new C.AddFriend { Name = Model.Name, Blocked = false }));
            _trade = Act(ClientTextKeys.ModernWinTrade, ButtonKind.Normal, 4, () => Network.Enqueue(new C.TradeRequest()), ClientTextKeys.ModernWinTradeHint);
            _dirty = true;
            Render();
        }

        private void AddCell(EquipmentSlot slot, int x, int y)
        {
            var c = new ModernItemCell(MirGridType.Inspect, (int)slot, 40) { Parent = this, Location = new Point(S(x), S(y)), ReadOnly = true };
            _cells.Add(c);
            _decor.Add(new CellDecor(c, -1, SlotName(slot)));
        }

        private static string SlotName(EquipmentSlot slot) => W.T(slot switch
        {
            EquipmentSlot.Weapon => ClientTextKeys.ModernWinSlotWeapon,
            EquipmentSlot.Armour => ClientTextKeys.ModernWinSlotArmour,
            EquipmentSlot.Helmet => ClientTextKeys.ModernWinSlotHelmet,
            EquipmentSlot.Torch => ClientTextKeys.ModernWinSlotTorch,
            EquipmentSlot.Necklace => ClientTextKeys.ModernWinSlotNecklace,
            EquipmentSlot.BraceletL or EquipmentSlot.BraceletR => ClientTextKeys.ModernWinSlotBracelet,
            EquipmentSlot.RingL or EquipmentSlot.RingR => ClientTextKeys.ModernWinSlotRing,
            EquipmentSlot.Amulet => ClientTextKeys.ModernWinSlotAmulet,
            EquipmentSlot.Belt => ClientTextKeys.ModernWinSlotBelt,
            EquipmentSlot.Boots => ClientTextKeys.ModernWinSlotBoots,
            EquipmentSlot.Stone => ClientTextKeys.ModernWinSlotStone,
            _ => ClientTextKeys.ModernWinSlotMount,
        });

        private void PaintBadge(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(2 * k, 2 * k, b.Size.Width - 4 * k, b.Size.Height - 4 * k);
            using (var p = new System.Drawing.Drawing2D.GraphicsPath())
            {
                p.AddEllipse(r);
                using var br = new System.Drawing.Drawing2D.PathGradientBrush(p) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } };
                br.CenterPoint = new PointF(r.X + r.Width / 2, r.Y + r.Height * 0.35f);
                g.FillEllipse(br, r);
            }
            using (var pen = new Pen(Color.Black, 2f * k)) g.DrawEllipse(pen, RectangleF.Inflate(r, 1.5f * k, 1.5f * k));
            using (var pen = new Pen(ModernTheme.Class(Model.Class), 2f * k)) g.DrawEllipse(pen, r);
        }

        protected override void Shown()
        {
            Model.Changed.On(_changed);
            Model.ProfileChanged.On(_profile);
            GameScene.Models.Friends.Changed.On(_changed);
            ItemMoves.SlotChanged.On(_slotChanged);
            _dirty = true;
        }

        protected override void Closed()
        {
            Model.Changed.Off(_changed);
            Model.ProfileChanged.Off(_profile);
            GameScene.Models.Friends.Changed.Off(_changed);
            ItemMoves.SlotChanged.Off(_slotChanged);
        }

        public override void Process()
        {
            base.Process();
            if (_dirty) Render();
        }

        private static string ClassTag(MirClass c) => c switch
        {
            MirClass.Warrior => W.T(ClientTextKeys.ModernWinTagWarrior),
            MirClass.Wizard => W.T(ClientTextKeys.ModernWinTagWizard),
            MirClass.Taoist => W.T(ClientTextKeys.ModernWinTagTaoist),
            MirClass.Assassin => W.T(ClientTextKeys.ModernWinTagAssassin),
            MirClass.Archer => W.T(ClientTextKeys.ModernWinTagArcher),
            _ => "?",
        };

        private void Render()
        {
            _dirty = false;
            if (_name == null || _name.IsDisposed) return;
            InspectState m = Model;
            _badge.Text = ClassTag(m.Class);
            _badgeBox.Invalidate();
            _name.Text = m.Name ?? string.Empty;
            _line.Text = W.T(ClientTextKeys.ModernWinLevelClass, m.Level, m.Class.ToLocalizedString()) + (_isHero ? " · " + W.T(ClientTextKeys.ModernWinHeroTab) : string.Empty);
            string guild = string.IsNullOrEmpty(m.GuildName) ? string.Empty : m.GuildName + (string.IsNullOrEmpty(m.GuildRank) ? string.Empty : " " + m.GuildRank);
            string lover = string.IsNullOrEmpty(m.LoverName) ? string.Empty : "♥ " + m.LoverName;
            _guild.Text = guild.Length > 0 && lover.Length > 0 ? guild + " · " + lover : guild + lover;
            for (int i = 0; i < _decor.Count; i++) _decor[i].Sync();

            // doll: the desktop takes the wings' gender offset from the inspecting user
            UserItem armour = m.Items != null && m.Items.Length > (int)EquipmentSlot.Armour ? m.Items[(int)EquipmentSlot.Armour] : null;
            int effect = 0;
            if (armour?.Info != null) effect = Functions.GetRealItem(armour.Info, m.Level, m.Class, GameScene.ItemInfoList)?.Effect ?? 0;
            _doll.Set(DollLook.Of(m.Items, m.Level, m.Class, m.Gender, m.Hair, effect > 0 ? (effect == 1 ? 1 : 2) : 0, GameScene.User?.Gender ?? MirGender.Male));

            // tabs: Profile only with a profile text
            bool hasProfile = !_isHero && !string.IsNullOrWhiteSpace(m.ProfileText) && m.ProfileName == m.Name;
            _tabList.Clear();
            _tabList.Add(Tab.Stats);
            _tabList.Add(Tab.Compare);
            if (hasProfile) _tabList.Add(Tab.Profile);
            if (!_tabList.Contains(_tab)) _tab = Tab.Stats;
            string key = string.Join(",", _tabList);
            if (key != _tabKey || _tabs == null)
            {
                _tabKey = key;
                _tabs?.Dispose();
                var names = new List<string>();
                foreach (Tab t in _tabList) names.Add(W.T(t == Tab.Stats ? ClientTextKeys.ModernWinTabStats : t == Tab.Compare ? ClientTextKeys.ModernWinTabCompare : ClientTextKeys.ModernWinTabProfile).ToUpperInvariant());
                _tabs = new Tabs(names, _tabList.IndexOf(_tab), WidthHud - DollW - 24) { Parent = this, Location = new Point(S(DollW + 12), S(TitleHeight + 67 + 6)) };
                _tabs.Changed += i => { _tab = _tabList[i]; _dirty = true; };
            }
            else _tabs.Select(_tabList.IndexOf(_tab));

            // pane
            int keep = _pane.ScrollY;
            for (int i = _pane.Content.Controls.Count - 1; i >= 0; i--) _pane.Content.Controls[i].Dispose();
            int y = _tab == Tab.Stats ? StatsPane() : _tab == Tab.Compare ? ComparePane() : ProfilePane();
            _pane.ContentHeight = S(y + 8);
            _pane.ScrollTo(keep);

            // buttons: none for a hero; Mail only with a mail window; Add friend only for a non-friend
            _acts.Visible = !_isHero;
            _mail.Visible = ModernRoot.Instance?.Wm.IsRegistered("mail") ?? false;
            bool friend = false;
            foreach (ClientFriend f in GameScene.Models.Friends.Friends)
                if (!f.Blocked && f.Name == m.Name) { friend = true; break; }
            _friend.Visible = !friend;
            bool requests = GameScene.Models.ServerRules.Rules?.FriendRequestsEnabled ?? false;
            _friend.Text = W.T(requests ? ClientTextKeys.ModernWinFriendRequest : ClientTextKeys.ModernWinAddFriend);
            _friend.Tooltip = W.T(requests ? ClientTextKeys.ModernWinFriendRequestHint : ClientTextKeys.AddToFriendsList);
            LayoutActs();
        }

        /// <summary>The visible buttons share the row (.acts .btn { flex: 1 }).</summary>
        private void LayoutActs()
        {
            var shown = new List<ModernButton>();
            foreach (ModernButton b in new[] { _whisper, _group, _mail, _friend, _trade }) if (b.Visible) shown.Add(b);
            if (shown.Count == 0) return;
            int total = WidthHud - DollW - 24, bw = (total - (shown.Count - 1) * 6) / shown.Count;
            for (int i = 0; i < shown.Count; i++)
            {
                var loc = new Point(S(i * (bw + 6)), S(4));
                var size = new Size(S(bw), shown[i].Size.Height);
                if (shown[i].Location != loc) shown[i].Location = loc;
                if (shown[i].Size != size) shown[i].Size = size;
            }
        }

        // ------------------------------------------------------------------ panes (HUD px in the pane's content)

        private int PaneW => (int)Math.Round(_pane.InnerWidth / ModernRoot.K);

        private ModernLabel PLabel(string text, int x, int y, int w, int h = 18, float size = 12, bool bold = false, Color? colour = null, StringAlignment align = StringAlignment.Near) =>
            W.Label(_pane.Content, text, x, y, w, h, size, bold, colour, align);

        /// <summary>One item's own stats: the real item for the wearer + added stats.</summary>
        private static void AddItemStats(UserItem item, ushort level, MirClass cls, Stats into)
        {
            if (item?.Info == null) return;
            ItemInfo real = Functions.GetRealItem(item.Info, level, cls, GameScene.ItemInfoList) ?? item.Info;
            if (real.Stats != null) into.Add(real.Stats);
            if (item.AddedStats != null) into.Add(item.AddedStats);
        }

        private static Stats GearStats(UserItem[] equipment, ushort level, MirClass cls)
        {
            var s = new Stats();
            if (equipment == null) return s;
            for (int i = 0; i < equipment.Length; i++) AddItemStats(equipment[i], level, cls, s);
            return s;
        }

        private int StatsPane()
        {
            InspectState m = Model;
            UserObject me = GameScene.User;
            Stats t = GearStats(m.Items, m.Level, m.Class), mine = GearStats(me?.Equipment, me?.Level ?? 1, me?.Class ?? MirClass.Warrior);
            int w = PaneW, y = 4;
            PLabel(W.T(ClientTextKeys.ModernWinFromGear).ToUpperInvariant(), 0, y, 110, 14, 9, true, ModernTheme.Dim);
            PLabel(m.Name ?? string.Empty, 118, y, 80, 14, 9, true, ModernTheme.Dim, StringAlignment.Far);
            PLabel(W.T(ClientTextKeys.ModernWinYou).ToUpperInvariant(), w - 80, y, 80, 14, 9, true, ModernTheme.Accent);
            y += 20;
            void Row(string name, int a, int b, string ta, string tb)
            {
                int max = Math.Max(1, Math.Max(a, b));
                float fa = Math.Max(0, a) / (float)max, fb = Math.Max(0, b) / (float)max;
                PLabel(name, 0, y, 110, 22, 12, false, ModernTheme.Dim);
                PLabel(ta, 118, y, 80, 22, 12, true, Color.White, StringAlignment.Far);
                new PaintBox((g, bx) =>
                {
                    float k = ModernRoot.K, half = bx.Size.Width / 2f, hy = bx.Size.Height / 2f - 4 * k;
                    float tw = half * fa, mw = half * fb;
                    if (tw > 0)
                        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(half - tw - 1, 0, tw + 2, 8), Color.FromArgb(0, ModernTheme.Gold), ModernTheme.Gold, 0f))
                            g.FillRectangle(br, half - tw, hy, tw, 8 * k);
                    if (mw > 0)
                        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(half - 1, 0, mw + 2, 8), ModernTheme.Accent, Color.FromArgb(0, ModernTheme.Accent), 0f))
                            g.FillRectangle(br, half, hy, mw, 8 * k);
                    using var pen = new Pen(ModernTheme.Frame, 1f);
                    g.DrawLine(pen, half, hy - 2 * k, half, hy + 10 * k);
                }) { Parent = _pane.Content, Location = new Point(S(206), S(y)), Size = new Size(S(w - 206 - 88), S(22)), NotControl = true };
                PLabel(tb, w - 80, y, 80, 22, 12, true, ModernTheme.Accent);
                y += 24;
            }
            for (int i = 0; i < Ranges.Length; i++)
            {
                var (n, lo, hi) = Ranges[i];
                int a0 = t[lo], a1 = t[hi], b0 = mine[lo], b1 = mine[hi];
                if (a1 != 0 || b1 != 0 || a0 != 0 || b0 != 0) Row(W.T(n), a1, b1, W.Range(a0, a1), W.Range(b0, b1));
            }
            y += 6;
            W.Caption(_pane.Content, W.T(ClientTextKeys.ModernWinOther), 0, y, w);
            y += 18;
            bool any = false;
            for (int i = 0; i < Extra.Length; i++)
            {
                int a = t[Extra[i].Stat], b = mine[Extra[i].Stat];
                if (a == 0 && b == 0) continue;
                any = true;
                Row(W.T(Extra[i].Label), a, b, W.Signed(a), W.Signed(b));
            }
            if (!any) { PLabel(W.T(ClientTextKeys.ModernWinNoOtherGearStats), 0, y, w, 18, 12, false, ModernTheme.Dim); y += 22; }
            y += 6;
            PLabel(W.T(ClientTextKeys.ModernWinGearStatsNote), 0, y, w, 30, 10, false, ModernTheme.Dim).Wrap = true;
            return y + 30;
        }

        private int ComparePane()
        {
            InspectState m = Model;
            UserObject me = GameScene.User;
            int w = PaneW, y = 4, side = (w - 64 - 120 - 24) / 2;
            PLabel(W.T(ClientTextKeys.ModernWinSlot).ToUpperInvariant(), 0, y, 64, 14, 9, true, ModernTheme.Dim);
            PLabel((m.Name ?? string.Empty).ToUpperInvariant(), 72, y, side, 14, 9, true, ModernTheme.Dim);
            PLabel(W.T(ClientTextKeys.ModernWinTheirsVsYours).ToUpperInvariant(), 80 + side, y, 120, 14, 9, true, ModernTheme.Dim, StringAlignment.Center);
            PLabel(W.T(ClientTextKeys.ModernWinYou).ToUpperInvariant(), 208 + side, y, side, 14, 9, true, ModernTheme.Dim);
            y += 18;
            EquipmentSlot[] order = { EquipmentSlot.Weapon, EquipmentSlot.Armour, EquipmentSlot.Torch, EquipmentSlot.BraceletL, EquipmentSlot.RingL, EquipmentSlot.Helmet, EquipmentSlot.Necklace,
                EquipmentSlot.Amulet, EquipmentSlot.BraceletR, EquipmentSlot.RingR, EquipmentSlot.Belt, EquipmentSlot.Boots, EquipmentSlot.Stone, EquipmentSlot.Mount };
            foreach (EquipmentSlot slot in order)
            {
                int i = (int)slot;
                UserItem theirs = m.Items != null && i < m.Items.Length ? m.Items[i] : null, mine = me?.Equipment != null && i < me.Equipment.Length ? me.Equipment[i] : null;
                var a = new Stats();
                var b = new Stats();
                AddItemStats(theirs, m.Level, m.Class, a);
                if (me != null) AddItemStats(mine, me.Level, me.Class, b);
                PLabel(SlotName(slot), 0, y, 64, 34, 11, false, ModernTheme.Dim);
                new ModernItemCell(MirGridType.Inspect, i, 30) { Parent = _pane.Content, Location = new Point(S(72), S(y + 2)), ReadOnly = true };
                PLabel(theirs?.Info?.Name ?? "—", 106, y, side - 34, 34, 11, false, theirs?.Info != null ? ModernTheme.Grade(theirs.Info.Grade) : ModernTheme.Dim);
                var chips = new List<(string Text, bool Up)>();
                for (int c = 0; c < Chips.Length && chips.Count < 4; c++)
                {
                    int v = a[Chips[c].Stat] - b[Chips[c].Stat];
                    if (v != 0) chips.Add(((v > 0 ? "+" : "") + W.N(v) + " " + W.T(Chips[c].Label), v > 0));
                }
                string eq = theirs != null || mine != null ? "=" : string.Empty;
                new PaintBox((g, bx) => PaintChips(g, bx, chips, eq)) { Parent = _pane.Content, Location = new Point(S(80 + side), S(y)), Size = new Size(S(120), S(34)), NotControl = true };
                new ModernItemCell(MirGridType.Equipment, i, 30) { Parent = _pane.Content, Location = new Point(S(208 + side), S(y + 2)), ReadOnly = true };
                PLabel(mine?.Info?.Name ?? "—", 242 + side, y, side - 34, 34, 11, false, mine?.Info != null ? ModernTheme.Grade(mine.Info.Grade) : ModernTheme.Dim);
                W.Divider(_pane.Content, 0, y + 35, w);
                y += 37;
            }
            y += 6;
            PLabel(W.T(ClientTextKeys.ModernWinCompareNote), 0, y, w, 16, 10, false, ModernTheme.Dim);
            return y + 18;
        }

        private static void PaintChips(Graphics g, PaintBox b, List<(string Text, bool Up)> chips, string eq)
        {
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(10, true);
            if (chips.Count == 0)
            {
                ModernSkin.Text(g, eq, f, ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width, b.Size.Height), StringAlignment.Center);
                return;
            }
            // wrap the chips onto at most two centred lines
            float lineH = 14 * k, gap = 3 * k;
            var lines = new List<List<(string, bool, float)>> { new List<(string, bool, float)>() };
            float x = 0;
            foreach (var (text, up) in chips)
            {
                float w = ModernSkin.Measure(text, f) + 10 * k;
                if (x > 0 && x + w > b.Size.Width) { lines.Add(new List<(string, bool, float)>()); x = 0; }
                lines[^1].Add((text, up, w));
                x += w + gap;
            }
            float y = (b.Size.Height - lines.Count * lineH - (lines.Count - 1) * gap) / 2;
            foreach (var line in lines)
            {
                float total = -gap;
                foreach (var c in line) total += c.Item3 + gap;
                float cx = (b.Size.Width - total) / 2;
                foreach (var (text, up, w) in line)
                {
                    Color col = up ? ModernTheme.Gold : ModernTheme.Accent;
                    RectangleF r = new RectangleF(cx, y, w, lineH);
                    using (var p = ModernSkin.RoundRect(r, 6 * k))
                    using (var br = new SolidBrush(W.Over(col, 0.14f)))
                        g.FillPath(br, p);
                    ModernSkin.Text(g, text, f, col, r, StringAlignment.Center);
                    cx += w + gap;
                }
                y += lineH + gap;
            }
        }

        private int ProfilePane()
        {
            int w = PaneW;
            W.Caption(_pane.Content, W.T(ClientTextKeys.ModernWinTabProfile), 0, 4, w);
            string text = Model.ProfileText ?? string.Empty;
            SizeF size = ModernSkin.MeasureWrapped(text, ModernSkin.Font(13), S(w - 24));
            int h = (int)Math.Ceiling(size.Height / ModernRoot.K) + 22;
            new PaintBox((g, b) => W.Card(g, b.Size, null, 0f)) { Parent = _pane.Content, Location = new Point(0, S(22)), Size = new Size(S(w), S(h)), NotControl = true };
            PLabel(text, 12, 32, w - 24, h - 20, 13, false, Color.White).Wrap = true;
            return 22 + h;
        }

        // ------------------------------------------------------------------ buttons

        /// <summary>The name label's whisper: the line is asked in a popup and sent as "/name text" (the chat input belongs to the
        /// chat area).</summary>
        private void Whisper()
        {
            string name = Model.Name;
            if (string.IsNullOrEmpty(name)) return;
            Hud.ChatHud chat = (GameScene.Ui as ModernUi)?.Chat;
            if (chat != null && !chat.IsDisposed)
            {
                chat.Prefill("/" + name + " "); // opens the chat input on a whisper to them (chat.ts prefill)
                return;
            }
            ModernRoot.Instance?.Popups.Input(W.T(ClientTextKeys.ModernWinWhisperTo, name), string.Empty, Math.Max(1, Globals.MaxChatLength - name.Length - 2), text =>
            {
                if (string.IsNullOrEmpty(text)) return;
                Network.Enqueue(new C.Chat { Message = "/" + name + " " + text });
            });
        }

        /// <summary>InspectDialog.GroupButton.Click (the desktop still sends after the not-leader line; the server refuses).</summary>
        private void Invite()
        {
            List<string> list = GameScene.Models.Group.Members;
            if (list.Count >= Globals.MaxGroup)
            {
                GameScene.Models.Chat.Add(W.T(ClientTextKeys.GroupHasMaxMembers), ChatType.System);
                return;
            }
            if (list.Count > 0 && list[0] != GameScene.User?.Name) GameScene.Models.Chat.Add(W.T(ClientTextKeys.YouNotGroupLeader), ChatType.System);
            Network.Enqueue(new C.AddMember { Name = Model.Name });
        }
    }
}
