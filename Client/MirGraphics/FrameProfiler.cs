using System.Diagnostics;

namespace Client.MirGraphics
{
    /// <summary>
    /// Frame-time profiler for the game loop (roadmap C1). Accumulates update, scene-draw and present time with
    /// Stopwatch ticks and publishes a one-line summary once per second (<see cref="Summary"/>, shown in the debug label).
    /// Never allocates or formats per frame. Main (UI) thread only.
    /// </summary>
    internal static class FrameProfiler
    {
        private static long _update, _draw, _present, _maxFrame;
        private static int _frames, _updates;
        private static long _windowEnd;

        /// <summary>Last one-second window, e.g. "upd 0.05ms draw 1.20ms present 0.40ms max 3.10ms (98 frames)".</summary>
        public static string Summary { get; private set; } = string.Empty;

        public static long Now() => Stopwatch.GetTimestamp();

        public static void AddUpdate(long start)
        {
            _update += Stopwatch.GetTimestamp() - start;
            _updates++;
        }

        /// <summary>One drawn frame: <paramref name="start"/> → <paramref name="presentStart"/> is the scene, the rest Present.</summary>
        public static void AddFrame(long start, long presentStart)
        {
            long end = Stopwatch.GetTimestamp();
            _draw += presentStart - start;
            _present += end - presentStart;
            if (end - start > _maxFrame) _maxFrame = end - start;
            _frames++;

            if (CMain.Time < _windowEnd) return;
            _windowEnd = CMain.Time + 1000;

            double ms = 1000.0 / Stopwatch.Frequency;
            int f = Math.Max(1, _frames), u = Math.Max(1, _updates);
            Summary = $"upd {_update * ms / u:0.000}ms draw {_draw * ms / f:0.00}ms present {_present * ms / f:0.00}ms max {_maxFrame * ms:0.00}ms ({_frames} frames)";
            _update = _draw = _present = _maxFrame = 0;
            _frames = _updates = 0;
        }
    }
}
