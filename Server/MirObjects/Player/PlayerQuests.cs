using log4net;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using S = ServerPackets;

namespace Server.MirObjects
{
    /// <summary>
    /// Quest V2 runtime of one player (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Runtime"): the run / objective /
    /// history records of <see cref="CharacterInfo"/>, the per-player objective index, event handling, step completion, and the
    /// coalesced client updates. Owned by <see cref="PlayerObject"/>, game thread only (no locks): events raised inside a mob pass
    /// reach it through <see cref="Envir.RunOutsideMobPass"/>. Raise allocates nothing; packets are built from copies when flushed.
    /// </summary>
    public sealed partial class PlayerQuests
    {
        private static readonly ILog QuestLog = Logger.GetLogger(LogType.Quests);
        private static Envir Envir => Envir.Main;

        /// <summary>One indexed objective of an active run (allocated when the index is rebuilt, never per event).</summary>
        private sealed class ObjectiveRef
        {
            public QuestRunState Run;
            public QuestRuntime Quest;
            public QuestObjectiveInfo Objective;
        }

        private readonly PlayerObject _player;
        private QuestCatalog _catalog;

        private readonly Dictionary<int, QuestRunState> _runs = new Dictionary<int, QuestRunState>();
        /// <summary>Stored objective values by objective id (objective ids are global and never reused).</summary>
        private readonly Dictionary<int, QuestObjectiveState> _values = new Dictionary<int, QuestObjectiveState>();
        private readonly Dictionary<int, QuestHistoryEntry> _history = new Dictionary<int, QuestHistoryEntry>();

        private readonly Dictionary<long, List<ObjectiveRef>> _index = new Dictionary<long, List<ObjectiveRef>>();
        private readonly Dictionary<int, List<long>> _indexKeysByQuest = new Dictionary<int, List<long>>();
        /// <summary>Indexed objectives with no target (ReachLevel, UseSkill without a spell).</summary>
        private readonly List<ObjectiveRef> _targetless = new List<ObjectiveRef>();

        // Coalesced client updates (flushed from PlayerObject.Process).
        private readonly HashSet<long> _dirtyProgress = new HashSet<long>();
        private readonly List<long> _dirtyProgressOrder = new List<long>();
        private readonly HashSet<int> _dirtyStates = new HashSet<int>();
        private readonly List<int> _dirtyStateOrder = new List<int>();
        private readonly HashSet<int> _trackHints = new HashSet<int>();
        private readonly List<int> _dirtyHistory = new List<int>();
        /// <summary>Status last sent per quest (Ready is derived, so a change is detected by comparing).</summary>
        private readonly Dictionary<int, QuestRunStatus> _sentStatus = new Dictionary<int, QuestRunStatus>();
        private readonly List<QuestRunState> _touched = new List<QuestRunState>();
        private bool _availabilityDirty;
        private bool _loggedIn;

        /// <summary>Last Deliver count per objective id (recounted while a Deliver objective is indexed).</summary>
        private readonly Dictionary<int, int> _deliverCounts = new Dictionary<int, int>();
        /// <summary>Indexed Deliver objectives (recounted on Process; inventory slots change in too many places for one hook).</summary>
        private readonly List<ObjectiveRef> _deliverRefs = new List<ObjectiveRef>();
        /// <summary>Indexed ReachArea objectives (checked on Process when the player's map or cell changed).</summary>
        private readonly List<ObjectiveRef> _areaRefs = new List<ObjectiveRef>();
        private Map _areaMap;
        private System.Drawing.Point _areaLocation;
        /// <summary>Active runs whose current step has a FailCondition (checked at most every <see cref="FailCheckMs"/>).</summary>
        private readonly List<QuestRunState> _failRuns = new List<QuestRunState>();
        private long _nextFailCheck;
        private const long FailCheckMs = 1000;

        /// <summary>Quests the player may accept without an NPC (a share or a non-NPC offer), until accepted or logged out.</summary>
        private readonly HashSet<int> _offers = new HashSet<int>();
        private readonly Dictionary<int, int> _offerSharedFrom = new Dictionary<int, int>();

        public PlayerQuests(PlayerObject player)
        {
            _player = player;
        }

        private CharacterInfo Info => _player.Info;

        /// <summary>The catalog this player's index was built against (Main's current one at login).</summary>
        private QuestCatalog Catalog => _catalog ??= Envir.QuestCatalog;

        /// <summary>True when Process has work (client updates, availability, timers, Deliver recounts).</summary>
        public bool HasPendingWork => _dirtyProgressOrder.Count > 0 || _dirtyStateOrder.Count > 0 || _dirtyHistory.Count > 0 || _availabilityDirty ||
                                      _deliverRefs.Count > 0 || _areaRefs.Count > 0 || _failRuns.Count > 0 || _nextDue != DateTime.MaxValue;

        // ------------------------------------------------------------------ records

        public QuestRunState GetRun(int quest) => _runs.TryGetValue(quest, out var run) ? run : null;
        public QuestHistoryEntry GetHistory(int quest) => _history.TryGetValue(quest, out var h) ? h : null;
        public int ActiveCount
        {
            get
            {
                var n = 0;
                foreach (var run in _runs.Values)
                    if (run.Status == QuestRunStatus.Active) n++;
                return n;
            }
        }

