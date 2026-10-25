using log4net;
using Server.MirDatabase;
using Server.MirObjects;

namespace Server.MirEnvir.Quests
{
    /// <summary>
    /// Plain data copy of the quest V2 definitions a quest replace needs (UserData-and-Quests-Plan.md, Part B "Live edits", step 2): the
    /// changed quests' whole graphs and the shared top-level tables, every row a fresh object. Taken from Envir.Edit under EditLock on the
    /// caller's thread; handed to the game thread, which makes these objects Main's (nothing in it is ever referenced by Edit).
    /// </summary>
    internal sealed class QuestReplaceSnapshot
    {
        /// <summary>One changed quest: its row values and graph, or <see cref="Deleted"/> when Edit no longer has it.</summary>
        internal sealed class Quest
        {
            public int Index;
            public bool Deleted;
            public string Name = string.Empty, Group = string.Empty, FileName = string.Empty;
            public QuestType Type;
            public int RequiredMinLevel, RequiredMaxLevel, RequiredQuest, TimeLimitInSeconds;
            public RequiredClass RequiredClass;
            public string GotoMessage = string.Empty, KillMessage = string.Empty, ItemMessage = string.Empty, FlagMessage = string.Empty;
            public QuestDefinition Definition;
            /// <summary>The condition nodes this quest owns (OwnerQuest = Index).</summary>
            public readonly List<QuestConditionInfo> OwnedConditions = new List<QuestConditionInfo>();
        }

        public readonly List<Quest> Quests = new List<Quest>();
        /// <summary>Shared condition nodes (OwnerQuest = 0).</summary>
        public readonly List<QuestConditionInfo> SharedConditions = new List<QuestConditionInfo>();
        public readonly List<QuestTextInfo> Texts = new List<QuestTextInfo>();
        public readonly List<QuestGroupInfo> Groups = new List<QuestGroupInfo>();
        public readonly List<StorylineInfo> Storylines = new List<StorylineInfo>();
        public readonly List<QuestPoolInfo> Pools = new List<QuestPoolInfo>();

        /// <summary>Who asked (log lines): "Quests page", "quest import", ...</summary>
        public string Source = string.Empty;
    }

    /// <summary>What a quest replace did (reported back to the web admin caller).</summary>
    internal sealed class QuestReplaceResult
    {
        /// <summary>Quests whose new graph is now Main's (created or changed).</summary>
        public readonly List<int> Replaced = new List<int>();
        /// <summary>Quests removed from Main (deleted in Edit): online runs dropped, history kept.</summary>
        public readonly List<int> Deleted = new List<int>();
        /// <summary>Quests kept as they were in Main, with the reason (a missing reference, or the whole batch rolled back).</summary>
        public readonly List<(int Quest, string Reason)> Failed = new List<(int, string)>();
        /// <summary>Every quest online players were refreshed for (replaced, deleted, and those using a changed shared row).</summary>
        public readonly List<int> Affected = new List<int>();
        public bool RolledBack;
        public int Players;
        public int PlayerErrors;

        public string Summary()
        {
            var s = "replaced " + Replaced.Count + ", deleted " + Deleted.Count + ", failed " + Failed.Count + ", refreshed " + Affected.Count +
                    " quest(s) for " + Players + " online player(s)";
            if (RolledBack) s += " (rolled back)";
            if (PlayerErrors > 0) s += ", " + PlayerErrors + " player error(s), see the server log";
            return s;
        }
    }

    /// <summary>
    /// The LiveDb quest-replace hook's engine side (UserData-and-Quests-Plan.md, Part B "Live edits"; server.md §2.7). <see cref="Take"/>
    /// copies the changed quests from Envir.Edit (caller holds EditLock); <see cref="Apply"/> runs on the game thread (an admin action):
    /// resolves every referenced monster / item / NPC / map against Main (a missing id keeps that quest's old graph), swaps the fresh
    /// graphs into Main's quest lists in one step with the old contents as before-images (any exception restores them all), rebuilds the
    /// quest catalog, and refreshes every online player (revision migration, index, sent definitions, availability).
    /// <para>Never shares an Edit object with Main: the snapshot rows are copies. Allocation is fine here (an operator action, not a tick).</para>
    /// </summary>
    internal static class QuestReplace
    {
        private static readonly ILog QuestLog = Logger.GetLogger(LogType.Quests);

