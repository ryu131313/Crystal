using System.Text.Json;
using Server.WebAdmin.Tools;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Tools page (Monitoring group): the stress bot fleet and the scratch server it can play on.
    ///   GET  /api/admin/tools/status
    ///   POST /api/admin/tools/bots/start   {target: scratch|self, bots, ramp, durationMinutes, prefix, firstIndex, password, class,
    ///                                       hunt, loot, town, quests, potions, equip, chat, party, director, run, reconnect,
    ///                                       runPercent, seekRange, roamRadius, workers, connectBudget}
    ///   POST /api/admin/tools/bots/stop
    ///   POST /api/admin/tools/bots/build
    ///   POST /api/admin/tools/scratch/start {port, maps, reset, rebuild, copyLiveMaps}
    ///   POST /api/admin/tools/scratch/stop
    /// No game-thread work: <see cref="WebAdminTools"/> only manages child processes.
    /// </summary>
    public sealed class ToolsRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            WebAdminTools.Hook();

            router.Get("/api/admin/tools/status", GetStatus);
            router.Post("/api/admin/tools/bots/start", StartBots);
            router.Post("/api/admin/tools/bots/stop", StopBots);
            router.Post("/api/admin/tools/bots/build", BuildBots);
            router.Post("/api/admin/tools/scratch/start", StartScratch);
            router.Post("/api/admin/tools/scratch/stop", StopScratch);
        }

        private static Task GetStatus(WebAdminRequest req) => req.Json(WebAdminTools.Status());

        private static async Task StartBots(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var config = StressBotConfig.FromJson(body);
            if (config.Target == StressBotConfig.TargetSelf) await WebAdminTools.CheckSelfAccountsAsync(config).ConfigureAwait(false);
            var started = WebAdminTools.StartBots(config, req.AdminName);
            WebAdminAudit.Record(req, "tools.bots.start", config.Target, new { config = config.ToDto(), started });
            await req.Ok(WebAdminTools.Status(), "Started " + config.Bots + " bots.").ConfigureAwait(false);
        }

        private static async Task StopBots(WebAdminRequest req)
        {
            if (!WebAdminTools.StopBots("stopped by " + req.AdminName)) throw new WebAdminException(409, "No bot fleet is running.");
            WebAdminAudit.Record(req, "tools.bots.stop", "bots", null);
            await req.Ok(WebAdminTools.Status(), "Stopping the bots (they disconnect politely).").ConfigureAwait(false);
        }

        private static async Task BuildBots(WebAdminRequest req)
        {
            WebAdminTools.BuildBots(req.AdminName);
            WebAdminAudit.Record(req, "tools.bots.build", "StressClient", null);
            await req.Ok(WebAdminTools.Status(), "Building StressClient…").ConfigureAwait(false);
        }

        private static async Task StartScratch(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");

            int port = body.GetInt("port", 17100);
            int maps = body.GetInt("maps", 1);
            bool reset = body.GetBool("reset", false);
            bool rebuild = body.GetBool("rebuild", false);
            bool copyLiveMaps = body.GetBool("copyLiveMaps", false);

            WebAdminTools.StartScratch(port, maps, reset, rebuild, copyLiveMaps, req.AdminName);
            WebAdminAudit.Record(req, "tools.scratch.start", "scratch", new { port, maps, reset, rebuild, copyLiveMaps });
            await req.Ok(WebAdminTools.Status(), "Starting the scratch server…").ConfigureAwait(false);
        }

        private static async Task StopScratch(WebAdminRequest req)
        {
            WebAdminTools.StopScratch(req.AdminName);
            WebAdminAudit.Record(req, "tools.scratch.stop", "scratch", null);
            await req.Ok(WebAdminTools.Status(), "Stopping the scratch server…").ConfigureAwait(false);
        }
    }
}
