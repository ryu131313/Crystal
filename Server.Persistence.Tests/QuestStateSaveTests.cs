using Server.MirDatabase;

namespace Server.Persistence.Tests;

/// <summary>
/// Envir.Version 121 character block (wave 2 kick-off): the quest V2 state records and PlayerStatistics round-trip in their
/// documented layout, and the pre-121 legacy quest lists are skipped by their own field reads in both legacy layouts
/// (before version 90 the task counts had no ids), without QuestProgressInfo or loaded quest definitions.
/// </summary>
public class QuestStateSaveTests
{
    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    /// <summary>Legacy CurrentQuests list with two records in the given layout, followed by a sentinel.</summary>
    private static byte[] LegacyCurrentQuests(int version) => Bytes(w =>
    {
        w.Write(2);
        for (int q = 0; q < 2; q++)
        {
            w.Write(10 + q);         // Index
            w.Write(123456789L);     // StartDateTime
            w.Write(987654321L);     // EndDateTime
            if (version < 90)
            {
                w.Write(2); w.Write(5); w.Write(6);           // kill counts
                w.Write(1); w.Write(3);                       // item counts
                w.Write(3); w.Write(true); w.Write(false); w.Write(true); // flag states
            }
            else
            {
                w.Write(2); w.Write(1); w.Write(5); w.Write(2); w.Write(6); // (MonsterID, Count) x2
                w.Write(1); w.Write(7); w.Write(3);                          // (ItemID, Count)
                w.Write(2); w.Write(521); w.Write(true); w.Write(522); w.Write(false); // (flag, state) x2
            }
        }
        w.Write(0x5EED5EED);
    });

    [Theory]
    [InlineData(60)]
    [InlineData(89)]
    [InlineData(90)]
    [InlineData(120)]
    public void Legacy_current_quests_are_skipped_exactly(int version)
    {
        var bytes = LegacyCurrentQuests(version);
        using var r = new BinaryReader(new MemoryStream(bytes));
        CharacterInfo.SkipLegacyCurrentQuests(r, version);
        Assert.Equal(0x5EED5EED, r.ReadInt32());
        Assert.Equal(bytes.Length, r.BaseStream.Position);
    }

    [Fact]
    public void Legacy_completed_quests_are_skipped_exactly()
    {
        var bytes = Bytes(w => { w.Write(3); w.Write(1); w.Write(2); w.Write(3); w.Write(0x5EED5EED); });
        using var r = new BinaryReader(new MemoryStream(bytes));
        CharacterInfo.SkipLegacyCompletedQuests(r);
        Assert.Equal(0x5EED5EED, r.ReadInt32());
    }

    [Fact]
    public void State_records_round_trip_in_the_documented_layout()
    {
        var run = new QuestRunState { Quest = 1, Status = QuestRunStatus.Failed, Step = -1, Revision = 2, AcceptedAt = 3, ExpiresAt = 4, StepExpiresAt = 5, SharedFrom = 6, Branch = 7 };
        var bytes = Bytes(run.Save);
        Assert.Equal(4 + 1 + 4 + 4 + 8 + 8 + 8 + 4 + 4, bytes.Length);
        var back = new QuestRunState(new BinaryReader(new MemoryStream(bytes)));
        Assert.Equal(bytes, Bytes(back.Save));
        Assert.Equal(QuestRunStatus.Failed, back.Status);
        Assert.Equal(-1, back.Step);

        Assert.Equal(12, Bytes(new QuestObjectiveState { Quest = 1, Objective = 2, Value = 3 }.Save).Length);
        Assert.Equal(28, Bytes(new QuestHistoryEntry { Quest = 1, Times = 2, FirstAt = 3, LastAt = 4, LastOutcome = 5 }.Save).Length);
        Assert.Equal(12, Bytes(new QuestVarState { Quest = 1, Key = 2, Value = 3 }.Save).Length);
        var h = new QuestHistoryEntry(new BinaryReader(new MemoryStream(Bytes(new QuestHistoryEntry { Quest = 1, Times = 2, FirstAt = 3, LastAt = 4, LastOutcome = 5 }.Save))));
        Assert.Equal((1, 2, 3L, 4L, 5), (h.Quest, h.Times, h.FirstAt, h.LastAt, h.LastOutcome));
    }

    [Fact]
    public void Statistics_add_max_and_keep_unknown_ids()
    {
        var s = new PlayerStatistics();
        s.Add(PlayerStat.MonstersKilled, 2);
        s.Add(PlayerStat.MonstersKilled, 3);
        s.Add(PlayerStat.Deaths, 0);           // stores nothing
        s.Max(PlayerStat.HighestHit, 50);
        s.Max(PlayerStat.HighestHit, 20);      // lower: ignored
        s.Max(PlayerStat.BestKillStreak, 0);   // not above the implicit 0: stores nothing
        s.Set((PlayerStat)65000, -9);
        Assert.Equal(5, s.Get(PlayerStat.MonstersKilled));
        Assert.Equal(0, s.Get(PlayerStat.Deaths));
        Assert.Equal(50, s.Get(PlayerStat.HighestHit));
        Assert.Equal(3, s.Count);

        var bytes = Bytes(s.Save);
        Assert.Equal(4 + 3 * (2 + 8), bytes.Length); // int count + (ushort id, long value) pairs
        var back = new PlayerStatistics(new BinaryReader(new MemoryStream(bytes)));
        Assert.Equal(-9, back.Get((PlayerStat)65000));
        Assert.Equal(bytes, Bytes(back.Save));
    }

    [Fact]
    public void PlayerStat_values_are_fixed()
    {
        Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(typeof(PlayerStat)));
        Assert.Equal(0, (ushort)PlayerStat.MonstersKilled);
        Assert.Equal(5, (ushort)PlayerStat.QuestsCompleted);
        Assert.Equal(7, (ushort)PlayerStat.PlayersKilled);
        Assert.Equal(14, (ushort)PlayerStat.HighestPkPoints);
        Assert.Equal(15, Enum.GetValues<PlayerStat>().Length);
    }
}
