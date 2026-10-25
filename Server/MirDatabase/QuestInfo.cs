using Server.MirEnvir;

namespace Server.MirDatabase
{
    /// <summary>
    /// One quest: its id, name and group, plus the quest V2 definition (<see cref="Definition"/>, QuestDefinitionModels.cs), which is
    /// what the runtime reads. The legacy fields (quest body file, level / class / required-quest gates, type, link words, time limit)
    /// and the body parsing were removed in wave 4 (UserData-and-Quests-Plan.md, Part B phase 4): SystemData.db dropped their columns
    /// with a written migration (SqliteWorldFile.DropLegacyQuestColumns).
    /// </summary>
    public partial class QuestInfo
    {
        public int Index;

        public string
            Name = string.Empty,
            Group = string.Empty;

        public QuestInfo() { }

        /// <summary>
        /// The binary Server.MirDB layout (import of an old file, tools and tests only). The legacy fields are read and discarded
        /// (no conversion: quest V2 definitions live only in SystemData.db), so a quest imported from Server.MirDB has no definition.
        /// </summary>
        public QuestInfo(BinaryReader reader)
        {
            Index = reader.ReadInt32();
            Name = reader.ReadString();
            Group = reader.ReadString();
            reader.ReadString(); // FileName (legacy body file)
            reader.ReadInt32(); // RequiredMinLevel
            reader.ReadInt32(); // RequiredMaxLevel
            reader.ReadInt32(); // RequiredQuest
            reader.ReadByte(); // RequiredClass
            reader.ReadByte(); // QuestType
            reader.ReadString(); // GotoMessage
            reader.ReadString(); // KillMessage
            reader.ReadString(); // ItemMessage
            reader.ReadString(); // FlagMessage

            if (Envir.LoadVersion > 90)
                reader.ReadInt32(); // TimeLimitInSeconds
        }

        /// <summary>
        /// The binary Server.MirDB layout (MirDbExport export-binary, tests): the legacy fields are written as a fresh legacy quest
        /// held them (no body file, no gates), so the layout stays readable by every binary reader.
        /// </summary>
        public void Save(BinaryWriter writer)
        {
            writer.Write(Index);
            writer.Write(Name);
            writer.Write(Group);
            writer.Write(string.Empty); // FileName
            writer.Write(0); // RequiredMinLevel
            writer.Write(0); // RequiredMaxLevel (0 = no limit)
            writer.Write(0); // RequiredQuest
            writer.Write((byte)RequiredClass.None);
            writer.Write((byte)0); // QuestType.General
            writer.Write(string.Empty); // GotoMessage
            writer.Write(string.Empty); // KillMessage
            writer.Write(string.Empty); // ItemMessage
            writer.Write(string.Empty); // FlagMessage
            writer.Write(0); // TimeLimitInSeconds
        }

        public override string ToString()
        {
            return string.Format("{0}:   {1}", Index, Name);
        }
    }
}
