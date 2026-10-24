using System.Text;

namespace MirDbExport.Tests;

/// <summary>Roadmap P7: import / export-binary / roundtrip, and the design files (check-files / import-files / export-files).</summary>
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
        return Path.Combine(dir.FullName, "Server.Persistence.Tests", "Fixtures", "v121", "Server.MirDB"); // the current Envir.Version: export-binary writes it
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

    [Fact]
    public void Design_files_import_check_and_export_byte_for_byte()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["Configs/Mines.ini"] = Encoding.UTF8.GetBytes("[Mine0]\r\nName=Gold\r\n"),
            ["Configs/Setup.ini"] = Encoding.UTF8.GetBytes("[General]\r\nX=1\r\n[Game]\r\nDropRate=2\r\n"),
            ["Envir/Drops/Hen.txt"] = Encoding.UTF8.GetBytes(";pots\n1/10 NoSuchItem\n1/20 Gold 150"),
            ["Envir/Routes/Loop.txt"] = Encoding.UTF8.GetBytes("10,20\r\n11,21,500\r\n"),
            ["Envir/Quests/Q.txt"] = Encoding.UTF8.GetBytes("[@Description]\nHello\n"),
        };
        foreach (var (rel, bytes) in files)
        {
            var path = Path.Combine(_src, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }

        Assert.Equal(0, DesignCommands.CheckFiles(_src));

        var store = Path.Combine(_dir, "SystemData.db");
        Assert.Equal(0, StoreCommands.Import(_src, store));
        Assert.Equal(0, DesignCommands.ImportFiles(store, _src));
        var outDir = Path.Combine(_dir, "files");
        Assert.Equal(0, DesignCommands.ExportFiles(store, outDir, force: false));

        foreach (var (rel, bytes) in files)
        {
            var exported = Path.Combine(outDir, rel);
            if (rel == "Configs/Setup.ini")
                Assert.Equal(new[] { "[Game]", "DropRate=2" }, File.ReadAllLines(exported).Where(l => l.Length > 0)); // only [Game]/[Items] are in the store
            else if (rel.EndsWith(".ini", StringComparison.Ordinal))
                Assert.Equal(Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries), File.ReadAllLines(exported));
            else Assert.Equal(bytes, File.ReadAllBytes(exported)); // text files: the original bytes, line endings included
        }
    }
}
