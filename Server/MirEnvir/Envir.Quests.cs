using Server.MirEnvir.Quests;

namespace Server.MirEnvir
{
    /// <summary>
    /// Quest system V2 definition data of an envir (UserData-and-Quests-Plan.md, Part B): the top-level shared tables and the stable id
    /// counters. The per-quest tables are lists on <see cref="QuestInfo.Definition"/>. Saved in SystemData.db (SqliteWorldCodec); the
    /// binary Server.MirDB does not carry them. Authored in the web admin Quests page; edits reach Main through the quest-replace hook.
    /// </summary>
    public partial class Envir
    {
        public List<QuestConditionInfo> QuestConditionList = new List<QuestConditionInfo>();
        public List<QuestTextInfo> QuestTextList = new List<QuestTextInfo>();
        public List<StorylineInfo> StorylineList = new List<StorylineInfo>();
        public List<QuestGroupInfo> QuestGroupList = new List<QuestGroupInfo>();
        public List<QuestPoolInfo> QuestPoolList = new List<QuestPoolInfo>();

        /// <summary>Stable id counters: they only go up (a deleted id is retired for good).</summary>
        public int QuestStepIndex, QuestObjectiveIndex, QuestConditionIndex, QuestGroupIndex, QuestTextIndex, QuestVarIndex, StorylineIndex, QuestPoolIndex;

        private QuestCatalog _questCatalog;

        /// <summary>
        /// Runtime lookups over this envir's quest V2 definitions (Main, game thread only): built on first use from the lists above and
        /// QuestInfoList. Never mutated; <see cref="InvalidateQuestCatalog"/> drops it so the next use rebuilds it.
        /// </summary>
        public QuestCatalog QuestCatalog => _questCatalog ??= QuestCatalog.Build(this);

        /// <summary>
        /// Drops the quest catalog (game thread). The caller (the LiveDb quest-replace hook, QuestReplace.Apply) then migrates every online
        /// player with <c>PlayerQuests.ApplyDefinitionChange</c>, so no player keeps references into the old graph.
        /// </summary>
        public void InvalidateQuestCatalog() => _questCatalog = null;
    }
}
