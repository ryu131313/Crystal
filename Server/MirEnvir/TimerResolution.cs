using System.Runtime.InteropServices;

namespace Server.MirEnvir
{
    /// <summary>
    /// Raises the Windows timer resolution to 1 ms while the world runs (roadmap P6), so the idle park's
    /// WaitOne(1) really waits ~1 ms instead of the default ~15.6 ms tick. No-op on other platforms.
    /// </summary>
    internal static class TimerResolution
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);

        private static int _active;

        public static void Begin()
        {
            if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref _active, 1) == 1) return;
            try { TimeBeginPeriod(1); }
            catch (Exception ex) { MessageQueue.Instance.Enqueue("[Loop] timeBeginPeriod(1) failed; the idle park will wait up to ~15 ms: " + ex.Message); }
        }

        public static void End()
        {
            if (!OperatingSystem.IsWindows() || Interlocked.Exchange(ref _active, 0) == 0) return;
            try { TimeEndPeriod(1); }
            catch { /* process is shutting the world down; nothing to report to */ }
        }
    }
}
