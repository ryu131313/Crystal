using System.Drawing;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.WebAdmin.GameData;

namespace MirDbExport
{
    /// <summary>
    /// INDEPENDENT verifier. Reloads Server.MirDB into Envir.Edit and compares every exported table / row / column with the
    /// value read directly from the entity by reflection on the member the column names. It never calls the exporter's
    /// encoder, <see cref="GameDataColumn.Get"/> or <see cref="GameDataTable.Rows"/>: rows are enumerated from the Envir lists
    /// here, columns are mapped to members by the naming rules below, and the encoding is re-implemented in <see cref="Expect"/>.
    /// The registry is consulted only for its endpoint and column NAMES (to prove nothing was dropped or added).
    /// </summary>
    internal static class Verifier
    {
        internal sealed class Result
        {
            public readonly List<string> Mismatches = new List<string>();
            public readonly List<(string Table, int Rows, int Columns)> Tables = new List<(string, int, int)>();
            public long CellsCompared;
            public int TotalRows => Tables.Sum(t => t.Rows);
        }

        /// <summary>One expected row, enumerated straight from Envir.Edit.</summary>
        internal sealed class ExpectedRow
        {
            public long Key, Parent, Position;
            public object Entity, ParentEntity;
        }

        /// <summary>The verifier's own description of each exported table.</summary>
        internal sealed class TableSpec
        {
            public string Endpoint;
            public Type EntityType;
            public string ParentColumn;          // "MapIndex" / "ConquestIndex" for child tables
            public string KeyMember;             // member the key column carries (null: synthetic key)
            public Dictionary<string, string> Renames = new Dictionary<string, string>(StringComparer.Ordinal);
            public Func<Envir, IEnumerable<ExpectedRow>> Rows;
        }

        private const int Stride = 10000;

        internal static readonly IReadOnlyList<TableSpec> Specs = BuildSpecs();

        private static List<TableSpec> BuildSpecs()
        {
            var specs = new List<TableSpec>
            {
                Top<ItemInfo>("items", e => e.ItemInfoList, x => x.Index, "Index"),
                Top<MonsterInfo>("monsters", e => e.MonsterInfoList, x => x.Index, "Index"),
                new TableSpec
                {
                    Endpoint = "magic", EntityType = typeof(MagicInfo), KeyMember = "Spell",
                    Rows = e => e.MagicInfoList.Select((m, i) => new ExpectedRow { Key = (long)(int)m.Spell, Position = i, Entity = m }).Where(r => r.Key > 0),
                },
                Top<NPCInfo>("npcs", e => e.NPCInfoList, x => x.Index, "Index"),
                Top<QuestInfo>("quests", e => e.QuestInfoList, x => x.Index, "Index"),
                Top<MapInfo>("maps", e => e.MapInfoList, x => x.Index, "Index"),
                MapChild<SafeZoneInfo>("mapsafezones", m => m.SafeZones, null),
                MapChild<RespawnInfo>("maprespawns", m => m.Respawns, r => r.RespawnIndex),
                MapChild<MovementInfo>("mapmovements", m => m.Movements, null),
                MapChild<MineZone>("mapminezones", m => m.MineZones, null),
                Top<GameShopItem>("gameshop", e => e.GameShopList, x => x.GIndex, "GIndex"),
                Top<ConquestInfo>("conquests", e => e.ConquestInfoList, x => x.Index, "Index"),
                ConquestChild<ConquestArcherInfo>("conquestguards", c => c.ConquestGuards),
                ConquestChild<ConquestGateInfo>("conquestgates", c => c.ConquestGates),
                ConquestChild<ConquestWallInfo>("conquestwalls", c => c.ConquestWalls),
                ConquestChild<ConquestSiegeInfo>("conquestsieges", c => c.ConquestSieges),
                ConquestChild<ConquestFlagInfo>("conquestflags", c => c.ConquestFlags),
                ConquestChild<ConquestFlagInfo>("conquestcontrolpoints", c => c.ControlPoints),
                new TableSpec
                {
                    Endpoint = "dragon", EntityType = typeof(DragonInfo),
                    Rows = e => e.DragonInfo == null ? Enumerable.Empty<ExpectedRow>() : new[] { new ExpectedRow { Key = 1, Entity = e.DragonInfo } },
                },
            };

            specs.Single(s => s.Endpoint == "mapmovements").Renames["DestinationMapIndex"] = "MapIndex";
            foreach (var s in specs.Where(s => s.ParentColumn == "ConquestIndex")) s.Renames["LocalIndex"] = "Index";
            return specs;
        }

