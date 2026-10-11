using Server.WebAdmin.MapArt;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Live Monitor: GET /api/admin/monitor/snapshot (one game-thread snapshot per poll), GET /api/admin/monitor/heat (sampler-accumulated
    /// player density), GET /api/admin/monitor/maps/{index}/image (walkability PNG, cached per world run), and the map art read from
    /// <c>[WebAdmin] ClientDataPath</c>: GET .../maps/{index}/minimap (the client's MMap.Lib frame) and
    /// GET .../maps/{index}/tiles/{z}/{tx}/{ty} (768 x 512 real-tile PNGs, <see cref="MapArtService"/>). Read-only: GM actions from the
    /// page go through POST /api/admin/gm/action. Register() subscribes the monitor hooks once (idempotent).
    /// </summary>
    public sealed class MonitorRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            WebAdminMonitor.EnsureHooked();
            MapArtService.EnsureHooked();

            router.Get("/api/admin/monitor/snapshot", GetSnapshot);
            router.Get("/api/admin/monitor/heat", GetHeat);
            router.Get("/api/admin/monitor/maps/{index:int}/image", GetMapImage);
            router.Get("/api/admin/monitor/maps/{index:int}/minimap", GetMiniMap);
            router.Get("/api/admin/monitor/maps/{index:int}/tiles/{z:int}/{tx:int}/{ty:int}", GetTile);
        }

        /// <summary>?map=&lt;index&gt;:&lt;instance&gt; (focus map), ?mobs=off|all|bosses, ?evSince=&lt;seq&gt; (omit on the first poll).</summary>
        private static async Task GetSnapshot(WebAdminRequest req)
        {
            var world = WebAdminGame.WorldState;
            var focus = WebAdminMonitor.NormaliseKey(req.Query["map"]);
            var mobs = (req.Query["mobs"] ?? "off").Trim().ToLowerInvariant();
            if (mobs != "all" && mobs != "bosses") mobs = "off";
            var evSince = req.Query.Has("evSince") ? Math.Max(0, req.QueryLong("evSince", 0)) : -1;

            if (!WebAdminGame.IsRunning)
            {
                await req.Json(Stopped(world)).ConfigureAwait(false);
                return;
            }

            MonitorSnapshot snap;
            try
            {
                snap = await WebAdminGame.RunAsync(() => WebAdminMonitor.BuildSnapshot(focus, mobs, evSince), 3000).ConfigureAwait(false);
            }
            catch (WorldNotRunningException)
            {
                await req.Json(Stopped(WebAdminGame.WorldState)).ConfigureAwait(false);
                return;
            }

            snap.Maps.Sort((a, b) =>
            {
                var c = b.Players.Count.CompareTo(a.Players.Count);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            for (var i = 0; i < snap.Maps.Count; i++)
                snap.Maps[i].Players.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            await req.Json(new
            {
                world,
                serverTimeUtc = DateTime.UtcNow,
                intervalSec = WebAdminStatusSampler.IntervalSeconds,
                totalPlayers = snap.TotalPlayers,
                deaths10m = snap.Deaths10m,
                latestSeq = snap.LatestSeq,
                maps = snap.Maps,
                mobs = snap.Mobs,
                events = snap.Events,
                maxMobRows = WebAdminMonitor.MaxMobRows,
                art = Art(),
            }).ConfigureAwait(false);
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
            };
        }

        private static object Stopped(string world) => new
        {
            world,
            serverTimeUtc = DateTime.UtcNow,
            intervalSec = WebAdminStatusSampler.IntervalSeconds,
            totalPlayers = 0,
            deaths10m = 0,
            latestSeq = 0,
            maps = Array.Empty<MonitorMap>(),
            mobs = (MonitorMobs)null,
            events = Array.Empty<MonitorEvent>(),
            maxMobRows = WebAdminMonitor.MaxMobRows,
            art = Art(),
        };

        /// <summary>?map=&lt;MapInfo.Index&gt;&amp;minutes=1..15. Works while the world is stopped (the buckets outlive a restart).</summary>
        private static Task GetHeat(WebAdminRequest req)
        {
            if (!req.Query.Has("map")) throw new WebAdminException(400, "map is required");
            var map = req.QueryInt("map", -1);
            if (map < 0) throw new WebAdminException(400, "map must be a map index");
            var minutes = Math.Clamp(req.QueryInt("minutes", 5), 1, WebAdminMonitor.HeatMinutes);

            var (cells, max, samples) = WebAdminMonitor.GetHeat(map, minutes, DateTime.UtcNow);

            return req.Json(new
            {
                map,
                minutes,
                grid = WebAdminMonitor.HeatGrid,
                sampleSec = WebAdminStatusSampler.IntervalSeconds,
                max,
                samples,
                cells,
            });
        }

        private static async Task GetMapImage(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var png = WebAdminMonitor.CachedImage(index);

            if (png == null)
            {
                if (!WebAdminGame.IsRunning) throw new WorldNotRunningException(WebAdminGame.WorldState);

                var cells = await WebAdminGame.RunAsync(() => WebAdminMonitor.CopyCells(index), 5000).ConfigureAwait(false);
                if (cells == null) throw new WebAdminException(404, "Map #" + index + " is not loaded");

                png = WebAdminMonitor.EncodePng(cells);   // HTTP thread
                WebAdminMonitor.CacheImage(index, png);
            }

            // The cells only change when the world restarts (the cache is cleared then); let the page reuse it for a while.
            req.SetHeader("Cache-Control", "private, max-age=300");
            await req.Bytes(png, "image/png").ConfigureAwait(false);
        }

        private static async Task GetMiniMap(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            if (!MapArtService.Status().minimap) throw new WebAdminException(404, "Map art is not configured ([WebAdmin] ClientDataPath)");

            var info = await MapArtService.GetMapInfoAsync(index).ConfigureAwait(false);
            if (info.MiniMap <= 0) throw new WebAdminException(404, "Map #" + index + " has no minimap");

            var png = MapArtService.MiniMapPng(info.MiniMap) ?? throw new WebAdminException(404, "Minimap " + info.MiniMap + " is not in MMap.Lib");
            req.SetHeader("Cache-Control", "private, max-age=3600");
            await req.Bytes(png, "image/png").ConfigureAwait(false);
        }

        private static async Task GetTile(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            if (!MapArtService.Status().tiles) throw new WebAdminException(404, "Map art is not configured ([WebAdmin] ClientDataPath)");

            var info = await MapArtService.GetMapInfoAsync(index).ConfigureAwait(false);
            var png = await MapArtService.TilePngAsync(info, req.RouteInt("z"), req.RouteInt("tx"), req.RouteInt("ty")).ConfigureAwait(false);
            req.SetHeader("Cache-Control", "private, max-age=3600");
            await req.Bytes(png, "image/png").ConfigureAwait(false);
        }
    }
}
