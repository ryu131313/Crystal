using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Client.MirControls;
using Client.MirScenes;

namespace Client.MirGraphics
{
    /// <summary>The cached render targets whose rebuilds <see cref="FrameProfiler"/> measures.</summary>
    internal enum RebuildTarget { Map, Floor, Ui, Overlay }

    /// <summary>
    /// Frame-time profiler for the game loop (roadmap C1). Accumulates update, scene-draw and present time with
    /// Stopwatch ticks and publishes a one-line summary once per second (<see cref="Summary"/>, shown in the debug label).
    /// Never allocates or formats per frame. Main (UI) thread only.
    /// <para>
    /// Rebuild counters: how often the map, floor and UI-scene render targets are re-rendered and what each rebuild costs
    /// on the CPU (time and sprite draws; GPU time is not included). In <see cref="Settings.DebugMode"/> it also records
    /// which code invalidated each target (one stack walk per valid→invalid flip, so at most one per rebuild) and appends
    /// a 10-second report to <see cref="ReportFile"/> from the thread pool.
    /// </para>
    /// </summary>
    internal static class FrameProfiler
    {
        public const string ReportFile = "RebuildProfile.txt";
        private const int ReportSeconds = 10;

        private static long _update, _draw, _present, _maxFrame;
        private static int _frames, _updates;
        private static long _windowEnd;

        // Rebuild stats, indexed by RebuildTarget. "Second" totals feed the debug label, "report" totals the 10 s report.
        private static readonly int[] _secRebuilds = new int[4];
        private static readonly long[] _secRebuildTicks = new long[4];
        private static readonly int[] _repRebuilds = new int[4], _repInvalidations = new int[4];
        private static readonly long[] _repRebuildTicks = new long[4], _repMaxRebuild = new long[4], _repRebuildDraws = new long[4];
        private static readonly Dictionary<MethodBase, int>[] _repCauses = { new(), new(), new(), new() };
        private static readonly Dictionary<string, int> _repTickReasons = new Dictionary<string, int>();
        private static int _repFrames, _repSeconds;
        private static long _repDraw, _repMaxFrame;

        /// <summary>Last one-second window, e.g. "upd 0.05ms draw 1.20ms present 0.40ms max 3.10ms (98 frames)".</summary>
        public static string Summary { get; private set; } = string.Empty;

        /// <summary>Last one-second window of rebuilds, e.g. "map 31/s 0.84ms floor 2/s 0.30ms ui 12/s 1.10ms".</summary>
        public static string RebuildSummary { get; private set; } = string.Empty;

        public static long Now() => Stopwatch.GetTimestamp();

        public static void AddUpdate(long start)
        {
            _update += Stopwatch.GetTimestamp() - start;
            _updates++;
        }

        /// <summary>One re-render of <paramref name="target"/>; <paramref name="drawsBefore"/> is <see cref="CMain.DPSCounter"/> at its start.</summary>
        public static void AddRebuild(RebuildTarget target, long start, int drawsBefore)
        {
            long ticks = Stopwatch.GetTimestamp() - start;
            int t = (int)target;
            _secRebuilds[t]++;
            _secRebuildTicks[t] += ticks;
            _repRebuilds[t]++;
            _repRebuildTicks[t] += ticks;
            _repRebuildDraws[t] += CMain.DPSCounter - drawsBefore;
            if (ticks > _repMaxRebuild[t]) _repMaxRebuild[t] = ticks;
        }

        /// <summary>
        /// Called on every invalidation of <paramref name="target"/>. When it flips a valid texture (the invalidation that
        /// causes the next rebuild) and DebugMode is on, the first caller outside the control/profiler plumbing is recorded.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void NoteInvalidation(RebuildTarget target, bool wasValid)
        {
            _repInvalidations[(int)target]++;
            if (!wasValid || !Settings.DebugMode) return;

            StackTrace trace = new StackTrace(1, false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                MethodBase method = trace.GetFrame(i)?.GetMethod();
                Type type = method?.DeclaringType;
                if (type == null) continue;
                // Skip the plumbing between the game code and the flag: MirControl setters and the Redraw bubble chain,
                // MirScene.Redraw, and MapControl's TextureValid shim.
                if (type.Namespace == "Client.MirControls") continue;
                if (type == typeof(MapControl) && method.Name == "set_TextureValid") continue;

                Dictionary<MethodBase, int> causes = _repCauses[(int)target];
                causes.TryGetValue(method, out int count);
                causes[method] = count + 1;
                return;
            }
        }