        private static TableSpec Top<T>(string endpoint, Func<Envir, List<T>> list, Func<T, int> key, string keyMember) where T : class => new TableSpec
        {
            Endpoint = endpoint,
            EntityType = typeof(T),
            KeyMember = keyMember,
            Rows = e => list(e).Select((x, i) => (x, i)).Where(p => p.x != null)
                .Select(p => new ExpectedRow { Key = key(p.x), Position = p.i, Entity = p.x }),
        };

        private static TableSpec MapChild<T>(string endpoint, Func<MapInfo, List<T>> list, Func<T, int> key) where T : class => new TableSpec
        {
            Endpoint = endpoint,
            EntityType = typeof(T),
            ParentColumn = "MapIndex",
            KeyMember = key == null ? null : "RespawnIndex",
            Rows = e => e.MapInfoList.SelectMany(m => list(m).Select((x, i) => new ExpectedRow
            {
                Key = key != null ? key(x) : (long)m.Index * Stride + i, Parent = m.Index, Position = i, Entity = x, ParentEntity = m,
            })),
        };

        private static TableSpec ConquestChild<T>(string endpoint, Func<ConquestInfo, List<T>> list) where T : class => new TableSpec
        {
            Endpoint = endpoint,
            EntityType = typeof(T),
            ParentColumn = "ConquestIndex",
            Rows = e => e.ConquestInfoList.SelectMany(c => list(c).Select((x, i) => new ExpectedRow
            {
                Key = (long)c.Index * Stride + i, Parent = c.Index, Position = i, Entity = x, ParentEntity = c,
            })),
        };

        // ================================================================== entry points

        public static Result Verify(string sourceFolder, string dbPath)
        {
            dbPath = Path.GetFullPath(dbPath);
            if (!File.Exists(dbPath)) throw new ToolException("No export at " + dbPath);
            return WorldLoader.WithLoadedEdit(sourceFolder, source => VerifyLoaded(source, dbPath));
        }

        internal static Result VerifyLoaded(WorldLoader.SourceFile source, string dbPath)
        {
            var result = new Result();
            using var conn = Exporter.Open(dbPath, SqliteOpenMode.ReadOnly);
            var dbTables = Query(conn, "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").Select(r => (string)r[0]).ToList();

            // --- table sets: verifier specs vs registry endpoints vs file
            var specNames = new HashSet<string>(Specs.Select(s => s.Endpoint), StringComparer.Ordinal);
            var registryNames = new HashSet<string>(GameDataRegistry.Tables.Where(t => GameDataRegistry.InBinaryFormat(t.Endpoint)).Select(t => t.Endpoint), StringComparer.Ordinal);
            foreach (var n in registryNames.Except(specNames)) result.Mismatches.Add("registry table '" + n + "' is unknown to the verifier (teach Verifier.Specs)");
            foreach (var n in specNames.Except(registryNames)) result.Mismatches.Add("verifier table '" + n + "' is no longer in GameDataRegistry");
            var expectedTables = new HashSet<string>(specNames, StringComparer.Ordinal) { Schema.MetaTable, Schema.CountersTable };
            foreach (var n in expectedTables.Where(n => !dbTables.Contains(n))) result.Mismatches.Add("table '" + n + "' missing from the export");
            foreach (var n in dbTables.Where(n => !expectedTables.Contains(n))) result.Mismatches.Add("unexpected table '" + n + "' in the export");

            if (dbTables.Contains(Schema.MetaTable)) VerifyMeta(conn, source, result);
            if (dbTables.Contains(Schema.CountersTable)) VerifyCounters(conn, result);

            foreach (var spec in Specs)
                if (dbTables.Contains(spec.Endpoint)) VerifyTable(conn, spec, result);

            return result;
        }

