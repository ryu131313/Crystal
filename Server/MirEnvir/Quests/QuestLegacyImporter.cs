using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Server.MirDatabase;
using Server.MirEnvir.Storage;

namespace Server.MirEnvir.Quests
{
    /// <summary>
    /// Builds the quest V2 definition rows from the legacy quests (UserData-and-Quests-Plan.md, Part B phase 1, "Importer field mapping"):
    /// each legacy quest becomes one step; kill tasks → Kill objectives on non-overlapping MonsterGroups; item tasks → Collect; flag tasks
    /// → Flag; carry items → Accept rewards; rewards → questrewards ((M)/(F) pairs → one reward each with a Gender condition); level,
    /// required quest and class → an accept condition tree; NPC script [QUESTS] sections and NPCInfo.Collect/FinishQuestIndexes → Npc
    /// endpoints (NPCInfo.Index); texts → texts rows keyed q&lt;Index&gt;.&lt;field&gt;.
    /// <para>The legacy bodies are parsed here, against the envir being imported, exactly as <see cref="QuestInfo.ParseFile"/> and
    /// <c>NPCScript.ParseQuests</c> parse them at runtime (QuestInfo.LoadInfo resolves names against Envir.Main, which is empty while Edit
    /// loads, so Edit's parsed task lists cannot be used).</para>
    /// <para>Idempotent: steps, objectives and rewards keep their ids through <c>LegacySource</c>, texts through their key, groups through
    /// their AuthorKey and owned conditions through their order, so a second import over unchanged legacy data gives identical rows and
    /// counters.</para>
    /// <para>Legacy quests only (wave 3): a quest is imported when it has a legacy body file name (<see cref="IsLegacy"/>); quests made in
    /// the Quests editor are never touched, nor their owned conditions, texts or groups. In a legacy quest the importer rebuilds only what it
    /// derives from the legacy data (rows with a <c>LegacySource</c>, the legacy-derived quest fields, the accept condition, its q&lt;n&gt;.
    /// texts and q&lt;n&gt;.kill groups) and keeps what an editor added (rows without a LegacySource, branches, variables, the conditions,
    /// texts and groups those use, and the quest fields the legacy data has no value for).</para>
    /// <para>Threads: the caller's (a worker thread holding EditLock for Envir.Edit, or a test/tool envir). Never the game thread.</para>
    /// </summary>
    internal static class QuestLegacyImporter
    {
        private static readonly Regex MessageRegex = new Regex("\"([^\"]*)\"");
        private static readonly Regex OwnedKey = new Regex(@"^q(\d+)\.", RegexOptions.CultureInvariant);

        /// <summary>
        /// The importer's version (wave 3), recorded in SystemData.db (meta <see cref="QuestImportRunner.VersionMetaKey"/>) with every import.
        /// A world start whose store holds an older version re-imports the legacy quests once (<see cref="QuestImportRunner"/>). Raise it
        /// when the importer's output changes for the same legacy data. 1 = the D1 importer (never recorded); 2 = legacy quests only, editor
        /// rows kept, and the RequiredQuest leaf fix (lane F).
        /// </summary>
        internal const int Version = 2;

        /// <summary>A quest the importer owns: it has a legacy body file name. Quests made in the Quests editor have none.</summary>
        internal static bool IsLegacy(QuestInfo q) => q != null && !string.IsNullOrWhiteSpace(q.FileName);

        /// <summary>The quest a q&lt;n&gt;. key belongs to (0 = not such a key).</summary>
        private static int KeyQuest(string key)
        {
            var m = OwnedKey.Match(key ?? string.Empty);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        private static readonly Regex FlagClear = new Regex(@"^\s*SET\s+\[(\d+)\]\s+0+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private const string DescriptionKey = "[@DESCRIPTION]", TaskDescriptionKey = "[@TASKDESCRIPTION]", ReturnDescriptionKey = "[@RETURNDESCRIPTION]",
            CompletionKey = "[@COMPLETION]", CarryItemsKey = "[@CARRYITEMS]", KillTasksKey = "[@KILLTASKS]", ItemTasksKey = "[@ITEMTASKS]",
            FlagTasksKey = "[@FLAGTASKS]", FixedRewardsKey = "[@FIXEDREWARDS]", SelectRewardsKey = "[@SELECTREWARDS]", ExpRewardKey = "[@EXPREWARD]",
            GoldRewardKey = "[@GOLDREWARD]", CreditRewardKey = "[@CREDITREWARD]";

        /// <summary>The header order of QuestInfo.ParseFile (it scans the body once per header, in this order).</summary>
        private static readonly string[] Headers =
        {
            DescriptionKey, TaskDescriptionKey, CompletionKey, CarryItemsKey, KillTasksKey, ItemTasksKey, FlagTasksKey, FixedRewardsKey,
            SelectRewardsKey, ExpRewardKey, GoldRewardKey, CreditRewardKey, ReturnDescriptionKey,
        };

        // ------------------------------------------------------------------ legacy body

        private sealed class LegacyBody
        {
            public bool Found;
            public readonly List<string> Description = new List<string>(), TaskDescription = new List<string>(), ReturnDescription = new List<string>(),
                CompletionDescription = new List<string>();
            public readonly List<(string Section, string Line)> Carry = new List<(string, string)>(), Kill = new List<(string, string)>(),
                Item = new List<(string, string)>(), Flag = new List<(string, string)>(), Fixed = new List<(string, string)>(), Select = new List<(string, string)>();
            public uint Exp, Gold, Credit;
        }

        /// <summary>As QuestInfo.LoadInfo + ParseFile, without resolving names.</summary>
        private static LegacyBody ReadBody(QuestInfo q)
        {
            var body = new LegacyBody();
            if (!Directory.Exists(Settings.QuestPath) && !DesignFiles.IsDesignFolder(Settings.QuestPath)) return body;
            var fileName = Path.Combine(Settings.QuestPath, q.FileName + ".txt");
            if (!DesignFiles.Exists(fileName)) return body;
            body.Found = true;
            var lines = DesignFiles.ReadAllLines(fileName);

            foreach (var header in Headers)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].ToUpper();
                    if (line != header.ToUpper()) continue;

                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var inner = lines[j];
                        if (inner.StartsWith("[") || inner.StartsWith("//")) break;
                        if (string.IsNullOrEmpty(inner)) continue;

                        switch (line)
                        {
                            case DescriptionKey: body.Description.Add(inner); break;
                            case TaskDescriptionKey: body.TaskDescription.Add(inner); break;
                            case ReturnDescriptionKey: body.ReturnDescription.Add(inner); break;
                            case CompletionKey: body.CompletionDescription.Add(inner); break;
                            case CarryItemsKey: body.Carry.Add((line, inner)); break;
                            case KillTasksKey: body.Kill.Add((line, inner)); break;
                            case ItemTasksKey: body.Item.Add((line, inner)); break;
                            case FlagTasksKey: body.Flag.Add((line, inner)); break;
                            case FixedRewardsKey: body.Fixed.Add((line, inner)); break;
                            case SelectRewardsKey: body.Select.Add((line, inner)); break;
                            case ExpRewardKey: uint.TryParse(inner, out body.Exp); break;
                            case GoldRewardKey: uint.TryParse(inner, out body.Gold); break;
                            case CreditRewardKey: uint.TryParse(inner, out body.Credit); break;
                        }
                    }
                }
            }

