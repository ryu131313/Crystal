using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.Quests
{
    /// <summary>
    /// The web admin Quests editor (UserData-and-Quests-Plan.md, Part B, "Authoring"): reads and saves whole quest graphs on a
    /// <see cref="QuestWorld"/> (Envir.Edit in production), allocating new ids from the counters (never reused), with validation that
    /// <b>blocks</b> the save for the quest it changes (every error is returned; other save paths stay report-only). Also the target
    /// groups, "where used" and the per-quest JSON export / import (preview diff, then apply).
    /// <para>Threads: a web admin worker holding <see cref="WebAdminGame.EditLock"/> (<see cref="QuestEditorGate"/>), never the game
    /// thread. Main is never touched: a saved quest reaches the running world only through <see cref="QuestEditNotifier"/>.</para>
    /// </summary>
    internal sealed class QuestEditorService
    {
        public const string ExportFormat = "crystal-quest/1";

        private readonly QuestWorld _w;
        private readonly Action _persist;

        /// <param name="world">The lists and counters edited (Envir.Edit's view in production).</param>
        /// <param name="persist">Writes the world to disk after a successful change (GameDataSave.SaveEdit; null = read only).</param>
        public QuestEditorService(QuestWorld world, Action persist)
        {
            _w = world ?? throw new ArgumentNullException(nameof(world));
            _persist = persist;
        }

        private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

        // ================================================================== read

        public QuestInfo Find(int index) => index <= 0 ? null : _w.QuestInfoList.FirstOrDefault(q => q != null && q.Index == index);

        /// <summary>The quest list with each quest's validation counts (one validation pass).</summary>
        public List<object> List()
        {
            var problems = QuestDefinitions.Validate(_w);
            var errors = problems.Where(p => !p.Warning && p.Quest != 0).GroupBy(p => p.Quest).ToDictionary(g => g.Key, g => g.Count());
            var warnings = problems.Where(p => p.Warning && p.Quest != 0).GroupBy(p => p.Quest).ToDictionary(g => g.Key, g => g.Count());
            var texts = TextMap();
            return _w.QuestInfoList.Where(q => q != null).OrderBy(q => q.Index).Select(q => (object)new
            {
                index = q.Index,
                name = DisplayName(q, texts),
                authorKey = q.Definition.AuthorKey,
                category = (int)q.Definition.Category,
                groupName = string.IsNullOrEmpty(q.Definition.GroupName) ? q.Group : q.Definition.GroupName,
                storylineIndex = q.Definition.StorylineIndex,
                hasDefinition = q.HasDefinition,
                legacy = !string.IsNullOrWhiteSpace(q.FileName),
                steps = q.Definition.Steps.Count,
                objectives = q.Definition.Objectives.Count,
                errors = errors.TryGetValue(q.Index, out var e) ? e : 0,
                warnings = warnings.TryGetValue(q.Index, out var w) ? w : 0,
            }).ToList();
        }

        private Dictionary<string, string> TextMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in _w.QuestTextList)
                if (string.IsNullOrEmpty(t.Lang) && !string.IsNullOrEmpty(t.TextKey)) map.TryAdd(t.TextKey, t.Body ?? string.Empty);
            return map;
        }

        private static string DisplayName(QuestInfo q, Dictionary<string, string> texts)
        {
            if (!string.IsNullOrEmpty(q.Definition.NameKey) && texts.TryGetValue(q.Definition.NameKey, out var body) && !string.IsNullOrWhiteSpace(body))
                return FirstLine(body);
            return QuestWhereUsed.Label(q);
        }

        private static string FirstLine(string s)
        {
            s = (s ?? string.Empty).Replace("\r", string.Empty);
            var i = s.IndexOf('\n');
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        /// <summary>The whole graph of one quest (null when it does not exist).</summary>
        public QuestGraphDto Read(int index)
        {
            var q = Find(index);
            if (q == null) return null;
            var dto = ToDto(q);
            dto.Rev = RevOf(dto);
            return dto;
        }

        private QuestGraphDto ToDto(QuestInfo q)
        {
            var d = q.Definition;
            var dto = new QuestGraphDto
            {
                Index = q.Index,
                Name = q.Name,
                Legacy = !string.IsNullOrWhiteSpace(q.FileName),
                Revision = d.Revision,
                ShapeHash = N(d.ShapeHash),
                AuthorKey = d.AuthorKey,
                NameKey = d.NameKey,
                SummaryKey = d.SummaryKey,
                CompleteKey = d.CompleteKey,
                Category = (int)d.Category,
                StorylineIndex = d.StorylineIndex,
                StorylineOrder = d.StorylineOrder,
                LevelHint = d.LevelHint,
                AcceptCondition = d.AcceptCondition,
                VisibleCondition = d.VisibleCondition,
                RepeatPolicy = (int)d.RepeatPolicy,
                CooldownSeconds = d.CooldownSeconds,
                MaxCompletions = d.MaxCompletions,
                ShareMode = (int)d.ShareMode,
                CreditMode = (int)d.CreditMode,
                TimeLimitSeconds = d.TimeLimitSeconds,
                FailPolicy = (int)d.FailPolicy,
                AbandonAllowed = d.AbandonAllowed,
                PoolIndex = d.PoolIndex,
                StartItemConsumed = d.StartItemConsumed,
                Flags = (int)d.Flags,
                GroupName = d.GroupName,
                Steps = d.Steps.Select(s => new QuestStepDto
                {
                    Index = s.Index, Order = s.Order, TitleKey = s.TitleKey, TextKey = s.TextKey, ReadyTextKey = s.ReadyTextKey, CompletionMode = (int)s.CompletionMode,
                    NRequired = s.NRequired, NextStep = s.NextStep, TimeLimitSeconds = s.TimeLimitSeconds, FailCondition = s.FailCondition, OnEnterPage = s.OnEnterPage,
                    OnCompletePage = s.OnCompletePage, LegacySource = s.LegacySource,
                }).ToList(),
                Objectives = d.Objectives.Select(o => new QuestObjectiveDto
                {
                    Index = o.Index, Step = o.Step, Order = o.Order, Type = (int)o.Type, PartyCredit = (int)o.PartyCredit, TargetKind = (int)o.TargetKind, TargetId = o.TargetId,
                    Count = o.Count, Map = o.Map, X = o.X, Y = o.Y, Radius = o.Radius, Optional = o.Optional, Hidden = o.Hidden, Condition = o.Condition, TextKey = o.TextKey,
                    DropChance = o.DropChance, Params = o.Params, LegacySource = o.LegacySource,
                }).ToList(),
                Branches = d.Branches.Select(b => new QuestBranchDto
                {
                    Step = b.Step, ChoiceKey = b.ChoiceKey, TextKey = b.TextKey, NextStep = b.NextStep, Outcome = b.Outcome, Condition = b.Condition,
                }).ToList(),
                Endpoints = d.Endpoints.Select(ep => new QuestEndpointDto
                {
                    Role = (int)ep.Role, Kind = (int)ep.Kind, TargetId = ep.TargetId, Map = ep.Map, LegacySource = ep.LegacySource,
                }).ToList(),
                Rewards = d.Rewards.Select(r => new QuestRewardDto
                {
                    Scope = (int)r.Scope, ScopeId = r.ScopeId, Kind = (int)r.Kind, TargetId = r.TargetId, Amount = r.Amount, Scaling = (int)r.Scaling,
                    ChoiceGroup = r.ChoiceGroup, Condition = r.Condition, FirstTimeOnly = r.FirstTimeOnly, LegacySource = r.LegacySource,
                }).ToList(),
                Vars = d.Vars.Select(v => new QuestVarDto { Index = v.Index, AuthorKey = v.AuthorKey, Default = v.Default }).ToList(),
                Conditions = _w.QuestConditionList.Where(c => c.OwnerQuest == q.Index).OrderBy(c => c.Index).Select(c => new QuestConditionDto
                {
                    Index = c.Index, Op = (int)c.Op, Parent = c.Parent, Order = c.Order, Leaf = (int)c.Leaf, Arg1 = c.Arg1, Arg2 = c.Arg2, Cmp = (int)c.Cmp,
                }).ToList(),
            };

            var texts = TextMap();
            foreach (var key in KeysOf(d))
                if (texts.TryGetValue(key, out var body)) dto.Texts[key] = body;
            return dto;
        }

        /// <summary>The concurrency token of a graph: a hash of its JSON without the token itself.</summary>
        public static string RevOf(QuestGraphDto dto)
        {
            var rev = dto.Rev;
            dto.Rev = null;
            try
            {
                var bytes = SHA256.HashData(WebAdminJson.SerializeToUtf8Bytes(dto));
                return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
            }
            finally
            {
                dto.Rev = rev;
            }
        }

        /// <summary>Every text key a definition references (distinct, non-empty).</summary>
        public static HashSet<string> KeysOf(QuestDefinition d)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Add(string k)
            {
                if (!string.IsNullOrEmpty(k)) keys.Add(k);
            }

            Add(d.NameKey); Add(d.SummaryKey); Add(d.CompleteKey);
            foreach (var s in d.Steps) { Add(s.TitleKey); Add(s.TextKey); Add(s.ReadyTextKey); }
            foreach (var o in d.Objectives) Add(o.TextKey);
            foreach (var b in d.Branches) { Add(b.ChoiceKey); Add(b.TextKey); }
            return keys;
        }

        private static HashSet<string> KeysOf(QuestGraphDto d)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Add(string k)
            {
                if (!string.IsNullOrEmpty(k)) keys.Add(k);
            }

            Add(d.NameKey); Add(d.SummaryKey); Add(d.CompleteKey);
            foreach (var s in d.Steps ?? new List<QuestStepDto>()) { Add(s.TitleKey); Add(s.TextKey); Add(s.ReadyTextKey); }
            foreach (var o in d.Objectives ?? new List<QuestObjectiveDto>()) Add(o.TextKey);
            foreach (var b in d.Branches ?? new List<QuestBranchDto>()) { Add(b.ChoiceKey); Add(b.TextKey); }
            return keys;
        }

        /// <summary>The enum names, groups, shared conditions, storylines and pools the page needs.</summary>
        public object Meta()
        {
            var texts = TextMap();
            return new
            {
                enums = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["category"] = EnumList<QuestCategory>(),
                    ["repeatPolicy"] = EnumList<QuestRepeatPolicy>(),
                    ["shareMode"] = EnumList<QuestShareMode>(),
                    ["creditMode"] = EnumList<QuestCreditMode>(),
                    ["failPolicy"] = EnumList<QuestFailPolicy>(),
                    ["flags"] = EnumList<QuestFlags>().Where(e => e.value != 0).ToList(),
                    ["completionMode"] = EnumList<QuestStepCompletion>(),
                    ["objectiveType"] = EnumList<QuestObjectiveType>(),
                    ["partyCredit"] = EnumList<QuestPartyCredit>(),
                    ["targetKind"] = EnumList<QuestTargetKind>(),
                    ["groupKind"] = EnumList<QuestGroupKind>(),
                    ["endpointRole"] = EnumList<QuestEndpointRole>(),
                    ["endpointKind"] = EnumList<QuestEndpointKind>(),
                    ["rewardScope"] = EnumList<QuestRewardScope>(),
                    ["rewardKind"] = EnumList<QuestRewardKind>(),
                    ["rewardScaling"] = EnumList<QuestRewardScaling>(),
                    ["conditionOp"] = EnumList<QuestConditionOp>(),
                    ["conditionLeaf"] = EnumList<QuestConditionLeaf>(),
                    ["compare"] = EnumList<QuestCompare>(),
                    ["mirClass"] = EnumList<MirClass>(),
                    ["mirGender"] = EnumList<MirGender>(),
                    ["runStatus"] = EnumList<QuestRunStatus>(),
                },
                groups = _w.QuestGroupList.OrderBy(g => g.Index).Select(g => new
                {
                    index = g.Index, authorKey = g.AuthorKey, kind = (int)g.Kind, members = g.Members.Select(m => m.TargetId).ToList(),
                }).ToList(),
                sharedConditions = _w.QuestConditionList.Where(c => c.OwnerQuest == 0).OrderBy(c => c.Index).Select(c => new
                {
                    index = c.Index, op = (int)c.Op, parent = c.Parent, order = c.Order, leaf = (int)c.Leaf, arg1 = c.Arg1, arg2 = c.Arg2, cmp = (int)c.Cmp,
                }).ToList(),
                storylines = _w.StorylineList.OrderBy(s => s.Order).ThenBy(s => s.Index).Select(s => new
                {
                    index = s.Index, authorKey = s.AuthorKey, name = !string.IsNullOrEmpty(s.NameKey) && texts.TryGetValue(s.NameKey, out var b) ? FirstLine(b) : s.AuthorKey,
                }).ToList(),
                pools = _w.QuestPoolList.OrderBy(p => p.Index).Select(p => new { index = p.Index, authorKey = p.AuthorKey }).ToList(),
                maxFlag = QuestDefinitions.MaxFlagNumber,
                tempIdLimit = QuestGraphDto.TempIdLimit,
            };
        }

        private static List<(int value, string label)> EnumListRaw<T>() where T : struct, Enum =>
            Enum.GetValues<T>().Select(v => (Convert.ToInt32(v, CultureInfo.InvariantCulture), v.ToString())).ToList();

        private static List<EnumOption> EnumList<T>() where T : struct, Enum =>
            EnumListRaw<T>().Select(x => new EnumOption { value = x.value, label = x.label }).ToList();

        internal sealed class EnumOption
        {
            public int value;
            public string label;
        }

        // ================================================================== where used

        public List<QuestUseDto> WhereUsed(QuestRefKind kind, int id) =>
            QuestWhereUsed.Find(_w.QuestInfoList, _w.QuestConditionList, _w.QuestGroupList, _w.QuestPoolList, kind, id);

        // ================================================================== save

        private sealed class Snapshot
        {
            public List<QuestInfo> Quests;
            public List<QuestConditionInfo> Conditions;
            public List<QuestTextInfo> Texts;
            public List<QuestGroupInfo> Groups;
            public QuestInfo Quest;
            public QuestDefinition Definition;
            public int[] Counters;
        }

        private Snapshot Take(QuestInfo q) => new Snapshot
        {
            Quests = new List<QuestInfo>(_w.QuestInfoList),
            Conditions = new List<QuestConditionInfo>(_w.QuestConditionList),
            Texts = new List<QuestTextInfo>(_w.QuestTextList),
            Groups = new List<QuestGroupInfo>(_w.QuestGroupList),
            Quest = q,
            Definition = q?.Definition,
            Counters = new[]
            {
                _w.QuestIndex, _w.QuestStepIndex, _w.QuestObjectiveIndex, _w.QuestConditionIndex, _w.QuestGroupIndex, _w.QuestTextIndex, _w.QuestVarIndex,
            },
        };

        private void Restore(Snapshot s, bool counters)
        {
            Replace(_w.QuestInfoList, s.Quests);
            Replace(_w.QuestConditionList, s.Conditions);
            Replace(_w.QuestTextList, s.Texts);
            Replace(_w.QuestGroupList, s.Groups);
            if (s.Quest != null) s.Quest.Definition = s.Definition;
            if (!counters) return;
            _w.QuestIndex = s.Counters[0]; _w.QuestStepIndex = s.Counters[1]; _w.QuestObjectiveIndex = s.Counters[2]; _w.QuestConditionIndex = s.Counters[3];
            _w.QuestGroupIndex = s.Counters[4]; _w.QuestTextIndex = s.Counters[5]; _w.QuestVarIndex = s.Counters[6];
        }

        private static void Replace<T>(List<T> target, List<T> items)
        {
            target.Clear();
            target.AddRange(items);
        }

        private static bool Defined<T>(int v) where T : struct, Enum => v >= 0 && v <= 255 && Enum.IsDefined(typeof(T), (byte)v);

        private static string CheckKey(string key, string what, int max)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.Length > max) return what + " is longer than " + N(max) + " characters";
            if (key.Trim() != key || key.Any(char.IsControl)) return what + " has leading/trailing spaces or control characters";
            return null;
        }

        /// <summary>
        /// Saves one quest graph (create when <see cref="QuestGraphDto.Index"/> is 0, else update; <paramref name="dto"/>.Rev must match the
        /// current graph). New rows get fresh counter ids. Refused with 422 and every error when the structure is invalid or validation finds an
        /// error on this quest (warnings never block). <paramref name="newGroups"/>: target groups created in the same save (JSON import),
        /// with temporary ids the objectives may reference. A dry run validates and rolls back (no ids are kept, nothing is written).
        /// </summary>
        public QuestSaveResult Save(QuestGraphDto dto, bool dryRun, List<QuestGroupDto> newGroups = null, bool checkRev = true)
        {
            if (dto == null) throw new WebAdminException(400, "Expected a quest graph");
            if (!dryRun && _persist == null) throw new InvalidOperationException("read-only editor");

            dto.Steps ??= new List<QuestStepDto>();
            dto.Objectives ??= new List<QuestObjectiveDto>();
            dto.Branches ??= new List<QuestBranchDto>();
            dto.Endpoints ??= new List<QuestEndpointDto>();
            dto.Rewards ??= new List<QuestRewardDto>();
            dto.Vars ??= new List<QuestVarDto>();
            dto.Conditions ??= new List<QuestConditionDto>();
            dto.Texts ??= new Dictionary<string, string>(StringComparer.Ordinal);
            newGroups ??= new List<QuestGroupDto>();

            var isNew = dto.Index == 0;
            var q = isNew ? null : Find(dto.Index);
            if (!isNew && q == null) throw new WebAdminException(404, "Quest " + N(dto.Index) + " does not exist (deleted meanwhile?)");
            if (!isNew && checkRev)
            {
                if (string.IsNullOrEmpty(dto.Rev)) throw new WebAdminException(400, "'rev' is required to update a quest (reload it)");
                var current = Read(q.Index).Rev;
                if (!string.Equals(current, dto.Rev, StringComparison.Ordinal))
                    throw new WebAdminException(409, "Quest " + N(q.Index) + " was changed since you loaded it (reload it and apply your edit again)", new { rev = current });
            }

            var errors = new List<string>();
            void Err(string m) => errors.Add(m);

            // ---- structure: enums, keys, ids -----------------------------------------------------------------------------------------
            if (string.IsNullOrWhiteSpace(dto.AuthorKey)) Err("AuthorKey is required");
            else
            {
                var k = CheckKey(dto.AuthorKey, "AuthorKey", 64);
                if (k != null) Err(k);
                if (_w.QuestInfoList.Any(x => x != null && x != q && string.Equals(x.Definition.AuthorKey, dto.AuthorKey, StringComparison.OrdinalIgnoreCase)))
                    Err("AuthorKey " + dto.AuthorKey + " is used by another quest");
            }

            foreach (var key in KeysOf(dto))
            {
                var k = CheckKey(key, "text key '" + key + "'", 128);
                if (k != null) Err(k);
            }

            if (!Defined<QuestCategory>(dto.Category)) Err("unknown Category " + N(dto.Category));
            if (!Defined<QuestRepeatPolicy>(dto.RepeatPolicy)) Err("unknown RepeatPolicy " + N(dto.RepeatPolicy));
            if (!Defined<QuestShareMode>(dto.ShareMode)) Err("unknown ShareMode " + N(dto.ShareMode));
            if (!Defined<QuestCreditMode>(dto.CreditMode)) Err("unknown CreditMode " + N(dto.CreditMode));
            if (!Defined<QuestFailPolicy>(dto.FailPolicy)) Err("unknown FailPolicy " + N(dto.FailPolicy));
            const int knownFlags = (int)(QuestFlags.AutoAccept | QuestFlags.AutoComplete | QuestFlags.HiddenInLog | QuestFlags.AccountWide);
            if ((dto.Flags & ~knownFlags) != 0) Err("unknown Flags bits " + N(dto.Flags & ~knownFlags));
            foreach (var n in new[] { dto.CooldownSeconds, dto.MaxCompletions, dto.TimeLimitSeconds, dto.LevelHint, dto.StorylineOrder })
                if (n < 0) { Err("quest numbers (level hint, cooldown, completions, time limit, storyline order) cannot be negative"); break; }
            if (dto.Steps.Count == 0) Err("a quest needs at least one step");
            if (dto.Steps.Count > 200 || dto.Objectives.Count > 500 || dto.Rewards.Count > 200 || dto.Conditions.Count > 500 || dto.Branches.Count > 200 ||
                dto.Endpoints.Count > 100 || dto.Vars.Count > 100)
                Err("the quest has too many rows (steps 200, objectives 500, rewards 200, conditions 500, branches 200, endpoints 100, variables 100)");

            var questIndex = isNew ? 0 : q.Index;
            var oldSteps = new HashSet<int>(q?.Definition.Steps.Select(s => s.Index) ?? Enumerable.Empty<int>());
            var oldObjectives = new HashSet<int>(q?.Definition.Objectives.Select(o => o.Index) ?? Enumerable.Empty<int>());
            var oldVars = new HashSet<int>(q?.Definition.Vars.Select(v => v.Index) ?? Enumerable.Empty<int>());
            var oldConditions = new HashSet<int>(isNew ? Enumerable.Empty<int>() : _w.QuestConditionList.Where(c => c.OwnerQuest == questIndex).Select(c => c.Index));

            void Ids(string what, IEnumerable<int> ids, HashSet<int> existing)
            {
                var seen = new HashSet<int>();
                foreach (var id in ids)
                {
                    if (id > 0 && !existing.Contains(id)) Err(what + " id " + N(id) + " is not one of this quest's (ids are never reused; leave new rows at a temporary id)");
                    else if (id <= 0 && id > QuestGraphDto.TempIdLimit) Err(what + " id " + N(id) + " is invalid (new rows use a temporary id <= " + N(QuestGraphDto.TempIdLimit) + ")");
                    if (!seen.Add(id)) Err(what + " id " + N(id) + " appears twice");
                }
            }

            Ids("step", dto.Steps.Select(s => s.Index), oldSteps);
            Ids("objective", dto.Objectives.Select(o => o.Index), oldObjectives);
            Ids("variable", dto.Vars.Select(v => v.Index), oldVars);
            Ids("condition", dto.Conditions.Select(c => c.Index), oldConditions);
            var groupTemps = new HashSet<int>();
            foreach (var g in newGroups)
                if (g.Index > QuestGraphDto.TempIdLimit || !groupTemps.Add(g.Index)) Err("a new group has an invalid or repeated temporary id " + N(g.Index));

            foreach (var s in dto.Steps)
            {
                if (!Defined<QuestStepCompletion>(s.CompletionMode)) Err("step " + N(s.Index) + ": unknown CompletionMode " + N(s.CompletionMode));
                if (s.TimeLimitSeconds < 0 || s.NRequired < 0) Err("step " + N(s.Index) + ": negative NRequired or time limit");
                foreach (var page in new[] { s.OnEnterPage, s.OnCompletePage })
                {
                    var k = CheckKey(page, "step " + N(s.Index) + " script page", 64);
                    if (k != null) Err(k);
                }
            }

            foreach (var o in dto.Objectives)
            {
                if (!Defined<QuestObjectiveType>(o.Type)) Err("objective " + N(o.Index) + ": unknown Type " + N(o.Type));
                if (!Defined<QuestPartyCredit>(o.PartyCredit)) Err("objective " + N(o.Index) + ": unknown PartyCredit " + N(o.PartyCredit));
                if (!Defined<QuestTargetKind>(o.TargetKind)) Err("objective " + N(o.Index) + ": unknown TargetKind " + N(o.TargetKind));
                if (o.DropChance < 0 || o.DropChance > 10000) Err("objective " + N(o.Index) + ": DropChance must be 0-10000");
                if (o.Radius < 0 || o.X < 0 || o.Y < 0) Err("objective " + N(o.Index) + ": negative marker position or radius");
            }

            foreach (var ep in dto.Endpoints)
            {
                if (!Defined<QuestEndpointRole>(ep.Role)) Err("unknown endpoint Role " + N(ep.Role));
                if (!Defined<QuestEndpointKind>(ep.Kind)) Err("unknown endpoint Kind " + N(ep.Kind));
            }

            foreach (var r in dto.Rewards)
            {
                if (!Defined<QuestRewardScope>(r.Scope)) Err("unknown reward Scope " + N(r.Scope));
                if (!Defined<QuestRewardKind>(r.Kind)) Err("unknown reward Kind " + N(r.Kind));
                if (!Defined<QuestRewardScaling>(r.Scaling)) Err("unknown reward Scaling " + N(r.Scaling));
            }

            foreach (var v in dto.Vars)
            {
                if (string.IsNullOrWhiteSpace(v.AuthorKey)) Err("variable " + N(v.Index) + " needs a key");
                else
                {
                    var k = CheckKey(v.AuthorKey, "variable key " + v.AuthorKey, 64);
                    if (k != null) Err(k);
                }
            }

            if (dto.Vars.Where(v => !string.IsNullOrEmpty(v.AuthorKey)).GroupBy(v => v.AuthorKey, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                Err("two variables have the same key");

            foreach (var c in dto.Conditions)
            {
                if (!Defined<QuestConditionOp>(c.Op)) Err("condition " + N(c.Index) + ": unknown Op " + N(c.Op));
                if (!Defined<QuestConditionLeaf>(c.Leaf)) Err("condition " + N(c.Index) + ": unknown Leaf " + N(c.Leaf));
                if (!Defined<QuestCompare>(c.Cmp)) Err("condition " + N(c.Index) + ": unknown Cmp " + N(c.Cmp));
            }

            foreach (var g in newGroups) CheckGroup(g, null, Err);

            if (errors.Count > 0) throw Refused(errors.Select(m => new QuestProblemDto { Quest = questIndex, Message = m }).ToList());

            // ---- allocate ids and resolve references -------------------------------------------------------------------------------
            var snap = Take(q);
            var stepMap = new Dictionary<int, int>();
            var objectiveMap = new Dictionary<int, int>();
            var conditionMap = new Dictionary<int, int>();
            var groupMap = new Dictionary<int, int>();
            if (isNew) questIndex = ++_w.QuestIndex;
            foreach (var s in dto.Steps) if (s.Index < 0) stepMap[s.Index] = ++_w.QuestStepIndex;
            foreach (var o in dto.Objectives) if (o.Index < 0) objectiveMap[o.Index] = ++_w.QuestObjectiveIndex;
            foreach (var c in dto.Conditions) if (c.Index < 0) conditionMap[c.Index] = ++_w.QuestConditionIndex;
            var varIds = dto.Vars.Select(v => v.Index < 0 ? ++_w.QuestVarIndex : v.Index).ToList();
            foreach (var g in newGroups) groupMap[g.Index] = ++_w.QuestGroupIndex;

            var finalSteps = new HashSet<int>(dto.Steps.Select(s => s.Index < 0 ? stepMap[s.Index] : s.Index));
            var finalObjectives = new HashSet<int>(dto.Objectives.Select(o => o.Index < 0 ? objectiveMap[o.Index] : o.Index));
            var finalConditions = new HashSet<int>(dto.Conditions.Select(c => c.Index < 0 ? conditionMap[c.Index] : c.Index));
            var conditionsById = _w.QuestConditionList.GroupBy(c => c.Index).ToDictionary(g => g.Key, g => g.First());

            int Step(int id, string what)
            {
                if (id == 0) return 0;
                var mapped = id < 0 ? (stepMap.TryGetValue(id, out var m) ? m : -999) : id;
                if (mapped == -999 || !finalSteps.Contains(mapped)) { Err(what + " names step " + N(id) + ", not a step of this quest"); return 0; }
                return mapped;
            }

            int Next(int id, string what) => id == -1 || id == 0 ? id : Step(id, what);

            int Cond(int id, string what)
            {
                if (id == 0) return 0;
                if (id < 0)
                {
                    if (conditionMap.TryGetValue(id, out var m)) return m;
                    Err(what + " names condition " + N(id) + ", not one of this quest's new conditions");
                    return 0;
                }

                if (finalConditions.Contains(id)) return id;
                if (!conditionsById.TryGetValue(id, out var c)) Err(what + " names a missing condition " + N(id));
                else if (c.OwnerQuest != 0 && c.OwnerQuest != questIndex) Err(what + " uses condition " + N(id) + ", owned by quest " + N(c.OwnerQuest));
                else if (c.OwnerQuest == questIndex) Err(what + " uses condition " + N(id) + ", which this save deletes");
                return id;
            }

            int Objective(int id, string what)
            {
                var mapped = id < 0 ? (objectiveMap.TryGetValue(id, out var m) ? m : -999) : id;
                if (mapped == -999 || !finalObjectives.Contains(mapped)) { Err(what + " names objective " + N(id) + ", not one of this quest"); return 0; }
                return mapped;
            }

            var def = new QuestDefinition
            {
                AuthorKey = dto.AuthorKey.Trim(),
                NameKey = dto.NameKey ?? string.Empty,
                SummaryKey = dto.SummaryKey ?? string.Empty,
                CompleteKey = dto.CompleteKey ?? string.Empty,
                Category = (QuestCategory)dto.Category,
                StorylineIndex = dto.StorylineIndex,
                StorylineOrder = dto.StorylineOrder,
                LevelHint = dto.LevelHint,
                AcceptCondition = Cond(dto.AcceptCondition, "AcceptCondition"),
                VisibleCondition = Cond(dto.VisibleCondition, "VisibleCondition"),
                RepeatPolicy = (QuestRepeatPolicy)dto.RepeatPolicy,
                CooldownSeconds = dto.CooldownSeconds,
                MaxCompletions = dto.MaxCompletions,
                ShareMode = (QuestShareMode)dto.ShareMode,
                CreditMode = (QuestCreditMode)dto.CreditMode,
                TimeLimitSeconds = dto.TimeLimitSeconds,
                FailPolicy = (QuestFailPolicy)dto.FailPolicy,
                AbandonAllowed = dto.AbandonAllowed,
                // Revision / ShapeHash carried over: QuestDefinitions.PrepareForSave bumps them when the shape changed.
                Revision = q?.Definition.Revision ?? 0,
                ShapeHash = q?.Definition.ShapeHash ?? 0,
                PoolIndex = dto.PoolIndex,
                StartItemConsumed = dto.StartItemConsumed,
                Flags = (QuestFlags)dto.Flags,
                GroupName = dto.GroupName ?? string.Empty,
            };

            foreach (var s in dto.Steps)
                def.Steps.Add(new QuestStepInfo
                {
                    Index = s.Index < 0 ? stepMap[s.Index] : s.Index, Order = s.Order, TitleKey = s.TitleKey ?? string.Empty, TextKey = s.TextKey ?? string.Empty,
                    ReadyTextKey = s.ReadyTextKey ?? string.Empty, CompletionMode = (QuestStepCompletion)s.CompletionMode, NRequired = s.NRequired,
                    NextStep = Next(s.NextStep, "step " + N(s.Index) + " NextStep"), TimeLimitSeconds = s.TimeLimitSeconds,
                    FailCondition = Cond(s.FailCondition, "step " + N(s.Index) + " FailCondition"), OnEnterPage = s.OnEnterPage ?? string.Empty,
                    OnCompletePage = s.OnCompletePage ?? string.Empty, LegacySource = s.LegacySource ?? string.Empty,
                });

            foreach (var o in dto.Objectives)
            {
                var kind = (QuestTargetKind)o.TargetKind;
                var target = o.TargetId;
                if ((kind == QuestTargetKind.MonsterGroup || kind == QuestTargetKind.ItemGroup) && target < 0)
                {
                    if (groupMap.TryGetValue(target, out var g)) target = g;
                    else Err("objective " + N(o.Index) + " names new group " + N(target) + ", which is not in this save");
                }

                def.Objectives.Add(new QuestObjectiveInfo
                {
                    Index = o.Index < 0 ? objectiveMap[o.Index] : o.Index, Step = Step(o.Step, "objective " + N(o.Index)), Order = o.Order,
                    Type = (QuestObjectiveType)o.Type, PartyCredit = (QuestPartyCredit)o.PartyCredit, TargetKind = kind, TargetId = target, Count = o.Count,
                    Map = o.Map, X = o.X, Y = o.Y, Radius = o.Radius, Optional = o.Optional, Hidden = o.Hidden,
                    Condition = Cond(o.Condition, "objective " + N(o.Index) + " Condition"), TextKey = o.TextKey ?? string.Empty, DropChance = o.DropChance,
                    Params = o.Params ?? string.Empty, LegacySource = o.LegacySource ?? string.Empty,
                });
            }

            foreach (var b in dto.Branches)
                def.Branches.Add(new QuestBranchInfo
                {
                    Step = Step(b.Step, "a branch"), ChoiceKey = b.ChoiceKey ?? string.Empty, TextKey = b.TextKey ?? string.Empty,
                    NextStep = Next(b.NextStep, "a branch NextStep"), Outcome = b.Outcome, Condition = Cond(b.Condition, "a branch Condition"),
                });

            foreach (var ep in dto.Endpoints)
                def.Endpoints.Add(new QuestEndpointInfo
                {
                    Role = (QuestEndpointRole)ep.Role, Kind = (QuestEndpointKind)ep.Kind, TargetId = ep.TargetId, Map = ep.Map, LegacySource = ep.LegacySource ?? string.Empty,
                });

            foreach (var r in dto.Rewards)
            {
                var scope = (QuestRewardScope)r.Scope;
                var scopeId = scope switch
                {
                    QuestRewardScope.Step => Step(r.ScopeId, scope + " reward"),
                    QuestRewardScope.Objective => Objective(r.ScopeId, scope + " reward"),
                    QuestRewardScope.Outcome => r.ScopeId,
                    _ => 0,
                };
                def.Rewards.Add(new QuestRewardInfo
                {
                    Scope = scope, ScopeId = scopeId, Kind = (QuestRewardKind)r.Kind, TargetId = r.TargetId, Amount = r.Amount, Scaling = (QuestRewardScaling)r.Scaling,
                    ChoiceGroup = r.ChoiceGroup, Condition = Cond(r.Condition, scope + " reward " + (QuestRewardKind)r.Kind + " Condition"),
                    FirstTimeOnly = r.FirstTimeOnly, LegacySource = r.LegacySource ?? string.Empty,
                });
            }

            for (var i = 0; i < dto.Vars.Count; i++)
                def.Vars.Add(new QuestVarInfo { Index = varIds[i], AuthorKey = dto.Vars[i].AuthorKey.Trim(), Default = dto.Vars[i].Default });

            var owned = new List<QuestConditionInfo>();
            foreach (var c in dto.Conditions)
            {
                var id = c.Index < 0 ? conditionMap[c.Index] : c.Index;
                var parent = 0;
                if (c.Parent != 0)
                {
                    parent = c.Parent < 0 ? (conditionMap.TryGetValue(c.Parent, out var p) ? p : -1) : c.Parent;
                    if (parent < 0 || !finalConditions.Contains(parent)) { Err("condition " + N(c.Index) + " has a parent " + N(c.Parent) + " that is not one of this quest's conditions"); parent = 0; }
                }

                var arg1 = c.Arg1;
                // A QuestVar leaf names a variable: map a new variable's temporary id.
                if ((QuestConditionLeaf)c.Leaf == QuestConditionLeaf.QuestVar && arg1 < 0)
                {
                    var vi = dto.Vars.FindIndex(v => v.Index == arg1);
                    if (vi >= 0) arg1 = varIds[vi];
                    else Err("condition " + N(c.Index) + " names new variable " + N(arg1) + ", which is not in this save");
                }

                owned.Add(new QuestConditionInfo
                {
                    Index = id, Op = (QuestConditionOp)c.Op, Parent = parent, Order = c.Order, Leaf = (QuestConditionLeaf)c.Leaf, Arg1 = arg1, Arg2 = c.Arg2,
                    Cmp = (QuestCompare)c.Cmp, OwnerQuest = questIndex,
                });
            }

            if (errors.Count > 0)
            {
                Restore(snap, true);
                throw Refused(errors.Select(m => new QuestProblemDto { Quest = questIndex, Message = m }).ToList());
            }

            // ---- texts: upsert the graph's keys, drop keys only this quest used --------------------------------------------------------
            var newKeys = KeysOf(def);
            var oldKeys = q != null ? KeysOf(q.Definition) : new HashSet<string>(StringComparer.Ordinal);
            var usedElsewhere = KeysUsedOutside(q);
            var textsChangedShared = false;
            var texts = new List<QuestTextInfo>(_w.QuestTextList);
            foreach (var pair in dto.Texts)
            {
                if (!newKeys.Contains(pair.Key)) continue; // a text no field points at is not stored
                var body = (pair.Value ?? string.Empty).Replace("\r\n", "\n");
                var at = texts.FindIndex(t => t.TextKey == pair.Key && string.IsNullOrEmpty(t.Lang));
                if (at >= 0)
                {
                    if (texts[at].Body == body) continue;
                    texts[at] = new QuestTextInfo { Index = texts[at].Index, TextKey = pair.Key, Lang = string.Empty, Body = body };
                }
                else
                {
                    texts.Add(new QuestTextInfo { Index = ++_w.QuestTextIndex, TextKey = pair.Key, Lang = string.Empty, Body = body });
                }

                if (usedElsewhere.Contains(pair.Key)) textsChangedShared = true;
            }

            texts.RemoveAll(t => oldKeys.Contains(t.TextKey) && !newKeys.Contains(t.TextKey) && !usedElsewhere.Contains(t.TextKey));

            // ---- apply, validate, persist -----------------------------------------------------------------------------------------------
            if (isNew)
            {
                var nameBody = dto.Texts.TryGetValue(def.NameKey, out var nb) ? FirstLine(nb) : string.Empty;
                q = new QuestInfo { Index = questIndex, Name = nameBody.Length > 0 ? nameBody : def.AuthorKey, Group = def.GroupName };
                _w.QuestInfoList.Add(q);
            }

            q.Definition = def;
            var conditions = _w.QuestConditionList.Where(c => c.OwnerQuest != questIndex).ToList();
            conditions.AddRange(owned);
            Replace(_w.QuestConditionList, conditions);
            Replace(_w.QuestTextList, texts);
            foreach (var g in newGroups)
            {
                var info = new QuestGroupInfo { Index = groupMap[g.Index], AuthorKey = g.AuthorKey.Trim(), Kind = (QuestGroupKind)g.Kind };
                foreach (var m in g.Members.Distinct()) info.Members.Add(new QuestGroupMemberInfo { TargetId = m });
                _w.QuestGroupList.Add(info);
            }

            var problems = QuestDefinitions.Validate(_w);
            var mine = problems.Where(p => p.Quest == questIndex).Select(ToDto).ToList();
            var blocking = mine.Where(p => !p.Warning).ToList();
            if (blocking.Count > 0 || dryRun)
            {
                Restore(snap, true);
                if (!dryRun) throw Refused(mine);
                return new QuestSaveResult { Index = isNew ? 0 : questIndex, Created = isNew, DryRun = true, Problems = mine };
            }

            _w.Commit();
            try
            {
                _persist();
            }
            catch
            {
                Restore(snap, false); // the counters stay up: ids handed out are retired even when the write failed
                throw;
            }

            var result = new QuestSaveResult { Index = questIndex, Created = isNew, Rev = Read(questIndex)?.Rev, Problems = mine };
            result.ChangedQuests.Add(questIndex);
            if (textsChangedShared) result.ChangedQuests.AddRange(QuestsUsingKeys(newKeys, questIndex));
            if (newGroups.Count > 0) result.SharedTablesChanged = true;
            result.SharedTablesChanged |= textsChangedShared;
            result.ChangedQuests = result.ChangedQuests.Distinct().OrderBy(i => i).ToList();
            return result;
        }

        private static QuestProblemDto ToDto(QuestValidationProblem p) => new QuestProblemDto { Quest = p.Quest, Warning = p.Warning, Message = p.Message };

        private static WebAdminException Refused(List<QuestProblemDto> problems)
        {
            var errors = problems.Count(p => !p.Warning);
            return new WebAdminException(422, "The quest was not saved: " + N(errors) + " error(s). Fix them and save again.", new { problems });
        }

        /// <summary>Text keys referenced by every quest except <paramref name="except"/>, and by the storylines.</summary>
        private HashSet<string> KeysUsedOutside(QuestInfo except)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var other in _w.QuestInfoList)
                if (other != null && other != except) keys.UnionWith(KeysOf(other.Definition));
            foreach (var s in _w.StorylineList)
                if (!string.IsNullOrEmpty(s.NameKey)) keys.Add(s.NameKey);
            return keys;
        }

        private IEnumerable<int> QuestsUsingKeys(HashSet<string> keys, int except) =>
            _w.QuestInfoList.Where(x => x != null && x.Index != except && KeysOf(x.Definition).Overlaps(keys)).Select(x => x.Index);

        // ================================================================== delete

        /// <summary>Deletes a quest with its owned conditions and the texts only it used. Refused (409) while anything references it.</summary>
        public QuestSaveResult Delete(int index, string rev)
        {
            if (_persist == null) throw new InvalidOperationException("read-only editor");
            var q = Find(index);
            if (q == null) throw new WebAdminException(404, "Quest " + N(index) + " does not exist");
            var current = Read(index).Rev;
            if (string.IsNullOrEmpty(rev) || !string.Equals(rev, current, StringComparison.Ordinal))
                throw new WebAdminException(409, "Quest " + N(index) + " was changed since you loaded it (reload it first)", new { rev = current });

            var uses = WhereUsed(QuestRefKind.Quest, index).Where(u => u.Quest != index).ToList();
            foreach (var npc in _w.NPCInfoList)
                if (npc != null && (npc.CollectQuestIndexes.Contains(index) || npc.FinishQuestIndexes.Contains(index)))
                    uses.Add(new QuestUseDto { Where = "NPC " + N(npc.Index) + " " + npc.Name + " (legacy [QUESTS] script)" });
            foreach (var other in _w.QuestInfoList)
                if (other != null && other != q && other.RequiredQuest == index)
                    uses.Add(new QuestUseDto { Quest = other.Index, QuestLabel = QuestWhereUsed.Label(other), Where = "legacy RequiredQuest" });
            if (uses.Count > 0)
                throw new WebAdminException(409, "Quest " + N(index) + " is still used (" + N(uses.Count) + " reference(s)); remove them first", new { uses });

            var snap = Take(q);
            var usedElsewhere = KeysUsedOutside(q);
            var mine = KeysOf(q.Definition);
            _w.QuestInfoList.Remove(q);
            _w.QuestConditionList.RemoveAll(c => c.OwnerQuest == index);
            _w.QuestTextList.RemoveAll(t => mine.Contains(t.TextKey) && !usedElsewhere.Contains(t.TextKey));
            try
            {
                _persist();
            }
            catch
            {
                Restore(snap, false);
                throw;
            }

            return new QuestSaveResult { Index = index, Deleted = true, ChangedQuests = new List<int> { index } };
        }

        // ================================================================== groups

        private void CheckGroup(QuestGroupDto g, QuestGroupInfo existing, Action<string> err)
        {
            var what = "group " + (string.IsNullOrEmpty(g.AuthorKey) ? N(g.Index) : g.AuthorKey);
            if (string.IsNullOrWhiteSpace(g.AuthorKey)) err(what + " needs a key");
            else
            {
                var k = CheckKey(g.AuthorKey, what + " key", 64);
                if (k != null) err(k);
                if (_w.QuestGroupList.Any(x => x != existing && string.Equals(x.AuthorKey, g.AuthorKey.Trim(), StringComparison.OrdinalIgnoreCase)))
                    err(what + ": the key is used by another group");
            }

            if (!Defined<QuestGroupKind>(g.Kind)) { err(what + ": unknown kind " + N(g.Kind)); return; }
            var members = g.Members ?? new List<int>();
            if (members.Count == 0) err(what + " has no members");
            if (members.Count > 1000) err(what + " has more than 1000 members");
            var ids = (QuestGroupKind)g.Kind == QuestGroupKind.Monster
                ? new HashSet<int>(_w.MonsterInfoList.Where(m => m != null).Select(m => m.Index))
                : new HashSet<int>(_w.ItemInfoList.Where(i => i != null).Select(i => i.Index));
            foreach (var m in members)
                if (!ids.Contains(m)) err(what + " names a missing " + ((QuestGroupKind)g.Kind == QuestGroupKind.Monster ? "monster " : "item ") + N(m));
        }

        /// <summary>Creates (Index 0) or replaces a target group. A group in use cannot change its kind.</summary>
        public QuestSaveResult SaveGroup(QuestGroupDto dto)
        {
            if (_persist == null) throw new InvalidOperationException("read-only editor");
            if (dto == null) throw new WebAdminException(400, "Expected a group");
            var existing = dto.Index == 0 ? null : _w.QuestGroupList.FirstOrDefault(g => g.Index == dto.Index);
            if (dto.Index != 0 && existing == null) throw new WebAdminException(404, "Quest group " + N(dto.Index) + " does not exist");

            var errors = new List<string>();
            CheckGroup(dto, existing, errors.Add);
            var users = existing == null ? new List<QuestUseDto>() : WhereUsed(QuestRefKind.Group, existing.Index);
            if (existing != null && users.Count > 0 && (int)existing.Kind != dto.Kind) errors.Add("the group is used by quests, so its kind cannot change");
            if (errors.Count > 0) throw Refused(errors.Select(m => new QuestProblemDto { Message = m }).ToList());

            var snap = Take(null);
            var info = new QuestGroupInfo { Index = existing?.Index ?? ++_w.QuestGroupIndex, AuthorKey = dto.AuthorKey.Trim(), Kind = (QuestGroupKind)dto.Kind };
            foreach (var m in dto.Members.Distinct()) info.Members.Add(new QuestGroupMemberInfo { TargetId = m });
            var at = existing == null ? -1 : _w.QuestGroupList.IndexOf(existing);
            if (at >= 0) _w.QuestGroupList[at] = info;
            else _w.QuestGroupList.Add(info);

            _w.Commit();
            try
            {
                _persist();
            }
            catch
            {
                Restore(snap, false);
                throw;
            }

            return new QuestSaveResult
            {
                Index = info.Index, Created = existing == null, SharedTablesChanged = true,
                ChangedQuests = users.Where(u => u.Quest != 0).Select(u => u.Quest).Distinct().OrderBy(i => i).ToList(),
            };
        }

        /// <summary>Deletes a target group nothing uses.</summary>
        public QuestSaveResult DeleteGroup(int index)
        {
            if (_persist == null) throw new InvalidOperationException("read-only editor");
            var g = _w.QuestGroupList.FirstOrDefault(x => x.Index == index);
            if (g == null) throw new WebAdminException(404, "Quest group " + N(index) + " does not exist");
            var uses = WhereUsed(QuestRefKind.Group, index);
            if (uses.Count > 0) throw new WebAdminException(409, "Quest group " + N(index) + " is still used; remove it from these quests first", new { uses });
            var snap = Take(null);
            _w.QuestGroupList.Remove(g);
            try
            {
                _persist();
            }
            catch
            {
                Restore(snap, false);
                throw;
            }

            return new QuestSaveResult { Index = index, Deleted = true, SharedTablesChanged = true };
        }

        // ================================================================== export / import

        public QuestExportFile Export(int index, DateTime utc)
        {
            var dto = Read(index);
            if (dto == null) return null;
            dto.Rev = null;
            var file = new QuestExportFile { Format = ExportFormat, ExportedUtc = utc, Quest = dto };

            void Ref(string kind, int id, string name)
            {
                if (id == 0) return;
                if (!file.Refs.TryGetValue(kind, out var map)) file.Refs[kind] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                map[N(id)] = name ?? string.Empty;
            }

            string Monster(int id) => _w.MonsterInfoList.FirstOrDefault(m => m != null && m.Index == id)?.Name;
            string Item(int id) => _w.ItemInfoList.FirstOrDefault(m => m != null && m.Index == id)?.Name;
            string Npc(int id) => _w.NPCInfoList.FirstOrDefault(m => m != null && m.Index == id)?.Name;
            string Map(int id) => _w.MapInfoList.FirstOrDefault(m => m != null && m.Index == id)?.FileName;
            string Quest(int id) { var x = Find(id); return x == null ? null : QuestWhereUsed.Label(x); }

            var groups = new HashSet<int>();
            foreach (var o in dto.Objectives)
            {
                switch ((QuestTargetKind)o.TargetKind)
                {
                    case QuestTargetKind.Monster: Ref("monster", o.TargetId, Monster(o.TargetId)); break;
                    case QuestTargetKind.Item: Ref("item", o.TargetId, Item(o.TargetId)); break;
                    case QuestTargetKind.Npc: Ref("npc", o.TargetId, Npc(o.TargetId)); break;
                    case QuestTargetKind.Map: Ref("map", o.TargetId, Map(o.TargetId)); break;
                    case QuestTargetKind.MonsterGroup:
                    case QuestTargetKind.ItemGroup: groups.Add(o.TargetId); break;
                }

                Ref("map", o.Map, Map(o.Map));
            }

            foreach (var ep in dto.Endpoints)
            {
                if (ep.Kind == (int)QuestEndpointKind.Npc) Ref("npc", ep.TargetId, Npc(ep.TargetId));
                if (ep.Kind == (int)QuestEndpointKind.ItemUse) Ref("item", ep.TargetId, Item(ep.TargetId));
                Ref("map", ep.Map, Map(ep.Map));
            }

            foreach (var r in dto.Rewards)
                if (r.Kind == (int)QuestRewardKind.Item) Ref("item", r.TargetId, Item(r.TargetId));

            foreach (var c in dto.Conditions)
            {
                switch ((QuestConditionLeaf)c.Leaf)
                {
                    case QuestConditionLeaf.ItemCount:
                    case QuestConditionLeaf.Equipped: Ref("item", c.Arg1, Item(c.Arg1)); break;
                    case QuestConditionLeaf.MapIs: Ref("map", c.Arg1, Map(c.Arg1)); break;
                    case QuestConditionLeaf.QuestStatus:
                    case QuestConditionLeaf.QuestCompletedCount:
                    case QuestConditionLeaf.QuestOutcome: Ref("quest", c.Arg1, Quest(c.Arg1)); break;
                }
            }

            var ownedIds = new HashSet<int>(dto.Conditions.Select(c => c.Index));
            foreach (var id in SharedConditionRefs(dto).Where(id => !ownedIds.Contains(id))) Ref("condition", id, "shared");
            if (dto.StorylineIndex != 0) Ref("storyline", dto.StorylineIndex, _w.StorylineList.FirstOrDefault(s => s.Index == dto.StorylineIndex)?.AuthorKey);
            if (dto.PoolIndex != 0) Ref("pool", dto.PoolIndex, _w.QuestPoolList.FirstOrDefault(p => p.Index == dto.PoolIndex)?.AuthorKey);
            foreach (var gi in groups)
            {
                var g = _w.QuestGroupList.FirstOrDefault(x => x.Index == gi);
                if (g == null) continue;
                Ref("group", gi, g.AuthorKey);
                file.Groups.Add(new QuestGroupDto { Index = g.Index, AuthorKey = g.AuthorKey, Kind = (int)g.Kind, Members = g.Members.Select(m => m.TargetId).ToList() });
            }

            return file;
        }

        private static IEnumerable<int> SharedConditionRefs(QuestGraphDto dto)
        {
            yield return dto.AcceptCondition;
            yield return dto.VisibleCondition;
            foreach (var s in dto.Steps) yield return s.FailCondition;
            foreach (var o in dto.Objectives) yield return o.Condition;
            foreach (var b in dto.Branches) yield return b.Condition;
            foreach (var r in dto.Rewards) yield return r.Condition;
        }

        /// <summary>
        /// JSON import, step 1 (<paramref name="apply"/> false): validates the file and returns the diff against the current quest with a
        /// token; step 2 (<paramref name="apply"/> true, with that token): saves it (validation blocks, as an editor save).
        /// </summary>
        public QuestSaveResult Import(string json, bool apply, string token)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new WebAdminException(400, "The import file is empty");
            QuestExportFile file;
            try
            {
                file = WebAdminJson.Deserialize<QuestExportFile>(json);
            }
            catch (Exception)
            {
                throw new WebAdminException(400, "The import file is not valid JSON of a quest export");
            }

            if (file == null || file.Quest == null || file.Format != ExportFormat)
                throw new WebAdminException(400, "Not a quest export file (format " + ExportFormat + " expected)");

            var warnings = new List<string>();
            var incoming = file.Quest;
            incoming.Steps ??= new List<QuestStepDto>();
            incoming.Objectives ??= new List<QuestObjectiveDto>();
            incoming.Branches ??= new List<QuestBranchDto>();
            incoming.Endpoints ??= new List<QuestEndpointDto>();
            incoming.Rewards ??= new List<QuestRewardDto>();
            incoming.Vars ??= new List<QuestVarDto>();
            incoming.Conditions ??= new List<QuestConditionDto>();
            incoming.Texts ??= new Dictionary<string, string>(StringComparer.Ordinal);

            // Target: the quest with the file's id (ids authoritative), else the one with its author key, else a new quest.
            var target = Find(incoming.Index);
            if (target != null && !string.Equals(target.Definition.AuthorKey, incoming.AuthorKey, StringComparison.Ordinal))
                warnings.Add("quest " + N(target.Index) + " has author key '" + target.Definition.AuthorKey + "' here and '" + incoming.AuthorKey + "' in the file (the id wins)");
            if (target == null && !string.IsNullOrEmpty(incoming.AuthorKey))
            {
                target = _w.QuestInfoList.FirstOrDefault(x => x != null && string.Equals(x.Definition.AuthorKey, incoming.AuthorKey, StringComparison.OrdinalIgnoreCase));
                if (target != null) warnings.Add("quest id " + N(incoming.Index) + " does not exist here; matched quest " + N(target.Index) + " by author key");
                else if (incoming.Index > 0) warnings.Add("quest id " + N(incoming.Index) + " does not exist here; the import creates a new quest (ids are never reused)");
            }

            var before = target == null ? null : Read(target.Index);
            var dto = Remap(incoming, target, file, warnings, out var newGroups);

            CheckRefNames(file, warnings);
            var currentToken = Token(json, before?.Rev);
            if (apply && !string.Equals(token, currentToken, StringComparison.Ordinal))
                throw new WebAdminException(409, "Preview the import first (or again): the file or the quest changed since the preview");
            var result = Save(dto, dryRun: !apply, newGroups, checkRev: false);
            result.Mode = target == null ? "create" : "update";
            if (!apply)
            {
                result.Token = currentToken;
                result.Diff = Diff(before, dto);
                foreach (var w in warnings) result.Problems.Insert(0, new QuestProblemDto { Quest = result.Index, Warning = true, Message = w });
                return result;
            }

            return result;
        }

        /// <summary>The apply step checks this token: the same file against the same current quest as the preview.</summary>
        public string Token(string json, string rev)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((rev ?? "new") + "\n" + json));
            return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
        }

        /// <summary>
        /// The file's graph as a save of <paramref name="target"/>: ids of rows that exist in the target are kept, every other row gets a
        /// temporary id (references follow); groups resolve by id, then by author key, else are created.
        /// </summary>
        private QuestGraphDto Remap(QuestGraphDto src, QuestInfo target, QuestExportFile file, List<string> warnings, out List<QuestGroupDto> newGroups)
        {
            var json = WebAdminJson.Serialize(src);
            var dto = WebAdminJson.Deserialize<QuestGraphDto>(json); // a copy
            dto.Index = target?.Index ?? 0;
            dto.Rev = null;
            var next = QuestGraphDto.TempIdLimit - 1;

            Dictionary<int, int> MapIds(IEnumerable<int> ids, HashSet<int> keep)
            {
                var map = new Dictionary<int, int>();
                foreach (var id in ids)
                    if (!map.ContainsKey(id)) map[id] = id > 0 && keep.Contains(id) ? id : next--;
                return map;
            }

            var steps = MapIds(dto.Steps.Select(s => s.Index), new HashSet<int>(target?.Definition.Steps.Select(s => s.Index) ?? Enumerable.Empty<int>()));
            var objectives = MapIds(dto.Objectives.Select(o => o.Index), new HashSet<int>(target?.Definition.Objectives.Select(o => o.Index) ?? Enumerable.Empty<int>()));
            var vars = MapIds(dto.Vars.Select(v => v.Index), new HashSet<int>(target?.Definition.Vars.Select(v => v.Index) ?? Enumerable.Empty<int>()));
            var conds = MapIds(dto.Conditions.Select(c => c.Index),
                new HashSet<int>(target == null ? Enumerable.Empty<int>() : _w.QuestConditionList.Where(c => c.OwnerQuest == target.Index).Select(c => c.Index)));

            int S(int id) => id > 0 && steps.TryGetValue(id, out var m) ? m : id;
            int NextS(int id) => id == -1 ? -1 : S(id);
            int C(int id) => id != 0 && conds.TryGetValue(id, out var m) ? m : id;
            int O(int id) => objectives.TryGetValue(id, out var m) ? m : id;

            foreach (var s in dto.Steps) { s.Index = steps[s.Index]; s.NextStep = NextS(s.NextStep); s.FailCondition = C(s.FailCondition); }
            foreach (var o in dto.Objectives) { o.Index = objectives[o.Index]; o.Step = S(o.Step); o.Condition = C(o.Condition); }
            foreach (var b in dto.Branches) { b.Step = S(b.Step); b.NextStep = NextS(b.NextStep); b.Condition = C(b.Condition); }
            foreach (var r in dto.Rewards)
            {
                if (r.Scope == (int)QuestRewardScope.Step) r.ScopeId = S(r.ScopeId);
                if (r.Scope == (int)QuestRewardScope.Objective) r.ScopeId = O(r.ScopeId);
                r.Condition = C(r.Condition);
            }

            foreach (var v in dto.Vars) v.Index = vars[v.Index];
            foreach (var c in dto.Conditions)
            {
                c.Index = conds[c.Index];
                c.Parent = C(c.Parent);
                if (c.Leaf == (int)QuestConditionLeaf.QuestVar && vars.TryGetValue(c.Arg1, out var vm)) c.Arg1 = vm;
            }

            dto.AcceptCondition = C(dto.AcceptCondition);
            dto.VisibleCondition = C(dto.VisibleCondition);

            // Groups.
            newGroups = new List<QuestGroupDto>();
            var groupMap = new Dictionary<int, int>();
            foreach (var o in dto.Objectives)
            {
                if (o.TargetKind != (int)QuestTargetKind.MonsterGroup && o.TargetKind != (int)QuestTargetKind.ItemGroup) continue;
                if (groupMap.TryGetValue(o.TargetId, out var mapped)) { o.TargetId = mapped; continue; }
                var original = o.TargetId;
                var fileGroup = file.Groups?.FirstOrDefault(g => g.Index == original);
                var here = _w.QuestGroupList.FirstOrDefault(g => g.Index == original);
                var wantKind = o.TargetKind == (int)QuestTargetKind.MonsterGroup ? QuestGroupKind.Monster : QuestGroupKind.Item;
                if (here != null && (fileGroup == null || string.Equals(here.AuthorKey, fileGroup.AuthorKey, StringComparison.OrdinalIgnoreCase)))
                {
                    groupMap[original] = original;
                    continue;
                }

                var byKey = fileGroup == null ? null : _w.QuestGroupList.FirstOrDefault(g => g.Kind == wantKind && string.Equals(g.AuthorKey, fileGroup.AuthorKey, StringComparison.OrdinalIgnoreCase));
                if (byKey != null)
                {
                    warnings.Add("group " + N(original) + " (" + fileGroup.AuthorKey + ") is group " + N(byKey.Index) + " here");
                    groupMap[original] = o.TargetId = byKey.Index;
                    continue;
                }

                if (fileGroup == null) { groupMap[original] = original; continue; } // validation reports the missing group

                var created = new QuestGroupDto { Index = next--, AuthorKey = fileGroup.AuthorKey, Kind = fileGroup.Kind, Members = fileGroup.Members ?? new List<int>() };
                newGroups.Add(created);
                warnings.Add("group " + N(original) + " (" + fileGroup.AuthorKey + ") does not exist here; the import creates it");
                groupMap[original] = o.TargetId = created.Index;
            }

            return dto;
        }

        private void CheckRefNames(QuestExportFile file, List<string> warnings)
        {
            if (file.Refs == null) return;
            string Here(string kind, int id) => kind switch
            {
                "monster" => _w.MonsterInfoList.FirstOrDefault(m => m != null && m.Index == id)?.Name,
                "item" => _w.ItemInfoList.FirstOrDefault(m => m != null && m.Index == id)?.Name,
                "npc" => _w.NPCInfoList.FirstOrDefault(m => m != null && m.Index == id)?.Name,
                "map" => _w.MapInfoList.FirstOrDefault(m => m != null && m.Index == id)?.FileName,
                _ => null,
            };

            foreach (var kind in new[] { "monster", "item", "npc", "map" })
            {
                if (!file.Refs.TryGetValue(kind, out var map) || map == null) continue;
                foreach (var pair in map)
                {
                    if (!int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                    var here = Here(kind, id);
                    if (here == null) warnings.Add(kind + " " + pair.Key + " (" + pair.Value + " in the file) does not exist here");
                    else if (!string.Equals(here, pair.Value, StringComparison.Ordinal)) warnings.Add(kind + " " + pair.Key + " is '" + here + "' here but '" + pair.Value + "' in the file");
                }
            }
        }

        // ------------------------------------------------------------------ diff

        /// <summary>Readable changes from <paramref name="before"/> (null = a new quest) to <paramref name="after"/>.</summary>
        public static List<string> Diff(QuestGraphDto before, QuestGraphDto after)
        {
            var lines = new List<string>();
            if (before == null)
            {
                lines.Add("new quest " + after.AuthorKey + ": " + N(after.Steps.Count) + " step(s), " + N(after.Objectives.Count) + " objective(s), " + N(after.Rewards.Count) +
                          " reward(s), " + N(after.Endpoints.Count) + " endpoint(s), " + N(after.Conditions.Count) + " condition(s), " + N(after.Texts.Count) + " text(s)");
                return lines;
            }

            FieldsDiff("quest", before, after, lines, "Index", "Rev", "Name", "Legacy", "Revision", "ShapeHash");
            ById("step", before.Steps, after.Steps, s => s.Index, lines);
            ById("objective", before.Objectives, after.Objectives, o => o.Index, lines);
            ById("variable", before.Vars, after.Vars, v => v.Index, lines);
            ById("condition", before.Conditions, after.Conditions, c => c.Index, lines);
            ByPosition("branch", before.Branches, after.Branches, lines);
            ByPosition("endpoint", before.Endpoints, after.Endpoints, lines);
            ByPosition("reward", before.Rewards, after.Rewards, lines);
            foreach (var key in before.Texts.Keys.Union(after.Texts.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                before.Texts.TryGetValue(key, out var a);
                after.Texts.TryGetValue(key, out var b);
                if (a == null && b != null) lines.Add("text " + key + " added: " + Short(b));
                else if (a != null && b == null) lines.Add("text " + key + " no longer in the file (kept unless no field uses it)");
                else if (!string.Equals(a, b, StringComparison.Ordinal)) lines.Add("text " + key + ": " + Short(a) + " → " + Short(b));
            }

            if (lines.Count == 0) lines.Add("no changes");
            return lines;
        }

        private static string Short(string s)
        {
            s = (s ?? string.Empty).Replace("\n", " ⏎ ");
            return "\"" + (s.Length > 60 ? s.Substring(0, 57) + "…" : s) + "\"";
        }

        private static string Value(object v) => v switch
        {
            null => "∅",
            string s => Short(s),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };

        private static IEnumerable<FieldInfo> Scalars(Type t) =>
            t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string));

        private static void FieldsDiff(string what, object a, object b, List<string> lines, params string[] skip)
        {
            foreach (var f in Scalars(a.GetType()))
            {
                if (skip.Contains(f.Name)) continue;
                var x = f.GetValue(a);
                var y = f.GetValue(b);
                if (f.FieldType == typeof(string)) { x ??= string.Empty; y ??= string.Empty; }
                if (Equals(x, y)) continue;
                lines.Add(what + " " + f.Name + ": " + Value(x) + " → " + Value(y));
            }
        }

        private static void ById<T>(string what, List<T> a, List<T> b, Func<T, int> id, List<string> lines)
        {
            var before = a.ToDictionary(id);
            foreach (var row in b)
            {
                var key = id(row);
                if (key < 0) { lines.Add(what + " added (new id)"); continue; }
                if (!before.TryGetValue(key, out var old)) { lines.Add(what + " " + N(key) + " added"); continue; }
                FieldsDiff(what + " " + N(key), old, row, lines);
            }

            var kept = new HashSet<int>(b.Select(id));
            foreach (var key in before.Keys.Where(k => !kept.Contains(k))) lines.Add(what + " " + N(key) + " removed (its id is retired)");
        }

        private static void ByPosition<T>(string what, List<T> a, List<T> b, List<string> lines)
        {
            for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                if (i >= a.Count) lines.Add(what + " #" + N(i + 1) + " added");
                else if (i >= b.Count) lines.Add(what + " #" + N(i + 1) + " removed");
                else FieldsDiff(what + " #" + N(i + 1), a[i], b[i], lines);
            }
        }
    }

    /// <summary>
    /// Envir.Edit access for the Quests editor: <see cref="WebAdminGame.EditLock"/> held for the whole call (HTTP worker threads only, never
    /// the game thread), with the same guards as other direct game-data saves (WinForms editors closed, no unsaved grid changes that the
    /// save would sweep in, editor copy loaded).
    /// </summary>
    internal static class QuestEditorGate
    {
        public static T Read<T>(Func<QuestEditorService, T> fn)
        {
            lock (WebAdminGame.EditLock)
                return fn(new QuestEditorService(QuestWorld.FromEnvir(Envir.Edit), null));
        }

        public static (T Result, GameDataSave.SaveOutcome? Saved) Write<T>(Func<QuestEditorService, T> fn)
        {
            lock (WebAdminGame.EditLock)
            {
                if (Envir.EditorFormsOpen > 0)
                    throw new WebAdminException(409, "A database editor window is open on the server (WinForms). Close it first: it edits the same data and saves it when it closes.");
                if (QuestImportRunner.BackgroundRunning)
                    throw new WebAdminException(409, "The automatic quest import is running; try again in a moment.");
                if (GameDataState.Unsaved.Changes > 0)
                    throw new WebAdminException(409, "The Game Data editor copy has " + GameDataState.Unsaved.Changes.ToString(CultureInfo.InvariantCulture) +
                                                     " unsaved change(s) that this save would write too. Save or discard them in Game Data first.");
                GameDataSave.EnsureSavable();

                GameDataSave.SaveOutcome? saved = null;
                var svc = new QuestEditorService(QuestWorld.FromEnvir(Envir.Edit), () =>
                {
                    try
                    {
                        saved = GameDataSave.SaveEdit();
                    }
                    catch (WebAdminException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "Saving the game data (quest editor) failed");
                        throw new WebAdminException(500, "Saving the game data failed (see the server log); nothing was changed.");
                    }
                });
                var result = fn(svc);
                return (result, saved);
            }
        }
    }
}
