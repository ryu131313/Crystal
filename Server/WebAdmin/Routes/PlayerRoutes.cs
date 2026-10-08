using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// GET /api/admin/players-online — snapshot of Envir.Main.Players taken on the game thread.
    /// While the world is not running the list is simply empty (200 with <c>world</c>), so the polling UI does not error.
    /// </summary>
    public sealed class PlayerRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/players-online", GetPlayersOnline);
        }

        private static async Task GetPlayersOnline(WebAdminRequest req)
        {
            var world = WebAdminGame.WorldState;

            if (!WebAdminGame.IsRunning)
            {
                await req.Json(new { rows = Array.Empty<OnlinePlayerRow>(), total = 0, world, serverTimeUtc = DateTime.UtcNow });
                return;
            }

            List<OnlinePlayerRow> rows;
            try
            {
                rows = await WebAdminGame.RunAsync(() =>
                {
                    var players = Envir.Main.Players;
                    var list = new List<OnlinePlayerRow>(players.Count);

                    for (var i = 0; i < players.Count; i++)
                    {
                        var p = players[i];
                        if (p?.Info == null) continue;
                        list.Add(CharacterProjection.OnlineRow(p));
                    }

                    return list;
                });
            }
            catch (WorldNotRunningException)
            {
                rows = new List<OnlinePlayerRow>();
                world = WebAdminGame.WorldState;
            }

            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            await req.Json(new { rows, total = rows.Count, world, serverTimeUtc = DateTime.UtcNow });
        }
    }
}
