using Server.MirEnvir.Quests;

namespace Server.MirEnvir
{
    /// <summary>
    /// Quest system V2 definition data of an envir (UserData-and-Quests-Plan.md, Part B phase 1): the top-level shared tables and the
    /// stable id counters. The per-quest tables are lists on <see cref="QuestInfo"/>. Saved in SystemData.db (SqliteWorldCodec); the
    /// binary Server.MirDB does not carry them. Legacy QuestInfo stays the runtime in this phase: nothing here is read per tick.
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

        /// <summary>
        /// World start (Main, game thread, after LoadDB): when the store has legacy quests and no V2 quest rows, imports them on a
        /// background thread (Envir.Edit under EditLock, then SaveEdit; never on the game thread, server.md §3.2). Main keeps running on
        /// what it loaded; the imported rows are RestartOnly and unused at runtime in this phase.
        /// </summary>
        /// <summary>The world-start quest import is running (it saves Envir.Edit): WinForms editors must not open meanwhile.</summary>
        public static bool QuestImportRunning => QuestImportRunner.BackgroundRunning;

        private QuestCatalog _questCatalog;

        /// <summary>
        /// Runtime lookups over this envir's quest V2 definitions (Main, game thread only): built on first use from the lists above and
        /// QuestInfoList. Never mutated; <see cref="InvalidateQuestCatalog"/> drops it so the next use rebuilds it.
        /// </summary>
        public QuestCatalog QuestCatalog => _questCatalog ??= QuestCatalog.Build(this);

        /// <summary>
        /// Drops the quest catalog (game thread). The caller (wave 3: the LiveDb quest-replace hook) then migrates every online player
        /// with <c>PlayerQuests.ApplyDefinitionChange</c>, so no player keeps references into the old graph.
        /// </summary>
        public void InvalidateQuestCatalog() => _questCatalog = null;

        private void StartQuestDefinitionImport()
        {
            if (this != Main || !(Store is Storage.SqliteWorldStore)) return;
            if (QuestInfoList.Count == 0) return;
            for (var i = 0; i < QuestInfoList.Count; i++)
                if (QuestInfoList[i].HasDefinition) return;

            QuestImportRunner.StartBackground("world start: the quest V2 tables are empty");
        }
    }
}
