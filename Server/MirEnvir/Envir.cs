using ClientPackets;
using Server.Library.MirDatabase;
using Server.Library.Utils;
using Server.MirDatabase;
using Server.MirEnvir.Storage;
using Server.MirNetwork;
using Server.MirObjects;
using Server.MirObjects.Monsters;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.RegularExpressions;
using S = ServerPackets;

namespace Server.MirEnvir
{
    public class MobThread
    {
        public int Id = 0;
        public long LastRunTime = 0;
        public long StartTime = 0;
        public long EndTime = 0;
        public LinkedList<MapObject> ObjectsList = new LinkedList<MapObject>();
        public LinkedListNode<MapObject> _current = null;
        /// <summary>Due-time scheduler of this thread's monsters (roadmap P9-05); used instead of walking ObjectsList.</summary>
        public readonly ObjectScheduler Scheduler = new ObjectScheduler();
        public bool Stop = false;
        /// <summary>Set when the thread ended on an unexpected exception; the barrier then ignores it (roadmap P5).</summary>
        public volatile bool Dead;
        /// <summary>
        /// Set when the thread has returned (shutdown interrupt or loop end). The barrier treats it as stopped even if the game
        /// thread re-armed it (Stop = false) after it left, which would otherwise hold a shutdown for the full timeout.
        /// </summary>
        public volatile bool Exited;
        /// <summary>OS thread id (Windows), for per-thread CPU in the web admin; 0 until the thread starts, or off Windows.</summary>
        public volatile int NativeThreadId;

        // Load counters for the dashboard (due scheduler only). Written by the owning thread during its pass; published and reset
        // by the game thread once a second while the thread is parked at the barrier (Envir.PublishMobLoad).
        internal long WorkTicks, MaxPassTicks;
        internal int Passes, Overruns, Processed, Awake;

        /// <summary>Owner thread, after one scheduler pass.</summary>
        internal void NotePass(long startTimestamp, int processed, bool overran)
        {
            var ticks = Stopwatch.GetTimestamp() - startTimestamp;
            WorkTicks += ticks;
            if (ticks > MaxPassTicks) MaxPassTicks = ticks;
            Passes++;
            Processed += processed;
            if (overran) Overruns++;
        }
    }

    /// <summary>One mob thread's last one-second window (see <see cref="LoopProfiler.Snapshot.MobThreads"/>). Immutable.</summary>
    public sealed class MobThreadLoad
    {
        public int Id { get; init; }
        public bool OnGameThread { get; init; }
        public bool Dead { get; init; }
        /// <summary>Milliseconds spent in scheduler passes in the window.</summary>
        public double BusyMs { get; init; }
        public double MaxPassMs { get; init; }
        public int Passes { get; init; }
        /// <summary>Passes that stopped at their 10 ms deadline with work possibly left.</summary>
        public int Overruns { get; init; }
        /// <summary>Monster Process() calls (AI runs) in the window.</summary>
        public int Processed { get; init; }
        /// <summary>Distinct monsters that ran AI in the window.</summary>
        public int Awake { get; init; }
        /// <summary>Entries in this thread's scheduler heap (includes stale entries until they are popped or compacted).</summary>
        public int Queued { get; init; }
        /// <summary>Worst lateness (ms past due) in the window.</summary>
        public long LateMs { get; init; }
    }

    /// <summary>OS thread ids for the web admin's per-thread CPU (Windows only; 0 elsewhere).</summary>
    internal static class NativeThread
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public static int CurrentId()
        {
            try
            {
                return OperatingSystem.IsWindows() ? (int)GetCurrentThreadId() : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
    public class RandomProvider
    {
        private static int seed = Environment.TickCount;
        private static readonly ThreadLocal<Random> RandomWrapper = new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref seed)));

        public static Random GetThreadRandom() =>
            RandomWrapper.Value;

