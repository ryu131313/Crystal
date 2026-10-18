using Client.MirNetwork;
using Client.MirObjects;
using C = ClientPackets;

namespace Client.MirScenes.Models
{
    /// <summary>
    /// The user's guild state that gameplay or several windows read (port of the WebClient/src/game/guild.ts subset). Was
    /// GuildDialog.MyRankId / MyOptions (statics), Ranks, the S.GuildStatus values, GuildBuffInfos and EnabledBuffs;
    /// UserObject.RefreshGuildBuffs adds the active buffs to the user's Stats. GuildDialog forwards its old fields here.
    /// Phase 4: the member-list edits of S.GuildMemberChange (statuses 0, 1, 6, 7, 8, 255: was GuildDialog.MemberStatusChange /
    /// NewRankRecieved / RankChangeRecieved / MyRankChanged / NewMembersList), the notice lines and the request flags, and the
    /// phase 2.5 extras (S.GuildMemberDetails, S.GuildWarList, S.GuildLog) live here; GameScene writes them, both UIs read them.
    /// </summary>
    public sealed class GuildState
    {
        public int MyRankId;
        public GuildRankOptions MyOptions;
        /// <summary>The member list by rank (S.GuildMemberChange status 255).</summary>
        public List<GuildRank> Ranks = new List<GuildRank>();

        /// <summary>S.GuildStatus (was the GuildDialog "DataValues"; the guild name and rank name stay on UserObject).</summary>
        public byte Level;
        public long Experience;
        public long MaxExperience;
        public uint Gold;
        public byte SparePoints;
        public int MemberCount;
        public int MaxMembers;
        public bool Voting;
        public byte ItemCount;
        public byte BuffCount;

        /// <summary>Every buff the server offers (S.GuildBuffList).</summary>
        public readonly List<GuildBuffInfo> BuffInfos = new List<GuildBuffInfo>();
        /// <summary>The guild's bought buffs, active or not.</summary>
        public readonly List<GuildBuff> EnabledBuffs = new List<GuildBuff>();
        /// <summary>The buff info list was asked for once (C.GuildBuffUpdate Action 0).</summary>
        public bool BuffListRequested;

        /// <summary>The notice lines (S.GuildNoticeChange with update != -1; was GuildDialog.Notice.MultiText).</summary>
        public readonly List<string> Notice = new List<string>();
        /// <summary>Was GuildDialog.NoticeChanged: the notice is asked for again (C.RequestGuildInfo 0) when it is shown.</summary>
        public bool NoticeStale = true;
        /// <summary>Was GuildDialog.MembersChanged: the member list is asked for again (C.RequestGuildInfo 1) when it is shown.</summary>
        public bool MembersStale = true;

        public readonly Emitter Changed = new Emitter();
        public readonly Emitter BuffsChanged = new Emitter();
        /// <summary>S.GuildNoticeChange with new lines (after <see cref="Notice"/> was replaced).</summary>
        public readonly Emitter NoticeChanged = new Emitter();
        /// <summary>The member list changed (statuses 0, 1, 6, 7, 8, 255: online flags, ranks, the user's rank or the whole list).</summary>
        public readonly Emitter MembersChanged = new Emitter();

        public void RequestBuffList()
        {
            if (BuffListRequested) return;
            BuffListRequested = true;
            Network.Enqueue(new C.GuildBuffUpdate { Action = 0 });
        }

        /// <summary>Was GuildDialog.FindGuildBuffInfo: asks for the list the first time.</summary>
        public GuildBuffInfo FindBuffInfo(int id)
        {
            if (!BuffListRequested)
                RequestBuffList();

            for (int i = 0; i < BuffInfos.Count; i++)
            {
                if (BuffInfos[i].Id == id)
                    return BuffInfos[i];
            }
            return null;
        }

        /// <summary>Was GuildDialog.FindGuildBuff.</summary>
        public GuildBuff FindBuff(int id)
        {
            for (int i = 0; i < EnabledBuffs.Count; i++)
            {
                if (EnabledBuffs[i] != null && EnabledBuffs[i].Id == id)
                    return EnabledBuffs[i];
            }
            return null;
        }

        // ---- S.GuildMemberChange (the data half of the old GuildDialog methods; GameScene calls these, then IGameUi.GuildMemberChange)

        /// <summary>Statuses 0 / 1 (was GuildDialog.MemberStatusChange).</summary>
        public void SetMemberOnline(string name, bool online)
        {
            for (int i = 0; i < Ranks.Count; i++)
            {
                if (Ranks[i]?.Members == null) continue;
                for (int j = 0; j < Ranks[i].Members.Count; j++)
                    if (Ranks[i].Members[j].Name == name)
                        Ranks[i].Members[j].Online = online;
            }
        }

