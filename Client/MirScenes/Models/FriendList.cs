using Client.MirNetwork;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Models
{
    /// <summary>
    /// The friend and block list (port of WebClient/src/game/friends.ts). Was FriendDialog.Friends (S.FriendUpdate replaces
    /// it). Phase 2.7 requests: C.AddFriend (not blocked) asks the target; the target gets S.FriendRequest (online only, kept
    /// 60 s by the server) and answers with <see cref="Reply"/> (C.FriendRequestReply); the asker gets S.FriendRequestResult.
    /// The server also sends a chat line for every outcome.
    /// </summary>
    public sealed class FriendList
    {
        /// <summary>How long an incoming request stays answerable (the server's expiry).</summary>
        public const long RequestMs = 60000;

        public readonly List<ClientFriend> Friends = new List<ClientFriend>();
        /// <summary>Pending incoming requests, oldest first (expired ones are dropped by <see cref="Prune"/>).</summary>
        public readonly List<FriendRequestInfo> Requests = new List<FriendRequestInfo>();

        public readonly Emitter Changed = new Emitter();
        /// <summary>S.FriendRequest arrived (after it was added to <see cref="Requests"/>).</summary>
        public readonly Emitter<FriendRequestInfo> RequestReceived = new Emitter<FriendRequestInfo>();
        /// <summary>The pending requests changed (added, answered, expired or settled by a list update).</summary>
        public readonly Emitter RequestsChanged = new Emitter();
        /// <summary>S.FriendRequestResult: what happened to a request the user sent (Name = the player it went to).</summary>
        public readonly Emitter<S.FriendRequestResult> ResultReceived = new Emitter<S.FriendRequestResult>();

        public void Set(List<ClientFriend> friends)
        {
            Friends.Clear();
            if (friends != null) Friends.AddRange(friends);
            // a request from someone who is now a friend was settled (both sides added each other)
            bool settled = false;
            for (int i = 0; i < Friends.Count; i++)
                if (Friends[i] != null && !Friends[i].Blocked && RemoveRequest(Friends[i].Name)) settled = true;
            Changed.Emit();
            if (settled) RequestsChanged.Emit();
        }

        public void AddRequest(S.FriendRequest p)
        {
            if (string.IsNullOrEmpty(p?.Name)) return;
            RemoveRequest(p.Name); // a repeat moves to the end with a fresh expiry
            var r = new FriendRequestInfo { Name = p.Name, Class = p.Class, Level = p.Level, Expires = CMain.Time + RequestMs };
            Requests.Add(r);
            RequestReceived.Emit(r);
            RequestsChanged.Emit();
        }

        /// <summary>Answers an incoming request (the server judges expiry, so this sends even after the local timer ran out).</summary>
        public void Reply(string name, bool accept)
        {
            if (string.IsNullOrEmpty(name)) return;
            Network.Enqueue(new C.FriendRequestReply { Name = name, Accept = accept });
            if (RemoveRequest(name)) RequestsChanged.Emit();
        }

        /// <summary>Drops expired requests (call when showing them, not per frame).</summary>
        public void Prune()
        {
            if (Requests.RemoveAll(r => r.Expires <= CMain.Time) > 0) RequestsChanged.Emit();
        }

        private bool RemoveRequest(string name)
        {
            for (int i = 0; i < Requests.Count; i++)
                if (string.Equals(Requests[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    Requests.RemoveAt(i);
                    return true;
                }
            return false;
        }
    }

    /// <summary>An incoming friend request (S.FriendRequest); Expires is on the CMain.Time clock.</summary>
    public sealed class FriendRequestInfo
    {
        public string Name = string.Empty;
        public MirClass Class;
        public ushort Level;
        public long Expires;
    }
}
