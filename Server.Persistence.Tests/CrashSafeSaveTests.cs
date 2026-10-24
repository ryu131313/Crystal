using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Roadmap P2 gate, on UserData.db since Part A of UserData-and-Quests-Plan.md: fault injection on the save/load paths. Each test builds
/// the synthetic world in a temp folder, breaks one step, and checks that no live data is lost, truncated or replaced by an empty database.
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
            Assert.True(LoadAll()); // imports Server.MirADB into UserData.db
            SaveAll();              // second save creates Back Up\Database
            return 0;
        });
        return dir;
    }

    [Fact]
    public void Serialisation_failure_leaves_the_store_and_clears_Saving()
    {
        var dir = NewWorld("p2-serialise");
        InFolder(dir, () =>
        {
            Assert.True(LoadAll());
            SaveUserData();
            var before = AccountsImage();
            var failuresBefore = Envir.AccountSaveFailures;
            var victim = Envir.Main.CharacterList[0];
            var name = victim.Name;
            victim.Name = null; // BinaryWriter.Write(null string) throws mid-serialisation

            Assert.Equal(Envir.UserDataSaveOutcome.Failed, Envir.Main.SaveUserData(false, allowBackup: false));

            Assert.False(Envir.Main.Saving);
            Assert.Equal(failuresBefore + 1, Envir.AccountSaveFailures);
            Assert.True(before.AsSpan().SequenceEqual(AccountsImage()), "UserData.db changed");

            // the next save works
            victim.Name = name;
            SaveUserData(full: false);
            Assert.Equal(failuresBefore + 1, Envir.AccountSaveFailures);
            Envir.Main.LoadAccounts(); // still loadable
            Assert.Equal(2, Envir.Main.CharacterList.Count);
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Missing_store_with_backups_is_refused_not_recreated_empty()
    {
        var dir = NewWorld("p2-missing-adb");
        InFolder(dir, () =>
        {
            Assert.True(LoadAll());
            Envir.UserStore.BackupTo(Path.Combine(Envir.AccountsBackUpPath, "Accounts 2099-01-01 00-00-00.bak"));
            Envir.UserStore.Writer.Run(w => { w.CloseConnection(); return 0; });
            File.Delete(Envir.UserDataPath); // an operator mistake or a lost disk

            var ex = Assert.Throws<InvalidOperationException>(() => Envir.Main.LoadAccounts());
            Assert.Contains("Refusing to create an empty user database", ex.Message);
            Assert.False(File.Exists(Envir.UserDataPath), "an empty UserData.db was created");
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
            Assert.True(Envir.Main.LoadDB());
            LoadUserData();
            Assert.True(File.Exists(Envir.UserDataPath));
            Assert.False(File.Exists(Envir.AccountPath));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Newer_user_data_version_is_refused()
    {
        var dir = NewWorld("p2-newer");
        InFolder(dir, () =>
        {
            var head = Envir.UserStore.Writer.Run(w => Server.MirEnvir.Storage.UserDataFile.ReadWorld(w.Connection)).Head;
            BitConverter.GetBytes(Envir.Version + 1).CopyTo(head, 0);
            Envir.UserStore.Writer.Run(w =>
            {
                using var cmd = w.Connection.CreateCommand();
                cmd.CommandText = "UPDATE world SET head = $h; UPDATE meta SET value = $v WHERE key = 'envir_version'";
                cmd.Parameters.AddWithValue("$h", head);
                cmd.Parameters.AddWithValue("$v", (long)Envir.Version + 1);
                return cmd.ExecuteNonQuery();
            });

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
    public void A_tmp_left_by_an_interrupted_import_is_replaced_by_the_next_import()
    {
        var dir = NewTempFolder("p2-stale");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            SaveAll(); // Server.MirADB
            File.WriteAllBytes(Envir.UserDataPath + ".tmp", new byte[] { 1, 2, 3 }); // process killed before the move

            Assert.True(LoadAll());
            Assert.True(File.Exists(Envir.UserDataPath));
            Assert.False(File.Exists(Envir.UserDataPath + ".tmp"));
            Assert.Equal(2, Envir.Main.CharacterList.Count);
            return 0;
        });
        Directory.Delete(dir, true);
    }
}
