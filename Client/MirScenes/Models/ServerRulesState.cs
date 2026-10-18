namespace Client.MirScenes.Models
{
    /// <summary>
    /// The live server rules (S.ServerRules, sent after login and again on StartGame): refine / awakening chances and costs,
    /// mentor, marriage, mail, territory costs, class gating and feature switches (port of WebClient/src/game/serverRules.ts).
    /// Windows read <see cref="Rules"/> for costs and limits instead of hard-coding them; null until the packet arrives.
    /// </summary>
    public sealed class ServerRulesState
    {
        public ServerPackets.ServerRules Rules;
        public readonly Emitter Changed = new Emitter();

        public void Set(ServerPackets.ServerRules rules)
        {
            if (rules == null) return;
            Rules = rules;
            Changed.Emit();
        }
    }

    /// <summary>
    /// The hero's spells with auto-cast off (S.HeroAutoCast: the full list, sent on hero load and after C.HeroSpellToggle).
    /// </summary>
    public sealed class HeroAutoCastState
    {
        public readonly HashSet<Spell> Disabled = new HashSet<Spell>();
        public readonly Emitter Changed = new Emitter();

        public bool AutoCast(Spell spell) => !Disabled.Contains(spell);

        public void Set(List<Spell> disabled)
        {
            Disabled.Clear();
            if (disabled != null)
                for (int i = 0; i < disabled.Count; i++) Disabled.Add(disabled[i]);
            Changed.Emit();
        }
    }

    /// <summary>
    /// The user's own social allow toggles (S.AllowToggles: a bit per AllowToggle — group, trade, observe, guild invite, group
    /// recall, marriage, lover recall, mentor, friend requests). Changed with C.SetAllowToggle; the server always echoes.
    /// </summary>
    public sealed class AllowTogglesState
    {
        public ushort Flags;
        /// <summary>False until the first S.AllowToggles (windows show the switches disabled until then).</summary>
        public bool Known;
        public readonly Emitter Changed = new Emitter();

        public bool Get(AllowToggle toggle) => (Flags & (1 << (int)toggle)) != 0;

        public void Set(ushort flags)
        {
            Flags = flags;
            Known = true;
            Changed.Emit();
        }
    }
}
