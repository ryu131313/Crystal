using Server.WebAdmin.GameData;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// GET /api/admin/lookups/{name} → {options:[{value, label, detail?}]} for items, monsters, maps, npcs, quests, magics, conquests,
    /// minesets. Read from the Edit envir (the copy the game-data grids edit) under <see cref="WebAdminGame.EditLock"/>.
    /// ?q= filters (label / detail / "#index" contains) and ?limit= caps the list for pickers that do not want every row.
    /// </summary>
    public sealed class LookupRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/lookups/{name}", GetLookup);
        }

        private static Task GetLookup(WebAdminRequest req)
        {
            var name = (req.Route["name"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!GameDataLookups.IsKnown(name))
                return req.Error(404, "Unknown lookup '" + name + "' (known: " + string.Join(", ", GameDataLookups.Names) + ")");

            var options = GameDataRoutes.WithEdit(ctx => GameDataLookups.Finish(GameDataLookups.Raw(ctx, name)));

            var q = (req.Query["q"] ?? string.Empty).Trim();
            if (q.Length > 0)
            {
                options = options.Where((o, i) => i == 0 || Matches(o, q)).ToList();
            }

            var limit = req.QueryInt("limit", 0);
            if (limit > 0 && options.Count > limit + 1) options = options.Take(limit + 1).ToList();

            return req.Json(new { name, options });
        }

        private static bool Matches(Dictionary<string, object> option, string q)
        {
            var index = option.TryGetValue("value", out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            if (q.TrimStart('#') == index) return true;
            if (option.TryGetValue("label", out var l) && l is string label && label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return option.TryGetValue("detail", out var d) && d is string detail && detail.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
