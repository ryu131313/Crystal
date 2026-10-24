using Server.MirDatabase;
using Server.MirEnvir.Quests;

namespace Server.MirObjects
{
    public sealed partial class PlayerQuests
    {
        /// <summary>
        /// Quest-bag housekeeping: true when an active or failed run still needs items of <paramref name="itemIndex"/> (a Collect
        /// objective of any of its steps, or one of its carry items).
        /// </summary>
        public bool KeepsQuestItem(int itemIndex)
        {
            foreach (var run in _runs.Values)
            {
                var q = Catalog.Get(run.Quest);
                if (q == null) continue;
                foreach (var o in q.ObjectivesById.Values)
                    if (o.Type == QuestObjectiveType.Collect && TargetsItem(o, itemIndex)) return true;
                var rewards = q.Def.Rewards;
                for (var i = 0; i < rewards.Count; i++)
                    if (rewards[i].Scope == QuestRewardScope.Accept && rewards[i].Kind == QuestRewardKind.Item && rewards[i].TargetId == itemIndex) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ GM / web admin (game thread)

        /// <summary>GM / admin: starts a quest at its first step with no accept checks. False when unknown or already running.</summary>
        public bool AdminStart(int quest)
        {
            var q = Catalog.Get(quest);
            if (q == null || _runs.ContainsKey(quest)) return false;
            var now = Envir.Now;
            var run = new QuestRunState
            {
                Quest = quest,
                Status = QuestRunStatus.Active,
                Step = q.FirstStep.Index,
                Revision = q.Def.Revision,
                AcceptedAt = now.ToBinary(),
                ExpiresAt = q.Def.TimeLimitSeconds > 0 ? now.AddSeconds(q.Def.TimeLimitSeconds).ToBinary() : 0,
            };
            _runs[quest] = run;
            Info.QuestRuns.Add(run);
            EnsureDefinition(q);
            _sentStatus[quest] = QuestRunStatus.None;
            EnterStep(q, run, run.Step);
            MarkState(quest, trackHint: true);
            Reevaluate(run);
            QuestLog.Info("[Quests] admin start " + _player.Name + " quest " + quest);
            return true;
        }

        /// <summary>GM / admin: removes the run and the history row of a quest. False when there was neither.</summary>
        public bool AdminReset(int quest)
        {
            var changed = false;
            if (_runs.ContainsKey(quest)) { RemoveRun(quest); changed = true; }
            if (_history.TryGetValue(quest, out var h))
            {
                _history.Remove(quest);
                Info.QuestHistory.Remove(h);
                changed = true;
                SendHistoryFull();
            }

            if (!changed) return false;
            _player.RecalculateQuestBag();
            MarkAvailabilityDirty();
            QuestLog.Info("[Quests] admin reset " + _player.Name + " quest " + quest);
            return true;
        }

        /// <summary>GM / admin: records a completion (history Times + 1) and removes the run; no rewards, no items taken.</summary>
        public bool AdminComplete(int quest)
        {
            var q = Catalog.Get(quest);
            if (q == null && !_runs.ContainsKey(quest)) return false;
            var now = Envir.Now;
            if (!_history.TryGetValue(quest, out var h))
            {
                h = new QuestHistoryEntry { Quest = quest, FirstAt = now.ToBinary() };
                _history[quest] = h;
                Info.QuestHistory.Add(h);
            }

            h.Times++;
            h.LastAt = now.ToBinary();
            if (!_dirtyHistory.Contains(quest)) _dirtyHistory.Add(quest);
            RemoveRun(quest);
            _player.RecalculateQuestBag();
            MarkAvailabilityDirty();
            QuestLog.Info("[Quests] admin complete " + _player.Name + " quest " + quest);
            return true;
        }

        /// <summary>GM / admin: removes every run and the whole history.</summary>
        public void AdminClearAll()
        {
            foreach (var run in RunsSnapshot()) RemoveRun(run.Quest);
            _history.Clear();
            Info.QuestHistory.Clear();
            Info.QuestVars.Clear();
            SendHistoryFull();
            _player.RecalculateQuestBag();
            MarkAvailabilityDirty();
            QuestLog.Info("[Quests] admin clear " + _player.Name);
        }

        /// <summary>GM / admin: moves a run to <paramref name="step"/> (-1 = turn-in pending). False when the run or step is unknown.</summary>
        public bool AdminSetStep(int quest, int step)
        {
            if (!_runs.TryGetValue(quest, out var run)) return false;
            var q = Catalog.Get(quest);
            if (q == null || (step != -1 && !q.StepsById.ContainsKey(step))) return false;
            if (run.Status == QuestRunStatus.Failed) run.Status = QuestRunStatus.Active;
            EnterStep(q, run, step);
            Reevaluate(run);
            QuestLog.Info("[Quests] admin step " + _player.Name + " quest " + quest + " step " + step);
            return true;
        }

        /// <summary>GM / admin: sets a stored objective value (not for Collect / Deliver / Flag, which are read live).</summary>
        public bool AdminSetObjective(int quest, int objective, int value)
        {
            if (!_runs.TryGetValue(quest, out var run)) return false;
            var q = Catalog.Get(quest);
            if (q == null || !q.ObjectivesById.TryGetValue(objective, out var o)) return false;
            if (o.Type == QuestObjectiveType.Collect || o.Type == QuestObjectiveType.Deliver || o.Type == QuestObjectiveType.Flag) return false;
            SetValue(quest, o, Math.Clamp(value, 0, Math.Max(1, o.Count)));
            MarkProgress(quest, objective);
            Reevaluate(run);
            return true;
        }

        private void SendHistoryFull()
        {
            var packet = new ServerPackets.QuestHistory { Full = true };
            foreach (var h in _history.Values) packet.History.Add(HistoryEntry(h));
            _dirtyHistory.Clear();
            _player.Enqueue(packet);
        }
    }
}
