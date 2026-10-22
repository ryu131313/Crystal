namespace Shared.Tests;

/// <summary>
/// Stats storage (2026-10-22): a packed sorted array, promoted to a dense table past Stats.DenseThreshold entries,
/// replaced a SortedDictionary. Pins it to a SortedDictionary model of the old class: same reads, same ascending
/// enumeration, and byte-identical Save output, through random sets, raw zero entries, removals, Clear and Add.
/// </summary>
public class StatsStorageTests
{
    /// <summary>The pre-2026-10-22 Save layout of a SortedDictionary.</summary>
    private static byte[] LegacySave(SortedDictionary<Stat, int> model)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(model.Count);
        foreach (var pair in model)
        {
            w.Write((byte)pair.Key);
            w.Write(pair.Value);
        }
        return ms.ToArray();
    }

    private static byte[] Save(Stats stats)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        stats.Save(w);
        return ms.ToArray();
    }

    private static void AssertSame(SortedDictionary<Stat, int> model, Stats stats)
    {
        Assert.Equal(model.Count, stats.Values.Count);
        Assert.Equal(model.ToList(), stats.Values.ToList());
        Assert.Equal(LegacySave(model), Save(stats));
        Assert.Equal(model.Values.Sum(Math.Abs), stats.Count);
        for (int k = 0; k < 256; k++)
        {
            Assert.Equal(model.TryGetValue((Stat)k, out int v) ? v : 0, stats[(Stat)k]);
            Assert.Equal(model.ContainsKey((Stat)k), stats.Values.ContainsKey((Stat)k));
        }

        var copy = new Stats(new BinaryReader(new MemoryStream(Save(stats))));
        Assert.Equal(LegacySave(model), Save(copy));
        Assert.True(copy.Equals(stats));
    }

    [Theory]
    [InlineData(1, 8)]     // stays sorted
    [InlineData(2, 40)]    // promotes to dense
    [InlineData(3, 256)]   // every byte value, incl. Unknown = 255
    public void RandomOperationsMatchTheSortedDictionaryModel(int seed, int keySpace)
    {
        var rng = new Random(seed);
        var model = new SortedDictionary<Stat, int>();
        var stats = new Stats();

        for (int step = 0; step < 4000; step++)
        {
            var key = (Stat)rng.Next(keySpace);
            int value = rng.Next(4) == 0 ? 0 : rng.Next(-1_000_000, 1_000_000);

            switch (rng.Next(10))
            {
                case 0: // raw set keeps zeros, as the stream ctor and the SQLite zero list do
                    model[key] = value;
                    stats.Values.Set(key, value);
                    break;
                case 1:
                    Assert.Equal(model.Remove(key), stats.Values.Remove(key));
                    break;
                case 2 when rng.Next(20) == 0:
                    model.Clear();
                    stats.Clear();
                    break;
                default: // the indexer: 0 removes
                    if (value == 0) model.Remove(key); else model[key] = value;
                    stats[key] = value;
                    break;
            }

            if (step % 97 == 0) AssertSame(model, stats);
        }

        AssertSame(model, stats);
    }

    [Fact]
    public void AddMatchesTheModelAcrossModes()
    {
        var rng = new Random(7);
        for (int round = 0; round < 50; round++)
        {
            var a = new Stats();
            var b = new Stats();
            var model = new SortedDictionary<Stat, int>();
            for (int i = 0; i < rng.Next(60); i++) a[(Stat)rng.Next(130)] = rng.Next(-5, 6);
            for (int i = 0; i < rng.Next(60); i++) b[(Stat)rng.Next(130)] = rng.Next(-5, 6);

            foreach (var p in a.Values) model[p.Key] = p.Value;
            foreach (var p in b.Values)
            {
                int sum = (model.TryGetValue(p.Key, out int v) ? v : 0) + p.Value;
                if (sum == 0) model.Remove(p.Key); else model[p.Key] = sum;
            }

            var total = new Stats(a);
            total.Add(b);
            AssertSame(model, total);
        }
    }

    [Fact]
    public void EqualsFollowsTheOldRules()
    {
        var a = new Stats { [Stat.MinAC] = 1, [Stat.HP] = 5 };
        var b = new Stats { [Stat.HP] = 5, [Stat.MinAC] = 1 };
        Assert.True(a.Equals(b));

        b.Values.Set(Stat.Luck, 0); // an explicit zero entry changes the entry count
        Assert.False(a.Equals(b));

        var big = new Stats();
        var big2 = new Stats();
        for (int k = 0; k < 40; k++) big[(Stat)k] = k + 1;
        for (int k = 0; k < 40; k++) big2[(Stat)k] = k + 1;
        Assert.True(big.Equals(big2));
        big2[(Stat)39] = 7;
        Assert.False(big.Equals(big2));
    }
}
