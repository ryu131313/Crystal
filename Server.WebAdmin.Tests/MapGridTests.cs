using System.Drawing;
using System.Runtime.CompilerServices;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Map memory layout (2026-10-22): respawn cell views, lazy mine spots and shared wall cells must behave like the per-respawn
    /// list copies, the per-cell MineSpot array and the per-cell wall objects they replace.
    /// </summary>
    public class MapGridTests
    {
        private static Map NewMap(int width, int height, Random random, int wallPercent)
        {
            // Map's ctor reads Envir.Main; the grid code only needs the fields set here.
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.Info = new MapInfo();
            map.Width = width;
            map.Height = height;
            map.Cells = new Cell[width, height];
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    map.Cells[x, y] = random.Next(100) < wallPercent
                        ? (random.Next(2) == 0 ? Cell.LowWall : Cell.HighWall)
                        : new Cell { Attribute = CellAttribute.Walk };
            map.GetWalkableCells();
            return map;
        }

        [Fact]
        public void WalkableBox_MatchesTheOldFilteredCopy_ElementForElement()
        {
            var random = new Random(1234);
            foreach (var (w, h, walls) in new[] { (1, 1, 0), (7, 3, 50), (40, 60, 30), (90, 50, 80), (30, 30, 100), (25, 25, 0) })
            {
                var map = NewMap(w, h, random, walls);
                Assert.Equal(map.WalkableCells.Count, map.WalkableCells.Capacity);
                for (int i = 0; i < 300; i++)
                {
                    var info = new RespawnInfo
                    {
                        Location = new Point(random.Next(-10, w + 10), random.Next(-10, h + 10)),
                        Spread = (ushort)random.Next(0, Math.Max(w, h) + 5)
                    };
                    var expected = map.WalkableCells.Where(p =>
                        p.X <= info.Location.X + info.Spread && p.X >= info.Location.X - info.Spread &&
                        p.Y <= info.Location.Y + info.Spread && p.Y >= info.Location.Y - info.Spread).ToList();

                    var box = new WalkableBox(map, info.Location.X - info.Spread, info.Location.Y - info.Spread,
                        info.Location.X + info.Spread, info.Location.Y + info.Spread);

                    Assert.Equal(expected.Count, box.Count);
                    for (int k = 0; k < expected.Count; k++) Assert.Equal(expected[k], box[k]);
                    Assert.Throws<ArgumentOutOfRangeException>(() => box[box.Count]);
                }
            }
        }

        [Fact]
        public void MineGrid_MatchesTheOldPerCellFill()
        {
            var a = new MineSet(1);
            var b = new MineSet(2);
            const int w = 30, h = 20;
            // Old fill: the base set everywhere, then each zone over [Location - Size, Location + Size), later zones winning.
            var zones = new[] { (new Point(5, 5), 3, b), (new Point(6, 6), 2, (MineSet)null), (new Point(28, 18), 4, a), (new Point(-2, 10), 3, b) };
            foreach (var baseSet in new[] { a, null })
            {
                var expected = new MineSet[w, h];
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                        expected[x, y] = baseSet;
                var grid = new MineGrid(w, h, baseSet);
                foreach (var (location, size, set) in zones)
                {
                    for (int x = location.X - size; x < location.X + size; x++)
                        for (int y = location.Y - size; y < location.Y + size; y++)
                            if (x >= 0 && x < w && y >= 0 && y < h) expected[x, y] = set;
                    grid.AddZone(location.X - size, location.Y - size, location.X + size - 1, location.Y + size - 1, set);
                }

                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                    {
                        var spot = grid[x, y];
                        Assert.Equal(expected[x, y], spot?.Mine);
                        if (spot == null) continue;
                        Assert.Equal(0, spot.StonesLeft);
                        Assert.Same(spot, grid[x, y]); // state persists per cell
                    }

                Assert.Throws<IndexOutOfRangeException>(() => grid[w, 0]);
            }
        }

        [Fact]
        public void Cells_HaveNoObjectListUntilUsed_AndSharedWallsTurnIntoOwnCopiesOnAdd()
        {
            var map = NewMap(4, 4, new Random(1), 0);
            map.Cells[2, 3] = Cell.SharedWall(CellAttribute.HighWall, 5);
            Assert.Same(map.Cells[2, 3], Cell.SharedWall(CellAttribute.HighWall, 5));
            Assert.Equal(5, map.Cells[2, 3].FishingAttribute);

            var walk = map.Cells[0, 0];
            Assert.Empty(walk.Objects);

            var spell = (SpellObject)RuntimeHelpers.GetUninitializedObject(typeof(SpellObject));
            spell.CurrentMap = map;
            spell.CurrentLocation = new Point(0, 0);
            walk.Add(spell);
            Assert.Single(walk.Objects);
            walk.Remove(spell);
            Assert.Empty(walk.Objects);

            var shared = map.Cells[2, 3];
            spell.CurrentLocation = new Point(2, 3);
            shared.Add(spell);
            Assert.Empty(shared.Objects);
            Assert.NotSame(shared, map.Cells[2, 3]);
            Assert.False(map.Cells[2, 3].IsShared);
            Assert.Equal(CellAttribute.HighWall, map.Cells[2, 3].Attribute);
            Assert.Equal(5, map.Cells[2, 3].FishingAttribute);
            Assert.Same(spell, Assert.Single(map.Cells[2, 3].Objects));
        }
    }
}
