namespace Client.MirScenes.Models
{
    /// <summary>
    /// The last inspected player (port of the WebClient/src/game/inspect.ts subset). Was InspectDialog.Items and InspectID
    /// (statics): the MirGridType.Inspect grid and the id the next S.PlayerInspect answers;
    /// and InspectDialog's Name / GuildName / GuildRank / Class / Gender / Hair / Level / LoverName / AllowObserve.
    /// </summary>
    public sealed class InspectState
    {
        public UserItem[] Items = new UserItem[14];
        public uint InspectID;

        /// <summary>The S.PlayerInspect values (was InspectDialog's fields; the item tooltip reads Level / Class).</summary>
        public string Name;
        public string GuildName;
        public string GuildRank;
        public MirClass Class;
        public MirGender Gender;
        public byte Hair;
        public ushort Level;
        public string LoverName;
        public bool AllowObserve;

        public readonly Emitter Changed = new Emitter();
    }
}