        /// <summary>A run's status as the client sees it: Failed, Ready (derived) or Active.</summary>
        public QuestRunStatus StatusOf(QuestRunState run)
        {
            if (run.Status == QuestRunStatus.Failed) return QuestRunStatus.Failed;
            var q = Catalog.Get(run.Quest);
            if (q == null) return QuestRunStatus.Active;
            return IsReady(q, run) ? QuestRunStatus.Ready : QuestRunStatus.Active;
        }

        /// <summary>The QuestStatus condition leaf: run status, else Completed when in history, else None.</summary>
        public QuestRunStatus QuestStatus(int quest)
        {
            var run = GetRun(quest);
            if (run != null) return StatusOf(run);
            return _history.ContainsKey(quest) ? QuestRunStatus.Completed : QuestRunStatus.None;
        }

        public int TimesCompleted(int quest) => _history.TryGetValue(quest, out var h) ? h.Times : 0;

        public int VarValue(int quest, int key)
        {
            var vars = Info.QuestVars;
            for (var i = 0; i < vars.Count; i++)
                if (vars[i].Quest == quest && vars[i].Key == key) return vars[i].Value;
            var q = Catalog.Get(quest);
            if (q != null)
                for (var i = 0; i < q.Def.Vars.Count; i++)
                    if (q.Def.Vars[i].Index == key) return q.Def.Vars[i].Default;
            return 0;
        }

        // ------------------------------------------------------------------ login

        /// <summary>
        /// Login (game thread, after Spawned): builds the lookups from the saved records, drops runs of deleted quests, migrates runs
        /// whose definition revision changed, applies expired time limits, builds the index and sends definitions, states, progress
        /// and history.
        /// </summary>
        public void Login()
        {
            _catalog = Envir.QuestCatalog;
            _runs.Clear();
            _values.Clear();
            _history.Clear();

            var runs = Info.QuestRuns;
            for (var i = runs.Count - 1; i >= 0; i--)
            {
                var run = runs[i];
                if (run == null || _runs.ContainsKey(run.Quest)) { runs.RemoveAt(i); continue; }
                if (_catalog.Get(run.Quest) == null)
                {
                    QuestLog.Info("[Quests] " + _player.Name + " drop run of missing quest " + run.Quest);
                    runs.RemoveAt(i);
                    RemoveQuestRecords(run.Quest, false);
                    continue;
                }

                _runs[run.Quest] = run;
            }

            var objectives = Info.QuestObjectives;
            for (var i = objectives.Count - 1; i >= 0; i--)
            {
                var o = objectives[i];
                if (o == null || !_runs.ContainsKey(o.Quest) || _values.ContainsKey(o.Objective)) { objectives.RemoveAt(i); continue; }
                _values[o.Objective] = o;
            }

            var history = Info.QuestHistory;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var h = history[i];
                if (h == null || _history.ContainsKey(h.Quest)) { history.RemoveAt(i); continue; }
                _history[h.Quest] = h;
            }

            foreach (var run in RunsSnapshot())
            {
                var q = _catalog.Get(run.Quest);
                if (run.Revision != q.Def.Revision) Migrate(q, run, false);
            }

            foreach (var run in RunsSnapshot())
                RebuildIndex(run);

            _loggedIn = true;
            CheckTimers(Envir.Now);

            foreach (var run in RunsSnapshot())
                Reevaluate(run);

            SendAll();
        }

        /// <summary>A copy of the runs (login and admin paths only, never per event).</summary>
        private List<QuestRunState> RunsSnapshot() => new List<QuestRunState>(_runs.Values);

        /// <summary>Sends the full quest picture (login, or after a class/gender change cleared the sent set).</summary>
        private void SendAll()
        {
            foreach (var run in _runs.Values)
                EnsureDefinition(Catalog.Get(run.Quest));

            var states = new S.QuestStates { Full = true };
            _sentStatus.Clear();
            foreach (var run in _runs.Values)
            {
                var status = StatusOf(run);
                _sentStatus[run.Quest] = status;
                states.States.Add(StateEntry(run, status, false));
            }

            _player.Enqueue(states);
            _dirtyStates.Clear();
            _dirtyStateOrder.Clear();
            _trackHints.Clear();

            var progress = new S.QuestProgress();
            foreach (var run in _runs.Values)
            {
                var q = Catalog.Get(run.Quest);
                var list = q.ObjectivesOf(run.Step);
                for (var i = 0; i < list.Count; i++)
                    progress.Progress.Add(new S.QuestProgress.Entry { Quest = run.Quest, Objective = list[i].Index, Value = ValueOf(q, run, list[i]) });
            }

            if (progress.Progress.Count > 0) _player.Enqueue(progress);
            _dirtyProgress.Clear();
            _dirtyProgressOrder.Clear();

            var hist = new S.QuestHistory { Full = true };
            foreach (var h in _history.Values)
                hist.History.Add(HistoryEntry(h));
            _player.Enqueue(hist);
            _dirtyHistory.Clear();

            MarkAvailabilityDirty();
        }

        // ------------------------------------------------------------------ values

        /// <summary>
        /// An objective's current value: Collect = quest-bag count, Deliver = main-inventory count, Flag = 1 while the flag is set (read
        /// live, Part B "Rules"); every other type = the stored value. Clamped to 0..Count.
        /// </summary>
        public int ValueOf(QuestRuntime q, QuestRunState run, QuestObjectiveInfo o)
        {
            int value;
            switch (o.Type)
            {
                case QuestObjectiveType.Collect:
                    value = CountItems(Info.QuestInventory, o);
                    break;
                case QuestObjectiveType.Deliver:
                    value = CountItems(Info.Inventory, o);
                    break;
                case QuestObjectiveType.Flag:
                    value = o.TargetId >= 0 && o.TargetId < Info.Flags.Length && Info.Flags[o.TargetId] ? 1 : 0;
                    break;
                default:
                    value = _values.TryGetValue(o.Index, out var state) ? state.Value : 0;
                    break;
            }

            var count = Math.Max(1, o.Count);
            return value < 0 ? 0 : value > count ? count : value;
        }

