using Server.MirEnvir;

namespace Server.WebAdmin
{
    /// <summary>One status sample (every 5 s, or every 1 s into the live ring) for the dashboard charts.</summary>
    public sealed class StatusSample
    {
        public DateTime Utc { get; set; }
        public int Players { get; set; }
        public int Connections { get; set; }
        public int Monsters { get; set; }
        public long CycleDelayMs { get; set; }
        public long WorkingSetMb { get; set; }
        public double CpuPercent { get; set; }

        // Load detail (2026-10-21). Loop values are the profiler's last one-second window at sample time; 0 until it publishes.
        /// <summary>Share of the second the game thread was busy (not parked), 0..100.</summary>
        public double GameBusyPercent { get; set; }
        /// <summary>Game thread CPU, % of one core.</summary>
        public double GameCpuPercent { get; set; }
        /// <summary>Rest of Server.exe (IOCP, GC, web admin), % of one core (may exceed 100).</summary>
        public double OtherCpuPercent { get; set; }
        public double MaxIterationMs { get; set; }
        /// <summary>Game loop ms per phase in the window, in <see cref="PhaseNames"/> order.</summary>
        public double[] PhaseMs { get; set; }
        public long PacketsIn { get; set; }
        public long PacketsOut { get; set; }
        public long BytesOut { get; set; }
        public double AllocMb { get; set; }
        public int Gen0 { get; set; }
        public int Gen1 { get; set; }
        public int Gen2 { get; set; }
        public double GcPauseMs { get; set; }
        /// <summary>Distinct monsters that ran AI in the window; -1 when not counted.</summary>
        public int MonstersAwake { get; set; } = -1;
        /// <summary>Monster AI runs in the window (all mob threads).</summary>
        public int AiRuns { get; set; }
        /// <summary>Per mob thread (index = thread id): CPU % of one core. [0] stays 0: thread 0 runs on the game thread (GameCpuPercent).</summary>
        public double[] MobCpu { get; set; }
        /// <summary>Per mob thread: ms busy in scheduler passes in the window.</summary>
        public double[] MobBusyMs { get; set; }
        /// <summary>Per mob thread: worst lateness in ms.</summary>
        public long[] MobLateMs { get; set; }

        /// <summary>Loop profiler phases charted on the dashboard (Park and Other are left out).</summary>
        public static readonly string[] PhaseNames = { "Connections", "MobBarrier", "Objects", "Maps", "EnvirProcess", "AdminDrain", "Saves", "Send" };
    }

    /// <summary>Fixed-capacity ring of <see cref="StatusSample"/> (oldest overwritten), thread-safe. Pure; unit tested.</summary>
    public sealed class WebAdminSampleRing
    {
        private readonly StatusSample[] _items;
        private readonly object _gate = new object();
        private int _start;
        private int _count;

        public WebAdminSampleRing(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new StatusSample[capacity];
        }

        public int Capacity => _items.Length;

        public int Count
        {
            get
            {
                lock (_gate) return _count;
            }
        }

        public void Add(StatusSample sample)
        {
            if (sample == null) return;

            lock (_gate)
            {
                if (_count < _items.Length)
                {
                    _items[(_start + _count) % _items.Length] = sample;
                    _count++;
                }
                else
                {
                    _items[_start] = sample;
                    _start = (_start + 1) % _items.Length;
                }
            }
        }

        public StatusSample Latest
        {
            get
            {
                lock (_gate) return _count == 0 ? null : _items[(_start + _count - 1) % _items.Length];
            }
        }

        /// <summary>Samples with Utc &gt;= <paramref name="sinceUtc"/>, oldest first.</summary>
        public List<StatusSample> Since(DateTime sinceUtc)
        {
            lock (_gate)
            {
                var list = new List<StatusSample>();
                for (var i = 0; i < _count; i++)
                {
                    var s = _items[(_start + i) % _items.Length];
                    if (s.Utc >= sinceUtc) list.Add(s);
                }

                return list;
            }
        }