        // ------------------------------------------------------------------ snapshot (EditLock, caller's thread)

        /// <summary>
        /// Copies <paramref name="quests"/> (a quest missing from <paramref name="source"/> is recorded as deleted) and the shared tables
        /// (shared conditions, texts, groups, storylines, pools: small, copied whole so Main's shared rows always equal the saved ones).
        /// </summary>
        public static QuestReplaceSnapshot Take(Envir source, IReadOnlyCollection<int> quests, string why)
        {
            var snap = new QuestReplaceSnapshot { Source = why ?? string.Empty };
            var byIndex = new Dictionary<int, QuestInfo>();
            for (var i = 0; i < source.QuestInfoList.Count; i++)
            {
                var q = source.QuestInfoList[i];
                if (q != null) byIndex.TryAdd(q.Index, q);
            }

            var wanted = new HashSet<int>();
            foreach (var index in quests ?? Array.Empty<int>())
            {
                if (index <= 0 || !wanted.Add(index)) continue;
                if (!byIndex.TryGetValue(index, out var q))
                {
                    snap.Quests.Add(new QuestReplaceSnapshot.Quest { Index = index, Deleted = true });
                    continue;
                }

                snap.Quests.Add(new QuestReplaceSnapshot.Quest
                {
                    Index = q.Index,
                    Name = q.Name ?? string.Empty,
                    Group = q.Group ?? string.Empty,
                    FileName = q.FileName ?? string.Empty,
                    Type = q.Type,
                    RequiredMinLevel = q.RequiredMinLevel,
                    RequiredMaxLevel = q.RequiredMaxLevel,
                    RequiredQuest = q.RequiredQuest,
                    RequiredClass = q.RequiredClass,
                    TimeLimitInSeconds = q.TimeLimitInSeconds,
                    GotoMessage = q.GotoMessage ?? string.Empty,
                    KillMessage = q.KillMessage ?? string.Empty,
                    ItemMessage = q.ItemMessage ?? string.Empty,
                    FlagMessage = q.FlagMessage ?? string.Empty,
                    Definition = q.Definition.DeepCopy(),
                });
            }

            for (var i = 0; i < source.QuestConditionList.Count; i++)
            {
                var c = source.QuestConditionList[i];
                if (c == null) continue;
                if (c.OwnerQuest == 0) { snap.SharedConditions.Add(c.Copy()); continue; }
                for (var s = 0; s < snap.Quests.Count; s++)
                    if (!snap.Quests[s].Deleted && snap.Quests[s].Index == c.OwnerQuest) { snap.Quests[s].OwnedConditions.Add(c.Copy()); break; }
            }

            for (var i = 0; i < source.QuestTextList.Count; i++) if (source.QuestTextList[i] != null) snap.Texts.Add(source.QuestTextList[i].Copy());
            for (var i = 0; i < source.QuestGroupList.Count; i++) if (source.QuestGroupList[i] != null) snap.Groups.Add(source.QuestGroupList[i].Copy());
            for (var i = 0; i < source.StorylineList.Count; i++) if (source.StorylineList[i] != null) snap.Storylines.Add(source.StorylineList[i].Copy());
            for (var i = 0; i < source.QuestPoolList.Count; i++) if (source.QuestPoolList[i] != null) snap.Pools.Add(source.QuestPoolList[i].Copy());
            return snap;
        }

        // ------------------------------------------------------------------ apply (game thread)

        /// <summary>Main's quest lists as they were before the swap (contents, plus the row fields the swap overwrites).</summary>
        private sealed class BeforeImage
        {
            public List<QuestInfo> Quests;
            public List<QuestConditionInfo> Conditions;
            public List<QuestTextInfo> Texts;
            public List<QuestGroupInfo> Groups;
            public List<StorylineInfo> Storylines;
            public List<QuestPoolInfo> Pools;
            public readonly List<(QuestInfo Info, QuestDefinition Definition, string Name, string Group)> Rows = new List<(QuestInfo, QuestDefinition, string, string)>();

