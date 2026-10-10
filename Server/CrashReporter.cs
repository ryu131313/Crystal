using log4net;

namespace Server
{
    /// <summary>
    /// Process-wide crash capture (roadmap P2). Observe-only: it logs and writes a crash file, then lets the runtime do
    /// what it would have done anyway (an unhandled exception still terminates the process; an unobserved task exception
    /// stays unobserved). Install once, early in Main, by any host (WinForms today, a headless host later).
    /// WinForms' Application.ThreadException is deliberately NOT hooked: attaching it replaces the default dialog and lets the
    /// UI thread continue, which is a behaviour change, not observation.
    /// </summary>
    public static class CrashReporter
    {
        private static int _installed;

        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Report("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception, e.ExceptionObject);

            TaskScheduler.UnobservedTaskException += (s, e) =>
                Report("TaskScheduler.UnobservedTaskException", e.Exception, null);
        }

        /// <summary>Writes Logs\Crash\crash-*.txt next to the exe and the Server log; never throws.</summary>
        public static void Report(string source, Exception ex, object raw)
        {
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {source}{Environment.NewLine}{(object)ex ?? raw}{Environment.NewLine}";
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "Logs", "Crash");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"), text);
            }
            catch
            {
                // Last-chance handler: nowhere left to report a failure to write the crash file.
            }

            try
            {
                Logger.GetLogger().Fatal(text);
                LogManager.Flush(3000);
            }
            catch
            {
                // Logging is not configured or already shut down; the crash file above is the record.
            }
        }
    }
}