        /// <summary>Count of the objective's target item (Item or ItemGroup) in <paramref name="grid"/>.</summary>
        private int CountItems(UserItem[] grid, QuestObjectiveInfo o)
        {
            var total = 0;
            for (var i = 0; i < grid.Length; i++)
            {
                var item = grid[i];
                if (item?.Info == null || !TargetsItem(o, item.Info.Index)) continue;
                total += item.Count;
            }

            return total;
        }

        private bool TargetsItem(QuestObjectiveInfo o, int itemIndex)
        {
            if (o.TargetKind == QuestTargetKind.Item) return o.TargetId == itemIndex;
            if (o.TargetKind != QuestTargetKind.ItemGroup || !Catalog.Groups.TryGetValue(o.TargetId, out var g)) return false;
            for (var i = 0; i < g.Members.Count; i++)
                if (g.Members[i].TargetId == itemIndex) return true;
            return false;
        }

        private bool ObjectiveMet(QuestRuntime q, QuestRunState run, QuestObjectiveInfo o) => ValueOf(q, run, o) >= Math.Max(1, o.Count);

        /// <summary>The step's required objectives are met (optional objectives never block; a step with none required is met).</summary>
        private bool StepMet(QuestRuntime q, QuestRunState run, QuestStepInfo step)
        {
            var list = q.ObjectivesOf(step.Index);
            int required = 0, met = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Optional) continue;
                required++;
                if (ObjectiveMet(q, run, list[i])) met++;
            }

            if (required == 0) return true;
            switch (step.CompletionMode)
            {
                case QuestStepCompletion.Any:
                    return met > 0;
                case QuestStepCompletion.NOf:
                    return met >= Math.Clamp(step.NRequired, 1, required);
                default:
                    return met == required;
            }
        }

        /// <summary>Ready (derived): turn-in pending, or the last step's required objectives met (and no branch choice pending).</summary>
        private bool IsReady(QuestRuntime q, QuestRunState run)
        {
            if (run.Status != QuestRunStatus.Active) return false;
            if (run.Step < 0) return true;
            if (!q.StepsById.TryGetValue(run.Step, out var step)) return false;
            if (q.BranchesOf(step.Index).Count > 0) return false;
            return q.NextStepOf(step) == null && StepMet(q, run, step);
        }

        // ------------------------------------------------------------------ events

        /// <summary>
        /// Raises one event (game thread). <paramref name="direct"/> = this player did it (false for party fan-out, so PartyCredit.Self
        /// objectives skip it). O(1) lookup of one index key; no allocation except the first stored row of an objective.
        /// </summary>
        public void Raise(in QuestEvent e, bool direct = true)
        {
            if (!_loggedIn) return;
            if (_index.TryGetValue(QuestEvent.Key(e.Type, e.Kind, e.TargetId), out var list))
                RaiseOn(list, in e, direct);
            if (_targetless.Count > 0 && (e.Type == QuestObjectiveType.ReachLevel || e.Type == QuestObjectiveType.UseSkill))
                RaiseOn(_targetless, in e, direct);
            FlushTouched();
        }

        private void RaiseOn(List<ObjectiveRef> list, in QuestEvent e, bool direct)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i];
                var o = r.Objective;
                if (o.Type != e.Type || r.Run.Status != QuestRunStatus.Active) continue;
                if (o.PartyCredit == QuestPartyCredit.Self && !direct) continue;
                if (o.Condition != 0 && !EvaluateCondition(o.Condition)) continue;

                switch (o.Type)
                {
                    case QuestObjectiveType.Collect:
                    case QuestObjectiveType.Deliver:
                    case QuestObjectiveType.Flag:
                        // Read live: only the client value and the step state can change.
                        MarkProgress(r.Run.Quest, o.Index);
                        break;
                    case QuestObjectiveType.ReachLevel:
                        if (!SetValue(r.Run.Quest, o, Math.Min(_player.Level, Math.Max(1, o.Count)))) continue;
                        break;
                    default:
                        if (!AddValue(r.Run.Quest, o, Math.Max(1, e.Amount))) continue;
                        break;
                }

                if (!_touched.Contains(r.Run)) _touched.Add(r.Run);
            }
        }

        private bool _flushingTouched;

        private void FlushTouched()
        {
            // Reevaluate may advance a step and rebuild the index: never while an index list is being walked. A raise from inside
            // (an AutoComplete turn-in taking quest items) only appends here; the outer loop picks it up.
            if (_flushingTouched || _touched.Count == 0) return;
            _flushingTouched = true;
            try
            {
                for (var i = 0; i < _touched.Count; i++)
                    if (_runs.TryGetValue(_touched[i].Quest, out var run) && run == _touched[i]) Reevaluate(run);
            }
            finally
            {
                _touched.Clear();
                _flushingTouched = false;
            }
        }

        /// <summary>Adds to a stored objective value, capped at Count. False when nothing changed.</summary>
        private bool AddValue(int quest, QuestObjectiveInfo o, int amount)
        {
            var count = Math.Max(1, o.Count);
            if (!_values.TryGetValue(o.Index, out var state))
            {
                state = new QuestObjectiveState { Quest = quest, Objective = o.Index };
                _values[o.Index] = state;
                Info.QuestObjectives.Add(state);
            }

            if (state.Value >= count) return false;
            state.Value = (int)Math.Min(count, (long)state.Value + amount);
            MarkProgress(quest, o.Index);
            return true;
        }

        private bool SetValue(int quest, QuestObjectiveInfo o, int value)
        {
            if (!_values.TryGetValue(o.Index, out var state))
            {
                if (value == 0) return false;
                state = new QuestObjectiveState { Quest = quest, Objective = o.Index };
                _values[o.Index] = state;
                Info.QuestObjectives.Add(state);
            }

            if (state.Value == value) return false;
            state.Value = value;
            MarkProgress(quest, o.Index);
            return true;
        }

        private void MarkProgress(int quest, int objective)
        {
            var key = ((long)quest << 32) | (uint)objective;
            if (_dirtyProgress.Add(key)) _dirtyProgressOrder.Add(key);
            Wake();
        }

        private void MarkState(int quest, bool trackHint = false)
        {
            if (_dirtyStates.Add(quest)) _dirtyStateOrder.Add(quest);
            if (trackHint) _trackHints.Add(quest);
            Wake();
        }

        public void MarkAvailabilityDirty()
        {
            _availabilityDirty = true;
            Wake();
        }

        /// <summary>Makes the owner's Process run soon (P9-08 idle cap), where the coalesced updates are flushed.</summary>
        private void Wake() => _player.OperateTime = 0;

        /// <summary>
        /// Re-derives one run after a change: advances completed non-final steps (Step rewards, OnCompletePage, next step), then
        /// compares the derived status with the last one sent, and turns AutoComplete quests in once they are Ready.
        /// </summary>
        private void Reevaluate(QuestRunState run)
        {
            var q = Catalog.Get(run.Quest);
            if (q == null || run.Status != QuestRunStatus.Active) { CheckStatusChange(run); return; }

            for (var guard = 0; guard <= q.StepsByOrder.Count && run.Step >= 0; guard++)
            {
                if (!q.StepsById.TryGetValue(run.Step, out var step)) break;
                if (q.BranchesOf(step.Index).Count > 0) break; // waits for C.QuestChoose
                var next = q.NextStepOf(step);
                if (next == null || !StepMet(q, run, step)) break;
                CompleteStep(q, run, step, next);
            }

            CheckStatusChange(run);

            if (q.Has(QuestFlags.AutoComplete) && IsReady(q, run))
                TurnIn(q.Index, 0, null, true);
        }

        private void CheckStatusChange(QuestRunState run)
        {
            var status = StatusOf(run);
            if (_sentStatus.TryGetValue(run.Quest, out var sent) && sent == status) return;
            _sentStatus[run.Quest] = status;
            MarkState(run.Quest);
            MarkAvailabilityDirty();
        }

        /// <summary>Moves a run from a completed step to <paramref name="next"/> (Step rewards first, then the new step's index).</summary>
        private void CompleteStep(QuestRuntime q, QuestRunState run, QuestStepInfo step, QuestStepInfo next)
        {
            GiveScopedRewards(q, QuestRewardScope.Step, step.Index);
            QuestLog.Info("[Quests] step " + _player.Name + " quest " + q.Index + " step " + step.Index + " -> " + next.Index + " after " + SecondsSinceAccept(run) + "s");
            EnterStep(q, run, next.Index);
            RunPage(step.OnCompletePage);
        }

        /// <summary>Puts a run on <paramref name="stepId"/> (-1 = turn-in pending): step timer, objective rows of the old step, index.</summary>
        private void EnterStep(QuestRuntime q, QuestRunState run, int stepId)
        {
            run.Step = stepId;
            run.StepExpiresAt = 0;
            if (stepId > 0 && q.StepsById.TryGetValue(stepId, out var step) && step.TimeLimitSeconds > 0)
                run.StepExpiresAt = Envir.Now.AddSeconds(step.TimeLimitSeconds).ToBinary();
            RebuildIndex(run);
            ScheduleTimers();
            MarkState(run.Quest);
            var list = q.ObjectivesOf(stepId);
            for (var i = 0; i < list.Count; i++) MarkProgress(run.Quest, list[i].Index);
            if (stepId > 0 && q.StepsById.TryGetValue(stepId, out var entered)) RunPage(entered.OnEnterPage);
        }

        /// <summary>Calls an NPC script page of the default NPC (the step escape hatch); empty = nothing.</summary>
        private void RunPage(string page)
        {
            if (string.IsNullOrWhiteSpace(page)) return;
            try
            {
                _player.CallDefaultNPCPage(page.Trim());
            }
            catch (Exception ex)
            {
                MessageQueue.Instance.Enqueue("[Quests] page " + page + " for " + _player.Name + " failed: " + ex.Message);
            }
        }

        private long SecondsSinceAccept(QuestRunState run)
        {
            if (run.AcceptedAt == 0) return 0;
            return (long)(Envir.Now - DateTime.FromBinary(run.AcceptedAt)).TotalSeconds;
        }

        // ------------------------------------------------------------------ index

        /// <summary>Rebuilds one run's index entries (accept, step change, migration, removal).</summary>
        private void RebuildIndex(QuestRunState run)
        {
            RemoveFromIndex(run.Quest);
            if (!_runs.TryGetValue(run.Quest, out var current) || current != run || run.Status != QuestRunStatus.Active || run.Step < 0) return;
            var q = Catalog.Get(run.Quest);
            if (q == null) return;

            var keys = new List<long>();
            var list = q.ObjectivesOf(run.Step);
            for (var i = 0; i < list.Count; i++)
            {
                var o = list[i];
                var r = new ObjectiveRef { Run = run, Quest = q, Objective = o };
                switch (o.Type)
                {
                    case QuestObjectiveType.Deliver:
                        _deliverCounts[o.Index] = CountItems(Info.Inventory, o);
                        _deliverRefs.Add(r);
                        break;
                    case QuestObjectiveType.Kill:
                        AddKeys(keys, r, o.Type, QuestTargetKind.Monster);
                        break;
                    case QuestObjectiveType.Collect:
                    case QuestObjectiveType.UseItem:
                    case QuestObjectiveType.Craft:
                    case QuestObjectiveType.Gather:
                        AddKeys(keys, r, o.Type, QuestTargetKind.Item);
                        break;
                    case QuestObjectiveType.ReachLevel:
                        _targetless.Add(r);
                        break;
                    case QuestObjectiveType.ReachArea:
                        _areaRefs.Add(r);
                        _areaMap = null; // check at the next Process
                        break;
                    case QuestObjectiveType.UseSkill:
                        if (o.TargetKind == QuestTargetKind.Spell) AddKey(keys, r, QuestEvent.Key(o.Type, QuestTargetKind.Spell, o.TargetId));
                        else _targetless.Add(r);
                        break;
                    case QuestObjectiveType.Counter:
                        // QUESTCOUNTER q objective n: keyed by the objective id itself.
                        AddKey(keys, r, QuestEvent.Key(o.Type, QuestTargetKind.None, o.Index));
                        break;
                    default:
                        AddKey(keys, r, QuestEvent.Key(o.Type, o.TargetKind, o.TargetId));
                        break;
                }
            }

            _indexKeysByQuest[run.Quest] = keys;
            if (q.StepsById.TryGetValue(run.Step, out var currentStep) && currentStep.FailCondition != 0) _failRuns.Add(run);
        }

        /// <summary>Single target → one key; a group → one key per member (as the event's single kind).</summary>
        private void AddKeys(List<long> keys, ObjectiveRef r, QuestObjectiveType type, QuestTargetKind singleKind)
        {
            var o = r.Objective;
            var groupKind = singleKind == QuestTargetKind.Monster ? QuestTargetKind.MonsterGroup : QuestTargetKind.ItemGroup;
            if (o.TargetKind == groupKind)
            {
                if (!Catalog.Groups.TryGetValue(o.TargetId, out var g)) return;
                for (var i = 0; i < g.Members.Count; i++) AddKey(keys, r, QuestEvent.Key(type, singleKind, g.Members[i].TargetId));
                return;
            }

            AddKey(keys, r, QuestEvent.Key(type, o.TargetKind, o.TargetId));
        }

        private void AddKey(List<long> keys, ObjectiveRef r, long key)
        {
            if (!_index.TryGetValue(key, out var list)) _index[key] = list = new List<ObjectiveRef>(2);
            list.Add(r);
            keys.Add(key);
        }

        private void RemoveFromIndex(int quest)
        {
            if (_indexKeysByQuest.TryGetValue(quest, out var keys))
            {
                for (var k = 0; k < keys.Count; k++)
                {
                    if (!_index.TryGetValue(keys[k], out var list)) continue;
                    for (var i = list.Count - 1; i >= 0; i--)
                        if (list[i].Run.Quest == quest) list.RemoveAt(i);
                    if (list.Count == 0) _index.Remove(keys[k]);
                }

                _indexKeysByQuest.Remove(quest);
            }

            for (var i = _targetless.Count - 1; i >= 0; i--)
                if (_targetless[i].Run.Quest == quest) _targetless.RemoveAt(i);

            for (var i = _deliverRefs.Count - 1; i >= 0; i--)
                if (_deliverRefs[i].Run.Quest == quest) _deliverRefs.RemoveAt(i);

            for (var i = _areaRefs.Count - 1; i >= 0; i--)
                if (_areaRefs[i].Run.Quest == quest) _areaRefs.RemoveAt(i);

            for (var i = _failRuns.Count - 1; i >= 0; i--)
                if (_failRuns[i].Quest == quest) _failRuns.RemoveAt(i);
        }
        /// <summary>True when an active run's current step has a Collect objective on this item that the quest bag does not yet satisfy.</summary>
        public bool NeedsQuestItem(ItemInfo info)
        {
            if (info == null || !_index.TryGetValue(QuestEvent.Key(QuestObjectiveType.Collect, QuestTargetKind.Item, info.Index), out var list)) return false;
            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r.Run.Status != QuestRunStatus.Active) continue;
                if (r.Objective.Condition != 0 && !EvaluateCondition(r.Objective.Condition)) continue;
                if (!ObjectiveMet(r.Quest, r.Run, r.Objective)) return true;
            }

            return false;
        }

        /// <summary>The quest bag changed for <paramref name="info"/> (gain or take): Collect objectives re-read it.</summary>
        public void QuestBagChanged(ItemInfo info)
        {
            if (info == null) return;
            Raise(new QuestEvent(QuestObjectiveType.Collect, QuestTargetKind.Item, info.Index));
        }

        /// <summary>A global flag changed (script SET, on or off): Flag objectives re-read it (Part B "Rules: Flag objectives").</summary>
        public void FlagChanged(int flag)
        {
            Raise(new QuestEvent(QuestObjectiveType.Flag, QuestTargetKind.Flag, flag));
            MarkAvailabilityDirty(); // Flag condition leaves
        }

        /// <summary>A kill credited to this player (<paramref name="direct"/> = the killer; false = a party member in range).</summary>
        public void MonsterKilled(MonsterInfo info, bool direct)
        {
            if (info == null) return;
            Raise(new QuestEvent(QuestObjectiveType.Kill, QuestTargetKind.Monster, info.Index), direct);
        }

        /// <summary>Level changed: ReachLevel objectives and level condition leaves.</summary>
        public void LevelChanged()
        {
            if (_targetless.Count > 0) Raise(new QuestEvent(QuestObjectiveType.ReachLevel, QuestTargetKind.None, 0));
            MarkAvailabilityDirty();
        }

        // ------------------------------------------------------------------ per tick

        private DateTime _nextDue = DateTime.MaxValue;

        /// <summary>
        /// PlayerObject.Process (game thread): Deliver recounts, due timers, availability, then one coalesced S.QuestStates /
        /// S.QuestProgress / S.QuestHistory each. Cheap when idle (a few count checks).
        /// </summary>
        public void Process()
        {
            if (!_loggedIn) return;

            if (_deliverRefs.Count > 0) RecountDeliver();
            if (_areaRefs.Count > 0 && (_player.CurrentMap != _areaMap || _player.CurrentLocation != _areaLocation)) CheckAreas();
            if (_failRuns.Count > 0 && Envir.Time >= _nextFailCheck) CheckFailConditions();

            if (_nextDue != DateTime.MaxValue)
            {
                var now = Envir.Now;
                if (now >= _nextDue) CheckTimers(now);
            }

            if (_availabilityDirty) SendAvailability();
            Flush();
        }

        /// <summary>Milliseconds until the next quest timer (for PlayerObject.NextTimerDue), or -1 = none.</summary>
        public long MsUntilDue()
        {
            long result = -1;
            if (_nextDue != DateTime.MaxValue)
            {
                var ms = (_nextDue - Envir.Now).TotalMilliseconds;
                result = ms <= 0 ? 0 : (long)Math.Min(ms, int.MaxValue);
            }

            if (_failRuns.Count > 0)
            {
                var fail = Math.Max(0, _nextFailCheck - Envir.Time);
                if (result < 0 || fail < result) result = fail;
            }

            return result;
        }

        /// <summary>ReachArea: met once the player stands within Radius of (X, Y) on the objective's template map (Map, else TargetId).</summary>
        private void CheckAreas()
        {
            _areaMap = _player.CurrentMap;
            _areaLocation = _player.CurrentLocation;
            var map = TemplateMapIndex();
            for (var i = 0; i < _areaRefs.Count; i++)
            {
                var r = _areaRefs[i];
                var o = r.Objective;
                if (r.Run.Status != QuestRunStatus.Active) continue;
                var target = o.Map != 0 ? o.Map : o.TargetId;
                if (target == 0 || target != map) continue;
                if (Math.Abs(_areaLocation.X - o.X) > o.Radius || Math.Abs(_areaLocation.Y - o.Y) > o.Radius) continue;
                if (o.Condition != 0 && !EvaluateCondition(o.Condition)) continue;
                if (!SetValue(r.Run.Quest, o, Math.Max(1, o.Count))) continue;
                if (!_touched.Contains(r.Run)) _touched.Add(r.Run);
            }

            FlushTouched();
        }

        /// <summary>Step FailCondition (Part B "Runtime"): evaluated at most once a second while a current step has one.</summary>
        private void CheckFailConditions()
        {
            _nextFailCheck = Envir.Time + FailCheckMs;
            List<QuestRunState> failed = null;
            for (var i = 0; i < _failRuns.Count; i++)
            {
                var run = _failRuns[i];
                if (run.Status != QuestRunStatus.Active) continue;
                var q = Catalog.Get(run.Quest);
                if (q == null || !q.StepsById.TryGetValue(run.Step, out var step) || step.FailCondition == 0) continue;
                if (EvaluateCondition(step.FailCondition)) (failed ??= new List<QuestRunState>()).Add(run);
            }

            if (failed != null)
                for (var i = 0; i < failed.Count; i++) Fail(failed[i], "fail condition");
        }

        private void RecountDeliver()
        {
            for (var i = 0; i < _deliverRefs.Count; i++)
            {
                var r = _deliverRefs[i];
                var count = CountItems(Info.Inventory, r.Objective);
                if (_deliverCounts.TryGetValue(r.Objective.Index, out var old) && old == count) continue;
                _deliverCounts[r.Objective.Index] = count;
                MarkProgress(r.Run.Quest, r.Objective.Index);
                if (!_touched.Contains(r.Run)) _touched.Add(r.Run);
            }

            FlushTouched();
        }
        private void Flush()
        {
            if (_dirtyStateOrder.Count > 0)
            {
                var packet = new S.QuestStates();
                for (var i = 0; i < _dirtyStateOrder.Count; i++)
                {
                    var quest = _dirtyStateOrder[i];
                    var track = _trackHints.Contains(quest);
                    if (_runs.TryGetValue(quest, out var run))
                    {
                        var status = StatusOf(run);
                        _sentStatus[quest] = status;
                        packet.States.Add(StateEntry(run, status, track));
                    }
                    else
                    {
                        _sentStatus.Remove(quest);
                        packet.States.Add(new S.QuestStates.Entry { Quest = quest, Status = QuestRunStatus.None });
                    }
                }

                _dirtyStates.Clear();
                _dirtyStateOrder.Clear();
                _trackHints.Clear();
                _player.Enqueue(packet);
            }

            if (_dirtyProgressOrder.Count > 0)
            {
                var packet = new S.QuestProgress();
                for (var i = 0; i < _dirtyProgressOrder.Count; i++)
                {
                    var key = _dirtyProgressOrder[i];
                    var quest = (int)(key >> 32);
                    var objective = (int)(key & 0xFFFFFFFF);
                    if (!_runs.TryGetValue(quest, out var run)) continue;
                    var q = Catalog.Get(quest);
                    if (q == null || !q.ObjectivesById.TryGetValue(objective, out var o)) continue;
                    packet.Progress.Add(new S.QuestProgress.Entry { Quest = quest, Objective = objective, Value = ValueOf(q, run, o) });
                }

                _dirtyProgress.Clear();
                _dirtyProgressOrder.Clear();
                if (packet.Progress.Count > 0) _player.Enqueue(packet);
            }

            if (_dirtyHistory.Count > 0)
            {
                var packet = new S.QuestHistory();
                for (var i = 0; i < _dirtyHistory.Count; i++)
                    if (_history.TryGetValue(_dirtyHistory[i], out var h)) packet.History.Add(HistoryEntry(h));
                _dirtyHistory.Clear();
                if (packet.History.Count > 0) _player.Enqueue(packet);
            }
        }

        private static S.QuestStates.Entry StateEntry(QuestRunState run, QuestRunStatus status, bool track) => new S.QuestStates.Entry
        {
            Quest = run.Quest,
            Status = status,
            Step = run.Step,
            ExpiresAt = run.ExpiresAt,
            StepExpiresAt = run.StepExpiresAt,
            TrackHint = track,
        };

        // ------------------------------------------------------------------ timers

        /// <summary>Recomputes the earliest quest / step expiry (accept, step change, removal, login).</summary>
        private void ScheduleTimers()
        {
            var next = DateTime.MaxValue;
            foreach (var run in _runs.Values)
            {
                if (run.Status != QuestRunStatus.Active) continue;
                if (run.ExpiresAt != 0) { var t = DateTime.FromBinary(run.ExpiresAt); if (t < next) next = t; }
                if (run.StepExpiresAt != 0) { var t = DateTime.FromBinary(run.StepExpiresAt); if (t < next) next = t; }
            }

            _nextDue = next;
        }

        /// <summary>Applies the fail policy to every run whose quest or step time ran out (also at login: limits count while offline).</summary>
        private void CheckTimers(DateTime now)
        {
            List<QuestRunState> expired = null;
            foreach (var run in _runs.Values)
            {
                if (run.Status != QuestRunStatus.Active) continue;
                var quest = run.ExpiresAt != 0 && DateTime.FromBinary(run.ExpiresAt) <= now;
                var step = run.StepExpiresAt != 0 && DateTime.FromBinary(run.StepExpiresAt) <= now;
                if (!quest && !step) continue;
                (expired ??= new List<QuestRunState>()).Add(run);
            }

            if (expired != null)
                for (var i = 0; i < expired.Count; i++) Fail(expired[i], "time limit");

            ScheduleTimers();
        }

        /// <summary>Applies the quest's FailPolicy: Abandon (run removed), Fail (kept as failed, retake allowed), RestartStep.</summary>
        public void Fail(QuestRunState run, string reason)
        {
            var q = Catalog.Get(run.Quest);
            if (q == null || run.Status != QuestRunStatus.Active) return;
            QuestLog.Info("[Quests] fail " + _player.Name + " quest " + q.Index + " step " + run.Step + " (" + reason + ", " + q.Def.FailPolicy + ") after " + SecondsSinceAccept(run) + "s");
            _player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.QuestFailed, QuestName(q)), ChatType.System);

            switch (q.Def.FailPolicy)
            {
                case QuestFailPolicy.Fail:
                    run.Status = QuestRunStatus.Failed;
                    run.ExpiresAt = 0;
                    run.StepExpiresAt = 0;
                    RebuildIndex(run);
                    MarkState(run.Quest);
                    MarkAvailabilityDirty();
                    break;
                case QuestFailPolicy.RestartStep:
                    if (run.ExpiresAt != 0 && DateTime.FromBinary(run.ExpiresAt) <= Envir.Now) goto default; // the quest limit itself ran out
                    ClearStepValues(q, run.Step);
                    EnterStep(q, run, run.Step);
                    break;
                default:
                    RemoveRun(run.Quest);
                    _player.RecalculateQuestBag();
                    break;
            }

            ScheduleTimers();
        }

        private void ClearStepValues(QuestRuntime q, int step)
        {
            var list = q.ObjectivesOf(step);
            for (var i = 0; i < list.Count; i++)
            {
                if (!_values.TryGetValue(list[i].Index, out var state)) continue;
                state.Value = 0;
                MarkProgress(q.Index, list[i].Index);
            }
        }

        // ------------------------------------------------------------------ removal

        /// <summary>Removes a run and its objective / variable rows; the client gets Status None.</summary>
        private void RemoveRun(int quest)
        {
            if (!_runs.TryGetValue(quest, out var run)) return;
            _runs.Remove(quest);
            Info.QuestRuns.Remove(run);
            RemoveFromIndex(quest);
            RemoveQuestRecords(quest, true);
            _offers.Remove(quest);
            MarkState(quest);
            MarkAvailabilityDirty();
            ScheduleTimers();
        }

        private void RemoveQuestRecords(int quest, bool lookups)
        {
            var objectives = Info.QuestObjectives;
            for (var i = objectives.Count - 1; i >= 0; i--)
            {
                if (objectives[i].Quest != quest) continue;
                if (lookups) { _values.Remove(objectives[i].Objective); _deliverCounts.Remove(objectives[i].Objective); }
                objectives.RemoveAt(i);
            }

            var vars = Info.QuestVars;
            for (var i = vars.Count - 1; i >= 0; i--)
                if (vars[i].Quest == quest) vars.RemoveAt(i);
        }

        // ------------------------------------------------------------------ migration

        /// <summary>
        /// Revision migration (Part B "Definition edits vs saved state"; called at login and, from wave 3, by the LiveDb quest-replace
        /// hook for online players): drop rows of removed objectives, move off a removed step, clamp values, re-run step completion,
        /// reset the step timer when the step changed, set Revision and send the new state.
        /// </summary>
        private void Migrate(QuestRuntime q, QuestRunState run, bool send)
        {
            MigrateRecords(q.Def, run, Info.QuestObjectives, Envir.Now);

            // Refresh the lookups from the (possibly trimmed) rows.
            var objectives = Info.QuestObjectives;
            foreach (var key in new List<int>(_values.Keys))
                if (_values[key].Quest == run.Quest && !objectives.Contains(_values[key])) _values.Remove(key);

            if (!send) return;
            RebuildIndex(run);
            Reevaluate(run);
            MarkState(run.Quest);
            var list = q.ObjectivesOf(run.Step);
            for (var i = 0; i < list.Count; i++) MarkProgress(run.Quest, list[i].Index);
            ScheduleTimers();
        }

        /// <summary>
        /// The record part of the revision migration, also used for offline characters (web admin routes, against Envir.Edit's
        /// definition): steps 1-3, 5 and 6 of the rules on plain records. Step 4 (step completion, Ready) needs a live player and runs
        /// in <see cref="Reevaluate"/>. Returns true when anything changed.
        /// </summary>
        public static bool MigrateRecords(QuestDefinition def, QuestRunState run, List<QuestObjectiveState> objectives, DateTime now)
        {
            var changed = run.Revision != def.Revision;
            var steps = new Dictionary<int, QuestStepInfo>();
            QuestStepInfo first = null;
            for (var i = 0; i < def.Steps.Count; i++)
            {
                var s = def.Steps[i];
                if (s == null || s.Index <= 0) continue;
                steps[s.Index] = s;
                if (first == null || s.Order < first.Order || (s.Order == first.Order && s.Index < first.Index)) first = s;
            }

            var objectivesById = new Dictionary<int, QuestObjectiveInfo>();
            for (var i = 0; i < def.Objectives.Count; i++)
                if (def.Objectives[i] != null && steps.ContainsKey(def.Objectives[i].Step)) objectivesById[def.Objectives[i].Index] = def.Objectives[i];

            // 1. Drop rows of objectives that no longer exist; 3. clamp the rest.
            for (var i = objectives.Count - 1; i >= 0; i--)
            {
                var o = objectives[i];
                if (o.Quest != run.Quest) continue;
                if (!objectivesById.TryGetValue(o.Objective, out var info)) { objectives.RemoveAt(i); changed = true; continue; }
                var count = Math.Max(1, info.Count);
                if (o.Value > count) { o.Value = count; changed = true; }
            }

            // 2. Move off a removed step; 5. reset the step timer when the step changed.
            if (run.Step >= 0 && !steps.ContainsKey(run.Step))
            {
                run.Step = first != null ? first.Index : -1;
                run.StepExpiresAt = 0;
                if (first != null && first.TimeLimitSeconds > 0) run.StepExpiresAt = now.AddSeconds(first.TimeLimitSeconds).ToBinary();
                changed = true;
            }

            // 6. Set the revision.
            run.Revision = def.Revision;
            return changed;
        }

        /// <summary>
        /// The wave 3 quest-replace hook calls this for every online player after swapping Main's quest graph and dropping the catalog
        /// (game thread): the player re-binds to the new catalog, runs the revision migration for the changed quests and is re-sent their
        /// definitions. <paramref name="quests"/> = the replaced quest indexes; a quest missing from the new catalog drops its run.
        /// </summary>
        public void ApplyDefinitionChange(IReadOnlyCollection<int> quests)
        {
            _catalog = Envir.QuestCatalog;
            foreach (var quest in quests)
            {
                _player.Connection?.SentQuestDefinitions.Remove(quest);
                if (!_runs.TryGetValue(quest, out var run)) continue;
                var q = _catalog.Get(quest);
                if (q == null)
                {
                    QuestLog.Info("[Quests] " + _player.Name + " drop run of deleted quest " + quest);
                    RemoveRun(quest);
                    continue;
                }

                EnsureDefinition(q);
                Migrate(q, run, true);
            }

            // Every run's index holds QuestRuntime references of the old catalog: rebind them all.
            foreach (var run in RunsSnapshot())
                RebuildIndex(run);
            _npcSent.Clear();
            MarkAvailabilityDirty();
        }
    }
}
