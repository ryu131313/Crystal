// The big map's and minimap's map records for the new UI (the web's GameInfo.map / requestMap / worldMap, ported for the
// desktop): the classic MapInfoList lives in the classic partial, so the modern UI keeps its own copy, fed by the same packets
// through ModernEvents (S.NewMapInfo → NewMapInfo, S.WorldMapSetupInfo → WorldMap, S.GameDataChanged → MapInfo
// invalidation) and the map details model (S.MapDetails → Models.MapDetails.Changed). Built with the modern UI (before any
// window opens, so the world map setup sent after login is kept).
using Client.MirNetwork;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class BigMapData
    {
        /// <summary>The view key of the world map (map views are MapIndex values ≥ 0).</summary>
        public const int World = -2;

        private readonly Dictionary<int, ClientMapInfo> _maps = new Dictionary<int, ClientMapInfo>();
        private readonly HashSet<int> _requested = new HashSet<int>();

        /// <summary>S.WorldMapSetup (Enabled + icons); empty until it arrives.</summary>
        public WorldMapSetup WorldMap { get; private set; } = new WorldMapSetup();

        /// <summary>Maps the client knows (MapIndex → title) for the big map's picker: world-map icons, exit destinations,
        /// search results and maps viewed (Crystal sends no map list).</summary>
        public readonly Dictionary<int, string> Known = new Dictionary<int, string>();

        /// <summary>A map record, the world map (<see cref="World"/>) or a map's details changed.</summary>
        public event Action<int> Changed;

        public BigMapData()
        {
            ModernEvents.NewMapInfo.On(OnNewMapInfo);
            ModernEvents.MapInfo.On(OnInvalidated);
            ModernEvents.WorldMap.On(OnWorldMap);
            GameScene.Models?.MapDetails.Changed.On(OnDetails);
        }

        public ClientMapInfo Map(int index) => _maps.TryGetValue(index, out ClientMapInfo info) ? info : null;

        /// <summary>C.RequestMapInfo once per map (BigMapDialog.SetTargetMap).</summary>
        public void RequestMap(int index)
        {
            if (index < 0 || _maps.ContainsKey(index) || !_requested.Add(index)) return;
            Network.Enqueue(new C.RequestMapInfo { MapIndex = index });
        }

        public void Learn(int index, string title)
        {
            if (index < 0) return;
            if (!string.IsNullOrEmpty(title) || !Known.ContainsKey(index))
                Known[index] = !string.IsNullOrEmpty(title) ? title : Known.TryGetValue(index, out string t) ? t : GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapN, index);
        }

        private void Raise(int index)
        {
            try
            {
                Changed?.Invoke(index);
            }
            catch (Exception ex)
            {
                Core.ModernLog.Once("[ModernUi] big map data", ex);
            }
        }

        private void OnNewMapInfo(S.NewMapInfo p)
        {
            if (p?.Info == null) return;
            _maps[p.MapIndex] = p.Info;
            _requested.Remove(p.MapIndex);
            Learn(p.MapIndex, p.Info.Title);
            Raise(p.MapIndex);
        }

        private void OnInvalidated(int index)
        {
            if (index < 0)
            {
                _maps.Clear();
                _requested.Clear();
            }
            else
            {
                _maps.Remove(index);
                _requested.Remove(index);
            }
            Raise(index);
        }

        private void OnWorldMap(WorldMapSetup setup)
        {
            WorldMap = setup ?? new WorldMapSetup();
            for (int i = 0; i < WorldMap.Icons.Count; i++)
            {
                WorldMapIcon icon = WorldMap.Icons[i];
                Learn(icon.MapIndex, Map(icon.MapIndex)?.Title ?? icon.Title);
            }
            Raise(World);
        }

        private void OnDetails(int index) => Raise(index);

        public void Dispose()
        {
            ModernEvents.NewMapInfo.Off(OnNewMapInfo);
            ModernEvents.MapInfo.Off(OnInvalidated);
            ModernEvents.WorldMap.Off(OnWorldMap);
            GameScene.Models?.MapDetails.Changed.Off(OnDetails);
            Changed = null;
        }
    }
}
