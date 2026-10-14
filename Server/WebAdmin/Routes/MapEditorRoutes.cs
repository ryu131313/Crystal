using Server.MirEnvir;
using Server.WebAdmin.GameData;
using Server.WebAdmin.MapArt;
using Server.WebAdmin.MapEditor;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Maps editor (read side; edits go through the Game Data grid / LiveDb routes):
    /// GET /api/admin/mapeditor/maps (editor-copy map list with row counts),
    /// GET .../maps/{index}/overlay (the map row, its safe zones / respawns / movements / mine zones / NPCs as grid rows, inbound movements,
    /// map size and walkable count), GET .../maps/{index}/terrain (walkability PNG, one pixel per cell), GET .../maps/{index}/minimap and
    /// GET .../maps/{index}/tiles/{z}/{tx}/{ty} (map art from <c>[WebAdmin] ClientDataPath</c>, <see cref="MapArtService"/>), and
    /// GET .../maps/{index}/players (players on the map's loaded instances; empty while the world is stopped) and
    /// GET /api/admin/mapeditor/online (players per map index for the list badges; one game-thread count, empty while stopped).
    /// Everything but /players reads the editor copy and the server's map file, so it works while the world is stopped.
    /// </summary>
    public sealed class MapEditorRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/mapeditor/maps", GetMaps);
            router.Get("/api/admin/mapeditor/maps/{index:int}/overlay", GetOverlay);
            router.Get("/api/admin/mapeditor/maps/{index:int}/terrain", GetTerrain);
            router.Get("/api/admin/mapeditor/maps/{index:int}/minimap", GetMiniMap);
            router.Get("/api/admin/mapeditor/maps/{index:int}/tiles/{z:int}/{tx:int}/{ty:int}", GetTile);
            router.Get("/api/admin/mapeditor/maps/{index:int}/players", GetPlayers);
            router.Get("/api/admin/mapeditor/online", GetOnline);
        }

        private static T WithEdit<T>(Func<GameDataContext, T> fn)
        {
            lock (WebAdminGame.EditLock)
            {
                return fn(GameDataContext.FromEnvir(Envir.Edit));
            }
        }

        private static object Art()
        {
            var (minimap, tiles) = MapArtService.Status();
            return new
            {
                minimap,
                tiles,
                tileCells = MapArtService.TileCellsAtZ0,
                maxZoom = MapArtService.MaxZoom,
                tilePx = new[] { MapArtService.TilePxW, MapArtService.TilePxH },
                cellPx = new[] { MapArtService.CellW, MapArtService.CellH },
            };
        }

        private static Task GetMaps(WebAdminRequest req)
        {
            var maps = WithEdit(MapEditorService.ListMaps);
            return req.Json(new { maps, world = WebAdminGame.WorldState, art = Art() });
        }

        private static Task GetOverlay(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var rows = WithEdit(ctx => MapEditorService.ReadOverlay(ctx, index)) ?? throw new WebAdminException(404, "Map #" + index + " does not exist");

            var walk = MapEditorService.Walkability(rows.FileName);   // file IO outside EditLock

            return req.Json(new
            {
                index,
                fileName = rows.FileName,
                hasMapFile = walk != null,
                width = walk?.Width ?? 0,
                height = walk?.Height ?? 0,
                walkableCells = walk?.WalkableCells ?? 0,
                map = rows.Map,
                safeZones = rows.SafeZones,
                respawns = rows.Respawns,
                movements = rows.Movements,
                mineZones = rows.MineZones,
                npcs = rows.Npcs,
                inbound = rows.Inbound,
                world = WebAdminGame.WorldState,
                art = Art(),
            });
        }

        private static string FileNameOf(int index) =>
            WithEdit(ctx => ctx.FindMap(index)?.FileName) ?? throw new WebAdminException(404, "Map #" + index + " does not exist");

        private static Task GetTerrain(WebAdminRequest req)
        {
            var walk = MapEditorService.Walkability(FileNameOf(req.RouteInt("index")))
                       ?? throw new WebAdminException(404, "The map file is missing or unreadable");
            // The URL carries no version: let the browser revalidate (the page adds a cache-buster per overlay load).
            req.SetHeader("Cache-Control", "private, max-age=60");
            return req.Bytes(walk.Png, "image/png");
        }

        private static Task GetMiniMap(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            if (!MapArtService.Status().minimap) throw new WebAdminException(404, "Map art is not configured ([WebAdmin] ClientDataPath)");
            var mini = WithEdit(ctx => ctx.FindMap(index)?.MiniMap) ?? throw new WebAdminException(404, "Map #" + index + " does not exist");
            if (mini <= 0) throw new WebAdminException(404, "Map #" + index + " has no minimap");

            var png = MapArtService.MiniMapPng(mini) ?? throw new WebAdminException(404, "Minimap " + mini + " is not in MMap.Lib");
            req.SetHeader("Cache-Control", "private, max-age=300");
            return req.Bytes(png, "image/png");
        }

        private static async Task GetTile(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            if (!MapArtService.Status().tiles) throw new WebAdminException(404, "Map art is not configured ([WebAdmin] ClientDataPath)");

            var fileName = FileNameOf(index);
            var walk = MapEditorService.Walkability(fileName) ?? throw new WebAdminException(404, "The map file is missing or unreadable");
            var info = new MapArtMapInfo { Index = index, FileName = fileName, Width = walk.Width, Height = walk.Height };

            var png = await MapArtService.TilePngAsync(info, req.RouteInt("z"), req.RouteInt("tx"), req.RouteInt("ty")).ConfigureAwait(false);
            req.SetHeader("Cache-Control", "private, max-age=3600");
            await req.Bytes(png, "image/png").ConfigureAwait(false);
        }

        private static async Task GetOnline(WebAdminRequest req)
        {
            Dictionary<int, int> counts = null;
            if (WebAdminGame.IsRunning)
            {
                try
                {
                    counts = await WebAdminGame.RunAsync(MapEditorService.PlayerCounts, 3000).ConfigureAwait(false);
                }
                catch (WorldNotRunningException)
                {
                    counts = null;
                }
            }

            await req.Json(new { world = WebAdminGame.WorldState, counts = counts ?? new Dictionary<int, int>() }).ConfigureAwait(false);
        }

        private static async Task GetPlayers(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var world = WebAdminGame.WorldState;
            if (!WebAdminGame.IsRunning)
            {
                await req.Json(new { world, players = Array.Empty<object>() }).ConfigureAwait(false);
                return;
            }

            List<object> players;
            try
            {
                players = await WebAdminGame.RunAsync(() => MapEditorService.PlayersOn(index), 3000).ConfigureAwait(false);
            }
            catch (WorldNotRunningException)
            {
                players = new List<object>();
            }

            await req.Json(new { world = WebAdminGame.WorldState, players }).ConfigureAwait(false);
        }
    }
}
