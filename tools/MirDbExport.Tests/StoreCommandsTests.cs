using Server;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace MirDbExport.Tests;

/// <summary>Roadmap P7 phase 7.2: import / export-binary / roundtrip, and shadow mode end to end.</summary>
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
    }

    [Fact]
    public void Shadow_mirror_of_an_edit_save_round_trips()
    {
        var old = Environment.CurrentDirectory;
        var shadow = Settings.DatabaseShadow;
        try
        {
            Directory.SetCurrentDirectory(_src);
            Settings.DatabaseShadow = true;
            if (Envir.Main.BuffInfoList.Count == 0)
                foreach (var b in Server.MirDatabase.BuffInfo.Load()) Envir.Main.BuffInfoList.Add(b);
            Envir.Edit.MagicInfoList.Clear();
            Assert.True(Envir.Edit.LoadDB());
            Envir.Edit.ItemInfoList[0].Price += 1; // a real edit
            Envir.Edit.SaveDB();
            ShadowMirror.Flush();
            Assert.Equal(0, ShadowMirror.Failures);
        }
        finally
        {
            Settings.DatabaseShadow = shadow;
            Directory.SetCurrentDirectory(old);
        }

        Assert.True(File.Exists(Path.Combine(_src, "SystemData.shadow.db")));
        Assert.Equal(0, StoreCommands.Roundtrip(_src, null));
    }
}