            public static BeforeImage Take(Envir e) => new BeforeImage
            {
                Quests = new List<QuestInfo>(e.QuestInfoList),
                Conditions = new List<QuestConditionInfo>(e.QuestConditionList),
                Texts = new List<QuestTextInfo>(e.QuestTextList),
                Groups = new List<QuestGroupInfo>(e.QuestGroupList),
                Storylines = new List<StorylineInfo>(e.StorylineList),
                Pools = new List<QuestPoolInfo>(e.QuestPoolList),
            };

            public void Restore(Envir e)
            {
                for (var i = Rows.Count - 1; i >= 0; i--)
                {
                    var r = Rows[i];
                    r.Info.Definition = r.Definition;
                    r.Info.Name = r.Name;
                    r.Info.Group = r.Group;
                }

                Reset(e.QuestInfoList, Quests);
                Reset(e.QuestConditionList, Conditions);
                Reset(e.QuestTextList, Texts);
                Reset(e.QuestGroupList, Groups);
                Reset(e.StorylineList, Storylines);
                Reset(e.QuestPoolList, Pools);
            }
        }

        private static void Reset<T>(List<T> list, List<T> contents)
        {
            list.Clear();
            list.AddRange(contents);
        }

        /// <summary>
        /// Applies <paramref name="snap"/> to <paramref name="main"/> and refreshes <paramref name="players"/> (Envir.Main.Players on the game
        /// thread). <paramref name="failAfterSwap"/> is for tests only (throws after the swap, before the catalog is used, to prove the rollback).
        /// </summary>
        public static QuestReplaceResult Apply(Envir main, QuestReplaceSnapshot snap, IReadOnlyList<PlayerObject> players, Action failAfterSwap = null)
        {
            var result = new QuestReplaceResult();
            var groupsById = new Dictionary<int, QuestGroupInfo>();
            for (var i = 0; i < snap.Groups.Count; i++) groupsById.TryAdd(snap.Groups[i].Index, snap.Groups[i]);
            var refs = new MainIds(main);

            // 1. Resolve references against Main (step 2 of the spec): a missing id keeps that quest's old graph.
            var replace = new List<QuestReplaceSnapshot.Quest>();
            var deleted = new List<QuestReplaceSnapshot.Quest>();
            for (var i = 0; i < snap.Quests.Count; i++)
            {
                var q = snap.Quests[i];
                if (q.Deleted) { deleted.Add(q); continue; }
                var missing = MissingReference(q, refs, groupsById);
                if (missing != null) { result.Failed.Add((q.Index, missing)); continue; }
                replace.Add(q);
            }

            var replacedIds = new HashSet<int>();
            for (var i = 0; i < replace.Count; i++) replacedIds.Add(replace[i].Index);
            var deletedIds = new HashSet<int>();
            for (var i = 0; i < deleted.Count; i++) deletedIds.Add(deleted[i].Index);

            // 2. Swap in one step, keeping the before-image (step 3).
            var before = BeforeImage.Take(main);
            try
            {
                // Quests kept as they are (not in the batch, or failed): their owned conditions, and any text / group row only they still
                // use, stay Main's as they were.
                var conditions = new List<QuestConditionInfo>(main.QuestConditionList.Count);
                for (var i = 0; i < before.Conditions.Count; i++)
                {
                    var c = before.Conditions[i];
                    if (c != null && c.OwnerQuest != 0 && !replacedIds.Contains(c.OwnerQuest) && !deletedIds.Contains(c.OwnerQuest)) conditions.Add(c);
                }

                conditions.AddRange(snap.SharedConditions);
                for (var i = 0; i < replace.Count; i++) conditions.AddRange(replace[i].OwnedConditions);

                var kept = new List<QuestInfo>();
                for (var i = 0; i < before.Quests.Count; i++)
                {
                    var info = before.Quests[i];
                    if (info != null && !replacedIds.Contains(info.Index) && !deletedIds.Contains(info.Index)) kept.Add(info);
                }

                var texts = new List<QuestTextInfo>(snap.Texts);
                CarryOver(texts, before.Texts, KeptTextKeys(kept), t => t.TextKey, t => string.IsNullOrEmpty(t.Lang));
                var groups = new List<QuestGroupInfo>(snap.Groups);
                CarryOver(groups, before.Groups, KeptGroupIds(kept), g => g.Index, g => true);

                for (var i = 0; i < replace.Count; i++)
                {
                    var q = replace[i];
                    var info = Find(main.QuestInfoList, q.Index);
                    if (info != null)
                    {
                        before.Rows.Add((info, info.Definition, info.Name, info.Group));
                        info.Definition = q.Definition;
                        info.Name = q.Name;
                        info.Group = q.Group;
                    }
                    else
                    {
                        // Created in the Quests page: a new Main row (no legacy body: FileName is empty for editor quests).
                        main.QuestInfoList.Add(new QuestInfo
                        {
                            Index = q.Index, Name = q.Name, Group = q.Group, FileName = q.FileName, Type = q.Type,
                            RequiredMinLevel = q.RequiredMinLevel, RequiredMaxLevel = q.RequiredMaxLevel, RequiredQuest = q.RequiredQuest,
                            RequiredClass = q.RequiredClass, TimeLimitInSeconds = q.TimeLimitInSeconds, GotoMessage = q.GotoMessage,
                            KillMessage = q.KillMessage, ItemMessage = q.ItemMessage, FlagMessage = q.FlagMessage, Definition = q.Definition,
                        });
                    }
                }

                for (var i = 0; i < deleted.Count; i++)
                {
                    var info = Find(main.QuestInfoList, deleted[i].Index);
                    if (info != null) main.QuestInfoList.Remove(info);
                }

                Reset(main.QuestConditionList, conditions);
                Reset(main.QuestTextList, texts);
                Reset(main.QuestGroupList, groups);
                Reset(main.StorylineList, snap.Storylines);
                Reset(main.QuestPoolList, snap.Pools);

                failAfterSwap?.Invoke();
                main.InvalidateQuestCatalog();
                _ = main.QuestCatalog; // built now, so a broken graph rolls back here instead of failing in a player's tick
            }
            catch (Exception ex)
            {
                before.Restore(main);
                main.InvalidateQuestCatalog();
                result.RolledBack = true;
                for (var i = 0; i < replace.Count; i++) result.Failed.Add((replace[i].Index, "rolled back: the quest graph could not be swapped in (see the server log)"));
                for (var i = 0; i < deleted.Count; i++) result.Failed.Add((deleted[i].Index, "rolled back: the quest graph could not be swapped in (see the server log)"));
                MessageQueue.Instance.Enqueue("[Quests] Quest replace (" + snap.Source + ") rolled back: " + ex.Message);
                MessageQueue.Instance.Enqueue(ex);
                return result;
            }

            result.Replaced.AddRange(replacedIds.OrderBy(x => x));
            result.Deleted.AddRange(deletedIds.OrderBy(x => x));

            // 3. Every quest online players must be refreshed for: the batch, plus every quest using a changed shared row (the reverse
            //    index, rebuilt from Main's new graph; Part B "Shared top-level tables").
            var affected = new SortedSet<int>(replacedIds);
            affected.UnionWith(deletedIds);
            affected.UnionWith(QuestsUsingChangedSharedRows(main, before, snap));
            result.Affected.AddRange(affected);

            // 4. Online players (steps 4-6): migration, index, sent definitions, availability. One player's failure never undoes the swap.
            var list = new List<int>(affected);
            for (var i = players.Count - 1; i >= 0; i--)
            {
                var p = players[i];
                if (p?.Info == null) continue;
                result.Players++;
                try
                {
                    p.Quests.ApplyDefinitionChange(list);
                }
                catch (Exception ex)
                {
                    result.PlayerErrors++;
                    MessageQueue.Instance.Enqueue("[Quests] Quest replace: refreshing " + p.Name + " failed: " + ex.Message);
                    MessageQueue.Instance.Enqueue(ex);
                }
            }

            for (var i = 0; i < result.Failed.Count; i++)
                QuestLog.Info("[Quests] replace (" + snap.Source + ") kept quest " + result.Failed[i].Quest + ": " + result.Failed[i].Reason);
            QuestLog.Info("[Quests] replace (" + snap.Source + "): " + result.Summary());
            return result;
        }

