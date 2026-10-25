using System.Globalization;
using Server.MirDatabase;
using Server.MirEnvir.Quests;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.Quests
{
    /// <summary>What a "where used" query names.</summary>
    public enum QuestRefKind
    {
        Monster,
        Item,
        Npc,
        Map,
        Group,
        Condition,
        Quest,
        Spell,
    }

    /// <summary>
    /// "Where used" over the quest V2 definitions (UserData-and-Quests-Plan.md, Part B, "Authoring" and Validation, "Deleting a referenced
    /// target"): which quests (objectives, endpoints, rewards, conditions, groups, pools) reference a monster, item, NPC, map, group,
    /// condition, quest or spell. Pure over the lists it is given; callers hold EditLock for Envir.Edit's lists.
    /// </summary>
    internal static class QuestWhereUsed
    {
        public static bool TryParseKind(string text, out QuestRefKind kind) =>
            Enum.TryParse((text ?? string.Empty).Trim(), true, out kind) && Enum.IsDefined(typeof(QuestRefKind), kind);

        public static List<QuestUseDto> Find(IReadOnlyList<QuestInfo> quests, IReadOnlyList<QuestConditionInfo> conditions, IReadOnlyList<QuestGroupInfo> groups,
            IReadOnlyList<QuestPoolInfo> pools, QuestRefKind kind, int id)
        {
            var uses = new List<QuestUseDto>();
            if (id == 0) return uses;

            var labels = new Dictionary<int, string>();
            foreach (var q in quests)
                if (q != null) labels[q.Index] = Label(q);

            void Add(int quest, string where) =>
                uses.Add(new QuestUseDto { Quest = quest, QuestLabel = quest != 0 && labels.TryGetValue(quest, out var l) ? l : null, Where = where });

            string N(int v) => v.ToString(CultureInfo.InvariantCulture);

            // Groups holding the target, and the quests whose objectives use those groups.
            var groupKind = kind == QuestRefKind.Monster ? QuestGroupKind.Monster : kind == QuestRefKind.Item ? QuestGroupKind.Item : (QuestGroupKind?)null;
            var holdingGroups = new HashSet<int>();
            if (groupKind != null)
                foreach (var g in groups)
                    if (g.Kind == groupKind && g.Members.Any(m => m.TargetId == id))
                    {
                        holdingGroups.Add(g.Index);
                        Add(0, "member of quest group " + N(g.Index) + " (" + g.AuthorKey + ")");
                    }

            foreach (var q in quests)
            {
                if (q == null) continue;
                var d = q.Definition;
                var qi = q.Index;

                foreach (var o in d.Objectives)
                {
                    var what = "objective " + N(o.Index) + " (" + o.Type + ")";
                    switch (kind)
                    {
                        case QuestRefKind.Monster when o.TargetKind == QuestTargetKind.Monster && o.TargetId == id:
                        case QuestRefKind.Item when o.TargetKind == QuestTargetKind.Item && o.TargetId == id:
                        case QuestRefKind.Npc when o.TargetKind == QuestTargetKind.Npc && o.TargetId == id:
                        case QuestRefKind.Map when o.TargetKind == QuestTargetKind.Map && o.TargetId == id:
                        case QuestRefKind.Spell when o.TargetKind == QuestTargetKind.Spell && o.TargetId == id:
                        case QuestRefKind.Group when (o.TargetKind == QuestTargetKind.MonsterGroup || o.TargetKind == QuestTargetKind.ItemGroup) && o.TargetId == id:
                        case QuestRefKind.Condition when o.Condition == id:
                            Add(qi, what);
                            break;
                    }

                    if (kind == QuestRefKind.Map && o.Map == id && !(o.TargetKind == QuestTargetKind.Map && o.TargetId == id)) Add(qi, what + " marker");
                    if (groupKind != null && (o.TargetKind == QuestTargetKind.MonsterGroup || o.TargetKind == QuestTargetKind.ItemGroup) && holdingGroups.Contains(o.TargetId))
                        Add(qi, what + " through group " + N(o.TargetId));
                }

                foreach (var ep in d.Endpoints)
                {
                    if ((kind == QuestRefKind.Npc && ep.Kind == QuestEndpointKind.Npc && ep.TargetId == id) ||
                        (kind == QuestRefKind.Item && ep.Kind == QuestEndpointKind.ItemUse && ep.TargetId == id) ||
                        (kind == QuestRefKind.Map && ep.Kind == QuestEndpointKind.AreaEnter && ep.Map == id))
                        Add(qi, ep.Role + " endpoint (" + ep.Kind + ")");
                }

                foreach (var r in d.Rewards)
                {
                    if (kind == QuestRefKind.Item && r.Kind == QuestRewardKind.Item && r.TargetId == id) Add(qi, r.Scope + " reward (item)");
                    if (kind == QuestRefKind.Condition && r.Condition == id) Add(qi, r.Scope + " reward " + r.Kind + " condition");
                }

                if (kind == QuestRefKind.Condition)
                {
                    if (d.AcceptCondition == id) Add(qi, "accept condition");
                    if (d.VisibleCondition == id) Add(qi, "visible condition");
                    foreach (var s in d.Steps)
                        if (s.FailCondition == id) Add(qi, "step " + N(s.Index) + " fail condition");
                    foreach (var b in d.Branches)
                        if (b.Condition == id) Add(qi, "branch of step " + N(b.Step) + " condition");
                }
            }

            // Condition leaves naming the target, and child nodes of a condition.
            foreach (var c in conditions)
            {
                var hit = kind switch
                {
                    QuestRefKind.Item => (c.Leaf == QuestConditionLeaf.ItemCount || c.Leaf == QuestConditionLeaf.Equipped) && c.Arg1 == id,
                    QuestRefKind.Map => c.Leaf == QuestConditionLeaf.MapIs && c.Arg1 == id,
                    QuestRefKind.Quest => (c.Leaf == QuestConditionLeaf.QuestStatus || c.Leaf == QuestConditionLeaf.QuestCompletedCount || c.Leaf == QuestConditionLeaf.QuestOutcome) && c.Arg1 == id,
                    QuestRefKind.Condition => c.Parent == id,
                    _ => false,
                };
                if (!hit || c.Op != QuestConditionOp.Leaf && kind != QuestRefKind.Condition) continue;
                Add(c.OwnerQuest, (c.OwnerQuest == 0 ? "shared condition " : "condition ") + N(c.Index) + (kind == QuestRefKind.Condition ? " (child)" : " (" + c.Leaf + ")"));
            }

            if (kind == QuestRefKind.Quest)
                foreach (var p in pools)
                    if (p.Members.Any(m => m.Quest == id)) Add(0, "quest pool " + N(p.Index) + " (" + p.AuthorKey + ")");

            return uses;
        }

        /// <summary>The quest's display name: its legacy name, else its author key, else "#index".</summary>
        public static string Label(QuestInfo q)
        {
            if (!string.IsNullOrWhiteSpace(q.Name)) return q.Name;
            if (!string.IsNullOrWhiteSpace(q.Definition.AuthorKey)) return q.Definition.AuthorKey;
            return "#" + q.Index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The kind a Game Data endpoint's rows are, for the delete refusal (null = quests never reference that table).</summary>
        public static QuestRefKind? KindOfEndpoint(string endpoint)
        {
            switch (endpoint)
            {
                case "monsters": return QuestRefKind.Monster;
                case "items": return QuestRefKind.Item;
                case "npcs": return QuestRefKind.Npc;
                case "maps": return QuestRefKind.Map;
                case "questgroups": return QuestRefKind.Group;
                case "conditions": return QuestRefKind.Condition;
                case "quests": return QuestRefKind.Quest;
                default: return null;
            }
        }

        /// <summary>Both refusal texts (either may be null); null when neither refuses.</summary>
        public static string Join(string a, string b) => a == null ? b : b == null ? a : a + ". " + b;

        /// <summary>
        /// The generic delete refusal (Game Data grid deletes and LiveDb deletes, both through <see cref="GameDataEngine.Delete"/>): a row
        /// that a quest V2 definition references cannot be deleted; returns the "where used" text, or null when nothing uses it.
        /// </summary>
        public static string DeleteGuard(GameDataContext ctx, string endpoint, int index)
        {
            var kind = KindOfEndpoint(endpoint);
            if (kind == null || ctx == null) return null;
            var uses = Find(ctx.Quests, ctx.QuestConditions, ctx.QuestGroups, ctx.QuestPools, kind.Value, index);
            if (kind == QuestRefKind.Quest) uses.RemoveAll(u => u.Quest == index); // its own rows go with it
            if (uses.Count == 0) return null;
            var shown = uses.Take(8).Select(u => (u.Quest != 0 ? "quest " + u.Quest.ToString(CultureInfo.InvariantCulture) + " " + u.QuestLabel + ": " : string.Empty) + u.Where).ToList();
            return "Used by quest definitions (fix them in the Quests page first): " + string.Join("; ", shown) + (uses.Count > shown.Count ? "; and " + (uses.Count - shown.Count).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty);
        }
    }
}
