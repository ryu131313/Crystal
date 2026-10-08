using System.Globalization;

namespace Server.WebAdmin.GameData
{
    /// <summary>One lookup option before finishing (sorting / de-duplicating labels).</summary>
    internal sealed class GameDataLookupItem
    {
        public int Value;
        public string Label;
        public string Detail;
    }

    /// <summary>
    /// GET /api/admin/lookups/{name} → <c>{options:[{value, label, detail?}]}</c> for items, monsters, maps, npcs, quests, magics,
    /// conquests, minesets (from the Edit envir, which is what the grids edit). The first option is always the empty choice
    /// (value 0, label "") so a reference can be cleared; labels shared by several rows are prefixed with "&lt;value&gt; — ".
    /// </summary>
    internal static class GameDataLookups
    {
        public static readonly string[] Names = { "items", "monsters", "maps", "npcs", "quests", "magics", "conquests", "minesets" };

        public static bool IsKnown(string name) => name != null && Names.Contains(name.Trim().ToLowerInvariant());

        /// <summary>Null for an unknown lookup name.</summary>
        public static List<GameDataLookupItem> Raw(GameDataContext ctx, string name)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "items":
                    return ctx.Items.Where(i => i != null).Select(i => new GameDataLookupItem { Value = i.Index, Label = i.Name, Detail = ItemDetail(i) }).ToList();
                case "monsters":
                    return ctx.Monsters.Where(m => m != null).Select(m => new GameDataLookupItem { Value = m.Index, Label = m.Name, Detail = MonsterDetail(m.Level, m.AI, m.Experience, m.IsBoss) }).ToList();
                case "maps":
                    return ctx.Maps.Where(m => m != null).Select(m => new GameDataLookupItem
                    {
                        Value = m.Index,
                        Label = MapLabel(m.Index, m.FileName, m.Title),
                        Detail = string.IsNullOrWhiteSpace(m.Title) ? null : m.FileName,
                    }).ToList();
                case "npcs":
                {
                    var maps = ctx.Maps.Where(m => m != null).GroupBy(m => m.Index).ToDictionary(g => g.Key, g => g.First());
                    return ctx.Npcs.Where(n => n != null).Select(n => new GameDataLookupItem
                    {
                        Value = n.Index,
                        Label = n.Name,
                        Detail = maps.TryGetValue(n.MapIndex, out var map) ? (string.IsNullOrWhiteSpace(map.Title) ? map.FileName : map.Title) : null,
                    }).ToList();
                }
                case "quests":
                    return ctx.Quests.Where(q => q != null).Select(q => new GameDataLookupItem { Value = q.Index, Label = q.Name, Detail = string.IsNullOrWhiteSpace(q.Group) ? null : q.Group }).ToList();
                case "magics":
                    return ctx.Magics.Where(m => m != null && m.Spell != Spell.None).Select(m => new GameDataLookupItem { Value = (int)m.Spell, Label = m.Name, Detail = m.Spell.ToString() }).ToList();
                case "conquests":
                    return ctx.Conquests.Where(c => c != null).Select(c => new GameDataLookupItem { Value = c.Index, Label = c.Name, Detail = "map " + c.MapIndex.ToString(CultureInfo.InvariantCulture) }).ToList();
                case "minesets":
                {
                    var names = ctx.MineSetNames();
                    var list = new List<GameDataLookupItem>();
                    for (var i = 0; i < names.Count; i++)
                        list.Add(new GameDataLookupItem { Value = i + 1, Label = string.IsNullOrWhiteSpace(names[i]) ? "Mine set " + (i + 1).ToString(CultureInfo.InvariantCulture) : names[i] });
                    return list;
                }
                default:
                    return null;
            }
        }

        /// <summary>Sorted options with the empty choice first and duplicate labels disambiguated.</summary>
        public static List<Dictionary<string, object>> Finish(IEnumerable<GameDataLookupItem> items, bool sortByLabel = true)
        {
            var raw = (items ?? Enumerable.Empty<GameDataLookupItem>())
                .Select(x => new GameDataLookupItem { Value = x.Value, Label = (x.Label ?? string.Empty).Trim(), Detail = x.Detail })
                .ToList();

            if (sortByLabel)
                raw = raw.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Value).ToList();

            var duplicates = new HashSet<string>(raw.Where(x => x.Label.Length > 0)
                .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

            var options = new List<Dictionary<string, object>>(raw.Count + 1)
            {
                new Dictionary<string, object> { ["value"] = 0, ["label"] = string.Empty },
            };

            foreach (var x in raw)
            {
                if (x.Value == 0) continue; // 0 is the empty choice
                var label = x.Label.Length == 0
                    ? "#" + x.Value.ToString(CultureInfo.InvariantCulture)
                    : duplicates.Contains(x.Label) ? x.Value.ToString(CultureInfo.InvariantCulture) + " — " + x.Label : x.Label;

                var option = new Dictionary<string, object> { ["value"] = x.Value, ["label"] = label };
                if (!string.IsNullOrEmpty(x.Detail)) option["detail"] = x.Detail;
                options.Add(option);
            }

            return options;
        }

        internal static string MapLabel(int index, string fileName, string title)
        {
            var file = (fileName ?? string.Empty).Trim();
            var t = (title ?? string.Empty).Trim();
            if (t.Length == 0) return file;
            if (file.Length == 0) return t;
            return file + " — " + t;
        }

        internal static string ItemDetail(ItemInfo i)
        {
            var text = i.Type.ToString();
            if (i.RequiredClass != RequiredClass.None) text += " · " + i.RequiredClass;
            if (i.Price > 0) text += " · " + i.Price.ToString(CultureInfo.InvariantCulture) + " gold";
            if (i.StackSize > 1) text += " · stack " + i.StackSize.ToString(CultureInfo.InvariantCulture);
            return text;
        }

        internal static string MonsterDetail(int level, int ai, uint experience, bool boss)
        {
            var text = "Lv " + level.ToString(CultureInfo.InvariantCulture) + " · AI " + ai.ToString(CultureInfo.InvariantCulture);
            if (experience > 0) text += " · " + experience.ToString(CultureInfo.InvariantCulture) + " exp";
            return boss ? text + " · boss" : text;
        }
    }
}
