using System.Globalization;
using Server.MirEnvir;
using Server.MirEnvir.Quests;

namespace Server.WebAdmin.Quests
{
    /// <summary>What the quest-replace hook did with a saved change (returned by the Quests routes as <c>live</c>).</summary>
    public sealed class QuestLiveOutcome
    {
        /// <summary>True when the running world now uses the saved definitions (every quest of the change; see <see cref="Failed"/>).</summary>
        public bool Applied;
        /// <summary>One line for the page ("applied live …", or why it waits for a restart).</summary>
        public string Message = string.Empty;
        public List<int> Replaced = new List<int>();
        public List<int> Deleted = new List<int>();
        /// <summary>Quests online players were refreshed for (the change plus quests using a changed shared row).</summary>
        public List<int> Refreshed = new List<int>();
        public int Players;
        /// <summary>Quests the running world kept as they were, with the reason: they load at the next world start.</summary>
        public List<QuestLiveFailure> Failed = new List<QuestLiveFailure>();
    }

    public sealed class QuestLiveFailure
    {
        public int Quest;
        public string Reason = string.Empty;
    }

    /// <summary>
    /// The one place that reacts to a saved quest definition (UserData-and-Quests-Plan.md, Part B, "Live edits"): the LiveDb quest-replace
    /// hook. Called by the web admin Quests editor, its JSON import and the legacy re-import after SystemData.db was written (EditLock
    /// released, never on the game thread).
    /// <para>Contract: <paramref name="questIndexes"/> lists every quest whose graph must be rebuilt in Main: the saved or created quest, a
    /// deleted quest (it is no longer in Envir.Edit: drop it), and every quest that references a changed shared row (a target group, or a
    /// text another quest also uses). <paramref name="sharedTablesChanged"/> is true when a shared top-level table changed. Indexes are
    /// distinct and ascending.</para>
    /// <para>Threads: the snapshot is taken here under EditLock (plain copies of Envir.Edit's rows, <see cref="QuestReplace.Take"/>); the swap
    /// and the online players' refresh run in one admin action on the game thread (<see cref="QuestReplace.Apply"/>). Only what could not be
    /// applied (world not running, a missing reference, a rollback, a timeout) is left to a restart through <see cref="WebAdminPending"/>.</para>
    /// </summary>
    public static class QuestEditNotifier
    {
        /// <summary>Game-thread budget of one replace (a whole re-import refreshes every quest for every online player).</summary>
        internal const int ApplyTimeoutMs = 30000;

        public static QuestLiveOutcome QuestsChanged(IReadOnlyList<int> questIndexes, bool sharedTablesChanged) =>
            QuestsChanged(questIndexes, sharedTablesChanged, null);

        /// <summary>Synchronous form (worker threads only: the import runner, tests).</summary>
        public static QuestLiveOutcome QuestsChanged(IReadOnlyList<int> questIndexes, bool sharedTablesChanged, string adminName, string source = null) =>
            QuestsChangedAsync(questIndexes, sharedTablesChanged, adminName, source).GetAwaiter().GetResult();

        public static async Task<QuestLiveOutcome> QuestsChangedAsync(IReadOnlyList<int> questIndexes, bool sharedTablesChanged, string adminName, string source = null)
        {
            var list = questIndexes ?? Array.Empty<int>();
            var outcome = new QuestLiveOutcome();
            if (list.Count == 0 && !sharedTablesChanged)
            {
                outcome.Applied = true;
                outcome.Message = "Nothing to apply.";
                return outcome;
            }

            var who = adminName ?? "web admin";
            var what = Describe(list, sharedTablesChanged);
            if (!WebAdminGame.IsRunning)
            {
                outcome.Message = "The world is not running: loaded at the next world start.";
                Pending(what + "; loaded at the next world start", who);
                return outcome;
            }

            QuestReplaceSnapshot snapshot;
            lock (WebAdminGame.EditLock) snapshot = QuestReplace.Take(Envir.Edit, list, source ?? "Quests page");

            QuestReplaceResult result;
            try
            {
                result = await WebAdminGame.RunAsync(() => QuestReplace.Apply(Envir.Main, snapshot, Envir.Main.Players), ApplyTimeoutMs).ConfigureAwait(false);
            }
            catch (WorldNotRunningException)
            {
                outcome.Message = "The world stopped before the change could be applied: loaded at the next world start.";
                Pending(what + "; loaded at the next world start", who);
                return outcome;
            }
            catch (GameThreadTimeoutException ex)
            {
                outcome.Message = ex.Started
                    ? "The game thread did not answer in time; the change may have been applied (see the Quests log). A restart applies it for sure."
                    : "The game thread did not answer in time: loaded at the next world start.";
                Pending(what + "; the live apply timed out, loaded at the next world start", who);
                return outcome;
            }

            outcome.Replaced = result.Replaced;
            outcome.Deleted = result.Deleted;
            outcome.Refreshed = result.Affected;
            outcome.Players = result.Players;
            foreach (var f in result.Failed) outcome.Failed.Add(new QuestLiveFailure { Quest = f.Quest, Reason = f.Reason });
            outcome.Applied = result.Failed.Count == 0;

            if (outcome.Applied)
                outcome.Message = "Applied live to the running world (" + result.Summary() + ").";
            else
            {
                var kept = string.Join("; ", result.Failed.Take(5).Select(f => "quest " + f.Quest.ToString(CultureInfo.InvariantCulture) + ": " + f.Reason)) +
                           (result.Failed.Count > 5 ? "; …" : string.Empty);
                outcome.Message = (result.RolledBack ? "Not applied live (rolled back): " : "Partly applied live; kept as before: ") + kept + ". Loaded at the next world start.";
                Pending("Quest definitions saved but not applied live (" + kept + "); loaded at the next world start", who);
            }

            WebAdminLog.Info("[Quests] " + what + ": " + outcome.Message);
            return outcome;
        }

        private static string Describe(IReadOnlyList<int> list, bool shared)
        {
            var shown = string.Join(", ", list.Take(10).Select(i => i.ToString(CultureInfo.InvariantCulture))) + (list.Count > 10 ? ", …" : string.Empty);
            return "Quest definitions saved (quests " + (list.Count == 0 ? "none" : shown) + (shared ? "; shared quest tables changed" : string.Empty) + ")";
        }

        private static void Pending(string description, string adminName)
        {
            WebAdminPending.Add("quests", description, adminName);
            WebAdminLog.Info("[Quests] " + description + ".");
        }
    }
}
