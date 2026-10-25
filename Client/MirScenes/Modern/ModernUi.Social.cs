using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 4 area "social" (worker B): group, trade, friends (+ requests), mentor (+ finder), relationship, quests, and the
    /// social toggles the mentor / relationship windows show. State: Models.Group (+ MemberStatus), Models.Trade, Models.Friends
    /// (+ Requests), Models.Relationship, Models.AllowToggles, Models.Quests (quest V2). Window keys "group", "trade", "friends",
    /// "mentor", "relationship", "quests" (Client\MirScenes\Modern\Windows\Group*, Trade*, Friends*, Mentor*, Relationship*,
    /// Quests*). The trade window opens only when the server opens a trade (S.TradeAccept). The quest progress toasts sit on the
    /// HUD layer (Hud\QuestToastHud) and the group log follows the roster from log-in.
    /// </summary>
    public sealed partial class ModernUi
    {
        private Action<FriendRequestInfo> _friendRequestToast;
        private Action _friendRequestsChanged;
        private QuestToastHud _questToasts;

        /// <summary>An open request toast; Settled = answered elsewhere (or replaced, or log-out): closing it sends nothing.</summary>
        private sealed class ToastRef
        {
            public PopupHandle Handle;
            public bool Settled;

            public void Settle()
            {
                Settled = true;
                Handle?.Close();
            }
        }

        private readonly Dictionary<string, ToastRef> _friendToasts = new Dictionary<string, ToastRef>(StringComparer.OrdinalIgnoreCase);
        private ToastRef _tradeToast;

        partial void RegisterSocialWindows()
        {
            DisposeSocial();
            TradeWindow.Ending = false;
            Root.Wm.Register(GroupWindow.WindowKey, () => new GroupWindow());
            Root.Wm.Register(TradeWindow.WindowKey, () => new TradeWindow());
            Root.Wm.Register(FriendsWindow.WindowKey, () => new FriendsWindow());
            Root.Wm.Register(MentorWindow.WindowKey, () => new MentorWindow());
            Root.Wm.Register(RelationshipWindow.WindowKey, () => new RelationshipWindow());
            Root.Wm.Register(QuestsWindow.WindowKey, () => new QuestsWindow());
            GroupLog.Start();
            QuestActions.Reset();
            _questToasts = new QuestToastHud { Parent = Root.Hud };
            _friendRequestToast = FriendRequestToast;
            _friendRequestsChanged = FriendRequestsChanged;
            GameScene.Models?.Friends.RequestReceived.On(_friendRequestToast);
            GameScene.Models?.Friends.RequestsChanged.On(_friendRequestsChanged);
        }

        partial void DisposeSocial()
        {
            if (_friendRequestToast != null) GameScene.Models?.Friends.RequestReceived.Off(_friendRequestToast);
            if (_friendRequestsChanged != null) GameScene.Models?.Friends.RequestsChanged.Off(_friendRequestsChanged);
            _friendRequestToast = null;
            _friendRequestsChanged = null;
            // log-out: the open request toasts close without an answer (ModernRoot.Dispose closes them after this)
            foreach (ToastRef t in new List<ToastRef>(_friendToasts.Values)) t.Settled = true;
            if (_tradeToast != null) _tradeToast.Settled = true;
            GroupLog.Stop();
            TradeWindow.Ending = true; // log-out: closing the trade window sends nothing (the server cancels the trade)
            _questToasts = null;       // a child of Root.Hud: disposed with it
        }

        /// <summary>KeybindOptions.Group / Friends / Mentor / Relationship / Quests toggle their windows; AddGroupMember invites the
        /// player under the mouse (the classic GroupDialog.AddMember rules). Trade is GameScene's (C.TradeRequest).</summary>
        private partial bool SocialKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Group:
                    Root.Wm.Toggle(GroupWindow.WindowKey);
                    return true;
                case KeybindOptions.Friends:
                    Root.Wm.Toggle(FriendsWindow.WindowKey);
                    return true;
                case KeybindOptions.Mentor:
                    Root.Wm.Toggle(MentorWindow.WindowKey);
                    return true;
                case KeybindOptions.Relationship:
                    Root.Wm.Toggle(RelationshipWindow.WindowKey);
                    return true;
                case KeybindOptions.Quests:
                    Root.Wm.Toggle(QuestsWindow.WindowKey);
                    return true;
                case KeybindOptions.AddGroupMember:
                    if (MapObject.MouseObject == null || MapObject.MouseObject.Race != ObjectType.Player) return true;
                    AddGroupMember(MapObject.MouseObject.Name);
                    return true;
            }
            return false;
        }

        /// <summary>GroupDialog.AddMember: refused (chat line) when the group is full or you are not its leader.</summary>
        private static void AddGroupMember(string name)
        {
            GroupState g = GameScene.Models.Group;
            if (g.Members.Count >= Globals.MaxGroup)
            {
                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.GroupHasMaxMembers), ChatType.System);
                return;
            }
            if (g.Members.Count > 0 && g.Members[0] != GameScene.User?.Name)
            {
                GameScene.Models.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YouAreNotGroupLeader), ChatType.System);
                return;
            }
            Network.Enqueue(new C.AddMember { Name = name });
        }

        private TradeWindow TradeWin => Root != null && !Root.IsDisposed ? Root.Wm.Get(TradeWindow.WindowKey) as TradeWindow : null;
        private TradeWindow OpenTradeWin => Root != null && !Root.IsDisposed && Root.Wm.IsOpen(TradeWindow.WindowKey) ? Root.Wm.Get(TradeWindow.WindowKey) as TradeWindow : null;

        /// <summary>S.TradeAccept, after Models.Trade.GuestName (opens the trade window; TradeDialog.TradeAccept).</summary>
        public void TradeAccepted()
        {
            _tradeToast?.Settle(); // the trade started: an open request toast closes without an answer
            _tradeToast = null;
            TradeWindow w = TradeWin;
            if (w == null) return;
            w.Start();
            Root.Wm.Open(TradeWindow.WindowKey);
        }

        /// <summary>The guest's offer changed; User.TradeLocked is already false.</summary>
        public void TradeGuestChanged() => OpenTradeWin?.GuestChanged();

        /// <summary>S.TradeConfirm: Models.Trade.Reset already ran (the window shows what changed hands).</summary>
        public void TradeConfirmed() => OpenTradeWin?.Confirmed();

        /// <summary>S.TradeCancel: unlock = only the lock cleared; else Models.Trade.Reset ran (close + tell the user, DealCancelledMustFace).</summary>
        public void TradeCancelled(bool unlock)
        {
            TradeWindow w = OpenTradeWin;
            bool tell = w?.Cancelled(unlock) ?? false;
            if (!tell || Root == null || Root.IsDisposed) return;
            Root.Popups.Confirm(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DealCancelledMustFace), PopupButtons.Ok, PopupKind.Info, null, false,
                GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernWinTrade));
        }

        /// <summary>
        /// For the integrator: ModernUi.ItemTradeUnlocked (ModernUi.Items.cs, a trade item move came back) should call this — the
        /// classic TradeDialog.ChangeLockState(false). Until then the trade window notices the grid change itself.
        /// </summary>
        internal void SocialTradeItemReturned() => OpenTradeWin?.ItemReturned();

        /// <summary>Models.Friends.Changed already fired (the friends window listens to it).</summary>
        public void FriendsChanged() { }
        /// <summary>Models.Relationship already holds the values (LoverChanged fired; the windows listen to it).</summary>
        public void LoverUpdate(S.LoverUpdate p) { }
        /// <summary>Models.Relationship already holds the values (MentorChanged fired; the windows listen to it).</summary>
        public void MentorUpdate(S.MentorUpdate p) { }

        // ---- prompts (classic: MirMessageBox in Classic\GameScene.ClassicPrompts.cs)

        /// <summary>S.TradeRequest: a request toast (a newer request replaces it without an answer; the trade starting closes it).</summary>
        public void TradeRequest(S.TradeRequest p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            _tradeToast?.Settle();
            var t = new ToastRef();
            _tradeToast = t;
            t.Handle = Root.Popups.Request(p.Name, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PlayerRequestedTrade, p.Name), 30,
                () => { if (_tradeToast == t) _tradeToast = null; if (!t.Settled) Network.Enqueue(new C.TradeReply { AcceptInvite = true }); },
                () => { if (_tradeToast == t) _tradeToast = null; if (!t.Settled) Network.Enqueue(new C.TradeReply { AcceptInvite = false }); });
        }

        public void MarriageRequest(S.MarriageRequest p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            Root.Popups.Request(p.Name, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PlayerAskedForMarriage, p.Name), 60,
                () => Network.Enqueue(new C.MarriageReply { AcceptInvite = true }),
                () => { if (!_ending) Network.Enqueue(new C.MarriageReply { AcceptInvite = false }); });
        }

        public void DivorceRequest(S.DivorceRequest p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            Root.Popups.Request(p.Name, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PlayerRequestedDivorce, p.Name), 60,
                () => Network.Enqueue(new C.DivorceReply { AcceptInvite = true }),
                () => { if (!_ending) Network.Enqueue(new C.DivorceReply { AcceptInvite = false }); });
        }

        public void MentorRequest(S.MentorRequest p)
        {
            if (Root == null || Root.IsDisposed || p == null || GameScene.User == null) return;
            string text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PlayerRequestedTeachClass, p.Name, p.Level, GameScene.User.Class.ToString());
            Root.Popups.Request(p.Name, text, 60,
                () => Network.Enqueue(new C.MentorReply { AcceptInvite = true }),
                () => { if (!_ending) Network.Enqueue(new C.MentorReply { AcceptInvite = false }); });
        }

        /// <summary>S.QuestOffer (a share, or an item / area / auto start): a request toast; accept sends C.QuestAccept { NpcObjectID = 0 },
        /// declining sends nothing (as classic's No). The definition arrives just before the offer (the quest's name in the text).</summary>
        public void QuestOffer(S.QuestOffer p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            int quest = p.Quest;
            string name = GameScene.Models?.Quests.Def(quest)?.Name ?? string.Empty;
            bool shared = !string.IsNullOrEmpty(p.SharedBy);
            string text = shared
                ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernQuestSharedOffer, p.SharedBy, name)
                : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.QuestOfferPrompt, name);
            Root.Popups.Request(shared ? p.SharedBy : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Quests), text, 60,
                () => Network.Enqueue(new C.QuestAccept { Quest = quest, NpcObjectID = 0 }), null);
        }

        /// <summary>S.QuestRefused: the quest's buttons unlock at once and the reason shows as a quest banner.</summary>
        public void QuestRefused(S.QuestRefused p)
        {
            if (p == null) return;
            QuestActions.Unlock(p.Quest);
            if (_questToasts != null && !_questToasts.IsDisposed) _questToasts.Refusal(p);
        }

        /// <summary>S.FriendRequest (Models.Friends.RequestReceived): a request toast for the 60 s the server keeps it. A repeat
        /// replaces it; answering in the friends window (or the request settling) closes it without a second answer.</summary>
        private void FriendRequestToast(FriendRequestInfo r)
        {
            if (Root == null || Root.IsDisposed || r == null) return;
            string name = r.Name;
            if (_friendToasts.TryGetValue(name, out ToastRef old)) old.Settle();
            var t = new ToastRef();
            _friendToasts[name] = t;
            int seconds = (int)Math.Max(1, (r.Expires - CMain.Time) / 1000);
            void Done()
            {
                if (_friendToasts.TryGetValue(name, out ToastRef cur) && cur == t) _friendToasts.Remove(name);
            }
            t.Handle = Root.Popups.Request(name, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernSocialFriendRequest, name, r.Level), seconds,
                () => { Done(); if (!t.Settled) GameScene.Models?.Friends.Reply(name, true); },
                () => { Done(); if (!t.Settled) GameScene.Models?.Friends.Reply(name, false); });
        }

        /// <summary>A request left Models.Friends.Requests (answered in the window, settled by a list update): close its toast.</summary>
        private void FriendRequestsChanged()
        {
            if (_friendToasts.Count == 0) return;
            List<FriendRequestInfo> live = GameScene.Models?.Friends.Requests;
            foreach (var kv in new List<KeyValuePair<string, ToastRef>>(_friendToasts))
            {
                bool still = false;
                if (live != null) foreach (FriendRequestInfo r in live) if (string.Equals(r.Name, kv.Key, StringComparison.OrdinalIgnoreCase)) { still = true; break; }
                if (!still) kv.Value.Settle();
            }
        }
    }
}
