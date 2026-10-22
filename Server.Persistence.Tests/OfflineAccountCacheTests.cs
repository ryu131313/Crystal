using System.Reflection;
using System.Runtime.CompilerServices;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using Server.MirNetwork;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// P9-12 gate: with [Optimisation] OfflineAccountCache the periodic save writes the same Server.MirADB bytes as a full save,
/// after mutation sequences driven through the real entry points where they need no PlayerObject (mail, rental return,
/// auction win, login screens), and through the same writes plus the same mark where they do (friends, mentor). Also: hot,
/// select-screen-only and timed accounts are never cached, full saves / loads / restores reset the cache, an off-thread
/// epoch bump during saves loses nothing, and Verify / the periodic compare catch an unmarked mutation.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class OfflineAccountCacheTests
{
    private const int ExtraAccounts = 40;

    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public OfflineAccountCacheTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static readonly MethodInfo SaveToWriter = typeof(Envir).GetMethod("SaveAccounts",
        BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(BinaryWriter) }, null)!;

    /// <summary>The full file as a full save would write it now, without touching the cache or the files.</summary>
    private static byte[] Reference()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, BufferBinaryWriterTests.SaveEncoding, true))
            SaveToWriter.Invoke(Envir.Main, new object[] { w });
        return ms.ToArray();
    }

    private static byte[] PeriodicSave(OfflineAccountCacheMode mode, int fullEvery = 1000, int verifyPerSave = 200)
    {
        var old = (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery, Settings.OfflineAccountCacheVerifyPerSave);
        Settings.AccountSaveOffThread = true;
        Settings.OfflineAccountCache = mode;
        Settings.OfflineAccountCacheFullEvery = fullEvery;
        Settings.OfflineAccountCacheVerifyPerSave = verifyPerSave;
        try
        {
            Envir.Main.BeginSaveAccounts();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Envir.Main.Saving && sw.ElapsedMilliseconds < 10000) Thread.Sleep(2);
            Assert.False(Envir.Main.Saving, "account save did not complete");
        }
        finally
        {
            (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery, Settings.OfflineAccountCacheVerifyPerSave) = old;
        }
        var bytes = File.ReadAllBytes(Envir.AccountPath);
        Assert.Equal(bytes.Length, Envir.LastAccountSaveBytes);
        return bytes;
    }

    private static AccountCacheStats AssertCachedSaveMatches(string step, OfflineAccountCacheMode mode = OfflineAccountCacheMode.On, int fullEvery = 1000)
    {
        var expected = Reference();
        var actual = PeriodicSave(mode, fullEvery);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "cached save differs from a full save after: " + step);
        return AccountSaveCache.Last!;
    }

    private static void BuildWorld()
    {
        EnsureBuffInfo();
        ResetMain();
        var e = Envir.Main;
        SyntheticWorld.Populate(e);
        var now = DateTime.UtcNow;
        var potion = e.ItemInfoList[2];
        for (var i = 0; i < ExtraAccounts; i++)
        {
            var acc = SyntheticWorld.NewAccount(4 + i, "bot" + i, now);
            acc.Gold = (uint)i;
            var c = SyntheticWorld.NewCharacter(3 + i, "Bot" + i, MirClass.Warrior, acc, now);
            c.Inventory[0] = SyntheticWorld.Item(e, potion, (ulong)(1000 + i), count: 3);
            acc.Characters.Add(c);
            e.AccountList.Add(acc);
            e.CharacterList.Add(c);
        }
        e.NextAccountID = 4 + ExtraAccounts;
        e.NextCharacterID = 3 + ExtraAccounts;
        e.NextUserItemID = 5000;
        SaveAll();
        Assert.True(LoadAll());
    }

    private static void Run(string tag, Action body)
    {
        var dir = NewTempFolder(tag);
        try
        {
            InFolder(dir, () =>
            {
                BuildWorld();
                var failures = Envir.AccountSaveFailures;
                body();
                Assert.Equal(failures, Envir.AccountSaveFailures);
                PeriodicSave(OfflineAccountCacheMode.Off); // drops the blobs again for the other tests
                return 0;
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static CharacterInfo Char(string name) => Envir.Main.GetCharacterInfo(name);

    private static MirConnection FakeConnection() => (MirConnection)RuntimeHelpers.GetUninitializedObject(typeof(MirConnection));

    private static UserItem NewItem(int infoIndex = 3, ushort count = 1)
    {
        var e = Envir.Main;
        var info = e.GetItemInfo(infoIndex);
        return new UserItem(info) { UniqueID = ++e.NextUserItemID, ItemIndex = info.Index, Count = count, CurrentDura = 10, MaxDura = 10 };
    }

    // ---- real entry points (no PlayerObject needed) ----

    private static void SendMail(CharacterInfo to, bool withItem)
    {
        var mail = new MailInfo(to.Index, true) { Sender = "Bot0", Message = "hi", Gold = 3, SenderIndex = 3 };
        if (withItem) mail.Items.Add(NewItem());
        mail.Send();
    }

    private static void RentalReturn(CharacterInfo owner, CharacterInfo renter)
    {
        var e = Envir.Main;
        var item = NewItem(1);
        item.RentalInformation = new RentalInformation { OwnerName = owner.Name, ExpiryDate = e.Now.AddDays(-1), BindingFlags = BindMode.DontDrop };
        // Set-up writes are marked like the rental trade marks nothing: both players are online then (sticky hot bit).
        Envir.MarkAccountDirty(owner.AccountInfo);
        Envir.MarkAccountDirty(renter.AccountInfo);
        for (var i = 0; i < renter.Inventory.Length; i++)
            if (renter.Inventory[i] == null) { renter.Inventory[i] = item; break; }
        renter.HasRentedItem = true;
        owner.RentedItems.Add(new ItemRentalInformation { ItemId = item.UniqueID, ItemName = "x", RentingPlayerName = renter.Name, ItemReturnDate = e.Now.AddDays(-1) });
        return;
    }

    private static void ProcessRentedItems() =>
        typeof(Envir).GetMethod("ProcessRentedItems", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Envir.Main, null);

    private static void AuctionWin(CharacterInfo seller, CharacterInfo buyer)
    {
        var e = Envir.Main;
        var info = new AuctionInfo(seller, NewItem(1), 100, MarketItemType.Auction)
        {
            ConsignmentDate = e.Now.AddDays(-Globals.ConsignmentLength - 1),
            CurrentBid = 150,
            CurrentBuyerIndex = buyer.Index,
            CurrentBuyerInfo = buyer,
        };
        e.Auctions.AddLast(info);
        seller.AccountInfo.Auctions.AddLast(info);
        typeof(Envir).GetMethod("ProcessAuction", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(e, null);
        Assert.True(info.Sold);
    }

    // ---- PlayerObject paths: the same writes and the same mark as the handler ----

    private static void RemoveFriendOtherSide(CharacterInfo me, CharacterInfo other) // PlayerObject.RemoveFriend, other side
    {
        for (var i = 0; i < other.Friends.Count; i++)
        {
            if (other.Friends[i].Index != me.Index) continue;
            Envir.MarkAccountDirty(other.AccountInfo);
            other.Friends.RemoveAt(i);
            return;
        }
    }

    private static void LogoutMentor(CharacterInfo mentor, long menteeExp) // PlayerObject.LogoutMentor
    {
        Envir.MarkAccountDirty(mentor.AccountInfo);
        mentor.MentorExp += menteeExp;
    }

    [Fact]
    public void Cached_save_is_byte_identical_and_reuses_offline_accounts()
    {
        Run("p912-basic", () =>
        {
            var first = AssertCachedSaveMatches("first cached save (builds the cache)");
            Assert.Equal(0, first.Reused());
            Assert.Equal(Envir.Main.AccountList.Count, first.Fresh);

            var second = AssertCachedSaveMatches("no change");
            Assert.Equal(Envir.Main.AccountList.Count, second.Reused());
            Assert.Equal(0, second.Fresh);
            Assert.Equal(0, second.Mismatch);

            SendMail(Char("Bot5"), withItem: true);
            Envir.Main.MailCharacter(Char("Bot6"), gold: 9, customMessage: "refund");
            var third = AssertCachedSaveMatches("mail to offline recipients");
            Assert.Equal(2, third.Fresh);

            // what a missed mark costs: an unmarked offline write is invisible to a cached save
            Char("Bot7").Experience += 1;
            Assert.False(Reference().AsSpan().SequenceEqual(PeriodicSave(OfflineAccountCacheMode.On)), "the cache did not cache");
        });
    }

    [Fact]
    public void Rental_return_auction_win_friends_and_mentor_reach_the_file()
    {
        Run("p912-paths", () =>
        {
            var e = Envir.Main;
            // friends both ways, mentor pair: set up while "online" (marked), then cached
            Envir.MarkAccountDirty(Char("Bot1").AccountInfo);
            Envir.MarkAccountDirty(Char("Bot2").AccountInfo);
            Char("Bot1").Friends.Add(new FriendInfo(Char("Bot2"), false));
            Char("Bot2").Friends.Add(new FriendInfo(Char("Bot1"), false));
            Char("Bot3").Mentor = Char("Bot4").Index; Char("Bot3").IsMentor = true;
            Char("Bot4").Mentor = Char("Bot3").Index;
            Envir.MarkAccountDirty(Char("Bot3").AccountInfo);
            Envir.MarkAccountDirty(Char("Bot4").AccountInfo);
            RentalReturn(Char("Bot10"), Char("Bot11"));
            AssertCachedSaveMatches("set-up");
            Assert.Equal(e.AccountList.Count, AssertCachedSaveMatches("cache warm").Reused());

            ProcessRentedItems();
            Assert.Null(Char("Bot11").Inventory[1]);
            Assert.Single(Char("Bot10").Mail);
            var s = AssertCachedSaveMatches("rental return (borrower + lender offline)");
            Assert.Equal(2, s.Fresh);

            AuctionWin(Char("Bot12"), Char("Bot13"));
            s = AssertCachedSaveMatches("auction won by an offline buyer");
            Assert.Equal(1, s.Fresh);

            RemoveFriendOtherSide(Char("Bot1"), Char("Bot2"));
            LogoutMentor(Char("Bot3"), 77);
            s = AssertCachedSaveMatches("friend removed on the offline side, mentee exp to an offline mentor");
            Assert.Equal(2, s.Fresh);
        });
    }

    [Fact]
    public void Login_screen_paths_mark_only_real_changes()
    {
        Run("p912-login", () =>
        {
            var e = Envir.Main;
            var acc = Char("Bot20").AccountInfo;
            acc.BanReason = "old"; acc.ExpiryDate = e.Now.AddDays(-1); acc.Banned = true;
            Envir.MarkAccountDirty(acc);
            AssertCachedSaveMatches("set-up");
            AssertCachedSaveMatches("warm");

            // C.Login with a wrong password from an unauthenticated client: the expired ban is cleared before the check
            var c = FakeConnection();
            e.Login(new ClientPackets.Login { AccountID = "bot20", Password = "nopenope1" }, c);
            Assert.False(acc.Banned);
            var s = AssertCachedSaveMatches("expired ban cleared by a login attempt");
            Assert.Equal(1, s.Fresh);

            // nothing to clear any more: no mark, the account stays cached
            e.Login(new ClientPackets.Login { AccountID = "bot20", Password = "nopenope1" }, FakeConnection());
            s = AssertCachedSaveMatches("second login attempt");
            Assert.Equal(0, s.Fresh);
        });
    }

    [Fact]
    public void Online_select_screen_only_and_timed_accounts_are_never_cached()
    {
        Run("p912-hot", () =>
        {
            AssertCachedSaveMatches("build");
            var acc = Char("Bot30").AccountInfo;
            var ch = Char("Bot30");

            // login (CompleteLogin: Connection + sticky hot bit), writes while online, logout writes after the connection is gone
            acc.Connection = FakeConnection();
            acc.SaveHot = true;
            ch.Experience += 5;
            var s = AssertCachedSaveMatches("online");
            Assert.Null(acc.SaveBlob);
            ch.LastLogoutDate = Envir.Main.Now; // StopGame writes, unmarked
            acc.Connection = null;
            AssertCachedSaveMatches("logged out after the last save (sticky hot)");
            Assert.NotNull(acc.SaveBlob);
            Assert.False(acc.SaveHot);

            // select-screen-only session between two saves
            acc.Connection = FakeConnection();
            acc.SaveHot = true;
            acc.LastDate = Envir.Main.Now.AddMinutes(1);
            acc.Connection = null;
            acc.LastIP = "10.0.0.9"; // a write after the session, before the save
            AssertCachedSaveMatches("select-screen-only session");

            // refine in progress and an expiring creature: written relative to the clock, so never cached
            var refine = Char("Bot31");
            Envir.MarkAccountDirty(refine.AccountInfo);
            refine.CollectTime = long.MaxValue / 4;
            var pet = Char("Bot32");
            Envir.MarkAccountDirty(pet.AccountInfo);
            pet.IntelligentCreatures.Add(new UserIntelligentCreature(IntelligentCreatureType.BabyPig, 0, 1));
            s = AssertCachedSaveMatches("timed accounts");
            Assert.Equal(2, s.Timed);
            Assert.Null(refine.AccountInfo.SaveBlob);
            Assert.Null(pet.AccountInfo.SaveBlob);
            Assert.Equal(2, AssertCachedSaveMatches("timed again").Timed);
        });
    }

    [Fact]
    public void Full_save_load_restore_and_save_now_reset_the_cache()
    {
        Run("p912-reset", () =>
        {
            var e = Envir.Main;
            var count = e.AccountList.Count;
            AssertCachedSaveMatches("build");
            Assert.Equal(count, AssertCachedSaveMatches("warm").Reused());

            Char("Bot8").AccountInfo.Gold += 1; // unmarked: a full save writes it, and no older blob may be written over it afterwards
            e.SaveAccounts();
            var full = File.ReadAllBytes(Envir.AccountPath);
            var s = AssertCachedSaveMatches("after a full save");
            Assert.Equal(0, s.Reused());
            Assert.True(full.AsSpan().SequenceEqual(File.ReadAllBytes(Envir.AccountPath)));

            Assert.Equal(count, AssertCachedSaveMatches("warm again").Reused());
            e.RequestSaveNow(); // operator "Save now": the next periodic save is a full one
            Assert.Equal(0, AssertCachedSaveMatches("after Save now").Reused());

            Assert.Equal(count, AssertCachedSaveMatches("warm again").Reused());
            e.LoadAccounts(); // world start / web admin restore
            Assert.Equal(0, AssertCachedSaveMatches("after LoadAccounts").Reused());

            Assert.Equal(count, AssertCachedSaveMatches("warm again").Reused());
            Envir.InvalidateAccountCache(); // ARCHIVEPLAYER / RESTOREPLAYER / new day / GameShop reset / web admin write
            Assert.Equal(0, AssertCachedSaveMatches("after invalidate all").Reused());

            AccountSaveCache.Suspend(); // a WinForms account editor is open
            Char("Bot9").Level += 1;
            Assert.Equal(0, AssertCachedSaveMatches("editor open").Reused());
            Char("Bot9").Level += 1;
            AccountSaveCache.Resume();
            Assert.Equal(0, AssertCachedSaveMatches("editor closed").Reused());
            Assert.Equal(count, AssertCachedSaveMatches("warm again").Reused());
        });
    }

    [Fact]
    public void Verify_and_the_periodic_compare_catch_an_unmarked_write_and_repair_it()
    {
        Run("p912-verify", () =>
        {
            var e = Envir.Main;
            AssertCachedSaveMatches("build", OfflineAccountCacheMode.Verify);

            Char("Bot15").AccountInfo.Gold += 1; // unmarked
            var s = AssertCachedSaveMatches("verify-all after an unmarked write", OfflineAccountCacheMode.Verify);
            Assert.Equal(1, s.Mismatch);
            Assert.Equal(e.AccountList.Count, s.Verified);
            Assert.Equal(0, AssertCachedSaveMatches("repaired", OfflineAccountCacheMode.Verify).Mismatch);

            // On mode, FullEvery=1: the rolling window covers every cached account on every save
            AssertCachedSaveMatches("warm", OfflineAccountCacheMode.On, fullEvery: 1000);
            Char("Bot16").AccountInfo.Gold += 1; // unmarked
            s = AssertCachedSaveMatches("compare pass", OfflineAccountCacheMode.On, fullEvery: 1);
            Assert.True(s.LapComplete);
            Assert.Equal(0, s.Cached);
            Assert.Equal(1, s.Mismatch);
            Assert.True(s.MismatchTotal >= 2);
        });
    }

    /// <summary>
    /// Review M1/L3: the compare is rolling. With FullEvery=N each save compares ceil(accounts/N) cached accounts (counted
    /// as accounts checked, not list positions), and a lap over every cached account completes within N saves, so an
    /// unmarked write is caught within N saves with no save comparing everything.
    /// </summary>
    [Fact]
    public void The_rolling_compare_covers_every_cached_account_within_FullEvery_saves()
    {
        Run("p912-rolling", () =>
        {
            var e = Envir.Main;
            const int fullEvery = 4;
            var share = (e.AccountList.Count + fullEvery - 1) / fullEvery;
            AssertCachedSaveMatches("warm", OfflineAccountCacheMode.On, fullEvery);
            AssertCachedSaveMatches("warm 2", OfflineAccountCacheMode.On, fullEvery);

            foreach (var target in new[] { "Bot0", "Bot20", "Bot39" })
            {
                // finish the current lap so the next one starts at 0
                for (var i = 0; i < fullEvery + 1 && !AccountSaveCache.Last!.LapComplete; i++)
                    AssertCachedSaveMatches("finish lap", OfflineAccountCacheMode.On, fullEvery);

                Char(target).AccountInfo.Gold += 1; // unmarked: the file is stale until the window reaches it
                var found = 0;
                var verified = 0;
                var eligible = -1; // cached accounts of a pass = blobs written + blobs compared
                for (var i = 0; i < fullEvery; i++)
                {
                    PeriodicSave(OfflineAccountCacheMode.On, fullEvery);
                    var s = AccountSaveCache.Last!;
                    Assert.InRange(s.Verified, 0, 2 * share);
                    Assert.True(s.Cached > 0, "no pass compares everything");
                    found += s.Mismatch;
                    verified += s.Verified;
                    if (eligible < 0) eligible = s.Cached + s.Verified;
                    if (s.LapComplete) break;
                }
                Assert.Equal(1, found);
                Assert.True(verified >= eligible, $"lap compared {verified} of {eligible} cached accounts");
                AssertCachedSaveMatches(target + " repaired", OfflineAccountCacheMode.On, fullEvery);
            }
        });
    }

    /// <summary>Review M2: an invalidate-all keeps the arrays of accounts whose bytes did not change (re-tagged only).</summary>
    [Fact]
    public void Invalidate_all_keeps_unchanged_blobs()
    {
        Run("p912-retag", () =>
        {
            var e = Envir.Main;
            AssertCachedSaveMatches("build");
            var bot3 = Char("Bot3").AccountInfo;
            var bot4 = Char("Bot4").AccountInfo;
            var blob3 = bot3.SaveBlob;
            var blob4 = bot4.SaveBlob;
            Assert.NotNull(blob3);

            bot4.Gold += 1; // unmarked, but covered by the invalidation below
            Envir.InvalidateAccountCache();
            var s = AssertCachedSaveMatches("after invalidate all");
            Assert.Equal(0, s.Reused());
            Assert.Equal(0, s.Mismatch);
            Assert.Same(blob3, bot3.SaveBlob);
            Assert.NotSame(blob4, bot4.SaveBlob);
            Assert.Equal(e.AccountList.Count, AssertCachedSaveMatches("warm again").Reused());
        });
    }

    [Fact]
    public void Off_thread_epoch_bumps_during_saves_lose_nothing()
    {
        Run("p912-race", () =>
        {
            var e = Envir.Main;
            AssertCachedSaveMatches("build");
            var targets = new List<AccountInfo>();
            for (var i = 0; i < ExtraAccounts; i++) targets.Add(e.AccountList[3 + i]);

            // a WinForms-style editor on another thread: mutate (unmarked), then bump the epoch
            using var stop = new ManualResetEventSlim();
            var editor = new Thread(() =>
            {
                var rng = new Random(912);
                while (!stop.IsSet)
                {
                    var a = targets[rng.Next(targets.Count)];
                    a.Credit = a.Credit + 1;
                    AccountSaveCache.InvalidateAll();
                    Thread.SpinWait(rng.Next(2000));
                }
            });
            editor.Start();
            try
            {
                for (var i = 0; i < 40; i++) PeriodicSave(OfflineAccountCacheMode.On);
            }
            finally
            {
                stop.Set();
                editor.Join();
            }

            AssertCachedSaveMatches("after the editor stopped");
            AssertCachedSaveMatches("and again");
        });
    }

    [Fact]
    public void Random_mutation_sequences_stay_byte_identical()
    {
        Run("p912-random", () =>
        {
            var e = Envir.Main;
            var rng = new Random(1212);
            AssertCachedSaveMatches("build");
            CharacterInfo Bot() => Char("Bot" + rng.Next(ExtraAccounts));

            for (var round = 0; round < 60; round++)
            {
                var ops = rng.Next(1, 5);
                for (var k = 0; k < ops; k++)
                {
                    switch (rng.Next(9))
                    {
                        case 0: SendMail(Bot(), rng.Next(2) == 0); break;
                        case 1: e.MailCharacter(Bot(), gold: (uint)rng.Next(1, 100), customMessage: "r" + round); break;
                        case 2:
                            {
                                var owner = Bot(); var renter = Bot();
                                if (owner != renter) { RentalReturn(owner, renter); ProcessRentedItems(); }
                                break;
                            }
                        case 3:
                            {
                                var seller = Bot(); var buyer = Bot();
                                if (seller != buyer) AuctionWin(seller, buyer);
                                break;
                            }
                        case 4:
                            {
                                var a = Bot(); var b = Bot();
                                if (a == b) break;
                                Envir.MarkAccountDirty(a.AccountInfo); Envir.MarkAccountDirty(b.AccountInfo);
                                a.Friends.Add(new FriendInfo(b, false)); b.Friends.Add(new FriendInfo(a, false));
                                RemoveFriendOtherSide(a, b);
                                break;
                            }
                        case 5: LogoutMentor(Bot(), rng.Next(1, 1000)); break;
                        case 6:
                            {
                                var acc = Bot().AccountInfo; // a session: login, writes, logout (all unmarked but the sticky bit)
                                acc.Connection = FakeConnection(); acc.SaveHot = true;
                                acc.LastDate = e.Now.AddSeconds(round);
                                if (rng.Next(2) == 0) acc.Connection = null;
                                break;
                            }
                        case 7:
                            {
                                var acc = e.AccountList[rng.Next(e.AccountList.Count)];
                                if (acc.Connection != null) { acc.Characters.ForEach(c => c.LastLogoutDate = e.Now); acc.Connection = null; }
                                break;
                            }
                        case 8:
                            if (rng.Next(6) == 0) e.SaveAccounts();
                            else if (rng.Next(3) == 0) Envir.InvalidateAccountCache();
                            break;
                    }
                }

                AssertCachedSaveMatches("round " + round, rng.Next(4) == 0 ? OfflineAccountCacheMode.Verify : OfflineAccountCacheMode.On);
                Assert.Equal(0, AccountSaveCache.Last!.Mismatch);
            }

            for (var i = 0; i < e.AccountList.Count; i++) e.AccountList[i].Connection = null;
        });
    }

    /// <summary>
    /// Bench on a copy of real data (MIR2_ACCOUNT_SAVE_BENCH=&lt;folder with Server.MirDB + Server.MirADB&gt;, optional
    /// MIR2_ACCOUNT_SAVE_BENCH_ROUNDS): game-thread ms of BeginSaveAccounts (serialisation + segment list + queueing the
    /// write) for P9-11 (cache Off) and the cache with 0 and N online accounts (MIR2_ACCOUNT_SAVE_BENCH_HOT, default 400),
    /// plus Verify (200 per save) and one save after an invalidate-all; MIR2_ACCOUNT_SAVE_BENCH_FULLEVERY (default 12) sets the
    /// rolling compare. The last file of each scenario is checked against a full save.
    /// </summary>
    [Fact]
    public void Bench_real_accounts_cached()
    {
        var src = Environment.GetEnvironmentVariable("MIR2_ACCOUNT_SAVE_BENCH");
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("MIR2_ACCOUNT_SAVE_BENCH not set: bench not run."); return; }
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("MIR2_ACCOUNT_SAVE_BENCH_ROUNDS"), out var r) ? r : 20;
        var hot = int.TryParse(Environment.GetEnvironmentVariable("MIR2_ACCOUNT_SAVE_BENCH_HOT"), out var h) ? h : 400;
        var fullEvery = int.TryParse(Environment.GetEnvironmentVariable("MIR2_ACCOUNT_SAVE_BENCH_FULLEVERY"), out var f) ? f : 12;

        var dir = NewTempFolder("p912-bench");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "load failed");
            var e = Envir.Main;
            static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            (double Median, double Min, double Max, double First) Scenario(string name, OfflineAccountCacheMode mode, int online)
            {
                var hotSet = 0;
                for (var i = 0; i < e.AccountList.Count; i++)
                {
                    var a = e.AccountList[i];
                    var on = hotSet < online && a.Characters.Count > 0;
                    if (on) hotSet++;
                    a.Connection = on ? FakeConnection() : null;
                    if (on) a.SaveHot = true;
                }

                var old = (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery, Settings.OfflineAccountCacheVerifyPerSave);
                Settings.AccountSaveOffThread = true;
                Settings.OfflineAccountCache = mode;
                Settings.OfflineAccountCacheFullEvery = fullEvery;
                Settings.OfflineAccountCacheVerifyPerSave = 200;
                var times = new List<double>();
                double first = 0;
                try
                {
                    for (var i = -2; i < rounds; i++)
                    {
                        var t = System.Diagnostics.Stopwatch.GetTimestamp();
                        e.BeginSaveAccounts();
                        var ms = Ms(t);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (e.Saving && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                        Assert.False(e.Saving);
                        if (i == -2) first = ms;
                        if (i >= 0) times.Add(ms);
                    }
                }
                finally
                {
                    (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery, Settings.OfflineAccountCacheVerifyPerSave) = old;
                }

                Assert.True(Reference().AsSpan().SequenceEqual(File.ReadAllBytes(Envir.AccountPath)), name + ": file differs from a full save");
                times.Sort();
                var s = AccountSaveCache.Last;
                _out.WriteLine($"{name,-28} median={times[times.Count / 2]:0.00} ms min={times[0]:0.00} max={times[^1]:0.00} first={first:0.00}" +
                    (mode == OfflineAccountCacheMode.Off ? "" : $" cached={s.Cached} fresh={s.Fresh} timed={s.Timed} verified={s.Verified} mismatch={s.Mismatch}"));
                return (times[times.Count / 2], times[0], times[^1], first);
            }

            _out.WriteLine($"accounts={e.AccountList.Count} characters={e.CharacterList.Count} heroes={e.HeroList.Count} bytes={new FileInfo(Envir.AccountPath).Length}");
            Scenario("P9-11 (cache Off)", OfflineAccountCacheMode.Off, 0);
            Scenario("On, 0 online", OfflineAccountCacheMode.On, 0);
            Scenario($"On, {hot} online", OfflineAccountCacheMode.On, hot);

            // One save right after an invalidate-all (any web admin write): every account serialised, unchanged blobs kept.
            {
                var old = (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery);
                (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery) = (true, OfflineAccountCacheMode.On, fullEvery);
                try
                {
                    Envir.InvalidateAccountCache();
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var t = System.Diagnostics.Stopwatch.GetTimestamp();
                    e.BeginSaveAccounts();
                    var ms = Ms(t);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (e.Saving && sw.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                    _out.WriteLine($"{"After invalidate-all",-28} {ms:0.00} ms allocated={alloc / 1024} KB");
                }
                finally
                {
                    (Settings.AccountSaveOffThread, Settings.OfflineAccountCache, Settings.OfflineAccountCacheFullEvery) = old;
                }
            }
            Scenario("P9-11 (cache Off) again", OfflineAccountCacheMode.Off, hot);
            Scenario($"Verify 200, {hot} online", OfflineAccountCacheMode.Verify, hot);
            for (var i = 0; i < e.AccountList.Count; i++) e.AccountList[i].Connection = null;
            Scenario("Off (drops the cache)", OfflineAccountCacheMode.Off, 0);
            return 0;
        });
        Directory.Delete(dir, recursive: true);
    }
}

internal static class AccountCacheStatsTestExtensions
{
    /// <summary>Cached accounts of a save: blobs written as is plus blobs compared by the rolling window (always ≥ 1 per save).</summary>
    public static int Reused(this AccountCacheStats s) => s.Cached + s.Verified;
}
