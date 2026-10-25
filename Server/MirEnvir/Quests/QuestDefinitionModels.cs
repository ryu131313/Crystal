using Server.MirEnvir.Quests;

namespace Server.MirDatabase
{
    public partial class QuestInfo
    {
        /// <summary>
        /// The quest V2 definition (UserData-and-Quests-Plan.md, Part B): the V2 columns of the quests table and its child lists. One field,
        /// of a Server.MirEnvir type, so the persistence oracle (which walks the binary-loaded data and skips Server.MirEnvir types) is
        /// unchanged: Server.MirDB never carries these.
        /// </summary>
        public QuestDefinition Definition = new QuestDefinition();

        /// <summary>True when the quest has V2 rows (a quest imported from an old Server.MirDB has none).</summary>
        public bool HasDefinition => Definition.Steps.Count > 0;
    }
}

namespace Server.MirEnvir.Quests
{
    // Quest system V2 definition model (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B, "Definition model"). System data in
    // SystemData.db: every public scalar field below is a column of its table (SqliteWorldCodec specs, GameDataRegistry entries, read-only in
    // the generic grid). Enums are stored by value (explicit values in Shared/Enums.cs, append only). Ids come from the Envir counters and are
    // never reused. Authored in the web admin Quests page (Server/WebAdmin/Quests); the runtime reads them through Envir.QuestCatalog.
    //
    // Column names: the spec's "Key" columns are AuthorKey (and TextKey in texts), because every store table already has a "key" column
    // (the row key) and SQLite column names ignore case.

    /// <summary>Quest V2 columns of the <c>quests</c> table (QuestInfo.Definition) and its child lists.</summary>
    public sealed class QuestDefinition
    {
        public string AuthorKey = string.Empty;
        public string NameKey = string.Empty;
        public string SummaryKey = string.Empty;
        public string CompleteKey = string.Empty;
        public QuestCategory Category;
        public int StorylineIndex;
        public int StorylineOrder;
        public int LevelHint;
        public int AcceptCondition;
        public int VisibleCondition;
        public QuestRepeatPolicy RepeatPolicy;
        public int CooldownSeconds;
        public int MaxCompletions;
        public QuestShareMode ShareMode;
        public QuestCreditMode CreditMode;
        public int TimeLimitSeconds;
        public QuestFailPolicy FailPolicy;
        public bool AbandonAllowed = true;
        public int Revision;
        public long ShapeHash;
        public int PoolIndex;
        public bool StartItemConsumed;
        public QuestFlags Flags;
        public string GroupName = string.Empty;

        public List<QuestStepInfo> Steps = new List<QuestStepInfo>();
        public List<QuestObjectiveInfo> Objectives = new List<QuestObjectiveInfo>();
        public List<QuestBranchInfo> Branches = new List<QuestBranchInfo>();
        public List<QuestEndpointInfo> Endpoints = new List<QuestEndpointInfo>();
        public List<QuestRewardInfo> Rewards = new List<QuestRewardInfo>();
        public List<QuestVarInfo> Vars = new List<QuestVarInfo>();

        /// <summary>A copy with its own lists (the rows themselves are shared).</summary>
        public QuestDefinition ShallowCopy()
        {
            var copy = (QuestDefinition)MemberwiseClone();
            copy.Steps = new List<QuestStepInfo>(Steps);
            copy.Objectives = new List<QuestObjectiveInfo>(Objectives);
            copy.Branches = new List<QuestBranchInfo>(Branches);
            copy.Endpoints = new List<QuestEndpointInfo>(Endpoints);
            copy.Rewards = new List<QuestRewardInfo>(Rewards);
            copy.Vars = new List<QuestVarInfo>(Vars);
            return copy;
        }

        /// <summary>
        /// A copy with copies of every row (the wave 3 quest-replace hook: the running world never shares a row object with Envir.Edit,
        /// server.md §3.4). Rows hold scalars and strings only.
        /// </summary>
        public QuestDefinition DeepCopy()
        {
            var copy = (QuestDefinition)MemberwiseClone();
            copy.Steps = Steps.ConvertAll(r => r?.Copy());
            copy.Objectives = Objectives.ConvertAll(r => r?.Copy());
            copy.Branches = Branches.ConvertAll(r => r?.Copy());
            copy.Endpoints = Endpoints.ConvertAll(r => r?.Copy());
            copy.Rewards = Rewards.ConvertAll(r => r?.Copy());
            copy.Vars = Vars.ConvertAll(r => r?.Copy());
            return copy;
        }
    }

    /// <summary><c>queststeps</c>, child of quests; keyed by its stable Index (counter QuestStepIndex).</summary>
    public sealed class QuestStepInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestStepInfo Copy() => (QuestStepInfo)MemberwiseClone();

