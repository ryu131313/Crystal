// Ported from WebClient/src/ui/modern/windows/group.ts (GroupWindow + GroupSession's log): the new UI's group window over
// GameScene.Models.Group (Members with the leader first, AllowGroup, MemberMaps, MemberLocations, MemberStatus). Head: the
// member count and capacity bar, the Allow group switch (C.SwitchGroup; locked until S.SwitchGroup echoes it; off while grouped
// asks first, because the server then takes you out of the group) and invite by name (C.AddMember: the classic
// GroupDialog.AddMember rules). Cards (two columns): class badge with the leader's crown, name, class + level, a status pill
// (You / Dead / Away / Nearby), HP / MP bars (S.GroupMemberStatus %, else the in-view player's S.ObjectHealth %, else empty),
// where (another map's title, else distance and direction on this map) and up to 8 buff icons. Side: the picked member's
// actions (Whisper prefills "/name ", Kick = C.DelMember for the leader, confirm twice; Leave = C.SwitchGroup off after a
// question), the note line and the group log (GroupLog: built from roster changes since log-in, kept while the window is closed).
// The incoming invite is ModernUi.GroupInvite's request toast. Live values (bars, where) refresh twice a second while open.
// HIDDEN as on the web (Crystal has no system for them): loot rule, join requests, make leader, disband, resurrect, trade.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>The group log (group.ts GroupSession): roster changes since log-in, newest last, at most 40 lines.</summary>
    internal static class GroupLog
    {
        private const int Max = 40;
        public static readonly List<(string Text, DateTime At)> Lines = new List<(string, DateTime)>();
        public static readonly Emitter Changed = new Emitter();
        private static readonly List<string> _roster = new List<string>();
        private static Action _onGroup;

        public static void Start()
        {
            Stop();
            Lines.Clear();
            _roster.Clear();
            GroupState g = GameScene.Models?.Group;
            if (g == null) return;
            _roster.AddRange(g.Members);
            _onGroup = Diff;
            g.Changed.On(_onGroup);
        }

        public static void Stop()
        {
            if (_onGroup != null) GameScene.Models?.Group.Changed.Off(_onGroup);
            _onGroup = null;
        }

        /// <summary>S.AddMember / S.DeleteMember / S.DeleteGroup → log lines (after the model's chat line).</summary>
        private static void Diff()
        {
            GroupState g = GameScene.Models?.Group;
            if (g == null) return;
            List<string> now = g.Members;
            bool same = now.Count == _roster.Count;
            for (int i = 0; same && i < now.Count; i++) same = now[i] == _roster[i];
            if (same) return;
            string me = Soc.Me;
            if (_roster.Count == 0 && now.Count > 0) Add(now[0] == me ? W.T(ClientTextKeys.ModernSocialLogFormed) : W.T(ClientTextKeys.ModernSocialLogJoinedGroup, now[0]));
            else if (now.Count == 0) Add(W.T(ClientTextKeys.ModernSocialLogLeft));
            if (now.Count > 0)
            {
                // joining sends one S.AddMember per member, the user among them: the first already said "You joined"
                foreach (string n in now) if (!_roster.Contains(n) && _roster.Count > 0 && n != me) Add(W.T(ClientTextKeys.ModernSocialLogJoined, n));
                foreach (string n in _roster) if (!now.Contains(n)) Add(W.T(ClientTextKeys.ModernSocialLogMemberLeft, n));
                if (_roster.Count > 0 && _roster[0] != now[0]) Add(W.T(ClientTextKeys.ModernSocialLogLeads, now[0]));
            }
            _roster.Clear();
            _roster.AddRange(now);
        }

        private static void Add(string text)
        {
            Lines.Add((text, CMain.Now));
            if (Lines.Count > Max) Lines.RemoveRange(0, Lines.Count - Max);
            Changed.Emit();
        }
    }

    public sealed class GroupWindow : ModernWindow
    {
        public const string WindowKey = "group";
        private const int WidthHud = 720, HeadH = 50, BodyH = 430, SideW = 236, CardH = 74, MaxBuffs = 8;

        private string _picked = string.Empty, _note = string.Empty, _rosterKey = "\u0000";
        private bool _allowPending, _dirty = true;
        private int _logCount = -1;
        private long _nextTick;

        private ModernLabel _count, _countSub;
        private PaintBox _cap;
        private ToggleSwitch _allow;
        private ModernTextField _invite;
        private ModernButton _inviteBtn, _whisper, _leave;
        private ConfirmTwiceButton _kick;
        private ScrollView _cards, _log;
        private ModernPanel _side;
        private ModernLabel _whoLbl, _who, _hint, _noteLbl;
        private readonly List<Card> _cardList = new List<Card>();
        private readonly Action _changed, _logChanged;
        private readonly Action<string> _status;

        private sealed class Card
        {
            public string Name;
            public bool Leader, You;
            public PaintBox Box;
            public string Key = string.Empty;
            // live values
            public int Cls = -1, Level;
            public int Hp = -1, Mp = -1;
            public bool Dead, Far;
            public string Where = string.Empty, Status = string.Empty;
            public double Angle = double.NaN;
            public readonly List<BuffType> Buffs = new List<BuffType>();
        }

        public GroupWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinGroup), 210, 50)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeadH + BodyH;
            _changed = () => { _allowPending = false; _dirty = true; };
            _status = _ => _dirty = true;
            _logChanged = () => _dirty = true;
        }

        private static GroupState Model => GameScene.Models.Group;
        private static bool IsLeader => Model.Members.Count == 0 || Model.Members[0] == Soc.Me;

        // ---- for the UI test
        internal string Picked => _picked;
        internal string Note => _note;
        internal bool AllowPending => _allowPending;
        internal ModernTextField InviteField => _invite;
        internal ModernButton InviteButton => _inviteBtn;
        internal ModernButton KickButton => _kick;
        internal ModernButton LeaveButton => _leave;
        internal ToggleSwitch AllowSwitch => _allow;
        internal PaintBox CardBox(int i) => i >= 0 && i < _cardList.Count ? _cardList[i].Box : null;
        internal string CardText(int i) => i >= 0 && i < _cardList.Count ? _cardList[i].Key : string.Empty;

        protected override void Build()
        {
            int top = TitleHeight;
            _count = W.Label(this, string.Empty, 14, top + 6, 90, 22, 18, true, Color.White);
            _countSub = W.Label(this, W.T(ClientTextKeys.ModernSocialGroupMembers), 70, top + 10, 80, 16, 11, false, ModernTheme.Dim);
            _cap = new PaintBox(PaintCap) { Parent = this, Location = new Point(S(14), S(top + 33)), Size = new Size(S(120), S(5)), NotControl = true };
            _allow = W.At(new ToggleSwitch(Model.AllowGroup) { Tooltip = W.T(ClientTextKeys.ModernSocialAllowGroupHint) }, this, 170, top + 15);
            _allow.Changed += SetAllow;
            W.Label(this, W.T(ClientTextKeys.ModernSocialAllowGroup), 214, top + 15, 120, 20, 11, false, ModernTheme.Dim);
            _invite = W.At(new ModernTextField(150, 26) { Placeholder = W.T(ClientTextKeys.ModernSocialInviteByName), MaxLength = 20 }, this, WidthHud - 14 - 84 - 6 - 150, top + 12);
            _invite.Submitted += _ => Invite();
            _inviteBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernWinInvite), ButtonKind.Main), this, WidthHud - 14 - 84, top + 12);
            _inviteBtn.Size = new Size(S(84), S(26));
            _inviteBtn.Click += (o, e) => Invite();
            W.Divider(this, 0, top + HeadH, WidthHud);

            int body = top + HeadH + 1;
            _cards = new ScrollView(WidthHud - SideW - 12, BodyH - 13) { Parent = this, Location = new Point(S(12), S(body + 12)), Background = W.Page };
            _cards.Framed = false;
            W.VDivider(this, WidthHud - SideW, body, BodyH - 1);

            int sx = WidthHud - SideW + 12, sw = SideW - 24;
            _side = new ModernPanel { Parent = this, Location = new Point(S(sx), S(body + 12)), Size = new Size(S(sw), S(170)) };
            _whoLbl = W.Caption(_side, string.Empty, 0, 0, sw);
            _who = W.Label(_side, string.Empty, 0, 18, sw, 18, 13, true, Color.White);
            int bw = (sw - 5) / 2;
            _whisper = W.At(new ModernButton(W.T(ClientTextKeys.ModernWinWhisper)) { Tooltip = W.T(ClientTextKeys.ModernSocialWhisperHint) }, _side, 0, 42);
            _whisper.Size = new Size(S(bw), S(26));
            _whisper.Click += (o, e) => { if (_picked.Length > 0) Soc.Prefill("/" + _picked + " "); };
            _kick = W.At(new ConfirmTwiceButton(W.T(ClientTextKeys.ModernSocialKick), ButtonKind.Warn) { Tooltip = W.T(ClientTextKeys.ModernSocialKickHint) }, _side, bw + 5, 42);
            _kick.Size = new Size(S(bw), S(26));
            _kick.Confirmed += Kick;
            _leave = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialLeaveGroup), ButtonKind.Warn) { Tooltip = W.T(ClientTextKeys.ModernSocialLeaveHint) }, _side, 0, 42);
            _leave.Size = new Size(S(sw), S(26));
            _leave.Click += (o, e) => Leave();
            _hint = W.Label(_side, string.Empty, 0, 76, sw, 42, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            int ly = body + 12 + 130;
            _noteLbl = W.Label(this, string.Empty, sx, ly, sw, 30, 11, false, ModernTheme.Accent, StringAlignment.Near, true);
            W.Caption(this, W.T(ClientTextKeys.ModernSocialGroupLog), sx, ly + 34, sw);
            _log = new ScrollView(sw, BodyH - 13 - 130 - 54) { Parent = this, Location = new Point(S(sx), S(ly + 52)), Background = W.Page };
            _log.Framed = false;

            _rosterKey = "\u0000";
            _logCount = -1;
            _cardList.Clear();
            _dirty = true;
            Render();
        }

        protected override void Shown()
        {
            Model.Changed.On(_changed);
            Model.StatusChanged.On(_status);
            GroupLog.Changed.On(_logChanged);
            _dirty = true;
        }

        protected override void Closed()
        {
            Model.Changed.Off(_changed);
            Model.StatusChanged.Off(_status);
            GroupLog.Changed.Off(_logChanged);
            _kick?.Reset();
        }

        public override void Process()
        {
            base.Process();
            if (_dirty) Render();
            else if (CMain.Time >= _nextTick) UpdateCards();
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_count == null || _count.IsDisposed || GameScene.User == null) return;
            GroupState g = Model;
            List<string> list = g.Members;
            int n = list.Count;
            string me = Soc.Me;
            _count.Text = n > 0 ? n + " / " + Globals.MaxGroup : W.T(ClientTextKeys.ModernSocialGroupNone);
            _count.AutoWidth();
            _countSub.Location = new Point(_count.Location.X + _count.Size.Width + S(4), _countSub.Location.Y);
            _countSub.Visible = n > 0;
            _cap.Index = n;
            _cap.Invalidate();
            if (!_allowPending)
            {
                _allow.Set(g.AllowGroup);
                _allow.Lock(false);
            }
            bool leader = IsLeader;
            if (!leader) _inviteBtn.SetDisabled(true, W.T(ClientTextKeys.ModernSocialOnlyLeaderInvites));
            else if (n >= Globals.MaxGroup) _inviteBtn.SetDisabled(true, W.T(ClientTextKeys.GroupHasMaxMembers));
            else _inviteBtn.SetDisabled(false);

            // cards: rebuilt only when the roster changed
            string key = string.Join("\u0001", list);
            if (key != _rosterKey)
            {
                _rosterKey = key;
                BuildCards(list, me);
                if (_picked.Length > 0 && !list.Contains(_picked)) _picked = string.Empty;
                _kick.Reset();
            }
            if (_picked.Length == 0 && n > 0) _picked = list.Contains(me) ? me : list[0];
            for (int i = 0; i < _cardList.Count; i++) _cardList[i].Box.Selected = _cardList[i].Name == _picked;
            UpdateCards();
            RenderSide();
        }

        private void BuildCards(List<string> list, string me)
        {
            Soc.Clear(_cards.Content);
            _cardList.Clear();
            int iw = _cards.InnerWidth, gap = S(8), cw = (iw - gap) / 2, ch = S(CardH);
            for (int i = 0; i < list.Count; i++)
            {
                var c = new Card { Name = list[i], Leader = i == 0, You = list[i] == me };
                Card cc = c;
                c.Box = new PaintBox((g, b) => PaintCard(g, b, cc))
                {
                    Parent = _cards.Content,
                    Location = new Point(i % 2 * (cw + gap), i / 2 * (ch + gap)),
                    Size = new Size(cw, ch),
                    AfterPaint = b => DrawCardIcons(b, cc),
                };
                c.Box.Click += (o, e) => Pick(cc.Name);
                _cardList.Add(c);
            }
            // the open-slot card
            int at = list.Count, free = Globals.MaxGroup - list.Count;
            string text = list.Count == 0 ? W.T(ClientTextKeys.ModernSocialNotInGroup)
                : free > 0 ? W.T(free == 1 ? ClientTextKeys.ModernSocialOpenSlot : ClientTextKeys.ModernSocialOpenSlots, free) : W.T(ClientTextKeys.ModernSocialGroupFull);
            new PaintBox((g, b) =>
            {
                W.Card(g, b.Size, W.Over(ModernTheme.Frame, 0.55f), 0.03f, true);
                float k = ModernRoot.K;
                ModernSkin.Text(g, text, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(10 * k, 0, b.Size.Width - 20 * k, b.Size.Height), StringAlignment.Center, StringAlignment.Center, false, true);
            })
            { Parent = _cards.Content, Location = new Point(at % 2 * (cw + gap), at / 2 * (ch + gap)), Size = new Size(list.Count == 0 ? iw : cw, ch), NotControl = true };
            _cards.FitContent(0);
        }

        private void Pick(string name)
        {
            if (_picked == name) return;
            _picked = name;
            _note = string.Empty;
            _kick.Reset();
            _dirty = true;
        }

        private void RenderSide()
        {
            GroupState g = Model;
            string me = Soc.Me;
            List<string> list = g.Members;
            bool grouped = list.Count > 0, leader = IsLeader;
            string name = _picked;
            bool you = name.Length == 0 || name == me;
            _whoLbl.Text = (!grouped ? W.T(ClientTextKeys.ModernSocialGroupNone)
                : you ? (list[0] == me ? W.T(ClientTextKeys.ModernSocialYouLeader) : W.T(ClientTextKeys.ModernSocialYou))
                : list[0] == name ? W.T(ClientTextKeys.ModernSocialLeader) : W.T(ClientTextKeys.ModernSocialMember)).ToUpperInvariant();
            _who.Text = grouped && name.Length > 0 ? name : me;
            _whisper.Visible = !you && grouped;
            _kick.Visible = !you && grouped && leader;
            _leave.Visible = you && grouped;
            _leave.SetDisabled(_allowPending, W.T(ClientTextKeys.ModernWinWaitingServer));
            _hint.Text = !grouped ? W.T(ClientTextKeys.ModernSocialHintForm)
                : you ? W.T(ClientTextKeys.ModernSocialHintLeave)
                : leader ? W.T(ClientTextKeys.ModernSocialHintKick) : W.T(ClientTextKeys.ModernSocialHintMember);
            _hint.Location = new Point(0, S(you && !grouped ? 42 : 76));
            _noteLbl.Text = _note;

            // the log: rebuilt only when it grew (newest first)
            List<(string Text, DateTime At)> log = GroupLog.Lines;
            if (log.Count != _logCount)
            {
                _logCount = log.Count;
                Soc.Clear(_log.Content);
                int y = 0, w = _log.InnerWidth;
                if (log.Count == 0)
                    W.Label(_log.Content, W.T(ClientTextKeys.ModernSocialNothingYet), 0, 0, (int)(w / ModernRoot.K), 20, 11, false, ModernTheme.Dim);
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    string text = log[i].Text, at = Soc.Clock(log[i].At);
                    new PaintBox((gg, b) =>
                    {
                        float k = ModernRoot.K;
                        float tw = ModernSkin.Measure(at, ModernSkin.Font(11)) + 2;
                        ModernSkin.Text(gg, text, ModernSkin.Font(11), ModernTheme.Text, new RectangleF(0, 0, b.Size.Width - tw - 8 * k, b.Size.Height));
                        ModernSkin.Text(gg, at, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(b.Size.Width - tw, 0, tw, b.Size.Height), StringAlignment.Far);
                        using var pen = new Pen(W.Over(ModernTheme.Frame, 0.25f), 1f);
                        gg.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
                    })
                    { Parent = _log.Content, Location = new Point(0, y), Size = new Size(w, S(22)), NotControl = true };
                    y += S(22);
                }
                _log.FitContent(0);
            }
        }

        /// <summary>The members' live values (2 Hz while open): repaint a card only when its values changed.</summary>
        private void UpdateCards()
        {
            _nextTick = CMain.Time + 500;
            UserObject user = GameScene.User;
            if (user == null) return;
            GroupState g = Model;
            string myMap = Soc.MapTitle;
            string[] dirs = W.T(ClientTextKeys.ModernSocialDirs).Split(',');
            for (int i = 0; i < _cardList.Count; i++)
            {
                Card c = _cardList[i];
                c.Buffs.Clear();
                PlayerObject ob = c.You ? null : Soc.InView(c.Name);
                if (c.You)
                {
                    c.Cls = (int)user.Class;
                    c.Level = user.Level;
                    int maxHp = user.Stats[Stat.HP], maxMp = user.Stats[Stat.MP];
                    c.Hp = maxHp > 0 ? Math.Min(100, user.HP * 100 / maxHp) : -1;
                    c.Mp = maxMp > 0 ? Math.Min(100, user.MP * 100 / maxMp) : -1;
                    c.Dead = user.Dead;
                    List<ClientBuff> buffs = GameScene.Models.Buffs.Items;
                    for (int b = 0; b < buffs.Count && c.Buffs.Count < MaxBuffs; b++) if (buffs[b].Visible) c.Buffs.Add(buffs[b].Type);
                }
                else if (g.MemberStatus.TryGetValue(c.Name, out S.GroupMemberStatus st))
                {
                    c.Cls = (int)st.Class;
                    c.Level = st.Level;
                    c.Hp = st.HPPercent;
                    c.Mp = st.MPPercent;
                    c.Dead = (ob?.Dead ?? false) || st.HPPercent == 0;
                    for (int b = 0; b < st.Buffs.Count && c.Buffs.Count < MaxBuffs; b++) c.Buffs.Add(st.Buffs[b]);
                }
                else
                {
                    c.Cls = ob != null ? (int)ob.Class : -1;
                    c.Level = ob?.Level ?? 0;
                    c.Hp = ob != null && ob.HealthTime > CMain.Time ? ob.PercentHealth : -1;
                    c.Mp = -1;
                    c.Dead = ob?.Dead ?? false;
                }
                // where: another map's title, else distance and direction on this map
                g.MemberMaps.TryGetValue(c.Name, out string map);
                bool hasLoc = g.MemberLocations.TryGetValue(c.Name, out Point loc);
                c.Far = false;
                c.Angle = double.NaN;
                if (c.You) c.Where = myMap;
                else if (!string.IsNullOrEmpty(map) && map != myMap) { c.Where = map; c.Far = true; }
                else if (hasLoc)
                {
                    int dx = loc.X - user.CurrentLocation.X, dy = loc.Y - user.CurrentLocation.Y, d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (d > 0)
                    {
                        c.Angle = Math.Atan2(dx, -dy);
                        string dir = dirs.Length == 8 ? dirs[((int)Math.Round(c.Angle / (Math.PI / 4)) + 8) % 8] : string.Empty;
                        c.Where = W.T(d == 1 ? ClientTextKeys.ModernSocialCell : ClientTextKeys.ModernSocialCells, d, dir);
                    }
                    else c.Where = W.T(ClientTextKeys.ModernSocialHere);
                }
                else c.Where = map ?? string.Empty;
                c.Status = c.Dead ? W.T(ClientTextKeys.ModernSocialDead) : c.You ? W.T(ClientTextKeys.ModernSocialYou) : c.Far ? W.T(ClientTextKeys.ModernSocialAway)
                    : hasLoc || ob != null ? W.T(ClientTextKeys.ModernSocialNearby) : string.Empty;
                string key = c.Cls + "|" + c.Level + "|" + c.Hp + "|" + c.Mp + "|" + c.Dead + "|" + c.Where + "|" + c.Status + "|" + (double.IsNaN(c.Angle) ? "" : c.Angle.ToString("0.0")) + "|" + string.Join(",", c.Buffs);
                if (key == c.Key) continue;
                c.Key = key;
                c.Box.Invalidate();
            }
        }

        // ------------------------------------------------------------------ painting

        private void PaintCap(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, frac = Math.Clamp(b.Index / (float)Globals.MaxGroup, 0f, 1f);
            RectangleF r = new RectangleF(0, 0, b.Size.Width, b.Size.Height);
            using (GraphicsPath p = ModernSkin.RoundRect(r, 2.5f * k))
            using (var br = new SolidBrush(Color.FromArgb(0x04, 0x06, 0x0a))) g.FillPath(br, p);
            if (frac <= 0) return;
            RectangleF f = new RectangleF(0, 0, Math.Max(2, b.Size.Width * frac), b.Size.Height);
            using GraphicsPath fp = ModernSkin.RoundRect(f, 2.5f * k);
            using var lg = new LinearGradientBrush(new RectangleF(-1, 0, b.Size.Width + 2, b.Size.Height), ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f), ModernTheme.Accent, 0f);
            g.FillPath(lg, fp);
        }

        private static void PaintCard(Graphics g, PaintBox b, Card c)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                Color fill = b.Selected ? W.Over(ModernTheme.Accent, 0.1f) : W.Over(ModernTheme.FrameLight, 0.05f);
                using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                Color border = b.Selected ? ModernTheme.Accent : b.Hovered ? W.Over(ModernTheme.Accent, 0.5f) : W.Over(ModernTheme.Frame, 0.55f);
                ModernSkin.Stroke(g, p, border);
            }
            float dim = c.Dead ? 0.4f : 0f;
            Color D(Color col) => dim > 0 ? ModernSkin.Dim(col, dim) : col;
            Color cls = c.Cls >= 0 ? ModernTheme.Class((MirClass)c.Cls) : ModernTheme.Frame;
            RectangleF badge = new RectangleF(10 * k, 12 * k, 40 * k, 40 * k);
            if (c.You)
                using (var glow = new Pen(ModernTheme.Alpha(ModernTheme.Accent, 0.35f), 4 * k)) g.DrawEllipse(glow, RectangleF.Inflate(badge, 1, 1));
            Soc.Medal(g, badge, c.Cls >= 0 ? Soc.Tag((MirClass)c.Cls) : "?", c.You ? ModernTheme.Accent : cls, -1, false, 1f, 10, D(c.Cls >= 0 ? cls : ModernTheme.Text));
            if (c.Leader) Crown(g, badge.X + badge.Width / 2, badge.Y - 2 * k, k);

            float x = 60 * k, w = b.Size.Width - x - 10 * k, y = 9 * k;
            // name line: name, class + level, the status pill on the right
            float pillW = 0;
            if (c.Status.Length > 0)
            {
                Color pc = c.Dead ? ModernTheme.BadSoft : c.Far ? ModernTheme.Warn : ModernTheme.AccentDim;
                pillW = Soc.Pill(g, x + w, y + 1 * k, c.Status, pc, 8.5f, false, true, 13);
            }
            string sub = c.Cls >= 0 ? Soc.ClassName((MirClass)c.Cls) + (c.Level > 0 ? " " + c.Level : string.Empty) : c.Level > 0 ? W.T(ClientTextKeys.ModernHudPartyLevel, c.Level) : string.Empty;
            Font nf = ModernSkin.Font(13, true), sf = ModernSkin.Font(10);
            float subW = sub.Length > 0 ? ModernSkin.Measure(sub, sf) + 2 : 0;
            float nameW = Math.Min(ModernSkin.Measure(c.Name, nf) + 2, Math.Max(20, w - pillW - subW - 12 * k));
            ModernSkin.Text(g, c.Name, nf, D(Color.White), new RectangleF(x, y, nameW, 16 * k));
            if (sub.Length > 0) ModernSkin.Text(g, sub, sf, D(ModernTheme.Dim), new RectangleF(x + nameW + 6 * k, y + 1 * k, subW, 16 * k));
            y += 21 * k;
            Soc.Bar(g, new RectangleF(x, y, w, 9 * k), c.Hp / 100f, Color.FromArgb(0x6a, 0x0c, 0x14), Color.FromArgb(0xc8, 0x30, 0x3a), c.Hp >= 0 ? c.Hp + "%" : string.Empty);
            y += 12 * k;
            Soc.Bar(g, new RectangleF(x, y, w, 9 * k), c.Mp / 100f, Color.FromArgb(0x12, 0x30, 0x6e), Color.FromArgb(0x3a, 0x7e, 0xe0), c.Mp >= 0 ? c.Mp + "%" : string.Empty);
            y += 14 * k;
            float tx = x;
            if (!double.IsNaN(c.Angle))
            {
                Arrow(g, x + 6 * k, y + 7.5f * k, 6 * k, c.Angle);
                tx += 16 * k;
            }
            float buffsW = c.Buffs.Count * 17 * k;
            ModernSkin.Text(g, c.Where, ModernSkin.Font(10), D(c.Far ? ModernTheme.Warn : ModernTheme.Dim), new RectangleF(tx, y, Math.Max(10, x + w - tx - buffsW - 4 * k), 15 * k));
        }

        /// <summary>The buff icons (library pictures: render time), right-aligned on the card's last line.</summary>
        private static void DrawCardIcons(PaintBox b, Card c)
        {
            if (c.Buffs.Count == 0) return;
            float k = ModernRoot.K, s = 15 * k;
            float x = b.DisplayLocation.X + b.Size.Width - 10 * k - c.Buffs.Count * 17 * k + 2 * k, y = b.DisplayLocation.Y + (9 + 21 + 12 + 14) * k;
            for (int i = 0; i < c.Buffs.Count; i++)
            {
                HudKit.BuffIcon(c.Buffs[i], out MLibrary lib, out int index);
                HudKit.DrawScaled(lib, index, new RectangleF(x + i * 17 * k, y, s, s), Color.White);
            }
        }

        private static void Crown(Graphics g, float cx, float top, float k)
        {
            // group.ts CROWN "M3 8l4 4 5-7 5 7 4-4-2 11H5z" in a 24 box, drawn 15 px
            float s = 15 * k / 24f, ox = cx - 12 * s, oy = top - 9 * k;
            PointF P(float x, float y) => new PointF(ox + x * s, oy + y * s);
            using var path = new GraphicsPath();
            path.AddPolygon(new[] { P(3, 8), P(7, 12), P(12, 5), P(17, 12), P(21, 8), P(19, 19), P(5, 19) });
            using (var br = new SolidBrush(Color.FromArgb(0xff, 0xd7, 0x5a))) g.FillPath(br, path);
            using (var pen = new Pen(Color.Black, Math.Max(1f, k))) g.DrawPath(pen, path);
        }

        private static void Arrow(Graphics g, float cx, float cy, float r, double angle)
        {
            // group.ts ARROW "M12 3l6 16-6-4-6 4z": a north-pointing dart, rotated by the bearing
            float sin = (float)Math.Sin(angle), cos = (float)Math.Cos(angle);
            PointF Rot(float x, float y) => new PointF(cx + x * cos - y * sin, cy + x * sin + y * cos);
            using var path = new GraphicsPath();
            path.AddPolygon(new[] { Rot(0, -r), Rot(r * 0.6f, r * 0.75f), Rot(0, r * 0.35f), Rot(-r * 0.6f, r * 0.75f) });
            using var br = new SolidBrush(ModernTheme.Accent);
            g.FillPath(br, path);
        }

        // ------------------------------------------------------------------ actions (nothing changes until the server answers)

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            if (_noteLbl != null) _noteLbl.Text = _note;
        }

        /// <summary>GroupDialog.AddMember: C.AddMember (the server checks the leader, the size and the target).</summary>
        private void Invite()
        {
            GroupState g = Model;
            string me = Soc.Me;
            string name = _invite.Text.Trim();
            if (name.Length == 0) { _invite.Focus(); return; }
            if (g.Members.Count >= Globals.MaxGroup) { Say(W.T(ClientTextKeys.GroupHasMaxMembers)); return; }
            if (!IsLeader) { Say(W.T(ClientTextKeys.YouAreNotGroupLeader)); return; }
            if (string.Equals(name, me, StringComparison.OrdinalIgnoreCase)) { Say(W.T(ClientTextKeys.ModernSocialNotYourself)); return; }
            foreach (string m in g.Members)
                if (string.Equals(m, name, StringComparison.OrdinalIgnoreCase)) { Say(W.T(ClientTextKeys.ModernSocialAlreadyInGroup, m)); return; }
            Network.Enqueue(new C.AddMember { Name = name });
            _invite.Text = string.Empty;
            Say(W.T(ClientTextKeys.ModernSocialInviteSent, name));
        }

        /// <summary>GroupDialog.DelMember: C.DelMember (leader only; S.DeleteMember removes the card).</summary>
        private void Kick()
        {
            string name = _picked;
            if (name.Length == 0 || name == Soc.Me || !IsLeader) return;
            Network.Enqueue(new C.DelMember { Name = name });
            Say(W.T(ClientTextKeys.ModernSocialRemoving, name));
        }

        /// <summary>The Allow group switch: C.SwitchGroup, locked until S.SwitchGroup. Off while grouped asks first (it leaves).</summary>
        private void SetAllow(bool on)
        {
            GroupState g = Model;
            _allow.Set(g.AllowGroup); // the server's state until the echo
            if (on == g.AllowGroup || _allowPending) return;
            if (!on && g.Members.Count > 0) { Leave(); return; }
            Send(W.T(on ? ClientTextKeys.ModernSocialGroupAllowed : ClientTextKeys.ModernSocialGroupRefused));
        }

        /// <summary>Leave = C.SwitchGroup off: the server takes you out of the group (PlayerObject.SwitchGroup → LeaveGroup).</summary>
        private void Leave()
        {
            GroupState g = Model;
            if (g.Members.Count == 0 || _allowPending) return;
            bool lead = IsLeader && g.Members.Count > 1;
            string text = W.T(ClientTextKeys.ModernSocialLeaveAsk) + (lead ? " " + W.T(ClientTextKeys.ModernSocialLeaveNextLeads) : string.Empty) + "\n" + W.T(ClientTextKeys.ModernSocialLeaveAllowOff);
            ModernRoot.Instance?.Popups.Ask(text, yes =>
            {
                if (!yes || IsDisposed || Model.Members.Count == 0 || !Model.AllowGroup || _allowPending) return;
                Send(W.T(ClientTextKeys.ModernSocialLeaving));
            }, false, PopupKind.Warning, W.T(ClientTextKeys.ModernSocialLeaveGroup));
        }

        private void Send(string note)
        {
            _allowPending = true;
            _allow.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
            Network.Enqueue(new C.SwitchGroup { AllowGroup = !Model.AllowGroup });
            Say(note);
            _dirty = true;
        }
    }
}
