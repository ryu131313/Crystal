using Server.MirDatabase;

namespace Server.MirEnvir.Quests
{
    /// <summary>
    /// One quest's runtime view (Main only): its definition plus lookups the engine needs per event, built once per catalog.
    /// Read-only after <see cref="QuestCatalog.Build"/>; the wave 3 quest-replace hook swaps in a fresh one.
    /// </summary>
    public sealed class QuestRuntime
    {
        public QuestInfo Info;
        public QuestDefinition Def;
        public int Index => Info.Index;

        /// <summary>Steps ordered by Order (then id).</summary>
        public readonly List<QuestStepInfo> StepsByOrder = new List<QuestStepInfo>();
        /// <summary>Objectives per step id, ordered by Order (then id).</summary>
        public readonly Dictionary<int, List<QuestObjectiveInfo>> ObjectivesByStep = new Dictionary<int, List<QuestObjectiveInfo>>();
        public readonly Dictionary<int, QuestObjectiveInfo> ObjectivesById = new Dictionary<int, QuestObjectiveInfo>();
        public readonly Dictionary<int, QuestStepInfo> StepsById = new Dictionary<int, QuestStepInfo>();
        /// <summary>Branches per step id (empty for most steps).</summary>
        public readonly Dictionary<int, List<QuestBranchInfo>> BranchesByStep = new Dictionary<int, List<QuestBranchInfo>>();

        /// <summary>True when the quest names a Finish endpoint; otherwise the Start NPCs take it back (legacy FinishNpcIndex default).</summary>
        public bool HasFinishEndpoint;

        private static readonly List<QuestObjectiveInfo> NoObjectives = new List<QuestObjectiveInfo>();
        private static readonly List<QuestBranchInfo> NoBranches = new List<QuestBranchInfo>();

        public List<QuestObjectiveInfo> ObjectivesOf(int step) =>
            step != 0 && ObjectivesByStep.TryGetValue(step, out var list) ? list : NoObjectives;

        public List<QuestBranchInfo> BranchesOf(int step) =>
            BranchesByStep.TryGetValue(step, out var list) ? list : NoBranches;

        public QuestStepInfo FirstStep => StepsByOrder.Count > 0 ? StepsByOrder[0] : null;

        /// <summary>The step after <paramref name="step"/> by NextStep (0 = next by Order), or null = ready to turn in (-1 or none left).</summary>
        public QuestStepInfo NextStepOf(QuestStepInfo step)
        {
            if (step.NextStep < 0) return null;
            if (step.NextStep > 0) return StepsById.TryGetValue(step.NextStep, out var target) ? target : null;
            for (var i = 0; i < StepsByOrder.Count - 1; i++)
                if (StepsByOrder[i] == step) return StepsByOrder[i + 1];
            return null;
        }

        public bool Has(QuestFlags flag) => (Def.Flags & flag) != 0;
    }

    /// <summary>
    /// Runtime lookups over Main's quest V2 definitions (UserData-and-Quests-Plan.md, Part B "Runtime"): quests, conditions, groups,
    /// texts and the NPC endpoint lookup. Built lazily on the game thread from Main's lists (<see cref="Envir.QuestCatalog"/>) and
    /// never mutated afterwards; <see cref="Envir.InvalidateQuestCatalog"/> drops it (wave 3: the quest-replace hook). Every holder
    /// of a QuestRuntime reference (player indexes, availability, sent sets) is refreshed by whoever swaps the catalog.
    /// </summary>
    public sealed class QuestCatalog
    {
        public readonly Dictionary<int, QuestRuntime> Quests = new Dictionary<int, QuestRuntime>();
        public readonly Dictionary<int, QuestConditionInfo> Conditions = new Dictionary<int, QuestConditionInfo>();
        /// <summary>Children of each condition node, ordered by Order.</summary>
        public readonly Dictionary<int, List<QuestConditionInfo>> ConditionChildren = new Dictionary<int, List<QuestConditionInfo>>();
        public readonly Dictionary<int, QuestGroupInfo> Groups = new Dictionary<int, QuestGroupInfo>();
        public readonly Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>NPCInfo.Index → quests it starts.</summary>
        public readonly Dictionary<int, List<QuestRuntime>> NpcStarts = new Dictionary<int, List<QuestRuntime>>();
        /// <summary>NPCInfo.Index → quests it finishes (Finish endpoints, or the Start NPCs of a quest without one).</summary>
        public readonly Dictionary<int, List<QuestRuntime>> NpcFinishes = new Dictionary<int, List<QuestRuntime>>();
        /// <summary>Quests with an Auto start endpoint (offered when the accept condition becomes true).</summary>
        public readonly List<QuestRuntime> AutoStarts = new List<QuestRuntime>();