        public int Next() =>
            RandomWrapper.Value.Next();
        public int Next(int maxValue) =>
            RandomWrapper.Value.Next(maxValue);
        public int Next(int minValue, int maxValue) =>
            RandomWrapper.Value.Next(minValue, maxValue);
    }

    public class Envir
    {
        public static Envir Main { get; } = new Envir();

        public static Envir Edit { get; } = new Envir();

        /// <summary>
        /// Where system data is loaded from and saved to: SQLite (SystemData.db) since roadmap P7 phase 7.3. The server never writes
        /// Server.MirDB; it only imports it on the first start. Tests and offline tools may set a <see cref="BinaryWorldStore"/>
        /// before the first load (internal setter).
        /// </summary>
        internal static IWorldStore Store
        {
            get
            {
                var store = Volatile.Read(ref _store);
                if (store != null) return store;
                Interlocked.CompareExchange(ref _store, new SqliteWorldStore(), null);
                return _store;
            }
            set
            {
                Volatile.Write(ref _store, value);
                DesignStore.Disabled = value is not SqliteWorldStore; // tests and tools on Server.MirDB read the design files
            }
        }

        private static IWorldStore _store;

        protected static MessageQueue MessageQueue => MessageQueue.Instance;

        public static object AccountLock = new object();
        public static object LoadLock = new object();

        public const int MinVersion = 60;
        public const int Version = 120;   // 120: persisted CharacterInfo chat ban; 119: persisted social toggles/play time/profile, mail sender, spell AutoCast, guild log, market sales, GameShop deal prices
        public const int CustomVersion = 0;
        public static readonly string DatabasePath = Path.Combine(".", "Server.MirDB");
        public static readonly string AccountPath = Path.Combine(".", "Server.MirADB");
        public static readonly string BackUpPath = Path.Combine(".", "Back Up");
        public static readonly string AccountsBackUpPath = Path.Combine(".", "Back Up", "Accounts");
        public static readonly string DatabaseBackUpPath = Path.Combine(".", "Back Up", "Database");
        public static readonly string ArchivePath = Path.Combine(".", "Archive");
        public bool ResetGS = false;
        public bool GuildRefreshNeeded;

        private static readonly Regex AccountIDReg, PasswordReg, EMailReg, CharacterReg;

        public static int LoadVersion;
        public static int LoadCustomVersion;

        private readonly DateTime _startTime = DateTime.UtcNow;
        public readonly Stopwatch Stopwatch = Stopwatch.StartNew();

        public long Time { get; private set; }
        public RespawnTimer RespawnTick = new RespawnTimer();

        private static List<string> DisabledCharNames = new List<string>();
        private static List<string> LineMessages = new List<string>();

        public static ConcurrentDictionary<string, DateTime> IPBlocks = new ConcurrentDictionary<string, DateTime>();

        public static ConcurrentDictionary<string, MirConnectionLog> ConnectionLogs = new ConcurrentDictionary<string, MirConnectionLog>();

        public DateTime Now =>
            _startTime.AddMilliseconds(Time);

        // Volatile: mob threads poll it inside RunDue so a stop ends their pass promptly (monster threading review, 2026-10-11).
        private volatile bool _running;
        public bool Running { get => _running; private set => _running = value; }


        private static uint _objectID;
        public uint ObjectID => Interlocked.Increment(ref _objectID); // atomic: mob threads create objects too (roadmap P5)

        public static int _playerCount;
        public int PlayerCount => Players.Count;

        public int[] OnlineRankingCount = new int[6];
        public int HeroCount => Heroes.Count;

        public RandomProvider Random = new RandomProvider();

        private Thread _thread;
        private TcpListener _listener;
        private static bool StatusPortEnabled => Settings.StatusPortEnabled; // [Network] StatusPortEnabled / StatusPort (roadmap P6)
        public List<MirStatusConnection> StatusConnections = new List<MirStatusConnection>();
        private TcpListener _StatusPort;
        private int _sessionID;
        public List<MirConnection> Connections = new List<MirConnection>();

        //Server DB
        public int MapIndex, ItemIndex, MonsterIndex, NPCIndex, QuestIndex, GameshopIndex, ConquestIndex, RespawnIndex, ScriptIndex;
        public List<MapInfo> MapInfoList = new List<MapInfo>();
        public List<ItemInfo> ItemInfoList = new List<ItemInfo>();
        public List<MonsterInfo> MonsterInfoList = new List<MonsterInfo>();
        public List<MagicInfo> MagicInfoList = new List<MagicInfo>();
        public List<NPCInfo> NPCInfoList = new List<NPCInfo>();
        public DragonInfo DragonInfo = new DragonInfo();
        public List<QuestInfo> QuestInfoList = new List<QuestInfo>();
        public List<GameShopItem> GameShopList = new List<GameShopItem>();
        public List<RecipeInfo> RecipeInfoList = new List<RecipeInfo>();
        public List<BuffInfo> BuffInfoList = new List<BuffInfo>();
        public List<ConquestInfo> ConquestInfoList = new List<ConquestInfo>();
        public List<GTMap> GTMapList = new List<GTMap>();

        /// <summary>
        /// S.MapDetails parts per MapInfo, built on the first C.RequestMapDetails for that map (PlayerObject.RequestMapDetails)
        /// and shared by every requester (packets are never mutated after their first send). Game thread only. Dropped by
        /// <see cref="InvalidateMapDetails"/> after every LiveDb apply to Main and on a DB load.
        /// </summary>
        public readonly Dictionary<MapInfo, MapDetailsEntry> MapDetailsCache = new Dictionary<MapInfo, MapDetailsEntry>();

        public sealed class MapDetailsEntry
        {
            public S.MapDetails[] Parts;
            public MonsterInfo[] Monsters; // distinct monsters the parts reference: CheckMonsterInfo per requester
        }

        public void InvalidateMapDetails()
        {
            MapDetailsCache.Clear();
        }

        //User DB
        public int NextAccountID, NextCharacterID, NextGuildID, NextHeroID;
        public ulong NextUserItemID, NextAuctionID, NextMailID, NextRecipeID;
        public List<AccountInfo> AccountList = new List<AccountInfo>();
        public List<CharacterInfo> CharacterList = new List<CharacterInfo>();
        public List<GuildInfo> GuildList = new List<GuildInfo>();
        public LinkedList<AuctionInfo> Auctions = new LinkedList<AuctionInfo>();
        public List<ConquestGuildInfo> ConquestList = new List<ConquestGuildInfo>();
        public Dictionary<int, int> GameshopLog = new Dictionary<int, int>();
        public Dictionary<int, MarketSaleHistory> MarketSales = new Dictionary<int, MarketSaleHistory>(); //v119: ItemIndex -> last sales
        // phase 2.6, not saved: sender CharacterInfo.Index -> player mail still in a recipient's box, oldest first (Sent folder; game thread)
        public Dictionary<int, List<SentMailEntry>> SentMailIndex = new Dictionary<int, List<SentMailEntry>>();
        public List<HeroInfo> HeroList = new List<HeroInfo>();

        public int GuildCount; //This shouldn't be needed?? -> remove in the future

        //Live Info
        public volatile bool Saving; // written by EndSaveAccounts on an IO thread (review, roadmap P5)
        public List<Map> MapList = new List<Map>();
        public List<SafeZoneInfo> StartPoints = new List<SafeZoneInfo>();
        public List<ItemInfo> StartItems = new List<ItemInfo>();

        public List<PlayerObject> Players = new List<PlayerObject>();
        public List<SpellObject> Spells = new List<SpellObject>();
        public List<NPCObject> NPCs = new List<NPCObject>();
        public List<GuildObject> Guilds = new List<GuildObject>();
        public List<ConquestObject> Conquests = new List<ConquestObject>();
        public List<HeroObject> Heroes = new List<HeroObject>();

        public LightSetting Lights;
        public LinkedList<MapObject> Objects = new LinkedList<MapObject>();
        public Dictionary<int, NPCScript> Scripts = new Dictionary<int, NPCScript>();
        public Dictionary<string, Timer> Timers = new Dictionary<string, Timer>();

        //multithread vars
        readonly object _locker = new object();
        public MobThread[] MobThreads = new MobThread[Settings.ThreadLimit];
        private readonly Thread[] MobThreading = new Thread[Settings.ThreadLimit];
        public int SpawnMultiplier = 1;//set this to 2 if you want double spawns (warning this can easily lag your server far beyond what you imagine)

        public List<string> CustomCommands = new List<string>();

        public Dragon DragonSystem;
        public NPCScript DefaultNPC, MonsterNPC, RobotNPC;

        public List<DropInfo> FishingDrops = new List<DropInfo>();
        public List<DropInfo> AwakeningDrops = new List<DropInfo>();

        public List<DropInfo> StrongboxDrops = new List<DropInfo>();
        public List<DropInfo> BlackstoneDrops = new List<DropInfo>();

        public List<GuildAtWar> GuildsAtWar = new List<GuildAtWar>();
        public List<MapRespawn> SavedSpawns = new List<MapRespawn>();

        public List<RankCharacterInfo> RankTop = new List<RankCharacterInfo>();
        public List<RankCharacterInfo>[] RankClass = new List<RankCharacterInfo>[5];

        static HttpServer http;

        static Envir()
        {
            AccountIDReg = new Regex(@"^[A-Za-z0-9]{" + Globals.MinAccountIDLength + "," + Globals.MaxAccountIDLength + "}$");
            PasswordReg = new Regex(@"^[A-Za-z0-9]{" + Globals.MinPasswordLength + "," + Globals.MaxPasswordLength + "}$");
            EMailReg = new Regex(@"\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*");
            CharacterReg = new Regex(@"^[\u4e00-\u9fa5_A-Za-z0-9]{" + Globals.MinCharacterNameLength + "," + Globals.MaxCharacterNameLength + "}$");
        }

        public static bool IsPasswordValid(string password)
        {
            if (string.IsNullOrEmpty(password)) return false;

            return PasswordReg.IsMatch(password);
        }

        public static int LastCount = 0, LastRealCount = 0;
        public static long LastRunTime = 0;
        /// <summary>OS id of the game thread (WorkLoop), for per-thread CPU in the web admin; 0 when not running or off Windows.</summary>
        public static volatile int GameThreadNativeId;
        public int MonsterCount;

        // Dashboard load window (due scheduler): a monster is counted as awake once per window via MapObject.ProfileWindow.
        // Game thread owns _profileWindow; _mainAwake counts monsters processed by the game thread's own scheduler.
        private int _profileWindow = 1, _mainAwake;

        private long warTime, guildTime, conquestTime, rentalItemsTime, auctionTime, spawnTime, robotTime, timerTime;
        private int dailyTime = DateTime.UtcNow.Day;
        internal bool MagicExists(Spell spell)
        {
            for (var i = 0; i < MagicInfoList.Count; i++)
            {
                if (MagicInfoList[i].Spell == spell) return true;
            }
            return false;
        }

        internal void UpdateMagicInfo()
        {
            for (var i = 0; i < MagicInfoList.Count; i++)
            {
                switch (MagicInfoList[i].Spell)
                {
                    //warrior
                    case Spell.Thrusting:
                        MagicInfoList[i].MultiplierBase = 0.25f;
                        MagicInfoList[i].MultiplierBonus = 0.25f;
                        break;
                    case Spell.HalfMoon:
                        MagicInfoList[i].MultiplierBase = 0.3f;
                        MagicInfoList[i].MultiplierBonus = 0.1f;
                        break;
                    case Spell.ShoulderDash:
                        MagicInfoList[i].MPowerBase = 4;
                        break;
                    case Spell.TwinDrakeBlade:
                        MagicInfoList[i].MultiplierBase = 0.8f;
                        MagicInfoList[i].MultiplierBonus = 0.1f;
                        break;
                    case Spell.FlamingSword:
                        MagicInfoList[i].MultiplierBase = 1.4f;
                        MagicInfoList[i].MultiplierBonus = 0.4f;
                        break;
                    case Spell.CrossHalfMoon:
                        MagicInfoList[i].MultiplierBase = 0.4f;
                        MagicInfoList[i].MultiplierBonus = 0.1f;
                        break;
                    case Spell.BladeAvalanche:
                        MagicInfoList[i].MultiplierBase = 1f;
                        MagicInfoList[i].MultiplierBonus = 0.4f;
                        break;
                    case Spell.SlashingBurst:
                        MagicInfoList[i].MultiplierBase = 3.25f;
                        MagicInfoList[i].MultiplierBonus = 0.25f;
                        break;
                    //wiz
                    case Spell.Repulsion:
                        MagicInfoList[i].MPowerBase = 4;
                        break;
                    //tao
                    case Spell.Poisoning:
                        MagicInfoList[i].MPowerBase = 0;
                        break;
                    case Spell.Curse:
                        MagicInfoList[i].MPowerBase = 20;
                        break;
                    case Spell.Plague:
                        MagicInfoList[i].MPowerBase = 0;
                        MagicInfoList[i].PowerBase = 0;
                        break;
                    //sin
                    case Spell.FatalSword:
                        MagicInfoList[i].MPowerBase = 20;
                        break;
                    case Spell.DoubleSlash:
                        MagicInfoList[i].MultiplierBase = 0.8f;
                        MagicInfoList[i].MultiplierBonus = 0.1f;
                        break;
                    case Spell.FireBurst:
                        MagicInfoList[i].MPowerBase = 4;
                        break;
                    case Spell.MoonLight:
                        MagicInfoList[i].MPowerBase = 20;
                        break;
                    case Spell.DarkBody:
                        MagicInfoList[i].MPowerBase = 20;
                        break;
                    case Spell.Hemorrhage:
                        MagicInfoList[i].MultiplierBase = 0.2f;
                        MagicInfoList[i].MultiplierBonus = 0.05f;
                        break;
                    case Spell.CrescentSlash:
                        MagicInfoList[i].MultiplierBase = 1f;
                        MagicInfoList[i].MultiplierBonus = 0.4f;
                        break;
                }
            }
        }

        internal void FillMagicInfoList()
        {
            //Warrior
            if (!MagicExists(Spell.Fencing))
                MagicInfoList.Add(new MagicInfo { Name = "Fencing", Spell = Spell.Fencing, Icon = 2, Level1 = 7, Level2 = 9, Level3 = 12, Need1 = 270, Need2 = 600, Need3 = 1300, Range = 0 });
            if (!MagicExists(Spell.Slaying))
                MagicInfoList.Add(new MagicInfo { Name = "Slaying", Spell = Spell.Slaying, Icon = 6, Level1 = 15, Level2 = 17, Level3 = 20, Need1 = 500, Need2 = 1100, Need3 = 1800, Range = 0 });
            if (!MagicExists(Spell.Thrusting))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Thrusting",
                    Spell = Spell.Thrusting,
                    Icon = 11,
                    Level1 = 22,
                    Level2 = 24,
                    Level3 = 27,
                    Need1 = 2000,
                    Need2 = 3500,
                    Need3 = 6000,
                    Range = 0,
                    MultiplierBase = 0.25f,
                    MultiplierBonus = 0.25f
                });
            if (!MagicExists(Spell.HalfMoon))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "HalfMoon",
                    Spell = Spell.HalfMoon,
                    Icon = 24,
                    Level1 = 26,
                    Level2 = 28,
                    Level3 = 31,
                    Need1 = 5000,
                    Need2 = 8000,
                    Need3 = 14000,
                    BaseCost = 3,
                    Range = 0,
                    MultiplierBase = 0.3f,
                    MultiplierBonus = 0.1f
                });
            if (!MagicExists(Spell.ShoulderDash))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ShoulderDash",
                    Spell = Spell.ShoulderDash,
                    Icon = 26,
                    Level1 = 30,
                    Level2 = 32,
                    Level3 = 34,
                    Need1 = 3000,
                    Need2 = 4000,
                    Need3 = 6000,
                    BaseCost = 4,
                    LevelCost = 4,
                    DelayBase = 2500,
                    Range = 0,
                    MPowerBase = 4
                });
            if (!MagicExists(Spell.TwinDrakeBlade))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "TwinDrakeBlade",
                    Spell = Spell.TwinDrakeBlade,
                    Icon = 37,
                    Level1 = 32,
                    Level2 = 34,
                    Level3 = 37,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 10000,
                    BaseCost = 10,
                    Range = 0,
                    MultiplierBase = 0.8f,
                    MultiplierBonus = 0.1f
                });
            if (!MagicExists(Spell.Entrapment))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Entrapment",
                    Spell = Spell.Entrapment,
                    Icon = 46,
                    Level1 = 32,
                    Level2 = 35,
                    Level3 = 37,
                    Need1 = 2000,
                    Need2 = 3500,
                    Need3 = 5500,
                    BaseCost = 15,
                    LevelCost = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.FlamingSword))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FlamingSword",
                    Spell = Spell.FlamingSword,
                    Icon = 25,
                    Level1 = 35,
                    Level2 = 37,
                    Level3 = 40,
                    Need1 = 2000,
                    Need2 = 4000,
                    Need3 = 6000,
                    BaseCost = 7,
                    Range = 0,
                    MultiplierBase = 1.4f,
                    MultiplierBonus = 0.4f
                });
            if (!MagicExists(Spell.LionRoar))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "LionRoar",
                    Spell = Spell.LionRoar,
                    Icon = 42,
                    Level1 = 36,
                    Level2 = 39,
                    Level3 = 41,
                    Need1 = 5000,
                    Need2 = 8000,
                    Need3 = 12000,
                    BaseCost = 14,
                    LevelCost = 4,
                    Range = 0
                });
            if (!MagicExists(Spell.CrossHalfMoon))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "CrossHalfMoon",
                    Spell = Spell.CrossHalfMoon,
                    Icon = 33,
                    Level1 = 38,
                    Level2 = 40,
                    Level3 = 42,
                    Need1 = 7000,
                    Need2 = 11000,
                    Need3 = 16000,
                    BaseCost = 6,
                    Range = 0,
                    MultiplierBase = 0.4f,
                    MultiplierBonus = 0.1f
                });
            if (!MagicExists(Spell.BladeAvalanche))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "BladeAvalanche",
                    Spell = Spell.BladeAvalanche,
                    Icon = 43,
                    Level1 = 38,
                    Level2 = 41,
                    Level3 = 43,
                    Need1 = 5000,
                    Need2 = 8000,
                    Need3 = 12000,
                    BaseCost = 14,
                    LevelCost = 4,
                    Range = 0,
                    MultiplierBonus = 0.3f
                });
            if (!MagicExists(Spell.ProtectionField))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ProtectionField",
                    Spell = Spell.ProtectionField,
                    Icon = 50,
                    Level1 = 39,
                    Level2 = 42,
                    Level3 = 45,
                    Need1 = 6000,
                    Need2 = 12000,
                    Need3 = 18000,
                    BaseCost = 23,
                    LevelCost = 6,
                    Range = 0
                });
            if (!MagicExists(Spell.Rage))
                MagicInfoList.Add(new MagicInfo
                { Name = "Rage", Spell = Spell.Rage, Icon = 49, Level1 = 44, Level2 = 47, Level3 = 50, Need1 = 8000, Need2 = 14000, Need3 = 20000, BaseCost = 20, LevelCost = 5, Range = 0 });
            if (!MagicExists(Spell.CounterAttack))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "CounterAttack",
                    Spell = Spell.CounterAttack,
                    Icon = 72,
                    Level1 = 47,
                    Level2 = 51,
                    Level3 = 55,
                    Need1 = 7000,
                    Need2 = 11000,
                    Need3 = 15000,
                    BaseCost = 12,
                    LevelCost = 4,
                    DelayBase = 24000,
                    Range = 0,
                    MultiplierBonus = 0.4f
                });
            if (!MagicExists(Spell.SlashingBurst))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SlashingBurst",
                    Spell = Spell.SlashingBurst,
                    Icon = 55,
                    Level1 = 50,
                    Level2 = 53,
                    Level3 = 56,
                    Need1 = 10000,
                    Need2 = 16000,
                    Need3 = 24000,
                    BaseCost = 25,
                    LevelCost = 4,
                    MPowerBase = 1,
                    PowerBase = 3,
                    DelayBase = 14000,
                    DelayReduction = 4000,
                    Range = 0,
                    MultiplierBase = 3.25f,
                    MultiplierBonus = 0.25f
                });
            if (!MagicExists(Spell.Fury))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Fury",
                    Spell = Spell.Fury,
                    Icon = 76,
                    Level1 = 45,
                    Level2 = 48,
                    Level3 = 51,
                    Need1 = 8000,
                    Need2 = 14000,
                    Need3 = 20000,
                    BaseCost = 10,
                    LevelCost = 4,
                    DelayBase = 600000,
                    DelayReduction = 120000,
                    Range = 0
                });
            if (!MagicExists(Spell.ImmortalSkin))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ImmortalSkin",
                    Spell = Spell.ImmortalSkin,
                    Icon = 80,
                    Level1 = 60,
                    Level2 = 61,
                    Level3 = 62,
                    Need1 = 1560,
                    Need2 = 2200,
                    Need3 = 3000,
                    BaseCost = 10,
                    LevelCost = 4,
                    DelayBase = 600000,
                    DelayReduction = 120000,
                    Range = 0
                });

            //Wizard
            if (!MagicExists(Spell.FireBall))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FireBall",
                    Spell = Spell.FireBall,
                    Icon = 0,
                    Level1 = 7,
                    Level2 = 9,
                    Level3 = 11,
                    Need1 = 200,
                    Need2 = 350,
                    Need3 = 700,
                    BaseCost = 3,
                    LevelCost = 2,
                    MPowerBase = 8,
                    PowerBase = 2,
                    Range = 9
                });
            if (!MagicExists(Spell.Repulsion))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Repulsion",
                    Spell = Spell.Repulsion,
                    Icon = 7,
                    Level1 = 12,
                    Level2 = 15,
                    Level3 = 19,
                    Need1 = 500,
                    Need2 = 1300,
                    Need3 = 2200,
                    BaseCost = 2,
                    LevelCost = 2,
                    Range = 0,
                    MPowerBase = 4
                });
            if (!MagicExists(Spell.ElectricShock))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ElectricShock",
                    Spell = Spell.ElectricShock,
                    Icon = 19,
                    Level1 = 13,
                    Level2 = 18,
                    Level3 = 24,
                    Need1 = 530,
                    Need2 = 1100,
                    Need3 = 2200,
                    BaseCost = 3,
                    LevelCost = 1,
                    Range = 9
                });
            if (!MagicExists(Spell.GreatFireBall))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "GreatFireBall",
                    Spell = Spell.GreatFireBall,
                    Icon = 4,
                    Level1 = 15,
                    Level2 = 18,
                    Level3 = 21,
                    Need1 = 2000,
                    Need2 = 2700,
                    Need3 = 3500,
                    BaseCost = 5,
                    LevelCost = 1,
                    MPowerBase = 6,
                    PowerBase = 10,
                    Range = 9
                });
            if (!MagicExists(Spell.HellFire))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "HellFire",
                    Spell = Spell.HellFire,
                    Icon = 8,
                    Level1 = 16,
                    Level2 = 20,
                    Level3 = 24,
                    Need1 = 700,
                    Need2 = 2700,
                    Need3 = 3500,
                    BaseCost = 10,
                    LevelCost = 3,
                    MPowerBase = 14,
                    PowerBase = 6,
                    Range = 0
                });
            if (!MagicExists(Spell.ThunderBolt))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ThunderBolt",
                    Spell = Spell.ThunderBolt,
                    Icon = 10,
                    Level1 = 17,
                    Level2 = 20,
                    Level3 = 23,
                    Need1 = 500,
                    Need2 = 2000,
                    Need3 = 3500,
                    BaseCost = 9,
                    LevelCost = 2,
                    MPowerBase = 8,
                    MPowerBonus = 20,
                    PowerBase = 9,
                    Range = 9
                });
            if (!MagicExists(Spell.Teleport))
                MagicInfoList.Add(new MagicInfo
                { Name = "Teleport", Spell = Spell.Teleport, Icon = 20, Level1 = 19, Level2 = 22, Level3 = 25, Need1 = 350, Need2 = 1000, Need3 = 2000, BaseCost = 10, LevelCost = 3, Range = 0 });
            if (!MagicExists(Spell.FireBang))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FireBang",
                    Spell = Spell.FireBang,
                    Icon = 22,
                    Level1 = 22,
                    Level2 = 25,
                    Level3 = 28,
                    Need1 = 3000,
                    Need2 = 5000,
                    Need3 = 10000,
                    BaseCost = 14,
                    LevelCost = 4,
                    MPowerBase = 8,
                    PowerBase = 8,
                    Range = 9
                });
            if (!MagicExists(Spell.FireWall))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FireWall",
                    Spell = Spell.FireWall,
                    Icon = 21,
                    Level1 = 24,
                    Level2 = 28,
                    Level3 = 33,
                    Need1 = 4000,
                    Need2 = 10000,
                    Need3 = 20000,
                    BaseCost = 30,
                    LevelCost = 5,
                    MPowerBase = 3,
                    PowerBase = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.Lightning))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Lightning",
                    Spell = Spell.Lightning,
                    Icon = 9,
                    Level1 = 26,
                    Level2 = 29,
                    Level3 = 32,
                    Need1 = 3000,
                    Need2 = 6000,
                    Need3 = 12000,
                    BaseCost = 38,
                    LevelCost = 7,
                    MPowerBase = 12,
                    PowerBase = 12,
                    Range = 0
                });
            if (!MagicExists(Spell.FrostCrunch))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FrostCrunch",
                    Spell = Spell.FrostCrunch,
                    Icon = 38,
                    Level1 = 28,
                    Level2 = 30,
                    Level3 = 33,
                    Need1 = 3000,
                    Need2 = 5000,
                    Need3 = 8000,
                    BaseCost = 15,
                    LevelCost = 3,
                    MPowerBase = 12,
                    PowerBase = 12,
                    Range = 9
                });
            if (!MagicExists(Spell.ThunderStorm))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ThunderStorm",
                    Spell = Spell.ThunderStorm,
                    Icon = 23,
                    Level1 = 30,
                    Level2 = 32,
                    Level3 = 34,
                    Need1 = 4000,
                    Need2 = 8000,
                    Need3 = 12000,
                    BaseCost = 29,
                    LevelCost = 9,
                    MPowerBase = 10,
                    MPowerBonus = 20,
                    PowerBase = 10,
                    PowerBonus = 20,
                    Range = 0
                });
            if (!MagicExists(Spell.MagicShield))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MagicShield",
                    Spell = Spell.MagicShield,
                    Icon = 30,
                    Level1 = 31,
                    Level2 = 34,
                    Level3 = 38,
                    Need1 = 3000,
                    Need2 = 7000,
                    Need3 = 10000,
                    BaseCost = 35,
                    LevelCost = 5,
                    Range = 0
                });
            if (!MagicExists(Spell.TurnUndead))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "TurnUndead",
                    Spell = Spell.TurnUndead,
                    Icon = 31,
                    Level1 = 32,
                    Level2 = 35,
                    Level3 = 39,
                    Need1 = 3000,
                    Need2 = 7000,
                    Need3 = 10000,
                    BaseCost = 52,
                    LevelCost = 13,
                    Range = 9
                });
            if (!MagicExists(Spell.Vampirism))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Vampirism",
                    Spell = Spell.Vampirism,
                    Icon = 47,
                    Level1 = 33,
                    Level2 = 36,
                    Level3 = 40,
                    Need1 = 3000,
                    Need2 = 5000,
                    Need3 = 8000,
                    BaseCost = 26,
                    LevelCost = 13,
                    MPowerBase = 12,
                    PowerBase = 12,
                    Range = 9
                });
            if (!MagicExists(Spell.IceStorm))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "IceStorm",
                    Spell = Spell.IceStorm,
                    Icon = 32,
                    Level1 = 35,
                    Level2 = 37,
                    Level3 = 40,
                    Need1 = 4000,
                    Need2 = 8000,
                    Need3 = 12000,
                    BaseCost = 33,
                    LevelCost = 3,
                    MPowerBase = 12,
                    PowerBase = 14,
                    Range = 9
                });
            if (!MagicExists(Spell.FlameDisruptor))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FlameDisruptor",
                    Spell = Spell.FlameDisruptor,
                    Icon = 34,
                    Level1 = 38,
                    Level2 = 40,
                    Level3 = 42,
                    Need1 = 5000,
                    Need2 = 9000,
                    Need3 = 14000,
                    BaseCost = 28,
                    LevelCost = 3,
                    MPowerBase = 15,
                    MPowerBonus = 20,
                    PowerBase = 9,
                    Range = 9
                });
            if (!MagicExists(Spell.Mirroring))
                MagicInfoList.Add(new MagicInfo
                { Name = "Mirroring", Spell = Spell.Mirroring, Icon = 41, Level1 = 41, Level2 = 43, Level3 = 45, Need1 = 6000, Need2 = 11000, Need3 = 16000, BaseCost = 21, Range = 0 });
            if (!MagicExists(Spell.FlameField))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FlameField",
                    Spell = Spell.FlameField,
                    Icon = 44,
                    Level1 = 42,
                    Level2 = 43,
                    Level3 = 45,
                    Need1 = 6000,
                    Need2 = 11000,
                    Need3 = 16000,
                    BaseCost = 45,
                    LevelCost = 8,
                    MPowerBase = 100,
                    PowerBase = 25,
                    Range = 9
                });
            if (!MagicExists(Spell.Blizzard))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Blizzard",
                    Spell = Spell.Blizzard,
                    Icon = 51,
                    Level1 = 44,
                    Level2 = 47,
                    Level3 = 50,
                    Need1 = 8000,
                    Need2 = 16000,
                    Need3 = 24000,
                    BaseCost = 65,
                    LevelCost = 10,
                    MPowerBase = 30,
                    MPowerBonus = 10,
                    PowerBase = 20,
                    PowerBonus = 5,
                    Range = 9
                });
            if (!MagicExists(Spell.MagicBooster))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MagicBooster",
                    Spell = Spell.MagicBooster,
                    Icon = 73,
                    Level1 = 47,
                    Level2 = 49,
                    Level3 = 52,
                    Need1 = 12000,
                    Need2 = 18000,
                    Need3 = 24000,
                    BaseCost = 150,
                    LevelCost = 15,
                    Range = 0
                });
            if (!MagicExists(Spell.MeteorStrike))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MeteorStrike",
                    Spell = Spell.MeteorStrike,
                    Icon = 52,
                    Level1 = 49,
                    Level2 = 52,
                    Level3 = 55,
                    Need1 = 15000,
                    Need2 = 20000,
                    Need3 = 25000,
                    BaseCost = 115,
                    LevelCost = 17,
                    MPowerBase = 40,
                    MPowerBonus = 10,
                    PowerBase = 20,
                    PowerBonus = 15,
                    Range = 9
                });
            if (!MagicExists(Spell.IceThrust))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "IceThrust",
                    Spell = Spell.IceThrust,
                    Icon = 56,
                    Level1 = 53,
                    Level2 = 56,
                    Level3 = 59,
                    Need1 = 17000,
                    Need2 = 22000,
                    Need3 = 27000,
                    BaseCost = 100,
                    LevelCost = 20,
                    MPowerBase = 100,
                    PowerBase = 50,
                    Range = 0
                });
            if (!MagicExists(Spell.Blink))
                MagicInfoList.Add(new MagicInfo
                { Name = "Blink", Spell = Spell.Blink, Icon = 20, Level1 = 19, Level2 = 22, Level3 = 25, Need1 = 350, Need2 = 1000, Need3 = 2000, BaseCost = 10, LevelCost = 3, Range = 9 });
            //if (!MagicExists(Spell.FastMove)) MagicInfoList.Add(new MagicInfo { Name = "FastMove", Spell = Spell.ImmortalSkin, Icon = ?, Level1 = ?, Level2 = ?, Level3 = ?, Need1 = ?, Need2 = ?, Need3 = ?, BaseCost = ?, LevelCost = ?, DelayBase = ?, DelayReduction = ? });
            if (!MagicExists(Spell.StormEscape))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "StormEscape",
                    Spell = Spell.StormEscape,
                    Icon = 23,
                    Level1 = 60,
                    Level2 = 61,
                    Level3 = 62,
                    Need1 = 2200,
                    Need2 = 3300,
                    Need3 = 4400,
                    BaseCost = 65,
                    LevelCost = 8,
                    MPowerBase = 12,
                    PowerBase = 4,
                    Range = 9
                });


            //Taoist
            if (!MagicExists(Spell.Healing))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Healing",
                    Spell = Spell.Healing,
                    Icon = 1,
                    Level1 = 7,
                    Level2 = 11,
                    Level3 = 14,
                    Need1 = 150,
                    Need2 = 350,
                    Need3 = 700,
                    BaseCost = 3,
                    LevelCost = 2,
                    MPowerBase = 14,
                    Range = 9
                });
            if (!MagicExists(Spell.SpiritSword))
                MagicInfoList.Add(new MagicInfo
                { Name = "SpiritSword", Spell = Spell.SpiritSword, Icon = 3, Level1 = 9, Level2 = 12, Level3 = 15, Need1 = 350, Need2 = 1300, Need3 = 2700, Range = 0 });
            if (!MagicExists(Spell.Poisoning))
                MagicInfoList.Add(new MagicInfo
                { Name = "Poisoning", Spell = Spell.Poisoning, Icon = 5, Level1 = 14, Level2 = 17, Level3 = 20, Need1 = 700, Need2 = 1300, Need3 = 2700, BaseCost = 2, LevelCost = 1, Range = 9 });
            if (!MagicExists(Spell.SoulFireBall))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SoulFireBall",
                    Spell = Spell.SoulFireBall,
                    Icon = 12,
                    Level1 = 18,
                    Level2 = 21,
                    Level3 = 24,
                    Need1 = 1300,
                    Need2 = 2700,
                    Need3 = 4000,
                    BaseCost = 3,
                    LevelCost = 1,
                    MPowerBase = 8,
                    PowerBase = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.SummonSkeleton))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SummonSkeleton",
                    Spell = Spell.SummonSkeleton,
                    Icon = 16,
                    Level1 = 19,
                    Level2 = 22,
                    Level3 = 26,
                    Need1 = 1000,
                    Need2 = 2000,
                    Need3 = 3500,
                    BaseCost = 12,
                    LevelCost = 4,
                    Range = 0
                });
            if (!MagicExists(Spell.Hiding))
                MagicInfoList.Add(new MagicInfo
                { Name = "Hiding", Spell = Spell.Hiding, Icon = 17, Level1 = 20, Level2 = 23, Level3 = 26, Need1 = 1300, Need2 = 2700, Need3 = 5300, BaseCost = 1, LevelCost = 1, Range = 0 });
            if (!MagicExists(Spell.MassHiding))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MassHiding",
                    Spell = Spell.MassHiding,
                    Icon = 18,
                    Level1 = 21,
                    Level2 = 25,
                    Level3 = 29,
                    Need1 = 1300,
                    Need2 = 2700,
                    Need3 = 5300,
                    BaseCost = 2,
                    LevelCost = 2,
                    Range = 9
                });
            if (!MagicExists(Spell.SoulShield))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SoulShield",
                    Spell = Spell.SoulShield,
                    Icon = 13,
                    Level1 = 22,
                    Level2 = 24,
                    Level3 = 26,
                    Need1 = 2000,
                    Need2 = 3500,
                    Need3 = 7000,
                    BaseCost = 2,
                    LevelCost = 2,
                    Range = 9
                });
            if (!MagicExists(Spell.Revelation))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Revelation",
                    Spell = Spell.Revelation,
                    Icon = 27,
                    Level1 = 23,
                    Level2 = 25,
                    Level3 = 28,
                    Need1 = 1500,
                    Need2 = 2500,
                    Need3 = 4000,
                    BaseCost = 4,
                    LevelCost = 4,
                    Range = 9
                });
            if (!MagicExists(Spell.BlessedArmour))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "BlessedArmour",
                    Spell = Spell.BlessedArmour,
                    Icon = 14,
                    Level1 = 25,
                    Level2 = 27,
                    Level3 = 29,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 10000,
                    BaseCost = 2,
                    LevelCost = 2,
                    Range = 9
                });
            if (!MagicExists(Spell.EnergyRepulsor))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "EnergyRepulsor",
                    Spell = Spell.EnergyRepulsor,
                    Icon = 36,
                    Level1 = 27,
                    Level2 = 29,
                    Level3 = 31,
                    Need1 = 1800,
                    Need2 = 2400,
                    Need3 = 3200,
                    BaseCost = 2,
                    LevelCost = 2,
                    Range = 0,
                    MPowerBase = 4
                });
            if (!MagicExists(Spell.TrapHexagon))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "TrapHexagon",
                    Spell = Spell.TrapHexagon,
                    Icon = 15,
                    Level1 = 28,
                    Level2 = 30,
                    Level3 = 32,
                    Need1 = 2500,
                    Need2 = 5000,
                    Need3 = 10000,
                    BaseCost = 7,
                    LevelCost = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.Purification))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Purification",
                    Spell = Spell.Purification,
                    Icon = 39,
                    Level1 = 30,
                    Level2 = 32,
                    Level3 = 35,
                    Need1 = 3000,
                    Need2 = 5000,
                    Need3 = 8000,
                    BaseCost = 14,
                    LevelCost = 2,
                    Range = 9
                });
            if (!MagicExists(Spell.MassHealing))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MassHealing",
                    Spell = Spell.MassHealing,
                    Icon = 28,
                    Level1 = 31,
                    Level2 = 33,
                    Level3 = 36,
                    Need1 = 2000,
                    Need2 = 4000,
                    Need3 = 8000,
                    BaseCost = 28,
                    LevelCost = 3,
                    MPowerBase = 10,
                    PowerBase = 4,
                    Range = 9
                });
            if (!MagicExists(Spell.Hallucination))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Hallucination",
                    Spell = Spell.Hallucination,
                    Icon = 48,
                    Level1 = 31,
                    Level2 = 34,
                    Level3 = 36,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 9000,
                    BaseCost = 22,
                    LevelCost = 10,
                    Range = 9
                });
            if (!MagicExists(Spell.UltimateEnhancer))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "UltimateEnchancer",
                    Spell = Spell.UltimateEnhancer,
                    Icon = 35,
                    Level1 = 33,
                    Level2 = 35,
                    Level3 = 38,
                    Need1 = 5000,
                    Need2 = 7000,
                    Need3 = 10000,
                    BaseCost = 28,
                    LevelCost = 4,
                    Range = 9
                });
            if (!MagicExists(Spell.SummonShinsu))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SummonShinsu",
                    Spell = Spell.SummonShinsu,
                    Icon = 29,
                    Level1 = 35,
                    Level2 = 37,
                    Level3 = 40,
                    Need1 = 2000,
                    Need2 = 4000,
                    Need3 = 6000,
                    BaseCost = 28,
                    LevelCost = 4,
                    Range = 0
                });
            if (!MagicExists(Spell.Reincarnation))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Reincarnation",
                    Spell = Spell.Reincarnation,
                    Icon = 53,
                    Level1 = 37,
                    Level2 = 39,
                    Level3 = 41,
                    Need1 = 2000,
                    Need2 = 6000,
                    Need3 = 10000,
                    BaseCost = 125,
                    LevelCost = 17,
                    Range = 9
                });
            if (!MagicExists(Spell.SummonHolyDeva))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SummonHolyDeva",
                    Spell = Spell.SummonHolyDeva,
                    Icon = 40,
                    Level1 = 38,
                    Level2 = 41,
                    Level3 = 43,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 9000,
                    BaseCost = 28,
                    LevelCost = 4,
                    Range = 0
                });
            if (!MagicExists(Spell.Curse))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Curse",
                    Spell = Spell.Curse,
                    Icon = 45,
                    Level1 = 40,
                    Level2 = 42,
                    Level3 = 44,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 9000,
                    BaseCost = 17,
                    LevelCost = 3,
                    Range = 9,
                    MPowerBase = 20
                });
            if (!MagicExists(Spell.Plague))
                MagicInfoList.Add(new MagicInfo
                { Name = "Plague", Spell = Spell.Plague, Icon = 74, Level1 = 42, Level2 = 44, Level3 = 47, Need1 = 5000, Need2 = 9000, Need3 = 13000, BaseCost = 20, LevelCost = 5, Range = 9 });
            if (!MagicExists(Spell.PoisonCloud))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "PoisonCloud",
                    Spell = Spell.PoisonCloud,
                    Icon = 54,
                    Level1 = 43,
                    Level2 = 45,
                    Level3 = 48,
                    Need1 = 4000,
                    Need2 = 8000,
                    Need3 = 12000,
                    BaseCost = 30,
                    LevelCost = 5,
                    MPowerBase = 40,
                    PowerBase = 20,
                    DelayBase = 18000,
                    DelayReduction = 2000,
                    Range = 9
                });
            if (!MagicExists(Spell.EnergyShield))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "EnergyShield",
                    Spell = Spell.EnergyShield,
                    Icon = 57,
                    Level1 = 48,
                    Level2 = 51,
                    Level3 = 54,
                    Need1 = 5000,
                    Need2 = 9000,
                    Need3 = 13000,
                    BaseCost = 50,
                    LevelCost = 20,
                    Range = 9
                });
            if (!MagicExists(Spell.PetEnhancer))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "PetEnhancer",
                    Spell = Spell.PetEnhancer,
                    Icon = 78,
                    Level1 = 45,
                    Level2 = 48,
                    Level3 = 51,
                    Need1 = 4000,
                    Need2 = 8000,
                    Need3 = 12000,
                    BaseCost = 30,
                    LevelCost = 40,
                    Range = 0
                });
            if (!MagicExists(Spell.HealingCircle))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "HealingCircle",
                    Spell = Spell.HealingCircle,
                    Icon = 82,
                    Level1 = 39,
                    Level2 = 41,
                    Level3 = 43,
                    Need1 = 7000,
                    Need2 = 12000,
                    Need3 = 15000,
                    BaseCost = 10,
                    LevelCost = 100
                });
            //Assassin
            if (!MagicExists(Spell.FatalSword))
                MagicInfoList.Add(new MagicInfo { Name = "FatalSword", Spell = Spell.FatalSword, Icon = 58, Level1 = 7, Level2 = 9, Level3 = 12, Need1 = 500, Need2 = 1000, Need3 = 2300, Range = 0 });
            if (!MagicExists(Spell.DoubleSlash))
                MagicInfoList.Add(new MagicInfo
                { Name = "DoubleSlash", Spell = Spell.DoubleSlash, Icon = 59, Level1 = 15, Level2 = 17, Level3 = 19, Need1 = 700, Need2 = 1500, Need3 = 2200, BaseCost = 2, LevelCost = 1 });
            if (!MagicExists(Spell.Haste))
                MagicInfoList.Add(new MagicInfo
                { Name = "Haste", Spell = Spell.Haste, Icon = 60, Level1 = 20, Level2 = 22, Level3 = 25, Need1 = 2000, Need2 = 3000, Need3 = 6000, BaseCost = 3, LevelCost = 2, Range = 0 });
            if (!MagicExists(Spell.FlashDash))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FlashDash",
                    Spell = Spell.FlashDash,
                    Icon = 61,
                    Level1 = 25,
                    Level2 = 27,
                    Level3 = 30,
                    Need1 = 4000,
                    Need2 = 7000,
                    Need3 = 9000,
                    BaseCost = 12,
                    LevelCost = 2,
                    DelayBase = 200,
                    Range = 0
                });
            if (!MagicExists(Spell.LightBody))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "LightBody",
                    Spell = Spell.LightBody,
                    Icon = 68,
                    Level1 = 27,
                    Level2 = 29,
                    Level3 = 32,
                    Need1 = 5000,
                    Need2 = 7000,
                    Need3 = 10000,
                    BaseCost = 11,
                    LevelCost = 2,
                    Range = 0
                });
            if (!MagicExists(Spell.HeavenlySword))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "HeavenlySword",
                    Spell = Spell.HeavenlySword,
                    Icon = 62,
                    Level1 = 30,
                    Level2 = 32,
                    Level3 = 35,
                    Need1 = 4000,
                    Need2 = 8000,
                    Need3 = 10000,
                    BaseCost = 13,
                    LevelCost = 2,
                    MPowerBase = 8,
                    Range = 0
                });
            if (!MagicExists(Spell.FireBurst))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FireBurst",
                    Spell = Spell.FireBurst,
                    Icon = 63,
                    Level1 = 33,
                    Level2 = 35,
                    Level3 = 38,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 8000,
                    BaseCost = 10,
                    LevelCost = 1,
                    Range = 0
                });
            if (!MagicExists(Spell.Trap))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Trap",
                    Spell = Spell.Trap,
                    Icon = 64,
                    Level1 = 33,
                    Level2 = 35,
                    Level3 = 38,
                    Need1 = 2000,
                    Need2 = 4000,
                    Need3 = 6000,
                    BaseCost = 14,
                    LevelCost = 2,
                    DelayBase = 60000,
                    DelayReduction = 15000,
                    Range = 9
                });
            if (!MagicExists(Spell.PoisonSword))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "PoisonSword",
                    Spell = Spell.PoisonSword,
                    Icon = 69,
                    Level1 = 34,
                    Level2 = 36,
                    Level3 = 39,
                    Need1 = 5000,
                    Need2 = 8000,
                    Need3 = 11000,
                    BaseCost = 14,
                    LevelCost = 3,
                    Range = 0
                });
            if (!MagicExists(Spell.MoonLight))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MoonLight",
                    Spell = Spell.MoonLight,
                    Icon = 65,
                    Level1 = 36,
                    Level2 = 39,
                    Level3 = 42,
                    Need1 = 3000,
                    Need2 = 5000,
                    Need3 = 8000,
                    BaseCost = 36,
                    LevelCost = 3,
                    Range = 0
                });
            if (!MagicExists(Spell.MPEater))
                MagicInfoList.Add(new MagicInfo { Name = "MPEater", Spell = Spell.MPEater, Icon = 66, Level1 = 38, Level2 = 41, Level3 = 44, Need1 = 5000, Need2 = 8000, Need3 = 11000, Range = 0 });
            if (!MagicExists(Spell.SwiftFeet))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SwiftFeet",
                    Spell = Spell.SwiftFeet,
                    Icon = 67,
                    Level1 = 40,
                    Level2 = 43,
                    Level3 = 46,
                    Need1 = 4000,
                    Need2 = 6000,
                    Need3 = 9000,
                    BaseCost = 17,
                    LevelCost = 5,
                    DelayBase = 210000,
                    DelayReduction = 40000,
                    Range = 0
                });
            if (!MagicExists(Spell.DarkBody))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "DarkBody",
                    Spell = Spell.DarkBody,
                    Icon = 70,
                    Level1 = 46,
                    Level2 = 49,
                    Level3 = 52,
                    Need1 = 6000,
                    Need2 = 10000,
                    Need3 = 14000,
                    BaseCost = 40,
                    LevelCost = 7,
                    Range = 0
                });
            if (!MagicExists(Spell.Hemorrhage))
                MagicInfoList.Add(new MagicInfo
                { Name = "Hemorrhage", Spell = Spell.Hemorrhage, Icon = 75, Level1 = 47, Level2 = 51, Level3 = 55, Need1 = 9000, Need2 = 15000, Need3 = 21000, Range = 0 });
            if (!MagicExists(Spell.CrescentSlash))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "CresentSlash",
                    Spell = Spell.CrescentSlash,
                    Icon = 71,
                    Level1 = 50,
                    Level2 = 53,
                    Level3 = 56,
                    Need1 = 12000,
                    Need2 = 16000,
                    Need3 = 24000,
                    BaseCost = 19,
                    LevelCost = 5,
                    Range = 0
                });
            if (!MagicExists(Spell.MoonMist))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MoonMist",
                    Spell = Spell.MoonMist,
                    Icon = 83,
                    Level1 = 48,
                    Level2 = 51,
                    Level3 = 56,
                    Need1 = 10,
                    Need2 = 20,
                    Need3 = 30,
                    BaseCost = 30,
                    LevelCost = 5,
                    DelayBase = 20000,
                    DelayReduction = 2000
                });
            if (!MagicExists(Spell.CatTongue))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "CatTongue",
                    Spell = Spell.CatTongue,
                    Icon = 79,
                    Level1 = 48,
                    Level2 = 51,
                    Level3 = 56,
                    Need1 = 10,
                    Need2 = 20,
                    Need3 = 30,
                    BaseCost = 30,
                    LevelCost = 5,
                    DelayBase = 20000,
                    DelayReduction = 2000
                });

            //Archer
            if (!MagicExists(Spell.Focus))
                MagicInfoList.Add(new MagicInfo { Name = "Focus", Spell = Spell.Focus, Icon = 88, Level1 = 7, Level2 = 13, Level3 = 17, Need1 = 270, Need2 = 600, Need3 = 1300, Range = 0 });
            if (!MagicExists(Spell.StraightShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "StraightShot",
                    Spell = Spell.StraightShot,
                    Icon = 89,
                    Level1 = 9,
                    Level2 = 12,
                    Level3 = 16,
                    Need1 = 350,
                    Need2 = 750,
                    Need3 = 1400,
                    BaseCost = 3,
                    LevelCost = 2,
                    MPowerBase = 8,
                    PowerBase = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.DoubleShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "DoubleShot",
                    Spell = Spell.DoubleShot,
                    Icon = 90,
                    Level1 = 14,
                    Level2 = 18,
                    Level3 = 21,
                    Need1 = 700,
                    Need2 = 1500,
                    Need3 = 2100,
                    BaseCost = 3,
                    LevelCost = 2,
                    MPowerBase = 6,
                    PowerBase = 2,
                    Range = 9
                });
            if (!MagicExists(Spell.ExplosiveTrap))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ExplosiveTrap",
                    Spell = Spell.ExplosiveTrap,
                    Icon = 91,
                    Level1 = 22,
                    Level2 = 25,
                    Level3 = 30,
                    Need1 = 2000,
                    Need2 = 3500,
                    Need3 = 5000,
                    BaseCost = 10,
                    LevelCost = 3,
                    MPowerBase = 15,
                    PowerBase = 15,
                    Range = 0
                });
            if (!MagicExists(Spell.DelayedExplosion))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "DelayedExplosion",
                    Spell = Spell.DelayedExplosion,
                    Icon = 92,
                    Level1 = 31,
                    Level2 = 34,
                    Level3 = 39,
                    Need1 = 3000,
                    Need2 = 7000,
                    Need3 = 10000,
                    BaseCost = 8,
                    LevelCost = 2,
                    MPowerBase = 30,
                    PowerBase = 15,
                    Range = 9
                });
            if (!MagicExists(Spell.Meditation))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Meditation",
                    Spell = Spell.Meditation,
                    Icon = 93,
                    Level1 = 19,
                    Level2 = 24,
                    Level3 = 29,
                    Need1 = 1800,
                    Need2 = 2600,
                    Need3 = 5600,
                    BaseCost = 8,
                    LevelCost = 2,
                    Range = 0
                });
            if (!MagicExists(Spell.ElementalShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ElementalShot",
                    Spell = Spell.ElementalShot,
                    Icon = 94,
                    Level1 = 20,
                    Level2 = 25,
                    Level3 = 31,
                    Need1 = 1800,
                    Need2 = 2700,
                    Need3 = 6000,
                    BaseCost = 8,
                    LevelCost = 2,
                    MPowerBase = 6,
                    PowerBase = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.Concentration))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "Concentration",
                    Spell = Spell.Concentration,
                    Icon = 96,
                    Level1 = 23,
                    Level2 = 27,
                    Level3 = 32,
                    Need1 = 2100,
                    Need2 = 3800,
                    Need3 = 6500,
                    BaseCost = 8,
                    LevelCost = 2,
                    Range = 0
                });
            if (!MagicExists(Spell.ElementalBarrier))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "ElementalBarrier",
                    Spell = Spell.ElementalBarrier,
                    Icon = 98,
                    Level1 = 33,
                    Level2 = 38,
                    Level3 = 44,
                    Need1 = 3000,
                    Need2 = 7000,
                    Need3 = 10000,
                    BaseCost = 10,
                    LevelCost = 2,
                    MPowerBase = 15,
                    PowerBase = 5,
                    Range = 0
                });
            if (!MagicExists(Spell.BackStep))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "BackStep",
                    Spell = Spell.BackStep,
                    Icon = 95,
                    Level1 = 30,
                    Level2 = 34,
                    Level3 = 38,
                    Need1 = 2400,
                    Need2 = 3000,
                    Need3 = 6000,
                    BaseCost = 12,
                    LevelCost = 2,
                    DelayBase = 2500,
                    Range = 0
                });
            if (!MagicExists(Spell.BindingShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "BindingShot",
                    Spell = Spell.BindingShot,
                    Icon = 97,
                    Level1 = 35,
                    Level2 = 39,
                    Level3 = 42,
                    Need1 = 400,
                    Need2 = 7000,
                    Need3 = 9500,
                    BaseCost = 7,
                    LevelCost = 3,
                    Range = 9
                });
            if (!MagicExists(Spell.Stonetrap))
                MagicInfoList.Add(new MagicInfo
                { Name = "Stonetrap", Spell = Spell.Stonetrap, Icon = 97, Level1 = 40, Level2 = 43, Level3 = 46, Need1 = 4900, Need2 = 9800, Need3 = 141, BaseCost = 7, LevelCost = 3, Range = 9 });
            if (!MagicExists(Spell.SummonVampire))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SummonVampire",
                    Spell = Spell.SummonVampire,
                    Icon = 99,
                    Level1 = 28,
                    Level2 = 33,
                    Level3 = 41,
                    Need1 = 2000,
                    Need2 = 2700,
                    Need3 = 7500,
                    BaseCost = 10,
                    LevelCost = 5,
                    Range = 9
                });
            if (!MagicExists(Spell.VampireShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "VampireShot",
                    Spell = Spell.VampireShot,
                    Icon = 100,
                    Level1 = 26,
                    Level2 = 32,
                    Level3 = 36,
                    Need1 = 3000,
                    Need2 = 6000,
                    Need3 = 12000,
                    BaseCost = 12,
                    LevelCost = 3,
                    MPowerBase = 10,
                    PowerBase = 7,
                    Range = 9
                });
            if (!MagicExists(Spell.SummonToad))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SummonToad",
                    Spell = Spell.SummonToad,
                    Icon = 101,
                    Level1 = 37,
                    Level2 = 43,
                    Level3 = 47,
                    Need1 = 5800,
                    Need2 = 10000,
                    Need3 = 13000,
                    BaseCost = 10,
                    LevelCost = 5,
                    Range = 9
                });
            if (!MagicExists(Spell.PoisonShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "PoisonShot",
                    Spell = Spell.PoisonShot,
                    Icon = 102,
                    Level1 = 40,
                    Level2 = 45,
                    Level3 = 49,
                    Need1 = 6000,
                    Need2 = 14000,
                    Need3 = 16000,
                    BaseCost = 10,
                    LevelCost = 4,
                    MPowerBase = 10,
                    PowerBase = 10,
                    Range = 9
                });
            if (!MagicExists(Spell.CrippleShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "CrippleShot",
                    Spell = Spell.CrippleShot,
                    Icon = 103,
                    Level1 = 43,
                    Level2 = 47,
                    Level3 = 50,
                    Need1 = 12000,
                    Need2 = 15000,
                    Need3 = 18000,
                    BaseCost = 15,
                    LevelCost = 3,
                    MPowerBase = 10,
                    MPowerBonus = 20,
                    PowerBase = 10,
                    Range = 9
                });
            if (!MagicExists(Spell.SummonSnakes))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "SummonSnakes",
                    Spell = Spell.SummonSnakes,
                    Icon = 104,
                    Level1 = 46,
                    Level2 = 51,
                    Level3 = 54,
                    Need1 = 14000,
                    Need2 = 17000,
                    Need3 = 20000,
                    BaseCost = 10,
                    LevelCost = 5,
                    Range = 9
                });
            if (!MagicExists(Spell.NapalmShot))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "NapalmShot",
                    Spell = Spell.NapalmShot,
                    Icon = 105,
                    Level1 = 48,
                    Level2 = 52,
                    Level3 = 55,
                    Need1 = 15000,
                    Need2 = 18000,
                    Need3 = 21000,
                    BaseCost = 40,
                    LevelCost = 10,
                    MPowerBase = 25,
                    MPowerBonus = 25,
                    PowerBase = 25,
                    Range = 9
                });
            if (!MagicExists(Spell.OneWithNature))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "OneWithNature",
                    Spell = Spell.OneWithNature,
                    Icon = 106,
                    Level1 = 50,
                    Level2 = 53,
                    Level3 = 56,
                    Need1 = 17000,
                    Need2 = 19000,
                    Need3 = 24000,
                    BaseCost = 80,
                    LevelCost = 15,
                    MPowerBase = 75,
                    MPowerBonus = 35,
                    PowerBase = 30,
                    PowerBonus = 20,
                    Range = 9
                });
            if (!MagicExists(Spell.MentalState))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MentalState",
                    Spell = Spell.MentalState,
                    Icon = 81,
                    Level1 = 11,
                    Level2 = 15,
                    Level3 = 22,
                    Need1 = 500,
                    Need2 = 900,
                    Need3 = 1800,
                    BaseCost = 1,
                    LevelCost = 1,
                    Range = 0
                });

            //Custom
            if (!MagicExists(Spell.Portal))
                MagicInfoList.Add(new MagicInfo
                { Name = "Portal", Spell = Spell.Portal, Icon = 1, Level1 = 7, Level2 = 11, Level3 = 14, Need1 = 150, Need2 = 350, Need3 = 700, BaseCost = 3, LevelCost = 2, Range = 9 });
            if (!MagicExists(Spell.BattleCry))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "BattleCry",
                    Spell = Spell.BattleCry,
                    Icon = 42,
                    Level1 = 48,
                    Level2 = 51,
                    Level3 = 55,
                    Need1 = 8000,
                    Need2 = 11000,
                    Need3 = 15000,
                    BaseCost = 22,
                    LevelCost = 10,
                    Range = 0
                });
            if (!MagicExists(Spell.FireBounce))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "FireBounce",
                    Spell = Spell.FireBounce,
                    Icon = 4,
                    Level1 = 15,
                    Level2 = 18,
                    Level3 = 21,
                    Need1 = 2000,
                    Need2 = 2700,
                    Need3 = 3500,
                    BaseCost = 5,
                    LevelCost = 1,
                    MPowerBase = 6,
                    PowerBase = 10,
                    Range = 9
                });
            if (!MagicExists(Spell.MeteorShower))
                MagicInfoList.Add(new MagicInfo
                {
                    Name = "MeteorShower",
                    Spell = Spell.MeteorShower,
                    Icon = 4,
                    Level1 = 15,
                    Level2 = 18,
                    Level3 = 21,
                    Need1 = 2000,
                    Need2 = 2700,
                    Need3 = 3500,
                    BaseCost = 5,
                    LevelCost = 1,
                    MPowerBase = 6,
                    PowerBase = 10,
                    Range = 9
                });
        }

        private string CanStartEnvir()
        {
            if (StartPoints.Count == 0) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMapAndStartPoint);

            if (Settings.EnforceDBChecks)
            {
                if (GetMonsterInfo(Settings.SkeletonName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.SkeletonName;
                if (GetMonsterInfo(Settings.ShinsuName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.ShinsuName;
                if (GetMonsterInfo(Settings.BugBatName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BugBatName;
                if (GetMonsterInfo(Settings.Zuma1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma1;
                if (GetMonsterInfo(Settings.Zuma2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma2;
                if (GetMonsterInfo(Settings.Zuma3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma3;
                if (GetMonsterInfo(Settings.Zuma4, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma4;
                if (GetMonsterInfo(Settings.Zuma5, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma5;
                if (GetMonsterInfo(Settings.Zuma6, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma6;
                if (GetMonsterInfo(Settings.Zuma7, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Zuma7;
                if (GetMonsterInfo(Settings.Turtle1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Turtle1;
                if (GetMonsterInfo(Settings.Turtle2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Turtle2;
                if (GetMonsterInfo(Settings.Turtle3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Turtle3;
                if (GetMonsterInfo(Settings.Turtle4, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Turtle4;
                if (GetMonsterInfo(Settings.Turtle5, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.Turtle5;
                if (GetMonsterInfo(Settings.BoneMonster1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BoneMonster1;
                if (GetMonsterInfo(Settings.BoneMonster2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BoneMonster2;
                if (GetMonsterInfo(Settings.BoneMonster3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BoneMonster3;
                if (GetMonsterInfo(Settings.BoneMonster4, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BoneMonster4;
                if (GetMonsterInfo(Settings.BehemothMonster1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BehemothMonster1;
                if (GetMonsterInfo(Settings.BehemothMonster2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BehemothMonster2;
                if (GetMonsterInfo(Settings.BehemothMonster3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BehemothMonster3;
                if (GetMonsterInfo(Settings.HellKnight1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellKnight1;
                if (GetMonsterInfo(Settings.HellKnight2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellKnight2;
                if (GetMonsterInfo(Settings.HellKnight3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellKnight3;
                if (GetMonsterInfo(Settings.HellKnight4, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellKnight4;
                if (GetMonsterInfo(Settings.HellBomb1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellBomb1;
                if (GetMonsterInfo(Settings.HellBomb2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellBomb2;
                if (GetMonsterInfo(Settings.HellBomb3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HellBomb3;
                if (GetMonsterInfo(Settings.WhiteSnake, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.WhiteSnake;
                if (GetMonsterInfo(Settings.AngelName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.AngelName;
                if (GetMonsterInfo(Settings.BombSpiderName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.BombSpiderName;
                if (GetMonsterInfo(Settings.CloneName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.CloneName;
                if (GetMonsterInfo(Settings.AssassinCloneName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.AssassinCloneName;
                if (GetMonsterInfo(Settings.VampireName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.VampireName;
                if (GetMonsterInfo(Settings.ToadName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.ToadName;
                if (GetMonsterInfo(Settings.SnakeTotemName, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.SnakeTotemName;
                if (GetMonsterInfo(Settings.FishingMonster, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.FishingMonster;
                if (GetMonsterInfo(Settings.GeneralMeowMeowMob1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.GeneralMeowMeowMob1;
                if (GetMonsterInfo(Settings.GeneralMeowMeowMob2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.GeneralMeowMeowMob2;
                if (GetMonsterInfo(Settings.GeneralMeowMeowMob3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.GeneralMeowMeowMob3;
                if (GetMonsterInfo(Settings.GeneralMeowMeowMob4, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.GeneralMeowMeowMob4;
                if (GetMonsterInfo(Settings.KingHydraxMob, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.KingHydraxMob;
                if (GetMonsterInfo(Settings.HornedCommanderMob, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HornedCommanderMob;
                if (GetMonsterInfo(Settings.HornedCommanderBombMob, true) == null)
                    return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.HornedCommanderBombMob;
                if (GetMonsterInfo(Settings.SnowWolfKingMob, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.SnowWolfKingMob;
                if (GetMonsterInfo(Settings.ScrollMob1, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.ScrollMob1;
                if (GetMonsterInfo(Settings.ScrollMob2, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.ScrollMob2;
                if (GetMonsterInfo(Settings.ScrollMob3, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.ScrollMob3;
                if (GetMonsterInfo(Settings.ScrollMob4, true) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutMob) + Settings.ScrollMob4;

                if (GetItemInfo(Settings.RefineOreName) == null) return GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotStartServerWithoutItem) + Settings.RefineOreName;
            }

            WorldMapIcon wmi = ValidateWorldMap();
            if (wmi != null)
                return GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.InvalidWorldmapIndex), wmi.MapIndex, wmi.Title);


            //add intelligent creature checks?

            return "true";
        }

        private void WorkLoop()
        {
            if (this == Main) DesignStore.GameThreadId = Environment.CurrentManagedThreadId; // design writes from here are posted (roadmap P7)
            if (this == Main) GameThreadNativeId = NativeThread.CurrentId();
            try
            {
                Time = Stopwatch.ElapsedMilliseconds;

                var conTime = Time;
                var saveTime = Time + Settings.SaveDelay * Settings.Minute;
                var userTime = Time + Settings.Minute * 5;
                var lineMessageTime = Time + Settings.Minute * Settings.LineMessageTimer;
                var processTime = Time + 1000;
                var startTime = Time;

                var processCount = 0;
                var processRealCount = 0;

                LinkedListNode<MapObject> current = null;

                // Captured once per world run: objects are scheduled from their first Spawned() (inside StartEnvir).
                _dueMultithreaded = Settings.Multithreaded;
                _mainScheduler = new ObjectScheduler();
                _dueScheduler = Settings.DueScheduler;
                WorldGeneration++;
                _mapHeap.Clear();
                _mapWakes.Clear();
                _mapsKnown = 0;

                if (Settings.Multithreaded)
                {
                    for (var j = 0; j < MobThreads.Length; j++)
                    {
                        MobThreads[j] = new MobThread();
                        MobThreads[j].Id = j;
                    }
                }

                StartEnvir();
                var canstartserver = CanStartEnvir();
                if (canstartserver != "true")
                {
                    MessageQueue.Enqueue(canstartserver);
                    StopEnvir();
                    _thread = null;
                    Stop();
                    global::Server.WebAdmin.WebAdminHooks.RaiseWorldStopped();
                    return;
                }

                if (Settings.Multithreaded)
                {
                    for (var j = 0; j < MobThreads.Length; j++)
                    {
                        var Info = MobThreads[j];
                        if (j <= 0) continue;
                        MobThreading[j] = new Thread(() => ThreadLoop(Info)) { IsBackground = true };
                        MobThreading[j].Start();
                    }
                }

                StartNetwork();
                if (Settings.StartHTTPService)
                {
                    http = new HttpServer();
                    http.Start();
                }

                OpenAdminQueue();
                global::Server.WebAdmin.WebAdminHooks.RaiseWorldStarted();

                try
                {
                    while (Running)
                    {
                        LoopProfiler.BeginIteration();
                        Time = Stopwatch.ElapsedMilliseconds;
                        if (!_timerResolutionSet && Settings.IdlePark) { TimerResolution.Begin(); _timerResolutionSet = true; }

                        if (Time >= processTime)
                        {
                            LastCount = processCount;
                            LastRealCount = processRealCount;
                            processCount = 0;
                            processRealCount = 0;
                            processTime = Time + 1000;
                            if (_dueScheduler)
                            {
                                // "Cycle delay" becomes the worst lateness of a processed object in the last second. Mob
                                // threads are parked at the barrier here, so their schedulers can be read.
                                LastRunTime = _mainScheduler.TakeMaxLateMs();
                                for (var j = 0; j < MobThreads.Length; j++)
                                    if (MobThreads[j] != null) MobThreads[j].LastRunTime = MobThreads[j].Scheduler.TakeMaxLateMs();
                            }
                            var awake = PublishMobLoad(out var mobLoad);
                            LoopProfiler.Publish(mobLoad, awake);
                            if (Settings.SectorGridVerify)
                            {
                                try { VerifySectorGrids(); }
                                catch (Exception ex) { WorldErrors.Record("SectorGridVerify", ex); }
                            }
                        }

                        var connPass = conTime != Time;
                        if (connPass)
                        {
                            conTime = Time;

                            AdjustLights();

                            lock (Connections)
                            {
                                for (var i = Connections.Count - 1; i >= 0; i--)
                                {
                                    if (i >= Connections.Count) continue; // an isolated disconnect below may shrink the list
                                    var connection = Connections[i];
                                    // Isolation (roadmap P5): one client's packet or handler exception disconnects that client only.
                                    try
                                    {
                                        connection.Process();
                                    }
                                    catch (Exception ex)
                                    {
                                        WorldErrors.Record(WorldErrors.Describe(connection), ex);
                                        IsolateConnection(connection);
                                    }
                                }
                                MirConnection.EndEncodePass(); // roadmap P9: encoded bytes live for one pass only
                            }

                            lock (StatusConnections)
                            {
                                for (var i = StatusConnections.Count - 1; i >= 0; i--)
                                {
                                    StatusConnections[i].Process();
                                }
                            }
                        }


                        LoopProfiler.Mark(LoopProfiler.Phase.Connections);

                        if (!_dueScheduler)
                        {
                            if (current == null)
                                current = Objects.First;

                            if (current == Objects.First)
                            {
                                LastRunTime = Time - startTime;
                                startTime = Time;
                            }
                        }

                        if (Settings.Multithreaded)
                        {
                            // Published before any sub-thread is woken (PulseAll's lock orders it); cleared by EndMobPass.
                            _inMobPass = true;
                            var armed = false;
                            for (var j = 1; j < MobThreads.Length; j++)
                            {
                                var Info = MobThreads[j];

                                if (Info.Dead || Info.Exited) continue; // ended on an unexpected exception (roadmap P5), or gone
                                if (!Running) break; // a stop is under way: start no new pass
                                if (!Info.Stop) continue;
                                // Due scheduler: a parked thread with nothing due stays parked (its scheduler is safe to read here).
                                if (_dueScheduler && !Info.Scheduler.HasWork(Time)) continue;
                                Info.EndTime = Stopwatch.ElapsedMilliseconds + 10; // review M2: was Time + 10 (tick start)
                                Info.Stop = false;
                                armed = true;
                            }
                            if (armed || !_dueScheduler)
                            {
                                lock (_locker)
                                {
                                    Monitor.PulseAll(_locker); //changing a blocking condition. (this makes the threads wake up!)
                                }
                            }
                            //run the first loop in the main thread so the main thread automaticaly 'halts' until the other threads are finished
                            ThreadLoop(MobThreads[0]);
                            EndMobPass();
                        }
                        LoopProfiler.Mark(LoopProfiler.Phase.MobBarrier);

                        if (_dueScheduler)
                        {
                            // A mob thread that ended on an unexpected exception no longer runs its scheduler: adopt its objects.
                            if (_dueMultithreaded)
                                for (var j = 1; j < MobThreads.Length; j++)
                                    if (MobThreads[j] != null && MobThreads[j].Dead) MobThreads[j].Scheduler.MoveAllTo(_mainScheduler);

                            // Per-object errors are isolated inside RunDue; this catches the rest (heap, ownership) so one bad
                            // pass costs a tick instead of stopping the world.
                            try
                            {
                                processCount += RunDue(_mainScheduler, Stopwatch.ElapsedMilliseconds + 20, null);
                            }
                            catch (Exception ex)
                            {
                                WorldErrors.Record("RunDue (game thread)", ex);
                            }
                        }

                        var TheEnd = _dueScheduler;
                        var Start = Stopwatch.ElapsedMilliseconds;
                        while (!TheEnd && Stopwatch.ElapsedMilliseconds - Start < 20)
                        {
                            if (current == null)
                            {
                                TheEnd = true;
                                break;
                            }

                            var next = current.Next;
                            if (!Settings.Multithreaded || current.Value.Race != ObjectType.Monster || current.Value.Master != null)
                            {
                                if (Time > current.Value.OperateTime)
                                {
                                    // Isolation (roadmap P5): a throwing object is quarantined, the world keeps running.
                                    var ob = current.Value;
                                    try
                                    {
                                        ob.Process();
                                        ob.SetOperateTime();
                                    }
                                    catch (Exception ex)
                                    {
                                        WorldErrors.Record(WorldErrors.Describe(ob), ex);
                                        IsolateObject(ob);
                                    }
                                }
                                processCount++;
                            }
                            current = next;
                        }
                        LoopProfiler.Mark(LoopProfiler.Phase.Objects);

                        if (_dueScheduler)
                        {
                            RunMaps();
                        }
                        else
                        {
                            for (var i = 0; i < MapList.Count; i++)
                            {
                                // Nothing timed is due and no map action was queued since the last call: skip it.
                                if (!MapList[i].ProcessDue(Time)) continue;

                                try
                                {
                                    MapList[i].Process();
                                }
                                catch (Exception ex)
                                {
                                    WorldErrors.Record("Map.Process " + (MapList[i].Info?.FileName ?? "?"), ex);
                                }
                            }
                        }

                        LoopProfiler.Mark(LoopProfiler.Phase.Maps);

                        try
                        {
                            DragonSystem?.Process();
                        }
                        catch (Exception ex)
                        {
                            WorldErrors.Record("DragonSystem.Process", ex);
                        }

                        try
                        {
                            Process();
                        }
                        catch (Exception ex)
                        {
                            WorldErrors.Record("Envir.Process", ex);
                        }

                        LoopProfiler.Mark(LoopProfiler.Phase.EnvirProcess);

                        DrainAdminActions();

                        // LiveDb: refresh spawned monsters after a live MonsterInfo change, a few hundred objects per tick (mob threads
                        // are parked here). Idle cost: one volatile read.
                        if (global::Server.WebAdmin.LiveDb.LiveDbSweeper.HasWork)
                        {
                            try
                            {
                                global::Server.WebAdmin.LiveDb.LiveDbSweeper.Tick(Settings.LiveDbSweepPerTick);
                            }
                            catch (Exception ex)
                            {
                                WorldErrors.Record("LiveDb sweeper", ex);
                                global::Server.WebAdmin.LiveDb.LiveDbSweeper.Drop();
                            }
                        }

                        LoopProfiler.Mark(LoopProfiler.Phase.AdminDrain);

                        if (Time >= saveTime || _saveNowRequested)
                        {
                            _saveNowRequested = false;
                            saveTime = Time + Settings.SaveDelay * Settings.Minute;
                            // Each save is isolated (roadmap P5): since P2 a failed save leaves the previous file intact.
                            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            for (var i = 0; i < Players.Count; i++) Players[i].FoldPlayTime(); // v119: a crash loses little play time
                            var accountSaveSkipped = Saving; // the previous write is still running: BeginSaveAccounts returns at once
                            BeginSaveAccounts();
                            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            // Main's save only posts the respawn counter (runtime_state): Envir.Edit is the only writer of design data.
                            try { SaveDB(); }
                            catch (Exception ex) { WorldErrors.Record("SaveDB", ex); }
                            var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                            try { SaveGuilds(); } catch (Exception ex) { WorldErrors.Record("SaveGuilds", ex); }
                            var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                            try { SaveGoods(); } catch (Exception ex) { WorldErrors.Record("SaveGoods", ex); }
                            var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                            try { SaveConquests(); } catch (Exception ex) { WorldErrors.Record("SaveConquests", ex); }
                            var t5 = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (Settings.LogLoopProfile) // once per save; P9-10
                                MessageQueue.Instance.EnqueueDebugging(
                                    $"[Diag] Save accounts={SaveMs(t0, t1):0.00}ms db={SaveMs(t1, t2):0.00}ms guilds={SaveMs(t2, t3):0.00}ms " +
                                    $"goods={SaveMs(t3, t4):0.00}ms conquests={SaveMs(t4, t5):0.00}ms total={SaveMs(t0, t5):0.00}ms " +
                                    $"bytes={LastAccountSaveBytes} accounts={AccountList.Count}{(accountSaveSkipped ? " skipped" : "")}" +
                                    (accountSaveSkipped ? "" : AccountCacheDiag())); // P9-12 fields, appended
                            BackupRetention.RunInBackground(); // no-op unless [Backup] RetentionEnabled
                            try { PruneIpTables(); } catch (Exception ex) { WorldErrors.Record("PruneIpTables", ex); }
                        }
                        LoopProfiler.Mark(LoopProfiler.Phase.Saves);

                        if (Time >= userTime)
                        {
                            userTime = Time + Settings.Minute * 5;
                            Broadcast(new S.Chat
                            {
                                Message = GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.OnlinePlayers), Players.Count),
                                Type = ChatType.Hint
                            });
                        }

                        if (LineMessages.Count > 0 && Time >= lineMessageTime)
                        {
                            lineMessageTime = Time + Settings.Minute * Settings.LineMessageTimer;
                            Broadcast(new S.Chat
                            {
                                Message = LineMessages[Random.Next(LineMessages.Count)],
                                Type = ChatType.LineMessage
                            });
                        }

                        LoopProfiler.Mark(LoopProfiler.Phase.Other);

                        // P9-06: in-game sends go out here, after monsters, objects and maps ran, so this iteration's world
                        // packets leave this iteration. One pass over all connections keeps encode-once to one encode.
                        if (connPass && Settings.FlushAtEnd)
                            FlushEndOfTick();
                        LoopProfiler.Mark(LoopProfiler.Phase.Send);

                        LoopProfiler.EndIteration();

                        // Idle park (roadmap P6; overrides server.md 3.2 "no waits on the game thread", approved 2026-10-09):
                        // when this iteration did almost nothing, wait at most 1 ms instead of spinning a core. Network
                        // receive, accept and admin actions wake the loop early (WakeLoop), so latency stays <= ~1 ms.
                        if (Settings.IdlePark && Stopwatch.ElapsedMilliseconds - Time < 1)
                            _loopSignal.WaitOne(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (Connections)
                    {
                        for (var i = Connections.Count - 1; i >= 0; i--)
                            Connections[i].SendDisconnect(3);
                    }

                    // Get stack trace for the exception with source file information
                    var st = new StackTrace(ex, true);
                    // Get the top stack frame
                    var frame = st.GetFrame(0);
                    // Get the line number from the stack frame
                    var line = frame.GetFileLineNumber();

                    MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.InnerWorkloopErrorLine), line, ex));
                }

                // No monster AI may run during the teardown below (logout saves, StopEnvir, SaveAccounts): wait until every
                // mob thread is parked or gone, then run what the last pass deferred.
                if (Settings.Multithreaded)
                {
                    WaitForSubThreads(true);
                    EndMobPass();
                }

                CloseAdminQueue();
                TimerResolution.End();
                _timerResolutionSet = false;

                StopNetwork();
                StopEnvir();
                SaveAccounts();
                SaveGuilds(true);
                SaveConquests(true);
            }
            catch (Exception ex)
            {
                // Get stack trace for the exception with source file information
                var st = new StackTrace(ex, true);
                // Get the top stack frame
                var frame = st.GetFrame(0);
                // Get the line number from the stack frame
                var line = frame.GetFileLineNumber();

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.OuterWorkloopErrorLine), line, ex));
            }

            CloseAdminQueue();
            TimerResolution.End(); // also after a crashed loop (review)
            _timerResolutionSet = false;
            global::Server.WebAdmin.WebAdminHooks.RaiseWorldStopped();

            _thread = null;
        }

        private ObjectScheduler _mainScheduler;
        private bool _dueScheduler, _dueMultithreaded;
        /// <summary>Incremented at every world start; objects spawned in an earlier run are never scheduled (they may still be referenced).</summary>
        internal int WorldGeneration;

        // Map scheduler (roadmap P9, with DueScheduler): game thread only, except _mapWakes. A map runs when its NextProcessTime is
        // due or when MapActionList.Add wakes it, instead of every map being checked on every iteration.
        private readonly PriorityQueue<Map, long> _mapHeap = new PriorityQueue<Map, long>();
        private readonly ConcurrentQueue<Map> _mapWakes = new ConcurrentQueue<Map>();
        private int _mapsKnown;

        /// <summary>Any thread: a map action was queued on <paramref name="map"/>; run it on the next pass.</summary>
        public void WakeMap(Map map)
        {
            if (!_dueScheduler) return;
            if (Interlocked.Exchange(ref map.WakePending, 1) == 0) _mapWakes.Enqueue(map);
        }

        private void ScheduleMap(Map map, long due)
        {
            map.ScheduledDue = due;
            map.ScheduledGeneration = WorldGeneration;
            _mapHeap.Enqueue(map, due);
        }

        private void RunMaps()
        {
            // MapList is filled by StartEnvir before the loop starts; pick up any map added since (each map once).
            for (; _mapsKnown < MapList.Count; _mapsKnown++)
                ScheduleMap(MapList[_mapsKnown], Time);

            while (_mapWakes.TryDequeue(out var woken))
            {
                Volatile.Write(ref woken.WakePending, 0);
                if (woken.ScheduledGeneration != WorldGeneration) continue; // not scheduled in this world run (an old map)
                if (woken.ScheduledDue > Time) ScheduleMap(woken, Time);
            }

            while (_mapHeap.TryPeek(out var map, out var due) && due <= Time)
            {
                _mapHeap.Dequeue();
                if (due != map.ScheduledDue || map.ScheduledGeneration != WorldGeneration) continue; // stale entry

                try
                {
                    map.Process();
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("Map.Process " + (map.Info?.FileName ?? "?"), ex);
                    // NextProcessTime is only written at the end of Process: retry next tick, as the old per-map loop did.
                    map.NextProcessTime = Time + 1;
                }

                ScheduleMap(map, Math.Max(map.NextProcessTime, Time + 1));
            }
        }

        /// <summary>
        /// Any thread: tells the owner of <paramref name="ob"/> to (re)schedule it at its current OperateTime. Called by the
        /// <see cref="MapObject.OperateTime"/> setter when a write makes the object due earlier, and by <see cref="MapObject.Spawned"/>.
        /// </summary>
        public void WakeObject(MapObject ob)
        {
            if (!_dueScheduler) return;
            OwnerOf(ob)?.Wake(ob);
        }

        /// <summary>Same split as the old walks: unowned monsters on their spawn thread's scheduler, everything else on the game thread's.</summary>
        private ObjectScheduler OwnerOf(MapObject ob)
        {
            if (_dueMultithreaded && ob.Race == ObjectType.Monster && ob.Master == null)
            {
                var thread = MobThreads[ob.SpawnThread];
                // A dead mob thread's monsters move to the game thread (see RunDue's caller), instead of freezing as with the old walk.
                if (thread != null && !thread.Dead) return thread.Scheduler;
            }
            return _mainScheduler;
        }

        /// <summary>
        /// Game thread, once a second, while every mob thread is parked at the barrier: snapshots and resets the mob threads' load
        /// counters and starts a new awake window. Returns the distinct monsters that ran AI in the window (all threads). One small
        /// array per second; nothing per tick.
        /// </summary>
        private int PublishMobLoad(out MobThreadLoad[] loads)
        {
            var awake = _mainAwake;
            _mainAwake = 0;
            _profileWindow++;
            loads = null;

            if (!Settings.Multithreaded || !_dueScheduler) return awake;

            loads = new MobThreadLoad[MobThreads.Length];
            var toMs = 1000.0 / Stopwatch.Frequency;
            for (var j = 0; j < MobThreads.Length; j++)
            {
                var t = MobThreads[j];
                if (t == null) continue;
                loads[j] = new MobThreadLoad
                {
                    Id = t.Id,
                    OnGameThread = t.Id == 0,
                    Dead = t.Dead,
                    BusyMs = Math.Round(t.WorkTicks * toMs, 2),
                    MaxPassMs = Math.Round(t.MaxPassTicks * toMs, 2),
                    Passes = t.Passes,
                    Overruns = t.Overruns,
                    Processed = t.Processed,
                    Awake = t.Awake,
                    Queued = t.Scheduler.Count,
                    LateMs = t.LastRunTime,
                };
                awake += t.Awake;
                t.WorkTicks = 0;
                t.MaxPassTicks = 0;
                t.Passes = 0;
                t.Overruns = 0;
                t.Processed = 0;
                t.Awake = 0;
            }

            return awake;
        }

        /// <summary>
        /// Owner thread: schedules woken objects, then processes every due object until <paramref name="deadline"/> (the same
        /// Process / SetOperateTime / isolation as the old walks). <paramref name="mob"/> is null on the game thread's scheduler.
        /// Returns the number of objects processed.
        /// </summary>
        private int RunDue(ObjectScheduler s, long deadline, MobThread mob)
        {
            while (s.TryDequeueWake(out var woken))
            {
                if (woken.Node == null || woken.SpawnedGeneration != WorldGeneration) continue;
                var owner = OwnerOf(woken);
                if (owner != s) { owner.Wake(woken); continue; }
                // Floor at Time: a woken object (OperateTime = 0) runs in this pass but doesn't count as late.
                s.Schedule(woken, Time);
            }

            var processed = 0;
            while (s.TryPopDue(Time, out var ob, out var due))
            {
                // Despawned, from an earlier world run, or a stale entry.
                if (ob.Node == null || ob.SpawnedGeneration != WorldGeneration || due != Volatile.Read(ref ob.ScheduledDue)) continue;

                var owner = OwnerOf(ob);
                if (owner != s) { owner.Wake(ob); continue; } // Master changed since it was scheduled

                if (Time > ob.OperateTime)
                {
                    var late = Time - due;
                    if (late > s.MaxLateMs) s.MaxLateMs = late;

                    if (ob.Race == ObjectType.Monster && ob.ProfileWindow != _profileWindow)
                    {
                        ob.ProfileWindow = _profileWindow;
                        if (mob != null) mob.Awake++;
                        else _mainAwake++;
                    }

                    // Isolation (roadmap P5): a throwing object is quarantined, the world keeps running.
                    try
                    {
                        ob.Process();
                        ob.SetOperateTime();
                    }
                    catch (Exception ex)
                    {
                        // Stop() interrupts mob threads: let that end the thread instead of quarantining an innocent monster.
                        if (ex is ThreadInterruptedException && mob != null && mob.Id != 0) throw;

                        if (mob == null)
                        {
                            WorldErrors.Record(WorldErrors.Describe(ob), ex);
                            IsolateObject(ob);
                        }
                        else
                        {
                            WorldErrors.Record($"mob thread {mob.Id}: {WorldErrors.Describe(ob)}", ex);
                            ob.OperateTime = Time + WorldErrors.QuarantineMs;
                        }
                    }
                    processed++;
                }

                // Never due again in this pass: "process on the next iteration" (OperateTime < Time) becomes the next ms.
                if (ob.Node != null) s.Schedule(ob, Time + 1);

                if (Stopwatch.ElapsedMilliseconds >= deadline) break;
                if (mob != null && !Running) break; // shutdown: end the pass now, WorkLoop is waiting to tear down
            }

            return processed;
        }

        /// <summary>Due-scheduler version of <see cref="ThreadLoop"/>: no list walks, and the game thread waits at the barrier instead of re-walking.</summary>
        private void ThreadLoopDue(MobThread Info)
        {
            if (Info.Id == 0)
            {
                // Game thread, inside the barrier: this thread's own monsters, then wait for the sub-threads. An error outside
                // the per-object catch must still reach the barrier wait, or the rest of the tick would overlap the sub-threads.
                try
                {
                    var start = Stopwatch.GetTimestamp();
                    var deadline = Stopwatch.ElapsedMilliseconds + 10;
                    var n = RunDue(Info.Scheduler, deadline, Info);
                    Info.NotePass(start, n, Stopwatch.ElapsedMilliseconds >= deadline);
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("mob thread 0 (game thread)", ex);
                }

                WaitForSubThreads(false);
                Info.Stop = true;
                return;
            }

            try
            {
                while (true)
                {
                    Volatile.Write(ref Info.Stop, true);
                    lock (_locker)
                    {
                        while (Info.Stop) Monitor.Wait(_locker);
                    }

                    if (!Running) continue; // shutdown: Stop() interrupts this thread
                    var start = Stopwatch.GetTimestamp();
                    var n = RunDue(Info.Scheduler, Info.EndTime, Info);
                    Info.NotePass(start, n, Stopwatch.ElapsedMilliseconds >= Info.EndTime);
                }
            }
            catch (Exception ex)
            {
                if (ex is ThreadInterruptedException)
                {
                    Info.Exited = true; // exiting: the shutdown barrier must not wait for this thread
                    return;
                }

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.ThreadLoopError), ex));
                var scheduled = Info.Scheduler.Count; // read before Dead: the game thread then empties this scheduler
                Info.Dead = true;
                Info.Stop = true;
                WorldErrors.Record($"mob thread {Info.Id} stopped; its {scheduled} scheduled monsters now run on the game thread", ex);
            }
        }

        private bool SubThreadsStopped()
        {
            for (var x = 1; x < MobThreads.Length; x++)
                if (!Volatile.Read(ref MobThreads[x].Stop) && !MobThreads[x].Dead && !MobThreads[x].Exited) return false;

            return true;
        }

        /// <summary>How long the game thread waits for mob threads once Running is false, before tearing down anyway.</summary>
        private const int ShutdownBarrierMs = 2000;

        /// <summary>
        /// Game thread: spins until every live sub-thread is parked (the mob barrier). Unbounded while the world runs, as before
        /// (sub-threads always park after their deadline); once <see cref="Running"/> is false, or with <paramref name="bounded"/>,
        /// it gives up after <see cref="ShutdownBarrierMs"/> so a stuck monster cannot block a stop. Returns false when it gave up.
        /// Sub-threads stop early on shutdown (RunDue checks Running), so the wait is normally a few ms at most.
        /// </summary>
        private bool WaitForSubThreads(bool bounded)
        {
            var spin = new SpinWait();
            var giveUpAt = bounded ? Stopwatch.ElapsedMilliseconds + ShutdownBarrierMs : long.MaxValue;
            while (!SubThreadsStopped())
            {
                if (giveUpAt == long.MaxValue && !Running) giveUpAt = Stopwatch.ElapsedMilliseconds + ShutdownBarrierMs;
                if (Stopwatch.ElapsedMilliseconds > giveUpAt)
                {
                    MessageQueue.Enqueue($"Mob threads still running {ShutdownBarrierMs} ms after the stop request; continuing the shutdown without them.");
                    return false;
                }
                spin.SpinOnce(-1);
            }
            return true;
        }

        // Mob-pass hand-off (monster threading review, 2026-10-11). While the mob threads run (thread 0 on the game thread
        // included), monster code must not touch shared lists, other maps, players or scripts. Such work is queued here and run
        // by the game thread in EndMobPass, right after the barrier, when every mob thread is parked. No locks: producers only
        // enqueue (ALWAYS ConcurrentQueue for hand-off), the game thread alone drains.
        private volatile bool _inMobPass;
        private readonly ConcurrentQueue<ListOp> _mobPassListOps = new ConcurrentQueue<ListOp>();
        private readonly ConcurrentQueue<Action> _afterMobPass = new ConcurrentQueue<Action>();

        private readonly struct ListOp
        {
            public readonly LinkedList<MapObject> List;
            public readonly LinkedListNode<MapObject> Node;
            public readonly bool Add;

            public ListOp(LinkedList<MapObject> list, LinkedListNode<MapObject> node, bool add)
            {
                List = list;
                Node = node;
                Add = add;
            }
        }

        /// <summary>True while mob threads may be running monster code (between arming them and the end of the barrier).</summary>
        public bool InMobPass => _inMobPass;

        /// <summary>
        /// Any thread: runs <paramref name="action"/> now, or, during a mob pass, on the game thread right after the barrier (same
        /// tick). For monster work that touches players, other maps, global lists or NPC scripts. Errors are isolated.
        /// </summary>
        public void RunOutsideMobPass(Action action)
        {
            if (_inMobPass)
            {
                _afterMobPass.Enqueue(action);
                return;
            }

            action();
        }

        /// <summary>
        /// Adds <paramref name="ob"/> to <paramref name="list"/> (<see cref="Objects"/> or a mob thread's ObjectsList). During a mob
        /// pass the link is deferred to the barrier, but the returned node is valid at once (callers use it as the "spawned" mark).
        /// </summary>
        internal LinkedListNode<MapObject> LinkObject(LinkedList<MapObject> list, MapObject ob)
        {
            if (!_inMobPass) return list.AddLast(ob);

            var node = new LinkedListNode<MapObject>(ob);
            _mobPassListOps.Enqueue(new ListOp(list, node, true));
            return node;
        }

        /// <summary>Removes <paramref name="node"/> from <paramref name="list"/>; deferred to the barrier during a mob pass.</summary>
        internal void UnlinkObject(LinkedList<MapObject> list, LinkedListNode<MapObject> node)
        {
            if (node == null) return;
            if (_inMobPass)
            {
                _mobPassListOps.Enqueue(new ListOp(list, node, false));
                return;
            }

            if (node.List == list) list.Remove(node);
        }

        /// <summary>Players moved by monster AI during the mob pass whose sector must be updated after it (P9-07).</summary>
        private readonly ConcurrentQueue<PlayerObject> _gridFixups = new ConcurrentQueue<PlayerObject>();

        /// <summary>Any thread during the mob pass: queue a player's sector update for after the barrier.</summary>
        internal void QueueGridFixup(PlayerObject player) => _gridFixups.Enqueue(player);

        /// <summary>Game thread, after the barrier: applies queued sector updates (and their dormant-monster wakes).</summary>
        internal void ApplyGridFixups()
        {
            while (_gridFixups.TryDequeue(out var player))
            {
                Volatile.Write(ref player.GridFixPending, 0);
                try
                {
                    PlayerGrid.Resync(player);
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("sector grid fix-up", ex);
                }
            }
        }

        /// <summary>[Optimisation] SectorGridVerify, once a second on the game thread: logs grid mismatches (scratch servers).</summary>
        private void VerifySectorGrids()
        {
            var bad = 0;
            for (var i = 0; i < MapList.Count; i++)
                if (MapList[i].Grid != null) bad += MapList[i].Grid.Verify();
            if (bad > 0) MessageQueue.Enqueue("[Optimisation] SectorGridVerify: " + bad + " player sector mismatch(es) this second.");
        }

        /// <summary>Game thread, after the barrier (every mob thread parked): applies grid fix-ups, deferred list changes, then deferred work.</summary>
        private void EndMobPass()
        {
            _inMobPass = false;
            ApplyGridFixups(); // P9-07: first, so deferred monster work below already sees every player in its sector (review M2)

            // Queue order is real-time order, so a node's add is always applied before its remove.
            while (_mobPassListOps.TryDequeue(out var op))
            {
                if (op.Add)
                {
                    if (op.Node.List == null) op.List.AddLast(op.Node);
                }
                else if (op.Node.List == op.List)
                {
                    op.List.Remove(op.Node);
                }
            }

            while (_afterMobPass.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("deferred monster work", ex);
                }
            }

        }

        private void ThreadLoop(MobThread Info)
        {
            if (Info.NativeThreadId == 0) Info.NativeThreadId = Info.Id == 0 ? GameThreadNativeId : NativeThread.CurrentId();

            if (_dueScheduler)
            {
                ThreadLoopDue(Info);
                return;
            }

            try
            {
                if (Info.Id != 0)
                {
                    // Park until the game loop arms this thread. It used to start walking at once, while WorkLoop was still in
                    // StartNetwork (LoadConquests spawns guards, walls and gates on the game thread).
                    Info.Stop = true;
                    lock (_locker)
                    {
                        while (Info.Stop) Monitor.Wait(_locker);
                    }
                }
                Info.Stop = false;

                var stopping = false;
                var passComplete = false; // roadmap P6: sub-threads stop after one full pass instead of re-walking until EndTime
                // Review H1: the game thread's own list also needs a pass/time rule, otherwise it walks only as many monsters
                // as it loops while the (now quick) sub-threads finish, i.e. almost none.
                var ownDeadline = Stopwatch.ElapsedMilliseconds + 10;
                if (Info._current == null)
                    Info._current = Info.ObjectsList.First;
                stopping = Info._current == null;

                while (Running)
                {
                    if (Info._current == null)
                    {
                        Info._current = Info.ObjectsList.First;
                        if (Info._current == null) passComplete = true; // empty list: nothing to walk
                    }
                    else
                    {
                        var next = Info._current.Next;

                        //if we reach the end of our list > go back to the top (since we are running threaded, we dont want the system to sit there for xxms doing nothing)
                        if (Info._current == Info.ObjectsList.Last)
                        {
                            passComplete = true;
                            next = Info.ObjectsList.First;
                            Info.LastRunTime = (Info.LastRunTime + (Time - Info.StartTime)) / 2;
                            //Info.LastRunTime = (Time - Info.StartTime) /*> 0 ? (Time - Info.StartTime) : Info.LastRunTime */;
                            Info.StartTime = Time;
                        }
                        if (Time > Info._current.Value.OperateTime)
                        {
                            // Node == null: despawned this pass; its list removal is deferred to the barrier (EndMobPass).
                            if (Info._current.Value.Master == null && Info._current.Value.Node != null) //since we are running multithreaded, dont allow pets to be processed (unless you constantly move pets into their map appropriate thead)
                            {
                                // Isolation (roadmap P5): a throwing monster is quarantined instead of ending this thread,
                                // which used to leave Stop == false and hang the game thread in the barrier forever.
                                var ob = Info._current.Value;
                                try
                                {
                                    ob.Process();
                                    ob.SetOperateTime();
                                }
                                catch (Exception ex)
                                {
                                    if (ex is ThreadInterruptedException && Info.Id != 0) throw; // Stop(): end the thread
                                    WorldErrors.Record($"mob thread {Info.Id}: {WorldErrors.Describe(ob)}", ex);
                                    ob.OperateTime = Time + WorldErrors.QuarantineMs;
                                }
                            }
                        }
                        Info._current = next;
                    }

                    //if it's the main thread > make it loop till the subthreads are done, else make it stop after 'endtime'
                    if (Info.Id == 0)
                    {
                        stopping = true;
                        for (var x = 1; x < MobThreads.Length; x++)
                        {
                            if (MobThreads[x].Stop == false && !MobThreads[x].Dead)
                            {
                                stopping = false;
                            }
                        }
                        if (!stopping) continue;
                        if (Settings.MobThreadOnePass && !passComplete && Stopwatch.ElapsedMilliseconds <= ownDeadline) continue;
                        Info.Stop = stopping;
                        return;
                    }

                    if (!Running) continue;
                    // One pass per tick (roadmap P6, [Optimisation] MobThreadOnePass): the old behaviour re-walked the list until
                    // EndTime (Time + 10 ms) every tick, keeping each mob thread and the game thread's barrier busy at idle.
                    if (!(Settings.MobThreadOnePass && passComplete) && Stopwatch.ElapsedMilliseconds <= Info.EndTime) continue;
                    passComplete = false;
                    Info.Stop = true;
                    lock (_locker)
                    {
                        while (Info.Stop) Monitor.Wait(_locker);
                    }
                }

                // Running went false. Thread 0 (the game thread) must still wait for the sub-threads, or the rest of the tick and
                // the teardown would overlap their pass; a sub-thread marks itself stopped as it exits.
                if (Info.Id == 0) WaitForSubThreads(false);
                else Info.Exited = true;
                Info.Stop = true;
            }
            catch (Exception ex)
            {
                if (ex is ThreadInterruptedException)
                {
                    if (Info.Id != 0) Info.Exited = true; // exiting: the shutdown barrier must not wait for this thread
                    return;
                }

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.ThreadLoopError), ex));

                // An exception outside the per-object catch (roadmap P5): mark the thread dead so the game thread's barrier
                // (ThreadLoop(MobThreads[0])) stops waiting for it. Its monsters stop moving until a restart; logged loudly.
                if (Info.Id != 0)
                {
                    Info.Dead = true;
                    Info.Stop = true;
                    WorldErrors.Record($"mob thread {Info.Id} stopped; its {Info.ObjectsList.Count} monsters are frozen until a restart", ex);
                }
                else
                {
                    // Thread 0 ended its pass early: still hold the barrier until the sub-threads finish theirs (review L7).
                    WorldErrors.Record("mob thread 0 (game thread)", ex);
                    WaitForSubThreads(false);
                    Info.Stop = true;
                }
            }
        }

        /// <summary>Next shared send tick for [Optimisation] SendFlushMs (game thread only).</summary>
        private long _nextSendFlush;

        /// <summary>
        /// End-of-iteration send pass (P9-06, [Optimisation] FlushAtEnd). With SendFlushMs above 0 every in-game connection
        /// sends on the same tick, so a packet broadcast to many players is still encoded once.
        /// </summary>
        private void FlushEndOfTick()
        {
            var flushMs = Settings.SendFlushMs;
            var tick = flushMs <= 0 || Time >= _nextSendFlush;
            if (tick && flushMs > 0) _nextSendFlush = Time - Time % flushMs + flushMs;

            lock (Connections)
            {
                for (var i = Connections.Count - 1; i >= 0; i--)
                {
                    if (i >= Connections.Count) continue; // an isolated disconnect below may shrink the list
                    var connection = Connections[i];
                    try
                    {
                        connection.FlushEndOfTick(tick);
                    }
                    catch (Exception ex)
                    {
                        WorldErrors.Record(WorldErrors.Describe(connection), ex);
                        IsolateConnection(connection);
                    }
                }
                MirConnection.EndEncodePass();
            }
        }

        /// <summary>
        /// Called after a connection's Process threw (roadmap P5). Disconnects only that client; if the disconnect itself
        /// throws (corrupted player state), the connection is still removed so it cannot throw every tick.
        /// Caller holds the Connections lock (game thread).
        /// </summary>
        private void IsolateConnection(MirConnection connection)
        {
            try
            {
                connection.Disconnect(3);
            }
            catch (Exception ex)
            {
                WorldErrors.Record("disconnect after isolated error, " + WorldErrors.Describe(connection), ex);
                Connections.Remove(connection);
                connection.ForceClose(); // review M1: release the account and socket so the player can log in again
            }
        }

        /// <summary>
        /// Called after an object's Process threw (roadmap P5). Players are disconnected (their state may be half-updated);
        /// anything else is quarantined: not processed again for WorldErrors.QuarantineMs.
        /// </summary>
        private void IsolateObject(MapObject ob)
        {
            ob.OperateTime = Time + WorldErrors.QuarantineMs;
            if (ob is PlayerObject player && player.Connection != null)
            {
                var connection = player.Connection;
                lock (Connections) IsolateConnection(connection);
            }
        }

        private void AdjustLights()
        {
            var oldLights = Lights;

            var hours = Now.Hour * 2 % 24;
            if (hours == 6 || hours == 7)
                Lights = LightSetting.Dawn;
            else if (hours >= 8 && hours <= 15)
                Lights = LightSetting.Day;
            else if (hours == 16 || hours == 17)
                Lights = LightSetting.Evening;
            else
                Lights = LightSetting.Night;

            if (oldLights == Lights) return;

            Broadcast(new S.TimeOfDay { Lights = Lights });
        }

        public void Process()
        {
            if (Now.Day != dailyTime)
            {
                dailyTime = Now.Day;
                ProcessNewDay();
            }

            if (Time >= warTime)
            {
                warTime = Time + Settings.Minute;
                for (var i = GuildsAtWar.Count - 1; i >= 0; i--)
                {
                    GuildsAtWar[i].TimeRemaining -= Settings.Minute;

                    if (GuildsAtWar[i].TimeRemaining >= 0) continue;
                    GuildsAtWar[i].EndWar();
                    GuildsAtWar.RemoveAt(i);
                }
            }

            if (Time >= guildTime)
            {
                guildTime = Time + Settings.Minute;
                for (var i = 0; i < Guilds.Count; i++)
                {
                    Guilds[i].Process();
                }
            }

            if (Time >= conquestTime)
            {
                conquestTime = Time + Settings.Second * 10;
                for (var i = 0; i < Conquests.Count; i++)
                {
                    Conquests[i].Process();
                }
            }

            if (Time >= rentalItemsTime)
            {
                rentalItemsTime = Time + Settings.Minute * 5;
                ProcessRentedItems();
            }

            if (Time >= auctionTime)
            {
                auctionTime = Time + Settings.Minute * 10;
                ProcessAuction();
            }

            if (Time >= spawnTime)
            {
                spawnTime = Time + Settings.Second * 10;
                Main.RespawnTick.Process();
                // F4 (LiveDb): publish the counter for Envir.Edit's SaveDB (an HTTP worker thread).
                Interlocked.Exchange(ref _publishedRespawnTick, (long)Main.RespawnTick.CurrentTickcounter);
            }

            if (Time >= robotTime)
            {
                robotTime = Time + Settings.Minute;
                Robot.Process(RobotNPC);
            }

            if (Time >= timerTime)
            {
                timerTime = Time + Settings.Second;

                string[] keys = Timers.Keys.ToArray();

                foreach (var key in keys)
                {
                    if (Timers[key].RelativeTime <= Time)
                    {
                        Timers.Remove(key);
                    }
                }
            }
        }

        private void ProcessAuction()
        {
            LinkedListNode<AuctionInfo> current = Auctions.First;

            while (current != null)
            {
                AuctionInfo info = current.Value;

                if (!info.Expired && !info.Sold && Now >= info.ConsignmentDate.AddDays(Globals.ConsignmentLength))
                {
                    if (info.ItemType == MarketItemType.Auction && info.CurrentBid > info.Price)
                    {
                        string message = GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.YouWonForGold), info.Item.FriendlyName, info.CurrentBid);

                        info.Sold = true;
                        RecordMarketSale(info.Item.ItemIndex, info.CurrentBid);
                        MailCharacter(info.CurrentBuyerInfo, item: info.Item, customMessage: message);

                        MessageAccount(info.CurrentBuyerInfo.AccountInfo, GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.YouBoughtForGold), info.Item.FriendlyName, info.CurrentBid),
                            ChatType.Hint);
                        MessageAccount(info.SellerInfo.AccountInfo, GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.YouSoldForGold), info.Item.FriendlyName, info.CurrentBid),
                            ChatType.Hint);
                    }
                    else
                    {
                        info.Expired = true;
                    }
                }

                current = current.Next;
            }
        }

        public void Broadcast(Packet p)
        {
            for (var i = 0; i < Players.Count; i++) Players[i].Enqueue(p);
        }

        public void RequiresBaseStatUpdate()
        {
            for (var i = 0; i < Players.Count; i++) Players[i].HasUpdatedBaseStats = false;
        }

        public void RequiresHeroBaseStatUpdate()
        {
            for (var i = 0; i < Heroes.Count; i++)
            {
                Heroes[i].HasUpdatedBaseStats = false;
                Heroes[i].RefreshStats();
            }
        }

        public void SaveDB()
        {
            SaveDB(null);
        }

        /// <summary>
        /// <paramref name="backupFileName"/>: the name the previous Server.MirDB gets in Back Up\Database (a plain file name; null = the
        /// envir's own "Database yyyy-MM-dd HH-mm-ss.bak", which for Envir.Edit never advances because its clock does not run).
        /// The web admin (LiveDb) passes a wall-clock name at most every [LiveDb] BackupEveryMinutes and a rolling name in between.
        /// </summary>
        public void SaveDB(string backupFileName)
        {
            Store.SaveDesign(this, backupFileName);

            //WebAdmin: once the Edit envir has saved, Main's in-memory system data is older than the store until it reloads.
            if (this == Edit) _systemDataStale = true;
        }


        public CharacterInfo GetArchivedCharacter(string name)
        {
            DirectoryInfo dir = new DirectoryInfo(ArchivePath);
            FileInfo[] files = dir.GetFiles($"{name}*.MirCA");

            if (files.Length != 1)
            {
                return null;
            }

            var fileInfo = files[0];

            CharacterInfo info = null;

            using (var stream = fileInfo.OpenRead())
            {
                using var reader = new BinaryReader(stream);

                var version = reader.ReadInt32();
                var customVersion = reader.ReadInt32();

                info = new CharacterInfo(reader, version, customVersion);
            }

            return info;
        }

        public void SaveArchivedCharacter(CharacterInfo info)
        {
            if (!Directory.Exists(ArchivePath)) Directory.CreateDirectory(ArchivePath);

            using var stream = File.Create(Path.Combine(ArchivePath, @$"{info.Name}{Now:_MMddyyyy_HHmmss}.MirCA"));
            using var writer = new BinaryWriter(stream);

            writer.Write(Version);
            writer.Write(CustomVersion);

            info.Save(writer);
        }

        public void SaveAccounts()
        {
            // Saving is always reset by EndSaveAccounts (roadmap P2); the bound only guards against a write that never completes.
            var waitStart = Stopwatch.ElapsedMilliseconds;
            while (Saving && Stopwatch.ElapsedMilliseconds - waitStart < AccountSaveWaitLimitMs)
                Thread.Sleep(1);
            if (Saving)
                MessageQueue.Enqueue($"[Save] The previous account save did not finish within {AccountSaveWaitLimitMs / 1000} s; saving accounts anyway.");

            try
            {
                using (var stream = File.Create(AccountPath + "n"))
                    SaveAccounts(stream);
                ReplaceAccountsFile(AccountPath + "n", null);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
            }

            // P9-12 (review H2): a full save never uses the cache, and afterwards no older blob may be written over it.
            AccountSaveCache.InvalidateAll();
        }

        private static double SaveMs(long from, long to) => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Size of the last periodic account save in bytes (diagnostics; written by whichever thread serialised it).</summary>
        public static long LastAccountSaveBytes => Volatile.Read(ref _lastAccountSaveBytes);
        private static long _lastAccountSaveBytes;

        private const int AccountSaveWaitLimitMs = 30000;
        private static int _accountSaveFailures;

        /// <summary>Account saves that failed since the process started (each is logged). Read from any thread.</summary>
        public static int AccountSaveFailures => Volatile.Read(ref _accountSaveFailures);

        /// <summary>
        /// Swaps a completely written temp file in as Server.MirADB. File.Replace is atomic on the same volume, so a
        /// Server.MirADB exists at every instant; the previous file becomes <paramref name="backupFile"/> when given.
        /// </summary>
        private static void ReplaceAccountsFile(string tempPath, string backupFile) => ReplaceAccountsFile(tempPath, AccountPath, backupFile);

        private static void ReplaceAccountsFile(string tempPath, string livePath, string backupFile)
        {
            if (File.Exists(livePath))
            {
                if (backupFile != null && File.Exists(backupFile)) File.Delete(backupFile);
                File.Replace(tempPath, livePath, backupFile);
            }
            else
            {
                File.Move(tempPath, livePath);
            }
        }

        /// <summary>
        /// True when a missing live file is more likely lost than new: temp files from a save, or backups, exist.
        /// Used to refuse creating an empty database over data that should be restored instead (roadmap P2).
        /// </summary>
        internal static bool HasPriorDataEvidence(string livePath, string backupDir)
        {
            if (File.Exists(livePath + "n") || File.Exists(livePath + "o")) return true;
            return Directory.Exists(backupDir) && Directory.EnumerateFiles(backupDir, "*.bak").Any();
        }

        // BinaryWriter's default encoding (UTF-8, no BOM, throws on invalid surrogates), so leaveOpen changes no byte.
        private static readonly System.Text.UTF8Encoding AccountSaveEncoding = new System.Text.UTF8Encoding(false, true);

        private void SaveAccounts(Stream stream, bool leaveOpen = false)
        {
            using (var writer = leaveOpen ? new BinaryWriter(stream, AccountSaveEncoding, true) : new BinaryWriter(stream))
                SaveAccounts(writer);

            if (this == Main) global::Server.WebAdmin.WebAdminHooks.RaiseAccountsSaved();
        }

        /// <summary>Writes the whole accounts file to <paramref name="writer"/> (does not flush or dispose it; the caller raises AccountsSaved).</summary>
        private void SaveAccounts(BinaryWriter writer) => SaveAccounts(writer, null);

        /// <summary>
        /// With <paramref name="segments"/> (P9-12, periodic save with the offline account cache): the file is the list of
        /// segments in order, ranges of <paramref name="writer"/>'s buffer and cached account blobs; otherwise the whole file
        /// is in <paramref name="writer"/>.
        /// </summary>
        private void SaveAccounts(BinaryWriter writer, List<AccountSaveSegment> segments)
        {
            writer.Write(Version);
            writer.Write(CustomVersion);
            writer.Write(NextAccountID);
            writer.Write(NextCharacterID);
            writer.Write(NextUserItemID);
            writer.Write(NextHeroID);

            writer.Write(GuildList.Count);
            writer.Write(NextGuildID);
            writer.Write(HeroList.Count);
            for (var i = 0; i < HeroList.Count; i++)
                HeroList[i].Save(writer);
            writer.Write(AccountList.Count);
            var runStart = 0;
            if (segments != null)
                runStart = SaveAccountListCached((BufferBinaryWriter)writer, segments);
            else
                for (var i = 0; i < AccountList.Count; i++)
                    AccountList[i].Save(writer);

            writer.Write(NextAuctionID);
            writer.Write(Auctions.Count);
            foreach (var auction in Auctions)
                auction.Save(writer);

            writer.Write(NextMailID);

            writer.Write(GameshopLog.Count);
            foreach (var item in GameshopLog)
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }

            writer.Write(SavedSpawns.Count);
            foreach (var Spawn in SavedSpawns)
            {
                var Save = new RespawnSave { RespawnIndex = Spawn.Info.RespawnIndex, NextSpawnTick = Spawn.NextSpawnTick, Spawned = Spawn.Count >= Spawn.Info.Count * SpawnMultiplier };
                Save.Save(writer);
            }

            writer.Write(MarketSales.Count);
            foreach (var sales in MarketSales)
            {
                writer.Write(sales.Key);
                sales.Value.Save(writer);
            }

            if (segments != null)
            {
                var length = ((BufferBinaryWriter)writer).Length;
                if (length > runStart) segments.Add(new AccountSaveSegment(null, runStart, length - runStart));
            }
        }

        private void SaveGuilds(bool forced = false)
        {
            if (!Directory.Exists(Settings.GuildPath)) Directory.CreateDirectory(Settings.GuildPath);

            var refreshing = false;
            if (GuildRefreshNeeded == true) //resaves with new indexing if a guild is deleted.
            {
                refreshing = true; // GuildRefreshNeeded is cleared only after every write below was started (review M3)
                // Crash-safe (roadmap P5): the old code deleted every .mgd first, so a crash before the async writes finished
                // lost guilds. Now every guild is rewritten (each file swapped atomically) and the surplus files (index >=
                // count; LoadGuilds never reads them) are pruned only after all of those writes have succeeded.
                forced = true; //triggers a full resave of all guilds
                Interlocked.Exchange(ref _guildRefreshFailed, 0);
                Interlocked.Exchange(ref _guildRefreshPending, GuildList.Count);
                _guildRefreshKeep = GuildList.Count;
                if (GuildList.Count == 0) PruneSurplusGuildFiles(0);
            }

            for (var i = 0; i < GuildList.Count; i++)
            {
                if (GuildList[i].NeedSave || forced)
                {
                    GuildList[i].NeedSave = false;

					GuildObject liveGuild = Guilds.Find(g => g.Guildindex == GuildList[i].GuildIndex);
					if (liveGuild != null)
					{
						GuildList[i] = liveGuild.Info;
					}

					var mStream = new MemoryStream();
                    var writer = new BinaryWriter(mStream);
					GuildList[i].Save(writer);
                    var fStream = new FileStream(Path.Combine(Settings.GuildPath, i + ".mgdn"), FileMode.Create);
                    var data = mStream.ToArray();
                    fStream.BeginWrite(data, 0, data.Length, EndSaveGuildsAsync, new SideFileSave(fStream, refreshing));
                }
            }

            if (refreshing) GuildRefreshNeeded = false; // an exception above leaves it set, so the next save retries the re-index
        }
        private void EndSaveGuildsAsync(IAsyncResult result)
        {
            var state = (SideFileSave)result.AsyncState;
            var ok = CompleteSideFileSave(state.Stream, result);
            if (!state.CountsTowardRefresh) return;
            if (!ok) Interlocked.Exchange(ref _guildRefreshFailed, 1);
            if (Interlocked.Decrement(ref _guildRefreshPending) == 0)
            {
                if (Volatile.Read(ref _guildRefreshFailed) == 0) PruneSurplusGuildFiles(_guildRefreshKeep);
                else MessageQueue.Enqueue("[Save] A guild file failed to save during a full guild resave; surplus guild files were kept.");
            }
        }

        private int _guildRefreshPending, _guildRefreshFailed, _guildRefreshKeep;

        private sealed class SideFileSave
        {
            public readonly FileStream Stream;
            public readonly bool CountsTowardRefresh;
            public SideFileSave(FileStream stream, bool countsTowardRefresh) { Stream = stream; CountsTowardRefresh = countsTowardRefresh; }
        }

        /// <summary>
        /// Shared completion for the async guild (.mgd), goods (.msd) and conquest (.mcd) saves (roadmap P5). Runs on an IO
        /// completion thread and touches only files. The temp file "&lt;name&gt;n" is swapped in with File.Replace, so the
        /// live file always exists; failures are logged and counted instead of swallowed.
        /// </summary>
        internal static bool CompleteSideFileSave(FileStream fStream, IAsyncResult result)
        {
            if (fStream == null) return false;
            try
            {
                var newfilename = fStream.Name;
                var oldfilename = newfilename.Substring(0, newfilename.Length - 1);
                fStream.EndWrite(result);
                fStream.Dispose();
                if (File.Exists(oldfilename)) File.Replace(newfilename, oldfilename, null);
                else File.Move(newfilename, oldfilename);
                return true;
            }
            catch (Exception ex)
            {
                fStream.Dispose();
                WorldErrors.Record("async save of " + fStream.Name, ex);
                return false;
            }
        }

        /// <summary>Deletes guild files whose index is outside 0..keep-1 (LoadGuilds never reads them).</summary>
        internal static void PruneSurplusGuildFiles(int keep)
        {
            try
            {
                foreach (var file in Directory.GetFiles(Settings.GuildPath, "*.mgd"))
                {
                    if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var index) && index >= keep)
                        File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                WorldErrors.Record("pruning surplus guild files", ex);
            }
        }

        private void SaveGoods(bool forced = false)
        {
            if (!Directory.Exists(Settings.GoodsPath)) Directory.CreateDirectory(Settings.GoodsPath);

            for (var i = 0; i < MapList.Count; i++)
            {
                var map = MapList[i];

                if (map.NPCs.Count == 0) continue;

                for (var j = 0; j < map.NPCs.Count; j++)
                {
                    var npc = map.NPCs[j];

                    if (forced)
                    {
                        npc.ProcessGoods(forced);
                    }

                    if (!npc.NeedSave) continue;

                    var path = Path.Combine(Settings.GoodsPath, npc.Info.Index + ".msdn");

                    var mStream = new MemoryStream();
                    var writer = new BinaryWriter(mStream);
                    var Temp = 9999;
                    writer.Write(Temp);
                    writer.Write(Version);
                    writer.Write(CustomVersion);
                    writer.Write(npc.UsedGoods.Count);

                    for (var k = 0; k < npc.UsedGoods.Count; k++)
                    {
                        npc.UsedGoods[k].Save(writer);
                    }

                    var fStream = new FileStream(path, FileMode.Create);
                    var data = mStream.ToArray();
                    fStream.BeginWrite(data, 0, data.Length, EndSaveGoodsAsync, fStream);
                }
            }
        }
        private void EndSaveGoodsAsync(IAsyncResult result)
        {
            CompleteSideFileSave(result.AsyncState as FileStream, result);
        }

        private void SaveConquests(bool forced = false)
        {
            if (!Directory.Exists(Settings.ConquestsPath)) Directory.CreateDirectory(Settings.ConquestsPath);
            for (var i = 0; i < ConquestList.Count; i++)
            {
                if (!ConquestList[i].NeedSave && !forced) continue;
                ConquestList[i].NeedSave = false;
                var mStream = new MemoryStream();
                var writer = new BinaryWriter(mStream);
                ConquestList[i].Save(writer);
                var fStream = new FileStream(Path.Combine(Settings.ConquestsPath, ConquestList[i].Info.Index + ".mcdn"), FileMode.Create);
                var data = mStream.ToArray();
                fStream.BeginWrite(data, 0, data.Length, EndSaveConquestsAsync, fStream);
            }
        }
        private void EndSaveConquestsAsync(IAsyncResult result)
        {
            CompleteSideFileSave(result.AsyncState as FileStream, result);
        }

        /// <summary>
        /// Periodic account save (game thread). Crash-safe since roadmap P2: accounts are serialised to memory first, so a
        /// serialisation exception leaves Server.MirADB untouched and Saving false; the bytes are then written to
        /// Server.MirADBn asynchronously and swapped in by EndSaveAccounts with File.Replace, which moves the previous file
        /// into Back Up\Accounts. Previously the live file was moved to Back Up first, leaving a window with no Server.MirADB.
        /// </summary>
        public void BeginSaveAccounts()
        {
            if (Saving) return;

            // P9-12: the offline account cache works only on the off-thread path (segment-list write) and only for Main.
            var cache = Settings.AccountSaveOffThread && Settings.OfflineAccountCache != OfflineAccountCacheMode.Off && this == Main;
            _lastSaveUsedCache = cache;
            if (!cache && _accountCacheUsed) DropAccountCache();

            if (Settings.AccountSaveOffThread) BeginSaveAccountsOffThread(cache);
            else BeginSaveAccountsLegacy();
        }

        /// <summary>
        /// P9-10 ([Optimisation] AccountSaveOffThread). The game thread only serialises: into one buffer kept across saves (no
        /// growth, no ToArray copy, no LOH allocation once it has reached the file size). Those bytes are the snapshot: they
        /// are cheaper to produce than a deep copy of the accounts, and nothing reads a live object after this call. A pool
        /// thread then creates Server.MirADBn, writes it and swaps it in exactly as <see cref="EndSaveAccounts"/> does. The
        /// buffer is not touched again until that thread clears <see cref="Saving"/> (volatile), which every save waits for.
        /// </summary>
        private void BeginSaveAccountsOffThread(bool cache = false)
        {
            Saving = true;

            // Sized from the live file on the first save, so it normally never grows (no LOH copies).
            var writer = _accountSaveWriter ??= new BufferBinaryWriter(AccountSaveEncoding, InitialAccountSaveCapacity());
            List<AccountSaveSegment> segments = null;
            try
            {
                writer.Reset();
                if (cache)
                {
                    segments = _accountSaveSegments;
                    segments.Clear();
                    _accountCacheUsed = true;
                }
                SaveAccounts(writer, segments);
                if (this == Main) global::Server.WebAdmin.WebAdminHooks.RaiseAccountsSaved();
            }
            catch (Exception ex)
            {
                Saving = false;
                _lastSaveUsedCache = false; // review L4: no stale counters of an earlier pass on the [Diag] Save line
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
                return;
            }

            var length = writer.Length;
            long fileLength = length;
            if (segments != null)
            {
                fileLength = 0;
                for (var i = 0; i < segments.Count; i++) fileLength += segments[i].Length;
            }
            Volatile.Write(ref _lastAccountSaveBytes, fileLength);
            // The name uses the game clock, as before; whether a backup is taken is decided with the file checks off the thread.
            // Full paths now, so the pool thread does not depend on the current directory.
            var job = new AccountSaveJob(writer.Buffer, length, Path.GetFullPath(AccountPath), Path.GetFullPath(AccountsBackUpPath),
                $"Accounts {Now.Year:0000}-{Now.Month:00}-{Now.Day:00} {Now.Hour:00}-{Now.Minute:00}-{Now.Second:00}.bak", segments);
            try
            {
                ThreadPool.UnsafeQueueUserWorkItem(WriteAccountsFile, job, preferLocal: false);
            }
            catch (Exception ex)
            {
                Saving = false;
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
            }
        }

        private BufferBinaryWriter _accountSaveWriter; // game thread while !Saving, the pool thread (reads Buffer) while Saving
        // P9-12: the cached save's file as pieces in order; owned like _accountSaveWriter (the pool thread reads it while Saving).
        private readonly List<AccountSaveSegment> _accountSaveSegments = new List<AccountSaveSegment>();

        private static int InitialAccountSaveCapacity()
        {
            try
            {
                var info = new FileInfo(AccountPath);
                if (info.Exists) return (int)Math.Min(info.Length + info.Length / 8 + 64 * 1024, Array.MaxLength);
            }
            catch (Exception) { } // only a size hint
            return 1024 * 1024;
        }

        private sealed class AccountSaveJob
        {
            public readonly byte[] Data;
            public readonly int Length;
            public readonly string AccountPath, BackUpDir, BackupName;
            public readonly List<AccountSaveSegment> Segments; // null = the whole file is Data[0..Length)
            public AccountSaveJob(byte[] data, int length, string accountPath, string backUpDir, string backupName, List<AccountSaveSegment> segments = null)
            {
                Data = data; Length = length; AccountPath = accountPath; BackUpDir = backUpDir; BackupName = backupName; Segments = segments;
            }
        }

        /// <summary>One piece of a cached periodic account save (P9-12): a range of the save buffer (Blob null) or an account's cached bytes.</summary>
        private readonly struct AccountSaveSegment
        {
            public readonly byte[] Blob;
            public readonly int Offset, Length;
            public AccountSaveSegment(byte[] blob, int offset, int length) { Blob = blob; Offset = offset; Length = length; }
        }

        // Runs on a pool thread: touches only the files, the job's bytes and the Saving flag (like EndSaveAccounts).
        private void WriteAccountsFile(AccountSaveJob job)
        {
            try
            {
                string backupFile = null;
                if (File.Exists(job.AccountPath))
                {
                    if (!Directory.Exists(job.BackUpDir)) Directory.CreateDirectory(job.BackUpDir);
                    backupFile = Path.Combine(job.BackUpDir, job.BackupName);
                }

                if (job.Segments == null)
                {
                    using (var fStream = new FileStream(job.AccountPath + "n", FileMode.Create))
                        fStream.Write(job.Data, 0, job.Length);
                }
                else
                {
                    // Thousands of small blobs: a large FileStream buffer turns them into few writes.
                    using (var fStream = new FileStream(job.AccountPath + "n", FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024))
                        for (var i = 0; i < job.Segments.Count; i++)
                        {
                            var segment = job.Segments[i];
                            fStream.Write(segment.Blob ?? job.Data, segment.Offset, segment.Length);
                        }
                }
                ReplaceAccountsFile(job.AccountPath + "n", job.AccountPath, backupFile);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
            }
            finally
            {
                Saving = false;
            }
        }

        // ---- P9-12 offline account cache (Future Ideas/Offline-Account-Cache-Plan.md) --------------------------------------

        private bool _accountCacheUsed;   // a cached save stored blobs that DropAccountCache has not cleared yet
        private bool _lastSaveUsedCache;  // for the [Diag] Save line
        private int _accountCacheCompareCursor; // list index the next pass's rolling compare window starts at
        private int _accountCacheCompareCarry;  // compare budget a pass left unused when it reached the end of the list
        private long _accountCacheMismatchTotal;
        private readonly HashSet<int> _accountCacheMismatchLogged = new HashSet<int>();

        /// <summary>
        /// The account's saved bytes may have changed: the next periodic save serialises it again (game thread; null-safe, so
        /// <c>MarkAccountDirty(info.AccountInfo)</c> is fine for a <see cref="HeroInfo"/>). Call it before the mutation, or at
        /// least before anything that can throw after mutating. Never throws.
        /// </summary>
        public static void MarkAccountDirty(AccountInfo account)
        {
            if (account != null) account.SaveBlob = null;
        }

        /// <summary>Every cached account is serialised again by the next periodic save (any thread; call after mutating).</summary>
        public static void InvalidateAccountCache() => AccountSaveCache.InvalidateAll();

        private static bool IsAccountOnline(AccountInfo account)
        {
            if (account.Connection != null) return true;
            var characters = account.Characters;
            for (var i = 0; i < characters.Count; i++)
                if (characters[i]?.Player != null) return true;
            return false;
        }

        /// <summary>
        /// True when the account's bytes change with the clock alone: an active refine (CharacterInfo.Save writes CollectTime -
        /// Time) or an intelligent creature that expires (writes Expire - Now). Such accounts are never cached.
        /// </summary>
        private bool HasTimedBytes(AccountInfo account)
        {
            var characters = account.Characters;
            for (var i = 0; i < characters.Count; i++)
            {
                var c = characters[i];
                if (c == null) continue;
                if (c.CollectTime > Time) return true;
                var creatures = c.IntelligentCreatures;
                for (var j = 0; j < creatures.Count; j++)
                    if (creatures[j].Expire != DateTime.MinValue) return true;
            }
            return false;
        }

        /// <summary>
        /// The account list of a cached periodic save (game thread). An account is serialised fresh when it is online, was
        /// online since the last save (sticky <see cref="AccountInfo.SaveHot"/>), has time-dependent bytes, has no blob, or its
        /// blob belongs to an older epoch; otherwise its blob is written as is. A cold account serialised fresh stores a copy of
        /// its bytes for the next save (when they equal its stale-epoch blob, the old array is kept and only re-tagged).
        /// A rolling round-robin window also serialises cached accounts and compares: each pass checks
        /// ceil(accounts / <see cref="Settings.OfflineAccountCacheFullEvery"/>) cached accounts (in Verify mode at least
        /// <see cref="Settings.OfflineAccountCacheVerifyPerSave"/>), so every cached account is compared at least once every
        /// FullEvery saves without a spike (review M1). Equal keeps the old array; a mismatch is counted, logged once per
        /// account and repaired. Returns the start of the buffer run still open (the caller closes it after the tail).
        /// </summary>
        private int SaveAccountListCached(BufferBinaryWriter writer, List<AccountSaveSegment> segments)
        {
            var epoch = AccountSaveCache.Epoch;          // one snapshot per pass, read before Suspended (review H3)
            var store = !AccountSaveCache.Suspended;     // a WinForms account editor is open: nothing cached is used or stored
            var count = AccountList.Count;

            // Rolling compare window (review M1/L3): starts at the cursor, runs to the end of the list at most (one lap per
            // pass), and its budget counts cached accounts actually compared. Budget left at the end of the list carries
            // into the next pass (capped at one pass's share), so a lap takes at most FullEvery passes.
            var fullEvery = Math.Max(1, Settings.OfflineAccountCacheFullEvery);
            var share = (count + fullEvery - 1) / fullEvery;
            if (Settings.OfflineAccountCache == OfflineAccountCacheMode.Verify)
                share = Math.Max(share, Math.Max(1, Settings.OfflineAccountCacheVerifyPerSave));
            var compareLeft = share + _accountCacheCompareCarry;
            var compareFrom = _accountCacheCompareCursor;
            if (compareFrom >= count || compareLeft >= count) compareFrom = 0; // list shrank, or the budget covers everything
            var compareNext = compareFrom;

            int cached = 0, fresh = 0, timed = 0, verified = 0, mismatch = 0;
            var runStart = 0; // the writer was reset for this save: the first run includes the header and the hero list
            for (var i = 0; i < count; i++)
            {
                var account = AccountList[i];
                var online = IsAccountOnline(account);
                var isTimed = !online && HasTimedBytes(account);
                var blob = account.SaveBlob;

                if (!online && !account.SaveHot && !isTimed && store && blob != null && account.SaveBlobEpoch == epoch)
                {
                    if (i < compareFrom || compareLeft <= 0)
                    {
                        if (writer.Length > runStart) segments.Add(new AccountSaveSegment(null, runStart, writer.Length - runStart));
                        segments.Add(new AccountSaveSegment(blob, 0, blob.Length));
                        runStart = writer.Length;
                        cached++;
                        continue;
                    }

                    var checkStart = writer.Length;
                    account.Save(writer);
                    verified++;
                    compareLeft--;
                    compareNext = i + 1;
                    var bytes = new ReadOnlySpan<byte>(writer.Buffer, checkStart, writer.Length - checkStart);
                    if (!bytes.SequenceEqual(blob))
                    {
                        mismatch++;
                        _accountCacheMismatchTotal++;
                        account.SaveBlob = bytes.ToArray(); // replaced, never mutated (blobs are shared with pool-thread writes); epoch unchanged
                        if (_accountCacheMismatchLogged.Add(account.Index))
                            MessageQueue.Enqueue($"[Diag] AccountCache mismatch account={account.Index} ({account.AccountID}): its cached bytes differed from a fresh save; a dirty mark is missing.");
                    }
                    continue;
                }

                var start = writer.Length;
                account.Save(writer);
                if (isTimed) timed++;
                else fresh++;

                if (online || isTimed || !store)
                {
                    account.SaveBlob = null;
                    account.SaveHot = online; // logged out after this pass: still hot at the next one (it may change until then)
                    continue;
                }

                // Cold: keep the bytes for the next save. A stale-epoch blob with the same bytes is kept and only re-tagged
                // (review M2: an invalidate-all would otherwise reallocate every blob). Different bytes are expected after an
                // invalidation, so they are not a mismatch.
                var saved = new ReadOnlySpan<byte>(writer.Buffer, start, writer.Length - start);
                if (blob == null || !saved.SequenceEqual(blob)) account.SaveBlob = saved.ToArray();
                account.SaveBlobEpoch = epoch;
                account.SaveHot = false;
            }

            // The window reached the end of the list with budget left: the lap is complete, the next one starts at 0.
            var lap = compareLeft > 0;
            _accountCacheCompareCarry = lap ? Math.Min(compareLeft, share) : 0;
            _accountCacheCompareCursor = lap ? 0 : compareNext;

            AccountSaveCache.Publish(new AccountCacheStats
            {
                Cached = cached, Fresh = fresh, Timed = timed, Verified = verified, Mismatch = mismatch, Epoch = epoch,
                LapComplete = lap, MismatchTotal = _accountCacheMismatchTotal,
            });
            return runStart;
        }

        /// <summary>The cache is off again: frees every blob (game thread).</summary>
        private void DropAccountCache()
        {
            for (var i = 0; i < AccountList.Count; i++) AccountList[i].SaveBlob = null;
            _accountCacheUsed = false;
            _accountCacheCompareCursor = 0;
            _accountCacheCompareCarry = 0;
        }

        private string AccountCacheDiag()
        {
            if (!_lastSaveUsedCache) return string.Empty;
            var s = AccountSaveCache.Last;
            if (s == null) return string.Empty;
            return $" cached={s.Cached} fresh={s.Fresh} timed={s.Timed} verified={s.Verified} mismatch={s.Mismatch} epoch={s.Epoch}{(s.LapComplete ? " lap" : "")}";
        }

        /// <summary>The save before P9-10, kept behind [Optimisation] AccountSaveOffThread=False.</summary>
        private void BeginSaveAccountsLegacy()
        {
            Saving = true;

            byte[] data;
            try
            {
                using (var mStream = new MemoryStream())
                {
                    SaveAccounts(mStream);
                    data = mStream.ToArray();
                }
                Volatile.Write(ref _lastAccountSaveBytes, data.Length);
            }
            catch (Exception ex)
            {
                Saving = false;
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
                return;
            }

            string backupFile = null;
            FileStream fStream = null;
            try
            {
                if (File.Exists(AccountPath))
                {
                    if (!Directory.Exists(AccountsBackUpPath)) Directory.CreateDirectory(AccountsBackUpPath);
                    backupFile = Path.Combine(AccountsBackUpPath,
                        $"Accounts {Now.Year:0000}-{Now.Month:00}-{Now.Day:00} {Now.Hour:00}-{Now.Minute:00}-{Now.Second:00}.bak");
                }

                fStream = new FileStream(AccountPath + "n", FileMode.Create);
                fStream.BeginWrite(data, 0, data.Length, EndSaveAccounts, new AccountSaveState(fStream, backupFile));
            }
            catch (Exception ex)
            {
                fStream?.Dispose();
                Saving = false;
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
            }
        }

        private sealed class AccountSaveState
        {
            public readonly FileStream Stream;
            public readonly string BackupFile;
            public AccountSaveState(FileStream stream, string backupFile) { Stream = stream; BackupFile = backupFile; }
        }

        // Runs on an IO completion thread: touches only the files and the Saving flag.
        private void EndSaveAccounts(IAsyncResult result)
        {
            var state = (AccountSaveState)result.AsyncState;
            try
            {
                state.Stream.EndWrite(result);
                state.Stream.Dispose();
                ReplaceAccountsFile(AccountPath + "n", state.BackupFile);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _accountSaveFailures);
                MessageQueue.Enqueue(ex);
            }
            finally
            {
                state.Stream.Dispose();
                Saving = false;
            }
        }

        public bool LoadDB()
        {
            lock (LoadLock)
            {
                if (!Store.LoadDesign(this)) return false;
                WorldBinder.Bind(this);
            }

            return true;
        }

        public void LoadAccounts()
        {
            AccountSaveCache.InvalidateAll(); // P9-12: also when the load below throws half-way

            //reset ranking
            for (var i = 0; i < RankClass.Count(); i++)
            {
                if (RankClass[i] != null)
                {
                    RankClass[i].Clear();
                }
                else
                {
                    RankClass[i] = new List<RankCharacterInfo>();
                }
            }

            RankTop.Clear();

            lock (LoadLock)
            {
                if (!File.Exists(AccountPath))
                {
                    // Fail closed (roadmap P2): never create an empty accounts DB over one that should be restored.
                    // Throwing keeps the world from starting (StartNetwork runs before the save tail) and makes a web
                    // admin restore report failure.
                    if (HasPriorDataEvidence(AccountPath, AccountsBackUpPath))
                        throw new InvalidOperationException("[Load] Server.MirADB is missing but Back Up\\Accounts or Server.MirADBn/o exists. Refusing to create an empty accounts database: restore the newest backup (Knowledgebase/Runbook.md).");
                    SaveAccounts();
                }

                using (var stream = File.OpenRead(AccountPath))
                using (var reader = new BinaryReader(stream))
                {
                    LoadVersion = reader.ReadInt32();
                    LoadCustomVersion = reader.ReadInt32();

                    if (LoadVersion < MinVersion || LoadVersion > Version)
                        throw new InvalidOperationException($"[Load] Server.MirADB version {LoadVersion} is outside the supported range {MinVersion}-{Version}. Refusing to load it (a newer file would be misread and then overwritten).");
                    NextAccountID = reader.ReadInt32();
                    NextCharacterID = reader.ReadInt32();
                    NextUserItemID = reader.ReadUInt64();
                    if (LoadVersion > 98)
                        NextHeroID = reader.ReadInt32();

                    GuildCount = reader.ReadInt32();
                    NextGuildID = reader.ReadInt32();

                    int count;
                    if (LoadVersion > 102)
                    {
                        count = reader.ReadInt32();

                        HeroList.Clear();

                        for (var i = 0; i < count; i++)
                            HeroList.Add(new HeroInfo(reader, LoadVersion, LoadCustomVersion));
                    }

                    count = reader.ReadInt32();

                    AccountList.Clear();
                    CharacterList.Clear();

                    int TrueAccount = 0;
                    for (var i = 0; i < count; i++)
                    {
                        AccountInfo NextAccount = new AccountInfo(reader);
                        // Accounts with no characters used to be dropped silently here (all but the first), and the next
                        // save made it permanent. They are kept now (roadmap P5, decision #14); pruning, if wanted, should
                        // be an explicit and logged maintenance job.
                        AccountList.Add(NextAccount);
                        CharacterList.AddRange(AccountList[TrueAccount].Characters);
                        if (LoadVersion > 98 && LoadVersion < 103)
                            AccountList[TrueAccount].Characters.ForEach(character => HeroList.AddRange(character.Heroes));
                        TrueAccount++;
                    }

                    foreach (var auction in Auctions)
                    {
                        auction.SellerInfo.AccountInfo.Auctions.Remove(auction);
                    }

                    Auctions.Clear();

                    NextAuctionID = reader.ReadUInt64();

                    count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var auction = new AuctionInfo(reader, LoadVersion, LoadCustomVersion);

                        if (!BindItem(auction.Item) || !BindCharacter(auction)) continue;

                        Auctions.AddLast(auction);
                        auction.SellerInfo.AccountInfo.Auctions.AddLast(auction);
                    }

                    NextMailID = reader.ReadUInt64();

                    if (LoadVersion <= 80)
                    {
                        count = reader.ReadInt32();
                        for (var i = 0; i < count; i++)
                        {
                            var mail = new MailInfo(reader, LoadVersion, LoadCustomVersion);

                            mail.RecipientInfo = GetCharacterInfo(mail.RecipientIndex);

                            if (mail.RecipientInfo != null)
                            {
                                mail.RecipientInfo.Mail.Add(mail); //add to players inbox
                            }
                        }
                    }

                    if (LoadVersion >= 63)
                    {
                        var logCount = reader.ReadInt32();
                        for (var i = 0; i < logCount; i++)
                        {
                            GameshopLog.Add(reader.ReadInt32(), reader.ReadInt32());
                        }

                        if (ResetGS) ClearGameshopLog();
                    }

                    if (LoadVersion >= 68)
                    {
                        var saveCount = reader.ReadInt32();
                        for (var i = 0; i < saveCount; i++)
                        {
                            var saved = new RespawnSave(reader);
                            foreach (var respawn in SavedSpawns)
                            {
                                if (respawn.Info.RespawnIndex != saved.RespawnIndex) continue;

                                respawn.NextSpawnTick = saved.NextSpawnTick;

                                if (!saved.Spawned || respawn.Info.Count * SpawnMultiplier <= respawn.Count)
                                {
                                    continue;
                                }

                                var mobcount = respawn.Info.Count * SpawnMultiplier - respawn.Count;
                                for (var j = 0; j < mobcount; j++)
                                {
                                    respawn.Spawn();
                                }
                            }
                        }
                    }

                    MarketSales.Clear();
                    if (LoadVersion >= 119)
                    {
                        var salesCount = reader.ReadInt32();
                        for (var i = 0; i < salesCount; i++)
                        {
                            var itemIndex = reader.ReadInt32();
                            MarketSales[itemIndex] = new MarketSaleHistory(reader);
                        }
                    }
                }

                BuildSentMailIndex(); // phase 2.6: Sent folder index from the loaded mailboxes
            }

            // P9-12: every list was replaced; the cache is rebuilt from a fresh save, never from the file's bytes (the load
            // drops archived characters, unbindable items and orphan quests, so those bytes differ).
            AccountSaveCache.InvalidateAll();
        }

        public void LoadGuilds()
        {
            lock (LoadLock)
            {
                var count = 0;

                GuildList.Clear();
                // In-process restarts used to keep the previous run's GuildObjects (StopEnvir never cleared them, and the
                // GuildObject ctor skips an index that is already listed), so the next SaveGuilds wrote stale guild data over
                // the freshly loaded files (roadmap P5 known defect).
                Guilds.Clear();

                for (var i = 0; i < GuildCount; i++)
                {
                    GuildInfo guildInfo;
                    if (!File.Exists(Path.Combine(Settings.GuildPath, i + ".mgd"))) continue;

                    using (var stream = File.OpenRead(Path.Combine(Settings.GuildPath, i + ".mgd")))
                    {
                        using var reader = new BinaryReader(stream);
                        guildInfo = new GuildInfo(reader);
                    }

                    GuildList.Add(guildInfo);

                    new GuildObject(guildInfo);

                    count++;
                }

                if (count != GuildCount) GuildCount = count;
            }
        }

        public void LoadConquests()
        {
            lock (LoadLock)
            {
                Conquests.Clear();
                ConquestList.Clear();

                for (var i = 0; i < ConquestInfoList.Count; i++)
                {
                    ConquestObject newConquest;
                    ConquestGuildInfo conquestGuildInfo;
                    var tempMap = GetMap(ConquestInfoList[i].MapIndex);

                    if (tempMap == null) continue;

                    if (File.Exists(Path.Combine(Settings.ConquestsPath, ConquestInfoList[i].Index + ".mcd")))
                    {
                        using (var stream = File.OpenRead(Path.Combine(Settings.ConquestsPath, ConquestInfoList[i].Index + ".mcd")))
                        {
                            using var reader = new BinaryReader(stream);
                            conquestGuildInfo = new ConquestGuildInfo(reader) { Info = ConquestInfoList[i] };
                        }

                        newConquest = new ConquestObject(conquestGuildInfo)
                        {
                            ConquestMap = tempMap
                        };

                        for (var k = 0; k < Guilds.Count; k++)
                        {
                            if (conquestGuildInfo.Owner != Guilds[k].Guildindex) continue;
                            newConquest.Guild = Guilds[k];
                            Guilds[k].Conquest = newConquest;
                        }
                    }
                    else
                    {
                        conquestGuildInfo = new ConquestGuildInfo { Info = ConquestInfoList[i], NeedSave = true };
                        newConquest = new ConquestObject(conquestGuildInfo)
                        {
                            ConquestMap = tempMap
                        };
                    }

                    ConquestList.Add(conquestGuildInfo);
                    Conquests.Add(newConquest);
                    tempMap.Conquest.Add(newConquest);

                    newConquest.Bind();
                }
            }
        }

        private void LoadGTInfo()
        {
            foreach (var gt in GTMapList)
            {
                var Guild = GuildList.FirstOrDefault(x => x.GTIndex == gt.Index);
                if (Guild != null)
                {
                    gt.Owner = Guild.Name;
                    if (Guild.Ranks.Count > 0 && Guild.Ranks[0] != null && Guild.Ranks[0].Members.Count > 0 && Guild.Ranks[0].Members[0] != null)
                        gt.Leader = Guild.Ranks[0].Members[0].Name;
                    gt.Price = 0;
                    gt.Days = (Now - Guild.GTRent).Days;
                }
            }
        }

        public void LoadDisabledChars()
        {
            DisabledCharNames.Clear();

            var path = Path.Combine(Settings.EnvirPath, "DisabledChars.txt");

            if (!File.Exists(path))
            {
                File.WriteAllText(path, "");
            }
            else
            {
                var lines = File.ReadAllLines(path);

                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith(";") || string.IsNullOrWhiteSpace(lines[i])) continue;
                    DisabledCharNames.Add(lines[i].ToUpper());
                }
            }
        }

        public void LoadLineMessages()
        {
            LineMessages.Clear();

            var path = Path.Combine(Settings.EnvirPath, "LineMessage.txt");

            if (!File.Exists(path))
            {
                File.WriteAllText(path, "");
            }
            else
            {
                var lines = File.ReadAllLines(path);

                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith(";") || string.IsNullOrWhiteSpace(lines[i])) continue;
                    LineMessages.Add(lines[i]);
                }
            }
        }

        private bool BindCharacter(AuctionInfo auction)
        {
            bool bound = false;

            for (int i = 0; i < CharacterList.Count; i++)
            {
                if (CharacterList[i].Index == auction.SellerIndex)
                {
                    auction.SellerInfo = CharacterList[i];
                    bound = true;
                }

                else if (CharacterList[i].Index == auction.CurrentBuyerIndex)
                {
                    auction.CurrentBuyerInfo = CharacterList[i];
                    bound = true;
                }
            }

            return bound;
        }

        #region WebAdmin hooks (MirCraft web admin)

        private static volatile bool _systemDataStale;

        /// <summary>True once Envir.Edit.SaveDB() ran after Envir.Main last loaded its DB: Main's design data is older than the store until the next Start/Reboot (informational since roadmap P7: Main never writes design data).</summary>
        public static bool SystemDataStale => _systemDataStale;

        /// <summary>Main's RespawnTick.CurrentTickcounter, published by the game thread after RespawnTick.Process(); -1 until then (F4).</summary>
        private static long _publishedRespawnTick = -1;

        internal static long PublishedRespawnTick => Interlocked.Read(ref _publishedRespawnTick);

        private static int _editorFormsOpen;

        /// <summary>
        /// WinForms database editors currently open (SMain counts them around ShowDialog). They edit Envir.Edit without the web admin's
        /// EditLock and save it when they close, so LiveDb refuses batches (409) while one is open.
        /// </summary>
        public static int EditorFormsOpen => Volatile.Read(ref _editorFormsOpen);

        public static void EditorFormOpened() => Interlocked.Increment(ref _editorFormsOpen);

        public static void EditorFormClosed()
        {
            if (Interlocked.Decrement(ref _editorFormsOpen) < 0) Interlocked.Exchange(ref _editorFormsOpen, 0);
        }

        private readonly ConcurrentQueue<(Action Run, Action<Exception> Dropped)> _adminActions = new ConcurrentQueue<(Action Run, Action<Exception> Dropped)>();
        private readonly object _adminQueueLock = new object();
        private volatile bool _adminQueueOpen;
        private volatile bool _saveNowRequested;

        private const int AdminActionsPerTick = 64;
        private const long AdminActionsBudgetMs = 10;

        /// <summary>True while the game loop is running and draining queued admin actions.</summary>
        public bool AdminQueueOpen => _adminQueueOpen;

        /// <summary>True while the WorkLoop thread exists (starting, running or stopping).</summary>
        public bool WorkLoopAlive => _thread != null;

        /// <summary>
        /// Queues an action to run on the game thread (right after Process() in WorkLoop).
        /// Returns false (and never runs it) when the world loop is not running.
        /// <paramref name="dropped"/> is invoked if the loop exits before the action ran.
        /// </summary>
        public bool QueueAdminAction(Action action, Action<Exception> dropped = null)
        {
            if (action == null) return false;

            lock (_adminQueueLock)
            {
                if (!_adminQueueOpen || !Running) return false;
                _adminActions.Enqueue((action, dropped));
            }

            WakeLoop();
            return true;
        }

        /// <summary>Makes the next game tick run the periodic save block (accounts, DB, guilds, goods, conquests).</summary>
        public void RequestSaveNow()
        {
            AccountSaveCache.InvalidateAll(); // P9-12 (review H1): an operator save serialises every account
            _saveNowRequested = true;
        }

        private void OpenAdminQueue()
        {
            _saveNowRequested = false;

            lock (_adminQueueLock)
                _adminQueueOpen = true;
        }

        private void DrainAdminActions()
        {
            if (_adminActions.IsEmpty) return;

            var start = Stopwatch.ElapsedMilliseconds;
            var count = 0;

            while (count < AdminActionsPerTick && _adminActions.TryDequeue(out var item))
            {
                count++;

                try
                {
                    item.Run();
                }
                catch (Exception ex)
                {
                    MessageQueue.Enqueue(ex);
                }

                if (Stopwatch.ElapsedMilliseconds - start >= AdminActionsBudgetMs) break;
            }
        }

        private void CloseAdminQueue()
        {
            lock (_adminQueueLock)
                _adminQueueOpen = false;

            while (_adminActions.TryDequeue(out var item))
            {
                if (item.Dropped == null) continue;

                try
                {
                    item.Dropped(new global::Server.WebAdmin.WorldNotRunningException());
                }
                catch (Exception ex)
                {
                    MessageQueue.Enqueue(ex);
                }
            }
        }

        #endregion

        public void Start()
        {
            if (Running || _thread != null) return;

            Running = true;

            _thread = new Thread(WorkLoop) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            Running = false;

            lock (_locker)
            {
                // changing a blocking condition. (this makes the threads wake up!)
                Monitor.PulseAll(_locker);
            }

            //simply intterupt all the mob threads if they are running (will give an invisible error on them but fastest way of getting rid of them on shutdowns)
            for (var i = 1; i < MobThreading.Length; i++)
            {
                if (MobThreads[i] != null)
                {
                    MobThreads[i].EndTime = Time + 9999;
                }
                if (MobThreading[i] != null &&
                    MobThreading[i].ThreadState != System.Threading.ThreadState.Stopped && MobThreading[i].ThreadState != System.Threading.ThreadState.Unstarted)
                {
                    MobThreading[i].Interrupt();
                }
            }

            http?.Stop();

            while (_thread != null)
                Thread.Sleep(1);
        }

        public void Reboot()
        {
            new Thread(() =>
            {
                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.ServerRebooting));
                Stop();
                Start();
            }).Start();
        }

        private readonly AutoResetEvent _loopSignal = new AutoResetEvent(false);
        private bool _timerResolutionSet;

        /// <summary>Wakes a parked WorkLoop (roadmap P6). Safe from any thread; cheap when the loop is not parked.</summary>
        public void WakeLoop() => _loopSignal.Set();

        public void UpdateIPBlock(string ipAddress, TimeSpan value)
        {
            if (IsLoadTestExempt(ipAddress)) return; // [LoadTest] ExemptLoopback (roadmap P6), scratch servers only
            IPBlocks[ipAddress] = Now.Add(value);
        }

        /// <summary>
        /// The client IP as a string (X1-30). IPv4 (also IPv4-mapped IPv6) gives the same "a.b.c.d" as the old
        /// RemoteEndPoint.ToString().Split(':')[0]; IPv6 now gives the real address instead of "[" for every client.
        /// </summary>
        public static string EndpointIp(System.Net.EndPoint endPoint)
        {
            if (endPoint is IPEndPoint ip)
            {
                var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
                return address.ToString();
            }
            return endPoint?.ToString() ?? string.Empty;
        }

        /// <summary>Drops expired IP blocks and empty connection logs (X1-30: both grew for the life of the process).</summary>
        public void PruneIpTables()
        {
            var now = Now;
            foreach (var pair in IPBlocks)
                if (pair.Value < now) IPBlocks.TryRemove(pair.Key, out _);
            foreach (var pair in ConnectionLogs)
            {
                var log = pair.Value;
                for (var i = log.AccountsMade.Count - 1; i >= 0; i--)
                    if (log.AccountsMade[i] + 60 * 60 * 1000 < Time) log.AccountsMade.RemoveAt(i);
                for (var i = log.CharactersMade.Count - 1; i >= 0; i--)
                    if (log.CharactersMade[i] + 60 * 60 * 1000 < Time) log.CharactersMade.RemoveAt(i);
                if (log.AccountsMade.Count == 0 && log.CharactersMade.Count == 0) ConnectionLogs.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>
        /// X1-19: per-connection login throttle. After MirConnection.MaxFailedLogins failures the connection is closed and the
        /// IP blocked for a minute (UpdateIPBlock honours the load-test loopback exemption), so password spraying and account
        /// lock-out spam run at a few attempts per minute per IP instead of many per packet read.
        /// </summary>
        private void CountFailedLogin(MirConnection c)
        {
            if (++c.FailedLogins < MirConnection.MaxFailedLogins) return;
            UpdateIPBlock(c.IPAddress, TimeSpan.FromMinutes(1));
            c.Disconnecting = true;
        }

        /// <summary>Wrong passwords an account may collect before the next one locks it (the literal 5 in CompleteLogin / ChangePassword).</summary>
        private const int AccountWrongPasswordLimit = 5;

        /// <summary>
        /// Phase 2.4: S.LoginFailed, enqueued right after (never instead of) S.Login and BEFORE CountFailedLogin counts this
        /// failure. ConnectionTriesLeft = failures this connection may still make before it is closed (0 = this one closes it,
        /// and LockedUntil is then the end of the one-minute IP block, 0 for a load-test-exempt address). Game thread; failure path only.
        /// </summary>
        private S.LoginFailed LoginFailedPacket(MirConnection c, byte result, int accountTriesLeft)
        {
            int connectionTriesLeft = Math.Max(0, MirConnection.MaxFailedLogins - (c.FailedLogins + 1));
            return new S.LoginFailed
            {
                Result = result,
                AccountTriesLeft = accountTriesLeft,
                ConnectionTriesLeft = connectionTriesLeft,
                // same condition as UpdateIPBlock: [LoadTest] ExemptLoopback addresses are closed but never blocked
                LockedUntil = connectionTriesLeft == 0 && !IsLoadTestExempt(c.IPAddress) ? Now.AddMinutes(1).ToBinary() : 0
            };
        }

        /// <summary>
        /// Phase 2.4: S.SelectDetails for the characters GetSelectInfo lists (not deleted, at most Globals.MaxCharacterCount),
        /// in the same order. Game thread; sent after LoginSuccess, LogOutSuccess and NewCharacterSuccess only.
        /// </summary>
        public S.SelectDetails SelectDetailsPacket(AccountInfo account)
        {
            var p = new S.SelectDetails { AccountGold = account.Gold };

            for (int i = 0; i < account.Characters.Count; i++)
            {
                CharacterInfo info = account.Characters[i];
                if (info.Deleted) continue;

                string guildName = string.Empty;
                if (info.GuildIndex >= 0)
                {
                    GuildObject guild = GetGuild(info.GuildIndex);
                    if (guild != null) guildName = guild.Name ?? string.Empty;
                }

                string mapTitle = string.Empty;
                for (int m = 0; m < MapInfoList.Count; m++)
                {
                    if (MapInfoList[m].Index != info.CurrentMapIndex) continue;
                    mapTitle = MapInfoList[m].Title ?? string.Empty;
                    break;
                }

                p.Characters.Add(new ClientSelectDetail
                {
                    Index = info.Index,
                    GuildName = guildName,
                    MapTitle = mapTitle,
                    PlayTime = info.PlayTime
                });

                if (p.Characters.Count >= Globals.MaxCharacterCount) break;
            }

            return p;
        }

        /// <summary>True for loopback addresses when [LoadTest] ExemptLoopback is on (off by default).</summary>
        public static bool IsLoadTestExempt(string ipAddress)
        {
            if (!Settings.LoadTestExemptLoopback || string.IsNullOrEmpty(ipAddress)) return false;
            // "[" is what the IPv4-style Split(':') leaves of an IPv6 endpoint such as [::1]:port (X1-30).
            return ipAddress.StartsWith("127.", StringComparison.Ordinal) || ipAddress == "::1" || ipAddress == "[";
        }

        private void StartEnvir()
        {
            Players.Clear();
            StartPoints.Clear();
            StartItems.Clear();
            MapList.Clear();
            GTMapList.Clear();
            GameshopLog.Clear();
            MarketSales.Clear();
            CustomCommands.Clear();
            Heroes.Clear();
            MonsterCount = 0;
            if (this == Main) Server.MirObjects.Monsters.StoneTrap.ResetLiveCount();

            if (this == Main) _systemDataStale = false;

            // Fail closed (roadmap P2): never run the world on empty or half-loaded system data.
            if (!LoadDB())
                throw new InvalidOperationException("[Load] Server.MirDB could not be loaded (see the message above); the world will not start.");

            BuffInfoList.Clear();
            foreach (var buff in BuffInfo.Load())
            {
                BuffInfoList.Add(buff);
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.BuffsLoaded), BuffInfoList.Count));

            RecipeInfoList.Clear();
            foreach (var recipe in DesignFiles.EnumerateFiles(Settings.RecipePath, "*.txt", SearchOption.TopDirectoryOnly)
                         .Select(path => Path.GetFileNameWithoutExtension(path))
                         .ToArray())
            {
                RecipeInfoList.Add(new RecipeInfo(recipe));
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.RecipesLoaded), RecipeInfoList.Count));

            for (var i = 0; i < MapInfoList.Count; i++)
            {
                // Call CreateMap(), which adds the map to Envir.MapList
                MapInfoList[i].CreateMap();

                // Fetch the created map from Envir.MapList
                Map map = MapList.FirstOrDefault(m => m.Info == MapInfoList[i]);

                if (map != null)
                {
                    if (MapInfoList[i].GT)
                    {
                        GTMap gt = GTMapList.FirstOrDefault(x => x.Index == MapInfoList[i].GTIndex);
                        if (gt != null)
                        {
                            gt.Maps.Add(map);
                        }
                        else
                        {
                            var GT = new GTMap()
                            {
                                Index = MapInfoList[i].GTIndex,
                                Name = MapInfoList[i].Title,
                                Price = Settings.BuyGTGold,
                                Days = 0,
                                Begin = 0,
                                Leader = "None",
                                Owner = "None",
                            };
                            GT.Maps.Add(map);

                            GTMapList.Add(GT);
                        }
                    }
                }
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.MapsLoaded), MapInfoList.Count));

            for (var i = 0; i < ItemInfoList.Count; i++)
            {
                if (ItemInfoList[i].StartItem)
                {
                    StartItems.Add(ItemInfoList[i]);
                }
            }

            ReloadDrops();

            LoadDisabledChars();
            LoadLineMessages();

            if (DragonInfo.Enabled)
            {
                DragonSystem = new Dragon(DragonInfo);
                if (DragonSystem != null)
                {
                    if (DragonSystem.Load()) DragonSystem.Info.LoadDrops();
                }

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.DragonLoaded));
            }

            DefaultNPC = NPCScript.GetOrAdd((uint)Random.Next(1000000, 1999999), Settings.DefaultNPCFilename, NPCScriptType.AutoPlayer);
            MonsterNPC = NPCScript.GetOrAdd((uint)Random.Next(2000000, 2999999), Settings.MonsterNPCFilename, NPCScriptType.AutoMonster);
            RobotNPC = NPCScript.GetOrAdd((uint)Random.Next(3000000, 3999999), Settings.RobotNPCFilename, NPCScriptType.Robot);

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.EnvirStarted));           

        }

        
        private void StartNetwork()
        {
            Connections.Clear();

            LoadAccounts();
            global::Server.WebAdmin.WebAdminHooks.RaiseAccountsLoaded();

            LoadGuilds();

            LoadConquests();
            LoadGTInfo();

            _listener = new TcpListener(IPAddress.Parse(Settings.IPAddress), Settings.Port);
            _listener.Start();
            _listener.BeginAcceptTcpClient(Connection, null);

            if (StatusPortEnabled)
            {
                _StatusPort = new TcpListener(IPAddress.Parse(Settings.IPAddress), Settings.StatusPort);
                _StatusPort.Start();
                _StatusPort.BeginAcceptTcpClient(StatusConnection, null);
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.NetworkStarted));
        }

        private void StopEnvir()
        {
            SaveGoods(true);

            MapList.Clear();
            StartPoints.Clear();
            StartItems.Clear();
            Objects.Clear();
            Players.Clear();
            Heroes.Clear();
            GTMapList.Clear();

            CleanUp();

            GC.Collect();

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.EnvirStopped));
        }
        private void StopNetwork()
        {
            _listener.Stop();
            lock (Connections)
            {
                for (var i = Connections.Count - 1; i >= 0; i--)
                    Connections[i].SendDisconnect(0);
            }

            if (StatusPortEnabled)
            {
                _StatusPort.Stop();
                for (var i = StatusConnections.Count - 1; i >= 0; i--)
                    StatusConnections[i].SendDisconnect();
            }

            var expire = Time + 5000;

            while (Connections.Count != 0 && Stopwatch.ElapsedMilliseconds < expire)
            {
                Time = Stopwatch.ElapsedMilliseconds;

                for (var i = Connections.Count - 1; i >= 0; i--)
                {
                    if (i >= Connections.Count) continue;
                    var connection = Connections[i];
                    // Isolation (roadmap P5) also during the shutdown drain: one bad connection must not abort the drain.
                    try
                    {
                        connection.Process();
                    }
                    catch (Exception ex)
                    {
                        WorldErrors.Record(WorldErrors.Describe(connection), ex);
                        IsolateConnection(connection);
                    }
                }
                MirConnection.EndEncodePass();

                Thread.Sleep(1);
            }


            Connections.Clear();

            expire = Time + 10000;
            while (StatusConnections.Count != 0 && Stopwatch.ElapsedMilliseconds < expire)
            {
                Time = Stopwatch.ElapsedMilliseconds;

                for (var i = StatusConnections.Count - 1; i >= 0; i--)
                    StatusConnections[i].Process();

                Thread.Sleep(1);
            }


            StatusConnections.Clear();
            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.NetworkStopped));
        }

        private void CleanUp()
        {
            for (var i = 0; i < CharacterList.Count; i++)
            {
                var info = CharacterList[i];

                if (info.Deleted)
                {
                    #region Mentor Cleanup
                    if (info.Mentor > 0)
                    {
                        var mentor = GetCharacterInfo(info.Mentor);

                        if (mentor != null)
                        {
                            mentor.Mentor = 0;
                            mentor.MentorExp = 0;
                            mentor.IsMentor = false;
                        }

                        info.Mentor = 0;
                        info.MentorExp = 0;
                        info.IsMentor = false;
                    }
                    #endregion

                    #region Marriage Cleanup
                    if (info.Married > 0)
                    {
                        var Lover = GetCharacterInfo(info.Married);

                        info.Married = 0;
                        info.MarriedDate = Now;

                        Lover.Married = 0;
                        Lover.MarriedDate = Now;
                        if (Lover.Equipment[(int)EquipmentSlot.RingL] != null)
                            Lover.Equipment[(int)EquipmentSlot.RingL].WeddingRing = -1;
                    }
                    #endregion
                }

                if (info.Mail.Count > Settings.MailCapacity)
                {
                    for (var j = info.Mail.Count - 1 - (int)Settings.MailCapacity; j >= 0; j--)
                    {
                        if (info.Mail[j].DateOpened > Now && info.Mail[j].Collected && info.Mail[j].Items.Count == 0 && info.Mail[j].Gold == 0)
                        {
                            RemoveSentMail(info.Mail[j]);
                            info.Mail.Remove(info.Mail[j]);
                        }
                    }
                }
            }
        }

        private void Connection(IAsyncResult result)
        {
            try
            {
                if (!Running || !_listener.Server.IsBound)
                {
                    // complete the aborted accept so its exception is observed (no crash file on every world stop)
                    try { _listener.EndAcceptTcpClient(result)?.Close(); } catch { }
                    return;
                }
            }
            catch (Exception e)
            {
                MessageQueue.Enqueue(e.ToString());
            }

            try
            {
                var tempTcpClient = _listener.EndAcceptTcpClient(result);

                bool connected = false;
                var ipAddress = EndpointIp(tempTcpClient.Client.RemoteEndPoint);

                if (!IPBlocks.TryGetValue(ipAddress, out DateTime banDate) || banDate < Now)
                {
                    int count = 0;

                    for (int i = 0; i < Connections.Count; i++)
                    {
                        var connection = Connections[i];

                        if (!connection.Connected || connection.IPAddress != ipAddress)
                            continue;

                        count++;
                    }

                    if (count >= Settings.MaxIP && !IsLoadTestExempt(ipAddress))
                    {
                        UpdateIPBlock(ipAddress, TimeSpan.FromSeconds(Settings.IPBlockSeconds));

                        MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.IpAddressDisconnectedTooManyConnections), ipAddress));
                    }
                    else
                    {
                        var tempConnection = new MirConnection(++_sessionID, tempTcpClient);
                        if (tempConnection.Connected)
                        {
                            connected = true;
                            lock (Connections)
                                Connections.Add(tempConnection);
                            WakeLoop();
                        }
                    }
                }

                if (!connected)
                    tempTcpClient.Close();
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue(ex);
            }
            finally
            {
                while (Connections.Count >= Settings.MaxUser)
                    Thread.Sleep(1);

                if (Running && _listener.Server.IsBound)
                    _listener.BeginAcceptTcpClient(Connection, null);
            }
        }

        private void StatusConnection(IAsyncResult result)
        {
            if (!Running || !_StatusPort.Server.IsBound)
            {
                try { _StatusPort.EndAcceptTcpClient(result)?.Close(); } catch { }
                return;
            }

            try
            {
                var tempTcpClient = _StatusPort.EndAcceptTcpClient(result);
                lock (StatusConnections)
                    StatusConnections.Add(new MirStatusConnection(tempTcpClient));
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue(ex);
            }
            finally
            {
                while (StatusConnections.Count >= 5) //dont allow to many status port connections it's just an abuse thing
                    Thread.Sleep(1);

                if (Running && _StatusPort.Server.IsBound)
                    _StatusPort.BeginAcceptTcpClient(StatusConnection, null);
            }
        }

        public void NewAccount(ClientPackets.NewAccount p, MirConnection c)
        {
            if (!Settings.AllowNewAccount)
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 0 });
                return;
            }


            if (ConnectionLogs.TryGetValue(c.IPAddress, out MirConnectionLog currentlog))
            {
                if (currentlog.AccountsMade.Count > 2 && !IsLoadTestExempt(c.IPAddress))
                {
                    IPBlocks[c.IPAddress] = Now.AddHours(24);
                    c.Enqueue(new ServerPackets.NewAccount { Result = 0 });
                    return;
                }
                currentlog.AccountsMade.Add(Time);
                for (int i = 0; i < currentlog.AccountsMade.Count; i++)
                {
                    if ((currentlog.AccountsMade[i] + 60 * 60 * 1000) < Time)
                    {
                        currentlog.AccountsMade.RemoveAt(i);
                        break;
                    }
                }
            }
            else
            {
                ConnectionLogs[c.IPAddress] = new MirConnectionLog() { IPAddress = c.IPAddress };
            }


            if (!AccountIDReg.IsMatch(p.AccountID))
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 1 });
                return;
            }

            if (!PasswordReg.IsMatch(p.Password))
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 2 });
                return;
            }
            if (!string.IsNullOrWhiteSpace(p.EMailAddress) && !EMailReg.IsMatch(p.EMailAddress) ||
                p.EMailAddress.Length > 50)
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 3 });
                return;
            }

            if (!string.IsNullOrWhiteSpace(p.UserName) && p.UserName.Length > 20)
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 4 });
                return;
            }

            if (!string.IsNullOrWhiteSpace(p.SecretQuestion) && p.SecretQuestion.Length > 30)
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 5 });
                return;
            }

            if (!string.IsNullOrWhiteSpace(p.SecretAnswer) && p.SecretAnswer.Length > 30)
            {
                c.Enqueue(new ServerPackets.NewAccount { Result = 6 });
                return;
            }

            if (Settings.PasswordHashV2)
            {
                // Roadmap P8: a v2 hash costs tens of ms, so hash on a worker and add the account on the game thread.
                if (c.LoginPending) return;
                c.LoginPending = true;
                var plain = p.Password;
                Task.Run(() =>
                {
                    var salt = Utils.Crypto.GenerateSalt();
                    var hash = Utils.Crypto.HashPasswordV2(plain, salt);
                    if (!QueueAdminAction(() => CompleteNewAccount(p, c, salt, hash), null))
                        c.LoginPending = false;
                });
                return;
            }

            lock (AccountLock)
            {
                if (AccountExists(p.AccountID))
                {
                    c.Enqueue(new ServerPackets.NewAccount { Result = 7 });
                    return;
                }

                AccountList.Add(new AccountInfo(p) { Index = ++NextAccountID, CreationIP = c.IPAddress });


                c.Enqueue(new ServerPackets.NewAccount { Result = 8 });
            }
        }

        /// <summary>Game-thread continuation of <see cref="NewAccount"/> after the off-thread v2 hash (roadmap P8).</summary>
        private void CompleteNewAccount(ClientPackets.NewAccount p, MirConnection c, byte[] salt, string hash)
        {
            c.LoginPending = false;
            if (!c.Connected) return;

            lock (AccountLock)
            {
                if (AccountExists(p.AccountID)) // created by someone else meanwhile
                {
                    c.Enqueue(new ServerPackets.NewAccount { Result = 7 });
                    return;
                }

                AccountList.Add(new AccountInfo(p, salt, hash) { Index = ++NextAccountID, CreationIP = c.IPAddress });
                c.Enqueue(new ServerPackets.NewAccount { Result = 8 });
            }
        }

        public int HTTPNewAccount(ClientPackets.NewAccount p, string ip)
        {
            if (!Settings.AllowNewAccount)
            {
                return 0;
            }

            if (!AccountIDReg.IsMatch(p.AccountID))
            {
                return 1;
            }

            if (!PasswordReg.IsMatch(p.Password))
            {
                return 2;
            }
            if (!string.IsNullOrWhiteSpace(p.EMailAddress) && !EMailReg.IsMatch(p.EMailAddress) ||
                p.EMailAddress.Length > 50)
            {
                return 3;
            }

            if (!string.IsNullOrWhiteSpace(p.UserName) && p.UserName.Length > 20)
            {
                return 4;
            }

            if (!string.IsNullOrWhiteSpace(p.SecretQuestion) && p.SecretQuestion.Length > 30)
            {
                return 5;
            }

            if (!string.IsNullOrWhiteSpace(p.SecretAnswer) && p.SecretAnswer.Length > 30)
            {
                return 6;
            }

            lock (AccountLock)
            {
                if (AccountExists(p.AccountID))
                {
                    return 7;
                }

                AccountList.Add(new AccountInfo(p) { Index = ++NextAccountID, CreationIP = ip });
                return 8;
            }
        }

        public void ChangePassword(ClientPackets.ChangePassword p, MirConnection c)
        {
            if (!Settings.AllowChangePassword)
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 0 });
                return;
            }

            if (!AccountIDReg.IsMatch(p.AccountID))
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 1 });
                return;
            }

            if (!PasswordReg.IsMatch(p.CurrentPassword))
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 2 });
                return;
            }

            if (!PasswordReg.IsMatch(p.NewPassword))
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 3 });
                return;
            }

            var account = GetAccount(p.AccountID);

            if (account == null)
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 4 });
                return;
            }

            if (account.Banned)
            {
                if (account.ExpiryDate > Now)
                {
                    c.Enqueue(new ServerPackets.ChangePasswordBanned { Reason = account.BanReason, ExpiryDate = account.ExpiryDate });
                    return;
                }
                account.Banned = false;
                MarkAccountDirty(account);
            }
            // P9-12 (review L4): marked only on a real change, so unauthenticated clients cannot churn the cache
            if (!string.IsNullOrEmpty(account.BanReason) || account.ExpiryDate != DateTime.MinValue) MarkAccountDirty(account);
            account.BanReason = string.Empty;
            account.ExpiryDate = DateTime.MinValue;

            if (Settings.PasswordHashV2 || Utils.Crypto.IsV2(account.Password))
            {
                // Roadmap P8: a v2 check or hash costs tens of ms, so both run on a worker; the result is applied on the game thread.
                if (c.LoginPending) return;
                c.LoginPending = true;
                var current = p.CurrentPassword;
                var next = p.NewPassword;
                var salt = account.Salt;
                var stored = account.Password;
                Task.Run(() =>
                {
                    var ok = Utils.Crypto.Verify(current, salt, stored);
                    var newSalt = ok ? Utils.Crypto.GenerateSalt() : null;
                    var newHash = ok ? Utils.Crypto.CreateHash(next, newSalt) : null;
                    if (!QueueAdminAction(() => CompleteChangePassword(c, account, stored, newSalt, newHash), null))
                        c.LoginPending = false;
                });
                return;
            }

            if (!Utils.Crypto.Verify(p.CurrentPassword, account.Salt, account.Password)) // v1 or v2 (roadmap P8)
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 5 });
                return;
            }

            MarkAccountDirty(account); // P9-12: the account may be offline (change password from the login screen)
            account.Password = p.NewPassword;
            account.RequirePasswordChange = false;
            c.Enqueue(new ServerPackets.ChangePassword { Result = 6 });
        }

        /// <summary>Game-thread continuation of <see cref="ChangePassword"/> after the off-thread check and hash (roadmap P8).</summary>
        private void CompleteChangePassword(MirConnection c, AccountInfo account, string checkedHash, byte[] newSalt, string newHash)
        {
            c.LoginPending = false;
            if (!c.Connected) return;

            // Wrong current password, or the password changed meanwhile (another client, web admin): refuse, the client may retry.
            if (newHash == null || !ReferenceEquals(account.Password, checkedHash))
            {
                c.Enqueue(new ServerPackets.ChangePassword { Result = 5 });
                return;
            }

            MarkAccountDirty(account); // P9-12
            account.SetPasswordHash(newSalt, newHash);
            account.RequirePasswordChange = false;
            c.Enqueue(new ServerPackets.ChangePassword { Result = 6 });
        }
        public void Login(ClientPackets.Login p, MirConnection c)
        {
            if (!Settings.AllowLogin)
            {
                c.Enqueue(new ServerPackets.Login { Result = 0 });
                return;
            }

            if (!AccountIDReg.IsMatch(p.AccountID))
            {
                c.Enqueue(new ServerPackets.Login { Result = 1 });
                return;
            }

            if (!PasswordReg.IsMatch(p.Password))
            {
                c.Enqueue(new ServerPackets.Login { Result = 2 });
                return;
            }
            var account = GetAccount(p.AccountID);

            if (account == null)
            {
                global::Server.WebAdmin.WebAdminHooks.RaiseLoginAttempt(p.AccountID, c.IPAddress, "no-account");
                c.Enqueue(new ServerPackets.Login { Result = 3 });
                c.Enqueue(LoginFailedPacket(c, 3, -1));
                CountFailedLogin(c);
                return;
            }

            if (account.Banned)
            {
                if (account.ExpiryDate > Now)
                {
                    global::Server.WebAdmin.WebAdminHooks.RaiseLoginAttempt(account.AccountID, c.IPAddress, "banned");
                    c.Enqueue(new ServerPackets.LoginBanned
                    {
                        Reason = account.BanReason,
                        ExpiryDate = account.ExpiryDate
                    });
                    return;
                }
                account.Banned = false;
                MarkAccountDirty(account);
            }
            // P9-12 (review L4): marked only on a real change, so unauthenticated clients cannot churn the cache
            if (!string.IsNullOrEmpty(account.BanReason) || account.ExpiryDate != DateTime.MinValue) MarkAccountDirty(account);
            account.BanReason = string.Empty;
            account.ExpiryDate = DateTime.MinValue;

            // Roadmap P8: the password check runs on a worker (v2 hashes cost tens of ms) and the rest of the login continues
            // on the game thread through the admin queue. The worker only gets copies (password, salt, stored hash).
            if (c.LoginPending) return; // one check at a time per connection
            c.LoginPending = true;
            var plain = p.Password;
            var salt = account.Salt;
            var stored = account.Password;
            Task.Run(() =>
            {
                var ok = Utils.Crypto.Verify(plain, salt, stored);
                var upgraded = ok && Utils.Crypto.NeedsUpgrade(stored) ? Utils.Crypto.HashPasswordV2(plain, salt) : null;
                if (!QueueAdminAction(() => CompleteLogin(c, account, stored, ok, upgraded), null))
                    c.LoginPending = false; // world stopping: nothing to continue
            });
        }

        /// <summary>Game-thread continuation of <see cref="Login"/> after the off-thread password check (roadmap P8).</summary>
        private void CompleteLogin(MirConnection c, AccountInfo account, string checkedHash, bool passwordOk, string upgradedHash)
        {
            c.LoginPending = false;
            if (!c.Connected || c.Stage != GameStage.Login || c.Account != null) return;   // disconnected or moved on meanwhile
            if (!ReferenceEquals(account.Password, checkedHash)) passwordOk = false;          // password changed meanwhile: retry
            if (passwordOk && upgradedHash != null)                                          // v1 -> v2 on a successful login
            {
                MarkAccountDirty(account); // P9-12: the account may stay offline (login gate / RequirePasswordChange below)
                account.SetPasswordHash(upgradedHash);
            }
            if (account.Banned && account.ExpiryDate > Now)                                  // banned while the check ran
            {
                c.Enqueue(new ServerPackets.LoginBanned { Reason = account.BanReason, ExpiryDate = account.ExpiryDate });
                return;
            }

            if (!passwordOk)
            {
                global::Server.WebAdmin.WebAdminHooks.RaiseLoginAttempt(account.AccountID, c.IPAddress, "bad-password");

                if (account.WrongPasswordCount++ >= 5)
                {
                    MarkAccountDirty(account); // P9-12 (WrongPasswordCount itself is not saved)
                    account.Banned = true;
                    account.BanReason = GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.TooManyWrongLoginAttempts);
                    account.ExpiryDate = Now.AddMinutes(2);

                    c.Enqueue(new ServerPackets.LoginBanned
                    {
                        Reason = account.BanReason,
                        ExpiryDate = account.ExpiryDate
                    });
                    return;
                }

                c.Enqueue(new ServerPackets.Login { Result = 4 });
                c.Enqueue(LoginFailedPacket(c, 4, Math.Max(0, AccountWrongPasswordLimit - account.WrongPasswordCount)));
                CountFailedLogin(c);
                return;
            }
            account.WrongPasswordCount = 0;

            //WebAdmin: optional login gate (e.g. maintenance mode). A non-null result refuses the login like AllowLogin=false does.
            var gateRefusal = global::Server.WebAdmin.WebAdminHooks.CheckLoginGate(account);
            if (gateRefusal != null)
            {
                global::Server.WebAdmin.WebAdminHooks.RaiseLoginAttempt(account.AccountID, c.IPAddress, "refused:" + gateRefusal);
                c.Enqueue(new ServerPackets.Login { Result = 0 });
                return;
            }

            if (account.RequirePasswordChange)
            {
                c.Enqueue(new ServerPackets.Login { Result = 5 });
                return;
            }

            lock (AccountLock)
            {
                account.Connection?.SendDisconnect(1);

                account.Connection = c;
            }
            // P9-12 (review L3): sticky until a save serialises the account while it is offline, so a select-screen-only
            // session and the logout writes (StopGame, after which Connection and Player are null) are always saved fresh.
            account.SaveHot = true;
            MarkAccountDirty(account);

            c.Account = account;
            c.Stage = GameStage.Select;

            account.LastDate = Now;
            account.LastIP = c.IPAddress;

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.UserLoggedIn), account.Connection.SessionID, account.Connection.IPAddress));
            c.Enqueue(new ServerPackets.LoginSuccess { Characters = account.GetSelectInfo() });
            c.Enqueue(SelectDetailsPacket(account));
            c.Enqueue(ServerRulesPacket());
            global::Server.WebAdmin.WebAdminHooks.RaiseLoginAttempt(account.AccountID, c.IPAddress, "ok");
        }

        /// <summary>
        /// Snapshot of the live server rules for S.ServerRules (sent after LoginSuccess and on StartGame success).
        /// Game thread only. Login-time only, so the copies below are not a per-tick cost.
        /// </summary>
        public static S.ServerRules ServerRulesPacket()
        {
            var p = new S.ServerRules
            {
                OnlyRefineWeapon = Settings.OnlyRefineWeapon,
                RefineBaseChance = Settings.RefineBaseChance,
                RefineTime = Settings.RefineTime,
                RefineIncrease = Settings.RefineIncrease,
                RefineCritChance = Settings.RefineCritChance,
                RefineCritIncrease = Settings.RefineCritIncrease,
                RefineWepStatReduce = Settings.RefineWepStatReduce,
                RefineItemStatReduce = Settings.RefineItemStatReduce,
                RefineCost = Settings.RefineCost,
                RefineOreName = Settings.RefineOreName ?? string.Empty,

                AwakeSuccessRate = Awake.AwakeSuccessRate,
                AwakeHitRate = Awake.AwakeHitRate,
                MaxAwakeLevel = Awake.MaxAwakeLevel,
                Awake_WeaponRate = Awake.Awake_WeaponRate,
                Awake_HelmetRate = Awake.Awake_HelmetRate,
                Awake_ArmorRate = Awake.Awake_ArmorRate,
                AwakeChanceMax = Awake.AwakeChanceMax != null ? (byte[])Awake.AwakeChanceMax.Clone() : new byte[0],
                AwakeMaterialRate = Awake.AwakeMaterialRate != null ? (float[])Awake.AwakeMaterialRate.Clone() : new float[0],

                MentorLevelGap = Settings.MentorLevelGap,
                MentorSkillBoost = Settings.MentorSkillBoost,
                MentorLength = Settings.MentorLength,
                MentorDamageBoost = Settings.MentorDamageBoost,
                MentorExpBoost = Settings.MentorExpBoost,
                MenteeExpBank = Settings.MenteeExpBank,

                LoverEXPBonus = Settings.LoverEXPBonus,
                MarriageCooldown = Settings.MarriageCooldown,
                WeddingRingRecall = Settings.WeddingRingRecall,
                MarriageLevelRequired = Settings.MarriageLevelRequired,
                ReplaceWedRingCost = Settings.ReplaceWedRingCost,

                MailAutoSendGold = Settings.MailAutoSendGold,
                MailAutoSendItems = Settings.MailAutoSendItems,
                MailFreeWithStamp = Settings.MailFreeWithStamp,
                MailCostPer1KGold = Settings.MailCostPer1KGold,
                MailItemInsurancePercentage = Settings.MailItemInsurancePercentage,
                MailCapacity = Settings.MailCapacity,

                BuyGTGold = Settings.BuyGTGold,
                ExtendGT = Settings.ExtendGT,
                GTDays = Settings.GTDays,

                GemStatIndependent = Settings.GemStatIndependent,

                AllowCreateAssassin = Settings.AllowCreateAssassin,
                AllowCreateArcher = Settings.AllowCreateArcher,

                MapDetailsEnabled = Settings.MapDetailsEnabled,
                FriendRequestsEnabled = Settings.FriendRequestsEnabled,
                MaxFriends = Settings.MaxFriends,
            };

            // The server reads AwakeMaterials[type - 1] for types 1..HPMP only; Settings.LoadAwakeAttribute/SaveAwakeAttribute
            // can append duplicate entries after that (Known defect), so send just the first HPMP entries, copied.
            List<List<byte>[]> materials = Awake.AwakeMaterials;
            int typeCount = materials == null ? 0 : Math.Min(materials.Count, (int)AwakeType.HPMP);
            for (int i = 0; i < typeCount; i++)
            {
                List<byte>[] source = materials[i];
                if (source == null)
                {
                    p.AwakeMaterials.Add(new List<byte>[0]);
                    continue;
                }

                var copy = new List<byte>[source.Length];
                for (int k = 0; k < source.Length; k++)
                    copy[k] = source[k] != null ? new List<byte>(source[k]) : new List<byte>();
                p.AwakeMaterials.Add(copy);
            }

            return p;
        }

        public int HTTPLogin(string AccountID, string Password)
        {
            if (!Settings.AllowLogin)
            {
                return 0;
            }

            if (!AccountIDReg.IsMatch(AccountID))
            {
                return 1;
            }

            if (!PasswordReg.IsMatch(Password))
            {
                return 2;
            }

            var account = GetAccount(AccountID);

            if (account == null)
            {
                return 3;
            }

            if (account.Banned)
            {
                if (account.ExpiryDate > Now)
                {
                    return 4;
                }
                account.Banned = false;
            }
            account.BanReason = string.Empty;
            account.ExpiryDate = DateTime.MinValue;
            if (string.CompareOrdinal(account.Password, Password) != 0)
            {
                if (account.WrongPasswordCount++ >= 5)
                {
                    account.Banned = true;
                    account.BanReason = GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.TooManyWrongLoginAttempts);
                    account.ExpiryDate = Now.AddMinutes(2);
                    return 5;
                }
                return 6;
            }
            account.WrongPasswordCount = 0;
            return 7;
        }

        public void NewCharacter(ClientPackets.NewCharacter p, MirConnection c, bool IsGm)
        {
            if (!Settings.AllowNewCharacter)
            {
                c.Enqueue(new ServerPackets.NewCharacter { Result = 0 });
                return;
            }

            if (ConnectionLogs.TryGetValue(c.IPAddress, out MirConnectionLog currentlog))
            {
                if (currentlog.CharactersMade.Count > 4 && !IsLoadTestExempt(c.IPAddress))
                {
                    IPBlocks[c.IPAddress] = Now.AddHours(24);
                    c.Enqueue(new ServerPackets.NewCharacter { Result = 0 });
                    return;
                }
                currentlog.CharactersMade.Add(Time);
                for (int i = 0; i < currentlog.CharactersMade.Count; i++)
                {
                    if ((currentlog.CharactersMade[i] + 60 * 60 * 1000) < Time)
                    {
                        currentlog.CharactersMade.RemoveAt(i);
                        break;
                    }
                }
            }
            else
            {
                ConnectionLogs[c.IPAddress] = new MirConnectionLog() { IPAddress = c.IPAddress };
            }


            if (!CharacterReg.IsMatch(p.Name))
            {
                c.Enqueue(new ServerPackets.NewCharacter { Result = 1 });
                return;
            }

            if (!IsGm && DisabledCharNames.Contains(p.Name.ToUpper()))
            {
                c.Enqueue(new ServerPackets.NewCharacter { Result = 1 });
                return;
            }

            if (p.Gender != MirGender.Male && p.Gender != MirGender.Female)
            {
                c.Enqueue(new ServerPackets.NewCharacter { Result = 2 });
                return;
            }

            if (p.Class != MirClass.Warrior && p.Class != MirClass.Wizard && p.Class != MirClass.Taoist &&
                p.Class != MirClass.Assassin && p.Class != MirClass.Archer)
            {
                c.Enqueue(new ServerPackets.NewCharacter { Result = 3 });
                return;
            }

            if (p.Class == MirClass.Assassin && !Settings.AllowCreateAssassin ||
                p.Class == MirClass.Archer && !Settings.AllowCreateArcher)
            {
                c.Enqueue(new ServerPackets.NewCharacter { Result = 3 });
                return;
            }

            var count = 0;

            for (var i = 0; i < c.Account.Characters.Count; i++)
            {
                if (c.Account.Characters[i].Deleted) continue;

                if (++count >= Globals.MaxCharacterCount)
                {
                    c.Enqueue(new ServerPackets.NewCharacter { Result = 4 });
                    return;
                }
            }

            lock (AccountLock)
            {
                if (CharacterExists(p.Name))
                {
                    c.Enqueue(new ServerPackets.NewCharacter { Result = 5 });
                    return;
                }

                var info = new CharacterInfo(p, c) { Index = ++NextCharacterID, AccountInfo = c.Account };

                MarkAccountDirty(c.Account); // P9-12 (the account is hot anyway: select screen)
                c.Account.Characters.Add(info);
                CharacterList.Add(info);

                c.Enqueue(new ServerPackets.NewCharacterSuccess { CharInfo = info.ToSelectInfo() });
                c.Enqueue(SelectDetailsPacket(c.Account));
            }
        }

        public bool CanCreateHero(ClientPackets.NewHero p, MirConnection c, bool IsGm)
        {
            if (!Settings.AllowNewHero)
            {
                c.Enqueue(new S.NewHero { Result = 0 });
                return false;
            }

            if (!CharacterReg.IsMatch(p.Name))
            {
                c.Enqueue(new S.NewHero { Result = 1 });
                return false;
            }

            if (!IsGm && DisabledCharNames.Contains(p.Name.ToUpper()))
            {
                c.Enqueue(new S.NewHero { Result = 1 });
                return false;
            }

            if (p.Gender != MirGender.Male && p.Gender != MirGender.Female)
            {
                c.Enqueue(new S.NewHero { Result = 2 });
                return false;
            }

            if (p.Class != MirClass.Warrior && p.Class != MirClass.Wizard && p.Class != MirClass.Taoist && p.Class != MirClass.Assassin && p.Class != MirClass.Archer)
            {
                c.Enqueue(new S.NewHero { Result = 3 });
                return false;
            }

            if (p.Class == MirClass.Warrior && !Settings.Hero_CanCreateClass[0] || p.Class == MirClass.Wizard && !Settings.Hero_CanCreateClass[1] || p.Class == MirClass.Taoist && !Settings.Hero_CanCreateClass[2] || p.Class == MirClass.Assassin && !Settings.Hero_CanCreateClass[3] || p.Class == MirClass.Archer && !Settings.Hero_CanCreateClass[4])
            {
                c.Enqueue(new S.NewHero { Result = 3 });
                return false;
            }

            lock (AccountLock)
            {
                if (CharacterExists(p.Name))
                {
                    c.Enqueue(new S.NewHero { Result = 5 });
                    return false;
                }
            }

            return true;
        }

        public bool AccountExists(string accountID)
        {
            for (var i = 0; i < AccountList.Count; i++)
                if (string.Compare(AccountList[i].AccountID, accountID, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;

            return false;
        }

        public bool CharacterExists(string name)
        {
            for (var i = 0; i < CharacterList.Count; i++)
                if (string.Compare(CharacterList[i].Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;

            return false;
        }

        public List<CharacterInfo> MatchPlayer(string PlayerID, bool match = false)
        {
            if (string.IsNullOrEmpty(PlayerID)) return new List<CharacterInfo>(CharacterList);

            List<CharacterInfo> list = new List<CharacterInfo>();

            for (int i = 0; i < CharacterList.Count; i++)
            {
                if (match)
                {
                    if (CharacterList[i].Name.Equals(PlayerID, StringComparison.OrdinalIgnoreCase))
                        list.Add(CharacterList[i]);
                }
                else
                {
                    if (CharacterList[i].Name.IndexOf(PlayerID, StringComparison.OrdinalIgnoreCase) >= 0)
                        list.Add(CharacterList[i]);
                }
            }

            return list;
        }
        public List<CharacterInfo> MatchPlayerbyItem(string itemIdentifier, bool match = false)
        {
            List<CharacterInfo> list = new List<CharacterInfo>();

            bool isNumeric = ulong.TryParse(itemIdentifier, out ulong itemId);

            for (int i = 0; i < CharacterList.Count; i++)
            {
                if (match)
                {
                    foreach (var item in CharacterList[i].Inventory)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.Equals(itemIdentifier, StringComparison.OrdinalIgnoreCase))) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var item in CharacterList[i].AccountInfo.Storage)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.Equals(itemIdentifier, StringComparison.OrdinalIgnoreCase))) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var item in CharacterList[i].QuestInventory)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.Equals(itemIdentifier, StringComparison.OrdinalIgnoreCase))) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var item in CharacterList[i].Equipment)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.Equals(itemIdentifier, StringComparison.OrdinalIgnoreCase))) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var mail in CharacterList[i].Mail)
                        foreach (var item in mail.Items)
                            if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.Equals(itemIdentifier, StringComparison.OrdinalIgnoreCase))) && !list.Contains(CharacterList[i]))
                                list.Add(CharacterList[i]);
                }
                else
                {
                    foreach (var item in CharacterList[i].Inventory)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.IndexOf(itemIdentifier, StringComparison.OrdinalIgnoreCase) >= 0)) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var item in CharacterList[i].QuestInventory)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.IndexOf(itemIdentifier, StringComparison.OrdinalIgnoreCase) >= 0)) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var item in CharacterList[i].Equipment)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.IndexOf(itemIdentifier, StringComparison.OrdinalIgnoreCase) >= 0)) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);

                    foreach (var item in CharacterList[i].AccountInfo.Storage)
                        if (item != null && ((isNumeric && item.UniqueID == itemId) || (!isNumeric && item.FriendlyName.IndexOf(itemIdentifier, StringComparison.OrdinalIgnoreCase) >= 0)) && !list.Contains(CharacterList[i]))
                            list.Add(CharacterList[i]);
                }
            }

            return list;
        }

        public AccountInfo GetAccount(string accountID)
        {
            for (var i = 0; i < AccountList.Count; i++)
                if (string.Compare(AccountList[i].AccountID, accountID, StringComparison.OrdinalIgnoreCase) == 0)
                    return AccountList[i];

            return null;
        }

        public AccountInfo GetAccountByCharacter(string name)
        {
            for (var i = 0; i < AccountList.Count; i++)
            {
                for (int j = 0; j < AccountList[i].Characters.Count; j++)
                {
                    if (string.Compare(AccountList[i].Characters[j].Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                        return AccountList[i];
                }
            }

            return null;
        }

        public List<AccountInfo> MatchAccounts(string accountID, bool match = false)
        {
            if (string.IsNullOrEmpty(accountID)) return new List<AccountInfo>(AccountList);

            var list = new List<AccountInfo>();

            for (var i = 0; i < AccountList.Count; i++)
            {
                if (match)
                {
                    if (AccountList[i].AccountID.Equals(accountID, StringComparison.OrdinalIgnoreCase))
                        list.Add(AccountList[i]);
                }
                else
                {
                    if (AccountList[i].AccountID.IndexOf(accountID, StringComparison.OrdinalIgnoreCase) >= 0)
                        list.Add(AccountList[i]);
                }
            }

            return list;
        }

        public List<AccountInfo> MatchAccountsByPlayer(string playerName, bool match = false)
        {
            if (string.IsNullOrEmpty(playerName)) return new List<AccountInfo>(AccountList);

            var list = new List<AccountInfo>();

            for (var i = 0; i < AccountList.Count; i++)
            {
                for (var j = 0; j < AccountList[i].Characters.Count; j++)
                {
                    if (match)
                    {
                        if (AccountList[i].Characters[j].Name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                            list.Add(AccountList[i]);
                    }
                    else
                    {
                        if (AccountList[i].Characters[j].Name.IndexOf(playerName, StringComparison.OrdinalIgnoreCase) >= 0)
                            list.Add(AccountList[i]);
                    }
                }
            }

            return list;
        }

        public List<AccountInfo> MatchAccountsByIP(string ipAddress, bool matchLastIP = false, bool match = false)
        {
            if (string.IsNullOrEmpty(ipAddress)) return new List<AccountInfo>(AccountList);

            var list = new List<AccountInfo>();

            for (var i = 0; i < AccountList.Count; i++)
            {
                string ipToMatch = matchLastIP ? AccountList[i].LastIP : AccountList[i].CreationIP;

                if (match)
                {
                    if (ipToMatch.Equals(ipAddress, StringComparison.OrdinalIgnoreCase))
                        list.Add(AccountList[i]);
                }
                else
                {
                    if (ipToMatch.IndexOf(ipAddress, StringComparison.OrdinalIgnoreCase) >= 0)
                        list.Add(AccountList[i]);
                }
            }

            return list;
        }


        public void CreateAccountInfo()
        {
            AccountList.Add(new AccountInfo { Index = ++NextAccountID });
        }
        public void CreateMapInfo()
        {
            MapInfoList.Add(new MapInfo { Index = ++MapIndex });
        }
        public void CreateItemInfo(ItemType type = ItemType.Nothing)
        {
            ItemInfoList.Add(new ItemInfo { Index = ++ItemIndex, Type = type, RandomStatsId = 255 });
        }
        public void CreateMonsterInfo()
        {
            MonsterInfoList.Add(new MonsterInfo { Index = ++MonsterIndex });
        }
        public void CreateNPCInfo()
        {
            NPCInfoList.Add(new NPCInfo { Index = ++NPCIndex });
        }
        public void CreateQuestInfo()
        {
            QuestInfoList.Add(new QuestInfo { Index = ++QuestIndex });
        }

        public void AddToGameShop(ItemInfo Info)
        {
            GameShopList.Add(new GameShopItem
            {
                GIndex = ++GameshopIndex,
                GoldPrice = (uint)(1000 * Settings.CredxGold),
                CreditPrice = 1000,
                ItemIndex = Info.Index,
                Info = Info,
                Date = Now,
                Class = "All",
                Category = Info.Type.ToString()
            });
        }

        public void Remove(MapInfo info)
        {
            MapInfoList.Remove(info);
            //Desync all objects\
        }
        public void Remove(ItemInfo info)
        {
            ItemInfoList.Remove(info);
        }
        public void Remove(MonsterInfo info)
        {
            MonsterInfoList.Remove(info);
            //Desync all objects\
        }
        public void Remove(NPCInfo info)
        {
            NPCInfoList.Remove(info);
            //Desync all objects\
        }
        public void Remove(QuestInfo info)
        {
            QuestInfoList.Remove(info);
            //Desync all objects\
        }

        public void Remove(GameShopItem info)
        {
            GameShopList.Remove(info);

            if (GameShopList.Count == 0)
            {
                GameshopIndex = 0;
            }

            //Desync all objects\
        }

        public UserItem CreateFreshItem(ItemInfo info)
        {
            var item = new UserItem(info)
            {
                UniqueID = ++NextUserItemID,
                CurrentDura = info.Durability,
                MaxDura = info.Durability
            };

            UpdateItemExpiry(item);

            return item;
        }
        public UserItem CreateDropItem(int index)
        {
            return CreateDropItem(GetItemInfo(index));
        }
        public UserItem CreateDropItem(ItemInfo info)
        {
            if (info == null) return null;

            var item = new UserItem(info)
            {
                UniqueID = ++NextUserItemID,
                MaxDura = info.Durability,
                CurrentDura = (ushort)Math.Min(info.Durability, Random.Next(info.Durability) + 1000)
            };

            UpgradeItem(item);

            UpdateItemExpiry(item);

            if (!info.NeedIdentify) item.Identified = true;
            return item;
        }

        public UserItem CreateShopItem(ItemInfo info, ulong id)
        {
            if (info == null) return null;

            var item = new UserItem(info)
            {
                UniqueID = id,
                CurrentDura = info.Durability,
                MaxDura = info.Durability,
                IsShopItem = true,
            };

            return item;
        }

        public void UpdateItemExpiry(UserItem item)
        {
            var expiryInfo = new ExpireInfo();

            var r = new Regex(@"\[(.*?)\]");
            var expiryMatch = r.Match(item.Info.Name);

            if (expiryMatch.Success)
            {
                var parameter = expiryMatch.Groups[1].Captures[0].Value;

                var numAlpha = new Regex("(?<Numeric>[0-9]*)(?<Alpha>[a-zA-Z]*)");
                var match = numAlpha.Match(parameter);

                var alpha = match.Groups["Alpha"].Value;
                var num = 0;

                int.TryParse(match.Groups["Numeric"].Value, out num);

                switch (alpha)
                {
                    case "m":
                        expiryInfo.ExpiryDate = Now.AddMinutes(num);
                        break;
                    case "h":
                        expiryInfo.ExpiryDate = Now.AddHours(num);
                        break;
                    case "d":
                        expiryInfo.ExpiryDate = Now.AddDays(num);
                        break;
                    case "M":
                        expiryInfo.ExpiryDate = Now.AddMonths(num);
                        break;
                    case "y":
                        expiryInfo.ExpiryDate = Now.AddYears(num);
                        break;
                    default:
                        expiryInfo.ExpiryDate = DateTime.MaxValue;
                        break;
                }

                item.ExpireInfo = expiryInfo;
            }
        }

        public void UpgradeItem(UserItem item)
        {
            if (item.Info.RandomStats == null) return;
            var stat = item.Info.RandomStats;
            if (stat.MaxDuraChance > 0 && Random.Next(stat.MaxDuraChance) == 0)
            {
                var dura = RandomomRange(stat.MaxDuraMaxStat, stat.MaxDuraStatChance);
                item.MaxDura = (ushort)Math.Min(ushort.MaxValue, item.MaxDura + dura * 1000);
                item.CurrentDura = (ushort)Math.Min(ushort.MaxValue, item.CurrentDura + dura * 1000);
            }

            if (stat.MaxAcChance > 0 && Random.Next(stat.MaxAcChance) == 0) item.AddedStats[Stat.MaxAC] = (byte)(RandomomRange(stat.MaxAcMaxStat - 1, stat.MaxAcStatChance) + 1);
            if (stat.MaxMacChance > 0 && Random.Next(stat.MaxMacChance) == 0) item.AddedStats[Stat.MaxMAC] = (byte)(RandomomRange(stat.MaxMacMaxStat - 1, stat.MaxMacStatChance) + 1);
            if (stat.MaxDcChance > 0 && Random.Next(stat.MaxDcChance) == 0) item.AddedStats[Stat.MaxDC] = (byte)(RandomomRange(stat.MaxDcMaxStat - 1, stat.MaxDcStatChance) + 1);
            if (stat.MaxMcChance > 0 && Random.Next(stat.MaxMcChance) == 0) item.AddedStats[Stat.MaxMC] = (byte)(RandomomRange(stat.MaxMcMaxStat - 1, stat.MaxMcStatChance) + 1);
            if (stat.MaxScChance > 0 && Random.Next(stat.MaxScChance) == 0) item.AddedStats[Stat.MaxSC] = (byte)(RandomomRange(stat.MaxScMaxStat - 1, stat.MaxScStatChance) + 1);
            if (stat.AccuracyChance > 0 && Random.Next(stat.AccuracyChance) == 0) item.AddedStats[Stat.Accuracy] = (byte)(RandomomRange(stat.AccuracyMaxStat - 1, stat.AccuracyStatChance) + 1);
            if (stat.AgilityChance > 0 && Random.Next(stat.AgilityChance) == 0) item.AddedStats[Stat.Agility] = (byte)(RandomomRange(stat.AgilityMaxStat - 1, stat.AgilityStatChance) + 1);
            if (stat.HpChance > 0 && Random.Next(stat.HpChance) == 0) item.AddedStats[Stat.HP] = (byte)(RandomomRange(stat.HpMaxStat - 1, stat.HpStatChance) + 1);
            if (stat.MpChance > 0 && Random.Next(stat.MpChance) == 0) item.AddedStats[Stat.MP] = (byte)(RandomomRange(stat.MpMaxStat - 1, stat.MpStatChance) + 1);
            if (stat.StrongChance > 0 && Random.Next(stat.StrongChance) == 0) item.AddedStats[Stat.Strong] = (byte)(RandomomRange(stat.StrongMaxStat - 1, stat.StrongStatChance) + 1);
            if (stat.MagicResistChance > 0 && Random.Next(stat.MagicResistChance) == 0) item.AddedStats[Stat.MagicResist] = (byte)(RandomomRange(stat.MagicResistMaxStat - 1, stat.MagicResistStatChance) + 1);
            if (stat.PoisonResistChance > 0 && Random.Next(stat.PoisonResistChance) == 0) item.AddedStats[Stat.PoisonResist] = (byte)(RandomomRange(stat.PoisonResistMaxStat - 1, stat.PoisonResistStatChance) + 1);
            if (stat.HpRecovChance > 0 && Random.Next(stat.HpRecovChance) == 0) item.AddedStats[Stat.HealthRecovery] = (byte)(RandomomRange(stat.HpRecovMaxStat - 1, stat.HpRecovStatChance) + 1);
            if (stat.MpRecovChance > 0 && Random.Next(stat.MpRecovChance) == 0) item.AddedStats[Stat.SpellRecovery] = (byte)(RandomomRange(stat.MpRecovMaxStat - 1, stat.MpRecovStatChance) + 1);
            if (stat.PoisonRecovChance > 0 && Random.Next(stat.PoisonRecovChance) == 0) item.AddedStats[Stat.PoisonRecovery] = (byte)(RandomomRange(stat.PoisonRecovMaxStat - 1, stat.PoisonRecovStatChance) + 1);
            if (stat.CriticalRateChance > 0 && Random.Next(stat.CriticalRateChance) == 0) item.AddedStats[Stat.CriticalRate] = (byte)(RandomomRange(stat.CriticalRateMaxStat - 1, stat.CriticalRateStatChance) + 1);
            if (stat.CriticalDamageChance > 0 && Random.Next(stat.CriticalDamageChance) == 0) item.AddedStats[Stat.CriticalDamage] = (byte)(RandomomRange(stat.CriticalDamageMaxStat - 1, stat.CriticalDamageStatChance) + 1);
            if (stat.FreezeChance > 0 && Random.Next(stat.FreezeChance) == 0) item.AddedStats[Stat.Freezing] = (byte)(RandomomRange(stat.FreezeMaxStat - 1, stat.FreezeStatChance) + 1);
            if (stat.PoisonAttackChance > 0 && Random.Next(stat.PoisonAttackChance) == 0) item.AddedStats[Stat.PoisonAttack] = (byte)(RandomomRange(stat.PoisonAttackMaxStat - 1, stat.PoisonAttackStatChance) + 1);
            if (stat.AttackSpeedChance > 0 && Random.Next(stat.AttackSpeedChance) == 0) item.AddedStats[Stat.AttackSpeed] = (sbyte)(RandomomRange(stat.AttackSpeedMaxStat - 1, stat.AttackSpeedStatChance) + 1);
            if (stat.LuckChance > 0 && Random.Next(stat.LuckChance) == 0) item.AddedStats[Stat.Luck] = (sbyte)(RandomomRange(stat.LuckMaxStat - 1, stat.LuckStatChance) + 1);
            if (stat.CurseChance > 0 && Random.Next(100) <= stat.CurseChance) item.Cursed = true;

            if (stat.SlotChance > 0 && Random.Next(stat.SlotChance) == 0)
            {
                var slot = (byte)(RandomomRange(stat.SlotMaxStat - 1, stat.SlotStatChance) + 1);

                if (slot > item.Info.Slots)
                {
                    item.SetSlotSize(slot);
                }
            }
        }

        public int RandomomRange(int count, int rate)
        {
            var x = 0;
            for (var i = 0; i < count; i++) if (Random.Next(rate) == 0) x++;
            return x;
        }
        public bool BindItem(UserItem item)
        {
            for (var i = 0; i < ItemInfoList.Count; i++)
            {
                var info = ItemInfoList[i];
                if (info.Index != item.ItemIndex) continue;
                item.Info = info;

                return BindSlotItems(item);
            }
            return false;
        }

        public bool BindGameShop(GameShopItem item, bool editEnvir = true)
        {
            for (var i = 0; i < Edit.ItemInfoList.Count; i++)
            {
                var info = Edit.ItemInfoList[i];
                if (info.Index != item.ItemIndex) continue;
                item.Info = info;

                return true;
            }
            return false;
        }

        public bool BindSlotItems(UserItem item)
        {
            for (var i = 0; i < item.Slots.Length; i++)
            {
                if (item.Slots[i] == null) continue;

                if (!BindItem(item.Slots[i])) return false;
            }

            return true;
        }

        public bool BindQuest(QuestProgressInfo quest)
        {
            for (var i = 0; i < QuestInfoList.Count; i++)
            {
                var info = QuestInfoList[i];
                if (info.Index != quest.Index) continue;
                quest.Info = info;
                return true;
            }
            return false;
        }

        public Map GetMap(int index)
        {
            return MapList.FirstOrDefault(t => t.Info.Index == index);
        }

        public Map GetMap(string name, bool strict = true)
        {
            return MapList.FirstOrDefault(t => strict ? string.Equals(t.Info.Title, name, StringComparison.CurrentCultureIgnoreCase) : t.Info.Title.StartsWith(name, StringComparison.CurrentCultureIgnoreCase));
        }

        public Map GetWorldMap(string name)
        {
            return MapList.FirstOrDefault(t => t.Info.Title.StartsWith(name, StringComparison.CurrentCultureIgnoreCase) && t.Info.BigMap > 0);
        }

        public MapInfo GetMapInfo(int index)
        {
            return MapInfoList.FirstOrDefault(t => t.Index == index);
        }

        public Map GetMapByNameAndInstance(string name, int instanceValue = 0)
        {
            if (instanceValue < 0) instanceValue = 0;
            if (instanceValue > 0) instanceValue--;

            var instanceMapList = MapList.Where(t => string.Equals(t.Info.FileName, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
            return instanceValue < instanceMapList.Count() ? instanceMapList[instanceValue] : null;
        }

        public MapObject GetObject(uint objectID)
        {
            return Objects.FirstOrDefault(e => e.ObjectID == objectID);
        }

        public List<MapObject> GetObjects(int map, ObjectType race)
        {
            return Objects.Where(x => x.CurrentMapIndex == map && x.Race == race).ToList();
        }

        public MonsterInfo GetMonsterInfo(int index)
        {
            for (var i = 0; i < MonsterInfoList.Count; i++)
                if (MonsterInfoList[i].Index == index) return MonsterInfoList[i];

            return null;
        }

        public NPCInfo GetNPCInfo(int index)
        {
            for (var i = 0; i < NPCInfoList.Count; i++)
            {
                if (NPCInfoList[i].Index == index)
                    return NPCInfoList[i];
            }

            return null;
        }

        public MonsterInfo GetMonsterInfo(int ai, int effect = -1)
        {
            for (var i = 0; i < MonsterInfoList.Count; i++)
                if (MonsterInfoList[i].AI == ai && (MonsterInfoList[i].Effect == effect || effect < 0)) return MonsterInfoList[i];

            return null;
        }

        public NPCObject GetNPC(string name)
        {
            return MapList.SelectMany(t1 => t1.NPCs.Where(t => t.Info.Name == name)).FirstOrDefault();
        }

        public NPCObject GetWorldMapNPC(string name)
        {
            return MapList.SelectMany(t1 => t1.NPCs.Where(t => t.Info.GameName.StartsWith(name, StringComparison.CurrentCultureIgnoreCase) && t.Info.ShowOnBigMap)).FirstOrDefault();
        }

        public MonsterInfo GetMonsterInfo(int id, bool strict = false)
        {
            String monsterName = MonsterInfoList.FirstOrDefault(x => x.Index == id)?.Name;

            if (monsterName == null)
            {
                return null;
            }
            else
            {
                return (GetMonsterInfo(monsterName, strict));
            }
        }

        public MonsterInfo GetMonsterInfo(string name, bool Strict = false)
        {
            for (var i = 0; i < MonsterInfoList.Count; i++)
            {
                var info = MonsterInfoList[i];
                if (Strict)
                {
                    if (info.Name != name) continue;
                    return info;
                }
                else
                {
                    if (string.Compare(info.Name, name, StringComparison.OrdinalIgnoreCase) != 0 &&
                        string.Compare(info.Name.Replace(" ", string.Empty), name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase) != 0) continue;
                    return info;
                }
            }
            return null;
        }
        public PlayerObject GetPlayer(string name)
        {
            for (var i = 0; i < Players.Count; i++)
                if (string.Compare(Players[i].Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                    return Players[i];

            return null;
        }
        public PlayerObject GetPlayer(uint PlayerId)
        {
            for (var i = 0; i < Players.Count; i++)
                if (Players[i].Info.Index == PlayerId)
                    return Players[i];

            return null;
        }
        public CharacterInfo GetCharacterInfo(string name)
        {
            for (var i = 0; i < CharacterList.Count; i++)
                if (string.Compare(CharacterList[i].Name, name, StringComparison.OrdinalIgnoreCase) == 0)
                    return CharacterList[i];

            return null;
        }

        public CharacterInfo GetCharacterInfo(int index)
        {
            for (var i = 0; i < CharacterList.Count; i++)
                if (CharacterList[i].Index == index)
                    return CharacterList[i];

            return null;
        }
        public HeroInfo GetHeroInfo(int index)
        {
            return HeroList.FirstOrDefault(x => x.Index == index);
        }

        public ItemInfo GetItemInfo(int index)
        {
            for (var i = 0; i < ItemInfoList.Count; i++)
            {
                var info = ItemInfoList[i];
                if (info.Index != index) continue;
                return info;
            }
            return null;
        }

        public ItemInfo GetItemInfo(string name)
        {
            for (var i = 0; i < ItemInfoList.Count; i++)
            {
                var info = ItemInfoList[i];
                if (string.Compare(info.Name.Replace(" ", ""), name, StringComparison.OrdinalIgnoreCase) != 0) continue;
                return info;
            }
            return null;
        }

        public QuestInfo GetQuestInfo(int index)
        {
            return QuestInfoList.FirstOrDefault(info => info.Index == index);
        }

        public ItemInfo GetBook(short Skill)
        {
            for (var i = 0; i < ItemInfoList.Count; i++)
            {
                var info = ItemInfoList[i];
                if (info.Type != ItemType.Book || info.Shape != Skill) continue;
                return info;
            }
            return null;
        }

        public BuffInfo GetBuffInfo(BuffType type)
        {
            for (int i = 0; i < BuffInfoList.Count; i++)
            {
                var info = BuffInfoList[i];
                if (info.Type != type) continue;

                return info;
            }

            throw new NotImplementedException($"{type} has not been implemented.");
        }

        public void MessageAccount(AccountInfo account, string message, ChatType type)
        {
            if (account?.Characters == null) return;

            for (var i = 0; i < account.Characters.Count; i++)
            {
                if (account.Characters[i].Player == null) continue;
                account.Characters[i].Player.ReceiveChat(message, type);
                return;
            }
        }


        public void MailCharacter(CharacterInfo info, UserItem item = null, uint gold = 0, int reason = 0, string customMessage = null)
        {
            string sender = "Bichon Administrator";

            string message = "You have been mailed due to the following reason:\r\n\r\n";

            switch (reason)
            {
                case 1:
                    message += "Could not return item to bag after trade.";
                    break;
                case 99:
                    message += "Code didn't correctly handle checking inventory space.";
                    break;
                default:
                    message += customMessage ?? "No reason provided.";
                    break;
            }

            MailInfo mail = new MailInfo(info.Index)
            {
                Sender = sender,
                Message = message,
                Gold = gold
            };

            if (item != null)
            {
                mail.Items.Add(item);
            }

            mail.Send();
        }

        public GuildObject GetGuild(string name)
        {
            for (var i = 0; i < Guilds.Count; i++)
            {
                if (string.Compare(Guilds[i].Name.Replace(" ", ""), name, StringComparison.OrdinalIgnoreCase) != 0) continue;

                return Guilds[i];
            }

            return null;
        }
        public GuildObject GetGuild(int index)
        {
            for (var i = 0; i < Guilds.Count; i++)
            {
                if (Guilds[i].Guildindex == index)
                {
                    return Guilds[i];
                }
            }

            return null;
        }

        public void ProcessNewDay()
        {
            InvalidateAccountCache(); // P9-12: daily quests are cleared on every character, offline ones too

            foreach (var c in CharacterList)
            {
                ClearDailyQuests(c);

                c.NewDay = true;

                c.Player?.CallDefaultNPC(DefaultNPCType.Daily);
            }
        }

        private void ProcessRentedItems()
        {
            foreach (var characterInfo in CharacterList)
            {
                if (characterInfo.RentedItems.Count <= 0)
                {
                    continue;
                }

                foreach (var rentedItemInfo in characterInfo.RentedItems)
                {
                    if (rentedItemInfo.ItemReturnDate >= Now)
                        continue;

                    var rentingPlayer = GetCharacterInfo(rentedItemInfo.RentingPlayerName);

                    for (var i = 0; i < rentingPlayer.Inventory.Length; i++)
                    {
                        if (rentedItemInfo.ItemId != rentingPlayer?.Inventory[i]?.UniqueID)
                        {
                            continue;
                        }

                        var item = rentingPlayer.Inventory[i];

                        if (item?.RentalInformation == null)
                        {
                            continue;
                        }

                        if (Now <= item.RentalInformation.ExpiryDate)
                        {
                            continue;
                        }

                        ReturnRentalItem(item, item.RentalInformation.OwnerName, rentingPlayer, false);
                        rentingPlayer.Inventory[i] = null;
                        rentingPlayer.HasRentedItem = false;

                        if (rentingPlayer.Player == null)
                        {
                            continue;
                        }

                        rentingPlayer.Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.ItemExpiredFromInventory), item.Info.FriendlyName), ChatType.Hint);
                        rentingPlayer.Player.Enqueue(new S.DeleteItem { UniqueID = item.UniqueID, Count = item.Count });
                        rentingPlayer.Player.RefreshStats();
                    }

                    for (var i = 0; i < rentingPlayer.Equipment.Length; i++)
                    {
                        var item = rentingPlayer.Equipment[i];

                        if (item?.RentalInformation == null)
                        {
                            continue;
                        }

                        if (Now <= item.RentalInformation.ExpiryDate)
                        {
                            continue;
                        }

                        ReturnRentalItem(item, item.RentalInformation.OwnerName, rentingPlayer, false);
                        rentingPlayer.Equipment[i] = null;
                        rentingPlayer.HasRentedItem = false;

                        if (rentingPlayer.Player == null)
                        {
                            continue;
                        }

                        rentingPlayer.Player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.ItemExpiredInventory), item.Info.FriendlyName), ChatType.Hint);
                        rentingPlayer.Player.Enqueue(new S.DeleteItem { UniqueID = item.UniqueID, Count = item.Count });
                        rentingPlayer.Player.RefreshStats();
                    }
                }
            }

            foreach (var characterInfo in CharacterList)
            {
                if (characterInfo.RentedItemsToRemove.Count <= 0)
                {
                    continue;
                }

                foreach (var rentalInformationToRemove in characterInfo.RentedItemsToRemove)
                {
                    characterInfo.RentedItems.Remove(rentalInformationToRemove);
                }

                characterInfo.RentedItemsToRemove.Clear();
            }
        }

        public bool ReturnRentalItem(UserItem rentedItem, string ownerName, CharacterInfo rentingCharacterInfo, bool removeNow = true)
        {
            if (rentedItem.RentalInformation == null)
            {
                return false;
            }

            // P9-12: the borrower's item and the lender's RentedItems change (both may be offline); before anything can throw.
            MarkAccountDirty(rentingCharacterInfo?.AccountInfo);
            var owner = GetCharacterInfo(ownerName);
            MarkAccountDirty(owner?.AccountInfo);
            var returnItems = new List<UserItem>();

            foreach (var rentalInformation in owner.RentedItems)
            {
                if (rentalInformation.ItemId == rentedItem.UniqueID)
                {
                    owner.RentedItemsToRemove.Add(rentalInformation);
                }
            }

            rentedItem.RentalInformation.BindingFlags = BindMode.None;
            rentedItem.RentalInformation.RentalLocked = true;
            rentedItem.RentalInformation.ExpiryDate = rentedItem.RentalInformation.ExpiryDate.AddDays(1);

            returnItems.Add(rentedItem);

            var mail = new MailInfo(owner.Index, true)
            {
                Sender = rentingCharacterInfo.Name,
                Message = rentedItem.Info.FriendlyName,
                Items = returnItems
            };

            mail.Send();

            if (removeNow)
            {
                foreach (var rentalInformationToRemove in owner.RentedItemsToRemove)
                {
                    owner.RentedItems.Remove(rentalInformationToRemove);
                }

                owner.RentedItemsToRemove.Clear();
            }

            return true;
        }

        private void ClearDailyQuests(CharacterInfo info)
        {
            foreach (var quest in QuestInfoList)
            {
                if (quest.Type != QuestType.Daily) continue;

                for (var i = 0; i < info.CompletedQuests.Count; i++)
                {
                    if (info.CompletedQuests[i] != quest.Index) continue;

                    info.CompletedQuests.RemoveAt(i);
                }
            }

            info.Player?.GetCompletedQuests();
        }

        public GuildBuffInfo FindGuildBuffInfo(int Id)
        {
            for (var i = 0; i < Settings.Guild_BuffList.Count; i++)
            {
                if (Settings.Guild_BuffList[i].Id == Id)
                {
                    return Settings.Guild_BuffList[i];
                }
            }

            return null;
        }

        /// <summary>Records a completed market sale (consignment bought or auction won) for the price history (game thread).</summary>
        public void RecordMarketSale(int itemIndex, uint price)
        {
            if (!MarketSales.TryGetValue(itemIndex, out var history))
            {
                history = new MarketSaleHistory();
                MarketSales[itemIndex] = history;
            }

            history.Add(price, Now);
        }

        private static readonly Comparison<SentMailEntry> CompareSentMailByDate = (a, b) =>
        {
            int result = a.Mail.DateSent.CompareTo(b.Mail.DateSent);
            return result != 0 ? result : a.Mail.MailID.CompareTo(b.Mail.MailID);
        };

        /// <summary>
        /// Rebuilds SentMailIndex from every mailbox (MailInfo.SenderIndex, v119; older mail has -1 and is skipped). Called at
        /// the end of LoadAccounts, so a world start and a web admin restore both start from the loaded boxes.
        /// </summary>
        private void BuildSentMailIndex()
        {
            SentMailIndex.Clear();

            for (var i = 0; i < CharacterList.Count; i++)
            {
                var recipient = CharacterList[i];

                for (var j = 0; j < recipient.Mail.Count; j++)
                    AddSentMail(recipient, recipient.Mail[j]);
            }

            foreach (var sent in SentMailIndex.Values)
                sent.Sort(CompareSentMailByDate);
        }

        /// <summary>Lists a mail just put in <paramref name="recipient"/>'s box under its sender (MailInfo.Send; game thread).</summary>
        public void AddSentMail(CharacterInfo recipient, MailInfo mail)
        {
            if (recipient == null || mail == null || mail.SenderIndex < 0) return;

            if (!SentMailIndex.TryGetValue(mail.SenderIndex, out var sent))
            {
                sent = new List<SentMailEntry>();
                SentMailIndex[mail.SenderIndex] = sent;
            }

            sent.Add(new SentMailEntry(recipient, mail));
        }

        /// <summary>Drops a mail that left its recipient's box from its sender's Sent folder (every mail removal path; game thread).</summary>
        public void RemoveSentMail(MailInfo mail)
        {
            if (mail == null || mail.SenderIndex < 0) return;
            if (!SentMailIndex.TryGetValue(mail.SenderIndex, out var sent)) return;

            for (var i = sent.Count - 1; i >= 0; i--)
            {
                if (sent[i].Mail != mail) continue;

                sent.RemoveAt(i);
                break;
            }

            if (sent.Count == 0) SentMailIndex.Remove(mail.SenderIndex);
        }

        /// <summary>
        /// Drops a character that leaves CharacterList for good (GM ARCHIVEPLAYER, web admin account delete) from the Sent
        /// folders: the mail in its box leaves its senders' folders, and its own folder goes (game thread).
        /// </summary>
        public void RemoveSentMailFor(CharacterInfo character)
        {
            if (character == null) return;

            for (var i = 0; i < character.Mail.Count; i++)
                RemoveSentMail(character.Mail[i]);

            SentMailIndex.Remove(character.Index);
        }

        public void ClearGameshopLog()
        {
            InvalidateAccountCache(); // P9-12: GSpurchases of every character; bumped again below (also called from WinForms)
            Main.GameshopLog.Clear();

            for (var i = 0; i < AccountList.Count; i++)
            {
                for (var f = 0; f < AccountList[i].Characters.Count; f++)
                {
                    AccountList[i].Characters[f].GSpurchases.Clear();
                }
            }

            ResetGS = false;
            InvalidateAccountCache();
            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.GameshopPurchaseLogsCleared));
        }

        public void Inspect(MirConnection con, uint id)
        {
            if (ObjectID == id) return;

            PlayerObject player = Players.FirstOrDefault(x => x.ObjectID == id || x.Pets.Count(y => y.ObjectID == id && y is HumanWizard) > 0);

            if (player == null) return;
            Inspect(con, player.Info.Index);
        }

        public void Inspect(MirConnection con, int id)
        {
            if (ObjectID == id) return;

            CharacterInfo player = GetCharacterInfo(id);
            if (player == null) return;

            CharacterInfo Lover = null;
            string loverName = "";

            if (player.Married != 0) Lover = GetCharacterInfo(player.Married);

            if (Lover != null)
            {
                loverName = Lover.Name;
            }

            for (int i = 0; i < player.Equipment.Length; i++)
            {
                UserItem u = player.Equipment[i];
                if (u == null) continue;

                con.CheckItem(u);
            }

            string guildname = "";
            string guildrank = "";
            GuildObject guild = null;
            GuildRank guildRank = null;
            if (player.GuildIndex != -1)
            {
                guild = GetGuild(player.GuildIndex);
                if (guild != null)
                {
                    guildRank = guild.FindRank(player.Name);
                    if (guildRank == null)
                    {
                        guild = null;
                    }
                    else
                    {
                        guildname = guild.Name;
                        guildrank = guildRank.Name;
                    }
                }
            }

            con.Enqueue(new S.PlayerInspect
            {
                Name = player.Name,
                Equipment = player.Equipment,
                GuildName = guildname,
                GuildRank = guildrank,
                Hair = player.Hair,
                Gender = player.Gender,
                Class = player.Class,
                Level = player.Level,
                LoverName = loverName,
                AllowObserve = player.AllowObserve && Settings.AllowObserve
            });

            // Phase 2.5: always sent (an empty Text clears the inspect window's profile from an earlier inspect).
            con.Enqueue(new S.PlayerProfile { Name = player.Name, Text = player.ProfileText ?? string.Empty });
        }

        public void InspectHero(MirConnection con, int id)
        {
            if (ObjectID == id)
            {
                return;
            }

            HeroObject heroObject = Heroes.SingleOrDefault(h => h.ObjectID == id);

            if (heroObject == null)
            {
                return;
            }

            HeroInfo heroInfo = GetHeroInfo(heroObject.Info.Index);

            if (heroInfo == null)
            {
                return;
            }

            for (int i = 0; i < heroInfo.Equipment.Length; i++)
            {
                UserItem u = heroInfo.Equipment[i];

                if (u == null)
                {
                    continue;
                }

                con.CheckItem(u);
            }

            var ownerName = heroObject.Owner.Name;

            con.Enqueue(new S.PlayerInspect
            {
                Name = GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.PlayerHero), ownerName),
                Equipment = heroInfo.Equipment,
                GuildName = String.Empty,
                GuildRank = String.Empty,
                Hair = heroInfo.Hair,
                Gender = heroInfo.Gender,
                Class = heroInfo.Class,
                Level = heroInfo.Level,
                LoverName = String.Empty,
                AllowObserve = false,
                IsHero = true
            });
        }

        public void Observe(MirConnection con, string Name)
        {
            var player = GetPlayer(Name);

            if (player == null) return;
            if (!player.AllowObserve || !Settings.AllowObserve) return;

            player.AddObserver(con);
        }

        public void GetRanking(MirConnection con, byte RankType, int RankIndex, bool OnlineOnly)
        {
            if (RankType > RankClass.Length || RankType >= OnlineRankingCount.Length) return; // X1-01: RankType 6 indexed RankClass[5] (world stop)
            List<RankCharacterInfo> listings = RankType == 0 ? RankTop : RankClass[RankType - 1];

            if (RankIndex >= listings.Count || RankIndex < 0) return;

            S.Rankings p = new S.Rankings
            {
                RankType = RankType,
                Count = OnlineOnly ? OnlineRankingCount[RankType] : listings.Count
            };

            if (con.Player != null)
            {
                if (RankType == 0)
                    p.MyRank = con.Player.Info.Rank[0];
                else
                    p.MyRank = (byte)con.Player.Class == (RankType - 1) ? con.Player.Info.Rank[1] : 0;
            }

            S.RankingsDetail detail = new S.RankingsDetail { RankType = RankType };

            int c = 0;
            for (int i = RankIndex; i < listings.Count; i++)
            {
                if (OnlineOnly && GetPlayer(listings[i].Name) == null) continue;

                if (!CheckListing(con, listings[i]))
                    p.ListingDetails.Add(listings[i]);
                p.Listings.Add(listings[i].PlayerId);
                detail.Rows.Add(RankingDetailRow(listings[i]));
                c++;

                if (c > 19 || c >= p.Count) break;
            }

            con.Enqueue(p);
            con.Enqueue(detail);
        }

        /// <summary>S.RankingsDetail row for one ranking listing: guild, online and EXP progress from the live CharacterInfo.</summary>
        private ClientRankingDetail RankingDetailRow(RankCharacterInfo listing)
        {
            var row = new ClientRankingDetail { PlayerId = listing.PlayerId };

            if (listing.info is CharacterInfo info)
            {
                row.Online = info.Player != null;
                row.ExpPerTenThousand = ExpPerTenThousand(info.Experience, info.Level > 0 && info.Level < Settings.ExperienceList.Count ? Settings.ExperienceList[info.Level - 1] : 0);

                if (info.GuildIndex != -1)
                {
                    GuildObject guild = GetGuild(info.GuildIndex);
                    if (guild != null) row.GuildName = guild.Name;
                }
            }

            return row;
        }

        /// <summary>EXP progress in 1/10000ths (0..10000); 0 when there is no next level (max 0).</summary>
        public static ushort ExpPerTenThousand(long experience, long maxExperience)
        {
            if (maxExperience <= 0 || experience <= 0) return 0;
            if (experience >= maxExperience) return 10000;
            return (ushort)(experience * 10000.0 / maxExperience);
        }

        private const long GuildRankingCacheMs = 5 * Settings.Minute;
        private List<ClientGuildRanking> _guildRankingCache;
        private long _guildRankingCacheTime;    // Envir.Time after which the guild board is rebuilt on the next request

        /// <summary>
        /// C.GetGuildRanking: one page (S.GuildRankings.PageSize rows) of the guild board, guilds by level then EXP (the newbie
        /// guild left out). The board is rebuilt lazily on a request at most every 5 minutes (no per-tick work). Page clamped.
        /// </summary>
        public void GetGuildRanking(MirConnection con, int page)
        {
            if (_guildRankingCache == null || Time >= _guildRankingCacheTime)
            {
                _guildRankingCache = BuildGuildRanking();
                _guildRankingCacheTime = Time + GuildRankingCacheMs;
            }

            List<ClientGuildRanking> board = _guildRankingCache;
            int pageSize = S.GuildRankings.PageSize;
            int lastPage = board.Count == 0 ? 0 : (board.Count - 1) / pageSize;
            if (page < 0) page = 0;
            if (page > lastPage) page = lastPage;

            var p = new S.GuildRankings { Page = page, Total = board.Count };
            int end = Math.Min(board.Count, (page + 1) * pageSize);
            for (int i = page * pageSize; i < end; i++)
                p.Guilds.Add(board[i]);

            con.Enqueue(p);
        }

        private List<ClientGuildRanking> BuildGuildRanking()
        {
            var sorted = new List<GuildObject>(Guilds.Count);
            for (int i = 0; i < Guilds.Count; i++)
            {
                GuildObject guild = Guilds[i];
                if (guild == null || guild.Info == null || guild.Name == Settings.NewbieGuild) continue;
                sorted.Add(guild);
            }

            sorted.Sort((a, b) =>
            {
                int byLevel = b.Info.Level.CompareTo(a.Info.Level);
                return byLevel != 0 ? byLevel : b.Info.Experience.CompareTo(a.Info.Experience);
            });

            var board = new List<ClientGuildRanking>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                GuildInfo info = sorted[i].Info;
                string leader = string.Empty;
                if (info.Ranks.Count > 0 && info.Ranks[0].Members.Count > 0)
                    leader = info.Ranks[0].Members[0].Name;

                board.Add(new ClientGuildRanking
                {
                    Name = info.Name,
                    Level = info.Level,
                    ExpPerTenThousand = ExpPerTenThousand(info.Experience, info.MaxExperience),
                    Members = info.Membercount,
                    Leader = leader
                });
            }

            return board;
        }

        private bool CheckListing(MirConnection con, RankCharacterInfo listing)
        {
            if (!con.SentRankings.ContainsKey(listing.PlayerId))
            {
                con.SentRankings.Add(listing.PlayerId, listing.LastUpdated);
                return false;
            }

            DateTime lastUpdated = con.SentRankings[listing.PlayerId];
            if (lastUpdated != listing.LastUpdated)
            {
                con.SentRankings[listing.PlayerId] = lastUpdated;
                return false;
            }

            return true;
        }

        public int InsertRank(List<RankCharacterInfo> Ranking, RankCharacterInfo NewRank)
        {
            if (Ranking.Count == 0)
            {
                Ranking.Add(NewRank);
                return Ranking.Count;
            }

            for (var i = 0; i < Ranking.Count; i++)
            {
                //if level is lower
                if (Ranking[i].level < NewRank.level)
                {
                    Ranking.Insert(i, NewRank);
                    return i + 1;
                }

                //if exp is lower but level = same
                if (Ranking[i].level == NewRank.level && Ranking[i].Experience < NewRank.Experience)
                {
                    Ranking.Insert(i, NewRank);
                    return i + 1;
                }
            }

            Ranking.Add(NewRank);
            return Ranking.Count;
        }

        public bool TryAddRank(List<RankCharacterInfo> Ranking, CharacterInfo info, byte type)
        {
            var NewRank = new RankCharacterInfo() { Name = info.Name, Class = info.Class, Experience = info.Experience, level = info.Level, PlayerId = info.Index, info = info, LastUpdated = Now };
            var NewRankIndex = InsertRank(Ranking, NewRank);
            if (NewRankIndex == 0) return false;
            for (var i = NewRankIndex; i < Ranking.Count; i++)
            {
                SetNewRank(Ranking[i], i + 1, type);
            }
            info.Rank[type] = NewRankIndex;
            return true;
        }

        public int FindRank(List<RankCharacterInfo> Ranking, CharacterInfo info, byte type)
        {
            var startindex = info.Rank[type];
            if (startindex > 0) //if there's a previously known rank then the user can only have gone down in the ranking (or stayed the same)
            {
                for (var i = startindex - 1; i < Ranking.Count; i++)
                {
                    if (Ranking[i].Name == info.Name)
                        return i;
                }
                info.Rank[type] = 0;//set the rank to 0 to tell future searches it's not there anymore
            }
            return -1;//index can be 0
        }

        public bool UpdateRank(List<RankCharacterInfo> Ranking, CharacterInfo info, byte type)
        {
            var CurrentRank = FindRank(Ranking, info, type);
            if (CurrentRank == -1) return false;//not in ranking list atm

            var NewRank = CurrentRank;
            //next find our updated rank
            for (var i = CurrentRank - 1; i >= 0; i--)
            {
                if (Ranking[i].level > info.Level || Ranking[i].level == info.Level && Ranking[i].Experience > info.Experience) break;
                NewRank = i;
            }

            Ranking[CurrentRank].level = info.Level;
            Ranking[CurrentRank].Experience = info.Experience;
            Ranking[CurrentRank].LastUpdated = Now;

            if (NewRank < CurrentRank)
            {//if we gained any ranks
                Ranking.Insert(NewRank, Ranking[CurrentRank]);
                Ranking.RemoveAt(CurrentRank + 1);
                for (var i = NewRank + 1; i < Math.Min(Ranking.Count, CurrentRank + 1); i++)
                {
                    SetNewRank(Ranking[i], i + 1, type);
                }
            }
            info.Rank[type] = NewRank + 1;

            return true;
        }

        public void SetNewRank(RankCharacterInfo Rank, int Index, byte type)
        {
            Rank.LastUpdated = Now;
            if (!(Rank.info is CharacterInfo Player)) return;
            Player.Rank[type] = Index;
        }

        public void RemoveRank(CharacterInfo info)
        {
            List<RankCharacterInfo> Ranking;
            var Rankindex = -1;
            //first check overall top           
            Ranking = RankTop;
            Rankindex = FindRank(Ranking, info, 0);
            if (Rankindex >= 0)
            {
                Ranking.RemoveAt(Rankindex);
                for (var i = Rankindex; i < Ranking.Count(); i++)
                {
                    SetNewRank(Ranking[i], i, 0);
                }
            }

            //next class based top
            Ranking = RankTop;
            Rankindex = FindRank(Ranking, info, 1);
            if (Rankindex >= 0)
            {
                Ranking.RemoveAt(Rankindex);
                for (var i = Rankindex; i < Ranking.Count(); i++)
                {
                    SetNewRank(Ranking[i], i, 1);
                }
            }
        }

        public void CheckRankUpdate(CharacterInfo info)
        {
            List<RankCharacterInfo> Ranking;

            //first check overall top           

            Ranking = RankTop;
            if (!UpdateRank(Ranking, info, 0))
            {
                TryAddRank(Ranking, info, 0);
            }

            //now check class top

            Ranking = RankClass[(byte)info.Class];
            if (!UpdateRank(Ranking, info, 1))
            {
                TryAddRank(Ranking, info, 1);
            }
        }


        public void ReloadNPCs()
        {
            SaveGoods(true);

            Robot.Clear();

            var keys = Scripts.Keys;

            foreach (var key in keys)
            {
                Scripts[key].Load();
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.NpcScriptsReloaded));
        }

        public void ReloadDrops()
        {
            for (var i = 0; i < MonsterInfoList.Count; i++)
            {
                MonsterInfoList[i].Drops.Clear();

                DropInfo.LoadForMonster(MonsterInfoList[i].Drops, MonsterInfoList[i]);
            }

            ReloadSpecialDrops();

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.DropsLoaded));
        }

        /// <summary>The fishing, awakening, strongbox and blackstone lists.</summary>
        private void ReloadSpecialDrops()
        {
            FishingDrops.Clear();
            for (int i = 0; i < 19; i++)
            {
                var path = Path.Combine(Settings.DropPath, Settings.FishingDropFilename + ".txt");
                path = path.Replace("00", i.ToString("D2"));

                DropInfo.Load(FishingDrops, $"Fishing {i}", path, (byte)i, i < 2);
            }

            AwakeningDrops.Clear();
            DropInfo.Load(AwakeningDrops, "Awakening", Path.Combine(Settings.DropPath, Settings.AwakeningDropFilename + ".txt"));

            StrongboxDrops.Clear();
            DropInfo.Load(StrongboxDrops, "StrongBox", Path.Combine(Settings.DropPath, Settings.StrongboxDropFilename + ".txt"));

            BlackstoneDrops.Clear();
            DropInfo.Load(BlackstoneDrops, "Blackstone", Path.Combine(Settings.DropPath, Settings.BlackstoneDropFilename + ".txt"));
        }

        /// <summary>
        /// After a drop list was edited in the web admin (game thread): reloads the drops of everything that loads it, its monsters, the
        /// special lists or the dragon; a list another list includes reloads everything. Returns what was reloaded (for the reply).
        /// </summary>
        /// <param name="includedElsewhere">Another list #INSERTs this one (worked out by the caller, off the game thread).</param>
        public string ReloadDropList(long listId, bool includedElsewhere)
        {
            var list = DropTables.Get(listId);
            if (list == null) return "nothing (the list is gone)";
            if (includedElsewhere)
            {
                ReloadDrops();
                return "every drop list (another list includes this one)";
            }

            // The special lists by their current Settings names (not the kind stored when the list was made), then every monster.
            string special = null;
            switch (DropTables.OwnerOf(list.Name, Enumerable.Empty<(long, string, string)>()).Kind)
            {
                case DropList.Fishing:
                case DropList.Awakening:
                case DropList.Strongbox:
                case DropList.Blackstone:
                    ReloadSpecialDrops();
                    special = "the fishing, awakening, strongbox and blackstone lists";
                    break;
                case DropList.Dragon:
                    if (DragonSystem != null) DragonSystem.Info.LoadDrops();
                    special = DragonSystem != null ? "the dragon's drops" : "nothing for the dragon (it is off)";
                    break;
            }

            var count = 0;
            for (var i = 0; i < MonsterInfoList.Count; i++)
            {
                var m = MonsterInfoList[i];
                if (DropTables.ForMonster(m.Index, m.Name, m.DropPath)?.Id != listId) continue;
                m.Drops.Clear();
                DropInfo.LoadForMonster(m.Drops, m, false);
                count++;
            }

            var monsters = count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " monster(s)";
            return special == null ? monsters : special + " and " + monsters;
        }

        public void ReloadLineMessages()
        {
            LineMessages.Clear();

            var path = Path.Combine(Settings.EnvirPath, "LineMessage.txt");

            if (!File.Exists(path))
            {
                File.WriteAllText(path, "");
            }
            else
            {
                var lines = File.ReadAllLines(path);

                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith(";") || string.IsNullOrWhiteSpace(lines[i])) continue;
                    LineMessages.Add(lines[i]);
                }

                MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.LineMessagesReloaded));
            }
        }

        private WorldMapIcon ValidateWorldMap()
        {
            foreach (WorldMapIcon wmi in Settings.WorldMapSetup.Icons)
            {
                MapInfo info = GetMapInfo(wmi.MapIndex);

                if (info == null)
                    return wmi;
            }
            return null;
        }

        public void DeleteGuild(GuildObject guild)
        {
            Guilds.Remove(guild);
            GuildList.Remove(guild.Info);

            GuildRefreshNeeded = true;
            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.GuildWillBeDeletedFromServer), guild.Info.Name));
        }
    }
}