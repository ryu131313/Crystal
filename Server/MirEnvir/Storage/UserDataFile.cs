using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>One row of UserData.db as read (Data is a copy owned by the reader).</summary>
    internal readonly struct UserDataRow
    {
        public readonly int Key;
        public readonly string Id;   // accounts.account_id; null for the other tables
        public readonly byte[] Data;

        public UserDataRow(int key, string id, byte[] data)
        {
            Key = key;
            Id = id;
            Data = data;
        }
    }

    /// <summary>Everything a load or the rollback export reads from UserData.db, or what an import writes into a new one.</summary>
    internal sealed class UserDataRows
    {
        public byte[] Head, Tail;
        public long? EnvirVersion; // meta.envir_version
        public readonly List<UserDataRow> Accounts = new List<UserDataRow>();
        public readonly List<UserDataRow> Guilds = new List<UserDataRow>();
        public readonly List<UserDataRow> Goods = new List<UserDataRow>();
        public readonly List<UserDataRow> Conquests = new List<UserDataRow>();
    }

    /// <summary>A row of a save job: a range of the job's buffer (no copy).</summary>
    internal struct UserRowRef
    {
        public int Key;
        public string Id;
        public int Offset, Length;

        public UserRowRef(int key, string id, int offset, int length)
        {
            Key = key;
            Id = id;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// One SaveUserData job (UserData-and-Quests-Plan.md Part A, Save). Envir keeps one instance and reuses it: jobs never overlap
    /// (every save is gated on Envir.Saving), and the game thread fills it only while no job reads it. The writer thread only reads it.
    /// </summary>
    internal sealed class UserDataSaveJob
    {
        public byte[] Buffer;
        public int HeadOffset, HeadLength, TailOffset, TailLength;
        public readonly List<UserRowRef> Accounts = new List<UserRowRef>();
        public readonly List<UserRowRef> Guilds = new List<UserRowRef>();
        public readonly List<UserRowRef> Goods = new List<UserRowRef>();
        public readonly List<UserRowRef> Conquests = new List<UserRowRef>();
        public int[] AccountDeletes, GuildDeletes; // null = none
        public int EnvirVersion;
        /// <summary>VACUUM INTO this file after the commit (a periodic backup); null = none.</summary>
        public string BackupPath;

        public void Clear()
        {
            Buffer = null;
            HeadOffset = HeadLength = TailOffset = TailLength = 0;
            Accounts.Clear();
            Guilds.Clear();
            Goods.Clear();
            Conquests.Clear();
            AccountDeletes = GuildDeletes = null;
            BackupPath = null;
        }
    }

    /// <summary>
    /// The UserData.db file format (Part A, Tables): meta, world (head + tail blobs), accounts, guilds, goods, conquests; journal_mode
    /// DELETE, synchronous FULL (<see cref="SqliteWorldFile.Open"/>). Writer thread only, except on a file nothing else has open
    /// (an import's .tmp, a restore candidate, the rollback export of a copy).
    /// </summary>
    internal static class UserDataFile
    {
        public const int SchemaVersion = 1;

        private static readonly string[] Tables = { "meta", "world", "accounts", "guilds", "goods", "conquests" };

        /// <summary>The writer's per-file open: the file must exist; integrity, schema and the world row are checked.</summary>
        public static SqliteConnection Open(string path)
        {
            var conn = SqliteWorldFile.Open(path, create: false);
            try
            {
                SqliteWorldFile.CheckIntegrity(conn);
                CheckSchema(conn);
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        public static void CreateSchema(SqliteConnection conn)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, "CREATE TABLE meta (key TEXT PRIMARY KEY, value)");
            Exec(conn, tx, "CREATE TABLE world (id INTEGER PRIMARY KEY CHECK (id = 1), head BLOB NOT NULL, tail BLOB NOT NULL)");
            Exec(conn, tx, "CREATE TABLE accounts (account_index INTEGER PRIMARY KEY, account_id TEXT NOT NULL, data BLOB NOT NULL)");
            Exec(conn, tx, "CREATE TABLE guilds (guild_index INTEGER PRIMARY KEY, data BLOB NOT NULL)");
            Exec(conn, tx, "CREATE TABLE goods (npc_index INTEGER PRIMARY KEY, data BLOB NOT NULL)");
            Exec(conn, tx, "CREATE TABLE conquests (conquest_index INTEGER PRIMARY KEY, data BLOB NOT NULL)");
            SetMeta(conn, tx, "schema_version", (long)SchemaVersion);
            tx.Commit();
        }

        /// <summary>Every table exists and meta.schema_version is one this build reads (fail closed otherwise).</summary>
        public static void CheckSchema(SqliteConnection conn)
        {
            foreach (var table in Tables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
                cmd.Parameters.AddWithValue("$n", table);
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidDataException("[UserData] table " + table + " is missing; this is not a UserData.db");
            }

            var v = ReadMeta(conn, "schema_version");
            if (v is not long version || version < 1 || version > SchemaVersion)
                throw new InvalidDataException("[UserData] schema_version is " + (v ?? "missing") + "; this build reads 1.." + SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }

        public static object ReadMeta(SqliteConnection conn, string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }

        /// <summary>meta.envir_version (null when missing).</summary>
        public static long? ReadEnvirVersion(SqliteConnection conn) => ReadMeta(conn, "envir_version") as long?;

        /// <summary>
        /// The world row and every account row (ORDER BY account_index). A store without a world row, or whose meta.envir_version
        /// differs from the version in world.head, is refused (Part A, Tables: fail closed).
        /// </summary>
        public static UserDataRows ReadWorld(SqliteConnection conn)
        {
            var rows = new UserDataRows();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT head, tail FROM world WHERE id = 1";
                using var r = cmd.ExecuteReader();
                if (!r.Read()) throw new InvalidDataException("[UserData] UserData.db has no world row; refusing to load it as an empty world (Knowledgebase/Runbook.md)");
                rows.Head = (byte[])r.GetValue(0);
                rows.Tail = (byte[])r.GetValue(1);
            }

            rows.EnvirVersion = ReadEnvirVersion(conn);
            if (rows.Head.Length < 4) throw new InvalidDataException("[UserData] world.head is truncated");
            var headVersion = BitConverter.ToInt32(rows.Head, 0);
            if (rows.EnvirVersion != headVersion)
                throw new InvalidDataException("[UserData] meta.envir_version (" + (rows.EnvirVersion?.ToString(CultureInfo.InvariantCulture) ?? "missing") +
                                               ") differs from the version in world.head (" + headVersion.ToString(CultureInfo.InvariantCulture) + "); refusing to load");

            ReadKeyed(conn, "SELECT account_index, account_id, data FROM accounts ORDER BY account_index", rows.Accounts, withId: true);
            return rows;
        }

        /// <summary>Rows of guilds / goods / conquests, ordered by key.</summary>
        public static List<UserDataRow> ReadTable(SqliteConnection conn, string table)
        {
            var key = KeyColumn(table);
            var list = new List<UserDataRow>();
            ReadKeyed(conn, "SELECT " + key + ", data FROM " + table + " ORDER BY " + key, list, withId: false);
            return list;
        }

        private static string KeyColumn(string table) => table switch
        {
            "accounts" => "account_index",
            "guilds" => "guild_index",
            "goods" => "npc_index",
            "conquests" => "conquest_index",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "not a UserData row table"),
        };

        private static void ReadKeyed(SqliteConnection conn, string sql, List<UserDataRow> into, bool withId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetInt64(0);
                if (key < int.MinValue || key > int.MaxValue) throw new InvalidDataException("[UserData] row key " + key.ToString(CultureInfo.InvariantCulture) + " is out of range");
                if (withId) into.Add(new UserDataRow((int)key, r.GetString(1), (byte[])r.GetValue(2)));
                else into.Add(new UserDataRow((int)key, null, (byte[])r.GetValue(1)));
            }
        }

        /// <summary>Everything, for the rollback export and the import's read-back.</summary>
        public static UserDataRows ReadAll(SqliteConnection conn)
        {
            var rows = ReadWorld(conn);
            rows.Guilds.AddRange(ReadTable(conn, "guilds"));
            rows.Goods.AddRange(ReadTable(conn, "goods"));
            rows.Conquests.AddRange(ReadTable(conn, "conquests"));
            return rows;
        }

        /// <summary>
        /// One save = one transaction (Part A, Save): the job's changed rows, world, meta and the pending deletes. Rows are written straight
        /// from the job's buffer (zeroblob + incremental blob write), so nothing is copied. Throws on any failure (rolled back).
        /// </summary>
        public static void WriteSave(SqliteConnection conn, UserDataSaveJob job)
        {
            using var tx = conn.BeginTransaction();

            using (var world = Command(conn, tx, "INSERT OR REPLACE INTO world (id, head, tail) VALUES (1, zeroblob($h), zeroblob($t))"))
            {
                world.Parameters.AddWithValue("$h", (long)job.HeadLength);
                world.Parameters.AddWithValue("$t", (long)job.TailLength);
                world.ExecuteNonQuery();
            }
            WriteBlob(conn, "world", "head", 1, job.Buffer, job.HeadOffset, job.HeadLength);
            WriteBlob(conn, "world", "tail", 1, job.Buffer, job.TailOffset, job.TailLength);

            WriteRefs(conn, tx, "accounts", job.Accounts, job.Buffer, withId: true);
            WriteRefs(conn, tx, "guilds", job.Guilds, job.Buffer, withId: false);
            WriteRefs(conn, tx, "goods", job.Goods, job.Buffer, withId: false);
            WriteRefs(conn, tx, "conquests", job.Conquests, job.Buffer, withId: false);

            Delete(conn, tx, "accounts", job.AccountDeletes);
            Delete(conn, tx, "guilds", job.GuildDeletes);

            SetMeta(conn, tx, "envir_version", (long)job.EnvirVersion);
            SetMeta(conn, tx, "saved_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            tx.Commit();
        }

        /// <summary>Writes a complete store (import or a new world) into an empty database with the schema created.</summary>
        public static void WriteRows(SqliteConnection conn, UserDataRows rows, int envirVersion, IDictionary<string, object> meta)
        {
            using var tx = conn.BeginTransaction();
            using (var world = Command(conn, tx, "INSERT OR REPLACE INTO world (id, head, tail) VALUES (1, $h, $t)"))
            {
                world.Parameters.AddWithValue("$h", rows.Head);
                world.Parameters.AddWithValue("$t", rows.Tail);
                world.ExecuteNonQuery();
            }

            WriteRows(conn, tx, "accounts", rows.Accounts, withId: true);
            WriteRows(conn, tx, "guilds", rows.Guilds, withId: false);
            WriteRows(conn, tx, "goods", rows.Goods, withId: false);
            WriteRows(conn, tx, "conquests", rows.Conquests, withId: false);

            SetMeta(conn, tx, "envir_version", (long)envirVersion);
            SetMeta(conn, tx, "created_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (meta != null)
                foreach (var pair in meta) SetMeta(conn, tx, pair.Key, pair.Value);
            tx.Commit();
        }

        private static void WriteRefs(SqliteConnection conn, SqliteTransaction tx, string table, List<UserRowRef> refs, byte[] buffer, bool withId)
        {
            if (refs.Count == 0) return;
            var key = KeyColumn(table);
            using var cmd = Command(conn, tx, withId
                ? "INSERT OR REPLACE INTO " + table + " (" + key + ", account_id, data) VALUES ($k, $id, zeroblob($n))"
                : "INSERT OR REPLACE INTO " + table + " (" + key + ", data) VALUES ($k, zeroblob($n))");
            var pk = cmd.Parameters.Add("$k", SqliteType.Integer);
            var pn = cmd.Parameters.Add("$n", SqliteType.Integer);
            var pid = withId ? cmd.Parameters.Add("$id", SqliteType.Text) : null;
            cmd.Prepare();

            for (var i = 0; i < refs.Count; i++)
            {
                var row = refs[i];
                pk.Value = (long)row.Key;
                pn.Value = (long)row.Length;
                if (pid != null) pid.Value = row.Id ?? string.Empty;
                cmd.ExecuteNonQuery();
                WriteBlob(conn, table, "data", row.Key, buffer, row.Offset, row.Length);
            }
        }

        private static void WriteRows(SqliteConnection conn, SqliteTransaction tx, string table, List<UserDataRow> rows, bool withId)
        {
            if (rows.Count == 0) return;
            var key = KeyColumn(table);
            using var cmd = Command(conn, tx, withId
                ? "INSERT INTO " + table + " (" + key + ", account_id, data) VALUES ($k, $id, $d)"
                : "INSERT INTO " + table + " (" + key + ", data) VALUES ($k, $d)");
            var pk = cmd.Parameters.Add("$k", SqliteType.Integer);
            var pd = cmd.Parameters.Add("$d", SqliteType.Blob);
            var pid = withId ? cmd.Parameters.Add("$id", SqliteType.Text) : null;
            cmd.Prepare();
            for (var i = 0; i < rows.Count; i++)
            {
                pk.Value = (long)rows[i].Key;
                pd.Value = rows[i].Data;
                if (pid != null) pid.Value = rows[i].Id ?? string.Empty;
                cmd.ExecuteNonQuery(); // plain INSERT: a duplicate key fails the import instead of replacing a row
            }
        }

        private static void Delete(SqliteConnection conn, SqliteTransaction tx, string table, int[] keys)
        {
            if (keys == null || keys.Length == 0) return;
            var key = KeyColumn(table);
            using var cmd = Command(conn, tx, "DELETE FROM " + table + " WHERE " + key + " = $k");
            var pk = cmd.Parameters.Add("$k", SqliteType.Integer);
            cmd.Prepare();
            for (var i = 0; i < keys.Length; i++)
            {
                pk.Value = (long)keys[i];
                cmd.ExecuteNonQuery(); // an already-missing row is harmless
            }
        }

        private static void WriteBlob(SqliteConnection conn, string table, string column, long rowid, byte[] buffer, int offset, int length)
        {
            if (length == 0) return;
            using var blob = new SqliteBlob(conn, table, column, rowid);
            blob.Write(buffer, offset, length);
        }

        private static void SetMeta(SqliteConnection conn, SqliteTransaction tx, string key, object value)
        {
            using var cmd = Command(conn, tx, "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)");
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd;
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = Command(conn, tx, sql);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// The rollback image (Part A, Backups: Rollback): world.head + the account rows + world.tail is byte for byte a Server.MirADB,
        /// with the two counts the store does not keep re-inserted (the guild count after NextHeroID, the account count before the rows).
        /// The head is always written at the current layout (Version &gt; 102), so the guild count goes at byte 28.
        /// </summary>
        public static byte[] LegacyAccountsImage(UserDataRows rows)
        {
            const int GuildCountOffset = 28; // Version, CustomVersion, NextAccountID, NextCharacterID, NextUserItemID (8), NextHeroID
            if (rows.Head.Length < GuildCountOffset) throw new InvalidDataException("[UserData] world.head is truncated");
            var size = (long)rows.Head.Length + 8 + rows.Tail.Length;
            for (var i = 0; i < rows.Accounts.Count; i++) size += rows.Accounts[i].Data.Length;
            using var ms = new MemoryStream((int)Math.Min(size, int.MaxValue));
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(rows.Head, 0, GuildCountOffset);
                w.Write(rows.Guilds.Count);
                w.Write(rows.Head, GuildCountOffset, rows.Head.Length - GuildCountOffset);
                w.Write(rows.Accounts.Count);
                for (var i = 0; i < rows.Accounts.Count; i++) w.Write(rows.Accounts[i].Data);
                w.Write(rows.Tail);
            }
            return ms.ToArray();
        }
    }
}
