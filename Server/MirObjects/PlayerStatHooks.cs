using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.MirObjects
{
    /// <summary>
    /// The <see cref="PlayerStatistics"/> hooks (UserData-and-Quests plan, Part A "Player statistics", lane G). Game thread only:
    /// every caller is player, pet or death code that runs outside a mob pass; the per-hit hooks also return during a mob pass,
    /// so a reflect inside monster AI can never write a CharacterInfo from a mob thread. No allocation once a counter exists.
    /// Heroes have no statistics: a hero's or a pet's credit goes to the owning player (<see cref="CreditedPlayer"/>).
    /// </summary>
    public static class PlayerStatHooks
    {
        /// <summary>The player credited for <paramref name="ob"/>: the player itself, a hero's owner or a pet's master (also a
        /// hero pet's owner); null for anything else.</summary>
        public static PlayerObject CreditedPlayer(MapObject ob)
        {
            if (ob == null) return null;
            if (ob.Race == ObjectType.Monster) ob = ob.Master;
            if (ob != null && ob.Race == ObjectType.Hero) ob = ((HeroObject)ob).Owner;
            return ob != null && ob.Race == ObjectType.Player ? (PlayerObject)ob : null;
        }

        /// <summary>A monster kill (credit = the monster's EXP owner, the same credit as exp and quest kills).</summary>
        public static void MonsterKilled(MapObject expOwner)
        {
            var player = CreditedPlayer(expOwner);
            if (player == null || player.Info == null) return;
            if (player.Node == null) Envir.MarkAccountDirty(player.Info.AccountInfo); // a stale owner who logged out
            player.Info.Statistics.Add(PlayerStat.MonstersKilled, 1);
        }

        /// <summary>A player's own hit on anything (highest damage hit).</summary>
        public static void Hit(PlayerObject attacker, int damage)
        {
            if (damage <= 0 || Envir.Main.InMobPass) return;
            attacker.Info.Statistics.Max(PlayerStat.HighestHit, damage);
        }

        /// <summary>A hit between two players (a hero's hit arrives here as its owner's).</summary>
        public static void PvpHit(PlayerObject attacker, PlayerObject target, int damage)
        {
            if (damage <= 0 || attacker == target || Envir.Main.InMobPass) return;
            attacker.Info.Statistics.Add(PlayerStat.PvpDamageDealt, damage);
            attacker.Info.Statistics.Max(PlayerStat.HighestHit, damage);
            target.Info.Statistics.Add(PlayerStat.PvpDamageTaken, damage);
        }

        /// <summary>A player's pet hits a player: damage dealt for the pet's master, taken for the target.</summary>
        public static void PvpPetHit(MonsterObject pet, PlayerObject target, int damage)
        {
            if (damage <= 0 || Envir.Main.InMobPass) return;
            var master = CreditedPlayer(pet);
            if (master == null || master == target) return;
            master.Info.Statistics.Add(PlayerStat.PvpDamageDealt, damage);
            target.Info.Statistics.Add(PlayerStat.PvpDamageTaken, damage);
        }

        /// <summary>
        /// A player died (after a revival ring had its chance). <paramref name="killer"/> = <see cref="CreditedPlayer"/> of the last
        /// hitter, possibly a stale reference to a player who logged out. The current kill streak is runtime-only.
        /// </summary>
        public static void PlayerDied(PlayerObject victim, PlayerObject killer)
        {
            victim.Info.Statistics.Add(PlayerStat.Deaths, 1);
            victim.KillStreak = 0;

            if (killer == null || killer == victim || killer.Info == null) return;

            Envir.MarkAccountDirty(killer.Info.AccountInfo); // the killer may be offline (stale LastHitter)
            victim.Info.Statistics.Add(PlayerStat.DeathsToPlayers, 1);

            var stats = killer.Info.Statistics;
            stats.Add(PlayerStat.PlayersKilled, 1);
            killer.KillStreak++;
            stats.Max(PlayerStat.BestKillStreak, killer.KillStreak);

            if (victim.MyGuild != null && killer.MyGuild != null && victim.MyGuild != killer.MyGuild && victim.MyGuild.WarringGuilds.Contains(killer.MyGuild))
                stats.Add(PlayerStat.GuildWarKills, 1);
        }

        /// <summary>A kill that added PK points to <paramref name="killer"/> (already marked dirty by the PK code).</summary>
        public static void Murder(PlayerObject killer)
        {
            killer.Info.Statistics.Add(PlayerStat.Murders, 1);
            killer.Info.Statistics.Max(PlayerStat.HighestPkPoints, killer.PKPoints);
        }
    }
}