        /// <summary>Status 255 (was GuildDialog.NewMembersList + the Ranks[i].Index = i of RefreshMemberList).</summary>
        public void ReplaceMembers(List<GuildRank> ranks)
        {
            Ranks = ranks ?? new List<GuildRank>();
            MemberCount = CountMembers(Ranks);
            MembersStale = false;
            Reindex();
        }

        /// <summary>Status 6 (was GuildDialog.NewRankRecieved): the new rank goes in before the last one.</summary>
        public void AddRank(GuildRank rank)
        {
            if (rank == null) return;
            int newIndex = Ranks.Count > 1 ? Ranks.Count - 1 : 1;
            if (newIndex > Ranks.Count) newIndex = Ranks.Count; // the old code threw here on an empty list
            Ranks.Insert(newIndex, rank);
            Reindex();
        }

        /// <summary>
        /// Status 7 (was GuildDialog.RankChangeRecieved): replaces the rank with the same Index. mine = the user is in it (the
        /// rank name and options follow); renamed = its name changed (the rank lists rebuild).
        /// </summary>
        public void ChangeRank(GuildRank rank, out bool mine, out bool renamed)
        {
            mine = false;
            renamed = false;
            if (rank == null) return;
            UserObject user = GameScene.User;
            for (int i = 0; i < Ranks.Count; i++)
            {
                if (Ranks[i] == null || Ranks[i].Index != rank.Index) continue;
                if (user != null && Ranks[i].Name == user.GuildRankName && Ranks[i].Members != null)
                    for (int j = 0; j < Ranks[i].Members.Count; j++)
                        if (Ranks[i].Members[j].Name == user.Name)
                        {
                            user.GuildRankName = rank.Name;
                            MyOptions = rank.Options;
                            mine = true;
                        }
                if (Ranks[i].Name != rank.Name) renamed = true;
                Ranks[i] = rank;
            }
            if (renamed) Reindex();
        }

        /// <summary>
        /// Status 8 (was GuildDialog.MyRankChanged): the user's options, rank name and rank id, and moves the user's member row.
        /// True = the row moved (the windows refresh); false = the list did not hold the user or the new rank (asked again later).
        /// </summary>
        public bool ChangeMyRank(GuildRank rank)
        {
            if (rank == null) return false;
            UserObject user = GameScene.User;
            MyOptions = rank.Options;
            if (user != null) user.GuildRankName = rank.Name;
            int oldRank = MyRankId;
            MyRankId = rank.Index;
            if (user == null || oldRank < 0 || oldRank >= Ranks.Count || Ranks[oldRank]?.Members == null) return false;
            GuildMember member = null;
            for (int i = 0; i < Ranks[oldRank].Members.Count; i++)
                if (Ranks[oldRank].Members[i].Name == user.Name)
                {
                    member = Ranks[oldRank].Members[i];
                    Ranks[oldRank].Members.Remove(member);
                    break;
                }

            if (member == null) return false;
            if (rank.Index < 0 || Ranks.Count <= rank.Index || Ranks[rank.Index]?.Members == null)
            {
                MembersStale = true; // the old code threw adding to the missing rank
                return false;
            }
            Ranks[rank.Index].Members.Add(member);
            return true;
        }

        /// <summary>S.GuildNoticeChange with lines (was GuildDialog.NoticeChange's data).</summary>
        public void SetNotice(List<string> lines)
        {
            Notice.Clear();
            if (lines != null) Notice.AddRange(lines);
            NoticeStale = false;
            NoticeChanged.Emit();
        }

        private void Reindex()
        {
            for (int i = 0; i < Ranks.Count; i++)
                if (Ranks[i] != null) Ranks[i].Index = i;
        }

        private static int CountMembers(List<GuildRank> ranks)
        {
            int total = 0;
            for (int i = 0; i < ranks.Count; i++)
                if (ranks[i]?.Members != null) total += ranks[i].Members.Count;
            return total;
        }

        // ---- phase 2.5 extras (no classic counterpart; the classic window ignores them)

        /// <summary>The server's cooldown on C.RequestGuildMemberDetails / C.RequestGuildLog (a request inside it is ignored).</summary>
        public const long DetailsThrottleMs = 5000;

