using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Server.MirDatabase;
using Server.WebAdmin.GameData;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// Detached rows of one envir's system data: only long / double / string / null cells and no references to model objects, so the
    /// SQLite writer thread can take them without touching game state (roadmap P7 phase 7.2).
    /// </summary>
    internal sealed class WorldRows
    {
        public sealed class Table
        {
            public string Name;
            /// <summary>parent_position, position, key, then <see cref="Columns"/>.</summary>
            public readonly List<object[]> Rows = new List<object[]>();
        }

        public readonly Dictionary<string, Table> Tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Counters = new Dictionary<string, long>(StringComparer.Ordinal);
        public long BaseSpawnRate;
        public readonly List<(long UserCount, double DelayLoss)> RespawnOptions = new List<(long, double)>();
        /// <summary>RespawnTimer.CurrentTickcounter (runtime state).</summary>
        public long RespawnTickCounter;
        public int EnvirVersion;
        public int EnvirCustomVersion;
    }

    /// <summary>
    /// Encodes the design lists of an envir into <see cref="WorldRows"/> and decodes them back (roadmap P7 phase 7.2). Columns are the
    /// web admin's <see cref="GameDataRegistry"/> columns, the encoding is the P7 step 1 exporter's (enums as INTEGER, DateTime as
    /// ToBinary, List&lt;int&gt; as JSON), and decode is loud: an unknown column, a NULL in a non-nullable column, a type mismatch or a
    /// value out of range fails with table, row and column. Every list keeps its order (rows are keyed by position), so binary → SQLite →
    /// binary gives the same bytes. Encode and decode run on the caller's thread (never the game thread: see SqliteWorldStore).
    /// </summary>
    internal static class SqliteWorldCodec
    {
        /// <summary>3 = drops as tables (Drops-Tables-Plan phase 1): a build that still reads drop_lines (2) refuses the file instead of
        /// finding no drops. A 2 store is still read, and becomes 3 when the writer opens it (<see cref="SqliteWorldFile.UpgradeSchemaVersion"/>).</summary>
        public const int SchemaVersion = 3;

        public const int OldestReadableSchemaVersion = 2;
        public const string ParentPositionColumn = "parent_position";
        public const string PositionColumn = "position";
        public const string KeyColumn = "key";
        /// <summary>Per Stats field: the stat keys stored with an explicit 0 (the binary format keeps them; the indexer drops them).</summary>
        public const string ZeroStatsSuffix = "__zero";

        public static readonly string[] CounterNames =
            { "MapIndex", "ItemIndex", "MonsterIndex", "NPCIndex", "QuestIndex", "GameshopIndex", "ConquestIndex", "RespawnIndex" };

        internal sealed class Column
        {
            public string Name;
            public Type ValueType;
            public string SqlType;
            public Func<GameDataRow, object> Get;
            public Action<GameDataRow, object> Set;
        }

        internal sealed class TableSpec
        {
            public string Name;
            public Type EntityType;
            public string Parent; // "maps" / "conquests" / null
            public Func<Envir, IList> TopList; // top-level tables
            public Func<object, IList> ChildList; // child tables: the parent's list
            public Func<object, int> Key;
            public Action<object, int> SetKey; // null when the key isn't a field of its own (it is a column, or synthetic)
            public FieldInfo StatsField; // null when the table has no Stat_ columns
            public readonly List<Column> Columns = new List<Column>();
            public bool Single; // dragon
        }

        private static readonly Lazy<List<TableSpec>> LazySpecs = new Lazy<List<TableSpec>>(BuildSpecs);

        /// <summary>Parents before children: decode attaches child rows to parents already built.</summary>
        public static IReadOnlyList<TableSpec> Specs => LazySpecs.Value;

        // ------------------------------------------------------------------ specs

        private static List<TableSpec> BuildSpecs()
        {
            var specs = new List<TableSpec>
            {
                Top("maps", e => e.MapInfoList, o => ((MapInfo)o).Index, (o, k) => ((MapInfo)o).Index = k),
                Top("items", e => e.ItemInfoList, o => ((ItemInfo)o).Index, (o, k) => ((ItemInfo)o).Index = k),
                Top("monsters", e => e.MonsterInfoList, o => ((MonsterInfo)o).Index, (o, k) => ((MonsterInfo)o).Index = k),
                Top("npcs", e => e.NPCInfoList, o => ((NPCInfo)o).Index, (o, k) => ((NPCInfo)o).Index = k),
                Top("quests", e => e.QuestInfoList, o => ((QuestInfo)o).Index, (o, k) => ((QuestInfo)o).Index = k),
                // Every MagicInfo, including Spell.None rows (the registry grid hides them; D3 keeps them). Spell is a column.
                Top("magic", e => e.MagicInfoList, o => (int)((MagicInfo)o).Spell, null),
                Top("gameshop", e => e.GameShopList, o => ((GameShopItem)o).GIndex, (o, k) => ((GameShopItem)o).GIndex = k),
                Top("conquests", e => e.ConquestInfoList, o => ((ConquestInfo)o).Index, (o, k) => ((ConquestInfo)o).Index = k),
                Child("mapsafezones", "maps", p => ((MapInfo)p).SafeZones, null, null),
                Child("maprespawns", "maps", p => ((MapInfo)p).Respawns, o => ((RespawnInfo)o).RespawnIndex, (o, k) => ((RespawnInfo)o).RespawnIndex = k),
                Child("mapmovements", "maps", p => ((MapInfo)p).Movements, null, null),
                Child("mapminezones", "maps", p => ((MapInfo)p).MineZones, null, null),
                Child("conquestguards", "conquests", p => ((ConquestInfo)p).ConquestGuards, o => ((ConquestArcherInfo)o).Index, null),
                Child("conquestgates", "conquests", p => ((ConquestInfo)p).ConquestGates, o => ((ConquestGateInfo)o).Index, null),
                Child("conquestwalls", "conquests", p => ((ConquestInfo)p).ConquestWalls, o => ((ConquestWallInfo)o).Index, null),
                Child("conquestsieges", "conquests", p => ((ConquestInfo)p).ConquestSieges, o => ((ConquestSiegeInfo)o).Index, null),
                Child("conquestflags", "conquests", p => ((ConquestInfo)p).ConquestFlags, o => ((ConquestFlagInfo)o).Index, null),
                Child("conquestcontrolpoints", "conquests", p => ((ConquestInfo)p).ControlPoints, o => ((ConquestFlagInfo)o).Index, null),
                new TableSpec { Name = "dragon", Single = true, Key = _ => 1 },
            };

            var known = new HashSet<string>(specs.Select(s => s.Name), StringComparer.Ordinal);
            foreach (var t in GameDataRegistry.Tables)
                if (!known.Contains(t.Endpoint))
                    throw new InvalidOperationException("[Store] game data table '" + t.Endpoint + "' has no SQLite table spec");

            foreach (var spec in specs)
            {
                var table = GameDataRegistry.Get(spec.Name) ?? throw new InvalidOperationException("[Store] no registry table " + spec.Name);
                spec.EntityType = table.EntityType;
                foreach (var c in table.Columns)
                {
                    if (c.IsParentField) continue; // parent_position replaces it
                    var sql = SqlTypeOf(c.ValueType) ?? throw new InvalidOperationException("[Store] " + spec.Name + "." + c.Name + ": no SQLite encoding for " + c.ValueType);
                    spec.Columns.Add(new Column { Name = c.Name, ValueType = c.ValueType, SqlType = sql, Get = c.Get, Set = c.Set ?? ReadOnlySetter(spec, c) });
                }

                spec.StatsField = spec.Columns.Any(c => c.Name.StartsWith(GameDataColumns.StatPrefix, StringComparison.Ordinal))
                    ? spec.EntityType.GetFields(BindingFlags.Public | BindingFlags.Instance).Single(f => f.FieldType == typeof(Stats))
                    : null;
            }

            return specs;
        }

        private static TableSpec Top(string name, Func<Envir, IList> list, Func<object, int> key, Action<object, int> setKey) =>
            new TableSpec { Name = name, TopList = list, Key = key, SetKey = setKey };

        private static TableSpec Child(string name, string parent, Func<object, IList> list, Func<object, int> key, Action<object, int> setKey) =>
            new TableSpec { Name = name, Parent = parent, ChildList = list, Key = key ?? (_ => 0), SetKey = setKey };

        /// <summary>Read-only grid columns are still saved data: set the same-named field (LocalIndex is the record's own Index).</summary>
        private static Action<GameDataRow, object> ReadOnlySetter(TableSpec spec, GameDataColumn c)
        {
            var fieldName = c.Name == "LocalIndex" ? "Index" : c.Name;
            var field = spec.EntityType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new InvalidOperationException("[Store] " + spec.Name + "." + c.Name + ": read-only column with no field to restore it");
            if (field.FieldType != c.ValueType)
                throw new InvalidOperationException("[Store] " + spec.Name + "." + c.Name + ": field type " + field.FieldType + " differs from column type " + c.ValueType);
            return (r, v) => field.SetValue(r.Entity, v);
        }

        // ------------------------------------------------------------------ DDL

        public static IEnumerable<string> CreateStatements()
        {
            yield return "CREATE TABLE meta (name TEXT PRIMARY KEY, value ANY) STRICT";
            yield return "CREATE TABLE index_counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL) STRICT";
            yield return "CREATE TABLE respawn_tick (id INTEGER PRIMARY KEY CHECK (id = 1), BaseSpawnRate INTEGER NOT NULL) STRICT";
            yield return "CREATE TABLE respawn_tick_options (position INTEGER PRIMARY KEY, UserCount INTEGER NOT NULL, DelayLoss REAL NOT NULL) STRICT";
            yield return "CREATE TABLE runtime_state (name TEXT PRIMARY KEY, value ANY) STRICT";
            foreach (var (_, ddl) in DesignStore.Tables()) yield return ddl; // roadmap P7 phase 7.4

            foreach (var spec in Specs)
            {
                var sb = new StringBuilder();
                sb.Append("CREATE TABLE ").Append(Quote(spec.Name)).Append(" (")
                  .Append(ParentPositionColumn).Append(" INTEGER NOT NULL, ")
                  .Append(PositionColumn).Append(" INTEGER NOT NULL, ")
                  .Append(KeyColumn).Append(" INTEGER NOT NULL");
                foreach (var c in spec.Columns)
                    sb.Append(", ").Append(Quote(c.Name)).Append(' ').Append(c.SqlType);
                if (spec.StatsField != null)
                    sb.Append(", ").Append(Quote(spec.StatsField.Name + ZeroStatsSuffix)).Append(" TEXT NOT NULL");
                sb.Append(", PRIMARY KEY (").Append(ParentPositionColumn).Append(", ").Append(PositionColumn).Append(")) STRICT");
                yield return sb.ToString();
            }
        }

        /// <summary>Every stored column of a table (name, SQL type), as <see cref="ColumnNames"/> minus the three key columns.</summary>
        public static IEnumerable<(string Name, string SqlType)> StoredColumns(TableSpec spec)
        {
            foreach (var c in spec.Columns) yield return (c.Name, c.SqlType);
            if (spec.StatsField != null) yield return (spec.StatsField.Name + ZeroStatsSuffix, "TEXT");
        }

        /// <summary>The encoded value a freshly constructed model holds in <paramref name="column"/> (an additive migration's default).</summary>
        public static object ColumnDefault(TableSpec spec, string column)
        {
            if (spec.StatsField != null && column == spec.StatsField.Name + ZeroStatsSuffix) return "[]";
            var c = spec.Columns.First(x => x.Name == column);
            var row = new GameDataRow { Entity = Activator.CreateInstance(spec.EntityType, nonPublic: true) };
            if (!TryEncode(c.ValueType, c.Get(row), out var value, out var error))
                throw new InvalidOperationException("[Store] " + spec.Name + "." + c.Name + ": default can't be stored: " + error);
            return value;
        }

        /// <summary>The cells of a freshly constructed model at position 0 (a new one-row table such as dragon).</summary>
        public static object[] FreshRow(TableSpec spec)
        {
            var errors = new List<string>();
            var cells = EncodeRow(spec, 0, null, 0, Activator.CreateInstance(spec.EntityType, nonPublic: true), errors);
            if (errors.Count > 0) throw new InvalidOperationException("[Store] " + spec.Name + ": fresh row can't be stored: " + string.Join("; ", errors));
            return cells;
        }

        /// <summary>The column names of a table's rows, in cell order.</summary>
        public static List<string> ColumnNames(TableSpec spec)
        {
            var names = new List<string> { ParentPositionColumn, PositionColumn, KeyColumn };
            names.AddRange(spec.Columns.Select(c => c.Name));
            if (spec.StatsField != null) names.Add(spec.StatsField.Name + ZeroStatsSuffix);
            return names;
        }

        public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        // ------------------------------------------------------------------ encode

        /// <summary>
        /// Encodes <paramref name="e"/>'s design data. Errors (a value that can't be stored losslessly) throw
        /// <see cref="InvalidDataException"/> listing up to 20 of them; nothing is half-encoded.
        /// </summary>
        public static WorldRows Encode(Envir e, long respawnTickCounter)
        {
            var rows = new WorldRows
            {
                EnvirVersion = Envir.Version,
                EnvirCustomVersion = Envir.CustomVersion,
                BaseSpawnRate = e.RespawnTick.BaseSpawnRate,
                RespawnTickCounter = respawnTickCounter,
            };

            rows.Counters["MapIndex"] = e.MapIndex; rows.Counters["ItemIndex"] = e.ItemIndex; rows.Counters["MonsterIndex"] = e.MonsterIndex;
            rows.Counters["NPCIndex"] = e.NPCIndex; rows.Counters["QuestIndex"] = e.QuestIndex; rows.Counters["GameshopIndex"] = e.GameshopIndex;
            rows.Counters["ConquestIndex"] = e.ConquestIndex; rows.Counters["RespawnIndex"] = e.RespawnIndex;
            foreach (var o in e.RespawnTick.Respawn) rows.RespawnOptions.Add((o.UserCount, o.DelayLoss));

            var errors = new List<string>();
            foreach (var spec in Specs)
            {
                var table = new WorldRows.Table { Name = spec.Name };
                rows.Tables[spec.Name] = table;
                foreach (var (parentPos, parent, pos, entity) in Enumerate(e, spec))
                    table.Rows.Add(EncodeRow(spec, parentPos, parent, pos, entity, errors));
            }

            if (errors.Count > 0)
                throw new InvalidDataException("[Store] " + errors.Count.ToString(CultureInfo.InvariantCulture) + " value(s) can't be stored: " + string.Join("; ", errors.Take(20)));
            return rows;
        }

        private static IEnumerable<(int ParentPos, object Parent, int Pos, object Entity)> Enumerate(Envir e, TableSpec spec)
        {
            if (spec.Single)
            {
                if (e.DragonInfo != null) yield return (0, null, 0, e.DragonInfo);
                yield break;
            }

            if (spec.Parent == null)
            {
                var list = spec.TopList(e);
                for (var i = 0; i < list.Count; i++) yield return (0, null, i, list[i]);
                yield break;
            }

            var parents = Specs.First(s => s.Name == spec.Parent).TopList(e);
            for (var p = 0; p < parents.Count; p++)
            {
                var children = spec.ChildList(parents[p]);
                for (var i = 0; i < children.Count; i++) yield return (p, parents[p], i, children[i]);
            }
        }

        private static object[] EncodeRow(TableSpec spec, int parentPos, object parent, int pos, object entity, List<string> errors)
        {
            var cells = new object[3 + spec.Columns.Count + (spec.StatsField != null ? 1 : 0)];
            cells[0] = (long)parentPos;
            cells[1] = (long)pos;
            cells[2] = (long)spec.Key(entity);
            var row = new GameDataRow { Entity = entity, Parent = parent, Position = pos, Index = spec.Key(entity) };

            for (var i = 0; i < spec.Columns.Count; i++)
            {
                var c = spec.Columns[i];
                if (!TryEncode(c.ValueType, c.Get(row), out var value, out var error))
                    errors.Add(spec.Name + " position " + pos.ToString(CultureInfo.InvariantCulture) + " column " + c.Name + ": " + error);
                cells[3 + i] = value;
            }

            if (spec.StatsField != null)
            {
                var stats = spec.StatsField.GetValue(entity) as Stats;
                var zero = new List<int>();
                if (stats != null)
                {
                    foreach (var pair in stats.Values)
                    {
                        if (pair.Value == 0) zero.Add((byte)pair.Key);
                        else if (pair.Key == Stat.Unknown || !Enum.IsDefined(typeof(Stat), pair.Key))
                            errors.Add(spec.Name + " position " + pos.ToString(CultureInfo.InvariantCulture) + ": stat key " + ((byte)pair.Key).ToString(CultureInfo.InvariantCulture) + " has no column");
                    }
                }

                cells[cells.Length - 1] = JsonInts(zero);
            }

            return cells;
        }

        // ------------------------------------------------------------------ decode

        /// <summary>
        /// Replaces <paramref name="e"/>'s design lists and counters with <paramref name="rows"/>, as the binary LoadDB does (MagicInfoList
        /// is not cleared: spells already present are kept, as in the binary load). Throws <see cref="InvalidDataException"/> with table,
        /// position and column on the first bad cell. Caller holds LoadLock.
        /// </summary>
        public static void Decode(WorldRows rows, Envir e)
        {
            foreach (var name in CounterNames)
                if (!rows.Counters.ContainsKey(name)) throw new InvalidDataException("[Store] index_counters has no " + name);

            var built = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var spec in Specs)
            {
                if (!rows.Tables.TryGetValue(spec.Name, out var table)) throw new InvalidDataException("[Store] missing table " + spec.Name);
                var entities = new List<object>();
                built[spec.Name] = entities;
                var parents = spec.Parent == null ? null : built[spec.Parent];
                var expectedParent = 0;
                var expectedPos = 0;

                foreach (var cells in table.Rows)
                {
                    var parentPos = (int)(long)cells[0];
                    var pos = (int)(long)cells[1];
                    if (parentPos != expectedParent) { expectedParent = parentPos; expectedPos = 0; }
                    if (pos != expectedPos || (spec.Parent == null && parentPos != 0) || (parents != null && (parentPos < 0 || parentPos >= parents.Count)))
                        throw new InvalidDataException("[Store] " + spec.Name + ": row (" + parentPos.ToString(CultureInfo.InvariantCulture) + ", " + pos.ToString(CultureInfo.InvariantCulture) + ") is out of order or has no parent");
                    expectedPos++;

                    var entity = Activator.CreateInstance(spec.EntityType, nonPublic: true);
                    var parent = parents?[parentPos];
                    var row = new GameDataRow { Entity = entity, Parent = parent, Position = pos };
                    spec.SetKey?.Invoke(entity, (int)(long)cells[2]);

                    for (var i = 0; i < spec.Columns.Count; i++)
                    {
                        var c = spec.Columns[i];
                        object value;
                        try { value = DecodeValue(c.ValueType, cells[3 + i]); }
                        catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException || ex is FormatException || ex is InvalidCastException)
                        {
                            throw new InvalidDataException("[Store] " + spec.Name + " position " + pos.ToString(CultureInfo.InvariantCulture) + " column " + c.Name + ": " + ex.Message, ex);
                        }

                        c.Set(row, value);
                    }

                    if (spec.StatsField != null)
                    {
                        var stats = spec.StatsField.GetValue(entity) as Stats;
                        var zero = ParseInts(cells[cells.Length - 1] as string ?? throw new InvalidDataException("[Store] " + spec.Name + ": NULL zero-stats list"));
                        if (zero.Count > 0)
                        {
                            if (stats == null) { stats = new Stats(); spec.StatsField.SetValue(entity, stats); }
                            foreach (var k in zero) stats.Values[(Stat)checked((byte)k)] = 0;
                        }
                    }

                    if (entity is SafeZoneInfo zone) zone.Info = (MapInfo)parent;
                    if (entity is QuestInfo quest) quest.LoadInfo(); // the binary QuestInfo ctor loads the quest .txt body too
                    entities.Add(entity);
                    if (parent != null) spec.ChildList(parent).Add(entity);
                }
            }

            e.MapIndex = (int)rows.Counters["MapIndex"]; e.ItemIndex = (int)rows.Counters["ItemIndex"]; e.MonsterIndex = (int)rows.Counters["MonsterIndex"];
            e.NPCIndex = (int)rows.Counters["NPCIndex"]; e.QuestIndex = (int)rows.Counters["QuestIndex"]; e.GameshopIndex = (int)rows.Counters["GameshopIndex"];
            e.ConquestIndex = (int)rows.Counters["ConquestIndex"]; e.RespawnIndex = (int)rows.Counters["RespawnIndex"];

            Replace(e.MapInfoList, built["maps"]);
            e.MapDetailsCache.Clear();
            Replace(e.ItemInfoList, built["items"]);
            Replace(e.MonsterInfoList, built["monsters"]);
            Replace(e.NPCInfoList, built["npcs"]);
            Replace(e.QuestInfoList, built["quests"]);
            foreach (MagicInfo m in built["magic"])
                if (!e.MagicExists(m.Spell)) e.MagicInfoList.Add(m);
            Replace(e.GameShopList, built["gameshop"]);
            Replace(e.ConquestInfoList, built["conquests"]);
            e.DragonInfo = built["dragon"].Count == 1 ? (DragonInfo)built["dragon"][0] : throw new InvalidDataException("[Store] dragon must have exactly one row");

            var tick = new RespawnTimer { BaseSpawnRate = checked((byte)rows.BaseSpawnRate), CurrentTickcounter = checked((ulong)rows.RespawnTickCounter), LastTick = Envir.Main.Time };
            tick.Respawn.Clear();
            foreach (var (users, loss) in rows.RespawnOptions) tick.Respawn.Add(new RespawnTickOption { UserCount = checked((int)users), DelayLoss = loss });
            tick.CurrentDelay = (long)Math.Round((double)tick.BaseSpawnRate * (double)60000); // as RespawnTimer(BinaryReader): 0 would tick every loop
            e.RespawnTick = tick;
        }

        private static void Replace<T>(List<T> target, List<object> items)
        {
            target.Clear();
            foreach (var o in items) target.Add((T)o);
        }

        // ------------------------------------------------------------------ cell encoding (P7 step 1 rules)

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

            return t == typeof(List<int>) ? "TEXT" : null;
        }

        internal static bool TryEncode(Type t, object value, out object encoded, out string error)
        {
            encoded = null;
            error = null;

            if (t == typeof(string))
            {
                if (value == null) return true; // NULL, distinct from ''
                var s = (string)value;
                for (var i = 0; i < s.Length; i++)
                {
                    if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                    if (char.IsSurrogate(s[i])) { error = "string contains an unpaired surrogate"; return false; }
                }

                encoded = s;
                return true;
            }

            if (value == null)
            {
                if (t == typeof(List<int>)) { encoded = "[]"; return true; }
                error = "null value for non-nullable type " + t.Name;
                return false;
            }

            if (t.IsEnum)
            {
                var u = Enum.GetUnderlyingType(t);
                if (u == typeof(ulong))
                {
                    var v = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    if (v > long.MaxValue) { error = "enum value exceeds INTEGER range"; return false; }
                    encoded = (long)v;
                }
                else encoded = Convert.ToInt64(Convert.ChangeType(value, u, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return true;
            }

            if (t == typeof(bool)) { encoded = (bool)value ? 1L : 0L; return true; }
            if (t == typeof(DateTime)) { encoded = ((DateTime)value).ToBinary(); return true; }
            if (t == typeof(List<int>)) { encoded = JsonInts((List<int>)value); return true; }

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
                    if (ul > long.MaxValue) { error = "UInt64 value exceeds INTEGER range"; return false; }
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

            error = "no SQLite encoding for " + t.FullName;
            return false;
        }

        internal static object DecodeValue(Type t, object cell)
        {
            if (t == typeof(string))
            {
                if (cell == null) return null;
                return cell as string ?? throw new InvalidDataException("expected TEXT, found " + cell.GetType().Name);
            }

            if (cell == null) throw new InvalidDataException("NULL in a non-nullable column");

            if (t == typeof(List<int>)) return ParseInts(cell as string ?? throw new InvalidDataException("expected TEXT list"));
            if (t == typeof(decimal)) return decimal.Parse(cell as string ?? throw new InvalidDataException("expected TEXT decimal"), NumberStyles.Number, CultureInfo.InvariantCulture);

            if (Type.GetTypeCode(t) == TypeCode.Single || Type.GetTypeCode(t) == TypeCode.Double)
            {
                if (cell is not double d) throw new InvalidDataException("expected REAL, found " + cell.GetType().Name);
                if (t == typeof(double)) return d;
                var f = (float)d;
                if ((double)f != d) throw new InvalidDataException("REAL " + d.ToString("R", CultureInfo.InvariantCulture) + " is not a float");
                return f;
            }

            if (cell is not long l) throw new InvalidDataException("expected INTEGER, found " + cell.GetType().Name);
            if (t == typeof(bool))
            {
                if (l != 0 && l != 1) throw new InvalidDataException("bool must be 0 or 1");
                return l == 1;
            }

            if (t == typeof(DateTime)) return DateTime.FromBinary(l);
            if (t.IsEnum)
            {
                var u = Enum.GetUnderlyingType(t);
                return Enum.ToObject(t, u == typeof(ulong) ? (object)checked((ulong)l) : Convert.ChangeType(l, u, CultureInfo.InvariantCulture));
            }

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.SByte: return checked((sbyte)l);
                case TypeCode.Byte: return checked((byte)l);
                case TypeCode.Int16: return checked((short)l);
                case TypeCode.UInt16: return checked((ushort)l);
                case TypeCode.Int32: return checked((int)l);
                case TypeCode.UInt32: return checked((uint)l);
                case TypeCode.Int64: return l;
                case TypeCode.UInt64: return checked((ulong)l);
                case TypeCode.Char: return checked((char)l);
            }

            throw new InvalidDataException("no decoding for " + t.FullName);
        }

        internal static string JsonInts(List<int> values) =>
            "[" + string.Join(",", values.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";

        internal static List<int> ParseInts(string text)
        {
            text = text.Trim();
            if (text.Length < 2 || text[0] != '[' || text[text.Length - 1] != ']') throw new InvalidDataException("not a JSON int list: " + text);
            var body = text.Substring(1, text.Length - 2);
            var list = new List<int>();
            if (body.Length == 0) return list;
            foreach (var part in body.Split(','))
                list.Add(int.Parse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            return list;
        }
    }
}
