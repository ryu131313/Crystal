using System.Runtime.CompilerServices;
using Server.MirDatabase;
using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// P9-10 gate: the off-thread account save ([Optimisation] AccountSaveOffThread) writes the same Server.MirADB bytes as the
/// legacy periodic save and the synchronous shutdown save, keeps the failure handling (counted, logged, Saving cleared, live
/// file untouched), and the block flag write equals one Write(bool) per flag.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class AccountSaveOffThreadTests
{
    private static void WaitForAccountSave()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Envir.Main.Saving && sw.ElapsedMilliseconds < 10000) Thread.Sleep(5);
        Assert.False(Envir.Main.Saving, "account save did not complete");
    }

    private static byte[] PeriodicSave(bool offThread)
    {
        var old = Settings.AccountSaveOffThread;
        Settings.AccountSaveOffThread = offThread;
        try
        {
            Envir.Main.BeginSaveAccounts();
            WaitForAccountSave();
        }
        finally { Settings.AccountSaveOffThread = old; }
        Assert.False(File.Exists(Envir.AccountPath + "n"), "temp file left behind");
        return File.ReadAllBytes(Envir.AccountPath);
    }

    [Fact]
    public void Off_thread_save_is_byte_identical_to_legacy_and_sync_saves()
    {
        var dir = NewTempFolder("p910-identical");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            SaveAll();
            Assert.True(LoadAll());

            // flags in every position class (vector body and scalar tail)
            var rng = new Random(910);
            foreach (var c in Envir.Main.CharacterList)
                for (var i = 0; i < c.Flags.Length; i++) c.Flags[i] = rng.Next(3) == 0;

            var failures = Envir.AccountSaveFailures;
            var legacy = PeriodicSave(offThread: false);
            var offThread = PeriodicSave(offThread: true);
            var offThreadAgain = PeriodicSave(offThread: true); // the reused buffer is reset, not appended to
            Envir.Main.SaveAccounts();
            var sync = File.ReadAllBytes(Envir.AccountPath);

            Assert.Equal(failures, Envir.AccountSaveFailures);
            Assert.True(legacy.AsSpan().SequenceEqual(offThread), "off-thread save differs from the legacy save");
            Assert.True(offThread.AsSpan().SequenceEqual(offThreadAgain), "second off-thread save differs");
            Assert.True(legacy.AsSpan().SequenceEqual(sync), "periodic save differs from the sync save");
            Assert.True(Directory.GetFiles(Envir.AccountsBackUpPath, "*.bak").Length >= 1, "previous accounts file not backed up");

            Envir.Main.LoadAccounts();
            Assert.Equal(2, Envir.Main.CharacterList.Count);
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Off_thread_write_failure_is_counted_clears_Saving_and_keeps_live_file()
    {
        var dir = NewTempFolder("p910-writefail");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            SaveAll();
            Assert.True(LoadAll());

            var before = PeriodicSave(offThread: true); // the post-load bytes
            var failures = Envir.AccountSaveFailures;
            Directory.CreateDirectory(Envir.AccountPath + "n"); // the temp file cannot be created

            var old = Settings.AccountSaveOffThread;
            Settings.AccountSaveOffThread = true;
            try
            {
                Envir.Main.BeginSaveAccounts();
                WaitForAccountSave();
            }
            finally { Settings.AccountSaveOffThread = old; }

            Assert.Equal(failures + 1, Envir.AccountSaveFailures);
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(Envir.AccountPath)), "live Server.MirADB changed");

            Directory.Delete(Envir.AccountPath + "n");
            var after = PeriodicSave(offThread: true); // recovers on the next save
            Assert.Equal(failures + 1, Envir.AccountSaveFailures);
            Assert.True(before.AsSpan().SequenceEqual(after));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Shutdown_save_waits_for_an_in_flight_write_and_a_periodic_save_is_skipped_meanwhile()
    {
        var dir = NewTempFolder("p910-wait");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            SaveAll();
            Assert.True(LoadAll());
            var expected = PeriodicSave(offThread: true);
            var stamp = File.GetLastWriteTimeUtc(Envir.AccountPath);

            Envir.Main.Saving = true; // stands in for a pool-thread write that has not finished
            try
            {
                Envir.Main.BeginSaveAccounts(); // skipped: must not touch the buffer or the files
                Assert.False(File.Exists(Envir.AccountPath + "n"));
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(Envir.AccountPath));

                var cwd = Environment.CurrentDirectory;
                var shutdown = Task.Run(() => { Directory.SetCurrentDirectory(cwd); Envir.Main.SaveAccounts(); });
                Assert.False(shutdown.Wait(300), "SaveAccounts did not wait for Saving");
                Envir.Main.Saving = false;
                Assert.True(shutdown.Wait(5000), "SaveAccounts did not finish after Saving cleared");
            }
            finally { Envir.Main.Saving = false; }

            Assert.Equal(expected, File.ReadAllBytes(Envir.AccountPath));
            return 0;
        });
        Directory.Delete(dir, true);
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
        }
    }
}
