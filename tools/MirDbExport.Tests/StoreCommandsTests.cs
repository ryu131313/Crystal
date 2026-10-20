namespace MirDbExport.Tests;

/// <summary>Roadmap P7: import / export-binary / roundtrip.</summary>
public sealed class StoreCommandsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _src;

    public StoreCommandsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mirdbexport-tests", Guid.NewGuid().ToString("N")[..8]);
        _src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(_src);
        File.Copy(FixtureDb(), Path.Combine(_src, "Server.MirDB"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string FixtureDb()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("repo root not found");
        return Path.Combine(dir.FullName, "Server.Persistence.Tests", "Fixtures", "v119", "Server.MirDB");
    }

    [Fact]
    public void Import_then_export_binary_gives_back_the_same_file()
    {
        var store = Path.Combine(_dir, "SystemData.db");
        Assert.Equal(0, StoreCommands.Import(_src, store));
        Assert.Throws<ToolException>(() => StoreCommands.Import(_src, store)); // never overwrites a store

        var outDir = Path.Combine(_dir, "out");
        Assert.Equal(0, StoreCommands.ExportBinary(store, outDir, force: false));
        Assert.Equal(File.ReadAllBytes(FixtureDb()), File.ReadAllBytes(Path.Combine(outDir, "Server.MirDB")));
        Assert.Throws<ToolException>(() => StoreCommands.ExportBinary(store, outDir, force: false));

        // The import recorded the source's hash: roundtrip finds it and gets the same bytes back.
        File.Copy(store, Path.Combine(_src, "SystemData.db"));
        Assert.Equal(0, StoreCommands.Roundtrip(_src, null));
    }
}