        private static readonly List<QuestConditionInfo> NoConditions = new List<QuestConditionInfo>();
        private static readonly List<QuestRuntime> NoQuests = new List<QuestRuntime>();

        public QuestRuntime Get(int quest) => Quests.TryGetValue(quest, out var q) ? q : null;

        public List<QuestConditionInfo> ChildrenOf(int condition) =>
            ConditionChildren.TryGetValue(condition, out var list) ? list : NoConditions;

        public List<QuestRuntime> StartsAt(int npcInfoIndex) => NpcStarts.TryGetValue(npcInfoIndex, out var list) ? list : NoQuests;
        public List<QuestRuntime> FinishesAt(int npcInfoIndex) => NpcFinishes.TryGetValue(npcInfoIndex, out var list) ? list : NoQuests;

        /// <summary>The text of <paramref name="key"/> in the server language ("" when missing or empty key).</summary>
        public string Text(string key) => !string.IsNullOrEmpty(key) && Texts.TryGetValue(key, out var body) ? body ?? string.Empty : string.Empty;

        public static QuestCatalog Build(Envir e)
        {
            var c = new QuestCatalog();

            for (var i = 0; i < e.QuestTextList.Count; i++)
            {
                var t = e.QuestTextList[i];
                if (t == null || !string.IsNullOrEmpty(t.Lang) || string.IsNullOrEmpty(t.TextKey)) continue;
                c.Texts[t.TextKey] = t.Body ?? string.Empty;
            }

            for (var i = 0; i < e.QuestConditionList.Count; i++)
            {
                var node = e.QuestConditionList[i];
                if (node == null || node.Index <= 0) continue;
                c.Conditions[node.Index] = node;
            }

            foreach (var node in c.Conditions.Values)
            {
                if (node.Parent == 0) continue;
                if (!c.ConditionChildren.TryGetValue(node.Parent, out var list)) c.ConditionChildren[node.Parent] = list = new List<QuestConditionInfo>();
                list.Add(node);
            }

            foreach (var list in c.ConditionChildren.Values)
                list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));

            for (var i = 0; i < e.QuestGroupList.Count; i++)
            {
                var g = e.QuestGroupList[i];
                if (g != null && g.Index > 0) c.Groups[g.Index] = g;
            }