        // ================================================================== meta / counters

        private static void VerifyMeta(SqliteConnection conn, WorldLoader.SourceFile source, Result result)
        {
            var meta = Query(conn, "SELECT name, value FROM " + Schema.MetaTable).ToDictionary(r => (string)r[0], r => r[1]);
            void Check(string name, object expected)
            {
                if (!meta.TryGetValue(name, out var actual)) { result.Mismatches.Add("meta." + name + ": missing"); return; }
                if (!SameCell(expected, actual)) result.Mismatches.Add("meta." + name + ": export has " + Show(actual) + ", expected " + Show(expected));
            }

            Check("schema_version", (long)Schema.Version);
            Check("envir_version", (long)Envir.LoadVersion);
            Check("envir_custom_version", (long)Envir.LoadCustomVersion);
            Check("source_size", source.Size);
            Check("source_sha256", source.Sha256);
            if (!meta.ContainsKey("exported_utc")) result.Mismatches.Add("meta.exported_utc: missing");
        }

        private static void VerifyCounters(SqliteConnection conn, Result result)
        {
            var rows = Query(conn, "SELECT name, value FROM " + Schema.CountersTable).ToDictionary(r => (string)r[0], r => r[1]);
            var names = new[] { "MapIndex", "ItemIndex", "MonsterIndex", "NPCIndex", "QuestIndex", "GameshopIndex", "ConquestIndex", "RespawnIndex" };
            foreach (var name in names)
            {
                var field = typeof(Envir).GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) { result.Mismatches.Add("index_counters." + name + ": Envir has no such field"); continue; }
                var expected = (long)(int)field.GetValue(Envir.Edit);
                if (!rows.TryGetValue(name, out var actual)) result.Mismatches.Add("index_counters." + name + ": missing");
                else if (!SameCell(expected, actual)) result.Mismatches.Add("index_counters." + name + ": export has " + Show(actual) + ", Envir.Edit has " + expected);
            }

            foreach (var extra in rows.Keys.Except(names)) result.Mismatches.Add("index_counters." + extra + ": unexpected counter");
        }

        // ================================================================== tables

