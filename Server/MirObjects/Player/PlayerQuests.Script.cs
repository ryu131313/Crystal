using Server.MirDatabase;
using Server.MirEnvir.Quests;

namespace Server.MirObjects
{
    /// <summary>
    /// NPC script integration of quest V2 (Part B "NPC script integration"): the checks CHECKQUESTSTEP, CHECKQUESTVAR,
    /// CHECKQUESTOUTCOME, CHECKCONDITION and the actions QUESTCOUNTER, SETQUESTVAR, QUESTSTEP, OFFERQUEST. Arguments are the raw
    /// script words; a malformed argument fails the check / skips the action (scripts are validated by ScriptValidation).
    /// </summary>
    public sealed partial class PlayerQuests
    {
        /// <summary>CHECKQUESTSTEP quest step: an active (not failed) run on that step id (-1 = turn-in pending).</summary>
        public bool ScriptCheckStep(string quest, string step)
        {
            if (!int.TryParse(quest, out var q) || !int.TryParse(step, out var s)) return false;
            return _runs.TryGetValue(q, out var run) && run.Status == QuestRunStatus.Active && run.Step == s;
        }

        /// <summary>CHECKQUESTOUTCOME quest outcome: the outcome chosen in the current run, else the last completed run's.</summary>
        public bool ScriptCheckOutcome(string quest, string outcome)
        {
            if (!int.TryParse(quest, out var q) || !int.TryParse(outcome, out var o)) return false;
            if (_runs.TryGetValue(q, out var run) && run.Branch != 0) return run.Branch == o;
            return _history.TryGetValue(q, out var h) && h.LastOutcome == o;
        }

        /// <summary>CHECKCONDITION id: a condition tree of the conditions table.</summary>
        public bool ScriptCheckCondition(string condition) => int.TryParse(condition, out var c) && c > 0 && EvaluateCondition(c);

        /// <summary>Resolves a quest variable by id or by its AuthorKey within the quest; 0 = unknown.</summary>
        public int ResolveVar(int quest, string var)
        {
            var q = Catalog.Get(quest);
            if (q == null || string.IsNullOrEmpty(var)) return 0;
            var byId = int.TryParse(var, out var id);
            for (var i = 0; i < q.Def.Vars.Count; i++)
            {
                var v = q.Def.Vars[i];
                if ((byId && v.Index == id) || string.Equals(v.AuthorKey, var, StringComparison.OrdinalIgnoreCase)) return v.Index;
            }

            return 0;
        }

        /// <summary>The current value of a quest variable (its default when never set); null when the quest or variable is unknown.</summary>
        public int? ScriptVarValue(string quest, string var)
        {
            if (!int.TryParse(quest, out var q)) return null;
            var key = ResolveVar(q, var);
            return key == 0 ? null : VarValue(q, key);
        }

        /// <summary>SETQUESTVAR quest var value: sets a quest-scoped variable (only while the quest has a run).</summary>
        public bool ScriptSetVar(string quest, string var, string value)
        {
            if (!int.TryParse(quest, out var q) || !int.TryParse(value, out var v) || !_runs.ContainsKey(q)) return false;
            var key = ResolveVar(q, var);
            if (key == 0) return false;
            SetVar(q, key, v);
            return true;
        }

        private void SetVar(int quest, int key, int value)
        {
            var vars = Info.QuestVars;
            for (var i = 0; i < vars.Count; i++)
            {
                if (vars[i].Quest != quest || vars[i].Key != key) continue;
                vars[i].Value = value;
                MarkAvailabilityDirty(); // QuestVar condition leaves
                return;
            }

            vars.Add(new QuestVarState { Quest = quest, Key = key, Value = value });
            MarkAvailabilityDirty();
        }

        /// <summary>QUESTCOUNTER quest objective n: adds n to a Counter objective of the run's current step.</summary>
        public bool ScriptCounter(string quest, string objective, string amount)
        {
            if (!int.TryParse(quest, out var q) || !int.TryParse(objective, out var o)) return false;
            if (!int.TryParse(amount, out var n)) n = 1;
            if (n <= 0 || !_runs.ContainsKey(q)) return false;
            Raise(new QuestEvent(QuestObjectiveType.Counter, QuestTargetKind.None, o, n));
            return true;
        }

        /// <summary>QUESTSTEP quest step: forces the run onto a step (-1 = turn-in pending), as the GM command does.</summary>
        public bool ScriptForceStep(string quest, string step)
        {
            if (!int.TryParse(quest, out var q) || !int.TryParse(step, out var s)) return false;
            return AdminSetStep(q, s);
        }

        /// <summary>OFFERQUEST quest: opens the accept prompt (S.QuestOffer) when the player can take the quest.</summary>
        public bool ScriptOffer(string quest)
        {
            if (!int.TryParse(quest, out var q)) return false;
            var runtime = Catalog.Get(q);
            return runtime != null && ReceiveOffer(runtime, 0, string.Empty, QuestEndpointKind.Npc);
        }
    }
}
