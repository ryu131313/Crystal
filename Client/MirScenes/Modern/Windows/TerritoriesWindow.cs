// Ported from WebClient/src/ui/modern/windows/territories.ts (TerritoryBook + TerritoriesWindow): the new UI's guild territories
// window. Classic reference: GuildTerritoryDialog (Client/MirScenes/Dialogs/GuildTerritoryDialog .cs) + GameScene.GuildTerritoryPage
// (status rules: "None" = available, begin > 0 = sale pending, price > 0 = for sale, else unavailable).
//  - the list is the server's (S.GuildTerritoryPage, 7 rows a page, no page number on the wire): TerritoryBook asks for every
//    page (C.GuildTerritoryPage) and files each reply by the request order, or by its first row's index once known, so the
//    window lists every territory at once;
//  - opens from the game menu and when the server sends a page while it is hidden (an NPC's [@GUILDTERRITORY] page calls
//    GetGuildTerritories(0)): ModernUi.GuildTerritoryPage;
//  - Rent / Extend / List for sale / Cancel sale / Teleport send C.GuildTerritoryAction (the server runs the NPC scripts' BuyGT /
//    ExtendGT / GTSale / GTCancelSale / TeleportGT checks); Buy sends C.PurchaseGuildTerritory with the owning guild's name (as
//    GuildTerritoryDialog.BuyButton). Rent / Extend / Teleport / Buy need the territory NPC page open on the server, so they are
//    enabled only while the NPC window shows that page (NpcState.LastPageKey); List for sale / Cancel sale need the player on the
//    territory (the server checks it). Results arrive as chat lines, then fresh pages (nothing changes locally);
//  - prices and lease length from S.ServerRules (BuyGTGold, ExtendGT, GTDays); the minimum sale price 2,000,000 is the server's;
//  - lease days: ClientGTMap.days is the server's (Now - GTRent).Days snapshot (negative = days left): "about N days";
//  - "Mail leader" opens the mail window's composer (MailWindow.ComposeTo, like the classic Mail button).
// HIDDEN (Crystal sends no data): the floor blueprint, amenities, the lease timeline, the handover countdown.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>Every territory page, filed from S.GuildTerritoryPage replies (one per ModernUi, built at UI start so a server-sent
    /// page is filed before the window exists).</summary>
    internal sealed class TerritoryBook
    {
        public const int PageRows = 7, MaxPending = 16;
        public const string None = "None";

        private readonly List<ClientGTMap> _rows = new List<ClientGTMap>();
        private readonly List<int> _expect = new List<int>();
        /// <summary>When the last C.GuildTerritoryPage was sent: the server ignores requests it cannot answer (a dead player), so a
        /// queue older than <see cref="ExpectMs"/> is dropped instead of swallowing the next (NPC-opened) page.</summary>
        private long _expectSent;
        private const long ExpectMs = 5000;

        private void DropStaleExpect()
        {
            if (_expect.Count > 0 && CMain.Time - _expectSent > ExpectMs) _expect.Clear();
        }
        private readonly HashSet<int> _loaded = new HashSet<int>();

        public event Action Changed;

        /// <summary>GT list in server order (null = a page not received yet).</summary>
        public IReadOnlyList<ClientGTMap> Rows => _rows;
        public int Length { get; private set; }
        public bool Received { get; private set; }
        /// <summary>The last reply came unasked (the server's own GetGuildTerritories(0), e.g. at the territory NPC).</summary>
        public bool LastUnasked { get; private set; }
        public bool Loading => _expect.Count > 0;
        /// <summary>The window was opened by an unasked page; its Shown then skips its own full refresh.</summary>
        public bool ServerOpened;

        private int Pages => Math.Max(1, (Length + PageRows - 1) / PageRows);

        /// <summary>Asks for every page again (the window opened, or after a purchase).</summary>
        public void Refresh()
        {
            _loaded.Clear();
            for (int p = 0; p < Pages; p++) Request(p);
        }

        /// <summary>C.GuildTerritoryAction. Except for Teleport, page 0 is asked again right after (S.GuildTerritoryPage carries no
        /// page number) and the other pages follow, so the whole list shows the result. False = invalid (nothing sent).</summary>
        public bool Action(GuildTerritoryActionKind kind, int index, uint price = 0)
        {
            if (kind < GuildTerritoryActionKind.Rent || kind > GuildTerritoryActionKind.Teleport) return false;
            if (kind == GuildTerritoryActionKind.ListForSale && (price == 0 || price > int.MaxValue)) return false;
            Network.Enqueue(new C.GuildTerritoryAction { Kind = kind, TerritoryIndex = index, Price = kind == GuildTerritoryActionKind.ListForSale ? price : 0 });
            if (kind == GuildTerritoryActionKind.Teleport) return true;
            Request(0);
            _loaded.Clear();
            for (int p = 1; p < Pages; p++) Request(p);
            return true;
        }

        /// <summary>C.PurchaseGuildTerritory (the server answers with chat lines only: every page is asked again).</summary>
        public void Purchase(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            Network.Enqueue(new C.PurchaseGuildTerritory { Owner = owner });
            Refresh();
        }

        private void Request(int page)
        {
            DropStaleExpect();
            if (_expect.Count >= MaxPending) _expect.Clear();
            _expect.Add(page);
            _expectSent = CMain.Time;
            Network.Enqueue(new C.GuildTerritoryPage { Page = page });
        }

        /// <summary>S.GuildTerritoryPage.</summary>
        public void File(S.GuildTerritoryPage p)
        {
            if (p == null) return;
            List<ClientGTMap> list = p.Listings ?? new List<ClientGTMap>();
            Length = Math.Max(0, p.length);
            while (_rows.Count < Length) _rows.Add(null);
            if (_rows.Count > Length) _rows.RemoveRange(Length, _rows.Count - Length);
            int page = -1;
            DropStaleExpect();
            bool unasked = _expect.Count == 0;
            if (!unasked)
            {
                page = _expect[0];
                _expect.RemoveAt(0);
            }
            LastUnasked = unasked;
            // content wins over the queue: a reply whose first row is already filed at a page start is that page
            if (list.Count > 0 && list[0] != null)
            {
                int at = -1;
                for (int i = 0; i < _rows.Count; i++) if (_rows[i] != null && _rows[i].index == list[0].index) { at = i; break; }
                if (at >= 0 && at % PageRows == 0) page = at / PageRows;
            }
            if (page < 0) page = 0; // unasked: the NPC's GetGuildTerritories(0)
            int b = page * PageRows;
            for (int i = 0; i < PageRows && b + i < Length; i++) _rows[b + i] = i < list.Count ? list[i] : null;
            Received = true;
            _loaded.Add(page);
            if (unasked)
            {
                // a fresh list from the server: ask for the other pages (an NPC opening the window sends only page 0)
                _loaded.Clear();
                _loaded.Add(page);
                for (int q = 0; q < Pages; q++) if (q != page) Request(q);
            }
            else
            {
                // the list grew past what was asked for: fetch the missing pages
                for (int q = 0; q < Pages; q++) if (!_loaded.Contains(q) && !_expect.Contains(q)) Request(q);
            }
            Changed?.Invoke();
        }
    }

    public sealed class TerritoriesWindow : ModernWindow
    {
        public const string WindowKey = "territories";
        private const string NpcPage = "@GUILDTERRITORY";
        private const uint MinSale = 2000000;
        private const int H = 530, TopH = 60;

        private enum Status { Vacant, Sale, Owned, Pend }
        private enum Filter { All, Vacant, ForSale, Owned }
        private static readonly (Filter F, ClientTextKeys Name)[] Filters =
        {
            (Filter.All, ClientTextKeys.ModernGuildLogAll), (Filter.Vacant, ClientTextKeys.ModernGuildGtVacant),
            (Filter.ForSale, ClientTextKeys.ModernGuildGtForSale), (Filter.Owned, ClientTextKeys.ModernGuildGtOwned),
        };

        private readonly TerritoryBook _book;
        private Filter _filter = Filter.All;
        private int _sel = -1;
        private string _why = string.Empty, _chipsKey = string.Empty;
        private bool _dirty = true;
        private bool _narrow;
        private int _w, _listW, _detW;

        private PaintBox _top;
        private ModernPanel _chipsBox, _det;
        private Chips<Filter> _chips;
        private ScrollView _rowsView;

        private readonly Action _onChange;
        private readonly Action _onNpc;

        internal TerritoriesWindow(TerritoryBook book) : base(WindowKey, W.T(ClientTextKeys.ModernGuildGtTitle), 42, 40)
        {
            _book = book;
            _onChange = () => _dirty = true;
            _onNpc = () => _dirty = true;
        }

        private static ServerPackets.ServerRules Rules => GameScene.Models?.ServerRules.Rules;
        private static string MyGuild => GameScene.User?.GuildName ?? string.Empty;
        private static bool Leader => MyGuild.Length > 0 && GameScene.Models?.Guild.MyRankId == 0;

        // test hooks
        internal string Why => _why;
        internal int SelectedIndex => _sel;
        internal ScrollView RowsView => _rowsView;
        internal ModernPanel Detail => _det;

        private static Status StatusOf(ClientGTMap gt)
        {
            if (gt.Owner == TerritoryBook.None) return Status.Vacant;
            if (gt.begin > 0) return Status.Pend;
            return gt.price > 0 ? Status.Sale : Status.Owned;
        }

        private static bool Passes(Filter f, Status s) => f == Filter.All || (f == Filter.Vacant ? s == Status.Vacant : f == Filter.ForSale ? s == Status.Sale : s != Status.Vacant);
        private static string Leaders(ClientGTMap gt) => string.IsNullOrEmpty(gt.Leader2) ? gt.Leader : W.T(ClientTextKeys.ModernGuildGtAnd, gt.Leader, gt.Leader2);
        private static int DaysLeft(ClientGTMap gt) => gt.Owner != TerritoryBook.None && gt.days < 0 ? -gt.days : -1;
        private static string Days(int n) => W.T(n == 1 ? ClientTextKeys.ModernGuildGtOneDay : ClientTextKeys.ModernGuildGtNDays, n);

        private ClientGTMap Mine()
        {
            string g = MyGuild;
            if (g.Length == 0) return null;
            foreach (ClientGTMap r in _book.Rows) if (r != null && r.Owner == g) return r;
            return null;
        }

        private static bool AtNpc()
        {
            NpcState npc = (GameScene.Ui as ModernUi)?.Npc;
            return npc != null && npc.Open && NpcState.NormKey(npc.LastPageKey) == NpcPage;
        }

        private int Number(ClientGTMap gt)
        {
            for (int i = 0; i < _book.Rows.Count; i++) if (_book.Rows[i] == gt) return i + 1;
            return 0;
        }

        private string NameOf(ClientGTMap gt, int no) => string.IsNullOrEmpty(gt.Name) ? W.T(ClientTextKeys.ModernGuildGtNumber, no) : gt.Name;

        // ------------------------------------------------------------------ build / lifecycle

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            _w = _narrow ? 700 : 860;
            _listW = _narrow ? 300 : 380;
            _detW = _w - _listW - 1;
            ContentWidth = _w;
            ContentHeight = H;
            var m = new ModernPanel { Parent = this, Location = new Point(0, S(TitleHeight)), Size = new Size(S(_w), S(H)) };
            _top = new PaintBox(PaintTop) { Parent = m, Location = Point.Empty, Size = new Size(S(_w), S(TopH)), NotControl = true };
            var refresh = new ModernButton(W.T(ClientTextKeys.ModernEcoRefresh)) { Parent = m, FontSize = 11, Tooltip = W.T(ClientTextKeys.ModernGuildGtRefreshHint) };
            refresh.AutoWidth(60);
            refresh.Location = new Point(S(_w - 16) - refresh.Size.Width, S((TopH - 22) / 2));
            refresh.Click += (o, e) => _book.Refresh();
            W.Divider(m, 0, TopH, _w);
            _chipsBox = new ModernPanel { Parent = m, Location = new Point(S(12), S(TopH + 10)), Size = new Size(S(_listW - 24), S(48)) };
            _chipsKey = string.Empty;
            _rowsView = new ScrollView(_listW - 14, H - TopH - 10 - 52) { Parent = m, Location = new Point(S(8), S(TopH + 58)), Background = W.Page };
            _rowsView.Framed = false;
            W.VDivider(m, _listW, TopH + 1, H - TopH - 1);
            _det = new ModernPanel { Parent = m, Location = new Point(S(_listW + 1), S(TopH + 1)), Size = new Size(S(_detW), S(H - TopH - 1)) };
            _dirty = true;
            Render();
        }

        protected override void Shown()
        {
            _book.Changed += _onChange;
            GameScene.Models.Guild.Changed.On(_onChange);
            GameScene.Models.ServerRules.Changed.On(_onChange);
            NpcState npc = (GameScene.Ui as ModernUi)?.Npc;
            if (npc != null)
            {
                npc.Changed += _onNpc;
                npc.Closed += _onNpc;
            }
            _why = string.Empty;
            // an NPC-opened window already has page 0 and the book asks for the rest; a menu open asks for everything
            if (_book.ServerOpened) _book.ServerOpened = false;
            else _book.Refresh();
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            _book.Changed -= _onChange;
            GameScene.Models.Guild.Changed.Off(_onChange);
            GameScene.Models.ServerRules.Changed.Off(_onChange);
            NpcState npc = (GameScene.Ui as ModernUi)?.Npc;
            if (npc != null)
            {
                npc.Changed -= _onNpc;
                npc.Closed -= _onNpc;
            }
        }

        public override void Process()
        {
            base.Process();
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ render

        private void PaintTop(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, h = b.Size.Height;
            string guild = MyGuild;
            var gs = GameScene.Models?.Guild;
            // the hexagonal crest
            RectangleF c = new RectangleF(16 * k, (h - 40 * k) / 2f, 40 * k, 40 * k);
            using (var p = new GraphicsPath())
            {
                p.AddPolygon(new[]
                {
                    new PointF(c.X + c.Width / 2, c.Y), new PointF(c.Right, c.Y + c.Height * 0.18f), new PointF(c.Right, c.Y + c.Height * 0.62f),
                    new PointF(c.X + c.Width / 2, c.Bottom), new PointF(c.X, c.Y + c.Height * 0.62f), new PointF(c.X, c.Y + c.Height * 0.18f),
                });
                using var br = new LinearGradientBrush(c, ModernTheme.Mix(ModernTheme.Accent, Color.White, 0.7f), ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f), LinearGradientMode.Vertical);
                g.FillPath(br, p);
            }
            ModernSkin.Text(g, guild.Length > 0 ? Gd.Capitals(guild) : "–", ModernSkin.Font(13, true), Color.White, c, StringAlignment.Center);
            float x = 70 * k, nw = (_narrow ? 170 : 220) * k;
            ModernSkin.Text(g, guild.Length > 0 ? guild : W.T(ClientTextKeys.ModernGuildGtNoGuild), ModernSkin.Font(14, true), Color.White, new RectangleF(x, h / 2f - 18 * k, nw, 18 * k));
            string role = guild.Length == 0 ? W.T(ClientTextKeys.ModernGuildGtJoinHint)
                : Leader ? W.T(ClientTextKeys.ModernGuildGtLeaderRole, Gd.RankName(GameScene.User?.GuildRankName, 0)) : Gd.RankName(GameScene.User?.GuildRankName, gs?.MyRankId ?? 1);
            ModernSkin.Text(g, role, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(x, h / 2f + 1 * k, nw, 15 * k));
            ClientGTMap mine = Mine();
            int left = mine != null ? DaysLeft(mine) : -1;
            string mineText = mine == null ? W.T(ClientTextKeys.ModernGuildGtNone) : NameOf(mine, Number(mine)) + (left >= 0 ? " · ~" + Days(left) : string.Empty);
            float sx = x + nw + 10 * k;
            void Stat(string label, string val, Color colour, float w)
            {
                using (var pen = new Pen(W.Over(ModernTheme.Frame, 0.6f), 1f)) g.DrawLine(pen, sx, h / 2f - 16 * k, sx, h / 2f + 16 * k);
                ModernSkin.SpacedText(g, label, ModernSkin.Font(9, true), ModernTheme.Dim, new RectangleF(sx + 14 * k, h / 2f - 17 * k, ModernSkin.Measure(label, ModernSkin.Font(9, true)) + label.Length * 1.3f * k, 14 * k), 1.3f * k);
                ModernSkin.Text(g, val, ModernSkin.Font(13, true), colour, new RectangleF(sx + 14 * k, h / 2f - 1 * k, w, 18 * k));
                sx += w + 28 * k;
            }
            Stat(W.T(ClientTextKeys.ModernGuildGtGuildGold), guild.Length > 0 && gs != null ? W.N(gs.Gold) : "–", ModernTheme.Gold, (_narrow ? 90 : 110) * k);
            Stat(W.T(ClientTextKeys.ModernGuildGtYourTerritory), mineText, Color.White, Math.Max(80 * k, b.Size.Width - sx - 14 * k - 110 * k));
        }

        private void Render()
        {
            _dirty = false;
            if (_rowsView == null || _rowsView.IsDisposed) return;
            _top.Invalidate();
            IReadOnlyList<ClientGTMap> rows = _book.Rows;
            ClientGTMap mine = Mine();

            // chips with counts (rebuilt only when a count changed)
            int[] counts = new int[Filters.Length];
            foreach (ClientGTMap r in rows)
            {
                if (r == null) continue;
                Status s = StatusOf(r);
                for (int i = 0; i < Filters.Length; i++) if (Passes(Filters[i].F, s)) counts[i]++;
            }
            string key = _filter + "," + string.Join(",", counts);
            if (key != _chipsKey)
            {
                _chipsKey = key;
                _chips?.Dispose();
                var choices = new List<Choice<Filter>>();
                for (int i = 0; i < Filters.Length; i++) choices.Add(new Choice<Filter>(Filters[i].F, W.T(Filters[i].Name) + " " + counts[i]));
                _chips = new Chips<Filter>(choices, new[] { _filter }, _listW - 24, false, false) { Parent = _chipsBox, Location = Point.Empty };
                _chips.Changed += s =>
                {
                    foreach (Filter f in s) if (f != _filter) { _filter = f; _chipsKey = string.Empty; _dirty = true; }
                };
            }

            // default selection: the guild's own, else the first available, else the first
            bool found = false;
            foreach (ClientGTMap r in rows) if (r != null && r.index == _sel) found = true;
            if (_sel < 0 || !found)
            {
                ClientGTMap pick = mine;
                if (pick == null) foreach (ClientGTMap r in rows) if (r != null && r.Owner == TerritoryBook.None) { pick = r; break; }
                if (pick == null) foreach (ClientGTMap r in rows) if (r != null) { pick = r; break; }
                _sel = pick?.index ?? -1;
            }

            // the list
            for (int i = _rowsView.Content.Controls.Count - 1; i >= 0; i--) _rowsView.Content.Controls[i].Dispose();
            int y = 0, w = _rowsView.InnerWidth, shown = 0, gtDays = Rules?.GTDays ?? 0;
            for (int i = 0; i < rows.Count; i++)
            {
                ClientGTMap r = rows[i];
                if (r == null || !Passes(_filter, StatusOf(r))) continue;
                int no = i + 1, days = DaysLeft(r);
                bool own = MyGuild.Length > 0 && r.Owner == MyGuild, sel = r.index == _sel;
                bool bar = days >= 0 && gtDays > 0;
                int h = bar ? 58 : 50;
                string sub = r.Owner == TerritoryBook.None
                    ? (Rules != null ? W.T(ClientTextKeys.ModernGuildGtForRent, W.N(Rules.BuyGTGold), Days(Rules.GTDays)) : W.T(ClientTextKeys.ModernGuildGtForRentShort))
                    : r.Owner + " · " + Leaders(r) + (days >= 0 ? " · ~" + Days(days) : string.Empty) + (r.price > 0 ? " · " + W.N(r.price) : string.Empty);
                float fill = bar ? Math.Min(1f, days / (float)gtDays) : 0;
                var row = new PaintBox((g, b) => PaintRow(g, b, r, no, sub, own, sel, bar, fill, days)) { Parent = _rowsView.Content, Location = new Point(0, y), Size = new Size(w, S(h)) };
                int index = r.index;
                row.Click += (o, e) => { if (_sel != index) { _sel = index; _why = string.Empty; _dirty = true; } };
                y += S(h + 5);
                shown++;
            }
            if (shown == 0)
                W.Label(_rowsView.Content, W.T(!_book.Received ? ClientTextKeys.ModernGuildGtLoading : _book.Length == 0 ? ClientTextKeys.ModernGuildGtNoneOnServer : ClientTextKeys.ModernGuildGtNoMatch),
                    0, 20, (int)(w / ModernRoot.K), 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _rowsView.FitContent(0);

            ClientGTMap cur = null;
            foreach (ClientGTMap r in rows) if (r != null && r.index == _sel) cur = r;
            RenderDetail(cur);
        }

        private void PaintRow(Graphics g, PaintBox b, ClientGTMap r, int no, string sub, bool own, bool sel, bool bar, float fill, int days)
        {
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            RectangleF box = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(box, ModernTheme.Radius * k))
            {
                if (sel)
                    using (var br = new LinearGradientBrush(new RectangleF(0, 0, w, h), W.Over(ModernTheme.Accent, 0.22f), W.Over(ModernTheme.FrameLight, 0.04f), LinearGradientMode.Horizontal)) g.FillPath(br, p);
                else
                    using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.04f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, sel ? ModernTheme.Accent : b.Hovered ? ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.5f) : W.Over(ModernTheme.Frame, 0.6f));
            }
            if (sel || own)
                using (var br = new SolidBrush(own && !sel ? ModernTheme.Gold : ModernTheme.Accent)) g.FillRectangle(br, 1, 2 * k, 3 * k, h - 4 * k);
            float top = bar ? 25 * k : h / 2f;
            Num(g, new RectangleF(10 * k, top - 17 * k, 34 * k, 34 * k), no);
            float tagW = TagWidth(r, own);
            float x = 54 * k, nw = w - x - tagW - 18 * k;
            ModernSkin.Text(g, NameOf(r, no), ModernSkin.Font(12.5f, true), Color.White, new RectangleF(x, top - 16 * k, nw, 16 * k));
            ModernSkin.Text(g, sub, ModernSkin.Font(10.5f), ModernTheme.Dim, new RectangleF(x, top, nw, 15 * k));
            DrawTag(g, w - tagW - 10 * k, top, r, own);
            if (bar)
            {
                RectangleF lb = new RectangleF(x, h - 9 * k, w - x - 10 * k, 4 * k);
                using (var br = new SolidBrush(Color.FromArgb(255, 4, 6, 10))) g.FillRectangle(br, lb);
                using (var br = new SolidBrush(days <= 5 ? ModernTheme.BadSoft : ModernTheme.Accent)) g.FillRectangle(br, lb.X, lb.Y, lb.Width * fill, lb.Height);
            }
        }

        private static void Num(Graphics g, RectangleF r, int no)
        {
            float k = ModernRoot.K;
            using (GraphicsPath p = ModernSkin.RoundRect(r, 8 * k))
            {
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(r.X - r.Width * 0.2f, r.Y - r.Height * 0.3f, r.Width * 1.4f, r.Height * 1.4f);
                    using var br = new PathGradientBrush(path) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) }, CenterPoint = new PointF(r.X + r.Width / 2, r.Y + r.Height * 0.35f) };
                    g.FillPath(br, p);
                }
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            ModernSkin.Text(g, no.ToString(), ModernSkin.Font(14, true), ModernTheme.Accent, r, StringAlignment.Center);
        }

        private (string Text, Color Colour, bool Solid) TagOf(ClientGTMap r, bool own)
        {
            Status s = StatusOf(r);
            if (own)
                return s == Status.Owned ? (W.T(ClientTextKeys.ModernGuildGtYours), ModernTheme.Gold, true)
                    : s == Status.Sale ? (W.T(ClientTextKeys.ModernGuildGtYoursForSale), ModernTheme.Gold, false) : (W.T(ClientTextKeys.ModernGuildGtKeysPending), ModernTheme.Accent, false);
            return s switch
            {
                Status.Vacant => (W.T(ClientTextKeys.ModernGuildGtAvailable), ModernTheme.Ok, false),
                Status.Sale => (W.T(ClientTextKeys.ModernGuildGtForSale), ModernTheme.Gold, false),
                Status.Pend => (W.T(ClientTextKeys.ModernGuildGtPending), ModernTheme.Accent, false),
                _ => (W.T(ClientTextKeys.ModernGuildGtUnavailable), ModernTheme.Dim, false),
            };
        }

        private float TagWidth(ClientGTMap r, bool own) => ModernSkin.Measure(TagOf(r, own).Text.ToUpperInvariant(), ModernSkin.Font(9.5f, true)) + 11 * ModernRoot.K;

        private void DrawTag(Graphics g, float x, float cy, ClientGTMap r, bool own)
        {
            var t = TagOf(r, own);
            Gd.Tag(g, x, cy, t.Text, t.Colour, t.Solid, true, 9.5f);
        }

        private void RenderDetail(ClientGTMap gt)
        {
            for (int i = _det.Controls.Count - 1; i >= 0; i--) _det.Controls[i].Dispose();
            int dw = _detW - 32, x = 16;
            if (gt == null)
            {
                W.Label(_det, W.T(_book.Received ? ClientTextKeys.ModernGuildGtSelect : ClientTextKeys.ModernGuildGtLoading), x, 16, dw, 18, 11, false, ModernTheme.Dim);
                return;
            }
            int no = Number(gt);
            bool own = MyGuild.Length > 0 && gt.Owner == MyGuild;
            Status s = StatusOf(gt);
            ServerPackets.ServerRules rules = Rules;
            var gs = GameScene.Models.Guild;

            // head
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                Num(g, new RectangleF(0, 0, 34 * k, 34 * k), no);
                float tw = TagWidth(gt, own);
                ModernSkin.Text(g, NameOf(gt, no), ModernSkin.Font(17, true), Color.White, new RectangleF(46 * k, -1 * k, b.Size.Width - 56 * k - tw, 22 * k));
                ModernSkin.Text(g, gt.Owner == TerritoryBook.None ? W.T(ClientTextKeys.ModernGuildGtUnclaimed) : W.T(ClientTextKeys.ModernGuildGtHeldBy, gt.Owner), ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(46 * k, 20 * k, b.Size.Width - 56 * k - tw, 15 * k));
                DrawTag(g, b.Size.Width - tw - 2, 17 * k, gt, own);
            }) { Parent = _det, Location = new Point(S(x), S(14)), Size = new Size(S(dw), S(36)), NotControl = true };

            // facts
            var kv = new List<(string K, string V, bool Gold)>();
            if (gt.Owner == TerritoryBook.None)
            {
                if (rules != null)
                {
                    kv.Add((W.T(ClientTextKeys.ModernGuildGtRent), W.T(ClientTextKeys.ModernGuildGtGuildGoldN, W.N(rules.BuyGTGold)), true));
                    kv.Add((W.T(ClientTextKeys.ModernGuildGtLease), Days(rules.GTDays), false));
                    kv.Add((W.T(ClientTextKeys.ModernGuildGtExtend), W.T(ClientTextKeys.ModernGuildGtExtendFor, W.N(rules.ExtendGT), Days(rules.GTDays)), false));
                }
                kv.Add((W.T(ClientTextKeys.ModernGuildGtRules), W.T(ClientTextKeys.ModernGuildGtRulesText), false));
            }
            else
            {
                kv.Add((W.T(ClientTextKeys.Guild), gt.Owner, false));
                kv.Add((W.T(ClientTextKeys.ModernGuildGtLeaders), Leaders(gt), false));
                int days = DaysLeft(gt);
                if (days >= 0) kv.Add((W.T(ClientTextKeys.ModernGuildGtLeaseLeft), W.T(ClientTextKeys.ModernGuildGtAbout, Days(days)), false));
                if (gt.price > 0 && s != Status.Pend) kv.Add((W.T(ClientTextKeys.ModernGuildGtAsking), W.T(ClientTextKeys.ModernGuildGtGuildGoldN, W.N(gt.price)), true));
                if (s == Status.Pend) kv.Add((W.T(ClientTextKeys.ModernGuildGtKeys), W.T(ClientTextKeys.ModernGuildGtKeysText), false));
                if (own && rules != null) kv.Add((W.T(ClientTextKeys.ModernGuildGtExtend), W.T(ClientTextKeys.ModernGuildGtExtendFor, W.N(rules.ExtendGT), Days(rules.GTDays)), false));
            }
            int kvW = _narrow ? 100 : 120;
            var lineH = new List<int>();
            int cardH = 32;
            foreach (var e in kv)
            {
                int lh = Math.Max(18, (int)Math.Ceiling(ModernSkin.MeasureWrapped(e.V, ModernSkin.Font(12, true), S(dw - 24 - kvW)).Height / ModernRoot.K) + 4);
                lineH.Add(lh);
                cardH += lh;
            }
            cardH += 6;
            string head = W.T(gt.Owner == TerritoryBook.None ? ClientTextKeys.ModernGuildGtLeaseTerms : ClientTextKeys.ModernGuildGtLease).ToUpperInvariant();
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                {
                    using (var br = new SolidBrush(ModernTheme.Mix(W.Page, Color.Black, 0.78f))) g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.7f));
                }
                ModernSkin.SpacedText(g, head, ModernSkin.Font(10, true), ModernTheme.Accent, new RectangleF(12 * k, 8 * k, ModernSkin.Measure(head, ModernSkin.Font(10, true)) + head.Length * 1.6f * k, 14 * k), 1.6f * k);
                float y = 30 * k;
                for (int i = 0; i < kv.Count; i++)
                {
                    float lh = lineH[i] * k;
                    ModernSkin.Text(g, kv[i].K, ModernSkin.Font(12), ModernTheme.Dim, new RectangleF(12 * k, y, kvW * k, 18 * k));
                    ModernSkin.Text(g, kv[i].V, ModernSkin.Font(12, true), kv[i].Gold ? ModernTheme.Gold : Color.White, new RectangleF((12 + kvW) * k, y, b.Size.Width - (24 + kvW) * k, lh), StringAlignment.Near, StringAlignment.Near, true, true);
                    y += lh;
                }
            }) { Parent = _det, Location = new Point(S(x), S(62)), Size = new Size(S(dw), S(cardH)), NotControl = true };

            // actions: refusals the client can tell are shown on the button (the server still checks everything)
            int ay = 62 + cardH + 12, ax = x;
            bool npc = AtNpc(), gated = false;
            string npcWhy = npc ? string.Empty : W.T(ClientTextKeys.ModernGuildGtSpeakKeeper);
            string lead = MyGuild.Length == 0 ? W.T(ClientTextKeys.ModernGuildGtNeedGuild) : !Leader ? W.T(ClientTextKeys.ModernGuildGtLeaderOnly) : string.Empty;
            ClientGTMap mine = Mine();
            void Btn(string text, string why, Action run, bool main = false)
            {
                var b = new ModernButton(text, main ? ButtonKind.Main : ButtonKind.Normal, true) { Parent = _det, FontSize = 12 };
                b.AutoWidth(80);
                if (S(ax) + b.Size.Width > S(x + dw)) { ax = x; ay += 38; }
                b.Location = new Point(S(ax), S(ay));
                if (!string.IsNullOrEmpty(why)) b.SetDisabled(true, why);
                b.Click += (o, e) => run();
                ax += (int)Math.Ceiling(b.Size.Width / ModernRoot.K) + 8;
            }
            string First(params string[] whys)
            {
                foreach (string w in whys) if (!string.IsNullOrEmpty(w)) return w;
                return string.Empty;
            }
            if (gt.Owner == TerritoryBook.None)
            {
                int cost = rules?.BuyGTGold ?? 0;
                string why = First(lead, mine != null ? W.T(ClientTextKeys.ModernGuildGtAlreadyOwn) : null, rules != null && gs.Gold < cost ? W.T(ClientTextKeys.ModernGuildGtFundsLow) : null, npcWhy);
                gated |= lead.Length == 0 && !npc;
                Btn(rules != null ? W.T(ClientTextKeys.ModernGuildGtRentFor, W.N(cost)) : W.T(ClientTextKeys.ModernGuildGtRent), why, () => Rent(gt, no), true);
            }
            else if (own)
            {
                gated |= !npc;
                Btn(W.T(ClientTextKeys.ModernGuildGtTeleport), First(npcWhy, s == Status.Pend ? W.T(ClientTextKeys.ModernGuildGtPendingWhy) : null), () => Act(GuildTerritoryActionKind.Teleport, -1), true);
                int ext = rules?.ExtendGT ?? 0;
                Btn(rules != null ? W.T(ClientTextKeys.ModernGuildGtExtendCost, W.N(ext)) : W.T(ClientTextKeys.ModernGuildGtExtend), First(lead, rules != null && gs.Gold < ext ? W.T(ClientTextKeys.GuildFundsInsufficient) : null, npcWhy), Extend);
                if (gt.price > 0 && s != Status.Pend) Btn(W.T(ClientTextKeys.ModernGuildGtCancelSale), lead, () => Act(GuildTerritoryActionKind.CancelSale, -1));
                else if (s != Status.Pend) Btn(W.T(ClientTextKeys.ModernGuildGtListForSale), lead, () => ListForSale(gt, no));
            }
            else if (s == Status.Sale)
            {
                gated |= lead.Length == 0 && !npc;
                Btn(W.T(ClientTextKeys.ModernGuildGtBuyFor, W.N(gt.price)), First(lead, mine != null ? W.T(ClientTextKeys.ModernGuildGtAlreadyOwn) : null, gs.Gold < gt.price ? W.T(ClientTextKeys.GuildFundsInsufficient) : null, npcWhy), () => Buy(gt, no), true);
            }
            else
            {
                W.Label(_det, W.T(s == Status.Pend ? ClientTextKeys.ModernGuildGtSalePending : ClientTextKeys.ModernGuildGtNotForSale), ax, ay + 6, dw, 18, 11, false, ModernTheme.Dim);
                ax += 120;
            }
            if (gt.Owner != TerritoryBook.None && !string.IsNullOrEmpty(gt.Leader) && gt.Leader != TerritoryBook.None && !own)
            {
                string leader = gt.Leader;
                Btn(W.T(ClientTextKeys.ModernGuildGtMailLeader), string.Empty, () => MailLeader(leader));
            }
            int ny = ay + 40;
            W.Label(_det, _why, x, ny, dw, 16, 11, false, ModernTheme.BadSoft);
            ny += 20;
            if (gated)
            {
                W.Label(_det, W.T(ClientTextKeys.ModernGuildGtGateNote), x, ny, dw, 32, 11, false, ModernTheme.Warn, StringAlignment.Near, true);
                ny += 36;
            }
            if (own) W.Label(_det, W.T(ClientTextKeys.ModernGuildGtStandInside), x, ny, dw, 32, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
        }

        // ------------------------------------------------------------------ actions (the server answers with chat lines and fresh pages)

        private void Act(GuildTerritoryActionKind kind, int index, uint price = 0)
        {
            _why = _book.Action(kind, index, price) ? string.Empty : W.T(ClientTextKeys.ModernGuildGtInvalid);
            _dirty = true;
        }

        private void Rent(ClientGTMap gt, int no)
        {
            var r = Rules;
            string name = NameOf(gt, no);
            int index = gt.index;
            ModernRoot.Instance?.Popups.Ask(r != null ? W.T(ClientTextKeys.ModernGuildGtRentAsk, name, W.N(r.BuyGTGold), Days(r.GTDays)) : W.T(ClientTextKeys.ModernGuildGtRentAskShort, name),
                yes => { if (yes && IsOpen) Act(GuildTerritoryActionKind.Rent, index); }, false, PopupKind.Question, W.T(ClientTextKeys.ModernGuildGtRentTitle));
        }

        private void Extend()
        {
            var r = Rules;
            ModernRoot.Instance?.Popups.Ask(r != null ? W.T(ClientTextKeys.ModernGuildGtExtendAsk, Days(r.GTDays), W.N(r.ExtendGT)) : W.T(ClientTextKeys.ModernGuildGtExtendAskShort),
                yes => { if (yes && IsOpen) Act(GuildTerritoryActionKind.Extend, -1); }, false, PopupKind.Question, W.T(ClientTextKeys.ModernGuildGtExtendTitle));
        }

        private void ListForSale(ClientGTMap gt, int no)
        {
            ModernRoot.Instance?.Popups.Input(W.T(ClientTextKeys.ModernGuildGtSellTitle), string.Empty, 14, text =>
            {
                if (text == null || !IsOpen) return;
                if (!Eco.Amount(text, out uint n) || n < MinSale || n > int.MaxValue)
                {
                    _why = W.T(ClientTextKeys.ModernGuildGtSellRange, W.N(MinSale), W.N(int.MaxValue));
                    _dirty = true;
                    return;
                }
                Act(GuildTerritoryActionKind.ListForSale, -1, n);
            }, W.T(ClientTextKeys.ModernGuildGtSellText, NameOf(gt, no), W.N(MinSale)));
        }

        private void Buy(ClientGTMap gt, int no)
        {
            string owner = gt.Owner;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernGuildGtBuyAsk, NameOf(gt, no), owner, W.N(gt.price)), yes =>
            {
                if (!yes || !IsOpen) return;
                _book.Purchase(owner); // GuildTerritoryDialog.BuyButton: by the owning guild's name
                _why = string.Empty;
                _dirty = true;
            }, false, PopupKind.Question, W.T(ClientTextKeys.ModernGuildGtBuyTitle));
        }

        /// <summary>mailButton.Click: MailComposeLetterDialog.ComposeMail(Leader) through the mail window.</summary>
        private void MailLeader(string name)
        {
            if (ModernRoot.Instance?.Wm.Get(MailWindow.WindowKey) is MailWindow mail) mail.ComposeTo(name);
            else { _why = W.T(ClientTextKeys.ModernGuildGtNoMail); _dirty = true; }
        }
    }
}
