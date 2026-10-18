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
    }
}
