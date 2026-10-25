// Ported from WebClient/src/ui/modern/windows/npc.ts (NpcWindow): the new UI's NPC dialogue. Desktop counterpart: NPCDialog +
// BigButtonDialog + the QuestListDialog its quest button opens (Client/MirScenes/Dialogs/NPCDialogs.cs, QuestDialogs.cs).
// Opens only on the server's answer (S.NPCResponse with something to show, NpcState.Response) and closes with the session (an
// empty page, @Exit, walking away: MapObject removal → CloseNpcDialog). Closing it is NPCDialog.Hide (no packet, every NPC window
// goes with it). The page: <text/@key> links, <<text/@key>> big buttons (numbered option cards, keys 1-9 while the pointer is on
// the window), {text/Colour}, (text/url) web links (http(s) only), [ITEM|MONSTER|NPC:n(|name)] / <$…:n> info links (the item
// tooltip; a monster / NPC card like NPCDialog.ShowTooltip); a row holding only page links becomes option cards too. Service
// chips = the page's own service links. The typewriter (180 chars/s; click or Space finishes it), the conversation log (Log),
// the Quests tab (quest V2: what S.QuestAvailability says this NPC offers / finishes; accept C.QuestAccept, hand in C.QuestTurnIn
// with the choice-group picks, a met step's branch C.QuestChoose, abandon C.QuestAbandon after a question; buttons lock until the
// quest's state changes or 5 s: QuestActions), the portrait (the NPC's own library frame,
// as it animates in the world).
// HIDDEN (no data): the NPC title line, the mock's fixed service list, the Back stack; the info card has no picture.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirSounds;
using Client.MirScenes.Modern.Core.Widgets;
using S = ServerPackets;
using C = ClientPackets;
using DxMatrix = SlimDX.Matrix;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class NpcWindow : ModernWindow
    {
        public const string WindowKey = "npc";
        private const int WidthHud = 780, HeightHud = 440, SideW = 230, MainX = 231, TypeRate = 180;

        private static readonly Dictionary<string, (int Icon, ClientTextKeys Label)> Services = new Dictionary<string, (int, ClientTextKeys)>
        {
            ["BUY"] = (ModernIcons.Shop, ClientTextKeys.ModernNpcSvcBuy), ["BUYNEW"] = (ModernIcons.Shop, ClientTextKeys.ModernNpcSvcBuyNew),
            ["BUYUSED"] = (ModernIcons.Shop, ClientTextKeys.ModernNpcSvcBuyUsed), ["BUYSELL"] = (ModernIcons.Shop, ClientTextKeys.ModernNpcSvcBuySell),
            ["BUYSELLNEW"] = (ModernIcons.Shop, ClientTextKeys.ModernNpcSvcBuySell), ["PEARLBUY"] = (ModernIcons.Gem, ClientTextKeys.ModernNpcSvcPearl),
            ["SELL"] = (ModernIcons.Coin, ClientTextKeys.ModernNpcSvcSell), ["BUYBACK"] = (ModernIcons.Back, ClientTextKeys.ModernNpcSvcBuyBack),
            ["REPAIR"] = (ModernIcons.Wrench, ClientTextKeys.ModernNpcSvcRepair), ["SREPAIR"] = (ModernIcons.Wrench, ClientTextKeys.ModernNpcSvcSRepair),
            ["STORAGE"] = (ModernIcons.Bank, ClientTextKeys.ModernNpcSvcStorage), ["CONSIGN"] = (ModernIcons.Tag, ClientTextKeys.ModernNpcSvcConsign),
            ["MARKET"] = (ModernIcons.Tag, ClientTextKeys.ModernNpcSvcMarket), ["CRAFT"] = (ModernIcons.Craft, ClientTextKeys.ModernNpcSvcCraft),
            ["REFINE"] = (ModernIcons.Spark, ClientTextKeys.ModernNpcSvcRefine), ["REFINECHECK"] = (ModernIcons.Spark, ClientTextKeys.ModernNpcSvcRefineCheck),
            ["REFINECOLLECT"] = (ModernIcons.Spark, ClientTextKeys.ModernNpcSvcRefineCollect), ["REPLACEWEDDINGRING"] = (ModernIcons.Ring, ClientTextKeys.ModernNpcSvcWedRing),
            ["AWAKENING"] = (ModernIcons.Star, ClientTextKeys.ModernNpcSvcAwaken), ["DISASSEMBLE"] = (ModernIcons.Swap, ClientTextKeys.ModernNpcSvcDisassemble),
            ["DOWNGRADE"] = (ModernIcons.ChevronDown, ClientTextKeys.ModernNpcSvcDowngrade), ["RESET"] = (ModernIcons.Refresh, ClientTextKeys.ModernNpcSvcReset),
            ["SENDPARCEL"] = (ModernIcons.Mail, ClientTextKeys.ModernNpcSvcSendParcel), ["COLLECTPARCEL"] = (ModernIcons.Inbox, ClientTextKeys.ModernNpcSvcCollectParcel),
            ["CREATEGUILD"] = (ModernIcons.Guild, ClientTextKeys.ModernNpcSvcCreateGuild), ["REQUESTWAR"] = (ModernIcons.Flag, ClientTextKeys.ModernNpcSvcGuildWar),
            ["GUILDTERRITORY"] = (ModernIcons.Flag, ClientTextKeys.ModernNpcSvcTerritory), ["CREATEHERO"] = (ModernIcons.Hero, ClientTextKeys.ModernNpcSvcCreateHero),
            ["MANAGEHERO"] = (ModernIcons.Hero, ClientTextKeys.ModernNpcSvcManageHero),
        };

        private readonly NpcState _state;
        private int _tab;
        private bool _showLog, _dirty = true, _questsDirty = true;
        private int _shownSeq = -1, _shownLog = -1;
        private uint _shownGold = uint.MaxValue;
        private readonly List<(string Text, string Key, string Colour)> _choices = new List<(string, string, string)>();
        private readonly List<(int Quest, bool Offer)> _npcQuests = new List<(int, bool)>();
        private readonly List<(int Quest, bool Pending)> _shownQuests = new List<(int, bool)>();
        private long _nextQuestTick;

        // typewriter
        private bool _typing, _noRetype;
        private long _typeStart, _nextType;

        private ModernLabel _whoName, _whoPlace, _gold, _hint, _mark;
        private NpcPortrait _portrait;
        private ModernPanel _chips;
        private Tabs _tabs;
        private ModernButton _logBtn, _mainBtn;
        private ScrollView _logView, _talk, _quests;
        private NpcRichText _say;
        private ModernPanel _linkAnchor;
        private ModernPanel _opts;
        private PaintBox _card;
        private string _cardTitle = string.Empty, _cardBody = string.Empty;
        private int _cardRun = -1;
        private readonly Action _changed, _questsChanged;

        public NpcWindow(NpcState state) : base(WindowKey, W.T(ClientTextKeys.ModernNpcFallbackName), 40, 50)
        {
            _state = state;
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _questsChanged = () => { _questsDirty = true; _dirty = true; };
        }

        internal bool Typing => _typing;
        internal int TabShown => _tab;
        internal int ChoiceCount => _choices.Count;
        internal bool LogShown => _showLog;
        internal string CardTitle => _card != null && _card.Visible ? _cardTitle : string.Empty;
        internal NpcRichText Say => _say;
        internal ModernPanel Options => _opts;
        internal Tabs TabsControl => _tabs;
        internal ScrollView QuestList => _quests;

        protected override void Build()
        {
            int top = TitleHeight;
            // ---- side: portrait, name, service chips
            new PaintBox((g, b) => PaintSide(g, b)) { Parent = this, Location = new Point(1, S(top)), Size = new Size(S(SideW - 1), S(HeightHud - 1)), NotControl = true };
            _portrait = new NpcPortrait { Parent = this, Location = new Point(S(10), S(top + 8)), Size = new Size(S(210), S(200)) };
            _mark = W.Label(this, string.Empty, 10, top + 8, 210, 34, 26, true, ModernTheme.Warn, StringAlignment.Center);
            _whoName = W.Label(this, string.Empty, 10, top + 212, 210, 24, 18, true, Color.White, StringAlignment.Center);
            _whoPlace = W.Label(this, string.Empty, 10, top + 236, 210, 14, 10.5f, false, ModernTheme.Accent, StringAlignment.Center);
            _chips = new ModernPanel { Parent = this, Location = new Point(S(10), S(top + 258)), Size = new Size(S(210), S(HeightHud - 266)) };
            W.VDivider(this, SideW, top, HeightHud);

            // ---- main: tabs + log, the talk / quests views, the foot
            _tabs = W.At(new Tabs(new[] { W.T(ClientTextKeys.ModernNpcTalk), W.T(ClientTextKeys.ModernNpcQuests) }, _tab, 220), this, MainX + 12, top + 6);
            _tabs.Changed += i => { _tab = i; _questsDirty = true; _dirty = true; HideCard(); };
            _logBtn = new ModernButton(W.T(ClientTextKeys.ModernNpcLog)) { Parent = this, Tooltip = W.T(ClientTextKeys.ModernNpcLogHint) };
            _logBtn.AutoWidth(60);
            _logBtn.Location = new Point(S(WidthHud - 12) - _logBtn.Size.Width, S(top + 7));
            _logBtn.Click += (o, e) => { _showLog = !_showLog; _shownLog = -1; BuildViews(); _shownSeq = -1; _noRetype = true; _dirty = true; };
            W.Divider(this, MainX, top + 34, WidthHud - MainX);
            BuildViews();

            W.Divider(this, MainX, top + HeightHud - 34, WidthHud - MainX);
            _mainBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernNpcMain)) { Tooltip = W.T(ClientTextKeys.ModernNpcMainHint) }, this, MainX + 12, top + HeightHud - 28);
            _mainBtn.AutoWidth(60);
            _mainBtn.Click += (o, e) => Choose("@Main", W.T(ClientTextKeys.ModernNpcMain));
            _gold = W.Label(this, string.Empty, MainX + 90, top + HeightHud - 27, 170, 20, 11, true, ModernTheme.Gold);
            _hint = W.Label(this, W.T(ClientTextKeys.ModernNpcKeysHint), MainX + 250, top + HeightHud - 27, WidthHud - MainX - 262, 20, 10.5f, false, ModernTheme.Dim, StringAlignment.Far);

            _card = new PaintBox((g, b) => PaintCard(g, b)) { Parent = this, Size = new Size(S(260), S(150)), NotControl = true, Visible = false };
            _shownSeq = -1;
            _shownGold = uint.MaxValue;
            _dirty = _questsDirty = true;
            Render();
        }

        /// <summary>The log (when shown) above the talk / quests views; rebuilt when the log is toggled.</summary>
        private void BuildViews()
        {
            _logView?.Dispose();
            _talk?.Dispose();
            _quests?.Dispose();
            int top = TitleHeight + 35, bottom = TitleHeight + HeightHud - 35, w = WidthHud - MainX - 1;
            _logView = null;
            if (_showLog)
            {
                _logView = new ScrollView(w, 110) { Parent = this, Location = new Point(S(MainX), S(top)) };
                _logView.Framed = false;
                top += 111;
            }
            _talk = new ScrollView(w, bottom - top) { Parent = this, Location = new Point(S(MainX), S(top)), Background = W.Page };
            _talk.Framed = false;
            _say = new NpcRichText(w - 46) { Parent = _talk.Content, Location = new Point(S(18), S(14)) };
            _say.RunClicked += SayClicked;
            _say.RunHover += SayHover;
            _linkAnchor = new ModernPanel { Parent = _say, NotControl = true, Size = new Size(1, 1) };
            _opts = new ModernPanel { Parent = _talk.Content, Location = new Point(S(18), S(40)), Size = new Size(S(w - 46), S(10)) };
            _quests = new ScrollView(w, bottom - top) { Parent = this, Location = new Point(S(MainX), S(top)), Background = W.Page, Visible = false };
            _quests.Framed = false;
            _card?.BringToFront();
        }

        private void PaintSide(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            // radial accent glow behind the portrait
            using var path = new GraphicsPath();
            RectangleF e = new RectangleF(b.Size.Width * 0.1f, 20 * k, b.Size.Width * 0.8f, 230 * k);
            path.AddEllipse(e);
            using var br = new PathGradientBrush(path) { CenterColor = W.Over(ModernTheme.Accent, 0.16f), SurroundColors = new[] { W.Page } };
            g.FillEllipse(br, e);
            RectangleF floor = new RectangleF(b.Size.Width / 2f - 75 * k, 175 * k, 150 * k, 30 * k);
            using var fp = new GraphicsPath();
            fp.AddEllipse(floor);
            using var fb = new PathGradientBrush(fp) { CenterColor = W.Over(ModernTheme.Accent, 0.4f), SurroundColors = new[] { W.Over(ModernTheme.Accent, 0.12f) } };
            g.FillEllipse(fb, floor);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _state.Changed += _changed;
            ModernEvents.Quests.On(_questsChanged);
            _dirty = _questsDirty = true;
            _shownSeq = -1;
            Render();
        }

        protected override void Closed()
        {
            _state.Changed -= _changed;
            ModernEvents.Quests.Off(_questsChanged);
            FinishTyping();
            HideCard();
            _questsDirty = true;
            if (_state.Open) _state.Close(); // NPCDialog.Hide: every NPC window goes with it (no packet)
        }

        public override bool HandleKey(KeyEventArgs e)
        {
            if (e.Control || e.Alt) return false;
            if (e.KeyCode == Keys.Space && _typing)
            {
                FinishTyping();
                return true;
            }
            int n = e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9 ? e.KeyCode - Keys.D1 : e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad1 : -1;
            if (n >= 0 && !e.Shift && _tab == 0 && !_typing && IsMouseOver(CMain.MPoint) && n < _choices.Count)
            {
                Choose(_choices[n].Key, _choices[n].Text);
                return true;
            }
            return false;
        }

        public override void Process()
        {
            base.Process();
            _state.Process();
            if (_typing && CMain.Time >= _nextType)
            {
                _nextType = CMain.Time + 40;
                int want = (int)((CMain.Time - _typeStart) * TypeRate / 1000);
                if (want >= _say.TotalChars) FinishTyping();
                else _say.SetShown(want);
            }
            _portrait?.Tick();
            if (_shownGold != GameScene.Gold)
            {
                _shownGold = GameScene.Gold;
                _gold.Text = W.T(ClientTextKeys.ModernWinGold) + " " + W.N(GameScene.Gold);
            }
            if (_tab == 1 && _shownQuests.Count > 0 && CMain.Time >= _nextQuestTick)
            {
                // a quest lock lifts after 5 s without a reply (QuestActions): redraw its buttons then
                _nextQuestTick = CMain.Time + 1000;
                for (int i = 0; i < _shownQuests.Count; i++)
                    if (QuestActions.IsPending(_shownQuests[i].Quest) != _shownQuests[i].Pending) { _questsDirty = _dirty = true; break; }
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_talk == null || _talk.IsDisposed) return;
            var (name, place) = _state.Name;
            SetTitle(name.ToUpperInvariant());
            _whoName.Text = name;
            _whoPlace.Text = place;
            // the portrait and quests follow the clicked NPC (GameScene.QuestNPCID: a quest accept's S.NPCUpdate moves NPCID to the default NPC)
            NPCObject npc = MapControl.Objects != null && MapControl.Objects.TryGetValue(GameScene.QuestNPCID, out MapObject ob) ? ob as NPCObject : null;
            _portrait.Npc = npc;

            // quest V2: what the server says this NPC offers / finishes (S.QuestAvailability), no client-side accept rules
            NpcQuests(GameScene.QuestNPCID, _npcQuests);
            int ready = 0;
            foreach (var q in _npcQuests) if (q.Offer || GameScene.Models.Quests.Run(q.Quest)?.Status == QuestRunStatus.Ready) ready++;
            _tabs.Badge(1, ready);
            QuestIcon icon = npc?.QuestIcon ?? QuestIcon.None;
            (string mark, Color mc) = Marker(icon);
            _mark.Text = mark;
            _mark.Colour = mc;

            if (_shownSeq != _state.Seq) RenderPage();
            _mainBtn.Visible = !string.Equals(_state.LastPageKey, "@Main", StringComparison.OrdinalIgnoreCase);
            _logBtn.Text = W.T(_showLog ? ClientTextKeys.ModernNpcHideLog : ClientTextKeys.ModernNpcLog);
            if (_showLog && _logView != null && _shownLog != _state.LogVersion) RenderLog();

            _talk.Visible = _tab == 0;
            _quests.Visible = _tab == 1;
            _hint.Visible = _tab == 0;
            if (_tab == 1 && _questsDirty) RenderQuests(_npcQuests);
        }

        /// <summary>questMiniMapMarker: "!" to accept, "?" to hand in / in progress, in the quest type's colour.</summary>
        private static (string, Color) Marker(QuestIcon icon)
        {
            switch (icon)
            {
                case QuestIcon.ExclamationYellow: return ("!", ModernTheme.Warn);
                case QuestIcon.QuestionYellow: return ("?", ModernTheme.Warn);
                case QuestIcon.ExclamationBlue: return ("!", ModernTheme.Info);
                case QuestIcon.QuestionBlue: return ("?", ModernTheme.Info);
                case QuestIcon.ExclamationGreen: return ("!", ModernTheme.Ok);
                case QuestIcon.QuestionGreen: return ("?", ModernTheme.Ok);
                case QuestIcon.QuestionWhite: return ("?", ModernTheme.Dim);
            }
            return (string.Empty, ModernTheme.Dim);
        }

        // ------------------------------------------------------------------ the page

        private void RenderPage()
        {
            _shownSeq = _state.Seq;
            HideCard();
            NpcPage page = _state.Page;
            _choices.Clear();
            foreach (NpcBigButton b in page.BigButtons) _choices.Add((b.Text, b.Action, b.Colour));
            var runs = new List<NpcRun>();
            int blank = 0;
            bool any = false;
            foreach (List<NpcSegment> row in page.Rows)
            {
                List<NpcSegment> only = NpcPage.LinkOnly(row);
                if (only != null)
                {
                    foreach (NpcSegment s in only) _choices.Add((s.Text, s.Key, null));
                    continue;
                }
                bool empty = NpcPage.RowText(row).Trim().Length == 0;
                if (empty) { if (++blank > 1 || !any) continue; }
                else blank = 0;
                if (any) runs.Add(new NpcRun { Text = "\n" });
                any = true;
                foreach (NpcSegment s in row) runs.Add(ToRun(s));
            }
            while (runs.Count > 0 && runs[^1].Kind == NpcSegKind.Text && runs[^1].Text.Trim().Length == 0) runs.RemoveAt(runs.Count - 1);
            _say.SetRuns(runs);
            BuildOptions();
            BuildChips(page);
            if (_noRetype) { _noRetype = false; FinishTyping(); }
            else StartTyping();
        }

        private static NpcRun ToRun(NpcSegment s)
        {
            switch (s.Kind)
            {
                case NpcSegKind.Link: return new NpcRun { Kind = NpcSegKind.Link, Text = s.Text, Key = s.Key };
                case NpcSegKind.Url: return new NpcRun { Kind = NpcSegKind.Url, Text = s.Text, Key = s.Key };
                case NpcSegKind.Colour: return new NpcRun { Kind = NpcSegKind.Text, Text = s.Text, Colour = NpcFmt.Named(s.Key, Color.Empty) };
                case NpcSegKind.Info: return new NpcRun { Kind = NpcSegKind.Info, Text = LinkName(s.Key, s.Index, s.Name), Key = s.Key, Index = s.Index };
            }
            return new NpcRun { Text = s.Text };
        }

        /// <summary>NPCDialog.GetDisplayNameForLink: the given name, else the info's (requested once when missing).</summary>
        internal static string LinkName(string kind, int idx, string provided)
        {
            if (!string.IsNullOrEmpty(provided)) return provided;
            switch (kind)
            {
                case "ITEM":
                    ItemInfo i = FindItem(idx);
                    if (i != null) return string.IsNullOrEmpty(i.FriendlyName) ? i.Name : i.FriendlyName;
                    GameScene.RequestItemInfo(idx);
                    return W.T(ClientTextKeys.ModernNpcItemN, idx);
                case "MONSTER":
                    ClientMonsterInfo m = FindMonster(idx);
                    if (m != null) return string.IsNullOrEmpty(m.GameName) ? m.Name : m.GameName;
                    GameScene.RequestMonsterInfo(idx);
                    return W.T(ClientTextKeys.ModernNpcMonsterN, idx);
                default:
                    ClientNPCInfo n = FindNpc(idx);
                    if (n != null) return n.Name;
                    GameScene.RequestNPCInfo(idx);
                    return W.T(ClientTextKeys.ModernNpcNpcN, idx);
            }
        }

        private static ItemInfo FindItem(int idx)
        {
            foreach (ItemInfo i in GameScene.ItemInfoList) if (i.Index == idx) return i;
            return null;
        }

        private static ClientMonsterInfo FindMonster(int idx)
        {
            foreach (ClientMonsterInfo m in GameScene.MonsterInfoList) if (m.Index == idx) return m;
            return null;
        }

        private static ClientNPCInfo FindNpc(int idx)
        {
            foreach (ClientNPCInfo n in GameScene.NPCInfoList) if (n.Index == idx) return n;
            return null;
        }

        /// <summary>Option cards: numbered 1-9 (the rest still click); @Exit as a dashed card; a big button's colour as a dot.</summary>
        private void BuildOptions()
        {
            for (int i = _opts.Controls.Count - 1; i >= 0; i--) _opts.Controls[i].Dispose();
            int y = 0, w = (int)Math.Round(_opts.Size.Width / ModernRoot.K);
            for (int i = 0; i < _choices.Count; i++)
            {
                var c = _choices[i];
                int n = i;
                bool exit = string.Equals(c.Key, "@Exit", StringComparison.OrdinalIgnoreCase);
                Color dot = c.Colour != null && c.Colour != "RoyalBlue" ? NpcFmt.Named(c.Colour, Color.Empty) : Color.Empty;
                var card = new PaintBox((g, b) => PaintOption(g, b, n < 9 ? (n + 1).ToString() : string.Empty, c.Text, exit, dot))
                {
                    Parent = _opts,
                    Location = new Point(0, S(y)),
                    Size = new Size(S(w), S(32)),
                    Sound = SoundList.ButtonA,
                    Index = n,
                };
                card.Click += (o, e) => Choose(c.Key, c.Text);
                y += 37;
            }
            _opts.Size = new Size(_opts.Size.Width, Math.Max(1, S(y)));
            _opts.Location = new Point(_opts.Location.X, _say.Location.Y + _say.Size.Height + S(12));
            _talk.FitContent(12);
            _talk.ScrollTo(0);
        }

        private static void PaintOption(Graphics g, PaintBox b, string num, string text, bool exit, Color dot)
        {
            float k = ModernRoot.K;
            bool hot = b.Hovered;
            RectangleF r = new RectangleF(hot ? 4 * k : 0.5f, 0.5f, b.Size.Width - 1f - (hot ? 4 * k : 0), b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            if (!exit)
                using (var br = new LinearGradientBrush(new RectangleF(r.X - 1, 0, r.Width + 2, r.Height), W.Over(ModernTheme.Accent, 0.12f), W.Page, 0f))
                    g.FillPath(br, p);
            using (var pen = new Pen(hot ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.7f), 1f))
            {
                if (exit) pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, p);
            }
            float x = r.X + 10 * k;
            if (num.Length > 0)
            {
                RectangleF kb = new RectangleF(x, (b.Size.Height - 20 * k) / 2f, 20 * k, 20 * k);
                using GraphicsPath kp = ModernSkin.RoundRect(kb, 4 * k);
                ModernSkin.Stroke(g, kp, ModernTheme.Accent);
                ModernSkin.Text(g, num, ModernSkin.Font(11, true), ModernTheme.Accent, kb, StringAlignment.Center);
            }
            x += 30 * k;
            ModernSkin.Text(g, text, ModernSkin.Font(12.5f), exit ? ModernTheme.Dim : hot ? Color.White : ModernTheme.Text, new RectangleF(x, 0, b.Size.Width - x - 24 * k, b.Size.Height));
            if (!dot.IsEmpty && dot.A > 0)
                using (var db = new SolidBrush(dot))
                    g.FillEllipse(db, b.Size.Width - 20 * k, (b.Size.Height - 10 * k) / 2f, 10 * k, 10 * k);
        }

        /// <summary>Service chips: the page's own service links (each is a key the server accepts on this page).</summary>
        private void BuildChips(NpcPage page)
        {
            for (int i = _chips.Controls.Count - 1; i >= 0; i--) _chips.Controls[i].Dispose();
            var seen = new HashSet<string>();
            int x = 0, y = 0, maxW = 210;
            void Chip(string key, string text)
            {
                string k = NpcState.NormKey(key).TrimStart('@');
                if (!Services.TryGetValue(k, out var svc) || !seen.Add(k)) return;
                string label = W.T(svc.Label);
                int w = (int)Math.Ceiling(ModernSkin.Measure(label, ModernSkin.Font(10.5f)) / ModernRoot.K) + 34;
                if (x > 0 && x + w > maxW) { x = 0; y += 26; }
                var chip = new PaintBox((g, b) => PaintChip(g, b, svc.Icon, label)) { Parent = _chips, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(22)), Hint = text, Sound = SoundList.ButtonA };
                string call = key, said = text;
                chip.Click += (o, e) => Choose(call, said);
                chip.AfterPaint = b => W.DrawIcon(svc.Icon, new Rectangle(b.DisplayLocation.X + S(8), b.DisplayLocation.Y + S(5), S(12), S(12)), b.Hovered ? Color.White : ModernTheme.Accent);
                x += w + 4;
            }
            foreach (var c in _choices) Chip(c.Key, c.Text);
            foreach (List<NpcSegment> row in page.Rows)
                foreach (NpcSegment s in row)
                    if (s.Kind == NpcSegKind.Link) Chip(s.Key, s.Text);
            // centre each row of chips (flex-wrap: justify-content: center)
            var rows = new Dictionary<int, List<MirControl>>();
            foreach (MirControl c in _chips.Controls)
            {
                if (!rows.TryGetValue(c.Location.Y, out var l)) rows[c.Location.Y] = l = new List<MirControl>();
                l.Add(c);
            }
            foreach (var l in rows.Values)
            {
                int right = l[^1].Location.X + l[^1].Size.Width, shift = (_chips.Size.Width - right) / 2;
                foreach (MirControl c in l) c.Location = new Point(c.Location.X + shift, c.Location.Y);
            }
        }

        private static void PaintChip(Graphics g, PaintBox b, int icon, string label)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, 10 * k);
            using (var br = new SolidBrush(ModernSkin.Opaque(ModernTheme.PanelB))) g.FillPath(br, p);
            ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : ModernTheme.Frame);
            ModernSkin.Text(g, label, ModernSkin.Font(10.5f), b.Hovered ? Color.White : ModernTheme.Text, new RectangleF(24 * k, 0, r.Width - 28 * k, r.Height));
        }

        /// <summary>A link, option card or chip: C.CallNPC "[key]" through the session (the 5 s gate; @Exit closes).</summary>
        private void Choose(string key, string text)
        {
            FinishTyping();
            bool exit = string.Equals(key, "@Exit", StringComparison.OrdinalIgnoreCase);
            _state.Call(key, exit ? null : text);
        }

        private void SayClicked(int run)
        {
            if (_typing)
            {
                FinishTyping();
                return;
            }
            if (run < 0 || run >= _say.Runs.Count) return;
            NpcRun r = _say.Runs[run];
            switch (r.Kind)
            {
                case NpcSegKind.Link:
                    Choose(r.Key, r.Text);
                    break;
                case NpcSegKind.Url:
                    // NewButton with an address (server-sent text): an absolute http(s) address only, and the player confirms
                    // with the host shown (IdnHost: punycode, so a look-alike name is visible) before the system browser opens
                    if (Uri.TryCreate(r.Key, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                        && ModernRoot.Instance?.Popups is Popups pop)
                    {
                        string address = uri.AbsoluteUri;
                        pop.Ask(W.T(ClientTextKeys.ModernNpcOpenLinkAsk, uri.IdnHost), ok =>
                        {
                            if (!ok) return;
                            try { System.Diagnostics.Process.Start(new ProcessStartInfo { FileName = address, UseShellExecute = true }); }
                            catch (Exception ex) { ModernLog.Once("[ModernUi] npc url", ex); }
                        }, true, PopupKind.Question, W.T(ClientTextKeys.ModernNpcOpenLinkTitle));
                    }
                    break;
                case NpcSegKind.Info:
                    HideCard();
                    break;
            }
        }

        // ------------------------------------------------------------------ typewriter

        private void StartTyping()
        {
            _typing = _say.TotalChars > 0;
            _typeStart = CMain.Time;
            _nextType = 0;
            _say.SetShown(_typing ? 0 : int.MaxValue);
            _opts.Visible = !_typing;
        }

        private void FinishTyping()
        {
            if (_say == null) return;
            _typing = false;
            _say.SetShown(int.MaxValue);
            if (_opts != null) _opts.Visible = true;
        }

        // ------------------------------------------------------------------ log

        private void RenderLog()
        {
            _shownLog = _state.LogVersion;
            for (int i = _logView.Content.Controls.Count - 1; i >= 0; i--) _logView.Content.Controls[i].Dispose();
            var runs = new List<NpcRun>();
            if (_state.Log.Count == 0) runs.Add(new NpcRun { Text = W.T(ClientTextKeys.ModernNpcNothingSaid), Colour = ModernTheme.Dim });
            for (int i = 0; i < _state.Log.Count; i++)
            {
                if (i > 0) runs.Add(new NpcRun { Text = "\n" });
                runs.Add(new NpcRun { Text = _state.Log[i].Who + ": ", Colour = ModernTheme.Text, Bold = true });
                runs.Add(new NpcRun { Text = _state.Log[i].Text, Colour = ModernTheme.Dim });
            }
            int w = (int)Math.Round(_logView.InnerWidth / ModernRoot.K) - 30;
            var text = new NpcRichText(w, 11) { Parent = _logView.Content, Location = new Point(S(18), S(6)), NotControl = true };
            text.SetRuns(runs);
            _logView.FitContent(6);
            _logView.ScrollTo(_logView.MaxScroll);
        }

        // ------------------------------------------------------------------ info links (NPCDialog.ShowTooltip)

        private void SayHover(int run, Rectangle at)
        {
            if (run < 0 || run >= _say.Runs.Count || _say.Runs[run].Kind != NpcSegKind.Info)
            {
                HideCard();
                return;
            }
            NpcRun r = _say.Runs[run];
            if (r.Key == "ITEM")
            {
                ItemInfo info = FindItem(r.Index);
                if (info == null) { GameScene.RequestItemInfo(r.Index); return; }
                _linkAnchor.Location = at.Location;
                _linkAnchor.Size = new Size(Math.Max(1, at.Width), Math.Max(1, at.Height));
                ModernTooltip.Show(new UserItem(info) { CurrentDura = info.Durability, MaxDura = info.Durability, Count = 1 }, _linkAnchor);
                return;
            }
            string title, body;
            if (r.Key == "MONSTER")
            {
                ClientMonsterInfo m = FindMonster(r.Index);
                if (m == null)
                {
                    GameScene.RequestMonsterInfo(r.Index);
                    title = W.T(ClientTextKeys.MonsterIndexTitle, r.Index);
                    body = W.T(ClientTextKeys.MonsterInfoLoading);
                }
                else
                {
                    Stats st = m.Stats ?? new Stats();
                    title = string.IsNullOrEmpty(m.GameName) ? m.Name : m.GameName;
                    body = W.T(ClientTextKeys.ModernNpcMonsterCard, m.Level, W.N(st[Stat.HP]), W.N(m.Experience), st[Stat.MinDC] + "~" + st[Stat.MaxDC],
                        st[Stat.MinMC] + "~" + st[Stat.MaxMC], st[Stat.MinAC] + "~" + st[Stat.MaxAC], st[Stat.MinMAC] + "~" + st[Stat.MaxMAC]);
                }
            }
            else
            {
                ClientNPCInfo n = FindNpc(r.Index);
                if (n == null)
                {
                    GameScene.RequestNPCInfo(r.Index);
                    title = W.T(ClientTextKeys.NPCIndexTitle, r.Index);
                    body = W.T(ClientTextKeys.NPCInfoLoading);
                }
                else
                {
                    title = n.Name;
                    string map = n.MapIndex == GameScene.Scene?.MapControl?.Index ? GameScene.Scene.MapControl.Title : W.T(ClientTextKeys.ModernNpcUnknownMap);
                    body = W.T(ClientTextKeys.ModernNpcNpcCard, n.Name, map, n.Location.X, n.Location.Y);
                }
            }
            _cardRun = run;
            _cardTitle = title;
            _cardBody = body;
            SizeF sz = ModernSkin.MeasureWrapped(body, ModernSkin.Font(11), S(240));
            _card.Size = new Size(S(260), (int)sz.Height + S(36));
            Point local = new Point(_say.DisplayLocation.X - DisplayLocation.X + at.X, _say.DisplayLocation.Y - DisplayLocation.Y + at.Bottom + S(6));
            if (local.Y + _card.Size.Height > Size.Height - S(4)) local.Y = Math.Max(S(4), local.Y - at.Height - _card.Size.Height - S(12));
            local.X = Math.Max(S(4), Math.Min(local.X, Size.Width - _card.Size.Width - S(6)));
            _card.Location = local;
            _card.Visible = true;
            _card.BringToFront();
            _card.Invalidate();
        }

        private void PaintCard(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.96f))) g.FillPath(br, p);
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
            ModernSkin.Text(g, _cardTitle, ModernSkin.Font(13, true), Color.White, new RectangleF(10 * k, 6 * k, r.Width - 20 * k, 18 * k));
            ModernSkin.Text(g, _cardBody, ModernSkin.Font(11), ModernTheme.Text, new RectangleF(10 * k, 28 * k, r.Width - 20 * k, r.Height - 32 * k), StringAlignment.Near, StringAlignment.Near, false, true);
        }

        private void HideCard()
        {
            _cardRun = -1;
            if (_card != null && _card.Visible) _card.Visible = false;
            if (_linkAnchor != null) ModernTooltip.HideFor(_linkAnchor);
        }

        // ------------------------------------------------------------------ quests (QuestListDialog for this NPC; quest V2)

        /// <summary>
        /// What this NPC finishes (the player's runs), takes a branch choice of (Choosing: runs whose step has branches, also at a
        /// start-only NPC) and offers for the player, as S.QuestAvailability said; definitions known.
        /// </summary>
        private static void NpcQuests(uint npc, List<(int Quest, bool Offer)> into)
        {
            into.Clear();
            QuestBook book = GameScene.Models?.Quests;
            if (book == null || !book.Npcs.TryGetValue(npc, out S.QuestAvailability.Entry e)) return;
            for (int i = 0; i < e.Finishing.Count; i++)
                if (book.Def(e.Finishing[i]) != null && book.Run(e.Finishing[i]) != null) into.Add((e.Finishing[i], false));
            for (int i = 0; i < e.Choosing.Count; i++)
                if (book.Def(e.Choosing[i]) != null && book.Run(e.Choosing[i]) != null && !e.Finishing.Contains(e.Choosing[i])) into.Add((e.Choosing[i], false));
            for (int i = 0; i < e.Offered.Count; i++)
                if (book.Def(e.Offered[i]) != null && book.Run(e.Offered[i]) == null) into.Add((e.Offered[i], true));
        }

        private void RenderQuests(List<(int Quest, bool Offer)> list)
        {
            _questsDirty = false;
            for (int i = _quests.Content.Controls.Count - 1; i >= 0; i--) _quests.Content.Controls[i].Dispose();
            int w = (int)Math.Round(_quests.InnerWidth / ModernRoot.K) - 24, y = 10;
            _shownQuests.Clear();
            if (list.Count == 0)
            {
                W.Label(_quests.Content, W.T(ClientTextKeys.ModernNpcNoQuests), 0, 30, w + 24, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
                _quests.FitContent(10);
                return;
            }
            foreach (var q in list)
            {
                _shownQuests.Add((q.Quest, QuestActions.IsPending(q.Quest)));
                y += QuestCard(q.Quest, q.Offer, 12, y, w) + 8;
            }
            _quests.ContentHeight = S(y + 4);
        }

        private int QuestCard(int quest, bool offer, int x, int y, int w)
        {
            QuestBook book = GameScene.Models.Quests;
            S.QuestDefinition def = book.Def(quest);
            if (def == null) return 0;
            QuestRun run = book.Run(quest);
            uint npcId = GameScene.QuestNPCID;
            bool ready = run != null && run.Status == QuestRunStatus.Ready, failed = run != null && run.Status == QuestRunStatus.Failed;
            bool completedBefore = book.Done(quest) != null;
            var lines = new List<(string Text, bool Section)>();
            void Add(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                foreach (string ln in text.Replace("\r\n", "\n").Split('\n')) lines.Add((NpcPage.RowText(NpcPage.ParseLine(ln)), false));
            }
            if (offer)
            {
                Add(def.Summary);
                var objectives = new List<S.QuestDefinition.Objective>();
                S.QuestDefinition.Step first = null;
                foreach (S.QuestDefinition.Step s in def.Steps) if (first == null || s.Order < first.Order) first = s;
                if (first != null) QuestBook.ObjectivesOf(def, first.Index, objectives);
                bool head = false;
                foreach (S.QuestDefinition.Objective o in objectives)
                {
                    if (o.Hidden) continue;
                    if (!head) { lines.Add((W.T(ClientTextKeys.ModernNpcTasks), true)); head = true; }
                    lines.Add((QuestText.Label(o), false));
                }
                if (def.TimeLimitSeconds > 0) { lines.Add((W.T(ClientTextKeys.ModernNpcTimeLimit), true)); lines.Add((W.Duration(def.TimeLimitSeconds), false)); }
            }
            else if (ready)
            {
                string text = string.Empty;
                foreach (S.QuestDefinition.Step s in def.Steps) if (!string.IsNullOrEmpty(s.ReadyText)) text = s.ReadyText;
                Add(text.Length > 0 ? text : def.CompleteText.Length > 0 ? def.CompleteText : W.T(ClientTextKeys.ModernSocialQReadyTurnIn));
            }
            else if (failed) lines.Add((W.T(ClientTextKeys.ModernQuestFailedHint), false));
            else if (run.Step >= 0)
            {
                var objectives = new List<S.QuestDefinition.Objective>();
                QuestBook.ObjectivesOf(def, run.Step, objectives);
                lines.Add((W.T(ClientTextKeys.ModernNpcProgress), true));
                foreach (S.QuestDefinition.Objective o in objectives)
                    if (book.Shown(quest, o)) lines.Add((QuestText.Line(o, book.Value(quest, o.Index)), false));
            }

            // measure the text
            int textW = w - 54 - 110;
            Font tf = ModernSkin.Font(11.5f);
            float ty = 26;
            var placed = new List<(string Text, bool Section, float Y, float H)>();
            foreach (var ln in lines)
            {
                float h = ln.Section ? 16 : Math.Max(15, ModernSkin.MeasureWrapped(ln.Text, tf, S(textW)).Height / ModernRoot.K + 2);
                placed.Add((ln.Text, ln.Section, ty + (ln.Section ? 4 : 0), h));
                ty += h + (ln.Section ? 4 : 0);
            }
            float rewardY = ty + 6;

            string meta = (def.LevelHint > 0 ? W.T(ClientTextKeys.ModernNpcLv, def.LevelHint) + " · " : string.Empty) + QuestGfx.CategoryName(def.Category)
                + (string.IsNullOrEmpty(def.GroupName) ? string.Empty : " · " + def.GroupName);
            bool story = def.Category == QuestCategory.Story, daily = def.Category == QuestCategory.Daily || def.Category == QuestCategory.Weekly;
            QuestIcon icon = offer ? (story ? QuestIcon.ExclamationGreen : daily ? QuestIcon.ExclamationBlue : QuestIcon.ExclamationYellow)
                : ready ? (story ? QuestIcon.QuestionGreen : daily ? QuestIcon.QuestionBlue : QuestIcon.QuestionYellow)
                : QuestIcon.QuestionWhite;
            (string mark, Color mc) = failed ? ("✗", ModernTheme.Hex("#ff6b5a")) : Marker(icon);
            string name = def.Name;
            var card = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                W.Card(g, b.Size);
                RectangleF qm = new RectangleF(10 * k, 10 * k, 30 * k, 30 * k);
                using (var pen = new Pen(mark.Length > 0 ? mc : ModernTheme.Dim, 1f)) g.DrawEllipse(pen, qm);
                ModernSkin.Text(g, mark.Length > 0 ? mark : "…", ModernSkin.Font(17, true), mark.Length > 0 ? mc : ModernTheme.Dim, qm, StringAlignment.Center);
                float tx = 50 * k;
                Font nf = ModernSkin.Font(13, true);
                float nw = Math.Min(ModernSkin.Measure(name, nf) + 2, textW * k * 0.7f);
                ModernSkin.Text(g, name, nf, Color.White, new RectangleF(tx, 8 * k, nw, 18 * k));
                ModernSkin.Text(g, meta, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(tx + nw + 6 * k, 9 * k, textW * k - nw, 16 * k));
                foreach (var pl in placed)
                {
                    if (pl.Section) ModernSkin.Text(g, pl.Text.ToUpperInvariant(), ModernSkin.Font(9.5f, true), ModernTheme.AccentDim, new RectangleF(tx, pl.Y * k, textW * k, 14 * k));
                    else ModernSkin.Text(g, pl.Text, tf, ModernTheme.Text, new RectangleF(tx, pl.Y * k, textW * k, pl.H * k), StringAlignment.Near, StringAlignment.Near, false, true);
                }
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernNpcReward).ToUpperInvariant(), ModernSkin.Font(9.5f, true), ModernTheme.AccentDim, new RectangleF(tx, rewardY * k, textW * k, 14 * k));
            })
            // not NotControl: a NotControl parent hides its children from the mouse (the card holds the buttons and reward cells)
            { Parent = _quests.Content, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(96)) };

            int bottom = QuestRewardsView.Build(card, def, completedBefore, ready, 50, (int)rewardY + 14, textW, () => _questsDirty = _dirty = true);

            // a met step with branches: the choice buttons (C.QuestChoose here: this NPC is an endpoint of the quest)
            bool pending = QuestActions.IsPending(quest);
            if (!offer && book.ChoiceDue(quest))
            {
                var branches = new List<S.QuestDefinition.Branch>();
                QuestBook.BranchesOf(def, run.Step, branches);
                W.Label(card, W.T(ClientTextKeys.ModernQuestChoose).ToUpperInvariant(), 50, bottom + 2, textW, 14, 9.5f, true, ModernTheme.AccentDim);
                bottom += 18;
                foreach (S.QuestDefinition.Branch br in branches)
                {
                    var b = new ModernButton(br.Text.Length > 0 ? br.Text : br.ChoiceKey, ButtonKind.Main) { Parent = card, FontSize = 11 };
                    b.AutoWidth(120);
                    b.Location = new Point(S(50), S(bottom));
                    string key = br.ChoiceKey;
                    b.SetDisabled(pending, W.T(ClientTextKeys.ModernWinWaitingServer));
                    b.Click += (o, e) => { QuestActions.Choose(quest, npcId, key); _questsDirty = _dirty = true; };
                    bottom += 32;
                }
            }
            int height = Math.Max(96, bottom + 8);
            card.Size = new Size(card.Size.Width, S(height));

            // buttons
            int bx = w - 104, by = Math.Min(height / 2, 70) - 24;
            if (offer)
            {
                bool full = book.ActiveCount >= Globals.MaxConcurrentQuests;
                var b = new ModernButton(W.T(pending ? ClientTextKeys.ModernNpcAccepting : ClientTextKeys.ModernNpcAccept), ButtonKind.Main) { Parent = card, Location = new Point(S(bx), S(by + 12)) };
                b.SetDisabled(pending || full, full ? W.T(ClientTextKeys.ModernNpcQuestFull, Globals.MaxConcurrentQuests) : W.T(ClientTextKeys.ModernWinWaitingServer));
                b.Click += (o, e) => { QuestActions.Accept(quest, npcId); _questsDirty = _dirty = true; };
            }
            else if (ready)
            {
                bool picks = QuestActions.PicksComplete(def, completedBefore);
                var b = new ModernButton(W.T(pending ? ClientTextKeys.ModernNpcHandingIn : ClientTextKeys.ModernNpcHandIn), ButtonKind.Main) { Parent = card, Location = new Point(S(bx), S(by + 12)) };
                b.SetDisabled(pending || !picks, pending ? W.T(ClientTextKeys.ModernWinWaitingServer) : W.T(ClientTextKeys.YouMustSelectRewardItem));
                b.Click += (o, e) =>
                {
                    QuestActions.TurnIn(def, npcId, completedBefore);
                    _questsDirty = _dirty = true;
                };
            }
            else
            {
                W.Label(card, W.T(failed ? ClientTextKeys.ModernQuestFailedTag : ClientTextKeys.ModernNpcInProgress), bx, by, 84, 16, 10, false, failed ? ModernTheme.Hex("#ff6b5a") : ModernTheme.Dim, StringAlignment.Center);
                var b = new ModernButton(W.T(failed ? ClientTextKeys.ModernQuestClear : ClientTextKeys.ModernNpcAbandon)) { Parent = card, Location = new Point(S(bx), S(by + 20)) };
                bool canAbandon = def.AbandonAllowed || failed;
                b.SetDisabled(pending || !canAbandon, !canAbandon ? W.T(ClientTextKeys.ModernQuestNoAbandon) : W.T(ClientTextKeys.ModernWinWaitingServer));
                b.Click += (o, e) => Abandon(quest, name);
            }
            return height;
        }

        private void Abandon(int quest, string name)
        {
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.AskCancelQuest, name), ok =>
            {
                if (!ok || QuestActions.IsPending(quest) || GameScene.Models?.Quests.Run(quest) == null) return;
                QuestActions.Abandon(quest);
                _questsDirty = _dirty = true;
            }, true, PopupKind.Warning, W.T(ClientTextKeys.ModernNpcAbandonTitle));
        }
    }

    /// <summary>The NPC's portrait: its own library frame as it animates in the world, scaled into the stage (bottom-centred).</summary>
    internal sealed class NpcPortrait : ModernControl
    {
        private NPCObject _npc;
        private int _frame = -1;
        private bool _drawn;
        private long _retryAt;
        private int _tries, _lastGood = -1;
        private Size _boxSize;
        private Point _boxOffset;
        // frames with the dithered shadow flattened (FlatShadow), per frame of the current NPC's library
        private readonly Dictionary<int, RenderTexture> _flat = new Dictionary<int, RenderTexture>();
        private MLibrary _flatLib;

        public NpcPortrait()
        {
            NotControl = true;
        }

        public NPCObject Npc
        {
            get => _npc;
            set
            {
                if (_npc == value) return;
                _npc = value;
                _frame = -1;
                _tries = 0;
                _lastGood = -1;
                _boxSize = Size.Empty;
                ClearFlat();
                Redraw();
            }
        }

        private void ClearFlat()
        {
            foreach (RenderTexture t in _flat.Values) t?.Dispose();
            _flat.Clear();
            _flatLib = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearFlat();
            base.Dispose(disposing);
        }

        private RenderTexture Flat(MLibrary lib, int frame)
        {
            if (lib != _flatLib) { ClearFlat(); _flatLib = lib; }
            if (_flat.TryGetValue(frame, out RenderTexture t) && (t == null || !t.Disposed)) return t;
            t = FlatShadow.Make(lib, frame);
            _flat[frame] = t;
            return t;
        }

        /// <summary>Per frame: redraw only when the NPC's frame moved.</summary>
        public void Tick()
        {
            NPCObject n = _npc;
            if (n == null) return;
            if (!MapControl.Objects.TryGetValue(n.ObjectID, out MapObject live) || live != n) { _npc = null; Redraw(); return; }
            if (n.DrawFrame == _frame)
            {
                // the library may not have its pictures yet (loaded on first use): try again a few times a second
                if (_drawn || CMain.Time < _retryAt || _tries >= 40) return;
                _retryAt = CMain.Time + 250;
                _tries++;
            }
            _frame = n.DrawFrame;
            Redraw();
        }

        protected override void Paint(Graphics g) { }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            _drawn = false;
            NPCObject n = _npc;
            MLibrary lib = n?.BodyLibrary;
            if (lib == null || n.Frame == null) return;
            // the box the standing picture spans, kept for the NPC (gesture frames may have another base or no picture)
            int baseFrame = n.Frame.Start + n.Frame.OffSet * (byte)n.Direction;
            Size bs = lib.GetTrueSize(baseFrame);
            if (bs.Width > 0 && bs.Height > 0 && (_boxSize.IsEmpty || n.CurrentAction == MirAction.Standing))
            {
                _boxSize = bs;
                _boxOffset = lib.GetOffSet(baseFrame);
            }
            if (_boxSize.IsEmpty) return;
            Size s0 = _boxSize;
            Point o0 = _boxOffset;
            float k = Math.Min(3f, Math.Min(Math.Max(1, Size.Height - S(40)) / (float)s0.Height, Math.Max(1, Size.Width - S(20)) / (float)s0.Width));
            k = Math.Max(1f, k);
            float ox = DisplayLocation.X + Size.Width / 2f - s0.Width * k / 2f - o0.X * k;
            float oy = DisplayLocation.Y + Size.Height - S(26) - s0.Height * k - o0.Y * k;
            DXManager.SetSpriteTransform(DxMatrix.Scaling(k, k, 0));
            try
            {
                int frame = n.DrawFrame;
                if (lib.GetTrueSize(frame).IsEmpty) frame = _lastGood; // a blank frame: keep the last picture
                if (frame < 0) return;
                _lastGood = frame;
                Point at = new Point((int)Math.Round(ox / k), (int)Math.Round(oy / k));
                RenderTexture flat = Flat(lib, frame);
                if (flat != null)
                {
                    Point o = lib.GetOffSet(frame);
                    DXManager.Draw(flat, new Rectangle(0, 0, flat.Width, flat.Height), new SlimDX.Vector3(at.X + o.X, at.Y + o.Y, 0), Color.White);
                }
                else lib.Draw(frame, at, Color.White, true);
                _drawn = true;
                _tries = 0;
            }
            finally
            {
                DXManager.SetSpriteTransform(DxMatrix.Identity);
            }
        }
    }
}
