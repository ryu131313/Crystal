using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// GM console: POST /api/admin/gm/action (cross-feature contract), GET /api/admin/gm/caps, /target, /players, /maps, /recent.
    /// </summary>
    public sealed class GmRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Post("/api/admin/gm/action", PostAction);
            router.Get("/api/admin/gm/caps", GetCaps);
            router.Get("/api/admin/gm/target", GetTarget);
            router.Get("/api/admin/gm/players", GetPlayers);
            router.Get("/api/admin/gm/maps", GetMaps);
            router.Get("/api/admin/gm/recent", GetRecent);
        }

        private static async Task PostAction(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var result = await GmActions.ExecuteAsync(req, body).ConfigureAwait(false);
            await req.Ok(result.Data, result.Message).ConfigureAwait(false);
        }

        private static async Task GetCaps(WebAdminRequest req)
        {
            var conquests = new List<GmOption>();

            if (WebAdminGame.IsRunning)
            {
                try
                {
                    conquests = await WebAdminGame.RunAsync(() => Envir.Main.Conquests
                        .Where(c => c?.Info != null)
                        .Select(c => new GmOption { Value = c.Info.Index.ToString(), Label = c.Info.Name + " (#" + c.Info.Index + ")" + (c.WarIsOn ? " — at war" : string.Empty) })
                        .ToList(), 2000).ConfigureAwait(false);
                }
                catch (WebAdminException)
                {
                    // world stopping / busy: the UI falls back to a number input
                }
            }

            await req.Json(new
            {
                verbs = GmCatalog.Verbs,
                groups = GmCatalog.Groups,
                conquests,
                world = WebAdminGame.WorldState,
                limits = new
                {
                    maxCurrency = GmCatalog.MaxCurrency,
                    maxItemCount = GmCatalog.MaxItemCount,
                    maxSpawnCount = GmCatalog.MaxSpawnCount,
                    maxChatBanMinutes = GmCatalog.MaxChatBanMinutes,
                    maxMessageLength = GmCatalog.MaxMessageLength,
                },
            }).ConfigureAwait(false);
        }

        private static async Task GetTarget(WebAdminRequest req)
        {
            var name = (req.Query["name"] ?? req.Query["character"])?.Trim();
            var index = req.QueryInt("characterIndex", 0);
            if (index <= 0 && string.IsNullOrEmpty(name)) throw new WebAdminException(400, "name or characterIndex is required");

            var dto = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var info = GmActions.FindCharacter(name, index);
                if (info == null) return null;

                var player = GmActions.OnlinePlayer(info);
                var account = info.AccountInfo;
                var now = GmActions.GameNow;

                string map, mapFile = null;
                int x, y, mapIndex;
                if (player?.CurrentMap?.Info != null)
                {
                    map = player.CurrentMap.Info.Title;
                    mapFile = player.CurrentMap.Info.FileName;
                    mapIndex = player.CurrentMap.Info.Index;
                    x = player.CurrentLocation.X;
                    y = player.CurrentLocation.Y;
                }
                else
                {
                    mapIndex = info.CurrentMapIndex;
                    map = MapTitle(mapIndex, out mapFile);
                    x = info.CurrentLocation.X;
                    y = info.CurrentLocation.Y;
                }

                var chatBanned = info.ChatBanned && info.ChatBanExpiryDate > now;
                var banned = account != null && account.Banned && account.ExpiryDate > now;

                return new
                {
                    characterIndex = info.Index,
                    name = info.Name,
                    online = player != null,
                    accountIndex = account?.Index ?? 0,
                    accountId = account?.AccountID,
                    admin = account?.AdminAccount ?? false,
                    level = player != null ? player.Level : info.Level,
                    className = info.Class.ToString(),
                    gender = info.Gender.ToString(),
                    map,
                    mapFile,
                    mapIndex,
                    x,
                    y,
                    dead = player?.Dead ?? false,
                    isGM = player?.IsGM ?? false,
                    pk = info.PKPoints,
                    deleted = info.Deleted,
                    chatBanned,
                    chatBanUntil = chatBanned ? info.ChatBanExpiryDate : (DateTime?)null,
                    accountBanned = banned,
                    banReason = banned ? account.BanReason : null,
                    banUntil = banned ? account.ExpiryDate : (DateTime?)null,
                    gold = account?.Gold ?? 0,
                    credit = account?.Credit ?? 0,
                    lastLogin = info.LastLoginDate,
                    pets = player?.Pets.Count ?? 0,
                };
            }).ConfigureAwait(false);

            if (dto == null) throw new WebAdminException(404, "Character not found: " + (string.IsNullOrEmpty(name) ? "#" + index : name));

            await req.Json(dto).ConfigureAwait(false);
        }

        private static string MapTitle(int index, out string fileName)
        {
            fileName = null;

            var info = Envir.Main.MapInfoList.FirstOrDefault(m => m.Index == index);

            // Inline path only (world stopped, HTTP thread): fall back to the Edit database. Never block the game thread on EditLock.
            // This runs under Envir.AccountLock (RunOrInlineAsync's inline path), and the lock order everywhere else is EditLock ->
            // LoadLock -> AccountLock, so never WAIT for EditLock here: if it is busy (a save/reload/restore), show "Map #n".
            if (info == null && !WebAdminGame.IsRunning && Monitor.TryEnter(WebAdminGame.EditLock, 0))
            {
                try
                {
                    info = Envir.Edit.MapInfoList.FirstOrDefault(m => m.Index == index);
                }
                finally
                {
                    Monitor.Exit(WebAdminGame.EditLock);
                }
            }

            if (info == null) return "Map #" + index;
            fileName = info.FileName;
            return string.IsNullOrEmpty(info.Title) ? info.FileName : info.Title;
        }

        private static async Task GetPlayers(WebAdminRequest req)
        {
            if (!WebAdminGame.IsRunning)
            {
                await req.Json(new { players = Array.Empty<object>(), world = WebAdminGame.WorldState }).ConfigureAwait(false);
                return;
            }

            var players = await WebAdminGame.RunAsync(() => Envir.Main.Players.Select(p => new
            {
                name = p.Name,
                characterIndex = p.Info?.Index ?? 0,
                level = p.Level,
                className = p.Info?.Class.ToString(),
                accountId = p.Account?.AccountID,
                map = p.CurrentMap?.Info?.Title,
                x = p.CurrentLocation.X,
                y = p.CurrentLocation.Y,
            }).OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ToList()).ConfigureAwait(false);

            await req.Json(new { players, world = WebAdminGame.WorldState }).ConfigureAwait(false);
        }

        private static async Task GetMaps(WebAdminRequest req)
        {
            if (WebAdminGame.IsRunning)
            {
                var live = await WebAdminGame.RunAsync(() => Envir.Main.MapList
                    .Where(m => m.Info != null)
                    .GroupBy(m => m.Info.Index)
                    .Select(g => new
                    {
                        index = g.Key,
                        fileName = g.First().Info.FileName,
                        title = g.First().Info.Title,
                        width = g.First().Width,
                        height = g.First().Height,
                        instances = g.Count(),
                        players = g.Sum(m => m.Players.Count),
                    })
                    .OrderBy(m => m.index)
                    .ToList()).ConfigureAwait(false);

                await req.Json(new { maps = live, live = true }).ConfigureAwait(false);
                return;
            }

            object maps;
            lock (WebAdminGame.EditLock)
            {
                maps = Envir.Edit.MapInfoList
                    .Select(m => new { index = m.Index, fileName = m.FileName, title = m.Title, width = 0, height = 0, instances = 0, players = 0 })
                    .OrderBy(m => m.index)
                    .ToList();
            }

            await req.Json(new { maps, live = false }).ConfigureAwait(false);
        }

        private static Task GetRecent(WebAdminRequest req)
        {
            var limit = Math.Clamp(req.QueryInt("limit", 40), 1, 500);
            var days = Math.Clamp(req.QueryInt("days", 30), 1, 365);
            var character = req.Query["character"]?.Trim();

            var entries = WebAdminAudit.Query(string.IsNullOrEmpty(character) ? limit : Math.Min(limit * 5, 2000), "gm", null, character, days)
                .Where(e => string.IsNullOrEmpty(character) || (e.Target ?? string.Empty).IndexOf(character, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(limit)
                .Select(e => new
                {
                    id = e.Id,
                    ts = e.Ts,
                    admin = e.Admin,
                    ip = e.Ip,
                    action = e.Action,
                    target = e.Target,
                    reason = e.Reason,
                    details = e.Details,
                })
                .ToList();

            return req.Json(new { entries });
        }
    }
}