            return body;
        }

        private static string Message(string line)
        {
            var match = MessageRegex.Match(line);
            return match.Success ? match.Groups[1].Captures[0].Value : string.Empty;
        }

        // ------------------------------------------------------------------ NPC scripts

        private sealed class ScriptQuests
        {
            public bool Found;
            public readonly List<int> Ids = new List<int>();        // signed: positive = start, negative = finish (file order)
            public int CutShortAt;                                    // the unknown quest id that ended the section (0 = none)
            public int IgnoredSections;                               // [QUESTS] sections after the first (the loader stops at the first)
            public string Problem;                                    // a missing #INCLUDE / #INSERT file
        }

        /// <summary>As NPCScript.LoadInfo (ParseInsert, ParseInclude) + ParseQuests, with quest ids checked against <paramref name="quests"/>.</summary>
        private static ScriptQuests ReadScriptQuests(string scriptName, HashSet<int> quests)
        {
            var result = new ScriptQuests();
            if (!Directory.Exists(Settings.NPCPath)) return result;
            var fileName = Path.Combine(Settings.NPCPath, scriptName + ".txt");
            if (!File.Exists(fileName)) return result;
            result.Found = true;

            var lines = File.ReadAllLines(fileName).ToList();
            lines = ParseInsert(lines, result);
            lines = ParseInclude(lines, result);

            var sections = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].ToUpper().StartsWith("[QUESTS]")) continue;
                sections++;
                if (sections > 1) { result.IgnoredSections++; continue; }

                while (++i < lines.Count)
                {
                    if (lines[i].StartsWith("[")) { i--; break; }
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    int.TryParse(lines[i], out var index);
                    if (index == 0) continue;
                    if (!quests.Contains(Math.Abs(index))) { result.CutShortAt = index; break; }
                    result.Ids.Add(index);
                }
            }

            return result;
        }

        private static List<string> ParseInsert(List<string> lines, ScriptQuests result)
        {
            var newLines = new List<string>(); // kept across lines, as the loader does
            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].ToUpper().StartsWith("#INSERT")) continue;
                var split = lines[i].Split(' ');
                if (split.Length < 2) continue;
                var path = Path.Combine(Settings.EnvirPath, split[1].Substring(1, split[1].Length - 2));
                if (!File.Exists(path)) result.Problem ??= "#INSERT file not found: " + path;
                else newLines = File.ReadAllLines(path).ToList();
                lines.AddRange(newLines);
            }

            lines.RemoveAll(str => str.ToUpper().StartsWith("#INSERT"));
            return lines;
        }

        private static List<string> ParseInclude(List<string> lines, ScriptQuests result)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].ToUpper().StartsWith("#INCLUDE")) continue;
                var split = lines[i].Split(' ');
                if (split.Length < 3 || split[1].Length < 2) { result.Problem ??= "malformed #INCLUDE line: " + lines[i]; continue; }
                var path = Path.Combine(Settings.EnvirPath, split[1].Substring(1, split[1].Length - 2));
                var page = ("[" + split[2] + "]").ToUpper();
                bool start = false, finish = false;
                var parsed = new List<string>();

                if (!File.Exists(path))
                {
                    result.Problem ??= "#INCLUDE file not found (the loader then drops the whole script): " + path;
                    return parsed; // as NPCScript.ParseInclude
                }

                var ext = File.ReadAllLines(path);
                for (var j = 0; j < ext.Length; j++)
                {
                    if (!ext[j].ToUpper().StartsWith(page)) continue;
                    for (var x = j + 1; x < ext.Length; x++)
                    {
                        if (ext[x].Trim() == "{") { start = true; continue; }
                        if (ext[x].Trim() == "}") { finish = true; break; }
                        parsed.Add(ext[x]);
                    }
                }

                if (start && finish) lines.InsertRange(i + 1, parsed);
            }

            lines.RemoveAll(str => str.ToUpper().StartsWith("#INCLUDE"));
            return lines;
        }

        // ------------------------------------------------------------------ import

        /// <summary>Id pools of the previous import, so a re-import keeps every id.</summary>
        private sealed class Previous
        {
            public readonly Dictionary<string, int> Texts = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Groups = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<int, Queue<int>> Conditions = new Dictionary<int, Queue<int>>();
            /// <summary>Owned condition nodes of legacy quests that rows the importer keeps still use (never rebuilt or dropped).</summary>
            public readonly HashSet<int> KeptConditions = new HashSet<int>();
            /// <summary>Text keys used by rows the importer keeps (editor rows, editor-made quests, storylines).</summary>
            public readonly HashSet<string> KeptTexts = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Groups used by rows the importer keeps.</summary>
            public readonly HashSet<int> KeptGroups = new HashSet<int>();
        }

        private sealed class Builder
        {
            public Envir E;
            public Previous Prev;
            public QuestImportReport Report;
            public readonly List<QuestTextInfo> Texts = new List<QuestTextInfo>();
            public readonly List<QuestGroupInfo> Groups = new List<QuestGroupInfo>();
            public readonly List<QuestConditionInfo> Conditions = new List<QuestConditionInfo>();
            public readonly HashSet<string> TextKeys = new HashSet<string>(StringComparer.Ordinal);

            public string Text(string key, string body)
            {
                if (!TextKeys.Add(key)) throw new InvalidOperationException("[Quests] text key " + key + " built twice");
                var index = Prev.Texts.TryGetValue(key, out var old) ? old : ++E.QuestTextIndex;
                Texts.Add(new QuestTextInfo { Index = index, TextKey = key, Lang = string.Empty, Body = body ?? string.Empty });
                return key;
            }

            public int Condition(int quest, QuestConditionOp op, int parent, int order, QuestConditionLeaf leaf = QuestConditionLeaf.None, int arg1 = 0,
                int arg2 = 0, QuestCompare cmp = QuestCompare.Eq)
            {
                var index = Prev.Conditions.TryGetValue(quest, out var pool) && pool.Count > 0 ? pool.Dequeue() : ++E.QuestConditionIndex;
                Conditions.Add(new QuestConditionInfo
                {
                    Index = index, Op = op, Parent = parent, Order = order, Leaf = leaf, Arg1 = arg1, Arg2 = arg2, Cmp = cmp, OwnerQuest = quest,
                });
                return index;
            }
        }

        /// <summary>
        /// Imports every legacy quest of <paramref name="e"/> into its V2 tables (replacing what an earlier import built) and returns the
        /// dry-run report. Caller holds EditLock when <paramref name="e"/> is Envir.Edit; nothing is saved here.
        /// </summary>
        public static QuestImportReport Import(Envir e)
        {
            var legacy = new HashSet<int>();
            foreach (var q in e.QuestInfoList)
                if (IsLegacy(q)) legacy.Add(q.Index);

            var report = new QuestImportReport { Quests = legacy.Count };
            report.ImportedQuests.AddRange(legacy.OrderBy(i => i));
            var prev = new Previous();
            CollectKept(e, legacy, prev);

            foreach (var t in e.QuestTextList)
                if (legacy.Contains(KeyQuest(t.TextKey))) prev.Texts.TryAdd(t.TextKey, t.Index);
            foreach (var g in e.QuestGroupList)
                if (legacy.Contains(KeyQuest(g.AuthorKey))) prev.Groups.TryAdd(g.AuthorKey, g.Index);
            foreach (var c in e.QuestConditionList.Where(c => legacy.Contains(c.OwnerQuest) && !prev.KeptConditions.Contains(c.Index)).OrderBy(c => c.Index))
            {
                if (!prev.Conditions.TryGetValue(c.OwnerQuest, out var pool)) prev.Conditions[c.OwnerQuest] = pool = new Queue<int>();
                pool.Enqueue(c.Index);
            }

            var b = new Builder { E = e, Prev = prev, Report = report };
            var questIds = new HashSet<int>(e.QuestInfoList.Where(q => q != null).Select(q => q.Index));
            var endpoints = CollectEndpoints(e, questIds, report);

            foreach (var q in e.QuestInfoList)
            {
                if (q == null || !legacy.Contains(q.Index)) continue;
                ImportQuest(b, q, endpoints.TryGetValue(q.Index, out var eps) ? eps : new List<QuestEndpointInfo>());
            }

            // A rebuilt row takes its old self's place (same key / index), so a re-import over unchanged data keeps every row where it was;
            // rows the importer owns and no longer builds are dropped unless a kept row uses them; every other row stays as it is.
            Merge(e.QuestTextList, b.Texts, x => x.TextKey ?? string.Empty,
                x => legacy.Contains(KeyQuest(x.TextKey)) && !prev.KeptTexts.Contains(x.TextKey));
            Merge(e.QuestGroupList, b.Groups, g => g.Index, g => legacy.Contains(KeyQuest(g.AuthorKey)) && !prev.KeptGroups.Contains(g.Index));
            Merge(e.QuestConditionList, b.Conditions, c => c.Index, c => legacy.Contains(c.OwnerQuest) && !prev.KeptConditions.Contains(c.Index));

            ScanFlagClears(report);
            report.Problems = QuestDefinitions.Validate(e);
            return report;
        }

        /// <summary>
        /// What the import must keep: in legacy quests the rows an editor added (no LegacySource), branches, variables and the quest's own
        /// visible condition, with every condition tree, text and group those use; everything editor-made quests and storylines use.
        /// </summary>
        private static void CollectKept(Envir e, HashSet<int> legacy, Previous prev)
        {
            var roots = new List<int>();
            foreach (var q in e.QuestInfoList)
            {
                if (q == null) continue;
                var d = q.Definition;
                if (!legacy.Contains(q.Index))
                {
                    AddKeys(prev.KeptTexts, d.NameKey, d.SummaryKey, d.CompleteKey);
                    foreach (var s in d.Steps) AddKeys(prev.KeptTexts, s.TitleKey, s.TextKey, s.ReadyTextKey);
                    foreach (var o in d.Objectives) { AddKeys(prev.KeptTexts, o.TextKey); AddGroup(prev.KeptGroups, o); }
                    foreach (var br in d.Branches) AddKeys(prev.KeptTexts, br.TextKey, br.ChoiceKey);
                    continue;
                }

                roots.Add(d.VisibleCondition);
                foreach (var s in d.Steps)
                {
                    if (!string.IsNullOrEmpty(s.LegacySource)) continue;
                    AddKeys(prev.KeptTexts, s.TitleKey, s.TextKey, s.ReadyTextKey);
                    roots.Add(s.FailCondition);
                }

                foreach (var o in d.Objectives)
                {
                    if (!string.IsNullOrEmpty(o.LegacySource)) continue;
                    AddKeys(prev.KeptTexts, o.TextKey);
                    AddGroup(prev.KeptGroups, o);
                    roots.Add(o.Condition);
                }

                foreach (var r in d.Rewards) if (string.IsNullOrEmpty(r.LegacySource)) roots.Add(r.Condition);
                foreach (var br in d.Branches) { AddKeys(prev.KeptTexts, br.TextKey, br.ChoiceKey); roots.Add(br.Condition); }
            }

            foreach (var s in e.StorylineList) AddKeys(prev.KeptTexts, s.NameKey);

            // The kept roots' whole trees (children by Parent).
            var children = new Dictionary<int, List<int>>();
            foreach (var c in e.QuestConditionList)
            {
                if (c.Parent == 0) continue;
                if (!children.TryGetValue(c.Parent, out var list)) children[c.Parent] = list = new List<int>();
                list.Add(c.Index);
            }

            var stack = new Stack<int>(roots.Where(r => r > 0));
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!prev.KeptConditions.Add(id)) continue;
                if (children.TryGetValue(id, out var list)) foreach (var child in list) stack.Push(child);
            }
        }

        /// <summary>
        /// Rebuilt rows replace the old row with the same key in place; old rows <paramref name="drop"/> selects (owned, no longer built)
        /// go; new keys are appended in build order.
        /// </summary>
        private static void Merge<T, TKey>(List<T> list, List<T> built, Func<T, TKey> key, Func<T, bool> drop)
        {
            var byKey = new Dictionary<TKey, T>();
            foreach (var row in built) byKey.TryAdd(key(row), row);
            var placed = new HashSet<TKey>();
            var result = new List<T>(list.Count + built.Count);
            foreach (var row in list)
            {
                var k = key(row);
                if (byKey.TryGetValue(k, out var rebuilt)) { if (placed.Add(k)) result.Add(rebuilt); continue; }
                if (!drop(row)) result.Add(row);
            }

            foreach (var row in built)
                if (placed.Add(key(row))) result.Add(row);

            list.Clear();
            list.AddRange(result);
        }

        private static void AddKeys(HashSet<string> set, params string[] keys)
        {
            foreach (var k in keys) if (!string.IsNullOrEmpty(k)) set.Add(k);
        }

        private static void AddGroup(HashSet<int> set, QuestObjectiveInfo o)
        {
            if (o.TargetKind == QuestTargetKind.MonsterGroup || o.TargetKind == QuestTargetKind.ItemGroup) set.Add(o.TargetId);
        }

        private static void ImportQuest(Builder b, QuestInfo q, List<QuestEndpointInfo> endpoints)
        {
            var e = b.E;
            var report = b.Report;
            var id = q.Index;
            var prefix = "q" + id.ToString(CultureInfo.InvariantCulture);
            var body = ReadBody(q);
            if (!body.Found) report.MissingBodies.Add(id + " " + q.Name + " (" + q.FileName + ")");

            var oldSteps = q.Definition.Steps.Where(s => !string.IsNullOrEmpty(s.LegacySource)).ToDictionary(s => s.LegacySource, s => s.Index, StringComparer.Ordinal);
            var oldObjectives = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in q.Definition.Objectives) if (!string.IsNullOrEmpty(o.LegacySource)) oldObjectives.TryAdd(o.LegacySource, o.Index);
            var oldLegacyStep = q.Definition.Steps.FirstOrDefault(s => s.LegacySource == "step");
            var firstImport = !q.HasDefinition;

            // Rows an editor added to this legacy quest (no LegacySource): kept as they are, after the imported ones.
            var keptSteps = q.Definition.Steps.Where(s => string.IsNullOrEmpty(s.LegacySource)).ToList();
            var keptObjectives = q.Definition.Objectives.Where(o => string.IsNullOrEmpty(o.LegacySource)).ToList();
            var keptRewards = q.Definition.Rewards.Where(r => string.IsNullOrEmpty(r.LegacySource)).ToList();
            var keptEndpoints = q.Definition.Endpoints.Where(x => string.IsNullOrEmpty(x.LegacySource)).ToList();

            // Quest row: the legacy-derived fields are rebuilt; the fields the legacy data has no value for are set on the first import
            // only, so an editor's values (share mode, flags, cooldown, ...) survive a re-import.
            if (string.IsNullOrWhiteSpace(q.Definition.AuthorKey)) q.Definition.AuthorKey = prefix;
            q.Definition.NameKey = b.Text(prefix + ".name", q.Name);
            q.Definition.SummaryKey = body.Description.Count > 0 ? b.Text(prefix + ".summary", string.Join("\n", body.Description)) : string.Empty;
            q.Definition.CompleteKey = body.CompletionDescription.Count > 0 ? b.Text(prefix + ".complete", string.Join("\n", body.CompletionDescription)) : string.Empty;
            switch (q.Type)
            {
                case QuestType.Daily: q.Definition.Category = QuestCategory.Daily; q.Definition.RepeatPolicy = QuestRepeatPolicy.Daily; break;
                case QuestType.Repeatable: q.Definition.Category = QuestCategory.Repeatable; q.Definition.RepeatPolicy = QuestRepeatPolicy.Unlimited; break;
                case QuestType.Story: q.Definition.Category = QuestCategory.Story; q.Definition.RepeatPolicy = QuestRepeatPolicy.Once; break;
                default: q.Definition.Category = QuestCategory.Side; q.Definition.RepeatPolicy = QuestRepeatPolicy.Once; break;
            }

            q.Definition.LevelHint = q.RequiredMinLevel;
            q.Definition.TimeLimitSeconds = q.TimeLimitInSeconds;
            q.Definition.GroupName = q.Group ?? string.Empty;
            if (firstImport)
            {
                q.Definition.StorylineIndex = 0;
                q.Definition.StorylineOrder = 0;
                q.Definition.VisibleCondition = 0;
                q.Definition.CooldownSeconds = 0;
                q.Definition.MaxCompletions = 0;
                q.Definition.ShareMode = QuestShareMode.Offer;
                q.Definition.CreditMode = QuestCreditMode.PartyInRange;
                q.Definition.FailPolicy = QuestFailPolicy.Abandon;
                q.Definition.AbandonAllowed = true;
                q.Definition.PoolIndex = 0;
                q.Definition.StartItemConsumed = false;
                q.Definition.Flags = QuestFlags.None;
            }
            // Revision / ShapeHash are kept: QuestDefinitions.PrepareForSave bumps them when the shape changed.

            // Link labels of the classic dialog (no step column for them: referenced by key, see the plan's dry-run report section).
            if (!string.IsNullOrEmpty(q.GotoMessage)) b.Text(prefix + ".goto", q.GotoMessage);
            if (!string.IsNullOrEmpty(q.KillMessage)) b.Text(prefix + ".kill", q.KillMessage);
            if (!string.IsNullOrEmpty(q.ItemMessage)) b.Text(prefix + ".item", q.ItemMessage);
            if (!string.IsNullOrEmpty(q.FlagMessage)) b.Text(prefix + ".flag", q.FlagMessage);

            // The one step.
            const string stepSource = "step";
            var step = new QuestStepInfo
            {
                Index = oldSteps.TryGetValue(stepSource, out var oldStep) ? oldStep : ++e.QuestStepIndex,
                Order = oldLegacyStep?.Order ?? 1,
                CompletionMode = QuestStepCompletion.All,
                // An editor may have chained steps of its own after the imported one: keep that link while its target is kept.
                NextStep = oldLegacyStep != null && keptSteps.Any(s => s.Index == oldLegacyStep.NextStep) ? oldLegacyStep.NextStep : -1,
                LegacySource = stepSource,
            };
            step.TextKey = body.TaskDescription.Count > 0 ? b.Text(prefix + ".task", string.Join("\n", body.TaskDescription)) : string.Empty;
            step.ReadyTextKey = body.ReturnDescription.Count > 0 ? b.Text(prefix + ".ready", string.Join("\n", body.ReturnDescription)) : string.Empty;
            q.Definition.Steps.Clear();
            q.Definition.Steps.Add(step);
            q.Definition.Steps.AddRange(keptSteps);

            // Objectives.
            var objectives = new List<QuestObjectiveInfo>();
            var sources = new HashSet<string>(StringComparer.Ordinal);
            string Source(string section, string target)
            {
                var s = section + " " + target;
                var n = 1;
                var unique = s;
                while (!sources.Add(unique)) unique = s + " #" + (++n).ToString(CultureInfo.InvariantCulture);
                return unique;
            }

            QuestObjectiveInfo Objective(string source, QuestObjectiveType type, QuestTargetKind kind, int target, int count, string message)
            {
                var o = new QuestObjectiveInfo
                {
                    Index = oldObjectives.TryGetValue(source, out var old) ? old : ++e.QuestObjectiveIndex,
                    Step = step.Index,
                    Order = objectives.Count + 1,
                    Type = type,
                    PartyCredit = QuestPartyCredit.Each,
                    TargetKind = kind,
                    TargetId = target,
                    Count = count,
                    LegacySource = source,
                };
                if (!string.IsNullOrEmpty(message)) o.TextKey = b.Text(prefix + ".obj" + o.Index.ToString(CultureInfo.InvariantCulture), message);
                objectives.Add(o);
                return o;
            }

            // Kill tasks: one MonsterGroup per task, a monster only in the first task (file order) whose name prefix matches it (ProcessKill).
            var claimed = new Dictionary<int, string>(); // monster index → the task that has it
            var taskMonsters = new HashSet<int>();
            foreach (var (section, line) in body.Kill)
            {
                if (line.Length < 1) continue;
                var split = line.Split(' ');
                var count = 1;
                var monster = e.GetMonsterInfo(split[0]);
                if (split.Length > 1) int.TryParse(split[1], out count);
                if (monster == null) { report.Unparsed.Add(id + " " + section + " \"" + line + "\": no monster named " + split[0]); continue; }
                if (!taskMonsters.Add(monster.Index)) report.DuplicateTasks.Add(id + " " + q.Name + ": two kill tasks on " + monster.Name);

                var source = Source(KillTasksKey, monster.Name);
                var groupKey = prefix + ".kill." + monster.Name + (source.Contains(" #", StringComparison.Ordinal) ? source.Substring(source.LastIndexOf(" #", StringComparison.Ordinal)) : string.Empty);
                var group = new QuestGroupInfo
                {
                    Index = b.Prev.Groups.TryGetValue(groupKey, out var oldGroup) ? oldGroup : ++e.QuestGroupIndex,
                    AuthorKey = groupKey,
                    Kind = QuestGroupKind.Monster,
                };
                foreach (var m in e.MonsterInfoList)
                {
                    if (m == null || m.Name == null || !m.Name.StartsWith(monster.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (claimed.TryGetValue(m.Index, out var owner))
                    {
                        report.OverlappingGroups.Add(id + " " + q.Name + ": " + m.Name + " matches kill tasks " + owner + " and " + monster.Name + " (counts for the first only)");
                        continue;
                    }

                    claimed[m.Index] = monster.Name;
                    group.Members.Add(new QuestGroupMemberInfo { TargetId = m.Index });
                }

                if (group.Members.Count == 0) report.EmptyGroups.Add(id + " " + q.Name + ": kill task " + monster.Name + " (every match is taken by an earlier task)");
                b.Groups.Add(group);
                Objective(source, QuestObjectiveType.Kill, QuestTargetKind.MonsterGroup, group.Index, count, Message(line));
            }

            var taskItems = new HashSet<int>();
            foreach (var (section, line) in body.Item)
            {
                if (line.Length < 1) continue;
                var split = line.Split(' ');
                ushort count = 1;
                var item = e.GetItemInfo(split[0]);
                if (split.Length > 1) ushort.TryParse(split[1], out count);
                if (item == null) { report.Unparsed.Add(id + " " + section + " \"" + line + "\": no item named " + split[0]); continue; }
                if (!taskItems.Add(item.Index)) report.DuplicateTasks.Add(id + " " + q.Name + ": two item tasks on " + item.Name);
                Objective(Source(ItemTasksKey, item.Name), QuestObjectiveType.Collect, QuestTargetKind.Item, item.Index, count, Message(line));
            }

            foreach (var (section, line) in body.Flag)
            {
                if (line.Length < 1) continue;
                var split = line.Split(' ');
                int.TryParse(split[0], out var number);
                if (number < 0 || number > QuestDefinitions.MaxFlagNumber)
                {
                    report.Unparsed.Add(id + " " + section + " \"" + line + "\": flag " + number + " is outside 0.." + QuestDefinitions.MaxFlagNumber);
                    continue;
                }

                if (!int.TryParse(split[0], out _)) report.Unparsed.Add(id + " " + section + " \"" + line + "\": not a number, read as flag 0 (as the legacy parser does)");
                report.AddFlag(number, id);
                Objective(Source(FlagTasksKey, number.ToString(CultureInfo.InvariantCulture)), QuestObjectiveType.Flag, QuestTargetKind.Flag, number, 1, Message(line));
            }

            q.Definition.Objectives.Clear();
            q.Definition.Objectives.AddRange(objectives);
            q.Definition.Objectives.AddRange(keptObjectives.Where(o => o.Step == step.Index || keptSteps.Any(s => s.Index == o.Step)));

            // Rewards (no ids: kept in a fixed order, LegacySource names the line).
            var rewards = new List<QuestRewardInfo>();
            var rewardSources = new HashSet<string>(StringComparer.Ordinal);
            int maleCondition = 0, femaleCondition = 0;
            void Reward(QuestRewardScope scope, QuestRewardKind kind, int target, long amount, int choiceGroup, int condition, string source)
            {
                var unique = source;
                var n = 1;
                while (!rewardSources.Add(unique)) unique = source + " #" + (++n).ToString(CultureInfo.InvariantCulture);
                rewards.Add(new QuestRewardInfo
                {
                    Scope = scope, Kind = kind, TargetId = target, Amount = amount, Scaling = QuestRewardScaling.Flat, ChoiceGroup = choiceGroup,
                    Condition = condition, LegacySource = unique,
                });
            }

            foreach (var (section, line) in body.Carry)
            {
                if (line.Length < 1) continue;
                var split = line.Split(' ');
                ushort count = 1;
                var item = e.GetItemInfo(split[0]);
                if (split.Length > 1) ushort.TryParse(split[1], out count);
                if (item == null) { report.Unparsed.Add(id + " " + section + " \"" + line + "\": no item named " + split[0]); continue; }
                Reward(QuestRewardScope.Accept, QuestRewardKind.Item, item.Index, count, 0, 0, CarryItemsKey + " " + item.Name);
            }

            void ItemRewards(List<(string Section, string Line)> lines, int choiceGroup)
            {
                foreach (var (section, line) in lines)
                {
                    if (line.Length < 1) continue;
                    var split = line.Split(' ');
                    ushort count = 1;
                    if (split.Length > 1) ushort.TryParse(split[1], out count);
                    var item = e.GetItemInfo(split[0]);
                    if (item != null)
                    {
                        Reward(QuestRewardScope.Quest, QuestRewardKind.Item, item.Index, count, choiceGroup, 0, section + " " + item.Name);
                        continue;
                    }

                    var male = e.GetItemInfo(split[0] + "(M)");
                    var female = e.GetItemInfo(split[0] + "(F)");
                    if (male == null && female == null) { report.Unparsed.Add(id + " " + section + " \"" + line + "\": no item named " + split[0] + " (nor (M)/(F))"); continue; }
                    var pair = male != null && female != null;
                    if (pair)
                    {
                        // Deliberate fix: legacy gives both; each now carries a Gender condition, one condition node per gender per quest.
                        report.GenderPairs.Add(id + " " + q.Name + ": " + split[0]);
                        if (maleCondition == 0) maleCondition = b.Condition(id, QuestConditionOp.Leaf, 0, 0, QuestConditionLeaf.Gender, (int)MirGender.Male);
                        if (femaleCondition == 0) femaleCondition = b.Condition(id, QuestConditionOp.Leaf, 0, 0, QuestConditionLeaf.Gender, (int)MirGender.Female);
                    }

                    if (male != null) Reward(QuestRewardScope.Quest, QuestRewardKind.Item, male.Index, count, choiceGroup, pair ? maleCondition : 0, section + " " + male.Name);
                    if (female != null) Reward(QuestRewardScope.Quest, QuestRewardKind.Item, female.Index, count, choiceGroup, pair ? femaleCondition : 0, section + " " + female.Name);
                }
            }

            ItemRewards(body.Fixed, 0);
            ItemRewards(body.Select, 1);
            if (body.Exp > 0) Reward(QuestRewardScope.Quest, QuestRewardKind.Exp, 0, body.Exp, 0, 0, ExpRewardKey);
            if (body.Gold > 0) Reward(QuestRewardScope.Quest, QuestRewardKind.Gold, 0, body.Gold, 0, 0, GoldRewardKey);
            if (body.Credit > 0) Reward(QuestRewardScope.Quest, QuestRewardKind.Credit, 0, body.Credit, 0, 0, CreditRewardKey);
            q.Definition.Rewards.Clear();
            q.Definition.Rewards.AddRange(rewards);
            q.Definition.Rewards.AddRange(keptRewards);

            // Accept condition: level range, required quest, class (as QuestInfo.CanAccept).
            var leaves = new List<(QuestConditionLeaf Leaf, int Arg1, int Arg2, QuestCompare Cmp)>();
            if (q.RequiredMinLevel > 0) leaves.Add((QuestConditionLeaf.Level, q.RequiredMinLevel, 0, QuestCompare.Ge));
            if (q.RequiredMaxLevel < ushort.MaxValue) leaves.Add((QuestConditionLeaf.Level, q.RequiredMaxLevel, 0, QuestCompare.Le));
            if (q.RequiredQuest > 0) leaves.Add((QuestConditionLeaf.QuestCompletedCount, q.RequiredQuest, 1, QuestCompare.Ge)); // completed at least once (Arg1 = quest, Arg2 = times)
            var classes = new List<MirClass>();
            foreach (MirClass c in Enum.GetValues(typeof(MirClass)))
                if (((byte)q.RequiredClass & (1 << (int)c)) != 0) classes.Add(c);
            var allClasses = classes.Count == Enum.GetValues(typeof(MirClass)).Length;
            if (!allClasses && classes.Count != 1)
            {
                if (classes.Count == 0) report.Unparsed.Add(id + " " + q.Name + ": RequiredClass allows no class (nobody can accept it; imported as an empty Or)");
            }
            else if (!allClasses) leaves.Add((QuestConditionLeaf.Class, (int)classes[0], 0, QuestCompare.Eq));

            var classOr = !allClasses && classes.Count != 1;
            var parts = leaves.Count + (classOr ? 1 : 0);
            q.Definition.AcceptCondition = 0;
            if (parts > 0)
            {
                // One part: it is the root. Several: an And root over them, in this order.
                var root = parts > 1 ? b.Condition(id, QuestConditionOp.And, 0, 0) : 0;
                var parent = root;
                var order = 0;
                foreach (var (leaf, arg1, arg2, cmp) in leaves)
                {
                    var node = b.Condition(id, QuestConditionOp.Leaf, parent, order++, leaf, arg1, arg2, cmp);
                    if (root == 0) root = node;
                }

                if (classOr)
                {
                    var or = b.Condition(id, QuestConditionOp.Or, parent, order);
                    if (root == 0) root = or;
                    var o = 0;
                    foreach (var c in classes) b.Condition(id, QuestConditionOp.Leaf, or, o++, QuestConditionLeaf.Class, (int)c);
                }

                q.Definition.AcceptCondition = root;
            }

            // Endpoints.
            q.Definition.Endpoints.Clear();
            q.Definition.Endpoints.AddRange(endpoints);
            foreach (var kept in keptEndpoints)
                if (!endpoints.Any(x => x.Role == kept.Role && x.Kind == kept.Kind && x.TargetId == kept.TargetId && x.Map == kept.Map))
                    q.Definition.Endpoints.Add(kept);
            var starts = endpoints.Count(x => x.Role == QuestEndpointRole.Start);
            if (starts == 0) report.NoGiver.Add(id + " " + q.Name);
            else if (starts > 1)
                report.MultiGiver.Add(id + " " + q.Name + ": " + string.Join(", ", endpoints.Where(x => x.Role == QuestEndpointRole.Start).Select(x => "NPC " + x.TargetId)));

            // Branches and variables are never imported (no legacy data maps to them): whatever an editor made stays.
        }

        /// <summary>Npc endpoints per quest from every NPCInfo row's script [QUESTS] section, then NPCInfo.Collect/FinishQuestIndexes.</summary>
        private static Dictionary<int, List<QuestEndpointInfo>> CollectEndpoints(Envir e, HashSet<int> quests, QuestImportReport report)
        {
            var result = new Dictionary<int, List<QuestEndpointInfo>>();
            var scripts = new Dictionary<string, ScriptQuests>(StringComparer.OrdinalIgnoreCase);
            ScriptQuests Script(string name)
            {
                if (!scripts.TryGetValue(name ?? string.Empty, out var s))
                {
                    s = ReadScriptQuests(name ?? string.Empty, quests);
                    scripts[name ?? string.Empty] = s;
                    if (s.CutShortAt != 0) report.CutShort.Add(name + ": stops at unknown quest " + s.CutShortAt);
                    if (s.IgnoredSections > 0) report.IgnoredQuestSections.Add(name + ": " + s.IgnoredSections + " extra [QUESTS] section(s), ignored by the loader");
                    if (s.Problem != null) report.ScriptProblems.Add(name + ": " + s.Problem);
                }

                return s;
            }

            void Add(int quest, QuestEndpointRole role, int npc, string source)
            {
                if (!result.TryGetValue(quest, out var list)) result[quest] = list = new List<QuestEndpointInfo>();
                if (list.Any(x => x.Role == role && x.Kind == QuestEndpointKind.Npc && x.TargetId == npc)) return;
                list.Add(new QuestEndpointInfo { Role = role, Kind = QuestEndpointKind.Npc, TargetId = npc, LegacySource = source });
            }

            foreach (var npc in e.NPCInfoList)
            {
                if (npc == null) continue;
                var s = Script(npc.FileName);
                if (!s.Found) { report.MissingScripts.Add("NPC " + npc.Index + " " + npc.Name + " (" + npc.FileName + ")"); continue; }
                foreach (var signed in s.Ids)
                    Add(Math.Abs(signed), signed > 0 ? QuestEndpointRole.Start : QuestEndpointRole.Finish, npc.Index, "script " + npc.FileName);
            }

            foreach (var npc in e.NPCInfoList)
            {
                if (npc == null) continue;
                foreach (var quest in npc.CollectQuestIndexes)
                    if (quests.Contains(quest)) Add(quest, QuestEndpointRole.Start, npc.Index, "NPCInfo.CollectQuestIndexes");
                foreach (var quest in npc.FinishQuestIndexes)
                    if (quests.Contains(quest)) Add(quest, QuestEndpointRole.Finish, npc.Index, "NPCInfo.FinishQuestIndexes");
            }

            // Runtime NPCs with no NPCInfo row (conquest flags and control points): reported, never endpoints.
            foreach (var c in e.ConquestInfoList)
            {
                if (c == null) continue;
                foreach (var flag in c.ConquestFlags.Concat(c.ControlPoints))
                {
                    var s = Script(flag.FileName);
                    if (s.Found && s.Ids.Count > 0)
                        report.NoNpcInfo.Add("conquest " + c.Index + " flag/control point " + flag.Name + " (" + flag.FileName + "): quests " + string.Join(", ", s.Ids));
                }
            }

            return result;
        }

        /// <summary>Which [@FLAGTASKS] flags a script ever clears (SET [n] 0): a V2 Flag objective is met only while the flag is set.</summary>
        private static void ScanFlagClears(QuestImportReport report)
        {
            if (report.Flags.Count == 0 || !Directory.Exists(Settings.NPCPath)) return;
            foreach (var path in Directory.EnumerateFiles(Settings.NPCPath, "*.txt", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string[] lines;
                try { lines = File.ReadAllLines(path); }
                catch (IOException) { continue; }
                foreach (var line in lines)
                {
                    var m = FlagClear.Match(line);
                    if (!m.Success || !int.TryParse(m.Groups[1].Value, out var flag) || !report.Flags.ContainsKey(flag)) continue;
                    var name = Path.GetRelativePath(Settings.NPCPath, path);
                    if (!report.FlagClearedBy.TryGetValue(flag, out var list)) report.FlagClearedBy[flag] = list = new List<string>();
                    if (!list.Contains(name)) list.Add(name);
                }
            }
        }
    }

    /// <summary>The importer's dry-run report (Part B phase 1): counts and short lists, plus the validation result.</summary>
    internal sealed class QuestImportReport
    {
        /// <summary>Legacy quests imported (quests with a legacy body file name; editor-made quests are not counted).</summary>
        public int Quests;
        /// <summary>Their indexes, ascending (the live quest replace after an import).</summary>
        public readonly List<int> ImportedQuests = new List<int>();
        public readonly List<string> MissingBodies = new List<string>();
        public readonly List<string> Unparsed = new List<string>();
        public readonly List<string> CutShort = new List<string>();
        public readonly List<string> IgnoredQuestSections = new List<string>();
        public readonly List<string> ScriptProblems = new List<string>();
        public readonly List<string> MissingScripts = new List<string>();
        public readonly List<string> MultiGiver = new List<string>();
        public readonly List<string> NoGiver = new List<string>();
        public readonly List<string> EmptyGroups = new List<string>();
        public readonly List<string> OverlappingGroups = new List<string>();
        public readonly List<string> DuplicateTasks = new List<string>();
        public readonly List<string> GenderPairs = new List<string>();
        public readonly List<string> NoNpcInfo = new List<string>();
        /// <summary>Flag number → quests whose [@FLAGTASKS] use it.</summary>
        public readonly SortedDictionary<int, List<int>> Flags = new SortedDictionary<int, List<int>>();
        /// <summary>Flag number → NPC script files that clear it (SET [n] 0).</summary>
        public readonly SortedDictionary<int, List<string>> FlagClearedBy = new SortedDictionary<int, List<string>>();
        public List<QuestValidationProblem> Problems = new List<QuestValidationProblem>();

        public int Errors => Problems.Count(p => !p.Warning);
        public int Warnings => Problems.Count(p => p.Warning);

        internal void AddFlag(int flag, int quest)
        {
            if (!Flags.TryGetValue(flag, out var list)) Flags[flag] = list = new List<int>();
            if (!list.Contains(quest)) list.Add(quest);
        }

        /// <summary>One log line.</summary>
        public string Summary() =>
            "[Quests] Imported " + Quests + " legacy quest(s): " + Unparsed.Count + " unparsable line(s), " + NoGiver.Count + " without a giver, " +
            MultiGiver.Count + " with several givers, " + CutShort.Count + " cut-short [QUESTS] section(s), " + Errors + " validation error(s), " +
            Warnings + " warning(s).";

        /// <summary>A Markdown summary (counts plus the first <paramref name="max"/> entries of each list).</summary>
        public string ToMarkdown(int max = 10)
        {
            var sb = new StringBuilder();
            void List(string title, IReadOnlyCollection<string> items)
            {
                sb.Append("- **").Append(title).Append(":** ").Append(items.Count.ToString(CultureInfo.InvariantCulture));
                if (items.Count > 0)
                {
                    sb.Append(" (").Append(string.Join("; ", items.Take(max)));
                    if (items.Count > max) sb.Append("; … ").Append((items.Count - max).ToString(CultureInfo.InvariantCulture)).Append(" more");
                    sb.Append(')');
                }

                sb.AppendLine();
            }

            sb.Append("- **Quests:** ").Append(Quests.ToString(CultureInfo.InvariantCulture)).AppendLine();
            List("Quests with no body file", MissingBodies);
            List("Legacy lines that fail to parse today (dropped silently by the legacy loader)", Unparsed);
            List("[QUESTS] sections cut short by an unknown quest id", CutShort);
            List("Extra [QUESTS] sections the loader ignores", IgnoredQuestSections);
            List("Script include problems", ScriptProblems);
            List("NPCInfo rows whose script is missing", MissingScripts);
            List("Quests with several would-be givers (last script wins today)", MultiGiver);
            List("Quests no NPC gives", NoGiver);
            List("Kill tasks with an empty group", EmptyGroups);
            List("Overlapping kill groups (monster counted for the first task only)", OverlappingGroups);
            List("Duplicate tasks on one monster or item", DuplicateTasks);
            List("Quests with (M)/(F) reward pairs (now one reward each with a Gender condition)", GenderPairs);
            var flags = Flags.Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + " (quest " + string.Join(", ", p.Value) + ")").ToList();
            List("Flags used by [@FLAGTASKS]", flags);
            var cleared = FlagClearedBy.Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + " by " + string.Join(", ", p.Value)).ToList();
            List("…of which a script clears (SET [n] 0)", cleared);
            List("Runtime NPCs with no NPCInfo row that give quests", NoNpcInfo);
            List("Validation errors", Problems.Where(p => !p.Warning).Select(p => p.ToString()).ToList());
            List("Validation warnings", Problems.Where(p => p.Warning).Select(p => p.ToString()).ToList());
            return sb.ToString();
        }
    }
}