        public int Index;
        public int Order;
        public string TitleKey = string.Empty;
        public string TextKey = string.Empty;
        public string ReadyTextKey = string.Empty;
        public QuestStepCompletion CompletionMode;
        public int NRequired;
        /// <summary>0 = the next step by Order; -1 = ready to turn in.</summary>
        public int NextStep;
        public int TimeLimitSeconds;
        public int FailCondition;
        public string OnEnterPage = string.Empty;
        public string OnCompletePage = string.Empty;
    }

    /// <summary><c>questobjectives</c>, child of quests (Step names the step); keyed by its stable Index (counter QuestObjectiveIndex).</summary>
    public sealed class QuestObjectiveInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestObjectiveInfo Copy() => (QuestObjectiveInfo)MemberwiseClone();

        public int Index;
        public int Step;
        public int Order;
        public QuestObjectiveType Type;
        public QuestPartyCredit PartyCredit;
        public QuestTargetKind TargetKind;
        public int TargetId;
        public int Count = 1;
        public int Map;
        public int X;
        public int Y;
        public int Radius;
        public bool Optional;
        public bool Hidden;
        public int Condition;
        public string TextKey = string.Empty;
        /// <summary>Per 10,000; Collect objectives only. 0 = the item comes from the normal drop files only.</summary>
        public int DropChance;
        /// <summary>JSON, reserved for the later objective types; empty for every designed type.</summary>
        public string Params = string.Empty;
    }

    /// <summary><c>questbranches</c>, child of quests (position keyed).</summary>
    public sealed class QuestBranchInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestBranchInfo Copy() => (QuestBranchInfo)MemberwiseClone();

        public int Step;
        public string ChoiceKey = string.Empty;
        public string TextKey = string.Empty;
        public int NextStep;
        public int Outcome;
        public int Condition;
    }

    /// <summary><c>questendpoints</c>, child of quests (position keyed). Npc endpoints hold NPCInfo.Index, never a runtime ObjectID.</summary>
    public sealed class QuestEndpointInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestEndpointInfo Copy() => (QuestEndpointInfo)MemberwiseClone();

        public QuestEndpointRole Role;
        public QuestEndpointKind Kind;
        public int TargetId;
        public int Map;
    }

    /// <summary><c>questrewards</c>, child of quests (position keyed).</summary>
    public sealed class QuestRewardInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestRewardInfo Copy() => (QuestRewardInfo)MemberwiseClone();

        public QuestRewardScope Scope;
        public int ScopeId;
        public QuestRewardKind Kind;
        public int TargetId;
        public long Amount;
        public QuestRewardScaling Scaling;
        /// <summary>0 = always given; n = pick one from group n.</summary>
        public int ChoiceGroup;
        public int Condition;
        public bool FirstTimeOnly;
    }

    /// <summary><c>questvars</c>, child of quests; keyed by its stable Index (counter QuestVarIndex).</summary>
    public sealed class QuestVarInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestVarInfo Copy() => (QuestVarInfo)MemberwiseClone();

        public int Index;
        public string AuthorKey = string.Empty;
        public int Default;
    }

    /// <summary><c>conditions</c> (top level, shared): one node of a condition tree (counter QuestConditionIndex).</summary>
    public sealed class QuestConditionInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestConditionInfo Copy() => (QuestConditionInfo)MemberwiseClone();

        public int Index;
        public QuestConditionOp Op;
        /// <summary>Parent condition id (0 = a root); children are ordered by Order.</summary>
        public int Parent;
        public int Order;
        public QuestConditionLeaf Leaf;
        public int Arg1;
        public int Arg2;
        public QuestCompare Cmp;
        /// <summary>The quest that owns this node (0 = shared, for other systems).</summary>
        public int OwnerQuest;
    }

    /// <summary><c>texts</c> (top level, shared), unique on (TextKey, Lang); Lang is always "" (server language, decision 8).</summary>
    public sealed class QuestTextInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestTextInfo Copy() => (QuestTextInfo)MemberwiseClone();

        public int Index;
        public string TextKey = string.Empty;
        public string Lang = string.Empty;
        public string Body = string.Empty;
    }

    /// <summary><c>storylines</c> (top level, counter StorylineIndex).</summary>
    public sealed class StorylineInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public StorylineInfo Copy() => (StorylineInfo)MemberwiseClone();

        public int Index;
        public string AuthorKey = string.Empty;
        public string NameKey = string.Empty;
        public int Order;
    }

    /// <summary><c>questgroups</c> (top level, counter QuestGroupIndex) with <c>questgroupmembers</c>.</summary>
    public sealed class QuestGroupInfo
    {
        /// <summary>A copy with copies of its members.</summary>
        public QuestGroupInfo Copy()
        {
            var copy = (QuestGroupInfo)MemberwiseClone();
            copy.Members = Members.ConvertAll(m => m?.Copy());
            return copy;
        }

        public int Index;
        public string AuthorKey = string.Empty;
        public QuestGroupKind Kind;
        public List<QuestGroupMemberInfo> Members = new List<QuestGroupMemberInfo>();
    }

    /// <summary><c>questgroupmembers</c>, child of questgroups (position keyed): a monster or item id.</summary>
    public sealed class QuestGroupMemberInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestGroupMemberInfo Copy() => (QuestGroupMemberInfo)MemberwiseClone();

        public int TargetId;
    }

    /// <summary><c>questpools</c> (top level, counter QuestPoolIndex) with <c>questpoolmembers</c>.</summary>
    public sealed class QuestPoolInfo
    {
        /// <summary>A copy with copies of its members.</summary>
        public QuestPoolInfo Copy()
        {
            var copy = (QuestPoolInfo)MemberwiseClone();
            copy.Members = Members.ConvertAll(m => m?.Copy());
            return copy;
        }

        public int Index;
        public string AuthorKey = string.Empty;
        public int Picks;
        public QuestRotatePolicy RotatePolicy;
        public List<QuestPoolMemberInfo> Members = new List<QuestPoolMemberInfo>();
    }

    /// <summary><c>questpoolmembers</c>, child of questpools (position keyed).</summary>
    public sealed class QuestPoolMemberInfo
    {
        /// <summary>A field-by-field copy (scalars and strings only).</summary>
        public QuestPoolMemberInfo Copy() => (QuestPoolMemberInfo)MemberwiseClone();

        public int Quest;
        public int Weight = 1;
    }
}
