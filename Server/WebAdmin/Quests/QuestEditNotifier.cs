using System.Globalization;

namespace Server.WebAdmin.Quests
{
    /// <summary>
    /// The one place that reacts to a saved quest definition (UserData-and-Quests-Plan.md, Part B, "Live edits"). Called by the web admin
    /// Quests editor and its JSON import after SystemData.db was written (EditLock already released, on the HTTP worker thread).
    /// <para>Contract: <paramref name="questIndexes"/> lists every quest whose graph must be rebuilt in Main: the saved or created quest,
    /// a deleted quest (it is no longer in Envir.Edit: drop it), and every quest that references a changed shared row (a target group, or a
    /// text another quest also uses). <paramref name="sharedTablesChanged"/> is true when a shared top-level table changed (questgroups +
    /// members, texts used by more than one quest). Indexes are distinct and ascending.</para>
    /// <para>Until the LiveDb quest-replace hook (wave 3) it only flags a pending restart, like other RestartOnly edits.</para>
    /// </summary>
    public static class QuestEditNotifier
    {
        public static void QuestsChanged(IReadOnlyList<int> questIndexes, bool sharedTablesChanged) => QuestsChanged(questIndexes, sharedTablesChanged, null);

        /// <summary>As above, with the admin shown in the pending-restart list.</summary>
        public static void QuestsChanged(IReadOnlyList<int> questIndexes, bool sharedTablesChanged, string adminName)
        {
            var list = questIndexes ?? Array.Empty<int>();
            if (list.Count == 0 && !sharedTablesChanged) return;

            var shown = string.Join(", ", list.Take(10).Select(i => i.ToString(CultureInfo.InvariantCulture))) + (list.Count > 10 ? ", …" : string.Empty);
            var description = "Quest definitions saved (quests " + (list.Count == 0 ? "none" : shown) + (sharedTablesChanged ? "; shared quest tables changed" : string.Empty) +
                              "); loaded at the next world start";
            WebAdminPending.Add("quests", description, adminName ?? "web admin");
            WebAdminLog.Info("[Quests] " + description + ".");
        }
    }
}