        /// <summary>Counts why the 100 ms map tick did (or did not, "skipped") rebuild the map. Reasons are string literals.</summary>
        public static void NoteTickReason(string reason)
        {
            _repTickReasons.TryGetValue(reason, out int count);
            _repTickReasons[reason] = count + 1;
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
            RebuildSummary = $"map {_secRebuilds[0]}/s {AvgMs(_secRebuildTicks[0], _secRebuilds[0]):0.00}ms floor {_secRebuilds[1]}/s {AvgMs(_secRebuildTicks[1], _secRebuilds[1]):0.00}ms ui {_secRebuilds[2]}/s {AvgMs(_secRebuildTicks[2], _secRebuilds[2]):0.00}ms ovl {_secRebuilds[3]}/s";

            _repFrames += _frames;
            _repDraw += _draw;
            if (_maxFrame > _repMaxFrame) _repMaxFrame = _maxFrame;
            if (++_repSeconds >= ReportSeconds) FlushReport();

            _update = _draw = _present = _maxFrame = 0;
            _frames = _updates = 0;
            Array.Clear(_secRebuilds);
            Array.Clear(_secRebuildTicks);
        }

        private static double AvgMs(long ticks, int count) => count == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / count;

        /// <summary>Formats the 10 s window (DebugMode only) and appends it to <see cref="ReportFile"/> off the UI thread.</summary>
        private static void FlushReport()
        {
            if (Settings.DebugMode)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                StringBuilder sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(' ')
                  .Append(MirScene.ActiveScene?.GetType().Name ?? "none")
                  .Append($" {_repSeconds}s: {_repFrames} frames ({(double)_repFrames / _repSeconds:0.0} fps), draw avg {_repDraw * ms / Math.Max(1, _repFrames):0.00}ms max {_repMaxFrame * ms:0.00}ms");
                if (MirScene.ActiveScene is GameScene)
                    sb.Append($", objects {MapControl.Objects.Count}, effects {MapControl.Effects.Count}");
                sb.AppendLine();

                string[] names = { "map", "floor", "ui", "ovl" };
                for (int t = 0; t < 4; t++)
                {
                    int n = _repRebuilds[t];
                    sb.Append($"  {names[t],-5} rebuilds {n,5} ({(double)n / _repSeconds,6:0.0}/s)  cpu avg {AvgMs(_repRebuildTicks[t], n):0.000}ms max {_repMaxRebuild[t] * ms:0.00}ms" +
                              $"  sprites/rebuild {(n == 0 ? 0 : _repRebuildDraws[t] / n)}  total {_repRebuildTicks[t] * ms / _repSeconds:0.00}ms/s");
                    if (t != (int)RebuildTarget.Floor) sb.Append($"  invalidate calls {_repInvalidations[t]}");
                    sb.AppendLine();
                }

                for (int t = 0; t < 4; t++)
                {
                    if (_repCauses[t].Count == 0) continue;
                    sb.Append($"  {names[t]} causes:");
                    foreach (KeyValuePair<MethodBase, int> cause in _repCauses[t].OrderByDescending(x => x.Value).Take(12))
                        sb.Append($" {cause.Key.DeclaringType?.Name}.{cause.Key.Name}={cause.Value}");
                    sb.AppendLine();
                }

                if (_repTickReasons.Count > 0)
                {
                    sb.Append("  map tick reasons:");
                    foreach (KeyValuePair<string, int> reason in _repTickReasons.OrderByDescending(x => x.Value))
                        sb.Append($" {reason.Key}={reason.Value}");
                    sb.AppendLine();
                }

                string text = sb.ToString();
                // Disk I/O off the UI thread; the profile is best-effort, so a failed append is dropped.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { File.AppendAllText(ReportFile, text); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                });
            }

            _repFrames = _repSeconds = 0;
            _repDraw = _repMaxFrame = 0;
            Array.Clear(_repRebuilds);
            Array.Clear(_repInvalidations);
            Array.Clear(_repRebuildTicks);
            Array.Clear(_repMaxRebuild);
            Array.Clear(_repRebuildDraws);
            for (int t = 0; t < 4; t++) _repCauses[t].Clear();
            _repTickReasons.Clear();
        }
    }
}
