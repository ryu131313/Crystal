using Server.MirEnvir.Quests;
using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// The system-data collections the grid engine works on. In the server it wraps <c>Envir.Edit</c> (always used under
    /// <see cref="WebAdminGame.EditLock"/>); tests build one from plain lists so the whole engine runs without Envir/Settings.
    /// </summary>
    internal sealed class GameDataContext
    {
        public List<ItemInfo> Items = new List<ItemInfo>();
        public List<MonsterInfo> Monsters = new List<MonsterInfo>();
        public List<MagicInfo> Magics = new List<MagicInfo>();
        public List<NPCInfo> Npcs = new List<NPCInfo>();
        public List<QuestInfo> Quests = new List<QuestInfo>();
        public List<MapInfo> Maps = new List<MapInfo>();
        public List<GameShopItem> GameShop = new List<GameShopItem>();
        public List<ConquestInfo> Conquests = new List<ConquestInfo>();
        // Quest system V2 shared tables (read-only grids until the phase 3 Quests editor).
        public List<QuestConditionInfo> QuestConditions = new List<QuestConditionInfo>();
        public List<QuestTextInfo> QuestTexts = new List<QuestTextInfo>();
        public List<StorylineInfo> Storylines = new List<StorylineInfo>();
        public List<QuestGroupInfo> QuestGroups = new List<QuestGroupInfo>();
        public List<QuestPoolInfo> QuestPools = new List<QuestPoolInfo>();

        public Func<DragonInfo> Dragon = () => null;

        public Func<int> NextItemIndex;
        public Func<int> NextMonsterIndex;
        public Func<int> NextMapIndex;
        public Func<int> NextNpcIndex;
        public Func<int> NextQuestIndex;
        public Func<int> NextGameShopIndex;
        public Func<int> NextConquestIndex;
        public Func<int> NextRespawnIndex;

        /// <summary>Envir.Remove(GameShopItem) resets GameshopIndex when the shop becomes empty; mirrored here.</summary>
        public Action GameShopEmptied = () => { };

        /// <summary>Number of mine sets (Settings.MineSetList); a Mine / MineIndex value v is valid when 0 ≤ v ≤ count.</summary>
        public Func<int> MineSetCount = () => 0;

        /// <summary>Mine set names (index 0 = mine set 1).</summary>
        public Func<IReadOnlyList<string>> MineSetNames = () => Array.Empty<string>();

        /// <summary>Default gold price of a new game-shop entry (AddToGameShop: 1000 * Settings.CredxGold).</summary>
        public Func<uint> DefaultGameShopGoldPrice = () => 1000;

        public Func<DateTime> Now = () => DateTime.Now;

        /// <summary>LiveDb rollback: an opaque copy of the index counters (null when the context cannot capture them).</summary>
        public Func<object> CaptureCounters;

        /// <summary>LiveDb rollback: puts the counters of <see cref="CaptureCounters"/> back.</summary>
        public Action<object> RestoreCounters;

        public GameDataContext()
        {
            var item = 0;
            var monster = 0;
            var map = 0;
            var npc = 0;
            var quest = 0;
            var shop = 0;
            var conquest = 0;
            var respawn = 0;

            // Standalone counters (tests): continue after the highest existing index.
            NextItemIndex = () => item = Math.Max(item, Items.Count == 0 ? 0 : Items.Max(x => x.Index)) + 1;
            NextMonsterIndex = () => monster = Math.Max(monster, Monsters.Count == 0 ? 0 : Monsters.Max(x => x.Index)) + 1;
            NextMapIndex = () => map = Math.Max(map, Maps.Count == 0 ? 0 : Maps.Max(x => x.Index)) + 1;
            NextNpcIndex = () => npc = Math.Max(npc, Npcs.Count == 0 ? 0 : Npcs.Max(x => x.Index)) + 1;
            NextQuestIndex = () => quest = Math.Max(quest, Quests.Count == 0 ? 0 : Quests.Max(x => x.Index)) + 1;
            NextGameShopIndex = () => shop = Math.Max(shop, GameShop.Count == 0 ? 0 : GameShop.Max(x => x.GIndex)) + 1;
            NextConquestIndex = () => conquest = Math.Max(conquest, Conquests.Count == 0 ? 0 : Conquests.Max(x => x.Index)) + 1;
            NextRespawnIndex = () => respawn = Math.Max(respawn, Maps.SelectMany(m => m.Respawns).Select(r => r.RespawnIndex).DefaultIfEmpty(0).Max()) + 1;
        }

        /// <summary>Wraps an Envir (the Edit envir in production). Counters allocate exactly like Envir.CreateXInfo / AddToGameShop.</summary>
        public static GameDataContext FromEnvir(Envir envir)
        {
            var ctx = new GameDataContext
            {
                Items = envir.ItemInfoList,
                Monsters = envir.MonsterInfoList,
                Magics = envir.MagicInfoList,
                Npcs = envir.NPCInfoList,
                Quests = envir.QuestInfoList,
                Maps = envir.MapInfoList,
                GameShop = envir.GameShopList,
                Conquests = envir.ConquestInfoList,
                QuestConditions = envir.QuestConditionList,
                QuestTexts = envir.QuestTextList,
                Storylines = envir.StorylineList,
                QuestGroups = envir.QuestGroupList,
                QuestPools = envir.QuestPoolList,
                Dragon = () => envir.DragonInfo,
                NextItemIndex = () => ++envir.ItemIndex,
                NextMonsterIndex = () => ++envir.MonsterIndex,
                NextMapIndex = () => ++envir.MapIndex,
                NextNpcIndex = () => ++envir.NPCIndex,
                NextQuestIndex = () => ++envir.QuestIndex,
                NextGameShopIndex = () => ++envir.GameshopIndex,
                NextConquestIndex = () => ++envir.ConquestIndex,
                NextRespawnIndex = () => ++envir.RespawnIndex,
                GameShopEmptied = () => envir.GameshopIndex = 0,
                MineSetCount = () => Settings.MineSetList.Count,
                MineSetNames = () => Settings.MineSetList.Select(m => m?.Name ?? string.Empty).ToList(),
                DefaultGameShopGoldPrice = () => (uint)Math.Max(0, Math.Min(uint.MaxValue, 1000L * Settings.CredxGold)),
                Now = () => DateTime.Now,
                CaptureCounters = () => new[]
                {
                    envir.ItemIndex, envir.MonsterIndex, envir.MapIndex, envir.NPCIndex, envir.QuestIndex, envir.GameshopIndex, envir.ConquestIndex,
                    envir.RespawnIndex,
                },
                RestoreCounters = o =>
                {
                    if (!(o is int[] c) || c.Length != 8) return;
                    envir.ItemIndex = c[0];
                    envir.MonsterIndex = c[1];
                    envir.MapIndex = c[2];
                    envir.NPCIndex = c[3];
                    envir.QuestIndex = c[4];
                    envir.GameshopIndex = c[5];
                    envir.ConquestIndex = c[6];
                    envir.RespawnIndex = c[7];
                },
            };

            return ctx;
        }

        // ------------------------------------------------------------------ FK lookups

        private HashSet<int> _itemSet, _monsterSet, _mapSet, _npcSet, _questSet, _conquestSet, _magicSet;

        /// <summary>Drops the FK caches (call after inserts / deletes).</summary>
        public void Invalidate()
        {
            _itemSet = _monsterSet = _mapSet = _npcSet = _questSet = _conquestSet = _magicSet = null;
        }

        /// <summary>True when <paramref name="value"/> names an existing record of the FK target (items, monsters, maps, ...).</summary>
        public bool Exists(string target, int value)
        {
            switch (target)
            {
                case "items":
                    return (_itemSet ??= new HashSet<int>(Items.Select(x => x.Index))).Contains(value);
                case "monsters":
                    return (_monsterSet ??= new HashSet<int>(Monsters.Select(x => x.Index))).Contains(value);
                case "maps":
                    return (_mapSet ??= new HashSet<int>(Maps.Select(x => x.Index))).Contains(value);
                case "npcs":
                    return (_npcSet ??= new HashSet<int>(Npcs.Select(x => x.Index))).Contains(value);
                case "quests":
                    return (_questSet ??= new HashSet<int>(Quests.Select(x => x.Index))).Contains(value);
                case "conquests":
                    return (_conquestSet ??= new HashSet<int>(Conquests.Select(x => x.Index))).Contains(value);
                case "magics":
                    return (_magicSet ??= new HashSet<int>(Magics.Select(x => (int)x.Spell))).Contains(value);
                case "minesets":
                    return value >= 1 && value <= MineSetCount();
                default:
                    return true;
            }
        }

        public ItemInfo FindItem(int index) => Items.FirstOrDefault(x => x.Index == index);

        public MapInfo FindMap(int index) => Maps.FirstOrDefault(x => x.Index == index);

        public ConquestInfo FindConquest(int index) => Conquests.FirstOrDefault(x => x.Index == index);
    }
}
