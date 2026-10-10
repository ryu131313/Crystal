using Server.MirEnvir;
using Xunit.Abstractions;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class PersistenceCollection { public const string Name = "Persistence (cwd + Envir statics)"; }

/// <summary>
/// Roadmap P1 harness. Gate for any persistence change (P2 crash-safe saves, P3 v118, P7 SQLite):
///  - byte harness: once data has been loaded and saved, the next load+save is byte-identical;
///  - oracle: two consecutive loads produce the same semantic dump.
/// The first load is allowed to change data (it drops zero-character accounts, unbindable items
/// and orphan quests, and archives stale characters); the drop report shows what it did.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class RoundTripTests
{
    private readonly ITestOutputHelper _out;
    public RoundTripTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Synthetic_world_round_trips_and_is_stable()
    {
        var dir = NewTempFolder("synthetic");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            int accountsWritten = Envir.Main.AccountList.Count;
            SaveAll();

            Assert.True(LoadAll(), "first load failed");
            var report = DropReport(accountsWritten);
            Assert.Equal(accountsWritten - SyntheticWorld.ZeroCharacterAccountsBeyondFirst, Envir.Main.AccountList.Count);
            Assert.Equal(1, CountBuffDataBlobs(Envir.Main));

            AssertStable(report);
            return 0;
        });
        Directory.Delete(dir, recursive: true); // only reached when the asserts passed
    }

    /// <summary>
    /// Same harness on a COPY of real data. Set MIR2_PERSISTENCE_DATA to a folder holding
    /// Server.MirDB and Server.MirADB (optionally Configs\). The source folder is never written.
    /// Passes trivially (and says so) when the variable is unset.
    /// </summary>
    [Fact]
    public void Real_data_copy_round_trips_and_is_stable()
    {
        var src = Environment.GetEnvironmentVariable("MIR2_PERSISTENCE_DATA");
        if (string.IsNullOrEmpty(src))
        {
            _out.WriteLine("MIR2_PERSISTENCE_DATA not set: real-data harness not run.");
            return;
        }
        var srcFull = Path.GetFullPath(src);
        Assert.True(File.Exists(Path.Combine(srcFull, "Server.MirDB")), "Server.MirDB not found in " + srcFull);
        Assert.True(File.Exists(Path.Combine(srcFull, "Server.MirADB")), "Server.MirADB not found in " + srcFull);

        var dir = NewTempFolder("realdata");
        File.Copy(Path.Combine(srcFull, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(srcFull, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        var cfg = Path.Combine(srcFull, "Configs");
        if (Directory.Exists(cfg)) CopyDir(cfg, Path.Combine(dir, "Configs"));

        InFolder(dir, () =>
        {
            var origAccounts = new FileInfo(Envir.AccountPath).Length;
            Assert.True(LoadAll(), "first load failed (version range? see Logs)");
            _out.WriteLine($"Envir.LoadVersion={Envir.LoadVersion}  original Server.MirADB={origAccounts} bytes");
            var report = DropReport(-1);
            AssertStable(report);
            return 0;
        });
        // Holds a copy of live data: remove it unless asked to keep it for inspection.
        if (Environment.GetEnvironmentVariable("MIR2_KEEP_PERSISTENCE_TEMP") == "1") _out.WriteLine("temp copy kept: " + dir);
        else Directory.Delete(dir, recursive: true);
    }

    private string DropReport(int accountsWritten)
    {
        var e = Envir.Main;
        var archived = Directory.Exists(Envir.ArchivePath) ? Directory.GetFiles(Envir.ArchivePath, "*.MirCA").Length : 0;
        var lines = new[]
        {
            $"accounts loaded: {e.AccountList.Count}" + (accountsWritten >= 0 ? $" (written {accountsWritten})" : ""),
            $"characters loaded: {e.CharacterList.Count}, heroes: {e.HeroList.Count}, auctions: {e.Auctions.Count}",
            $"characters archived on load: {archived}",
            $"Concentration Buff.Data blobs (BinaryFormatter, roadmap P3): {CountBuffDataBlobs(e)}",
            $"items/monsters/maps/npcs/quests: {e.ItemInfoList.Count}/{e.MonsterInfoList.Count}/{e.MapInfoList.Count}/{e.NPCInfoList.Count}/{e.QuestInfoList.Count}",
        };
        var report = string.Join(Environment.NewLine, lines);
        _out.WriteLine(report);
        File.WriteAllText("drop-report.txt", report);
        return report;
    }

    private void AssertStable(string report)
    {
        var dump1 = Oracle.Dump(Envir.Main);
        File.WriteAllText("oracle-1.txt", dump1);
        SaveAll();
        var gen2 = ReadFiles();

        Assert.True(LoadAll(), "second load failed");
        var dump2 = Oracle.Dump(Envir.Main);
        SaveAll();
        var gen3 = ReadFiles();

        Assert.True(dump1 == dump2, "oracle differs between consecutive loads: " + Oracle.FirstDifference(dump1, dump2));
        Assert.True(gen2.db.AsSpan().SequenceEqual(gen3.db), $"Server.MirDB not byte-stable ({gen2.db.Length} vs {gen3.db.Length})");
        Assert.True(gen2.accounts.AsSpan().SequenceEqual(gen3.accounts), $"Server.MirADB not byte-stable ({gen2.accounts.Length} vs {gen3.accounts.Length})");
        _out.WriteLine($"stable: MirDB {gen3.db.Length} bytes, MirADB {gen3.accounts.Length} bytes, oracle {dump2.Length} chars");
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
