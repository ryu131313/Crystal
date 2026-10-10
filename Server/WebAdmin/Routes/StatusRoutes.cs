using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>GET /api/admin/status — cheap scalar reads only (safe off the game thread, like SMain's UI timer).</summary>
    public sealed class StatusRoutes : IWebAdminRoutes
    {
        private static readonly Lazy<DateTime> ProcessStartUtc = new Lazy<DateTime>(() =>
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    return process.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return DateTime.UtcNow;
            }
        });

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/status", GetStatus);
        }

        private static Task GetStatus(WebAdminRequest req)
        {
            var envir = Envir.Main;
            var world = WebAdminGame.WorldState;
            var running = world == "running";

            int players = 0, connections = 0, monsters = 0, blockedIps = 0;
            var mobThreadDelays = new List<long>();

            try
            {
                players = envir.Players.Count;
                connections = envir.Connections.Count;
                monsters = envir.MonsterCount;

                var now = envir.Now;
                foreach (var pair in Envir.IPBlocks)
                {
                    if (pair.Value > now) blockedIps++;
                }

                var threads = envir.MobThreads;
                if (running && Settings.Multithreaded && threads != null)
                {
                    for (var i = 0; i < threads.Length; i++)
                    {
                        var t = threads[i];
                        if (t != null) mobThreadDelays.Add(t.LastRunTime);
                    }
                }
            }
            catch (Exception)
            {
                // racy reads of live lists (same tolerance as the WinForms status timer)
            }

            var pending = WebAdminPending.Snapshot().Select(p => new
            {
                area = p.Area,
                description = p.Description,
                admin = p.Admin,
                utc = p.Utc,
            }).ToList();

            return req.Json(new
            {
                world,
                uptimeMs = WebAdminGame.WorldUptimeMs,
                processUptimeMs = (long)(DateTime.UtcNow - ProcessStartUtc.Value).TotalMilliseconds,
                players,
                connections,
                monsters,
                blockedIps,
                cycleDelayMs = Envir.LastRunTime,
                mobThreadDelays,
                lastCount = Envir.LastCount,
                lastRealCount = Envir.LastRealCount,
                serverTimeUtc = DateTime.UtcNow,
                systemDataStale = Envir.SystemDataStale,
                pendingReboot = pending,
                sessions = WebAdminSessions.Store.Count,
                // roadmap P2/P5 health counters (appended; fields are never renamed)
                isolatedErrors = WorldErrors.Count,
                recentIsolatedErrors = WorldErrors.Recent(),
                accountSaveFailures = Envir.AccountSaveFailures,
            });
        }
    }
}
