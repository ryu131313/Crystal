using Client.MirNetwork;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Models
{
    /// <summary>
    /// Rankings (port of WebClient/src/game/rankings.ts): the per-tab lists of S.Rankings (was only in RankingDialog.RankList /
    /// Rank / RankCount), the phase 2.5 S.RankingsDetail extras and the guild board (C.GetGuildRanking → S.GuildRankings).
    /// The details caches are static like GameScene.RankingList: the server sends each row only once per connection.
    /// </summary>
    public sealed class RankingsState
    {
        /// <summary>RankType 0..5 = overall, Warrior, Wizard, Taoist, Assassin, Archer (RankingDialog.RankList.Length).</summary>
        public const int RankTypes = 6;

        /// <summary>S.RankingsDetail rows by PlayerId (guild, online, EXP progress), for the whole connection.</summary>
        public static readonly Dictionary<long, ClientRankingDetail> Extras = new Dictionary<long, ClientRankingDetail>();

        private readonly List<RankCharacterInfo>[] _lists = new List<RankCharacterInfo>[RankTypes];
        private readonly int[] _myRank = new int[RankTypes];
        /// <summary>The last S.Rankings Count (whatever its type; RankingDialog.RankCount).</summary>
        public int Count;

        private readonly Dictionary<int, List<ClientGuildRanking>> _guildPages = new Dictionary<int, List<ClientGuildRanking>>();
        /// <summary>The number of ranked guilds (0 until the first S.GuildRankings).</summary>
        public int GuildTotal;

        /// <summary>S.Rankings applied.</summary>
        public readonly Emitter Changed = new Emitter();
        /// <summary>S.RankingsDetail applied (the page's RankType).</summary>
        public readonly Emitter<byte> DetailsChanged = new Emitter<byte>();
        /// <summary>S.GuildRankings applied (the server-clamped page).</summary>
        public readonly Emitter<int> GuildBoardChanged = new Emitter<int>();

        public RankingsState()
        {
            for (int i = 0; i < RankTypes; i++) _lists[i] = new List<RankCharacterInfo>();
        }

        /// <summary>The listings of one tab in rank order from the requested RankIndex (empty for an unknown type).</summary>
        public List<RankCharacterInfo> List(int rankType) => rankType >= 0 && rankType < RankTypes ? _lists[rankType] : _lists[0];
        /// <summary>The user's rank in a tab (0 = not listed).</summary>
        public int MyRank(int rankType) => rankType >= 0 && rankType < RankTypes ? _myRank[rankType] : 0;
        /// <summary>One guild board page (rank of row i = page * 20 + i + 1), or null when not received yet.</summary>
        public List<ClientGuildRanking> GuildPage(int page) => _guildPages.TryGetValue(page, out List<ClientGuildRanking> rows) ? rows : null;
        public static ClientRankingDetail Extra(long playerId) => Extras.TryGetValue(playerId, out ClientRankingDetail row) ? row : null;

        /// <summary>RankingDialog.RequestRanks: C.GetRanking from rankIndex (the first row shown).</summary>
        public static void Request(byte rankType, int rankIndex, bool onlineOnly)
        {
            if (rankType > 6) return;
            Network.Enqueue(new C.GetRanking { RankType = rankType, RankIndex = rankIndex, OnlineOnly = onlineOnly });
        }

        /// <summary>C.GetGuildRanking (0-based page; the server clamps it and rebuilds its board at most every 5 minutes).</summary>
        public static void RequestGuildPage(int page) => Network.Enqueue(new C.GetGuildRanking { Page = Math.Max(0, page) });

        public void ApplyRankings(S.Rankings p, List<RankCharacterInfo> listings)
        {
            if (p.RankType < RankTypes)
            {
                _lists[p.RankType] = listings;
                _myRank[p.RankType] = p.MyRank;
            }
            Count = p.Count;
            Changed.Emit();
        }

        public void ApplyDetails(S.RankingsDetail p)
        {
            for (int i = 0; i < p.Rows.Count; i++)
                Extras[p.Rows[i].PlayerId] = p.Rows[i];
            DetailsChanged.Emit(p.RankType);
        }

        public void ApplyGuildPage(S.GuildRankings p)
        {
            if (p.Total != GuildTotal) _guildPages.Clear(); // a new total = the board was rebuilt: the other pages are stale
            GuildTotal = p.Total;
            _guildPages[p.Page] = p.Guilds;
            GuildBoardChanged.Emit(p.Page);
        }
    }

    /// <summary>
    /// Marriage and mentor (port of WebClient/src/game/relationship.ts). Was only in RelationshipDialog (LoverName, Date, MapName,
    /// MarriedDays) and MentorDialog (MentorName, MentorLevel, MentorOnline, MenteeEXP); GameScene now writes the S.LoverUpdate /
    /// S.MentorUpdate values here before IGameUi.LoverUpdate / MentorUpdate, plus the phase 2.5 S.MentorDetails and the mentor
    /// finder (C.FindMentor → S.MentorCandidates).
    /// </summary>
    public sealed class RelationshipState
    {
        public string LoverName = string.Empty;
        public DateTime MarriedDate;
        public string LoverMap = string.Empty;
        public short MarriedDays;

        public string MentorName = string.Empty;
        public ushort MentorLevel;
        public bool MentorOnline;
        public long MenteeExp;

        /// <summary>S.MentorDetails arrived for the current partner.</summary>
        public bool HasMentorDetails;
        public MirClass PartnerClass;
        /// <summary>DateTime.ToBinary values (EndsAt = MentorDate + the server's MentorLength days).</summary>
        public long MentorDate, MentorEndsAt;

        public readonly List<ClientMentorCandidate> Candidates = new List<ClientMentorCandidate>();
        /// <summary>The server's C.FindMentor cooldown (refusals arrive as chat lines).</summary>
        public const long FindMentorThrottleMs = 3000;
        private long _lastFind = long.MinValue / 2;

        public readonly Emitter LoverChanged = new Emitter();
        public readonly Emitter MentorChanged = new Emitter();
        public readonly Emitter CandidatesChanged = new Emitter();

        public void ApplyLover(S.LoverUpdate p)
        {
            LoverName = p.Name ?? string.Empty;
            MarriedDate = p.Date;
            LoverMap = p.MapName ?? string.Empty;
            MarriedDays = p.MarriedDays;
            LoverChanged.Emit();
        }

        public void ApplyMentor(S.MentorUpdate p)
        {
            string name = p.Name ?? string.Empty;
            // keep the details while the partner is the same (an online / offline update); drop them when it ends or changes
            if (name.Length == 0 || name != MentorName) HasMentorDetails = false;
            MentorName = name;
            MentorLevel = p.Level;
            MentorOnline = p.Online;
            MenteeExp = p.MenteeEXP;
            MentorChanged.Emit();
        }

        public void ApplyMentorDetails(S.MentorDetails p)
        {
            HasMentorDetails = true;
            PartnerClass = p.PartnerClass;
            MentorDate = p.MentorDate;
            MentorEndsAt = p.EndsAt;
            MentorChanged.Emit();
        }

        public void ApplyCandidates(S.MentorCandidates p)
        {
            Candidates.Clear();
            if (p.Candidates != null) Candidates.AddRange(p.Candidates);
            CandidatesChanged.Emit();
        }

        /// <summary>C.FindMentor (level range). False = inside the 3 s throttle (nothing sent).</summary>
        public bool FindMentor(ushort minLevel, ushort maxLevel)
        {
            if (CMain.Time - _lastFind < FindMentorThrottleMs) return false;
            _lastFind = CMain.Time;
            Network.Enqueue(new C.FindMentor { MinLevel = minLevel, MaxLevel = maxLevel });
            return true;
        }
    }

    /// <summary>
    /// The user's poison timers from S.PoisonStatus (port of WebClient/src/game/poisons.ts; the classic UI only knows the
    /// S.Poisoned flags). Each packet is the full set; timers count down locally from the arrival time; S.Poisoned drops the
    /// entries whose flag went away.
    /// </summary>
    public sealed class PoisonTimers
    {
        public readonly List<PoisonTimer> Timers = new List<PoisonTimer>();
        public readonly Emitter Changed = new Emitter();

        public void Apply(List<ClientPoisonStatus> poisons)
        {
            Timers.Clear();
            if (poisons != null)
                for (int i = 0; i < poisons.Count; i++)
                    Timers.Add(new PoisonTimer { Type = poisons[i].Type, Value = poisons[i].Value, EndsAt = CMain.Time + Math.Max(0, poisons[i].TimeLeft) });
            Changed.Emit();
        }

        /// <summary>S.Poisoned for the user: drop the timers whose flag is gone.</summary>
        public void Trim(PoisonType flags)
        {
            int removed = Timers.RemoveAll(t => (flags & t.Type) == 0);
            if (removed > 0) Changed.Emit();
        }

        public PoisonTimer Get(PoisonType type)
        {
            for (int i = 0; i < Timers.Count; i++)
                if (Timers[i].Type == type) return Timers[i];
            return null;
        }

        public static long TimeLeft(PoisonTimer t) => Math.Max(0, t.EndsAt - CMain.Time);
    }

    public sealed class PoisonTimer
    {
        /// <summary>One PoisonType flag.</summary>
        public PoisonType Type;
        /// <summary>The PoisonTime-style payload as the server sent it.</summary>
        public int Value;
        /// <summary>The CMain.Time the poison ends.</summary>
        public long EndsAt;
    }
}
