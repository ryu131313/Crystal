using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>
/// Runs Envir's real load/save code against a folder. Envir resolves every data path relative
/// to the current directory and keeps its state in statics (Envir.Main / Envir.Edit,
/// LoadVersion), so every use runs inside <see cref="InFolder"/> and the tests that use it share
/// one non-parallel collection.
/// </summary>
internal static class PersistenceHarness
{
    public static readonly object Gate = new();

    private static readonly Server.MirEnvir.Storage.BinaryWorldStore Binary = new();

    public static T InFolder<T>(string dir, Func<T> body)
    {
        lock (Gate)
        {
            // These tests pin the binary Server.MirDB format; the server itself runs on SQLite since roadmap P7 phase 7.3.
            Envir.Store = Binary;
            var old = Environment.CurrentDirectory;
            Directory.SetCurrentDirectory(dir);
            try { return body(); }
            finally { Directory.SetCurrentDirectory(old); }
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

    /// <summary>Loads system data (Edit first, as SMain does, then Main) and accounts into Main.</summary>
    public static bool LoadAll()
    {
        EnsureBuffInfo();
        if (!Envir.Edit.LoadDB()) return false;
        if (!Envir.Main.LoadDB()) return false;
        Envir.Main.LoadAccounts();
        return true;
    }

    public static void SaveAll()
    {
        Envir.Main.SaveDB();
        Envir.Main.SaveAccounts();
    }

    public static (byte[] db, byte[] accounts) ReadFiles() =>
        (File.ReadAllBytes(Envir.DatabasePath), File.ReadAllBytes(Envir.AccountPath));

    /// <summary>Clears the Main lists Populate writes into (LoadDB/LoadAccounts clear the rest themselves).</summary>
    public static void ResetMain()
    {
        var e = Envir.Main;
        e.MapInfoList.Clear(); e.ItemInfoList.Clear(); e.MonsterInfoList.Clear(); e.NPCInfoList.Clear();
        e.QuestInfoList.Clear(); e.MagicInfoList.Clear(); e.GameShopList.Clear(); e.ConquestInfoList.Clear();
        e.AccountList.Clear(); e.CharacterList.Clear(); e.HeroList.Clear(); e.Auctions.Clear();
        e.GTMapList.Clear(); e.GameshopLog.Clear(); e.MarketSales.Clear();
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