            for (var i = 0; i < e.QuestInfoList.Count; i++)
            {
                var info = e.QuestInfoList[i];
                if (info == null || !info.HasDefinition || c.Quests.ContainsKey(info.Index)) continue;

                var q = new QuestRuntime { Info = info, Def = info.Definition };
                var def = info.Definition;

                for (var s = 0; s < def.Steps.Count; s++)
                {
                    var step = def.Steps[s];
                    if (step == null || step.Index <= 0 || q.StepsById.ContainsKey(step.Index)) continue;
                    q.StepsById[step.Index] = step;
                    q.StepsByOrder.Add(step);
                }

                q.StepsByOrder.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));

                for (var o = 0; o < def.Objectives.Count; o++)
                {
                    var obj = def.Objectives[o];
                    if (obj == null || obj.Index <= 0 || !q.StepsById.ContainsKey(obj.Step) || q.ObjectivesById.ContainsKey(obj.Index)) continue;
                    q.ObjectivesById[obj.Index] = obj;
                    if (!q.ObjectivesByStep.TryGetValue(obj.Step, out var list)) q.ObjectivesByStep[obj.Step] = list = new List<QuestObjectiveInfo>();
                    list.Add(obj);
                }

                foreach (var list in q.ObjectivesByStep.Values)
                    list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));

                for (var b = 0; b < def.Branches.Count; b++)
                {
                    var br = def.Branches[b];
                    if (br == null || !q.StepsById.ContainsKey(br.Step)) continue;
                    if (!q.BranchesByStep.TryGetValue(br.Step, out var list)) q.BranchesByStep[br.Step] = list = new List<QuestBranchInfo>();
                    list.Add(br);
                }

                if (q.StepsByOrder.Count == 0) continue;
                c.Quests[info.Index] = q;

                for (var p = 0; p < def.Endpoints.Count; p++)
                {
                    var ep = def.Endpoints[p];
                    if (ep == null) continue;
                    if (ep.Role == QuestEndpointRole.Finish) q.HasFinishEndpoint = true;
                    if (ep.Kind == QuestEndpointKind.Auto && ep.Role == QuestEndpointRole.Start && !c.AutoStarts.Contains(q)) c.AutoStarts.Add(q);
                    if (ep.Kind != QuestEndpointKind.Npc) continue;
                    AddTo(ep.Role == QuestEndpointRole.Start ? c.NpcStarts : c.NpcFinishes, ep.TargetId, q);
                }

                if (!q.HasFinishEndpoint)
                    for (var p = 0; p < def.Endpoints.Count; p++)
                    {
                        var ep = def.Endpoints[p];
                        if (ep != null && ep.Kind == QuestEndpointKind.Npc && ep.Role == QuestEndpointRole.Start) AddTo(c.NpcFinishes, ep.TargetId, q);
                    }
            }

            return c;
        }

        private static void AddTo(Dictionary<int, List<QuestRuntime>> map, int npc, QuestRuntime q)
        {
            if (!map.TryGetValue(npc, out var list)) map[npc] = list = new List<QuestRuntime>();
            if (!list.Contains(q)) list.Add(q);
        }

        /// <summary>True when the quest has an Npc endpoint of <paramref name="role"/> at <paramref name="npcInfoIndex"/>.</summary>
        public bool IsEndpoint(QuestRuntime q, QuestEndpointRole role, int npcInfoIndex)
        {
            var list = role == QuestEndpointRole.Start ? StartsAt(npcInfoIndex) : FinishesAt(npcInfoIndex);
            for (var i = 0; i < list.Count; i++)
                if (list[i] == q) return true;
            return false;
        }
    }

    /// <summary>
    /// Daily / weekly reset boundaries (Part B "Rules"; shared with future instance lockouts): <c>[Quests] DailyResetHour</c> and
    /// <c>WeeklyResetDay</c>, in UTC like <see cref="Envir.Now"/>. Pure functions of the time passed in.
    /// </summary>
    public static class ResetClock
    {
        /// <summary>The most recent daily boundary at or before <paramref name="now"/>.</summary>
        public static DateTime LastDailyReset(DateTime now) => LastDailyReset(now, Settings.QuestDailyResetHour);

        public static DateTime LastDailyReset(DateTime now, int hour)
        {
            hour = Math.Clamp(hour, 0, 23);
            var boundary = now.Date.AddHours(hour);
            return boundary <= now ? boundary : boundary.AddDays(-1);
        }

        /// <summary>The most recent weekly boundary (WeeklyResetDay at DailyResetHour) at or before <paramref name="now"/>.</summary>
        public static DateTime LastWeeklyReset(DateTime now) => LastWeeklyReset(now, Settings.QuestDailyResetHour, Settings.QuestWeeklyResetDay);

        public static DateTime LastWeeklyReset(DateTime now, int hour, DayOfWeek day)
        {
            var daily = LastDailyReset(now, hour);
            var back = ((int)daily.DayOfWeek - (int)day + 7) % 7;
            return daily.AddDays(-back);
        }

        public static DateTime NextDailyReset(DateTime now) => LastDailyReset(now).AddDays(1);
        public static DateTime NextWeeklyReset(DateTime now) => LastWeeklyReset(now).AddDays(7);
    }

    /// <summary>
    /// One quest event (Part B "Event pipeline"): a value type, so raising allocates nothing. <see cref="Map"/> is the template map
    /// index (Instances rule 1): the map's own index for a normal map, 0 for a generated dungeon.
    /// </summary>
    public readonly struct QuestEvent
    {
        public readonly QuestObjectiveType Type;
        public readonly QuestTargetKind Kind;
        public readonly int TargetId;
        public readonly int Amount;
        public readonly int Map, X, Y;

        public QuestEvent(QuestObjectiveType type, QuestTargetKind kind, int targetId, int amount = 1, int map = 0, int x = 0, int y = 0)
        {
            Type = type;
            Kind = kind;
            TargetId = targetId;
            Amount = amount;
            Map = map;
            X = x;
            Y = y;
        }

        /// <summary>The objective index key: (type, target kind, target id) packed into a long.</summary>
        public static long Key(QuestObjectiveType type, QuestTargetKind kind, int targetId) =>
            ((long)(byte)type << 40) | ((long)(byte)kind << 32) | (uint)targetId;
    }
}
