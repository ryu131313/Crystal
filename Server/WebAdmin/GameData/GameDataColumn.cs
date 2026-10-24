using System.Drawing;
using System.Reflection;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// One row of a game-data grid: the Crystal object it projects (<see cref="Entity"/>) plus, for child tables, the owning record.
    /// </summary>
    internal sealed class GameDataRow
    {
        /// <summary>Row key published as "Index" (synthetic for child tables, see <see cref="GameDataRegistry"/>).</summary>
        public int Index;

        /// <summary>The Crystal record (ItemInfo, MonsterInfo, RespawnInfo, ...).</summary>
        public object Entity;

        /// <summary>Owning record for child tables (MapInfo / ConquestInfo), else null.</summary>
        public object Parent;

        /// <summary>Owning record's Index for child tables (MapIndex / ConquestIndex), else 0.</summary>
        public int ParentIndex;

        /// <summary>Position inside the owning list (child tables) or the top-level list.</summary>
        public int Position;
    }

    /// <summary>Column kinds published in the schema ("kind" property, matches admin-schema.js).</summary>
    internal static class GameDataKinds
    {
        public const string Number = "number";
        public const string Bool = "bool";
        public const string Enum = "enum";
        public const string String = "string";
        public const string DateTime = "datetime";
        public const string Reference = "reference";
    }

    /// <summary>
    /// One editable (or read-only) grid column. <see cref="ValueType"/> is the CLR type of the typed value that
    /// <see cref="Get"/> returns and <see cref="Set"/> accepts (an enum type, int, byte, string, DateTime, List&lt;int&gt; ...).
    /// </summary>
    internal sealed class GameDataColumn
    {
        public string Name;

        /// <summary>CLR type of the typed value (List&lt;int&gt; for comma-list columns).</summary>
        public Type ValueType;

        public Func<GameDataRow, object> Get;

        /// <summary>Null for read-only columns.</summary>
        public Action<GameDataRow, object> Set;

        /// <summary>
        /// Read-only columns whose value is not a same-named field of the entity (quest V2 columns live on QuestInfo.Definition): how the
        /// SQLite store restores the value on load. The grid never uses it (the column stays read-only there).
        /// </summary>
        public Action<GameDataRow, object> StoreSet;

        public bool ReadOnly;

        /// <summary>Lookup name for the schema (items, monsters, maps, npcs, quests, magics, conquests, minesets).</summary>
        public string Lookup;

        /// <summary>FK target checked on write (same names as <see cref="Lookup"/>); null = no FK.</summary>
        public string ForeignKey;

        /// <summary>FK must be non-zero.</summary>
        public bool Required;

        /// <summary>Treat the enum as a bit set even without [Flags] (WeatherSetting).</summary>
        public bool ForceFlags;

        /// <summary>Column group for the schema ("Identity", "Stats", "Flags", ...).</summary>
        public string Group;

        public bool Multiline;

        /// <summary>The owning table's endpoint (set by <see cref="GameDataRegistry"/>); drives the LiveDb classification.</summary>
        public string Table;

        /// <summary>
        /// Compatible view of the LiveDb classification: true when a saved change of this column reaches the running world in this build
        /// (<see cref="LiveDb.LiveDbClassification.Effective"/> is ReadThrough or Hooked).
        /// </summary>
        public bool LiveSafe => Table != null && LiveDb.LiveDbClassification.Effective(Table, Name).IsLive;

        /// <summary>Synthetic parent column of a child table (MapIndex / ConquestIndex): writable only on insert.</summary>
        public bool IsParentField;

        public bool IsList => ValueType == typeof(List<int>);

        public bool IsEnum => ValueType.IsEnum;

        public bool IsFlags => ValueType.IsEnum && (ForceFlags || ValueType.IsDefined(typeof(FlagsAttribute), false));

        public bool IsReference => !string.IsNullOrEmpty(Lookup) && !IsList;

        public string Kind
        {
            get
            {
                if (IsReference) return GameDataKinds.Reference;
                if (ValueType == typeof(bool)) return GameDataKinds.Bool;
                if (ValueType.IsEnum) return GameDataKinds.Enum;
                if (ValueType == typeof(string) || IsList) return GameDataKinds.String;
                if (ValueType == typeof(System.DateTime)) return GameDataKinds.DateTime;
                return GameDataKinds.Number;
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>Options for <see cref="GameDataColumns.Reflect"/>.</summary>
    internal sealed class GameDataReflectOptions
    {
        /// <summary>When set, only these fields (in this order) become columns.</summary>
        public string[] Include;

        /// <summary>Fields that never become columns (runtime state, object refs, keys published as "Index").</summary>
        public HashSet<string> Exclude = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Field name → column name (e.g. MovementInfo.MapIndex → DestinationMapIndex).</summary>
        public Dictionary<string, string> Rename = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Column name → lookup / FK name.</summary>
        public Dictionary<string, string> Lookups = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Column names whose FK must be non-zero.</summary>
        public HashSet<string> Required = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> ForceFlags = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> Multiline = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds grid columns from a Crystal type's public instance fields. Supported field types: primitives, string, enums, bool,
    /// DateTime, <see cref="Point"/> (→ <c>Field_X</c>/<c>Field_Y</c>), <see cref="Stats"/> (→ <c>Stat_&lt;Name&gt;</c> per Stat member),
    /// <c>List&lt;int&gt;</c> (→ comma string), arrays of primitives (→ <c>Field_1..n</c>). Everything else (object refs, cycles, runtime
    /// lists) is skipped.
    /// </summary>
    internal static class GameDataColumns
    {
        public const string StatPrefix = "Stat_";

        /// <summary>Stat members exposed as columns (every Stat except Unknown), in enum order.</summary>
        public static IReadOnlyList<Stat> StatMembers { get; } = Enum.GetValues(typeof(Stat)).Cast<Stat>()
            .Where(s => s != Stat.Unknown).Distinct().OrderBy(s => (int)s).ToArray();

        public static bool IsScalar(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(decimal);
        }

        public static List<GameDataColumn> Reflect(Type type, GameDataReflectOptions options)
        {
            options ??= new GameDataReflectOptions();
            var columns = new List<GameDataColumn>();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            IEnumerable<FieldInfo> ordered = fields;

            if (options.Include != null)
            {
                var byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
                var list = new List<FieldInfo>();
                foreach (var name in options.Include)
                {
                    if (!byName.TryGetValue(name, out var f))
                        throw new InvalidOperationException("Game data: " + type.Name + " has no public field " + name);
                    list.Add(f);
                }

                ordered = list;
            }

            foreach (var field in ordered)
            {
                if (options.Exclude.Contains(field.Name)) continue;
                if (field.IsInitOnly || field.IsLiteral) continue;

                var ft = field.FieldType;
                var columnName = options.Rename.TryGetValue(field.Name, out var renamed) ? renamed : field.Name;

                if (IsScalar(ft))
                {
                    var f = field;
                    columns.Add(Configure(new GameDataColumn
                    {
                        Name = columnName,
                        ValueType = ft,
                        Get = r => f.GetValue(r.Entity),
                        Set = (r, v) => f.SetValue(r.Entity, v),
                    }, options));
                }
                else if (ft == typeof(Point))
                {
                    var f = field;
                    columns.Add(Configure(new GameDataColumn
                    {
                        Name = columnName + "_X",
                        ValueType = typeof(int),
                        Get = r => ((Point)f.GetValue(r.Entity)).X,
                        Set = (r, v) =>
                        {
                            var p = (Point)f.GetValue(r.Entity);
                            f.SetValue(r.Entity, new Point((int)v, p.Y));
                        },
                    }, options, columnName));
                    columns.Add(Configure(new GameDataColumn
                    {
                        Name = columnName + "_Y",
                        ValueType = typeof(int),
                        Get = r => ((Point)f.GetValue(r.Entity)).Y,
                        Set = (r, v) =>
                        {
                            var p = (Point)f.GetValue(r.Entity);
                            f.SetValue(r.Entity, new Point(p.X, (int)v));
                        },
                    }, options, columnName));
                }
                else if (ft == typeof(Stats))
                {
                    var f = field;
                    foreach (var stat in StatMembers)
                    {
                        var s = stat;
                        columns.Add(Configure(new GameDataColumn
                        {
                            Name = StatPrefix + s,
                            ValueType = typeof(int),
                            Group = "Stats",
                            Get = r => (f.GetValue(r.Entity) as Stats)?[s] ?? 0,
                            Set = (r, v) =>
                            {
                                var stats = f.GetValue(r.Entity) as Stats;
                                if (stats == null)
                                {
                                    stats = new Stats();
                                    f.SetValue(r.Entity, stats);
                                }

                                stats[s] = (int)v;
                            },
                        }, options, columnName));
                    }
                }
                else if (ft == typeof(List<int>))
                {
                    var f = field;
                    columns.Add(Configure(new GameDataColumn
                    {
                        Name = columnName,
                        ValueType = typeof(List<int>),
                        Get = r => new List<int>((f.GetValue(r.Entity) as List<int>) ?? new List<int>()),
                        Set = (r, v) =>
                        {
                            var list = f.GetValue(r.Entity) as List<int>;
                            if (list == null)
                            {
                                list = new List<int>();
                                f.SetValue(r.Entity, list);
                            }

                            list.Clear();
                            list.AddRange((List<int>)v);
                        },
                    }, options));
                }
                else if (ft.IsArray && ft.GetArrayRank() == 1 && IsScalar(ft.GetElementType()) && ft.GetElementType() != typeof(string))
                {
                    // Fixed-length arrays (DragonInfo.Exps): one column per element, sized from a fresh instance.
                    var f = field;
                    var length = ArrayLength(type, field);
                    var elementType = ft.GetElementType();
                    for (var i = 0; i < length; i++)
                    {
                        var at = i;
                        columns.Add(Configure(new GameDataColumn
                        {
                            Name = columnName + "_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ValueType = elementType,
                            Get = r =>
                            {
                                var arr = f.GetValue(r.Entity) as Array;
                                return arr != null && at < arr.Length ? arr.GetValue(at) : Activator.CreateInstance(elementType);
                            },
                            Set = (r, v) =>
                            {
                                if (f.GetValue(r.Entity) is Array arr && at < arr.Length) arr.SetValue(v, at);
                            },
                        }, options, columnName));
                    }
                }
            }

            return columns;
        }

        private static int ArrayLength(Type type, FieldInfo field)
        {
            try
            {
                var instance = Activator.CreateInstance(type);
                return (field.GetValue(instance) as Array)?.Length ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static GameDataColumn Configure(GameDataColumn column, GameDataReflectOptions options, string sourceName = null)
        {
            var key = column.Name;
            if (options.Lookups.TryGetValue(key, out var lookup))
            {
                column.Lookup = lookup;
                column.ForeignKey = lookup;
            }

            column.Required = options.Required.Contains(key);
            column.ForceFlags = options.ForceFlags.Contains(key);
            column.Multiline = options.Multiline.Contains(key);

            if (options.ReadOnly.Contains(key) || (sourceName != null && options.ReadOnly.Contains(sourceName)))
            {
                column.ReadOnly = true;
                column.Set = null;
            }

            column.Group ??= DefaultGroup(column);
            return column;
        }

        private static string DefaultGroup(GameDataColumn column)
        {
            var name = column.Name;
            if (column.ValueType == typeof(bool)) return "Flags";
            if (column.IsFlags) return "Flags";
            if (name.StartsWith("Required", StringComparison.Ordinal) || name.StartsWith("Need", StringComparison.Ordinal) ||
                name == "MinLev" || name == "MaxLev")
                return "Requirements";
            if (column.ValueType == typeof(string) || column.IsEnum || column.IsReference || column.IsList) return "Identity";
            return "Values";
        }
    }
}
