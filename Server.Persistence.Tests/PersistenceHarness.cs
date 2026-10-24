using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;

namespace Server.Persistence.Tests;

/// <summary>
/// Runs Envir's real load/save code against a folder. Envir resolves every data path relative
/// to the current directory and keeps its state in statics (Envir.Main / Envir.Edit,
/// LoadVersion), so every use runs inside <see cref="InFolder"/> and the tests that use it share
/// one non-parallel collection. Each folder gets its own UserData.db writer (pinned to that folder),
/// shut down when the body returns.
/// </summary>
internal static class PersistenceHarness
{
    public static readonly object Gate = new();

    private static readonly BinaryWorldStore Binary = new();

    public static T InFolder<T>(string dir, Func<T> body)
    {
        lock (Gate)
        {
            // These tests pin the binary Server.MirDB format; the server itself runs on SQLite since roadmap P7 phase 7.3.
            Envir.Store = Binary;
            var old = Environment.CurrentDirectory;
            Directory.SetCurrentDirectory(dir);
            var store = new UserDataStore(Path.Combine(dir, "UserData.db"));
            Envir.UserStore = store;
            // Nothing of an earlier folder's user data may be saved into this one.
            Envir.Main.MarkUserDataUnloaded();
            Envir.Main.ResetUserDataAfterRestore();
            try { return body(); }
            finally
            {
                store.Shutdown();
                Envir.Main.MarkUserDataUnloaded();
                Directory.SetCurrentDirectory(old);
            }
        }
    }

    public static string NewTempFolder(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mir2-persistence", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void EnsureBuffInfo()
    {
        if (Envir.Main.BuffInfoList.Count > 0) return;
        foreach (var b in BuffInfo.Load()) Envir.Main.BuffInfoList.Add(b);
    }

    /// <summary>
    /// Loads system data (Edit first, as SMain does, then Main) and the user data into Main in the server's order: goods bytes, accounts,
    /// guilds, conquests, then the first-start import when the user data came from Server.MirADB (or an empty store for a new world).
    /// </summary>
    public static bool LoadAll()
    {
        EnsureBuffInfo();
        if (!Envir.Edit.LoadDB()) return false;
        if (!Envir.Main.LoadDB()) return false;
        LoadUserData();
        return true;
    }

    /// <summary>The user-data part of <see cref="LoadAll"/> (StartEnvir's goods step + StartNetwork's loads).</summary>
    public static void LoadUserData()
    {
        var e = Envir.Main;
        e.LoadGoodsBytes();
        e.LoadAccounts();
        e.LoadGuilds();
        e.LoadConquests();
        e.CompleteUserDataImport();
    }

    /// <summary>
    /// Saves system data, and the user data: a full save into UserData.db (waited for) once it is loaded, otherwise the in-memory world is
    /// written as a Server.MirADB file (a freshly populated world), which the next <see cref="LoadAll"/> imports.
    /// </summary>
    public static void SaveAll()
    {
        Envir.Main.SaveDB();
        SaveUserData();
    }

    public static void SaveUserData(bool full = true)
    {
        var e = Envir.Main;
        if (e.UserDataLoaded && e.LoadedUserDataSource == Envir.UserDataSource.Store)
        {
            Assert.Equal(Envir.UserDataSaveOutcome.Posted, e.SaveUserData(full, allowBackup: false));
            Envir.UserStore.WaitIdle();
            Assert.False(e.Saving, "the UserData save did not complete");
            return;
        }

        WriteLegacyAccounts(e);
    }

    /// <summary>Writes the world in memory as Server.MirADB (the legacy layout the import reads).</summary>
    public static void WriteLegacyAccounts(Envir e)
    {
        using var stream = File.Create(Envir.AccountPath);
        using var writer = new BinaryWriter(stream, BufferBinaryWriterTests.SaveEncoding, leaveOpen: true);
        e.SaveAccounts(writer);
    }

    /// <summary>The user data as Server.MirADB bytes: rebuilt from UserData.db when it exists (the rollback image), else the file.</summary>
    public static byte[] AccountsImage()
    {
        if (!Envir.UserStore.Exists) return File.ReadAllBytes(Envir.AccountPath);
        return Envir.UserStore.Writer.Run(w => UserDataFile.LegacyAccountsImage(UserDataFile.ReadAll(w.Connection)));
    }

    /// <summary>Every row of UserData.db (through the folder's writer).</summary>
    public static UserDataRows ReadStore() => Envir.UserStore.Writer.Run(w => UserDataFile.ReadAll(w.Connection));

    public static (byte[] db, byte[] accounts) ReadFiles() =>
        (File.ReadAllBytes(Envir.DatabasePath), AccountsImage());

    /// <summary>Clears the Main lists Populate writes into (LoadDB/LoadAccounts clear the rest themselves).</summary>
    public static void ResetMain()
    {
        var e = Envir.Main;
        e.MapInfoList.Clear(); e.ItemInfoList.Clear(); e.MonsterInfoList.Clear(); e.NPCInfoList.Clear();
        e.QuestInfoList.Clear(); e.MagicInfoList.Clear(); e.GameShopList.Clear(); e.ConquestInfoList.Clear();
        e.AccountList.Clear(); e.CharacterList.Clear(); e.HeroList.Clear(); e.Auctions.Clear();
        e.GTMapList.Clear(); e.GameshopLog.Clear(); e.MarketSales.Clear();
        e.GuildList.Clear(); e.Guilds.Clear(); e.ConquestList.Clear(); e.Conquests.Clear();
    }

    /// <summary>Concentration buffs carrying Buff.Data (the BinaryFormatter-encoded field, roadmap P3).</summary>
    public static int CountBuffDataBlobs(Envir e)
    {
        var prop = typeof(Buff).GetProperty("Data", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        int Count(Buff b) => (prop?.GetValue(b) as System.Collections.IDictionary)?.Count ?? 0;
        int n = 0;
        foreach (var c in e.CharacterList) foreach (var b in c.Buffs) if (Count(b) > 0) n++;
        foreach (var h in e.HeroList) foreach (var b in h.Buffs) if (Count(b) > 0) n++;
        return n;
    }
}