        private static void VerifyTable(SqliteConnection conn, TableSpec spec, Result result)
        {
            var dbColumns = Query(conn, "SELECT name FROM pragma_table_info(" + SqlString(spec.Endpoint) + ") ORDER BY cid").Select(r => (string)r[0]).ToList();
            var reserved = new[] { Schema.KeyColumn, Schema.ParentColumn, Schema.PositionColumn };
            foreach (var r in reserved.Where(r => !dbColumns.Contains(r))) result.Mismatches.Add(spec.Endpoint + ": column '" + r + "' missing");
            var dataColumns = dbColumns.Where(c => !reserved.Contains(c)).ToList();

            // Column names: export == registry (names only).
            var registryColumns = GameDataRegistry.Get(spec.Endpoint)?.Columns.Where(c => GameDataRegistry.InBinaryFormat(spec.Endpoint, c.Name)).Select(c => c.Name).ToList() ?? new List<string>();
            foreach (var c in registryColumns.Except(dataColumns)) result.Mismatches.Add(spec.Endpoint + ": registry column '" + c + "' missing from the export");
            foreach (var c in dataColumns.Except(registryColumns)) result.Mismatches.Add(spec.Endpoint + ": export column '" + c + "' is not a registry column");

            // Map every exported column to an entity member, independently.
            var accessors = new List<(string Name, Accessor Accessor)>();
            foreach (var c in dataColumns)
            {
                var a = Resolve(spec, c);
                if (a == null) result.Mismatches.Add(spec.Endpoint + "." + c + ": no member of " + spec.EntityType.Name + " maps to this column");
                else accessors.Add((c, a));
            }

            var lossChecks = LossChecks(spec, dataColumns);
            var expected = new Dictionary<long, ExpectedRow>();
            foreach (var row in spec.Rows(Envir.Edit))
                if (!expected.TryAdd(row.Key, row)) result.Mismatches.Add(spec.Endpoint + " key " + row.Key + ": duplicate key in Envir.Edit");

            var select = "SELECT " + string.Join(", ", reserved.Concat(accessors.Select(a => a.Name)).Select(Schema.Quote)) + " FROM " + Schema.Quote(spec.Endpoint);
            var seen = new HashSet<long>();
            var count = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = select;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    count++;
                    var keyCell = reader.GetValue(0);
                    if (keyCell is not long key) { result.Mismatches.Add(spec.Endpoint + ": non-integer key " + Show(keyCell)); continue; }
                    seen.Add(key);
                    if (!expected.TryGetValue(key, out var exp)) { result.Mismatches.Add(spec.Endpoint + " key " + key + ": row not in Envir.Edit"); continue; }

                    Compare(result, spec, key, Schema.ParentColumn, exp.Parent, reader.GetValue(1));
                    Compare(result, spec, key, Schema.PositionColumn, exp.Position, reader.GetValue(2));
                    for (var i = 0; i < accessors.Count; i++)
                    {
                        object want;
                        try { want = Expect(accessors[i].Accessor(exp)); }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            result.Mismatches.Add(spec.Endpoint + " key " + key + " column " + accessors[i].Name + ": cannot derive expected value: " + ex.Message);
                            continue;
                        }

                        Compare(result, spec, key, accessors[i].Name, want, reader.GetValue(i + 3));
                    }

