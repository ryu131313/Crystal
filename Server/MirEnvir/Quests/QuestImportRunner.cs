using Server.WebAdmin;
using Server.WebAdmin.GameData;

namespace Server.MirEnvir.Quests
{
    /// <summary>Why an import did not run (or that it did).</summary>
    internal sealed class QuestImportOutcome
    {
        public bool Imported;
        public string Message;
        public QuestImportReport Report;
    }

    /// <summary>
    /// Runs <see cref="QuestLegacyImporter"/> on <see cref="Envir.Edit"/> and saves it (UserData-and-Quests-Plan.md, Part B phase 1):
    /// automatically at world start when the V2 tables are empty, and from the web admin's "Re-import legacy quests" action.
    /// <para>Threads: a worker thread only (never the game thread: it takes <see cref="WebAdminGame.EditLock"/>, server.md §3.2). Lock order
    /// EditLock → LoadLock (SaveEdit). Refuses while unsaved grid changes or WinForms editors could be swept into its save.</para>
    /// </summary>
    internal static class QuestImportRunner
    {
        private static int _backgroundRunning;

        private static MessageQueue MessageQueue => MessageQueue.Instance;

        /// <summary>World start: one background import thread (a second start while one runs is ignored).</summary>
        public static void StartBackground(string why)
        {
            if (Interlocked.CompareExchange(ref _backgroundRunning, 1, 0) != 0) return;
            var thread = new Thread(() =>
            {
                try
                {
                    var outcome = Run(why, onlyWhenEmpty: true);
                    MessageQueue.Enqueue(outcome.Imported ? outcome.Report.Summary() : "[Quests] Automatic import skipped: " + outcome.Message);
                }
                catch (Exception ex)
                {
                    MessageQueue.Enqueue("[Quests] Automatic import of the legacy quests failed: " + ex.Message);
                    MessageQueue.Enqueue(ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _backgroundRunning, 0);
                }
            })
            {
                IsBackground = true,
                Name = "Quest definition import",
            };
            thread.Start();
        }

        /// <summary>
        /// Imports the legacy quests into Envir.Edit's V2 tables and saves Envir.Edit (SystemData.db). With <paramref name="onlyWhenEmpty"/>
        /// nothing happens when any quest already has V2 rows. Loads Envir.Edit first when it is not loaded (logged).
        /// </summary>
        public static QuestImportOutcome Run(string why, bool onlyWhenEmpty, bool dryRun = false)
        {
            lock (WebAdminGame.EditLock)
            {
                if (Envir.EditorFormsOpen > 0)
                    return new QuestImportOutcome { Message = "a WinForms game data editor is open (close it first)" };
                if (GameDataState.EditReloadFailed)
                    return new QuestImportOutcome { Message = "the last reload of the editor copy failed; reload it first" };
                if (!dryRun && GameDataState.Unsaved.Changes > 0)
                    return new QuestImportOutcome { Message = GameDataState.Unsaved.Changes + " unsaved game data change(s) would be saved with it; save or discard them first" };

                var edit = Envir.Edit;
                if (!GameDataSave.EditLooksLoaded() || (edit.ItemInfoList.Count == 0 && edit.MonsterInfoList.Count == 0 && edit.MapInfoList.Count == 0 && edit.QuestInfoList.Count == 0))
                {
                    MessageQueue.Enqueue("[Quests] The editor copy (Envir.Edit) is not loaded; loading it before the quest import.");
                    edit.MagicInfoList.Clear(); // LoadDB only adds missing spells
                    if (!GameDataSave.RunFileIo(() => edit.LoadDB()))
                    {
                        GameDataState.EditReloadFailed = true;
                        return new QuestImportOutcome { Message = "loading the editor copy failed (see the [Store] lines)" };
                    }

                    // Edit.LoadDB re-links the guild creation items to the editor copy's items; point them back at the live world's.
                    if (WebAdminGame.IsRunning) WebAdminGame.Post(() => Settings.LinkGuildCreationItems(Envir.Main.ItemInfoList));
                }

                if (onlyWhenEmpty)
                    for (var i = 0; i < edit.QuestInfoList.Count; i++)
                        if (edit.QuestInfoList[i] != null && edit.QuestInfoList[i].HasDefinition)
                            return new QuestImportOutcome { Message = "the quest V2 tables already have rows" };

                if (dryRun)
                {
                    // On a throwaway copy: nothing in Envir.Edit changes. The copy shares the info lists (read only here).
                    var scratch = ScratchCopy(edit);
                    var dry = QuestLegacyImporter.Import(scratch);
                    return new QuestImportOutcome { Message = "dry run", Report = dry };
                }

                MessageQueue.Enqueue("[Quests] Importing the legacy quests into the V2 tables (" + why + ").");
                var report = QuestLegacyImporter.Import(edit);
                GameDataSave.EnsureSavable();
                GameDataSave.SaveEdit();
                return new QuestImportOutcome { Imported = true, Message = report.Summary(), Report = report };
            }
        }

        /// <summary>A detached Envir with copies of Edit's quest graph and V2 tables (items, monsters, NPCs, maps and conquests shared).</summary>
        private static Envir ScratchCopy(Envir edit)
        {
            var e = new Envir();
            e.ItemInfoList = edit.ItemInfoList;
            e.MonsterInfoList = edit.MonsterInfoList;
            e.NPCInfoList = edit.NPCInfoList;
            e.MapInfoList = edit.MapInfoList;
            e.ConquestInfoList = edit.ConquestInfoList;
            e.QuestStepIndex = edit.QuestStepIndex; e.QuestObjectiveIndex = edit.QuestObjectiveIndex; e.QuestConditionIndex = edit.QuestConditionIndex;
            e.QuestGroupIndex = edit.QuestGroupIndex; e.QuestTextIndex = edit.QuestTextIndex; e.QuestVarIndex = edit.QuestVarIndex;
            e.StorylineIndex = edit.StorylineIndex; e.QuestPoolIndex = edit.QuestPoolIndex;
            foreach (var q in edit.QuestInfoList)
            {
                if (q == null) continue;
                var copy = (Server.MirDatabase.QuestInfo)GameDataClone.Clone(q);
                copy.Definition = q.Definition.ShallowCopy();
                e.QuestInfoList.Add(copy);
            }

            e.QuestConditionList.AddRange(edit.QuestConditionList);
            e.QuestTextList.AddRange(edit.QuestTextList);
            e.StorylineList.AddRange(edit.StorylineList);
            e.QuestGroupList.AddRange(edit.QuestGroupList);
            e.QuestPoolList.AddRange(edit.QuestPoolList);
            return e;
        }
    }
}
