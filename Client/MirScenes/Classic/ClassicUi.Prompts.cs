using Client.MirScenes.Models;
using S = ServerPackets;

namespace Client.MirScenes.Classic
{
    public sealed partial class ClassicUi
    {
        public void QuestShareRequest(S.ShareQuest p, ClientQuestInfo quest) => _scene.ClassicQuestShareRequest(p, quest);
        public void GuildInviteRequest(S.GuildInvite p) => _scene.ClassicGuildInviteRequest(p);
        public void GuildNameRequest() => _scene.ClassicGuildNameRequest();
        public void GuildWarRequest() => _scene.ClassicGuildWarRequest();
        public void MarriageRequest(S.MarriageRequest p) => _scene.ClassicMarriageRequest(p);
        public void DivorceRequest(S.DivorceRequest p) => _scene.ClassicDivorceRequest(p);
        public void MentorRequest(S.MentorRequest p) => _scene.ClassicMentorRequest(p);
        public void TradeRequest(S.TradeRequest p) => _scene.ClassicTradeRequest(p);
        public void ReincarnationRequest() => _scene.ClassicReincarnationRequest();
        public void ConfirmQuit() => _scene.ClassicConfirmQuit();
        public void ConfirmLogOut() => _scene.ClassicConfirmLogOut();
        public void MapChanged(int mapIndex) { } // the classic dialogs read MapControl when they draw
        public void PatchStatusChanged() => _scene.ClassicPatchStatusChanged();

        // S.FriendRequest is model-only (Models.Friends); the classic prompt listens to it like ModernUi's toast does
        private Action<FriendRequestInfo> _friendRequest;
        private Action _friendRequestsChanged;

        private void ListenFriendRequests()
        {
            _friendRequest = _scene.ClassicFriendRequest;
            _friendRequestsChanged = _scene.ClassicFriendRequestsChanged;
            GameScene.Models?.Friends.RequestReceived.On(_friendRequest);
            GameScene.Models?.Friends.RequestsChanged.On(_friendRequestsChanged);
        }

        private void StopFriendRequests()
        {
            if (_friendRequest != null) GameScene.Models?.Friends.RequestReceived.Off(_friendRequest);
            if (_friendRequestsChanged != null) GameScene.Models?.Friends.RequestsChanged.Off(_friendRequestsChanged);
            _friendRequest = null;
            _friendRequestsChanged = null;
        }
    }
}
