namespace Server.MirDatabase
{
    /// <summary>
    /// A character's <see cref="PlayerStat"/> counters, saved by CharacterInfo from Envir.Version 121 on as an int count followed by
    /// (ushort id, long value) pairs. Ids this build does not know (a newer build's stats) are kept and written back. Game-thread
    /// owned like the rest of CharacterInfo; no allocation per Add once a stat exists. Hooks are lane G's.
    /// </summary>
    public sealed class PlayerStatistics
    {
        private readonly Dictionary<PlayerStat, long> _values = new Dictionary<PlayerStat, long>();

        public PlayerStatistics() { }

        public PlayerStatistics(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var stat = (PlayerStat)reader.ReadUInt16();
                _values[stat] = reader.ReadInt64();
            }
        }

        public int Count => _values.Count;

        /// <summary>Read-only view (website export, web admin, tests).</summary>
        public IReadOnlyDictionary<PlayerStat, long> Values => _values;

        public long Get(PlayerStat stat)
        {
            return _values.TryGetValue(stat, out var value) ? value : 0;
        }

        /// <summary>Adds <paramref name="amount"/> to a counter (a zero amount changes nothing).</summary>
        public void Add(PlayerStat stat, long amount)
        {
            if (amount == 0) return;
            _values.TryGetValue(stat, out var value);
            _values[stat] = value + amount;
        }

        /// <summary>Raises a "highest" counter to <paramref name="value"/> when it is larger.</summary>
        public void Max(PlayerStat stat, long value)
        {
            _values.TryGetValue(stat, out var current); // a missing counter is 0
            if (value <= current) return;
            _values[stat] = value;
        }

        /// <summary>Sets a counter (GM / web admin edits, tests).</summary>
        public void Set(PlayerStat stat, long value)
        {
            _values[stat] = value;
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(_values.Count);
            foreach (var pair in _values)
            {
                writer.Write((ushort)pair.Key);
                writer.Write(pair.Value);
            }
        }
    }
}
