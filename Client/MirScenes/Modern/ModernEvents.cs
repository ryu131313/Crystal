using Client.MirScenes.Models;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// World notifications for the new UI (Desktop-Modern-UI-Plan phase 2; the desktop form of WebClient's GameUi.notify /
    /// UiEvent). ModernUi raises these from the IGameUi members that are pure notifications, so any number of HUD pieces and
    /// windows can listen without owning a seam member. Subscribe when a component is built / shown and unsubscribe when it is
    /// disposed / closed. Per game: <see cref="Clear"/> runs when the modern UI is disposed. UI thread only.
    /// State itself lives in GameScene.Models, GameScene.User / Hero and ItemMoves: these only say "it changed".
    /// </summary>
    public static class ModernEvents
    {
        /// <summary>S.UserInformation applied (GameScene.User rebuilt: level, gold, inventory, magics, ...).</summary>
        public static Emitter UserInformation = new Emitter();
        /// <summary>The user's object loaded (name, class, appearance).</summary>
        public static Emitter UserLoaded = new Emitter();
        /// <summary>GameScene.HasHero changed.</summary>
        public static Emitter<bool> HasHero = new Emitter<bool>();
        /// <summary>S.HeroInformation applied (GameScene.Hero rebuilt).</summary>
        public static Emitter HeroInformation = new Emitter();
        public static Emitter<HeroSpawnState> HeroSpawnState = new Emitter<HeroSpawnState>();
        public static Emitter<HeroBehaviour> HeroBehaviour = new Emitter<HeroBehaviour>();
        /// <summary>The hero's level / exp / hp / mp / stats changed.</summary>
        public static Emitter HeroInfo = new Emitter();
        public static Emitter HeroAutoPot = new Emitter();
        /// <summary>Settings.SkillMode changed (true = Ctrl skill mode).</summary>
        public static Emitter<bool> SkillMode = new Emitter<bool>();
        /// <summary>Magic keys / skill bar assignment changed.</summary>
        public static Emitter SkillBars = new Emitter();
        /// <summary>S.GameDataChanged applied: (magics changed, shop changed, hover data changed).</summary>
        public static Emitter<(bool Magics, bool Shop, bool Hover)> GameData = new Emitter<(bool, bool, bool)>();
        /// <summary>An item's durability (or any worn item) changed; null = refresh everything.</summary>
        public static Emitter<UserItem> Durability = new Emitter<UserItem>();
        /// <summary>An item's info arrived / changed (tooltips, cells redraw).</summary>
        public static Emitter<UserItem> ItemInfo = new Emitter<UserItem>();
        public static Emitter InventoryResized = new Emitter();
        public static Emitter StorageResized = new Emitter();
        public static Emitter Lights = new Emitter();
        public static Emitter Mount = new Emitter();
        /// <summary>Objects moved / died (minimap and big map dots); fires often — repaint at most once per frame.</summary>
        public static Emitter ObjectPositions = new Emitter();
        /// <summary>A map's info changed (big map records); -1 = all.</summary>
        public static Emitter<int> MapInfo = new Emitter<int>();
        public static Emitter<S.NewMapInfo> NewMapInfo = new Emitter<S.NewMapInfo>();
        public static Emitter<WorldMapSetup> WorldMap = new Emitter<WorldMapSetup>();
        public static Emitter<S.SearchMapResult> SearchMap = new Emitter<S.SearchMapResult>();
        /// <summary>Quests changed (added / removed / progress); the tracker and quest windows re-read GameScene.Models.Quests.</summary>
        public static Emitter Quests = new Emitter();
        /// <summary>A quest run was added after login (S.QuestStates); track = the server's TrackHint.</summary>
        public static Emitter<(int Quest, bool Track)> QuestAdded = new Emitter<(int, bool)>();
        /// <summary>The user is on a new (or reloaded) map: S.MapInformation / S.MapChanged, after MapControl loaded it (the map index).</summary>
        public static Emitter<int> MapChanged = new Emitter<int>();

        /// <summary>Drops every subscriber (end of the game's modern UI).</summary>
        public static void Clear()
        {
            UserInformation = new Emitter();
            UserLoaded = new Emitter();
            HasHero = new Emitter<bool>();
            HeroInformation = new Emitter();
            HeroSpawnState = new Emitter<HeroSpawnState>();
            HeroBehaviour = new Emitter<HeroBehaviour>();
            HeroInfo = new Emitter();
            HeroAutoPot = new Emitter();
            SkillMode = new Emitter<bool>();
            SkillBars = new Emitter();
            GameData = new Emitter<(bool, bool, bool)>();
            Durability = new Emitter<UserItem>();
            ItemInfo = new Emitter<UserItem>();
            InventoryResized = new Emitter();
            StorageResized = new Emitter();
            Lights = new Emitter();
            Mount = new Emitter();
            ObjectPositions = new Emitter();
            MapInfo = new Emitter<int>();
            NewMapInfo = new Emitter<S.NewMapInfo>();
            WorldMap = new Emitter<WorldMapSetup>();
            SearchMap = new Emitter<S.SearchMapResult>();
            Quests = new Emitter();
            QuestAdded = new Emitter<(int, bool)>();
            MapChanged = new Emitter<int>();
        }
    }
}
