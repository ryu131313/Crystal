using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    public class GuildMarketPagingTests
    {
        [Theory]
        [InlineData(0, 1, 50, 1, 50, 1, 0)]
        [InlineData(120, 2, 50, 2, 50, 3, 50)]
        [InlineData(120, 9, 50, 3, 50, 3, 100)]
        [InlineData(120, -4, 50, 1, 50, 3, 0)]
        [InlineData(10, 1, 0, 1, 50, 1, 0)]
        [InlineData(2000, 2, 10000, 2, 500, 4, 500)]
        public void Compute_ClampsPageAndSize(int total, int page, int pageSize, int expPage, int expSize, int expPages, int expSkip)
        {
            var r = GuildMarketPaging.Compute(total, page, pageSize);
            Assert.Equal(expPage, r.Page);
            Assert.Equal(expSize, r.PageSize);
            Assert.Equal(expPages, r.Pages);
            Assert.Equal(expSkip, r.Skip);
        }
    }

    public class GuildQueryTests
    {
        private static List<GuildRow> Sample()
        {
            return new List<GuildRow>
            {
                new GuildRow { Index = 3, Name = "Bravo", Leader = "Zed", Members = 10, Level = 2, Experience = 500, Gold = 100 },
                new GuildRow { Index = 1, Name = "alpha", Leader = "Kim", Members = 4, Level = 5, Experience = 10, Gold = 9000, OnlineMembers = 2 },
                new GuildRow { Index = 2, Name = "Charlie", Leader = "—", Members = 0, Level = 2, Experience = 900, Gold = 0 },
            };
        }

        [Fact]
        public void Apply_DefaultSortsByIndex()
        {
            var rows = GuildQuery.Apply(Sample(), null, null, null);
            Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Index));
        }

        [Fact]
        public void Apply_FiltersByNameLeaderOrIndex_CaseInsensitive()
        {
            Assert.Equal(new[] { 1 }, GuildQuery.Apply(Sample(), "ALPH", null, null).Select(r => r.Index));
            Assert.Equal(new[] { 3 }, GuildQuery.Apply(Sample(), "zed", null, null).Select(r => r.Index));
            Assert.Equal(new[] { 2 }, GuildQuery.Apply(Sample(), "2", null, null).Select(r => r.Index));
            Assert.Empty(GuildQuery.Apply(Sample(), "nobody", null, null));
        }

        [Fact]
        public void Apply_SortsByNameIgnoringCase()
        {
            Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, GuildQuery.Apply(Sample(), null, "name", "asc").Select(r => r.Name));
            Assert.Equal(new[] { "Charlie", "Bravo", "alpha" }, GuildQuery.Apply(Sample(), null, "name", "desc").Select(r => r.Name));
        }

        [Fact]
        public void Apply_LevelSortBreaksTiesByExperience()
        {
            Assert.Equal(new[] { 1, 2, 3 }, GuildQuery.Apply(Sample(), null, "level", "desc").Select(r => r.Index));
            Assert.Equal(new[] { 3, 2, 1 }, GuildQuery.Apply(Sample(), null, "level", "asc").Select(r => r.Index));
        }

        [Fact]
        public void Apply_SortsNumericColumns()
        {
            Assert.Equal(new[] { 1, 3, 2 }, GuildQuery.Apply(Sample(), null, "gold", "desc").Select(r => r.Index));
            Assert.Equal(new[] { 2, 1, 3 }, GuildQuery.Apply(Sample(), null, "members", "asc").Select(r => r.Index));
            Assert.Equal(1, GuildQuery.Apply(Sample(), null, "online", "desc")[0].Index);
        }

        [Fact]
        public void NormaliseNotice_SplitsLines_TrimsEnds_DropsTrailingBlanks()
        {
            var lines = GuildQuery.NormaliseNotice(new[] { "Welcome!  ", "Line two\r\nLine three", "", "  " });
            Assert.Equal(new[] { "Welcome!", "Line two", "Line three" }, lines);
        }

        [Fact]
        public void NormaliseNotice_KeepsInnerBlankLines_AndStripsControlChars()
        {
            var lines = GuildQuery.NormaliseNotice(new[] { "a", "", "b\u0001c" });
            Assert.Equal(new[] { "a", "", "b c" }, lines);
            Assert.Null(GuildQuery.NormaliseNotice(null));
            Assert.Empty(GuildQuery.NormaliseNotice(new string[0]));
        }

        [Fact]
        public void CleanMessage_FlattensToOneLine()
        {
            Assert.Equal("hello  world", GuildQuery.CleanMessage(" hello\r\nworld\t"));
            Assert.Equal(string.Empty, GuildQuery.CleanMessage(null));
        }

        [Theory]
        [InlineData(100L, 50L, false, 255L, true, 50L)]
        [InlineData(100L, 50L, true, 255L, true, 150L)]
        [InlineData(100L, -100L, true, 255L, true, 0L)]
        [InlineData(100L, -101L, true, 255L, false, 100L)]
        [InlineData(200L, 55L, true, 255L, true, 255L)]
        [InlineData(200L, 56L, true, 255L, false, 200L)]
        [InlineData(0L, -1L, false, 255L, false, 0L)]
        [InlineData(5L, long.MaxValue, true, long.MaxValue, false, 5L)]
        public void TryApplyAmount_SetAndAddWithinBounds(long current, long value, bool add, long max, bool ok, long expected)
        {
            Assert.Equal(ok, GuildQuery.TryApplyAmount(current, value, add, max, out var result));
            Assert.Equal(expected, result);
        }
    }

    public class MarketQueryTests
    {
        private static readonly DateTime Base = new DateTime(2026, 1, 10, 12, 0, 0);

        private static MarketRow Row(ulong id, string item, string seller, uint price, bool sold = false, bool expired = false, int daysAgo = 0, string buyer = null)
        {
            return new MarketRow
            {
                AuctionId = id,
                ItemName = item,
                Seller = seller,
                SellerAccount = seller?.ToLowerInvariant() + "_acc",
                Buyer = buyer,
                Price = price,
                CurrentBid = price,
                Sold = sold,
                Expired = expired,
                ConsignmentDate = Base.AddDays(-daysAgo),
                Expiry = MarketQuery.ExpiryOf(Base.AddDays(-daysAgo)),
                Status = MarketQuery.StatusOf(sold, expired),
            };
        }

        private static List<MarketRow> Sample()
        {
            return new List<MarketRow>
            {
                Row(1, "Dragon Slayer", "Alice", 900000, daysAgo: 3),
                Row(2, "Sun Potion (10)", "Bob", 5000, daysAgo: 1),
                Row(3, "Dragon Armour", "Bob", 750000, expired: true, daysAgo: 8),
                Row(4, "Ring of Speed", "Carol", 20000, sold: true, daysAgo: 2, buyer: "Dave"),
            };
        }

        [Fact]
        public void StatusOf_SoldWinsOverExpired()
        {
            Assert.Equal("active", MarketQuery.StatusOf(false, false));
            Assert.Equal("expired", MarketQuery.StatusOf(false, true));
            Assert.Equal("sold", MarketQuery.StatusOf(true, false));
            Assert.Equal("sold", MarketQuery.StatusOf(true, true));
        }

        [Fact]
        public void ExpiryOf_UsesConsignmentLength()
        {
            Assert.Equal(Base.AddDays(Globals.ConsignmentLength), MarketQuery.ExpiryOf(Base));
            Assert.Equal(DateTime.MaxValue, MarketQuery.ExpiryOf(DateTime.MaxValue.AddDays(-1)));
        }

        [Fact]
        public void Apply_DefaultsToActiveListings_NewestFirst()
        {
            var rows = MarketQuery.Apply(Sample(), null, null, null, null, null);
            Assert.Equal(new ulong[] { 2, 1 }, rows.Select(r => r.AuctionId));
        }

        [Fact]
        public void Apply_StatusFilters()
        {
            Assert.Equal(4, MarketQuery.Apply(Sample(), null, null, "all", null, null).Count);
            Assert.Equal(new ulong[] { 3 }, MarketQuery.Apply(Sample(), null, null, "expired", null, null).Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 4 }, MarketQuery.Apply(Sample(), null, null, "sold", null, null).Select(r => r.AuctionId));
        }

        [Fact]
        public void Apply_SearchByItemSellerOrAny()
        {
            Assert.Equal(new ulong[] { 1, 3 }, MarketQuery.Apply(Sample(), "dragon", "item", "all", "id", "asc").Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 2, 3 }, MarketQuery.Apply(Sample(), "BOB", "seller", "all", "id", "asc").Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 3 }, MarketQuery.Apply(Sample(), "bob_acc", "seller", "expired", null, null).Select(r => r.AuctionId));
            Assert.Empty(MarketQuery.Apply(Sample(), "bob", "item", "all", null, null));
            Assert.Equal(new ulong[] { 4 }, MarketQuery.Apply(Sample(), "dave", "any", "all", null, null).Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 4 }, MarketQuery.Apply(Sample(), "#4", null, "all", null, null).Select(r => r.AuctionId));
        }

        [Fact]
        public void Apply_ExplicitSortIsAscendingUnlessDesc()
        {
            Assert.Equal(new ulong[] { 2, 4, 3, 1 }, MarketQuery.Apply(Sample(), null, null, "all", "price", null).Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 1, 3, 4, 2 }, MarketQuery.Apply(Sample(), null, null, "all", "price", "desc").Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 1, 2, 3, 4 }, MarketQuery.Apply(Sample(), null, null, "all", "seller", "asc").Select(r => r.AuctionId));
            Assert.Equal(new ulong[] { 4, 2, 3, 1 }, MarketQuery.Apply(Sample(), null, null, "all", "seller", "desc").Select(r => r.AuctionId));
        }

        [Fact]
        public void Counts_ByStatus()
        {
            var json = WebAdminJson.Serialize(MarketQuery.Counts(Sample()));
            Assert.Contains("\"active\":2", json);
            Assert.Contains("\"expired\":1", json);
            Assert.Contains("\"sold\":1", json);
            Assert.Contains("\"all\":4", json);
        }

        [Fact]
        public void PlanRemoval_UnsoldConsign_ReturnsItem()
        {
            var plan = MarketQuery.PlanRemoval(MarketItemType.Consign, false, 1000, 1000, false, true);
            Assert.True(plan.ReturnItem);
            Assert.Equal(0u, plan.PayoutGold);
            Assert.Equal(0u, plan.RefundBid);
        }

        [Fact]
        public void PlanRemoval_UnsoldAuctionWithBidder_AlwaysRefundsBid()
        {
            var keep = MarketQuery.PlanRemoval(MarketItemType.Auction, false, 1000, 2500, true, true);
            Assert.True(keep.ReturnItem);
            Assert.Equal(2500u, keep.RefundBid);

            var destroy = MarketQuery.PlanRemoval(MarketItemType.Auction, false, 1000, 2500, true, false);
            Assert.False(destroy.ReturnItem);
            Assert.Equal(0u, destroy.PayoutGold);
            Assert.Equal(2500u, destroy.RefundBid);
        }

        [Fact]
        public void PlanRemoval_SoldListing_PaysOutEarningsAfterCommission()
        {
            var consign = MarketQuery.PlanRemoval(MarketItemType.Consign, true, 10000, 10000, true, true);
            Assert.False(consign.ReturnItem);
            Assert.Equal((uint)(10000 - 10000 * Globals.Commission), consign.PayoutGold);
            Assert.Equal(0u, consign.RefundBid);

            var auction = MarketQuery.PlanRemoval(MarketItemType.Auction, true, 1000, 20000, true, true);
            Assert.Equal((uint)(20000 - 20000 * Globals.Commission), auction.PayoutGold);
            Assert.Equal(0u, auction.RefundBid);

            var none = MarketQuery.PlanRemoval(MarketItemType.Consign, true, 10000, 10000, true, false);
            Assert.Equal(0u, none.PayoutGold);
            Assert.False(none.ReturnItem);
        }
    }

    public class GuildMarketRouteTests
    {
        [Fact]
        public void Routes_RegisterWithoutConflicts_AndMatch()
        {
            var router = new WebAdminRouter();
            new GuildRoutes().Register(router);
            new MarketRoutes().Register(router);
            new NamelistRoutes().Register(router);

            Assert.True(router.Match("GET", "/api/admin/guilds").Success);
            Assert.True(router.Match("GET", "/api/admin/guilds/7").Success);

            var remove = router.Match("POST", "/api/admin/guilds/12/members/remove");
            Assert.True(remove.Success);
            Assert.Equal("12", remove.Values["index"]);
            Assert.True(router.Match("POST", "/api/admin/guilds/12/notice").Success);
            Assert.True(router.Match("POST", "/api/admin/guilds/12/message").Success);
            Assert.True(router.Match("POST", "/api/admin/guilds/12/funds").Success);
            Assert.False(router.Match("GET", "/api/admin/guilds/abc").Success);

            Assert.True(router.Match("GET", "/api/admin/market").Success);
            Assert.Equal("123456789", router.Match("POST", "/api/admin/market/123456789/expire").Values["auctionId"]);
            Assert.True(router.Match("POST", "/api/admin/market/5/remove").Success);

            foreach (var path in new[] { "/api/admin/namelists", "/api/admin/namelists/file", "/api/admin/namelists/find" })
                Assert.True(router.Match("GET", path).Success, path);
            foreach (var verb in new[] { "create", "delete", "add", "remove" })
                Assert.True(router.Match("POST", "/api/admin/namelists/" + verb).Success, verb);

            Assert.Equal(RouteAuth.Admin, router.Match("POST", "/api/admin/namelists/add").Route.Auth);
            Assert.Equal(RouteAuth.Admin, router.Match("GET", "/api/admin/guilds").Route.Auth);
        }
    }

    public sealed class NamelistStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _root;
        private readonly string _backup;

        public NamelistStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mircraft-namelists-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_dir, "NameLists");
            _backup = Path.Combine(_dir, "Backup");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private NamelistStore Store() => new NamelistStore(_root, _backup);

        private static int StatusOf(Action a)
        {
            var ex = Assert.ThrowsAny<WebAdminException>(a);
            return ex.Status;
        }

        [Theory]
        [InlineData("winners", "winners.txt")]
        [InlineData("winners.txt", "winners.txt")]
        [InlineData("Events\\2026\\Spring.TXT", "Events/2026/Spring.TXT")]
        [InlineData("  Events/vip  ", "Events/vip.txt")]
        public void NormaliseRelative_Canonicalises(string input, string expected)
        {
            Assert.Equal(expected, NamelistStore.NormaliseRelative(input));
        }

        [Theory]
        [InlineData((string)null)]
        [InlineData("")]
        [InlineData("../escape")]
        [InlineData("a/../../escape")]
        [InlineData("a/./b")]
        [InlineData("/rooted")]
        [InlineData("\\\\server\\share\\x")]
        [InlineData("C:\\Windows\\win.ini")]
        [InlineData("list.txt:stream")]
        [InlineData("con")]
        [InlineData("sub/LPT1.txt")]
        [InlineData("list.ini")]
        [InlineData("trailing./x")]
        [InlineData("a//b")]
        [InlineData("bad*name")]
        public void NormaliseRelative_RejectsUnsafePaths(string input)
        {
            Assert.Equal(400, StatusOf(() => NamelistStore.NormaliseRelative(input)));
        }

        [Fact]
        public void ValidateName_TrimsAndRejectsControlChars()
        {
            Assert.Equal("Hero", NamelistStore.ValidateName("  Hero "));
            Assert.Equal(400, StatusOf(() => NamelistStore.ValidateName("   ")));
            Assert.Equal(422, StatusOf(() => NamelistStore.ValidateName("a\nb")));
            Assert.Equal(422, StatusOf(() => NamelistStore.ValidateName(new string('x', NamelistStore.MaxNameLength + 1))));
        }

        [Fact]
        public void CreateAddRemove_RoundTrip_WithDedupeAndTrailingNewline()
        {
            var store = Store();
            var rel = store.Normalise("Events/vip");

            store.Create(rel);
            Assert.Equal(409, StatusOf(() => store.Create(rel)));

            Assert.True(store.Add(rel, "Alice"));
            Assert.True(store.Add(rel, "Bob"));
            Assert.False(store.Add(rel, "Alice"));
            Assert.True(store.Add(rel, "alice")); // ordinal, like CHECKNAMELIST

            var file = store.Read(rel);
            Assert.Equal(new[] { "Alice", "Bob", "alice" }, file.Names);
            Assert.Equal(3, file.Count);

            var text = File.ReadAllText(Path.Combine(_root, "Events", "vip.txt"));
            Assert.EndsWith("\r\n", text);
            Assert.False(text.StartsWith("\uFEFF", StringComparison.Ordinal));

            Assert.Equal(1, store.Remove(rel, "Bob"));
            Assert.Equal(0, store.Remove(rel, "Bob"));
            Assert.Equal(new[] { "Alice", "alice" }, store.Read(rel).Names);

            Assert.True(File.Exists(Path.Combine(_backup, "Events", "vip.txt.bak")));
        }

        [Fact]
        public void Add_ToMissingList_Is404()
        {
            var store = Store();
            Assert.Equal(404, StatusOf(() => store.Add("missing.txt", "Alice")));
            Assert.Equal(404, StatusOf(() => store.Read("missing.txt")));
            Assert.Equal(404, StatusOf(() => store.Delete("missing.txt")));
        }

        [Fact]
        public void Remove_IgnoresSurroundingWhitespaceAndDropsBlankLines()
        {
            File.WriteAllText(Path.Combine(_root, "raw.txt"), "Alice\r\n  Bob  \r\n\r\nCarol");
            var store = Store();

            Assert.Equal(1, store.Remove("raw.txt", "Bob"));
            Assert.Equal("Alice\r\nCarol\r\n", File.ReadAllText(Path.Combine(_root, "raw.txt")));
        }

        [Fact]
        public void List_CountsTotalsAndUniqueNames()
        {
            File.WriteAllText(Path.Combine(_root, "a.txt"), "Alice\r\nBob\r\n");
            Directory.CreateDirectory(Path.Combine(_root, "Sub"));
            File.WriteAllText(Path.Combine(_root, "Sub", "b.txt"), "Bob\nCarol\n\n");
            File.WriteAllText(Path.Combine(_root, "ignored.ini"), "Zed\r\n");

            var result = Store().List();

            Assert.Equal(new[] { "a.txt", "Sub/b.txt" }, result.Lists.Select(l => l.Path));
            Assert.Equal(new[] { 2, 2 }, result.Lists.Select(l => l.Count));
            Assert.Equal(4, result.TotalNames);
            Assert.Equal(3, result.UniqueNames);
        }

        [Fact]
        public void List_MissingRoot_IsEmpty()
        {
            var store = new NamelistStore(Path.Combine(_dir, "nope"), null);
            var result = store.List();
            Assert.Empty(result.Lists);
            Assert.Equal(0, result.TotalNames);
            Assert.Empty(store.Find("x", false));
        }

        [Fact]
        public void Find_SubstringOrExact()
        {
            File.WriteAllText(Path.Combine(_root, "a.txt"), "Alice\r\nBob\r\n");
            File.WriteAllText(Path.Combine(_root, "b.txt"), "Alicia\r\n");
            var store = Store();

            var partial = store.Find("ali", false);
            Assert.Equal(new[] { "a.txt", "b.txt" }, partial.Select(h => h.Path));
            Assert.Equal(new[] { "Alicia" }, partial[1].Matches);

            var exact = store.Find("Alice", true);
            Assert.Single(exact);
            Assert.Equal("a.txt", exact[0].Path);

            Assert.Empty(store.Find("alice", true));
        }

        [Fact]
        public void Delete_MovesFileToBackup()
        {
            File.WriteAllText(Path.Combine(_root, "old.txt"), "Alice\r\nBob\r\n");
            var store = Store();

            var result = store.Delete("old.txt");

            Assert.Equal(2, result.Count);
            Assert.False(File.Exists(Path.Combine(_root, "old.txt")));
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));
            Assert.StartsWith(Path.GetFullPath(_backup), result.BackupPath, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(store.List().Lists);
        }

        [Fact]
        public void FullPathOf_StaysUnderRoot()
        {
            var store = Store();
            var full = store.FullPathOf("Sub/list.txt");
            Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Sub/list.txt", store.RelativeOf(full));
        }

        [Fact]
        public void ExtensionlessLists_AreListedAndResolvedVerbatim()
        {
            // NPC scripts use the path verbatim: "CHECKNAMELIST NewbieGuild" reads the file "NewbieGuild".
            File.WriteAllText(Path.Combine(_root, "NewbieGuild"), "Alice\r\n");
            File.WriteAllText(Path.Combine(_root, "other.txt"), "Bob\r\n");
            var store = Store();

            Assert.Equal(new[] { "NewbieGuild", "other.txt" }, store.List().Lists.Select(l => l.Path));
            Assert.Equal("NewbieGuild", store.Normalise("NewbieGuild"));
            Assert.Equal("other.txt", store.Normalise("other"));
            Assert.Equal("fresh.txt", store.Normalise("fresh"));
            Assert.Equal("fresh", store.Normalise("fresh", true));
            Assert.Equal("NewbieGuild", NamelistStore.NormaliseRelative("NewbieGuild", false));

            Assert.Equal(409, StatusOf(() => store.Create(store.Normalise("NewbieGuild"))));
            Assert.True(store.Add("NewbieGuild", "Carol"));
            Assert.Equal(new[] { "Alice", "Carol" }, store.Read("NewbieGuild").Names);
            Assert.Single(store.Find("Carol", true));
        }

        [Fact]
        public void Add_RepairsWhitespacePaddedLine_InsteadOfReportingPresent()
        {
            File.WriteAllText(Path.Combine(_root, "pad.txt"), "Alice\r\nBob \r\n  Bob\r\n");
            var store = Store();

            Assert.Equal(new[] { "Bob" }, store.Read("pad.txt").Untrimmed.Distinct());
            Assert.Equal(NamelistStore.AddOutcome.Repaired, store.AddName("pad.txt", "Bob"));
            Assert.Equal("Alice\r\nBob\r\n", File.ReadAllText(Path.Combine(_root, "pad.txt")));
            Assert.Equal(NamelistStore.AddOutcome.AlreadyPresent, store.AddName("pad.txt", "Bob"));
            Assert.Empty(store.Read("pad.txt").Untrimmed);
        }

        [Fact]
        public void OversizedFile_IsFlagged_NotFatalForListOrFind()
        {
            File.WriteAllText(Path.Combine(_root, "a.txt"), "Alice\r\n");
            using (var fs = new FileStream(Path.Combine(_root, "huge.txt"), FileMode.CreateNew))
            {
                fs.SetLength(NamelistStore.MaxFileBytes + 1);
            }

            var store = Store();
            var result = store.List();

            Assert.Equal(new[] { "a.txt", "huge.txt" }, result.Lists.Select(l => l.Path));
            Assert.True(result.Lists[1].TooLarge);
            Assert.False(result.Lists[0].TooLarge);
            Assert.Equal(1, result.TotalNames);
            Assert.Single(store.Find("Alice", true));
        }

        [Fact]
        public void Delete_Twice_KeepsBothBackups()
        {
            var store = Store();
            File.WriteAllText(Path.Combine(_root, "x.txt"), "One\r\n");
            var first = store.Delete("x.txt");
            File.WriteAllText(Path.Combine(_root, "x.txt"), "Two\r\n");
            var second = store.Delete("x.txt");

            Assert.NotEqual(first.BackupPath, second.BackupPath);
            Assert.Equal("One\r\n", File.ReadAllText(first.BackupPath));
            Assert.Equal("Two\r\n", File.ReadAllText(second.BackupPath));
        }
    }
}
