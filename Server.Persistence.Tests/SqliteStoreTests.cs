using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Roadmap P7 phase 7.2: the SQLite system-data store. The gate is the byte round trip: binary → SQLite → load → binary gives the
/// same Server.MirDB bytes. Plus loud decode, refusing empty files, the import, backups and refusing to re-import over a lost store.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class SqliteStoreTests
{
    /// <summary>Saves Main, pushes it through SQLite and back into Main, saves again: the two files must be byte-identical.</summary>
    private static void AssertBinarySqliteBinary(string dir)
    {
        InFolder(dir, () =>
        {
            Envir.Main.SaveDB();
            var before = File.ReadAllBytes(Envir.DatabasePath);

            var rows = SqliteWorldCodec.Encode(Envir.Main, (long)Envir.Main.RespawnTick.CurrentTickcounter);
            var db = Path.Combine(dir, "roundtrip.db");
            SqliteWorldStore.Create(db, rows, new Dictionary<string, object> { ["source"] = "test" });

            WorldRows back;
            using (var conn = SqliteWorldFile.Open(db, create: false)) back = SqliteWorldFile.ReadAll(conn);
            Assert.Null(SqliteWorldStore.FirstDifference(rows, back));

            lock (Envir.LoadLock)
            {
                Envir.Main.MagicInfoList.Clear();
                SqliteWorldCodec.Decode(back, Envir.Main);
            }

            Envir.Main.SaveDB();
            var after = File.ReadAllBytes(Envir.DatabasePath);
            Assert.True(before.SequenceEqual(after), "binary → SQLite → binary changed Server.MirDB (" + before.Length + " vs " + after.Length + " bytes)");
            return 0;
        });
    }

    [Theory]
    [InlineData(118)]
    [InlineData(119)]
    public void Fixture_round_trips_through_sqlite_byte_identically(int version)
    {
        var dir = NewTempFolder("p7-sqlite-v" + version);
        foreach (var f in new[] { "Server.MirDB", "Server.MirADB" })
            File.Copy(Path.Combine(FixtureTests.FixtureDir(version), f), Path.Combine(dir, f));
        InFolder(dir, () => { Assert.True(LoadAll()); return 0; });
        AssertBinarySqliteBinary(dir);
    }

    [Fact]
    public void Synthetic_world_with_every_child_kind_and_zero_stats_round_trips()
    {
        var dir = NewTempFolder("p7-sqlite-synthetic");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            var e = Envir.Main;
            e.ItemInfoList[0].Stats.Values[Stat.MaxAC] = 0;           // explicit zero entry: the binary format keeps it
            e.ItemInfoList[1].ToolTip = null;
            e.ItemInfoList[2].Name = "Pot \U0001F600";                  // non-BMP text
            e.MagicInfoList.Add(new MagicInfo { Spell = Spell.None, Name = "None" }); // Spell.None rows are kept (D3)
            var map = e.MapInfoList[0];
            map.MineZones.Add(new MineZone { Mine = 1, Location = new System.Drawing.Point(3, 4), Size = 5 });
            var conquest = new ConquestInfo { Index = ++e.ConquestIndex, Name = "Keep", MapIndex = map.Index };
            conquest.ConquestGuards.Add(new ConquestArcherInfo { Index = ++conquest.GuardIndex, Name = "G", RepairCost = 4000000000 });
            conquest.ConquestGates.Add(new ConquestGateInfo { Index = ++conquest.GateIndex, Name = "Gate" });
            conquest.ConquestWalls.Add(new ConquestWallInfo { Index = ++conquest.WallIndex, Name = "Wall" });
            conquest.ConquestSieges.Add(new ConquestSiegeInfo { Index = ++conquest.SiegeIndex, Name = "Siege" });
            conquest.ConquestFlags.Add(new ConquestFlagInfo { Index = ++conquest.FlagIndex, Name = "Flag" });
            conquest.ControlPoints.Add(new ConquestFlagInfo { Index = ++conquest.ControlPointIndex, Name = "Point" });
            e.ConquestInfoList.Add(conquest);
            e.RespawnTick.BaseSpawnRate = 7;
            e.RespawnTick.CurrentTickcounter = 123456789;
            e.RespawnTick.Respawn.Add(new RespawnTickOption { UserCount = 50, DelayLoss = 0.75 });
            SaveAll();
            return 0;
        });

        AssertBinarySqliteBinary(dir);
    }

    [Fact]
    public void A_bad_cell_fails_the_load_with_table_and_column()
    {
        var dir = NewTempFolder("p7-sqlite-corrupt");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            var db = Path.Combine(dir, "bad.db");
            SqliteWorldStore.Create(db, SqliteWorldCodec.Encode(Envir.Main, 0), null);

            using (var conn = SqliteWorldFile.Open(db, create: false))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE items SET \"Price\" = 99999999999 WHERE position = 0"; // out of range for uint
                cmd.ExecuteNonQuery();
            }

            WorldRows rows;
            using (var conn = SqliteWorldFile.Open(db, create: false)) rows = SqliteWorldFile.ReadAll(conn);
            var ex = Assert.Throws<InvalidDataException>(() => { lock (Envir.LoadLock) SqliteWorldCodec.Decode(rows, Envir.Edit); });
            Assert.Contains("items", ex.Message);
            Assert.Contains("Price", ex.Message);
            return 0;
        });
    }

    [Fact]
    public void An_unknown_column_fails_the_read()
    {
        var dir = NewTempFolder("p7-sqlite-column");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            var db = Path.Combine(dir, "extra.db");
            SqliteWorldStore.Create(db, SqliteWorldCodec.Encode(Envir.Main, 0), null);
            using var conn = SqliteWorldFile.Open(db, create: false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE monsters ADD COLUMN \"FromTheFuture\" INTEGER";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<InvalidDataException>(() => SqliteWorldFile.ReadAll(conn));
            Assert.Contains("FromTheFuture", ex.Message);
            return 0;
        });
    }

    [Fact]
    public void A_new_model_field_is_added_as_a_column_with_its_default()
    {
        var dir = NewTempFolder("p7-sqlite-migrate");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            var db = Path.Combine(dir, "old.db");
            SqliteWorldStore.Create(db, SqliteWorldCodec.Encode(Envir.Main, 0), null);

            // An older store: written before ItemInfo.Price and the dragon table existed.
            using (var conn = SqliteWorldFile.Open(db, create: false))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE items DROP COLUMN \"Price\"; DROP TABLE dragon";
                cmd.ExecuteNonQuery();
                Assert.Throws<InvalidDataException>(() => SqliteWorldFile.ReadAll(conn));

                var log = new List<string>();
                Assert.Equal(2, SqliteWorldFile.Migrate(conn, log.Add));
                Assert.Contains(log, l => l.Contains("items.Price"));
                var rows = SqliteWorldFile.ReadAll(conn);
                lock (Envir.LoadLock) SqliteWorldCodec.Decode(rows, Envir.Edit); // the new one-row dragon table got the fresh model's row
                Assert.All(Envir.Edit.ItemInfoList, i => Assert.Equal(new ItemInfo().Price, i.Price));
                Assert.Equal(new DragonInfo().MapFileName, Envir.Edit.DragonInfo.MapFileName);
                Assert.Equal(0, SqliteWorldFile.Migrate(conn, log.Add)); // idempotent
            }

            return 0;
        });
    }

    [Fact]
    public void Empty_or_missing_files_are_refused()
    {
        var dir = NewTempFolder("p7-sqlite-empty");
        var empty = Path.Combine(dir, "empty.db");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => SqliteWorldFile.Open(empty, create: false));
        Assert.Throws<FileNotFoundException>(() => SqliteWorldFile.Open(Path.Combine(dir, "missing.db"), create: false));
    }

    [Fact]
    public void Store_imports_saves_with_backups_and_refuses_to_reimport_over_a_lost_store()
    {
        var dir = NewTempFolder("p7-sqlite-store");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            SaveAll();
            var binarySha = SqliteWorldStore.Sha256(Envir.DatabasePath);
            Assert.True(LoadAll());
            var expected = Oracle.Dump(Envir.Main);

            var store = new SqliteWorldStore(privateWriter: true);
            try
            {
                // First load imports Server.MirDB (left untouched) and loads from the new store.
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(Envir.Main));
                WorldBinder.Bind(Envir.Main);
                Envir.Main.LoadAccounts(); // accounts bind to the new item objects, as at a world start
                Assert.True(File.Exists(SqliteWorldStore.DatabasePath));
                Assert.Equal(binarySha, SqliteWorldStore.Sha256(Envir.DatabasePath));
                Assert.Equal(expected, Oracle.Dump(Envir.Main));

                // An Edit save first copies the previous store to Back Up\Database (as a SQLite .bak, so retention prunes it).
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(Envir.Edit));
                Envir.Edit.ItemInfoList[0].Price = 4242;
                store.SaveDesign(Envir.Edit, null);
                Assert.Contains(Directory.GetFiles(Envir.DatabaseBackUpPath, "*.bak"), SqliteWorldFile.LooksLikeSqlite);

                // Main's save only posts the respawn counter: it never overwrites Edit's design data.
                Envir.Main.ItemInfoList[0].Price = 1;
                Envir.Main.RespawnTick.CurrentTickcounter = 777;
                store.SaveDesign(Envir.Main, null);
                Envir.Main.MagicInfoList.Clear();
                lock (Envir.LoadLock) Assert.True(store.LoadDesign(Envir.Main));
                Assert.Equal(4242u, Envir.Main.ItemInfoList[0].Price);
                Assert.Equal(777ul, Envir.Main.RespawnTick.CurrentTickcounter);
                Assert.Equal(Envir.Main.RespawnTick.BaseSpawnRate * 60000L, Envir.Main.RespawnTick.CurrentDelay); // 0 would tick every loop

                // A Server.MirDB newer than the store means the server ran on Binary since (a rollback): refuse the stale store.
                File.SetLastWriteTimeUtc(Envir.DatabasePath, DateTime.UtcNow.AddMinutes(5));
                lock (Envir.LoadLock) Assert.False(store.LoadDesign(Envir.Edit));
                File.SetLastWriteTimeUtc(Envir.DatabasePath, DateTime.UtcNow.AddDays(-1));
            }
            finally
            {
                store.Writer.Shutdown();
            }

            // The store is lost and a SQLite backup newer than Server.MirDB exists: refuse instead of re-importing the stale Server.MirDB.
            File.Delete(SqliteWorldStore.DatabasePath);
            var again = new SqliteWorldStore(privateWriter: true);
            try
            {
                lock (Envir.LoadLock) Assert.False(again.LoadDesign(Envir.Edit));
                Assert.False(File.Exists(SqliteWorldStore.DatabasePath));

                // After a rollback a binary build wrote Server.MirDB, newer than every SQLite backup: importing it again is allowed.
                File.SetLastWriteTimeUtc(Envir.DatabasePath, DateTime.UtcNow.AddMinutes(10));
                Envir.Edit.MagicInfoList.Clear();
                lock (Envir.LoadLock) Assert.True(again.LoadDesign(Envir.Edit));
                Assert.True(File.Exists(SqliteWorldStore.DatabasePath));
            }
            finally
            {
                again.Writer.Shutdown();
            }

            return 0;
        });
    }
}
