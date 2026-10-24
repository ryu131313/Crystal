namespace Server.MirObjects
{
    /// <summary>
    /// Quest V2 entry points (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Accept / complete / turn in"): the six V2
    /// client packets arrive here from MirConnection.ProcessPacket (Stage == Game, on the game thread) and go to <see cref="Quests"/>
    /// (Player/PlayerQuests*.cs), which validates every argument (the values come straight from the client).
    /// </summary>
    public partial class PlayerObject
    {
        /// <summary>C.QuestAccept. <paramref name="npcObjectId"/> = the giver's ObjectID, 0 = not at an NPC (an S.QuestOffer prompt).</summary>
        public void QuestAccept(int quest, uint npcObjectId)
        {
            Quests.Accept(quest, npcObjectId);
        }

        /// <summary>C.QuestTurnIn. <paramref name="rewardPicks"/> = positions in the definition's Rewards list, one per choice group.</summary>
        public void QuestTurnIn(int quest, uint npcObjectId, List<int> rewardPicks)
        {
            if (rewardPicks != null && rewardPicks.Count > 16) return; // one pick per choice group; more is a bad client
            Quests.TurnIn(quest, npcObjectId, rewardPicks);
        }

        /// <summary>C.QuestAbandon.</summary>
        public void QuestAbandon(int quest)
        {
            Quests.Abandon(quest);
        }

        /// <summary>C.QuestShare.</summary>
        public void QuestShare(int quest)
        {
            Quests.Share(quest);
        }

        /// <summary>C.QuestChoose: a branch ChoiceKey at an NPC.</summary>
        public void QuestChoose(int quest, uint npcObjectId, string choiceKey)
        {
            if (choiceKey != null && choiceKey.Length > 64) return;
            Quests.Choose(quest, npcObjectId, choiceKey);
        }

        /// <summary>C.QuestDefinitionRequest: answer with S.QuestDefinition only for a quest in the player's runs or history.</summary>
        public void QuestDefinitionRequest(int quest)
        {
            Quests.DefinitionRequest(quest);
        }

        /// <summary>Calls a page of the default NPC script for this player (quest step OnEnterPage / OnCompletePage).</summary>
        public void CallDefaultNPCPage(string page)
        {
            var key = page.StartsWith("[", StringComparison.Ordinal) ? page.ToUpperInvariant() : "[" + page.ToUpperInvariant() + "]";
            DelayedAction action = new DelayedAction(DelayedType.NPC, Envir.Time, Envir.DefaultNPC.LoadedObjectID, Envir.DefaultNPC.ScriptID, key);
            ActionList.Add(action);
            Enqueue(new ServerPackets.NPCUpdate { NPCID = Envir.DefaultNPC.LoadedObjectID });
        }
    }
}
