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
