using System.Globalization;
using System.Text;
using Server.MirDatabase;

namespace Server.MirEnvir.Quests
{
    /// <summary>One validation finding of a quest definition (UserData-and-Quests-Plan.md, Part B, "Validation").</summary>
    internal sealed class QuestValidationProblem
    {
        public int Quest;          // 0 = not tied to one quest (a shared row)
        public bool Warning;       // warnings never block, even from phase 3 on
        public string Message;

        public override string ToString() =>
            (Warning ? "warning: " : "error: ") + (Quest != 0 ? "quest " + Quest.ToString(CultureInfo.InvariantCulture) + ": " : string.Empty) + Message;
    }

    /// <summary>
    /// The pre-save step of the quest V2 definitions (Part B, "Codec constraints"): <see cref="PrepareForSave"/> recomputes each quest's
    /// <see cref="QuestInfo.ShapeHash"/> and bumps <see cref="QuestInfo.Revision"/> when the shape changed, then validates. It runs from
    /// <see cref="Storage.SqliteWorldCodec.Encode"/>, which every design save goes through. In phase 1 validation only reports (log + the
    /// web admin's last-report view); from phase 3 the Quests editor and JSON import block on the quests they changed.
    /// Runs on the saving thread (a web admin worker or a tool), never per tick.
    /// </summary>
    internal static class QuestDefinitions
    {
        /// <summary>Upper bound of a legacy flag number (ParseFlag: 0 … FlagIndexCount − 1000, inclusive).</summary>
        public static int MaxFlagNumber => Globals.FlagIndexCount - 1000;

        private static readonly object ReportGate = new object();
        private static List<QuestValidationProblem> _lastProblems = new List<QuestValidationProblem>();
        private static string _lastFingerprint;

        /// <summary>The problems found by the last save's validation (copy).</summary>
        public static List<QuestValidationProblem> LastProblems
        {
            get { lock (ReportGate) return new List<QuestValidationProblem>(_lastProblems); }
        }

        public static void PrepareForSave(Envir e)
        {
            for (var i = 0; i < e.QuestInfoList.Count; i++)
            {
                var q = e.QuestInfoList[i];
                if (q == null || !q.HasDefinition) continue; // legacy-only quests keep ShapeHash 0 / Revision 0
                var hash = ShapeHashOf(q);
                if (hash == q.Definition.ShapeHash) continue;
                q.Definition.ShapeHash = hash;
                q.Definition.Revision++;
            }

            var problems = Validate(e);
            var errors = problems.Count(p => !p.Warning);
            var fingerprint = errors.ToString(CultureInfo.InvariantCulture) + "/" + (problems.Count - errors).ToString(CultureInfo.InvariantCulture) + "/" +
                              (problems.Count > 0 ? problems[0].ToString() : string.Empty);
            bool changed;
            lock (ReportGate)
            {
                _lastProblems = problems;
                changed = fingerprint != _lastFingerprint;
                _lastFingerprint = fingerprint;
            }

            if (changed && problems.Count > 0 && e == Envir.Edit)
                MessageQueue.Instance.Enqueue("[Quests] Definition check: " + errors.ToString(CultureInfo.InvariantCulture) + " error(s), " +
                                              (problems.Count - errors).ToString(CultureInfo.InvariantCulture) + " warning(s) (reported only; first: " + problems[0] + ")");
        }

        // ------------------------------------------------------------------ shape hash

        /// <summary>
        /// FNV-1a 64 over what a character's saved state depends on: steps (id, order, completion, next), objectives (id, step, type,
        /// target, count, optional) and branches (step, next, outcome), in list order. Text, rewards and conditions do not change it.
        /// </summary>
        public static long ShapeHashOf(QuestInfo q)
        {
            var h = 14695981039346656037UL;
            void Mix(long v)
            {
                for (var b = 0; b < 8; b++)
                {
                    h ^= (byte)(v >> (b * 8));
                    h *= 1099511628211UL;
                }
            }

            Mix(q.Definition.Steps.Count);
            foreach (var s in q.Definition.Steps) { Mix(s.Index); Mix(s.Order); Mix((long)s.CompletionMode); Mix(s.NRequired); Mix(s.NextStep); }
            Mix(q.Definition.Objectives.Count);
            foreach (var o in q.Definition.Objectives)
            {
                Mix(o.Index); Mix(o.Step); Mix((long)o.Type); Mix((long)o.TargetKind); Mix(o.TargetId); Mix(o.Count); Mix(o.Optional ? 1 : 0);
            }

            Mix(q.Definition.Branches.Count);
            foreach (var b in q.Definition.Branches) { Mix(b.Step); Mix(b.NextStep); Mix(b.Outcome); }
            return unchecked((long)h);
        }

