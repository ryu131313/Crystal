using Server.MirDatabase;
using S = ServerPackets;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>
    /// What one LiveDb batch changed that connected clients may hold (finding F8: the client computes stats and tooltips from its own
    /// copies). Keys are Main's records (they are what the connections' Sent*Info lists contain); values are FRESH snapshot copies made
    /// on the game thread after the live step, never Main references, so a packet cannot change after it was queued (EncodeOnce).
    /// Batch B fills items, monsters and spells; Batch C fills NPCs, quests, the game shop and map invalidations.
    /// </summary>
    internal sealed class LiveDbPushChanges
    {
        public readonly Dictionary<ItemInfo, ItemInfo> Items = new Dictionary<ItemInfo, ItemInfo>();
        public readonly Dictionary<MonsterInfo, ClientMonsterInfo> Monsters = new Dictionary<MonsterInfo, ClientMonsterInfo>();
        public readonly Dictionary<NPCInfo, ClientNPCInfo> Npcs = new Dictionary<NPCInfo, ClientNPCInfo>();
        public readonly Dictionary<QuestInfo, ClientQuestInfo> Quests = new Dictionary<QuestInfo, ClientQuestInfo>();

        /// <summary>Changed spells; each connection's ClientMagic copies come from its player's / hero's own UserMagic.</summary>
        public readonly HashSet<Spell> Spells = new HashSet<Spell>();

        /// <summary>Game-shop rows sent to every connection in the game (the client removes them by GIndex, then adds them).</summary>
        public readonly List<S.GameDataChanged.ShopEntry> GameShop = new List<S.GameDataChanged.ShopEntry>();

        /// <summary>Main's game-shop rows behind <see cref="GameShop"/> (same order): only for the per-connection stock level.</summary>
        public readonly List<GameShopItem> GameShopSources = new List<GameShopItem>();

        public readonly List<int> GameShopRemoved = new List<int>();

        /// <summary>Maps whose big-map info changed: connections that hold it drop it (client and SentMapInfo) and re-request it.</summary>
        public readonly HashSet<MapInfo> MapsInvalidated = new HashSet<MapInfo>();

        public int Rows => Items.Count + Monsters.Count + Npcs.Count + Quests.Count + Spells.Count + GameShop.Count + GameShopRemoved.Count +
                           MapsInvalidated.Count;

        public bool IsEmpty => Rows == 0;
    }

    /// <summary>One connection as the push selection sees it (production: the live MirConnection's lists, read on the game thread).</summary>
    internal sealed class LiveDbPushConnection
    {
        public IList<ItemInfo> SentItems;
        public IList<MonsterInfo> SentMonsters;
        public IList<NPCInfo> SentNpcs;
        public IList<QuestInfo> SentQuests;
        public IList<MapInfo> SentMaps;

        /// <summary>ClientMagic copies of the changed spells this connection's player knows (empty when none).</summary>
        public List<ClientMagic> Magics;

        /// <summary>The same for the player's spawned hero.</summary>
        public List<ClientMagic> HeroMagics;

        /// <summary>The connection receives game-shop rows (a player in the game).</summary>
        public bool ReceivesGameShop;

        /// <summary>
        /// This connection's own copies of changed quests (production: <c>CreateClientQuestInfo(player)</c>, which also sends the
        /// item / monster / NPC infos its links mention first). Missing entries use the shared snapshot of <see cref="LiveDbPushChanges.Quests"/>.
        /// </summary>
        public Dictionary<QuestInfo, ClientQuestInfo> QuestSnapshots;

        /// <summary>
        /// This connection's stock level per game-shop GIndex (individual stock counts the player's own purchases, as GetGameShop
        /// does). A negative value hides the row (sent as removed). Missing entries use the shared entry's level.
        /// </summary>
        public Dictionary<int, int> ShopStock;
    }

    /// <summary>What a push did (reported in the batch outcome).</summary>
    internal sealed class LiveDbPushResult
    {
        public int Connections;
        public int Packets;
        public int Rows;
        public int Bytes;

        /// <summary>Elements larger than one packet (never sent; logged).</summary>
        public int Oversized;

        public void Add(LiveDbPushResult other)
        {
            if (other == null) return;
            Connections += other.Connections;
            Packets += other.Packets;
            Rows += other.Rows;
            Bytes += other.Bytes;
            Oversized += other.Oversized;
        }
    }

    /// <summary>
    /// Pure selection and chunking of <see cref="S.GameDataChanged"/> packets for one connection: one pass over each of the
    /// connection's Sent*Info lists with a dictionary lookup into the changed set, each element measured by writing it to a scratch
    /// writer, packets cut before they pass <see cref="ChunkBytes"/> (well under the 65 535-byte packet limit). The last packet of the
    /// connection carries <c>Last = true</c>. Runs on the game thread inside the LiveDb live step (one-off admin action, not per tick).
    /// </summary>
    internal static class LiveDbPush
    {
        /// <summary>Target size of one packet, header included.</summary>
        public const int ChunkBytes = 60 * 1024;

        /// <summary>Hard limit: the packet length field is a ushort.</summary>
        public const int MaxPacketBytes = ushort.MaxValue;

        private enum Section { Items, Monsters, Npcs, Quests, Magics, HeroMagics, GameShop, GameShopRemoved, MapInfoInvalidated }

        /// <summary>Measures elements by writing them to one reused buffer.</summary>
        private sealed class Meter
        {
            private readonly MemoryStream _ms = new MemoryStream(4096);
            private readonly BinaryWriter _writer;

            public Meter()
            {
                _writer = new BinaryWriter(_ms);
            }

            public int Measure(Action<BinaryWriter> write)
            {
                _ms.Position = 0;
                _ms.SetLength(0);
                write(_writer);
                _writer.Flush();
                return (int)_ms.Length;
            }
        }

        private sealed class Builder
        {
            private readonly int _limit;
            private readonly Meter _meter = new Meter();
            private S.GameDataChanged _current;
            private int _size;

            public readonly List<S.GameDataChanged> Packets = new List<S.GameDataChanged>();
            public readonly LiveDbPushResult Result = new LiveDbPushResult();

            public Builder(int limit)
            {
                _limit = limit;
            }

            public void Add(Section section, object element, Action<BinaryWriter> write)
            {
                var size = _meter.Measure(write);
                if (size + S.GameDataChanged.FixedOverhead > _limit)
                {
                    Result.Oversized++;
                    return;
                }

                if (_current != null && _size + size > _limit) Flush();
                if (_current == null)
                {
                    _current = new S.GameDataChanged();
                    _size = S.GameDataChanged.FixedOverhead;
                }

                switch (section)
                {
                    case Section.Items: _current.Items.Add((ItemInfo)element); break;
                    case Section.Monsters: _current.Monsters.Add((ClientMonsterInfo)element); break;
                    case Section.Npcs: _current.Npcs.Add((ClientNPCInfo)element); break;
                    case Section.Quests: _current.Quests.Add((ClientQuestInfo)element); break;
                    case Section.Magics: _current.Magics.Add((ClientMagic)element); break;
                    case Section.HeroMagics: _current.HeroMagics.Add((ClientMagic)element); break;
                    case Section.GameShop: _current.GameShop.Add((S.GameDataChanged.ShopEntry)element); break;
                    case Section.GameShopRemoved: _current.GameShopRemoved.Add((int)element); break;
                    case Section.MapInfoInvalidated: _current.MapInfoInvalidated.Add((int)element); break;
                }

                _size += size;
                Result.Rows++;
            }

            private void Flush()
            {
                if (_current == null) return;
                Packets.Add(_current);
                Result.Bytes += _size;
                _current = null;
                _size = 0;
            }

            public void Finish()
            {
                Flush();
                if (Packets.Count == 0) return;
                Packets[Packets.Count - 1].Last = true;
                Result.Packets = Packets.Count;
                Result.Connections = 1;
            }
        }

        /// <summary>
        /// The packets for one connection (empty when it holds none of the changed rows). <paramref name="chunkBytes"/> is clamped to
        /// <see cref="MaxPacketBytes"/>; tests pass a small value to exercise chunking.
        /// </summary>
        public static List<S.GameDataChanged> Build(LiveDbPushChanges changes, LiveDbPushConnection conn, out LiveDbPushResult result,
            int chunkBytes = ChunkBytes)
        {
            var b = new Builder(Math.Max(S.GameDataChanged.FixedOverhead + 1, Math.Min(chunkBytes, MaxPacketBytes)));
            if (changes != null && conn != null && !changes.IsEmpty)
            {
                if (changes.Items.Count > 0 && conn.SentItems != null)
                    for (var i = 0; i < conn.SentItems.Count; i++)
                    {
                        var key = conn.SentItems[i];
                        if (key != null && changes.Items.TryGetValue(key, out var snap)) b.Add(Section.Items, snap, w => snap.Save(w));
                    }

                if (changes.Monsters.Count > 0 && conn.SentMonsters != null)
                    for (var i = 0; i < conn.SentMonsters.Count; i++)
                    {
                        var key = conn.SentMonsters[i];
                        if (key != null && changes.Monsters.TryGetValue(key, out var snap)) b.Add(Section.Monsters, snap, w => snap.Save(w));
                    }

                if (changes.Npcs.Count > 0 && conn.SentNpcs != null)
                    for (var i = 0; i < conn.SentNpcs.Count; i++)
                    {
                        var key = conn.SentNpcs[i];
                        if (key != null && changes.Npcs.TryGetValue(key, out var snap)) b.Add(Section.Npcs, snap, w => snap.Save(w));
                    }

                if (changes.Quests.Count > 0 && conn.SentQuests != null)
                    for (var i = 0; i < conn.SentQuests.Count; i++)
                    {
                        var key = conn.SentQuests[i];
                        if (key == null || !changes.Quests.TryGetValue(key, out var snap)) continue;
                        if (conn.QuestSnapshots != null && conn.QuestSnapshots.TryGetValue(key, out var own) && own != null) snap = own;
                        b.Add(Section.Quests, snap, w => snap.Save(w));
                    }

                if (conn.Magics != null)
                    for (var i = 0; i < conn.Magics.Count; i++)
                    {
                        var m = conn.Magics[i];
                        if (m != null && changes.Spells.Contains(m.Spell)) b.Add(Section.Magics, m, w => m.Save(w));
                    }

                if (conn.HeroMagics != null)
                    for (var i = 0; i < conn.HeroMagics.Count; i++)
                    {
                        var m = conn.HeroMagics[i];
                        if (m != null && changes.Spells.Contains(m.Spell)) b.Add(Section.HeroMagics, m, w => m.Save(w));
                    }

                if (conn.ReceivesGameShop)
                {
                    for (var i = 0; i < changes.GameShopRemoved.Count; i++)
                    {
                        var gIndex = changes.GameShopRemoved[i];
                        b.Add(Section.GameShopRemoved, gIndex, w => w.Write(gIndex));
                    }

                    // Rows this player may not see (sold out for them): removed instead.
                    if (conn.ShopStock != null)
                        for (var i = 0; i < changes.GameShop.Count; i++)
                        {
                            var hidden = changes.GameShop[i]?.Item;
                            if (hidden == null || !conn.ShopStock.TryGetValue(hidden.GIndex, out var level) || level >= 0) continue;
                            var gIndex = hidden.GIndex;
                            b.Add(Section.GameShopRemoved, gIndex, w => w.Write(gIndex));
                        }

                    for (var i = 0; i < changes.GameShop.Count; i++)
                    {
                        var entry = changes.GameShop[i];
                        if (entry?.Item?.Info == null) continue;
                        if (conn.ShopStock != null && conn.ShopStock.TryGetValue(entry.Item.GIndex, out var stock))
                        {
                            if (stock < 0) continue;
                            if (stock != entry.StockLevel) entry = new S.GameDataChanged.ShopEntry { Item = entry.Item, StockLevel = stock };
                        }
                        b.Add(Section.GameShop, entry, w =>
                        {
                            entry.Item.Save(w, true);
                            w.Write(entry.StockLevel);
                        });
                    }
                }

                if (changes.MapsInvalidated.Count > 0 && conn.SentMaps != null)
                    for (var i = 0; i < conn.SentMaps.Count; i++)
                    {
                        var key = conn.SentMaps[i];
                        if (key == null || !changes.MapsInvalidated.Contains(key)) continue;
                        var index = key.Index;
                        b.Add(Section.MapInfoInvalidated, index, w => w.Write(index));
                    }
            }

            b.Finish();
            result = b.Result;
            return b.Packets;
        }

        /// <summary>Encoded size of a packet (header included), for tests and diagnostics.</summary>
        public static int EncodedSize(Packet p) => p.GetPacketBytes().Length;
    }
}
