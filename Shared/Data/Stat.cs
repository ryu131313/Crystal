/// <summary>
/// Stat values keyed by <see cref="Stat"/>. Storage (2026-10-22, was a SortedDictionary): a small array of
/// entries sorted by Stat, each packed as ((long)stat &lt;&lt; 32) | (uint)value; past <see cref="DenseThreshold"/>
/// entries (player, hero and big monster totals) a flat table indexed by the stat byte. Both iterate in ascending
/// Stat order, which Save writes and the wire and DB formats depend on. An entry may hold 0 when it was read or set
/// raw through <see cref="Values"/>; the indexer setter removes the entry instead.
/// Like the dictionary it replaces, it is not safe to change while another thread reads it, or during enumeration.
/// </summary>
public sealed class Stats : IEquatable<Stats>
{
    internal const int DenseThreshold = 24;
    private const int DenseBits = 256; // _dense[256..263]: one bit per stat byte, set when the entry exists

    private long[] _entries; // sorted mode; null until the first entry
    private int[] _dense;    // dense mode; once set, _entries is null and stays null
    private int _count;

    public StatValues Values => new StatValues(this);
    public int Count
    {
        get
        {
            int sum = 0;
            for (StatValues.Enumerator e = Values.GetEnumerator(); e.MoveNext();)
                sum += Math.Abs(e.Current.Value);
            return sum;
        }
    }

    public int this[Stat stat]
    {
        get
        {
            int[] dense = _dense;
            if (dense != null) return dense[(byte)stat];

            long[] entries = _entries;
            int i = Find(entries, _count, (byte)stat);
            return i < 0 ? 0 : (int)entries[i];
        }
        set
        {
            if (value == 0)
            {
                RemoveEntry(stat);
                return;
            }

            SetEntry(stat, value);
        }
    }

    public Stats() { }

    public Stats(Stats stats)
    {
        Add(stats);
    }

    public Stats(BinaryReader reader, int version = int.MaxValue, int customVersion = int.MaxValue)
    {
        int count = reader.ReadInt32();

        for (int i = 0; i < count; i++)
            SetEntry((Stat)reader.ReadByte(), reader.ReadInt32());
    }

    public void Add(Stats stats)
    {
        for (StatValues.Enumerator e = stats.Values.GetEnumerator(); e.MoveNext();)
        {
            KeyValuePair<Stat, int> pair = e.Current;
            this[pair.Key] += pair.Value;
        }
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(_count);

        for (StatValues.Enumerator e = Values.GetEnumerator(); e.MoveNext();)
        {
            KeyValuePair<Stat, int> pair = e.Current;
            writer.Write((byte)pair.Key);
            writer.Write(pair.Value);
        }
    }

    public void Clear()
    {
        // Keeps the storage: totals are cleared and rebuilt on every refresh.
        if (_dense != null) Array.Clear(_dense);
        _count = 0;
    }

    public bool Equals(Stats other)
    {
        if (_count != other._count) return false;

        for (StatValues.Enumerator e = Values.GetEnumerator(); e.MoveNext();)
            if (other[e.Current.Key] != e.Current.Value) return false;

        return true;
    }

    internal int EntryCount => _count;

    internal bool TryGetEntry(Stat stat, out int value)
    {
        int[] dense = _dense;
        if (dense != null)
        {
            value = dense[(byte)stat];
            return (dense[DenseBits + ((byte)stat >> 5)] & (1 << (byte)stat)) != 0;
        }

        long[] entries = _entries;
        int i = Find(entries, _count, (byte)stat);
        value = i < 0 ? 0 : (int)entries[i];
        return i >= 0;
    }

    /// <summary>Sets an entry, keeping it when <paramref name="value"/> is 0.</summary>
    internal void SetEntry(Stat stat, int value)
    {
        byte key = (byte)stat;

        if (_dense != null)
        {
            ref int bits = ref _dense[DenseBits + (key >> 5)];
            if ((bits & (1 << key)) == 0)
            {
                bits |= 1 << key;
                _count++;
            }
            _dense[key] = value;
            return;
        }

        long packed = ((long)key << 32) | (uint)value;
        int i = Find(_entries, _count, key);
        if (i >= 0)
        {
            _entries[i] = packed;
            return;
        }

        if (_count == DenseThreshold)
        {
            ToDense();
            SetEntry(stat, value);
            return;
        }

        i = ~i;
        if (_entries == null) _entries = new long[4];
        else if (_count == _entries.Length) Array.Resize(ref _entries, Math.Min(_count * 2, DenseThreshold));

        if (i < _count) Array.Copy(_entries, i, _entries, i + 1, _count - i);
        _entries[i] = packed;
        _count++;
    }

    internal bool RemoveEntry(Stat stat)
    {
        byte key = (byte)stat;

        if (_dense != null)
        {
            ref int bits = ref _dense[DenseBits + (key >> 5)];
            if ((bits & (1 << key)) == 0) return false;

            bits &= ~(1 << key);
            _dense[key] = 0;
            _count--;
            return true;
        }

        int i = Find(_entries, _count, key);
        if (i < 0) return false;

        _count--;
        if (i < _count) Array.Copy(_entries, i + 1, _entries, i, _count - i);
        return true;
    }