        // ------------------------------------------------------------------ validation

        public static List<QuestValidationProblem> Validate(Envir e) => Validate(QuestWorld.FromEnvir(e));

        /// <summary>Validates the quest definitions of <paramref name="e"/> (an Envir, or a detached view for the web admin editor and tests).</summary>
        public static List<QuestValidationProblem> Validate(QuestWorld e)
        {
            var problems = new List<QuestValidationProblem>();
            void Error(int quest, string message) => problems.Add(new QuestValidationProblem { Quest = quest, Message = message });
            void Warn(int quest, string message) => problems.Add(new QuestValidationProblem { Quest = quest, Warning = true, Message = message });

            var items = new HashSet<int>(e.ItemInfoList.Where(x => x != null).Select(x => x.Index));
            var monsters = new HashSet<int>(e.MonsterInfoList.Where(x => x != null).Select(x => x.Index));
            var npcs = new HashSet<int>(e.NPCInfoList.Where(x => x != null).Select(x => x.Index));
            var maps = new HashSet<int>(e.MapInfoList.Where(x => x != null).Select(x => x.Index));
            var quests = new HashSet<int>(e.QuestInfoList.Where(x => x != null).Select(x => x.Index));
            var storylines = new HashSet<int>(e.StorylineList.Select(x => x.Index));
            var pools = new HashSet<int>(e.QuestPoolList.Select(x => x.Index));
            var groups = new Dictionary<int, QuestGroupInfo>();
            foreach (var g in e.QuestGroupList)
                if (!groups.TryAdd(g.Index, g)) Error(0, "quest group id " + g.Index + " is used twice");

            var texts = new Dictionary<string, string>(StringComparer.Ordinal); // key + U+0001 + lang → body
            foreach (var t in e.QuestTextList)
            {
                if (string.IsNullOrEmpty(t.TextKey)) Error(0, "text #" + t.Index + " has no key");
                else if (!texts.TryAdd(t.TextKey + "\u0001" + (t.Lang ?? string.Empty), t.Body ?? string.Empty)) Error(0, "text key " + t.TextKey + " is defined twice for one language");
            }

            bool TextExists(string key) => texts.ContainsKey(key + "\u0001");

            // Conditions: unique ids, parents exist, no cycles, Not has one child, leaf args.
            var conditions = new Dictionary<int, QuestConditionInfo>();
            foreach (var c in e.QuestConditionList)
                if (c.Index <= 0 || !conditions.TryAdd(c.Index, c)) Error(c.OwnerQuest, "condition id " + c.Index + " is invalid or used twice");
            var children = new Dictionary<int, int>();
            foreach (var c in conditions.Values)
            {
                if (c.Parent != 0)
                {
                    if (!conditions.TryGetValue(c.Parent, out var parent)) Error(c.OwnerQuest, "condition " + c.Index + " has a missing parent " + c.Parent);
                    else
                    {
                        if (parent.Op == QuestConditionOp.Leaf) Error(c.OwnerQuest, "condition " + c.Index + " has a leaf as its parent");
                        if (parent.OwnerQuest != c.OwnerQuest) Error(c.OwnerQuest, "condition " + c.Index + " and its parent " + parent.Index + " have different owners");
                    }

                    children[c.Parent] = children.TryGetValue(c.Parent, out var n) ? n + 1 : 1;
                }

                var seen = new HashSet<int> { c.Index };
                for (var p = c.Parent; p != 0 && conditions.TryGetValue(p, out var up); p = up.Parent)
                    if (!seen.Add(p)) { Error(c.OwnerQuest, "condition " + c.Index + " is in a parent cycle"); break; }

                if (c.Op == QuestConditionOp.Leaf) CheckLeaf(c, items, maps, quests, Error);
            }

            foreach (var c in conditions.Values)
            {
                var count = children.TryGetValue(c.Index, out var n) ? n : 0;
                if (c.Op == QuestConditionOp.Not && count != 1) Error(c.OwnerQuest, "condition " + c.Index + " (Not) has " + count + " children, needs 1");
                if ((c.Op == QuestConditionOp.And || c.Op == QuestConditionOp.Or) && count == 0)
                    Warn(c.OwnerQuest, "condition " + c.Index + " (" + c.Op + ") has no children (And = always true, Or = never true)");
            }

            void CheckConditionRef(int quest, int id, string what)
            {
                if (id == 0) return;
                if (!conditions.TryGetValue(id, out var c)) { Error(quest, what + " names a missing condition " + id); return; }
                if (c.OwnerQuest != 0 && c.OwnerQuest != quest) Error(quest, what + " uses condition " + id + ", owned by quest " + c.OwnerQuest);
            }

            void CheckText(int quest, string key, string what)
            {
                if (!string.IsNullOrEmpty(key) && !TextExists(key)) Error(quest, what + " names a missing text " + key);
            }

            var stepIds = new HashSet<int>();
            var objectiveIds = new HashSet<int>();
            var varIds = new HashSet<int>();
            var collectBy = new Dictionary<int, List<int>>(); // item → quests collecting it
            foreach (var q in e.QuestInfoList)
            {
                if (q == null || !q.HasDefinition) continue;
                var id = q.Index;

                CheckText(id, q.Definition.NameKey, "NameKey");
                CheckText(id, q.Definition.SummaryKey, "SummaryKey");
                CheckText(id, q.Definition.CompleteKey, "CompleteKey");
                if (string.IsNullOrEmpty(q.Definition.NameKey)) Error(id, "has no NameKey");
                CheckConditionRef(id, q.Definition.AcceptCondition, "AcceptCondition");
                CheckConditionRef(id, q.Definition.VisibleCondition, "VisibleCondition");
                if (q.Definition.StorylineIndex != 0 && !storylines.Contains(q.Definition.StorylineIndex)) Error(id, "names a missing storyline " + q.Definition.StorylineIndex);
                if (q.Definition.PoolIndex != 0 && !pools.Contains(q.Definition.PoolIndex)) Error(id, "names a missing pool " + q.Definition.PoolIndex);
                if (q.Definition.RepeatPolicy == QuestRepeatPolicy.Cooldown && q.Definition.CooldownSeconds <= 0) Error(id, "Cooldown repeat policy needs CooldownSeconds > 0");

                // Steps.
                var steps = new Dictionary<int, QuestStepInfo>();
                foreach (var s in q.Definition.Steps)
                {
                    if (s.Index <= 0 || s.Index > e.QuestStepIndex) Error(id, "step id " + s.Index + " is outside the counter (1.." + e.QuestStepIndex + ")");
                    if (!stepIds.Add(s.Index)) Error(id, "step id " + s.Index + " is used twice");
                    steps[s.Index] = s;
                    CheckText(id, s.TitleKey, "step " + s.Index + " TitleKey");
                    CheckText(id, s.TextKey, "step " + s.Index + " TextKey");
                    CheckText(id, s.ReadyTextKey, "step " + s.Index + " ReadyTextKey");
                    CheckConditionRef(id, s.FailCondition, "step " + s.Index + " FailCondition");
                    if (s.CompletionMode == QuestStepCompletion.NOf && s.NRequired <= 0) Error(id, "step " + s.Index + " (NOf) needs NRequired > 0");
                }

                var ordered = q.Definition.Steps.OrderBy(s => s.Order).ThenBy(s => s.Index).ToList();
                foreach (var s in q.Definition.Steps)
                    if (s.NextStep > 0 && !steps.ContainsKey(s.NextStep)) Error(id, "step " + s.Index + " NextStep " + s.NextStep + " is not a step of this quest");
                    else if (s.NextStep < -1) Error(id, "step " + s.Index + " NextStep " + s.NextStep + " is invalid");

                // Review C1: C.QuestChoose names a branch by its ChoiceKey and the engine takes the first match, so a key repeated
                // within one step makes the later branch unreachable.
                var choiceKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in q.Definition.Branches)
                    if (!choiceKeys.Add(b.Step + "\u0001" + (b.ChoiceKey ?? string.Empty)))
                        Error(id, "step " + b.Step + " has two branches with the ChoiceKey '" + b.ChoiceKey + "' (the choice must be unique within a step)");

                // One text key on two different fields is allowed (deliberate reuse), but usually an accident: a warning.
                var fieldsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                void Field(string key, string what)
                {
                    if (string.IsNullOrEmpty(key)) return;
                    if (fieldsByKey.TryGetValue(key, out var first)) Warn(id, "text key " + key + " is used by " + first + " and " + what + " (one text for both)");
                    else fieldsByKey[key] = what;
                }

                Field(q.Definition.NameKey, "NameKey"); Field(q.Definition.SummaryKey, "SummaryKey"); Field(q.Definition.CompleteKey, "CompleteKey");
                foreach (var s in q.Definition.Steps) { Field(s.TitleKey, "step " + s.Index + " TitleKey"); Field(s.TextKey, "step " + s.Index + " TextKey"); Field(s.ReadyTextKey, "step " + s.Index + " ReadyTextKey"); }
                foreach (var o in q.Definition.Objectives) Field(o.TextKey, "objective " + o.Index + " TextKey");
                foreach (var b in q.Definition.Branches) { Field(b.ChoiceKey, "a branch ChoiceKey of step " + b.Step); Field(b.TextKey, "a branch TextKey of step " + b.Step); }

                foreach (var b in q.Definition.Branches)
                {
                    if (!steps.ContainsKey(b.Step)) Error(id, "a branch names step " + b.Step + ", not a step of this quest");
                    if (b.NextStep > 0 && !steps.ContainsKey(b.NextStep)) Error(id, "a branch goes to step " + b.NextStep + ", not a step of this quest");
                    CheckText(id, b.ChoiceKey, "branch ChoiceKey");
                    CheckText(id, b.TextKey, "branch TextKey");
                    CheckConditionRef(id, b.Condition, "branch Condition");
                }

                // Reachability from the first step (default chain + branch targets); a loop without a branch never ends.
                if (ordered.Count > 0)
                {
                    var reached = new HashSet<int>();
                    var queue = new Queue<int>();
                    queue.Enqueue(ordered[0].Index);
                    while (queue.Count > 0)
                    {
                        var at = queue.Dequeue();
                        if (!reached.Add(at)) continue;
                        var step = steps[at];
                        var branched = false;
                        foreach (var b in q.Definition.Branches)
                            if (b.Step == at) { branched = true; if (b.NextStep > 0 && steps.ContainsKey(b.NextStep)) queue.Enqueue(b.NextStep); }
                        if (branched) continue;
                        var next = step.NextStep > 0 ? step.NextStep : step.NextStep == 0 ? NextByOrder(ordered, step) : -1;
                        if (next > 0 && steps.ContainsKey(next)) queue.Enqueue(next);
                    }

                    foreach (var s in q.Definition.Steps)
                        if (!reached.Contains(s.Index)) Error(id, "step " + s.Index + " cannot be reached");

                    var walked = new HashSet<int>();
                    for (var at = ordered[0].Index; at > 0 && steps.ContainsKey(at);)
                    {
                        if (q.Definition.Branches.Any(b => b.Step == at)) break;
                        if (!walked.Add(at)) { Error(id, "the steps loop back to step " + at + " with no branch to leave the loop"); break; }
                        var step = steps[at];
                        at = step.NextStep > 0 ? step.NextStep : step.NextStep == 0 ? NextByOrder(ordered, step) : -1;
                    }
                }

                // Objectives.
                foreach (var o in q.Definition.Objectives)
                {
                    var what = "objective " + o.Index;
                    if (o.Index <= 0 || o.Index > e.QuestObjectiveIndex) Error(id, what + " id is outside the counter (1.." + e.QuestObjectiveIndex + ")");
                    if (!objectiveIds.Add(o.Index)) Error(id, what + " id is used twice");
                    if (!steps.ContainsKey(o.Step)) Error(id, what + " names step " + o.Step + ", not a step of this quest");
                    if (o.Count < 1) Error(id, what + " Count is " + o.Count + " (needs at least 1)");
                    CheckConditionRef(id, o.Condition, what + " Condition");
                    CheckText(id, o.TextKey, what + " TextKey");
                    CheckObjectiveTarget(o, items, monsters, npcs, maps, groups, msg => Error(id, what + " " + msg));
                    if (o.Type == QuestObjectiveType.Collect && o.TargetKind == QuestTargetKind.Item)
                    {
                        if (!collectBy.TryGetValue(o.TargetId, out var list)) collectBy[o.TargetId] = list = new List<int>();
                        if (!list.Contains(id)) list.Add(id);
                    }
                }

                // Endpoints.
                foreach (var ep in q.Definition.Endpoints)
                {
                    switch (ep.Kind)
                    {
                        case QuestEndpointKind.Npc:
                            if (!npcs.Contains(ep.TargetId)) Error(id, ep.Role + " endpoint names a missing NPC " + ep.TargetId);
                            break;
                        case QuestEndpointKind.ItemUse:
                            if (!items.Contains(ep.TargetId)) Error(id, ep.Role + " endpoint names a missing item " + ep.TargetId);
                            break;
                        case QuestEndpointKind.AreaEnter:
                            if (!maps.Contains(ep.Map)) Error(id, ep.Role + " endpoint names a missing map " + ep.Map);
                            break;
                        case QuestEndpointKind.Auto:
                            break;
                        default:
                            Error(id, ep.Role + " endpoint kind " + ep.Kind + " has no runtime");
                            break;
                    }
                }

                // Rewards.
                var objectivesOfQuest = new HashSet<int>(q.Definition.Objectives.Select(o => o.Index));
                foreach (var r in q.Definition.Rewards)
                {
                    var what = r.Scope + " reward " + r.Kind;
                    if (r.Kind == QuestRewardKind.Item && !items.Contains(r.TargetId)) Error(id, what + " names a missing item " + r.TargetId);
                    if (r.Scope == QuestRewardScope.Accept && r.Kind != QuestRewardKind.Item) Error(id, what + ": Accept rewards must be items");
                    if (r.Scope == QuestRewardScope.Step && !steps.ContainsKey(r.ScopeId)) Error(id, what + " names step " + r.ScopeId + ", not a step of this quest");
                    if (r.Scope == QuestRewardScope.Objective && !objectivesOfQuest.Contains(r.ScopeId)) Error(id, what + " names objective " + r.ScopeId + ", not one of this quest");
                    if (r.Amount < 0) Error(id, what + " has a negative amount");
                    if (r.ChoiceGroup < 0) Error(id, what + " has a negative ChoiceGroup");
                    CheckConditionRef(id, r.Condition, what + " Condition");
                }

                // Quest variables.
                var vars = new HashSet<int>();
                foreach (var v in q.Definition.Vars)
                {
                    if (v.Index <= 0 || v.Index > e.QuestVarIndex) Error(id, "variable id " + v.Index + " is outside the counter");
                    if (!varIds.Add(v.Index)) Error(id, "variable id " + v.Index + " is used twice");
                    vars.Add(v.Index);
                }

                foreach (var c in conditions.Values)
                    if (c.OwnerQuest == id && c.Op == QuestConditionOp.Leaf && c.Leaf == QuestConditionLeaf.QuestVar && !vars.Contains(c.Arg1))
                        Error(id, "condition " + c.Index + " names variable " + c.Arg1 + ", not a variable of this quest");

                // Size: a generous estimate of the S.QuestDefinition encoding (fixed fields per row, every text by its UTF-8 length, the key
                // strings, and 4 bytes per group member plus a label per group target; review S5). The engine also refuses to send a
                // definition over the limit (PlayerQuests.EnsureDefinition).
                var estimate = EstimateDefinitionBytes(q.Definition, texts, groups);
                if (estimate > MaxDefinitionBytes) Error(id, "its definition would encode to about " + estimate + " bytes (packet limit 65,535)");
            }

