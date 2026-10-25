using Server.MirDatabase;
using Server.MirEnvir;
using Xunit.Abstractions;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Committed format fixtures (synthetic data only, safe to commit). Fixtures\v117 was written by the v117 code
/// (roadmap P3 pre-step) and holds BinaryFormatter-encoded Buff.Data blobs, so later format bumps can prove they
/// still load old files. Regenerate only deliberately: MIR2_WRITE_FIXTURE=1 while Envir.Version is the target version.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class FixtureTests
{
    internal static readonly DateTime FixtureClock = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _out;
    public FixtureTests(ITestOutputHelper output) => _out = output;

    internal static string FixtureDir(int version) =>
        Path.Combine(RepoRoot(), "Server.Persistence.Tests", "Fixtures", "v" + version);

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Write_fixture_for_current_version_when_requested()
    {
        if (Environment.GetEnvironmentVariable("MIR2_WRITE_FIXTURE") != "1")
        {
            _out.WriteLine("MIR2_WRITE_FIXTURE not set: nothing written.");
            return;
        }
        var target = FixtureDir(Envir.Version);
        var dir = NewTempFolder("fixture");
        if (Environment.GetEnvironmentVariable("MIR2_WRITE_FIXTURE_ORACLE_ONLY") == "1")
        {
            // Re-dump the oracle of the committed fixture files (e.g. after an Oracle format change), without rewriting them.
            File.Copy(Path.Combine(target, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
            File.Copy(Path.Combine(target, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
            InFolder(dir, () =>
            {
                Assert.True(LoadAll());
                File.WriteAllText(Path.Combine(target, "oracle.txt"), Oracle.Dump(Envir.Main));
                return 0;
            });
            Directory.Delete(dir, true);
            _out.WriteLine("oracle rewritten: " + target);
            return;
        }
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main, FixtureClock);
            SaveAll();
            Assert.True(LoadAll());
            SaveAll();
            Directory.CreateDirectory(target);
            File.Copy(Envir.DatabasePath, Path.Combine(target, "Server.MirDB"), true);
            File.WriteAllBytes(Path.Combine(target, "Server.MirADB"), AccountsImage()); // the rollback image of UserData.db = Server.MirADB bytes
            Assert.True(LoadAll());
            File.WriteAllText(Path.Combine(target, "oracle.txt"), Oracle.Dump(Envir.Main));
            return 0;
        });
        Directory.Delete(dir, true);
        _out.WriteLine("fixture written: " + target);
    }

    /// <summary>
    /// Roadmap P3 gate: the v117 fixture (BinaryFormatter Buff.Data blobs) loads on current code without BinaryFormatter.
    /// Everything except the Concentration buff's Data must be identical to the v117 oracle; the blobs are skipped
    /// (decision: skip and reset). Fields added in v119 must load as their defaults. The upgraded save is current and byte-stable.
    /// </summary>
    [Fact]
    public void V117_fixture_upgrades_to_current_version()
    {
        var src = FixtureDir(117);
        var dir = NewTempFolder("upgrade117");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        var expected = File.ReadAllText(Path.Combine(src, "oracle.txt")).Replace("\r\n", "\n");

        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "v117 fixture failed to load");
            Assert.Equal(117, Envir.LoadVersion);
            var upgraded = Oracle.Dump(Envir.Main);
            var nonDefault = new List<string>();
            var upgradedPre119 = Oracle.WithoutV119(Oracle.WithoutV121(upgraded, nonDefault), nonDefault);
            Assert.True(nonDefault.Count == 0, "v119/v121 fields not at their defaults after a v117 load: " + string.Join("; ", nonDefault));
            expected = Oracle.WithoutLegacyQuestFields(Oracle.WithoutV121(expected, new List<string>())); // the legacy quest lists are discarded by a pre-121 load

            static string WithoutBuffData(string dump) => string.Join("\n",
                dump.Split('\n').Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\.Buffs\[\d+\]\.Data")));
            Assert.True(WithoutBuffData(expected) == WithoutBuffData(upgradedPre119),
                "v117 -> current changed more than Buff.Data: " + Oracle.FirstDifference(WithoutBuffData(expected), WithoutBuffData(upgradedPre119)));
            Assert.Contains("Buffs[0].Data.Count=2", expected);
            Assert.Contains("AccountList[1].Characters[1].Buffs[0].Data.Count=0", upgraded);
            Assert.Equal(0, CountBuffDataBlobs(Envir.Main));

            SaveAll();
            Assert.Equal(Envir.Version, BitConverter.ToInt32(AccountsImage(), 0));
            Assert.Equal(Envir.Version, BitConverter.ToInt32(File.ReadAllBytes(Envir.DatabasePath), 0));
            var gen1 = ReadFiles();
            Assert.True(LoadAll());
            Assert.Equal(upgraded, Oracle.Dump(Envir.Main));
            SaveAll();
            var gen2 = ReadFiles();
            Assert.True(gen1.accounts.AsSpan().SequenceEqual(gen2.accounts));
            Assert.True(gen1.db.AsSpan().SequenceEqual(gen2.db));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// v119 gate (2026-10-16; was the roadmap P4 v118 byte-identical re-save): the v118 fixture loads on v119 code with
    /// every v119 field at its default and nothing else changed (oracle equal apart from the v119 lines). The upgraded
    /// save is v119 and byte-stable.
    /// </summary>
    [Fact]
    public void V118_fixture_upgrades_to_current_version()
    {
        var src = FixtureDir(118);
        var dir = NewTempFolder("upgrade118");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        var expected = File.ReadAllText(Path.Combine(src, "oracle.txt")).Replace("\r\n", "\n");
        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "v118 fixture failed to load");
            Assert.Equal(118, Envir.LoadVersion);
            var upgraded = Oracle.Dump(Envir.Main);
            var nonDefault = new List<string>();
            var upgradedPre119 = Oracle.WithoutV119(Oracle.WithoutV121(upgraded, nonDefault), nonDefault);
            Assert.True(nonDefault.Count == 0, "v119/v121 fields not at their defaults after a v118 load: " + string.Join("; ", nonDefault));
            expected = Oracle.WithoutLegacyQuestFields(Oracle.WithoutV121(expected, new List<string>())); // the legacy quest lists are discarded by a pre-121 load
            Assert.True(expected == upgradedPre119, "v118 -> v119 changed existing data: " + Oracle.FirstDifference(expected, upgradedPre119));
            Assert.Contains(".AllowFriendRequests=True", upgraded);
            var oldMail = Envir.Main.AccountList[1].Characters[0].Mail[0]; // MailInfo lives in Server.MirEnvir, which the oracle skips
            Assert.Equal(-1, oldMail.SenderIndex);
            Assert.False(oldMail.SenderHidden);
            Assert.Contains("MarketSales.Count=0", upgraded);

            SaveAll();
            Assert.Equal(Envir.Version, BitConverter.ToInt32(AccountsImage(), 0));
            Assert.Equal(Envir.Version, BitConverter.ToInt32(File.ReadAllBytes(Envir.DatabasePath), 0));
            var gen1 = ReadFiles();
            Assert.True(LoadAll());
            Assert.Equal(upgraded, Oracle.Dump(Envir.Main));
            SaveAll();
            var gen2 = ReadFiles();
            Assert.True(gen1.accounts.AsSpan().SequenceEqual(gen2.accounts));
            Assert.True(gen1.db.AsSpan().SequenceEqual(gen2.db));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// v120 gate (2026-10-20; was the v119 byte-identical re-save): the v119 fixture loads on current code with the chat ban
    /// at its default and nothing else changed. ChatBanned/ChatBanExpiryDate already printed in the oracle (memory-only
    /// fields before v120), so the v119 oracle must match the upgraded dump apart from the v121 quest/statistics lines (all
    /// defaults) and the discarded legacy quest lists. The upgraded save is current and byte-stable.
    /// </summary>
    [Fact]
    public void V119_fixture_upgrades_to_current_version()
    {
        var src = FixtureDir(119);
        var dir = NewTempFolder("upgrade119");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        var expected = File.ReadAllText(Path.Combine(src, "oracle.txt")).Replace("\r\n", "\n");
        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "v119 fixture failed to load");
            Assert.Equal(119, Envir.LoadVersion);
            var upgraded = Oracle.Dump(Envir.Main);
            var nonDefault = new List<string>();
            var upgradedPre121 = Oracle.WithoutV121(upgraded, nonDefault);
            Assert.True(nonDefault.Count == 0, "v121 fields not at their defaults after a v119 load: " + string.Join("; ", nonDefault));
            var expectedPre121 = Oracle.WithoutLegacyQuestFields(Oracle.WithoutV121(expected, new List<string>()));
            Assert.True(expectedPre121 == upgradedPre121, "v119 -> current changed existing data: " + Oracle.FirstDifference(expectedPre121, upgradedPre121));
            var c1 = Envir.Main.AccountList[1].Characters[0];
            Assert.False(c1.ChatBanned);
            Assert.Equal(DateTime.MinValue, c1.ChatBanExpiryDate);

            SaveAll();
            Assert.Equal(Envir.Version, BitConverter.ToInt32(AccountsImage(), 0));
            Assert.Equal(Envir.Version, BitConverter.ToInt32(File.ReadAllBytes(Envir.DatabasePath), 0));
            var gen1 = ReadFiles();
            Assert.True(LoadAll());
            Assert.Equal(upgraded, Oracle.Dump(Envir.Main));
            SaveAll();
            var gen2 = ReadFiles();
            Assert.True(gen1.accounts.AsSpan().SequenceEqual(gen2.accounts));
            Assert.True(gen1.db.AsSpan().SequenceEqual(gen2.db));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// v121 gate (2026-10-24, quest V2 + statistics; was the v120 byte-identical re-save): the v120 fixture loads on v121 code
    /// with the quest V2 state empty, no statistics, HideFromRankings false and the legacy quest lists discarded (no conversion:
    /// the D2 wipe), and nothing else changed. The upgraded save is v121 and byte-stable.
    /// </summary>
    [Fact]
    public void V120_fixture_upgrades_to_current_version()
    {
        var src = FixtureDir(120);
        var dir = NewTempFolder("upgrade120");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        var expected = File.ReadAllText(Path.Combine(src, "oracle.txt")).Replace("\r\n", "\n");
        Assert.Contains("AccountList[1].Characters[0].CompletedQuests[0]=1", expected); // the v120 file really holds legacy quest data
        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "v120 fixture failed to load");
            Assert.Equal(120, Envir.LoadVersion);
            var upgraded = Oracle.Dump(Envir.Main);
            var nonDefault = new List<string>();
            var upgradedPre121 = Oracle.WithoutV121(upgraded, nonDefault);
            Assert.True(nonDefault.Count == 0, "v121 fields not at their defaults after a v120 load: " + string.Join("; ", nonDefault));
            var expectedPre121 = Oracle.WithoutLegacyQuestFields(Oracle.WithoutV121(expected, new List<string>()));
            Assert.True(expectedPre121 == upgradedPre121, "v120 -> v121 changed existing data: " + Oracle.FirstDifference(expectedPre121, upgradedPre121));

            var c1 = Envir.Main.AccountList[1].Characters[0];
            Assert.Empty(c1.QuestRuns);
            Assert.Empty(c1.QuestObjectives);
            Assert.Empty(c1.QuestHistory);
            Assert.Empty(c1.QuestVars);
            Assert.Empty(c1.QuestRewardsPaid);
            Assert.Equal(0, c1.Statistics.Count);
            Assert.False(c1.HideFromRankings);
            Assert.True(c1.ChatBanned); // the fields after the skipped legacy lists still read correctly
            Assert.Equal(FixtureClock.AddDays(3), c1.ChatBanExpiryDate);

            SaveAll();
            Assert.Equal(Envir.Version, BitConverter.ToInt32(AccountsImage(), 0));
            Assert.Equal(Envir.Version, BitConverter.ToInt32(File.ReadAllBytes(Envir.DatabasePath), 0));
            var gen1 = ReadFiles();
            Assert.True(LoadAll());
            Assert.Equal(upgraded, Oracle.Dump(Envir.Main));
            SaveAll();
            var gen2 = ReadFiles();
            Assert.True(gen1.accounts.AsSpan().SequenceEqual(gen2.accounts));
            Assert.True(gen1.db.AsSpan().SequenceEqual(gen2.db));
            return 0;
        });
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// The v121 fixture (written 2026-10-24, every v119-v121 field non-default: quest runs, objective values, history, quest
    /// vars, statistics incl. an id this build does not know, HideFromRankings) loads to its committed oracle and re-saves
    /// byte for byte on this runtime (takes over the gate from v120).
    /// </summary>
    [Fact]
    public void V121_fixture_resaves_byte_identically_on_this_runtime()
    {
        var src = FixtureDir(121);
        var dir = NewTempFolder("resave121");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        // The legacy QuestInfo / NPCInfo quest fields and the always-empty legacy character lists left the models in wave 4.
        var expected = Oracle.WithoutLegacyQuestFields(File.ReadAllText(Path.Combine(src, "oracle.txt")).Replace("\r\n", "\n"));
        InFolder(dir, () =>
        {
            Assert.True(LoadAll());
            Assert.Equal(121, Envir.LoadVersion);
            var dump = Oracle.Dump(Envir.Main);
            Assert.True(expected == dump, "v121 fixture oracle differs: " + Oracle.FirstDifference(expected, dump));
            var nonDefault = new List<string>();
            Oracle.WithoutV119(dump, nonDefault);
            Assert.NotEmpty(nonDefault); // the fixture really carries v119 data
            nonDefault.Clear();
            Oracle.WithoutV121(dump, nonDefault);
            Assert.NotEmpty(nonDefault); // ... and v121 data

            var c1 = Envir.Main.AccountList[1].Characters[0];
            Assert.True(c1.ChatBanned);
            Assert.Equal(2, c1.QuestRuns.Count);
            var run = c1.QuestRuns[0];
            Assert.Equal((1, QuestRunStatus.Active, 2, 3, 2, 1), (run.Quest, run.Status, run.Step, run.Revision, run.SharedFrom, run.Branch));
            Assert.Equal(FixtureClock.AddDays(1).ToBinary(), run.ExpiresAt);
            Assert.Equal(QuestRunStatus.Failed, c1.QuestRuns[1].Status);
            Assert.Equal(-1, c1.QuestRuns[1].Step);
            Assert.Equal(unchecked((int)0x80000005), c1.QuestObjectives[1].Value);
            Assert.Equal(3, c1.QuestHistory.Single().Times);
            Assert.Equal(-7, c1.QuestVars.Single().Value);
            Assert.Equal((1, QuestRewardScope.Step, 2), (c1.QuestRewardsPaid.Single().Quest, c1.QuestRewardsPaid.Single().Scope, c1.QuestRewardsPaid.Single().ScopeId));
            Assert.Equal(1234, c1.Statistics.Get(PlayerStat.MonstersKilled));
            Assert.Equal(5_000_000_000L, c1.Statistics.Get(PlayerStat.GoldEarned));
            Assert.Equal(987, c1.Statistics.Get(PlayerStat.HighestHit));
            Assert.Equal(42, c1.Statistics.Get((PlayerStat)60000)); // an unknown id survives the load
            Assert.Equal(4, c1.Statistics.Count);
            Assert.True(c1.HideFromRankings);
            var c2 = Envir.Main.AccountList[1].Characters[1];
            Assert.Empty(c2.QuestRuns);
            Assert.Equal(0, c2.Statistics.Count);
            Assert.False(c2.HideFromRankings);

            SaveAll();
            // Server.MirDB: the legacy quest fields (FileName, gates, link words) and the NPC quest lists are dropped on load since wave 4
            // and written as defaults, so the system data re-saves byte-stable but no longer equal to the fixture; the user data does.
            Assert.True(File.ReadAllBytes(Path.Combine(src, "Server.MirADB")).AsSpan().SequenceEqual(AccountsImage()), "the user data differs from the v121 fixture");
            var db1 = File.ReadAllBytes(Envir.DatabasePath);
            Assert.True(LoadAll());
            Assert.Equal(dump, Oracle.Dump(Envir.Main));
            SaveAll();
            Assert.True(db1.AsSpan().SequenceEqual(File.ReadAllBytes(Envir.DatabasePath)), "Server.MirDB is not byte-stable");
            return 0;
        });
        Directory.Delete(dir, true);
    }
}
