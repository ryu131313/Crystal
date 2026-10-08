namespace Server.WebAdmin.Tests
{
    public class AuditTests
    {
        private static (WebAdminAuditLog log, TestClock clock) Seed()
        {
            var clock = new TestClock();
            var log = new WebAdminAuditLog(null, 2000, clock.Now);

            log.Record("Admin1", "127.0.0.1", "accounts.ban", "account:5", new { days = 7 }, "spamming");
            clock.Advance(TimeSpan.FromMinutes(1));
            log.Record("admin2", "10.0.0.2", "accounts.unban", "account:5");
            clock.Advance(TimeSpan.FromMinutes(1));
            log.Record("Admin1", "127.0.0.1", "server.reboot", "world", new { note = "patch day" });
            clock.Advance(TimeSpan.FromMinutes(1));
            log.Record("Admin1", "127.0.0.1", "accountsx.other", "x");

            return (log, clock);
        }

        [Fact]
        public void Query_ReturnsNewestFirst_WithLimit()
        {
            var (log, _) = Seed();

            var all = log.Query(10);
            Assert.Equal(4, all.Count);
            Assert.Equal("accountsx.other", all[0].Action);
            Assert.Equal("accounts.ban", all[3].Action);

            var two = log.Query(2);
            Assert.Equal(2, two.Count);
            Assert.Equal("accountsx.other", two[0].Action);
        }

        [Fact]
        public void ActionFilter_ExactOrDottedPrefix()
        {
            var (log, _) = Seed();

            var group = log.Query(10, action: "accounts");
            Assert.Equal(2, group.Count);
            Assert.All(group, e => Assert.StartsWith("accounts.", e.Action));

            var exact = log.Query(10, action: "ACCOUNTS.BAN");
            Assert.Single(exact);
        }

        [Fact]
        public void AdminFilter_IsCaseInsensitiveExact()
        {
            var (log, _) = Seed();

            Assert.Equal(3, log.Query(10, admin: "admin1").Count);
            Assert.Single(log.Query(10, admin: "ADMIN2"));
            Assert.Empty(log.Query(10, admin: "admin"));
        }

        [Fact]
        public void TextFilter_SearchesTargetReasonAndDetails()
        {
            var (log, _) = Seed();

            Assert.Single(log.Query(10, q: "patch DAY"));
            Assert.Single(log.Query(10, q: "spamming"));
            Assert.Equal(2, log.Query(10, q: "account:5").Count);
            Assert.Single(log.Query(10, q: "10.0.0.2"));
            Assert.Empty(log.Query(10, q: "nothing-matches"));
        }

        [Fact]
        public void DaysFilter_ExcludesOldEntries()
        {
            var (log, clock) = Seed();

            clock.Advance(TimeSpan.FromDays(3));
            log.Record("Admin1", "127.0.0.1", "config.save", "Setup.ini");

            Assert.Single(log.Query(10, days: 1));
            Assert.Equal(5, log.Query(10, days: 7).Count);
            Assert.Equal(5, log.Query(10, days: 0).Count);
        }

        [Fact]
        public void Details_AreSnapshotted_NotLiveReferences()
        {
            var clock = new TestClock();
            var log = new WebAdminAuditLog(null, 100, clock.Now);
            var details = new Dictionary<string, object> { ["Gold"] = 10 };

            log.Record("a", "ip", "characters.edit", "char:1", details);
            details["Gold"] = 999;

            var entry = Assert.Single(log.Query(10));
            Assert.Contains("10", entry.Details.Value.GetRawText());
            Assert.DoesNotContain("999", entry.Details.Value.GetRawText());
            Assert.Null(log.Query(10)[0].Reason);
        }

        [Fact]
        public void Ring_IsBounded()
        {
            var log = new WebAdminAuditLog(null, 10, new TestClock().Now);
            for (var i = 0; i < 25; i++) log.Record("a", "ip", "x.y", "t" + i);

            Assert.Equal(10, log.Count);
            Assert.Equal("t24", log.Query(100)[0].Target);
        }

        [Fact]
        public void JsonlFiles_ArePersisted_AndReadBackWithoutDuplicates()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mircraft-audit-tests", Guid.NewGuid().ToString("N"));

            try
            {
                var clock = new TestClock(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc));
                var log = new WebAdminAuditLog(dir, 100, clock.Now);

                log.Record("gm", "127.0.0.1", "accounts.ban", "account:1", new { days = 1 });
                clock.Advance(TimeSpan.FromSeconds(5));
                log.Record("gm", "127.0.0.1", "accounts.unban", "account:1");

                var file = Path.Combine(dir, "audit-2026-03-04.jsonl");
                Assert.True(File.Exists(file));

                var lines = File.ReadAllLines(file);
                Assert.Equal(2, lines.Length);
                Assert.Contains("\"action\":\"accounts.ban\"", lines[0]);
                Assert.Contains("\"admin\":\"gm\"", lines[0]);
                Assert.Contains("\"ts\":\"2026-03-04T10:00:00Z\"", lines[0]);
                Assert.DoesNotContain("\"details\"", lines[1]);

                // Same instance: ring + disk must not duplicate.
                Assert.Equal(2, log.Query(10).Count);

                // Fresh instance (e.g. after a restart) reads the history from disk.
                var restarted = new WebAdminAuditLog(dir, 100, clock.Now);
                var fromDisk = restarted.Query(10, action: "accounts");
                Assert.Equal(2, fromDisk.Count);
                Assert.Equal("accounts.unban", fromDisk[0].Action);
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
    }
}
