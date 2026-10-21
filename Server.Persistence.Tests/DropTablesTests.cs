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

    [Fact]
    public void Grid_edits_bind_monsters_by_index_and_load_like_their_text()
    {
        WithWorld("drops-grid", (dir, db) =>
        {
            WriteDropLines(db);
            var writer = Open(db);
            try
            {
                string ItemName(long i) => Envir.Main.GetItemInfo((int)i)?.Name.Replace(" ", string.Empty);
                var hen = DropTables.Get(DropTables.ForMonster(1, "Hen", null).Id);

                // Index binding: a renamed monster keeps its own list, also before any file moves; a DropPath list is shared by name.
                Assert.Same(hen, DropTables.ForMonster(1, "Chicken", null));
                Assert.Equal("Packs/Shared.txt", DropTables.ForMonster(3, "Wolf", "Packs\\Shared").Name);
                Assert.Same(DropTables.ForMonster(3, "Wolf", "Packs\\Shared"), DropTables.ForMonster(4, "Wolf0", "Packs\\Shared"));
                Assert.Same(hen, DropTables.ForMonster(9, "Ghost", "Hen")); // another monster loading the owner's file by name
                var renamed = new MonsterInfo { Index = 1, Name = "Chicken" };
                var loaded = new List<DropInfo>();
                DropInfo.LoadForMonster(loaded, renamed, false);
                Assert.Equal(Describe(Load("Hen.txt")), Describe(loaded));

                // A grid save: change a chance, add an item to the random group, add a new group with a member, delete the gold 150 row;
                // untouched lines keep their text, new and changed rows get canonical lines, notes stay in place.
                var edits = DropTables.AsEdits(hen);
                var group = edits.First(e => e.Kind == DropKind.Group && e.GroupMode == "*");
                edits.First(e => e.Kind == DropKind.Item && e.ItemIndex == 3).Chance = 12;
                edits.RemoveAll(e => e.Kind == DropKind.Gold && e.Gold == 150);
                edits.Insert(edits.IndexOf(group) + 1, new DropEdit { Id = -1, ParentId = group.Id, Kind = DropKind.Item, Chance = 7, ItemIndex = 3 });
                edits.Add(new DropEdit { Id = -2, Kind = DropKind.Group, Chance = 4, GroupMode = "^" });
                edits.Add(new DropEdit { Id = -3, ParentId = -2, Kind = DropKind.Gold, Chance = 2, Gold = 99 });
                var errors = new List<DropEditError>();
                var rows = DropTables.ApplyEdits(hen, edits, ItemName, Names(), errors, out var changed);
                Assert.Empty(errors);
                Assert.Equal(5, changed); // chance, the deleted gold row, 3 new rows
                hen = DropTables.ReplaceRows(hen, rows);

                var text = DesignFiles.ReadAllLines("Envir/Drops/Hen.txt");
                Assert.Equal(";Pots", text[0]);
                Assert.Equal("1/12 (HP)DrugSmall", text[1]);
                Assert.Equal("1/5 woodensword Q", text[2]); // untouched: as written
                Assert.DoesNotContain("1/20 Gold 150", text);
                Assert.Equal(new[] { "1/4 GROUP^", "{", "1/2 Gold 99", "}" }, text.Skip(text.Length - 4));
                Assert.Contains("1/7 (HP)DrugSmall", text);
                AssertRowsLoadLikeTheirText("Hen.txt");
                Assert.Contains("1/3 group(* [1/7 item 3, 1/2 item 2, 1/4 group(^ [1/9 item 1])])", Describe(Load("Hen.txt")));
                Assert.Contains("1/4 group(^ [1/2 gold 99])", Describe(Load("Hen.txt")));

                // Validation, per row.
                var bad = DropTables.AsEdits(hen);
                bad.Add(new DropEdit { Id = -9, Kind = DropKind.Item, Chance = 0, ItemIndex = 77 });
                bad.Add(new DropEdit { Id = -10, ParentId = -9, Kind = DropKind.Gold, Chance = 1, Gold = 1 });
                bad.Add(new DropEdit { Id = -11, Kind = DropKind.Note });
                Assert.Null(DropTables.ApplyEdits(hen, bad, ItemName, Names(), errors, out _));
                Assert.Contains(errors, e => e.Field == "chance" && e.Row == bad.Count - 3);
                Assert.Contains(errors, e => e.Field == "itemIndex");
                Assert.Contains(errors, e => e.Field == "parentId");
                Assert.Contains(errors, e => e.Field == "kind" && e.Row == bad.Count - 1);

                // Dragon list: levels required, no groups.
                var dragon = DropTables.Get(DropTables.Lists().First(l => l.IsDragon).Id);
                var de = DropTables.AsEdits(dragon);
                de.Add(new DropEdit { Id = -1, Kind = DropKind.Item, Chance = 3, ItemIndex = 2 });
                errors.Clear();
                Assert.Null(DropTables.ApplyEdits(dragon, de, ItemName, Names(), errors, out _));
                Assert.Contains(errors, e => e.Field == "dragonLevel");
                de[^1].DragonLevel = 4;
                errors.Clear();
                dragon = DropTables.ReplaceRows(dragon, DropTables.ApplyEdits(dragon, de, ItemName, Names(), errors, out _));
                Assert.Equal("1/3 BaseDress(M) 4", DesignFiles.ReadAllLines("Envir/Drops/DragonItem.txt")[^1]);
                var levels = Levels();
                DropTables.LoadDragon(levels, dragon.Name, _ => { });
                Assert.Contains(levels[3], d => d.Chance == 3 && d.Item.Index == 2);

                // An old group without { } stays as it is, and nothing but notes may follow it.
                var unclosed = DropTables.Get(DropTables.Lists().First(l => l.Name == "Unclosed.txt").Id);
                var ue = DropTables.AsEdits(unclosed);
                ue.Add(new DropEdit { Id = -1, Kind = DropKind.Gold, Chance = 1, Gold = 5 });
                errors.Clear();
                Assert.Null(DropTables.ApplyEdits(unclosed, ue, ItemName, Names(), errors, out _));
                Assert.Contains("no { }", errors[0].Message);

                // Copy and delete-disabled.
                var shared = DropTables.Get(DropTables.ForMonster(3, "Wolf", "Packs\\Shared").Id);
                shared = DropTables.ReplaceRows(shared, DropTables.CopyRows(hen, shared, out var copyError));
                Assert.Null(copyError);
                Assert.Equal(DesignFiles.ReadAllLines("Envir/Drops/Hen.txt"), DesignFiles.ReadAllLines("Envir/Drops/Packs/Shared.txt"));
                Assert.Null(DropTables.CopyRows(hen, dragon, out copyError));
                var noDisabled = DropTables.ApplyEdits(shared, DropTables.AsEdits(shared).Where(e => e.Kind != DropKind.Disabled).ToList(), ItemName, Names(), errors, out var removed);
                shared = DropTables.ReplaceRows(shared, noDisabled);
                Assert.True(removed >= 3);
                Assert.DoesNotContain(shared.Rows, r => r.Kind == DropKind.Disabled);
                AssertRowsLoadLikeTheirText("Packs/Shared.txt");

                // A save the text would read differently is refused: an old group without "}" moved inside a new group would take its "}".
                var open = DropTables.Get(DropTables.Lists().First(l => l.Name == "Unclosed.txt").Id);
                var oe = DropTables.AsEdits(open);
                oe.Insert(0, new DropEdit { Id = -1, Kind = DropKind.Group, Chance = 1 });
                oe[1].ParentId = -1; // the old open group into the new one
                errors.Clear();
                Assert.Null(DropTables.ApplyEdits(open, oe, ItemName, Names(), errors, out _));
                Assert.Contains("differently", errors[0].Message);

                // A member moved out of its group gets a fresh, unindented line (its indentation read differently at the top level).
                var hen2 = DropTables.Get(hen.Id);
                var me = DropTables.AsEdits(hen2);
                var member = me.First(e => e.Kind == DropKind.Item && e.ItemIndex == 2 && e.ParentId != null && e.Chance == 2);
                me.Remove(member);
                member.ParentId = null;
                me.Add(member);
                errors.Clear();
                hen2 = DropTables.ReplaceRows(hen2, DropTables.ApplyEdits(hen2, me, ItemName, Names(), errors, out _));
                Assert.Equal("1/2 BaseDress(M)", DesignFiles.ReadAllLines("Envir/Drops/Hen.txt")[^1]);
                AssertRowsLoadLikeTheirText("Hen.txt");

                // A renamed monster's list isn't taken over by a new monster of the old name, nor by a list created under the new name.
                Envir.Main.MonsterInfoList[0].Name = "Chicken";
                DesignTexts.RefreshNames(SqliteWorldCodec.Encode(Envir.Main, 0));
                Assert.Null(DropTables.ForMonster(10, "Hen", null));
                Assert.Equal("Hen_10.txt", DropTables.FreeMonsterListName("Hen.txt", 10));
                Assert.Equal(DropList.Named, DropTables.OwnerForNew("Chicken.txt", new[] { (1L, "Chicken", (string)null) }).Kind);
                Assert.Same(DropTables.Get(hen.Id), DropTables.ForMonster(1, "Chicken", null));
                Envir.Main.MonsterInfoList[0].Name = "Hen";
                DesignTexts.RefreshNames(SqliteWorldCodec.Encode(Envir.Main, 0));
                text = DesignFiles.ReadAllLines("Envir/Drops/Hen.txt");

                // A new monster's own list, bound to its Index; then the store reads it all back the same.
                var created = DropTables.CreateList("Ghost.txt", DropList.Monster, 9, null);
                Assert.Same(created, DropTables.ForMonster(9, "Ghost", null));
                DesignTexts.ResetCache();
                Assert.Equal(created.Id, DropTables.ForMonster(9, "Renamed", null).Id);
                Assert.Equal(text, DesignFiles.ReadAllLines("Envir/Drops/Hen.txt"));
                AssertRowsLoadLikeTheirText("Hen.txt");
            }
            finally { writer.Shutdown(); }
        });
    }

    /// <summary>A list's rows load the same drops as the text loader reading its rendered lines (so text tools stay true).</summary>
    private static void AssertRowsLoadLikeText(string name, IList<string> lines)
    {
        var a = new List<DropInfo>();
        DropInfo.LoadFromLines(a, name, lines.ToList(), 0, sub => DesignFiles.ReadAllLines(Path.Combine(Settings.DropPath, sub)), (_, _) => { });
        Assert.Equal(Describe(a), Describe(Load(name)));
    }

    private static void AssertRowsLoadLikeTheirText(string name) => AssertRowsLoadLikeText(name, DesignFiles.ReadAllLines(Path.Combine(Settings.DropPath, name)));

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

    private static DesignNames Names() => DesignNames.FromEnvir(Envir.Main);

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
