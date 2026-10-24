using Server.MirDatabase;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// Reloads the file-loaded parts of one RUNNING quest (Envir.Main) from its quest txt file: descriptions, carry items, kill /
    /// item / flag tasks, rewards. Shared by the Scripts editor's "reload quests" (<c>ScriptRoutes.PostReloadQuests</c>) and the
    /// LiveDb live step (a changed <c>QuestInfo.FileName</c>). Game thread only (inside an admin action).
    /// <para>
    /// Refusals (the quest is left exactly as it was): the file cannot be read, or the kill / item / flag tasks no longer line up
    /// one-to-one (kept from the legacy runtime, whose progress was sized from the task lists). Since quest V2 (lane F) no character
    /// holds legacy progress, so a reload re-points nothing; <see cref="Outcome.Undo"/> puts the previous lists back. The
    /// <c>characters</c> argument is unused and goes with this class in phase 4.
    /// </para>
    /// </summary>
    internal static class QuestReload
    {
        internal sealed class Outcome
        {
            public bool Reloaded;

            /// <summary>Why the quest was not reloaded (operator text, never exception text); null when it was.</summary>
            public string Refusal;

            /// <summary>Restores the quest as it was before the reload (only when <see cref="Reloaded"/>).</summary>
            public Action Undo;
        }

        /// <summary>The comparable key of a quest file name: trimmed, '/' separators, no ".txt".</summary>
        public static string Key(string fileName)
        {
            var key = (fileName ?? string.Empty).Trim().Replace('\\', '/');
            if (key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) key = key.Substring(0, key.Length - 4);
            return key;
        }

        /// <summary>
        /// Reloads <paramref name="quest"/>. <paramref name="load"/> replaces <c>quest.LoadInfo(true)</c> (tests). Never throws for a
        /// file problem: the exception is logged and reported as a refusal.
        /// </summary>
        public static Outcome Reload(QuestInfo quest, List<CharacterInfo> characters, Action<QuestInfo> load = null)
        {
            if (quest == null) return new Outcome { Refusal = "no such quest" };
            var before = Snapshot.Take(quest);

            // QuestInfo.ClearInfo leaves these lists alone, so LoadInfo(true) alone would duplicate their lines.
            quest.TaskDescription.Clear();
            quest.ReturnDescription.Clear();
            quest.CompletionDescription.Clear();
            quest.CarryItems.Clear();
            try
            {
                if (load != null) load(quest);
                else quest.LoadInfo(true);
            }
            catch (Exception ex)
            {
                before.Restore(quest);
                WebAdminLog.Error(ex, "[WebAdmin] reloading quest " + quest.Index + " (" + quest.FileName + ") failed");
                return new Outcome { Refusal = "the file could not be read (see the server log)" };
            }

            var mismatch = before.TaskShapeMismatch(quest);
            if (mismatch != null)
            {
                before.Restore(quest);
                return new Outcome { Refusal = mismatch };
            }

            // Quest V2 (lane F): no character holds legacy progress any more (V2 runs key on objective ids and are migrated by the
            // wave 3 quest-replace hook), so nothing is re-pointed here; the legacy body only feeds the importer until the ship.
            return new Outcome
            {
                Reloaded = true,
                Undo = () => before.Restore(quest),
            };
        }

        /// <summary>The file-loaded parts of a live QuestInfo, so a refused or undone reload can be rolled back exactly.</summary>
        private sealed class Snapshot
        {
            private List<string> _description, _taskDescription, _returnDescription, _completionDescription;
            private List<QuestItemTask> _carryItems, _itemTasks;
            private List<QuestKillTask> _killTasks;
            private List<QuestFlagTask> _flagTasks;
            private List<QuestItemReward> _fixedRewards, _selectRewards;
            private uint _expReward, _goldReward, _creditReward;

            public static Snapshot Take(QuestInfo q) => new Snapshot
            {
                _description = new List<string>(q.Description),
                _taskDescription = new List<string>(q.TaskDescription),
                _returnDescription = new List<string>(q.ReturnDescription),
                _completionDescription = new List<string>(q.CompletionDescription),
                _carryItems = new List<QuestItemTask>(q.CarryItems),
                _killTasks = q.KillTasks,
                _itemTasks = q.ItemTasks,
                _flagTasks = q.FlagTasks,
                _fixedRewards = q.FixedRewards,
                _selectRewards = q.SelectRewards,
                _expReward = q.ExpReward,
                _goldReward = q.GoldReward,
                _creditReward = q.CreditReward,
            };

            public void Restore(QuestInfo q)
            {
                q.Description.Clear(); q.Description.AddRange(_description);
                q.TaskDescription.Clear(); q.TaskDescription.AddRange(_taskDescription);
                q.ReturnDescription.Clear(); q.ReturnDescription.AddRange(_returnDescription);
                q.CompletionDescription.Clear(); q.CompletionDescription.AddRange(_completionDescription);
                q.CarryItems.Clear(); q.CarryItems.AddRange(_carryItems);
                q.KillTasks = _killTasks;
                q.ItemTasks = _itemTasks;
                q.FlagTasks = _flagTasks;
                q.FixedRewards = _fixedRewards;
                q.SelectRewards = _selectRewards;
                q.ExpReward = _expReward;
                q.GoldReward = _goldReward;
                q.CreditReward = _creditReward;
            }

            /// <summary>Null when the kill / item / flag tasks still line up one-to-one (same count, same monster / item / flag per slot).</summary>
            public string TaskShapeMismatch(QuestInfo q)
            {
                if (q.KillTasks.Count != _killTasks.Count) return "kill tasks " + _killTasks.Count + " -> " + q.KillTasks.Count;
                for (var i = 0; i < _killTasks.Count; i++)
                    if (q.KillTasks[i].Monster?.Index != _killTasks[i].Monster?.Index) return "kill task " + (i + 1) + " monster changed";
                if (q.ItemTasks.Count != _itemTasks.Count) return "item tasks " + _itemTasks.Count + " -> " + q.ItemTasks.Count;
                for (var i = 0; i < _itemTasks.Count; i++)
                    if (q.ItemTasks[i].Item?.Index != _itemTasks[i].Item?.Index) return "item task " + (i + 1) + " item changed";
                if (q.FlagTasks.Count != _flagTasks.Count) return "flag tasks " + _flagTasks.Count + " -> " + q.FlagTasks.Count;
                for (var i = 0; i < _flagTasks.Count; i++)
                    if (q.FlagTasks[i].Number != _flagTasks[i].Number) return "flag task " + (i + 1) + " number changed";
                return null;
            }
        }
    }
}
