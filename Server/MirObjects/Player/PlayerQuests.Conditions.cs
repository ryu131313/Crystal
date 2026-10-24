using Server.MirEnvir;
using Server.MirEnvir.Quests;

namespace Server.MirObjects
{
    public sealed partial class PlayerQuests
    {
        private const int MaxConditionDepth = 32;

        /// <summary>
        /// Evaluates a condition tree root for this player (Part B "conditions"): 0 = always true; a missing node = false (fail closed).
        /// And over no children = true, Or over no children = false (the importer's "no class allowed" case), Not = negation of its
        /// first child. Leaves read live player state; no allocation.
        /// </summary>
        public bool EvaluateCondition(int condition) => condition == 0 || Evaluate(condition, 0);

        private bool Evaluate(int id, int depth)
        {
            if (depth > MaxConditionDepth || !Catalog.Conditions.TryGetValue(id, out var node)) return false;

            switch (node.Op)
            {
                case QuestConditionOp.And:
                {
                    var children = Catalog.ChildrenOf(id);
                    for (var i = 0; i < children.Count; i++)
                        if (!Evaluate(children[i].Index, depth + 1)) return false;
                    return true;
                }
                case QuestConditionOp.Or:
                {
                    var children = Catalog.ChildrenOf(id);
                    for (var i = 0; i < children.Count; i++)
                        if (Evaluate(children[i].Index, depth + 1)) return true;
                    return false;
                }
                case QuestConditionOp.Not:
                {
                    var children = Catalog.ChildrenOf(id);
                    return children.Count == 0 || !Evaluate(children[0].Index, depth + 1);
                }
                case QuestConditionOp.Leaf:
                    return EvaluateLeaf(node);
                default:
                    return false;
            }
        }

        /// <summary>
        /// One leaf. Player-value leaves compare the player's value with Arg1 (Level, Class, Gender, PartySize, TimeOfDay, DayOfWeek,
        /// MapIs, GuildRank); quest leaves name the quest in Arg1 and compare with Arg2 (QuestStatus as a <see cref="QuestRunStatus"/>,
        /// QuestCompletedCount, QuestOutcome); ItemCount names the item in Arg1 and compares the inventory count with Arg2; QuestVar
        /// names the variable in Arg1 (any quest's run) and compares with Arg2; Flag / InParty / Married / HasHero / Guild /
        /// Equipped are true-or-false (Cmp Ne inverts). Reputation has no model yet (Part B open decision 4): false.
        /// </summary>
        private bool EvaluateLeaf(QuestConditionInfo c)
        {
            var p = _player;
            switch (c.Leaf)
            {
                case QuestConditionLeaf.Level: return Compare(p.Level, c.Cmp, c.Arg1);
                case QuestConditionLeaf.Class: return Compare((int)p.Class, c.Cmp, c.Arg1);
                case QuestConditionLeaf.Gender: return Compare((int)p.Gender, c.Cmp, c.Arg1);
                case QuestConditionLeaf.QuestStatus: return Compare((int)QuestStatus(c.Arg1), c.Cmp, c.Arg2);
                case QuestConditionLeaf.QuestCompletedCount: return Compare(TimesCompleted(c.Arg1), c.Cmp, c.Arg2);
                case QuestConditionLeaf.QuestOutcome:
                {
                    var run = GetRun(c.Arg1);
                    var outcome = run != null && run.Branch != 0 ? run.Branch : _history.TryGetValue(c.Arg1, out var h) ? h.LastOutcome : 0;
                    return Compare(outcome, c.Cmp, c.Arg2);
                }
                case QuestConditionLeaf.ItemCount: return Compare(CountInventory(c.Arg1), c.Cmp, c.Arg2);
                case QuestConditionLeaf.Equipped: return Truth(IsEquipped(c.Arg1), c.Cmp);
                case QuestConditionLeaf.Guild: return Truth(p.MyGuild != null, c.Cmp);
                case QuestConditionLeaf.GuildRank:
                    return p.MyGuild != null && p.MyGuildRank != null && Compare(p.MyGuildRank.Index, c.Cmp, c.Arg1);
                case QuestConditionLeaf.InParty: return Truth(p.GroupMembers != null, c.Cmp);
                case QuestConditionLeaf.PartySize: return Compare(p.GroupMembers?.Count ?? 0, c.Cmp, c.Arg1);
                case QuestConditionLeaf.Flag:
                    return Truth(c.Arg1 >= 0 && c.Arg1 < Info.Flags.Length && Info.Flags[c.Arg1], c.Cmp);
                case QuestConditionLeaf.QuestVar: return Compare(AnyVarValue(c.Arg1), c.Cmp, c.Arg2);
                case QuestConditionLeaf.TimeOfDay: return Compare(Envir.Now.Hour, c.Cmp, c.Arg1);
                case QuestConditionLeaf.DayOfWeek: return Compare((int)Envir.Now.DayOfWeek, c.Cmp, c.Arg1);
                case QuestConditionLeaf.MapIs: return Compare(TemplateMapIndex(), c.Cmp, c.Arg1);
                case QuestConditionLeaf.Married: return Truth(Info.Married != 0, c.Cmp);
                case QuestConditionLeaf.HasHero: return Truth(HasHero(), c.Cmp);
                default: return false;
            }
        }

        public static bool Compare(int value, QuestCompare cmp, int target)
        {
            switch (cmp)
            {
                case QuestCompare.Ne: return value != target;
                case QuestCompare.Lt: return value < target;
                case QuestCompare.Le: return value <= target;
                case QuestCompare.Gt: return value > target;
                case QuestCompare.Ge: return value >= target;
                default: return value == target;
            }
        }

        private static bool Truth(bool value, QuestCompare cmp) => cmp == QuestCompare.Ne ? !value : value;

        private int CountInventory(int itemIndex)
        {
            var total = 0;
            var grid = Info.Inventory;
            for (var i = 0; i < grid.Length; i++)
                if (grid[i]?.Info != null && grid[i].Info.Index == itemIndex) total += grid[i].Count;
            return total;
        }

        private bool IsEquipped(int itemIndex)
        {
            var grid = Info.Equipment;
            for (var i = 0; i < grid.Length; i++)
                if (grid[i]?.Info != null && grid[i].Info.Index == itemIndex) return true;
            return false;
        }

        private int AnyVarValue(int key)
        {
            var vars = Info.QuestVars;
            for (var i = 0; i < vars.Count; i++)
                if (vars[i].Key == key) return vars[i].Value;
            return 0;
        }

        private bool HasHero()
        {
            var heroes = Info.Heroes;
            if (heroes == null) return false;
            for (var i = 0; i < heroes.Length; i++)
                if (heroes[i] != null) return true;
            return false;
        }

        /// <summary>The template map index of the player's map (Instances rule 1): a generated dungeon map is 0.</summary>
        public int TemplateMapIndex()
        {
            var info = _player.CurrentMap?.Info;
            if (info == null || info.Index >= DungeonSystem.IndexBase) return 0;
            return info.Index;
        }

    }
}
