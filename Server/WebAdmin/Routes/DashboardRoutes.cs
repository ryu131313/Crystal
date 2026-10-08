namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Dashboard data: GET /api/admin/status/history (sampler ring, downsampled), GET /api/admin/host (process metrics),
    /// GET /api/admin/events (activity feed). Register() subscribes the activity hooks and starts the sampler (both idempotent; the
    /// sampler does nothing until the host is listening, so route discovery in tests never touches Envir/Settings).
    /// </summary>
    public sealed class DashboardRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            WebAdminActivity.EnsureHooks();
            WebAdminStatusSampler.EnsureStarted();

            router.Get("/api/admin/status/history", GetHistory);
            router.Get("/api/admin/host", GetHost);
            router.Get("/api/admin/events", GetEvents);
        }

        private static Task GetHistory(WebAdminRequest req)
        {
            var minutes = WebAdminStatusSampler.ClampMinutes(req.QueryInt("minutes", 60));
            var samples = WebAdminStatusSampler.History(minutes, DateTime.UtcNow);

            return req.Json(new
            {
                minutes,
                intervalSec = WebAdminStatusSampler.IntervalSeconds,
                samples = samples.Select(s => new
                {
                    utc = s.Utc,
                    players = s.Players,
                    connections = s.Connections,
                    monsters = s.Monsters,
                    cycleDelayMs = s.CycleDelayMs,
                    workingSetMb = s.WorkingSetMb,
                    cpuPercent = s.CpuPercent,
                }).ToList(),
            });
        }

        private static Task GetHost(WebAdminRequest req)
        {
            var h = WebAdminHostMetrics.Read();

            return req.Json(new
            {
                cpuPercent = h.CpuPercent,
                workingSetMb = h.WorkingSetMb,
                peakWorkingSetMb = h.PeakWorkingSetMb,
                privateMb = h.PrivateMb,
                gcHeapMb = h.GcHeapMb,
                gcTotalMemoryMb = h.GcTotalMemoryMb,
                gen0 = h.Gen0,
                gen1 = h.Gen1,
                gen2 = h.Gen2,
                threads = h.Threads,
                handles = h.Handles,
                processorCount = h.ProcessorCount,
                diskRoot = h.DiskRoot,
                diskFreeMb = h.DiskFreeMb,
                diskTotalMb = h.DiskTotalMb,
                dotnetVersion = h.DotnetVersion,
                os = h.Os,
                architecture = h.Architecture,
                machineName = h.MachineName,
                processStartUtc = h.ProcessStartUtc == DateTime.MinValue ? (DateTime?)null : h.ProcessStartUtc,
                processUptimeSec = h.ProcessUptimeSec,
                worldUptimeMs = WebAdminGame.WorldUptimeMs,
                sampledUtc = h.SampledUtc,
            });
        }

        private static Task GetEvents(WebAdminRequest req)
        {
            var after = req.QueryLong("after", 0);
            var max = Math.Clamp(req.QueryInt("max", 200), 1, 500);
            var (events, last) = WebAdminActivity.Get(after, max);

            return req.Json(new
            {
                events = events.Select(e => new
                {
                    seq = e.Seq,
                    utc = e.Utc,
                    kind = e.Kind,
                    text = e.Text,
                    character = e.Character,
                    account = e.Account,
                }).ToList(),
                last,
            });
        }
    }
}