        private static QuestInfo Find(List<QuestInfo> list, int index)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Index == index) return list[i];
            return null;
        }

        /// <summary>Adds the old rows whose key a kept quest still uses and the new list lacks.</summary>
        private static void CarryOver<T, TKey>(List<T> fresh, List<T> old, HashSet<TKey> keptKeys, Func<T, TKey> key, Func<T, bool> include)
        {
            if (keptKeys.Count == 0) return;
            var present = new HashSet<TKey>();
            for (var i = 0; i < fresh.Count; i++) if (fresh[i] != null && include(fresh[i])) present.Add(key(fresh[i]));
            for (var i = 0; i < old.Count; i++)
            {
                var row = old[i];
                if (row == null || !include(row)) continue;
                var k = key(row);
                if (keptKeys.Contains(k) && present.Add(k)) fresh.Add(row);
            }
        }

        private static HashSet<string> KeptTextKeys(List<QuestInfo> kept)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < kept.Count; i++) AddTextKeys(kept[i].Definition, keys);
            return keys;
        }

        private static HashSet<int> KeptGroupIds(List<QuestInfo> kept)
        {
            var ids = new HashSet<int>();
            for (var i = 0; i < kept.Count; i++) AddGroupIds(kept[i].Definition, ids);
            return ids;
        }

        private static void AddTextKeys(QuestDefinition d, HashSet<string> keys)
        {
            void Add(string k) { if (!string.IsNullOrEmpty(k)) keys.Add(k); }
            Add(d.NameKey); Add(d.SummaryKey); Add(d.CompleteKey);
            foreach (var s in d.Steps) if (s != null) { Add(s.TitleKey); Add(s.TextKey); Add(s.ReadyTextKey); }
            foreach (var o in d.Objectives) if (o != null) Add(o.TextKey);
            foreach (var b in d.Branches) if (b != null) { Add(b.TextKey); Add(b.ChoiceKey); }
        }

        private static void AddGroupIds(QuestDefinition d, HashSet<int> ids)
        {
            foreach (var o in d.Objectives)
                if (o != null && (o.TargetKind == QuestTargetKind.MonsterGroup || o.TargetKind == QuestTargetKind.ItemGroup)) ids.Add(o.TargetId);
        }

        /// <summary>Every condition root a quest references.</summary>
        private static void AddConditionRoots(QuestDefinition d, List<int> roots)
        {
            void Add(int c) { if (c > 0) roots.Add(c); }
            Add(d.AcceptCondition); Add(d.VisibleCondition);
            foreach (var s in d.Steps) if (s != null) Add(s.FailCondition);
            foreach (var o in d.Objectives) if (o != null) Add(o.Condition);
            foreach (var b in d.Branches) if (b != null) Add(b.Condition);
            foreach (var r in d.Rewards) if (r != null) Add(r.Condition);
        }

        // ------------------------------------------------------------------ references

        /// <summary>Main's monster / item / NPC / map ids, read once per replace.</summary>
        private sealed class MainIds
        {
            public readonly HashSet<int> Monsters = new HashSet<int>(), Items = new HashSet<int>(), Npcs = new HashSet<int>(), Maps = new HashSet<int>();

            public MainIds(Envir e)
            {
                for (var i = 0; i < e.MonsterInfoList.Count; i++) if (e.MonsterInfoList[i] != null) Monsters.Add(e.MonsterInfoList[i].Index);
                for (var i = 0; i < e.ItemInfoList.Count; i++) if (e.ItemInfoList[i] != null) Items.Add(e.ItemInfoList[i].Index);
                for (var i = 0; i < e.NPCInfoList.Count; i++) if (e.NPCInfoList[i] != null) Npcs.Add(e.NPCInfoList[i].Index);
                for (var i = 0; i < e.MapInfoList.Count; i++) if (e.MapInfoList[i] != null) Maps.Add(e.MapInfoList[i].Index);
            }
        }

        /// <summary>The first id the quest names that Main does not have, as operator text; null when everything resolves.</summary>
        private static string MissingReference(QuestReplaceSnapshot.Quest q, MainIds ids, Dictionary<int, QuestGroupInfo> groups)
        {
            var d = q.Definition;
            foreach (var o in d.Objectives)
            {
                if (o == null) continue;
                var what = "objective " + o.Index;
                switch (o.TargetKind)
                {
                    case QuestTargetKind.Monster: if (!ids.Monsters.Contains(o.TargetId)) return what + ": monster " + o.TargetId + " is not in the running world"; break;
                    case QuestTargetKind.Item: if (!ids.Items.Contains(o.TargetId)) return what + ": item " + o.TargetId + " is not in the running world"; break;
                    case QuestTargetKind.Npc: if (!ids.Npcs.Contains(o.TargetId)) return what + ": NPC " + o.TargetId + " is not in the running world"; break;
                    case QuestTargetKind.Map: if (!ids.Maps.Contains(o.TargetId)) return what + ": map " + o.TargetId + " is not in the running world"; break;
                    case QuestTargetKind.MonsterGroup:
                    case QuestTargetKind.ItemGroup:
                        if (!groups.TryGetValue(o.TargetId, out var g)) return what + ": target group " + o.TargetId + " does not exist";
                        var monsters = o.TargetKind == QuestTargetKind.MonsterGroup;
                        foreach (var m in g.Members)
                            if (m != null && !(monsters ? ids.Monsters : ids.Items).Contains(m.TargetId))
                                return what + ": group " + g.Index + " member " + (monsters ? "monster " : "item ") + m.TargetId + " is not in the running world";
                        break;
                }

                if (o.Map != 0 && !ids.Maps.Contains(o.Map)) return what + ": map " + o.Map + " is not in the running world";
            }

            foreach (var ep in d.Endpoints)
            {
                if (ep == null) continue;
                switch (ep.Kind)
                {
                    case QuestEndpointKind.Npc: if (!ids.Npcs.Contains(ep.TargetId)) return "endpoint: NPC " + ep.TargetId + " is not in the running world"; break;
                    case QuestEndpointKind.ItemUse: if (!ids.Items.Contains(ep.TargetId)) return "endpoint: item " + ep.TargetId + " is not in the running world"; break;
                    case QuestEndpointKind.AreaEnter: if (ep.Map != 0 && !ids.Maps.Contains(ep.Map)) return "endpoint: map " + ep.Map + " is not in the running world"; break;
                }
            }

            foreach (var r in d.Rewards)
                if (r != null && r.Kind == QuestRewardKind.Item && !ids.Items.Contains(r.TargetId)) return "reward: item " + r.TargetId + " is not in the running world";

            foreach (var c in q.OwnedConditions)
            {
                if (c == null || c.Op != QuestConditionOp.Leaf) continue;
                switch (c.Leaf)
                {
                    case QuestConditionLeaf.ItemCount:
                    case QuestConditionLeaf.Equipped:
                        if (!ids.Items.Contains(c.Arg1)) return "condition " + c.Index + ": item " + c.Arg1 + " is not in the running world";
                        break;
                    case QuestConditionLeaf.MapIs:
                        if (!ids.Maps.Contains(c.Arg1)) return "condition " + c.Index + ": map " + c.Arg1 + " is not in the running world";
                        break;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ reverse index of the shared rows

        /// <summary>
        /// Quests of Main's new graph that use a shared row whose value changed in this replace: a text (by key), a target group, a shared
        /// condition node (anywhere in a referenced tree) or a storyline. Built from Main at each replace, so it is never stale.
        /// </summary>
        private static IEnumerable<int> QuestsUsingChangedSharedRows(Envir main, BeforeImage before, QuestReplaceSnapshot snap)
        {
            var texts = ChangedTexts(before.Texts, main.QuestTextList);
            var groups = Changed(before.Groups, main.QuestGroupList, g => g.Index, SameGroup);
            var conditions = Changed(Shared(before.Conditions), Shared(main.QuestConditionList), c => c.Index, SameCondition);
            var storylines = Changed(before.Storylines, main.StorylineList, s => s.Index, SameStoryline);
            if (texts.Count == 0 && groups.Count == 0 && conditions.Count == 0 && storylines.Count == 0) yield break;

            var children = new Dictionary<int, List<int>>();
            foreach (var c in main.QuestConditionList)
            {
                if (c == null || c.Parent == 0) continue;
                if (!children.TryGetValue(c.Parent, out var list)) children[c.Parent] = list = new List<int>();
                list.Add(c.Index);
            }

            var storylineKeys = new Dictionary<int, string>();
            foreach (var s in main.StorylineList) if (s != null) storylineKeys[s.Index] = s.NameKey;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<int>();
            var roots = new List<int>();
            foreach (var info in main.QuestInfoList)
            {
                if (info == null || !info.HasDefinition) continue;
                var d = info.Definition;
                if (storylines.Contains(d.StorylineIndex)) { yield return info.Index; continue; }

                keys.Clear();
                AddTextKeys(d, keys);
                if (d.StorylineIndex != 0 && storylineKeys.TryGetValue(d.StorylineIndex, out var sk) && !string.IsNullOrEmpty(sk)) keys.Add(sk);
                if (keys.Overlaps(texts)) { yield return info.Index; continue; }

                ids.Clear();
                AddGroupIds(d, ids);
                if (ids.Overlaps(groups)) { yield return info.Index; continue; }

                if (conditions.Count == 0) continue;
                roots.Clear();
                AddConditionRoots(d, roots);
                if (TreeTouches(roots, children, conditions)) yield return info.Index;
            }
        }

        private static bool TreeTouches(List<int> roots, Dictionary<int, List<int>> children, HashSet<int> changed)
        {
            var stack = new Stack<int>(roots);
            var seen = new HashSet<int>();
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue; // a cycle is a validation error; never loop on it
                if (changed.Contains(id)) return true;
                if (children.TryGetValue(id, out var list)) foreach (var c in list) stack.Push(c);
            }

            return false;
        }

        private static List<QuestConditionInfo> Shared(List<QuestConditionInfo> list)
        {
            var shared = new List<QuestConditionInfo>();
            foreach (var c in list) if (c != null && c.OwnerQuest == 0) shared.Add(c);
            return shared;
        }

        private static HashSet<string> ChangedTexts(List<QuestTextInfo> before, List<QuestTextInfo> after)
        {
            var old = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in before) if (t != null && string.IsNullOrEmpty(t.Lang) && !string.IsNullOrEmpty(t.TextKey)) old[t.TextKey] = t.Body ?? string.Empty;
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in after)
            {
                if (t == null || !string.IsNullOrEmpty(t.Lang) || string.IsNullOrEmpty(t.TextKey)) continue;
                seen.Add(t.TextKey);
                if (!old.TryGetValue(t.TextKey, out var body) || body != (t.Body ?? string.Empty)) changed.Add(t.TextKey);
            }

            foreach (var key in old.Keys) if (!seen.Contains(key)) changed.Add(key);
            return changed;
        }

        private static HashSet<int> Changed<T>(List<T> before, List<T> after, Func<T, int> id, Func<T, T, bool> same) where T : class
        {
            var old = new Dictionary<int, T>();
            foreach (var r in before) if (r != null) old[id(r)] = r;
            var changed = new HashSet<int>();
            var seen = new HashSet<int>();
            foreach (var r in after)
            {
                if (r == null) continue;
                seen.Add(id(r));
                if (!old.TryGetValue(id(r), out var o) || !same(o, r)) changed.Add(id(r));
            }

            foreach (var key in old.Keys) if (!seen.Contains(key)) changed.Add(key);
            return changed;
        }

        private static bool SameGroup(QuestGroupInfo a, QuestGroupInfo b)
        {
            if (a.Kind != b.Kind || a.Members.Count != b.Members.Count) return false;
            for (var i = 0; i < a.Members.Count; i++)
                if (a.Members[i]?.TargetId != b.Members[i]?.TargetId) return false;
            return true;
        }

        private static bool SameCondition(QuestConditionInfo a, QuestConditionInfo b) =>
            a.Op == b.Op && a.Parent == b.Parent && a.Order == b.Order && a.Leaf == b.Leaf && a.Arg1 == b.Arg1 && a.Arg2 == b.Arg2 && a.Cmp == b.Cmp &&
            a.OwnerQuest == b.OwnerQuest;

        private static bool SameStoryline(StorylineInfo a, StorylineInfo b) => a.NameKey == b.NameKey && a.Order == b.Order && a.AuthorKey == b.AuthorKey;
    }
}
