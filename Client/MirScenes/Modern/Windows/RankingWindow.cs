// Ported from WebClient/src/ui/modern/windows/ranking.ts (RankingWindow): the new UI's ranking window over the desktop's rankings
// model (GameScene.Models.Rankings: S.Rankings per RankType, the S.RankingsDetail extras, the S.GuildRankings board). Classic
// reference: RankingDialog (Client/MirScenes/Dialogs/RankingDialog.cs); its request rules are kept: a tab or the Online Only
// switch asks at once (RankIndex 0), paging asks 0.5 s after the last move (NextRequestTime); RankIndex = the first row; rank
// labels = RankIndex + row + 1.
//  - the boards: RankType 0 = overall, 1..5 = one per class (C.GetRanking -> S.Rankings, 20 rows from RankIndex; Count = the
//    board's size), the server's Online Only flag, Prev / Next pages of 20; the shown rows keep the RankIndex of the request
//    that brought them, so labels never run ahead of the data;
//  - rows enriched with S.RankingsDetail (guild, online pip, EXP to the next level), blank until it arrives; the podium shows
//    the board's first three while its first page is shown;
//  - "Your rank" = S.Rankings.MyRank (0 = not listed); "Find me" asks for the page around it and selects your row;
//  - the player card: class, guild, online, the rank on this board, level / EXP; Inspect = C.Inspect with Ranking = true
//    (RankingRow.Inspect; a double-click on a row too), Whisper (the chat input), Add friend (C.AddFriend), Invite (C.AddMember);
//  - the Guilds tab is the server's guild board (C.GetGuildRanking -> S.GuildRankings, 20 a page, by level then EXP).
// HIDDEN (Crystal keeps no history or seasons): movement arrows, sparkline, season countdown, the whole-board search.
// Desktop: the mouse wheel scrolls the 20 rows (the web moves the board by 3 rows a notch); below a 1040 px wide screen the
// web's narrow layer (no podium, a narrower card).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class RankingWindow : ModernWindow
    {
        public const string WindowKey = "ranking";
        private const int H = 580, Rows = 20, RequestDelayMs = 500, GuildTab = RankingsState.RankTypes, GuildPageSize = 20;

        private static readonly Color P1 = Color.FromArgb(255, 0xf2, 0xc9, 0x4c), P2 = Color.FromArgb(255, 0xc9, 0xd3, 0xe0), P3 = Color.FromArgb(255, 0xd0, 0x8a, 0x4a);

        private int _board;
        private bool _onlineOnly, _findMe, _dirty = true;
        private readonly int[] _offset = new int[RankingsState.RankTypes], _sentOffset = new int[RankingsState.RankTypes], _shownOffset = new int[RankingsState.RankTypes];
        private readonly List<RankCharacterInfo>[] _lastList = new List<RankCharacterInfo>[RankingsState.RankTypes];
        private long _picked = -1, _requestAt, _lastClickId = -1, _lastClickAt;
        private int _guildPage;
        private bool _narrow;
        private int _w, _sideW, _boardW, _podH;

        private Tabs _tabs;
        private ModernPanel _playerBoard, _guildBoard, _side;
        private PaintBox _podium, _pin, _pageText, _gPageText;
        private ToggleSwitch _online;
        private ModernButton _findBtn, _prevBtn, _nextBtn, _gPrev, _gNext;
        private ScrollView _rows, _gRows;
        private readonly PaintBox[] _rowBoxes = new PaintBox[Rows];
        private readonly PaintBox[] _gRowBoxes = new PaintBox[GuildPageSize];

        private readonly Action _onChanged, _onOther;
        private readonly Action<byte> _onDetails;
        private readonly Action<int> _onGuildBoard;

        public RankingWindow() : base(WindowKey, W.T(ClientTextKeys.ModernGuildRankTitle), 62, 30)
        {
            _onChanged = Received;
            _onOther = () => _dirty = true;
            _onDetails = _ => _dirty = true;
            _onGuildBoard = page =>
            {
                if (_board == GuildTab && page != _guildPage) _guildPage = page;
                _dirty = true;
            };
        }

        private static RankingsState R => GameScene.Models.Rankings;
        private static string Me => GameScene.User?.Name ?? string.Empty;

        private static string TabName(int i) => i == 0 ? W.T(ClientTextKeys.ModernGuildRankOverall) : i <= 5 ? Gd.ClassName((MirClass)(i - 1)) : W.T(ClientTextKeys.ModernGuildRankGuilds);

        // test hooks
        internal int Board => _board;
        internal int Offset => _board < GuildTab ? _offset[_board] : _guildPage;
        internal long Picked => _picked;
        internal Tabs TabsControl => _tabs;
        internal PaintBox Row(int i) => i >= 0 && i < Rows ? _rowBoxes[i] : null;
        internal ModernButton NextButton => _nextBtn;
        internal ModernButton FindButton => _findBtn;
        internal ModernPanel Side => _side;
        internal ToggleSwitch OnlineSwitch => _online;

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            _w = _narrow ? 720 : 900;
            _sideW = _narrow ? 200 : 250;
            _boardW = _w - _sideW - 1;
            _podH = _narrow ? 0 : 132;
            ContentWidth = _w;
            ContentHeight = H;
            var m = new ModernPanel { Parent = this, Location = new Point(0, S(TitleHeight)), Size = new Size(S(_w), S(H)) };
            var names = new List<string>();
            for (int i = 0; i <= GuildTab; i++) names.Add(TabName(i));
            _tabs = W.At(new Tabs(names, _board, _w - 28), m, 14, 8);
            _tabs.Changed += SelectBoard;
            W.Divider(m, 0, 40, _w);
            int top = 41, bh = H - top;

            // ---- player boards
            _playerBoard = new ModernPanel { Parent = m, Location = new Point(0, S(top)), Size = new Size(S(_boardW), S(bh)) };
            int bw = _boardW - 24, y = 10;
            if (_podH > 0)
            {
                _podium = new PaintBox(PaintPodium) { Parent = _playerBoard, Location = new Point(S(12), S(y)), Size = new Size(S(bw), S(_podH)) };
                _podium.Click += (o, e) => PodiumClick();
                y += _podH + 8;
            }
            _online = W.At(new ToggleSwitch(_onlineOnly, true), _playerBoard, 12, y + 5);
            _online.Changed += on =>
            {
                _onlineOnly = on;
                if (_board < GuildTab) _offset[_board] = 0;
                RequestNow(); // the desktop's check box: RowOffset 0, request at once
            };
            W.Label(_playerBoard, W.T(ClientTextKeys.ModernGuildRankOnlineOnly), 48, y + 5, 90, 16, 11, false, ModernTheme.Dim);
            _findBtn = new ModernButton(W.T(ClientTextKeys.ModernGuildRankFindMe)) { Parent = _playerBoard, Location = new Point(S(140), S(y + 2)), FontSize = 11, Tooltip = W.T(ClientTextKeys.ModernGuildRankFindHint) };
            _findBtn.AutoWidth(60);
            _findBtn.Click += (o, e) => FindMine();
            _nextBtn = Arrow(_playerBoard, "›", 12 + bw - 26, y + 2, () => Page(1), W.T(ClientTextKeys.ModernGuildRankNext));
            _prevBtn = Arrow(_playerBoard, "‹", 12 + bw - 54, y + 2, () => Page(-1), W.T(ClientTextKeys.ModernGuildRankPrev));
            _pageText = new PaintBox((g, b) => PaintPager(g, b, PlayerPagerText())) { Parent = _playerBoard, Location = new Point(S(12 + bw - 230), S(y + 2)), Size = new Size(S(170), S(22)), NotControl = true };
            y += 32;
            new PaintBox((g, b) => PaintHead(g, b, false)) { Parent = _playerBoard, Location = new Point(S(12), S(y)), Size = new Size(S(bw), S(16)), NotControl = true };
            y += 18;
            int rowsH = bh - y - 38;
            _rows = new ScrollView(bw, rowsH) { Parent = _playerBoard, Location = new Point(S(12), S(y)), Background = W.Page };
            _rows.Framed = false;
            int rw = _rows.InnerWidth;
            for (int i = 0; i < Rows; i++)
            {
                int idx = i;
                _rowBoxes[i] = new PaintBox((g, b) => PaintRow(g, b, idx)) { Parent = _rows.Content, Location = new Point(0, S(i * 30)), Size = new Size(rw, S(30)), Visible = false };
                _rowBoxes[i].Click += (o, e) => RowClick(idx);
            }
            _rows.ContentHeight = S(Rows * 30);
            _pin = new PaintBox(PaintPin) { Parent = _playerBoard, Location = new Point(S(12), S(bh - 34)), Size = new Size(S(bw), S(28)), NotControl = true };

            // ---- guild board
            _guildBoard = new ModernPanel { Parent = m, Location = new Point(0, S(top)), Size = new Size(S(_boardW), S(bh)), Visible = false };
            W.Label(_guildBoard, W.T(ClientTextKeys.ModernGuildRankGuildsHint), 12, 14, bw - 260, 16, 11, false, ModernTheme.Dim);
            _gNext = Arrow(_guildBoard, "›", 12 + bw - 26, 12, () => GuildMove(1), W.T(ClientTextKeys.ModernGuildRankNext));
            _gPrev = Arrow(_guildBoard, "‹", 12 + bw - 54, 12, () => GuildMove(-1), W.T(ClientTextKeys.ModernGuildRankPrev));
            _gPageText = new PaintBox((g, b) => PaintPager(g, b, GuildPagerText())) { Parent = _guildBoard, Location = new Point(S(12 + bw - 230), S(12)), Size = new Size(S(170), S(22)), NotControl = true };
            new PaintBox((g, b) => PaintHead(g, b, true)) { Parent = _guildBoard, Location = new Point(S(12), S(44)), Size = new Size(S(bw), S(16)), NotControl = true };
            _gRows = new ScrollView(bw, bh - 62 - 10) { Parent = _guildBoard, Location = new Point(S(12), S(62)), Background = W.Page };
            _gRows.Framed = false;
            for (int i = 0; i < GuildPageSize; i++)
            {
                int idx = i;
                _gRowBoxes[i] = new PaintBox((g, b) => PaintGuildRow(g, b, idx)) { Parent = _gRows.Content, Location = new Point(0, S(i * 34)), Size = new Size(_gRows.InnerWidth, S(34)), Visible = false, NotControl = true };
            }
            _gRows.ContentHeight = S(GuildPageSize * 34);

            W.VDivider(m, _boardW, top, bh);
            _side = new ModernPanel { Parent = m, Location = new Point(S(_boardW + 1), S(top)), Size = new Size(S(_sideW), S(bh)) };
            _dirty = true;
            Render();
        }

        private static ModernButton Arrow(MirControl parent, string text, int x, int y, Action run, string hint)
        {
            var b = new ModernButton(text) { Parent = parent, Location = new Point(S(x), S(y)), Size = new Size(S(26), S(22)), FontSize = 15, Tooltip = hint };
            b.Click += (o, e) => run();
            return b;
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            RankingsState r = R;
            r.Changed.On(_onChanged);
            r.DetailsChanged.On(_onDetails);
            r.GuildBoardChanged.On(_onGuildBoard);
            GameScene.Models.Guild.Changed.On(_onOther);
            GameScene.Models.Friends.Changed.On(_onOther);
            GameScene.Models.Group.Changed.On(_onOther);
            // RankingDialog.Show: request the current tab
            if (_board == GuildTab) RankingsState.RequestGuildPage(_guildPage);
            else RequestNow();
            _dirty = true;
        }

        protected override void Closed()
        {
            RankingsState r = R;
            r.Changed.Off(_onChanged);
            r.DetailsChanged.Off(_onDetails);
            r.GuildBoardChanged.Off(_onGuildBoard);
            GameScene.Models.Guild.Changed.Off(_onOther);
            GameScene.Models.Friends.Changed.Off(_onOther);
            GameScene.Models.Group.Changed.Off(_onOther);
            _requestAt = 0;
        }

        public override void Process()
        {
            base.Process();
            if (_requestAt > 0 && CMain.Time >= _requestAt) RequestNow(); // RankingDialog.Process: NextRequestTime
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ requests

        /// <summary>RankingDialog.RequestRanks for the current tab at its offset.</summary>
        private void RequestNow()
        {
            _requestAt = 0;
            int t = _board;
            if (t >= GuildTab) return;
            _sentOffset[t] = _offset[t];
            RankingsState.Request((byte)t, _offset[t], _onlineOnly);
            _dirty = true; // the pager shows the wanted range at once
        }

        /// <summary>S.Rankings applied: the changed lists take the offset of their request.</summary>
        private void Received()
        {
            RankingsState r = R;
            for (int t = 0; t < RankingsState.RankTypes; t++)
            {
                List<RankCharacterInfo> l = r.List(t);
                if (l != _lastList[t])
                {
                    _lastList[t] = l;
                    _shownOffset[t] = _sentOffset[t];
                }
            }
            _dirty = true;
        }

        /// <summary>RankingDialog.SelectRank (+ the guild tab).</summary>
        private void SelectBoard(int i)
        {
            if (i < 0 || i > GuildTab) return;
            _board = i;
            _picked = -1;
            _findMe = false;
            if (i == GuildTab)
            {
                _requestAt = 0;
                RankingsState.RequestGuildPage(_guildPage);
            }
            else
            {
                _offset[i] = 0;
                RequestNow();
            }
            _rows?.ScrollTo(0);
            _dirty = true;
        }

        private int MaxOffset() => Math.Max(0, R.Count - Rows);

        /// <summary>RankingDialog.Move: the range moves now, the request follows 0.5 s after the last move.</summary>
        private void Move(int rows)
        {
            int t = _board;
            if (t >= GuildTab) return;
            int next = Math.Min(MaxOffset(), Math.Max(0, _offset[t] + rows));
            if (next == _offset[t]) return;
            _offset[t] = next;
            _requestAt = CMain.Time + RequestDelayMs;
            _dirty = true;
        }

        private void Page(int dir) => Move(dir * Rows);

        /// <summary>"Find me": the page around the user's rank; the row is selected when it arrives.</summary>
        private void FindMine()
        {
            int t = _board;
            if (t >= GuildTab) return;
            int mine = R.MyRank(t);
            if (mine <= 0) return;
            _offset[t] = Math.Min(MaxOffset(), Math.Max(0, mine - 1 - Rows / 2));
            _findMe = true;
            RequestNow();
        }

        private void GuildMove(int dir)
        {
            int total = R.GuildTotal, last = total > 0 ? (total - 1) / GuildPageSize : 0;
            int next = Math.Min(last, Math.Max(0, _guildPage + dir));
            if (next == _guildPage) return;
            _guildPage = next;
            RankingsState.RequestGuildPage(next);
            _gRows?.ScrollTo(0);
            _dirty = true;
        }

        /// <summary>A click picks the row; a second click on it within the double-click time inspects (RankingRow's double-click).</summary>
        private void RowClick(int i)
        {
            long id = RowId(i);
            if (id < 0) return;
            if (id == _lastClickId && CMain.Time - _lastClickAt <= SystemInformation.DoubleClickTime) Inspect(id);
            _lastClickId = id;
            _lastClickAt = CMain.Time;
            Pick(id);
        }

        private void Pick(long id)
        {
            if (_picked == id) return;
            _picked = id;
            _dirty = true;
        }

        /// <summary>RankingRow.Inspect: C.Inspect with Ranking = true, ObjectID = (uint)PlayerId (the inspect window opens on the reply).</summary>
        private static void Inspect(long id)
        {
            if (CMain.Time <= GameScene.InspectTime) return;
            GameScene.InspectTime = CMain.Time + 500;
            GameScene.Models.Inspect.InspectID = (uint)id;
            Network.Enqueue(new C.Inspect { ObjectID = (uint)id, Ranking = true });
        }

        // ------------------------------------------------------------------ render

        private RankCharacterInfo RowInfo(int i)
        {
            if (_board >= GuildTab) return null;
            List<RankCharacterInfo> list = R.List(_board);
            return i < list.Count && i < R.Count ? list[i] : null;
        }

        private long RowId(int i) => RowInfo(i)?.PlayerId ?? -1;

        private RankCharacterInfo Find(long id)
        {
            if (_board >= GuildTab || id < 0) return null;
            List<RankCharacterInfo> list = R.List(_board);
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].PlayerId == id) return list[i];
            return null;
        }

        private void Render()
        {
            _dirty = false;
            if (_rows == null || _rows.IsDisposed) return;
            bool guilds = _board == GuildTab;
            if (_tabs.Active != _board) _tabs.Select(_board);
            _playerBoard.Visible = !guilds;
            _guildBoard.Visible = guilds;
            if (guilds)
            {
                RenderGuilds();
                RenderGuildCard();
                return;
            }
            RenderBoard();
            RenderCard();
        }

        private void RenderBoard()
        {
            int t = _board;
            List<RankCharacterInfo> list = R.List(t);
            string me = Me;
            for (int i = 0; i < Rows; i++)
            {
                RankCharacterInfo info = RowInfo(i);
                bool vis = info != null;
                if (_rowBoxes[i].Visible != vis) _rowBoxes[i].Visible = vis;
                if (vis && _findMe && info.Name == me) { _findMe = false; _picked = info.PlayerId; }
                if (vis) _rowBoxes[i].Invalidate();
            }
            int n = 0;
            for (int i = 0; i < Rows; i++) if (_rowBoxes[i].Visible) n++;
            _rows.ContentHeight = S(n * 30);
            if (_podium != null)
            {
                bool pod = _shownOffset[t] == 0 && list.Count > 0;
                if (_podium.Visible != pod) _podium.Visible = pod;
                _podium.Invalidate();
            }
            int want = _offset[t];
            _prevBtn.SetDisabled(want <= 0, W.T(ClientTextKeys.ModernGuildRankFirstPage));
            _nextBtn.SetDisabled(want >= MaxOffset(), W.T(ClientTextKeys.ModernGuildRankLastPage));
            _findBtn.SetDisabled(R.MyRank(t) <= 0, W.T(ClientTextKeys.ModernGuildRankNotListedBoard));
            _pageText.Invalidate();
            _pin.Invalidate();
        }

        private string PlayerPagerText()
        {
            if (_board >= GuildTab) return string.Empty;
            int count = R.Count, want = _offset[_board];
            return count > 0 ? W.T(ClientTextKeys.ModernGuildRankRange, W.N(want + 1), W.N(Math.Min(count, want + Rows)), W.N(count)) : W.T(ClientTextKeys.ModernGuildRankNobody);
        }

        private string GuildPagerText()
        {
            int total = R.GuildTotal, b = _guildPage * GuildPageSize;
            return R.GuildPage(_guildPage) == null ? W.T(ClientTextKeys.ModernEcoLoading)
                : total > 0 ? W.T(ClientTextKeys.ModernGuildRankRange, W.N(b + 1), W.N(Math.Min(total, b + GuildPageSize)), W.N(total)) : W.T(ClientTextKeys.ModernGuildRankNoGuilds);
        }

        private static void PaintPager(Graphics g, PaintBox b, string text) =>
            ModernSkin.Text(g, text, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width, b.Size.Height), StringAlignment.Far);

        private float[] ColWidths(float w, bool guild)
        {
            float k = ModernRoot.K;
            float[] c = guild
                ? (_narrow ? new[] { 42f, 28f, 0f, 48f, 70f, 70f } : new[] { 50f, 30f, 0f, 60f, 90f, 110f })
                : (_narrow ? new[] { 42f, 28f, 0f, 90f, 40f, 70f } : new[] { 50f, 30f, 0f, 120f, 50f, 110f });
            float gap = (_narrow ? 6 : 8) * k, fixedW = 0;
            for (int i = 0; i < c.Length; i++) { c[i] *= k; fixedW += c[i]; }
            c[2] = Math.Max(40 * k, w - 16 * k - fixedW - gap * 5);
            return c;
        }

        private void PaintHead(Graphics g, PaintBox b, bool guild)
        {
            float k = ModernRoot.K, gap = (_narrow ? 6 : 8) * k;
            float[] c = ColWidths(b.Size.Width, guild);
            string[] t = guild
                ? new[] { W.T(ClientTextKeys.ModernGuildColRank), string.Empty, W.T(ClientTextKeys.Guild), W.T(ClientTextKeys.ModernGuildColLevel), W.T(ClientTextKeys.ModernGuildMembers), W.T(ClientTextKeys.ModernGuildRankExp) }
                : new[] { W.T(ClientTextKeys.ModernGuildColRank), string.Empty, W.T(ClientTextKeys.ModernGuildRankPlayer), W.T(ClientTextKeys.Guild), W.T(ClientTextKeys.ModernGuildColLevel), W.T(ClientTextKeys.ModernGuildRankExp) };
            float x = 8 * k;
            Font f = ModernSkin.Font(9, true);
            for (int i = 0; i < c.Length; i++)
            {
                string s = t[i].ToUpperInvariant();
                if (s.Length > 0) ModernSkin.SpacedText(g, s, f, ModernTheme.Dim, new RectangleF(i == 0 ? x + c[i] - ModernSkin.Measure(s, f) - s.Length * 1.1f * k : x, 0, ModernSkin.Measure(s, f) + s.Length * 1.1f * k, b.Size.Height), 1.1f * k);
                x += c[i] + gap;
            }
        }

        private void PaintRow(Graphics g, PaintBox b, int i)
        {
            RankCharacterInfo info = RowInfo(i);
            if (info == null) return;
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height, gap = (_narrow ? 6 : 8) * k;
            int rank = _shownOffset[_board] + i + 1;
            ClientRankingDetail ex = RankingsState.Extra(info.PlayerId);
            bool me = info.Name == Me, sel = info.PlayerId == _picked;
            RectangleF r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                Color fill = sel ? W.Over(ModernTheme.Accent, 0.18f) : me ? W.Over(ModernTheme.Accent, 0.12f) : b.Hovered ? W.Over(ModernTheme.Accent, 0.09f) : i % 2 == 0 ? W.Over(ModernTheme.FrameLight, 0.04f) : W.Page;
                using (var br = new SolidBrush(W.Page)) g.FillRectangle(br, 0, 0, w, h); // opaque: text on transparent pixels renders thin
                using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                if (me && !sel) ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.6f));
            }
            if (sel) using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, 1, 2 * k, 3 * k, h - 4 * k);
            float[] c = ColWidths(w, false);
            float x = 8 * k;
            ModernSkin.Text(g, "#" + rank, ModernSkin.Font(13, true), rank <= 3 ? ModernTheme.Accent : ModernTheme.Text, new RectangleF(x, 0, c[0], h), StringAlignment.Far);
            x += c[0] + gap;
            float d = 26 * k;
            Gd.ClassBadge(g, new RectangleF(x, (h - d) / 2f, d, d), Gd.ClassTag(info.Class), ModernTheme.Class(info.Class), ex != null && ex.Online, 8);
            x += c[1] + gap;
            string cls = Gd.ClassName(info.Class);
            float nw = Math.Min(ModernSkin.Measure(info.Name, ModernSkin.Font(12, true)) + 2, c[2]);
            ModernSkin.Text(g, info.Name, ModernSkin.Font(12, true), Color.White, new RectangleF(x, 0, nw, h));
            if (!_narrow) ModernSkin.Text(g, cls, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x + nw + 6 * k, 0, Math.Max(0, c[2] - nw - 6 * k), h));
            x += c[2] + gap;
            ModernSkin.Text(g, ex != null ? (string.IsNullOrEmpty(ex.GuildName) ? "—" : ex.GuildName) : string.Empty, ModernSkin.Font(10), ModernTheme.AccentDim, new RectangleF(x, 0, c[3], h));
            x += c[3] + gap;
            ModernSkin.Text(g, info.level.ToString(), ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(x, 0, c[4], h), StringAlignment.Center);
            x += c[4] + gap;
            if (ex != null) Gd.Bar(g, new RectangleF(x, h / 2f - 2.5f * k, c[5], 5 * k), ex.ExpPerTenThousand / 10000f);
        }

        private void PaintPodium(Graphics g, PaintBox b)
        {
            List<RankCharacterInfo> list = R.List(_board);
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height, gap = 10 * k;
            float unit = (w - 2 * gap) / 3.15f;
            float[] widths = { unit, unit * 1.15f, unit };
            int[] pos = { 2, 1, 3 };
            float x = 0;
            for (int s = 0; s < 3; s++)
            {
                int place = pos[s];
                RankCharacterInfo info = place - 1 < list.Count ? list[place - 1] : null;
                float pw = widths[s];
                if (info != null)
                {
                    Color mc = place == 1 ? P1 : place == 2 ? P2 : P3;
                    float ph = place == 1 ? h - 14 * k : h - 30 * k;
                    RectangleF r = new RectangleF(x + 0.5f, h - ph, pw - 1, ph - 1);
                    using (GraphicsPath p = ModernSkin.RoundTop(r, ModernTheme.Radius * k))
                    {
                        using (var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), ModernTheme.Mix(mc, W.Page, 0.22f), W.Over(ModernTheme.PanelB, 0.7f), LinearGradientMode.Vertical)) g.FillPath(br, p);
                        ModernSkin.Stroke(g, p, ModernTheme.Mix(mc, W.Page, 0.6f));
                    }
                    using (var br = new SolidBrush(ModernTheme.Mix(mc, W.Page, 0.6f))) g.FillRectangle(br, r.X, r.Bottom - 3 * k, r.Width, 3 * k);
                    // medal
                    float md = 26 * k;
                    RectangleF medal = new RectangleF(r.X + (r.Width - md) / 2f, r.Y - md / 2f, md, md);
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(medal);
                        using var br = new PathGradientBrush(path) { CenterColor = Color.White, SurroundColors = new[] { mc }, CenterPoint = new PointF(medal.X + md * 0.35f, medal.Y + md * 0.35f) };
                        g.FillPath(br, path);
                    }
                    using (var pen = new Pen(Color.Black, 2f)) g.DrawEllipse(pen, medal);
                    ModernSkin.Text(g, place.ToString(), ModernSkin.Font(12, true), Color.FromArgb(255, 26, 26, 26), medal, StringAlignment.Center);
                    ClientRankingDetail ex = RankingsState.Extra(info.PlayerId);
                    float bd = 40 * k, cy = r.Y + md / 2f + 4 * k;
                    Gd.ClassBadge(g, new RectangleF(r.X + (r.Width - bd) / 2f, cy, bd, bd), Gd.ClassTag(info.Class), mc, false, 10);
                    cy += bd + 3 * k;
                    ModernSkin.Text(g, info.Name, ModernSkin.Font(13, true), Color.White, new RectangleF(r.X + 4 * k, cy, r.Width - 8 * k, 17 * k), StringAlignment.Center);
                    cy += 17 * k;
                    string sub = ex != null && !string.IsNullOrEmpty(ex.GuildName) ? Gd.ClassName(info.Class) + " · " + ex.GuildName : Gd.ClassName(info.Class);
                    ModernSkin.Text(g, sub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(r.X + 4 * k, cy, r.Width - 8 * k, 14 * k), StringAlignment.Center);
                    cy += 14 * k;
                    if (cy + 18 * k <= h) ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildRankLv, info.level), ModernSkin.Font(15, true), mc, new RectangleF(r.X, cy, r.Width, 20 * k), StringAlignment.Center);
                }
                x += pw + gap;
            }
        }

        private void PodiumClick()
        {
            List<RankCharacterInfo> list = R.List(_board);
            float k = ModernRoot.K, w = _podium.Size.Width, gap = 10 * k, unit = (w - 2 * gap) / 3.15f;
            float x = CMain.MPoint.X - _podium.DisplayLocation.X;
            int place = x < unit ? 2 : x < unit * 2.15f + gap ? 1 : 3;
            if (place - 1 < list.Count && list[place - 1] != null) Pick(list[place - 1].PlayerId);
        }

        private void PaintPin(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            using (var pen = new Pen(W.Over(ModernTheme.Accent, 0.5f), 1f)) g.DrawLine(pen, 0, 0.5f, b.Size.Width, 0.5f);
            int t = _board;
            int mine = t < GuildTab ? R.MyRank(t) : 0;
            string text = mine > 0 ? W.T(ClientTextKeys.ModernGuildRankYourBoard, W.N(mine)) : t == 0 ? W.T(ClientTextKeys.ModernGuildRankNotListed) : W.T(ClientTextKeys.ModernGuildRankNotListedBoard);
            ModernSkin.Text(g, text, ModernSkin.Font(12, mine > 0), mine > 0 ? Color.White : ModernTheme.Dim, new RectangleF(4 * k, 6 * k, b.Size.Width - 8 * k, b.Size.Height - 6 * k));
        }

        // ------------------------------------------------------------------ the card

        private void ClearSide()
        {
            for (int i = _side.Controls.Count - 1; i >= 0; i--) _side.Controls[i].Dispose();
        }

        /// <summary>The profile block (.prof) and the two big numbers (.big), painted.</summary>
        private void Profile(string badge, Color ring, bool online, string name, string sub, string big1, string label1, string big2, string label2)
        {
            int sw = _sideW - 24;
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K, w = b.Size.Width, d = 60 * k;
                Gd.ClassBadge(g, new RectangleF((w - d) / 2f, 6 * k, d, d), badge, ring, online, 14);
                ModernSkin.Text(g, name, ModernSkin.Font(17, true), Color.White, new RectangleF(0, 72 * k, w, 22 * k), StringAlignment.Center);
                ModernSkin.Text(g, sub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(0, 94 * k, w, 15 * k), StringAlignment.Center);
                float bw = (w - 6 * k) / 2f, by = 118 * k, bh = 54 * k;
                for (int i = 0; i < 2; i++)
                {
                    RectangleF r = new RectangleF(i * (bw + 6 * k) + 0.5f, by + 0.5f, bw - 1, bh - 1);
                    using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                    {
                        using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.06f))) g.FillPath(br, p);
                        ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.5f));
                    }
                    ModernSkin.Text(g, i == 0 ? big1 : big2, ModernSkin.Font(19, true), Color.White, new RectangleF(r.X, r.Y + 5 * k, r.Width, 26 * k), StringAlignment.Center);
                    ModernSkin.Text(g, i == 0 ? label1 : label2, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(r.X + 2 * k, r.Y + 32 * k, r.Width - 4 * k, 14 * k), StringAlignment.Center);
                }
            }) { Parent = _side, Location = new Point(S(12), S(10)), Size = new Size(S(sw), S(176)), NotControl = true };
        }

        private void Note(string text, int y) => W.Label(_side, text, 12, y, _sideW - 24, 32, 11, false, ModernTheme.Dim, StringAlignment.Center, true);

        /// <summary>The picked player's card, else the user's own summary.</summary>
        private void RenderCard()
        {
            ClearSide();
            int t = _board;
            RankCharacterInfo info = Find(_picked);
            string me = Me;
            if (info == null)
            {
                int mine = R.MyRank(t);
                Profile(W.T(ClientTextKeys.ModernGuildRankYou), Color.Empty, false, me.Length > 0 ? me : W.T(ClientTextKeys.ModernGuildRankYou), TabName(t),
                    mine > 0 ? "#" + W.N(mine) : "–", W.T(ClientTextKeys.ModernGuildRankYourRank), W.N(GameScene.User?.Level ?? 0), W.T(ClientTextKeys.ModernGuildRankYourLevel));
                Note(W.T(ClientTextKeys.ModernGuildRankClickHint), 196);
                return;
            }
            ClientRankingDetail ex = RankingsState.Extra(info.PlayerId);
            List<RankCharacterInfo> list = R.List(t);
            int at = list.IndexOf(info), rank = at >= 0 ? _shownOffset[t] + at + 1 : 0;
            var subs = new List<string> { Gd.ClassName(info.Class) };
            if (ex != null)
            {
                subs.Add(string.IsNullOrEmpty(ex.GuildName) ? W.T(ClientTextKeys.ModernGuildRankNoGuild) : ex.GuildName);
                subs.Add(W.T(ex.Online ? ClientTextKeys.ModernGuildRankOnlineWord : ClientTextKeys.ModernGuildRankOfflineWord));
            }
            Profile(Gd.ClassTag(info.Class), ModernTheme.Class(info.Class), ex != null && ex.Online, info.Name, string.Join(" · ", subs),
                rank > 0 ? "#" + W.N(rank) : "–", W.T(ClientTextKeys.ModernGuildRankBoardRank, TabName(t).ToLowerInvariant()),
                info.level.ToString(), ex != null ? W.T(ClientTextKeys.ModernGuildRankLevelExp, (ex.ExpPerTenThousand / 100.0).ToString("0.0")) : W.T(ClientTextKeys.ModernGuildColLevel).ToLowerInvariant());
            if (info.Name == me)
            {
                Note(W.T(ClientTextKeys.ModernGuildRankThatsYou), 196);
                return;
            }
            string name = info.Name;
            long id = info.PlayerId;
            bool isFriend = false;
            foreach (ClientFriend f in GameScene.Models.Friends.Friends)
                if (f != null && !f.Blocked && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) isFriend = true;
            GroupState grp = GameScene.Models.Group;
            bool inGroup = grp.Contains(name), leader = grp.Members.Count == 0 || grp.Members[0] == me;
            int bw = (_sideW - 24 - 5) / 2, n = 0;
            void Act(string text, Action run, string why = null, string hint = null)
            {
                var b = new ModernButton(text) { Parent = _side, FontSize = 11, Size = new Size(S(bw), S(26)) };
                b.Location = new Point(S(12 + (n % 2) * (bw + 5)), S(196 + (n / 2) * 31));
                if (hint != null) b.Tooltip = hint;
                if (!string.IsNullOrEmpty(why)) b.SetDisabled(true, why);
                b.Click += (o, e) => run();
                n++;
            }
            Act(W.T(ClientTextKeys.ModernGuildRankInspect), () => Inspect(id), null, W.T(ClientTextKeys.ModernGuildRankInspectHint));
            Act(W.T(ClientTextKeys.ModernGuildWhisper), () => (GameScene.Ui as ModernUi)?.Chat?.Prefill("/" + name + " "), null, W.T(ClientTextKeys.ModernGuildWhisperHint));
            Act(W.T(ClientTextKeys.ModernGuildRankAddFriend), () => Network.Enqueue(new C.AddFriend { Name = name, Blocked = false }),
                isFriend ? W.T(ClientTextKeys.ModernGuildRankAlreadyFriend) : null, W.T(ClientTextKeys.ModernGuildRankAddFriendHint));
            Act(W.T(ClientTextKeys.ModernGuildInvite), () => Network.Enqueue(new C.AddMember { Name = name }),
                inGroup ? W.T(ClientTextKeys.ModernGuildRankInGroup) : !leader ? W.T(ClientTextKeys.ModernGuildRankLeaderOnly) : ex != null && !ex.Online ? W.T(ClientTextKeys.ModernGuildRankOffline) : null,
                W.T(ClientTextKeys.ModernGuildGroupHint));
        }

        // ------------------------------------------------------------------ guild board

        private void RenderGuilds()
        {
            List<ClientGuildRanking> rows = R.GuildPage(_guildPage);
            int n = 0;
            for (int i = 0; i < GuildPageSize; i++)
            {
                bool vis = rows != null && i < rows.Count && rows[i] != null;
                if (_gRowBoxes[i].Visible != vis) _gRowBoxes[i].Visible = vis;
                if (vis) { _gRowBoxes[i].Invalidate(); n++; }
            }
            _gRows.ContentHeight = S(n * 34);
            int total = R.GuildTotal, last = total > 0 ? (total - 1) / GuildPageSize : 0;
            _gPrev.SetDisabled(_guildPage <= 0, W.T(ClientTextKeys.ModernGuildRankFirstPage));
            _gNext.SetDisabled(_guildPage >= last, W.T(ClientTextKeys.ModernGuildRankLastPage));
            _gPageText.Invalidate();
        }

        private void PaintGuildRow(Graphics g, PaintBox b, int i)
        {
            List<ClientGuildRanking> rows = R.GuildPage(_guildPage);
            if (rows == null || i >= rows.Count || rows[i] == null) return;
            ClientGuildRanking info = rows[i];
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height, gap = (_narrow ? 6 : 8) * k;
            string mine = GameScene.User?.GuildName ?? string.Empty;
            bool me = mine.Length > 0 && info.Name == mine;
            RectangleF r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                Color fill = me ? W.Over(ModernTheme.Accent, 0.12f) : i % 2 == 0 ? W.Over(ModernTheme.FrameLight, 0.04f) : W.Page;
                using (var br = new SolidBrush(W.Page)) g.FillRectangle(br, 0, 0, w, h);
                using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                if (me) ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.6f));
            }
            float[] c = ColWidths(w, true);
            float x = 8 * k;
            ModernSkin.Text(g, "#" + (_guildPage * GuildPageSize + i + 1), ModernSkin.Font(13, true), ModernTheme.Text, new RectangleF(x, 0, c[0], h), StringAlignment.Far);
            x += c[0] + gap;
            Gd.Shield(g, new RectangleF(x, (h - 28 * k) / 2f, 24 * k, 28 * k), string.IsNullOrEmpty(info.Name) ? "?" : info.Name.Substring(0, 1).ToUpperInvariant(), 1.5f * k, 11);
            x += c[1] + gap;
            float nw = Math.Min(ModernSkin.Measure(info.Name, ModernSkin.Font(12, true)) + 2, c[2]);
            ModernSkin.Text(g, info.Name, ModernSkin.Font(12, true), Color.White, new RectangleF(x, 0, nw, h));
            if (!_narrow && !string.IsNullOrEmpty(info.Leader))
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildRankLedBy, info.Leader), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x + nw + 6 * k, 0, Math.Max(0, c[2] - nw - 6 * k), h));
            x += c[2] + gap;
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernGuildRankLv, info.Level), ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(x, 0, c[3], h), StringAlignment.Center);
            x += c[3] + gap;
            ModernSkin.Text(g, W.T(info.Members == 1 ? ClientTextKeys.ModernGuildRankOneMember : ClientTextKeys.ModernGuildRankNMembers, info.Members), ModernSkin.Font(10), ModernTheme.AccentDim, new RectangleF(x, 0, c[4], h));
            x += c[4] + gap;
            Gd.Bar(g, new RectangleF(x, h / 2f - 2.5f * k, c[5], 5 * k), info.ExpPerTenThousand / 10000f);
        }

        /// <summary>The user's guild beside the guild board (its rank when it is on a received page).</summary>
        private void RenderGuildCard()
        {
            ClearSide();
            string name = GameScene.User?.GuildName ?? string.Empty;
            if (name.Length == 0)
            {
                Profile("–", Color.Empty, false, W.T(ClientTextKeys.ModernGuildGtNoGuild), string.Empty, "–", W.T(ClientTextKeys.ModernGuildRankGuildRank), "–", W.T(ClientTextKeys.ModernGuildKpiLevel));
                Note(W.T(ClientTextKeys.ModernGuildRankJoinHint), 196);
                return;
            }
            int rank = 0, pages = R.GuildTotal > 0 ? (R.GuildTotal + GuildPageSize - 1) / GuildPageSize : 0;
            for (int p = 0; p < pages && rank == 0; p++)
            {
                List<ClientGuildRanking> rows = R.GuildPage(p);
                if (rows == null) continue;
                for (int i = 0; i < rows.Count; i++) if (rows[i] != null && rows[i].Name == name) { rank = p * GuildPageSize + i + 1; break; }
            }
            Profile(name.Substring(0, 1).ToUpperInvariant(), Color.Empty, false, name, W.T(ClientTextKeys.ModernGuildRankYourGuild),
                rank > 0 ? "#" + W.N(rank) : "–", W.T(ClientTextKeys.ModernGuildRankGuildRank), GameScene.Models.Guild.Level.ToString(), W.T(ClientTextKeys.ModernGuildKpiLevel));
            Note(W.T(rank > 0 ? ClientTextKeys.ModernGuildRankRebuilt : ClientTextKeys.ModernGuildRankNotSeen), 196);
        }
    }
}
