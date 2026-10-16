using System.Drawing;

/// <summary>One poison on the player (S.PoisonStatus, phase 2.4). TimeLeft in ms, clamped to 0 or more.</summary>
public sealed class ClientPoisonStatus
{
    public PoisonType Type;
    public int TimeLeft;
    public int Value;

    public ClientPoisonStatus() { }

    public ClientPoisonStatus(BinaryReader reader)
    {
        Type = (PoisonType)reader.ReadUInt16();
        TimeLeft = reader.ReadInt32();
        Value = reader.ReadInt32();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write((ushort)Type);
        writer.Write(TimeLeft < 0 ? 0 : TimeLeft);
        writer.Write(Value);
    }
}

/// <summary>One respawn of a map (S.MapDetails, phase 2.4). MonsterIndex = MonsterInfo.Index.</summary>
public sealed class ClientMapRespawn
{
    public int MonsterIndex;
    public Point Location;
    public ushort Spread;
    public ushort Count;

    public const int WireSize = 16;

    public ClientMapRespawn() { }

    public ClientMapRespawn(BinaryReader reader)
    {
        MonsterIndex = reader.ReadInt32();
        Location = new Point(reader.ReadInt32(), reader.ReadInt32());
        Spread = reader.ReadUInt16();
        Count = reader.ReadUInt16();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(MonsterIndex);
        writer.Write(Location.X);
        writer.Write(Location.Y);
        writer.Write(Spread);
        writer.Write(Count);
    }
}

/// <summary>One safe zone of a map (S.MapDetails, phase 2.4).</summary>
public sealed class ClientMapSafeZone
{
    public Point Location;
    public ushort Size;
    public bool StartPoint;

    public const int WireSize = 11;

    public ClientMapSafeZone() { }

    public ClientMapSafeZone(BinaryReader reader)
    {
        Location = new Point(reader.ReadInt32(), reader.ReadInt32());
        Size = reader.ReadUInt16();
        StartPoint = reader.ReadBoolean();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Location.X);
        writer.Write(Location.Y);
        writer.Write(Size);
        writer.Write(StartPoint);
    }
}

/// <summary>Select-screen details of one character (S.SelectDetails, phase 2.4). PlayTime in seconds.</summary>
public sealed class ClientSelectDetail
{
    public int Index;
    public string GuildName = string.Empty;
    public string MapTitle = string.Empty;
    public long PlayTime;

    public ClientSelectDetail() { }

    public ClientSelectDetail(BinaryReader reader)
    {
        Index = reader.ReadInt32();
        GuildName = reader.ReadString();
        MapTitle = reader.ReadString();
        PlayTime = reader.ReadInt64();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Index);
        writer.Write(GuildName ?? string.Empty);
        writer.Write(MapTitle ?? string.Empty);
        writer.Write(PlayTime);
    }
}

/// <summary>One guild member (S.GuildMemberDetails, phase 2.5). LastLogin = DateTime.ToBinary. RankIndex = index into the guild's ranks.</summary>
public sealed class ClientGuildMemberDetail
{
    public string Name = string.Empty;
    public MirClass Class;
    public ushort Level;
    public long LastLogin;
    public bool Online;
    public int RankIndex;

    /// <summary>Worst case on the wire: a 15-char name of 3-byte UTF-8 chars (1 + 45) + 1 + 2 + 8 + 1 + 4.</summary>
    public const int MaxWireSize = 62;

    public ClientGuildMemberDetail() { }

    public ClientGuildMemberDetail(BinaryReader reader)
    {
        Name = reader.ReadString();
        Class = (MirClass)reader.ReadByte();
        Level = reader.ReadUInt16();
        LastLogin = reader.ReadInt64();
        Online = reader.ReadBoolean();
        RankIndex = reader.ReadInt32();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Name ?? string.Empty);
        writer.Write((byte)Class);
        writer.Write(Level);
        writer.Write(LastLogin);
        writer.Write(Online);
        writer.Write(RankIndex);
    }
}

/// <summary>One war of the guild (S.GuildWarList, phase 2.5). MillisecondsLeft clamped to 0 or more.</summary>
public sealed class ClientGuildWar
{
    public string GuildName = string.Empty;
    public long MillisecondsLeft;

    public ClientGuildWar() { }

    public ClientGuildWar(BinaryReader reader)
    {
        GuildName = reader.ReadString();
        MillisecondsLeft = reader.ReadInt64();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(GuildName ?? string.Empty);
        writer.Write(MillisecondsLeft < 0 ? 0 : MillisecondsLeft);
    }
}

/// <summary>
/// One guild activity log entry (S.GuildLog, phase 2.5). Time = DateTime.ToBinary. Kind = the server's GuildLogKind byte
/// (0 Join, 1 Leave, 2 Kick, 3 RankChange, 4 Notice, 5 Buff, 6 War, 7 Territory, 8 Gold). Text is cut to MaxTextLength chars on write.
/// </summary>
public sealed class ClientGuildLogEntry
{
    public long Time;
    public byte Kind;
    public string Text = string.Empty;

    public const int MaxTextLength = 128;

    public ClientGuildLogEntry() { }

    public ClientGuildLogEntry(BinaryReader reader)
    {
        Time = reader.ReadInt64();
        Kind = reader.ReadByte();
        Text = reader.ReadString();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Time);
        writer.Write(Kind);
        string text = Text ?? string.Empty;
        writer.Write(text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) : text);
    }
}

/// <summary>Extra details of one row of an S.Rankings page (S.RankingsDetail, phase 2.5). ExpPerTenThousand = EXP progress of the level in 1/10000ths (0..10000).</summary>
public sealed class ClientRankingDetail
{
    public long PlayerId;
    public string GuildName = string.Empty;
    public bool Online;
    public ushort ExpPerTenThousand;

    public ClientRankingDetail() { }

    public ClientRankingDetail(BinaryReader reader)
    {
        PlayerId = reader.ReadInt64();
        GuildName = reader.ReadString();
        Online = reader.ReadBoolean();
        ExpPerTenThousand = reader.ReadUInt16();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(PlayerId);
        writer.Write(GuildName ?? string.Empty);
        writer.Write(Online);
        writer.Write(ExpPerTenThousand);
    }
}

/// <summary>One guild of the guild board (S.GuildRankings, phase 2.5). ExpPerTenThousand = EXP progress of the level in 1/10000ths (0..10000).</summary>
public sealed class ClientGuildRanking
{
    public string Name = string.Empty;
    public ushort Level;
    public ushort ExpPerTenThousand;
    public int Members;
    public string Leader = string.Empty;

    public ClientGuildRanking() { }

    public ClientGuildRanking(BinaryReader reader)
    {
        Name = reader.ReadString();
        Level = reader.ReadUInt16();
        ExpPerTenThousand = reader.ReadUInt16();
        Members = reader.ReadInt32();
        Leader = reader.ReadString();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Name ?? string.Empty);
        writer.Write(Level);
        writer.Write(ExpPerTenThousand);
        writer.Write(Members);
        writer.Write(Leader ?? string.Empty);
    }
}

/// <summary>One online player who could be the asker's mentor (S.MentorCandidates, phase 2.5).</summary>
public sealed class ClientMentorCandidate
{
    public string Name = string.Empty;
    public MirClass Class;
    public ushort Level;

    public ClientMentorCandidate() { }

    public ClientMentorCandidate(BinaryReader reader)
    {
        Name = reader.ReadString();
        Class = (MirClass)reader.ReadByte();
        Level = reader.ReadUInt16();
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(Name ?? string.Empty);
        writer.Write((byte)Class);
        writer.Write(Level);
    }
}
