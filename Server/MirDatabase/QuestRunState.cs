namespace Server.MirDatabase
{
    // Quest V2 character state (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Character state"), saved by
    // CharacterInfo from Envir.Version 121 on: each list is a count followed by its records, in the field order below. Times
    // are DateTime.ToBinary() (UTC), 0 = none. Never holds a runtime (dungeon) map index. Game-thread owned, like CharacterInfo.

    /// <summary>One active or failed quest run. Ready is derived at runtime and never saved (Status is Active or Failed).</summary>
    public sealed class QuestRunState
    {
        public int Quest;
        public QuestRunStatus Status;
        /// <summary>Current step id, or -1 = turn-in pending (every step done).</summary>
        public int Step;
        /// <summary>Definition revision when last migrated.</summary>
        public int Revision;
        public long AcceptedAt;
        /// <summary>0 = no time limit.</summary>
        public long ExpiresAt;
        /// <summary>0 = no step limit.</summary>
        public long StepExpiresAt;
        /// <summary>Character index of the sharer, 0 = none.</summary>
        public int SharedFrom;
        /// <summary>Outcome chosen in this run, 0 = none.</summary>
        public int Branch;

        public QuestRunState() { }

        public QuestRunState(BinaryReader reader)
        {
            Quest = reader.ReadInt32();
            Status = (QuestRunStatus)reader.ReadByte();
            Step = reader.ReadInt32();
            Revision = reader.ReadInt32();
            AcceptedAt = reader.ReadInt64();
            ExpiresAt = reader.ReadInt64();
            StepExpiresAt = reader.ReadInt64();
            SharedFrom = reader.ReadInt32();
            Branch = reader.ReadInt32();
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Quest);
            writer.Write((byte)Status);
            writer.Write(Step);
            writer.Write(Revision);
            writer.Write(AcceptedAt);
            writer.Write(ExpiresAt);
            writer.Write(StepExpiresAt);
            writer.Write(SharedFrom);
            writer.Write(Branch);
        }
    }

    /// <summary>
    /// Progress of one objective (no row = 0). Never for Collect, Deliver or Flag objectives, whose progress is read live from
    /// the quest bag, the inventory or Flags[].
    /// </summary>
    public sealed class QuestObjectiveState
    {
        public int Quest;
        /// <summary>Objective id (not a monster or item id).</summary>
        public int Objective;
        /// <summary>Count, or a bitmask for multi-spot objectives.</summary>
        public int Value;

        public QuestObjectiveState() { }

        public QuestObjectiveState(BinaryReader reader)
        {
            Quest = reader.ReadInt32();
            Objective = reader.ReadInt32();
            Value = reader.ReadInt32();
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Quest);
            writer.Write(Objective);
            writer.Write(Value);
        }
    }

    /// <summary>One quest ever completed (replaces the legacy CompletedQuests list).</summary>
    public sealed class QuestHistoryEntry
    {
        public int Quest;
        public int Times;
        public long FirstAt;
        public long LastAt;
        public int LastOutcome;

        public QuestHistoryEntry() { }

        public QuestHistoryEntry(BinaryReader reader)
        {
            Quest = reader.ReadInt32();
            Times = reader.ReadInt32();
            FirstAt = reader.ReadInt64();
            LastAt = reader.ReadInt64();
            LastOutcome = reader.ReadInt32();
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Quest);
            writer.Write(Times);
            writer.Write(FirstAt);
            writer.Write(LastAt);
            writer.Write(LastOutcome);
        }
    }

    /// <summary>
    /// A Step / Outcome reward already paid in the current cycle of a quest (Scope + ScopeId of the questrewards rows), so abandoning
    /// and retaking the quest cannot pay it again. Cleared at turn-in (a repeatable quest pays again on its next cycle) and by an
    /// admin reset; an abandon keeps it.
    /// </summary>
    public sealed class QuestRewardPaid
    {
        public int Quest;
        public QuestRewardScope Scope;
        public int ScopeId;

        public QuestRewardPaid() { }

        public QuestRewardPaid(BinaryReader reader)
        {
            Quest = reader.ReadInt32();
            Scope = (QuestRewardScope)reader.ReadByte();
            ScopeId = reader.ReadInt32();
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Quest);
            writer.Write((byte)Scope);
            writer.Write(ScopeId);
        }
    }

    /// <summary>A quest-scoped variable (Key = the variable id from the definition).</summary>
    public sealed class QuestVarState
    {
        public int Quest;
        public int Key;
        public int Value;

        public QuestVarState() { }

        public QuestVarState(BinaryReader reader)
        {
            Quest = reader.ReadInt32();
            Key = reader.ReadInt32();
            Value = reader.ReadInt32();
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Quest);
            writer.Write(Key);
            writer.Write(Value);
        }
    }
}
