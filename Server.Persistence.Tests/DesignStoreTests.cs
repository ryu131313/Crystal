using Server.MirEnvir;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>Roadmap P7 phase 7.4: the design files in SystemData.db (the ini layer, then the text formats).</summary>
[Collection(PersistenceCollection.Name)]
public class DesignStoreTests
{
    /// <summary>Runs <paramref name="body"/> in a temp folder with an empty SystemData.db and the design layer pointed at it.</summary>
    internal static void WithStore(string tag, Action<string> body)
    {
        var dir = NewTempFolder(tag);
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            var db = Path.Combine(dir, "SystemData.db");
            SqliteWorldStore.Create(db, SqliteWorldCodec.Encode(Envir.Main, 0), null);
            var writer = new SqliteWriter(db, "design test writer");
            DesignStore.WriterOverride = writer;
            DesignStore.Disabled = false;
            try { body(dir); }
            finally
            {
                DesignStore.Disabled = true;
                DesignStore.WriterOverride = null;
                writer.Shutdown();
            }

            return 0;
        });
    }

    [Fact]
    public void Ini_files_round_trip_exactly_and_saves_go_to_the_store()
    {
        WithStore("p7-ini", dir =>
        {
            Directory.CreateDirectory("Configs");
            var lines = new[] { "; mines", "[Mine0]", "Name=Gold Mine", "Formula=a=b", "", "[Mine1]", "Name=Iron", "odd line without equals" };
            File.WriteAllLines("Configs/Mines.ini", lines);

            Assert.True(DesignIni.Exists("Configs/Mines.ini"));
            var reader = DesignIni.Open("Configs/Mines.ini");
            Assert.IsType<StoreIniReader>(reader);
            Assert.Equal("Gold Mine", reader.ReadString("Mine0", "Name", null, false));
            Assert.Equal("a", reader.ReadString("Mine0", "Formula", null, false)); // InIReader keeps only the text up to the next '='

            reader.Write("Mine1", "Name", "Copper");
            reader.Save();
            File.Delete("Configs/Mines.ini"); // the store is the record now

            var again = DesignIni.Open("Configs/Mines.ini");
            Assert.Equal("Copper", again.ReadString("Mine1", "Name", null, false));
            var stored = DesignStore.Run(conn => DesignIni.Read(conn, "Configs/Mines.ini")).Lines;
            Assert.Equal(lines.Select(l => l == "Name=Iron" ? "Name=Copper" : l), stored); // every other line exactly as it was

            DesignIni.Delete("Configs/Mines.ini");
            Assert.False(DesignIni.Exists("Configs/Mines.ini"));
            Assert.False(DesignIni.Exists("Configs/Never.ini")); // a file the store never saw and the disk doesn't have
        });
    }

    [Fact]
    public void Setup_ini_keeps_game_and_items_in_the_store_and_the_rest_in_the_file()
    {
        WithStore("p7-setup", dir =>
        {
            Directory.CreateDirectory("Configs");
            File.WriteAllLines("Configs/Setup.ini", new[] { "[General]", "X=1", "", "[Game]", "DropRate=2", "", "[Items]", "Y=3", "[Network]", "Port=7000" });

            var reader = new SetupIniReader("Configs/Setup.ini");
            reader.LoadDesignSections();
            Assert.Equal("2", reader.ReadString("Game", "DropRate", null, false));
            reader.Write("Game", "DropRate", 5);
            reader.Write("General", "X", 9);
            reader.Save();

            var file = File.ReadAllLines("Configs/Setup.ini");
            Assert.Contains("X=9", file);
            Assert.Contains("Port=7000", file);
            Assert.DoesNotContain("[Game]", file);
            Assert.DoesNotContain("[Items]", file);

            var next = new SetupIniReader("Configs/Setup.ini");
            next.LoadDesignSections();
            Assert.Equal(5, next.ReadInt32("Game", "DropRate", 0, false));
            Assert.Equal("3", next.ReadString("Items", "Y", null, false));
            Assert.Equal(9, next.ReadInt32("General", "X", 0, false));
        });
    }

    private static readonly Dictionary<string, string[]> TextFiles = new()
    {
        ["Envir/Drops/Hen.txt"] = new[] { ";Pots", "1/10 (HP)DrugSmall", "1/5 woodensword Q", "1/20 Gold 150", "1/3 GROUP*", "{", "  1/2 BaseDress(M)", "}",
                                          "1/7 NoSuchItem", "#INSERT [Shared/Common.txt]", "", "2/10 odd line" },
        ["Envir/Drops/Shared/Common.txt"] = new[] { "1/100 WoodenSword" },
        ["Envir/Drops/DragonItem.txt"] = new[] { "1/50 WoodenSword 2", "1/10 Gold 500 1" },
        ["Envir/Quests/FirstSteps.txt"] = new[] { "[@Description]", "Kill the hens.", "[@KillTasks]", "Hen 5 \"Hens killed\"", "Ghost 1",
                                                  "[@ItemTasks]", "(HP)DrugSmall 2", "[@FixedRewards]", "BaseDress 1", "[@ExpReward]", "100", "// end" },
        ["Envir/Recipe/WoodenSword.txt"] = new[] { "[Recipe]", "Amount 1", "Chance 90", "[Ingredients]", "(HP)DrugSmall 3", "  BaseDress(M) 1", "[Criteria]", "Level 5" },
        ["Envir/Routes/Loop.txt"] = new[] { "10,20", "11,21,500", " 12, 22", "bad" },
    };

    [Fact]
    public void Text_design_files_round_trip_exactly_and_follow_renames()
    {
        WithStore("p7-texts", dir =>
        {
            foreach (var (path, lines) in TextFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines);
            }

            DesignTexts.ResetCache();
            var rows = SqliteWorldCodec.Encode(Envir.Main, 0);
            DesignTexts.RefreshNames(rows);
            DesignTexts.EnsureLoaded();

            foreach (var (path, lines) in TextFiles)
            {
                Assert.True(DesignFiles.Exists(path), path);
                Assert.Equal(lines, DesignFiles.ReadAllLines(path)); // byte for byte, untyped lines included
            }

            // Typed lines carry Index links: rename the sword and every reference follows.
            var drops = DesignStore.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM drop_lines WHERE line_kind = 'item' AND item_index = 1";
                return (long)cmd.ExecuteScalar();
            });
            Assert.Equal(3, drops); // woodensword in Hen.txt, Common.txt and DragonItem.txt
            Envir.Main.ItemInfoList[0].Name = "Oak Sword";
            DesignTexts.RefreshNames(SqliteWorldCodec.Encode(Envir.Main, 0));
            Assert.Equal("1/5 OakSword Q", DesignFiles.ReadAllLines("Envir/Drops/Hen.txt")[2]);
            Assert.Equal("1/50 OakSword 2", DesignFiles.ReadAllLines("Envir/Drops/DragonItem.txt")[0]);
            Assert.Equal("1/7 NoSuchItem", DesignFiles.ReadAllLines("Envir/Drops/Hen.txt")[8]); // unresolved: kept as written
            Assert.Equal("Hen 5 \"Hens killed\"", DesignFiles.ReadAllLines("Envir/Quests/FirstSteps.txt")[3]);

            // A save under another casing replaces the stored file (one row), it doesn't add a second one.
            DesignFiles.WriteAllLines("Envir/Drops/HEN.TXT", new[] { "1/9 (HP)DrugSmall" });
            var henRows = DesignStore.Run(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM design_files WHERE kind = 'drops' AND lower(name) = 'hen.txt'";
                return (long)cmd.ExecuteScalar();
            });
            Assert.Equal(1, henRows);
            DesignTexts.ResetCache();
            Assert.Equal(new[] { "1/9 (HP)DrugSmall" }, DesignFiles.ReadAllLines("Envir/Drops/Hen.txt"));
            DesignFiles.WriteAllLines("Envir/Drops/Hen.txt", TextFiles["Envir/Drops/Hen.txt"]);
            Envir.Main.ItemInfoList[0].Name = "Oak Sword";

            // The store is the record: the files can go.
            Directory.Delete("Envir", true);
            Assert.Equal(new[] { Path.GetFullPath("Envir/Drops/DragonItem.txt"), Path.GetFullPath("Envir/Drops/Hen.txt") },
                DesignFiles.EnumerateFiles("Envir/Drops", "*.txt", SearchOption.TopDirectoryOnly).ToArray());
            Assert.Equal(3, DesignFiles.EnumerateFiles("Envir/Drops", "*.txt", SearchOption.AllDirectories).Count());

            Assert.Equal(1, DesignFiles.WriteAllLines("Envir/Routes/New.txt", new[] { "1,1", "2,2" }) + 1);
            DesignFiles.Move("Envir/Routes/New.txt", "Envir/Routes/Renamed.txt");
            Assert.False(DesignFiles.Exists("Envir/Routes/New.txt"));
            Assert.Equal(new[] { "1,1", "2,2" }, DesignFiles.ReadAllLines("Envir/Routes/Renamed.txt"));
            DesignFiles.Delete("Envir/Routes/Renamed.txt");
            Assert.False(DesignFiles.Exists("Envir/Routes/Renamed.txt"));

            // A fresh cache reads the same from the store.
            DesignTexts.ResetCache();
            Assert.Equal("1/5 OakSword Q", DesignFiles.ReadAllLines("Envir/Drops/Hen.txt")[2]);
            DesignTexts.ResetCache();
        });
    }
}