            foreach (var g in groups.Values)
            {
                if (g.Members.Count == 0) Warn(0, "quest group " + g.Index + " (" + g.AuthorKey + ") is empty");
                foreach (var m in g.Members)
                    if (g.Kind == QuestGroupKind.Monster ? !monsters.Contains(m.TargetId) : !items.Contains(m.TargetId))
                        Error(0, "quest group " + g.Index + " has a missing " + (g.Kind == QuestGroupKind.Monster ? "monster " : "item ") + m.TargetId);
            }

            foreach (var p in e.QuestPoolList)
                foreach (var m in p.Members)
                    if (!quests.Contains(m.Quest)) Error(0, "quest pool " + p.Index + " names a missing quest " + m.Quest);

            foreach (var pair in collectBy)
                if (pair.Value.Count > 1)
                    Warn(0, "quests " + string.Join(", ", pair.Value) + " all collect item " + pair.Key + " (they share the quest-bag count when active together)");

            return problems;
        }

        /// <summary>The packet limit of one S.QuestDefinition (a ushort length).</summary>
        public const int MaxDefinitionBytes = 65535;

        /// <summary>A generous estimate of one quest's S.QuestDefinition size (review S5); texts keyed "key\u0001lang".</summary>
        internal static int EstimateDefinitionBytes(QuestDefinition d, Dictionary<string, string> texts, Dictionary<int, QuestGroupInfo> groups)
        {
            static int Str(string s) => 5 + (string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s));
            var estimate = 128 + Str(d.GroupName) + Str(d.AuthorKey);
            estimate += 48 * (d.Steps.Count + d.Objectives.Count + d.Branches.Count + d.Endpoints.Count + d.Rewards.Count + d.Vars.Count);
            foreach (var b in d.Branches) estimate += Str(b.ChoiceKey);
            foreach (var o in d.Objectives)
            {
                if (o.TargetKind != QuestTargetKind.MonsterGroup && o.TargetKind != QuestTargetKind.ItemGroup) continue;
                estimate += 64; // the server-filled target label
                if (groups.TryGetValue(o.TargetId, out var g)) estimate += 4 * g.Members.Count + Str(g.AuthorKey);
            }

