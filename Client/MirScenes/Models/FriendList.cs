namespace Client.MirScenes.Models
{
    /// <summary>
    /// The friend and block list (port of the WebClient/src/game/friends.ts subset). Was FriendDialog.Friends
    /// (S.FriendUpdate replaces it).
    /// </summary>
    public sealed class FriendList
    {
        public readonly List<ClientFriend> Friends = new List<ClientFriend>();

        public readonly Emitter Changed = new Emitter();

        public void Set(List<ClientFriend> friends)
        {
            Friends.Clear();
            if (friends != null) Friends.AddRange(friends);
            Changed.Emit();
        }
    }
}
