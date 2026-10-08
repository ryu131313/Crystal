using Server.MirEnvir;

namespace Server.WebAdmin
{
    /// <summary>One status sample (every 5 s) for the dashboard charts.</summary>
    public sealed class StatusSample
    {
        public DateTime Utc { get; set; }
        public int Players { get; set; }
        public int Connections { get; set; }
        public int Monsters { get; set; }
        public long CycleDelayMs { get; set; }
        public long WorkingSetMb { get; set; }
        public double CpuPercent { get; set; }
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
        /// Per bucket: Utc = the bucket's last sample; Players/Connections/Monsters/CycleDelayMs/WorkingSetMb = the maximum (spikes
        /// stay visible); CpuPercent = the average (one decimal). Fewer samples than maxPoints are returned unchanged (copied).
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
                var agg = new StatusSample();
                double cpu = 0;

                for (var i = start; i < end; i++)
                {
                    var s = samples[i];
                    agg.Players = Math.Max(agg.Players, s.Players);
                    agg.Connections = Math.Max(agg.Connections, s.Connections);
                    agg.Monsters = Math.Max(agg.Monsters, s.Monsters);
                    agg.CycleDelayMs = Math.Max(agg.CycleDelayMs, s.CycleDelayMs);
                    agg.WorkingSetMb = Math.Max(agg.WorkingSetMb, s.WorkingSetMb);
                    cpu += s.CpuPercent;
                }

                agg.Utc = samples[end - 1].Utc;
                agg.CpuPercent = Math.Round(cpu / (end - start), 1);
                result.Add(agg);
            }

            return result;
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
            };
        }
    }

    /// <summary>
    /// Background sampler: every 5 s (only while the web admin host is listening) records players / connections / monsters / cycle delay /
    /// working set / CPU into a 24 h ring, refreshes the host CPU baseline and turns new audit records into activity events.
    /// Scalar reads of Envir.Main off the game thread are tolerated (same as the WinForms status timer).
    /// </summary>
    public static class WebAdminStatusSampler
    {
        public const int IntervalSeconds = 5;
        public const int RetentionHours = 24;
        public const int MaxPoints = 360;

        internal static WebAdminSampleRing Ring { get; } = new WebAdminSampleRing(RetentionHours * 3600 / IntervalSeconds);

        private static System.Threading.Timer _timer;
        private static int _started;
        private static int _busy;

        /// <summary>Starts the timer once (idempotent). Ticks do nothing until <see cref="WebAdminHost.IsRunning"/>.</summary>
        internal static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(IntervalSeconds));
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;

            try
            {
                if (!WebAdminHost.IsRunning) return;

                WebAdminHostMetrics.Sample();
                Ring.Add(Capture());
                WebAdminActivity.PollAudit();
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
                }
            }
            catch (Exception)
            {
                // racy read of a live list: keep zeroes for this tick
            }

            return sample;
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
    }
}
