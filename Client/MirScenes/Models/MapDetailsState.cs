using Client.MirNetwork;
using C = ClientPackets;

namespace Client.MirScenes.Models
{
    /// <summary>
    /// Map details for the big map (S.MapDetails: respawns and safe zones, in parts of at most MaxRespawnsPerPart /
    /// MaxSafeZonesPerPart; port of the WebClient/src/game/mapDetails.ts subset). <see cref="Request"/> asks once per map
    /// (the server has a 1 s cooldown and S.ServerRules.MapDetailsEnabled); <see cref="Changed"/> fires with the map index when
    /// the last part arrived.
    /// </summary>
    public sealed class MapDetailsState
    {
        public sealed class Entry
        {
            public readonly List<ClientMapRespawn> Respawns = new List<ClientMapRespawn>();
            public readonly List<ClientMapSafeZone> SafeZones = new List<ClientMapSafeZone>();
            public int PartsReceived, PartCount = 1;
            public bool Complete;
        }

        private readonly Dictionary<int, Entry> _maps = new Dictionary<int, Entry>();
        private readonly HashSet<int> _requested = new HashSet<int>();
        private long _nextRequest;
        private int _pending;

        public readonly Emitter<int> Changed = new Emitter<int>();

        /// <summary>The details of a map, or null when not (fully) received.</summary>
        public Entry Get(int mapIndex) => _maps.TryGetValue(mapIndex, out Entry e) && e.Complete ? e : null;

        /// <summary>Asks the server once per map (needs ServerRules.MapDetailsEnabled; at most one request a second).</summary>
        public bool Request(int mapIndex, ServerRulesState rules)
        {
            if (mapIndex <= 0 || _requested.Contains(mapIndex)) return false;
            if (rules?.Rules == null || !rules.Rules.MapDetailsEnabled) return false;
            if (CMain.Time < _nextRequest)
            {
                _pending = mapIndex; // the latest ask wins; Retry sends it once the cooldown is over
                return false;
            }
            if (_pending == mapIndex) _pending = 0;
            _requested.Add(mapIndex);
            _nextRequest = CMain.Time + 1000;
            Network.Enqueue(new C.RequestMapDetails { MapIndex = mapIndex });
            return true;
        }

        /// <summary>Sends a request the cooldown held back (call it from the big map window's Process; cheap when nothing waits).</summary>
        public void Retry(ServerRulesState rules)
        {
            if (_pending == 0 || CMain.Time < _nextRequest) return;
            int map = _pending;
            _pending = 0;
            Request(map, rules);
        }

        public void Apply(ServerPackets.MapDetails p)
        {
            if (p == null) return;
            if (_maps.TryGetValue(p.MapIndex, out Entry done) && done.Complete && p.Part != 0) return; // a stray late part
            if (!_maps.TryGetValue(p.MapIndex, out Entry e) || p.Part == 0)
            {
                e = new Entry();
                _maps[p.MapIndex] = e;
            }
            e.PartCount = Math.Max((byte)1, p.PartCount);
            if (p.Respawns != null) e.Respawns.AddRange(p.Respawns);
            if (p.SafeZones != null) e.SafeZones.AddRange(p.SafeZones);
            e.PartsReceived++;
            if (e.PartsReceived >= e.PartCount)
            {
                e.Complete = true;
                Changed.Emit(p.MapIndex);
            }
        }

        /// <summary>Game data changed on the server (LiveDb): ask again next time.</summary>
        public void Invalidate(int mapIndex)
        {
            _requested.Remove(mapIndex);
            _maps.Remove(mapIndex);
        }
    }
}
