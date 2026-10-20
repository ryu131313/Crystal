using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// Reads and writes <see cref="WorldRows"/> on one SQLite connection (roadmap P7 phase 7.2). Every call runs on the thread that owns
    /// the connection (<see cref="SqliteWriter"/>, or an offline tool). Writes are one transaction: the design tables are deleted and
    /// re-inserted (full rewrite, decision D4), so a failed save leaves the previous data.
    /// </summary>
    internal static class SqliteWorldFile
    {
        public static SqliteConnection Open(string path, bool create)
        {
            if (!create)
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("[Store] " + path + " is missing", path);
                if (info.Length == 0) throw new InvalidDataException("[Store] " + path + " is empty (0 bytes); refusing to open it");
            }

            // Pooling off: a pooled connection keeps the file open after Dispose (backups, restores and moves need it closed).
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();

            var conn = new SqliteConnection(cs);
            try
            {
                conn.Open();
                Exec(conn, null, "PRAGMA journal_mode=DELETE");
                Exec(conn, null, "PRAGMA synchronous=FULL");
                Exec(conn, null, "PRAGMA foreign_keys=OFF");
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        public static void CheckIntegrity(SqliteConnection conn)
        {
            var result = Convert.ToString(Scalar(conn, null, "PRAGMA integrity_check"), CultureInfo.InvariantCulture);
            if (result != "ok") throw new InvalidDataException("[Store] SQLite integrity_check failed: " + result);
        }

        /// <summary>Creates the schema in an empty database.</summary>
        public static void CreateSchema(SqliteConnection conn)
        {
            using var tx = conn.BeginTransaction();
            foreach (var sql in SqliteWorldCodec.CreateStatements()) Exec(conn, tx, sql);
            tx.Commit();
        }

        /// <summary>Checks meta.schema_version before anything is read or written.</summary>
        public static void CheckSchema(SqliteConnection conn)
        {
            var v = Scalar(conn, null, "SELECT value FROM meta WHERE name = 'schema_version'");
            if (v is not long version || version != SqliteWorldCodec.SchemaVersion)
                throw new InvalidDataException("[Store] schema_version is " + (v ?? "missing") + "; this build reads " + SqliteWorldCodec.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }

        /// <param name="includeRuntime">Also write runtime_state (the respawn tick counter). Editor saves don't, so they can't roll Main's
        /// counter back; imports, shadow mirrors and Main's own save do.</param>
        /// <param name="meta">Extra meta rows (source, source_sha256, ...).</param>
        public static void WriteAll(SqliteConnection conn, WorldRows rows, bool includeRuntime, IDictionary<string, object> meta)
        {
            using var tx = conn.BeginTransaction();

            Upsert(conn, tx, "meta", "schema_version", (long)SqliteWorldCodec.SchemaVersion);
            Upsert(conn, tx, "meta", "code_envir_version", (long)rows.EnvirVersion);
            Upsert(conn, tx, "meta", "code_envir_custom_version", (long)rows.EnvirCustomVersion);
            Upsert(conn, tx, "meta", "saved_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (meta != null)
                foreach (var pair in meta) Upsert(conn, tx, "meta", pair.Key, pair.Value);

            foreach (var name in SqliteWorldCodec.CounterNames)
                Upsert(conn, tx, "index_counters", name, rows.Counters[name]);

            Exec(conn, tx, "DELETE FROM respawn_tick");
            Insert(conn, tx, "respawn_tick", new[] { "id", "BaseSpawnRate" }, new object[] { 1L, rows.BaseSpawnRate });
            Exec(conn, tx, "DELETE FROM respawn_tick_options");
            for (var i = 0; i < rows.RespawnOptions.Count; i++)
                Insert(conn, tx, "respawn_tick_options", new[] { "position", "UserCount", "DelayLoss" },
                    new object[] { (long)i, rows.RespawnOptions[i].UserCount, rows.RespawnOptions[i].DelayLoss });

            if (includeRuntime) Upsert(conn, tx, "runtime_state", "RespawnTickCounter", rows.RespawnTickCounter);

            foreach (var spec in SqliteWorldCodec.Specs)
            {
                var name = SqliteWorldCodec.Quote(spec.Name);
                Exec(conn, tx, "DELETE FROM " + name);
                var columns = SqliteWorldCodec.ColumnNames(spec);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO " + name + " (" + string.Join(", ", columns.Select(SqliteWorldCodec.Quote)) + ") VALUES (" +
                                  string.Join(", ", columns.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture))) + ")";
                var parameters = new SqliteParameter[columns.Count];
                for (var i = 0; i < columns.Count; i++) parameters[i] = cmd.Parameters.Add("$p" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Text);
                cmd.Prepare();

                foreach (var cells in rows.Tables[spec.Name].Rows)
                {
                    for (var i = 0; i < cells.Length; i++) SetParam(parameters[i], cells[i]);
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        /// <summary>Writes only runtime_state (Main's periodic save in Backend=Sqlite).</summary>
        public static void WriteRuntime(SqliteConnection conn, long respawnTickCounter)
        {
            using var tx = conn.BeginTransaction();
            Upsert(conn, tx, "runtime_state", "RespawnTickCounter", respawnTickCounter);
            tx.Commit();
        }

        public static WorldRows ReadAll(SqliteConnection conn)
        {
            CheckSchema(conn);
            var rows = new WorldRows { EnvirVersion = Envir.Version, EnvirCustomVersion = Envir.CustomVersion };

            using (var cmd = Command(conn, "SELECT name, value FROM index_counters"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) rows.Counters[r.GetString(0)] = r.GetInt64(1);

            var spawn = Scalar(conn, null, "SELECT BaseSpawnRate FROM respawn_tick WHERE id = 1");
            rows.BaseSpawnRate = spawn is long b ? b : throw new InvalidDataException("[Store] respawn_tick has no row");
            using (var cmd = Command(conn, "SELECT UserCount, DelayLoss FROM respawn_tick_options ORDER BY position"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) rows.RespawnOptions.Add((r.GetInt64(0), r.GetDouble(1)));

            var counter = Scalar(conn, null, "SELECT value FROM runtime_state WHERE name = 'RespawnTickCounter'");
            rows.RespawnTickCounter = counter is long c ? c : 0;

            foreach (var spec in SqliteWorldCodec.Specs)
            {
                var columns = SqliteWorldCodec.ColumnNames(spec);
                CheckColumns(conn, spec.Name, columns);
                var table = new WorldRows.Table { Name = spec.Name };
                rows.Tables[spec.Name] = table;
                using var cmd = Command(conn, "SELECT " + string.Join(", ", columns.Select(SqliteWorldCodec.Quote)) + " FROM " +
                                              SqliteWorldCodec.Quote(spec.Name) + " ORDER BY " + SqliteWorldCodec.ParentPositionColumn + ", " + SqliteWorldCodec.PositionColumn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var cells = new object[columns.Count];
                    for (var i = 0; i < cells.Length; i++)
                        cells[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                    table.Rows.Add(cells);
                }
            }

            return rows;
        }

        /// <summary>Loud schema check: a column the code doesn't know, or one it expects and can't find, fails the read.</summary>
        private static void CheckColumns(SqliteConnection conn, string table, List<string> expected)
        {
            var actual = new List<string>();
            using (var cmd = Command(conn, "SELECT name FROM pragma_table_info($t)"))
            {
                cmd.Parameters.AddWithValue("$t", table);
                using var r = cmd.ExecuteReader();
                while (r.Read()) actual.Add(r.GetString(0));
            }

            var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToList();
            var unknown = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0 || unknown.Count > 0)
                throw new InvalidDataException("[Store] table " + table + ": missing column(s) [" + string.Join(", ", missing) + "], unknown column(s) [" + string.Join(", ", unknown) + "]");
        }

        /// <summary>A consistent copy of the open database (never File.Copy of an open file).</summary>
        public static void BackupTo(SqliteConnection conn, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            using var cmd = Command(conn, "VACUUM INTO $p");
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }

        public static bool LooksLikeSqlite(string path)
        {
            try
            {
                using var f = File.OpenRead(path);
                var header = new byte[16];
                return f.Read(header, 0, 16) == 16 && System.Text.Encoding.ASCII.GetString(header, 0, 15) == "SQLite format 3";
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // ------------------------------------------------------------------ helpers

        private static SqliteCommand Command(SqliteConnection conn, string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd;
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static object Scalar(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }

        private static void Upsert(SqliteConnection conn, SqliteTransaction tx, string table, string name, object value)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO " + table + " (name, value) VALUES ($n, $v) ON CONFLICT(name) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$n", name);
            SetParam(cmd.Parameters.Add("$v", SqliteType.Text), value);
            cmd.ExecuteNonQuery();
        }

        private static void Insert(SqliteConnection conn, SqliteTransaction tx, string table, string[] columns, object[] values)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO " + table + " (" + string.Join(", ", columns) + ") VALUES (" +
                              string.Join(", ", columns.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture))) + ")";
            for (var i = 0; i < values.Length; i++) SetParam(cmd.Parameters.Add("$p" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Text), values[i]);
            cmd.ExecuteNonQuery();
        }

        private static void SetParam(SqliteParameter p, object value)
        {
            switch (value)
            {
                case null: p.SqliteType = SqliteType.Text; p.Value = DBNull.Value; break;
                case long l: p.SqliteType = SqliteType.Integer; p.Value = l; break;
                case int i: p.SqliteType = SqliteType.Integer; p.Value = (long)i; break;
                case double d: p.SqliteType = SqliteType.Real; p.Value = d; break;
                case string s: p.SqliteType = SqliteType.Text; p.Value = s; break;
                default: throw new InvalidOperationException("[Store] cell of type " + value.GetType());
            }
        }
    }
}
