using Client.MirObjects;
using S = ServerPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// Quest V2 packet handlers (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Wire protocol"). Game state goes
    /// into GameScene.Models.Quests (QuestBook); the NPC objects take their server-picked QuestIcon from S.QuestAvailability; the
    /// UI hears about it through IGameUi (QuestsChanged after every change, QuestAccepted per new run, QuestOffer for a prompt).
    /// UI thread only, like every ProcessPacket handler.
    /// </summary>
    public sealed partial class GameScene
    {
        private void QuestDefinition(S.QuestDefinition p)
        {
            Models.Quests.ApplyDefinition(p);
            Ui.QuestsChanged();
        }

        private void QuestStates(S.QuestStates p)
        {
            Models.Quests.ApplyStates(p);
            List<(int Quest, bool Track)> added = Models.Quests.LastAccepted;
            for (int i = 0; i < added.Count; i++) Ui.QuestAccepted(added[i].Quest, added[i].Track);
            Ui.QuestsChanged();
        }

        private void QuestProgress(S.QuestProgress p)
        {
            Models.Quests.ApplyProgress(p);
            Ui.QuestsChanged();
        }

        private void QuestHistory(S.QuestHistory p)
        {
            Models.Quests.ApplyHistory(p);
            Ui.QuestsChanged();
        }

        private void QuestAvailability(S.QuestAvailability p)
        {
            Models.Quests.ApplyAvailability(p);
            for (int i = 0; i < p.Npcs.Count; i++)
                if (MapControl.Objects.TryGetValue(p.Npcs[i].ObjectID, out MapObject ob) && ob is NPCObject npc) npc.SetQuestIcon(p.Npcs[i].Icon);
            Ui.QuestsChanged();
        }

        private void QuestOffer(S.QuestOffer p)
        {
            Ui.QuestOffer(p);
        }

        /// <summary>S.QuestRefused: the model tells the UI (unlock the quest's buttons), then the UI shows the reason.</summary>
        private void QuestRefused(S.QuestRefused p)
        {
            Models.Quests.ApplyRefused(p);
            Ui.QuestRefused(p);
            Ui.QuestsChanged();
        }
    }
}
