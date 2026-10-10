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
}
