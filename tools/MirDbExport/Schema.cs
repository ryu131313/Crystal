namespace MirDbExport
{
    /// <summary>
    /// Names in the exported SQLite file (shared by the exporter and the verifier: names only, never encoding).
    /// Bump <see cref="Version"/> whenever the layout or an encoding rule changes.
    /// Registry-driven column additions (a new public field on a game-data model, e.g. gameshop DealGoldPrice/DealCreditPrice
    /// in DB v119) do not bump it: the column set follows meta.envir_version, the layout rule stays "one column per GameDataColumn".
    /// </summary>
    internal static class Schema
    {
        public const int Version = 1;

        public const string MetaTable = "meta";
        public const string CountersTable = "index_counters";

        public const string KeyColumn = "key";
        public const string ParentColumn = "parent_index";
        public const string PositionColumn = "position";

        /// <summary>The Envir.Edit index counters persisted in Server.MirDB's header, in file order.</summary>
        public static readonly string[] Counters =
        {
            "MapIndex", "ItemIndex", "MonsterIndex", "NPCIndex", "QuestIndex", "GameshopIndex", "ConquestIndex", "RespawnIndex",
        };

        public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
