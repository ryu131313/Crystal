using System.Runtime.CompilerServices;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using Server.MirObjects;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// UserData-and-Quests-Plan.md Part A phases 1-2: user data in UserData.db. The import of Server.MirADB and the side folders, the
/// one-transaction save, failed commits, pending deletes against a restore, the import's count checks, fail-closed starts, backups
/// and the rollback export.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class UserDataStoreTests
{
    private const int SpawnedNpc = 1, UnspawnedNpc = 7;

    /// <summary>The synthetic world as Server.MirADB, plus one guild, two goods files and a conquest file whose conquest is not loaded.</summary>
    private static void WriteLegacyWorld(string dir, bool withSideFiles = true)
    {
        EnsureBuffInfo();
        ResetMain();
        var e = Envir.Main;
        SyntheticWorld.Populate(e);
        if (withSideFiles)
        {
            var guild = NewGuild(e, "Knights", e.CharacterList[0]);
            e.GuildList.Add(guild);
        }
        SaveAll(); // Server.MirDB + Server.MirADB (nothing is loaded yet, so the harness writes the legacy file)
        if (!withSideFiles) return;

        Directory.CreateDirectory(Settings.GuildPath);
        File.WriteAllBytes(Path.Combine(Settings.GuildPath, "0.mgd"), Bytes(w => e.GuildList[0].Save(w)));
        Directory.CreateDirectory(Settings.GoodsPath);
        File.WriteAllBytes(Path.Combine(Settings.GoodsPath, SpawnedNpc + ".msd"), GoodsFile(e, 3, 21));
        File.WriteAllBytes(Path.Combine(Settings.GoodsPath, UnspawnedNpc + ".msd"), GoodsFile(e, 1, 22));
        Directory.CreateDirectory(Settings.ConquestsPath);
        File.WriteAllBytes(Path.Combine(Settings.ConquestsPath, "42.mcd"), new byte[] { 1, 2, 3, 4, 5 }); // no ConquestInfo 42: kept unparsed
        e.GuildList.Clear();
    }

    internal static GuildInfo NewGuild(Envir e, string name, CharacterInfo leader)
    {
        var owner = (PlayerObject)RuntimeHelpers.GetUninitializedObject(typeof(PlayerObject));
        owner.Info = leader;
        return new GuildInfo(owner, name) { GuildIndex = ++e.NextGuildID };
    }

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, BufferBinaryWriterTests.SaveEncoding, true)) write(w);
        return ms.ToArray();
    }

    /// <summary>A goods file in the current format (9999 header) with one item.</summary>
    private static byte[] GoodsFile(Envir e, int itemIndex, ulong uniqueId) => Bytes(w =>
    {
        w.Write(9999);
        w.Write(Envir.Version);
        w.Write(Envir.CustomVersion);
        w.Write(1);
        SyntheticWorld.Item(e, e.ItemInfoList[itemIndex - 1], uniqueId).Save(w);
    });

    /// <summary>Puts a spawned NPC on a map (without map files) whose goods are parsed from Envir's goods bytes, as NPCScript.LoadGoods does.</summary>
    private static NPCObject SpawnNpc(Envir e, int npcIndex)
    {
        var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
        map.NPCs = new List<NPCObject>();
        var npc = (NPCObject)RuntimeHelpers.GetUninitializedObject(typeof(NPCObject));
        npc.Info = new NPCInfo { Index = npcIndex };
        npc.UsedGoods = new List<UserItem>();
        Assert.True(e.TryGetGoods(npcIndex, out var data));
        using (var r = new BinaryReader(new MemoryStream(data)))
        {
            Assert.Equal(9999, r.ReadInt32());
            var version = r.ReadInt32();
            var custom = r.ReadInt32();
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var item = new UserItem(r, version, custom);
                Assert.True(e.BindItem(item));
                npc.UsedGoods.Add(item);
            }
        }
        map.NPCs.Add(npc);
        e.MapList.Add(map);
        return npc;
    }

    private static UserDataRow Row(List<UserDataRow> rows, int key)
    {
        foreach (var r in rows) if (r.Key == key) return r;
        throw new Xunit.Sdk.XunitException("no row " + key);
    }

    private static void Exec(string sql) => Envir.UserStore.Writer.Run(w =>
    {
        using var cmd = w.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    });

    private static void CloseStore() => Envir.UserStore.Writer.Run(w => { w.CloseConnection(); return 0; });

    [Fact]
    public void Import_save_load_gives_an_equal_oracle_and_goods_equal_to_the_msd_files()
    {
        var dir = NewTempFolder("ud-import");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                var spawnedFile = File.ReadAllBytes(Path.Combine(Settings.GoodsPath, SpawnedNpc + ".msd"));
                var unspawnedFile = File.ReadAllBytes(Path.Combine(Settings.GoodsPath, UnspawnedNpc + ".msd"));


                Assert.True(LoadAll()); // the import run
                var e = Envir.Main;
                Assert.Equal(Envir.UserDataSource.Store, e.LoadedUserDataSource);
                Assert.True(File.Exists(Envir.UserDataPath));
                Assert.False(File.Exists(Envir.AccountPath), "Server.MirADB was not renamed");
                Assert.True(File.Exists(Envir.AccountPath + ".imported"));
                Assert.True(Directory.Exists(Settings.GuildPath + ".imported"));
                Assert.True(Directory.Exists(Settings.GoodsPath + ".imported"));
                Assert.True(Directory.Exists(Settings.ConquestsPath + ".imported"));
                Assert.Single(e.GuildList);

                var imported = ReadStore();
                Assert.Equal(e.AccountList.Count, imported.Accounts.Count);
                Assert.Equal(unspawnedFile, Row(imported.Goods, UnspawnedNpc).Data);   // goods rows come from the files
                Assert.Equal(spawnedFile, Row(imported.Goods, SpawnedNpc).Data);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Row(imported.Conquests, 42).Data); // an unloaded conquest is kept, unparsed

                // The import run's save: the NPC spawned from the file's goods is serialised again; every row is rewritten (first save).
                SpawnNpc(e, SpawnedNpc);
                var dump1 = Oracle.Dump(e);
                SaveUserData(full: false);
                e.MapList.Clear();
                var saved = ReadStore();
                Assert.Equal(spawnedFile, Row(saved.Goods, SpawnedNpc).Data);
                Assert.Equal(unspawnedFile, Row(saved.Goods, UnspawnedNpc).Data);
                Assert.Equal(Row(imported.Guilds, e.GuildList[0].GuildIndex).Data, Row(saved.Guilds, e.GuildList[0].GuildIndex).Data); // the load resets members to offline, so not the file bytes
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Row(saved.Conquests, 42).Data);

                Assert.True(LoadAll()); // from UserData.db
                Assert.Equal(Envir.UserDataSource.Store, e.LoadedUserDataSource);
                var dump2 = Oracle.Dump(e);
                Assert.True(dump1 == dump2, "oracle differs after import -> save -> load: " + Oracle.FirstDifference(dump1, dump2));
                Assert.Single(e.GuildList);
                Assert.True(e.TryGetGoods(SpawnedNpc, out var goods));
                Assert.Equal(spawnedFile, goods);

                var image1 = AccountsImage();
                SaveAll();
                Assert.True(LoadAll());
                Assert.True(image1.AsSpan().SequenceEqual(AccountsImage()), "the user data is not byte-stable");
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_failed_commit_with_a_pending_delete_is_retried_by_the_next_save()
    {
        var dir = NewTempFolder("ud-commitfail");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                Assert.True(LoadAll());
                SaveUserData();
                var e = Envir.Main;
                var victim = e.AccountList[e.AccountList.Count - 1];
                var victimIndex = victim.Index;

                e.AccountList.Remove(victim);
                foreach (var c in victim.Characters) e.CharacterList.Remove(c);
                e.MarkAccountDeleted(victimIndex);
                e.CharacterList[0].Level = 77;
                var expected = Oracle.Dump(e);

                var failures = Envir.AccountSaveFailures;
                Exec("CREATE TRIGGER fail_save BEFORE INSERT ON world BEGIN SELECT RAISE(ABORT, 'injected failure'); END");
                Assert.Equal(Envir.UserDataSaveOutcome.Posted, e.SaveUserData(false, allowBackup: false));
                Envir.UserStore.WaitIdle();
                Assert.False(e.Saving);
                Assert.Equal(failures + 1, Envir.AccountSaveFailures);
                var afterFailure = ReadStore();
                Assert.Contains(afterFailure.Accounts, r => r.Key == victimIndex); // rolled back: the delete did not happen

                Exec("DROP TRIGGER fail_save");
                SaveUserData(full: false); // forced full by the failure, with the delete again
                Assert.Equal(failures + 1, Envir.AccountSaveFailures);
                var after = ReadStore();
                Assert.DoesNotContain(after.Accounts, r => r.Key == victimIndex);

                Assert.True(LoadAll());
                var reloaded = Oracle.Dump(e);
                Assert.True(expected == reloaded, "reload after a failed and a retried save differs: " + Oracle.FirstDifference(expected, reloaded));
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_offline_delete_then_a_restore_does_not_remove_a_restored_row(bool savedBeforeRestore)
    {
        var dir = NewTempFolder("ud-restore-delete");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                Assert.True(LoadAll());
                SaveUserData();
                var e = Envir.Main;
                var backup = Path.Combine(dir, "Back Up", "Accounts", "Manual test.bak");
                Envir.UserStore.BackupTo(backup);
                Assert.True(SqliteWorldFile.LooksLikeSqlite(backup));

                var victim = e.AccountList[e.AccountList.Count - 1];
                e.AccountList.Remove(victim);
                e.MarkAccountDeleted(victim.Index);
                if (savedBeforeRestore)
                {
                    Assert.True(e.SaveAccounts()); // the offline (world stopped) save drains the delete
                    Assert.DoesNotContain(ReadStore().Accounts, r => r.Key == victim.Index);
                }

                // What the web admin restore does (BackupRoutes.Restore, accounts kind).
                Envir.UserStore.Restore(backup, Path.Combine(dir, "Back Up", "Accounts", "Pre-restore test.bak"));
                e.ResetUserDataAfterRestore();
                e.LoadAccounts();
                Assert.Contains(e.AccountList, a => a.Index == victim.Index);

                Assert.True(e.SaveAccounts()); // an offline save after the restore
                SaveUserData();                 // and a running-world save
                Assert.Contains(ReadStore().Accounts, r => r.Key == victim.Index);
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_import_stops_on_a_count_mismatch_and_renames_nothing()
    {
        var dir = NewTempFolder("ud-mismatch");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                // A guild file the loader does not read (beyond the saved guild count) and a goods file not named by an NPC index.
                File.Copy(Path.Combine(Settings.GuildPath, "0.mgd"), Path.Combine(Settings.GuildPath, "5.mgd"));
                File.WriteAllBytes(Path.Combine(Settings.GoodsPath, "abc.msd"), new byte[] { 0 });

                Assert.True(Envir.Edit.LoadDB());
                Assert.True(Envir.Main.LoadDB());
                var ex = Assert.Throws<InvalidOperationException>(() => LoadUserData());
                Assert.Contains("import of Server.MirADB stopped", ex.Message);
                Assert.False(File.Exists(Envir.UserDataPath), "UserData.db was created");
                Assert.False(File.Exists(Envir.UserDataPath + ".tmp"));
                Assert.True(File.Exists(Envir.AccountPath), "Server.MirADB was renamed");
                Assert.True(Directory.Exists(Settings.GuildPath));
                Assert.True(Directory.Exists(Settings.GoodsPath));

                // The operator fixes the files: the import runs.
                File.Delete(Path.Combine(Settings.GuildPath, "5.mgd"));
                File.Delete(Path.Combine(Settings.GoodsPath, "abc.msd"));
                LoadUserData();
                Assert.True(File.Exists(Envir.UserDataPath));
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_duplicate_guild_index_stops_the_import()
    {
        var dir = NewTempFolder("ud-dupguild");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                // The head says one guild: rewrite Server.MirADB with two guilds sharing an index, and both files.
                var e = Envir.Main;
                var bytes = File.ReadAllBytes(Envir.AccountPath);
                BitConverter.GetBytes(2).CopyTo(bytes, 28); // the guild count after NextHeroID
                File.WriteAllBytes(Envir.AccountPath, bytes);
                File.Copy(Path.Combine(Settings.GuildPath, "0.mgd"), Path.Combine(Settings.GuildPath, "1.mgd"));

                Assert.True(Envir.Edit.LoadDB());
                Assert.True(e.LoadDB());
                var ex = Assert.Throws<InvalidOperationException>(() => LoadUserData());
                Assert.Contains("import of Server.MirADB stopped", ex.Message);
                Assert.False(File.Exists(Envir.UserDataPath));
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_store_with_evidence_is_refused_and_a_new_server_gets_an_empty_store()
    {
        var dir = NewTempFolder("ud-evidence");
        try
        {
            InFolder(dir, () =>
            {
                ResetMain();
                Assert.True(LoadAll()); // a brand-new server: no Server.MirADB, no evidence
                Assert.True(File.Exists(Envir.UserDataPath));
                Assert.Equal("new", Envir.UserStore.Writer.Run(w => (string)UserDataFile.ReadMeta(w.Connection, "source")));

                CloseStore();
                File.Delete(Envir.UserDataPath);
                File.WriteAllText(Envir.AccountPath + ".imported", "x"); // something was imported once
                var ex = Assert.Throws<InvalidOperationException>(() => Envir.Main.LoadAccounts());
                Assert.Contains("Refusing to create an empty user database", ex.Message);
                Assert.False(File.Exists(Envir.UserDataPath));
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Server_MirADB_older_than_a_UserData_backup_is_not_imported()
    {
        var dir = NewTempFolder("ud-newerbackup");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir, withSideFiles: false);
                Assert.True(LoadAll());
                Envir.UserStore.BackupTo(Path.Combine(dir, "Back Up", "Accounts", "Accounts 2099-01-01 00-00-00.bak"));
                CloseStore();
                File.Move(Envir.UserDataPath, Path.Combine(dir, "lost.db"));
                File.Move(Envir.AccountPath + ".imported", Envir.AccountPath);
                File.SetLastWriteTimeUtc(Envir.AccountPath, DateTime.UtcNow.AddDays(-1));

                var ex = Assert.Throws<InvalidOperationException>(() => Envir.Main.LoadAccounts());
                Assert.Contains("newer than Server.MirADB", ex.Message);
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_store_without_a_world_row_or_with_a_version_mismatch_is_refused()
    {
        var dir = NewTempFolder("ud-failclosed");
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir, withSideFiles: false);
                Assert.True(LoadAll());

                Exec("UPDATE meta SET value = 1 WHERE key = 'envir_version'");
                var ex = Assert.ThrowsAny<Exception>(() => Envir.Main.LoadAccounts());
                Assert.Contains("differs from the version in world.head", ex.ToString());

                Exec("UPDATE meta SET value = " + Envir.Version + " WHERE key = 'envir_version'");
                Exec("DELETE FROM world");
                ex = Assert.ThrowsAny<Exception>(() => Envir.Main.LoadAccounts());
                Assert.Contains("no world row", ex.ToString());
                Assert.False(Envir.Main.UserDataLoaded);
                Assert.False(Envir.Main.SaveAccounts(), "an offline save ran without loaded data");
                return 0;
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_skipped_save_touches_nothing_and_a_periodic_backup_is_a_restorable_sqlite_copy()
    {
        var dir = NewTempFolder("ud-backup");
        var oldMinutes = Settings.AccountBackupMinutes;
        try
        {
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                Assert.True(LoadAll());
                SaveUserData();
                var e = Envir.Main;
                var before = AccountsImage();

                e.Saving = true; // stands in for a job that has not finished
                try
                {
                    e.CharacterList[0].Level = 55;
                    Assert.Equal(Envir.UserDataSaveOutcome.Skipped, e.SaveUserData(true, allowBackup: true));
                    Assert.True(before.AsSpan().SequenceEqual(AccountsImage()));
                }
                finally { e.Saving = false; }

                Settings.AccountBackupMinutes = 5;
                Assert.Equal(Envir.UserDataSaveOutcome.Posted, e.SaveUserData(false, allowBackup: true));
                Envir.UserStore.WaitIdle();
                var backups = Directory.GetFiles(Envir.AccountsBackUpPath, "Accounts *.bak");
                Assert.Single(backups);
                Assert.True(BackupRetention.TryParseTimestamp(Path.GetFileName(backups[0]), out _));
                Assert.True(SqliteWorldFile.LooksLikeSqlite(backups[0]));
                UserDataStore.CheckCandidate(backups[0]);

                var withLevel55 = Oracle.Dump(e);
                e.CharacterList[0].Level = 1;
                SaveUserData();
                Envir.UserStore.Restore(backups[0], null);
                e.ResetUserDataAfterRestore();
                e.LoadAccounts();
                Assert.Equal(55, e.CharacterList[0].Level);
                Assert.True(withLevel55 == Oracle.Dump(e), "restored backup differs from the saved state");

                // a legacy binary file is not a restore candidate
                var legacy = Path.Combine(dir, "legacy.bak");
                File.WriteAllBytes(legacy, before);
                Assert.Throws<InvalidDataException>(() => UserDataStore.CheckCandidate(legacy));
                return 0;
            });
        }
        finally
        {
            Settings.AccountBackupMinutes = oldMinutes;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_rollback_export_writes_files_an_older_build_reads()
    {
        var dir = NewTempFolder("ud-export");
        var back = NewTempFolder("ud-export-back");
        try
        {
            string expected = null;
            byte[] image = null;
            InFolder(dir, () =>
            {
                WriteLegacyWorld(dir);
                Assert.True(LoadAll());
                SaveUserData();
                expected = Oracle.Dump(Envir.Main);
                image = AccountsImage();
                CloseStore();
                return 0;
            });

            var result = UserDataExport.WriteLegacy(Path.Combine(dir, "UserData.db"), back, force: false);
            Assert.Equal(1, result.Guilds);
            Assert.Equal(2, result.Goods);
            Assert.Equal(1, result.Conquests);
            Assert.Equal(image, File.ReadAllBytes(Path.Combine(back, "Server.MirADB")));
            Assert.True(File.Exists(Path.Combine(back, "Guilds", "0.mgd")));
            Assert.True(File.Exists(Path.Combine(back, "Envir", "Goods", UnspawnedNpc + ".msd")));
            Assert.True(File.Exists(Path.Combine(back, "Conquests", "42.mcd")));
            Assert.Throws<InvalidOperationException>(() => UserDataExport.WriteLegacy(Path.Combine(dir, "UserData.db"), back, force: false));

            // The exported files import again into the same world.
            File.Copy(Path.Combine(dir, "Server.MirDB"), Path.Combine(back, "Server.MirDB"));
            InFolder(back, () =>
            {
                Assert.True(LoadAll());
                Assert.Equal(Envir.UserDataSource.Store, Envir.Main.LoadedUserDataSource);
                var dump = Oracle.Dump(Envir.Main);
                Assert.True(expected == dump, "export -> import differs: " + Oracle.FirstDifference(expected, dump));
                return 0;
            });
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(back, true);
        }
    }

    [Fact]
    public void Block_flag_write_matches_one_write_per_flag_for_any_bool_pattern()
    {
        var rng = new Random(1999);
        foreach (var length in new[] { 0, 1, 31, 32, 33, 1999, 5000 })
        {
            var flags = new bool[length];
            for (var i = 0; i < length; i++)
            {
                var b = (byte)rng.Next(4); // 0, 1 and non-canonical true values (2, 3)
                if (rng.Next(10) == 0) b = 255;
                flags[i] = Unsafe.As<byte, bool>(ref b);
            }

            var expected = new MemoryStream();
            using (var w = new BinaryWriter(expected, System.Text.Encoding.UTF8, true))
                for (var i = 0; i < length; i++) w.Write(flags[i]);
            var actual = new MemoryStream();
            using (var w = new BinaryWriter(actual, System.Text.Encoding.UTF8, true))
                CharacterInfo.WriteFlags(w, flags);

            Assert.Equal(expected.ToArray(), actual.ToArray());

            // the account save's own writer takes the in-place path (after a prefix, from a tiny array so it grows)
            var direct = new BufferBinaryWriter(BufferBinaryWriterTests.SaveEncoding, 1);
            direct.Write(77);
            CharacterInfo.WriteFlags(direct, flags);
            Assert.Equal(new byte[] { 77, 0, 0, 0 }.Concat(expected.ToArray()).ToArray(), direct.Buffer.AsSpan(0, direct.Length).ToArray());
        }
    }
}
