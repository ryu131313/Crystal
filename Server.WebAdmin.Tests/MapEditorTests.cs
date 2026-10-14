using System.Drawing;
using System.Text.RegularExpressions;
using Server.MirDatabase;
using Server.WebAdmin.GameData;
using Server.WebAdmin.MapArt;
using Server.WebAdmin.MapEditor;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Maps editor (section "mapeditor"): walkability from a map file, the overlay / list read from a plain GameDataContext (no Envir,
    /// no Settings), and the static contract of the frontend files (admin-mapeditor.js / -view.js / .css, index.html, the shell).
    /// </summary>
    public class MapEditorTests
    {
        // ------------------------------------------------------------------ walkability

        /// <summary>A type-0 (old-school) map: w, h, a 52-byte header, then 12 bytes per cell, x-major.</summary>
        private static byte[] OldSchoolMap(int w, int h, Action<int, int, byte[], int> cell)
        {
            var bytes = new byte[Math.Max(64, 52 + w * h * 12)];
            BitConverter.GetBytes((short)w).CopyTo(bytes, 0);
            BitConverter.GetBytes((short)h).CopyTo(bytes, 2);
            for (var x = 0; x < w; x++)
                for (var y = 0; y < h; y++)
                    cell(x, y, bytes, 52 + (x * h + y) * 12);
            return bytes;
        }

        [Fact]
        public void Walkability_FollowsTheServerWalls_OfAnOldSchoolMap()
        {
            // row 0: back wall, middle wall (server LowWall), front wall; row 1: all walkable
            var bytes = OldSchoolMap(3, 2, (x, y, b, at) =>
            {
                if (y != 0) return;
                var offset = x == 0 ? 0 : x == 1 ? 2 : 4;
                BitConverter.GetBytes(unchecked((short)0x8001)).CopyTo(b, at + offset);
            });

            var walk = MapEditorService.Build(new ClientMapData(bytes));

            Assert.Equal(3, walk.Width);
            Assert.Equal(2, walk.Height);
            Assert.Equal(3, walk.WalkableCells);
            Assert.Equal(new byte[] { 0, 0, 0, 1, 1, 1 }, walk.Walk);   // row-major (y, then x)
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, walk.Png.Take(4).ToArray());
            Assert.Equal(3, (walk.Png[16] << 24) | (walk.Png[17] << 16) | (walk.Png[18] << 8) | walk.Png[19]);   // IHDR width
            Assert.Equal(2, (walk.Png[20] << 24) | (walk.Png[21] << 16) | (walk.Png[22] << 8) | walk.Png[23]);   // IHDR height
        }

        [Fact]
        public void Walkability_OfAMissingFile_IsNull()
        {
            Assert.Null(MapEditorService.Walkability(null));
            Assert.Null(MapEditorService.Walkability("   "));
        }

        // ------------------------------------------------------------------ overlay and list

        private static GameDataContext Context()
        {
            var ctx = new GameDataContext();
            ctx.Monsters.Add(new MonsterInfo { Index = 1, Name = "Hen", Level = 1 });
            ctx.Monsters.Add(new MonsterInfo { Index = 2, Name = "OmaKing", Level = 40, IsBoss = true });

            var bichon = new MapInfo { Index = 1, FileName = "0", Title = "BichonProvince" };
            bichon.SafeZones.Add(new SafeZoneInfo { Info = bichon, Location = new Point(10, 10), Size = 5, StartPoint = true });
            bichon.SafeZones.Add(new SafeZoneInfo { Info = bichon, Location = new Point(20, 20), Size = 6 });
            bichon.Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = new Point(50, 50), Count = 10, Spread = 4, RespawnIndex = 7 });
            bichon.Respawns.Add(new RespawnInfo { MonsterIndex = 2, Location = new Point(60, 60), Count = 1, RespawnIndex = 8 });
            bichon.Movements.Add(new MovementInfo { MapIndex = 2, Source = new Point(1, 1), Destination = new Point(2, 2) });
            bichon.MineZones.Add(new MineZone { Mine = 1, Location = new Point(30, 30), Size = 3 });
            ctx.Maps.Add(bichon);

            var woomyon = new MapInfo { Index = 2, FileName = "1", Title = "" };
            woomyon.Movements.Add(new MovementInfo { MapIndex = 1, Source = new Point(5, 5), Destination = new Point(11, 12) });
            woomyon.Movements.Add(new MovementInfo { MapIndex = 2, Source = new Point(6, 6), Destination = new Point(7, 7) });
            ctx.Maps.Add(woomyon);

            ctx.Npcs.Add(new NPCInfo { Index = 1, Name = "Merchant", FileName = "Bichon\\Merchant", MapIndex = 1, Location = new Point(12, 11) });
            ctx.Npcs.Add(new NPCInfo { Index = 2, Name = "Elsewhere", FileName = "x", MapIndex = 2 });
            return ctx;
        }

        [Fact]
        public void Overlay_IsTheGridRowsOfTheMap_WithNamesAndInboundMovements()
        {
            var ctx = Context();
            var ov = MapEditorService.ReadOverlay(ctx, 1);

            Assert.NotNull(ov);
            Assert.Equal("0", ov.FileName);
            Assert.Equal("BichonProvince", ov.Map["Title"]);
            Assert.True(ov.Map.ContainsKey(GameDataCodec.RevKey));

            // Same keys and wire values as GET /api/admin/db/<endpoint>?MapIndex=1, so the page saves through the grid pipeline.
            var grid = GameDataEngine.Query(ctx, GameDataRegistry.Get("mapsafezones"), new[] { new KeyValuePair<string, string>("MapIndex", "1") },
                null, null, null, 1, 0, out _);
            Assert.Equal(grid.Select(r => r["Index"]), ov.SafeZones.Select(r => r["Index"]));
            Assert.Equal(grid.Select(r => r[GameDataCodec.RevKey]), ov.SafeZones.Select(r => r[GameDataCodec.RevKey]));
            Assert.Equal(10000, Convert.ToInt32(ov.SafeZones[0]["Index"]));
            Assert.Equal(10, Convert.ToInt32(ov.SafeZones[0]["Location_X"]));

            Assert.Equal(new[] { 7, 8 }, ov.Respawns.Select(r => Convert.ToInt32(r["Index"])));
            Assert.Equal("Hen", ov.Respawns[0]["__monster"]);
            Assert.Equal(false, ov.Respawns[0]["__boss"]);
            Assert.Equal(true, ov.Respawns[1]["__boss"]);

            Assert.Single(ov.Movements);
            Assert.Equal(2, Convert.ToInt32(ov.Movements[0]["DestinationMapIndex"]));
            Assert.Equal("1", ov.Movements[0]["__destTitle"]);            // no title: the file name
            Assert.Single(ov.MineZones);

            Assert.Single(ov.Npcs);
            Assert.Equal("Merchant", ov.Npcs[0]["Name"]);

            // Only the other map's movement that leads here; its own same-map movement is not "inbound".
            var inbound = Assert.Single(ov.Inbound);
            var json = System.Text.Json.JsonSerializer.Serialize(inbound);
            Assert.Contains("\"rowIndex\":20000", json);
            Assert.Contains("\"fromMapIndex\":2", json);
            Assert.Contains("\"destX\":11", json);
        }

        [Fact]
        public void Overlay_OfAnUnknownMap_IsNull()
        {
            Assert.Null(MapEditorService.ReadOverlay(Context(), 99));
        }

        [Fact]
        public void List_CountsEveryKindPerMap()
        {
            var list = MapEditorService.ListMaps(Context());
            var json = System.Text.Json.JsonSerializer.Serialize(list);
            Assert.Equal(2, list.Count);
            Assert.Contains("{\"index\":1,\"fileName\":\"0\",\"title\":\"BichonProvince\",\"miniMap\":0,\"safeZones\":2,\"respawns\":2,\"movements\":1,\"inbound\":1,\"mineZones\":1,\"npcs\":1}", json);
            Assert.Contains("\"index\":2,", json);
            Assert.Contains("\"movements\":2,\"inbound\":1,", json);
        }

        // ------------------------------------------------------------------ frontend contract

        private static string AdminDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Server.MirForms", "wwwroot", "admin");
                if (File.Exists(Path.Combine(candidate, "admin-mapeditor.js"))) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Server.MirForms\\wwwroot\\admin\\admin-mapeditor.js not found above " + AppContext.BaseDirectory);
        }

        private static string Read(string file) => File.ReadAllText(Path.Combine(AdminDir(), file));

        public static IEnumerable<object[]> JsFiles() => new[]
        {
            new object[] { "admin-mapeditor.js" },
            new object[] { "admin-mapeditor-view.js" },
            new object[] { "admin-mapeditor-edit.js" },
            new object[] { "admin-mapeditor-picker.js" },
        };

        [Fact]
        public void Section_IsRegistered_InTheShellAndThePage()
        {
            var html = Read("index.html");
            Assert.Contains("data-section=\"mapeditor\"", html);
            Assert.Contains("<section id=\"section-mapeditor\" class=\"admin-section hidden\"></section>", html);
            Assert.Contains("href=\"admin-mapeditor.css\"", html);
            var view = html.IndexOf("<script src=\"admin-mapeditor-view.js\"></script>", StringComparison.Ordinal);
            var edit = html.IndexOf("<script src=\"admin-mapeditor-edit.js\"></script>", StringComparison.Ordinal);
            var page = html.IndexOf("<script src=\"admin-mapeditor.js\"></script>", StringComparison.Ordinal);
            var gamedata = html.IndexOf("<script src=\"admin-gamedata.js\"></script>", StringComparison.Ordinal);
            var app = html.IndexOf("<script src=\"app.js\"></script>", StringComparison.Ordinal);
            Assert.True(gamedata >= 0 && gamedata < view && view < edit && edit < page && page < app,
                "script order: gamedata, mapeditor-view, mapeditor-edit, mapeditor, app.js");

            Assert.Contains("mapeditor: { label: 'Maps', group: 'Game Data' },", Read("admin-shell.js"));

            var js = Read("admin-mapeditor.js");
            Assert.Contains("var SECTION = 'mapeditor';", js);
            Assert.Contains("global.AdminSections.register(SECTION,", js);
            Assert.Contains("global.portalRegisterDeepLink(SECTION,", js);
        }

        [Theory]
        [MemberData(nameof(JsFiles))]
        public void ApiCalls_AreTheMapEditorReadRoutesOnly(string file)
        {
            // Writes go through the Game Data pipeline (GameData.persist / MirLiveDb.runBatch), never a URL of this module.
            var urls = Regex.Matches(Read(file), @"'(/api/[^']*)'").Select(m => m.Groups[1].Value).Distinct().ToList();
            if (file != "admin-mapeditor-edit.js") Assert.NotEmpty(urls);
            var unknown = urls.Where(u => !u.StartsWith("/api/admin/mapeditor/maps", StringComparison.Ordinal) && u != "/api/admin/mapeditor/online").ToList();
            Assert.True(unknown.Count == 0, "Routes outside the map editor: " + string.Join(", ", unknown));
        }

        [Theory]
        [MemberData(nameof(JsFiles))]
        public void NoRawFetch_AndInnerHtmlIsOnlyCleared(string file)
        {
            var js = Read(file);
            Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
            var assignments = Regex.Matches(js, @"innerHTML\s*=\s*([^;]+);").Select(m => m.Groups[1].Value.Trim()).ToList();
            Assert.All(assignments, a => Assert.Equal("''", a));
            Assert.DoesNotContain("insertAdjacentHTML", js);
            Assert.DoesNotContain("outerHTML", js);
            Assert.DoesNotMatch(new Regex(@"localStorage\.(get|set)Item\(\s*'(?!mircraft_)"), js);
        }

        [Fact]
        public void EditMode_WritesOnlyThroughOneLiveDbBatch()
        {
            // Map edits become one LiveDb batch (dry run + preview + apply in admin-gamedata.js), with __rev on every edited row
            // and deleteRevs on deletes; the module never calls the server itself.
            var js = Read("admin-mapeditor-edit.js");
            Assert.Contains("MirLiveDb.runBatch(", js);
            Assert.Contains("{ Index: e.index, __rev: e.rev }", js);
            Assert.Contains("tb.deleteRevs[d.index] = d.rev;", js);
            Assert.DoesNotContain("portalApi(", js);
            Assert.DoesNotContain("OpsUI.post(", js);
            Assert.Contains("DbSave.setDirty(page.EDITS_KEY", js);
        }

        // Modals and the grid hover preview are appended to <body>, outside the section.
        private static readonly string[] BodyLevel = { ".me-pick", ".me-help", ".me-hover-preview" };

        [Fact]
        public void Picker_RegistersTheDrawerExtension_AndPreviewsOnlyWhenNoOtherModuleDoes()
        {
            var js = Read("admin-mapeditor-picker.js");
            Assert.Contains("global.FormDrawer.EXTENSIONS.push(", js);
            Assert.Contains("if (!global.ImagePreview) global.ImagePreview = previews;", js);
            Assert.Contains("global.MapPicker = { pick: pick };", js);
            var drawer = Read("admin-form-drawer.js");
            Assert.Contains("EXTENSIONS: EXTENSIONS,", drawer);
            Assert.Contains("catch (e) { if (global.console) console.warn('[FormDrawer] extension failed', e); }", drawer);
            var html = Read("index.html");
            var picker = html.IndexOf("<script src=\"admin-mapeditor-picker.js\"></script>", StringComparison.Ordinal);
            var drawerAt = html.IndexOf("<script src=\"admin-form-drawer.js\"></script>", StringComparison.Ordinal);
            var view = html.IndexOf("<script src=\"admin-mapeditor-view.js\"></script>", StringComparison.Ordinal);
            Assert.True(drawerAt >= 0 && view >= 0 && view < picker && drawerAt < picker, "the picker loads after the drawer and the view");
        }

        [Fact]
        public void Css_IsScopedToTheSection()
        {
            var css = Regex.Replace(Read("admin-mapeditor.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            var selectors = Regex.Matches(css, @"([^{}]+)\{[^{}]*\}")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("@", StringComparison.Ordinal))
                .SelectMany(s => s.Split(','))
                .Select(s => s.Trim())
                .ToList();
            Assert.NotEmpty(selectors);
            var unscoped = selectors.Where(s => !s.StartsWith("#section-mapeditor", StringComparison.Ordinal) && !s.StartsWith("body.me-", StringComparison.Ordinal) &&
                                                   !BodyLevel.Any(b => s.StartsWith(b, StringComparison.Ordinal))).ToList();
            Assert.True(unscoped.Count == 0, "Unscoped selectors: " + string.Join(" | ", unscoped));
        }
    }
}
