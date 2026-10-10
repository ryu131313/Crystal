using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>Roadmap P5: guild/goods/conquest async saves swap files atomically and the guild refresh prunes safely.</summary>
[Collection(PersistenceCollection.Name)]
public class SideFileSaveTests
{
    private static bool WriteAndComplete(string path, byte[] data)
    {
        var fs = new FileStream(path + "n", FileMode.Create);
        var ar = fs.BeginWrite(data, 0, data.Length, null, null);
        ar.AsyncWaitHandle.WaitOne();
        return Envir.CompleteSideFileSave(fs, ar);
    }

    [Fact]
    public void Side_file_save_replaces_existing_and_creates_missing()
    {
        var dir = NewTempFolder("p5-side");
        var existing = Path.Combine(dir, "1.mcd");
        File.WriteAllBytes(existing, new byte[] { 9, 9 });

        Assert.True(WriteAndComplete(existing, new byte[] { 1, 2, 3 }));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existing));
        Assert.False(File.Exists(existing + "n"));

        var fresh = Path.Combine(dir, "2.mcd");
        Assert.True(WriteAndComplete(fresh, new byte[] { 4 }));
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(fresh));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Side_file_save_failure_is_reported_and_keeps_the_live_file()
    {
        var dir = NewTempFolder("p5-side-fail");
        var live = Path.Combine(dir, "3.msd");
        File.WriteAllBytes(live, new byte[] { 7 });
        var fs = new FileStream(live + "n", FileMode.Create);
        var ar = fs.BeginWrite(new byte[] { 1 }, 0, 1, null, null);
        ar.AsyncWaitHandle.WaitOne();
        var before = WorldErrors.Count;
        using (File.Open(live, FileMode.Open, FileAccess.Read, FileShare.None)) // lock the live file: the swap must fail
        {
            Assert.False(Envir.CompleteSideFileSave(fs, ar));
        }
        Assert.Equal(before + 1, WorldErrors.Count);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(live));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Guild_prune_removes_only_surplus_indexes()
    {
        var dir = NewTempFolder("p5-prune");
        InFolder(dir, () =>
        {
            Directory.CreateDirectory(Settings.GuildPath);
            foreach (var n in new[] { "0", "1", "2", "3", "notes" })
                File.WriteAllText(Path.Combine(Settings.GuildPath, n + ".mgd"), n);
            Envir.PruneSurplusGuildFiles(2);
            var left = Directory.GetFiles(Settings.GuildPath, "*.mgd").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "0", "1", "notes" }, left);
            return 0;
        });
        Directory.Delete(dir, true);
    }
}
