using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>
/// The legacy guild re-index (Guilds\Reindex\ + "done" marker): nothing writes guild files since UserData.db, but the import run still
/// finishes an interrupted re-index of the legacy folder before it reads the files.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class SideFileSaveTests
{
    /// <summary>What the legacy server's re-index staged: every file, then the marker with the count.</summary>
    private static void Stage(string guildPath, byte[][] files, bool marker)
    {
        var stage = Path.Combine(guildPath, "Reindex");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        for (var i = 0; i < files.Length; i++) File.WriteAllBytes(Path.Combine(stage, i + ".mgd"), files[i]);
        if (marker) File.WriteAllText(Path.Combine(stage, "done"), files.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Guild_reindex_rolls_forward_after_its_marker_and_is_discarded_without_it()
    {
        var dir = PersistenceHarness.NewTempFolder("guild-reindex");
        foreach (var n in new[] { "0", "1", "2" }) File.WriteAllText(Path.Combine(dir, n + ".mgd"), "old" + n);

        // Staged but not swapped (a crash right after the marker): the next call finishes it.
        Stage(dir, new[] { new byte[] { 10 }, new byte[] { 11 } }, marker: true);
        Envir.CompleteGuildReindex(dir);
        Assert.Equal(new byte[] { 10 }, File.ReadAllBytes(Path.Combine(dir, "0.mgd")));
        Assert.Equal(new byte[] { 11 }, File.ReadAllBytes(Path.Combine(dir, "1.mgd")));
        Assert.False(File.Exists(Path.Combine(dir, "2.mgd")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Reindex")));

        // Staging without its marker (a crash while staging): discarded, the live files stay.
        Stage(dir, new[] { new byte[] { 20 } }, marker: false);
        Envir.CompleteGuildReindex(dir);
        Assert.Equal(new byte[] { 10 }, File.ReadAllBytes(Path.Combine(dir, "0.mgd")));
        Assert.True(File.Exists(Path.Combine(dir, "1.mgd")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Reindex")));
        Directory.Delete(dir, true);
    }
}
