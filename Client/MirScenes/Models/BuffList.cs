namespace Client.MirScenes.Models
{
    /// <summary>
    /// The user's (or the hero's) own buffs with timers and stats (S.AddBuff / RemoveBuff / PauseBuff). Was BuffDialog.Buffs;
    /// UserObject.RefreshBuffs reads it for Stats, Sprint and TransformType, so it must not depend on a window existing.
    /// The classic BuffDialog renders <see cref="Items"/> (the same list instance).
    /// </summary>
    public sealed class BuffList
    {
        public readonly List<ClientBuff> Items = new List<ClientBuff>();
        public readonly Emitter Changed = new Emitter();

        public bool Has(BuffType type)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Type == type) return true;
            return false;
        }
    }
}
