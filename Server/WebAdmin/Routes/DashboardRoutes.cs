namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Dashboard data: GET /api/admin/status/history (sampler ring, downsampled; ?live=1&amp;after=&lt;unix ms&gt; = the 1 s live ring and
    /// renews the live lease), GET /api/admin/host (process metrics + per-thread CPU),
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
            if (req.QueryBool("live"))
            {
                WebAdminStatusSampler.RequestLive();
                var after = req.QueryLong("after", 0);
                DateTime? afterUtc = after > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(after).UtcDateTime : null;
                var live = WebAdminStatusSampler.LiveSince(afterUtc, DateTime.UtcNow);

                return req.Json(new
                {
                    live = true,
                    minutes = WebAdminStatusSampler.LiveRetentionMinutes,
                    intervalSec = WebAdminStatusSampler.LiveIntervalSeconds,
                    phases = StatusSample.PhaseNames,
                    samples = live.Select(Project).ToList(),
                });
            }

            var minutes = WebAdminStatusSampler.ClampMinutes(req.QueryInt("minutes", 60));
            var samples = WebAdminStatusSampler.History(minutes, DateTime.UtcNow);

            return req.Json(new
            {
                minutes,
                intervalSec = WebAdminStatusSampler.IntervalSeconds,
                phases = StatusSample.PhaseNames,
                samples = samples.Select(Project).ToList(),
            });
        }

        private static object Project(StatusSample s)
        {
            return new
            {
                utc = s.Utc,
                players = s.Players,
                connections = s.Connections,
                monsters = s.Monsters,
                cycleDelayMs = s.CycleDelayMs,
                workingSetMb = s.WorkingSetMb,
                cpuPercent = s.CpuPercent,
                gameBusyPercent = s.GameBusyPercent,
                gameCpuPercent = s.GameCpuPercent,
                otherCpuPercent = s.OtherCpuPercent,
                maxIterationMs = s.MaxIterationMs,
                phaseMs = s.PhaseMs,
                packetsIn = s.PacketsIn,
                packetsOut = s.PacketsOut,
                bytesOut = s.BytesOut,
                allocMb = s.AllocMb,
                gen0 = s.Gen0,
                gen1 = s.Gen1,
                gen2 = s.Gen2,
                gcPauseMs = s.GcPauseMs,
                monstersAwake = s.MonstersAwake,
                aiRuns = s.AiRuns,
                mobCpu = s.MobCpu,
                mobBusyMs = s.MobBusyMs,
                mobLateMs = s.MobLateMs,
            };
        }

        private static Task GetHost(WebAdminRequest req)
        {
            var h = WebAdminHostMetrics.Read();
            var tc = WebAdminThreadCpu.Last;

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
                threadCpu = new
                {
                    threads = tc.Threads.Select(t => new { name = t.Name, mobThreadId = t.MobThreadId, cpuPercent = t.CpuPercent }).ToList(),
                    otherPercent = tc.OtherPercent,
                    sampledUtc = tc.SampledUtc,
                },
                liveSampling = WebAdminStatusSampler.LiveActive,
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
