using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    /// <summary>Pure account / security logic (no Envir, no Settings, no process-wide statics).</summary>
    public class AccountTests
    {
        private static readonly DateTime Now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        private static AccountSnapshot Acc(int index, string id, params string[] characters)
        {
            var a = new AccountSnapshot { Index = index, AccountId = id, CharacterCount = characters.Length };
            a.CharacterNames.AddRange(characters);
            return a;
        }

        private static List<AccountSnapshot> Sample()
        {
            var alice = Acc(1, "alice", "Warrior1", "Mage1");
            alice.Gold = 500;
            alice.Credit = 10;
            alice.LastIp = "10.0.0.1";
            alice.CreationIp = "10.0.0.9";
            alice.LastDate = Now.AddDays(-1);

            var bob = Acc(2, "bob", "Taoist");
            bob.Gold = 1000;
            bob.Credit = 5;
            bob.Banned = true;
            bob.LastIp = "10.0.0.1";
            bob.CreationIp = "10.0.0.1";
            bob.LastDate = Now.AddDays(-3);

            var admin = Acc(3, "gmboss", "GameMaster");
            admin.Admin = true;
            admin.Gold = 99999;
            admin.LastIp = "127.0.0.1";
            admin.Online = true;
            admin.OnlineCharacter = "GameMaster";

            var carol = Acc(4, "Carol", "WarriorX");
            carol.Gold = 1;
            carol.RequirePasswordChange = true;
            carol.LastIp = "192.168.1.5";
            carol.CreationIp = "10.0.0.9";

            return new List<AccountSnapshot> { carol, admin, bob, alice };
        }

        // ------------------------------------------------------------------ validation

        [Theory]
        [InlineData("abc", true)]
        [InlineData("Player123", true)]
        [InlineData("abcdefghijklmno", true)]   // 15
        [InlineData("ab", false)]
        [InlineData("abcdefghijklmnop", false)] // 16
        [InlineData("bad id", false)]
        [InlineData("näme", false)]
        [InlineData("", false)]
        public void AccountId_FollowsCrystalRules(string id, bool valid)
        {
            Assert.Equal(valid, AccountLogic.ValidateAccountId(id) == null);
        }

        [Theory]
        [InlineData("pass1", true)]
        [InlineData("abcdefghijklmno", true)]
        [InlineData("abcd", false)]
        [InlineData("abcdefghijklmnop", false)]
        [InlineData("with space", false)]
        [InlineData("p@ssword", false)]
        [InlineData(null, false)]
        public void Password_FollowsCrystalRules(string password, bool valid)
        {
            Assert.Equal(valid, AccountLogic.ValidatePassword(password) == null);
        }

        [Fact]
        public void Email_IsOptional_ButValidatedWhenGiven()
        {
            Assert.Null(AccountLogic.ValidateEmail(""));
            Assert.Null(AccountLogic.ValidateEmail(null));
            Assert.Null(AccountLogic.ValidateEmail("player@example.com"));
            Assert.NotNull(AccountLogic.ValidateEmail("not-an-email"));
            Assert.NotNull(AccountLogic.ValidateEmail(new string('a', 45) + "@x.com"));
        }

        [Fact]
        public void Length_IsChecked()
        {
            Assert.Null(AccountLogic.ValidateLength(new string('x', 20), 20, "User name"));
            Assert.Contains("User name", AccountLogic.ValidateLength(new string('x', 21), 20, "User name"));
            Assert.Null(AccountLogic.ValidateLength(null, 20, "User name"));
        }

        [Fact]
        public void BirthDate_ParsesIsoDates_AndEmptyMeansUnset()
        {
            Assert.Null(AccountLogic.ParseBirthDate("1990-12-31", out var d));
            Assert.Equal(new DateTime(1990, 12, 31), d);

            Assert.Null(AccountLogic.ParseBirthDate("  ", out d));
            Assert.Equal(DateTime.MinValue, d);

            Assert.NotNull(AccountLogic.ParseBirthDate("yesterday", out _));
            Assert.NotNull(AccountLogic.ParseBirthDate("1800-01-01", out _));
        }

        // ------------------------------------------------------------------ bans

        [Fact]
        public void BanExpiry_Permanent_IsMaxValue()
        {
            Assert.Null(AccountLogic.ResolveBanExpiry(Now, true, null, null, null, out var expiry));
            Assert.Equal(DateTime.MaxValue, expiry);
            Assert.True(AccountLogic.IsPermanent(expiry, Now));
        }

        [Fact]
        public void BanExpiry_Days_Minutes_Until()
        {
            Assert.Null(AccountLogic.ResolveBanExpiry(Now, false, 7, null, null, out var expiry));
            Assert.Equal(Now.AddDays(7), expiry);

            Assert.Null(AccountLogic.ResolveBanExpiry(Now, false, 0.5, null, null, out expiry));
            Assert.Equal(Now.AddHours(12), expiry);

            Assert.Null(AccountLogic.ResolveBanExpiry(Now, false, null, 90, null, out expiry));
            Assert.Equal(Now.AddMinutes(90), expiry);

            Assert.Null(AccountLogic.ResolveBanExpiry(Now, false, null, null, "2026-04-01T00:00:00Z", out expiry));
            Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), expiry);
            Assert.False(AccountLogic.IsPermanent(expiry, Now));
        }

        [Fact]
        public void BanExpiry_RejectsMissingAmbiguousAndPastValues()
        {
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, null, null, null, out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, true, 3, null, null, out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, 0, null, null, out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, -1, null, null, out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, null, 0, null, out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, null, null, "2020-01-01", out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, null, null, "not a date", out _));
            Assert.NotNull(AccountLogic.ResolveBanExpiry(Now, false, 1e9, null, null, out _));
        }

        [Fact]
        public void BanReason_DefaultsAndIsClipped()
        {
            Assert.Equal("Banned by an administrator", AccountLogic.NormalizeBanReason("  "));
            Assert.Equal("Botting", AccountLogic.NormalizeBanReason(" Botting "));
            Assert.Equal(AccountLogic.MaxBanReasonLength, AccountLogic.NormalizeBanReason(new string('r', 500)).Length);
        }

        [Fact]
        public void BanActive_MatchesEnvirLogin()
        {
            Assert.True(AccountLogic.IsBanActive(true, Now.AddMinutes(1), Now));
            Assert.False(AccountLogic.IsBanActive(true, Now, Now));          // Login: ExpiryDate > Now
            Assert.False(AccountLogic.IsBanActive(false, DateTime.MaxValue, Now));
        }

        // ------------------------------------------------------------------ currency

        [Fact]
        public void Currency_Add_ClampsToUintRange()
        {
            Assert.Null(AccountLogic.ApplyCurrency(100, 50, "add", out var next));
            Assert.Equal(150u, next);

            Assert.Null(AccountLogic.ApplyCurrency(100, -500, "add", out next));
            Assert.Equal(0u, next);

            Assert.Null(AccountLogic.ApplyCurrency(uint.MaxValue - 1, 10, "ADD", out next));
            Assert.Equal(uint.MaxValue, next);
        }

        [Fact]
        public void Currency_Set_ValidatesRange()
        {
            Assert.Null(AccountLogic.ApplyCurrency(100, 7, "set", out var next));
            Assert.Equal(7u, next);

            Assert.NotNull(AccountLogic.ApplyCurrency(100, -1, "set", out next));
            Assert.Equal(100u, next);
            Assert.NotNull(AccountLogic.ApplyCurrency(100, (long)uint.MaxValue + 1, "set", out _));
            Assert.NotNull(AccountLogic.ApplyCurrency(100, 1, "multiply", out _));
        }

        // ------------------------------------------------------------------ list query

        [Fact]
        public void Query_SearchByAccount_ContainsOrExact()
        {
            var r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "AL" });
            Assert.Equal(new[] { 1 }, r.Rows.Select(a => a.Index));

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "o" });
            Assert.Equal(new[] { 2, 3, 4 }, r.Rows.Select(a => a.Index));

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "carol", Exact = true });
            Assert.Equal(new[] { 4 }, r.Rows.Select(a => a.Index));

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "caro", Exact = true });
            Assert.Empty(r.Rows);
        }

        [Fact]
        public void Query_SearchByCharacterAndIp()
        {
            var r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "warrior", Mode = "player" });
            Assert.Equal(new[] { 1, 4 }, r.Rows.Select(a => a.Index));

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "10.0.0.1", Mode = "ip", Exact = true });
            Assert.Equal(new[] { 1, 2 }, r.Rows.Select(a => a.Index));

            // IP mode also matches the creation IP.
            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Q = "10.0.0.9", Mode = "ip", Exact = true });
            Assert.Equal(new[] { 1, 4 }, r.Rows.Select(a => a.Index));
        }

        [Theory]
        [InlineData("banned", new[] { 2 })]
        [InlineData("admin", new[] { 3 })]
        [InlineData("online", new[] { 3 })]
        [InlineData("requirepw", new[] { 4 })]
        [InlineData("all", new[] { 1, 2, 3, 4 })]
        [InlineData("nonsense", new[] { 1, 2, 3, 4 })]
        public void Query_Filters(string filter, int[] expected)
        {
            var r = AccountLogic.Query(Sample(), new AccountQueryOptions { Filter = filter });
            Assert.Equal(expected, r.Rows.Select(a => a.Index).ToArray());
        }

        [Fact]
        public void Query_SortsWithStableTieBreak()
        {
            var r = AccountLogic.Query(Sample(), new AccountQueryOptions { Sort = "gold", Desc = true });
            Assert.Equal(new[] { 3, 2, 1, 4 }, r.Rows.Select(a => a.Index));

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Sort = "accountId" });
            Assert.Equal(new[] { "alice", "bob", "Carol", "gmboss" }, r.Rows.Select(a => a.AccountId));

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Sort = "lastIp" });
            Assert.Equal(new[] { 1, 2, 3, 4 }, r.Rows.Select(a => a.Index)); // 10.0.0.1 (1, 2 by index), 127..., 192...

            r = AccountLogic.Query(Sample(), new AccountQueryOptions { Sort = "index", Desc = true });
            Assert.Equal(new[] { 4, 3, 2, 1 }, r.Rows.Select(a => a.Index));
        }

        [Fact]
        public void Query_PagesAndClamps()
        {
            var many = Enumerable.Range(1, 120).Select(i => Acc(i, "acc" + i)).ToList();

            var r = AccountLogic.Query(many, new AccountQueryOptions { PageSize = 50, Page = 2 });
            Assert.Equal(120, r.Total);
            Assert.Equal(3, r.Pages);
            Assert.Equal(2, r.Page);
            Assert.Equal(51, r.Rows[0].Index);
            Assert.Equal(50, r.Rows.Count);

            r = AccountLogic.Query(many, new AccountQueryOptions { PageSize = 50, Page = 99 });
            Assert.Equal(3, r.Page);
            Assert.Equal(20, r.Rows.Count);

            r = AccountLogic.Query(many, new AccountQueryOptions { PageSize = 100000, Page = -4 });
            Assert.Equal(AccountLogic.MaxPageSize, r.PageSize);
            Assert.Equal(1, r.Page);

            r = AccountLogic.Query(new List<AccountSnapshot>(), new AccountQueryOptions());
            Assert.Equal(0, r.Total);
            Assert.Equal(1, r.Pages);
            Assert.Empty(r.Rows);
        }

        [Fact]
        public void Totals_ExcludeAdminsAndBanned()
        {
            var (gold, credit) = AccountLogic.Totals(Sample());
            Assert.Equal(501UL, gold);   // alice 500 + carol 1 (bob banned, gmboss admin)
            Assert.Equal(10UL, credit);
        }

        // ------------------------------------------------------------------ security helpers

        [Fact]
        public void SharedIps_GroupsLastAndCreationIps()
        {
            var groups = AccountLogic.SharedIps(Sample());

            var g1 = Assert.Single(groups, g => g.Ip == "10.0.0.1");
            Assert.Equal(2, g1.Count);
            Assert.Equal("last", g1.Accounts.Single(a => a.Index == 1).Via);
            Assert.Equal("both", g1.Accounts.Single(a => a.Index == 2).Via);
            Assert.True(g1.Accounts.Single(a => a.Index == 2).Banned);

            var g2 = Assert.Single(groups, g => g.Ip == "10.0.0.9");
            Assert.Equal(new[] { 1, 4 }, g2.Accounts.Select(a => a.Index).OrderBy(i => i));

            Assert.DoesNotContain(groups, g => g.Ip == "127.0.0.1" || g.Ip == "192.168.1.5");
        }

        [Fact]
        public void SharedIps_IgnoresBlankAndInvalidAddresses()
        {
            var a = Acc(1, "a");
            var b = Acc(2, "b");
            a.LastIp = b.LastIp = "";
            a.CreationIp = b.CreationIp = "WebAdmin";
            Assert.Empty(AccountLogic.SharedIps(new[] { a, b }));
        }

        [Theory]
        [InlineData("1.2.3.4", "1.2.3.4")]
        [InlineData(" 10.0.0.1 ", "10.0.0.1")]
        [InlineData("::ffff:1.2.3.4", "1.2.3.4")]
        [InlineData("10", null)]
        [InlineData("1.2.3", null)]
        [InlineData("::1", null)]
        [InlineData("not-an-ip", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void NormalizeIp_AcceptsDottedIpv4Only(string input, string expected)
        {
            Assert.Equal(expected, AccountLogic.NormalizeIp(input));
        }

        [Fact]
        public void SessionKey_IsStableShortAndDistinct()
        {
            var k1 = AccountLogic.SessionKey("abc123");
            Assert.Equal(16, k1.Length);
            Assert.Matches("^[0-9a-f]{16}$", k1);
            Assert.Equal(k1, AccountLogic.SessionKey("abc123"));
            Assert.NotEqual(k1, AccountLogic.SessionKey("abc124"));
            Assert.DoesNotContain("abc123", k1);
            Assert.Equal(string.Empty, AccountLogic.SessionKey(null));
        }

        [Fact]
        public void Iso_HidesSentinelsAndMarksUtc()
        {
            Assert.Null(AccountLogic.Iso(DateTime.MinValue));
            Assert.Null(AccountLogic.Iso(DateTime.MaxValue));

            var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
            var iso = AccountLogic.Iso(unspecified);
            Assert.Equal(DateTimeKind.Utc, iso.Value.Kind);
            Assert.Equal(unspecified.Ticks, iso.Value.Ticks);
        }
    }

    /// <summary>Login log store: ring, filters, JSONL persistence, aggregation, audit mapping.</summary>
    public class AccountLoginLogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mircraft-logins-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        private static (WebAdminLoginLogStore store, TestClock clock) Seed(string dir = null)
        {
            var clock = new TestClock();
            var store = new WebAdminLoginLogStore(dir, 100, clock.Now) { BackgroundWrites = false };

            store.Record("alice", "10.0.0.1", "ok");
            clock.Advance(TimeSpan.FromMinutes(1));
            store.Record("alice", "10.0.0.2", "bad-password");
            clock.Advance(TimeSpan.FromMinutes(1));
            store.Record("bob", "10.0.0.2", "banned");
            clock.Advance(TimeSpan.FromMinutes(1));
            store.Record("ghost", "10.0.0.3", "no-account");
            clock.Advance(TimeSpan.FromMinutes(1));
            store.Record("carol", "10.0.0.2", "refused:Maintenance");
            clock.Advance(TimeSpan.FromMinutes(1));
            store.Record("admin", "127.0.0.1", "ok", "web");

            return (store, clock);
        }

        [Fact]
        public void Query_NewestFirst_WithResultFilters()
        {
            var (store, clock) = Seed();
            var since = clock.UtcNow.AddDays(-1);

            var all = store.Query(null, null, since, 100);
            Assert.Equal(6, all.Count);
            Assert.Equal("admin", all[0].AccountId);
            Assert.Equal("alice", all[5].AccountId);

            Assert.Equal(new[] { "admin", "alice" }, store.Query(null, "ok", since, 100).Select(e => e.AccountId));
            Assert.Equal(4, store.Query(null, "failed", since, 100).Count);
            Assert.Equal("carol", Assert.Single(store.Query(null, "refused", since, 100)).AccountId);
            Assert.Equal("bob", Assert.Single(store.Query(null, "BANNED", since, 100)).AccountId);
        }

        [Fact]
        public void Query_TextSourceWindowAndLimit()
        {
            var (store, clock) = Seed();
            var since = clock.UtcNow.AddDays(-1);

            Assert.Equal(3, store.Query("10.0.0.2", null, since, 100).Count);
            Assert.Equal(2, store.Query("ALICE", null, since, 100).Count);
            Assert.Equal("admin", Assert.Single(store.Query(null, null, since, 100, "web")).AccountId);
            Assert.Equal(5, store.Query(null, null, since, 100, "game").Count);
            Assert.Equal(6, store.Query(null, null, since, 100, "all").Count);

            // Only the last two minutes: carol (refused) and the web sign-in.
            Assert.Equal(2, store.Query(null, null, clock.UtcNow.AddMinutes(-1), 100).Count);

            Assert.Equal(2, store.Query(null, null, since, 2).Count);
        }

        [Fact]
        public void Ring_DropsOldestBeyondCapacity()
        {
            var clock = new TestClock();
            var store = new WebAdminLoginLogStore(null, 10, clock.Now);

            for (var i = 0; i < 25; i++)
            {
                store.Record("acc" + i, "1.1.1.1", "ok");
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(10, store.Count);
            var all = store.Query(null, null, DateTime.MinValue, 100);
            Assert.Equal("acc24", all[0].AccountId);
            Assert.Equal("acc15", all[9].AccountId);
        }

        [Fact]
        public void Record_TrimsAndDefaults()
        {
            var store = new WebAdminLoginLogStore(null, 10, new TestClock().Now);
            var e = store.Record(" bob ", null, null);

            Assert.Equal("bob", e.AccountId);
            Assert.Equal(string.Empty, e.Ip);
            Assert.Equal("unknown", e.Result);
            Assert.Equal("game", e.Source);
            Assert.False(string.IsNullOrEmpty(e.Id));
            Assert.Equal(DateTimeKind.Utc, e.Utc.Kind);
        }

        [Fact]
        public void Files_ArePersistedAndReadBack()
        {
            var (store, clock) = Seed(_dir);

            var files = Directory.GetFiles(_dir, "logins-*.jsonl");
            Assert.Single(files);
            Assert.EndsWith("logins-2026-01-01.jsonl", files[0]);
            Assert.Equal(6, File.ReadAllLines(files[0]).Count(l => l.Trim().Length > 0));

            // A fresh store (new process) with an empty ring reads the history from the daily files.
            var fresh = new WebAdminLoginLogStore(_dir, 100, clock.Now) { BackgroundWrites = false };
            var fromFiles = fresh.Query("10.0.0.2", "failed", clock.UtcNow.AddDays(-2), 100);
            Assert.Equal(new[] { "carol", "bob", "alice" }, fromFiles.Select(e => e.AccountId));

            // The original store never duplicates ring entries with their file copies.
            Assert.Equal(6, store.Query(null, null, clock.UtcNow.AddDays(-2), 100).Count);
        }

        [Fact]
        public void Files_SurviveCorruptLines()
        {
            var (_, clock) = Seed(_dir);
            File.AppendAllText(Directory.GetFiles(_dir, "logins-*.jsonl")[0], "{ this is not json\n");

            var fresh = new WebAdminLoginLogStore(_dir, 100, clock.Now);
            Assert.Equal(6, fresh.Query(null, null, clock.UtcNow.AddDays(-2), 100).Count);
        }

        [Fact]
        public void BackgroundWrites_EventuallyFlush()
        {
            var clock = new TestClock();
            var store = new WebAdminLoginLogStore(_dir, 100, clock.Now);
            for (var i = 0; i < 20; i++) store.Record("acc" + i, "2.2.2.2", "ok");

            store.Flush(); // drains anything the background flusher has not written yet
            var path = Path.Combine(_dir, "logins-2026-01-01.jsonl");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            int lines;
            do
            {
                lines = File.Exists(path) ? File.ReadAllLines(path).Count(l => l.Trim().Length > 0) : 0;
                if (lines == 20) break;
                Thread.Sleep(20);
            } while (DateTime.UtcNow < deadline);

            Assert.Equal(20, lines);
        }

        [Fact]
        public void GroupFailuresByIp_CountsAndOrders()
        {
            var (store, clock) = Seed();
            var groups = WebAdminLoginLogStore.GroupFailuresByIp(store.Query(null, null, clock.UtcNow.AddDays(-1), 100));

            Assert.Equal(2, groups.Count);
            Assert.Equal("10.0.0.2", groups[0].Ip);
            Assert.Equal(3, groups[0].Count);
            Assert.Equal(new[] { "carol", "bob", "alice" }, groups[0].Accounts);
            Assert.Equal("10.0.0.3", groups[1].Ip);
            Assert.DoesNotContain(groups, g => g.Ip == "10.0.0.1"); // only a success there
        }

        [Theory]
        [InlineData("ok", "ok", true)]
        [InlineData("bad-password", "ok", false)]
        [InlineData("bad-password", "failed", true)]
        [InlineData("ok", "failed", false)]
        [InlineData("refused:Maintenance", "refused", true)]
        [InlineData("refusedx", "refused", false)]
        [InlineData("anything", "", true)]
        [InlineData("anything", "all", true)]
        public void ResultFilter(string result, string filter, bool expected)
        {
            Assert.Equal(expected, WebAdminLoginLogStore.MatchesResult(result, filter));
        }

        [Fact]
        public void FromAudit_MapsWebSignIns()
        {
            var ok = WebAdminLoginLog.FromAudit(new WebAdminAuditEntry
            {
                Id = "a1", Ts = new DateTime(2026, 1, 1), Admin = "boss", Ip = "127.0.0.1", Action = "auth.login", Target = "boss",
            });
            Assert.Equal("ok", ok.Result);
            Assert.Equal("web", ok.Source);
            Assert.Equal("boss", ok.AccountId);
            Assert.Equal(DateTimeKind.Utc, ok.Utc.Kind);

            var failed = WebAdminLoginLog.FromAudit(new WebAdminAuditEntry
            {
                Id = "a2", Ts = new DateTime(2026, 1, 1), Admin = "eve", Ip = "6.6.6.6", Action = "auth.login-failed", Target = "eve",
                Details = WebAdminJson.ToElement(new { reason = "bad-password", online = true }),
            });
            Assert.Equal("bad-password", failed.Result);

            var throttled = WebAdminLoginLog.FromAudit(new WebAdminAuditEntry { Id = "a3", Action = "auth.login-throttled", Target = "eve" });
            Assert.Equal("throttled", throttled.Result);

            Assert.Null(WebAdminLoginLog.FromAudit(new WebAdminAuditEntry { Id = "a4", Action = "auth.logout", Target = "boss" }));
            Assert.Null(WebAdminLoginLog.FromAudit(new WebAdminAuditEntry { Id = "a5", Action = "account.ban", Target = "x" }));
            Assert.Null(WebAdminLoginLog.FromAudit(null));
        }
    }
}
