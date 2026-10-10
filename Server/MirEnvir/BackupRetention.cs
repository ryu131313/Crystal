using System.Globalization;
using System.Text.RegularExpressions;

namespace Server.MirEnvir
{
    /// <summary>
    /// Backup retention for Back Up\Accounts and Back Up\Database (roadmap P5). OFF by default ([Backup] RetentionEnabled):
    /// it deletes files, so the operator opts in. Policy: keep everything younger than KeepAllHours; then the newest file
    /// per hour until KeepHourlyDays; then the newest file per day until KeepDailyDays; delete older files.
    /// Only files named "&lt;Accounts|Database&gt; yyyy-MM-dd HH-mm-ss.bak" (written by the server) are ever considered,
    /// and the newest file is always kept. Runs off the game thread (files only).
    /// </summary>
    public static class BackupRetention
    {
        private static readonly Regex Name = new Regex(@"^(Accounts|Database) (\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})\.bak$", RegexOptions.CultureInvariant);
        private static int _running;

        public static bool TryParseTimestamp(string fileName, out DateTime stamp)
        {
            stamp = default;
            var m = Name.Match(fileName);
            return m.Success && DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
        }

        /// <summary>Pure policy: which of these (name, timestamp) entries should be deleted at <paramref name="now"/>.</summary>
        public static List<string> SelectForDeletion(IEnumerable<(string Name, DateTime Stamp)> files, DateTime now, int keepAllHours, int keepHourlyDays, int keepDailyDays)
        {
            var sorted = files.OrderByDescending(f => f.Stamp).ToList();
            var delete = new List<string>();
            var hourly = new HashSet<DateTime>();
            var daily = new HashSet<DateTime>();
            for (var i = 0; i < sorted.Count; i++)
            {
                var f = sorted[i];
                var age = now - f.Stamp;
                if (i == 0 || age <= TimeSpan.FromHours(keepAllHours)) continue;            // newest, or recent: keep
                if (age <= TimeSpan.FromDays(keepHourlyDays))
                {
                    if (hourly.Add(new DateTime(f.Stamp.Year, f.Stamp.Month, f.Stamp.Day, f.Stamp.Hour, 0, 0))) continue;
                }
                else if (age <= TimeSpan.FromDays(keepDailyDays))
                {
                    if (daily.Add(f.Stamp.Date)) continue;
                }
                delete.Add(f.Name);
            }
            return delete;
        }

        /// <summary>Starts a background prune of both backup folders when enabled; at most one runs at a time.</summary>
        public static void RunInBackground()
        {
            if (!Settings.BackupRetentionEnabled) return;
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            var allHours = Settings.BackupKeepAllHours;
            var hourlyDays = Settings.BackupKeepHourlyDays;
            var dailyDays = Settings.BackupKeepDailyDays;
            var dirs = new[] { Path.GetFullPath(Envir.AccountsBackUpPath), Path.GetFullPath(Envir.DatabaseBackUpPath) };
            Task.Run(() =>
            {
                try
                {
                    foreach (var dir in dirs) Prune(dir, DateTime.Now, allHours, hourlyDays, dailyDays);
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("backup retention", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                }
            });
        }

        public static int Prune(string dir, DateTime now, int keepAllHours, int keepHourlyDays, int keepDailyDays)
        {
            if (!Directory.Exists(dir)) return 0;
            var entries = new List<(string, DateTime)>();
            foreach (var path in Directory.GetFiles(dir, "*.bak"))
                if (TryParseTimestamp(Path.GetFileName(path), out var stamp)) entries.Add((path, stamp));

            var deleted = 0;
            foreach (var path in SelectForDeletion(entries, now, keepAllHours, keepHourlyDays, keepDailyDays))
            {
                File.Delete(path);
                deleted++;
            }
            if (deleted > 0) MessageQueue.Instance.Enqueue($"[Backup] Retention removed {deleted} old backup(s) from {dir}.");
            return deleted;
        }
    }
}
