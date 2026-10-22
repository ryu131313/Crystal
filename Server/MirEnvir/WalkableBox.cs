using System.Drawing;

namespace Server.MirEnvir
{
    /// <summary>
    /// The walkable cells of a map inside a box, as a read-only view over <see cref="Map.WalkableCells"/>: element i is the i-th
    /// map walkable cell inside the box, in the map list's x-major, y-minor order (what the old per-respawn
    /// <c>WalkableCells.Where(in box).ToList()</c> copy held, without the copy). Holds two ints per non-empty column of the box.
    /// Immutable after construction, so mob threads may read it.
    /// </summary>
    public sealed class WalkableBox
    {
        private static readonly int[] NoColumns = new int[0];

        private readonly List<Point> _cells;
        // Per non-empty column of the box: the index of its first in-box cell in _cells, and the number of in-box cells in the
        // columns before it (ascending, starts at 0).
        private readonly int[] _start;
        private readonly int[] _before;

        public readonly int Count;

        /// <summary>The map's walkable cells with left &lt;= X &lt;= right and top &lt;= Y &lt;= bottom.</summary>
        public WalkableBox(Map map, int left, int top, int right, int bottom)
        {
            _cells = map.WalkableCells;
            _start = _before = NoColumns;
            var columnStart = map.WalkableColumnStart;
            if (_cells == null || columnStart == null) return;

            if (left < 0) left = 0;
            if (right > map.Width - 1) right = map.Width - 1;
            if (left > right || top > bottom) return;

            var start = new int[right - left + 1];
            var before = new int[start.Length];
            var columns = 0;
            var count = 0;
            for (int x = left; x <= right; x++)
            {
                int first = LowerBound(_cells, columnStart[x], columnStart[x + 1], top);
                int end = LowerBound(_cells, first, columnStart[x + 1], bottom + 1);
                if (end <= first) continue;

                start[columns] = first;
                before[columns] = count;
                columns++;
                count += end - first;
            }

            if (columns == 0) return;
            if (columns < start.Length)
            {
                Array.Resize(ref start, columns);
                Array.Resize(ref before, columns);
            }

            _start = start;
            _before = before;
            Count = count;
        }

        public Point this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));

                // The last column whose first in-box cell comes at or before index.
                int lo = 0, hi = _before.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (_before[mid] <= index) lo = mid;
                    else hi = mid - 1;
                }

                return _cells[_start[lo] + index - _before[lo]];
            }
        }

        // First index in [from, to) of one column's cells (sorted by Y) whose Y >= y.
        private static int LowerBound(List<Point> cells, int from, int to, int y)
        {
            while (from < to)
            {
                int mid = (from + to) >> 1;
                if (cells[mid].Y < y) from = mid + 1;
                else to = mid;
            }

            return from;
        }
    }
}
