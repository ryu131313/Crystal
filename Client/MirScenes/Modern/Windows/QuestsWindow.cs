// Ported from WebClient/src/ui/modern/windows/quests.ts (QuestsWindow): the new UI's quest log, a modern QuestDiaryDialog +
// QuestDetailDialog + the InventoryDialog quest grid. Tabs: Active = User.CurrentQuests; Available = the quests the user could
// take now (NPCObject.CanAccept's rules: level range, class, the quest needed; not completed, not taken) from the NPCs met this
// session (QuestNpcs: NPC objects seen on screen, plus the current map's NPC list); Completed = User.CompletedQuests looked up
// in GameScene.QuestInfoList; Items = User.QuestInventory (display only). Each grouped by QuestType (Story / General / Daily /
// Repeatable) in folding groups, with a search (name, group, NPC, description, tasks). The detail: the quest text with the
// quest markup ({text/Colour}, [ITEM|MONSTER|NPC:n(|name)] links: NpcRichText), tasks, progress (ClientQuestProgress.TaskList),
// return, time limit, rewards (EXP / gold / credit chips, the fixed items, the choose-one items for your gender: the pick
// happens at the NPC). Actions: Track / Untrack (the HUD tracker: TrackerHud.Track, at most 5, saved per character), Share
// (C.ShareQuest; off without a group), Map (opens the big map; a note when the NPC is on another map), Abandon (AskCancelQuest,
// then C.AbandonQuest; the row stays locked until S.ChangeQuest, 5 s fallback). Accept / Turn in are not here: the server needs
// the NPC in range, so the window says "Speak to <NPC>". The shared-quest invitation is ModernUi.QuestShareRequest's toast.
// HIDDEN as on the web: the completed date, objective bars, local accept / turn-in state.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>The NPCs met this game (quests.ts metNpcs): ObjectID → name, map index and cell. Fed once a second by the social
    /// ticker (ModernUi.Social) from the NPC objects on screen.</summary>
    internal static class QuestNpcs
    {
        public sealed class Met
        {
            public string Name;
            public int Map;
            public Point Loc;
        }

        public static readonly Dictionary<uint, Met> Seen = new Dictionary<uint, Met>();
        /// <summary>Bumped when a new NPC was met (the Available tab may change).</summary>
        public static int Version;

        public static void Clear()
        {
            Seen.Clear();
            Version++;
        }

        /// <summary>Records the NPC objects on screen (cheap: one pass over the object list; call it on a tick).</summary>
        public static void Scan()
        {
            MapControl map = GameScene.Scene?.MapControl;
            if (map == null) return;
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is NPCObject n)) continue;
                if (Seen.TryGetValue(n.ObjectID, out Met m) && m.Map == map.Index && m.Name == n.Name) continue;
                Seen[n.ObjectID] = new Met { Name = n.Name, Map = map.Index, Loc = n.CurrentLocation };
                Version++;
            }
        }

        /// <summary>The NPC with this ObjectID: met this game, on the current map's NPC list, or null.</summary>
        public static Met Find(uint id)
        {
            if (Seen.TryGetValue(id, out Met m)) return m;
            int idx = Soc.MapIndex;
            ClientMapInfo info = idx >= 0 ? Soc.Ui?.MapData?.Map(idx) : null;
            if (info != null)
                for (int i = 0; i < info.NPCs.Count; i++)
                    if (info.NPCs[i].ObjectID == id) return new Met { Name = info.NPCs[i].Name, Map = idx, Loc = info.NPCs[i].Location };
            return null;
        }
    }

    public sealed class QuestsWindow : ModernWindow
    {
        public const string WindowKey = "quests";
        private const int WidthHud = 660, HeightHud = 446, LeftW = 300, PendingMs = 5000, ShareRestMs = 3000, ItemCols = 8;

        private enum Tab { Active, Available, Completed, Items }
        private static readonly QuestType[] Types = { QuestType.Story, QuestType.General, QuestType.Daily, QuestType.Repeatable };

        private Tab _tab = Tab.Active;
        private readonly Dictionary<Tab, int> _picked = new Dictionary<Tab, int> { { Tab.Active, -1 }, { Tab.Available, -1 }, { Tab.Completed, -1 } };
        private readonly HashSet<string> _shut = new HashSet<string>();
        private readonly Dictionary<int, long> _pending = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _shared = new Dictionary<int, long>();
        private string _query = string.Empty;
        private bool _listDirty = true, _detailDirty = true;
        private int _npcVersion = -1;
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
        private readonly Action<(ClientQuestProgress, bool)> _added;
        private readonly Action<(bool, bool, bool)> _gameData;

        public QuestsWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinQuests), 100, 50)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _quests = () => { _pending.Clear(); _listDirty = _detailDirty = true; };
            _added = _ => { _listDirty = _detailDirty = true; };
            _group = () => _detailDirty = true;
            _gameData = _ => { _listDirty = _detailDirty = true; };
        }

        private static UserObject User => GameScene.User;
        private static TrackerHud Tracker => Soc.Ui?.Tracker;
        private static bool IsTracked(int id) => Tracker?.IsTracked(id) ?? false;
        private static int TrackedCount => Tracker?.Tracked.Count ?? 0;

        // ---- for the UI test
        internal string TabShown => _tab.ToString();
        internal int PickedShown => _tab == Tab.Items ? -1 : _picked[_tab];
        internal MirControl Row(int i) => i >= 0 && i < _rows.Count ? _rows[i].Row : null;
        internal ModernPanel Actions => _actions;
        internal bool IsPending(int id) => _pending.ContainsKey(id);

        /// <summary>The HUD tracker's click: the Active tab with this quest (QuestInfo.Index) selected.</summary>
        public void FocusQuest(int questIndex)
        {
            SetTab(Tab.Active);
            if (_query.Length > 0) { _query = string.Empty; _search?.SetQuery(string.Empty); }
            _picked[Tab.Active] = questIndex;
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
            ModernEvents.GameData.On(_gameData);
            GameScene.Models.Group.Changed.On(_group);
            QuestNpcs.Scan();
            _listDirty = _detailDirty = true;
        }

        protected override void Closed()
        {
            ModernEvents.Quests.Off(_quests);
            ModernEvents.QuestAdded.Off(_added);
            ModernEvents.GameData.Off(_gameData);
            GameScene.Models?.Group.Changed.Off(_group);
            ModernTooltip.HideAll();
        }

        public override void Process()
        {
            base.Process();
            if (CMain.Time >= _nextTick)
            {
                _nextTick = CMain.Time + 1000;
                if (_npcVersion != QuestNpcs.Version) { _npcVersion = QuestNpcs.Version; _listDirty = _detailDirty = true; }
                if (Expire(_pending)) _listDirty = _detailDirty = true;
                if (Expire(_shared)) _detailDirty = true;
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
            public ClientQuestInfo Info;
            public ClientQuestProgress Progress;
        }

        private static ClientQuestInfo Info(int index)
        {
            List<ClientQuestInfo> all = GameScene.QuestInfoList;
            for (int i = 0; i < all.Count; i++) if (all[i].Index == index) return all[i];
            return null;
        }

        /// <summary>NPCObject.CanAccept.</summary>
        private static bool CanAccept(ClientQuestInfo q)
        {
            UserObject u = User;
            if (u == null || q.MinLevelNeeded > u.Level || q.MaxLevelNeeded < u.Level) return false;
            if (!q.ClassNeeded.HasFlag(RequiredClass.None))
            {
                RequiredClass need = u.Class switch
                {
                    MirClass.Warrior => RequiredClass.Warrior,
                    MirClass.Wizard => RequiredClass.Wizard,
                    MirClass.Taoist => RequiredClass.Taoist,
                    MirClass.Assassin => RequiredClass.Assassin,
                    MirClass.Archer => RequiredClass.Archer,
                    _ => RequiredClass.None,
                };
                if (need != RequiredClass.None && !q.ClassNeeded.HasFlag(need)) return false;
            }
            return q.QuestNeeded <= 0 || u.CompletedQuests.Contains(q.QuestNeeded);
        }

        private List<Entry> Entries(Tab t)
        {
            var list = new List<Entry>();
            UserObject u = User;
            if (u == null) return list;
            if (t == Tab.Active)
            {
                foreach (ClientQuestProgress p in u.CurrentQuests) if (p?.QuestInfo != null) list.Add(new Entry { Info = p.QuestInfo, Progress = p });
                return list;
            }
            if (t == Tab.Completed)
            {
                foreach (int id in u.CompletedQuests)
                {
                    ClientQuestInfo info = Info(id);
                    if (info != null) list.Add(new Entry { Info = info, Progress = new ClientQuestProgress { Id = id, QuestInfo = info, Taken = true, Completed = true } });
                }
                return list;
            }
            // Available: the NPCs met (and the current map's list) × NPCObject.GetAvailableQuests' accept rule
            var ids = new HashSet<uint>(QuestNpcs.Seen.Keys);
            int idx = Soc.MapIndex;
            ClientMapInfo map = idx >= 0 ? Soc.Ui?.MapData?.Map(idx) : null;
            if (map != null) foreach (ClientNPCInfo n in map.NPCs) ids.Add(n.ObjectID);
            var taken = new HashSet<int>();
            foreach (ClientQuestProgress p in u.CurrentQuests) if (p != null) taken.Add(p.Id);
            foreach (ClientQuestInfo q in GameScene.QuestInfoList)
            {
                if (!ids.Contains(q.NPCIndex) || taken.Contains(q.Index) || u.CompletedQuests.Contains(q.Index) || !CanAccept(q)) continue;
                list.Add(new Entry { Info = q, Progress = new ClientQuestProgress { Id = q.Index, QuestInfo = q } });
            }
            return list;
        }

        private static string NpcLabel(uint id)
        {
            QuestNpcs.Met n = QuestNpcs.Find(id);
            return n != null ? ChatGfx.NpcName(n.Name).Name : W.T(ClientTextKeys.ModernChatTrackQuestGiver);
        }

        private static string MapTitle(int index)
        {
            if (index == Soc.MapIndex) return Soc.MapTitle;
            BigMapData data = Soc.Ui?.MapData;
            ClientMapInfo rec = data?.Map(index);
            if (rec == null && index > 0) data?.RequestMap(index);
            return rec?.Title ?? string.Empty;
        }

        private bool Matches(Entry e)
        {
            string s = _query;
            if (s.Length == 0) return true;
            ClientQuestInfo i = e.Info;
            if ((i.Name ?? string.Empty).ToLowerInvariant().Contains(s) || (i.Group ?? string.Empty).ToLowerInvariant().Contains(s)) return true;
            if (NpcLabel(i.NPCIndex).ToLowerInvariant().Contains(s) || NpcLabel(i.FinishNPCIndex).ToLowerInvariant().Contains(s)) return true;
            foreach (string l in i.Description) if (l != null && l.ToLowerInvariant().Contains(s)) return true;
            foreach (string l in i.TaskDescription) if (l != null && l.ToLowerInvariant().Contains(s)) return true;
            return false;
        }

        private Entry Selected(Tab t, List<Entry> list)
        {
            foreach (Entry e in list) if (e.Info.Index == _picked[t]) return e;
            return list.Count > 0 ? list[0] : null;
        }

        private static string TypeName(QuestType t) => W.T(t switch
        {
            QuestType.Story => ClientTextKeys.ModernSocialQTypeStory,
            QuestType.Daily => ClientTextKeys.ModernSocialQTypeDaily,
            QuestType.Repeatable => ClientTextKeys.ModernSocialQTypeRepeatable,
            _ => ClientTextKeys.ModernSocialQTypeGeneral,
        });

        private static int TypeIcon(QuestType t) => t switch
        {
            QuestType.Story => ModernIcons.Star,
            QuestType.Daily => ModernIcons.Sun,
            QuestType.Repeatable => ModernIcons.Repeat,
            _ => ModernIcons.Scroll,
        };

        // ------------------------------------------------------------------ render

        private void Render()
        {
            if (_list == null || _list.IsDisposed || User == null) { _listDirty = _detailDirty = false; return; }
            QuestNpcs.Scan();
            List<Entry> available = Entries(Tab.Available);
            SetCount(0, User.CurrentQuests.Count);
            SetCount(1, available.Count);
            SetCount(2, User.CompletedQuests.Count);
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
                return;
            }
            List<Entry> source = _tab == Tab.Available ? available : Entries(_tab);
            var list = new List<Entry>();
            foreach (Entry e in source) if (Matches(e)) list.Add(e);
            Entry sel = Selected(_tab, list);
            if (sel != null) _picked[_tab] = sel.Info.Index;
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
            int y = 0, w = _list.InnerWidth, wh = (int)(w / ModernRoot.K);
            Tab t = _tab;
            if (list.Count == 0)
            {
                string empty = W.T(_query.Length > 0 ? ClientTextKeys.ModernSocialQNoMatch : t == Tab.Active ? ClientTextKeys.ModernSocialQNoActive
                    : t == Tab.Available ? ClientTextKeys.ModernSocialQNoAvailable : ClientTextKeys.ModernSocialQNoCompleted);
                W.Label(_list.Content, empty, 4, 24, wh - 8, 60, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
            }
            foreach (QuestType type in Types)
            {
                var group = new List<Entry>();
                foreach (Entry e in list) if (e.Info.Type == type) group.Add(e);
                if (group.Count == 0) continue;
                string key = t + ":" + type;
                bool shut = _shut.Contains(key);
                string title = TypeName(type).ToUpperInvariant(), count = group.Count.ToString();
                int icon = TypeIcon(type);
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
                    Hint = W.T(shut ? ClientTextKeys.ModernSocialQShowGroup : ClientTextKeys.ModernSocialQFoldGroup, TypeName(type)),
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
                foreach (Entry e in list) if (e.Progress.Completed) ready++;
                _foot.Text = W.T(ClientTextKeys.ModernSocialQReadyCount, ready) + "   ·   " + W.T(ClientTextKeys.ModernSocialQTracked, TrackedCount, TrackerHud.MaxTracked);
            }
            else if (t == Tab.Available) _foot.Text = W.T(ClientTextKeys.ModernSocialQTakeFromNpc);
            else _foot.Text = W.T(ClientTextKeys.ModernSocialQCompletedCount, User.CompletedQuests.Count);
        }

        private int Row(Entry e, bool sel, int y, int w)
        {
            Tab t = _tab;
            ClientQuestInfo info = e.Info;
            ClientQuestProgress prog = e.Progress;
            bool ready = t == Tab.Active && prog.Completed, done = t == Tab.Completed, busy = _pending.ContainsKey(info.Index);
            bool low = t != Tab.Completed && User.Level - info.MinLevelNeeded > 10; // QuestSingleQuestItem: grey 10+ levels below
            string mark = done ? string.Empty : ready ? "?" : t == Tab.Available ? "!" : prog.Taken || t == Tab.Active ? "?" : "!";
            Color markC = ready || t == Tab.Available ? Color.FromArgb(0xff, 0xd7, 0x40) : Color.White;
            string where = t == Tab.Active ? (ready ? W.T(ClientTextKeys.ModernSocialQReturnTo, NpcLabel(info.FinishNPCIndex)) : prog.TaskList.Count > 0 ? prog.TaskList[0] : info.Group ?? string.Empty)
                : t == Tab.Available ? NpcLabel(info.NPCIndex) : info.Group ?? string.Empty;
            string sub = W.T(ClientTextKeys.ModernHudPartyLevel, info.MinLevelNeeded) + (string.IsNullOrEmpty(where) ? string.Empty : " · " + where);
            string pill = ready ? W.T(ClientTextKeys.ModernSocialQReady) : t == Tab.Active && prog.New ? W.T(ClientTextKeys.ModernSocialQNew) : string.Empty;
            bool tracked = t == Tab.Active && IsTracked(info.Index), full = t == Tab.Active && !tracked && TrackedCount >= TrackerHud.MaxTracked;
            int id = info.Index;
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
                if (pill.Length > 0) right -= Soc.Pill(g, right, b.Size.Height / 2f - 7 * k, pill, ModernTheme.Accent, 8.5f, ready, true, 14) + 6 * k;
                float x = 28 * k, tw = right - x;
                ModernSkin.Text(g, info.Name, ModernSkin.Font(12, true), D(done || low ? ModernTheme.Dim : ModernTheme.Text), new RectangleF(x, 6 * k, tw, 16 * k));
                ModernSkin.Text(g, sub, ModernSkin.Font(10), D(ModernTheme.Dim), new RectangleF(x, 23 * k, tw, 14 * k));
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
            int keep = _detail.ScrollY;
            Soc.Clear(_detail.Content);
            Soc.Clear(_actions);
            int dw = WidthHud - LeftW - 1, w = dw - 32, y = 14;
            if (e == null)
            {
                W.Label(_detail.Content, W.T(ClientTextKeys.ModernSocialQPick), 16, 30, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
                _detail.FitContent(0);
                return;
            }
            Tab t = _tab;
            ClientQuestInfo info = e.Info;
            ClientQuestProgress prog = e.Progress;
            bool ready = t == Tab.Active && prog.Completed;

            // head: icon + name, tags, giver lines
            new IconView(TypeIcon(info.Type), 16, ModernTheme.Accent) { Parent = _detail.Content, Location = new Point(S(16), S(y + 3)) };
            W.Label(_detail.Content, info.Name, 40, y, w - 24, 22, 17, true, Color.White);
            y += 28;
            var tags = new List<(string Text, bool Hot)> { (TypeName(info.Type), false) };
            tags.Add((info.MaxLevelNeeded > 0 && info.MaxLevelNeeded < 65535 && info.MaxLevelNeeded != info.MinLevelNeeded
                ? W.T(ClientTextKeys.ModernSocialQLevelRange, info.MinLevelNeeded, info.MaxLevelNeeded) : W.T(ClientTextKeys.ModernSocialQLevel, info.MinLevelNeeded), false));
            if (!string.IsNullOrEmpty(info.Group)) tags.Add((info.Group, false));
            if (ready) tags.Add((W.T(ClientTextKeys.ModernSocialQReadyTurnIn), true));
            else if (t == Tab.Active) tags.Add((W.T(ClientTextKeys.ModernSocialQInProgress), false));
            if (t == Tab.Completed) tags.Add((W.T(ClientTextKeys.ModernSocialQCompleted), false));
            if (t == Tab.Active && IsTracked(info.Index)) tags.Add((W.T(ClientTextKeys.ModernSocialQTrackedTag), false));
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K, x = 0;
                foreach (var tg in tags) x += Soc.Pill(g, x, 1 * k, tg.Text, ModernTheme.Accent, 9, tg.Hot, false, 15) + 5 * k;
            })
            { Parent = _detail.Content, Location = new Point(S(16), S(y)), Size = new Size(S(w), S(18)), NotControl = true };
            y += 24;
            y = Giver(W.T(ClientTextKeys.ModernSocialQFrom), info.NPCIndex, y, w);
            if (!info.SameFinishNPC) y = Giver(W.T(ClientTextKeys.ModernSocialQTurnInTo), info.FinishNPCIndex, y, w);
            y += 8;

            // QuestMessage.UpdateQuest's sections
            if (info.Description.Count > 0) y = Text(info.Description, y, w) + 10;
            if (info.TaskDescription.Count > 0) y = Section(W.T(ClientTextKeys.ModernSocialQTasks), info.TaskDescription, true, y, w);
            if (t == Tab.Active && prog.TaskList.Count > 0) y = Section(W.T(ClientTextKeys.ModernSocialQProgress), prog.TaskList, true, y, w);
            if (info.ReturnDescription.Count > 0 && t != Tab.Completed) y = Section(W.T(ClientTextKeys.ModernSocialQReturn), info.ReturnDescription, false, y, w);
            if (info.TimeLimitInSeconds > 0)
            {
                W.Caption(_detail.Content, W.T(ClientTextKeys.ModernSocialQTimeLimit), 16, y, w);
                W.Label(_detail.Content, W.Duration(info.TimeLimitInSeconds), 16, y + 18, w, 18, 12);
                y += 46;
            }
            y = Rewards(info, y, w);
            new ModernPanel { Parent = _detail.Content, Location = new Point(0, S(y)), Size = new Size(1, 1) };
            _detail.FitContent(0);
            _detail.ScrollTo(keep);
            RenderActions(t, e, dw);
        }

        private int Giver(string label, uint id, int y, int w)
        {
            QuestNpcs.Met n = QuestNpcs.Find(id);
            string name = n != null ? ChatGfx.NpcName(n.Name).Name : W.T(ClientTextKeys.ModernSocialQNotMet);
            string where = n != null ? MapTitle(n.Map) : string.Empty;
            string tail = n != null && where.Length > 0 ? " · " + where + " (" + n.Loc.X + ", " + n.Loc.Y + ")" : string.Empty;
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                Font f = ModernSkin.Font(11), bf = ModernSkin.Font(11, true);
                string lead = label + " ";
                float lw = ModernSkin.Measure(lead, f) + 1, nw = ModernSkin.Measure(name, bf) + 2;
                ModernSkin.Text(g, lead, f, ModernTheme.Dim, new RectangleF(0, 0, lw, b.Size.Height));
                ModernSkin.Text(g, name, bf, ModernTheme.Text, new RectangleF(lw, 0, nw, b.Size.Height));
                ModernSkin.Text(g, tail, f, ModernTheme.Dim, new RectangleF(lw + nw, 0, b.Size.Width - lw - nw, b.Size.Height));
            })
            { Parent = _detail.Content, Location = new Point(S(16), S(y)), Size = new Size(S(w), S(16)), NotControl = true };
            return y + 18;
        }

        /// <summary>Quest text rows with the markup (colours, info links) as one rich text; returns its bottom (HUD px).</summary>
        private int Text(List<string> lines, int y, int w)
        {
            var rt = new NpcRichText(w, 12) { Parent = _detail.Content, Location = new Point(S(16), S(y)) };
            rt.SetRuns(Runs(lines));
            WireTips(rt);
            return y + (int)Math.Ceiling(rt.Size.Height / ModernRoot.K);
        }

        private static List<NpcRun> Runs(List<string> lines)
        {
            var runs = new List<NpcRun>();
            for (int i = 0; i < lines.Count; i++)
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
                ItemInfo info = null;
                foreach (ItemInfo i in GameScene.ItemInfoList) if (i.Index == rt.Runs[run].Index) { info = i; break; }
                if (info == null) return;
                ModernTooltip.Show(new UserItem(info) { Count = 1, CurrentDura = info.Durability, MaxDura = info.Durability }, rt);
            };
        }

        private int Section(string title, List<string> lines, bool boxed, int y, int w)
        {
            W.Caption(_detail.Content, title, 16, y, w);
            y += 18;
            if (!boxed) return Text(lines, y, w) + 12;
            foreach (string l in lines)
            {
                var back = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * 0.7f * k);
                    using var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.06f));
                    g.FillPath(br, p);
                })
                { Parent = _detail.Content, Location = new Point(S(16), S(y)), Size = new Size(S(w), S(20)), NotControl = true };
                var rt = new NpcRichText(w - 16, 12) { Parent = _detail.Content, Location = new Point(S(24), S(y + 3)) };
                rt.SetRuns(Runs(new List<string> { l }));
                WireTips(rt);
                int h = (int)Math.Ceiling(rt.Size.Height / ModernRoot.K) + 6;
                back.Size = new Size(back.Size.Width, S(h));
                y += h + 4;
            }
            return y + 8;
        }

        /// <summary>QuestRewards: EXP / gold / credit, the fixed items, the select items (your gender only; picked at the NPC).</summary>
        private int Rewards(ClientQuestInfo info, int y, int w)
        {
            W.Caption(_detail.Content, W.T(ClientTextKeys.ModernSocialQRewards), 16, y, w);
            y += 20;
            int x = 16;
            void ChipBox(string k, uint v)
            {
                string val = W.N(v);
                float cw = (ModernSkin.Measure(k, ModernSkin.Font(10, true)) + ModernSkin.Measure(val, ModernSkin.Font(12, true))) / ModernRoot.K + 30;
                int iw = (int)Math.Ceiling(cw);
                new PaintBox((g, b) =>
                {
                    float kk = ModernRoot.K;
                    RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                    using (GraphicsPath p = ModernSkin.RoundRect(r, b.Size.Height / 2f))
                    {
                        using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.08f))) g.FillPath(br, p);
                        ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.7f));
                    }
                    float kw = ModernSkin.Measure(k, ModernSkin.Font(10, true)) + 2;
                    ModernSkin.Text(g, k, ModernSkin.Font(10, true), ModernTheme.Dim, new RectangleF(10 * kk, 0, kw, b.Size.Height));
                    ModernSkin.Text(g, val, ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(10 * kk + kw + 6 * kk, 0, b.Size.Width - kw - 22 * kk, b.Size.Height));
                })
                { Parent = _detail.Content, Location = new Point(S(x), S(y + 6)), Size = new Size(S(iw), S(24)), NotControl = true };
                x += iw + 6;
            }
            bool any = false;
            if (info.RewardExp > 0) { ChipBox(W.T(ClientTextKeys.ModernSocialQExp), info.RewardExp); any = true; }
            if (info.RewardGold > 0) { ChipBox(W.T(ClientTextKeys.ModernSocialQGold), info.RewardGold); any = true; }
            if (info.RewardCredit > 0) { ChipBox(W.T(ClientTextKeys.ModernSocialQCredit), info.RewardCredit); any = true; }
            for (int i = 0; i < info.RewardsFixedItem.Count && i < 5; i++)
            {
                if (x + 36 > w + 16) { x = 16; y += 42; }
                RewardCell(info.RewardsFixedItem[i], x, y);
                x += 42;
                any = true;
            }
            if (!any) W.Label(_detail.Content, W.T(ClientTextKeys.ModernSocialQNoReward), 16, y + 8, w, 18, 11, false, ModernTheme.Dim);
            y += 46;
            MirGender gender = User?.Gender ?? MirGender.Male;
            var select = new List<QuestItemReward>();
            foreach (QuestItemReward r in info.RewardsSelectItem)
            {
                if (r?.Item == null) continue;
                RequiredGender g = r.Item.RequiredGender;
                if (gender == MirGender.Male && !g.HasFlag(RequiredGender.Male)) continue;
                if (gender == MirGender.Female && !g.HasFlag(RequiredGender.Female)) continue;
                select.Add(r);
                if (select.Count == 5) break;
            }
            if (select.Count > 0)
            {
                W.Caption(_detail.Content, W.T(ClientTextKeys.ModernSocialQChooseOne), 16, y, w);
                y += 20;
                for (int i = 0; i < select.Count; i++) RewardCell(select[i], 16 + i * 42, y);
                y += 46;
            }
            return y;
        }

        private void RewardCell(QuestItemReward r, int x, int y)
        {
            if (r?.Item == null) return;
            ItemInfo info = r.Item;
            var shown = new UserItem(info) { Count = Math.Max((ushort)1, r.Count), CurrentDura = info.Durability, MaxDura = info.Durability };
            new NpcItemCell(36, () => shown) { Parent = _detail.Content, Location = new Point(S(x), S(y)), CountOverride = r.Count };
        }

        private void RenderActions(Tab t, Entry e, int dw)
        {
            ClientQuestInfo info = e.Info;
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
            uint target = t == Tab.Available ? info.NPCIndex : info.FinishNPCIndex;
            string speak = string.Empty;
            if (t == Tab.Completed)
            {
                W.Label(_actions, W.T(ClientTextKeys.ModernSocialQCompleted), 16, ay + 4, 200, 18, 11, false, ModernTheme.Dim);
                return;
            }
            if (t == Tab.Active)
            {
                bool pending = _pending.ContainsKey(info.Index), tracked = IsTracked(info.Index);
                int id = info.Index;
                var track = Act(W.T(tracked ? ClientTextKeys.ModernSocialQUntrack : ClientTextKeys.ModernSocialQTrack), ButtonKind.Normal, () => ToggleTrack(id),
                    W.T(tracked ? ClientTextKeys.ModernSocialQUntrackHint : ClientTextKeys.ModernSocialQTrackHint));
                if (!tracked && TrackedCount >= TrackerHud.MaxTracked) track.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQTrackMax, TrackerHud.MaxTracked));
                bool shared = _shared.ContainsKey(id);
                var share = Act(W.T(shared ? ClientTextKeys.ModernSocialQShared : ClientTextKeys.ModernSocialQShare), ButtonKind.Normal, () => Share(id), W.T(ClientTextKeys.ModernSocialQShareHint));
                if (GameScene.Models.Group.Members.Count < 2) share.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQShareNoGroup));
                else if (shared) share.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQShareWait));
                var find = Act(W.T(ClientTextKeys.ModernSocialQMap), ButtonKind.Normal, () => FindOnMap(target), W.T(ClientTextKeys.ModernSocialQFindHint, NpcLabel(target)));
                if (QuestNpcs.Find(target) == null) find.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQNotMetHint));
                var abandon = Act(W.T(pending ? ClientTextKeys.ModernSocialQAbandoning : ClientTextKeys.ModernSocialQAbandon), ButtonKind.Warn, () => Abandon(info), W.T(ClientTextKeys.ModernSocialQAbandonHint));
                if (pending) abandon.SetDisabled(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                if (e.Progress.Completed) speak = W.T(ClientTextKeys.ModernSocialQSpeakTurnIn, NpcLabel(info.FinishNPCIndex));
            }
            else
            {
                var find = Act(W.T(ClientTextKeys.ModernSocialQMap), ButtonKind.Normal, () => FindOnMap(target), W.T(ClientTextKeys.ModernSocialQFindHint, NpcLabel(target)));
                if (QuestNpcs.Find(target) == null) find.SetDisabled(true, W.T(ClientTextKeys.ModernSocialQNotMetHint));
                speak = W.T(ClientTextKeys.ModernSocialQSpeakAccept, NpcLabel(info.NPCIndex));
            }
            if (speak.Length > 0)
            {
                // the turn-in / accept hint fills what the buttons leave, centred on the bar (up to three short lines)
                int sw = dw - 16 - x - 4, sh = Math.Min(42, Soc.WrapHeight(speak, 10, sw, true));
                W.Label(_actions, speak, x + 4, Math.Max(3, (49 - sh) / 2), sw, sh, 10, true, ModernTheme.Accent, StringAlignment.Far, sh > 16);
            }
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

        /// <summary>QuestDetailDialog's share button: C.ShareQuest (the server offers it to group members nearby).</summary>
        private void Share(int id)
        {
            if (_shared.ContainsKey(id)) return;
            Network.Enqueue(new C.ShareQuest { QuestIndex = id });
            _shared[id] = CMain.Time + ShareRestMs;
            _detailDirty = true;
        }

        /// <summary>QuestDetailDialog's cancel: AskCancelQuest, then C.AbandonQuest; locked until S.ChangeQuest.</summary>
        private void Abandon(ClientQuestInfo info)
        {
            int id = info.Index;
            if (_pending.ContainsKey(id)) return;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.AskCancelQuest), yes =>
            {
                if (!yes || IsDisposed || _pending.ContainsKey(id)) return;
                bool current = false;
                foreach (ClientQuestProgress p in User.CurrentQuests) if (p.Id == id) current = true;
                if (!current) return;
                Network.Enqueue(new C.AbandonQuest { QuestIndex = id });
                _pending[id] = CMain.Time + PendingMs;
                _listDirty = _detailDirty = true;
            }, false, PopupKind.Warning, W.T(ClientTextKeys.ModernSocialQAbandonTitle));
        }

        /// <summary>Opens the big map; when the NPC is on another map, a note says where it is.</summary>
        private static void FindOnMap(uint id)
        {
            QuestNpcs.Met n = QuestNpcs.Find(id);
            if (n == null) return;
            WindowManager wm = Soc.Wm;
            if (wm != null && wm.IsRegistered(BigMapWindow.WindowKey)) wm.Open(BigMapWindow.WindowKey);
            if (n.Map == Soc.MapIndex) return;
            string where = MapTitle(n.Map);
            if (where.Length == 0) where = W.T(ClientTextKeys.ModernSocialQAnotherMap);
            ModernRoot.Instance?.Popups.Confirm(W.T(ClientTextKeys.ModernSocialQNpcIsIn, ChatGfx.NpcName(n.Name).Name, where, n.Loc.X, n.Loc.Y), PopupButtons.Ok, PopupKind.Info, null, false, W.T(ClientTextKeys.ModernSocialQFindTitle));
        }
    }
}
