using System.Diagnostics;

namespace Server.MirEnvir
{
    /// <summary>
    /// Game-loop profiler (roadmap P6). Accumulators are always on and allocation-free: the WorkLoop marks phase
    /// boundaries with Stopwatch timestamps, and once per second <see cref="Publish"/> turns the totals into an immutable
    /// <see cref="Snapshot"/> (read by the web admin /status JSON) and, when [Diag] LogLoopProfile is on, one log line.
    /// Game thread only, except <see cref="Last"/>, which any thread may read.
    /// The "[Diag] Loop" line format is a parser contract: only ever append fields.
    /// </summary>
    public static class LoopProfiler
    {
        public enum Phase { Connections, MobBarrier, Objects, Maps, EnvirProcess, AdminDrain, Saves, Park, Other }
        private const int PhaseCount = 9;

        // iteration duration histogram upper bounds in microseconds; the last bucket is "everything slower"
        private static readonly long[] BucketUpperUs = { 100, 1000, 5000, 10000, 20000, 50000, 100000 };

        private static readonly long[] _phaseTicks = new long[PhaseCount];
        private static readonly long[] _buckets = new long[BucketUpperUs.Length + 1];
        private static long _iterations, _iterationStart, _mark, _maxIterationTicks;
        private static long _packetsIn, _packetsOut, _bytesOut;
        private static long _lastPublish = Stopwatch.GetTimestamp();
        private static int _gen0, _gen1, _gen2;
        private static long _allocated;
        private static TimeSpan _gcPause;

        public sealed class Snapshot
        {
            public DateTime Utc { get; init; }
            public double WindowMs { get; init; }
            public long Iterations { get; init; }
            public double MaxIterationMs { get; init; }
            /// <summary>Milliseconds spent per phase in the window, by <see cref="Phase"/> name.</summary>
            public Dictionary<string, double> PhaseMs { get; init; }
            /// <summary>Iteration counts per duration bucket: "&lt;0.1ms", "&lt;1ms", ... "&gt;=100ms".</summary>
            public Dictionary<string, long> Histogram { get; init; }
            /// <summary>Share of the window attributed to a named phase (not Other), 0..1.</summary>
            public double Attributed { get; init; }
            /// <summary>Share of the window the loop was busy (not parked), 0..1.</summary>
            public double Busy { get; init; }
            public long PacketsIn { get; init; }
            public long PacketsOut { get; init; }
            public long BytesOut { get; init; }
            public int Gen0 { get; init; }
            public int Gen1 { get; init; }
            public int Gen2 { get; init; }
            public long AllocatedBytes { get; init; }
            public double GcPauseMs { get; init; }
            /// <summary>Per mob thread load (index = thread id; null when not multithreaded or without the due scheduler).</summary>
            public MobThreadLoad[] MobThreads { get; init; }
            /// <summary>Distinct monsters that ran AI in the window (due scheduler; -1 when not counted).</summary>
            public int MonstersAwake { get; init; } = -1;
        }

        /// <summary>The most recent one-second snapshot (null until the first publish).</summary>
        public static Snapshot Last { get; private set; }

        public static void BeginIteration()
        {
            var now = Stopwatch.GetTimestamp();
            if (_mark != 0) _phaseTicks[(int)Phase.Park] += now - _mark; // the gap since EndIteration is the idle park
            _iterationStart = now;
            _mark = now;
        }

        /// <summary>Attributes the time since the previous mark to <paramref name="phase"/>.</summary>
        public static void Mark(Phase phase)
        {
            var now = Stopwatch.GetTimestamp();
            _phaseTicks[(int)phase] += now - _mark;
            _mark = now;
        }

        public static void EndIteration()
        {
            var now = Stopwatch.GetTimestamp();
            _phaseTicks[(int)Phase.Other] += now - _mark;
            _mark = now;
            var busy = now - _iterationStart;
            _iterations++;
            if (busy > _maxIterationTicks) _maxIterationTicks = busy;
            var us = busy * 1_000_000 / Stopwatch.Frequency;
            var b = 0;
            while (b < BucketUpperUs.Length && us >= BucketUpperUs[b]) b++;
            _buckets[b]++;
        }

