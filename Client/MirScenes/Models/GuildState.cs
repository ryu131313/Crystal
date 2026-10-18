using Client.MirNetwork;
using C = ClientPackets;

namespace Client.MirScenes.Models
{
    /// <summary>
    /// The user's guild state that gameplay or several windows read (port of the WebClient/src/game/guild.ts subset). Was
    /// GuildDialog.MyRankId / MyOptions (statics), Ranks, the S.GuildStatus values, GuildBuffInfos and EnabledBuffs;
    /// UserObject.RefreshGuildBuffs adds the active buffs to the user's Stats. GuildDialog forwards its old fields here.
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

        public readonly Emitter Changed = new Emitter();
        public readonly Emitter BuffsChanged = new Emitter();

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
    }
}
