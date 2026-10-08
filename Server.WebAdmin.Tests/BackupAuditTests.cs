using System.Text;
using System.Text.Json;
using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    public class BackupAuditPagingTests
    {
        [Fact]
        public void Routes_RegisterUnderAdminAuth_WithoutTouchingGameState()
        {
            var router = new WebAdminRouter();
            new AuditRoutes().Register(router);
            new BackupRoutes().Register(router);

            foreach (var (method, path) in new[]
            {
                ("GET", "/api/admin/audit"), ("GET", "/api/admin/audit/actions"), ("GET", "/api/admin/audit/export"),
                ("GET", "/api/admin/db/history"), ("GET", "/api/admin/backups"), ("POST", "/api/admin/backups/create"),
                ("GET", "/api/admin/backups/download"), ("POST", "/api/admin/backups/restore"), ("POST", "/api/admin/backups/prune"),
            })
            {
                var match = router.Match(method, path);
                Assert.True(match.Success, method + " " + path);
                Assert.Equal(RouteAuth.Admin, match.Route.Auth);
            }

            Assert.True(router.Match("GET", "/api/admin/backups/restore").MethodNotAllowed);
        }

        [Theory]
        [InlineData("gm.kick", "gm", true)]
        [InlineData("gm.kick", "gm.", true)]
        [InlineData("gm.kick", "gm.*", true)]
        [InlineData("gm.kick", "GM.KICK", true)]
        [InlineData("gm.kick", "gm.kick.", true)]
        [InlineData("gmx.kick", "gm", false)]
        [InlineData("gm.kick", "gm.kic", false)]
        [InlineData("anything", null, true)]
        [InlineData("anything", "", true)]
        [InlineData("anything", "*", true)]
        public void ActionFilter_ExactOrPrefixGroup(string action, string filter, bool expected)
        {
            Assert.Equal(expected, AuditFilter.ActionMatches(action, filter));
        }

        [Fact]
        public void Cursor_RoundTrips_AndAcceptsBareTimestamps()
        {
            var ts = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddTicks(1234567);
            var cursor = new AuditCursor { Ts = ts, Id = "abc123" };

            Assert.True(AuditCursor.TryParse(cursor.Format(), out var parsed));
            Assert.Equal(ts, parsed.Ts);
            Assert.Equal(DateTimeKind.Utc, parsed.Ts.Kind);
            Assert.Equal("abc123", parsed.Id);

            Assert.True(AuditCursor.TryParse("2026-05-06T07:08:09Z", out var bare));
            Assert.Null(bare.Id);
            Assert.Equal(new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc), bare.Ts);

            Assert.False(AuditCursor.TryParse("not a date", out _));
            Assert.False(AuditCursor.TryParse("", out _));
        }

        [Fact]
        public void Cursor_OrdersEqualTimestampsById()
        {
            var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cursor = new AuditCursor { Ts = ts, Id = "m" };

            Assert.True(cursor.IsBefore(new WebAdminAuditEntry { Ts = ts.AddSeconds(-1), Id = "z" }));
            Assert.False(cursor.IsBefore(new WebAdminAuditEntry { Ts = ts.AddSeconds(1), Id = "a" }));
            Assert.True(cursor.IsBefore(new WebAdminAuditEntry { Ts = ts, Id = "a" }));
            Assert.False(cursor.IsBefore(new WebAdminAuditEntry { Ts = ts, Id = "m" }));
            Assert.False(cursor.IsBefore(new WebAdminAuditEntry { Ts = ts, Id = "z" }));

            var bare = new AuditCursor { Ts = ts };
            Assert.False(bare.IsBefore(new WebAdminAuditEntry { Ts = ts, Id = "a" }));
        }

        private static List<string> PageThrough(WebAdminAuditLog log, AuditFilter filter, int limit, DateTime now)
        {
            var targets = new List<string>();
            AuditCursor cursor = null;

            for (var guard = 0; guard < 100; guard++)
            {
                var page = AuditPager.Page(log, filter, limit, cursor, now);
                Assert.True(page.Entries.Count <= limit);
                targets.AddRange(page.Entries.Select(e => e.Target));
                if (page.Next == null) return targets;

                // Round-trip the cursor through its wire format like the UI does.
                Assert.True(AuditCursor.TryParse(page.Next.Format(), out cursor));
            }

            throw new InvalidOperationException("paging did not terminate");
        }

        [Fact]
        public void MemoryLog_PagesWithoutGapsOrDuplicates_EvenWithEqualTimestamps()
        {
            var clock = new TestClock();
            var log = new WebAdminAuditLog(null, 500, clock.Now);

            for (var i = 0; i < 23; i++)
            {
                log.Record("gm", "127.0.0.1", i % 2 == 0 ? "gm.kick" : "account.ban", "t" + i);
                if (i % 3 == 0) clock.Advance(TimeSpan.FromSeconds(1)); // several entries share a timestamp
            }

            var all = PageThrough(log, new AuditFilter { Days = 0 }, 5, clock.UtcNow);
            Assert.Equal(23, all.Count);
            Assert.Equal(23, all.Distinct().Count());

            var gm = PageThrough(log, new AuditFilter { Action = "gm.", Days = 0 }, 4, clock.UtcNow);
            Assert.Equal(12, gm.Count);
        }

        [Fact]
        public void FirstPage_IsNewestFirst_WithNextOnlyWhenMoreExist()
        {
            var clock = new TestClock();
            var log = new WebAdminAuditLog(null, 100, clock.Now);
            for (var i = 0; i < 3; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                log.Record("a", "ip", "x.y", "t" + i);
            }

            var page = AuditPager.Page(log, new AuditFilter(), 3, null, clock.UtcNow);
            Assert.Equal(new[] { "t2", "t1", "t0" }, page.Entries.Select(e => e.Target));
            Assert.Null(page.Next);

            var small = AuditPager.Page(log, new AuditFilter(), 2, null, clock.UtcNow);
            Assert.NotNull(small.Next);
            Assert.Equal("t1", small.Entries.Last().Target);
        }

        [Fact]
        public void Filters_AdminAndText_ApplyBeforePaging()
        {
            var clock = new TestClock();
            var log = new WebAdminAuditLog(null, 100, clock.Now);
            log.Record("Alice", "10.0.0.1", "config.update", "Setup.ini", new { field = "ExpRate" });
            log.Record("bob", "10.0.0.2", "gm.kick", "Hero", null, "afk");
            log.Record("alice", "10.0.0.1", "gm.kill", "Villain");

            Assert.Equal(2, AuditPager.Page(log, new AuditFilter { Admin = "ALICE" }, 10, null, clock.UtcNow).Entries.Count);
            Assert.Single(AuditPager.Page(log, new AuditFilter { Q = "exprate" }, 10, null, clock.UtcNow).Entries);
            Assert.Single(AuditPager.Page(log, new AuditFilter { Q = "AFK" }, 10, null, clock.UtcNow).Entries);
            Assert.Single(AuditPager.Page(log, new AuditFilter { Predicate = e => e.Target == "Hero" }, 10, null, clock.UtcNow).Entries);
        }

        [Fact]
        public void FileLog_PagesAcrossDays_RespectsDaysWindow_AndSurvivesRestart()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mircraft-b11-tests", Guid.NewGuid().ToString("N"));

            try
            {
                var clock = new TestClock(new DateTime(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc));
                var log = new WebAdminAuditLog(dir, 100, clock.Now);

                // 4 per day over 5 days (Mar 1 .. Mar 5).
                for (var day = 0; day < 5; day++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        log.Record("gm", "127.0.0.1", "db.update", "d" + day + "-" + i);
                        clock.Advance(TimeSpan.FromMinutes(10));
                    }

                    clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMinutes(40));
                }

                Assert.True(File.Exists(Path.Combine(dir, "audit-2026-03-01.jsonl")));
                Assert.True(File.Exists(Path.Combine(dir, "audit-2026-03-05.jsonl")));

                var now = clock.UtcNow;
                var restarted = new WebAdminAuditLog(dir, 100, () => now);

                var all = PageThrough(restarted, new AuditFilter { Days = 30 }, 3, now);
                Assert.Equal(20, all.Count);
                Assert.Equal(20, all.Distinct().Count());
                Assert.Equal("d4-3", all[0]);
                Assert.Equal("d0-0", all[19]);

                // "now" is Mar 6 22:00, so a 2-day window starts Mar 4 22:00: only the day-3 (Mar 4) and day-4 (Mar 5) entries.
                var window = PageThrough(restarted, new AuditFilter { Days = 2 }, 50, now);
                Assert.All(window, t => Assert.True(t.StartsWith("d4-", StringComparison.Ordinal) || t.StartsWith("d3-", StringComparison.Ordinal)));
                Assert.Equal(8, window.Count);
                Assert.Contains("d4-0", window);
                Assert.Contains("d3-0", window);

                // A torn line is skipped, not fatal.
                File.AppendAllText(Path.Combine(dir, "audit-2026-03-05.jsonl"), "{\"id\":\"broken\n", new UTF8Encoding(false));
                Assert.Equal(20, PageThrough(restarted, new AuditFilter { Days = 30 }, 7, now).Count);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void Summary_CountsActionsGroupsAndAdmins()
        {
            var clock = new TestClock();
            var log = new WebAdminAuditLog(null, 100, clock.Now);
            log.Record("a", "ip", "gm.kick", "x");
            log.Record("a", "ip", "gm.kick", "y");
            log.Record("b", "ip", "gm.kill", "z");
            log.Record("b", "ip", "backup.create", "database:Manual.bak");
            log.Record("b", "ip", "reboot", "world");

            var s = AuditPager.Summarize(log, 0, clock.UtcNow);

            Assert.Equal(5, s.Scanned);
            Assert.Equal(2, s.Actions.Single(p => p.Key == "gm.kick").Value);
            Assert.Equal(3, s.Groups.Single(p => p.Key == "gm").Value);
            Assert.Equal(1, s.Groups.Single(p => p.Key == "reboot").Value);
            Assert.Equal("b", s.Admins[0].Key);
            Assert.Equal(3, s.Admins[0].Value);
        }

        [Fact]
        public void Csv_QuotesCells_AndNeutralisesFormulas()
        {
            var entries = new[]
            {
                new WebAdminAuditEntry
                {
                    Id = "id1",
                    Ts = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                    Admin = "gm",
                    Ip = "127.0.0.1",
                    Action = "gm.message",
                    Target = "=HYPERLINK(\"x\")",
                    Reason = "said \"hi\", twice",
                    Details = JsonDocument.Parse("{\"text\":\"a,b\"}").RootElement.Clone(),
                },
            };

            var csv = AuditCsv.Build(entries);
            var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.Equal("\"ts\",\"admin\",\"ip\",\"action\",\"target\",\"reason\",\"details\",\"id\"", lines[0]);
            Assert.StartsWith("\"2026-02-03 04:05:06Z\",\"gm\",", lines[1]);
            Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
            Assert.Contains("\"said \"\"hi\"\", twice\"", lines[1]);
            Assert.Contains("\"{\"\"text\"\":\"\"a,b\"\"}\"", lines[1]);
        }
    }

    public class BackupAuditDbHistoryTests
    {
        private static WebAdminAuditEntry Entry(string action, string target, object details)
        {
            var log = new WebAdminAuditLog(null, 10, new TestClock().Now);
            return log.Record("gm", "127.0.0.1", action, target, details, "tuning");
        }

        [Fact]
        public void ChangesArray_IsRevertibleUpdate()
        {
            var e = Entry("db.update", "items#12", new
            {
                endpoint = "items",
                index = 12,
                changes = new object[]
                {
                    new { field = "Name", before = "Sword", after = "Great Sword" },
                    new { field = "Price", before = (object)null, after = 500 },
                    new { field = "__rev", before = "a", after = "b" },
                },
            });

            var item = Assert.Single(AuditDbHistory.Normalize(e));
            Assert.Equal("update", item.Kind);
            Assert.Equal("items", item.Endpoint);
            Assert.Equal(12, item.Index);
            Assert.Equal("tuning", item.Reason);
            Assert.Equal(2, item.Changes.Count);
            Assert.Equal("Sword", item.Changes[0].Before.Value.GetString());
            Assert.Equal(JsonValueKind.Null, item.Changes[1].Before.Value.ValueKind);
            Assert.Equal(500, item.Changes[1].After.Value.GetInt32());
            Assert.True(item.Revertible);
        }

        [Theory]
        [InlineData("mapsafezones", 50002, true)]
        [InlineData("mapmovements", 40000, true)]
        [InlineData("conquestguards", 10003, true)]
        [InlineData("maprespawns", 77, false)]
        [InlineData("items", 12, false)]
        public void PositionKeyedTables_AreNeverRevertible(string endpoint, int index, bool positionKeyed)
        {
            // Child rows keyed parent*10000 + position: after an earlier sibling is deleted the same Index is a different row.
            var e = Entry("db.update", endpoint + "#" + index, new
            {
                endpoint,
                index,
                changes = new object[] { new { field = "Size", before = 3, after = 5 } },
            });

            var item = Assert.Single(AuditDbHistory.Normalize(e));
            Assert.Equal(positionKeyed, item.PositionKeyed);
            Assert.Equal(!positionKeyed, item.Revertible);
        }

        [Fact]
        public void ChangeMap_WithBeforeAfterObjectsOrPairs_FallsBackToTarget()
        {
            var e = Entry("db.edit", "monsters/7", new Dictionary<string, object>
            {
                ["changes"] = new Dictionary<string, object>
                {
                    ["Level"] = new { old = 10, @new = 12 },
                    ["Stat_MaxHP"] = new object[] { 100, 150 },
                },
            });

            var item = Assert.Single(AuditDbHistory.Normalize(e));
            Assert.Equal("monsters", item.Endpoint);
            Assert.Equal(7, item.Index);
            Assert.Equal("update", item.Kind);
            Assert.Equal(10, item.Changes.Single(c => c.Field == "Level").Before.Value.GetInt32());
            Assert.Equal(150, item.Changes.Single(c => c.Field == "Stat_MaxHP").After.Value.GetInt32());
            Assert.True(item.Revertible);
        }

        [Fact]
        public void RowSnapshots_AreDiffed()
        {
            // Grid rows are dictionaries keyed by the C# field name (WebAdminJson keeps dictionary keys); null members are omitted.
            var e = Entry("db.update", "npcs:3", new
            {
                before = new Dictionary<string, object> { ["Name"] = "Guard", ["Rate"] = 100, ["Image"] = 5 },
                after = new Dictionary<string, object> { ["Name"] = "Guard", ["Rate"] = 120, ["Image"] = 6, ["Script"] = "Guard.txt" },
            });

            var item = Assert.Single(AuditDbHistory.Normalize(e));
            Assert.Equal(new[] { "Rate", "Image", "Script" }, item.Changes.Select(c => c.Field));
            Assert.Equal(JsonValueKind.Null, item.Changes[2].Before.Value.ValueKind);
            Assert.True(item.Revertible);
        }

        [Fact]
        public void MultiRowEntries_ExpandToOneItemPerRow()
        {
            var e = Entry("db.update", "items", new
            {
                endpoint = "items",
                rows = new object[]
                {
                    new { index = 1, changes = new[] { new { field = "Price", before = 1, after = 2 } } },
                    new { index = 2, changes = new[] { new { field = "Price", before = 3, after = 4 } } },
                },
            });

            var items = AuditDbHistory.Normalize(e);
            Assert.Equal(2, items.Count);
            Assert.Equal(new int?[] { 1, 2 }, items.Select(i => i.Index));
            Assert.All(items, i => Assert.Equal("items", i.Endpoint));
            Assert.True(AuditDbHistory.ItemMatches(items[1], "ITEMS", 2));
            Assert.False(AuditDbHistory.ItemMatches(items[1], "items", 1));
            Assert.False(AuditDbHistory.ItemMatches(items[1], "monsters", null));
        }

        [Fact]
        public void InsertsDeletesAndUnknownBefore_AreNotRevertible()
        {
            var insert = Assert.Single(AuditDbHistory.Normalize(Entry("db.insert", "items#99", new { after = new { Name = "New" } })));
            Assert.Equal("insert", insert.Kind);
            Assert.False(insert.Revertible);
            Assert.False(insert.Changes[0].Before.HasValue);

            var delete = Assert.Single(AuditDbHistory.Normalize(Entry("db.delete", "items#99", new { indexes = new[] { 99 } })));
            Assert.Equal("delete", delete.Kind);
            Assert.False(delete.Revertible);

            var noBefore = Assert.Single(AuditDbHistory.Normalize(Entry("db.update", "items#5", new { changes = new { Price = 10 } })));
            Assert.False(noBefore.Revertible);

            var flat = Assert.Single(AuditDbHistory.Normalize(Entry("db.update", "items#5", new { field = "Price", before = 1, after = 2 })));
            Assert.True(flat.Revertible);
            Assert.Equal("Price", flat.Changes.Single().Field);

            var save = Assert.Single(AuditDbHistory.Normalize(Entry("db.save", null, null)));
            Assert.Equal("save", save.Kind);
            Assert.False(save.Revertible);
        }

        [Theory]
        [InlineData("items#12", "items", 12)]
        [InlineData("items/12", "items", 12)]
        [InlineData("items:12", "items", 12)]
        [InlineData("items[12]", "items", 12)]
        [InlineData("db:maps:4", "maps", 4)]
        [InlineData("mapsafezones #40001", "mapsafezones", 40001)]
        [InlineData("dragon", "dragon", null)]
        public void Target_IsParsed(string target, string endpoint, int? index)
        {
            Assert.True(AuditDbHistory.TryParseTarget(target, out var ep, out var idx));
            Assert.Equal(endpoint, ep);
            Assert.Equal(index, idx);
        }

        [Fact]
        public void Kind_ComesFromDetailsOpOrActionVerb()
        {
            Assert.Equal("insert", AuditDbHistory.KindOf("db.items.create", default));
            Assert.Equal("update", AuditDbHistory.KindOf("db.update", default));
            Assert.Equal("delete", AuditDbHistory.KindOf("db.update", JsonDocument.Parse("{\"op\":\"remove\"}").RootElement));
            Assert.Equal("rename-propagate", AuditDbHistory.KindOf("db.rename-propagate", default));
        }
    }

    public class BackupAuditStoreTests : IDisposable
    {
        private readonly string _root;

        public BackupAuditStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "mircraft-b11-backups", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        private string Make(string relative, byte[] content, DateTime? modifiedUtc = null)
        {
            var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, content);
            if (modifiedUtc.HasValue) File.SetLastWriteTimeUtc(path, modifiedUtc.Value);
            return path;
        }

        private static byte[] Header(int version, int custom, int extra = 16)
        {
            var bytes = new byte[8 + extra];
            BitConverter.GetBytes(version).CopyTo(bytes, 0);
            BitConverter.GetBytes(custom).CopyTo(bytes, 4);
            return bytes;
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("..\\Server.MirDB.bak")]
        [InlineData("../x.bak")]
        [InlineData("a/../../x.bak")]
        [InlineData("C:\\Windows\\x.bak")]
        [InlineData("/etc/x.bak")]
        [InlineData("\\\\server\\share\\x.bak")]
        [InlineData("x.bak:stream.bak")]
        [InlineData("CON.bak")]
        [InlineData("nul.txt.bak")]
        [InlineData("x.txt")]
        [InlineData("x.bak.")]
        [InlineData("x*.bak")]
        [InlineData("sub/x.bak")]
        [InlineData("./x.bak")]
        public void Resolve_RejectsUnsafeNames_ForFlatKinds(string name)
        {
            var ex = Assert.Throws<WebAdminException>(() => BackupStore.Resolve(_root, name, false));
            Assert.Equal(400, ex.Status);
        }

        [Fact]
        public void Resolve_AcceptsPlainAndNestedNames_InsideRoot()
        {
            var flat = BackupStore.Resolve(_root, "Manual 2026-01-02 03-04-05.bak", false);
            Assert.Equal(Path.Combine(Path.GetFullPath(_root), "Manual 2026-01-02 03-04-05.bak"), flat);

            var nested = BackupStore.Resolve(_root, "npcs/Town/Guard.txt.20260102-030405.bak", true);
            Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, nested);
            Assert.EndsWith(Path.Combine("npcs", "Town", "Guard.txt.20260102-030405.bak"), nested);

            Assert.Throws<WebAdminException>(() => BackupStore.Resolve(_root, "npcs/../../x.bak", true));
            Assert.Throws<WebAdminException>(() => BackupStore.Resolve(_root, "npcs//x.bak", true));
        }

        [Fact]
        public void List_ReturnsBakFilesNewestFirst_WithOriginAndHeader()
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Make("Database 2026-01-01 00-00-00.bak", Header(117, 0), t0);
            Make("Manual 2026-01-02 00-00-00.bak", Header(116, 3), t0.AddDays(1));
            Make("Pre-restore 2026-01-03 00-00-00.bak", Header(117, 0), t0.AddDays(2));
            Make("notes.txt", new byte[] { 1 }, t0.AddDays(3));
            Make("weird.bak1", new byte[] { 1 }, t0.AddDays(3));
            Make("sub/nested.bak", Header(1, 1), t0.AddDays(4));

            var files = BackupStore.List(_root, false, true);

            Assert.Equal(new[] { "Pre-restore 2026-01-03 00-00-00.bak", "Manual 2026-01-02 00-00-00.bak", "Database 2026-01-01 00-00-00.bak" }, files.Select(f => f.Name));
            Assert.Equal(new[] { "pre-restore", "manual", "auto" }, files.Select(f => f.Origin));
            Assert.Equal(116, files[1].Version);
            Assert.Equal(3, files[1].CustomVersion);
            Assert.Equal(24, files[1].Size);

            Assert.Empty(BackupStore.List(Path.Combine(_root, "missing"), false, false));
        }

        [Fact]
        public void List_Recursive_ParsesScriptsEditorBackups()
        {
            Make("npcs/Town/Guard.txt.20260102-030405.bak", Encoding.UTF8.GetBytes("[@MAIN]"));
            Make("drops/Hen.txt.20260102-030406.bak", Encoding.UTF8.GetBytes("1/2 Gold 10"));
            Make("odd.bak", new byte[] { 0 });

            var files = BackupStore.List(_root, true, false);
            Assert.Equal(3, files.Count);

            var guard = files.Single(f => f.Name == "npcs/Town/Guard.txt.20260102-030405.bak");
            Assert.Equal("script", guard.Origin);
            Assert.Equal("npcs", guard.ScriptRoot);
            Assert.Equal("Town/Guard.txt", guard.ScriptPath);
            Assert.Equal("20260102-030405", guard.Stamp);

            Assert.Null(files.Single(f => f.Name == "odd.bak").ScriptRoot);
        }

        [Fact]
        public void NewName_UsesCrystalStamp_AndAvoidsCollisions()
        {
            var when = new DateTime(2026, 7, 8, 9, 10, 11);
            var first = BackupStore.NewName(_root, "Manual", when);
            Assert.Equal("Manual 2026-07-08 09-10-11.bak", first);

            Make(first, new byte[] { 1 });
            Assert.Equal("Manual 2026-07-08 09-10-11 (2).bak", BackupStore.NewName(_root, "Manual", when));
        }

        [Fact]
        public void TryReadHeader_ReadsVersions_AndRejectsShortFiles()
        {
            var good = Make("good.bak", Header(117, 2, 0));
            var bad = Make("bad.bak", new byte[] { 1, 2, 3 });

            Assert.True(BackupStore.TryReadHeader(good, out var v, out var c));
            Assert.Equal(117, v);
            Assert.Equal(2, c);
            Assert.False(BackupStore.TryReadHeader(bad, out _, out _));
            Assert.False(BackupStore.TryReadHeader(Path.Combine(_root, "missing.bak"), out _, out _));
        }

        [Fact]
        public void StableCopy_CopiesAtomically_WithoutWaiting()
        {
            var src = Make("live.dat", Encoding.UTF8.GetBytes("hello world"));
            var dest = Path.Combine(_root, "copy.bak");
            var waits = 0;

            var copied = BackupStore.StableCopy(src, dest, 3, _ => waits++);

            Assert.Equal(11, copied);
            Assert.Equal("hello world", File.ReadAllText(dest));
            Assert.Equal(0, waits);
            Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
        }

        [Fact]
        public void StableCopy_RetriesThenFails_WhenSourceMissing()
        {
            var waits = new List<int>();
            Assert.Throws<FileNotFoundException>(() => BackupStore.StableCopy(Path.Combine(_root, "nope.dat"), Path.Combine(_root, "x.bak"), 3, a => waits.Add(a)));
            Assert.Equal(new[] { 1, 2 }, waits);
            Assert.False(File.Exists(Path.Combine(_root, "x.bak")));
        }

        [Fact]
        public void StableCopy_RetriesWhenTheSourceAppearsLate()
        {
            var src = Path.Combine(_root, "late.dat");
            var dest = Path.Combine(_root, "late.bak");

            // The source is missing on the first attempt (e.g. mid-swap during an accounts save) and present on the second.
            var copied = BackupStore.StableCopy(src, dest, 3, _ => File.WriteAllText(src, "abc"));

            Assert.Equal(3, copied);
            Assert.Equal("abc", File.ReadAllText(dest));
        }

        [Fact]
        public void ReplaceFile_OverwritesTarget()
        {
            var backup = Make("b.bak", Encoding.UTF8.GetBytes("restored"));
            var target = Make("Server.MirDB", Encoding.UTF8.GetBytes("current data"));

            BackupStore.ReplaceFile(backup, target);

            Assert.Equal("restored", File.ReadAllText(target));
            Assert.Equal("restored", File.ReadAllText(backup));
            Assert.Empty(Directory.GetFiles(_root, "*.restore-*"));
        }

        [Fact]
        public void WriteAllBytesAtomic_RefusesToOverwrite()
        {
            var dest = Path.Combine(_root, "Manual.bak");
            BackupStore.WriteAllBytesAtomic(dest, new byte[] { 1, 2, 3 });
            Assert.Equal(3, new FileInfo(dest).Length);

            Assert.ThrowsAny<IOException>(() => BackupStore.WriteAllBytesAtomic(dest, new byte[] { 9 }));
            Assert.Equal(3, new FileInfo(dest).Length);
        }

        [Fact]
        public void PlanPrune_DeletesOnlyOldFiles_AndKeepsTheNewest()
        {
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var files = new List<BackupFileEntry>
            {
                new BackupFileEntry { Name = "a.bak", ModifiedUtc = now.AddDays(-1), Size = 1 },
                new BackupFileEntry { Name = "b.bak", ModifiedUtc = now.AddDays(-10), Size = 2 },
                new BackupFileEntry { Name = "c.bak", ModifiedUtc = now.AddDays(-20), Size = 3 },
                new BackupFileEntry { Name = "d.bak", ModifiedUtc = now.AddDays(-40), Size = 4 },
            };

            var plan = BackupStore.PlanPrune(files, 7, now, 1);
            Assert.Equal(new[] { "b.bak", "c.bak", "d.bak" }, plan.Delete.Select(f => f.Name));
            Assert.Equal(new[] { "a.bak" }, plan.Keep.Select(f => f.Name));

            // Everything is old, but the newest two are always kept.
            var keep2 = BackupStore.PlanPrune(files, 0, now.AddYears(1), 2);
            Assert.Equal(new[] { "c.bak", "d.bak" }, keep2.Delete.Select(f => f.Name));

            var none = BackupStore.PlanPrune(files, 100, now, 1);
            Assert.Empty(none.Delete);
        }

        [Fact]
        public void RemoveEmptyDirectories_KeepsRootAndNonEmptyFolders()
        {
            Directory.CreateDirectory(Path.Combine(_root, "empty", "deeper"));
            Make("full/x.bak", new byte[] { 1 });

            BackupStore.RemoveEmptyDirectories(_root);

            Assert.True(Directory.Exists(_root));
            Assert.False(Directory.Exists(Path.Combine(_root, "empty")));
            Assert.True(Directory.Exists(Path.Combine(_root, "full")));
        }

        [Fact]
        public void OriginOf_ClassifiesNames()
        {
            Assert.Equal("manual", BackupStore.OriginOf("Manual 2026-01-01 00-00-00.bak"));
            Assert.Equal("pre-restore", BackupStore.OriginOf("Pre-restore 2026-01-01 00-00-00.bak"));
            Assert.Equal("auto", BackupStore.OriginOf("Database 2026-01-01 00-00-00.bak"));
            Assert.Equal("auto", BackupStore.OriginOf("Accounts 2026-01-01 00-00-00.bak"));
            Assert.Equal("other", BackupStore.OriginOf("random.bak"));
        }
    }
}