    /// <summary>Index of <paramref name="key"/> in the sorted entries, or the complement of its insert position.
    /// Takes the array and count as read once, and clamps, so a racing reader gets a stale answer, not an exception.</summary>
    private static int Find(long[] entries, int count, byte key)
    {
        if (entries == null) return ~0;

        int lo = 0, hi = Math.Min(count, entries.Length) - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int k = (int)(entries[mid] >> 32);
            if (k == key) return mid;
            if (k < key) lo = mid + 1;
            else hi = mid - 1;
        }

        return ~lo;
    }

    private void ToDense()
    {
        int[] dense = new int[DenseBits + DenseBits / 32];
        for (int i = 0; i < _count; i++)
        {
            int key = (int)(_entries[i] >> 32);
            dense[key] = (int)_entries[i];
            dense[DenseBits + (key >> 5)] |= 1 << key;
        }

        _dense = dense;
        _entries = null;
    }

    /// <summary>Read and raw-write view over a <see cref="Stats"/>' entries, in ascending Stat order (the dictionary API it replaces).</summary>
    public readonly struct StatValues : IEnumerable<KeyValuePair<Stat, int>>
    {
        private readonly Stats _stats;

        internal StatValues(Stats stats) { _stats = stats; }

        public int Count => _stats._count;

        /// <summary>Gets the entry (0 when absent).</summary>
        public int this[Stat stat] => _stats[stat];

        public bool ContainsKey(Stat stat) => _stats.TryGetEntry(stat, out _);
        public bool TryGetValue(Stat stat, out int value) => _stats.TryGetEntry(stat, out value);

        /// <summary>Sets an entry and keeps it even when <paramref name="value"/> is 0 (the binary formats can hold explicit zeros).</summary>
        public void Set(Stat stat, int value) => _stats.SetEntry(stat, value);
        public bool Remove(Stat stat) => _stats.RemoveEntry(stat);

        public Enumerator GetEnumerator() => new Enumerator(_stats);
        IEnumerator<KeyValuePair<Stat, int>> IEnumerable<KeyValuePair<Stat, int>>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<Stat, int>>
        {
            private readonly Stats _stats;
            private int _index; // sorted: entry index; dense: stat byte
            private KeyValuePair<Stat, int> _current;

            internal Enumerator(Stats stats)
            {
                _stats = stats;
                _index = -1;
                _current = default;
            }

            public readonly KeyValuePair<Stat, int> Current => _current;
            readonly object System.Collections.IEnumerator.Current => _current;

            public bool MoveNext()
            {
                int[] dense = _stats._dense;
                if (dense == null)
                {
                    if (_index + 1 >= _stats._count) return false;

                    long e = _stats._entries[++_index];
                    _current = new KeyValuePair<Stat, int>((Stat)(e >> 32), (int)e);
                    return true;
                }

                if (_index >= DenseBits) return false;

                int next = _index + 1;
                while (next < DenseBits)
                {
                    int word = (int)((uint)dense[DenseBits + (next >> 5)] >> (next & 31));
                    if (word != 0)
                    {
                        next += System.Numerics.BitOperations.TrailingZeroCount(word);
                        _index = next;
                        _current = new KeyValuePair<Stat, int>((Stat)next, dense[next]);
                        return true;
                    }
                    next = (next | 31) + 1;
                }

                _index = DenseBits;
                return false;
            }

            public void Reset()
            {
                _index = -1;
                _current = default;
            }

            public readonly void Dispose() { }
        }
    }
}

public enum StatFormula : byte
{
    Health,
    Mana,
    Weight,
    Stat
}

public enum Stat : byte
{
    MinAC = 0,
    MaxAC = 1,
    MinMAC = 2,
    MaxMAC = 3,
    MinDC = 4,
    MaxDC = 5,
    MinMC = 6,
    MaxMC = 7,
    MinSC = 8,
    MaxSC = 9,

    Accuracy = 10,
    Agility = 11,
    HP = 12,
    MP = 13,
    AttackSpeed = 14,
    Luck = 15,
    BagWeight = 16,
    HandWeight = 17,
    WearWeight = 18,
    Reflect = 19,
    Strong = 20,
    Holy = 21,
    Freezing = 22,
    PoisonAttack = 23,

    MagicResist = 30,
    PoisonResist = 31,
    HealthRecovery = 32,
    SpellRecovery = 33,
    PoisonRecovery = 34, //TODO - Should this be in seconds or milliseconds??
    CriticalRate = 35,
    CriticalDamage = 36,

    MaxACRatePercent = 40,
    MaxMACRatePercent = 41,
    MaxDCRatePercent = 42,
    MaxMCRatePercent = 43,
    MaxSCRatePercent = 44,
    AttackSpeedRatePercent = 45,
    HPRatePercent = 46,
    MPRatePercent = 47,
    HPDrainRatePercent = 48,

    ExpRatePercent = 100,
    ItemDropRatePercent = 101,
    GoldDropRatePercent = 102,
    MineRatePercent = 103,
    GemRatePercent = 104,
    FishRatePercent = 105,
    CraftRatePercent = 106,
    SkillGainMultiplier = 107,
    AttackBonus = 108,

    LoverExpRatePercent = 120,
    MentorDamageRatePercent = 121,
    MentorExpRatePercent = 123,
    DamageReductionPercent = 124,
    EnergyShieldPercent = 125,
    EnergyShieldHPGain = 126,
    ManaPenaltyPercent = 127,
    TeleportManaPenaltyPercent = 128,
    Hero = 129,

    Unknown = 255
}