        /// <summary>
        /// Reduces <paramref name="samples"/> (oldest first) to at most <paramref name="maxPoints"/> buckets of consecutive samples.
        /// Per bucket: Utc = the bucket's last sample; Players/Connections/Monsters/CycleDelayMs/WorkingSetMb and the spike values
        /// (MaxIterationMs, Gen2, GcPauseMs, MobLateMs) = the maximum (spikes stay visible); CpuPercent and the rates and shares (busy,
        /// thread CPU, phases, packets, bytes, allocation, Gen0/Gen1, awake, AI runs, mob busy) = the average (one decimal; integers
        /// rounded). Fewer samples than maxPoints are returned unchanged (copied).
        /// </summary>
        public static List<StatusSample> Downsample(IReadOnlyList<StatusSample> samples, int maxPoints)
        {
            var result = new List<StatusSample>();
            if (samples == null || samples.Count == 0) return result;
            if (maxPoints < 1) maxPoints = 1;

            if (samples.Count <= maxPoints)
            {
                foreach (var s in samples) result.Add(Copy(s));
                return result;
            }

            var bucketSize = (int)Math.Ceiling(samples.Count / (double)maxPoints);

            for (var start = 0; start < samples.Count; start += bucketSize)
            {
                var end = Math.Min(samples.Count, start + bucketSize);
                var n = end - start;
                var agg = new StatusSample();
                double cpu = 0, busy = 0, gameCpu = 0, otherCpu = 0, pin = 0, pout = 0, bout = 0, alloc = 0, g0 = 0, g1 = 0, awake = 0, ai = 0;
                double[] phases = null, mobCpu = null, mobBusy = null;
                long[] mobLate = null;
                var awakeSamples = 0;

                for (var i = start; i < end; i++)
                {
                    var s = samples[i];
                    agg.Players = Math.Max(agg.Players, s.Players);
                    agg.Connections = Math.Max(agg.Connections, s.Connections);
                    agg.Monsters = Math.Max(agg.Monsters, s.Monsters);
                    agg.CycleDelayMs = Math.Max(agg.CycleDelayMs, s.CycleDelayMs);
                    agg.WorkingSetMb = Math.Max(agg.WorkingSetMb, s.WorkingSetMb);
                    agg.MaxIterationMs = Math.Max(agg.MaxIterationMs, s.MaxIterationMs);
                    agg.Gen2 = Math.Max(agg.Gen2, s.Gen2);
                    agg.GcPauseMs = Math.Max(agg.GcPauseMs, s.GcPauseMs);
                    cpu += s.CpuPercent;
                    busy += s.GameBusyPercent;
                    gameCpu += s.GameCpuPercent;
                    otherCpu += s.OtherCpuPercent;
                    pin += s.PacketsIn;
                    pout += s.PacketsOut;
                    bout += s.BytesOut;
                    alloc += s.AllocMb;
                    g0 += s.Gen0;
                    g1 += s.Gen1;
                    ai += s.AiRuns;
                    if (s.MonstersAwake >= 0)
                    {
                        awake += s.MonstersAwake;
                        awakeSamples++;
                    }
                    SumInto(ref phases, s.PhaseMs);
                    SumInto(ref mobCpu, s.MobCpu);
                    SumInto(ref mobBusy, s.MobBusyMs);
                    MaxInto(ref mobLate, s.MobLateMs);
                }

                agg.Utc = samples[end - 1].Utc;
                agg.CpuPercent = Math.Round(cpu / n, 1);
                agg.GameBusyPercent = Math.Round(busy / n, 1);
                agg.GameCpuPercent = Math.Round(gameCpu / n, 1);
                agg.OtherCpuPercent = Math.Round(otherCpu / n, 1);
                agg.PacketsIn = (long)Math.Round(pin / n);
                agg.PacketsOut = (long)Math.Round(pout / n);
                agg.BytesOut = (long)Math.Round(bout / n);
                agg.AllocMb = Math.Round(alloc / n, 1);
                agg.Gen0 = (int)Math.Round(g0 / n);
                agg.Gen1 = (int)Math.Round(g1 / n);
                agg.AiRuns = (int)Math.Round(ai / n);
                agg.MonstersAwake = awakeSamples == 0 ? -1 : (int)Math.Round(awake / awakeSamples);
                agg.PhaseMs = Average(phases, n);
                agg.MobCpu = Average(mobCpu, n);
                agg.MobBusyMs = Average(mobBusy, n);
                agg.MobLateMs = mobLate;
                result.Add(agg);
            }

            return result;
        }

        // Element-wise helpers for the per-phase / per-thread arrays (a thread count change mid-bucket keeps the longer length).
        private static void SumInto(ref double[] acc, double[] v)
        {
            if (v == null) return;
            if (acc == null || acc.Length < v.Length) Array.Resize(ref acc, v.Length);
            for (var i = 0; i < v.Length; i++) acc[i] += v[i];
        }

