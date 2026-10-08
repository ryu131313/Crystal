using System.Collections.Concurrent;
using System.Globalization;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// GET /api/admin/db/schema/{endpoint}: the shape WoOOLcraft's admin-schema.js / admin-form-drawer.js consume
    /// (<c>{endpoint, type, properties:[{name, kind, type, readOnly, lookup?, enumValues?, members?, flags?, multiline?}]}</c>) plus
    /// MirCraft extras: per-property <c>group</c>, <c>list</c>/<c>listLookup</c> for comma-list columns, <c>live</c> for monster columns the
    /// "apply live" option pushes, and table-level <c>label</c>, <c>insertable</c>, <c>deletable</c>, <c>parent</c>, <c>indexNote</c>,
    /// <c>nameColumn</c>, <c>columnGroups</c>, <c>presets</c> (All / Basic / Stats / Flags column sets) and <c>defaults</c> (new-row values).
    /// Enums always publish <c>members</c> ({name, value}) because Crystal enums are not contiguous; [Flags] values are unsigned.
    /// </summary>
    internal static class GameDataSchema
    {
        private static readonly ConcurrentDictionary<string, object> Cache = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public static object Get(string endpoint)
        {
            var table = GameDataRegistry.Get(endpoint);
            if (table == null) return null;
            return Cache.GetOrAdd(table.Endpoint, _ => Build(table));
        }

        internal static object Build(GameDataTable table)
        {
            var properties = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "Index",
                    ["kind"] = GameDataKinds.Number,
                    ["type"] = "Int32",
                    ["readOnly"] = true,
                    ["group"] = "Identity",
                },
            };

            foreach (var c in table.Columns)
                properties.Add(Property(c));

            var groups = table.Columns
                .GroupBy(c => c.Group ?? "Values")
                .Select(g => new { id = g.Key.ToLowerInvariant(), label = g.Key, fields = g.Select(c => c.Name).ToList() })
                .ToList();

            var identity = new List<string> { "Index" };
            if (table.ParentField != null) identity.Add(table.ParentField);
            if (table.NameColumn != null && table.Column(table.NameColumn) != null) identity.Add(table.NameColumn);

            var basic = identity.Concat(table.Columns.Where(c => c.Group != "Stats" && c.ValueType != typeof(bool) && !c.IsFlags).Select(c => c.Name))
                .Distinct().ToList();
            var stats = identity.Concat(table.Columns.Where(c => c.Group == "Stats").Select(c => c.Name)).Distinct().ToList();
            var flags = identity.Concat(table.Columns.Where(c => c.ValueType == typeof(bool) || c.IsFlags).Select(c => c.Name)).Distinct().ToList();

            var presets = new Dictionary<string, object>
            {
                ["all"] = new[] { "Index" }.Concat(table.Columns.Select(c => c.Name)).ToList(),
                ["basic"] = basic,
            };
            if (stats.Count > identity.Count) presets["stats"] = stats;
            if (flags.Count > identity.Count) presets["flags"] = flags;

            return new Dictionary<string, object>
            {
                ["endpoint"] = table.Endpoint,
                ["type"] = table.EntityType.Name,
                ["label"] = table.Label,
                ["group"] = table.Group,
                ["insertable"] = table.Insertable,
                ["deletable"] = table.Deletable,
                ["parent"] = table.IsChild ? new { endpoint = table.ParentEndpoint, field = table.ParentField } : null,
                ["nameColumn"] = table.NameColumn,
                ["indexNote"] = table.IndexNote,
                ["properties"] = properties,
                ["columnGroups"] = groups,
                ["presets"] = presets,
            };
        }

        /// <summary>
        /// The schema plus <c>defaults</c>: the wire value of every writable column of a NEW row as the server creates it
        /// (<see cref="GameDataTable.NewDetached"/>: ItemInfo.RandomStatsId 255, MonsterInfo speeds / ViewRange, respawn Count / Delay,
        /// game-shop prices, zone sizes ...). The grid's "Add row" seeds new rows from it instead of 0 / "" / false, which would otherwise
        /// override those defaults (every column counts as written for an insert). Computed per request because some defaults depend on
        /// settings / the clock. Null defaults for non-insertable tables.
        /// </summary>
        public static object GetWithDefaults(string endpoint, GameDataContext ctx)
        {
            var table = GameDataRegistry.Get(endpoint);
            if (table == null) return null;
            if (!(Get(endpoint) is Dictionary<string, object> cached)) return Get(endpoint);

            var copy = new Dictionary<string, object>(cached);
            copy["defaults"] = Defaults(table, ctx);
            return copy;
        }

        internal static Dictionary<string, object> Defaults(GameDataTable table, GameDataContext ctx)
        {
            if (!table.Insertable || table.NewDetached == null) return null;

            var row = table.NewDetached(ctx);
            var defaults = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var c in table.Columns)
            {
                if (c.IsParentField || c.ReadOnly || c.Set == null) continue;
                try
                {
                    defaults[c.Name] = GameDataCodec.ToWire(c, c.Get(row));
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Default of " + table.Endpoint + "." + c.Name + " failed");
                }
            }

            return defaults;
        }

        internal static Dictionary<string, object> Property(GameDataColumn c)
        {
            var p = new Dictionary<string, object>
            {
                ["name"] = c.Name,
                ["kind"] = c.Kind,
                ["type"] = TypeName(c),
                ["readOnly"] = c.ReadOnly || c.Set == null,
                ["group"] = c.Group ?? "Values",
            };

            if (c.IsReference)
            {
                p["lookup"] = c.Lookup;
                p["required"] = c.Required;
            }

            if (c.IsList)
            {
                p["list"] = true;
                if (!string.IsNullOrEmpty(c.Lookup)) p["listLookup"] = c.Lookup;
            }

            if (c.IsEnum)
            {
                var names = Enum.GetNames(c.ValueType);
                p["enumValues"] = names;
                p["members"] = names.Select(n => new
                {
                    name = n,
                    value = c.IsFlags
                        ? GameDataCodec.EnumUnsigned(c.ValueType, Enum.Parse(c.ValueType, n))
                        : Convert.ToInt64(Enum.Parse(c.ValueType, n), CultureInfo.InvariantCulture),
                }).ToList();
                if (c.IsFlags) p["flags"] = true;
            }

            if (c.Multiline) p["multiline"] = true;
            if (c.LiveSafe) p["live"] = true;
            if (c.IsParentField) p["parentField"] = true;

            return p;
        }

        private static string TypeName(GameDataColumn c)
        {
            if (c.IsList) return "List<Int32>";
            if (c.IsEnum) return c.ValueType.Name;
            return c.ValueType.Name;
        }
    }
}
