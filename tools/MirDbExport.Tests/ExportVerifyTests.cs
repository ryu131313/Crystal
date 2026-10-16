using System.Drawing;
using Microsoft.Data.Sqlite;
using Server.MirDatabase;
using Server.MirEnvir;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // Envir.Edit / LoadVersion are process statics

namespace MirDbExport.Tests;

public sealed class ExportVerifyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _src;

    public ExportVerifyTests()
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
        return Path.Combine(dir.FullName, "Server.Persistence.Tests", "Fixtures", "v119", "Server.MirDB"); // v119: GameShopItem deal prices are non-zero
    }

    private string Out(string name = "export.db") => Path.Combine(_dir, name);

    private static void Exec(string db, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Fixture_exports_and_verifies_with_zero_mismatches()
    {
        var fixtureHash = WorldLoader.Sha256Of(FixtureDb());
        var export = Exporter.Export(_src, Out());
        Assert.Empty(export.Errors);
        Assert.True(File.Exists(Out()));
        Assert.False(File.Exists(Out() + ".tmp"));
        Assert.Equal(19, export.Tables.Count);

        var verify = Verifier.Verify(_src, Out());
        Assert.Empty(verify.Mismatches);
        Assert.Equal(export.TotalRows, verify.TotalRows);
        Assert.Equal(fixtureHash, WorldLoader.Sha256Of(FixtureDb())); // the fixture is never touched
    }

    [Theory]
    [InlineData("UPDATE items SET \"Price\" = \"Price\" + 1 WHERE key = (SELECT min(key) FROM items)", "column Price")]
    [InlineData("UPDATE monsters SET \"Stat_HP\" = \"Stat_HP\" + 1 WHERE key = (SELECT min(key) FROM monsters)", "column Stat_HP")]
    [InlineData("UPDATE maps SET \"Light\" = \"Light\" + 1 WHERE key = (SELECT min(key) FROM maps)", "column Light")]
    [InlineData("UPDATE magic SET \"MultiplierBase\" = \"MultiplierBase\" + 0.5 WHERE key = (SELECT min(key) FROM magic)", "column MultiplierBase")]
    [InlineData("UPDATE gameshop SET \"Date\" = \"Date\" + 1 WHERE key = (SELECT min(key) FROM gameshop)", "column Date")]
    [InlineData("UPDATE gameshop SET \"DealGoldPrice\" = \"DealGoldPrice\" + 1 WHERE key = (SELECT min(key) FROM gameshop)", "column DealGoldPrice")]
    [InlineData("UPDATE gameshop SET \"DealCreditPrice\" = 0 WHERE key = (SELECT min(key) FROM gameshop)", "column DealCreditPrice")]
    [InlineData("UPDATE npcs SET \"Name\" = NULL WHERE key = (SELECT min(key) FROM npcs)", "column Name")]
    [InlineData("UPDATE mapmovements SET parent_index = parent_index + 1 WHERE key = (SELECT min(key) FROM mapmovements)", "column parent_index")]
    [InlineData("DELETE FROM maprespawns WHERE key = (SELECT min(key) FROM maprespawns)", "missing from the export")]
    [InlineData("UPDATE index_counters SET value = value + 1 WHERE name = 'ItemIndex'", "index_counters.ItemIndex")]
    [InlineData("UPDATE meta SET value = 'x' WHERE name = 'source_sha256'", "meta.source_sha256")]
    public void Verify_catches_a_corrupted_export(string corruption, string expectedFragment)
    {
        Assert.Empty(Exporter.Export(_src, Out()).Errors);
        Exec(Out(), corruption);

        var verify = Verifier.Verify(_src, Out());
        Assert.NotEmpty(verify.Mismatches);
        Assert.Contains(verify.Mismatches, m => m.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void V119_gameshop_deal_prices_are_exported()
    {
        Assert.Empty(Exporter.Export(_src, Out()).Errors);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Out(), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"DealGoldPrice\", \"DealCreditPrice\" FROM gameshop ORDER BY key";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(750L, r.GetInt64(0));
        Assert.Equal(8L, r.GetInt64(1));
    }

    [Fact]
    public void Missing_or_empty_source_is_refused_and_nothing_is_written()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<ToolException>(() => Exporter.Export(empty, Out()));
        Assert.False(File.Exists(Path.Combine(empty, "Server.MirDB"))); // LoadDB would have created one
        File.WriteAllBytes(Path.Combine(empty, "Server.MirDB"), Array.Empty<byte>());
        Assert.Throws<ToolException>(() => Exporter.Export(empty, Out()));
        Assert.False(File.Exists(Out()));
        Assert.False(File.Exists(Out() + ".tmp"));
    }

    /// <summary>The fixture has no conquests or mine zones: add one of everything in memory so every table carries rows.</summary>
    private static void Enrich()
    {
        var e = Envir.Edit;
        var map = e.MapInfoList[0];
        map.MineZones.Add(new MineZone { Location = new Point(3, 4), Size = 7, Mine = 2 });
        var c = new ConquestInfo
        {
            Index = ++e.ConquestIndex, Name = "Test Conquest", MapIndex = map.Index, PalaceIndex = e.MapInfoList[^1].Index, Location = new Point(5, 6),
            Size = 12, ExtraMaps = new List<int> { map.Index, 77 }, Type = ConquestType.Forced, Game = ConquestGame.KingOfHill, Friday = true,
            KingLocation = new Point(9, 9), KingSize = 3, WarLength = 90, StartHour = 20,
        };
        c.ConquestGuards.Add(new ConquestArcherInfo { Index = ++c.GuardIndex, Location = new Point(1, 2), MobIndex = 1, Name = "Guard", RepairCost = 4000000000 });
        c.ConquestGates.Add(new ConquestGateInfo { Index = ++c.GateIndex, Location = new Point(2, 3), MobIndex = 1, Name = "Gate", RepairCost = 10 });
        c.ConquestWalls.Add(new ConquestWallInfo { Index = ++c.WallIndex, MobIndex = 2, Name = "Wall", RepairCost = 11 });
        c.ConquestSieges.Add(new ConquestSiegeInfo { Index = ++c.SiegeIndex, MobIndex = 2, Name = "Siege", RepairCost = 12 });
        c.ConquestFlags.Add(new ConquestFlagInfo { Index = ++c.FlagIndex, Name = "Flag", FileName = "flag.txt" });
        c.ControlPoints.Add(new ConquestFlagInfo { Index = ++c.ControlPointIndex, Name = "CP", FileName = string.Empty });
        e.ConquestInfoList.Add(c);
        e.ItemInfoList[0].Stats[Stat.MaxDC] = -5;
        e.ItemInfoList[0].ToolTip = null;
        e.ItemInfoList[0].Name = "Unicode é中\U0001F600";
    }

    [Fact]
    public void Every_table_round_trips_through_export_and_verify()
    {
        WorldLoader.WithLoadedEdit(_src, source =>
        {
            Enrich();
            var export = Exporter.ExportLoaded(source, Out());
            Assert.Empty(export.Errors);
            Assert.All(export.Tables, t => Assert.True(t.Rows > 0, t.Table + " has no rows"));

            var verify = Verifier.VerifyLoaded(source, Out());
            Assert.Empty(verify.Mismatches);

            Exec(Out(), "UPDATE conquestguards SET \"RepairCost\" = 1 WHERE key = (SELECT min(key) FROM conquestguards)");
            Assert.Contains(Verifier.VerifyLoaded(source, Out()).Mismatches, m => m.Contains("conquestguards", StringComparison.Ordinal));
            return 0;
        });
    }

    [Fact]
    public void Content_without_a_column_fails_the_export_loudly()
    {
        WorldLoader.WithLoadedEdit(_src, source =>
        {
            Envir.Edit.ItemInfoList[0].Stats.Values[(Stat)254] = 3; // a Stat value the enum does not define
            var export = Exporter.ExportLoaded(source, Out());
            Assert.Contains(export.Errors, m => m.Contains("254=3", StringComparison.Ordinal));
            Assert.False(File.Exists(Out()));
            Assert.False(File.Exists(Out() + ".tmp"));
            Envir.Edit.ItemInfoList[0].Stats.Values.Remove((Stat)254);
            return 0;
        });
    }

    [Fact]
    public void Export_is_deterministic_apart_from_the_timestamp()
    {
        Assert.Empty(Exporter.Export(_src, Out("a.db")).Errors);
        Assert.Empty(Exporter.Export(_src, Out("b.db")).Errors);
        Assert.Equal(Dump(Out("a.db")), Dump(Out("b.db")));
    }

    private static string Dump(string db)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        var sb = new System.Text.StringBuilder();
        var tables = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { tables.Add(r.GetString(0)); sb.AppendLine(r.GetString(1)); }
        }

        foreach (var t in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM \"" + t + "\"" + (t == "meta" ? " WHERE name <> 'exported_utc'" : string.Empty) + " ORDER BY 1";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (var i = 0; i < r.FieldCount; i++) sb.Append(r.GetValue(i) is DBNull ? "NULL" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void Completeness_report_maps_every_registry_column()
    {
        var gaps = Completeness.Compute(out var problems);
        Assert.Empty(problems);
        Assert.Contains(gaps, g => g.Model == typeof(NPCInfo) && g.Member == "Colour");
    }
}
