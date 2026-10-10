using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>Roadmap P5: backup retention policy (off by default; tested here as a pure function plus a temp folder run).</summary>
public class BackupRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0);

    [Fact]
    public void Keeps_recent_then_hourly_then_daily_and_never_the_newest()
    {
        var files = new List<(string, DateTime)>();
        for (var m = 0; m < 60 * 24 * 120; m += 5) files.Add(($"f{m}", Now.AddMinutes(-m))); // a backup every 5 minutes for 120 days

        var delete = BackupRetention.SelectForDeletion(files, Now, keepAllHours: 48, keepHourlyDays: 14, keepDailyDays: 90).ToHashSet();
        var kept = files.Where(f => !delete.Contains(f.Item1)).ToList();

        Assert.All(files.Where(f => Now - f.Item2 <= TimeSpan.FromHours(48)), f => Assert.DoesNotContain(f.Item1, delete));
        Assert.DoesNotContain(kept, f => Now - f.Item2 > TimeSpan.FromDays(90));
        var hourlyBand = kept.Where(f => Now - f.Item2 > TimeSpan.FromHours(48) && Now - f.Item2 <= TimeSpan.FromDays(14)).ToList();
        Assert.Equal(hourlyBand.Count, hourlyBand.Select(f => f.Item2.ToString("yyyyMMddHH")).Distinct().Count());
        var dailyBand = kept.Where(f => Now - f.Item2 > TimeSpan.FromDays(14)).ToList();
        Assert.Equal(dailyBand.Count, dailyBand.Select(f => f.Item2.Date).Distinct().Count());
        Assert.True(kept.Count < 1000, $"kept {kept.Count}");
    }

    [Fact]
    public void Only_server_named_backups_are_considered_and_the_newest_survives()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mir2-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Accounts 2025-01-01 00-00-00.bak"), "old");
        File.WriteAllText(Path.Combine(dir, "Accounts 2025-02-01 00-00-00.bak"), "newest");
        File.WriteAllText(Path.Combine(dir, "Manual copy.bak"), "operator");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");

        var deleted = BackupRetention.Prune(dir, Now, 48, 14, 90);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(dir, "Accounts 2025-01-01 00-00-00.bak")));
        Assert.True(File.Exists(Path.Combine(dir, "Accounts 2025-02-01 00-00-00.bak")));
        Assert.True(File.Exists(Path.Combine(dir, "Manual copy.bak")));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
        Directory.Delete(dir, true);
    }
}
