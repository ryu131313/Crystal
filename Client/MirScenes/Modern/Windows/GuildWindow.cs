// Ported from WebClient/src/ui/modern/windows/guild.ts (GuildWindow): the new UI's guild hub over the desktop's guild model
// (GameScene.Models.Guild: S.GuildStatus values, the rank / member list of S.GuildMemberChange, the notice, buffs, and the
// phase 2.5 S.GuildMemberDetails / S.GuildWarList / S.GuildLog) and GameScene.GuildStorage. Classic reference: GuildDialog
// (Client/MirScenes/Dialogs/GuildDialog.cs); its rules are kept: who may edit what, the throttles, the texts.
//  - banner: crest (the guild's initials), name, level, EXP / MaxExperience, members / MaxMembers, online, fund, buff points;
//  - Overview: KPIs, the notice board (C.RequestGuildInfo 0 while NoticeStale, 5 s apart; Edit only with CanChangeNotice, Save
//    sends C.EditGuildNotice and stays read-only until S.GuildNoticeChange or 5 s), who is online, recent log lines;
//  - Members: Models.Guild.Ranks (C.RequestGuildInfo 1 while MembersStale, 5 s apart) with S.GuildMemberDetails merged in by
//    name (class, level; C.RequestGuildMemberDetails on open / Refresh, 5 s throttle in the model); search, online filter, sort;
//    Promote / Demote / Set rank (C.EditGuildMember 2, ranks at or below your own, CanChangeRank), Kick (1, CanKick), Invite
//    (0, CanRecruit), each after a popup and with GuildDialog's 5 s LastGuildMsg; the row waits for the server's answer;
//    whisper (the chat input) and group invite (C.AddMember); Leave = "@LEAVEGUILD" after a hold-to-confirm;
//  - Ranks (CanChangeRank): every rank's option switches (C.EditGuildMember 5, 300 ms apart; the switch shows the model's value
//    and waits for S.GuildMemberChange 7), Rename (3: 3-20 characters, no backslash, 5 s apart), New rank (4, up to 255);
//  - Buffs (when the server offers any): GuildSkill icons (Icon, +1 active, +2 not obtained), stats, requirements, Obtain /
//    Activate (C.GuildBuffUpdate 1 / 2) with GuildDialog.RequestBuff's checks as disabled reasons; countdowns from GuildBuffClock;
//  - Storage (CanStoreItem or CanRetrieveItem): the 112 MirGridType.GuildStorage cells (ItemMoves checks the rights), the list
//    asked with C.GuildStorageItemChange 3 when the page opens; gold deposit / leader-only withdraw (C.GuildStorageGoldChange
//    0 / 1, GuildDialog.StorageAddGold / StorageRemoveGold);
//  - Wars: S.GuildWarList with countdowns (declared at an NPC: no button); Log: S.GuildLog with kind filters (5 s throttle).
// HIDDEN (no system in Crystal): alliances, member location / contribution, emblems, the five fixed ranks.
// Desktop: below a 1040 px wide screen the web's narrow layer (icon-only side menu, the banner stats on a second row).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class GuildWindow : ModernWindow
    {
        public const string WindowKey = "guild";
        private const int H = 530, MemberGapMs = 5000, OptionGapMs = 300, RankNameGapMs = 5000, GoldGapMs = 100, BuffGapMs = 100,
            PendingMs = 5000, InfoGapMs = 5000, MaxRanks = 255, MaxNoticeLines = 200, StorageSlots = 112;

        internal enum Page { Overview, Members, Ranks, Buffs, Storage, Wars, Log }
        private enum LogFilter { All, Members, Notice, Buffs, Wars, Gold }
        private enum SortKey { Name, Class, Level, Rank, Seen }

        private static readonly (Page P, ClientTextKeys Name, int Icon)[] Pages =
        {
            (Page.Overview, ClientTextKeys.ModernGuildOverview, ModernIcons.Home), (Page.Members, ClientTextKeys.ModernGuildMembers, ModernIcons.Friends),
            (Page.Ranks, ClientTextKeys.ModernGuildRanks, ModernIcons.Star), (Page.Buffs, ClientTextKeys.ModernGuildBuffs, ModernIcons.Spark),
            (Page.Storage, ClientTextKeys.ModernGuildStorage, ModernIcons.Bank), (Page.Wars, ClientTextKeys.ModernGuildWars, ModernIcons.Kill),
            (Page.Log, ClientTextKeys.ModernGuildLog, ModernIcons.Scroll),
        };

        /// <summary>GuildDialog's option switches by bit (CanAlterAlliance is unused by Crystal and hidden).</summary>
        private static readonly (GuildRankOptions Bit, int BitIndex, ClientTextKeys Label, ClientTextKeys Hint)[] Options =
        {
            (GuildRankOptions.CanChangeRank, 0, ClientTextKeys.ModernGuildOptRanks, ClientTextKeys.ModernGuildOptRanksHint),
            (GuildRankOptions.CanRecruit, 1, ClientTextKeys.ModernGuildOptRecruit, ClientTextKeys.ModernGuildOptRecruitHint),
            (GuildRankOptions.CanKick, 2, ClientTextKeys.ModernGuildOptKick, ClientTextKeys.ModernGuildOptKickHint),
            (GuildRankOptions.CanStoreItem, 3, ClientTextKeys.ModernGuildOptStore, ClientTextKeys.ModernGuildOptStoreHint),
            (GuildRankOptions.CanRetrieveItem, 4, ClientTextKeys.ModernGuildOptRetrieve, ClientTextKeys.ModernGuildOptRetrieveHint),
            (GuildRankOptions.CanChangeNotice, 6, ClientTextKeys.ModernGuildOptNotice, ClientTextKeys.ModernGuildOptNoticeHint),
            (GuildRankOptions.CanActivateBuff, 7, ClientTextKeys.ModernGuildOptBuffs, ClientTextKeys.ModernGuildOptBuffsHint),
        };

        private static readonly (LogFilter F, ClientTextKeys Name, int Icon)[] LogFilters =
        {
            (LogFilter.All, ClientTextKeys.ModernGuildLogAll, ModernIcons.Scroll), (LogFilter.Members, ClientTextKeys.ModernGuildMembers, ModernIcons.Friends),
            (LogFilter.Notice, ClientTextKeys.ModernGuildLogNotice, ModernIcons.Memo), (LogFilter.Buffs, ClientTextKeys.ModernGuildBuffs, ModernIcons.Spark),
            (LogFilter.Wars, ClientTextKeys.ModernGuildWars, ModernIcons.Kill), (LogFilter.Gold, ClientTextKeys.ModernGuildLogGold, ModernIcons.Coin),
        };

        /// <summary>The server's GuildLogKind (Join 0, Leave 1, Kick 2, RankChange 3, Notice 4, Buff 5, War 6, Territory 7, Gold 8).</summary>
        private static LogFilter FilterOf(byte kind) => kind switch
        {
            0 or 1 or 2 or 3 => LogFilter.Members,
            4 => LogFilter.Notice,
            5 => LogFilter.Buffs,
            6 or 7 => LogFilter.Wars,
            8 => LogFilter.Gold,
            _ => LogFilter.All,
        };

        private static int LogIcon(byte kind)
        {
            LogFilter f = FilterOf(kind);
            for (int i = 0; i < LogFilters.Length; i++) if (LogFilters[i].F == f) return LogFilters[i].Icon;
            return ModernIcons.Scroll;
        }

        /// <summary>One roster row: a GuildRank member, enriched by S.GuildMemberDetails (HasDetail false until they arrive).</summary>
        private struct Row
        {
            public string Name, RankName;
            public int Rank;
            public bool Online, HasDetail;
            public DateTime Last;
            public MirClass Cls;
            public ushort Level;
        }

        private readonly GuildBuffClock _clock;
        private Page _page = Page.Overview;
        private bool _dirty = true, _rosterDirty = true, _rowsDirty = true, _ranksDirty = true, _buffsDirty = true, _warsDirty = true, _logDirty = true, _feedDirty = true, _noticeDirty = true;
        private readonly List<Row> _roster = new List<Row>();

        // throttles / pending requests (server authoritative: controls wait for the answer)
        private long _lastMemberMsg, _lastOption, _lastRankName, _lastGold, _lastBuff, _noticeReqAt, _membersReqAt, _wakeAt, _nextTick;
        private string _pendingMember = string.Empty;
        private int _pendingRank = -1, _pendingBuff = -1;
        private long _pendingUntil, _noticeSaveUntil;
        private bool _noticeSaving, _editing;

        // members page
        private string _query = string.Empty, _selected = string.Empty, _memberNote = string.Empty, _rankNote = string.Empty, _buffNote = string.Empty, _storeNote = string.Empty, _logNote = string.Empty;
        private bool _onlineOnly, _rankPick;
        private SortKey _sort = SortKey.Rank;
        private int _sortDir = 1;
        private LogFilter _logFilter = LogFilter.All;
        private uint _goldSeen = uint.MaxValue;
        private readonly GridWatch _storeWatch = new GridWatch();

        // layout
        private bool _narrow;
        private int _w, _navW, _bannerH, _pw, _ph;

        // controls
        private ModernPanel _m, _main, _host;
        private PaintBox _banner, _noGuild;
        private readonly PaintBox[] _nav = new PaintBox[Pages.Length];
        private readonly int[] _navCount = new int[Pages.Length];
        private ModernLabel _youName, _youRights;
        private ModernButton _leaveBtn;
        private readonly ModernPanel[] _pages = new ModernPanel[Pages.Length];
        private Page? _pageShown;

        // overview
        private readonly PaintBox[] _kpi = new PaintBox[4];
        private PaintBox _noticeCard, _onlineCard, _feedCard;
        private ModernButton _noticeBtn, _noticeCancel;
        private ScrollView _noticeView;
        private ModernLabel _noticeText;
        private ModernTextField _noticeField;

        // members
        private SearchBox _search;
        private Chips<string> _filterChips;
        private ModernButton _inviteBtn, _detailsBtn;
        private PaintBox _memberHead, _memberCard;
        private ScrollView _memberRows;
        private ModernLabel _memberNoteLbl;
        private readonly List<MirControl> _cardActs = new List<MirControl>();
        private int _cardW, _tableW;

        // ranks
        private ModernButton _newRankBtn;
        private ScrollView _rankRows;
        private ModernLabel _rankNoteLbl;
        private int _rankNameW, _rankColW;

        // buffs
        private PaintBox _buffHead;
        private ScrollView _buffGrid;
        private ModernLabel _buffNoteLbl;
        private readonly List<(PaintBox Box, int Id)> _buffTimers = new List<(PaintBox, int)>();

        // storage
        private PaintBox _storeCard, _fundBox;
        private ModernButton _depositBtn, _withdrawBtn;
        private ModernLabel _storeNoteLbl;
        private readonly ModernItemCell[] _cells = new ModernItemCell[StorageSlots];

        // wars / log
        private PaintBox _warCard;
        private ScrollView _warRows;
        private readonly List<(PaintBox Box, int Index)> _warTimers = new List<(PaintBox, int)>();
        private ScrollView _logRows;
        private ModernLabel _logNoteLbl;

        private readonly Action _onChanged, _onBuffs, _onNotice, _onMembers, _onWars, _onLog;
        private readonly Action<bool> _onDetails;
        private readonly Action<ItemSlotKey> _onSlot;

        internal GuildWindow(GuildBuffClock clock) : base(WindowKey, W.T(ClientTextKeys.ModernGuildTitle), 52, 40)
        {
            _clock = clock;
            _onChanged = () => { _rosterDirty = _ranksDirty = _buffsDirty = true; _dirty = true; };
            _onBuffs = () => { _buffsDirty = true; _pendingBuff = -1; _dirty = true; };
            _onNotice = () => { _noticeDirty = true; _editing = _noticeSaving = false; _dirty = true; };
            _onMembers = () => { _rosterDirty = _ranksDirty = true; _pendingMember = string.Empty; _pendingRank = -1; _dirty = true; };
            _onDetails = _ => { _rosterDirty = true; _dirty = true; };
            _onWars = () => { _warsDirty = true; _dirty = true; };
            _onLog = () => { _logDirty = _feedDirty = true; _dirty = true; };
            _onSlot = k => { if (k.Grid == MirGridType.GuildStorage && _page == Page.Storage) _dirty = true; };
        }

        private static GuildState G => GameScene.Models?.Guild;
        private static bool InGuild => !string.IsNullOrEmpty(GameScene.User?.GuildName);
        private static bool Can(GuildRankOptions bit) => G != null && (G.MyOptions & bit) != 0;
        private static string Me => GameScene.User?.Name ?? string.Empty;

        // test hooks
        internal Page PageShown => _page;
        internal PaintBox NavRow(Page p) => _nav[(int)p];
        internal ModernButton NoticeButton => _noticeBtn;
        internal ModernTextField NoticeField => _noticeField;
        internal ScrollView MemberRows => _memberRows;
        internal IReadOnlyList<MirControl> CardActions => _cardActs;
        internal ScrollView RankRows => _rankRows;
        internal ScrollView BuffGrid => _buffGrid;
        internal ModernButton DepositButton => _depositBtn;
        internal ModernButton WithdrawButton => _withdrawBtn;
        internal ModernItemCell Cell(int i) => i >= 0 && i < StorageSlots ? _cells[i] : null;
        internal ScrollView LogRows => _logRows;
        internal ScrollView WarRows => _warRows;
        internal string Selected => _selected;
        internal bool Editing => _editing;
        internal bool NoticeSaving => _noticeSaving;
        internal string Notes => _memberNote + "|" + _rankNote + "|" + _buffNote + "|" + _storeNote + "|" + _logNote;

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            _w = _narrow ? 700 : 880;
            _navW = _narrow ? 46 : 140;
            _bannerH = _narrow ? 122 : 88;
            _pw = _w - _navW - 1;
            _ph = H - _bannerH;
            ContentWidth = _w;
            ContentHeight = H;
            for (int i = 0; i < _pages.Length; i++) _pages[i] = null;
            _pageShown = null;
            _cardActs.Clear();
            _buffTimers.Clear();
            _warTimers.Clear();

            _m = new ModernPanel { Parent = this, Location = new Point(0, S(TitleHeight)), Size = new Size(S(_w), S(H)) };
            _noGuild = new PaintBox(PaintNoGuild) { Parent = _m, Location = Point.Empty, Size = _m.Size, NotControl = true, Visible = false };
            _noGuild.AfterPaint = b =>
            {
                int s = S(40);
                W.DrawIcon(ModernIcons.Guild, new Rectangle(b.DisplayLocation.X + (b.Size.Width - s) / 2, b.DisplayLocation.Y + b.Size.Height / 2 - S(70), s, s), ModernTheme.Accent);
            };
            _main = new ModernPanel { Parent = _m, Location = Point.Empty, Size = _m.Size };
            _banner = new PaintBox(PaintBanner) { Parent = _main, Location = Point.Empty, Size = new Size(S(_w), S(_bannerH)), NotControl = true };

            // side menu
            for (int i = 0; i < Pages.Length; i++)
            {
                int idx = i;
                _nav[i] = new PaintBox((g, b) => PaintNav(g, b, idx))
                {
                    Parent = _main,
                    Location = new Point(S(_narrow ? 6 : 8), S(_bannerH + 10 + i * 33)),
                    Size = new Size(S(_navW - (_narrow ? 12 : 16)), S(30)),
                    Hint = W.T(Pages[i].Name),
                };
                _nav[i].Click += (o, e) => Go(Pages[idx].P);
                _nav[i].AfterPaint = b =>
                {
                    bool on = _page == Pages[idx].P;
                    int s = S(14), x = _narrow ? (b.Size.Width - s) / 2 : S(10);
                    W.DrawIcon(Pages[idx].Icon, new Rectangle(b.DisplayLocation.X + x, b.DisplayLocation.Y + S(8), s, s), on ? ModernTheme.Accent : b.Hovered ? ModernTheme.Text : ModernTheme.Dim);
                };
            }
            if (!_narrow)
            {
                int y = H - 92;
                W.Divider(_main, 8, y, _navW - 16);
                W.Label(_main, W.T(ClientTextKeys.ModernGuildYouAre), 14, y + 6, _navW - 28, 14, 10, false, ModernTheme.Dim);
                _youName = W.Label(_main, string.Empty, 14, y + 20, _navW - 28, 16, 12, true, ModernTheme.Accent);
                _youRights = W.Label(_main, string.Empty, 14, y + 36, _navW - 28, 14, 10, false, ModernTheme.Dim);
                _leaveBtn = new ModernButton(W.T(ClientTextKeys.ModernGuildLeave)) { Parent = _main, Location = new Point(S(14), S(y + 56)), Size = new Size(S(_navW - 28), ModernButton.BarHeight(false)), Tooltip = W.T(ClientTextKeys.ModernGuildLeaveHint), FontSize = 11 };
                _leaveBtn.Click += (o, e) => Leave();
            }
            W.VDivider(_main, _navW, _bannerH, _ph);
            _host = new ModernPanel { Parent = _main, Location = new Point(S(_navW + 1), S(_bannerH)), Size = new Size(S(_pw), S(_ph)) };

            _rowsDirty = _ranksDirty = _buffsDirty = _warsDirty = _logDirty = _feedDirty = _noticeDirty = _rosterDirty = true;
            _dirty = true;
            Render();
        }

        private ModernPanel PagePanel(Page p)
        {
            int i = (int)p;
            if (_pages[i] != null && !_pages[i].IsDisposed) return _pages[i];
            var panel = new ModernPanel { Parent = _host, Location = Point.Empty, Size = _host.Size, Visible = false };
            _pages[i] = panel;
            switch (p)
            {
                case Page.Overview: BuildOverview(panel); break;
                case Page.Members: BuildMembers(panel); break;
                case Page.Ranks: BuildRanks(panel); break;
                case Page.Buffs: BuildBuffs(panel); break;
                case Page.Storage: BuildStorage(panel); break;
                case Page.Wars: BuildWars(panel); break;
                case Page.Log: BuildLog(panel); break;
            }
            return panel;
        }

        private static ModernButton SmallButton(MirControl parent, string text, int x, int y, Action run, string hint = null, ButtonKind kind = ButtonKind.Normal, int minW = 50)
        {
            var b = new ModernButton(text, kind) { Parent = parent, Location = new Point(S(x), S(y)), FontSize = 11 };
            if (hint != null) b.Tooltip = hint;
            b.AutoWidth(minW);
            b.Click += (o, e) => run();
            return b;
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            GuildState g = G;
            if (g != null)
            {
                g.Changed.On(_onChanged);
                g.BuffsChanged.On(_onBuffs);
                g.NoticeChanged.On(_onNotice);
                g.MembersChanged.On(_onMembers);
                g.MemberDetailsChanged.On(_onDetails);
                g.WarsChanged.On(_onWars);
                g.LogChanged.On(_onLog);
            }
            ItemMoves.SlotChanged.On(_onSlot);
            _rosterDirty = _rowsDirty = _ranksDirty = _buffsDirty = _warsDirty = _logDirty = _feedDirty = _noticeDirty = true;
            _storeWatch.Reset();
            if (InGuild && g != null)
            {
                g.RequestBuffList();
                if (!PageVisible(_page)) _page = Page.Overview;
                Enter(_page);
                g.RequestMemberDetails();
            }
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            GuildState g = G;
            if (g != null)
            {
                g.Changed.Off(_onChanged);
                g.BuffsChanged.Off(_onBuffs);
                g.NoticeChanged.Off(_onNotice);
                g.MembersChanged.Off(_onMembers);
                g.MemberDetailsChanged.Off(_onDetails);
                g.WarsChanged.Off(_onWars);
                g.LogChanged.Off(_onLog);
            }
            ItemMoves.SlotChanged.Off(_onSlot);
            _editing = _noticeSaving = false;
            _rankPick = false;
            _noticeField?.Blur();
            _search?.Blur();
            ItemSlotRef sel = ItemMoves.Selected;
            if (sel != null && sel.GridType == MirGridType.GuildStorage) ItemMoves.Selected = null; // guild.ts closed(): drop a picked storage item
        }

        public override void Process()
        {
            base.Process();
            if (GameScene.User == null) return;
            long now = CMain.Time;
            if (now >= _nextTick)
            {
                _nextTick = now + 1000;
                Tick();
            }
            if (_wakeAt > 0 && now >= _wakeAt)
            {
                _wakeAt = 0;
                if (_pendingUntil > 0 && now >= _pendingUntil) { _pendingUntil = 0; _pendingMember = string.Empty; _pendingRank = -1; _pendingBuff = -1; _ranksDirty = _buffsDirty = true; }
                if (_noticeSaving && now >= _noticeSaveUntil) { _noticeSaving = false; _noticeDirty = true; }
                _dirty = true;
            }
            if (_page == Page.Storage)
            {
                if (_storeWatch.Poll(MirGridType.GuildStorage, GameScene.GuildStorage)) _dirty = true;
                if (GameScene.Gold != _goldSeen) _dirty = true;
            }
            if (_dirty) Render();
        }

        public override bool HandleKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _editing && !e.Shift)
            {
                _editing = false;
                _noticeDirty = true;
                _dirty = true;
                return true;
            }
            return false;
        }

        /// <summary>Re-render when <paramref name="until"/> (a throttle or a pending answer) runs out.</summary>
        private void WakeAt(long until)
        {
            until += 50;
            if (_wakeAt == 0 || until < _wakeAt) _wakeAt = until;
        }

        private bool Throttled(long until) => CMain.Time < until;

        /// <summary>Once a second: the countdowns of the shown page, and the stale notice / member list asked again (throttled).</summary>
        private void Tick()
        {
            if (!InGuild) return;
            if (_page == Page.Overview || _page == Page.Members || _page == Page.Ranks) AskMembers();
            if (_page == Page.Overview) AskNotice();
            if (_page == Page.Buffs)
                for (int i = 0; i < _buffTimers.Count; i++) _buffTimers[i].Box.Invalidate();
            if (_page == Page.Wars)
                for (int i = 0; i < _warTimers.Count; i++) _warTimers[i].Box.Invalidate();
        }

        // ------------------------------------------------------------------ seam (ModernUi.Guild)

        /// <summary>S.GuildStatus with no guild: GameScene.GuildStatus hides the dialog.</summary>
        internal void StatusChanging(S.GuildStatus p)
        {
            if (p != null && string.IsNullOrEmpty(p.GuildName)) Close();
            _editing = _noticeSaving = false; // GuildDialog.StatusChanged disables the notice editor
            _noticeDirty = true;
            _dirty = true;
        }

        /// <summary>S.GuildNoticeChange: -1 = the notice went stale (asked again while the overview shows); else new lines.</summary>
        internal void NoticeChange(S.GuildNoticeChange p)
        {
            _noticeSaving = false;
            _editing = false;
            _noticeDirty = true;
            _dirty = true;
            if (p != null && p.update == -1 && IsOpen && _page == Page.Overview) AskNotice();
        }

        /// <summary>S.GuildMemberChange (the model is already edited): the list moved, the pending row unlocks.</summary>
        internal void MemberChange(S.GuildMemberChange p)
        {
            if (p == null) return;
            if (p.Status >= 5) { _pendingMember = string.Empty; _pendingRank = -1; }
            if (p.Status == 7 || p.Status == 6 || p.Status == 255) _rankNote = string.Empty;
            _rosterDirty = _ranksDirty = true;
            _dirty = true;
            if (IsOpen && G != null && G.MembersStale) AskMembers();
        }

        internal void Refresh()
        {
            _rosterDirty = _ranksDirty = _buffsDirty = true;
            _dirty = true;
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>Which pages the user's rank may see (GuildDialog.StatusChanged / RefreshInterface).</summary>
        private static bool PageVisible(Page p)
        {
            switch (p)
            {
                case Page.Ranks: return Can(GuildRankOptions.CanChangeRank);
                case Page.Storage: return Can(GuildRankOptions.CanStoreItem) || Can(GuildRankOptions.CanRetrieveItem);
                case Page.Buffs: return G != null && G.BuffInfos.Count > 0;
                default: return true;
            }
        }

        internal void Go(Page p)
        {
            if (p == _page || !PageVisible(p)) return;
            _page = p;
            _editing = false;
            _rankPick = false;
            _rowsDirty = _ranksDirty = _buffsDirty = _warsDirty = _logDirty = _feedDirty = _noticeDirty = true; // the page shows the model as it is now
            Enter(p);
            _dirty = true;
        }

        /// <summary>A page was opened: the requests its desktop page sends (LeftDialog / RightDialog).</summary>
        private void Enter(Page p)
        {
            GuildState g = G;
            if (g == null || !InGuild) return;
            switch (p)
            {
                case Page.Overview: AskNotice(); g.RequestLog(); break;
                case Page.Storage: Network.Enqueue(new C.GuildStorageItemChange { Type = 3 }); break;
                case Page.Log: g.RequestLog(); break;
                case Page.Members: g.RequestMemberDetails(); break;
            }
            if (p == Page.Overview || p == Page.Members || p == Page.Ranks) AskMembers();
        }

        /// <summary>GuildDialog.RequestUpdateMembers: C.RequestGuildInfo 1 while the list is stale, 5 s apart.</summary>
        private void AskMembers()
        {
            GuildState g = G;
            if (g == null || !g.MembersStale || !InGuild || CMain.Time < _membersReqAt) return;
            _membersReqAt = CMain.Time + InfoGapMs;
            Network.Enqueue(new C.RequestGuildInfo { Type = 1 });
        }

        /// <summary>GuildDialog.RequestUpdateNotice: C.RequestGuildInfo 0 while the notice is stale, 5 s apart.</summary>
        private void AskNotice()
        {
            GuildState g = G;
            if (g == null || !g.NoticeStale || !InGuild || CMain.Time < _noticeReqAt) return;
            _noticeReqAt = CMain.Time + InfoGapMs;
            Network.Enqueue(new C.RequestGuildInfo { Type = 0 });
        }

        // ------------------------------------------------------------------ render (on change only)

        private void Render()
        {
            _dirty = false;
            if (_m == null || _m.IsDisposed || GameScene.User == null) return;
            bool inGuild = InGuild && G != null;
            _main.Visible = inGuild;
            _noGuild.Visible = !inGuild;
            if (!inGuild) { _noGuild.Invalidate(); return; }
            if (!PageVisible(_page)) { _page = Page.Overview; Enter(_page); }
            if (_rosterDirty) BuildRoster();
            _banner.Invalidate();
            RenderNav();
            if (_pageShown != _page)
            {
                for (int i = 0; i < _pages.Length; i++) if (_pages[i] != null) _pages[i].Visible = false;
                PagePanel(_page).Visible = true;
                _pageShown = _page;
            }
            switch (_page)
            {
                case Page.Overview: RenderOverview(); break;
                case Page.Members: RenderMembers(); break;
                case Page.Ranks: RenderRanks(); break;
                case Page.Buffs: RenderBuffs(); break;
                case Page.Storage: RenderStorage(); break;
                case Page.Wars: RenderWars(); break;
                case Page.Log: RenderLog(); break;
            }
        }

        /// <summary>The roster: Ranks' members (live online flags) with the details merged in by name; the details alone while
        /// the rank list has not arrived.</summary>
        private void BuildRoster()
        {
            _rosterDirty = false;
            _rowsDirty = true;
            _roster.Clear();
            GuildState g = G;
            if (g == null) return;
            var details = new Dictionary<string, ClientGuildMemberDetail>();
            for (int i = 0; i < g.MemberDetails.Count; i++)
                if (g.MemberDetails[i] != null) details[g.MemberDetails[i].Name ?? string.Empty] = g.MemberDetails[i];
            if (g.Ranks.Count > 0)
            {
                for (int r = 0; r < g.Ranks.Count; r++)
                {
                    GuildRank rank = g.Ranks[r];
                    if (rank?.Members == null) continue;
                    for (int j = 0; j < rank.Members.Count; j++)
                    {
                        GuildMember m = rank.Members[j];
                        details.TryGetValue(m.Name ?? string.Empty, out ClientGuildMemberDetail d);
                        _roster.Add(new Row
                        {
                            Name = m.Name ?? string.Empty, Rank = rank.Index, RankName = rank.Name, Online = m.Online, Last = m.LastLogin,
                            HasDetail = d != null, Cls = d?.Class ?? MirClass.Warrior, Level = d?.Level ?? 0,
                        });
                    }
                }
            }
            else
            {
                for (int i = 0; i < g.MemberDetails.Count; i++)
                {
                    ClientGuildMemberDetail d = g.MemberDetails[i];
                    if (d == null) continue;
                    DateTime last = DateTime.MinValue;
                    try { if (d.LastLogin != 0) last = DateTime.FromBinary(d.LastLogin); } catch (ArgumentException) { last = DateTime.MinValue; }
                    _roster.Add(new Row { Name = d.Name ?? string.Empty, Rank = d.RankIndex, RankName = string.Empty, Online = d.Online, Last = last, HasDetail = true, Cls = d.Class, Level = d.Level });
                }
            }
        }

        private int OnlineCount()
        {
            int n = 0;
            for (int i = 0; i < _roster.Count; i++) if (_roster[i].Online) n++;
            return n;
        }

        private int ActiveBuffs()
        {
            int n = 0;
            GuildState g = G;
            if (g == null) return 0;
            for (int i = 0; i < g.EnabledBuffs.Count; i++) if (g.EnabledBuffs[i] != null && g.EnabledBuffs[i].Active) n++;
            return n;
        }

        private void RenderNav()
        {
            GuildState g = G;
            for (int i = 0; i < Pages.Length; i++)
            {
                Page p = Pages[i].P;
                bool vis = PageVisible(p);
                if (_nav[i].Visible != vis) _nav[i].Visible = vis;
                int n = p == Page.Members ? _roster.Count : p == Page.Buffs ? ActiveBuffs() : p == Page.Wars ? g.Wars.Count : 0;
                if (n != _navCount[i]) _navCount[i] = n;
                _nav[i].Invalidate();
            }
            // the visible buttons stack without gaps
            int y = _bannerH + 10;
            for (int i = 0; i < Pages.Length; i++)
            {
                if (!_nav[i].Visible) continue;
                Point at = new Point(_nav[i].Location.X, S(y));
                if (_nav[i].Location != at) _nav[i].Location = at;
                y += 33;
            }
            if (_youName != null)
            {
                _youName.Text = Gd.RankName(GameScene.User?.GuildRankName, g.MyRankId);
                int rights = 0;
                for (int i = 0; i < Options.Length; i++) if (Can(Options[i].Bit)) rights++;
                _youRights.Text = rights == Options.Length ? W.T(ClientTextKeys.ModernGuildAllRights) : rights > 0 ? W.T(rights == 1 ? ClientTextKeys.ModernGuildOneRight : ClientTextKeys.ModernGuildNRights, rights) : W.T(ClientTextKeys.ModernGuildNoRights);
            }
        }

        // ------------------------------------------------------------------ painters (banner / nav / no guild)

        private void PaintNoGuild(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, cy = b.Size.Height / 2f;
            ModernSkin.Text(g, W.T(ClientTextKeys.NotInGuild), ModernSkin.Font(15, true), Color.White, new RectangleF(0, cy - 20 * k, b.Size.Width, 20 * k), StringAlignment.Center);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildNoGuildHint), ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(40 * k, cy + 6 * k, b.Size.Width - 80 * k, 40 * k), StringAlignment.Center, StringAlignment.Near, false, true);
        }

        private void PaintBanner(Graphics g, PaintBox b)
        {
            GuildState gs = G;
            if (gs == null) return;
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            float top = _narrow ? 82 * k : h;
            // background: an opaque page base (translucent pixels would let the world show through the window), a fading
            // panel band, an accent glow at the left, and the bottom line
            using (var page = new SolidBrush(W.Page)) g.FillRectangle(page, 0, 0, w, h);
            using (var band = new LinearGradientBrush(new RectangleF(0, 0, w, h + 1), ModernTheme.Alpha(ModernTheme.PanelA, 0.6f), Color.Transparent, LinearGradientMode.Vertical))
                g.FillRectangle(band, 0, 0, w, h);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-w * 0.5f, -h * 0.9f, w, h * 2.8f);
                using var glow = new PathGradientBrush(path) { CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.18f), SurroundColors = new[] { Color.Transparent }, CenterPoint = new PointF(0, h / 2f) };
                g.FillRectangle(glow, 0, 0, w, h);
            }
            using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.Frame, 0.7f), 1f)) g.DrawLine(pen, 0, h - 0.5f, w, h - 0.5f);

            string name = GameScene.User?.GuildName ?? string.Empty;
            Gd.Shield(g, new RectangleF(18 * k, (top - 64 * k) / 2f, 56 * k, 64 * k), Gd.Initials(name), 2.5f * k, 20);
            float x = 90 * k, y = (top - 70 * k) / 2f;
            ModernSkin.Text(g, name, ModernSkin.Font(20, true), Color.White, new RectangleF(x, y, (_narrow ? w - 110 * k : 300 * k), 26 * k));
            // "Level N guild · you are RANK"
            float sx = x;
            Font f = ModernSkin.Font(11), fb = ModernSkin.Font(11, true);
            void Seg(string t, Font font, Color c)
            {
                float tw = ModernSkin.Measure(t, font);
                ModernSkin.Text(g, t, font, c, new RectangleF(sx, y + 27 * k, tw + 2, 15 * k), StringAlignment.Near, StringAlignment.Center, false);
                sx += tw;
            }
            Seg(W.T(ClientTextKeys.ModernGuildLevelWord) + " ", f, ModernTheme.Dim);
            Seg(gs.Level.ToString(), fb, ModernTheme.Accent);
            Seg(" " + W.T(ClientTextKeys.ModernGuildGuildYouAre) + " ", f, ModernTheme.Dim);
            Seg(Gd.RankName(GameScene.User?.GuildRankName, gs.MyRankId), fb, ModernTheme.Accent);
            // EXP
            float ew = 240 * k, ey = y + 48 * k;
            string exp = gs.MaxExperience > 0
                ? W.N(gs.Experience) + " / " + W.N(gs.MaxExperience) + " (" + (gs.Experience * 100.0 / gs.MaxExperience).ToString("0.#") + "%)"
                : W.T(ClientTextKeys.ModernGuildMaxLevel);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildExp), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, ey, ew, 13 * k));
            ModernSkin.Text(g, exp, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, ey, ew, 13 * k), StringAlignment.Far);
            Gd.Bar(g, new RectangleF(x, ey + 16 * k, ew, 6 * k), gs.MaxExperience > 0 ? (float)Math.Min(1.0, gs.Experience / (double)gs.MaxExperience) : 1f, null, true);

            // stats: members, online, fund, buff points
            string[] vals =
            {
                gs.MaxMembers > 0 ? gs.MemberCount + " / " + gs.MaxMembers : gs.MemberCount.ToString(),
                _roster.Count > 0 ? OnlineCount().ToString() : "—", W.Compact(gs.Gold), gs.SparePoints.ToString(),
            };
            ClientTextKeys[] labels = { ClientTextKeys.ModernGuildStatMembers, ClientTextKeys.ModernGuildStatOnline, ClientTextKeys.ModernGuildStatFund, ClientTextKeys.ModernGuildStatPoints };
            float bw, bh, bx, by, gap = 8 * k;
            if (_narrow)
            {
                bw = (w - 24 * k - 3 * gap) / 4f;
                bh = 32 * k;
                bx = 12 * k;
                by = top;
            }
            else
            {
                bw = 84 * k;
                bh = 46 * k;
                bx = w - 18 * k - 4 * bw - 3 * gap;
                by = (h - bh) / 2f;
            }
            for (int i = 0; i < 4; i++)
            {
                RectangleF r = new RectangleF(bx + i * (bw + gap), by, bw, bh);
                using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                {
                    using (var br = new SolidBrush(ModernTheme.Alpha(ModernTheme.PanelB, 0.55f))) g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
                }
                Color vc = i == 3 ? ModernTheme.Accent : Color.White;
                if (_narrow)
                {
                    ModernSkin.Text(g, vals[i], ModernSkin.Font(13, true), vc, new RectangleF(r.X, r.Y + 2 * k, r.Width, 16 * k), StringAlignment.Center);
                    ModernSkin.SpacedText(g, W.T(labels[i]).ToUpperInvariant(), ModernSkin.Font(8, true), ModernTheme.Dim, new RectangleF(r.X, r.Y + 18 * k, r.Width, 12 * k), 0.8f * k);
                }
                else
                {
                    ModernSkin.Text(g, vals[i], ModernSkin.Font(14, true), vc, new RectangleF(r.X, r.Y + 6 * k, r.Width, 18 * k), StringAlignment.Center);
                    ModernSkin.SpacedText(g, W.T(labels[i]).ToUpperInvariant(), ModernSkin.Font(9, true), ModernTheme.Dim, new RectangleF(r.X, r.Y + 26 * k, r.Width, 14 * k), 1f * k);
                }
            }
        }

        private void PaintNav(Graphics g, PaintBox b, int i)
        {
            float k = ModernRoot.K;
            bool on = _page == Pages[i].P;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                if (on || b.Hovered)
                    using (var br = new SolidBrush(W.Over(ModernTheme.Accent, on ? 0.16f : 0.08f))) g.FillPath(br, p);
                if (on) ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.6f));
            }
            if (on)
                using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, 1, 2 * k, 3 * k, b.Size.Height - 4 * k);
            if (_narrow) return;
            ModernSkin.Text(g, W.T(Pages[i].Name), ModernSkin.Font(12, true), on ? Color.White : b.Hovered ? ModernTheme.Text : ModernTheme.Dim, new RectangleF(32 * k, 0, b.Size.Width - 64 * k, b.Size.Height));
            int n = _navCount[i];
            if (n <= 0) return;
            string t = n.ToString();
            float tw = ModernSkin.Measure(t, ModernSkin.Font(10, true)) + 12 * k;
            RectangleF pill = new RectangleF(b.Size.Width - tw - 6 * k, (b.Size.Height - 15 * k) / 2f, tw, 15 * k);
            using (GraphicsPath pp = ModernSkin.RoundRect(pill, 7 * k))
            using (var br = new SolidBrush(W.Over(ModernTheme.Frame, 0.5f)))
                g.FillPath(br, pp);
            ModernSkin.Text(g, t, ModernSkin.Font(10, true), ModernTheme.Text, pill, StringAlignment.Center);
        }

        // ------------------------------------------------------------------ overview

        private int Inner => _pw - 28;

        private void BuildOverview(ModernPanel p)
        {
            int iw = Inner, cw = (iw - 30) / 4;
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                _kpi[i] = new PaintBox((g, b) => PaintKpi(g, b, idx)) { Parent = p, Location = new Point(S(14 + i * (cw + 10)), S(12)), Size = new Size(S(cw), S(66)), NotControl = true };
            }
            int nw = (int)((iw - 12) * 0.575f), ow = iw - 12 - nw, cy = 88, ch = 206;
            _noticeCard = new PaintBox((g, b) => Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildNoticeBoard))) { Parent = p, Location = new Point(S(14), S(cy)), Size = new Size(S(nw), S(ch)), NotControl = true };
            _noticeBtn = SmallButton(p, W.T(ClientTextKeys.ModernGuildEdit), 0, cy + 5, NoticeAction, W.T(ClientTextKeys.ModernGuildEditHint));
            _noticeCancel = SmallButton(p, W.T(ClientTextKeys.ModernCancel), 0, cy + 5, () => { _editing = false; _noticeDirty = true; _dirty = true; });
            _noticeView = new ScrollView(nw - 20, ch - 36) { Parent = p, Location = new Point(S(24), S(cy + 30)), Background = W.Over(ModernTheme.FrameLight, 0.05f) };
            _noticeView.Framed = false;
            _noticeText = W.Label(_noticeView.Content, string.Empty, 0, 0, nw - 32, 20, 12, false, null, StringAlignment.Near, true);
            _noticeField = W.At(new ModernTextField(nw - 20, ch - 36), p, 24, cy + 30);
            _noticeField.Box.MultiLine();
            _noticeField.Box.Size = new Size(_noticeField.Size.Width - S(14), _noticeField.Size.Height - S(10));
            _noticeField.Box.Location = new Point(S(7), S(5));
            _noticeField.MaxLength = 4000;
            _noticeField.Submitted += _ => { if (!_noticeField.Box.IsDisposed) _noticeField.Box.TextBox.SelectedText = "\r\n"; }; // Enter = a new line
            _noticeField.Visible = false;
            _onlineCard = new PaintBox(PaintOnline) { Parent = p, Location = new Point(S(14 + nw + 12), S(cy)), Size = new Size(S(ow), S(ch)), NotControl = true };
            int fy = cy + ch + 10, fh = _ph - fy - 12;
            _feedCard = new PaintBox((g, b) => PaintFeed(g, b)) { Parent = p, Location = new Point(S(14), S(fy)), Size = new Size(S(iw), S(fh)), NotControl = true };
            _feedCard.AfterPaint = b => FeedIcons(b);
        }

        private void PaintKpi(Graphics g, PaintBox b, int i)
        {
            GuildState gs = G;
            if (gs == null) return;
            W.Card(g, b.Size);
            float k = ModernRoot.K, x = 12 * k, w = b.Size.Width - 24 * k;
            string big, sub, note;
            int online = OnlineCount();
            switch (i)
            {
                case 0:
                    big = _roster.Count > 0 ? online.ToString() : "—";
                    sub = W.T(ClientTextKeys.ModernGuildKpiOnlineOf, gs.MemberCount);
                    note = _roster.Count > 0 ? W.T(ClientTextKeys.ModernGuildKpiActive, (int)Math.Round(online * 100.0 / Math.Max(1, _roster.Count))) : W.T(ClientTextKeys.ModernGuildLoadingRoster);
                    break;
                case 1:
                    big = gs.Level.ToString();
                    sub = W.T(ClientTextKeys.ModernGuildKpiLevel);
                    note = gs.MaxExperience > 0 && gs.MaxExperience > gs.Experience ? W.T(ClientTextKeys.ModernGuildKpiToLevel, W.N(gs.MaxExperience - gs.Experience), gs.Level + 1) : string.Empty;
                    break;
                case 2:
                    big = W.Compact(gs.Gold);
                    sub = W.T(ClientTextKeys.ModernGuildKpiFund);
                    note = string.Empty;
                    break;
                default:
                    big = ActiveBuffs().ToString();
                    sub = W.T(ClientTextKeys.ModernGuildKpiBuffs);
                    note = W.T(gs.SparePoints == 1 ? ClientTextKeys.ModernGuildKpiOnePoint : ClientTextKeys.ModernGuildKpiPoints, gs.SparePoints);
                    break;
            }
            ModernSkin.Text(g, big, ModernSkin.Font(19, true), Color.White, new RectangleF(x, 8 * k, w, 24 * k));
            ModernSkin.Text(g, sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, 32 * k, w, 13 * k));
            ModernSkin.Text(g, note, ModernSkin.Font(10), ModernTheme.AccentDim, new RectangleF(x, 46 * k, w, 13 * k));
        }

        private void PaintOnline(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            int online = OnlineCount();
            Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildOnlineNow, online));
            float y = 30 * k, x = 12 * k, w = b.Size.Width - 24 * k;
            int max = Math.Max(1, (int)((b.Size.Height - 30 * k - 24 * k) / (24 * k))), shown = 0;
            for (int i = 0; i < _roster.Count && shown < max; i++)
            {
                Row r = _roster[i];
                if (!r.Online) continue;
                shown++;
                float cy = y + 12 * k;
                Gd.Dot(g, x + 2 * k, cy, true);
                float nw = Math.Min(ModernSkin.Measure(r.Name, ModernSkin.Font(12)) + 2, w * 0.45f);
                ModernSkin.Text(g, r.Name, ModernSkin.Font(12), ModernTheme.Text, new RectangleF(x + 14 * k, y, nw, 24 * k));
                Gd.RankTag(g, x + 20 * k + nw, cy, r.RankName, r.Rank);
                if (r.HasDetail)
                    ModernSkin.Text(g, Gd.ClassName(r.Cls) + " " + r.Level, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, y, w, 24 * k), StringAlignment.Far);
                y += 24 * k;
            }
            if (online > shown && shown > 0)
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildAndMore, online - shown), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, y, w, 20 * k), StringAlignment.Far);
            if (online == 0)
                ModernSkin.Text(g, W.T(_roster.Count > 0 ? ClientTextKeys.ModernGuildNobodyOnline : ClientTextKeys.ModernGuildLoadingRoster), ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(x, y + 4 * k, w, 20 * k));
        }

        /// <summary>The recent activity card (overview: the newest lines that fit).</summary>
        private void PaintFeed(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildRecent));
            List<ClientGuildLogEntry> log = G?.Log;
            float y = 28 * k, x = 12 * k, w = b.Size.Width - 24 * k, rh = 26 * k;
            int max = Math.Max(1, (int)((b.Size.Height - y - 4 * k) / rh)), n = 0;
            if (log == null || log.Count == 0)
            {
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildNoActivity), ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(x, y, w, 20 * k));
                return;
            }
            for (int i = log.Count - 1; i >= 0 && n < max; i--, n++)
            {
                ClientGuildLogEntry e = log[i];
                string ago = AgoBinary(e.Time);
                float aw = ModernSkin.Measure(ago, ModernSkin.Font(10)) + 4;
                ModernSkin.Text(g, e.Text, ModernSkin.Font(12), ModernTheme.Text, new RectangleF(x + 24 * k, y, w - 24 * k - aw - 6 * k, rh));
                ModernSkin.Text(g, ago, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x + w - aw, y, aw, rh), StringAlignment.Far);
                if (n < max - 1 && i > 0)
                    using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.3f), 1f)) g.DrawLine(pen, x, y + rh - 0.5f, x + w, y + rh - 0.5f);
                y += rh;
            }
        }

        private void FeedIcons(PaintBox b)
        {
            List<ClientGuildLogEntry> log = G?.Log;
            if (log == null || log.Count == 0) return;
            float k = ModernRoot.K, rh = 26 * k;
            int max = Math.Max(1, (int)((b.Size.Height - 28 * k - 4 * k) / rh)), n = 0;
            float y = 28 * k;
            for (int i = log.Count - 1; i >= 0 && n < max; i--, n++)
            {
                int s = S(14);
                W.DrawIcon(LogIcon(log[i].Kind), new Rectangle(b.DisplayLocation.X + S(14), b.DisplayLocation.Y + (int)(y + (rh - s) / 2f), s, s), ModernTheme.AccentDim);
                y += rh;
            }
        }

        private static string AgoBinary(long time)
        {
            if (time == 0) return string.Empty;
            try
            {
                return Eco.Ago(DateTime.FromBinary(time));
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private void RenderOverview()
        {
            for (int i = 0; i < 4; i++) _kpi[i].Invalidate();
            _onlineCard.Invalidate();
            if (_feedDirty) { _feedDirty = false; _feedCard.Invalidate(); }
            GuildState gs = G;
            bool canEdit = Can(GuildRankOptions.CanChangeNotice);
            if (!canEdit) _editing = false;
            _noticeBtn.Visible = canEdit;
            _noticeCancel.Visible = _editing;
            _noticeBtn.Text = _noticeSaving ? W.T(ClientTextKeys.ModernGuildSaving) : _editing ? W.T(ClientTextKeys.ModernGuildSave) : W.T(ClientTextKeys.ModernGuildEdit);
            _noticeBtn.AutoWidth(50);
            _noticeBtn.Kind = _editing ? ButtonKind.Main : ButtonKind.Normal;
            _noticeBtn.SetDisabled(_noticeSaving, W.T(ClientTextKeys.ModernWinWaitingServer));
            _noticeBtn.Tooltip = _editing || _noticeSaving ? string.Empty : W.T(ClientTextKeys.ModernGuildEditHint);
            int right = _noticeCard.Location.X + _noticeCard.Size.Width - S(10);
            _noticeBtn.Location = new Point(right - _noticeBtn.Size.Width, _noticeBtn.Location.Y);
            _noticeCancel.Location = new Point(_noticeBtn.Location.X - S(6) - _noticeCancel.Size.Width, _noticeCancel.Location.Y);
            _noticeField.Visible = _editing;
            _noticeView.Visible = !_editing;
            if (!_editing) _noticeField.Blur();
            if (_noticeDirty && !_editing && !_noticeSaving)
            {
                _noticeDirty = false;
                bool any = false;
                foreach (string l in gs.Notice) if (!string.IsNullOrWhiteSpace(l)) any = true;
                string text = any ? string.Join("\n", gs.Notice) : W.T(gs.NoticeStale ? ClientTextKeys.ModernGuildLoadingNotice : ClientTextKeys.ModernGuildNoNotice);
                _noticeText.Text = text;
                _noticeText.Colour = any ? Color.Empty : ModernTheme.Dim;
                int tw = _noticeView.InnerWidth;
                SizeF sz = ModernSkin.MeasureWrapped(text, ModernSkin.Font(12), tw);
                _noticeText.Size = new Size(tw, (int)Math.Ceiling(sz.Height) + S(4));
                _noticeView.FitContent(0);
                _noticeView.ScrollTo(0);
            }
        }

        private void NoticeAction()
        {
            GuildState gs = G;
            if (gs == null || !Can(GuildRankOptions.CanChangeNotice) || _noticeSaving) return;
            if (!_editing)
            {
                _editing = true;
                _noticeField.Text = string.Join("\r\n", gs.Notice);
                _dirty = true;
                _noticeField.Focus();
                return;
            }
            string[] lines = (_noticeField.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            if (lines.Length > MaxNoticeLines)
            {
                GameScene.Models.Chat.Add(W.T(ClientTextKeys.ModernGuildNoticeTooLong, MaxNoticeLines), ChatType.System);
                return;
            }
            // C.EditGuildNotice; the view stays read-only until S.GuildNoticeChange (or 5 s) answers
            Network.Enqueue(new C.EditGuildNotice { notice = new List<string>(lines) });
            gs.Notice.Clear();
            gs.Notice.AddRange(lines); // what the server will echo; shown greyed while saving
            _editing = false;
            _noticeSaving = true;
            _noticeSaveUntil = CMain.Time + PendingMs;
            WakeAt(_noticeSaveUntil);
            _noticeDirty = true;
            _dirty = true;
            ShowSavingText();
        }

        private void ShowSavingText()
        {
            GuildState gs = G;
            if (gs == null || _noticeText == null) return;
            string text = string.Join("\n", gs.Notice);
            _noticeText.Text = text;
            _noticeText.Colour = ModernTheme.Dim;
            SizeF sz = ModernSkin.MeasureWrapped(text, ModernSkin.Font(12), _noticeView.InnerWidth);
            _noticeText.Size = new Size(_noticeView.InnerWidth, (int)Math.Ceiling(sz.Height) + S(4));
            _noticeView.FitContent(0);
        }

        // ------------------------------------------------------------------ members

        private void BuildMembers(ModernPanel p)
        {
            int iw = Inner;
            _cardW = _narrow ? 196 : 220;
            _tableW = iw - _cardW - 12;
            int sw = _narrow ? 150 : 190;
            _search = W.At(new SearchBox(sw, W.T(ClientTextKeys.ModernGuildSearchMembers)), p, 14, 10);
            if (_query.Length > 0) _search.Text = _query;
            _search.Search += q => { _query = q.ToLowerInvariant(); _rowsDirty = true; _dirty = true; };
            var choices = new List<Choice<string>> { new Choice<string>("all", W.T(ClientTextKeys.ModernGuildLogAll)), new Choice<string>("online", W.T(ClientTextKeys.ModernGuildOnline)) };
            _filterChips = W.At(new Chips<string>(choices, new[] { _onlineOnly ? "online" : "all" }, 130, false, false), p, 14 + sw + 8, 12);
            _filterChips.Changed += s => { _onlineOnly = s.Contains("online"); _rowsDirty = true; _dirty = true; };
            _inviteBtn = SmallButton(p, W.T(ClientTextKeys.ModernGuildInvite), 0, 11, Invite, W.T(ClientTextKeys.ModernGuildInviteHint), ButtonKind.Main, 60);
            _inviteBtn.Location = new Point(S(14 + iw) - _inviteBtn.Size.Width, _inviteBtn.Location.Y);
            _detailsBtn = SmallButton(p, W.T(ClientTextKeys.ModernEcoRefresh), 0, 11, () =>
            {
                if (G == null || !G.RequestMemberDetails()) { _memberNote = W.T(ClientTextKeys.ModernGuildTryAgain); _dirty = true; }
            }, W.T(ClientTextKeys.ModernGuildRefreshHint));
            _detailsBtn.Location = new Point(_inviteBtn.Location.X - S(6) - _detailsBtn.Size.Width, _detailsBtn.Location.Y);

            _memberHead = new PaintBox(PaintMemberHead) { Parent = p, Location = new Point(S(14), S(44)), Size = new Size(S(_tableW), S(24)) };
            _memberHead.Click += (o, e) => SortClick();
            _memberRows = new ScrollView(_tableW, _ph - 68 - 34) { Parent = p, Location = new Point(S(14), S(68)), Background = W.Page };
            _memberCard = new PaintBox(PaintMemberCard) { Parent = p, Location = new Point(S(14 + _tableW + 12), S(44)), Size = new Size(S(_cardW), S(_ph - 44 - 34)), NotControl = true };
            _memberNoteLbl = W.Label(p, string.Empty, 14, _ph - 28, iw, 16, 11, false, ModernTheme.Accent);
        }

        private static readonly (SortKey K, ClientTextKeys Name, float W)[] Cols =
        {
            (SortKey.Name, ClientTextKeys.ModernGuildColName, 0.31f), (SortKey.Class, ClientTextKeys.ModernGuildColClass, 0.17f), (SortKey.Level, ClientTextKeys.ModernGuildColLevel, 0.12f),
            (SortKey.Rank, ClientTextKeys.ModernGuildColRank, 0.2f), (SortKey.Seen, ClientTextKeys.ModernGuildColSeen, 0.2f),
        };

        private float ColX(int col, float width)
        {
            float x = 0;
            for (int i = 0; i < col; i++) x += Cols[i].W * width;
            return x;
        }

        private void PaintMemberHead(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, w = b.Size.Width;
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.9f))) g.FillRectangle(br, 0, 0, w, b.Size.Height);
            for (int i = 0; i < Cols.Length; i++)
            {
                bool on = Cols[i].K == _sort;
                string t = W.T(Cols[i].Name).ToUpperInvariant() + (on ? (_sortDir > 0 ? " ▲" : " ▼") : string.Empty);
                ModernSkin.Text(g, t, ModernSkin.Font(10, true), on ? ModernTheme.Accent : ModernTheme.Dim, new RectangleF(ColX(i, w) + 8 * k, 0, Cols[i].W * w - 8 * k, b.Size.Height));
            }
        }

        private void SortClick()
        {
            int x = CMain.MPoint.X - _memberHead.DisplayLocation.X;
            float w = _memberHead.Size.Width;
            for (int i = 0; i < Cols.Length; i++)
            {
                if (x < ColX(i, w) || x >= ColX(i + 1, w)) continue;
                _sortDir = _sort == Cols[i].K ? -_sortDir : 1;
                _sort = Cols[i].K;
                _memberHead.Invalidate();
                _rowsDirty = true;
                _dirty = true;
                return;
            }
        }

        private List<Row> FilteredRows()
        {
            var rows = new List<Row>();
            for (int i = 0; i < _roster.Count; i++)
            {
                Row r = _roster[i];
                if (_onlineOnly && !r.Online) continue;
                if (_query.Length > 0 && !r.Name.ToLowerInvariant().Contains(_query) && !(r.RankName ?? string.Empty).ToLowerInvariant().Contains(_query)
                    && !(r.HasDetail && Gd.ClassName(r.Cls).ToLowerInvariant().Contains(_query))) continue;
                rows.Add(r);
            }
            rows.Sort((a, b) =>
            {
                int c;
                switch (_sort)
                {
                    case SortKey.Name: c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case SortKey.Class: c = (a.HasDetail ? (int)a.Cls : 99).CompareTo(b.HasDetail ? (int)b.Cls : 99); break;
                    case SortKey.Level: c = (a.HasDetail ? a.Level : -1).CompareTo(b.HasDetail ? b.Level : -1); break;
                    case SortKey.Rank: c = a.Rank.CompareTo(b.Rank); break;
                    default: c = SeenValue(a).CompareTo(SeenValue(b)); break;
                }
                if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c * _sortDir;
            });
            return rows;
        }

        private static long SeenValue(Row r) => r.Online ? long.MaxValue : r.Last.Year < 2000 ? 0 : r.Last.Ticks;

        private void RenderMembers()
        {
            bool recruit = Can(GuildRankOptions.CanRecruit);
            _inviteBtn.Visible = recruit;
            _inviteBtn.SetDisabled(Throttled(_lastMemberMsg), W.T(ClientTextKeys.ModernGuildWaitChange));
            _memberNoteLbl.Text = _memberNote;
            if (_rowsDirty) RenderMemberRows();
            RenderMemberCard();
        }

        private void RenderMemberRows()
        {
            _rowsDirty = false;
            for (int i = _memberRows.Content.Controls.Count - 1; i >= 0; i--) _memberRows.Content.Controls[i].Dispose();
            List<Row> rows = FilteredRows();
            int y = 0, w = _memberRows.InnerWidth;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                bool odd = i % 2 == 1;
                var row = new PaintBox((g, b) => PaintMemberRow(g, b, r, odd)) { Parent = _memberRows.Content, Location = new Point(0, y), Size = new Size(w, S(26)), Tag2 = r.Name };
                row.Click += (o, e) => { if (_selected != r.Name) { _selected = r.Name; _rankPick = false; _rowsDirty = true; _dirty = true; } };
                y += S(26);
            }
            if (rows.Count == 0)
                W.Label(_memberRows.Content, W.T(_roster.Count > 0 ? ClientTextKeys.ModernGuildNoMatch : ClientTextKeys.ModernGuildLoadingRoster), 0, 20, (int)(w / ModernRoot.K), 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _memberRows.FitContent(0);
        }

        private void PaintMemberRow(Graphics g, PaintBox b, Row r, bool odd)
        {
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            bool sel = r.Name == _selected;
            Color fill = sel ? W.Over(ModernTheme.Accent, 0.16f) : b.Hovered ? W.Over(ModernTheme.Accent, 0.07f) : odd ? W.Over(ModernTheme.FrameLight, 0.03f) : W.Page;
            using (var br = new SolidBrush(fill)) g.FillRectangle(br, 0, 0, w, h); // opaque: text on transparent pixels renders thin
            using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.25f), 1f)) g.DrawLine(pen, 0, h - 0.5f, w, h - 0.5f);
            Font f = ModernSkin.Font(12), fs = ModernSkin.Font(11);
            float x0 = ColX(0, w) + 8 * k;
            Gd.Dot(g, x0, h / 2f, r.Online);
            ModernSkin.Text(g, r.Name, f, sel ? Color.White : ModernTheme.Text, new RectangleF(x0 + 13 * k, 0, Cols[0].W * w - 21 * k, h));
            ModernSkin.Text(g, r.HasDetail ? Gd.ClassName(r.Cls) : "…", fs, r.HasDetail ? ModernTheme.Class(r.Cls) : ModernTheme.Dim, new RectangleF(ColX(1, w) + 8 * k, 0, Cols[1].W * w - 8 * k, h));
            ModernSkin.Text(g, r.HasDetail ? r.Level.ToString() : "…", f, ModernTheme.Text, new RectangleF(ColX(2, w) + 8 * k, 0, Cols[2].W * w - 8 * k, h));
            g.SetClip(new RectangleF(ColX(3, w), 0, Cols[3].W * w - 4 * k, h));
            Gd.RankTag(g, ColX(3, w) + 8 * k, h / 2f, r.RankName, r.Rank);
            g.ResetClip();
            ModernSkin.Text(g, Gd.Seen(r.Online, r.Last), fs, ModernTheme.Dim, new RectangleF(ColX(4, w) + 8 * k, 0, Cols[4].W * w - 8 * k, h));
        }

        private bool CurrentRow(out Row row)
        {
            row = default;
            string me = Me;
            int at = -1;
            for (int i = 0; i < _roster.Count; i++) if (_roster[i].Name == _selected) at = i;
            if (at < 0) for (int i = 0; i < _roster.Count; i++) if (_roster[i].Name == me) at = i;
            if (at < 0 && _roster.Count > 0) at = 0;
            if (at < 0) return false;
            row = _roster[at];
            return true;
        }

        private void PaintMemberCard(Graphics g, PaintBox b)
        {
            W.Card(g, b.Size);
            float k = ModernRoot.K, x = 12 * k, w = b.Size.Width - 24 * k;
            if (!CurrentRow(out Row r))
            {
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildPickMember), ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(x, 12 * k, w, 20 * k));
                return;
            }
            Gd.Dot(g, x, 22 * k, r.Online, 8);
            ModernSkin.Text(g, r.Name, ModernSkin.Font(15, true), Color.White, new RectangleF(x + 14 * k, 10 * k, w - 14 * k, 24 * k));
            Gd.RankTag(g, x, 44 * k, r.RankName, r.Rank);
            float y = 58 * k;
            void Ln(string key, string val)
            {
                ModernSkin.Text(g, key, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(x, y, w, 22 * k));
                ModernSkin.Text(g, val, ModernSkin.Font(11, true), ModernTheme.Text, new RectangleF(x, y, w, 22 * k), StringAlignment.Far);
                using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.25f), 1f)) g.DrawLine(pen, x, y + 22 * k - 0.5f, x + w, y + 22 * k - 0.5f);
                y += 22 * k;
            }
            Ln(W.T(ClientTextKeys.ModernGuildColClass), r.HasDetail ? Gd.ClassName(r.Cls) : "…");
            Ln(W.T(ClientTextKeys.ModernGuildColLevel), r.HasDetail ? r.Level.ToString() : "…");
            Ln(W.T(r.Online ? ClientTextKeys.ModernGuildStatus : ClientTextKeys.ModernGuildColSeen), Gd.Seen(r.Online, r.Last));
            if (_rankPick)
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildSetRankTo), ModernSkin.Font(10, true), ModernTheme.AccentDim, new RectangleF(x, 128 * k, w, 14 * k));
        }

        /// <summary>The card's action buttons (rebuilt on a change): whisper / group, promote / demote / set rank, kick, leave.</summary>
        private void RenderMemberCard()
        {
            _memberCard.Invalidate();
            for (int i = 0; i < _cardActs.Count; i++) _cardActs[i].Dispose();
            _cardActs.Clear();
            if (!CurrentRow(out Row r)) return;
            _selected = r.Name;
            GuildState g = G;
            bool self = r.Name == Me;
            bool busy = _pendingMember == r.Name;
            bool wait = Throttled(_lastMemberMsg);
            bool editable = !self && Can(GuildRankOptions.CanChangeRank) && r.Rank >= g.MyRankId && g.Ranks.Count > 0;
            string why = busy ? W.T(ClientTextKeys.ModernWinWaitingServer) : wait ? W.T(ClientTextKeys.ModernGuildWaitChange) : string.Empty;
            int cx = _memberCard.Location.X + S(10), cy = _memberCard.Location.Y + S(_rankPick ? 146 : 130), bw = (_memberCard.Size.Width - S(25)) / 2;
            ModernPanel parent = (ModernPanel)_memberCard.Parent;
            int n = 0;
            ModernButton Act(string text, Action run, ButtonKind kind = ButtonKind.Normal, string disabled = null, string hint = null, bool full = false)
            {
                var b = new ModernButton(text, kind) { Parent = parent, FontSize = 11 };
                if (full && n % 2 == 1) n++;
                b.Size = new Size(full ? bw * 2 + S(5) : bw, ModernButton.BarHeight(false));
                b.Location = new Point(cx + (n % 2) * (bw + S(5)), cy + (n / 2) * S(27));
                n += full ? 2 : 1;
                if (hint != null) b.Tooltip = hint;
                if (!string.IsNullOrEmpty(disabled)) b.SetDisabled(true, disabled);
                b.Click += (o, e) => run();
                _cardActs.Add(b);
                return b;
            }

            if (_rankPick && editable)
            {
                var list = new ScrollView((int)(_memberCard.Size.Width / ModernRoot.K) - 20, (int)((_memberCard.Location.Y + _memberCard.Size.Height - cy) / ModernRoot.K) - 40)
                { Parent = parent, Location = new Point(cx, cy), Background = W.Over(ModernTheme.FrameLight, 0.05f) };
                list.Framed = false;
                _cardActs.Add(list);
                int y = 0;
                string name = r.Name;
                for (int i = 0; i < g.Ranks.Count; i++)
                {
                    GuildRank rank = g.Ranks[i];
                    if (rank == null || rank.Index < g.MyRankId) continue;
                    int to = rank.Index;
                    var b = new ModernButton(Gd.RankName(rank.Name, rank.Index) + (rank.Index == r.Rank ? " ✓" : string.Empty), rank.Index == r.Rank ? ButtonKind.Main : ButtonKind.Normal)
                    { Parent = list.Content, Location = new Point(0, y), Size = new Size(list.InnerWidth, ModernButton.BarHeight(false)), FontSize = 11 };
                    if (rank.Index == r.Rank) b.SetDisabled(true, W.T(ClientTextKeys.ModernGuildAlreadyRank));
                    else if (why.Length > 0) b.SetDisabled(true, why);
                    b.Click += (o, e) => { _rankPick = false; _dirty = true; ChangeRank(name, to); };
                    y += ModernButton.BarHeight(false) + S(4);
                }
                list.FitContent(0);
                var cancel = new ModernButton(W.T(ClientTextKeys.ModernCancel)) { Parent = parent, FontSize = 11, Size = new Size(bw * 2 + S(5), ModernButton.BarHeight(false)) };
                cancel.Location = new Point(cx, list.Location.Y + list.Size.Height + S(6));
                cancel.Click += (o, e) => { _rankPick = false; _dirty = true; };
                _cardActs.Add(cancel);
                return;
            }
            _rankPick = false;

            if (!self)
            {
                string off = W.T(ClientTextKeys.ModernGuildIsOffline, r.Name);
                string name = r.Name;
                Act(W.T(ClientTextKeys.ModernGuildWhisper), () => (GameScene.Ui as ModernUi)?.Chat?.Prefill("/" + name + " "), ButtonKind.Normal, r.Online ? null : off, W.T(ClientTextKeys.ModernGuildWhisperHint));
                bool inGroup = GameScene.Models.Group.Contains(name);
                Act(W.T(ClientTextKeys.ModernGuildGroup), () =>
                {
                    Network.Enqueue(new C.AddMember { Name = name });
                    _memberNote = W.T(ClientTextKeys.ModernGuildGroupSent, name);
                    _dirty = true;
                }, ButtonKind.Normal, !r.Online ? off : inGroup ? W.T(ClientTextKeys.ModernGuildInGroup, name) : null, W.T(ClientTextKeys.ModernGuildGroupHint));
            }
            if (editable)
            {
                int up = r.Rank - 1, down = r.Rank + 1;
                string name = r.Name;
                string upWhy = why.Length > 0 ? why : up < g.MyRankId || up < 0 ? W.T(ClientTextKeys.ModernGuildRankAtOrBelow) : null;
                string downWhy = why.Length > 0 ? why : down >= g.Ranks.Count ? W.T(ClientTextKeys.ModernGuildLowestRank) : null;
                Act(W.T(ClientTextKeys.ModernGuildPromote), () => ChangeRank(name, up), ButtonKind.Normal, upWhy,
                    up >= 0 && up < g.Ranks.Count ? W.T(ClientTextKeys.ModernGuildMoveTo, Gd.RankName(g.Ranks[up]?.Name, up)) : null);
                Act(W.T(ClientTextKeys.ModernGuildDemote), () => ChangeRank(name, down), ButtonKind.Normal, downWhy,
                    down < g.Ranks.Count ? W.T(ClientTextKeys.ModernGuildMoveTo, Gd.RankName(g.Ranks[down]?.Name, down)) : null);
                Act(W.T(ClientTextKeys.ModernGuildSetRank), () => { _rankPick = true; _dirty = true; }, ButtonKind.Normal, why.Length > 0 ? why : null, null, true);
            }
            if (self) Act(W.T(ClientTextKeys.ModernGuildLeave), Leave, ButtonKind.Warn, null, W.T(ClientTextKeys.ModernGuildLeaveHint), true);
            if (!self && Can(GuildRankOptions.CanKick) && r.Rank >= g.MyRankId)
            {
                string name = r.Name;
                Act(W.T(ClientTextKeys.ModernGuildKick), () => Kick(name), ButtonKind.Warn, why.Length > 0 ? why : null, null, true);
            }
        }

        /// <summary>GuildDialog.OnNewRank: Yes / No, then C.EditGuildMember 2 (the server moves the member and answers).</summary>
        private void ChangeRank(string name, int to)
        {
            GuildState g = G;
            if (g == null || to < 0 || to >= g.Ranks.Count || to < g.MyRankId || Throttled(_lastMemberMsg) || _pendingMember.Length > 0) return;
            GuildRank rank = g.Ranks[to];
            if (rank == null) return;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ConfirmChangeRank, name, Gd.RankName(rank.Name, rank.Index)), yes =>
            {
                if (!yes || !InGuild || Throttled(_lastMemberMsg)) return;
                Network.Enqueue(new C.EditGuildMember { ChangeType = 2, RankIndex = (byte)rank.Index, Name = name, RankName = rank.Name });
                MemberSent(name);
            }, false, PopupKind.Question, W.T(ClientTextKeys.ModernGuildChangeRankTitle));
        }

        /// <summary>GuildDialog.DeleteMember: Yes / No, then C.EditGuildMember 1.</summary>
        private void Kick(string name)
        {
            if (name == Me || Throttled(_lastMemberMsg) || _pendingMember.Length > 0) return;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ConfirmKickPlayer, name), yes =>
            {
                if (!yes || !InGuild || Throttled(_lastMemberMsg)) return;
                Network.Enqueue(new C.EditGuildMember { ChangeType = 1, Name = name });
                MemberSent(name);
            }, false, PopupKind.Danger, W.T(ClientTextKeys.ModernGuildKickTitle));
        }

        /// <summary>GuildDialog.AddMember: the name, then C.EditGuildMember 0 (the server sends the player S.GuildInvite).</summary>
        private void Invite()
        {
            if (!Can(GuildRankOptions.CanRecruit) || Throttled(_lastMemberMsg)) return;
            ModernRoot.Instance?.Popups.Input(W.T(ClientTextKeys.ModernGuildInviteTitle), string.Empty, 20, text =>
            {
                string n = text?.Trim() ?? string.Empty;
                if (n.Length == 0 || !InGuild || Throttled(_lastMemberMsg)) return;
                Network.Enqueue(new C.EditGuildMember { ChangeType = 0, Name = n });
                _lastMemberMsg = CMain.Time + MemberGapMs;
                WakeAt(_lastMemberMsg);
                _memberNote = W.T(ClientTextKeys.ModernGuildInviteSent, n);
                _dirty = true;
            }, W.T(ClientTextKeys.ModernGuildInviteText));
        }

        private void MemberSent(string name)
        {
            _lastMemberMsg = CMain.Time + MemberGapMs;
            _pendingMember = name;
            _pendingUntil = CMain.Time + PendingMs;
            WakeAt(_lastMemberMsg);
            WakeAt(_pendingUntil);
            _dirty = true;
        }

        /// <summary>"@LEAVEGUILD" after a hold-to-confirm (the server refuses at war, and the last leader must be the last member).</summary>
        private void Leave()
        {
            GuildState g = G;
            if (g == null || !InGuild) return;
            string text = W.T(ClientTextKeys.ModernGuildLeaveAsk, GameScene.User.GuildName) + (g.MyRankId == 0 ? "\n" + W.T(ClientTextKeys.ModernGuildLeaveLeader) : string.Empty);
            ModernRoot.Instance?.Popups.Confirm(text, PopupButtons.OkCancel, PopupKind.Danger, (a, _) =>
            {
                if (a == PopupAnswer.Ok && InGuild) Network.Enqueue(new C.Chat { Message = "@LEAVEGUILD" });
            }, false, W.T(ClientTextKeys.ModernGuildLeave), 900);
        }

        // ------------------------------------------------------------------ ranks

        private void BuildRanks(ModernPanel p)
        {
            int iw = Inner, ch = _ph - 10 - 48;
            new PaintBox((g, b) => Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildRanksTitle))) { Parent = p, Location = new Point(S(14), S(10)), Size = new Size(S(iw), S(ch)), NotControl = true };
            _newRankBtn = SmallButton(p, W.T(ClientTextKeys.ModernGuildNewRank), 0, 15, NewRank, W.T(ClientTextKeys.ModernGuildNewRankHint), ButtonKind.Main);
            _newRankBtn.Location = new Point(S(14 + iw - 10) - _newRankBtn.Size.Width, _newRankBtn.Location.Y);
            int tw = iw - 20;
            _rankNameW = _narrow ? 128 : 150;
            _rankColW = (tw - _rankNameW - 76 - 10) / Options.Length;
            var head = new PaintBox(PaintRankHead) { Parent = p, Location = new Point(S(24), S(44)), Size = new Size(S(tw), S(24)) };
            _rankRows = new ScrollView(tw, ch - 44 - 10) { Parent = p, Location = new Point(S(24), S(68)), Background = W.Over(ModernTheme.FrameLight, 0.05f) };
            _rankRows.Framed = false;
            _rankNoteLbl = W.Label(p, string.Empty, 14, _ph - 44, iw, 16, 11, false, ModernTheme.Accent);
            W.Label(p, W.T(ClientTextKeys.ModernGuildRanksHint), 14, _ph - 26, iw, 16, 10, false, ModernTheme.Dim);
        }

        private void PaintRankHead(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.9f))) g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height);
            Font f = ModernSkin.Font(_narrow ? 8.5f : 9.5f, true);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildColRank).ToUpperInvariant(), f, ModernTheme.Dim, new RectangleF(8 * k, 0, _rankNameW * k, b.Size.Height));
            for (int i = 0; i < Options.Length; i++)
                ModernSkin.Text(g, W.T(Options[i].Label).ToUpperInvariant(), f, ModernTheme.Dim, new RectangleF((_rankNameW + i * _rankColW) * k, 0, _rankColW * k, b.Size.Height), StringAlignment.Center);
        }

        private void RenderRanks()
        {
            GuildState g = G;
            bool full = g.Ranks.Count >= MaxRanks;
            _newRankBtn.SetDisabled(full || Throttled(_lastMemberMsg), full ? W.T(ClientTextKeys.ModernGuildNoRankSlots) : W.T(ClientTextKeys.ModernGuildWaitChange));
            _rankNoteLbl.Text = _rankNote;
            if (!_ranksDirty) return;
            _ranksDirty = false;
            for (int i = _rankRows.Content.Controls.Count - 1; i >= 0; i--) _rankRows.Content.Controls[i].Dispose();
            int w = _rankRows.InnerWidth, y = 0;
            if (g.Ranks.Count == 0)
            {
                W.Label(_rankRows.Content, W.T(ClientTextKeys.ModernGuildLoadingRanks), 8, 10, (int)(w / ModernRoot.K) - 16, 20, 12, false, ModernTheme.Dim);
                _rankRows.FitContent(0);
                return;
            }
            for (int r = 0; r < g.Ranks.Count; r++)
            {
                GuildRank rank = g.Ranks[r];
                if (rank == null) continue;
                bool editable = rank.Index >= g.MyRankId;
                string rname = Gd.RankName(rank.Name, rank.Index);
                int count = rank.Members?.Count ?? 0;
                bool odd = r % 2 == 1;
                new PaintBox((gr, b) => PaintRankRow(gr, b, rname, count, odd)) { Parent = _rankRows.Content, Location = new Point(0, y), Size = new Size(w, S(30)), NotControl = true };
                for (int i = 0; i < Options.Length; i++)
                {
                    int oi = i;
                    bool on = (rank.Options & Options[i].Bit) != 0;
                    var t = new ToggleSwitch(on, true) { Parent = _rankRows.Content };
                    t.Location = new Point(S(_rankNameW + i * _rankColW) + (S(_rankColW) - t.Size.Width) / 2, y + (S(30) - t.Size.Height) / 2);
                    t.Tooltip = W.T(Options[i].Hint);
                    if (!editable) t.Lock(true, W.T(ClientTextKeys.ModernGuildAboveYou));
                    else if (_pendingRank == rank.Index) t.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                    GuildRank rk = rank;
                    t.Changed += _ => ToggleOption(rk, oi, t);
                }
                var rename = new ModernButton(W.T(ClientTextKeys.ModernGuildRename)) { Parent = _rankRows.Content, FontSize = 10, Size = new Size(S(70), S(20)) };
                rename.Location = new Point(w - S(76), y + S(5));
                rename.Tooltip = W.T(ClientTextKeys.ModernGuildRenameHint);
                if (!editable) rename.SetDisabled(true, W.T(ClientTextKeys.ModernGuildAboveYou));
                GuildRank rr = rank;
                rename.Click += (o, e) => RenameRank(rr);
                y += S(30);
            }
            _rankRows.FitContent(0);
        }

        private void PaintRankRow(Graphics g, PaintBox b, string name, int count, bool odd)
        {
            float k = ModernRoot.K;
            if (odd) using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.04f))) g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height);
            using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.25f), 1f)) g.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
            float nw = Math.Min(ModernSkin.Measure(name, ModernSkin.Font(12, true)) + 2, (_rankNameW - 34) * k);
            ModernSkin.Text(g, name, ModernSkin.Font(12, true), Color.White, new RectangleF(8 * k, 0, nw, b.Size.Height));
            ModernSkin.Text(g, count.ToString(), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(8 * k + nw + 6 * k, 0, 30 * k, b.Size.Height));
        }

        /// <summary>GuildDialog.SwitchRankOption (ChangeType 5): "true" sets the bit, "false" clears it; the switch keeps the model's
        /// value until S.GuildMemberChange 7 answers.</summary>
        private void ToggleOption(GuildRank rank, int option, ToggleSwitch t)
        {
            GuildRankOptions bit = Options[option].Bit;
            bool on = (rank.Options & bit) != 0;
            t.Set(on);
            GuildState g = G;
            long now = CMain.Time;
            if (g == null || rank.Index < g.MyRankId || _pendingRank >= 0 || now < _lastOption) return;
            _lastOption = now + OptionGapMs;
            Network.Enqueue(new C.EditGuildMember { ChangeType = 5, RankIndex = (byte)rank.Index, Name = on ? "false" : "true", RankName = Options[option].BitIndex.ToString() });
            _pendingRank = rank.Index;
            _pendingUntil = now + PendingMs;
            WakeAt(_pendingUntil);
            t.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
        }

        /// <summary>GuildDialog.RanksChangeName (ChangeType 3): 3-20 characters, no backslash, 5 s apart.</summary>
        private void RenameRank(GuildRank rank)
        {
            GuildState g = G;
            if (g == null || rank.Index < g.MyRankId) return;
            if (Throttled(_lastRankName)) { _rankNote = W.T(ClientTextKeys.ModernGuildRenameWait); _dirty = true; return; }
            ModernRoot.Instance?.Popups.Input(W.T(ClientTextKeys.ModernGuildRenameTitle), rank.Name, 20, text =>
            {
                string n = text?.Trim() ?? string.Empty;
                if (n.Length == 0 || !InGuild || Throttled(_lastRankName)) return;
                if (n.Contains('\\') || n.Length < 3) { _rankNote = W.T(ClientTextKeys.ModernGuildRenameRule); _dirty = true; return; }
                Network.Enqueue(new C.EditGuildMember { ChangeType = 3, RankIndex = (byte)rank.Index, RankName = n });
                _lastRankName = CMain.Time + RankNameGapMs;
                _rankNote = W.T(ClientTextKeys.ModernGuildRenaming);
                _dirty = true;
            }, W.T(ClientTextKeys.ModernGuildRenameText, Gd.RankName(rank.Name, rank.Index)));
        }

        /// <summary>GuildDialog.OnRankSelect "Add New" (ChangeType 4 with "Rank-N").</summary>
        private void NewRank()
        {
            GuildState g = G;
            if (g == null || g.Ranks.Count >= MaxRanks || Throttled(_lastMemberMsg)) return;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ConfirmCreateNewRank), yes =>
            {
                if (!yes || !InGuild || Throttled(_lastMemberMsg)) return;
                Network.Enqueue(new C.EditGuildMember { ChangeType = 4, RankName = W.T(ClientTextKeys.RankTitle, g.Ranks.Count - 1) });
                _lastMemberMsg = CMain.Time + MemberGapMs;
                WakeAt(_lastMemberMsg);
                _rankNote = W.T(ClientTextKeys.ModernGuildAddingRank);
                _dirty = true;
            }, false, PopupKind.Question, W.T(ClientTextKeys.ModernGuildNewRank));
        }

        // ------------------------------------------------------------------ buffs

        private void BuildBuffs(ModernPanel p)
        {
            int iw = Inner;
            _buffHead = new PaintBox(PaintBuffHead) { Parent = p, Location = new Point(S(14), S(10)), Size = new Size(S(iw), S(22)), NotControl = true };
            _buffGrid = new ScrollView(iw, _ph - 40 - 34) { Parent = p, Location = new Point(S(14), S(38)), Background = W.Page };
            _buffGrid.Framed = false;
            _buffNoteLbl = W.Label(p, string.Empty, 14, _ph - 28, iw, 16, 11, false, ModernTheme.Accent);
        }

        private void PaintBuffHead(Graphics g, PaintBox b)
        {
            GuildState gs = G;
            if (gs == null) return;
            float x = 0;
            Font f = ModernSkin.Font(12), fb = ModernSkin.Font(12, true);
            void Seg(string t, Font font, Color c)
            {
                float tw = ModernSkin.Measure(t, font);
                ModernSkin.Text(g, t, font, c, new RectangleF(x, 0, tw + 2, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                x += tw;
            }
            Seg(W.T(ClientTextKeys.ModernGuildBuffHeadLevel, gs.Level) + " · ", f, ModernTheme.Dim);
            Seg(W.T(gs.SparePoints == 1 ? ClientTextKeys.ModernGuildOnePoint : ClientTextKeys.ModernGuildNPoints, gs.SparePoints), fb, ModernTheme.Text);
            Seg(" " + W.T(ClientTextKeys.ModernGuildToSpendFund) + " ", f, ModernTheme.Dim);
            Seg(W.N(gs.Gold), fb, ModernTheme.Gold);
            Seg(" " + W.T(ClientTextKeys.ModernGuildGoldWord), f, ModernTheme.Dim);
        }

        private void RenderBuffs()
        {
            _buffHead.Invalidate();
            _buffNoteLbl.Text = _buffNote;
            if (!_buffsDirty) return;
            _buffsDirty = false;
            _buffTimers.Clear();
            for (int i = _buffGrid.Content.Controls.Count - 1; i >= 0; i--) _buffGrid.Content.Controls[i].Dispose();
            GuildState g = G;
            int cols = _narrow ? 1 : 2, gap = S(8), w = (_buffGrid.InnerWidth - gap * (cols - 1)) / cols, h = S(_narrow ? 60 : 72);
            for (int i = 0; i < g.BuffInfos.Count; i++)
            {
                GuildBuffInfo info = g.BuffInfos[i];
                if (info == null) continue;
                BuffRow(info, new Point((i % cols) * (w + gap), (i / cols) * (h + gap)), new Size(w, h));
            }
            if (g.BuffInfos.Count == 0)
                W.Label(_buffGrid.Content, W.T(ClientTextKeys.ModernGuildNoBuffs), 0, 20, (int)(_buffGrid.InnerWidth / ModernRoot.K), 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _buffGrid.FitContent(0);
        }

        /// <summary>One GuildBuffButton: icon (GuildDialog's GuildSkill rule), name, stats, requirements, state and action.</summary>
        private void BuffRow(GuildBuffInfo info, Point at, Size size)
        {
            GuildState g = G;
            GuildBuff buff = g.FindBuff(info.Id);
            bool active = buff != null && buff.Active;
            int icon = buff == null ? info.Icon + 2 : active ? info.Icon + 1 : info.Icon;
            var fx = new List<string>();
            if (info.Stats != null)
                foreach (KeyValuePair<Stat, int> s in info.Stats.Values)
                {
                    if (s.Value == 0) continue;
                    fx.Add(s.Key.ToLocalizedString() + " " + W.Signed(s.Value) + (s.Key.ToString().Contains("Percent") ? "%" : string.Empty));
                }
            var req = new List<string>();
            if (info.LevelRequirement > 0) req.Add(W.T(ClientTextKeys.ModernGuildReqLevel, info.LevelRequirement));
            if (info.PointsRequirement > 0) req.Add(W.T(info.PointsRequirement == 1 ? ClientTextKeys.ModernGuildOnePoint : ClientTextKeys.ModernGuildNPoints, info.PointsRequirement));
            if (info.ActivationCost > 0) req.Add(W.T(ClientTextKeys.ModernGuildReqCost, W.N(info.ActivationCost)));
            req.Add(info.TimeLimit > 0 ? W.T(ClientTextKeys.ModernGuildReqLasts, info.TimeLimit) : W.T(ClientTextKeys.ModernGuildPermanent));
            string fxText = string.Join(" · ", fx), reqText = string.Join(" · ", req), name = info.Name ?? string.Empty;

            string state, stateSub, btnText = null, why = string.Empty;
            bool main = false, low = info.LevelRequirement > g.Level;
            if (buff == null)
            {
                state = low ? W.T(ClientTextKeys.ModernGuildLocked) : "—";
                stateSub = low ? W.T(ClientTextKeys.ModernGuildLevelLow) : W.T(ClientTextKeys.ModernGuildNotObtained);
                btnText = W.T(ClientTextKeys.ModernGuildObtain);
                why = !Can(GuildRankOptions.CanActivateBuff) ? W.T(ClientTextKeys.GuildRankNoBuffActivation) : g.SparePoints < info.PointsRequirement ? W.T(ClientTextKeys.InsufficientPointsAvailable) : low ? W.T(ClientTextKeys.GuildLevelTooLow) : string.Empty;
            }
            else if (active)
            {
                state = info.TimeLimit > 0 ? string.Empty : W.T(ClientTextKeys.ModernGuildActive);
                stateSub = info.TimeLimit > 0 ? W.T(ClientTextKeys.ModernGuildRemaining) : W.T(ClientTextKeys.ModernGuildObtained);
            }
            else
            {
                state = W.T(ClientTextKeys.ModernGuildReady);
                stateSub = W.T(ClientTextKeys.ModernGuildObtained);
                btnText = W.T(ClientTextKeys.ModernGuildActivate);
                main = true;
                why = !Can(GuildRankOptions.CanActivateBuff) ? W.T(ClientTextKeys.GuildRankNoBuffActivation) : g.Gold < info.ActivationCost ? W.T(ClientTextKeys.GuildFundsInsufficient) : string.Empty;
            }
            if (btnText != null && _pendingBuff == info.Id) why = W.T(ClientTextKeys.ModernWinWaitingServer);
            int btnW = btnText != null ? S(74) : 0;
            int id = info.Id;
            bool timed = active && info.TimeLimit > 0;
            var box = new PaintBox((gr, b) => PaintBuffRow(gr, b, name, fxText, reqText, state, stateSub, active, timed ? id : -1, btnW))
            { Parent = _buffGrid.Content, Location = at, Size = size, NotControl = true };
            box.AfterPaint = b =>
            {
                int s = S(36);
                Rectangle r = new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + (b.Size.Height - s) / 2, s, s);
                if (Libraries.GuildSkill != null && icon >= 0)
                    Hud.HudKit.DrawScaled(Libraries.GuildSkill, icon, new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), Color.White);
            };
            if (timed) _buffTimers.Add((box, id));
            if (btnText != null)
            {
                var btn = new ModernButton(btnText, main ? ButtonKind.Main : ButtonKind.Normal) { Parent = _buffGrid.Content, FontSize = 11, Size = new Size(btnW, S(24)) };
                btn.Location = new Point(at.X + size.Width - S(10) - btnW, at.Y + (size.Height - btn.Size.Height) / 2);
                btn.Tooltip = buff == null ? W.T(info.PointsRequirement == 1 ? ClientTextKeys.ModernGuildSpendOne : ClientTextKeys.ModernGuildSpendN, info.PointsRequirement)
                    : info.ActivationCost > 0 ? W.T(ClientTextKeys.ModernGuildCostsFund, W.N(info.ActivationCost)) : string.Empty;
                if (why.Length > 0) btn.SetDisabled(true, why);
                btn.Click += (o, e) => RequestBuff(info);
            }
        }

        private void PaintBuffRow(Graphics g, PaintBox b, string name, string fx, string req, string state, string stateSub, bool active, int timerId, int btnW)
        {
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            W.Card(g, b.Size, active ? ModernTheme.Accent : (Color?)null);
            RectangleF icon = new RectangleF(10 * k, (h - 36 * k) / 2f, 36 * k, 36 * k);
            using (GraphicsPath p = ModernSkin.RoundRect(icon, ModernTheme.Radius * 0.8f * k))
            {
                using (var br = new SolidBrush(Color.Black)) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            float sw = 70 * k, sx = w - 10 * k - btnW - (btnW > 0 ? 8 * k : 0) - sw;
            float mx = 56 * k, mw = sx - mx - 6 * k, y = 8 * k;
            ModernSkin.Text(g, name, ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(mx, y, mw, 16 * k));
            y += 16 * k;
            if (fx.Length > 0)
            {
                float fh = Math.Min(h - y - 16 * k, ModernSkin.MeasureWrapped(fx, ModernSkin.Font(10), mw).Height + 1);
                ModernSkin.Text(g, fx, ModernSkin.Font(10), ModernTheme.AccentDim, new RectangleF(mx, y, mw, fh), StringAlignment.Near, StringAlignment.Near, true, true);
                y += fh;
            }
            ModernSkin.Text(g, req, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(mx, y, mw, h - y - 4 * k), StringAlignment.Near, StringAlignment.Near, true, true);
            if (timerId >= 0)
            {
                GuildBuff buff = G?.FindBuff(timerId);
                state = Gd.Clock(_clock?.Left(buff) ?? 0);
            }
            ModernSkin.Text(g, state, ModernSkin.Font(12, true), ModernTheme.Accent, new RectangleF(sx, h / 2f - 16 * k, sw, 16 * k), StringAlignment.Far);
            ModernSkin.Text(g, stateSub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(sx, h / 2f, sw, 14 * k), StringAlignment.Far);
        }

        /// <summary>GuildDialog.RequestBuff: C.GuildBuffUpdate 1 (obtain) or 2 (activate); the row waits for S.GuildBuffList.</summary>
        private void RequestBuff(GuildBuffInfo info)
        {
            long now = CMain.Time;
            GuildState g = G;
            if (g == null || _pendingBuff >= 0 || now < _lastBuff + BuffGapMs) return;
            _lastBuff = now;
            GuildBuff buff = g.FindBuff(info.Id);
            Network.Enqueue(new C.GuildBuffUpdate { Action = (byte)(buff != null ? 2 : 1), Id = info.Id });
            _pendingBuff = info.Id;
            _pendingUntil = now + PendingMs;
            WakeAt(_pendingUntil);
            _buffNote = string.Empty;
            _buffsDirty = true;
            _dirty = true;
        }

        // ------------------------------------------------------------------ storage

        private void BuildStorage(ModernPanel p)
        {
            int iw = Inner, size = _narrow ? 36 : 40, pitch = size + 4, gw = 14 * pitch - 4;
            int ch = 30 + 8 * pitch + 6 + 34;
            _storeCard = new PaintBox(PaintStoreCard) { Parent = p, Location = new Point(S(14), S(10)), Size = new Size(S(iw), S(ch)), NotControl = true };
            int gx = 14 + (iw - gw) / 2, gy = 10 + 30;
            for (int i = 0; i < StorageSlots; i++)
                _cells[i] = new ModernItemCell(MirGridType.GuildStorage, i, size) { Parent = p, Location = new Point(S(gx + i % 14 * pitch), S(gy + i / 14 * pitch)) };
            int my = gy + 8 * pitch + 6;
            _depositBtn = SmallButton(p, W.T(ClientTextKeys.Deposit), gx, my, () => Gold(0), W.T(ClientTextKeys.ModernGuildDepositHint), ButtonKind.Main, 70);
            _withdrawBtn = SmallButton(p, W.T(ClientTextKeys.ModernGuildWithdraw), 0, my, () => Gold(1), W.T(ClientTextKeys.ModernGuildWithdrawHint), ButtonKind.Normal, 70);
            _withdrawBtn.Location = new Point(_depositBtn.Location.X + _depositBtn.Size.Width + S(6), _depositBtn.Location.Y);
            _fundBox = new PaintBox(PaintFund) { Parent = p, Location = new Point(S(gx + gw - 220), S(my)), Size = new Size(S(220), S(22)), NotControl = true };
            int nx = gx + 170;
            _storeNoteLbl = W.Label(p, string.Empty, nx, my + 3, gw - 170 - 226, 16, 11, false, ModernTheme.Accent);
            int hy = 10 + ch + 6;
            if (hy + 28 <= _ph) W.Label(p, W.T(ClientTextKeys.ModernGuildStorageHint), 14, hy, iw, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        private void PaintStoreCard(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildStorageTitle));
            int used = 0;
            for (int i = 0; i < StorageSlots && i < GameScene.GuildStorage.Length; i++) if (GameScene.GuildStorage[i] != null) used++;
            ModernSkin.Text(g, used + " / " + StorageSlots, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(12 * k, 6 * k, b.Size.Width - 24 * k, 16 * k), StringAlignment.Far);
        }

        private void PaintFund(Graphics g, PaintBox b)
        {
            GuildState gs = G;
            if (gs == null) return;
            float x = b.Size.Width;
            Font f = ModernSkin.Font(12), fb = ModernSkin.Font(14, true);
            string a = W.T(ClientTextKeys.ModernGuildFundWord) + " ", v = W.N(gs.Gold), c = " " + W.T(ClientTextKeys.ModernGuildGoldWord);
            float wa = ModernSkin.Measure(a, f), wv = ModernSkin.Measure(v, fb), wc = ModernSkin.Measure(c, f);
            x -= wa + wv + wc + 4;
            ModernSkin.Text(g, a, f, ModernTheme.Dim, new RectangleF(x, 0, wa + 2, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
            ModernSkin.Text(g, v, fb, ModernTheme.Gold, new RectangleF(x + wa, 0, wv + 2, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
            ModernSkin.Text(g, c, f, ModernTheme.Dim, new RectangleF(x + wa + wv, 0, wc + 2, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
        }

        private void RenderStorage()
        {
            GuildState g = G;
            _goldSeen = GameScene.Gold;
            _storeCard.Invalidate();
            _fundBox.Invalidate();
            _storeNoteLbl.Text = _storeNote;
            _withdrawBtn.Visible = g.MyRankId == 0; // GuildDialog: StorageGoldRemove only for the leader rank
            _depositBtn.SetDisabled(GameScene.Gold == 0, W.T(ClientTextKeys.ModernGuildNoGold));
            _withdrawBtn.SetDisabled(g.Gold == 0, W.T(ClientTextKeys.ModernGuildFundEmpty));
            for (int i = 0; i < StorageSlots; i++) _cells[i]?.Refresh(false);
        }

        /// <summary>GuildDialog.StorageAddGold / StorageRemoveGold: an amount, then C.GuildStorageGoldChange (gold moves when the server
        /// answers with S.LoseGold / S.GainedGold and S.GuildStorageGoldChange).</summary>
        private void Gold(byte type)
        {
            GuildState g = G;
            if (g == null || Throttled(_lastGold)) return;
            uint max = type == 0 ? GameScene.Gold : g.Gold;
            if (max == 0 || (type == 1 && g.MyRankId != 0)) return;
            ModernRoot.Instance?.Popups.Amount(type == 0 ? W.T(ClientTextKeys.Deposit) : W.T(ClientTextKeys.GoldToRetrieve), -1, max, amount =>
            {
                if (amount == 0 || !InGuild) return;
                _lastGold = CMain.Time + GoldGapMs;
                Network.Enqueue(new C.GuildStorageGoldChange { Type = type, Amount = amount });
                _storeNote = W.T(type == 0 ? ClientTextKeys.ModernGuildDepositing : ClientTextKeys.ModernGuildWithdrawing, W.N(amount));
                _dirty = true;
            });
        }

        // ------------------------------------------------------------------ wars

        private void BuildWars(ModernPanel p)
        {
            int iw = Inner, ch = _ph - 10 - 40;
            _warCard = new PaintBox((g, b) => Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildAtWar, G?.Wars.Count ?? 0))) { Parent = p, Location = new Point(S(14), S(10)), Size = new Size(S(iw), S(ch)), NotControl = true };
            _warRows = new ScrollView(iw - 20, ch - 40) { Parent = p, Location = new Point(S(24), S(40)), Background = W.Over(ModernTheme.FrameLight, 0.05f) };
            _warRows.Framed = false;
            W.Label(p, W.T(ClientTextKeys.ModernGuildWarsHint), 14, _ph - 30, iw, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        private void RenderWars()
        {
            if (!_warsDirty) return;
            _warsDirty = false;
            _warCard.Invalidate();
            _warTimers.Clear();
            for (int i = _warRows.Content.Controls.Count - 1; i >= 0; i--) _warRows.Content.Controls[i].Dispose();
            List<GuildWarEntry> wars = G.Wars;
            int w = _warRows.InnerWidth, y = 0;
            for (int i = 0; i < wars.Count; i++)
            {
                GuildWarEntry war = wars[i];
                bool last = i == wars.Count - 1;
                var row = new PaintBox((g, b) => PaintWarRow(g, b, war, last)) { Parent = _warRows.Content, Location = new Point(0, y), Size = new Size(w, S(38)), NotControl = true };
                row.AfterPaint = b =>
                {
                    int s = S(16);
                    W.DrawIcon(ModernIcons.Kill, new Rectangle(b.DisplayLocation.X + S(4), b.DisplayLocation.Y + (b.Size.Height - s) / 2, s, s), ModernTheme.Bad);
                };
                _warTimers.Add((row, i));
                y += S(38);
            }
            if (wars.Count == 0)
                W.Label(_warRows.Content, W.T(ClientTextKeys.ModernGuildNotAtWar), 4, 8, (int)(w / ModernRoot.K) - 8, 20, 12, false, ModernTheme.Dim);
            _warRows.FitContent(0);
        }

        private static void PaintWarRow(Graphics g, PaintBox b, GuildWarEntry war, bool last)
        {
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            if (!last) using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.3f), 1f)) g.DrawLine(pen, 0, h - 0.5f, w, h - 0.5f);
            long left = GuildState.WarTimeLeft(war);
            string timer = left > 0 ? W.T(ClientTextKeys.ModernGuildEndsIn, Gd.Clock(left)) : W.T(ClientTextKeys.ModernGuildEnding);
            float tw = ModernSkin.Measure(timer, ModernSkin.Font(11)) + 4;
            ModernSkin.Text(g, war.GuildName, ModernSkin.Font(12, true), Color.White, new RectangleF(30 * k, 0, w - 40 * k - tw, h));
            ModernSkin.Text(g, timer, ModernSkin.Font(11), ModernTheme.AccentDim, new RectangleF(w - tw - 4 * k, 0, tw, h), StringAlignment.Far);
        }

        // ------------------------------------------------------------------ log

        private void BuildLog(ModernPanel p)
        {
            int iw = Inner;
            var choices = new List<Choice<LogFilter>>();
            for (int i = 0; i < LogFilters.Length; i++) choices.Add(new Choice<LogFilter>(LogFilters[i].F, W.T(LogFilters[i].Name)));
            var chips = W.At(new Chips<LogFilter>(choices, new[] { _logFilter }, iw - 110, false, false), p, 14, 12);
            chips.Changed += s => { foreach (LogFilter f in s) _logFilter = f; _logDirty = true; _dirty = true; };
            var refresh = SmallButton(p, W.T(ClientTextKeys.ModernEcoRefresh), 0, 11, () =>
            {
                if (G == null || !G.RequestLog()) { _logNote = W.T(ClientTextKeys.ModernGuildTryAgain); _dirty = true; }
            });
            refresh.Location = new Point(S(14 + iw) - refresh.Size.Width, refresh.Location.Y);
            int top = 12 + (int)Math.Ceiling(chips.Size.Height / ModernRoot.K) + 10, ch = _ph - top - 34;
            new PaintBox((g, b) => Gd.CardWithHeader(g, b.Size, W.T(ClientTextKeys.ModernGuildActivity))) { Parent = p, Location = new Point(S(14), S(top)), Size = new Size(S(iw), S(ch)), NotControl = true };
            _logRows = new ScrollView(iw - 20, ch - 34) { Parent = p, Location = new Point(S(24), S(top + 28)), Background = W.Over(ModernTheme.FrameLight, 0.05f) };
            _logRows.Framed = false;
            _logNoteLbl = W.Label(p, string.Empty, 14, _ph - 28, iw, 16, 11, false, ModernTheme.Accent);
        }

        private void RenderLog()
        {
            _logNoteLbl.Text = _logNote;
            if (!_logDirty) return;
            _logDirty = false;
            for (int i = _logRows.Content.Controls.Count - 1; i >= 0; i--) _logRows.Content.Controls[i].Dispose();
            List<ClientGuildLogEntry> log = G.Log;
            int w = _logRows.InnerWidth, y = 0, n = 0;
            Font f = ModernSkin.Font(12);
            for (int i = log.Count - 1; i >= 0; i--)
            {
                ClientGuildLogEntry e = log[i];
                if (_logFilter != LogFilter.All && FilterOf(e.Kind) != _logFilter) continue;
                string ago = AgoBinary(e.Time), text = e.Text ?? string.Empty;
                float aw = ModernSkin.Measure(ago, ModernSkin.Font(10)) + 4;
                int th = (int)Math.Ceiling(ModernSkin.MeasureWrapped(text, f, w - S(30) - aw - S(6)).Height);
                int h = Math.Max(S(30), th + S(12));
                int icon = LogIcon(e.Kind);
                var row = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    ModernSkin.Text(g, text, f, ModernTheme.Text, new RectangleF(26 * k, 6 * k, b.Size.Width - 26 * k - aw - 6 * k, b.Size.Height - 12 * k), StringAlignment.Near, StringAlignment.Center, true, true);
                    ModernSkin.Text(g, ago, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(b.Size.Width - aw, 0, aw, b.Size.Height), StringAlignment.Far);
                    using var pen = new Pen(W.Over(ModernTheme.Frame, 0.3f), 1f);
                    g.DrawLine(pen, 0, b.Size.Height - 0.5f, b.Size.Width, b.Size.Height - 0.5f);
                }) { Parent = _logRows.Content, Location = new Point(0, y), Size = new Size(w, h), NotControl = true };
                row.AfterPaint = b =>
                {
                    int s = S(14);
                    W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(4), b.DisplayLocation.Y + (b.Size.Height - s) / 2, s, s), ModernTheme.AccentDim);
                };
                y += h;
                n++;
            }
            if (n == 0)
                W.Label(_logRows.Content, W.T(_logFilter == LogFilter.All ? ClientTextKeys.ModernGuildNoActivity : ClientTextKeys.ModernGuildNothingOfKind), 4, 8, (int)(w / ModernRoot.K) - 8, 20, 12, false, ModernTheme.Dim);
            _logRows.FitContent(0);
        }
    }
}
