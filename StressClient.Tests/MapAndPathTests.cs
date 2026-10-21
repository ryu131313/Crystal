using System.Drawing;

namespace StressClient.Tests;

/// <summary>
/// The parser mirrors Server/MirEnvir/Map.cs; these fixtures are built in memory in the same layout the server reads.
/// </summary>
public class MapFileTests
{
    private static byte[] BuildV0(int width, int height)
    {
        const int header = 52, cell = 12;
        var bytes = new byte[header + width * height * cell];
        BitConverter.GetBytes((short)width).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)height).CopyTo(bytes, 2);
        return bytes;
    }

    private static int V0Cell(int x, int y, int height) => 52 + (x * height + y) * 12;

    private static byte[] BuildV100(int width, int height)
    {
        const int header = 8, cell = 26;
        var bytes = new byte[header + width * height * cell];
        bytes[0] = 1;          // version 1
        bytes[1] = 0;
        bytes[2] = 0x43;       // "C#"
        bytes[3] = 0x23;
        BitConverter.GetBytes((short)width).CopyTo(bytes, 4);
        BitConverter.GetBytes((short)height).CopyTo(bytes, 6);
        return bytes;
    }

    private static int V100Cell(int x, int y, int height) => 8 + (x * height + y) * 26;

    [Fact]
    public void V0_AllZeroCells_AreWalkable()
    {
        var grid = MapFile.Load(BuildV0(10, 8));

        Assert.Null(grid.Error);
        Assert.True(grid.Loaded);
        Assert.Equal(0, grid.Type);
        Assert.Equal(10, grid.Width);
        Assert.Equal(8, grid.Height);
        Assert.False(grid.IsBlocked(0, 0));
        Assert.False(grid.IsBlocked(9, 7));
    }

    [Theory]
    [InlineData(0)] // back tile
    [InlineData(2)] // middle tile: LowWall
    [InlineData(4)] // front tile: no floor
    public void V0_WallBits_BlockTheCell(int cellOffset)
    {
        var bytes = BuildV0(4, 4);
        int cell = V0Cell(1, 2, 4);
        BitConverter.GetBytes(unchecked((short)0x8000)).CopyTo(bytes, cell + cellOffset);

        var grid = MapFile.Load(bytes);

        Assert.Null(grid.Error);
        Assert.True(grid.IsBlocked(1, 2));
        Assert.False(grid.IsBlocked(1, 1));
    }

    [Fact]
    public void V0_Door_IsRecordedAndBlocked()
    {
        var bytes = BuildV0(4, 4);
        bytes[V0Cell(2, 1, 4) + 8] = 7;

        var grid = MapFile.Load(bytes);

        Assert.Null(grid.Error);
        Assert.Equal(7, grid.DoorAt(2, 1));
        Assert.True(grid.IsBlocked(2, 1)); // only walkable while the door is open
    }

    [Fact]
    public void V100_IsDetectedAndParsed()
    {
        var bytes = BuildV100(12, 9);
        var grid = MapFile.Load(bytes);

        Assert.Null(grid.Error);
        Assert.True(grid.Loaded);
        Assert.Equal(100, grid.Type);
        Assert.Equal(12, grid.Width);
        Assert.Equal(9, grid.Height);
        Assert.False(grid.IsBlocked(3, 3));
    }

    [Fact]
    public void V100_BackImageAndFrontImage_BlockTheCell()
    {
        var bytes = BuildV100(5, 5);

        int back = V100Cell(1, 1, 5);
        BitConverter.GetBytes(0x20000000).CopyTo(bytes, back + 2);

        int front = V100Cell(3, 3, 5);
        BitConverter.GetBytes(unchecked((short)0x8000)).CopyTo(bytes, front + 12);

        var grid = MapFile.Load(bytes);

        Assert.Null(grid.Error);
        Assert.True(grid.IsBlocked(1, 1));
        Assert.True(grid.IsBlocked(3, 3));
        Assert.False(grid.IsBlocked(2, 2));
    }

    [Fact]
    public void V100_Door_IsRecorded()
    {
        var bytes = BuildV100(5, 5);
        bytes[V100Cell(2, 2, 5) + 14] = 3;

        var grid = MapFile.Load(bytes);

        Assert.Null(grid.Error);
        Assert.Equal(3, grid.DoorAt(2, 2));
        Assert.True(grid.IsBlocked(2, 2));
    }

    [Fact]
    public void V100_UnsupportedVersion_ReportsError()
    {
        var bytes = BuildV100(4, 4);
        bytes[0] = 2; // only version 1 is supported by the server too

        var grid = MapFile.Load(bytes);

        Assert.NotNull(grid.Error);
        Assert.False(grid.Loaded);
    }

    [Fact]
    public void TruncatedFile_ReportsError()
    {
        var bytes = BuildV0(10, 10);
        Array.Resize(ref bytes, 60); // header only, cells missing

        var grid = MapFile.Load(bytes);

        Assert.NotNull(grid.Error);
    }

    [Fact]
    public void OutOfBounds_IsBlocked()
    {
        var grid = MapFile.Load(BuildV0(4, 4));

        Assert.True(grid.IsBlocked(-1, 0));
        Assert.True(grid.IsBlocked(0, -1));
        Assert.True(grid.IsBlocked(4, 0));
        Assert.False(grid.IsBlocked(0, 0));
    }
}