        private static void MaxInto(ref long[] acc, long[] v)
        {
            if (v == null) return;
            if (acc == null || acc.Length < v.Length) Array.Resize(ref acc, v.Length);
            for (var i = 0; i < v.Length; i++) acc[i] = Math.Max(acc[i], v[i]);
        }

        private static double[] Average(double[] sum, int n)
        {
            if (sum == null) return null;
            var r = new double[sum.Length];
            for (var i = 0; i < sum.Length; i++) r[i] = Math.Round(sum[i] / n, 1);
            return r;
        }

        private static StatusSample Copy(StatusSample s)
        {
            return new StatusSample
            {
                Utc = s.Utc,
                Players = s.Players,
                Connections = s.Connections,
                Monsters = s.Monsters,
                CycleDelayMs = s.CycleDelayMs,
                WorkingSetMb = s.WorkingSetMb,
                CpuPercent = s.CpuPercent,
                GameBusyPercent = s.GameBusyPercent,
                GameCpuPercent = s.GameCpuPercent,
                OtherCpuPercent = s.OtherCpuPercent,
                MaxIterationMs = s.MaxIterationMs,
                PhaseMs = (double[])s.PhaseMs?.Clone(),
                PacketsIn = s.PacketsIn,
                PacketsOut = s.PacketsOut,
                BytesOut = s.BytesOut,
                AllocMb = s.AllocMb,
                Gen0 = s.Gen0,
                Gen1 = s.Gen1,
                Gen2 = s.Gen2,
                GcPauseMs = s.GcPauseMs,
                MonstersAwake = s.MonstersAwake,
                AiRuns = s.AiRuns,
                MobCpu = (double[])s.MobCpu?.Clone(),
                MobBusyMs = (double[])s.MobBusyMs?.Clone(),
                MobLateMs = (long[])s.MobLateMs?.Clone(),
            };
        }
    }

    /// <summary>
    /// Background sampler: every 5 s (only while the web admin host is listening) records players / connections / monsters / cycle delay /
    /// working set / CPU / game loop and mob thread load into a 24 h ring, refreshes the host CPU baseline, turns new audit records into
    /// activity events and posts the Live Monitor heat capture. Live mode (the dashboard's Live range; 1 s exception approved by the user
    /// 2026-10-21): while a client holds the lease (<see cref="RequestLive"/>, 15 s) it also samples every second into a 15 min ring.
    /// Scalar reads of Envir.Main off the game thread are tolerated (same as the WinForms status timer).
    /// </summary>
    public static class WebAdminStatusSampler
    {
        public const int IntervalSeconds = 5;
        public const int RetentionHours = 24;
        public const int MaxPoints = 360;
        public const int LiveIntervalSeconds = 1;
        public const int LiveRetentionMinutes = 15;
        private const int LiveLeaseSeconds = 15;

        internal static WebAdminSampleRing Ring { get; } = new WebAdminSampleRing(RetentionHours * 3600 / IntervalSeconds);
        internal static WebAdminSampleRing LiveRing { get; } = new WebAdminSampleRing(LiveRetentionMinutes * 60 / LiveIntervalSeconds);

        private static System.Threading.Timer _timer;
        private static int _started;
        private static int _busy;
        private static int _tick;
        private static long _liveUntilTicks;

        /// <summary>Any thread: keeps 1 s sampling on for the next <see cref="LiveLeaseSeconds"/> seconds.</summary>
        public static void RequestLive() => Interlocked.Exchange(ref _liveUntilTicks, DateTime.UtcNow.AddSeconds(LiveLeaseSeconds).Ticks);

        public static bool LiveActive => DateTime.UtcNow.Ticks < Interlocked.Read(ref _liveUntilTicks);

        /// <summary>Starts the timer once (idempotent). Ticks do nothing until <see cref="WebAdminHost.IsRunning"/>.</summary>
        internal static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(LiveIntervalSeconds));
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;

