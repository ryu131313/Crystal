// Ported from WebClient/src/ui/modern/windows/quests.ts (QuestsWindow): the new UI's quest log, a modern QuestDiaryDialog +
// QuestDetailDialog + the InventoryDialog quest grid. Quest V2 (Part B phase 3): everything comes from GameScene.Models.Quests.
// Tabs: Active = the runs (Active, Ready, Failed); Available = what the NPCs in view offer this player (S.QuestAvailability, no
// client-side accept rules); Completed = S.QuestHistory (names come with it; opening one without a definition sends
// C.QuestDefinitionRequest once); Items = User.QuestInventory (display only). Each grouped by category in folding groups, with a
// search (name, group, summary, objectives). The detail: the summary (quest markup: NpcRichText), the quest / step time left or
// the repeat timer (S.QuestHistory.AvailableAt, refreshed once a second), the step list (done / current / future) with the current
// step's text and objectives (client-built text, count and bar; hidden ones once they have progress), the branch choice when the
// step is met (C.QuestChoose at the NPC in view), and the rewards (quest scope; choice groups picked here or in the NPC window,
// sent with C.QuestTurnIn). Actions: Track / Untrack (TrackerHud, at most 5, saved per character), Share (C.QuestShare; off without
// a group), Map (the endpoint NPC on the big map when its info is known), Turn in / Accept (only with the NPC on screen: the server
// checks the range; otherwise "Speak to <NPC>"), Abandon (when the quest allows it, or to clear a failed run). No map markers
// (Part B phase 5). The shared-quest invitation is ModernUi.QuestOffer's toast.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class QuestsWindow : ModernWindow
    {
        public const string WindowKey = "quests";
        private const int WidthHud = 660, HeightHud = 446, LeftW = 300, ShareRestMs = 3000, ItemCols = 8;

        private enum Tab { Active, Available, Completed, Items }

        private Tab _tab = Tab.Active;
        private readonly Dictionary<Tab, int> _picked = new Dictionary<Tab, int> { { Tab.Active, -1 }, { Tab.Available, -1 }, { Tab.Completed, -1 } };
        private readonly HashSet<string> _shut = new HashSet<string>();
        private readonly Dictionary<int, long> _shared = new Dictionary<int, long>();
        private string _query = string.Empty;
        private bool _listDirty = true, _detailDirty = true;
        private long _nextTick;

        private readonly PaintBox[] _segs = new PaintBox[4];
        private readonly int[] _counts = { -1, -1, -1, -1 };
        private SearchBox _search;
        private ScrollView _list, _detail;
        private ModernLabel _foot;
        private ModernPanel _actions, _itemsPane;
        private ModernLabel _itemsNote;
        private readonly List<ModernItemCell> _itemCells = new List<ModernItemCell>();
        private readonly List<(PaintBox Row, int Quest)> _rows = new List<(PaintBox, int)>();
        private readonly Action _quests, _group;
        private readonly Action<(int, bool)> _added;
        private readonly Action<S.QuestRefused> _refused;

        // the detail's live bits: the timer line (text refreshed once a second) and the pending state the buttons show
        private ModernLabel _timer;
        private Entry _timerEntry;
        private bool _pendingShown;
        private int _detailQuest = -1;

        private readonly List<S.QuestDefinition.Objective> _objectives = new List<S.QuestDefinition.Objective>();
        private readonly List<S.QuestDefinition.Branch> _branches = new List<S.QuestDefinition.Branch>();
        private readonly List<int> _offered = new List<int>();

        public QuestsWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinQuests), 100, 50)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _quests = () => { _listDirty = _detailDirty = true; };
            _added = _ => { _listDirty = _detailDirty = true; };
            _group = () => _detailDirty = true;
            // Review C2: a refused share re-enables Share at once instead of after the 3 s rest.
            _refused = p => { if (p != null && p.Request == QuestRequestKind.Share && _shared.Remove(p.Quest)) _detailDirty = true; };
        }

        private static UserObject User => GameScene.User;
        private static QuestBook Book => GameScene.Models?.Quests;
        private static TrackerHud Tracker => Soc.Ui?.Tracker;
        private static bool IsTracked(int id) => Tracker?.IsTracked(id) ?? false;
        private static int TrackedCount => Tracker?.Tracked.Count ?? 0;

        // ---- for the UI test
        internal string TabShown => _tab.ToString();
        internal int PickedShown => _tab == Tab.Items ? -1 : _picked[_tab];
        internal MirControl Row(int i) => i >= 0 && i < _rows.Count ? _rows[i].Row : null;
        internal int RowQuest(int i) => i >= 0 && i < _rows.Count ? _rows[i].Quest : -1;
        internal ModernPanel Actions => _actions;
        internal ScrollView Detail => _detail;
        internal bool IsPending(int id) => QuestActions.IsPending(id);
        internal string TimerShown => _timer != null && !_timer.IsDisposed ? _timer.Text : string.Empty;

        /// <summary>The HUD tracker's click: the Active tab with this quest selected.</summary>
        public void FocusQuest(int quest)
        {
            SetTab(Tab.Active);
            if (_query.Length > 0) { _query = string.Empty; _search?.SetQuery(string.Empty); }
            _picked[Tab.Active] = quest;
            _listDirty = _detailDirty = true;
        }

        protected override void Build()
        {
            int top = TitleHeight;
            var names = new[] { W.T(ClientTextKeys.ModernSocialQActive), W.T(ClientTextKeys.ModernSocialQAvailable), W.T(ClientTextKeys.ModernSocialQCompleted), W.T(ClientTextKeys.ModernSocialQItems) };
            // the .seg strip: four equal buttons with a neutral count each (the kit's Tabs badges read as alerts)
            int segW = (LeftW - 20) / 4;
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                string label = names[i];
                _segs[i] = new PaintBox((g, b) => PaintSeg(g, b, label, idx)) { Parent = this, Location = new Point(S(12 + i * segW), S(top + 10)), Size = new Size(S(segW + (i < 3 ? 1 : 0)), S(26)) };
                _segs[i].Click += (o, e) => SetTab((Tab)idx);
            }
            _search = W.At(new SearchBox(LeftW - 20, W.T(ClientTextKeys.ModernSocialQSearch)), this, 12, top + 44);
            if (_query.Length > 0) _search.Text = _query;
            _search.Search += q => { _query = q.ToLowerInvariant(); _listDirty = _detailDirty = true; };
            _list = new ScrollView(LeftW - 16, HeightHud - 74 - 28) { Parent = this, Location = new Point(S(12), S(top + 74)), Background = W.Page };
            _list.Framed = false;
            _foot = W.Label(this, string.Empty, 12, top + HeightHud - 24, LeftW - 20, 16, 11, false, ModernTheme.Dim);
            W.VDivider(this, LeftW, top, HeightHud);

            int dw = WidthHud - LeftW - 1;
            _detail = new ScrollView(dw, HeightHud - 50) { Parent = this, Location = new Point(S(LeftW + 1), S(top)), Background = W.Page };
            _detail.Framed = false;
            W.Divider(this, LeftW + 1, top + HeightHud - 50, dw);
            _actions = new ModernPanel { Parent = this, Location = new Point(S(LeftW + 1), S(top + HeightHud - 49)), Size = new Size(S(dw), S(49)) };

            _itemsPane = new ModernPanel { Parent = this, Location = new Point(S(LeftW + 1), S(top)), Size = new Size(S(dw), S(HeightHud)), Visible = false };
            W.Caption(_itemsPane, W.T(ClientTextKeys.ModernSocialQItemsTitle), 16, 14, dw - 32);
            _itemsNote = W.Label(_itemsPane, string.Empty, 16, 32, dw - 32, 16, 11, false, ModernTheme.Dim);
            _itemCells.Clear();
            int n = Math.Max(40, User?.QuestInventory?.Length ?? 40);
            for (int i = 0; i < n; i++)
            {
                var c = new ModernItemCell(MirGridType.QuestInventory, i, 38) { Parent = _itemsPane, Location = new Point(S(14 + i % ItemCols * 42), S(56 + i / ItemCols * 42)), ReadOnly = true };
                _itemCells.Add(c);
            }
            _listDirty = _detailDirty = true;
            Render();
        }

        protected override void Shown()
        {
            ModernEvents.Quests.On(_quests);
            ModernEvents.QuestAdded.On(_added);
            GameScene.Models.Group.Changed.On(_group);
            GameScene.Models.Quests.Refused.On(_refused);
            _listDirty = _detailDirty = true;
        }

        protected override void Closed()
        {
            ModernEvents.Quests.Off(_quests);
            ModernEvents.QuestAdded.Off(_added);
            GameScene.Models?.Group.Changed.Off(_group);
            GameScene.Models?.Quests.Refused.Off(_refused);
            ModernTooltip.HideAll();
        }

        public override void Process()
        {
            base.Process();
            if (CMain.Time >= _nextTick)
            {
                _nextTick = CMain.Time + 1000;
                if (Expire(_shared)) _detailDirty = true;
                if (_detailQuest >= 0 && QuestActions.IsPending(_detailQuest) != _pendingShown) _detailDirty = true;
                if (!_detailDirty) Tick();
            }
            if (_listDirty || _detailDirty) Render();
        }

        private static bool Expire(Dictionary<int, long> d)
        {
            if (d.Count == 0) return false;
            List<int> gone = null;
            foreach (var kv in d) if (kv.Value <= CMain.Time) (gone ??= new List<int>()).Add(kv.Key);
            if (gone == null) return false;
            foreach (int i in gone) d.Remove(i);
            return true;
        }

        private void SetTab(Tab t)
        {
            if (t == _tab) return;
            _tab = t;
            for (int i = 0; i < _segs.Length; i++) _segs[i]?.Invalidate();
            ModernTooltip.HideAll();
            _listDirty = _detailDirty = true;
        }

        // ------------------------------------------------------------------ data

        private sealed class Entry
        {
            public int Quest;
            public S.QuestDefinition Def;
            public QuestRun Run;
            public S.QuestHistory.Entry Done;
            public string Name;
            public QuestCategory Category;
            /// <summary>Available: the NPC (ObjectID) that offers it.</summary>
            public uint Npc;
            public bool Ready => Run != null && Run.Status == QuestRunStatus.Ready;
            public bool Failed => Run != null && Run.Status == QuestRunStatus.Failed;
        }

        private List<Entry> Entries(Tab t)
        {
            var list = new List<Entry>();
            QuestBook book = Book;
            if (book == null || User == null) return list;
            if (t == Tab.Active)
            {
                for (int i = 0; i < book.Runs.Count; i++)
                {
                    QuestRun run = book.Runs[i];
                    S.QuestDefinition def = book.Def(run.Quest);
                    if (def == null || !QuestGfx.InLog(def)) continue;
                    list.Add(new Entry { Quest = run.Quest, Def = def, Run = run, Done = book.Done(run.Quest), Name = def.Name, Category = def.Category });
                }
                return list;
            }
            if (t == Tab.Completed)
            {
                for (int i = book.History.Count - 1; i >= 0; i--)
                {
                    S.QuestHistory.Entry h = book.History[i];
                    S.QuestDefinition def = book.Def(h.Quest);
                    if (def != null && !QuestGfx.InLog(def)) continue;
                    list.Add(new Entry { Quest = h.Quest, Def = def, Done = h, Name = def?.Name ?? h.Name, Category = def?.Category ?? QuestCategory.Side });
                }
                return list;
            }
            book.Offered(_offered);
            for (int i = 0; i < _offered.Count; i++)
            {
                S.QuestDefinition def = book.Def(_offered[i]);
                if (def == null || !QuestGfx.InLog(def)) continue;
                list.Add(new Entry { Quest = def.Quest, Def = def, Done = book.Done(def.Quest), Name = def.Name, Category = def.Category, Npc = book.NpcOffering(def.Quest) });
            }
            return list;
        }

        private static bool InView(uint npc) => npc != 0 && MapControl.Objects != null && MapControl.Objects.ContainsKey(npc);

        private static string NpcObjectName(uint npc) =>
            npc != 0 && MapControl.Objects != null && MapControl.Objects.TryGetValue(npc, out MapObject ob) ? QuestText.NpcName(ob.Name) : string.Empty;

        private static string Giver(Entry e, QuestEndpointRole role)
        {
            string name = QuestText.EndpointName(e.Def, role);
            if (name.Length == 0 && role == QuestEndpointRole.Start) name = NpcObjectName(e.Npc);
            return name.Length > 0 ? name : W.T(ClientTextKeys.ModernChatTrackQuestGiver);
        }

        private bool Matches(Entry e)
        {
            string s = _query;
            if (s.Length == 0) return true;
            if ((e.Name ?? string.Empty).ToLowerInvariant().Contains(s)) return true;
            S.QuestDefinition d = e.Def;
            if (d == null) return false;
            if ((d.GroupName ?? string.Empty).ToLowerInvariant().Contains(s) || (d.Summary ?? string.Empty).ToLowerInvariant().Contains(s)) return true;
            if (QuestText.EndpointName(d, QuestEndpointRole.Start).ToLowerInvariant().Contains(s)) return true;
            for (int i = 0; i < d.Objectives.Count; i++) if (QuestText.Label(d.Objectives[i]).ToLowerInvariant().Contains(s)) return true;
            return false;
        }

        private Entry Selected(Tab t, List<Entry> list)
        {
            foreach (Entry e in list) if (e.Quest == _picked[t]) return e;
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>The steps in display order.</summary>
        private static List<S.QuestDefinition.Step> Steps(S.QuestDefinition def)
        {
            var steps = new List<S.QuestDefinition.Step>(def.Steps);
            steps.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));
            return steps;
        }

        /// <summary>The short status line of a row (Ready: where to return; Failed; else the first open objective).</summary>
        private string RowWhere(Entry e)
        {
            QuestBook book = Book;
            if (_tab == Tab.Available) return Giver(e, QuestEndpointRole.Start);
            if (_tab == Tab.Completed)
            {
                string done = e.Done != null && e.Done.Times > 1 ? W.T(ClientTextKeys.ModernQuestDoneTimes, e.Done.Times) : W.T(ClientTextKeys.ModernSocialQCompleted);
                long again = e.Done != null ? QuestText.SecondsLeft(e.Done.AvailableAt) : -1;
                return again > 0 ? done + " · " + W.T(ClientTextKeys.ModernQuestAgainIn, W.Duration(again)) : done;
            }
            if (e.Ready) return W.T(ClientTextKeys.ModernSocialQReturnTo, Giver(e, QuestEndpointRole.Finish));
            if (e.Failed) return W.T(ClientTextKeys.ModernQuestFailed);
            if (e.Run.Step >= 0)
            {
                QuestBook.ObjectivesOf(e.Def, e.Run.Step, _objectives);
                for (int i = 0; i < _objectives.Count; i++)
                    if (book.Shown(e.Quest, _objectives[i]) && !book.ObjectiveDone(e.Quest, _objectives[i]))
                        return QuestText.Line(_objectives[i], book.Value(e.Quest, _objectives[i].Index));
                S.QuestDefinition.Step step = QuestBook.StepOf(e.Def, e.Run.Step);
                if (step != null && step.Title.Length > 0) return step.Title;
            }
            return e.Def.GroupName ?? string.Empty;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            if (_list == null || _list.IsDisposed || User == null || Book == null) { _listDirty = _detailDirty = false; return; }
            List<Entry> active = Entries(Tab.Active), available = Entries(Tab.Available);
            SetCount(0, active.Count);
            SetCount(1, available.Count);
            SetCount(2, Book.History.Count);
            int items = 0;
            foreach (UserItem it in User.QuestInventory) if (it != null) items++;
            SetCount(3, items);
            bool itemsTab = _tab == Tab.Items;
            _search.Visible = !itemsTab;
            _detail.Visible = _actions.Visible = !itemsTab;
            _itemsPane.Visible = itemsTab;
            if (itemsTab)
            {
                if (_listDirty)
                {
                    Soc.Clear(_list.Content);
                    _rows.Clear();
                    W.Label(_list.Content, W.T(ClientTextKeys.ModernSocialQItemsHelp), 4, 20, LeftW - 30, 60, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
                    _list.FitContent(0);
                    _foot.Text = string.Empty;
                }
                for (int i = 0; i < _itemCells.Count; i++)
                {
                    _itemCells[i].Visible = i < User.QuestInventory.Length;
                    _itemCells[i].Refresh(false);
                }
                _itemsNote.Text = items > 0 ? W.T(items == 1 ? ClientTextKeys.ModernEcoOneItem : ClientTextKeys.ModernEcoNItems, items) : W.T(ClientTextKeys.ModernSocialQNoItems);
                _listDirty = _detailDirty = false;
                _timer = null;
                _detailQuest = -1;
                return;
            }
            List<Entry> source = _tab == Tab.Active ? active : _tab == Tab.Available ? available : Entries(_tab);
            var list = new List<Entry>();
            foreach (Entry e in source) if (Matches(e)) list.Add(e);
            Entry sel = Selected(_tab, list);
            if (sel != null) _picked[_tab] = sel.Quest;
            if (_listDirty) { _listDirty = false; RenderList(list, sel); }
            if (_detailDirty) { _detailDirty = false; RenderDetail(sel); }
        }

        private void SetCount(int i, int n)
        {
            if (_counts[i] == n) return;
            _counts[i] = n;
            _segs[i]?.Invalidate();
        }

        private void PaintSeg(Graphics g, PaintBox b, string label, int i)
        {
            float k = ModernRoot.K;
            bool on = (int)_tab == i;
            RectangleF r = new RectangleF(0, 0, b.Size.Width, b.Size.Height);
            Color fill = on ? ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Accent), ModernSkin.Opaque(ModernTheme.PanelB), 0.24f)
                : ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.6f);
            using (GraphicsPath p = Corners(r, ModernTheme.Radius * 0.8f * k, i == 0, i == 3))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ModernSkin.Opaque(ModernTheme.Frame));
            }
            if (on) using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, 1, b.Size.Height - 2 * k - 1, b.Size.Width - 2, 2 * k);
            // the narrow strip: a smaller label, then no count, when it does not fit (quests.ts narrow layout hides the counts)
            Font f = ModernSkin.Font(11, true), cf = ModernSkin.Font(9);
            string count = _counts[i] >= 0 ? _counts[i].ToString() : string.Empty;
            float lw = ModernSkin.Measure(label, f) + 2, cw = count.Length > 0 ? ModernSkin.Measure(count, cf) + 8 * k : 0;
            float room = b.Size.Width - 6 * k;
            if (lw + (cw > 0 ? 3 * k + cw : 0) > room) { f = ModernSkin.Font(10, true); lw = ModernSkin.Measure(label, f) + 2; }
            if (lw + (cw > 0 ? 3 * k + cw : 0) > room) cw = 0;
            float total = lw + (cw > 0 ? 3 * k + cw : 0), x = (b.Size.Width - total) / 2f;
            Color tc = on ? Color.White : b.Hovered ? ModernTheme.Text : ModernTheme.Dim;
            ModernSkin.Text(g, label, f, tc, new RectangleF(x, 0, lw, b.Size.Height));
            if (cw <= 0) return;
            RectangleF pill = new RectangleF(x + lw + 3 * k, b.Size.Height / 2f - 6.5f * k, cw, 13 * k);
            using (GraphicsPath pp = ModernSkin.RoundRect(pill, 6.5f * k))
            using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Frame), fill, 0.5f))) g.FillPath(br, pp);
            ModernSkin.Text(g, count, cf, ModernTheme.Text, pill, StringAlignment.Center, StringAlignment.Center, false);
        }

        /// <summary>A rectangle with rounded corners on the left and / or right end only (the segmented strip's ends).</summary>
        private static GraphicsPath Corners(RectangleF r, float rad, bool left, bool right)
        {
            r = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
            var p = new GraphicsPath();
            float d = rad * 2;
            if (left) p.AddArc(r.X, r.Y, d, d, 180, 90); else p.AddLine(r.X, r.Y, r.X, r.Y);
            if (right) { p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); }
            else { p.AddLine(r.Right, r.Y, r.Right, r.Bottom); }
            if (left) p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); else p.AddLine(r.X, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }

        internal MirControl Seg(int i) => i >= 0 && i < _segs.Length ? _segs[i] : null;

        private void RenderList(List<Entry> list, Entry sel)
        {
            int keep = _list.ScrollY;
            Soc.Clear(_list.Content);
            _rows.Clear();
            int y = 0, w = _list.InnerWidth;
            Tab t = _tab;
            int wh = (int)(w / ModernRoot.K);
            if (list.Count == 0)
            {
                string empty = W.T(_query.Length > 0 ? ClientTextKeys.ModernSocialQNoMatch : t == Tab.Active ? ClientTextKeys.ModernSocialQNoActive
                    : t == Tab.Available ? ClientTextKeys.ModernQuestNoAvailable : ClientTextKeys.ModernSocialQNoCompleted);
                W.Label(_list.Content, empty, 4, 24, wh - 8, 60, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
            }
            foreach (QuestCategory cat in QuestGfx.Categories)
            {
                var group = new List<Entry>();
                foreach (Entry e in list) if (e.Category == cat) group.Add(e);
                if (group.Count == 0) continue;
                string key = t + ":" + cat;
                bool shut = _shut.Contains(key);
                string name = QuestGfx.CategoryName(cat);
                string title = name.ToUpperInvariant(), count = group.Count.ToString();
                int icon = QuestGfx.CategoryIcon(cat);
                var head = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    Font f = ModernSkin.Font(10, true);
                    ModernSkin.Text(g, shut ? "▸" : "▾", f, ModernTheme.AccentDim, new RectangleF(4 * k, 0, 12 * k, b.Size.Height));
                    ModernSkin.SpacedText(g, title, f, ModernTheme.AccentDim, new RectangleF(36 * k, 0, ModernSkin.Measure(title, f) + title.Length * 1.4f * k, b.Size.Height), 1.4f * k);
                    ModernSkin.Text(g, count, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 4 * k, b.Size.Height), StringAlignment.Far);
                })
                {
                    Parent = _list.Content, Location = new Point(0, y + S(4)), Size = new Size(w, S(24)),
                    Hint = W.T(shut ? ClientTextKeys.ModernSocialQShowGroup : ClientTextKeys.ModernSocialQFoldGroup, name),
                    AfterPaint = b => W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(18), b.DisplayLocation.Y + S(5.5f), S(13), S(13)), ModernTheme.AccentDim),
                };
                head.Click += (o, e) => { if (!_shut.Remove(key)) _shut.Add(key); _listDirty = true; };
                y += S(30);
                if (shut) continue;
                foreach (Entry e in group) y = Row(e, e == sel, y, w);
            }
            _list.FitContent(0);
            _list.ScrollTo(keep);
            if (t == Tab.Active)
            {
                int ready = 0;
                foreach (Entry e in list) if (e.Ready) ready++;
                _foot.Text = W.T(ClientTextKeys.ModernSocialQReadyCount, ready) + "   ·   " + W.T(ClientTextKeys.ModernSocialQTracked, TrackedCount, TrackerHud.MaxTracked);
            }
            else if (t == Tab.Available) _foot.Text = W.T(ClientTextKeys.ModernSocialQTakeFromNpc);
            else _foot.Text = W.T(ClientTextKeys.ModernSocialQCompletedCount, Book.History.Count);
        }

        private int Row(Entry e, bool sel, int y, int w)
        {
            Tab t = _tab;
            bool ready = e.Ready, failed = e.Failed, done = t == Tab.Completed, busy = QuestActions.IsPending(e.Quest);
            int level = e.Def?.LevelHint ?? 0;
            bool low = t != Tab.Completed && level > 0 && User.Level - level > 10; // QuestSingleQuestItem: grey 10+ levels below
            string mark = done ? string.Empty : failed ? "✗" : ready ? "?" : t == Tab.Available ? "!" : "?";
            Color markC = failed ? ModernTheme.Hex("#ff6b5a") : ready || t == Tab.Available ? Color.FromArgb(0xff, 0xd7, 0x40) : Color.White;
            string where = RowWhere(e);
            string sub = (level > 0 ? W.T(ClientTextKeys.ModernHudPartyLevel, level) + (string.IsNullOrEmpty(where) ? string.Empty : " · ") : string.Empty) + where;
            string pill = ready ? W.T(ClientTextKeys.ModernSocialQReady) : failed ? W.T(ClientTextKeys.ModernQuestFailedTag) : string.Empty;
            Color pillC = failed ? ModernTheme.Hex("#ff6b5a") : ModernTheme.Accent;
            bool tracked = t == Tab.Active && IsTracked(e.Quest), full = t == Tab.Active && !tracked && TrackedCount >= TrackerHud.MaxTracked;
            int id = e.Quest;
            string name = e.Name;
            var row = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
                {
                    Color fill = sel ? W.Over(ModernTheme.Accent, 0.14f) : b.Hovered ? W.Over(ModernTheme.Accent, 0.08f) : W.Over(ModernTheme.FrameLight, 0.05f);
                    using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                    if (sel || b.Hovered) ModernSkin.Stroke(g, p, sel ? ModernTheme.Accent : W.Over(ModernTheme.Accent, 0.5f));
                }
                using (var br = new SolidBrush(sel || ready ? ModernTheme.Accent : W.Over(ModernTheme.Accent, 0.4f))) g.FillRectangle(br, 3 * k, 7 * k, 2 * k, b.Size.Height - 14 * k);
                float dim = busy ? 0.4f : 0f;
                Color D(Color c) => dim > 0 ? ModernSkin.Dim(c, dim) : c;
                ModernSkin.Text(g, mark, ModernSkin.Font(12, true), D(markC), new RectangleF(10 * k, 0, 12 * k, b.Size.Height), StringAlignment.Center);
                float right = b.Size.Width - 6 * k - (t == Tab.Active ? 24 * k : 0);
                if (pill.Length > 0) right -= Soc.Pill(g, right, b.Size.Height / 2f - 7 * k, pill, pillC, 8.5f, ready, true, 14) + 6 * k;
                float x = 28 * k, tw = right - x;
                ModernSkin.Text(g, name, ModernSkin.Font(12, true), D(done || low ? ModernTheme.Dim : ModernTheme.Text), new RectangleF(x, 6 * k, tw, 16 * k));
                ModernSkin.Text(g, sub, ModernSkin.Font(10), D(failed ? ModernTheme.Hex("#ff8a7a") : ModernTheme.Dim), new RectangleF(x, 23 * k, tw, 14 * k));
            })
            { Parent = _list.Content, Location = new Point(0, y), Size = new Size(w, S(44)) };
            row.Click += (o, ev) =>
            {
                if (_picked[t] == id) return;
                _picked[t] = id;
                _listDirty = _detailDirty = true;
            };
            _rows.Add((row, id));
            if (t == Tab.Active)
            {
                // the pin (track on screen)
                var pin = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    if (b.Hovered)
                        using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1), ModernTheme.Radius * 0.7f * k))
                            ModernSkin.Stroke(g, p, ModernSkin.Opaque(ModernTheme.Frame));
                })
                {
                    Parent = row, Location = new Point(w - S(28), S(11)), Size = new Size(S(22), S(22)),
                    Hint = tracked ? W.T(ClientTextKeys.ModernSocialQUntrack) : full ? W.T(ClientTextKeys.ModernSocialQTrackMax, TrackerHud.MaxTracked) : W.T(ClientTextKeys.ModernSocialQTrackOnScreen),
                    AfterPaint = b => W.DrawIcon(ModernIcons.Pin, new Rectangle(b.DisplayLocation.X + S(4), b.DisplayLocation.Y + S(4), S(14), S(14)),
                        tracked ? ModernTheme.Accent : full ? ModernSkin.Dim(ModernTheme.Dim, 0.6f) : ModernSkin.Dim(ModernTheme.Dim, 0.3f)),
                };
                pin.Click += (o, ev) => ToggleTrack(id);
            }
            return y + S(47);
        }

        private void RenderDetail(Entry e)
        {
            ModernTooltip.HideAll();
            int keep = e != null && e.Quest == _detailQuest ? _detail.ScrollY : 0;
            Soc.Clear(_detail.Content);
            Soc.Clear(_actions);
            _timer = null;
            _timerEntry = null;
            _detailQuest = e?.Quest ?? -1;
            _pendingShown = e != null && QuestActions.IsPending(e.Quest);
            int dw = WidthHud - LeftW - 1, w = dw - 32, y = 14;
            if (e == null)
            {
                W.Label(_detail.Content, W.T(ClientTextKeys.ModernSocialQPick), 16, 30, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
                _detail.FitContent(0);
                return;
            }
            Tab t = _tab;
            S.QuestDefinition def = e.Def;
            QuestBook book = Book;

            // head: icon + name, tags, giver lines
            new IconView(QuestGfx.CategoryIcon(e.Category), 16, ModernTheme.Accent) { Parent = _detail.Content, Location = new Point(S(16), S(y + 3)) };
            W.Label(_detail.Content, e.Name, 40, y, w - 24, 22, 17, true, Color.White);
            y += 28;
            if (def == null)
            {
                // a completed quest whose definition this connection has not had yet: ask once, show what the history row has
                book.RequestDefinition(e.Quest);
                W.Label(_detail.Content, W.T(ClientTextKeys.ModernQuestLoading), 16, y, w, 18, 11, false, ModernTheme.Dim);
                y += 24;
                y = RepeatLine(e, y, w);
                _detail.FitContent(0);
                W.Label(_actions, W.T(ClientTextKeys.ModernSocialQCompleted), 16, 15, 200, 18, 11, false, ModernTheme.Dim);
                return;
            }
            var tags = new List<(string Text, bool Hot, Color C)> { (QuestGfx.CategoryName(e.Category), false, ModernTheme.Accent) };
            if (def.LevelHint > 0) tags.Add((W.T(ClientTextKeys.ModernSocialQLevel, def.LevelHint), false, ModernTheme.Accent));
            if (!string.IsNullOrEmpty(def.GroupName)) tags.Add((def.GroupName, false, ModernTheme.Accent));
            if (e.Ready) tags.Add((W.T(ClientTextKeys.ModernSocialQReadyTurnIn), true, ModernTheme.Accent));
            else if (e.Failed) tags.Add((W.T(ClientTextKeys.ModernQuestFailedTag), true, ModernTheme.Hex("#ff6b5a")));
            else if (t == Tab.Active) tags.Add((W.T(ClientTextKeys.ModernSocialQInProgress), false, ModernTheme.Accent));
            if (t == Tab.Completed) tags.Add((W.T(ClientTextKeys.ModernSocialQCompleted), false, ModernTheme.Accent));
            if (t == Tab.Active && IsTracked(e.Quest)) tags.Add((W.T(ClientTextKeys.ModernSocialQTrackedTag), false, ModernTheme.Accent));
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K, x = 0;
                foreach (var tg in tags) x += Soc.Pill(g, x, 1 * k, tg.Text, tg.C, 9, tg.Hot, false, 15) + 5 * k;
            })
            { Parent = _detail.Content, Location = new Point(S(16), S(y)), Size = new Size(S(w), S(18)), NotControl = true };
            y += 24;
            string from = Giver(e, QuestEndpointRole.Start), to = Giver(e, QuestEndpointRole.Finish);
            y = GiverLine(W.T(ClientTextKeys.ModernSocialQFrom), from, y, w);
            if (to != from) y = GiverLine(W.T(ClientTextKeys.ModernSocialQTurnInTo), to, y, w);
            y += 8;

            if (!string.IsNullOrEmpty(def.Summary)) y = Text(def.Summary, y, w) + 10;

            // timers: the run's quest / step time left, the offer's limit, or the repeat timer
            if (t == Tab.Active && e.Run != null && (e.Run.ExpiresAt != 0 || e.Run.StepExpiresAt != 0) && !e.Failed)
            {
                W.Caption(_detail.Content, W.T(ClientTextKeys.ModernSocialQTimeLimit), 16, y, w);
                _timer = W.Label(_detail.Content, string.Empty, 16, y + 18, w, 18, 12, true, ModernTheme.Gold);
                _timerEntry = e;
                Tick();
                y += 44;
            }
            else if (t == Tab.Available && def.TimeLimitSeconds > 0)
            {
                W.Caption(_detail.Content, W.T(ClientTextKeys.ModernSocialQTimeLimit), 16, y, w);
                W.Label(_detail.Content, W.Duration(def.TimeLimitSeconds), 16, y + 18, w, 18, 12);
                y += 44;
            }
            else if (t == Tab.Completed) y = RepeatLine(e, y, w);

            y = StepList(e, y, w);
            y = Choices(e, y, w);

            W.Caption(_detail.Content, W.T(ClientTextKeys.ModernSocialQRewards), 16, y, w);
            y += 18;
            bool completedBefore = e.Done != null && t != Tab.Completed;
            y = QuestRewardsView.Build(_detail.Content, def, completedBefore, t == Tab.Active && e.Ready, 16, y, w, () => _detailDirty = true);
            new ModernPanel { Parent = _detail.Content, Location = new Point(0, S(y)), Size = new Size(1, 1) };
            _detail.FitContent(0);
            _detail.ScrollTo(keep);
            RenderActions(t, e, dw);
        }

        /// <summary>The once-a-second refresh of the timer line (text only: the label repaints alone).</summary>
        private void Tick()
        {
            if (_timer == null || _timer.IsDisposed || _timerEntry == null) return;
            Entry e = _timerEntry;
            if (e.Run != null)
            {
                long quest = QuestText.SecondsLeft(e.Run.ExpiresAt), step = QuestText.SecondsLeft(e.Run.StepExpiresAt);
                string text = quest >= 0 ? W.T(ClientTextKeys.ModernQuestTimeLeft, QuestGfx.Clock(quest)) : string.Empty;
                if (step >= 0) text += (text.Length > 0 ? "   ·   " : string.Empty) + W.T(ClientTextKeys.ModernQuestStepTimeLeft, QuestGfx.Clock(step));
                _timer.Text = text;
                _timer.Colour = (quest >= 0 && quest <= 60) || (step >= 0 && step <= 60) ? ModernTheme.Hex("#ff6b5a") : ModernTheme.Gold;
                return;
            }
            long again = e.Done != null ? QuestText.SecondsLeft(e.Done.AvailableAt) : -1;
            _timer.Text = again > 0 ? W.T(ClientTextKeys.ModernQuestAgainIn, QuestGfx.Clock(again)) : W.T(ClientTextKeys.ModernQuestAgainNow);
        }

        /// <summary>Completed tab: times done and, for a repeatable quest, when it can be taken again.</summary>
        private int RepeatLine(Entry e, int y, int w)
        {
            if (e.Done == null) return y;
            W.Label(_detail.Content, e.Done.Times > 1 ? W.T(ClientTextKeys.ModernQuestDoneTimes, e.Done.Times) : W.T(ClientTextKeys.ModernSocialQCompleted), 16, y, w, 18, 11, false, ModernTheme.Dim);
            y += 20;
            bool repeatable = e.Def != null ? e.Def.RepeatPolicy != QuestRepeatPolicy.Once : e.Done.AvailableAt != 0;
            if (!repeatable) return y + 6;
            _timer = W.Label(_detail.Content, string.Empty, 16, y, w, 18, 12, true, ModernTheme.Gold);
            _timerEntry = e;
            Tick();
            return y + 28;
        }

        /// <summary>The steps (done / current / future); the current one (the first, for an offer) with its text and objective bars.</summary>
        private int StepList(Entry e, int y, int w)
        {
            S.QuestDefinition def = e.Def;
            QuestBook book = Book;
            List<S.QuestDefinition.Step> steps = Steps(def);
            if (steps.Count == 0) return y;
            W.Caption(_detail.Content, W.T(ClientTextKeys.ModernQuestSteps), 16, y, w);
            y += 20;
            int currentOrder = int.MaxValue, current = -1;
            if (e.Run != null && e.Run.Step >= 0)
            {
                S.QuestDefinition.Step s = QuestBook.StepOf(def, e.Run.Step);
                if (s != null) { currentOrder = s.Order; current = s.Index; }
            }
            else if (_tab == Tab.Available) { currentOrder = steps[0].Order; current = steps[0].Index; }
            bool allDone = _tab == Tab.Completed || e.Ready || (e.Run != null && e.Run.Step < 0);
            for (int i = 0; i < steps.Count; i++)
            {
                S.QuestDefinition.Step s = steps[i];
                bool isCurrent = !allDone && s.Index == current, isDone = allDone || (!isCurrent && s.Order < currentOrder && current >= 0);
                string title = s.Title.Length > 0 ? s.Title : W.T(ClientTextKeys.ModernQuestStepN, i + 1);
                string glyph = isDone ? "✓" : isCurrent ? (e.Failed ? "✗" : "▸") : "○";
                Color gc = isDone ? ModernTheme.Hex("#5fe07a") : isCurrent ? (e.Failed ? ModernTheme.Hex("#ff6b5a") : ModernTheme.Accent) : ModernTheme.Dim;
                Color tc = isCurrent ? Color.White : isDone ? ModernTheme.Text : ModernTheme.Dim;
                new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    ModernSkin.Text(g, glyph, ModernSkin.Font(12, true), gc, new RectangleF(0, 0, 16 * k, b.Size.Height), StringAlignment.Center);
                    ModernSkin.Text(g, title, ModernSkin.Font(12, isCurrent), tc, new RectangleF(20 * k, 0, b.Size.Width - 20 * k, b.Size.Height));
                })
                { Parent = _detail.Content, Location = new Point(S(16), S(y)), Size = new Size(S(w), S(18)), NotControl = true };
                y += 20;
                if (!isCurrent) continue;
                if (s.Text.Length > 0) y = Text(s.Text, y, w - 20, 36) + 6;
                QuestBook.ObjectivesOf(def, s.Index, _objectives);
                for (int j = 0; j < _objectives.Count; j++)
                {
                    S.QuestDefinition.Objective o = _objectives[j];
                    if (e.Run != null && !book.Shown(e.Quest, o)) continue;
                    if (e.Run == null && o.Hidden) continue;
                    y = ObjectiveRow(e, o, y, w);
                }
                if (s.CompletionMode != QuestStepCompletion.All && _objectives.Count > 1)
                {
                    string mode = s.CompletionMode == QuestStepCompletion.Any ? W.T(ClientTextKeys.ModernQuestAnyOf) : W.T(ClientTextKeys.ModernQuestNOf, Math.Max(1, s.NRequired));
                    W.Label(_detail.Content, mode, 36, y, w - 20, 16, 10, false, ModernTheme.Dim);
                    y += 18;
                }
            }
            if (e.Ready)
            {
                // the last step's ready text, else the quest's turn-in text
                string text = string.Empty;
                for (int i = steps.Count - 1; i >= 0 && text.Length == 0; i--) text = steps[i].ReadyText ?? string.Empty;
                if (text.Length == 0) text = def.CompleteText ?? string.Empty;
                if (text.Length > 0) y = Text(text, y + 2, w, 16) + 6;
            }
            return y + 8;
        }

        private int ObjectiveRow(Entry e, S.QuestDefinition.Objective o, int y, int w)
        {
            QuestBook book = Book;
            int need = Math.Max(1, o.Count), have = e.Run != null ? Math.Min(book.Value(e.Quest, o.Index), need) : 0;
            bool done = e.Run != null && have >= need, bar = QuestText.Countable(o) && e.Run != null;
            string label = QuestText.Label(o);
            string count = QuestText.Countable(o) ? have + "/" + need : done ? "✓" : string.Empty;
            int h = bar ? 28 : 20;
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * 0.7f * k))
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.06f))) g.FillPath(br, p);
                Font f = ModernSkin.Font(11.5f), cf = ModernSkin.Font(11, true);
                float cw = count.Length > 0 ? ModernSkin.Measure(count, cf) + 4 : 0;
                Color tc = done ? ModernTheme.Hex("#7ff09a") : o.Optional ? ModernTheme.Dim : ModernTheme.Text;
                ModernSkin.Text(g, label, f, tc, new RectangleF(8 * k, 2 * k, b.Size.Width - 16 * k - cw, 16 * k));
                if (cw > 0) ModernSkin.Text(g, count, cf, tc, new RectangleF(b.Size.Width - 8 * k - cw, 2 * k, cw, 16 * k), StringAlignment.Far);
                if (bar) QuestGfx.Bar(g, new RectangleF(8 * k, 20 * k, b.Size.Width - 16 * k, 4 * k), have / (float)need, done);
            })
            { Parent = _detail.Content, Location = new Point(S(36), S(y)), Size = new Size(S(w - 20), S(h)), NotControl = true };
            return y + h + 4;
        }

        /// <summary>The branch choice of a met step (C.QuestChoose at an endpoint NPC on screen).</summary>
        private int Choices(Entry e, int y, int w)
        {
            if (_tab != Tab.Active || !Book.ChoiceDue(e.Quest)) return y;
            QuestBook.BranchesOf(e.Def, e.Run.Step, _branches);
            uint npc = Book.NpcChoosing(e.Quest);
            bool here = InView(npc), pending = QuestActions.IsPending(e.Quest);
            W.Caption(_detail.Content, W.T(ClientTextKeys.ModernQuestChoose), 16, y, w);
            y += 20;
            for (int i = 0; i < _branches.Count; i++)
            {
                S.QuestDefinition.Branch br = _branches[i];
                string text = br.Text.Length > 0 ? br.Text : br.ChoiceKey;
                var b = new ModernButton(text, ButtonKind.Main) { Parent = _detail.Content, FontSize = 11 };
                b.AutoWidth(120);
                b.Location = new Point(S(16), S(y));
                string key = br.ChoiceKey;
                int quest = e.Quest;
                if (!here) b.SetDisabled(true, W.T(ClientTextKeys.ModernQuestSpeakChoose, Giver(e, QuestEndpointRole.Finish)));
                else if (pending) b.SetDisabled(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                b.Click += (o, ev) => { QuestActions.Choose(quest, npc, key); _detailDirty = true; };
                y += 32;
            }
            return y + 6;
        }

        private int GiverLine(string label, string name, int y, int w)
        {
            new PaintBox((g, b) =>
            {
                Font f = ModernSkin.Font(11), bf = ModernSkin.Font(11, true);
                string lead = label + " ";
                float lw = ModernSkin.Measure(lead, f) + 1;
                ModernSkin.Text(g, lead, f, ModernTheme.Dim, new RectangleF(0, 0, lw, b.Size.Height));
                ModernSkin.Text(g, name, bf, ModernTheme.Text, new RectangleF(lw, 0, b.Size.Width - lw, b.Size.Height));
            })
            { Parent = _detail.Content, Location = new Point(S(16), S(y)), Size = new Size(S(w), S(16)), NotControl = true };
            return y + 18;
        }

        /// <summary>Quest text with the markup (colours, info links) as one rich text; returns its bottom (HUD px).</summary>
        private int Text(string text, int y, int w, int x = 16)
        {
            var rt = new NpcRichText(w, 12) { Parent = _detail.Content, Location = new Point(S(x), S(y)) };
            rt.SetRuns(Runs(text));
            WireTips(rt);
            return y + (int)Math.Ceiling(rt.Size.Height / ModernRoot.K);
        }

        private static List<NpcRun> Runs(string text)
        {
            var runs = new List<NpcRun>();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) runs.Add(new NpcRun { Text = "\n" });
                foreach (NpcSegment s in NpcPage.ParseLine(lines[i] ?? string.Empty))
                {
                    switch (s.Kind)
                    {
                        case NpcSegKind.Colour: runs.Add(new NpcRun { Text = s.Text, Colour = NpcFmt.Named(s.Key, Color.Empty) }); break;
                        case NpcSegKind.Info: runs.Add(new NpcRun { Kind = NpcSegKind.Info, Text = NpcWindow.LinkName(s.Key, s.Index, s.Name), Key = s.Key, Index = s.Index }); break;
                        default: runs.Add(new NpcRun { Text = s.Text }); break; // page / web links as plain text (QuestMessage only colours and links)
                    }
                }
            }
            return runs;
        }

        /// <summary>An [ITEM:n] link shows the item tooltip on hover.</summary>
        private static void WireTips(NpcRichText rt)
        {
            rt.RunHover += (run, at) =>
            {
                if (run < 0 || run >= rt.Runs.Count || rt.Runs[run].Kind != NpcSegKind.Info || rt.Runs[run].Key != "ITEM") { ModernTooltip.HideFor(rt); return; }
                ItemInfo info = QuestText.ItemInfo(rt.Runs[run].Index);
                if (info == null) return;
                ModernTooltip.Show(new UserItem(info) { Count = 1, CurrentDura = info.Durability, MaxDura = info.Durability }, rt);
            };
        }

        private void RenderActions(Tab t, Entry e, int dw)
        {
            int x = 16, ay = 11;
            ModernButton Act(string text, ButtonKind kind, Action run, string hint = null)
            {
                var b = new ModernButton(text, kind) { Parent = _actions, FontSize = 10 };
                b.AutoWidth(0);
                b.Size = new Size(b.Size.Width, S(26));
                b.Location = new Point(S(x), S(ay));
                x += (int)Math.Round(b.Size.Width / ModernRoot.K) + 5;
                if (hint != null) b.Tooltip = hint;
                b.Click += (o, ev) => run();
                return b;
            }
            if (t == Tab.Completed)
            {
                W.Label(_actions, W.T(ClientTextKeys.ModernSocialQCompleted), 16, ay + 4, 200, 18, 11, false, ModernTheme.Dim);
                return;
            }
            QuestBook book = Book;
            int id = e.Quest;
            bool pending = QuestActions.IsPending(id);
            string speak = string.Empty;
            if (t == Tab.Active)
            {
                bool tracked = IsTracked(id);
                if (e.Ready)
                {
                    uint npc = book.NpcFinishing(id);
                    bool picks = QuestActions.PicksComplete(e.Def, e.Done != null);
                    if (InView(npc))
                    {
                        var turn = Act(W.T(pending ? ClientTextKeys.ModernNpcHandingIn : ClientTextKeys.ModernNpcHandIn), ButtonKind.Main, () => { QuestActions.TurnIn(e.Def, npc, e.Done != null); _detailDirty = true; });
                        if (pending) turn.SetDisabled(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                        else if (!picks) turn.SetDisabled(true, W.T(ClientTextKeys.YouMustSelectRewardItem));
                    }
                    else speak = W.T(ClientTextKeys.ModernSocialQSpeakTurnIn, Giver(e, QuestEndpointRole.Finish));
                }
                var track = Act(W.T(tracked ? ClientTextKeys.ModernSocialQUntrack : ClientTextKeys.ModernSocialQTrack), ButtonKind.Normal, () => ToggleTrack(id),
                    W.T(tracked ? ClientTextKeys.ModernSocialQUntrackHint : ClientTextKeys.ModernSocialQTrackHint));
                if (!tracked && TrackedCount >= TrackerHud.MaxTracked) track.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQTrackMax, TrackerHud.MaxTracked));
                if (!e.Failed)
                {
                    bool shared = _shared.ContainsKey(id);
                    var share = Act(W.T(shared ? ClientTextKeys.ModernSocialQShared : ClientTextKeys.ModernSocialQShare), ButtonKind.Normal, () => Share(id), W.T(ClientTextKeys.ModernSocialQShareHint));
                    if (!QuestText.Shareable(e.Def)) share.SetDisabled(true, W.T(ClientTextKeys.ModernQuestNoShare));
                    else if (GameScene.Models.Group.Members.Count < 2) share.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQShareNoGroup));
                    else if (shared) share.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQShareWait));
                }
                MapButton(Act, e);
                bool canAbandon = e.Def.AbandonAllowed || e.Failed;
                var abandon = Act(W.T(pending && !e.Ready ? ClientTextKeys.ModernSocialQAbandoning : e.Failed ? ClientTextKeys.ModernQuestClear : ClientTextKeys.ModernSocialQAbandon), ButtonKind.Warn, () => Abandon(e),
                    W.T(ClientTextKeys.ModernSocialQAbandonHint));
                if (!canAbandon) abandon.SetDisabled(true, W.T(ClientTextKeys.ModernQuestNoAbandon));
                else if (pending) abandon.SetDisabled(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                if (e.Failed && speak.Length == 0) speak = W.T(ClientTextKeys.ModernQuestFailedHint);
            }
            else
            {
                uint npc = e.Npc;
                if (InView(npc))
                {
                    bool full = book.ActiveCount >= Globals.MaxConcurrentQuests;
                    var accept = Act(W.T(pending ? ClientTextKeys.ModernNpcAccepting : ClientTextKeys.ModernNpcAccept), ButtonKind.Main, () => { QuestActions.Accept(id, npc); _detailDirty = true; });
                    if (pending) accept.SetDisabled(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                    else if (full) accept.SetDisabled(true, W.T(ClientTextKeys.ModernNpcQuestFull, Globals.MaxConcurrentQuests));
                }
                else speak = W.T(ClientTextKeys.ModernSocialQSpeakAccept, Giver(e, QuestEndpointRole.Start));
                MapButton(Act, e);
            }
            if (speak.Length > 0)
            {
                // the turn-in / accept hint fills what the buttons leave, centred on the bar (up to three short lines)
                int sw = dw - 16 - x - 4, sh = Math.Min(42, Soc.WrapHeight(speak, 10, sw, true));
                W.Label(_actions, speak, x + 4, Math.Max(3, (49 - sh) / 2), sw, sh, 10, true, ModernTheme.Accent, StringAlignment.Far, sh > 16);
            }
        }

        /// <summary>"Map": the quest's NPC (finish when Ready, else start) on the big map, when its NPC info is known.</summary>
        private void MapButton(Func<string, ButtonKind, Action, string, ModernButton> act, Entry e)
        {
            S.QuestDefinition.Endpoint ep = QuestText.Endpoint(e.Def, e.Ready ? QuestEndpointRole.Finish : QuestEndpointRole.Start);
            ClientNPCInfo info = null;
            if (ep != null)
                for (int i = 0; i < GameScene.NPCInfoList.Count; i++) if (GameScene.NPCInfoList[i].Index == ep.TargetId) { info = GameScene.NPCInfoList[i]; break; }
            string who = ep != null ? QuestText.NpcName(ep.Name) : W.T(ClientTextKeys.ModernChatTrackQuestGiver);
            var find = act(W.T(ClientTextKeys.ModernSocialQMap), ButtonKind.Normal, () => FindOnMap(info), W.T(ClientTextKeys.ModernSocialQFindHint, who));
            if (info == null) find.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQNotMetHint));
        }

        // ------------------------------------------------------------------ actions

        /// <summary>QuestDiaryDialog right-click: QuestTrackingDialog.AddQuest / RemoveQuest (the tracker saves it per character).</summary>
        private void ToggleTrack(int id)
        {
            TrackerHud tr = Tracker;
            if (tr == null) return;
            if (tr.IsTracked(id)) tr.Track(id, false);
            else if (tr.Tracked.Count < TrackerHud.MaxTracked) tr.Track(id, true);
            _listDirty = _detailDirty = true;
        }

        /// <summary>C.QuestShare (the server offers it to group members nearby who can take it).</summary>
        private void Share(int id)
        {
            if (_shared.ContainsKey(id)) return;
            Network.Enqueue(new C.QuestShare { Quest = id });
            _shared[id] = CMain.Time + ShareRestMs;
            _detailDirty = true;
        }

        /// <summary>AskCancelQuest, then C.QuestAbandon (also clears a failed run); locked until the run changes.</summary>
        private void Abandon(Entry e)
        {
            int id = e.Quest;
            if (QuestActions.IsPending(id)) return;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.AskCancelQuest), yes =>
            {
                if (!yes || IsDisposed || Book?.Run(id) == null) return;
                QuestActions.Abandon(id);
                _listDirty = _detailDirty = true;
            }, false, PopupKind.Warning, W.T(ClientTextKeys.ModernSocialQAbandonTitle));
        }

        /// <summary>Opens the big map; when the NPC is on another map, a note says where it is.</summary>
        private static void FindOnMap(ClientNPCInfo n)
        {
            if (n == null) return;
            WindowManager wm = Soc.Wm;
            if (wm != null && wm.IsRegistered(BigMapWindow.WindowKey)) wm.Open(BigMapWindow.WindowKey);
            if (n.MapIndex == Soc.MapIndex) return;
            BigMapData data = Soc.Ui?.MapData;
            ClientMapInfo rec = data?.Map(n.MapIndex);
            if (rec == null && n.MapIndex > 0) data?.RequestMap(n.MapIndex);
            string where = rec?.Title ?? string.Empty;
            if (where.Length == 0) where = W.T(ClientTextKeys.ModernSocialQAnotherMap);
            ModernRoot.Instance?.Popups.Confirm(W.T(ClientTextKeys.ModernSocialQNpcIsIn, QuestText.NpcName(n.Name), where, n.Location.X, n.Location.Y), PopupButtons.Ok, PopupKind.Info, null, false, W.T(ClientTextKeys.ModernSocialQFindTitle));
        }
    }
}
