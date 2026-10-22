using System.Drawing;
using System.Runtime.CompilerServices;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Map memory layout (2026-10-22): respawn cell views, lazy mine spots and the byte cell grid with lazy object lists must behave
    /// like the per-respawn list copies, the per-cell MineSpot array and the Cell[,] of objects they replace.
    /// </summary>
    public class MapGridTests
    {
        private static Map NewMap(int width, int height, Random random, int wallPercent)
        {
            // Map's ctor reads Envir.Main; the grid code only needs the fields set here.
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.Info = new MapInfo();
            var data = new byte[width * height];
            for (int i = 0; i < data.Length; i++)
                data[i] = random.Next(100) < wallPercent
                    ? (byte)(random.Next(2) == 0 ? CellAttribute.LowWall : CellAttribute.HighWall)
                    : (byte)CellAttribute.Walk;
            map.InitCells(width, height, data);
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

        private static SpellObject NewSpell()
        {
            return (SpellObject)RuntimeHelpers.GetUninitializedObject(typeof(SpellObject));
        }

        [Fact]
        public void CellGrid_ReadsAttributesAndFishing_AndThrowsOutsideTheMap()
        {
            // 9 x 11: neither side a multiple of 8, and the cell count (99) is not a multiple of the 64-cell object blocks.
            const int w = 9, h = 11;
            var data = new byte[w * h];
            data[2 * h + 3] = (byte)CellAttribute.HighWall | ((5 + 1) << 2);
            data[8 * h + 10] = (byte)CellAttribute.LowWall;
            data[0 * h + 1] = (byte)CellAttribute.Walk | ((19 + 1) << 2);
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.InitCells(w, h, data);

            Assert.Equal(CellAttribute.HighWall, map.GetCell(2, 3).Attribute);
            Assert.Equal(5, map.GetCell(2, 3).FishingAttribute);
            Assert.False(map.GetCell(2, 3).Valid);
            Assert.False(map.ValidPoint(8, 10));
            Assert.Equal(CellAttribute.LowWall, map.GetCell(new Point(8, 10)).Attribute);
            Assert.Equal(19, map.GetCell(0, 1).FishingAttribute);
            Assert.True(map.GetCell(0, 1).Valid);
            Assert.Equal(-1, map.GetCell(4, 4).FishingAttribute);
            Assert.True(map.ValidPoint(4, 4));

            Assert.False(map.ValidPoint(-1, 0));
            Assert.False(map.ValidPoint(0, h));
            Assert.False(map.ValidPoint(w, 0));
            Assert.Throws<IndexOutOfRangeException>(() => map.GetCell(0, h)); // would alias cell (1, 0) without the check
            Assert.Throws<IndexOutOfRangeException>(() => map.GetCell(w, 0));
            Assert.Throws<IndexOutOfRangeException>(() => map.GetCell(-1, 0));
        }

        [Fact]
        public void CellObjects_AreCreatedOnFirstAdd_OnWallsToo_AndSeenThroughEarlierHandles()
        {
            var data = new byte[9 * 11];
            data[2 * 11 + 3] = (byte)CellAttribute.HighWall;
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.InitCells(9, 11, data);

            var walk = map.GetCell(0, 0);
            Assert.Empty(walk.Objects);
            Assert.Same(walk.Objects, map.GetCell(5, 5).Objects); // the shared empty list

            var spell = NewSpell();
            walk.Add(spell);
            Assert.Same(spell, Assert.Single(walk.Objects)); // a handle taken before the first Add sees the object
            Assert.Same(walk.Objects, map.GetCell(0, 0).Objects);
            Assert.Empty(map.GetCell(0, 1).Objects);
            var list = walk.Objects;
            walk.Remove(spell);
            Assert.Empty(walk.Objects);
            Assert.Same(list, walk.Objects); // the list is kept

            var wall = map.GetCell(2, 3);
            var other = NewSpell();
            wall.Add(other);
            Assert.Same(other, Assert.Single(map.GetCell(2, 3).Objects));
            Assert.Equal(CellAttribute.HighWall, map.GetCell(2, 3).Attribute);

            var last = NewSpell();
            map.GetCell(8, 10).Add(last); // index 98: the last, partial block
            Assert.Same(last, Assert.Single(map.GetCell(8, 10).Objects));
        }

        [Fact]
        public void ObjectCells_VisitsCellsWithLists_InXMajorOrder()
        {
            var random = new Random(7);
            const int w = 37, h = 23;
            var map = NewMap(w, h, random, 30);
            var expected = new List<Point>();
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    if (random.Next(10) == 0)
                    {
                        map.GetCell(x, y).Add(NewSpell());
                        expected.Add(new Point(x, y));
                    }

            var seen = new List<Point>();
            foreach (var cell in map.ObjectCells())
            {
                var ob = Assert.Single(cell.Objects);
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                        if (map.GetCell(x, y).Objects.Count > 0 && map.GetCell(x, y).Objects[0] == ob)
                            seen.Add(new Point(x, y));
            }

            Assert.Equal(expected, seen);
            Assert.False(NewMap(5, 5, random, 0).ObjectCells().MoveNext());
        }
    }
}
