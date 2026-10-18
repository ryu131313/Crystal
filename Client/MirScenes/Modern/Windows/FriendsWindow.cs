// Ported from WebClient/src/ui/modern/windows/friends.ts (FriendsWindow): the new UI's friends window, a modern FriendDialog
// (+ MemoDialog) over GameScene.Models.Friends (ClientFriend rows: class badge, level, guild, online / last seen, memo,
// blocked; the phase 2.7 requests). Opening sends C.RefreshFriends like FriendDialog.Show. Tabs Friends / Requests / Blocked
// (Requests: the pending incoming S.FriendRequest entries with their 60 s countdown, Accept / Decline = Friends.Reply; hidden
// when S.ServerRules says friend requests are off; the same request is also ModernUi's request toast). Search (names, guilds,
// memos, classes); Online / Offline groups that fold. The add box: a friend request (C.AddFriend Blocked = false; the outcome
// comes back as S.FriendRequestResult, shown in the note) or, on Blocked, a block (Blocked = true). Detail: the profile, the
// memo (C.AddMemo, 1-200 characters: replaced, never cleared), actions Whisper (prefill "/name "), Invite (C.AddMember), Mail
// (the mail window's ComposeTo), Inspect (only while the friend is on screen: C.Inspect), Block and Remove (both ask; the row
// stays locked until S.FriendUpdate, 5 s fallback). The bonds strip shows the lover and the mentor and opens their windows.
// "Accepting friend requests" mirrors S.AllowToggles; the switch is in Options. Desktop: the memo is a one-line field.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class FriendsWindow : ModernWindow
    {
        public const string WindowKey = "friends";
        private const int WidthHud = 700, HeightHud = 490, LeftW = 290, MaxMemo = 200, PendingMs = 5000;

        private enum Tab { Friends, Requests, Blocked }

        private Tab _tab = Tab.Friends;
        private readonly List<Tab> _tabList = new List<Tab>();
        private string _tabKey = string.Empty, _query = string.Empty, _note = string.Empty;
        private bool _noteOk, _dirty = true, _bondsShown;
        private int _picked = int.MinValue;
        private readonly HashSet<string> _shut = new HashSet<string>();
        private readonly Dictionary<int, long> _pending = new Dictionary<int, long>();
        private (int Index, string Text) _memoDraft = (int.MinValue, null);
        private uint _inViewId;
        private long _nextTick;

        private Tabs _tabs;
        private SearchBox _search;
        private ScrollView _list;
        private ModernTextField _addField, _memo;
        private ModernButton _addBtn, _inspect, _save;
        private ModernLabel _noteLbl, _memoCount;
        private PaintBox _allow;
        private ModernPanel _detail, _bonds;
        private readonly List<(PaintBox Row, FriendRequestInfo Req)> _reqRows = new List<(PaintBox, FriendRequestInfo)>();
        private readonly Action _changed, _requests, _rels, _toggles;
        private readonly Action<S.FriendRequestResult> _result;

        public FriendsWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinFriends), 120, 50)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => { _pending.Clear(); _dirty = true; };
            _requests = () => _dirty = true;
            _rels = () => _dirty = true;
            _toggles = () => _dirty = true;
            _result = ShowResult;
        }

        private static FriendList Model => GameScene.Models.Friends;
        private static bool RequestsOn => GameScene.Models?.ServerRules.Rules?.FriendRequestsEnabled ?? true;
        private static bool HasBonds => GameScene.Models != null && (GameScene.Models.Relationship.LoverName.Length > 0 || GameScene.Models.Relationship.MentorName.Length > 0);

        // ---- for the UI test
        internal string TabShown => _tab.ToString();
        internal string NoteShown => _note;
        internal int PickedIndex => _picked;
        internal ModernTextField AddField => _addField;
        internal ModernButton AddButton => _addBtn;
        internal ModernTextField MemoField => _memo;
        internal ModernButton SaveButton => _save;
        internal ModernPanel Detail => _detail;
        internal ScrollView List => _list;
        internal MirControl RequestRow(int i) => i >= 0 && i < _reqRows.Count ? _reqRows[i].Row : null;
        internal bool IsPending(int index) => _pending.ContainsKey(index);

        protected override void Build()
        {
            int top = TitleHeight;
            _bondsShown = HasBonds;
            int bh = _bondsShown ? 47 : 0;
            _bonds = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(WidthHud), S(Math.Max(1, bh))), Visible = _bondsShown };
            if (_bondsShown) W.Divider(this, 0, top + bh - 1, WidthHud);
            int body = top + bh, h = HeightHud - bh;

            // left column
            _tabKey = string.Empty;
            _tabs = null;
            _search = W.At(new SearchBox(LeftW - 20, W.T(ClientTextKeys.ModernSocialFriendsSearch)), this, 12, body + 42);
            if (_query.Length > 0) _search.Text = _query;
            _search.Search += q => { _query = q.ToLowerInvariant(); _dirty = true; };
            int listTop = body + 72, listH = h - 72 - 82;
            _list = new ScrollView(LeftW - 16, listH) { Parent = this, Location = new Point(S(12), S(listTop)), Background = W.Page };
            _list.Framed = false;
            int ay = listTop + listH + 8;
            _addField = W.At(new ModernTextField(LeftW - 20 - 90 - 6, 26) { MaxLength = 20 }, this, 12, ay);
            _addField.Submitted += _ => Add();
            _addBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialAdd)), this, 12 + LeftW - 20 - 90, ay);
            _addBtn.Size = new Size(S(90), S(26));
            _addBtn.Click += (o, e) => Add();
            _noteLbl = W.Label(this, string.Empty, 12, ay + 30, LeftW - 20, 14, 10, false, ModernTheme.Dim);
            _allow = new PaintBox(PaintAllow) { Parent = this, Location = new Point(S(12), S(ay + 48)), Size = new Size(S(LeftW - 20), S(16)), Hint = W.T(ClientTextKeys.ModernSocialAcceptingHint) };
            _allow.Click += (o, e) =>
            {
                float lx = (CMain.MPoint.X - _allow.DisplayLocation.X) / ModernRoot.K;
                if (lx > LeftW - 20 - 60 && Soc.Wm != null && Soc.Wm.IsRegistered(OptionsWindow.WindowKey)) Soc.Wm.Open(OptionsWindow.WindowKey);
            };
            W.VDivider(this, LeftW, body, h);

            // detail
            _detail = new ModernPanel { Parent = this, Location = new Point(S(LeftW + 1), S(body)), Size = new Size(S(WidthHud - LeftW - 1), S(h)) };
            _reqRows.Clear();
            _dirty = true;
            Render();
        }

        protected override void Shown()
        {
            GameModels m = GameScene.Models;
            m.Friends.Changed.On(_changed);
            m.Friends.RequestsChanged.On(_requests);
            m.Friends.ResultReceived.On(_result);
            m.Relationship.LoverChanged.On(_rels);
            m.Relationship.MentorChanged.On(_rels);
            m.AllowToggles.Changed.On(_toggles);
            m.ServerRules.Changed.On(_toggles);
            m.Group.Changed.On(_rels);
            Network.Enqueue(new C.RefreshFriends()); // FriendDialog.Show: the online state goes stale while closed
            _dirty = true;
        }

        protected override void Closed()
        {
            GameModels m = GameScene.Models;
            if (m == null) return;
            m.Friends.Changed.Off(_changed);
            m.Friends.RequestsChanged.Off(_requests);
            m.Friends.ResultReceived.Off(_result);
            m.Relationship.LoverChanged.Off(_rels);
            m.Relationship.MentorChanged.Off(_rels);
            m.AllowToggles.Changed.Off(_toggles);
            m.ServerRules.Changed.Off(_toggles);
            m.Group.Changed.Off(_rels);
            _memoDraft = (int.MinValue, null);
        }

        public override void Process()
        {
            base.Process();
            if (CMain.Time >= _nextTick) Tick();
            if (_dirty) Render();
        }

        /// <summary>Once a second while open: request countdowns + local expiry, the 5 s pending fallback, Inspect's in-view check.</summary>
        private void Tick()
        {
            _nextTick = CMain.Time + 1000;
            Model.Prune(); // RequestsChanged → dirty when one ran out
            if (_pending.Count > 0)
            {
                var expired = new List<int>();
                foreach (var kv in _pending) if (kv.Value <= CMain.Time) expired.Add(kv.Key);
                foreach (int i in expired) _pending.Remove(i);
                if (expired.Count > 0) _dirty = true;
            }
            if (_tab == Tab.Requests)
                for (int i = 0; i < _reqRows.Count; i++)
                {
                    _reqRows[i].Row.Invalidate();
                    _reqRows[i].Row.Hint = _reqRows[i].Req.Name + " " + W.T(ClientTextKeys.ModernSocialWantsFriends, Math.Max(0, (_reqRows[i].Req.Expires - CMain.Time + 999) / 1000));
                }
            if (_tab != Tab.Requests && _inspect != null && !_inspect.IsDisposed)
            {
                ClientFriend f = Find(_picked);
                uint id = f != null && !f.Blocked && f.Online ? (Soc.InView(f.Name)?.ObjectID ?? 0) : 0;
                if (id != _inViewId) { _inViewId = id; _inspect.SetDisabled(id == 0, W.T(ClientTextKeys.ModernSocialInViewOnly)); }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static ClientFriend Find(int index)
        {
            List<ClientFriend> all = Model.Friends;
            for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].Index == index) return all[i];
            return null;
        }

        private List<ClientFriend> Entries(bool blocked)
        {
            var list = new List<ClientFriend>();
            foreach (ClientFriend f in Model.Friends) if (f != null && f.Blocked == blocked) list.Add(f);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private bool Matches(params string[] fields)
        {
            if (_query.Length == 0) return true;
            foreach (string s in fields) if (!string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(_query)) return true;
            return false;
        }

        private void SetNote(string text, bool ok = false)
        {
            _note = text ?? string.Empty;
            _noteOk = ok;
            if (_noteLbl == null || _noteLbl.IsDisposed) return;
            _noteLbl.Text = _note;
            _noteLbl.Colour = ok ? ModernTheme.Accent : ModernTheme.Dim;
        }

        private void ShowResult(S.FriendRequestResult p)
        {
            if (p == null) return;
            ClientTextKeys? key = p.Result switch
            {
                ServerPackets.FriendRequestResult.Accepted => ClientTextKeys.ModernSocialResAccepted,
                ServerPackets.FriendRequestResult.Declined => ClientTextKeys.ModernSocialResDeclined,
                ServerPackets.FriendRequestResult.Expired => ClientTextKeys.ModernSocialResExpired,
                ServerPackets.FriendRequestResult.Offline => ClientTextKeys.ModernSocialResOffline,
                ServerPackets.FriendRequestResult.ListFull => ClientTextKeys.ModernSocialResListFull,
                ServerPackets.FriendRequestResult.NotAccepting => ClientTextKeys.ModernSocialResNotAccepting,
                _ => null,
            };
            if (key.HasValue) SetNote(W.T(key.Value, p.Name), p.Result == ServerPackets.FriendRequestResult.Accepted);
        }

        private static string LastSeen(ClientFriend f)
        {
            if (f.Online) return W.T(ClientTextKeys.ModernSocialOnline);
            DateTime d = Soc.FromBinary(f.LastSeen);
            return d.Year > 2000 ? W.T(ClientTextKeys.ModernSocialLastSeen, Eco.Ago(d)) : W.T(ClientTextKeys.ModernSocialOffline);
        }

        private void Lock(int index)
        {
            _pending[index] = CMain.Time + PendingMs;
            _dirty = true;
        }

        // ------------------------------------------------------------------ actions

        /// <summary>FriendDialog's add button: a friend request (Friends tab) or a block (Blocked tab), by name.</summary>
        private void Add()
        {
            string name = _addField.Text.Trim();
            if (name.Length == 0) return;
            bool blocked = _tab == Tab.Blocked;
            foreach (ClientFriend f in Model.Friends)
                if (f != null && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && f.Blocked == blocked)
                {
                    SetNote(W.T(blocked ? ClientTextKeys.ModernSocialAlreadyBlocked : ClientTextKeys.ModernSocialAlreadyFriend, f.Name));
                    return;
                }
            Network.Enqueue(new C.AddFriend { Name = name, Blocked = blocked });
            _addField.Text = string.Empty;
            if (blocked) SetNote(W.T(ClientTextKeys.ModernSocialBlocking, name));
            else if (RequestsOn) SetNote(W.T(ClientTextKeys.ModernSocialRequestSent, name), true);
            else SetNote(W.T(ClientTextKeys.ModernSocialAdding, name));
        }

        private void Remove(ClientFriend f)
        {
            string text = f.Blocked ? W.T(ClientTextKeys.ModernSocialUnblockAsk, f.Name) : W.T(ClientTextKeys.ModernSocialRemoveFriendAsk, f.Name);
            int index = f.Index;
            ModernRoot.Instance?.Popups.Ask(text, yes =>
            {
                if (!yes || IsDisposed || _pending.ContainsKey(index) || Find(index) == null) return;
                Network.Enqueue(new C.RemoveFriend { CharacterIndex = index });
                Lock(index);
            }, false, f.Blocked ? PopupKind.Question : PopupKind.Warning, W.T(f.Blocked ? ClientTextKeys.ModernSocialUnblock : ClientTextKeys.ModernSocialRemoveFriend));
        }

        private void Block(ClientFriend f)
        {
            int index = f.Index;
            string name = f.Name;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernSocialBlockAsk, name), yes =>
            {
                if (!yes || IsDisposed || _pending.ContainsKey(index)) return;
                Network.Enqueue(new C.AddFriend { Name = name, Blocked = true });
                Lock(index);
            }, false, PopupKind.Warning, W.T(ClientTextKeys.ModernSocialBlock));
        }

        /// <summary>MemoDialog's OK: C.AddMemo (1-200 characters; it cannot be cleared, only replaced).</summary>
        private void SaveMemo(ClientFriend f)
        {
            string memo = _memo?.Text.Trim() ?? string.Empty;
            if (memo.Length == 0 || memo.Length > MaxMemo || _pending.ContainsKey(f.Index)) return;
            Network.Enqueue(new C.AddMemo { CharacterIndex = f.Index, Memo = memo });
            _memoDraft = (int.MinValue, null);
            Lock(f.Index);
        }

        private static void ComposeMail(string name)
        {
            WindowManager wm = Soc.Wm;
            if (wm == null || !wm.IsRegistered(MailWindow.WindowKey)) return;
            wm.Open(MailWindow.WindowKey);
            (wm.Get(MailWindow.WindowKey) as MailWindow)?.ComposeTo(name);
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_list == null || _list.IsDisposed || GameScene.User == null) return;
            if (HasBonds != _bondsShown) { Layout(); return; } // the strip comes or goes: lay out again

            // tabs: Requests only while the server uses friend requests
            _tabList.Clear();
            _tabList.Add(Tab.Friends);
            if (RequestsOn) _tabList.Add(Tab.Requests);
            _tabList.Add(Tab.Blocked);
            if (!_tabList.Contains(_tab)) _tab = Tab.Friends;
            string key = string.Join(",", _tabList);
            if (key != _tabKey || _tabs == null)
            {
                _tabKey = key;
                _tabs?.Dispose();
                var names = new List<string>();
                foreach (Tab t in _tabList) names.Add(W.T(t == Tab.Friends ? ClientTextKeys.ModernSocialFriendsTab : t == Tab.Requests ? ClientTextKeys.ModernSocialRequestsTab : ClientTextKeys.ModernSocialBlockedTab).ToUpperInvariant());
                _tabs = new Tabs(names, _tabList.IndexOf(_tab), LeftW - 20) { Parent = this, Location = new Point(S(12), _search.Location.Y - S(34)) };
                _tabs.Changed += i =>
                {
                    _tab = _tabList[i];
                    _picked = int.MinValue;
                    _memoDraft = (int.MinValue, null);
                    _dirty = true;
                };
            }
            else _tabs.Select(_tabList.IndexOf(_tab));
            int ri = _tabList.IndexOf(Tab.Requests);
            if (ri >= 0) _tabs.Badge(ri, Model.Requests.Count);

            bool blockedTab = _tab == Tab.Blocked;
            _addField.Visible = _addBtn.Visible = _tab != Tab.Requests;
            _addField.Placeholder = W.T(blockedTab ? ClientTextKeys.ModernSocialBlockByName : RequestsOn ? ClientTextKeys.ModernSocialRequestByName : ClientTextKeys.ModernSocialAddByName);
            _addBtn.Text = W.T(blockedTab ? ClientTextKeys.ModernSocialBlock : RequestsOn ? ClientTextKeys.ModernSocialRequest : ClientTextKeys.ModernSocialAdd);
            SetNote(_note, _noteOk);
            _allow.Visible = RequestsOn && GameScene.Models.AllowToggles.Known;
            _allow.Invalidate();

            RenderBonds();
            RenderList();
            RenderDetail();
        }

        private void PaintAllow(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            bool on = GameScene.Models.AllowToggles.Get(AllowToggle.FriendRequests);
            Font f = ModernSkin.Font(10), bf = ModernSkin.Font(10, true);
            string lead = W.T(ClientTextKeys.ModernSocialAccepting) + " ";
            float w = ModernSkin.Measure(lead, f) + 2;
            ModernSkin.Text(g, lead, f, ModernTheme.Dim, new RectangleF(0, 0, w, b.Size.Height));
            string state = W.T(on ? ClientTextKeys.ModernSocialOn : ClientTextKeys.ModernSocialOff);
            ModernSkin.Text(g, state, bf, on ? ModernTheme.Text : ModernTheme.Warn, new RectangleF(w, 0, 60 * k, b.Size.Height));
            if (Soc.Wm == null || !Soc.Wm.IsRegistered(OptionsWindow.WindowKey)) return;
            string link = W.T(ClientTextKeys.ModernSocialOptionsLink);
            Font lf = ModernSkin.Font(10);
            float lw = ModernSkin.Measure(link, lf) + 2;
            Color lc = b.Hovered ? ModernTheme.Accent : ModernTheme.AccentDim;
            ModernSkin.Text(g, link, lf, lc, new RectangleF(b.Size.Width - lw, 0, lw, b.Size.Height));
            using var pen = new Pen(lc, 1f);
            g.DrawLine(pen, b.Size.Width - lw, b.Size.Height - 2 * k, b.Size.Width - 2, b.Size.Height - 2 * k);
        }

        private void RenderBonds()
        {
            if (!_bondsShown) return;
            Soc.Clear(_bonds);
            RelationshipState rel = GameScene.Models.Relationship;
            var parts = new List<(int Icon, string Title, string Line, string Win)>();
            if (rel.LoverName.Length > 0)
            {
                string days = rel.MarriedDays > 0 ? W.T(rel.MarriedDays == 1 ? ClientTextKeys.ModernSocialTogetherDay : ClientTextKeys.ModernSocialTogetherDays, W.N(rel.MarriedDays)) : W.T(ClientTextKeys.ModernSocialMarried);
                parts.Add((ModernIcons.HeartFull, W.T(ClientTextKeys.ModernSocialMarriedTo, rel.LoverName), rel.LoverMap.Length > 0 ? W.T(ClientTextKeys.ModernSocialInMap, days, NpcFmt.Pretty(rel.LoverMap)) : days, RelationshipWindow.WindowKey));
            }
            if (rel.MentorName.Length > 0)
            {
                bool theyTeach = rel.MentorLevel > (GameScene.User?.Level ?? 0);
                parts.Add((ModernIcons.Mentor, W.T(theyTeach ? ClientTextKeys.ModernSocialMentoredBy : ClientTextKeys.ModernSocialMentoring, rel.MentorName),
                    W.T(rel.MentorOnline ? ClientTextKeys.ModernSocialLevelOnline : ClientTextKeys.ModernSocialLevelOffline, rel.MentorLevel), MentorWindow.WindowKey));
            }
            int n = Math.Max(1, parts.Count), bw = (WidthHud - 24 - (n - 1) * 10) / n;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                int x = 12 + i * (bw + 10);
                new PaintBox((g, b) =>
                {
                    W.Card(g, b.Size);
                    float k = ModernRoot.K;
                    ModernSkin.Text(g, p.Title, ModernSkin.Font(12, true), Color.White, new RectangleF(40 * k, 4 * k, b.Size.Width - 110 * k, 16 * k));
                    ModernSkin.Text(g, p.Line, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(40 * k, 18 * k, b.Size.Width - 110 * k, 15 * k));
                })
                { Parent = _bonds, Location = new Point(S(x), S(6)), Size = new Size(S(bw), S(36)), NotControl = true };
                new IconView(p.Icon, 20, ModernTheme.Accent) { Parent = _bonds, Location = new Point(S(x + 10), S(14)) };
                if (Soc.Wm != null && Soc.Wm.IsRegistered(p.Win))
                {
                    string win = p.Win;
                    var open = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialOpen)) { FontSize = 10 }, _bonds, x + bw - 10 - 56, 13);
                    open.Size = new Size(S(56), S(22));
                    open.Click += (o, e) => Soc.Wm?.Open(win);
                }
            }
        }

        private void RenderList()
        {
            int keep = _list.ScrollY;
            Soc.Clear(_list.Content);
            _reqRows.Clear();
            int y = 0, w = _list.InnerWidth;
            if (_tab == Tab.Requests)
            {
                List<FriendRequestInfo> reqs = Model.Requests;
                int shown = 0;
                foreach (FriendRequestInfo r in reqs)
                {
                    if (!Matches(r.Name, Soc.ClassName(r.Class))) continue;
                    shown++;
                    FriendRequestInfo req = r;
                    var row = new PaintBox((g, b) => PaintRequest(g, b, req)) { Parent = _list.Content, Location = new Point(0, y), Size = new Size(w, S(44)) };
                    var ok = W.At(new ModernButton(W.T(ClientTextKeys.ModernAccept), ButtonKind.Main) { FontSize = 10 }, row, (int)(w / ModernRoot.K) - 6 - 54, 11);
                    ok.Size = new Size(S(54), S(22));
                    ok.Click += (o, e) => Model.Reply(req.Name, true);
                    var no = W.At(new ModernButton(W.T(ClientTextKeys.ModernDecline)) { FontSize = 10 }, row, (int)(w / ModernRoot.K) - 6 - 54 - 4 - 56, 11);
                    no.Size = new Size(S(56), S(22));
                    no.Hint = W.T(ClientTextKeys.ModernSocialWantsFriendsHint);
                    no.Click += (o, e) => Model.Reply(req.Name, false);
                    _reqRows.Add((row, req));
                    y += S(47);
                }
                if (shown == 0) Empty(W.T(reqs.Count > 0 ? ClientTextKeys.ModernSocialNoMatch : ClientTextKeys.ModernSocialNoRequests), w);
                _list.FitContent(0);
                _list.ScrollTo(keep);
                return;
            }
            bool blocked = _tab == Tab.Blocked;
            List<ClientFriend> all = Entries(blocked);
            var pool = new List<ClientFriend>();
            foreach (ClientFriend f in all) if (Matches(f.Name, f.GuildName, f.Memo, Soc.ClassName(f.Class))) pool.Add(f);
            var groups = new List<(string Key, string Title, List<ClientFriend> Items)>();
            if (blocked) groups.Add(("blocked", W.T(ClientTextKeys.ModernSocialBlockedTab), pool));
            else
            {
                var on = new List<ClientFriend>();
                var off = new List<ClientFriend>();
                foreach (ClientFriend f in pool) (f.Online ? on : off).Add(f);
                groups.Add(("online", W.T(ClientTextKeys.ModernSocialOnline), on));
                groups.Add(("offline", W.T(ClientTextKeys.ModernSocialOffline), off));
            }
            foreach (var grp in groups)
            {
                if (grp.Items.Count == 0) continue;
                bool shut = _shut.Contains(grp.Key);
                string gk = grp.Key, title = grp.Title.ToUpperInvariant(), count = grp.Items.Count.ToString();
                var head = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    Font f = ModernSkin.Font(10, true);
                    ModernSkin.Text(g, shut ? "▸" : "▾", f, ModernTheme.AccentDim, new RectangleF(4 * k, 0, 12 * k, b.Size.Height));
                    ModernSkin.SpacedText(g, title, f, ModernTheme.AccentDim, new RectangleF(18 * k, 0, ModernSkin.Measure(title, f) + title.Length * 1.4f * k, b.Size.Height), 1.4f * k);
                    ModernSkin.Text(g, count, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 4 * k, b.Size.Height), StringAlignment.Far);
                })
                { Parent = _list.Content, Location = new Point(0, y + S(4)), Size = new Size(w, S(24)) };
                head.Click += (o, e) => { if (!_shut.Remove(gk)) _shut.Add(gk); _dirty = true; };
                y += S(30);
                if (shut) continue;
                foreach (ClientFriend f in grp.Items)
                {
                    ClientFriend fr = f;
                    bool sel = f.Index == _picked;
                    var row = new PaintBox((g, b) => PaintFriend(g, b, fr, sel)) { Parent = _list.Content, Location = new Point(0, y), Size = new Size(w, S(44)) };
                    row.Click += (o, e) => { if (_picked == fr.Index) return; _picked = fr.Index; _memoDraft = (int.MinValue, null); _dirty = true; };
                    y += S(47);
                }
            }
            if (pool.Count == 0)
            {
                int max = Soc.Rules?.MaxFriends ?? 0;
                Empty(all.Count > 0 ? W.T(ClientTextKeys.ModernSocialNoMatch) : blocked ? W.T(ClientTextKeys.ModernSocialNoBlocked)
                    : max > 0 ? W.T(ClientTextKeys.ModernSocialNoFriendsMax, max) : W.T(ClientTextKeys.ModernSocialNoFriends), w);
            }
            _list.FitContent(0);
            _list.ScrollTo(keep);
        }

        private void Empty(string text, int w) =>
            W.Label(_list.Content, text, 0, 26, (int)(w / ModernRoot.K), 40, 12, false, ModernTheme.Dim, StringAlignment.Center, true);

        private static void RowBack(Graphics g, PaintBox b, bool sel)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            Color fill = sel ? W.Over(ModernTheme.Accent, 0.14f) : b.Hovered ? W.Over(ModernTheme.Accent, 0.08f) : W.Over(ModernTheme.FrameLight, 0.05f);
            using (var br = new SolidBrush(fill)) g.FillPath(br, p);
            if (sel || b.Hovered) ModernSkin.Stroke(g, p, sel ? ModernTheme.Accent : W.Over(ModernTheme.Accent, 0.5f));
        }

        private void PaintFriend(Graphics g, PaintBox b, ClientFriend f, bool sel)
        {
            float k = ModernRoot.K;
            RowBack(g, b, sel);
            bool off = !f.Online && !f.Blocked;
            Color D(Color c) => off ? ModernSkin.Dim(c, 0.38f) : c;
            Soc.Medal(g, new RectangleF(8 * k, 7 * k, 30 * k, 30 * k), Soc.Tag(f.Class), ModernTheme.Class(f.Class), f.Blocked ? -1 : f.Online ? 1 : 0, false, 1f, 9, D(ModernTheme.Text));
            float x = 47 * k, w = b.Size.Width - x - 6 * k;
            Font nf = ModernSkin.Font(12, true), sf = ModernSkin.Font(10);
            float nw = Math.Min(ModernSkin.Measure(f.Name, nf) + 2, w * 0.6f);
            ModernSkin.Text(g, f.Name, nf, D(ModernTheme.Text), new RectangleF(x, 6 * k, nw, 16 * k));
            ModernSkin.Text(g, Soc.ClassName(f.Class) + " " + f.Level, sf, D(ModernTheme.Dim), new RectangleF(x + nw + 6 * k, 7 * k, w - nw - 6 * k, 15 * k));
            string sub = f.Blocked ? (f.Memo.Length > 0 ? f.Memo : W.T(ClientTextKeys.ModernSocialBlockedTab))
                : (f.Online ? (f.GuildName.Length > 0 ? f.GuildName : W.T(ClientTextKeys.ModernSocialOnline)) : LastSeen(f)) + (f.Memo.Length > 0 ? " · " + f.Memo : string.Empty);
            ModernSkin.Text(g, sub, sf, D(ModernTheme.Dim), new RectangleF(x, 23 * k, w, 15 * k));
            if (_pending.ContainsKey(f.Index)) ModernSkin.Text(g, "…", nf, ModernTheme.Accent, new RectangleF(0, 6 * k, b.Size.Width - 6 * k, 16 * k), StringAlignment.Far);
        }

        private static void PaintRequest(Graphics g, PaintBox b, FriendRequestInfo r)
        {
            float k = ModernRoot.K;
            using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f), ModernTheme.Radius * k))
            using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
            Soc.Medal(g, new RectangleF(8 * k, 7 * k, 30 * k, 30 * k), Soc.Tag(r.Class), ModernTheme.Class(r.Class), -1, false, 1f, 9, ModernTheme.Text);
            // desktop: the column is narrow beside the two buttons, so class / level go to the second line with the countdown
            float x = 47 * k, w = b.Size.Width - x - 124 * k;
            ModernSkin.Text(g, r.Name, ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(x, 6 * k, w, 16 * k));
            long left = Math.Max(0, (r.Expires - CMain.Time + 999) / 1000);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialReqSub, Soc.ClassName(r.Class), r.Level, left), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(x, 23 * k, w, 15 * k));
        }

        private void RenderDetail()
        {
            Soc.Clear(_detail);
            _inspect = null;
            _save = null;
            _memo = null;
            _memoCount = null;
            int dw = WidthHud - LeftW - 1, dh = (int)Math.Round(_detail.Size.Height / ModernRoot.K);
            if (_tab == Tab.Requests)
            {
                W.Label(_detail, W.T(Model.Requests.Count > 0 ? ClientTextKeys.ModernSocialRequestsHelp1 : ClientTextKeys.ModernSocialRequestsHelp2), 20, 30, dw - 40, 60, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
                return;
            }
            List<ClientFriend> pool = Entries(_tab == Tab.Blocked);
            ClientFriend f = Find(_picked);
            if (f != null && f.Blocked != (_tab == Tab.Blocked)) f = null;
            if (f == null) foreach (ClientFriend x in pool) if (x.Online) { f = x; break; }
            if (f == null && pool.Count > 0) f = pool[0];
            if (f == null)
            {
                _picked = int.MinValue;
                W.Label(_detail, W.T(_tab == Tab.Blocked ? ClientTextKeys.ModernSocialBlockedHelp : ClientTextKeys.ModernSocialFriendsHelp), 20, 30, dw - 40, 40, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
                return;
            }
            if (_picked != f.Index)
            {
                _picked = f.Index;
                _memoDraft = (int.MinValue, null);
                RenderList(); // the selection highlight
            }
            bool busy = _pending.ContainsKey(f.Index);
            ClientFriend fr = f;

            // profile
            new PaintBox((g, b) => PaintProfile(g, b, fr)) { Parent = _detail, Location = new Point(S(16), S(14)), Size = new Size(S(dw - 32), S(60)), NotControl = true };

            // memo
            int my = 92;
            new IconView(ModernIcons.Memo, 13, ModernTheme.AccentDim) { Parent = _detail, Location = new Point(S(16), S(my)) };
            W.Caption(_detail, W.T(ClientTextKeys.ModernSocialMemo), 34, my, 120);
            W.Label(_detail, W.T(ClientTextKeys.ModernSocialMemoOnlyYou), 16, my, dw - 32, 14, 10, false, ModernTheme.Dim, StringAlignment.Far);
            string saved = (f.Memo ?? string.Empty).Replace("\r\n", " ");
            _memo = W.At(new ModernTextField(dw - 32, 30, 12) { MaxLength = MaxMemo, Placeholder = W.T(ClientTextKeys.ModernSocialMemoPlaceholder) }, _detail, 16, my + 19);
            _memo.Text = _memoDraft.Index == f.Index && _memoDraft.Text != null ? _memoDraft.Text : saved;
            _memoCount = W.Label(_detail, string.Empty, 16, my + 56, 120, 16, 10, false, ModernTheme.Dim);
            _save = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialSaveMemo)), _detail, dw - 16 - 100, my + 54);
            _save.Size = new Size(S(100), S(24));
            _save.Click += (o, e) => SaveMemo(fr);
            void Sync(string v)
            {
                v ??= string.Empty;
                if (_memoCount == null || _save == null) return;
                _memoCount.Text = v.Length + " / " + MaxMemo;
                string t = v.Trim();
                _save.SetDisabled(busy || t.Length == 0 || v == saved, busy ? W.T(ClientTextKeys.ModernSocialSaving) : t.Length == 0 ? W.T(ClientTextKeys.ModernSocialMemoNoClear) : W.T(ClientTextKeys.ModernSocialNoChanges));
            }
            _memo.TextChanged += v => { _memoDraft = (fr.Index, v); Sync(v); };
            _memo.Submitted += _ => { if (!_save.Disabled) SaveMemo(fr); };
            Sync(_memo.Text);

            // actions
            W.Divider(_detail, 0, dh - 48, dw);
            int ay = dh - 38, x0 = 12;
            ModernButton Act(string text, ButtonKind kind, Action run, ref int x, string hint = null, bool right = false)
            {
                var b = new ModernButton(text, kind) { Parent = _detail, FontSize = 10 };
                b.AutoWidth(0);
                b.Size = new Size(b.Size.Width, S(26));
                int wh = (int)Math.Round(b.Size.Width / ModernRoot.K);
                if (right) { x -= wh; b.Location = new Point(S(x), S(ay)); x -= 5; }
                else { b.Location = new Point(S(x), S(ay)); x += wh + 5; }
                if (hint != null) b.Tooltip = hint;
                b.Click += (o, e) => run();
                return b;
            }
            int rx = dw - 12;
            if (f.Blocked)
            {
                Act(W.T(ClientTextKeys.ModernSocialUnblock), ButtonKind.Normal, () => Remove(fr), ref rx, null, true).SetDisabled(busy, W.T(ClientTextKeys.ModernSocialSaving));
                return;
            }
            var whisper = Act(W.T(ClientTextKeys.ModernWinWhisper), ButtonKind.Main, () => Soc.Prefill("/" + fr.Name + " "), ref x0, W.T(ClientTextKeys.ModernSocialWhisperHint));
            if (!f.Online) whisper.SetDisabled(true, W.T(ClientTextKeys.PlayerNotOnline));
            var invite = Act(W.T(ClientTextKeys.ModernWinInvite), ButtonKind.Normal, () => Network.Enqueue(new C.AddMember { Name = fr.Name }), ref x0, W.T(ClientTextKeys.ModernWinInviteHint));
            if (!f.Online) invite.SetDisabled(true, W.T(ClientTextKeys.PlayerNotOnline));
            else if (GameScene.Models.Group.Contains(f.Name)) invite.SetDisabled(true, W.T(ClientTextKeys.ModernSocialAlreadyGrouped));
            if (Soc.Wm != null && Soc.Wm.IsRegistered(MailWindow.WindowKey))
                Act(W.T(ClientTextKeys.ModernWinMail), ButtonKind.Normal, () => ComposeMail(fr.Name), ref x0, W.T(ClientTextKeys.ModernWinMailHint));
            _inViewId = f.Online ? (Soc.InView(f.Name)?.ObjectID ?? 0) : 0;
            _inspect = Act(W.T(ClientTextKeys.ModernSocialInspect), ButtonKind.Normal, () => { if (_inViewId != 0) Network.Enqueue(new C.Inspect { ObjectID = _inViewId }); }, ref x0);
            _inspect.SetDisabled(_inViewId == 0, W.T(ClientTextKeys.ModernSocialInViewOnly));
            Act(W.T(ClientTextKeys.ModernSocialRemove), ButtonKind.Warn, () => Remove(fr), ref rx, null, true).SetDisabled(busy, W.T(ClientTextKeys.ModernSocialSaving));
            Act(W.T(ClientTextKeys.ModernSocialBlock), ButtonKind.Normal, () => Block(fr), ref rx, null, true).SetDisabled(busy, W.T(ClientTextKeys.ModernSocialSaving));
        }

        private static void PaintProfile(Graphics g, PaintBox b, ClientFriend f)
        {
            float k = ModernRoot.K;
            RectangleF av = new RectangleF(2 * k, 2 * k, 56 * k, 56 * k);
            Soc.Medal(g, av, Soc.Tag(f.Class), ModernTheme.Class(f.Class), f.Blocked ? -1 : f.Online ? 1 : 0, false, 2f, 14, ModernTheme.Text);
            float x = 72 * k, w = b.Size.Width - x;
            Font nf = ModernSkin.Font(18, true);
            float nw = Math.Min(ModernSkin.Measure(f.Name, nf) + 2, w - 90 * k);
            ModernSkin.Text(g, f.Name, nf, Color.White, new RectangleF(x, 4 * k, nw, 24 * k));
            string pill = W.T(f.Blocked ? ClientTextKeys.ModernSocialBlockedTab : f.Online ? ClientTextKeys.ModernSocialOnline : ClientTextKeys.ModernSocialOffline);
            Soc.Pill(g, x + nw + 8 * k, 9 * k, pill, f.Blocked ? ModernTheme.BadSoft : f.Online ? ModernTheme.Ok : ModernTheme.AccentDim, 9, false, false, 15);
            string meta = W.T(ClientTextKeys.ModernWinLevelClass, f.Level, Soc.ClassName(f.Class));
            Font mf = ModernSkin.Font(11, true), df = ModernSkin.Font(11);
            float mw = ModernSkin.Measure(meta, mf) + 2;
            ModernSkin.Text(g, meta, mf, ModernTheme.Text, new RectangleF(x, 30 * k, mw, 15 * k));
            if (!f.Online && !f.Blocked) ModernSkin.Text(g, " · " + LastSeen(f), df, ModernTheme.Dim, new RectangleF(x + mw, 30 * k, w - mw, 15 * k));
            string guild = f.GuildName.Length > 0 ? W.T(ClientTextKeys.ModernSocialGuildOf, f.GuildName) : W.T(ClientTextKeys.ModernSocialNoGuild);
            ModernSkin.Text(g, guild, df, ModernTheme.Dim, new RectangleF(x, 45 * k, w, 15 * k));
        }
    }
}
