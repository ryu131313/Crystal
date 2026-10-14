using System.Globalization;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.WebAdmin.GameData;
using Server.WebAdmin.MapArt;

namespace Server.WebAdmin.MapEditor
{
    /// <summary>One map file's walkability, one byte per cell (1 = walkable), row-major (y, then x).</summary>
    internal sealed class MapWalkability
    {
        public int Width, Height;
        public byte[] Walk;
        public int WalkableCells;
        public byte[] Png;
    }

    /// <summary>
    /// The web Maps editor (admin section "mapeditor"): a map list, one overlay per map and the map's walkability, all read from
    /// the editor copy (<c>Envir.Edit</c>, under <see cref="WebAdminGame.EditLock"/>) and the server's own map file, so the editor works
    /// while the world is stopped. Overlay rows are the Game Data grid rows (same keys, wire values and <c>__rev</c>), so the page saves
    /// through the existing grid / LiveDb pipeline and never through a route of its own.
    ///
    /// Threads: HTTP threads only. Walkability is parsed with the client's map reader (<see cref="ClientMapData"/>, cached with the
    /// tiles) and the client's rule (<c>GameScene</c> <c>EmptyCell</c>: blocked when <c>BackImage &amp; 0x20000000</c> or
    /// <c>FrontImage &amp; 0x8000</c>), plus the server's extra wall of the old-school format (middle tile <c>0x8000</c>,
    /// <c>Map.LoadMapCellsv0</c>), which matches the server's cell walls without loading a Map.
    /// </summary>
    internal static class MapEditorService
    {
        private const int MaxWalkCache = 8;

        private static readonly object WalkGate = new object();
        private static readonly LinkedList<(string key, MapWalkability walk)> WalkCache = new LinkedList<(string, MapWalkability)>();

        // Palette of the walkability PNG: blocked, walkable.
        private static readonly byte[] WalkPalette =
        {
            10, 15, 30,
            38, 66, 95,
        };

        // ---- map list ----------------------------------------------------------------------------------------------------

        /// <summary>Under EditLock: every map of the editor copy with its row counts.</summary>
        internal static List<object> ListMaps(GameDataContext ctx)
        {
            var npcs = new Dictionary<int, int>();
            foreach (var n in ctx.Npcs)
                npcs[n.MapIndex] = npcs.TryGetValue(n.MapIndex, out var c) ? c + 1 : 1;

            var inbound = new Dictionary<int, int>();
            foreach (var m in ctx.Maps)
                foreach (var mv in m.Movements)
                    if (mv.MapIndex != m.Index) inbound[mv.MapIndex] = inbound.TryGetValue(mv.MapIndex, out var c) ? c + 1 : 1;

            var list = new List<object>(ctx.Maps.Count);
            foreach (var m in ctx.Maps.OrderBy(m => m.Index))
            {
                list.Add(new
                {
                    index = m.Index,
                    fileName = m.FileName,
                    title = m.Title,
                    miniMap = (int)m.MiniMap,
                    safeZones = m.SafeZones.Count,
                    respawns = m.Respawns.Count,
                    movements = m.Movements.Count,
                    inbound = inbound.TryGetValue(m.Index, out var ib) ? ib : 0,
                    mineZones = m.MineZones.Count,
                    npcs = npcs.TryGetValue(m.Index, out var nc) ? nc : 0,
                });
            }
            return list;
        }

        // ---- overlay -----------------------------------------------------------------------------------------------------

        /// <summary>What the overlay needs from the editor copy, read under EditLock (no file IO).</summary>
        internal sealed class OverlayRows
        {
            public Dictionary<string, object> Map;
            public string FileName;
            public List<Dictionary<string, object>> SafeZones, Respawns, Movements, MineZones, Npcs;
            public List<object> Inbound;
        }

        /// <summary>Under EditLock: the map's row and its child rows as the Game Data grids return them, or null for an unknown map.</summary>
        internal static OverlayRows ReadOverlay(GameDataContext ctx, int index)
        {
            var map = ctx.FindMap(index);
            if (map == null) return null;

            var mapTable = GameDataRegistry.Get("maps");
            var mapRow = mapTable.Rows(ctx).FirstOrDefault(r => r.Index == index);

            var rows = new OverlayRows
            {
                Map = mapRow != null ? mapTable.ToWire(mapRow) : null,
                FileName = map.FileName,
                SafeZones = ChildRows(ctx, "mapsafezones", map),
                Respawns = ChildRows(ctx, "maprespawns", map),
                Movements = ChildRows(ctx, "mapmovements", map),
                MineZones = ChildRows(ctx, "mapminezones", map),
                Npcs = new List<Dictionary<string, object>>(),
                Inbound = new List<object>(),
            };

            // Display names the page shows next to the indexes (extra "__" keys: never sent back by the grid / batch code).
            foreach (var r in rows.Respawns)
            {
                var mi = ToInt(r, "MonsterIndex");
                var mon = ctx.Monsters.FirstOrDefault(x => x.Index == mi);
                r["__monster"] = mon?.Name;
                r["__boss"] = mon != null && mon.IsBoss;
            }

            foreach (var r in rows.Movements)
            {
                var dest = ctx.FindMap(ToInt(r, "DestinationMapIndex"));
                r["__destTitle"] = dest == null ? null : MapLabel(dest);
            }

            var npcTable = GameDataRegistry.Get("npcs");
            foreach (var row in npcTable.Rows(ctx))
            {
                if (row.Entity is NPCInfo n && n.MapIndex == index) rows.Npcs.Add(npcTable.ToWire(row));
            }

            foreach (var other in ctx.Maps)
            {
                if (other.Index == index) continue;
                for (var i = 0; i < other.Movements.Count; i++)
                {
                    var mv = other.Movements[i];
                    if (mv.MapIndex != index) continue;
                    rows.Inbound.Add(new
                    {
                        rowIndex = other.Index * GameDataRegistry.Stride + i,
                        fromMapIndex = other.Index,
                        fromTitle = MapLabel(other),
                        sourceX = mv.Source.X,
                        sourceY = mv.Source.Y,
                        destX = mv.Destination.X,
                        destY = mv.Destination.Y,
                    });
                }
            }

            return rows;
        }