            foreach (var key in TextKeysOf(d))
                if (!string.IsNullOrEmpty(key) && texts.TryGetValue(key + "\u0001", out var body)) estimate += Str(body) + 8;
            return estimate;
        }

        private static int NextByOrder(List<QuestStepInfo> ordered, QuestStepInfo step)
        {
            var i = ordered.IndexOf(step);
            return i >= 0 && i + 1 < ordered.Count ? ordered[i + 1].Index : -1;
        }

        private static void CheckLeaf(QuestConditionInfo c, HashSet<int> items, HashSet<int> maps, HashSet<int> quests, Action<int, string> error)
        {
            var what = "condition " + c.Index + " (" + c.Leaf + ")";
            switch (c.Leaf)
            {
                case QuestConditionLeaf.None:
                    error(c.OwnerQuest, what + " is a leaf with no kind");
                    break;
                case QuestConditionLeaf.Level:
                case QuestConditionLeaf.PartySize:
                    if (c.Arg1 < 0) error(c.OwnerQuest, what + " needs a value of at least 0");
                    break;
                case QuestConditionLeaf.Class:
                    if (!Enum.IsDefined(typeof(MirClass), (byte)Math.Clamp(c.Arg1, 0, 255)) || c.Arg1 > 255) error(c.OwnerQuest, what + " names an unknown class " + c.Arg1);
                    break;
                case QuestConditionLeaf.Gender:
                    if (!Enum.IsDefined(typeof(MirGender), (byte)Math.Clamp(c.Arg1, 0, 255)) || c.Arg1 > 255) error(c.OwnerQuest, what + " names an unknown gender " + c.Arg1);
                    break;
                case QuestConditionLeaf.QuestStatus:
                case QuestConditionLeaf.QuestCompletedCount:
                case QuestConditionLeaf.QuestOutcome:
                    if (!quests.Contains(c.Arg1)) error(c.OwnerQuest, what + " names a missing quest " + c.Arg1);
                    break;
                case QuestConditionLeaf.ItemCount:
                case QuestConditionLeaf.Equipped:
                    if (!items.Contains(c.Arg1)) error(c.OwnerQuest, what + " names a missing item " + c.Arg1);
                    break;
                case QuestConditionLeaf.Flag:
                    if (c.Arg1 < 0 || c.Arg1 >= Globals.FlagIndexCount) error(c.OwnerQuest, what + " names flag " + c.Arg1 + ", outside 0.." + (Globals.FlagIndexCount - 1));
                    break;
                case QuestConditionLeaf.MapIs:
                    if (!maps.Contains(c.Arg1)) error(c.OwnerQuest, what + " names a missing map " + c.Arg1);
                    break;
                case QuestConditionLeaf.TimeOfDay:
                    if (c.Arg1 < 0 || c.Arg1 > 23) error(c.OwnerQuest, what + " needs an hour 0-23");
                    break;
                case QuestConditionLeaf.DayOfWeek:
                    if (c.Arg1 < 0 || c.Arg1 > 6) error(c.OwnerQuest, what + " needs a day 0-6");
                    break;
            }

            if (!Enum.IsDefined(typeof(QuestCompare), c.Cmp)) error(c.OwnerQuest, what + " has an unknown comparison " + (int)c.Cmp);
        }

        private static void CheckObjectiveTarget(QuestObjectiveInfo o, HashSet<int> items, HashSet<int> monsters, HashSet<int> npcs,
            HashSet<int> maps, Dictionary<int, QuestGroupInfo> groups, Action<string> error)
        {
            bool Kind(params QuestTargetKind[] allowed) => allowed.Contains(o.TargetKind);
            switch (o.Type)
            {
                case QuestObjectiveType.Kill:
                    if (!Kind(QuestTargetKind.Monster, QuestTargetKind.MonsterGroup)) { error("(Kill) needs a Monster or MonsterGroup target, has " + o.TargetKind); return; }
                    break;
                case QuestObjectiveType.Collect:
                case QuestObjectiveType.Deliver:
                case QuestObjectiveType.UseItem:
                case QuestObjectiveType.Craft:
                case QuestObjectiveType.Gather:
                    if (!Kind(QuestTargetKind.Item, QuestTargetKind.ItemGroup)) { error("(" + o.Type + ") needs an Item or ItemGroup target, has " + o.TargetKind); return; }
                    break;
                case QuestObjectiveType.Talk:
                    if (!Kind(QuestTargetKind.Npc)) { error("(Talk) needs an Npc target, has " + o.TargetKind); return; }
                    break;
                case QuestObjectiveType.ReachArea:
                    if (!Kind(QuestTargetKind.Map)) { error("(ReachArea) needs a Map target, has " + o.TargetKind); return; }
                    if (o.Radius <= 0) error("(ReachArea) needs a radius above 0");
                    break;
                case QuestObjectiveType.ReachLevel:
                case QuestObjectiveType.Counter:
                    if (!Kind(QuestTargetKind.None)) { error("(" + o.Type + ") takes no target, has " + o.TargetKind); return; }
                    break;
                case QuestObjectiveType.UseSkill:
                    if (!Kind(QuestTargetKind.Spell)) { error("(UseSkill) needs a Spell target, has " + o.TargetKind); return; }
                    break;
                case QuestObjectiveType.Flag:
                    if (!Kind(QuestTargetKind.Flag)) { error("(Flag) needs a Flag target, has " + o.TargetKind); return; }
                    break;
                default:
                    error("type " + o.Type + " is reserved (no runtime yet)");
                    return;
            }

            switch (o.TargetKind)
            {
                case QuestTargetKind.Monster:
                    if (!monsters.Contains(o.TargetId)) error("names a missing monster " + o.TargetId);
                    break;
                case QuestTargetKind.Item:
                    if (!items.Contains(o.TargetId)) error("names a missing item " + o.TargetId);
                    break;
                case QuestTargetKind.Npc:
                    if (!npcs.Contains(o.TargetId)) error("names a missing NPC " + o.TargetId);
                    break;
                case QuestTargetKind.Map:
                    if (!maps.Contains(o.TargetId)) error("names a missing map " + o.TargetId);
                    break;
                case QuestTargetKind.MonsterGroup:
                case QuestTargetKind.ItemGroup:
                    if (!groups.TryGetValue(o.TargetId, out var g)) error("names a missing group " + o.TargetId);
                    else if ((o.TargetKind == QuestTargetKind.MonsterGroup) != (g.Kind == QuestGroupKind.Monster)) error("names group " + o.TargetId + " of the wrong kind");
                    break;
                case QuestTargetKind.Spell:
                    if (!Enum.IsDefined(typeof(Spell), (byte)Math.Clamp(o.TargetId, 0, 255)) || o.TargetId > 255) error("names an unknown spell " + o.TargetId);
                    break;
                case QuestTargetKind.Flag:
                    if (o.TargetId < 0 || o.TargetId > MaxFlagNumber) error("names flag " + o.TargetId + ", outside 0.." + MaxFlagNumber);
                    break;
            }

            if (o.Map != 0 && !maps.Contains(o.Map)) error("marker names a missing map " + o.Map);
        }

        private static IEnumerable<string> TextKeysOf(QuestDefinition d)
        {
            yield return d.NameKey;
            yield return d.SummaryKey;
            yield return d.CompleteKey;
            foreach (var s in d.Steps) { yield return s.TitleKey; yield return s.TextKey; yield return s.ReadyTextKey; }
            foreach (var o in d.Objectives) yield return o.TextKey;
            foreach (var b in d.Branches) { yield return b.ChoiceKey; yield return b.TextKey; }
        }
    }

    /// <summary>
    /// The lists and counters quest validation and the web admin Quests editor read (UserData-and-Quests-Plan.md, Part B, "Authoring"): a
    /// view over an <see cref="Envir"/> (its lists by reference, its counters copied; <see cref="Commit"/> writes them back), or a detached
    /// world for tests. Member names match Envir's so validation reads the same either way. Same thread rules as the Envir it views
    /// (Envir.Edit: EditLock).
    /// </summary>
    internal sealed class QuestWorld
    {
        public List<ItemInfo> ItemInfoList = new List<ItemInfo>();
        public List<MonsterInfo> MonsterInfoList = new List<MonsterInfo>();
        public List<NPCInfo> NPCInfoList = new List<NPCInfo>();
        public List<MapInfo> MapInfoList = new List<MapInfo>();
        public List<QuestInfo> QuestInfoList = new List<QuestInfo>();
        public List<QuestConditionInfo> QuestConditionList = new List<QuestConditionInfo>();
        public List<QuestTextInfo> QuestTextList = new List<QuestTextInfo>();
        public List<StorylineInfo> StorylineList = new List<StorylineInfo>();
        public List<QuestGroupInfo> QuestGroupList = new List<QuestGroupInfo>();
        public List<QuestPoolInfo> QuestPoolList = new List<QuestPoolInfo>();

        public int QuestIndex, QuestStepIndex, QuestObjectiveIndex, QuestConditionIndex, QuestGroupIndex, QuestTextIndex, QuestVarIndex, StorylineIndex, QuestPoolIndex;

        /// <summary>The Envir this view reads (null for a detached world).</summary>
        public Envir Source;

        public static QuestWorld FromEnvir(Envir e)
        {
            var w = new QuestWorld
            {
                Source = e,
                ItemInfoList = e.ItemInfoList, MonsterInfoList = e.MonsterInfoList, NPCInfoList = e.NPCInfoList, MapInfoList = e.MapInfoList,
                QuestInfoList = e.QuestInfoList, QuestConditionList = e.QuestConditionList, QuestTextList = e.QuestTextList,
                StorylineList = e.StorylineList, QuestGroupList = e.QuestGroupList, QuestPoolList = e.QuestPoolList,
            };
            w.ReadCounters();
            return w;
        }

        /// <summary>Copies the counters from <see cref="Source"/> (no-op for a detached world).</summary>
        public void ReadCounters()
        {
            var e = Source;
            if (e == null) return;
            QuestIndex = e.QuestIndex; QuestStepIndex = e.QuestStepIndex; QuestObjectiveIndex = e.QuestObjectiveIndex;
            QuestConditionIndex = e.QuestConditionIndex; QuestGroupIndex = e.QuestGroupIndex; QuestTextIndex = e.QuestTextIndex;
            QuestVarIndex = e.QuestVarIndex; StorylineIndex = e.StorylineIndex; QuestPoolIndex = e.QuestPoolIndex;
        }

        /// <summary>Writes the counters back to <see cref="Source"/> (they only go up; no-op for a detached world).</summary>
        public void Commit()
        {
            var e = Source;
            if (e == null) return;
            e.QuestIndex = Math.Max(e.QuestIndex, QuestIndex); e.QuestStepIndex = Math.Max(e.QuestStepIndex, QuestStepIndex);
            e.QuestObjectiveIndex = Math.Max(e.QuestObjectiveIndex, QuestObjectiveIndex); e.QuestConditionIndex = Math.Max(e.QuestConditionIndex, QuestConditionIndex);
            e.QuestGroupIndex = Math.Max(e.QuestGroupIndex, QuestGroupIndex); e.QuestTextIndex = Math.Max(e.QuestTextIndex, QuestTextIndex);
            e.QuestVarIndex = Math.Max(e.QuestVarIndex, QuestVarIndex); e.StorylineIndex = Math.Max(e.StorylineIndex, StorylineIndex);
            e.QuestPoolIndex = Math.Max(e.QuestPoolIndex, QuestPoolIndex);
        }
    }
}
