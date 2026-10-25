namespace Client.MirScenes.Dialogs
{
    // Client-only view models of the classic quest dialogs (QuestDialogs.cs), filled by the quest V2 classic adapter (QuestV2Classic).
    // They keep the shapes of the legacy wire types they replace (Shared ClientQuestInfo / ClientQuestProgress / QuestItemReward, removed
    // with the legacy quest packets in quest V2 wave 4), so the classic dialogs draw exactly as before. Never serialised.

    /// <summary>The classic dialogs' quest kind (picks the quest icon colour); the adapter maps QuestCategory onto it.</summary>
    public enum ClassicQuestKind : byte
    {
        General = 0,
        Daily = 1,
        Repeatable = 2,
        Story = 3,
    }

    /// <summary>An item reward row of the classic reward panel.</summary>
    public sealed class QuestItemReward
    {
        public ItemInfo Item;
        public ushort Count = 1;
    }

    /// <summary>One quest as the classic dialogs draw it (texts, level hint, rewards, the giver / finisher NPC of the open dialog).</summary>
    public sealed class ClientQuestInfo
    {
        public int Index;

        public uint NPCIndex;

        public string Name, Group;
        public List<string> Description = new List<string>();
        public List<string> TaskDescription = new List<string>();
        public List<string> ReturnDescription = new List<string>();
        public List<string> CompletionDescription = new List<string>();

        public int MinLevelNeeded, MaxLevelNeeded;
        public int QuestNeeded;
        public RequiredClass ClassNeeded;

        public ClassicQuestKind Kind;

        public int TimeLimitInSeconds = 0;

        public uint RewardGold;
        public uint RewardExp;
        public uint RewardCredit;
        public List<QuestItemReward> RewardsFixedItem = new List<QuestItemReward>();
        public List<QuestItemReward> RewardsSelectItem = new List<QuestItemReward>();

        public uint FinishNPCIndex;

        public bool SameFinishNPC
        {
            get { return NPCIndex == FinishNPCIndex; }
        }

        public QuestIcon GetQuestIcon(bool taken = false, bool completed = false)
        {
            QuestIcon icon = QuestIcon.None;

            switch (Kind)
            {
                case ClassicQuestKind.General:
                case ClassicQuestKind.Repeatable:
                    if (completed)
                        icon = QuestIcon.QuestionYellow;
                    else if (taken)
                        icon = QuestIcon.QuestionWhite;
                    else
                        icon = QuestIcon.ExclamationYellow;
                    break;
                case ClassicQuestKind.Daily:
                    if (completed)
                        icon = QuestIcon.QuestionBlue;
                    else if (taken)
                        icon = QuestIcon.QuestionWhite;
                    else
                        icon = QuestIcon.ExclamationBlue;
                    break;
                case ClassicQuestKind.Story:
                    if (completed)
                        icon = QuestIcon.QuestionGreen;
                    else if (taken)
                        icon = QuestIcon.QuestionWhite;
                    else
                        icon = QuestIcon.ExclamationGreen;
                    break;
            }

            return icon;
        }
    }

    /// <summary>A quest's state as the classic dialogs draw it (the task lines and the taken / ready / new flags).</summary>
    public sealed class ClientQuestProgress
    {
        public int Id;

        public ClientQuestInfo QuestInfo;

        public List<string> TaskList = new List<string>();

        public bool Taken;
        public bool Completed;
        public bool New;

        public QuestIcon Icon
        {
            get
            {
                return QuestInfo.GetQuestIcon(Taken, Completed);
            }
        }
    }
}
