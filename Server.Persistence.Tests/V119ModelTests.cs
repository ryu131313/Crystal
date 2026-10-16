using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>
/// DB version 119 (2026-10-16): the guild log (Guilds\*.mgd, outside the world fixtures) and the market sales ring.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class V119ModelTests
{
    private static byte[] Bytes(Action<BinaryWriter> save)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) save(w);
        return ms.ToArray();
    }

    private static BinaryReader Reader(byte[] b) => new BinaryReader(new MemoryStream(b));

    /// <summary>A guild file in the self-versioned layout GuildInfo.Save writes (no ranks, items, buffs), optionally with a log.</summary>
    private static byte[] GuildFile(int version, IReadOnlyList<GuildLogEntry> log) => Bytes(w =>
    {
        w.Write(int.MaxValue); w.Write(version); w.Write(0); // marker, version, customVersion
        w.Write(7); w.Write("Hens"); w.Write((byte)3); w.Write((byte)1); w.Write(1000L); w.Write(500u); w.Write(0);
        w.Write(new DateTime(2099, 1, 1).ToBinary()); w.Write(false);
        w.Write(0); // ranks with members
        w.Write(112); for (int i = 0; i < 112; i++) w.Write(false); // storage slots
        w.Write(0); // buffs
        w.Write(1); w.Write("Be nice");
        w.Write((ushort)1000); w.Write(System.Drawing.Color.White.ToArgb());
        w.Write(DateTime.MinValue.ToBinary()); w.Write(-1); w.Write(0); w.Write(0); w.Write(DateTime.MinValue.ToBinary());
        if (log == null) return;
        w.Write(log.Count);
        foreach (var e in log) e.Save(w);
    });

    [Fact]
    public void Guild_log_round_trips_at_v119()
    {
        var log = new[]
        {
            new GuildLogEntry { Time = new DateTime(2099, 1, 2, 3, 4, 5, DateTimeKind.Utc), Kind = GuildLogKind.Join, Text = "Warrior1 joined" },
            new GuildLogEntry { Time = new DateTime(2099, 1, 3, 0, 0, 0, DateTimeKind.Utc), Kind = GuildLogKind.Gold, Text = "Warrior1 donated 500 gold" },
        };
        var file = GuildFile(Envir.Version, log);
        var guild = new GuildInfo(Reader(file));
        Assert.Equal(2, guild.Log.Count);
        Assert.Equal(GuildLogKind.Gold, guild.Log[1].Kind);
        Assert.Equal("Warrior1 joined", guild.Log[0].Text);
        Assert.Equal(log[0].Time, guild.Log[0].Time);
        Assert.Equal(file, Bytes(guild.Save));
    }

    [Fact]
    public void Guild_file_before_v119_loads_with_an_empty_log_and_resaves_as_v119()
    {
        var guild = new GuildInfo(Reader(GuildFile(118, null)));
        Assert.Empty(guild.Log);
        Assert.Equal(GuildFile(Envir.Version, Array.Empty<GuildLogEntry>()), Bytes(guild.Save));
    }

    [Fact]
    public void Guild_log_is_capped_oldest_first()
    {
        var guild = new GuildInfo(Reader(GuildFile(Envir.Version, Array.Empty<GuildLogEntry>())));
        for (int i = 0; i < GuildInfo.MaxLogEntries + 5; i++) guild.AddLog(GuildLogKind.Notice, "n" + i);
        Assert.Equal(GuildInfo.MaxLogEntries, guild.Log.Count);
        Assert.Equal("n5", guild.Log[0].Text);
        Assert.Equal("n" + (GuildInfo.MaxLogEntries + 4), guild.Log[^1].Text);
        Assert.True(guild.NeedSave);

        // an over-long list in a file is trimmed to the newest MaxLogEntries on load
        var many = new List<GuildLogEntry>();
        for (int i = 0; i < GuildInfo.MaxLogEntries + 3; i++) many.Add(new GuildLogEntry { Kind = GuildLogKind.War, Text = "w" + i });
        var loaded = new GuildInfo(Reader(GuildFile(Envir.Version, many)));
        Assert.Equal(GuildInfo.MaxLogEntries, loaded.Log.Count);
        Assert.Equal("w3", loaded.Log[0].Text);
    }

    [Fact]
    public void Market_sale_history_keeps_the_last_14_oldest_first_and_round_trips()
    {
        var start = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var h = new MarketSaleHistory();
        for (int i = 0; i < MarketSaleHistory.Capacity + 6; i++) h.Add((uint)i, start.AddMinutes(i));
        Assert.Equal(MarketSaleHistory.Capacity, h.Count);
        Assert.Equal(6u, h[0].Price);
        Assert.Equal(start.AddMinutes(6), h[0].Date);
        Assert.Equal((uint)(MarketSaleHistory.Capacity + 5), h[h.Count - 1].Price);
        Assert.Throws<ArgumentOutOfRangeException>(() => h[h.Count]);

        var b = Bytes(h.Save);
        Assert.Equal(4 + MarketSaleHistory.Capacity * 12, b.Length);
        var back = new MarketSaleHistory(Reader(b));
        Assert.Equal(b, Bytes(back.Save));
        for (int i = 0; i < h.Count; i++) Assert.Equal(h[i], back[i]);
    }
}
