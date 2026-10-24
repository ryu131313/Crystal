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

        /// <summary>
        /// Web admin edit of an offline character's quest records (no PlayerQuests; game thread, or inline while the world is stopped).
        /// <paramref name="def"/> is a copy of Envir.Edit's definition (null = unknown quest: only "complete" works). The run is first
        /// migrated to the definition's revision (<see cref="MigrateRecords"/>). Ready is derived at the next login. False = nothing changed.
        /// </summary>
        public static bool EditOfflineRecords(CharacterInfo ci, QuestDefinition def, int quest, string action, int step, int objective, int value, DateTime now)
        {
            QuestRunState run = null;
            for (var i = 0; i < ci.QuestRuns.Count; i++)
                if (ci.QuestRuns[i]?.Quest == quest) { run = ci.QuestRuns[i]; break; }
            if (run != null && def != null) MigrateRecords(def, run, ci.QuestObjectives, now);

            switch (action)
            {
                case "start":
                {
                    if (run != null || def == null) return false;
                    QuestStepInfo first = null;
                    for (var i = 0; i < def.Steps.Count; i++)
                    {
                        var s = def.Steps[i];
                        if (first == null || s.Order < first.Order || (s.Order == first.Order && s.Index < first.Index)) first = s;
                    }

                    if (first == null) return false;
                    ci.QuestRuns.Add(new QuestRunState
                    {
                        Quest = quest, Status = QuestRunStatus.Active, Step = first.Index, Revision = def.Revision, AcceptedAt = now.ToBinary(),
                        ExpiresAt = def.TimeLimitSeconds > 0 ? now.AddSeconds(def.TimeLimitSeconds).ToBinary() : 0,
                        StepExpiresAt = first.TimeLimitSeconds > 0 ? now.AddSeconds(first.TimeLimitSeconds).ToBinary() : 0,
                    });
                    return true;
                }
                case "complete":
                {
                    QuestHistoryEntry h = null;
                    for (var i = 0; i < ci.QuestHistory.Count; i++)
                        if (ci.QuestHistory[i]?.Quest == quest) { h = ci.QuestHistory[i]; break; }
                    if (h == null) ci.QuestHistory.Add(h = new QuestHistoryEntry { Quest = quest, FirstAt = now.ToBinary() });
                    h.Times++;
                    h.LastAt = now.ToBinary();
                    ci.QuestRuns.RemoveAll(r => r == null || r.Quest == quest);
                    ci.QuestObjectives.RemoveAll(o => o == null || o.Quest == quest);
                    ci.QuestVars.RemoveAll(v => v == null || v.Quest == quest);
                    return true;
                }
                case "step":
                {
                    if (run == null || def == null) return false;
                    QuestStepInfo target = null;
                    for (var i = 0; i < def.Steps.Count; i++)
                        if (def.Steps[i].Index == step) target = def.Steps[i];
                    if (step != -1 && target == null) return false;
                    run.Status = QuestRunStatus.Active;
                    run.Step = step;
                    run.StepExpiresAt = target != null && target.TimeLimitSeconds > 0 ? now.AddSeconds(target.TimeLimitSeconds).ToBinary() : 0;
                    return true;
                }
                case "objective":
                {
                    if (run == null || def == null) return false;
                    QuestObjectiveInfo o = null;
                    for (var i = 0; i < def.Objectives.Count; i++)
                        if (def.Objectives[i].Index == objective) o = def.Objectives[i];
                    if (o == null || o.Type == QuestObjectiveType.Collect || o.Type == QuestObjectiveType.Deliver || o.Type == QuestObjectiveType.Flag) return false;
                    var clamped = Math.Clamp(value, 0, Math.Max(1, o.Count));
                    for (var i = 0; i < ci.QuestObjectives.Count; i++)
                    {
                        var row = ci.QuestObjectives[i];
                        if (row == null || row.Quest != quest || row.Objective != objective) continue;
                        row.Value = clamped;
                        return true;
                    }

                    ci.QuestObjectives.Add(new QuestObjectiveState { Quest = quest, Objective = objective, Value = clamped });
                    return true;
                }
                default:
                    return false;
            }
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