        public static void CountPacketIn() => _packetsIn++;
        public static void CountPacketsOut(int packets, int bytes) { _packetsOut += packets; _bytesOut += bytes; }

        /// <summary>Publishes and resets the window without mob thread data.</summary>
        public static void Publish() => Publish(null, -1);

        /// <summary>Called once per second by the WorkLoop: publishes and resets the window.</summary>
        public static void Publish(MobThreadLoad[] mobThreads, int monstersAwake)
        {
            var now = Stopwatch.GetTimestamp();
            var windowTicks = Math.Max(1, now - _lastPublish);
            _lastPublish = now;
            double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

            var phaseMs = new Dictionary<string, double>(PhaseCount);
            long named = 0;
            for (var i = 0; i < PhaseCount; i++)
            {
                phaseMs[((Phase)i).ToString()] = Math.Round(ToMs(_phaseTicks[i]), 3);
                if (i != (int)Phase.Other && i != (int)Phase.Park) named += _phaseTicks[i];
            }
            var park = _phaseTicks[(int)Phase.Park];
            var nonPark = Math.Max(1, windowTicks - park);

            var hist = new Dictionary<string, long>(_buckets.Length);
            for (var i = 0; i < _buckets.Length; i++)
                hist[i < BucketUpperUs.Length ? "<" + FormatUs(BucketUpperUs[i]) : ">=" + FormatUs(BucketUpperUs[^1])] = _buckets[i];

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var alloc = GC.GetTotalAllocatedBytes();
            var pause = GC.GetTotalPauseDuration();

            var snap = new Snapshot
            {
                Utc = DateTime.UtcNow,
                WindowMs = Math.Round(ToMs(windowTicks), 1),
                Iterations = _iterations,
                MaxIterationMs = Math.Round(ToMs(_maxIterationTicks), 3),
                PhaseMs = phaseMs,
                Histogram = hist,
                Attributed = Math.Round((double)named / nonPark, 4),
                Busy = Math.Round(1.0 - (double)park / windowTicks, 4),
                PacketsIn = _packetsIn,
                PacketsOut = _packetsOut,
                BytesOut = _bytesOut,
                Gen0 = g0 - _gen0,
                Gen1 = g1 - _gen1,
                Gen2 = g2 - _gen2,
                AllocatedBytes = alloc - _allocated,
                GcPauseMs = Math.Round((pause - _gcPause).TotalMilliseconds, 3),
                MobThreads = mobThreads,
                MonstersAwake = monstersAwake,
            };
            Last = snap;

            _gen0 = g0; _gen1 = g1; _gen2 = g2; _allocated = alloc; _gcPause = pause;
            Array.Clear(_phaseTicks);
            Array.Clear(_buckets);
            _iterations = 0; _maxIterationTicks = 0; _packetsIn = 0; _packetsOut = 0; _bytesOut = 0;

            if (Settings.LogLoopProfile)
                MessageQueue.Instance.EnqueueDebugging(Format(snap));
        }

        /// <summary>The "[Diag] Loop" line. Append-only field order.</summary>
        public static string Format(Snapshot s)
        {
            var p = s.PhaseMs;
            return $"[Diag] Loop it={s.Iterations} max={s.MaxIterationMs}ms busy={s.Busy:P1} attributed={s.Attributed:P1} " +
                   $"conn={p["Connections"]} mob={p["MobBarrier"]} obj={p["Objects"]} maps={p["Maps"]} envir={p["EnvirProcess"]} " +
                   $"admin={p["AdminDrain"]} saves={p["Saves"]} park={p["Park"]} other={p["Other"]} " +
                   $"pin={s.PacketsIn} pout={s.PacketsOut} bout={s.BytesOut} gc={s.Gen0}/{s.Gen1}/{s.Gen2} alloc={s.AllocatedBytes} gcpause={s.GcPauseMs}ms " +
                   $"hist={string.Join(",", s.Histogram.Values)}";
        }

        private static string FormatUs(long us) => us >= 1000 ? (us / 1000) + "ms" : (us / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "ms";
    }
}