public class PathFinderTests
{
    private static Func<int, int, bool> None => (_, _) => false;

    [Fact]
    public void OpenMap_ReturnsPathFromStartToGoal()
    {
        var path = PathFinder.FindPath(10, 10, None, new Point(0, 0), new Point(5, 5), 4000);

        Assert.NotNull(path);
        Assert.Equal(new Point(0, 0), path[0]);
        Assert.Equal(new Point(5, 5), path[^1]);
        AssertPathIsConnected(path);
    }

    [Fact]
    public void Wall_IsDetoured()
    {
        // Vertical wall at x = 5 from y = 0 to y = 7; the only way through is y = 8 or 9.
        bool Blocked(int x, int y) => x == 5 && y <= 7;

        var path = PathFinder.FindPath(10, 10, Blocked, new Point(0, 0), new Point(9, 0), 4000);

        Assert.NotNull(path);
        Assert.Equal(new Point(9, 0), path[^1]);
        AssertPathIsConnected(path);
        Assert.DoesNotContain(path, p => Blocked(p.X, p.Y));
    }

    [Fact]
    public void EnclosedGoal_HasNoPath()
    {
        bool Blocked(int x, int y) =>
            (x == 4 || x == 6) && y >= 4 && y <= 6 ||
            (y == 4 || y == 6) && x >= 4 && x <= 6;

        var path = PathFinder.FindPath(10, 10, Blocked, new Point(0, 0), new Point(5, 5), 4000);

        Assert.Null(path);
    }

    [Fact]
    public void DynamicBlocker_IsAvoided()
    {
        bool Blocked(int x, int y) => x == 2 && y == 2;

        var path = PathFinder.FindPath(6, 6, Blocked, new Point(0, 0), new Point(5, 5), 4000);

        Assert.NotNull(path);
        Assert.DoesNotContain(new Point(2, 2), path);
        AssertPathIsConnected(path);
    }

    [Fact]
    public void ExpansionCap_AbandonsLongPaths()
    {
        var path = PathFinder.FindPath(200, 200, None, new Point(0, 0), new Point(199, 199), maxNodes: 50);

        Assert.Null(path);
    }

    [Fact]
    public void SameStartAndGoal_ReturnsSingleCell()
    {
        var path = PathFinder.FindPath(5, 5, None, new Point(2, 2), new Point(2, 2), 4000);

        Assert.NotNull(path);
        Assert.Single(path);
        Assert.Equal(new Point(2, 2), path[0]);
    }

    [Fact]
    public void OutOfBoundsGoal_ReturnsNull()
    {
        var path = PathFinder.FindPath(5, 5, None, new Point(0, 0), new Point(5, 5), 4000);
        Assert.Null(path);
    }

    private static void AssertPathIsConnected(List<Point> path)
    {
        for (int i = 1; i < path.Count; i++)
        {
            int dx = Math.Abs(path[i].X - path[i - 1].X);
            int dy = Math.Abs(path[i].Y - path[i - 1].Y);
            Assert.True(dx <= 1 && dy <= 1 && dx + dy > 0, $"step {path[i - 1]} -> {path[i]} is not adjacent");
        }
    }
}