                    foreach (var check in lossChecks)
                    {
                        var problem = check(exp.Entity);
                        if (problem != null) result.Mismatches.Add(spec.Endpoint + " key " + key + ": " + problem);
                    }
                }
            }

            foreach (var missing in expected.Keys.Where(k => !seen.Contains(k)))
                result.Mismatches.Add(spec.Endpoint + " key " + missing + ": row in Envir.Edit but missing from the export");
            if (count != expected.Count)
                result.Mismatches.Add(spec.Endpoint + ": row count " + count + " in export, " + expected.Count + " in Envir.Edit");

            result.Tables.Add((spec.Endpoint, count, accessors.Count));
            result.CellsCompared += (long)count * (accessors.Count + 2);
        }

        /// <summary>
        /// Entity content no column carries (would be lost): non-zero Stats entries whose Stat has no Stat_ column in the
        /// export, and arrays whose length differs from the number of Name_N columns.
        /// </summary>
        private static List<Func<object, string>> LossChecks(TableSpec spec, List<string> dataColumns)
        {
            var checks = new List<Func<object, string>>();
            var columns = new HashSet<string>(dataColumns, StringComparer.Ordinal);
            foreach (var m in Members(spec.EntityType))
            {
                var member = m;
                var type = MemberType(member);
                if (type == typeof(Stats))
                {
                    var exported = new HashSet<byte>();
                    foreach (var c in dataColumns.Where(c => c.StartsWith("Stat_", StringComparison.Ordinal)))
                        if (Enum.TryParse<Stat>(c.Substring(5), false, out var s)) exported.Add((byte)s);
                    if (exported.Count == 0) continue;
                    checks.Add(e =>
                    {
                        if (GetMember(member, e) is not Stats stats) return null;
                        var lost = stats.Values.Where(p => p.Value != 0 && !exported.Contains((byte)p.Key)).Select(p => (byte)p.Key + "=" + p.Value).ToList();
                        return lost.Count == 0 ? null : member.Name + " has stat key(s) the export cannot carry: " + string.Join(", ", lost);
                    });
                }
                else if (type.IsArray)
                {
                    var n = 0;
                    while (columns.Contains(member.Name + "_" + (n + 1).ToString(CultureInfo.InvariantCulture))) n++;
                    if (n == 0) continue;
                    checks.Add(e =>
                    {
                        var length = (GetMember(member, e) as Array)?.Length ?? -1;
                        return length == n ? null : member.Name + " has " + length + " elements, the export has " + n + " columns";
                    });
                }
            }

            return checks;
        }

        private static void Compare(Result result, TableSpec spec, long key, string column, object expected, object actual)
        {
            if (!SameCell(expected, actual))
                result.Mismatches.Add(spec.Endpoint + " key " + key + " column " + column + ": export has " + Show(actual) + ", entity has " + Show(expected));
        }

        // ================================================================== column -> member (independent naming rules)

        /// <summary>Reads the raw CLR value (boxed) and its declared type for one expected row.</summary>
        internal delegate (object Value, Type Type) Accessor(ExpectedRow row);

        /// <summary>
        /// Maps a column to the entity member it carries:
        /// parent column → the parent's Index; renamed columns (DestinationMapIndex, LocalIndex); a public field or property of
        /// the same name; Name_X / Name_Y of a Point; Stat_&lt;Stat&gt; of the single Stats field; Name_N (1-based) of an array.
        /// Returns null when nothing matches. <paramref name="member"/> names the member(s) used (for the completeness report).
        /// </summary>
        internal static Accessor Resolve(TableSpec spec, string column) => Resolve(spec, column, out _);

        internal static Accessor Resolve(TableSpec spec, string column, out MemberInfo member)
        {
            member = null;
            var t = spec.EntityType;
            if (column == spec.ParentColumn)
                return r => ((object)ReadInt(r.ParentEntity, "Index"), typeof(int));

            var name = spec.Renames.TryGetValue(column, out var renamed) ? renamed : column;
            var direct = FindMember(t, name);
            if (direct != null)
            {
                var m = direct;
                member = m;
                return r => (GetMember(m, r.Entity), MemberType(m));
            }

            if (column.EndsWith("_X", StringComparison.Ordinal) || column.EndsWith("_Y", StringComparison.Ordinal))
            {
                var p = FindMember(t, column.Substring(0, column.Length - 2));
                if (p != null && MemberType(p) == typeof(Point))
                {
                    var isX = column.EndsWith("_X", StringComparison.Ordinal);
                    member = p;
                    return r =>
                    {
                        var point = (Point)GetMember(p, r.Entity);
                        return (isX ? point.X : point.Y, typeof(int));
                    };
                }
            }

            if (column.StartsWith("Stat_", StringComparison.Ordinal))
            {
                var statsMembers = Members(t).Where(m => MemberType(m) == typeof(Stats)).ToList();
                if (statsMembers.Count == 1 && Enum.TryParse<Stat>(column.Substring(5), false, out var stat) && Enum.IsDefined(typeof(Stat), stat))
                {
                    var sm = statsMembers[0];
                    member = sm;
                    return r =>
                    {
                        var stats = GetMember(sm, r.Entity) as Stats;
                        return (stats == null ? 0 : stats[stat], typeof(int));
                    };
                }
            }

            var underscore = column.LastIndexOf('_');
            if (underscore > 0 && int.TryParse(column.Substring(underscore + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
            {
                var am = FindMember(t, column.Substring(0, underscore));
                if (am != null && MemberType(am).IsArray)
                {
                    var elementType = MemberType(am).GetElementType();
                    member = am;
                    return r =>
                    {
                        if (GetMember(am, r.Entity) is not Array arr) throw new InvalidOperationException(am.Name + " is null");
                        if (n > arr.Length) throw new InvalidOperationException(am.Name + " has " + arr.Length + " elements, column wants element " + n);
                        return (arr.GetValue(n - 1), elementType);
                    };
                }
            }

            return null;
        }

        internal static IEnumerable<MemberInfo> Members(Type t) =>
            t.GetFields(BindingFlags.Public | BindingFlags.Instance).Cast<MemberInfo>()
                .Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0));

        private static MemberInfo FindMember(Type t, string name) => Members(t).FirstOrDefault(m => m.Name == name);

        internal static Type MemberType(MemberInfo m) => m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

        private static object GetMember(MemberInfo m, object entity) => m is FieldInfo f ? f.GetValue(entity) : ((PropertyInfo)m).GetValue(entity);

        private static int ReadInt(object entity, string name)
        {
            if (entity == null) throw new InvalidOperationException("child row has no parent");
            return (int)GetMember(FindMember(entity.GetType(), name) ?? throw new InvalidOperationException(entity.GetType().Name + " has no " + name), entity);
        }

        // ================================================================== expected encoding (re-implemented; see P7-SQLite-Export.md)

        /// <summary>
        /// The SQLite cell an entity value must produce: enums → their numeric value, bool → 0/1, integers → long,
        /// float/double → double, string → string (null stays null), DateTime → ToBinary(), List&lt;int&gt; → "[a,b,c]" (null → "[]").
        /// </summary>
        internal static object Expect((object Value, Type Type) v)
        {
            var (value, type) = v;
            if (type == typeof(List<int>))
            {
                var list = value as List<int> ?? new List<int>();
                var sb = new System.Text.StringBuilder("[");
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(list[i].ToString(CultureInfo.InvariantCulture));
                }

                return sb.Append(']').ToString();
            }

            if (value == null)
            {
                if (type == typeof(string)) return null;
                throw new InvalidOperationException("null " + type.Name);
            }

            if (type.IsEnum)
            {
                // Underlying numeric value, read via the raw bits so no name lookup can be involved.
                var raw = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
                return raw is ulong ul ? checked((long)ul) : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            }

            switch (value)
            {
                case bool b: return b ? 1L : 0L;
                case byte x: return (long)x;
                case sbyte x: return (long)x;
                case short x: return (long)x;
                case ushort x: return (long)x;
                case int x: return (long)x;
                case uint x: return (long)x;
                case long x: return x;
                case ulong x: return checked((long)x);
                case char x: return (long)x;
                case float x: return (double)x;
                case double x: return x;
                case decimal x: return x.ToString(CultureInfo.InvariantCulture);
                case string s: return s;
                case DateTime d: return d.ToBinary();
            }

            throw new InvalidOperationException("no expected encoding for " + value.GetType().FullName);
        }

        private static bool SameCell(object expected, object actual)
        {
            if (actual is DBNull) actual = null;
            if (expected == null || actual == null) return expected == null && actual == null;
            if (expected is long el && actual is long al) return el == al;
            if (expected is double ed && actual is double ad) return ed.Equals(ad);
            if (expected is string es && actual is string @as) return string.Equals(es, @as, StringComparison.Ordinal);
            return false; // different storage classes
        }

        private static string Show(object v)
        {
            if (v == null || v is DBNull) return "NULL";
            if (v is string s) return "'" + (s.Length > 60 ? s.Substring(0, 60) + "..." : s) + "' (TEXT)";
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture) + " (REAL)";
            if (v is long l) return l.ToString(CultureInfo.InvariantCulture) + " (INTEGER)";
            return v + " (" + v.GetType().Name + ")";
        }

        private static string SqlString(string s) => "'" + s.Replace("'", "''") + "'";

        private static List<object[]> Query(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row);
            }

            return rows;
        }
    }
}
