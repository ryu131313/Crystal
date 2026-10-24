using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>
/// Website export (UserData-and-Quests plan, Part A "Website export", lane G): who is exported, the field whitelist of every
/// JSON file, safe file names, unchanged files left alone and stale character files deleted.
/// </summary>
public class WebsiteExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mir2-website-export-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Every property name a page can see. Anything else in any exported file fails the whitelist test.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // characters/<id>.json
        "name", "class", "gender", "level", "guild", "stats",
        "monstersKilled", "deaths", "goldEarned", "goldSpent", "itemsCrafted", "questsCompleted", "highestHit", "playersKilled",
        "deathsToPlayers", "bestKillStreak", "murders", "guildWarKills", "pvpDamageDealt", "pvpDamageTaken", "highestPkPoints",
        // leaderboards/<stat>.json
        "stat", "entries", "rank", "id", "value",
        // guilds.json
        "guilds", "members",
        // meta.json
        "updated", "characters", "leaderboardSize", "leaderboards",
    };

    private const string Secret = "SECRETzq";

    private static AccountInfo Account(string id, bool admin = false, bool banned = false)
    {
        return new AccountInfo
        {
            Index = id.Length,
            AccountID = Secret + "acct" + id,
            UserName = Secret + "user",
            EMailAddress = Secret + "@mail.test",
            SecretQuestion = Secret + "q",
            SecretAnswer = Secret + "a",
            CreationIP = "10.9.8.7",
            LastIP = "10.9.8.6",
            BanReason = Secret + "ban",
            AdminAccount = admin,
            Banned = banned,
            ExpiryDate = banned ? DateTime.MaxValue : DateTime.MinValue,
        };
    }

    private static CharacterInfo Character(AccountInfo account, string name, ushort level = 10, int guild = -1, bool deleted = false, bool hidden = false)
    {
        var info = new CharacterInfo
        {
            Name = name,
            Level = level,
            Class = MirClass.Wizard,
            Gender = MirGender.Female,
            GuildIndex = guild,
            Deleted = deleted,
            HideFromRankings = hidden,
            AccountInfo = account,
            CreationIP = "10.1.1.1",
            LastIP = "10.1.1.2",
        };
        account.Characters.Add(info);
        return info;
    }

    private static GuildInfo Guild(int index, string name, byte level, int members)
    {
        var guild = (GuildInfo)RuntimeHelpers.GetUninitializedObject(typeof(GuildInfo));
        guild.GuildIndex = index;
        guild.Name = name;
        guild.Level = level;
        guild.Membercount = members;
        return guild;
    }

    private static (List<AccountInfo> Accounts, List<GuildInfo> Guilds) World()
    {
        var accounts = new List<AccountInfo>();

        var normal = Account("a");
        var alice = Character(normal, "Alice", 40, guild: 7);
        alice.Statistics.Add(PlayerStat.MonstersKilled, 12);
        alice.Statistics.Add(PlayerStat.PlayersKilled, 3);
        alice.Statistics.Set((PlayerStat)999, 5); // a stat this build does not export
        var bob = Character(normal, "Bob", 55);
        bob.Statistics.Add(PlayerStat.MonstersKilled, 30);
        Character(normal, "Deleted1", deleted: true).Statistics.Add(PlayerStat.MonstersKilled, 99);
        Character(normal, "Hidden1", hidden: true).Statistics.Add(PlayerStat.MonstersKilled, 99);
        accounts.Add(normal);

        var admin = Account("bb", admin: true);
        Character(admin, "AdminChar").Statistics.Add(PlayerStat.MonstersKilled, 99);
        accounts.Add(admin);

        var banned = Account("ccc", banned: true);
        Character(banned, "BannedChar").Statistics.Add(PlayerStat.MonstersKilled, 99);
        accounts.Add(banned);

        var other = Account("dddd");
        Character(other, "Con", 1); // a Windows device name
        accounts.Add(other);

        return (accounts, new List<GuildInfo> { Guild(7, "Knights", 3, 12), Guild(8, "Empty", 1, 0) });
    }

    private WebsiteExportSnapshot Snapshot()
    {
        var (accounts, guilds) = World();
        var snapshot = new WebsiteExportSnapshot { Path = _dir, Taken = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc) };
        WebsiteExport.Collect(snapshot, accounts, guilds);
        return snapshot;
    }

    [Fact]
    public void Collect_skips_admin_banned_deleted_and_hidden_characters()
    {
        var snapshot = Snapshot();

        var names = new List<string>();
        for (var i = 0; i < snapshot.RowCount; i++) names.Add(snapshot.Rows[i].Name);
        Assert.Equal(new[] { "Alice", "Bob", "Con" }, names);

        var alice = snapshot.Rows[0];
        Assert.Equal("Knights", alice.Guild);
        Assert.Equal((ushort)40, alice.Level);
        Assert.Equal(12, alice.Stats[Array.IndexOf(WebsiteExport.ExportedStats, PlayerStat.MonstersKilled)]);
        Assert.Equal(3, alice.Stats[Array.IndexOf(WebsiteExport.ExportedStats, PlayerStat.PlayersKilled)]);
        Assert.Null(snapshot.Rows[1].Guild);
        Assert.Equal(2, snapshot.GuildCount);
    }

    [Fact]
    public void Collect_reuses_rows_between_exports()
    {
        var (accounts, guilds) = World();
        var snapshot = new WebsiteExportSnapshot();
        WebsiteExport.Collect(snapshot, accounts, guilds);
        var first = snapshot.Rows[0];
        accounts[0].Characters[0].HideFromRankings = true;
        WebsiteExport.Collect(snapshot, accounts, guilds);

        Assert.Equal(2, snapshot.RowCount);
        Assert.Same(first, snapshot.Rows[0]);
        Assert.Equal("Bob", snapshot.Rows[0].Name);
    }

    [Fact]
    public void Exported_files_contain_only_whitelisted_fields_and_nothing_from_the_account()
    {
        new WebsiteExportWriter().Write(Snapshot());

        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories);
        Assert.Contains(Path.Combine(_dir, "meta.json"), files);
        Assert.Contains(Path.Combine(_dir, "guilds.json"), files);
        Assert.Contains(Path.Combine(_dir, "leaderboards", "level.json"), files);
        Assert.Contains(Path.Combine(_dir, "leaderboards", "playersKilled.json"), files);
        Assert.Contains(Path.Combine(_dir, "characters", "alice.json"), files);
        Assert.Contains(Path.Combine(_dir, "characters", "_con.json"), files);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_dir, "characters")).Length);

        foreach (var file in files)
        {
            Assert.EndsWith(".json", file);
            var text = File.ReadAllText(file, Encoding.UTF8);
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("10.", text);
            Assert.DoesNotContain("Deleted1", text);
            Assert.DoesNotContain("Hidden1", text);
            Assert.DoesNotContain("AdminChar", text);
            Assert.DoesNotContain("BannedChar", text);

            using var doc = JsonDocument.Parse(text);
            AssertWhitelisted(doc.RootElement, file);
        }

        using var alice = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "characters", "alice.json")));
        Assert.Equal("Alice", alice.RootElement.GetProperty("name").GetString());
        Assert.Equal("Wizard", alice.RootElement.GetProperty("class").GetString());
        Assert.Equal("Knights", alice.RootElement.GetProperty("guild").GetString());
        Assert.Equal(12, alice.RootElement.GetProperty("stats").GetProperty("monstersKilled").GetInt64());

        using var board = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "leaderboards", "monstersKilled.json")));
        var entries = board.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength()); // zero values are left off
        Assert.Equal("Bob", entries[0].GetProperty("name").GetString());
        Assert.Equal(30, entries[0].GetProperty("value").GetInt64());
        Assert.Equal("alice", entries[1].GetProperty("id").GetString());
    }

    private static void AssertWhitelisted(JsonElement element, string file)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.True(Allowed.Contains(property.Name), $"{file}: field '{property.Name}' is not on the website whitelist");
                    AssertWhitelisted(property.Value, file);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) AssertWhitelisted(item, file);
                break;
        }
    }

    [Fact]
    public void Exported_stat_names_match_the_whitelist()
    {
        Assert.Equal(WebsiteExport.ExportedStats.Length, WebsiteExport.ExportedStatNames.Length);
        foreach (var name in WebsiteExport.ExportedStatNames) Assert.Contains(name, Allowed);
    }

    [Fact]
    public void Second_export_skips_unchanged_files_and_deletes_stale_characters()
    {
        var (accounts, guilds) = World();
        var snapshot = new WebsiteExportSnapshot { Path = _dir, Taken = DateTime.UtcNow };
        var writer = new WebsiteExportWriter();

        WebsiteExport.Collect(snapshot, accounts, guilds);
        writer.Write(snapshot);
        Assert.True(writer.LastWritten > 3);

        writer.Write(snapshot); // same snapshot (meta.json included: same Taken)
        Assert.Equal(0, writer.LastWritten);

        accounts[0].Characters[1].HideFromRankings = true; // Bob opts out
        File.WriteAllText(Path.Combine(_dir, "characters", "leftover.json.tmp"), "x");
        WebsiteExport.Collect(snapshot, accounts, guilds);
        writer.Write(snapshot);

        Assert.False(File.Exists(Path.Combine(_dir, "characters", "bob.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "characters", "leftover.json.tmp")));
        Assert.True(File.Exists(Path.Combine(_dir, "characters", "alice.json")));
        Assert.DoesNotContain("Bob", File.ReadAllText(Path.Combine(_dir, "leaderboards", "level.json")));
    }

    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("CON", "_con")]
    [InlineData("lpt1", "_lpt1")]
    [InlineData("a.b", "a_002eb")]
    [InlineData("..", "_002e_002e")]
    [InlineData("战士", "_6218_58eb")]
    public void FileId_is_safe(string name, string expected)
    {
        Assert.Equal(expected, WebsiteExportWriter.FileId(name));
    }
}
