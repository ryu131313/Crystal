using System.Text.Json;
using System.Text.RegularExpressions;
using Server.WebAdmin.GameData;
using Server.WebAdmin.LiveDb;
using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    /// <summary>LiveDb Batch D: the undo planner, undo through the pipeline, history and the frontend contract (pure, like LiveDbTests).</summary>
    public partial class LiveDbTests
    {
        private static LiveDbUndoOutcome Undo(Rig rig, string target, bool dryRun = false) =>
            LiveDbUndo.Run(rig.Env, target, dryRun, "tester", "127.0.0.1", null, false);

        // ------------------------------------------------------------------ planner

        [Fact]
        public void UndoPlan_Updates_PutTheBeforeValuesBack_GuardedByRevAfter()
        {
            var rig = new Rig();
            var r = rig.Run("d-1", "monsters", Upd(rig, "monsters", 1, "\"Level\":9,\"Name\":\"BigHen\""));
            var revAfter = rig.Rev("monsters", 1);
            Assert.Equal(revAfter, r.Rows[0].RevAfter);

            var plan = LiveDbUndo.Plan(r, "undo:d-1");

            Assert.True(plan.Undoable);
            Assert.Equal("d-1", plan.Request.InverseOf);
            Assert.Equal("undo", plan.Request.Source);
            var ops = Assert.Single(plan.Request.Tables);
            Assert.Equal("monsters", ops.Endpoint);
            var row = Assert.Single(ops.Rows);
            Assert.Equal(1, row.GetProperty("Index").GetInt32());
            Assert.Equal(revAfter, row.GetProperty("__rev").GetString());
            Assert.Equal(1, row.GetProperty("Level").GetInt32());
            Assert.Equal("Hen", row.GetProperty("Name").GetString());
            Assert.Empty(ops.Deletes);
        }

        [Fact]
        public void UndoPlan_Inserts_BecomeRevGuardedDeletes_DeletesAreRefused()
        {
            var rig = new Rig();
            var ins = rig.Run("d-2", "maprespawns", "[{\"Index\":-1,\"MapIndex\":1,\"MonsterIndex\":2,\"Location_X\":60,\"Location_Y\":61,\"Count\":4,\"Delay\":3}]");
            var plan = LiveDbUndo.Plan(ins, "undo:d-2");
            var ops = Assert.Single(plan.Request.Tables);
            var newIndex = ins.Rows[0].NewIndex;
            Assert.Equal(new[] { newIndex }, ops.Deletes);
            Assert.Equal(ins.Rows[0].RevAfter, ops.DeleteRevs[newIndex]);
            Assert.Empty(plan.Notes); // respawn deletes are live

            var del = rig.Run("d-3", "maprespawns", null, deletes: "1");
            var refused = LiveDbUndo.Plan(del, "undo:d-3");
            Assert.False(refused.Undoable);
            Assert.Null(refused.Request);
            Assert.Contains("backup", (string)Assert.Single(refused.Refused)["message"]);
        }

        [Fact]
        public void UndoId_IsDeterministic_Generational_AndBounded()
        {
            Assert.Equal("undo:b-1", LiveDbUndo.UndoId("b-1", 1));
            Assert.Equal("undo2:b-1", LiveDbUndo.UndoId("b-1", 2));
            var longId = new string('x', LiveDbBatchRequest.MaxIdLength);
            var id = LiveDbUndo.UndoId(longId, 1);
            Assert.True(id.Length <= LiveDbBatchRequest.MaxIdLength);
            Assert.True(LiveDbBatchRequest.IsValidId(id));
            Assert.Equal(id, LiveDbUndo.UndoId(longId, 1));
            Assert.NotEqual(id, LiveDbUndo.UndoId(longId, 2));
        }

        // ------------------------------------------------------------------ undo through the pipeline

        [Fact]
        public void Undo_RestoresEditAndMain_IsJournaled_AndADoubleClickIsIdempotent()
        {
            var rig = new Rig();
            rig.Run("d-4", "monsters", Upd(rig, "monsters", 1, "\"Level\":9,\"Stat_HP\":99"));
            Assert.Equal(9, M(rig.Main, 1).Level);

            var o = Undo(rig, "d-4");

            Assert.Equal("applied", o.Result.Status);
            Assert.Equal("undo:d-4", o.Result.Id);
            Assert.Equal("d-4", o.Result.InverseOf);
            Assert.Equal(1, M(rig.Edit, 1).Level);
            Assert.Equal(15, M(rig.Edit, 1).Stats[Stat.HP]);
            Assert.Equal(1, M(rig.Main, 1).Level);           // live too
            Assert.Equal(2, rig.Persistence.Saves);
            Assert.Same(o.Result, rig.Env.Journal.Find("undo:d-4"));
            Assert.Contains("db.livedb-batch batch:undo:d-4", rig.Audit);
            Assert.True(LiveDbUndo.IsUndone(rig.Env.Journal, "d-4"));

            var again = Undo(rig, "d-4");
            Assert.True(again.AlreadyUndone);
            Assert.True(again.Result.Duplicate);
            Assert.Equal(2, rig.Persistence.Saves);          // not applied twice
        }

        [Fact]
        public void Undo_DryRun_PreviewsWithoutChanging()
        {
            var rig = new Rig();
            rig.Run("d-5", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));
            var o = Undo(rig, "d-5", dryRun: true);
            Assert.Equal("dry-run", o.Result.Status);
            Assert.Equal(9, M(rig.Edit, 1).Level);
            Assert.Equal(1, rig.Persistence.Saves);
            Assert.Null(rig.Env.Journal.Find("undo:d-5"));
        }

        [Fact]
        public void Undo_RowChangedAfterwards_IsAConflict_AndChangesNothing()
        {
            var rig = new Rig();
            rig.Run("d-6", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));
            rig.Run("d-7", "monsters", Upd(rig, "monsters", 1, "\"Level\":12"));

            var o = Undo(rig, "d-6");

            Assert.Equal("rejected", o.Result.Status);
            Assert.Equal(409, o.Result.HttpStatus);
            Assert.Single(o.Result.Conflicts);
            Assert.Equal(12, M(rig.Edit, 1).Level);
            Assert.Equal(12, M(rig.Main, 1).Level);
            Assert.Equal(2, rig.Persistence.Saves);
            Assert.Null(rig.Env.Journal.Find("undo:d-6"));   // a rejected undo can be retried with the same id
        }

        [Fact]
        public void Undo_OfAnUndo_IsARedo_AndTheOriginalCanBeUndoneAgain()
        {
            var rig = new Rig();
            rig.Run("d-8", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));
            Undo(rig, "d-8");
            Assert.Equal(1, M(rig.Edit, 1).Level);

            var redo = Undo(rig, "undo:d-8");
            Assert.Equal("applied", redo.Result.Status);
            Assert.Equal("undo:undo:d-8", redo.Result.Id);
            Assert.Equal(9, M(rig.Edit, 1).Level);
            Assert.False(LiveDbUndo.IsUndone(rig.Env.Journal, "d-8"));

            var again = Undo(rig, "d-8");
            Assert.False(again.AlreadyUndone);
            Assert.Equal("undo2:d-8", again.Result.Id);
            Assert.Equal("applied", again.Result.Status);
            Assert.Equal(1, M(rig.Edit, 1).Level);
        }

        [Fact]
        public void Undo_OfAnItemInsert_IsSavedOnly_AndSaysTheWorldKeepsIt()
        {
            var rig = new Rig();
            var ins = rig.Run("d-9", "items", "[{\"Index\":-1,\"Name\":\"IronSword\",\"Type\":\"Weapon\"}]");
            Assert.True(ins.Rows[0].RowLive);

            var o = Undo(rig, "d-9");

            Assert.Equal("applied", o.Result.Status);
            Assert.DoesNotContain(rig.Edit.Items, i => i.Name == "IronSword");
            Assert.Contains(rig.Main.Items, i => i.Name == "IronSword");   // item deletes are restart-only
            var row = Assert.Single(o.Result.Rows);
            Assert.Equal("delete", row.Kind);
            Assert.False(row.RowLive);
            Assert.Equal("partial", o.Result.Live.Outcome);              // a row that did not reach the running world
            Assert.Contains(o.Result.Notes, n => n.Contains("keeps", StringComparison.Ordinal) && n.Contains("restart", StringComparison.Ordinal));
            Assert.Contains("keeps", o.Result.Message);
        }

        [Fact]
        public void Undo_OfARespawnInsert_RemovesItLive()
        {
            var rig = new Rig();
            var ins = rig.Run("d-10", "maprespawns", "[{\"Index\":-1,\"MapIndex\":1,\"MonsterIndex\":2,\"Location_X\":60,\"Location_Y\":61,\"Count\":4,\"Delay\":3}]");
            var newIndex = ins.Rows[0].NewIndex;
            Assert.Contains(rig.Main.Maps[0].Respawns, x => x.RespawnIndex == newIndex);

            var o = Undo(rig, "d-10");

            Assert.Equal("applied", o.Result.Status);
            Assert.True(Assert.Single(o.Result.Rows).RowLive);
            Assert.DoesNotContain(rig.Edit.Maps[0].Respawns, x => x.RespawnIndex == newIndex);
            Assert.DoesNotContain(rig.Main.Maps[0].Respawns, x => x.RespawnIndex == newIndex);
            Assert.Empty(o.Result.Notes);
        }

        [Fact]
        public void Undo_OfAPositionKeyedInsert_IsRefusedOnceAnEarlierSiblingWasDeleted()
        {
            var rig = new Rig();
            var ins = rig.Run("d-11", "mapsafezones", "[{\"Index\":-1,\"MapIndex\":1,\"Location_X\":70,\"Location_Y\":70,\"Size\":4}]");
            Assert.Equal(10002, ins.Rows[0].NewIndex);
            rig.Run("d-12", "mapsafezones", null, deletes: "10000"); // the new zone moves to key 10001

            var o = Undo(rig, "d-11");

            Assert.Equal("rejected", o.Result.Status);
            Assert.Single(o.Result.Conflicts);
            Assert.Equal(2, rig.Edit.Maps[0].SafeZones.Count);
            Assert.Equal(70, rig.Edit.Maps[0].SafeZones[1].Location.X);
        }

        [Fact]
        public void Undo_OfAPositionKeyedInsert_GuardsTheKeyByRev()
        {
            var rig = new Rig();
            var ins = rig.Run("d-13", "mapsafezones", "[{\"Index\":-1,\"MapIndex\":1,\"Location_X\":70,\"Location_Y\":70,\"Size\":4}]");
            rig.Run("d-14", "mapsafezones", Upd(rig, "mapsafezones", 10002, "\"Size\":8")); // changed after the insert
            Assert.Equal("rejected", Undo(rig, "d-13").Result.Status);

            rig.Run("d-15", "mapsafezones", Upd(rig, "mapsafezones", 10002, "\"Size\":4")); // back to the inserted values
            var ok = Undo(rig, "d-13");
            Assert.Equal("applied", ok.Result.Status);
            Assert.Equal(2, rig.Edit.Maps[0].SafeZones.Count);
            Assert.Equal(2, rig.Main.Maps[0].SafeZones.Count);
            Assert.Equal(10002, ins.Rows[0].NewIndex);
        }

        [Fact]
        public void Undo_OfADelete_Is409_WithTheBackupHint()
        {
            var rig = new Rig();
            rig.Run("d-16", "gameshop", null, deletes: "1");
            var ex = Assert.Throws<WebAdminException>(() => Undo(rig, "d-16"));
            Assert.Equal(409, ex.Status);
            Assert.Contains("backup", ex.Message);
            Assert.Equal(1, rig.Persistence.Saves);
        }

        [Fact]
        public void Undo_UnknownBatch_Is404()
        {
            var rig = new Rig();
            Assert.Equal(404, Assert.Throws<WebAdminException>(() => Undo(rig, "nope")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => Undo(rig, "bad id!")).Status);
        }

        [Fact]
        public void RevGuardedDelete_WithAStaleRev_RejectsTheBatch()
        {
            var rig = new Rig();
            var req = new LiveDbBatchRequest { Id = "d-17", Admin = "tester" };
            var ops = new LiveDbTableOps { Endpoint = "gameshop" };
            ops.Deletes.Add(1);
            ops.DeleteRevs[1] = "stale";
            req.Tables.Add(ops);

            var r = new LiveDbEngine(rig.Env).Run(req);

            Assert.Equal("rejected", r.Status);
            Assert.Single(r.Conflicts);
            Assert.Single(rig.Edit.GameShop);
            Assert.Equal(0, rig.Persistence.Saves);
        }

        [Fact]
        public void Parse_ReadsDeleteRevs()
        {
            var req = LiveDbBatchRequest.Parse(Json("{\"id\":\"x-1\",\"tables\":[{\"endpoint\":\"gameshop\",\"deletes\":[1],\"deleteRevs\":{\"1\":\"abc\"}}]}"));
            Assert.Equal("abc", req.Tables[0].DeleteRevs[1]);
            Assert.Throws<WebAdminException>(() => LiveDbBatchRequest.Parse(Json("{\"id\":\"x-1\",\"tables\":[{\"endpoint\":\"gameshop\",\"deleteRevs\":{\"a\":\"abc\"}}]}")));
        }

        // ------------------------------------------------------------------ journal / history

        [Fact]
        public void Journal_File_KeepsTheRowImages_SoUndoWorksAfterARestart()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mir2-livedb-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var rig = new Rig();
                rig.Env.Journal = new LiveDbJournal(dir);
                var r = rig.Run("d-18", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));

                var fresh = new LiveDbJournal(dir); // a new process: images only from the file
                var found = fresh.Find("d-18");
                Assert.NotNull(found);
                Assert.Equal(1, ((JsonElement)found.Rows[0].BeforeImage["Level"]).GetInt32());
                Assert.Equal(r.Rows[0].RevAfter, found.Rows[0].RevAfter);

                rig.Env.Journal = fresh;
                var o = Undo(rig, "d-18");
                Assert.Equal("applied", o.Result.Status);
                Assert.Equal(1, M(rig.Edit, 1).Level);
                Assert.Equal("d-18", new LiveDbJournal(dir).Find("undo:d-18").InverseOf);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void History_FiltersPages_AndReportsUndoneBy()
        {
            var rig = new Rig();
            rig.Run("d-19", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));
            rig.Run("d-20", "items", Upd(rig, "items", 1, "\"Price\":200"));
            Undo(rig, "d-19");

            var all = rig.Env.Journal.Recent(100);
            var undone = LiveDbRoutes.UndoneBy(all);
            Assert.Equal("undo:d-19", undone["d-19"]);
            Assert.False(undone.ContainsKey("d-20"));

            var json = JsonSerializer.Serialize(LiveDbRoutes.History(all, new LiveDbRoutes.HistoryFilter { Table = "monsters" }, 0, 50), WebAdminJson.Options);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count); // d-19 and its undo
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
            var d19 = items.Single(i => i.GetProperty("id").GetString() == "d-19");
            Assert.Equal("undo:d-19", d19.GetProperty("undoneBy").GetString());
            Assert.Equal("d-19", items.Single(i => i.GetProperty("id").GetString() == "undo:d-19").GetProperty("inverseOf").GetString());
            Assert.True(d19.GetProperty("counters").TryGetProperty("pushConnections", out _));

            var paged = JsonSerializer.Serialize(LiveDbRoutes.History(all, null, 1, 1), WebAdminJson.Options);
            using var pdoc = JsonDocument.Parse(paged);
            Assert.Single(pdoc.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(3, pdoc.RootElement.GetProperty("total").GetInt32());

            var none = LiveDbRoutes.History(all, new LiveDbRoutes.HistoryFilter { Admin = "someone-else" }, 0, 50);
            Assert.Contains("\"total\":0", JsonSerializer.Serialize(none, WebAdminJson.Options));
        }

        [Fact]
        public void HistoryDetail_HasBeforeAfterValues_AndTheUndoState()
        {
            var rig = new Rig();
            rig.Run("d-21", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));
            var json = JsonSerializer.Serialize(LiveDbRoutes.Detail(rig.Env.Journal, rig.Env.Journal.Find("d-21")), WebAdminJson.Options);
            using var doc = JsonDocument.Parse(json);
            var change = doc.RootElement.GetProperty("rows")[0].GetProperty("changes")[0];
            Assert.Equal("Level", change.GetProperty("field").GetString());
            Assert.Equal(1, change.GetProperty("before").GetInt32());
            Assert.Equal(9, change.GetProperty("after").GetInt32());
            var undo = doc.RootElement.GetProperty("undo");
            Assert.True(undo.GetProperty("undoable").GetBoolean());
            Assert.Equal("undo:d-21", undo.GetProperty("undoId").GetString());

            Undo(rig, "d-21");
            var after = JsonSerializer.Serialize(LiveDbRoutes.Detail(rig.Env.Journal, rig.Env.Journal.Find("d-21")), WebAdminJson.Options);
            using var adoc = JsonDocument.Parse(after);
            Assert.False(adoc.RootElement.GetProperty("undo").GetProperty("undoable").GetBoolean());
            Assert.Equal("undo:d-21", adoc.RootElement.GetProperty("undo").GetProperty("undoneBy").GetString());
        }

        [Fact]
        public void UndoReply_CarriesTheTargetAndUndoId()
        {
            var rig = new Rig();
            rig.Run("d-22", "monsters", Upd(rig, "monsters", 1, "\"Level\":9"));
            var reply = LiveDbRoutes.UndoReply(Undo(rig, "d-22"));
            Assert.Equal("d-22", reply["target"]);
            Assert.Equal("undo:d-22", reply["undoId"]);
            Assert.Equal("d-22", reply["inverseOf"]);
            Assert.Equal(false, reply["alreadyUndone"]);
        }

        // ------------------------------------------------------------------ frontend contract

        [Fact]
        public void Frontend_LiveDbSection_IsRegistered_AndUsesTheUndoRoute()
        {
            var dir = AdminDir();
            var html = File.ReadAllText(Path.Combine(dir, "index.html"));
            Assert.Contains("data-section=\"livedb\"", html);
            Assert.Contains("<section id=\"section-livedb\" class=\"admin-section hidden\"></section>", html);
            Assert.Contains("href=\"admin-livedb.css\"", html);
            var script = html.IndexOf("<script src=\"admin-livedb.js\"></script>", StringComparison.Ordinal);
            Assert.True(script > 0);
            Assert.True(script < html.IndexOf("<script src=\"app.js\"></script>", StringComparison.Ordinal)); // app.js stays last

            var shell = File.ReadAllText(Path.Combine(dir, "admin-shell.js"));
            Assert.Matches(new Regex(@"livedb:\s*\{\s*label:\s*'LiveDb History'"), shell);

            var js = File.ReadAllText(Path.Combine(dir, "admin-livedb.js"));
            Assert.Contains("AdminSections.register('livedb'", js);
            Assert.Contains("portalRegisterDeepLink('livedb'", js);
            Assert.Contains("'/api/admin/livedb/undo/'", js);
            Assert.Contains("'/api/admin/livedb/history'", js);
            Assert.Contains("'?dryRun=1'", js);
            Assert.Contains("portalApi(", js);
            Assert.DoesNotContain("innerHTML", js);                                      // textContent only
            Assert.DoesNotMatch(new Regex(@"(?<![\w.$])fetch\s*\("), js);                // portalApi carries the CSRF header
            Assert.DoesNotMatch(new Regex(@"https?://"), js);                            // no CDN / hard-coded host
            Assert.DoesNotContain("localStorage", js);

            var css = File.ReadAllText(Path.Combine(dir, "admin-livedb.css"));
            Assert.Contains(".ldb-", css);
            Assert.DoesNotMatch(new Regex(@"^\s*(body|html|table|button)\s*\{", RegexOptions.Multiline), css); // scoped

            var dash = File.ReadAllText(Path.Combine(dir, "admin-dashboard.js"));
            Assert.Contains("key: 'livedb'", dash);
            Assert.Contains("st.livedb", dash);
        }
    }

    /// <summary>Batch D pieces that read process-wide statics (route registration, the /status block).</summary>
    [Collection(WebAdminGlobalsCollection.Name)]
    public class LiveDbUndoGlobalsTests
    {
        [Theory]
        [InlineData("GET", "/api/admin/livedb/history/b-1", "/api/admin/livedb/history/{id}")]
        [InlineData("GET", "/api/admin/livedb/history/undo%3Ab-1", "/api/admin/livedb/history/{id}")]
        [InlineData("POST", "/api/admin/livedb/undo/b-1", "/api/admin/livedb/undo/{id}")]
        public void UndoAndHistoryRoutes_AreRegistered_AdminOnly(string method, string path, string pattern)
        {
            var router = new WebAdminRouter();
            new LiveDbRoutes().Register(router);
            var match = router.Match(method, path);
            Assert.True(match.Success, method + " " + path);
            Assert.Equal(pattern, match.Route.Pattern);
            Assert.Equal(RouteAuth.Admin, match.Route.Auth);
        }

        [Fact]
        public void StatusBlock_ReportsTheLastBatch_SweeperAndDivergence()
        {
            var json = JsonSerializer.Serialize(LiveDbRoutes.StatusBlock(true), WebAdminJson.Options);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // lastBatchId / lastBatchUtc / lastLiveOutcome are null (omitted by WebAdminJson) until a batch ran in this process
            foreach (var key in new[] { "enabled", "batches", "rejected", "liveFailures", "lastSaved", "sweeperActive", "sweeperPending", "divergedCount", "diverged", "undos" })
                Assert.True(root.TryGetProperty(key, out _), key);
            Assert.True(root.GetProperty("enabled").GetBoolean());
        }

        [Fact]
        public void StatusRoute_IncludesTheLiveDbBlock()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Server", "WebAdmin", "Routes", "StatusRoutes.cs"));
            Assert.Contains("livedb = LiveDbRoutes.StatusBlock(", src);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }
}