        private static List<Dictionary<string, object>> ChildRows(GameDataContext ctx, string endpoint, MapInfo map)
        {
            var table = GameDataRegistry.Get(endpoint);
            var list = new List<Dictionary<string, object>>();
            foreach (var row in table.Rows(ctx))
                if (ReferenceEquals(row.Parent, map)) list.Add(table.ToWire(row));
            return list;
        }

        private static int ToInt(Dictionary<string, object> row, string key)
        {
            if (!row.TryGetValue(key, out var v) || v == null) return -1;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        }

        internal static string MapLabel(MapInfo m) => string.IsNullOrWhiteSpace(m.Title) ? m.FileName : m.Title;

        // ---- walkability -------------------------------------------------------------------------------------------------

        /// <summary>HTTP thread. The walkability of a map file (cached per file and write stamp), or null when the file is missing.</summary>
        internal static MapWalkability Walkability(string fileName)
        {
            var (data, file, stamp) = MapArtService.MapFile(fileName);
            if (data == null || data.Width <= 0 || data.Height <= 0) return null;

            var key = file + "|" + stamp.Ticks;
            lock (WalkGate)
            {
                for (var n = WalkCache.First; n != null; n = n.Next)
                {
                    if (n.Value.key != key) continue;
                    WalkCache.Remove(n);
                    WalkCache.AddFirst(n);
                    return n.Value.walk;
                }
            }

            var walk = Build(data);
            lock (WalkGate)
            {
                WalkCache.AddFirst((key, walk));
                while (WalkCache.Count > MaxWalkCache) WalkCache.RemoveLast();
            }
            return walk;
        }

        /// <summary>Pure: walkability and its PNG (one palette pixel per cell) from a parsed map.</summary>
        internal static MapWalkability Build(ClientMapData data)
        {
            int w = data.Width, h = data.Height;
            var walk = new byte[w * h];
            var count = 0;
            var cells = data.MapCells;
            for (var x = 0; x < w; x++)
            {
                for (var y = 0; y < h; y++)
                {
                    ref var c = ref cells[x, y];
                    if ((c.BackImage & 0x20000000) != 0 || (c.FrontImage & 0x8000) != 0) continue;
                    if (data.OldSchool && (c.MiddleImage & 0x8000) != 0) continue;   // server LowWall (the client ignores it)
                    walk[y * w + x] = 1;
                    count++;
                }
            }

            return new MapWalkability
            {
                Width = w,
                Height = h,
                Walk = walk,
                WalkableCells = count,
                Png = WebAdminMonitor.EncodePalettePng(w, h, walk, WalkPalette),
            };
        }

        // ---- live players ------------------------------------------------------------------------------------------------

        /// <summary>Game thread. Online players per map index (every loaded instance together; maps without players are left out).</summary>
        internal static Dictionary<int, int> PlayerCounts()
        {
            var counts = new Dictionary<int, int>();
            var maps = Envir.Main.MapList;
            for (var i = 0; i < maps.Count; i++)
            {
                var m = maps[i];
                var n = m?.Players.Count ?? 0;
                if (n <= 0 || m.Info == null) continue;
                counts[m.Info.Index] = counts.TryGetValue(m.Info.Index, out var c) ? c + n : n;
            }
            return counts;
        }

        /// <summary>Game thread. The players on every loaded instance of a map index (snapshot DTOs).</summary>
        internal static List<object> PlayersOn(int index)
        {
            var list = new List<object>();
            var maps = Envir.Main.MapList;
            for (var i = 0; i < maps.Count; i++)
            {
                var m = maps[i];
                if (m?.Info == null || m.Info.Index != index) continue;
                var players = m.Players;
                for (var p = 0; p < players.Count; p++)
                {
                    var pl = players[p];
                    if (pl == null) continue;
                    list.Add(new
                    {
                        name = pl.Name,
                        level = (int)pl.Level,
                        x = pl.CurrentLocation.X,
                        y = pl.CurrentLocation.Y,
                        dead = pl.Dead,
                        hpPercent = pl.Stats[Stat.HP] > 0 ? (int)Math.Round(100.0 * pl.HP / pl.Stats[Stat.HP]) : 0,
                    });
                }
            }
            return list;
        }
    }
}
