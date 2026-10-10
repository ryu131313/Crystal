using Server.MirEnvir;
using Xunit.Abstractions;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Committed format fixtures (synthetic data only, safe to commit). Fixtures\v117 was written by the v117 code
/// (roadmap P3 pre-step) and holds BinaryFormatter-encoded Buff.Data blobs, so later format bumps can prove they
/// still load old files. Regenerate only deliberately: MIR2_WRITE_FIXTURE=1 while Envir.Version is the target version.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class FixtureTests
{
    internal static readonly DateTime FixtureClock = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _out;
    public FixtureTests(ITestOutputHelper output) => _out = output;

    internal static string FixtureDir(int version) =>
        Path.Combine(RepoRoot(), "Server.Persistence.Tests", "Fixtures", "v" + version);

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Write_fixture_for_current_version_when_requested()
    {
        if (Environment.GetEnvironmentVariable("MIR2_WRITE_FIXTURE") != "1")
        {
            _out.WriteLine("MIR2_WRITE_FIXTURE not set: nothing written.");
            return;
        }
        var target = FixtureDir(Envir.Version);
        var dir = NewTempFolder("fixture");
        if (Environment.GetEnvironmentVariable("MIR2_WRITE_FIXTURE_ORACLE_ONLY") == "1")
        {
            // Re-dump the oracle of the committed fixture files (e.g. after an Oracle format change), without rewriting them.
            File.Copy(Path.Combine(target, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
            File.Copy(Path.Combine(target, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
            InFolder(dir, () =>
            {
                Assert.True(LoadAll());
                File.WriteAllText(Path.Combine(target, "oracle.txt"), Oracle.Dump(Envir.Main));
                return 0;
            });
            Directory.Delete(dir, true);
            _out.WriteLine("oracle rewritten: " + target);
            return;
        }
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main, FixtureClock);
            SaveAll();
            Assert.True(LoadAll());
            SaveAll();
            Directory.CreateDirectory(target);
            File.Copy(Envir.DatabasePath, Path.Combine(target, "Server.MirDB"), true);
            File.Copy(Envir.AccountPath, Path.Combine(target, "Server.MirADB"), true);
            Assert.True(LoadAll());
            File.WriteAllText(Path.Combine(target, "oracle.txt"), Oracle.Dump(Envir.Main));
            return 0;
        });
        Directory.Delete(dir, true);
        _out.WriteLine("fixture written: " + target);
    }

    /// <summary>
    /// Roadmap P3 gate: the v117 fixture (BinaryFormatter Buff.Data blobs) loads on v118 code without BinaryFormatter.
    /// Everything except the Concentration buff's Data must be identical to the v117 oracle; the blobs are skipped
    /// (decision: skip and reset). The upgraded save is v118 and byte-stable.
    /// </summary>
    [Fact]
    public void V117_fixture_upgrades_to_current_version()
    {
        var src = FixtureDir(117);
        var dir = NewTempFolder("upgrade117");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        var expected = File.ReadAllText(Path.Combine(src, "oracle.txt")).Replace("\r\n", "\n");

        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "v117 fixture failed to load");
            Assert.Equal(117, Envir.LoadVersion);
            var upgraded = Oracle.Dump(Envir.Main);

            static string WithoutBuffData(string dump) => string.Join("\n",
                dump.Split('\n').Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\.Buffs\[\d+\]\.Data")));
            Assert.True(WithoutBuffData(expected) == WithoutBuffData(upgraded),
                "v117 -> v118 changed more than Buff.Data: " + Oracle.FirstDifference(WithoutBuffData(expected), WithoutBuffData(upgraded)));
            Assert.Contains("Buffs[0].Data.Count=2", expected);
            Assert.Contains("AccountList[1].Characters[1].Buffs[0].Data.Count=0", upgraded);
            Assert.Equal(0, CountBuffDataBlobs(Envir.Main));

            SaveAll();
            Assert.Equal(Envir.Version, BitConverter.ToInt32(File.ReadAllBytes(Envir.AccountPath), 0));
            Assert.Equal(Envir.Version, BitConverter.ToInt32(File.ReadAllBytes(Envir.DatabasePath), 0));
            var gen1 = ReadFiles();
            Assert.True(LoadAll());
            Assert.Equal(upgraded, Oracle.Dump(Envir.Main));
            SaveAll();
            var gen2 = ReadFiles();
            Assert.True(gen1.accounts.AsSpan().SequenceEqual(gen2.accounts));
            Assert.True(gen1.db.AsSpan().SequenceEqual(gen2.db));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// Roadmap P4 gate: the v118 fixture was written by the net8 build after a load+save. Loading and saving it on the
    /// current runtime must reproduce it byte for byte (no runtime-dependent serialisation).
    /// </summary>
    [Fact]
    public void V118_fixture_resaves_byte_identically_on_this_runtime()
    {
        var src = FixtureDir(118);
        var dir = NewTempFolder("resave118");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        InFolder(dir, () =>
        {
            Assert.True(LoadAll());
            Assert.Equal(118, Envir.LoadVersion);
            SaveAll();
            Assert.True(File.ReadAllBytes(Path.Combine(src, "Server.MirDB")).AsSpan().SequenceEqual(File.ReadAllBytes(Envir.DatabasePath)), "Server.MirDB differs from the net8-written fixture");
            Assert.True(File.ReadAllBytes(Path.Combine(src, "Server.MirADB")).AsSpan().SequenceEqual(File.ReadAllBytes(Envir.AccountPath)), "Server.MirADB differs from the net8-written fixture");
            return 0;
        });
        Directory.Delete(dir, true);
    }
}
