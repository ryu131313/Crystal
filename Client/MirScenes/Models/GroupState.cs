using S = ServerPackets;

namespace Client.MirScenes.Models
{
    /// <summary>
    /// The group (port of WebClient/src/game/group.ts). Was the GroupDialog statics GroupList / GroupMembersMap / AllowGroup
    /// and BigMapViewPort.PlayerLocations;
    /// MapObject name colours, monster / hero ownership checks and the minimaps read it.
    /// </summary>
    public sealed class GroupState
    {
        /// <summary>Member names in server order; [0] is the leader.</summary>
        public readonly List<string> Members = new List<string>();
        /// <summary>Member name → map title (S.GroupMembersMap).</summary>
        public readonly Dictionary<string, string> MemberMaps = new Dictionary<string, string>();
        public bool AllowGroup;
        /// <summary>Member name → location on the user's map (S.SendMemberLocation; was BigMapViewPort.PlayerLocations).</summary>
        public readonly Dictionary<string, Point> MemberLocations = new Dictionary<string, Point>();
        /// <summary>Member name → the last S.GroupMemberStatus (class, level, HP / MP %, buffs; phase 2.4, at most 1 / s per member).</summary>
        public readonly Dictionary<string, S.GroupMemberStatus> MemberStatus = new Dictionary<string, S.GroupMemberStatus>();

        public readonly Emitter Changed = new Emitter();
        /// <summary>A member's status arrived (the name; <see cref="Changed"/> is not fired: the roster did not change).</summary>
        public readonly Emitter<string> StatusChanged = new Emitter<string>();

        public bool Contains(string name)
        {
            return name != null && Members.Contains(name);
        }
    }
}
