using System.Drawing;
using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// P9-08 S.ObjectMoveBatch: ushort count, then per move byte (Kind &lt;&lt; 3 | Direction), uint ObjectID, ushort X, ushort Y.
/// Pins the layout WebClient/src/protocol/server.ts decodes and the server's direct ToFrame encoder.
/// </summary>
public class MoveBatchPacketTests
{
    private static ServerPackets.ObjectMoveBatch Sample()
    {
        var p = new ServerPackets.ObjectMoveBatch();
        p.Moves.Add(new ServerPackets.ObjectMoveBatch.Move { Kind = ServerPackets.ObjectMoveBatch.Walk, ObjectID = 77, Location = new Point(123, 456), Direction = MirDirection.DownRight });
        p.Moves.Add(new ServerPackets.ObjectMoveBatch.Move { Kind = ServerPackets.ObjectMoveBatch.Run, ObjectID = 4_000_000_000, Location = new Point(65535, 0), Direction = MirDirection.UpLeft });
        p.Moves.Add(new ServerPackets.ObjectMoveBatch.Move { Kind = ServerPackets.ObjectMoveBatch.Turn, ObjectID = 1, Location = new Point(0, 999), Direction = MirDirection.Up });
        return p;
    }

    [Fact]
    public void Id_is_appended_after_AllowToggles_and_known_to_the_factory()
    {
        Assert.Equal(301, (int)ServerPacketIds.ObjectMoveBatch);
        Assert.IsType<ServerPackets.ObjectMoveBatch>(GetServerPacket((short)ServerPacketIds.ObjectMoveBatch));
    }

    [Fact]
    public void ToFrame_is_byte_identical_to_GetPacketBytes()
    {
        var p = Sample();
        Assert.Equal(p.GetPacketBytes().ToArray(), p.ToFrame());
        var empty = new ServerPackets.ObjectMoveBatch();
        Assert.Equal(empty.GetPacketBytes().ToArray(), empty.ToFrame());
    }

    [Fact]
    public void Round_trips_every_move()
    {
        var p = Sample();
        var copy = new ServerPackets.ObjectMoveBatch();
        Read(copy, Write(p));
        Assert.Equal(p.Moves, copy.Moves);
        Assert.Equal(2 + 9 * 3, Write(p).Length);
    }
}