        /// <summary>S.GuildMemberDetails rows: class, level, LastLogin (DateTime.ToBinary), Online, RankIndex (into Ranks).</summary>
        public readonly List<ClientGuildMemberDetail> MemberDetails = new List<ClientGuildMemberDetail>();
        /// <summary>Parts of a multi-part S.GuildMemberDetails still to come (0 = complete).</summary>
        public int MemberDetailsPending;
        private long _lastDetailsRequest = long.MinValue / 2;
        /// <summary>The guild's wars (S.GuildWarList), each ending at a CMain.Time.</summary>
        public readonly List<GuildWarEntry> Wars = new List<GuildWarEntry>();
        /// <summary>S.GuildLog entries, oldest first (Time = DateTime.ToBinary; Text already localised by the server).</summary>
        public readonly List<ClientGuildLogEntry> Log = new List<ClientGuildLogEntry>();
        private long _lastLogRequest = long.MinValue / 2;

        /// <summary>A part of S.GuildMemberDetails was applied (true = the last part arrived).</summary>
        public readonly Emitter<bool> MemberDetailsChanged = new Emitter<bool>();
        public readonly Emitter WarsChanged = new Emitter();
        public readonly Emitter LogChanged = new Emitter();

        public bool MemberDetailsComplete => MemberDetailsPending == 0;

        /// <summary>C.RequestGuildMemberDetails. False = not in a guild or inside the 5 s throttle (nothing sent).</summary>
        public bool RequestMemberDetails()
        {
            if (string.IsNullOrEmpty(GameScene.User?.GuildName) || CMain.Time - _lastDetailsRequest < DetailsThrottleMs) return false;
            _lastDetailsRequest = CMain.Time;
            Network.Enqueue(new C.RequestGuildMemberDetails());
            return true;
        }

        /// <summary>C.RequestGuildLog. False = not in a guild or inside the 5 s throttle (nothing sent).</summary>
        public bool RequestLog()
        {
            if (string.IsNullOrEmpty(GameScene.User?.GuildName) || CMain.Time - _lastLogRequest < DetailsThrottleMs) return false;
            _lastLogRequest = CMain.Time;
            Network.Enqueue(new C.RequestGuildLog());
            return true;
        }

        /// <summary>Milliseconds left in a war now (0 = over; the server sends a new list when it ends).</summary>
        public static long WarTimeLeft(GuildWarEntry war) => Math.Max(0, war.EndsAt - CMain.Time);

        public void ApplyMemberDetails(byte part, byte partCount, List<ClientGuildMemberDetail> rows)
        {
            if (part == 0)
            {
                MemberDetails.Clear();
                if (rows != null) MemberDetails.AddRange(rows);
            }
            else if (MemberDetailsPending > 0)
            {
                if (rows != null) MemberDetails.AddRange(rows);
            }
            else return; // a stray later part (its part 0 was dropped by a guild change)
            int count = Math.Max(1, (int)partCount);
            MemberDetailsPending = part + 1 >= count ? 0 : count - part - 1;
            MemberDetailsChanged.Emit(MemberDetailsPending == 0);
        }

        public void ApplyWars(List<ClientGuildWar> wars)
        {
            Wars.Clear();
            if (wars != null)
                for (int i = 0; i < wars.Count; i++)
                    Wars.Add(new GuildWarEntry { GuildName = wars[i].GuildName ?? string.Empty, EndsAt = CMain.Time + Math.Max(0, wars[i].MillisecondsLeft) });
            WarsChanged.Emit();
        }

        public void ApplyLog(List<ClientGuildLogEntry> entries)
        {
            Log.Clear();
            if (entries != null) Log.AddRange(entries);
            LogChanged.Emit();
        }

        /// <summary>The user left the guild or moved to another: the extras belong to the old one.</summary>
        public void ClearExtras()
        {
            MemberDetails.Clear();
            MemberDetailsPending = 0;
            _lastDetailsRequest = long.MinValue / 2;
            Log.Clear();
            _lastLogRequest = long.MinValue / 2;
            bool hadWars = Wars.Count > 0;
            Wars.Clear();
            MemberDetailsChanged.Emit(true);
            LogChanged.Emit();
            if (hadWars) WarsChanged.Emit();
        }
    }

    /// <summary>What a S.GuildMemberChange status 7 / 8 changed in <see cref="GuildState"/> (IGameUi.GuildMemberChange).</summary>
    [Flags]
    public enum GuildMemberEdit : byte
    {
        None = 0,
        /// <summary>Status 7: the changed rank is the user's (rank name and options followed).</summary>
        Mine = 1,
        /// <summary>Status 7: the rank's name changed (rank lists rebuild).</summary>
        Renamed = 2,
        /// <summary>Status 8: the user's member row moved to the new rank.</summary>
        Moved = 4,
    }

    /// <summary>One of the guild's wars (S.GuildWarList) with its end on the CMain.Time clock.</summary>
    public sealed class GuildWarEntry
    {
        public string GuildName = string.Empty;
        public long EndsAt;
    }
}
