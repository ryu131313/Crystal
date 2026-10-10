using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Roadmap P2 gate: fault injection on the save/load paths. Each test builds the synthetic world in a temp folder,
/// breaks one step, and checks that no live file is lost, truncated or replaced by an empty database.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class CrashSafeSaveTests
{
    private static string NewWorld(string tag)
    {
        var dir = NewTempFolder(tag);
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            SaveAll();
            Assert.True(LoadAll());
            SaveAll(); // second save creates Back Up\Database (accounts backups come from the periodic save)
            return 0;
        });
        return dir;
    }

    private static void WaitForAccountSave()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Envir.Main.Saving && sw.ElapsedMilliseconds < 10000) Thread.Sleep(5);
        Assert.False(Envir.Main.Saving, "account save did not complete");
    }

    [Fact]
    public void Serialisation_failure_leaves_live_accounts_file_and_clears_Saving()
    {
        var dir = NewWorld("p2-serialise");
        InFolder(dir, () =>
        {
            var before = File.ReadAllBytes(Envir.AccountPath);
            var failuresBefore = Envir.AccountSaveFailures;
            var victim = Envir.Main.CharacterList[0];
            var name = victim.Name;
            victim.Name = null; // BinaryWriter.Write(null string) throws mid-serialisation

            Envir.Main.BeginSaveAccounts();

            Assert.False(Envir.Main.Saving);
            Assert.Equal(failuresBefore + 1, Envir.AccountSaveFailures);
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(Envir.AccountPath)), "live Server.MirADB changed");
            Assert.False(File.Exists(Envir.AccountPath + "n"), "no temp file should be written when serialisation fails");

            // the next periodic save works and backs up the previous file
            victim.Name = name;
            Envir.Main.BeginSaveAccounts();
            WaitForAccountSave();
            Assert.Equal(failuresBefore + 1, Envir.AccountSaveFailures);
            Assert.True(Directory.GetFiles(Envir.AccountsBackUpPath, "*.bak").Length >= 1, "previous accounts file not backed up");
            Assert.False(File.Exists(Envir.AccountPath + "n"));
            Envir.Main.LoadAccounts(); // still loadable
            Assert.Equal(2, Envir.Main.CharacterList.Count);
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_accounts_file_with_backups_is_refused_not_recreated_empty()
    {
        var dir = NewWorld("p2-missing-adb");
        InFolder(dir, () =>
        {
            Envir.Main.BeginSaveAccounts();
            WaitForAccountSave();               // creates Back Up\Accounts\*.bak
            File.Delete(Envir.AccountPath);     // simulate the old crash window / an operator mistake

            var ex = Assert.Throws<InvalidOperationException>(() => Envir.Main.LoadAccounts());
            Assert.Contains("Refusing to create an empty accounts database", ex.Message);
            Assert.False(File.Exists(Envir.AccountPath), "an empty Server.MirADB was created");
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_database_with_backups_is_refused_not_recreated_empty()
    {
        var dir = NewWorld("p2-missing-db");
        InFolder(dir, () =>
        {
            Assert.True(Directory.GetFiles(Envir.DatabaseBackUpPath, "*.bak").Length >= 1);
            File.Delete(Envir.DatabasePath);

            Assert.False(Envir.Edit.LoadDB());
            Assert.False(File.Exists(Envir.DatabasePath), "an empty Server.MirDB was created");
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Brand_new_server_still_creates_empty_files()
    {
        var dir = NewTempFolder("p2-new");
        InFolder(dir, () =>
        {
            ResetMain();
            Assert.True(Envir.Edit.LoadDB());
            Assert.True(File.Exists(Envir.DatabasePath));
            Envir.Main.LoadAccounts();
            Assert.True(File.Exists(Envir.AccountPath));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Newer_accounts_version_is_refused()
    {
        var dir = NewWorld("p2-newer");
        InFolder(dir, () =>
        {
            var bytes = File.ReadAllBytes(Envir.AccountPath);
            BitConverter.GetBytes(Envir.Version + 1).CopyTo(bytes, 0);
            File.WriteAllBytes(Envir.AccountPath, bytes);

            var ex = Assert.Throws<InvalidOperationException>(() => Envir.Main.LoadAccounts());
            Assert.Contains("outside the supported range", ex.Message);
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Database_save_failure_mid_write_leaves_live_database_intact()
    {
        var dir = NewWorld("p2-savedb");
        InFolder(dir, () =>
        {
            var before = File.ReadAllBytes(Envir.DatabasePath);
            var map = Envir.Main.MapInfoList[1];
            var title = map.Title;
            map.Title = null; // the second map's Save throws after the header and first map are written

            Assert.ThrowsAny<Exception>(() => Envir.Main.SaveDB());
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(Envir.DatabasePath)), "Server.MirDB was truncated or changed");

            map.Title = title;
            Envir.Main.SaveDB(); // recovers; the stale temp file is overwritten
            Assert.False(File.Exists(Envir.DatabasePath + "n"));
            Assert.True(Envir.Edit.LoadDB());
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Stale_temp_file_from_a_killed_save_is_ignored_and_replaced()
    {
        var dir = NewWorld("p2-stale");
        InFolder(dir, () =>
        {
            File.WriteAllBytes(Envir.AccountPath + "n", new byte[] { 1, 2, 3 }); // process killed mid-write
            Envir.Main.LoadAccounts();                                          // live file wins
            Assert.Equal(2, Envir.Main.CharacterList.Count);

            Envir.Main.BeginSaveAccounts();
            WaitForAccountSave();
            Assert.False(File.Exists(Envir.AccountPath + "n"));
            Envir.Main.LoadAccounts();
            Assert.Equal(2, Envir.Main.CharacterList.Count);
            return 0;
        });
        Directory.Delete(dir, true);
    }
}
