using System.Text.Json;

namespace Server.MirEnvir
{
    /// <summary>
    /// Which monsters fill each dungeon style: <c>Components\DungeonGen\monsters.json</c>, copied to <c>DungeonGen\</c> next to
    /// Server.exe. Each style lists its monsters from easiest to hardest (deeper rooms draw from further up the list) and
    /// names its boss. Names are <see cref="Server.MirDatabase.MonsterInfo"/> names, resolved on the game thread when an
    /// instance spawns; a name the database lacks is skipped. Parsed off the game thread (<see cref="DungeonSystem"/>).
    /// </summary>
    public sealed class DungeonMonsterTable
    {
        public sealed class Style
        {
            public readonly List<string> Monsters = new List<string>();
            public string Boss;
            /// <summary>Walkable cells per monster in a room; fewer means denser rooms.</summary>
            public int CellsPerMonster;
            public int MaxPerRoom;
        }

        private readonly Dictionary<string, Style> styles = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);

        public Style ForStyle(string name) => name != null && styles.TryGetValue(name, out var style) ? style : null;

        public static DungeonMonsterTable Load(string path)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            int cellsPerMonster = Math.Clamp(root.TryGetProperty("cellsPerMonster", out var c) ? c.GetInt32() : 40, 4, 1000);
            int maxPerRoom = Math.Clamp(root.TryGetProperty("maxPerRoom", out var m) ? m.GetInt32() : 12, 1, 100);
            var table = new DungeonMonsterTable();
            foreach (var entry in root.GetProperty("styles").EnumerateObject())
            {
                var style = new Style
                {
                    Boss = entry.Value.TryGetProperty("boss", out var boss) ? boss.GetString() : null,
                    CellsPerMonster = entry.Value.TryGetProperty("cellsPerMonster", out var sc) ? Math.Clamp(sc.GetInt32(), 4, 1000) : cellsPerMonster,
                    MaxPerRoom = entry.Value.TryGetProperty("maxPerRoom", out var sm) ? Math.Clamp(sm.GetInt32(), 1, 100) : maxPerRoom,
                };
                foreach (var name in entry.Value.GetProperty("monsters").EnumerateArray())
                    if (!string.IsNullOrWhiteSpace(name.GetString())) style.Monsters.Add(name.GetString());
                table.styles[entry.Name] = style;
            }
            return table;
        }
    }
}
