using Microsoft.Data.Sqlite;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Quest V2 wave 4 (UserData-and-Quests-Plan.md, Part B phase 4; deploy D3): a D2-era SystemData.db (legacy quests columns, LegacySource,
/// EditorOwned, the NPC quest lists, the legacy quest bodies in the design data, the importer's meta key, schema_version 3) is migrated
/// when the writer opens it: a pre-migration VACUUM INTO copy first, then the legacy data is dropped, schema_version becomes 4 (the D2
/// build reads 2..3, so it refuses the migrated file: the D3 rollback is the copy) and the quest V2 definitions load unchanged.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class QuestLegacyDropTests
{
    [Fact]
    public void A_D2_store_drops_the_legacy_quest_data_after_a_copy_and_keeps_every_V2_definition()
    {
        var dir = NewTempFolder("quest-legacy-drop");
        InFolder(dir, () =>
        {
            var e = QuestDefinitionTests.World();
            var rows = SqliteWorldCodec.Encode(e, 0);
            var db = Path.Combine(dir, "SystemData.db");
            SqliteWorldStore.Create(db, rows, null);

            // A first open adds the design tables (as on the live store); then the file is turned into what the D2 build left.
            var first = new SqliteWriter(db, "legacy drop test writer 1");
            try { first.Run(w => SqliteWorldFile.ReadAll(w.Connection)); }
            finally { first.Shutdown(); }

            using (var conn = SqliteWorldFile.Open(db, create: false))
            {
                foreach (var (table, column, ddl, value) in new (string, string, string, string)[]
                {
                    ("quests", "FileName", "TEXT NOT NULL DEFAULT ''", "'Wolves'"),
                    ("quests", "RequiredMinLevel", "INTEGER NOT NULL DEFAULT 0", "3"),
                    ("quests", "RequiredMaxLevel", "INTEGER NOT NULL DEFAULT 0", "65535"),
                    ("quests", "RequiredQuest", "INTEGER NOT NULL DEFAULT 0", "0"),
                    ("quests", "RequiredClass", "INTEGER NOT NULL DEFAULT 31", "31"),
                    ("quests", "Type", "INTEGER NOT NULL DEFAULT 0", "1"),
                    ("quests", "GotoMessage", "TEXT NOT NULL DEFAULT ''", "'Go'"),
                    ("quests", "KillMessage", "TEXT NOT NULL DEFAULT ''", "'Hunt'"),
                    ("quests", "ItemMessage", "TEXT NOT NULL DEFAULT ''", "''"),
                    ("quests", "FlagMessage", "TEXT NOT NULL DEFAULT ''", "''"),
                    ("quests", "TimeLimitInSeconds", "INTEGER NOT NULL DEFAULT 0", "600"),
                    ("quests", "EditorOwned", "INTEGER NOT NULL DEFAULT 0", "1"),
                    ("queststeps", "LegacySource", "TEXT NOT NULL DEFAULT ''", "'step'"),
                    ("questobjectives", "LegacySource", "TEXT NOT NULL DEFAULT ''", "'[@KILLTASKS] Wolf'"),
                    ("questendpoints", "LegacySource", "TEXT NOT NULL DEFAULT ''", "'script Guide'"),
                    ("questrewards", "LegacySource", "TEXT NOT NULL DEFAULT ''", "''"),
                    ("npcs", "CollectQuestIndexes", "TEXT NOT NULL DEFAULT '[]'", "'[1]'"),
                    ("npcs", "FinishQuestIndexes", "TEXT NOT NULL DEFAULT '[]'", "'[]'"),
                })
                {
                    Exec(conn, "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + ddl);
                    Exec(conn, "UPDATE \"" + table + "\" SET \"" + column + "\" = " + value);
                }

                Exec(conn, "CREATE TABLE quest_lines (name TEXT NOT NULL, line INTEGER NOT NULL, section TEXT, ref_kind TEXT, ref_index INTEGER, ref_name TEXT, rest TEXT, text TEXT, PRIMARY KEY (name, line)) STRICT");
                Exec(conn, "INSERT INTO quest_lines (name, line, text) VALUES ('Wolves', 0, '[@KILLTASKS]')");
                Exec(conn, "INSERT INTO design_files (kind, name, imported_utc, updated_utc, newline, final_newline, bom) VALUES ('quests', '', 'x', 'x', '\r\n', 1, 0)");
                Exec(conn, "INSERT INTO design_files (kind, name, imported_utc, updated_utc, newline, final_newline, bom) VALUES ('quests', 'Wolves', 'x', 'x', '\r\n', 1, 0)");
                Exec(conn, "INSERT INTO meta (name, value) VALUES ('" + SqliteWorldFile.LegacyQuestImporterMetaKey + "', 2)");
                Exec(conn, "UPDATE meta SET value = 3 WHERE name = 'schema_version'");
                Assert.Throws<InvalidDataException>(() => SqliteWorldFile.ReadAll(conn)); // unknown columns: this build needs the migration
            }

            var writer = new SqliteWriter(db, "legacy drop test writer 2");
            try
            {
                var back = writer.Run(w => SqliteWorldFile.ReadAll(w.Connection)); // opening the store runs the written migration
                Assert.Null(SqliteWorldStore.FirstDifference(rows, back));

                var loaded = new Envir();
                lock (Envir.LoadLock) SqliteWorldCodec.Decode(back, loaded);
                var q = Assert.Single(loaded.QuestInfoList);
                Assert.Equal("Wolves", q.Name);
                Assert.Equal(2, q.Definition.Steps.Count);
                Assert.Equal(5_000_000_000L, q.Definition.Rewards[0].Amount);

                writer.Run(w =>
                {
                    var c = w.Connection;
                    Assert.Equal(4L, Scalar(c, "SELECT value FROM meta WHERE name = 'schema_version'"));
                    Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM pragma_table_info('quests') WHERE name IN ('FileName', 'Type', 'EditorOwned')"));
                    Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM pragma_table_info('queststeps') WHERE name = 'LegacySource'"));
                    Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM pragma_table_info('npcs') WHERE name = 'CollectQuestIndexes'"));
                    Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM sqlite_master WHERE name = 'quest_lines'"));
                    Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM design_files WHERE kind = 'quests'"));
                    Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM meta WHERE name = '" + SqliteWorldFile.LegacyQuestImporterMetaKey + "'"));
                    Assert.Equal(0, SqliteWorldFile.DropLegacyQuestColumns(c, _ => { })); // idempotent
                    return 0;
                });
            }
            finally
            {
                writer.Shutdown();
            }

            // The pre-migration copy is the D2 file: schema_version 3 and every legacy column, which is what the D2 build runs on.
            var copy = Assert.Single(Directory.GetFiles(Path.Combine(dir, "Back Up", "Database"), "Pre-quest-legacy-drop *.bak"));
            using (var conn = SqliteWorldFile.Open(copy, create: false))
            {
                Assert.Equal(3L, Scalar(conn, "SELECT value FROM meta WHERE name = 'schema_version'"));
                Assert.Equal("Wolves", Scalar(conn, "SELECT FileName FROM quests"));
                Assert.Equal(2L, Scalar(conn, "SELECT count(*) FROM design_files WHERE kind = 'quests'"));
            }

            return 0;
        });
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