            try
            {
                if (!WebAdminHost.IsRunning) return;

                // The timer ticks every second: the 5 s work runs on every fifth tick, the live ring only while leased.
                var full = _tick++ % IntervalSeconds == 0;
                var live = LiveActive;
                if (!full && !live) return;

                WebAdminHostMetrics.Sample();
                var sample = Capture();
                if (live) LiveRing.Add(sample);
                if (!full) return;

                Ring.Add(sample);
                WebAdminActivity.PollAudit();

                try
                {
                    WebAdminMonitor.SampleTick();
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Live monitor heat sample failed");
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Status sampler tick failed");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static StatusSample Capture()
        {
            var sample = new StatusSample
            {
                Utc = DateTime.UtcNow,
                WorkingSetMb = WebAdminHostMetrics.WorkingSetMb,
                CpuPercent = WebAdminHostMetrics.CpuPercent,
            };

            try
            {
                var envir = Envir.Main;
                if (WebAdminGame.IsRunning)
                {
                    sample.Players = envir.Players.Count;
                    sample.Connections = envir.Connections.Count;
                    sample.Monsters = envir.MonsterCount;
                    sample.CycleDelayMs = Envir.LastRunTime;
                    AddLoad(sample);
                }
            }
            catch (Exception)
            {
                // racy read of a live list: keep zeroes for this tick
            }

            return sample;
        }

        /// <summary>Copies the per-thread CPU and the loop profiler's last window (immutable snapshots) into <paramref name="sample"/>.</summary>
        private static void AddLoad(StatusSample sample)
        {
            var cpu = WebAdminThreadCpu.Last;
            sample.OtherCpuPercent = cpu.OtherPercent;
            for (var i = 0; i < cpu.Threads.Count; i++)
            {
                var t = cpu.Threads[i];
                if (t.MobThreadId == 0)
                {
                    sample.GameCpuPercent = t.CpuPercent;
                    continue;
                }
                if (sample.MobCpu == null) sample.MobCpu = new double[Math.Max(1, Settings.ThreadLimit)];
                if (t.MobThreadId < sample.MobCpu.Length) sample.MobCpu[t.MobThreadId] = t.CpuPercent;
            }

            var p = LoopProfiler.Last;
            // The profiler keeps its last window after a stop: use only a recent one.
            if (p == null || (DateTime.UtcNow - p.Utc).TotalSeconds > 5) return;

            sample.GameBusyPercent = Math.Round(p.Busy * 100, 1);
            sample.MaxIterationMs = p.MaxIterationMs;
            sample.PacketsIn = p.PacketsIn;
            sample.PacketsOut = p.PacketsOut;
            sample.BytesOut = p.BytesOut;
            sample.AllocMb = Math.Round(p.AllocatedBytes / 1048576.0, 1);
            sample.Gen0 = p.Gen0;
            sample.Gen1 = p.Gen1;
            sample.Gen2 = p.Gen2;
            sample.GcPauseMs = p.GcPauseMs;
            sample.MonstersAwake = p.MonstersAwake;

            if (p.PhaseMs != null)
            {
                sample.PhaseMs = new double[StatusSample.PhaseNames.Length];
                for (var i = 0; i < sample.PhaseMs.Length; i++)
                    if (p.PhaseMs.TryGetValue(StatusSample.PhaseNames[i], out var ms)) sample.PhaseMs[i] = ms;
            }

            var mob = p.MobThreads;
            if (mob != null)
            {
                sample.MobBusyMs = new double[mob.Length];
                sample.MobLateMs = new long[mob.Length];
                for (var i = 0; i < mob.Length; i++)
                {
                    if (mob[i] == null) continue;
                    sample.MobBusyMs[i] = mob[i].BusyMs;
                    sample.MobLateMs[i] = mob[i].LateMs;
                    sample.AiRuns += mob[i].Processed;
                }
            }
        }

        /// <summary>Normalises a requested window to 5..1440 minutes (default 60).</summary>
        public static int ClampMinutes(int minutes)
        {
            if (minutes <= 0) return 60;
            return Math.Clamp(minutes, 5, RetentionHours * 60);
        }

        /// <summary>Samples of the last <paramref name="minutes"/>, downsampled to ≤ <see cref="MaxPoints"/>.</summary>
        public static List<StatusSample> History(int minutes, DateTime nowUtc)
        {
            minutes = ClampMinutes(minutes);
            return WebAdminSampleRing.Downsample(Ring.Since(nowUtc.AddMinutes(-minutes)), MaxPoints);
        }

        /// <summary>Live ring samples newer than <paramref name="afterUtc"/> (the whole last 15 min when null), oldest first.</summary>
        public static List<StatusSample> LiveSince(DateTime? afterUtc, DateTime nowUtc)
        {
            var list = LiveRing.Since(nowUtc.AddMinutes(-LiveRetentionMinutes));
            if (afterUtc == null) return list;

            var r = new List<StatusSample>(list.Count);
            for (var i = 0; i < list.Count; i++)
                if (list[i].Utc > afterUtc.Value) r.Add(list[i]);
            return r;
        }
    }
}
