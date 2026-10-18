// Ported from WebClient/src/ui/modern/windows/mentor.ts (MentorWindow): the new UI's mentor window over
// GameScene.Models.Relationship (S.MentorUpdate partner name / level / online / banked EXP, the phase 2.5 S.MentorDetails start /
// end dates + partner class, the finder C.FindMentor → S.MentorCandidates), Models.AllowToggles and Models.ServerRules (the
// MentorSystem numbers). The classic MentorDialog's rules are kept: the higher level is the mentor, the banked EXP shows to the
// mentor only, Add asks for a name (C.AddMentor), End asks to confirm (C.CancelMentor). Banner: the mentor and mentee medals,
// the term ring (S.MentorDetails; hidden until it arrives) and the buff chips (the mentor's damage buff while both are online,
// the mentee's EXP bonus only while grouped with the partner nearby and alive). Column A: graduation, the EXP bank (mentor),
// perks, Group invite / Mentor chat ("!#" prefill) / End mentorship, and the Allow mentor requests switch (C.SetAllowToggle
// Mentor, locked until S.AllowToggles; a server without toggles: the classic C.AllowMentor flip). Column B: the term, the find
// board while free (C.FindMentor with a level range, 3 s throttle; Request = C.AddMentor; Add by name…), the rules.
// The incoming request is ModernUi.MentorRequest's toast. Buffs / the partner in view / your level are checked once a second.
// HIDDEN as on the web: the partner's location, the cancel cooldown, the graduation track's start level.
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class MentorWindow : ModernWindow
    {
        public const string WindowKey = "mentor";
        private const int WidthHud = 760, BanH = 116, BodyH = 440, ColA = 330;

        private string _liveKey = string.Empty, _sentTo = string.Empty, _minText = string.Empty, _maxText = string.Empty, _boardNote = string.Empty;
        private bool _searched, _dirty = true, _togglePending;
        private long _restUntil, _nextTick;

        private PaintBox _ban;
        private ProgressRing _ring;
        private ScrollView _colA, _colB;
        private ToggleSwitch _allow;
        private ModernTextField _min, _max;
        private ModernButton _searchBtn, _addBtn, _endBtn;
        private readonly List<ModernButton> _requestBtns = new List<ModernButton>();
        private readonly Action _changed, _candidates, _toggles, _other;

        public MentorWindow() : base(WindowKey, W.T(ClientTextKeys.ModernSocialMentorship), -1, 40)
        {
            ContentWidth = WidthHud;
            ContentHeight = BanH + BodyH;
            _changed = () => { _sentTo = string.Empty; _dirty = true; };
            _candidates = () => { _searched = true; _boardNote = string.Empty; _dirty = true; };
            _toggles = () => { _togglePending = false; _dirty = true; };
            _other = () => _dirty = true;
        }

        private static RelationshipState Rel => GameScene.Models.Relationship;
        private static bool Bond => Rel.MentorLevel != 0 && Rel.MentorName.Length > 0;
        private static int MyLevel => GameScene.User?.Level ?? 0;
        private static bool AmMentor => Bond && MyLevel > Rel.MentorLevel;
        private static bool HasBuff(BuffType t) => GameScene.Models.Buffs.Has(t);
        private static bool Grouped => Rel.MentorName.Length > 0 && GameScene.Models.Group.Contains(Rel.MentorName);
        private static bool Nearby { get { PlayerObject p = Soc.InView(Rel.MentorName); return p != null && !p.Dead; } }
        private static bool AllowOn => GameScene.Models.AllowToggles.Known ? GameScene.Models.AllowToggles.Get(AllowToggle.Mentor) : _legacyAllow;
        /// <summary>A server without S.AllowToggles: the classic flip, tracked locally like MentorDialog's button (starts allowed).</summary>
        private static bool _legacyAllow = true;

        // ---- for the UI test
        internal ModernTextField MinField => _min;
        internal ModernTextField MaxField => _max;
        internal ModernButton SearchButton => _searchBtn;
        internal ModernButton AddButton => _addBtn;
        internal ModernButton EndButton => _endBtn;
        internal ToggleSwitch AllowSwitch => _allow;
        internal ModernButton RequestButton(int i) => i >= 0 && i < _requestBtns.Count ? _requestBtns[i] : null;
        internal string BoardNote => _boardNote;
        internal string SentTo => _sentTo;

        protected override void Build()
        {
            int top = TitleHeight;
            _ban = new PaintBox(PaintBanner) { Parent = this, Location = new Point(S(1), S(top)), Size = new Size(S(WidthHud) - 2, S(BanH)), NotControl = true };
            _ring = new ProgressRing(80, 6) { Parent = this, Location = new Point(S(WidthHud / 2 - 40), S(top + 6)), Visible = false };
            W.Divider(this, 0, top + BanH, WidthHud);
            int body = top + BanH + 1;
            _colA = new ScrollView(ColA, BodyH - 1) { Parent = this, Location = new Point(0, S(body)), Background = W.Page };
            _colA.Framed = false;
            W.VDivider(this, ColA, body, BodyH - 1);
            _colB = new ScrollView(WidthHud - ColA - 1, BodyH - 1) { Parent = this, Location = new Point(S(ColA + 1), S(body)), Background = W.Page };
            _colB.Framed = false;
            _dirty = true;
            Render();
        }

        protected override void Shown()
        {
            GameModels m = GameScene.Models;
            m.Relationship.MentorChanged.On(_changed);
            m.Relationship.CandidatesChanged.On(_candidates);
            m.AllowToggles.Changed.On(_toggles);
            m.ServerRules.Changed.On(_other);
            m.Group.Changed.On(_other);
            if (_minText.Length == 0) DefaultRange();
            _dirty = true;
        }

        protected override void Closed()
        {
            GameModels m = GameScene.Models;
            if (m == null) return;
            m.Relationship.MentorChanged.Off(_changed);
            m.Relationship.CandidatesChanged.Off(_candidates);
            m.AllowToggles.Changed.Off(_toggles);
            m.ServerRules.Changed.Off(_other);
            m.Group.Changed.Off(_other);
        }

        public override void Process()
        {
            base.Process();
            if (CMain.Time >= _nextTick)
            {
                _nextTick = CMain.Time + 1000;
                if (StateKey() != _liveKey) _dirty = true;
                if (_restUntil > 0 && CMain.Time >= _restUntil) { _restUntil = 0; _dirty = true; }
            }
            if (_dirty) Render();
        }

        private string StateKey() => MyLevel + "|" + HasBuff(BuffType.Mentor) + "|" + HasBuff(BuffType.Mentee) + "|" + Grouped + "|" + Nearby + "|" +
            (Rel.HasMentorDetails ? (CMain.Now.Ticks / TimeSpan.TicksPerMinute).ToString() : "0") + "|" + AllowOn;

        /// <summary>The level range of a search: from the lowest level the server accepts (your level + MentorLevelGap) up.</summary>
        private void DefaultRange()
        {
            int gap = Soc.Rules?.MentorLevelGap ?? 0;
            _minText = Math.Min(65535, MyLevel + gap).ToString();
            _maxText = Math.Min(65535, MyLevel + gap + 30).ToString();
        }

        // ------------------------------------------------------------------ actions

        private void Search()
        {
            if (Rel.MentorLevel != 0) return;
            ushort lo = ushort.TryParse(_minText.Trim(), out ushort a) && a > 0 ? a : (ushort)1;
            ushort hi = ushort.TryParse(_maxText.Trim(), out ushort b) && b > 0 ? b : ushort.MaxValue;
            if (hi < lo) (lo, hi) = (hi, lo);
            if (!Rel.FindMentor(lo, hi)) { _boardNote = W.T(ClientTextKeys.ModernSocialSearchTooFast); _dirty = true; return; }
            _boardNote = W.T(ClientTextKeys.ModernSocialSearching);
            _restUntil = CMain.Time + RelationshipState.FindMentorThrottleMs; // the server's throttle: the button rests meanwhile
            _dirty = true;
        }

        private void Request(string name)
        {
            if (Bond || string.IsNullOrEmpty(name)) return;
            Network.Enqueue(new C.AddMentor { Name = name });
            _sentTo = name;
            _dirty = true;
        }

        /// <summary>MentorDialog's add button: ask for the mentor's name, then C.AddMentor.</summary>
        private void AddByName()
        {
            if (Bond)
            {
                GameScene.Models.Chat.Add(W.T(ClientTextKeys.YouAlreadyHaveMentor), ChatType.System);
                return;
            }
            ModernRoot.Instance?.Popups.Input(W.T(ClientTextKeys.ModernSocialAddMentor), string.Empty, 20, name =>
            {
                if (string.IsNullOrEmpty(name) || Bond) return;
                Request(name);
            }, W.T(ClientTextKeys.MentorEnterName));
        }

        /// <summary>MentorDialog's remove button: CancellingMentorshipCausesCooldown, then C.CancelMentor.</summary>
        private void Cancel()
        {
            if (!Bond)
            {
                GameScene.Models.Chat.Add(W.T(ClientTextKeys.NoMentorship), ChatType.System);
                return;
            }
            var r = Soc.Rules;
            string partner = Rel.MentorName;
            string days = r != null ? W.T(r.MentorLength == 1 ? ClientTextKeys.ModernSocialOneDay : ClientTextKeys.ModernSocialNDays, r.MentorLength) : W.T(ClientTextKeys.ModernSocialAWhile);
            string bank = Rel.MentorOnline ? W.T(ClientTextKeys.ModernSocialBankPaidNow, partner) : W.T(ClientTextKeys.ModernSocialBankLost, partner);
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernSocialEndMentorAsk, days) + "\n" + bank + "\n\n" + W.T(ClientTextKeys.ModernSocialEndMentorQuestion, partner), yes =>
            {
                if (yes && Rel.MentorName == partner) Network.Enqueue(new C.CancelMentor());
            }, false, PopupKind.Danger, W.T(ClientTextKeys.ModernSocialEndMentorship));
        }

        /// <summary>Allow mentor requests: C.SetAllowToggle (the switch moves on S.AllowToggles); without toggles the classic
        /// C.AllowMentor flip.</summary>
        private void ToggleAllow(bool on)
        {
            _allow.Set(AllowOn);
            if (_togglePending || on == AllowOn) return;
            if (GameScene.Models.AllowToggles.Known)
            {
                Network.Enqueue(new C.SetAllowToggle { Toggle = AllowToggle.Mentor, Value = on });
                _togglePending = true;
                _allow.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
            }
            else
            {
                Network.Enqueue(new C.AllowMentor());
                _legacyAllow = on; // MentorDialog.AllowButton: the classic flip is tracked locally too
                _dirty = true;
            }
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_ban == null || _ban.IsDisposed || GameScene.User == null) return;
            _liveKey = StateKey();
            _ban.Invalidate();
            RenderRing();
            RenderA();
            RenderB();
        }

        private (float Frac, string Left)? Term()
        {
            if (!Rel.HasMentorDetails) return null;
            DateTime s = Soc.FromBinary(Rel.MentorDate), e = Soc.FromBinary(Rel.MentorEndsAt);
            if (s == DateTime.MinValue || e <= s) return null;
            DateTime now = CMain.Now;
            TimeSpan left = e > now ? e - now : TimeSpan.Zero;
            string text = left.TotalDays >= 1 ? Math.Ceiling(left.TotalDays) + "d" : Math.Ceiling(left.TotalHours) + "h";
            return ((float)((now - s).TotalSeconds / (e - s).TotalSeconds), text);
        }

        private void RenderRing()
        {
            var term = Bond ? Term() : null;
            _ring.Visible = term.HasValue;
            if (term.HasValue)
            {
                _ring.Set(term.Value.Frac, term.Value.Left, W.T(ClientTextKeys.ModernSocialLeft));
                _ring.Hint = Soc.Date(Soc.FromBinary(Rel.MentorDate)) + " – " + Soc.Date(Soc.FromBinary(Rel.MentorEndsAt));
            }
        }

        private void PaintBanner(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Soc.BannerGlow(g, b.Size, 0.5f, 0.9f);
            UserObject u = GameScene.User;
            if (u == null) return;
            RelationshipState rel = Rel;
            float sideW = (b.Size.Width - 190 * k - 36 * k - 28 * k) / 2f;
            RectangleF left = new RectangleF(18 * k, 0, sideW, b.Size.Height), right = new RectangleF(b.Size.Width - 18 * k - sideW, 0, sideW, b.Size.Height);
            float cx = b.Size.Width / 2f;
            string meSub = W.T(ClientTextKeys.ModernSocialLvClass, MyLevel, Soc.ClassName(u.Class));
            if (!Bond)
            {
                Soc.Who(g, left, false, Soc.Tag(u.Class), ModernTheme.Class(u.Class), true, true, W.T(ClientTextKeys.ModernSocialYouTag), u.Name, meSub, null);
                Soc.Who(g, right, true, "?", ModernTheme.Frame, false, false, W.T(ClientTextKeys.MENTOR), W.T(ClientTextKeys.ModernSocialNobodyYet), W.T(ClientTextKeys.ModernSocialFindBelow), null, false);
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialNoMentorship), ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(cx - 95 * k, 0, 190 * k, b.Size.Height), StringAlignment.Center);
                return;
            }
            bool amMentor = AmMentor;
            MirClass pc = rel.HasMentorDetails ? rel.PartnerClass : u.Class;
            string partnerSub = rel.HasMentorDetails ? W.T(ClientTextKeys.ModernSocialLvClass, rel.MentorLevel, Soc.ClassName(pc)) : W.T(ClientTextKeys.ModernHudPartyLevel, rel.MentorLevel);
            string online = W.T(rel.MentorOnline ? ClientTextKeys.ModernSocialOnline : ClientTextKeys.ModernSocialOffline);
            void Me(RectangleF r, bool rt, string tag) => Soc.Who(g, r, rt, Soc.Tag(u.Class), ModernTheme.Class(u.Class), true, true, tag, u.Name, meSub, null);
            void Partner(RectangleF r, bool rt, string tag) => Soc.Who(g, r, rt, rel.HasMentorDetails ? Soc.Tag(pc) : "?", rel.HasMentorDetails ? ModernTheme.Class(pc) : ModernTheme.Accent, rel.MentorOnline, false, tag, rel.MentorName, partnerSub, online);
            if (amMentor) { Me(left, false, W.T(ClientTextKeys.MENTOR)); Partner(right, true, W.T(ClientTextKeys.MENTEE)); }
            else { Partner(left, false, W.T(ClientTextKeys.MENTOR)); Me(right, true, W.T(ClientTextKeys.MENTEE)); }
            // centre: (the ring is its own control) the state line and the buff chips
            var r = Soc.Rules;
            bool hasTerm = _ring.Visible;
            if (!hasTerm)
                ModernSkin.Text(g, W.T(rel.MentorOnline ? ClientTextKeys.ModernSocialMentorshipActive : ClientTextKeys.ModernSocialPartnerOffline), ModernSkin.Font(11), ModernTheme.Dim,
                    new RectangleF(cx - 95 * k, 34 * k, 190 * k, 18 * k), StringAlignment.Center);
            bool expOn = rel.MentorOnline && Grouped && Nearby && (amMentor || HasBuff(BuffType.Mentee));
            string dmg = r != null ? W.T(ClientTextKeys.ModernSocialDamagePct, r.MentorDamageBoost) : W.T(ClientTextKeys.ModernSocialDamageBuff);
            string exp = r != null ? W.T(ClientTextKeys.ModernSocialExpPct, r.MentorExpBoost) : W.T(ClientTextKeys.ModernSocialExpBuff);
            float w1 = Soc.ChipWidth(dmg), w2 = Soc.ChipWidth(exp), cy = hasTerm ? 90 * k : 58 * k;
            float x = cx - (w1 + w2 + 4 * k) / 2f;
            Soc.Chip(g, x, cy, dmg, rel.MentorOnline);
            Soc.Chip(g, x + w1 + 4 * k, cy, exp, expOn);
        }

        private int Label(ScrollView v, int icon, string text, int y, int w, string right = null)
        {
            Soc.Section(v.Content, icon, text, 14, y, w - 28, right);
            return y + 20;
        }

        private void RenderA()
        {
            int keep = _colA.ScrollY;
            Soc.Clear(_colA.Content);
            _endBtn = null;
            int w = ColA - 10, y = 12, bw = w - 28;
            var r = Soc.Rules;
            RelationshipState rel = Rel;
            if (Bond)
            {
                bool amMentor = AmMentor;
                int mentorLv = amMentor ? MyLevel : rel.MentorLevel;
                bool menteeYou = !amMentor;
                if (r != null)
                {
                    y = Label(_colA, ModernIcons.Cap, W.T(ClientTextKeys.ModernSocialGraduation), y, w);
                    int grad = mentorLv - r.MentorLevelGap + 1;
                    string t = menteeYou ? W.T(ClientTextKeys.ModernSocialGradYou, grad, r.MentorLevelGap) : W.T(ClientTextKeys.ModernSocialGradThem, rel.MentorName, grad, r.MentorLevelGap);
                    y = Soc.TextBox(_colA.Content, 14, y, bw, t) + 10;
                }
                if (amMentor)
                {
                    y = Label(_colA, ModernIcons.Bank, W.T(ClientTextKeys.ModernSocialExpBank), y, w);
                    string mentee = rel.MentorName;
                    string note = r != null ? W.T(ClientTextKeys.ModernSocialBankPct, r.MenteeExpBank, mentee) : W.T(ClientTextKeys.ModernSocialBankPart, mentee);
                    int nh = Soc.WrapHeight(note, 10, bw - 120);
                    int hh = Math.Max(44, nh + 16);
                    string big = W.N(rel.MenteeExp);
                    new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        W.Card(g, b.Size);
                        float bigW = Math.Max(60 * k, ModernSkin.Measure(big, ModernSkin.Font(20, true)) + 4);
                        ModernSkin.Text(g, big, ModernSkin.Font(20, true), ModernTheme.Gold, new RectangleF(10 * k, 0, bigW, b.Size.Height));
                        ModernSkin.Text(g, note, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(10 * k + bigW + 12 * k, 8 * k, b.Size.Width - bigW - 32 * k, b.Size.Height - 16 * k), StringAlignment.Near, StringAlignment.Center, false, true);
                    })
                    { Parent = _colA.Content, Location = new Point(S(14), S(y)), Size = new Size(S(bw), S(hh)), NotControl = true };
                    y += hh + 10;
                }
                bool grouped = Grouped, nearby = Nearby;
                bool expOn = rel.MentorOnline && grouped && nearby && (amMentor || HasBuff(BuffType.Mentee));
                var perks = new List<(bool On, string Text, string Sub)>
                {
                    (rel.MentorOnline, r != null ? W.T(ClientTextKeys.ModernSocialPerkDmg, r.MentorDamageBoost) : W.T(ClientTextKeys.ModernSocialPerkDmgAny), W.T(ClientTextKeys.ModernSocialBothOnline)),
                    (expOn, r != null ? W.T(ClientTextKeys.ModernSocialPerkExp, r.MentorExpBoost) : W.T(ClientTextKeys.ModernSocialPerkExpAny), W.T(ClientTextKeys.ModernSocialGroupedNearby)),
                };
                if (r != null && r.MentorSkillBoost) perks.Add((rel.MentorOnline && grouped && nearby, W.T(ClientTextKeys.ModernSocialPerkSkill), W.T(ClientTextKeys.ModernSocialGroupedNearby)));
                if (r == null || r.MenteeExpBank > 0) perks.Add((true, r != null ? W.T(ClientTextKeys.ModernSocialPerkBank, r.MenteeExpBank) : W.T(ClientTextKeys.ModernSocialPerkBankAny), W.T(ClientTextKeys.ModernSocialAlways)));
                y = Label(_colA, ModernIcons.Star, W.T(ClientTextKeys.ModernSocialPerks), y, w);
                int ph = 12 + perks.Count * 22;
                new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    W.Card(g, b.Size);
                    float py = 6 * k;
                    foreach (var p in perks)
                    {
                        using (var br = new SolidBrush(p.On ? ModernTheme.Ok : ModernSkin.Opaque(ModernTheme.Frame))) g.FillEllipse(br, 10 * k, py + 7 * k, 8 * k, 8 * k);
                        float sw = ModernSkin.Measure(p.Sub, ModernSkin.Font(10)) + 2;
                        ModernSkin.Text(g, p.Text, ModernSkin.Font(12), ModernTheme.Text, new RectangleF(26 * k, py, b.Size.Width - 36 * k - sw, 22 * k));
                        ModernSkin.Text(g, p.Sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(b.Size.Width - 10 * k - sw, py, sw, 22 * k));
                        py += 22 * k;
                    }
                })
                { Parent = _colA.Content, Location = new Point(S(14), S(y)), Size = new Size(S(bw), S(ph)), NotControl = true };
                y += ph + 10;
                GroupState grp = GameScene.Models.Group;
                int hw = (bw - 6) / 2;
                var invite = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialGroupInvite), ButtonKind.Main) { Tooltip = W.T(ClientTextKeys.ModernSocialInviteName, rel.MentorName) }, _colA.Content, 14, y);
                invite.Size = new Size(S(hw), S(26));
                string partner = rel.MentorName;
                invite.Click += (o, e) => Network.Enqueue(new C.AddMember { Name = partner });
                if (!rel.MentorOnline) invite.SetDisabled(true, W.T(ClientTextKeys.ModernSocialIsOffline, partner));
                else if (grouped) invite.SetDisabled(true, W.T(ClientTextKeys.ModernSocialAlreadyGrouped));
                else if (grp.Members.Count > 0 && grp.Members[0] != Soc.Me) invite.SetDisabled(true, W.T(ClientTextKeys.ModernSocialOnlyLeaderInvites));
                var talk = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialMentorChat)) { Tooltip = W.T(ClientTextKeys.ModernSocialMentorChatHint) }, _colA.Content, 14 + hw + 6, y);
                talk.Size = new Size(S(hw), S(26));
                talk.Click += (o, e) => Soc.Prefill("!#");
                y += 32;
                _endBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialEndMentorship), ButtonKind.Warn), _colA.Content, 14, y);
                _endBtn.Size = new Size(S(bw), S(26));
                _endBtn.Click += (o, e) => Cancel();
                y += 36;
            }
            else
            {
                y = Soc.TextBox(_colA.Content, 14, y, bw, W.T(ClientTextKeys.ModernSocialNoMentorHelp), W.T(ClientTextKeys.ModernSocialNoMentorLead)) + 12;
            }
            // Allow mentor requests
            _allow = W.At(new ToggleSwitch(AllowOn, true) { Tooltip = W.T(ClientTextKeys.ModernSocialAllowMentor) }, _colA.Content, 14, y + 2);
            _allow.Changed += ToggleAllow;
            if (_togglePending) _allow.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
            W.Label(_colA.Content, W.T(ClientTextKeys.ModernSocialAllowMentor), 52, y, bw - 40, 20, 12);
            y += 28;
            new ModernPanel { Parent = _colA.Content, Location = new Point(0, S(y)), Size = new Size(1, 1) };
            _colA.FitContent(0);
            _colA.ScrollTo(keep);
        }

        private void RenderB()
        {
            int keep = _colB.ScrollY;
            Soc.Clear(_colB.Content);
            _requestBtns.Clear();
            _min = _max = null;
            _searchBtn = _addBtn = null;
            int w = WidthHud - ColA - 11, y = 12, bw = w - 28;
            var r = Soc.Rules;
            RelationshipState rel = Rel;
            if (Bond && rel.HasMentorDetails)
            {
                y = Label(_colB, ModernIcons.Calendar, W.T(ClientTextKeys.ModernSocialTerm), y, w);
                string t = W.T(ClientTextKeys.ModernSocialTermText, Soc.Date(Soc.FromBinary(rel.MentorDate)), Soc.Date(Soc.FromBinary(rel.MentorEndsAt)), rel.MentorName, rel.MentorLevel, Soc.ClassName(rel.PartnerClass));
                y = Soc.TextBox(_colB.Content, 14, y, bw, t) + 10;
            }
            if (!Bond)
            {
                y = Label(_colB, ModernIcons.Book, W.T(ClientTextKeys.ModernSocialFindMentor), y, w,
                    r != null ? W.T(ClientTextKeys.ModernSocialFindRange, r.MentorLevelGap) : W.T(ClientTextKeys.ModernSocialFindRangeAny));
                W.Label(_colB.Content, W.T(ClientTextKeys.ModernSocialLevelWord), 14, y + 4, 40, 18, 11, false, ModernTheme.Dim);
                _min = W.At(new ModernTextField(64, 26) { MaxLength = 5 }, _colB.Content, 54, y);
                _min.Text = _minText;
                _min.TextChanged += v => _minText = v ?? string.Empty;
                _min.Submitted += _ => Search();
                W.Label(_colB.Content, W.T(ClientTextKeys.ModernSocialTo), 124, y + 4, 24, 18, 11, false, ModernTheme.Dim, StringAlignment.Center);
                _max = W.At(new ModernTextField(64, 26) { MaxLength = 5 }, _colB.Content, 150, y);
                _max.Text = _maxText;
                _max.TextChanged += v => _maxText = v ?? string.Empty;
                _max.Submitted += _ => Search();
                _searchBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialSearch), ButtonKind.Main) { Tooltip = W.T(ClientTextKeys.ModernSocialSearchHint) }, _colB.Content, 222, y);
                _searchBtn.Size = new Size(S(90), S(26));
                _searchBtn.Click += (o, e) => Search();
                if (_restUntil > 0) _searchBtn.SetDisabled(true, W.T(ClientTextKeys.ModernSocialWaitSearch));
                y += 32;
                List<ClientMentorCandidate> list = rel.Candidates;
                string note = _boardNote.Length > 0 ? _boardNote : !_searched ? W.T(ClientTextKeys.ModernSocialSearchHelp)
                    : list.Count > 0 ? W.T(list.Count == 1 ? ClientTextKeys.ModernSocialFoundOne : ClientTextKeys.ModernSocialFoundMany, list.Count) : W.T(ClientTextKeys.ModernSocialFoundNone);
                y = Soc.Para(_colB.Content, note, 14, y, bw, 11, ModernTheme.Dim) + 4;
                if (_searched)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        ClientMentorCandidate c = list[i];
                        bool odd = i % 2 == 1;
                        new PaintBox((g, b) =>
                        {
                            float k = ModernRoot.K;
                            if (odd) using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.04f))) using (var p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k)) g.FillPath(br, p);
                            RectangleF tg = new RectangleF(8 * k, 6 * k, 34 * k, 22 * k);
                            using (var p = ModernSkin.RoundRect(tg, 6 * k))
                            {
                                using (var br = new SolidBrush(ModernSkin.Opaque(ModernTheme.PanelB))) g.FillPath(br, p);
                                ModernSkin.Stroke(g, p, ModernTheme.Class(c.Class));
                            }
                            ModernSkin.Text(g, Soc.Tag(c.Class), ModernSkin.Font(9, true), ModernTheme.Text, tg, StringAlignment.Center, StringAlignment.Center, false);
                            ModernSkin.Text(g, c.Name, ModernSkin.Font(12, true), Color.White, new RectangleF(52 * k, 3 * k, b.Size.Width - 150 * k, 16 * k));
                            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialLvClass, c.Level, Soc.ClassName(c.Class)), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(52 * k, 18 * k, b.Size.Width - 150 * k, 14 * k));
                        })
                        { Parent = _colB.Content, Location = new Point(S(14), S(y)), Size = new Size(S(bw), S(34)), NotControl = true };
                        bool sent = _sentTo == c.Name;
                        string name = c.Name;
                        var req = W.At(new ModernButton(W.T(sent ? ClientTextKeys.ModernSocialSent : ClientTextKeys.ModernSocialRequest), sent ? ButtonKind.Normal : ButtonKind.Main) { Tooltip = W.T(ClientTextKeys.ModernSocialAskMentor, name) }, _colB.Content, 14 + bw - 8 - 80, y + 5);
                        req.Size = new Size(S(80), S(24));
                        req.Click += (o, e) => Request(name);
                        if (sent) req.SetDisabled(true, W.T(ClientTextKeys.ModernSocialRequestSentWait));
                        _requestBtns.Add(req);
                        y += 36;
                    }
                }
                y += 4;
                _addBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialAddByNameDots)) { Tooltip = W.T(ClientTextKeys.ModernSocialAddMentorHint) }, _colB.Content, 14, y);
                _addBtn.Size = new Size(S(bw), S(26));
                _addBtn.Click += (o, e) => AddByName();
                y += 38;
            }
            if (r != null)
            {
                y = Label(_colB, ModernIcons.Info, W.T(ClientTextKeys.ModernSocialHowItWorks), y, w);
                var rows = new List<(string V, string T)>
                {
                    (r.MentorLevelGap + "+", W.T(ClientTextKeys.ModernSocialRuleGap)),
                    (W.T(ClientTextKeys.ModernSocialNDays, r.MentorLength), W.T(ClientTextKeys.ModernSocialRuleLength)),
                    ("+" + r.MentorDamageBoost + "%", W.T(ClientTextKeys.ModernSocialRuleDamage)),
                    ("+" + r.MentorExpBoost + "%", W.T(ClientTextKeys.ModernSocialRuleExp)),
                    (r.MenteeExpBank + "%", W.T(ClientTextKeys.ModernSocialRuleBank)),
                };
                if (r.MentorSkillBoost) rows.Add(("×2", W.T(ClientTextKeys.ModernSocialRuleSkill)));
                int cw = (bw - 6) / 2;
                var heights = new int[(rows.Count + 1) / 2];
                for (int i = 0; i < rows.Count; i++) heights[i / 2] = Math.Max(heights[i / 2], Math.Max(48, 30 + Soc.WrapHeight(rows[i].T, 11, cw - 16)));
                int ry = y;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    int rh = heights[i / 2];
                    int x = 14 + i % 2 * (cw + 6);
                    new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        using (var p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1), ModernTheme.Radius * k))
                            ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.45f));
                        ModernSkin.Text(g, row.V, ModernSkin.Font(14, true), ModernTheme.Accent, new RectangleF(8 * k, 5 * k, b.Size.Width - 16 * k, 20 * k));
                        ModernSkin.Text(g, row.T, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(8 * k, 25 * k, b.Size.Width - 16 * k, b.Size.Height - 28 * k), StringAlignment.Near, StringAlignment.Near, false, true);
                    })
                    { Parent = _colB.Content, Location = new Point(S(x), S(ry)), Size = new Size(S(cw), S(rh)), NotControl = true };
                    if (i % 2 == 1 || i == rows.Count - 1) ry += rh + 6;
                }
                y = ry;
            }
            new ModernPanel { Parent = _colB.Content, Location = new Point(0, S(y)), Size = new Size(1, 1) };
            _colB.FitContent(0);
            _colB.ScrollTo(keep);
        }
    }
}
