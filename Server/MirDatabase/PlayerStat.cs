namespace Server.MirDatabase
{
    /// <summary>
    /// Per-character statistics (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part A "Player statistics"). Server-only
    /// persisted enum: saved by ushort value in CharacterInfo.Statistics (Envir.Version 121 on). Explicit values, append only,
    /// never renumbered or reused; a new member needs no version bump (unknown ids are kept and written back).
    /// </summary>
    public enum PlayerStat : ushort
    {
        MonstersKilled = 0,
        Deaths = 1,
        GoldEarned = 2,
        GoldSpent = 3,
        ItemsCrafted = 4,
        QuestsCompleted = 5,
        HighestHit = 6,
        PlayersKilled = 7,
        DeathsToPlayers = 8,
        BestKillStreak = 9,
        Murders = 10,
        GuildWarKills = 11,
        PvpDamageDealt = 12,
        PvpDamageTaken = 13,
        HighestPkPoints = 14,
    }
}
