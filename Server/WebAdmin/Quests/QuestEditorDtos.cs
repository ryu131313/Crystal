namespace Server.WebAdmin.Quests
{
    // Wire shapes of the web admin Quests editor (UserData-and-Quests-Plan.md, Part B, "Authoring"). Serialised with WebAdminJson.Options:
    // camelCase fields, enums as their numbers (the page reads the names from GET /api/admin/quests/meta).
    //
    // Ids: a positive id names an existing row (it must already belong to the quest being saved); a new row carries a temporary id
    // <= TempIdLimit (the page counts down from -1000), which references inside the same graph may use and the save replaces with a fresh
    // counter id. -1 stays "ready to turn in" for NextStep and 0 stays "none" everywhere.

    /// <summary>One quest graph: the quest row, its child rows, its owned conditions and the texts its keys point to.</summary>
    public sealed class QuestGraphDto
    {
        /// <summary>Temporary ids are at or below this value.</summary>
        public const int TempIdLimit = -1000;

        public int Index;                 // 0 = a new quest
        public string Rev;                // concurrency token of the read (null for a new quest)
        public string Name;               // QuestInfo.Name (display; set from the name text when a quest is created)
        public int Revision;              // read only
        public string ShapeHash;          // read only (a long as text: JavaScript numbers lose precision above 2^53)

        public string AuthorKey;
        public string NameKey;
        public string SummaryKey;
        public string CompleteKey;
        public int Category;
        public int StorylineIndex;
        public int StorylineOrder;
        public int LevelHint;
        public int AcceptCondition;
        public int VisibleCondition;
        public int RepeatPolicy;
        public int CooldownSeconds;
        public int MaxCompletions;
        public int ShareMode;
        public int CreditMode;
        public int TimeLimitSeconds;
        public int FailPolicy;
        public bool AbandonAllowed = true;
        public int PoolIndex;
        public bool StartItemConsumed;
        public int Flags;
        public string GroupName;

        public List<QuestStepDto> Steps = new List<QuestStepDto>();
        public List<QuestObjectiveDto> Objectives = new List<QuestObjectiveDto>();
        public List<QuestBranchDto> Branches = new List<QuestBranchDto>();
        public List<QuestEndpointDto> Endpoints = new List<QuestEndpointDto>();
        public List<QuestRewardDto> Rewards = new List<QuestRewardDto>();
        public List<QuestVarDto> Vars = new List<QuestVarDto>();

        /// <summary>The conditions this quest owns (OwnerQuest = this quest): the whole set; owned rows left out are deleted.</summary>
        public List<QuestConditionDto> Conditions = new List<QuestConditionDto>();

        /// <summary>Text key → body (Lang = "", the server language) for the keys the graph references; a save upserts these rows.</summary>
        public Dictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class QuestStepDto
    {
        public int Index;
        public int Order;
        public string TitleKey;
        public string TextKey;
        public string ReadyTextKey;
        public int CompletionMode;
        public int NRequired;
        public int NextStep;
        public int TimeLimitSeconds;
        public int FailCondition;
        public string OnEnterPage;
        public string OnCompletePage;
    }

    public sealed class QuestObjectiveDto
    {
        public int Index;
        public int Step;
        public int Order;
        public int Type;
        public int PartyCredit;
        public int TargetKind;
        public int TargetId;
        public int Count = 1;
        public int Map;
        public int X;
        public int Y;
        public int Radius;
        public bool Optional;
        public bool Hidden;
        public int Condition;
        public string TextKey;
        public int DropChance;
        public string Params;
    }

    public sealed class QuestBranchDto
    {
        public int Step;
        public string ChoiceKey;
        public string TextKey;
        public int NextStep;
        public int Outcome;
        public int Condition;
    }

    public sealed class QuestEndpointDto
    {
        public int Role;
        public int Kind;
        public int TargetId;
        public int Map;
    }

    public sealed class QuestRewardDto
    {
        public int Scope;
        public int ScopeId;
        public int Kind;
        public int TargetId;
        public long Amount;
        public int Scaling;
        public int ChoiceGroup;
        public int Condition;
        public bool FirstTimeOnly;
    }

    public sealed class QuestVarDto
    {
        public int Index;
        public string AuthorKey;
        public int Default;
    }

    public sealed class QuestConditionDto
    {
        public int Index;
        public int Op;
        public int Parent;
        public int Order;
        public int Leaf;
        public int Arg1;
        public int Arg2;
        public int Cmp;
    }

    /// <summary>A quest target group (questgroups + members), edited from the Quests page.</summary>
    public sealed class QuestGroupDto
    {
        public int Index;                 // 0 = new
        public string AuthorKey;
        public int Kind;
        public List<int> Members = new List<int>();
    }

    /// <summary>One validation finding as the page shows it.</summary>
    public sealed class QuestProblemDto
    {
        public int Quest;
        public bool Warning;
        public string Message;
        public string RefKind;            // "quest" | "step" | "objective" | "condition" | "variable" | "endpoint" | "reward" | "branch"
        public int RefId;                 // the row id as the page knows it (a temporary id for a row that is not saved yet); 0 = none
    }

    /// <summary>One "where used" reference.</summary>
    public sealed class QuestUseDto
    {
        public int Quest;                 // 0 = a shared row (group / shared condition / pool)
        public string QuestLabel;
        public string Where;
    }

    /// <summary>Outcome of a save, delete or import apply.</summary>
    public sealed class QuestSaveResult
    {
        public int Index;
        public bool Created;
        public bool Deleted;
        public bool DryRun;
        public string Rev;
        public List<QuestProblemDto> Problems = new List<QuestProblemDto>();

        /// <summary>Every quest the change touches (for <see cref="QuestEditNotifier.QuestsChanged"/>).</summary>
        public List<int> ChangedQuests = new List<int>();
        public bool SharedTablesChanged;

        /// <summary>What the quest-replace hook did with the change (null for a dry run or a preview).</summary>
        public QuestLiveOutcome Live;

        /// <summary>Import preview only: what the import would change.</summary>
        public List<string> Diff;
        public string Mode;               // import: "create" | "update"
        public string Token;              // import preview: pass back to apply
    }

    /// <summary>The per-quest export file (format crystal-quest/1): ids authoritative, names of referenced rows written for diffs.</summary>
    public sealed class QuestExportFile
    {
        public string Format;
        public DateTime ExportedUtc;
        public QuestGraphDto Quest;

        /// <summary>Referenced rows by id: kind ("monster", "item", "npc", "map", "quest", "group", "condition", "storyline", "pool") → id → name.</summary>
        public Dictionary<string, Dictionary<string, string>> Refs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>The target groups the quest uses, whole (members as ids).</summary>
        public List<QuestGroupDto> Groups = new List<QuestGroupDto>();
    }
}
