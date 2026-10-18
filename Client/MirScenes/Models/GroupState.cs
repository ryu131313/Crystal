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

        public readonly Emitter Changed = new Emitter();

        public bool Contains(string name)
        {
            return name != null && Members.Contains(name);
        }
    }
}
