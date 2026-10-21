using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>Drops-Tables-Plan phase 1: drops as tables, the drop_lines conversion, and the game loading rows like it loaded text.</summary>
[Collection(PersistenceCollection.Name)]
public class DropTablesTests
{
    /// <summary>A phase 7.4 drop set: every line kind, nested groups, includes, a shared DropPath, the special lists and odd lines.</summary>
    private static readonly Dictionary<string, string[]> Drops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hen.txt"] = new[]
        {
            ";Pots", "1/10 (HP)DrugSmall", "1/5 woodensword Q", "1/20 Gold 150", "1/2 Gold 20000 LV1", "1/3 GROUP*", "{", "  1/2 BaseDress(M)",
            "  1/4 GROUP^", "  {", "    1/9 WoodenSword", "  }", "  ;inner note", "  1/8 NoSuchItem", "}", "1/7 NoSuchItem", "#INSERT [Shared/Common.txt]",
            "", "   ", "2/10 WoodenSword", "  1/3  WoodenSword  LV2 ", "1/1RedDagger Q", "  ;indented comment",
        },
        ["Shared/Common.txt"] = new[] { "1/100 WoodenSword", ";#INSERT [Shared/Deep.txt]" }, // an #INSERT inside a comment still includes
        ["Shared/Deep.txt"] = new[] { "1/50 BaseDress(M)" },
        ["Packs/Shared.txt"] = new[] { "1/30 (HP)DrugSmall", "1/31 Gold 7" },
        ["DragonItem.txt"] = new[] { "1/50 WoodenSword 2", "1/10 Gold 500 1", ";x", "1/5 NoSuchItem 3", "1/6 BaseDress(M) 2" },
        ["00Fishing.txt"] = new[] { "1/40 (HP)DrugSmall" },
        ["01Fishing.txt"] = new[] { "1/41 WoodenSword" },
        ["00Awakening.txt"] = new[] { "1/3 BaseDress(M)" },
        ["Unclosed.txt"] = new[] { "1/3 GROUP", "{", "1/2 WoodenSword" },
        ["Token.txt"] = new[] { "1/80 GroupToken", "#INSERT [Shared/Deep.txt]" }, // the open group takes the included lines too
    };

    /// <summary>A store with the synthetic world plus two monsters sharing a DropPath, the design layer pointed at it.</summary>
    private static void WithWorld(string tag, Action<string, string> body)
    {
        var dir = NewTempFolder(tag);
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);
            Envir.Main.MonsterInfoList.Add(new MonsterInfo { Index = 3, Name = "Wolf", DropPath = "Packs\\Shared" });
            Envir.Main.MonsterInfoList.Add(new MonsterInfo { Index = 4, Name = "Wolf0", DropPath = "Packs\\Shared" });
            var db = Path.Combine(dir, "SystemData.db");
            SqliteWorldStore.Create(db, SqliteWorldCodec.Encode(Envir.Main, 0), null);
            try { body(dir, db); }
            finally
            {
                DesignStore.Disabled = true;
                DesignStore.WriterOverride = null;
                DesignTexts.ResetCache();
            }

            return 0;
        });
    }

    private static SqliteWriter Open(string db)
    {
        var writer = new SqliteWriter(db, "drop tables test writer");
        DesignStore.WriterOverride = writer;
        DesignStore.Disabled = false;
        DesignTexts.ResetCache();
        DesignTexts.RefreshNames(SqliteWorldCodec.Encode(Envir.Main, 0));
        DesignTexts.EnsureLoaded();
        return writer;
    }

    /// <summary>Writes the set as a phase 7.4 store would hold it: drop_lines rows + design_files, imported.</summary>
    private static void WriteDropLines(string db)
    {
        using var conn = SqliteWorldFile.Open(db, create: false);
        SqliteWorldFile.Migrate(conn, _ => { });
        var f = DesignTexts.FormatOf("drops");
        Exec(conn, "CREATE TABLE drop_lines (name TEXT NOT NULL, position INTEGER NOT NULL, " +
                   string.Join(", ", f.Columns.Select(c => SqliteWorldCodec.Quote(c.Name) + " " + c.SqlType)) + ", PRIMARY KEY (name, position)) STRICT");
        var names = DesignNames.FromStore(conn);
        foreach (var (name, lines) in Drops)
        {
            var rows = DesignTexts.Parse(f, lines, names).Rows;
            Exec(conn, "INSERT INTO design_files (kind, name, imported_utc, updated_utc, newline, final_newline, bom) VALUES ('drops', '" + name + "', 'x', '2026-10-01T00:00:00.0000000Z', '\n', 0, 0)");
            for (var i = 0; i < rows.Length; i++)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO drop_lines (name, position, " + string.Join(", ", f.Columns.Select(c => SqliteWorldCodec.Quote(c.Name))) + ") VALUES ($n, $p, " +
                                  string.Join(", ", f.Columns.Select((_, c) => "$c" + c)) + ")";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", (long)i);
                for (var c = 0; c < f.Columns.Length; c++) cmd.Parameters.AddWithValue("$c" + c, rows[i][c] ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        Exec(conn, "INSERT INTO design_files (kind, name, imported_utc, updated_utc, newline, final_newline, bom) VALUES ('drops', '', 'x', 'x', '\r\n', 1, 0)");
    }

    [Fact]
    public void Drop_lines_convert_into_tables_that_load_and_render_like_the_text()
    {
        WithWorld("drops-convert", (dir, db) =>
        {
            WriteDropLines(db);
            var writer = Open(db); // opening the store converts drop_lines
            try
            {
                Assert.Single(Directory.GetFiles(dir, "Pre-drops *.bak", SearchOption.AllDirectories));
                Assert.Equal(0L, Count("SELECT count(*) FROM sqlite_master WHERE name = 'drop_lines'"));
                Assert.Equal((long)SqliteWorldCodec.SchemaVersion, Count("SELECT value FROM meta WHERE name = 'schema_version'")); // older builds refuse it
                Assert.Equal(0L, Count("SELECT count(*) FROM design_files WHERE kind = 'drops' AND name <> ''"));
                Assert.Equal(Drops.Count, (int)Count("SELECT count(*) FROM drop_lists"));

                // Owners (D1).
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = 'Hen.txt' AND kind = 'monster' AND monster_index = 1"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = 'Packs/Shared.txt' AND kind = 'named'"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = 'DragonItem.txt' AND kind = 'dragon'"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = '01Fishing.txt' AND kind = 'fishing' AND fishing_type = 1"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = '00Awakening.txt' AND kind = 'awakening'"));

                // Rows: groups nest by parent_id, includes link by id, unresolved and unreadable lines are disabled with their text.
                Assert.Equal(3L, Count("SELECT count(*) FROM drops d JOIN drops g ON d.parent_id = g.id WHERE g.kind = 'group' AND d.kind = 'item'")); // 2 in Hen, 1 in Unclosed
                Assert.Equal(1L, Count("SELECT count(*) FROM drops d JOIN drop_lists l ON d.include_list_id = l.id WHERE d.kind = 'include' AND l.name = 'Shared/Common.txt'"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drops d JOIN drop_lists l ON d.include_list_id = l.id WHERE d.kind = 'note' AND l.name = 'Shared/Deep.txt'"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drops WHERE kind = 'disabled' AND text = '1/1RedDagger Q'"));
                Assert.Equal(1L, Count("SELECT count(*) FROM drops WHERE kind = 'gold' AND gold = 20000")); // "LV1" after the amount: the game reads it
                Assert.Equal(1L, Count("SELECT count(*) FROM drops WHERE kind = 'item' AND dragon_level = 2 AND item_index = 1"));

                AssertLoadsLikeText();

                // Idempotent: a second open converts nothing.
                writer.Shutdown();
                writer = Open(db);
                Assert.Single(Directory.GetFiles(dir, "Pre-drops *.bak", SearchOption.AllDirectories));
                AssertLoadsLikeText();
            }
            finally { writer.Shutdown(); }
        });
    }

    [Fact]
    public void A_store_without_drops_imports_the_files_straight_into_the_tables()
    {
        WithWorld("drops-import", (dir, db) =>
        {
            foreach (var (name, lines) in Drops)
            {
                var path = Path.Combine("Envir", "Drops", name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, string.Join("\n", lines)); // LF, no final newline: kept per list
            }

            var writer = Open(db);
            try
            {
                Assert.Empty(Directory.GetFiles(dir, "Pre-drops *.bak", SearchOption.AllDirectories)); // nothing to convert
                Assert.Equal(Drops.Count, (int)Count("SELECT count(*) FROM drop_lists"));
                Assert.Equal(File.ReadAllBytes("Envir/Drops/Hen.txt"), DesignFiles.ReadAllBytes("Envir/Drops/Hen.txt"));
                AssertLoadsLikeText();
            }
            finally { writer.Shutdown(); }
        });
    }

    [Fact]
    public void Lists_follow_renames_create_defaults_and_round_trip_edited_text()
    {
        WithWorld("drops-edit", (dir, db) =>
        {
            WriteDropLines(db);
            var writer = Open(db);
            try
            {
                // A renamed item renders under its new name; the drop keeps its Index link.
                Envir.Main.ItemInfoList[0].Name = "Oak Sword";
                DesignTexts.RefreshNames(SqliteWorldCodec.Encode(Envir.Main, 0));
                Assert.Equal("1/5 OakSword Q", DesignFiles.ReadAllLines("Envir/Drops/Hen.txt")[2]);
                Assert.Equal("  1/3  OakSword  LV2 ", DesignFiles.ReadAllLines("Envir/Drops/Hen.txt")[20]);
                Assert.Equal("1/50 OakSword 2", DesignFiles.ReadAllLines("Envir/Drops/DragonItem.txt")[0]);

                // A renamed monster: the list stays under its file name until the rename propagation moves it (a running world, or
                // another monster sharing the file, still loads the old name), then it belongs to the monster under the new name.
                var before = Load("Hen.txt");
                Envir.Main.MonsterInfoList[0].Name = "Chicken";
                DesignTexts.RefreshNames(SqliteWorldCodec.Encode(Envir.Main, 0));
                Assert.True(DesignFiles.Exists("Envir/Drops/Hen.txt"));
                Assert.False(DesignFiles.Exists("Envir/Drops/Chicken.txt"));
                DesignFiles.Move("Envir/Drops/Hen.txt", "Envir/Drops/Chicken.txt");
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = 'Chicken.txt' AND kind = 'monster' AND monster_index = 1"));
                Assert.False(DesignFiles.Exists("Envir/Drops/Hen.txt"));
                Assert.Equal(Describe(before), Describe(Load("Chicken.txt")));

                // A missing list is created with the default lines (DropInfo.Load with create), stored and reloaded.
                var created = new List<DropInfo>();
                DropInfo.Load(created, "New", Path.Combine(Settings.DropPath, "New.txt"));
                Assert.Empty(created);
                DesignTexts.ResetCache();
                Assert.Equal(DropInfo.DefaultFileLines, DesignFiles.ReadAllLines("Envir/Drops/New.txt"));

                // Text written by an editor (a drop toggled off and on) parses back into rows and renders back exactly.
                var lines = DesignFiles.ReadAllLines("Envir/Drops/Chicken.txt").ToList();
                lines[1] = ";" + lines[1];
                DesignFiles.WriteAllLines("Envir/Drops/Chicken.txt", lines);
                Assert.Equal(lines, DesignFiles.ReadAllLines("Envir/Drops/Chicken.txt"));
                Assert.Equal(before.Count - 1, Load("Chicken.txt").Count);
                lines[1] = lines[1].Substring(1);
                DesignFiles.WriteAllLines("Envir/Drops/Chicken.txt", lines);
                Assert.Equal(Describe(before), Describe(Load("Chicken.txt")));

                // A list that includes itself loads once instead of looping forever.
                DesignFiles.WriteAllLines("Envir/Drops/Self.txt", new[] { "1/5 BaseDress(M)", "#INSERT [Self.txt]" });
                Assert.Single(Load("Self.txt"));

                // A line the text loader threw on is logged instead.
                DesignFiles.WriteAllLines("Envir/Drops/Throws.txt", new[] { "0 Gold 1000", "1/9 BaseDress(M)" });
                var bad = new List<string>();
                var list = new List<DropInfo>();
                Assert.True(DropTables.LoadInto(list, "Throws", "Throws.txt", 0, (_, l) => bad.Add(l)));
                Assert.Single(list);
                Assert.Equal(new[] { "0 Gold 1000" }, bad);

                // Renaming a monster's list to another name makes it a named list; a fresh cache reads it all back from the store.
                DesignFiles.Move("Envir/Drops/Chicken.txt", "Envir/Drops/Rooster.txt");
                Assert.Equal(1L, Count("SELECT count(*) FROM drop_lists WHERE name = 'Rooster.txt' AND kind = 'named'"));
                DesignTexts.ResetCache();
                Assert.Equal(Describe(before), Describe(Load("Rooster.txt")));
                Assert.False(DesignFiles.Exists("Envir/Drops/Chicken.txt"));
            }
            finally { writer.Shutdown(); }
        });
    }

    /// <summary>Every list of the set loads from the tables as the text loader loads its text, with the same logged lines.</summary>
    private static void AssertLoadsLikeText()
    {
        string[] Include(string sub) => Drops[DropTables.IncludeName(sub)];
        foreach (var (name, lines) in Drops)
        {
            Assert.Equal(lines, DesignFiles.ReadAllLines(Path.Combine(Settings.DropPath, name)));
            var oldBad = new List<string>();
            var newBad = new List<string>();
            if (name == "DragonItem.txt")
            {
                var a = Levels();
                var b = Levels();
                DragonInfo.LoadFromLines(a, lines, oldBad.Add);
                Assert.True(DropTables.LoadDragon(b, name, newBad.Add));
                Assert.Equal(a.Select(l => string.Join(",", l.Select(d => d.Chance + ":" + d.Item?.Index + ":" + d.Gold + ":" + d.level))),
                             b.Select(l => string.Join(",", l.Select(d => d.Chance + ":" + d.Item?.Index + ":" + d.Gold + ":" + d.level))));
                Assert.Equal(2, a[1].Count); // two drops on level 2
            }
            else
            {
                var type = (byte)(name == "01Fishing.txt" ? 1 : 0);
                var a = new List<DropInfo>();
                var b = new List<DropInfo>();
                DropInfo.LoadFromLines(a, name, lines.ToList(), type, Include, (_, l) => oldBad.Add(l));
                Assert.True(DropTables.LoadInto(b, name, name, type, (_, l) => newBad.Add(l)));
                Assert.Equal(Describe(a), Describe(b));
            }

            Assert.Equal(oldBad, newBad);
        }

        // The structure the text loader builds for Hen.txt: a random group with a member and a nested first-group, includes appended.
        var hen = Load("Hen.txt");
        Assert.Contains("1/3 group(* [1/2 item 2, 1/4 group(^ [1/9 item 1])])", Describe(hen));
        Assert.Contains("1/50 item 2", Describe(hen)); // Deep.txt, through the comment's #INSERT in Common.txt
        Assert.Equal("1/80 group( [])", Describe(Load("Token.txt"))); // a group token without braces: an empty group that swallows what follows, as before
        Assert.Contains("1/3 group( [])", Describe(Load("Unclosed.txt"))); // no closing brace: an empty group
    }

    private static List<DropInfo> Load(string name)
    {
        var list = new List<DropInfo>();
        Assert.True(DropTables.LoadInto(list, name, name, 0, (_, _) => { }));
        return list;
    }

    private static List<DragonInfo.DropInfo>[] Levels() =>
        Enumerable.Range(0, Globals.MaxDragonLevel).Select(_ => new List<DragonInfo.DropInfo>()).ToArray();

    private static string Describe(List<DropInfo> list) => string.Join(", ", list.Select(Describe));

    private static string Describe(DropInfo d) =>
        "1/" + d.Chance + " " + (d.GroupedDrop != null
            ? "group(" + (d.GroupedDrop.Random ? "*" : d.GroupedDrop.First ? "^" : string.Empty) + " [" + Describe(d.GroupedDrop) + "])"
            : d.Item != null ? "item " + d.Item.Index + (d.QuestRequired ? " Q" : string.Empty) : "gold " + d.Gold) + (d.Type != 0 ? " t" + d.Type : string.Empty);

    private static long Count(string sql) => DesignStore.Run(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    });

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
