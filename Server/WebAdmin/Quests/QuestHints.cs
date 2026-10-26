using Server.MirDatabase;
using Server.MirEnvir.Quests;
using Server.MirEnvir.Storage;

namespace Server.WebAdmin.Quests
{
    /// <summary>
    /// Read-only hint data for the Quests editor's pickers: what spawns where, which monsters drop an item, where an NPC stands, and a
    /// map's NPCs and respawns. Threads: HTTP workers only; the world parts are read under <see cref="WebAdminGame.EditLock"/> through
    /// <see cref="QuestEditorGate"/>, and the drop tables are read without that lock (as DropRoutes does).
    /// </summary>
    internal static class QuestHints
    {
        public sealed class SpawnHint
        {
            public int map;
            public string mapTitle;
            public int respawns;
            public int count;
        }

        public sealed class DropHint
        {
            public int monster;
            public string name;
            public int chance;
            public bool grouped;
            public bool quest;
        }

        public sealed class ItemHint
        {
            public string kind = "item";
            public int id;
            public string name;
            public uint price;
            public int image;
            public string requiredClass;
            public List<DropHint> droppedBy = new List<DropHint>();
            public bool dropsKnown;
        }

        public sealed class MapHint
        {
            public string kind = "map";
            public int id;
            public string title;
            public string fileName;
            public int miniMap;
            public int width;
            public int height;
            public List<MapNpc> npcs = new List<MapNpc>();
            public List<MapRespawn> respawns = new List<MapRespawn>();
        }

        public sealed class MapNpc
        {
            public int index;
            public string name;
            public int x;
            public int y;
        }

        public sealed class MapRespawn
        {
            public int monster;
            public string name;
            public int level;
            public bool boss;
            public int x;
            public int y;
            public int spread;
            public int count;
        }

        private static string MapTitle(MapInfo m) => string.IsNullOrWhiteSpace(m.Title) ? m.FileName : m.Title;

        public static object Monster(QuestWorld w, int id)
        {
            var m = w.MonsterInfoList.FirstOrDefault(x => x != null && x.Index == id);
            if (m == null) return null;
            var spawns = new List<SpawnHint>();
            foreach (var map in w.MapInfoList)
            {
                if (map == null) continue;
                var rows = map.Respawns.Where(r => r != null && r.MonsterIndex == id).ToList();
                if (rows.Count == 0) continue;
                spawns.Add(new SpawnHint { map = map.Index, mapTitle = MapTitle(map), respawns = rows.Count, count = rows.Sum(r => (int)r.Count) });
            }
            return new
            {
                kind = "monster", id, name = m.Name, level = (int)m.Level, experience = (long)m.Experience, boss = m.IsBoss,
                spawns = spawns.OrderByDescending(s => s.count).Take(10).ToList(),
            };
        }

        public static object Npc(QuestWorld w, int id)
        {
            var n = w.NPCInfoList.FirstOrDefault(x => x != null && x.Index == id);
            if (n == null) return null;
            var map = w.MapInfoList.FirstOrDefault(m => m != null && m.Index == n.MapIndex);
            return new
            {
                kind = "npc", id, name = n.Name, map = n.MapIndex,
                mapTitle = map == null ? null : MapTitle(map), x = n.Location.X, y = n.Location.Y,
            };
        }

        public static ItemHint Item(QuestWorld w, int id)
        {
            var i = w.ItemInfoList.FirstOrDefault(x => x != null && x.Index == id);
            if (i == null) return null;
            return new ItemHint
            {
                id = id, name = i.Name, price = i.Price, image = i.Image, requiredClass = i.RequiredClass.ToString(),
            };
        }

        public static MapHint Map(QuestWorld w, int id)
        {
            var map = w.MapInfoList.FirstOrDefault(x => x != null && x.Index == id);
            if (map == null) return null;
            var hint = new MapHint { id = id, title = MapTitle(map), fileName = map.FileName, miniMap = map.MiniMap };
            foreach (var n in w.NPCInfoList)
                if (n != null && n.MapIndex == id) hint.npcs.Add(new MapNpc { index = n.Index, name = n.Name, x = n.Location.X, y = n.Location.Y });
            foreach (var r in map.Respawns)
            {
                if (r == null) continue;
                var m = w.MonsterInfoList.FirstOrDefault(x => x != null && x.Index == r.MonsterIndex);
                hint.respawns.Add(new MapRespawn
                {
                    monster = r.MonsterIndex, name = m?.Name ?? ("#" + r.MonsterIndex), level = m == null ? 0 : (int)m.Level,
                    boss = m != null && m.IsBoss, x = r.Location.X, y = r.Location.Y, spread = (int)r.Spread, count = (int)r.Count,
                });
            }
            return hint;
        }

        // Which monsters drop an item: each monster's own list plus the lists it includes (followed once each, cycles ignored). The best
        // (lowest N) chance per monster wins.
        public static List<DropHint> DroppedBy(long itemIndex, IEnumerable<(int Index, string Name, string DropPath)> monsters,
            Func<int, string, string, DropList> listOf, Func<long, DropList> byId)
        {
            var best = new Dictionary<int, DropHint>();
            foreach (var (index, name, dropPath) in monsters)
            {
                var root = listOf(index, name, dropPath);
                if (root == null) continue;
                var seen = new HashSet<long>();
                var stack = new Stack<DropList>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var l = stack.Pop();
                    if (l == null || !seen.Add(l.Id)) continue;
                    foreach (var r in l.Rows)
                    {
                        if (r == null) continue;
                        if (r.Kind == DropKind.Include && r.IncludeListId is long inc) stack.Push(byId(inc));
                        else if (r.Kind == DropKind.Item && r.ItemIndex == itemIndex && r.Chance > 0
                                 && (!best.TryGetValue(index, out var h) || r.Chance < h.chance))
                            best[index] = new DropHint { monster = index, name = name, chance = r.Chance, grouped = r.Parent >= 0, quest = r.Quest };
                    }
                }
            }
            return best.Values.OrderBy(h => h.chance).ThenBy(h => h.name, StringComparer.OrdinalIgnoreCase).Take(15).ToList();
        }
    }
}
