using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Server.MirEnvir;
using Server.WebAdmin.GameData;

namespace MirDbExport
{
    /// <summary>
    /// Exports every <see cref="GameDataRegistry"/> table of the loaded <see cref="Envir.Edit"/> to SQLite (one STRICT table per
    /// endpoint), plus <c>meta</c> and <c>index_counters</c>. Encoding is total and loud: a value or column type that cannot be
    /// encoded is an error, and any error means no output file is produced.
    /// </summary>
    internal static class Exporter
    {
        internal sealed class Result
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<(string Table, int Rows, int Columns)> Tables = new List<(string, int, int)>();
            public int TotalRows => Tables.Sum(t => t.Rows);
        }

        public static Result Export(string sourceFolder, string outPath)
        {
            return WorldLoader.WithLoadedEdit(sourceFolder, source => ExportLoaded(source, outPath));
        }

        /// <summary>Exports what Envir.Edit currently holds (callers load it first).</summary>
        internal static Result ExportLoaded(WorldLoader.SourceFile source, string outPath)
        {
            var result = new Result();
            outPath = Path.GetFullPath(outPath);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (string.Equals(Path.GetFileName(outPath), WorldLoader.DbFileName, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("Refusing to write the export over a file named " + WorldLoader.DbFileName + ".");

            var tmp = outPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);

            var ctx = GameDataContext.FromEnvir(Envir.Edit);
            var ok = false;
            try
            {
                using (var conn = Open(tmp))
                {
                    using (var tx = conn.BeginTransaction())
                    {
                        WriteMeta(conn, tx, source);
                        WriteCounters(conn, tx);
                        foreach (var table in GameDataRegistry.Tables)
                            WriteTable(conn, tx, ctx, table, result);

                        if (result.Errors.Count > 0)
                        {
                            tx.Rollback();
                            return result;
                        }

                        tx.Commit();
                    }

                    using var check = conn.CreateCommand();
                    check.CommandText = "PRAGMA integrity_check";
                    var integrity = Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (integrity != "ok")
                    {
                        result.Errors.Add("SQLite integrity_check: " + integrity);
                        return result;
                    }
                }

                File.Move(tmp, outPath, true);
                ok = true;
                return result;
            }
            finally
            {
                if (!ok && File.Exists(tmp)) File.Delete(tmp);
            }
        }

        internal static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        {
            // Pooling off: a pooled connection keeps the file open and would block the final File.Move.
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
            var conn = new SqliteConnection(cs);
            conn.Open();
            return conn;
        }

        // ------------------------------------------------------------------ meta / counters

        private static void WriteMeta(SqliteConnection conn, SqliteTransaction tx, WorldLoader.SourceFile source)
        {
            Exec(conn, tx, "CREATE TABLE " + Schema.MetaTable + " (name TEXT PRIMARY KEY, value ANY) STRICT");
            var rows = new (string, object)[]
            {
                ("schema_version", (long)Schema.Version),
                ("envir_version", (long)Envir.LoadVersion),
                ("envir_custom_version", (long)Envir.LoadCustomVersion),
                ("code_envir_version", (long)Envir.Version),
                ("exported_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                ("source_file", source.Path),
                ("source_size", source.Size),
                ("source_sha256", source.Sha256),
                ("tool", "MirDbExport"),
            };

            foreach (var (name, value) in rows)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO " + Schema.MetaTable + " (name, value) VALUES ($n, $v)";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteCounters(SqliteConnection conn, SqliteTransaction tx)
        {
            // Persisted as read, never recomputed from max(key): a recomputed counter reuses deleted indexes (WoOOLcraft lesson).
            Exec(conn, tx, "CREATE TABLE " + Schema.CountersTable + " (name TEXT PRIMARY KEY, value INTEGER NOT NULL) STRICT");
            var e = Envir.Edit;
            var values = new Dictionary<string, int>
            {
                ["MapIndex"] = e.MapIndex, ["ItemIndex"] = e.ItemIndex, ["MonsterIndex"] = e.MonsterIndex, ["NPCIndex"] = e.NPCIndex,
                ["QuestIndex"] = e.QuestIndex, ["GameshopIndex"] = e.GameshopIndex, ["ConquestIndex"] = e.ConquestIndex,
                ["RespawnIndex"] = e.RespawnIndex,
            };

            foreach (var name in Schema.Counters)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO " + Schema.CountersTable + " (name, value) VALUES ($n, $v)";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$v", (long)values[name]);
                cmd.ExecuteNonQuery();
            }
        }

        // ------------------------------------------------------------------ tables

        private static void WriteTable(SqliteConnection conn, SqliteTransaction tx, GameDataContext ctx, GameDataTable table, Result result)
        {
            var reserved = new HashSet<string>(new[] { Schema.KeyColumn, Schema.ParentColumn, Schema.PositionColumn }, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // SQLite column names are case-insensitive
            var columns = new List<(GameDataColumn Column, string SqlType)>();
            foreach (var c in table.Columns)
            {
                if (reserved.Contains(c.Name) || !seen.Add(c.Name))
                {
                    result.Errors.Add(table.Endpoint + "." + c.Name + ": column name collides with another column (case-insensitive) or a reserved name");
                    continue;
                }

                var sqlType = SqlTypeOf(c.ValueType);
                if (sqlType == null)
                {
                    result.Errors.Add(table.Endpoint + "." + c.Name + ": no SQLite encoding for CLR type " + c.ValueType.FullName);
                    continue;
                }

                columns.Add((c, sqlType));
            }

            var ddl = new StringBuilder();
            ddl.Append("CREATE TABLE ").Append(Schema.Quote(table.Endpoint)).Append(" (")
                .Append(Schema.KeyColumn).Append(" INTEGER PRIMARY KEY, ")
                .Append(Schema.ParentColumn).Append(" INTEGER NOT NULL, ")
                .Append(Schema.PositionColumn).Append(" INTEGER NOT NULL");
            foreach (var (c, t) in columns) ddl.Append(", ").Append(Schema.Quote(c.Name)).Append(' ').Append(t);
            ddl.Append(") STRICT");
            Exec(conn, tx, ddl.ToString());

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            var names = new List<string> { Schema.KeyColumn, Schema.ParentColumn, Schema.PositionColumn };
            names.AddRange(columns.Select(c => Schema.Quote(c.Column.Name)));
            insert.CommandText = "INSERT INTO " + Schema.Quote(table.Endpoint) + " (" + string.Join(", ", names) + ") VALUES (" +
                                 string.Join(", ", Enumerable.Range(0, names.Count).Select(i => "$p" + i.ToString(CultureInfo.InvariantCulture))) + ")";
            var parameters = new List<SqliteParameter>();
            for (var i = 0; i < names.Count; i++) parameters.Add(insert.Parameters.Add("$p" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Text));

            var lossChecks = LossChecks(table);
            var count = 0;
            var keys = new HashSet<int>();
            foreach (var row in table.Rows(ctx))
            {
                if (!keys.Add(row.Index))
                {
                    result.Errors.Add(table.Endpoint + " key " + row.Index.ToString(CultureInfo.InvariantCulture) + ": duplicate key (two records share it; not exportable without losing one)");
                    continue;
                }

                SetParam(parameters[0], (long)row.Index);
                SetParam(parameters[1], (long)row.ParentIndex);
                SetParam(parameters[2], (long)row.Position);
                var rowOk = true;
                for (var i = 0; i < columns.Count; i++)
                {
                    var c = columns[i].Column;
                    object raw;
                    try { raw = c.Get(row); }
                    catch (Exception ex)
                    {
                        result.Errors.Add(table.Endpoint + " key " + row.Index.ToString(CultureInfo.InvariantCulture) + " column " + c.Name + ": read failed: " + ex.GetType().Name + ": " + ex.Message);
                        rowOk = false;
                        continue;
                    }

                    if (!TryEncode(c.ValueType, raw, out var value, out var error))
                    {
                        result.Errors.Add(table.Endpoint + " key " + row.Index.ToString(CultureInfo.InvariantCulture) + " column " + c.Name + ": " + error);
                        rowOk = false;
                        continue;
                    }

                    SetParam(parameters[i + 3], value);
                }

                foreach (var check in lossChecks)
                {
                    var problem = check(row.Entity);
                    if (problem == null) continue;
                    result.Errors.Add(table.Endpoint + " key " + row.Index.ToString(CultureInfo.InvariantCulture) + ": " + problem);
                    rowOk = false;
                }

                if (!rowOk) continue;
                insert.ExecuteNonQuery();
                count++;
            }

            result.Tables.Add((table.Endpoint, count, columns.Count));
        }

        /// <summary>
        /// Content the columns cannot carry: Stats entries for keys that have no Stat_ column (undefined Stat values or
        /// Stat.Unknown, non-zero), and arrays longer or shorter than their Name_N columns. Each check returns null or a reason.
        /// </summary>
        private static List<Func<object, string>> LossChecks(GameDataTable table)
        {
            var checks = new List<Func<object, string>>();
            var names = new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.Ordinal);
            var exportedStats = new HashSet<Stat>(GameDataColumns.StatMembers);
            foreach (var f in table.EntityType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var field = f;
                if (field.FieldType == typeof(Stats) && names.Any(n => n.StartsWith(GameDataColumns.StatPrefix, StringComparison.Ordinal)))
                {
                    checks.Add(e =>
                    {
                        if (field.GetValue(e) is not Stats stats) return null;
                        var lost = stats.Values.Where(p => p.Value != 0 && !exportedStats.Contains(p.Key)).ToList();
                        return lost.Count == 0 ? null : field.Name + " holds stat key(s) with no column: " +
                            string.Join(", ", lost.Select(p => ((byte)p.Key).ToString(CultureInfo.InvariantCulture) + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
                    });
                }
                else if (field.FieldType.IsArray)
                {
                    var columns = 0;
                    while (names.Contains(field.Name + "_" + (columns + 1).ToString(CultureInfo.InvariantCulture))) columns++;
                    if (columns == 0) continue;
                    checks.Add(e =>
                    {
                        var length = (field.GetValue(e) as Array)?.Length ?? -1;
                        return length == columns ? null : field.Name + " has " + length.ToString(CultureInfo.InvariantCulture) + " elements but " +
                            columns.ToString(CultureInfo.InvariantCulture) + " columns";
                    });
                }
            }

            return checks;
        }

        private static void SetParam(SqliteParameter p, object value)
        {
            switch (value)
            {
                case null:
                    p.SqliteType = SqliteType.Text;
                    p.Value = DBNull.Value;
                    break;
                case long l:
                    p.SqliteType = SqliteType.Integer;
                    p.Value = l;
                    break;
                case double d:
                    p.SqliteType = SqliteType.Real;
                    p.Value = d;
                    break;
                case string s:
                    p.SqliteType = SqliteType.Text;
                    p.Value = s;
                    break;
                default:
                    throw new InvalidOperationException("encoder produced " + value.GetType());
            }
        }

        // ------------------------------------------------------------------ encoding (the exporter's copy; the verifier has its own)

        /// <summary>SQLite column type for a column's CLR value type, or null when there is no encoding.</summary>
        internal static string SqlTypeOf(Type t)
        {
            if (t.IsEnum || t == typeof(bool) || t == typeof(DateTime)) return "INTEGER";
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.SByte: case TypeCode.Byte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64: case TypeCode.Char:
                    return "INTEGER";
                case TypeCode.Single: case TypeCode.Double:
                    return "REAL";
                case TypeCode.String: case TypeCode.Decimal:
                    return "TEXT";
            }

            if (t == typeof(List<int>)) return "TEXT";
            return null;
        }

        /// <summary>Encodes one value to long / double / string / null. False (with a reason) when it cannot be stored losslessly.</summary>
        internal static bool TryEncode(Type t, object value, out object encoded, out string error)
        {
            encoded = null;
            error = null;

            if (t == typeof(string))
            {
                if (value == null) return true; // NULL, distinct from ''
                var s = (string)value;
                if (!IsWellFormedUtf16(s))
                {
                    error = "string contains an unpaired surrogate (UTF-8 TEXT would replace it)";
                    return false;
                }

                encoded = s;
                return true;
            }

            if (value == null)
            {
                if (t == typeof(List<int>))
                {
                    encoded = "[]";
                    return true;
                }

                error = "null value for non-nullable type " + t.Name;
                return false;
            }

            if (t.IsEnum)
            {
                var u = Enum.GetUnderlyingType(t);
                if (u == typeof(ulong))
                {
                    var v = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    if (v > long.MaxValue) { error = "enum value " + v + " exceeds INTEGER range"; return false; }
                    encoded = (long)v;
                }
                else encoded = Convert.ToInt64(Convert.ChangeType(value, u, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return true;
            }

            if (t == typeof(bool)) { encoded = (bool)value ? 1L : 0L; return true; }
            if (t == typeof(DateTime)) { encoded = ((DateTime)value).ToBinary(); return true; }
            if (t == typeof(List<int>))
            {
                encoded = "[" + string.Join(",", ((List<int>)value).Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
                return true;
            }

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.SByte: encoded = (long)(sbyte)value; return true;
                case TypeCode.Byte: encoded = (long)(byte)value; return true;
                case TypeCode.Int16: encoded = (long)(short)value; return true;
                case TypeCode.UInt16: encoded = (long)(ushort)value; return true;
                case TypeCode.Int32: encoded = (long)(int)value; return true;
                case TypeCode.UInt32: encoded = (long)(uint)value; return true;
                case TypeCode.Int64: encoded = (long)value; return true;
                case TypeCode.Char: encoded = (long)(char)value; return true;
                case TypeCode.UInt64:
                    var ul = (ulong)value;
                    if (ul > long.MaxValue) { error = "UInt64 value " + ul + " exceeds INTEGER range"; return false; }
                    encoded = (long)ul;
                    return true;
                case TypeCode.Single:
                case TypeCode.Double:
                    var d = Type.GetTypeCode(t) == TypeCode.Single ? (double)(float)value : (double)value;
                    if (double.IsNaN(d)) { error = "NaN (SQLite stores NaN as NULL)"; return false; }
                    encoded = d;
                    return true;
                case TypeCode.Decimal:
                    encoded = ((decimal)value).ToString(CultureInfo.InvariantCulture);
                    return true;
            }

            error = "no SQLite encoding for CLR type " + t.FullName;
            return false;
        }

        private static bool IsWellFormedUtf16(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(s[i])) return false;
            }

            return true;
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
