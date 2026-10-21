using System.Diagnostics;
using Server.MirEnvir;

namespace Server.WebAdmin
{
    /// <summary>CPU of one game-related OS thread over the last sampler interval, as % of ONE core (0..100).</summary>
    public sealed class ThreadCpuEntry
    {
        /// <summary>"game" for the game thread (WorkLoop, which also runs mob thread 0), else "mob1", "mob2", ...</summary>
        public string Name { get; set; }
        public int MobThreadId { get; set; } = -1;
        public double CpuPercent { get; set; }
    }

    /// <summary>Per-thread CPU snapshot (see <see cref="WebAdminThreadCpu"/>).</summary>
    public sealed class ThreadCpuSnapshot
    {
        public List<ThreadCpuEntry> Threads { get; set; } = new List<ThreadCpuEntry>();
        /// <summary>Everything else in Server.exe (IOCP, GC, web admin, timers), in cores x 100 (e.g. 150 = 1.5 cores).</summary>
        public double OtherPercent { get; set; }
        public DateTime SampledUtc { get; set; }
    }

    /// <summary>
    /// Per-thread CPU for the game thread and the mob threads: deltas of <see cref="ProcessThread.TotalProcessorTime"/> over at least
    /// <see cref="MinWindow"/> (sampled on the status sampler timer thread; never the game thread), matched by the OS ids the threads record
    /// at start (<see cref="Envir.GameThreadNativeId"/>, <see cref="MobThread.NativeThreadId"/>). Windows only; never throws.
    /// </summary>
    public static class WebAdminThreadCpu
    {
        /// <summary>
        /// Shortest measuring window. Windows charges thread CPU in clock ticks (~15.6 ms), so a 1 s window (live sampling) moves in
        /// ~1.6 % steps; each sample is measured against the newest baseline at least this old instead.
        /// </summary>
        private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(5);

        private static readonly object Gate = new object();
        private static readonly List<(DateTime Wall, Dictionary<int, TimeSpan> Cpu, TimeSpan Total)> _baselines = new List<(DateTime, Dictionary<int, TimeSpan>, TimeSpan)>();
        private static ThreadCpuSnapshot _last = new ThreadCpuSnapshot();

        public static ThreadCpuSnapshot Last
        {
            get
            {
                lock (Gate) return _last;
            }
        }

        /// <summary>CPU over an interval as % of one core, clamped to 0..100, one decimal (pure; tested).</summary>
        public static double OneCorePercent(TimeSpan prevCpu, TimeSpan nowCpu, double wallMs)
        {
            if (wallMs <= 0) return 0;
            var cpuMs = (nowCpu - prevCpu).TotalMilliseconds;
            if (cpuMs <= 0) return 0;
            return Math.Round(Math.Clamp(cpuMs / wallMs * 100.0, 0, 100), 1);
        }

        internal static void Sample(Process p, TimeSpan processCpu)
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                var now = DateTime.UtcNow;
                var cur = new Dictionary<int, TimeSpan>();
                foreach (ProcessThread t in p.Threads)
                {
                    try
                    {
                        cur[t.Id] = t.TotalProcessorTime;
                    }
                    catch (Exception)
                    {
                        // the thread exited between enumeration and the read
                    }
                    finally
                    {
                        t.Dispose();
                    }
                }

                // Ids of the threads to report; scalar reads of Envir.Main off the game thread (same tolerance as the status timer).
                var ids = new List<(string Name, int MobId, int NativeId)>();
                if (WebAdminGame.IsRunning)
                {
                    var game = Envir.GameThreadNativeId;
                    if (game != 0) ids.Add(("game", 0, game));
                    var threads = Envir.Main?.MobThreads;
                    if (Settings.Multithreaded && threads != null)
                    {
                        for (var i = 1; i < threads.Length; i++)
                        {
                            var t = threads[i];
                            if (t != null && t.NativeThreadId != 0) ids.Add(("mob" + i, i, t.NativeThreadId));
                        }
                    }
                }

                lock (Gate)
                {
                    // Keep the newest baseline that is at least MinWindow old (plus the younger ones after it).
                    while (_baselines.Count > 1 && now - _baselines[1].Wall >= MinWindow) _baselines.RemoveAt(0);

                    var snap = new ThreadCpuSnapshot { SampledUtc = now };
                    var baseline = _baselines.Count > 0 ? _baselines[0] : default;
                    var prev = baseline.Cpu;
                    var wallMs = (now - baseline.Wall).TotalMilliseconds;
                    if (prev != null && prev.Count > 0 && wallMs > 0)
                    {
                        double named = 0;
                        foreach (var (name, mobId, nativeId) in ids)
                        {
                            if (!cur.TryGetValue(nativeId, out var nowCpu) || !prev.TryGetValue(nativeId, out var prevCpu)) continue;
                            var pct = OneCorePercent(prevCpu, nowCpu, wallMs);
                            named += (nowCpu - prevCpu).TotalMilliseconds;
                            snap.Threads.Add(new ThreadCpuEntry { Name = name, MobThreadId = mobId, CpuPercent = pct });
                        }

                        var otherMs = (processCpu - baseline.Total).TotalMilliseconds - named;
                        snap.OtherPercent = Math.Round(Math.Max(0, otherMs) / wallMs * 100.0, 1);
                    }

                    _baselines.Add((now, cur, processCpu));
                    if (_baselines.Count > 64) _baselines.RemoveAt(0); // bound: the sampler runs at most once a second
                    _last = snap;
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Thread CPU sample failed");
            }
        }
    }
}
