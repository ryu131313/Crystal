namespace Server.MirObjects
{
    /// <summary>
    /// Quest V2 entry points (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Accept / complete / turn in"). The
    /// wave 2 kick-off only routes the six V2 client packets here from MirConnection.ProcessPacket (Stage == Game, on the game
    /// thread); lane F fills the bodies (PlayerQuests, conditions, rewards, availability). Validate every argument here: the
    /// values come straight from the client.
    /// </summary>
    public partial class PlayerObject
    {
        /// <summary>C.QuestAccept. <paramref name="npcObjectId"/> = the giver's ObjectID, 0 = not at an NPC (an S.QuestOffer prompt).</summary>
        public void QuestAccept(int quest, uint npcObjectId)
        {
            // lane F
        }

        /// <summary>C.QuestTurnIn. <paramref name="rewardPicks"/> = positions in the definition's Rewards list, one per choice group.</summary>
        public void QuestTurnIn(int quest, uint npcObjectId, List<int> rewardPicks)
        {
            // lane F
        }

        /// <summary>C.QuestAbandon.</summary>
        public void QuestAbandon(int quest)
        {
            // lane F
        }

        /// <summary>C.QuestShare.</summary>
        public void QuestShare(int quest)
        {
            // lane F
        }

        /// <summary>C.QuestChoose: a branch ChoiceKey at an NPC.</summary>
        public void QuestChoose(int quest, uint npcObjectId, string choiceKey)
        {
            // lane F
        }

        /// <summary>C.QuestDefinitionRequest: answer with S.QuestDefinition only for a quest in the player's runs or history.</summary>
        public void QuestDefinitionRequest(int quest)
        {
            // lane F
        }
    }
}
