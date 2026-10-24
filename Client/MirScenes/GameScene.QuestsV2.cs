using S = ServerPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// Quest V2 packet handlers (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Wire protocol"). The wave 2
    /// kick-off only routes the packets here from GameScene.ProcessPacket; lanes C (desktop modern) and D (classic adapter) fill
    /// the bodies (a quest model in GameScene.Models, then the UI). UI thread only, like every ProcessPacket handler.
    /// </summary>
    public sealed partial class GameScene
    {
        private void QuestDefinition(S.QuestDefinition p)
        {
            // lane C
        }

        private void QuestStates(S.QuestStates p)
        {
            // lane C
        }

        private void QuestProgress(S.QuestProgress p)
        {
            // lane C
        }

        private void QuestHistory(S.QuestHistory p)
        {
            // lane C
        }

        private void QuestAvailability(S.QuestAvailability p)
        {
            // lane C
        }

        private void QuestOffer(S.QuestOffer p)
        {
            // lane C
        }
    }
}
