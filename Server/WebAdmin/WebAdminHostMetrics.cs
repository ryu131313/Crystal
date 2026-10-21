using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Server.WebAdmin
{
    /// <summary>Process / host health snapshot for GET /api/admin/host (sizes in MB, CPU normalised over all cores).</summary>
    public sealed class HostMetricsSnapshot
    {
        public double CpuPercent { get; set; }
        public long WorkingSetMb { get; set; }
        public long PeakWorkingSetMb { get; set; }
        public long PrivateMb { get; set; }
        public long GcHeapMb { get; set; }
        public long GcTotalMemoryMb { get; set; }
        public int Gen0 { get; set; }
        public int Gen1 { get; set; }
        public int Gen2 { get; set; }
        public int Threads { get; set; }
        public int Handles { get; set; }
        public int ProcessorCount { get; set; }
        public string DiskRoot { get; set; }
        public long DiskFreeMb { get; set; }
        public long DiskTotalMb { get; set; }
        public string DotnetVersion { get; set; }
        public string Os { get; set; }
        public string Architecture { get; set; }
        public string MachineName { get; set; }
        public DateTime ProcessStartUtc { get; set; }
        public long ProcessUptimeSec { get; set; }
        public DateTime SampledUtc { get; set; }
    }

    /// <summary>
    /// Server.exe process metrics. CPU% is a derivative of TotalProcessorTime between two <see cref="Sample"/> calls (the status sampler
    /// ticks every 5 s, or every 1 s in live mode); per-thread CPU comes from <see cref="WebAdminThreadCpu"/>; everything else is read fresh by <see cref="Read"/>. Never throws.
    /// </summary>
    public static class WebAdminHostMetrics
    {
        private const long Mb = 1024 * 1024;

        private static readonly object Gate = new object();
        private static TimeSpan _prevCpu;
        private static DateTime _prevWallUtc;
        private static bool _haveBaseline;
        private static double _cpuPercent;
        private static long _workingSetMb;

        /// <summary>CPU % from the last <see cref="Sample"/> (0 before two samples exist).</summary>
        public static double CpuPercent
        {
            get
            {
                lock (Gate) return _cpuPercent;
            }
        }

        /// <summary>Working set (MB) from the last <see cref="Sample"/>.</summary>
        public static long WorkingSetMb
        {
            get
            {
                lock (Gate) return _workingSetMb;
            }
        }

        /// <summary>
        /// Whole-process CPU percentage over an interval, normalised across <paramref name="cores"/> and clamped to 0..100, one decimal.
        /// Returns 0 for a non-positive wall interval (pure; tested).
        /// </summary>
        public static double ComputeCpuPercent(TimeSpan prevCpu, DateTime prevWallUtc, TimeSpan nowCpu, DateTime nowWallUtc, int cores)
        {
            var wallMs = (nowWallUtc - prevWallUtc).TotalMilliseconds;
            if (wallMs <= 0) return 0;

            var cpuMs = (nowCpu - prevCpu).TotalMilliseconds;
            if (cpuMs <= 0) return 0;

            cores = Math.Max(1, cores);
            var pct = cpuMs / (wallMs * cores) * 100.0;
            return Math.Round(Math.Clamp(pct, 0, 100), 1);
        }

        /// <summary>Updates the CPU % baseline and the cached working set. Called by the status sampler.</summary>
        public static void Sample()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    TimeSpan cpu;
                    try
                    {
                        cpu = p.TotalProcessorTime;
                    }
                    catch (Exception)
                    {
                        cpu = TimeSpan.Zero;
                    }

                    var now = DateTime.UtcNow;
                    var ws = p.WorkingSet64 / Mb;

                    lock (Gate)
                    {
                        if (_haveBaseline && cpu > TimeSpan.Zero)
                            _cpuPercent = ComputeCpuPercent(_prevCpu, _prevWallUtc, cpu, now, Environment.ProcessorCount);

                        _prevCpu = cpu;
                        _prevWallUtc = now;
                        _haveBaseline = cpu > TimeSpan.Zero;
                        _workingSetMb = ws;
                    }

                    if (cpu > TimeSpan.Zero) WebAdminThreadCpu.Sample(p, cpu);
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Host metrics sample failed");
            }
        }

        /// <summary>A fresh snapshot (CPU % comes from the last sampler tick).</summary>
        public static HostMetricsSnapshot Read()
        {
            var s = new HostMetricsSnapshot
            {
                CpuPercent = CpuPercent,
                ProcessorCount = Environment.ProcessorCount,
                SampledUtc = DateTime.UtcNow,
                Gen0 = GC.CollectionCount(0),
                Gen1 = GC.CollectionCount(1),
                Gen2 = GC.CollectionCount(2),
                GcTotalMemoryMb = GC.GetTotalMemory(false) / Mb,
            };

            try
            {
                s.GcHeapMb = GC.GetGCMemoryInfo().HeapSizeBytes / Mb;
            }
            catch (Exception)
            {
                s.GcHeapMb = s.GcTotalMemoryMb;
            }

            try
            {
                s.DotnetVersion = RuntimeInformation.FrameworkDescription;
                s.Os = RuntimeInformation.OSDescription;
                s.Architecture = RuntimeInformation.ProcessArchitecture.ToString();
                s.MachineName = Environment.MachineName;
            }
            catch (Exception)
            {
                s.DotnetVersion ??= Environment.Version.ToString();
            }

            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    s.WorkingSetMb = p.WorkingSet64 / Mb;
                    s.PeakWorkingSetMb = p.PeakWorkingSet64 / Mb;
                    s.PrivateMb = p.PrivateMemorySize64 / Mb;
                    s.Threads = p.Threads.Count;

                    try
                    {
                        s.Handles = p.HandleCount;
                    }
                    catch (Exception)
                    {
                        s.Handles = 0;
                    }

                    try
                    {
                        s.ProcessStartUtc = p.StartTime.ToUniversalTime();
                        s.ProcessUptimeSec = (long)Math.Max(0, (DateTime.UtcNow - s.ProcessStartUtc).TotalSeconds);
                    }
                    catch (Exception)
                    {
                        s.ProcessStartUtc = DateTime.MinValue;
                    }
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Host metrics read failed");
            }

            try
            {
                var root = Path.GetPathRoot(AppContext.BaseDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    s.DiskRoot = drive.Name;

                    if (drive.IsReady)
                    {
                        s.DiskFreeMb = drive.AvailableFreeSpace / Mb;
                        s.DiskTotalMb = drive.TotalSize / Mb;
                    }
                }
            }
            catch (Exception)
            {
                // network / removable volume: leave zeroed
            }

            return s;
        }
    }
}
