using System.Drawing;
using Server.MirObjects;

namespace Server.MirEnvir
{
    /// <summary>
    /// Players of one map bucketed by 8×8-cell sector (P9-07, [Optimisation] SectorGrid), so broadcasts and "is anyone
    /// near?" checks look at nearby players instead of every player on the map.
    /// <para>
    /// Membership follows <see cref="Map.Players"/> exactly: added and removed in <see cref="Map.AddObject"/> /
    /// <see cref="Map.RemoveObject"/>, and moved by the <see cref="HumanObject.CurrentLocation"/> setter. Written on the game
    /// thread only: a move made by monster AI during the mob pass (a push) is queued and applied right after the barrier
    /// (<see cref="Envir.ApplyGridFixups"/>), and readers scan one sector of slack while a mob pass runs. Readers on mob
    /// threads only read, during the mob pass, when the game thread does not write it.
    /// </para>
    /// </summary>
    public sealed class PlayerGrid
    {
        public const int Shift = 3;
        public const int Size = 1 << Shift;

        /// <summary>Readers use the grid only above this many players on the map; a short linear scan is cheaper.</summary>
        public const int MinPlayers = 16;

        /// <summary>Ring, in sectors, that the dormant-monster wake covers around a player (2 × DataRange, rounded up).</summary>
        public static readonly int WakeSectors = (2 * Globals.DataRange + Size - 1) / Size;

        private readonly Map _map;
        private readonly int _cols, _rows;
        private List<PlayerObject>[] _sectors; // allocated on the first player; most maps never get one

        public PlayerGrid(Map map)
        {
            _map = map;
            _cols = Math.Max(1, (map.Width + Size - 1) >> Shift);
            _rows = Math.Max(1, (map.Height + Size - 1) >> Shift);
        }

        public int Columns => _cols;
        public int Rows => _rows;

        public int SectorX(int x) => Math.Clamp(x >> Shift, 0, _cols - 1);
        public int SectorY(int y) => Math.Clamp(y >> Shift, 0, _rows - 1);
        public int IndexOf(Point p) => SectorY(p.Y) * _cols + SectorX(p.X);

        /// <summary>The players in sector (sx, sy), or null when it never had one. Read-only for callers.</summary>
        public List<PlayerObject> Sector(int sx, int sy) => _sectors?[sy * _cols + sx];

        internal void Add(PlayerObject player)
        {
            // Monster code can move a player between maps during the mob pass (a death script's MOVE): never write the
            // lists or wake monsters from a mob thread, resync after the barrier instead (review M1).
            if (Envir.Main.InMobPass) { QueueResync(player); return; }

            player.GridMap?.Grid.RemoveNow(player); // a stale entry from a missed RemoveObject (review L1)
            _sectors ??= new List<PlayerObject>[_cols * _rows];
            var index = IndexOf(player.CurrentLocation);
            (_sectors[index] ??= new List<PlayerObject>(4)).Add(player);
            player.GridMap = _map;
            player.GridSector = index;
            WakeAround(-1, index);
        }

        internal void Remove(PlayerObject player)
        {
            if (Envir.Main.InMobPass) { QueueResync(player); return; }
            RemoveNow(player);
        }

        private void RemoveNow(PlayerObject player)
        {
            if (player.GridMap != _map) return;
            if (player.GridSector >= 0) RemoveFrom(player.GridSector, player);
            player.GridMap = null;
            player.GridSector = -1;
        }

        /// <summary>Called by the location setter. Defers to after the barrier when a mob pass is running.</summary>
        internal void Moved(PlayerObject player)
        {
            var index = IndexOf(player.CurrentLocation);
            if (index == player.GridSector) return;

            if (Envir.Main.InMobPass) { QueueResync(player); return; }

            var old = player.GridSector;
            if (old >= 0) RemoveFrom(old, player);
            (_sectors[index] ??= new List<PlayerObject>(4)).Add(player);
            player.GridSector = index;
            WakeAround(old, index);
        }

        private static void QueueResync(PlayerObject player)
        {
            if (Interlocked.Exchange(ref player.GridFixPending, 1) == 0) Envir.Main.QueueGridFixup(player);
        }

        /// <summary>
        /// Game thread, after the barrier: puts <paramref name="player"/> where it belongs now (its current map's grid if it
        /// is in that map's player list, else nowhere), whatever adds, removes and moves were deferred during the pass.
        /// </summary>
        internal static void Resync(PlayerObject player)
        {
            var map = player.CurrentMap;
            var belongs = map?.Grid != null && map.Players.Contains(player);
            if (belongs && player.GridMap == map)
            {
                map.Grid.Moved(player);
                return;
            }
            player.GridMap?.Grid.RemoveNow(player);
            if (belongs) map.Grid.Add(player);
        }

        private void RemoveFrom(int index, PlayerObject player)
        {
            var list = _sectors[index];
            if (list == null) return;
            var i = list.IndexOf(player);
            if (i < 0) return;
            var last = list.Count - 1;
            list[i] = list[last]; // order inside a sector doesn't matter
            list.RemoveAt(last);
        }

        /// <summary>
        /// [Optimisation] MonsterDormant: wakes alone monsters in the sectors a player newly covers (its 2 × DataRange ring
        /// at <paramref name="newIndex"/> minus the ring at <paramref name="oldIndex"/>; -1 = the whole ring), so a monster
        /// asleep for its long alone check reacts at once. Game thread, outside the mob pass (monster fields are free).
        /// </summary>
        private void WakeAround(int oldIndex, int newIndex)
        {
            if (!Settings.MonsterDormant) return;

            int nx = newIndex % _cols, ny = newIndex / _cols;
            int nx0 = Math.Max(0, nx - WakeSectors), nx1 = Math.Min(_cols - 1, nx + WakeSectors);
            int ny0 = Math.Max(0, ny - WakeSectors), ny1 = Math.Min(_rows - 1, ny + WakeSectors);

            int ox0 = 1, ox1 = 0, oy0 = 1, oy1 = 0; // empty
            if (oldIndex >= 0)
            {
                int ox = oldIndex % _cols, oy = oldIndex / _cols;
                ox0 = ox - WakeSectors; ox1 = ox + WakeSectors;
                oy0 = oy - WakeSectors; oy1 = oy + WakeSectors;
            }

            for (var sy = ny0; sy <= ny1; sy++)
            {
                var rowCovered = sy >= oy0 && sy <= oy1;
                for (var sx = nx0; sx <= nx1; sx++)
                {
                    if (rowCovered && sx >= ox0 && sx <= ox1) continue; // already in the old ring
                    WakeSector(sx, sy);
                }
            }
        }

        private void WakeSector(int sx, int sy)
        {
            int x0 = sx << Shift, y0 = sy << Shift;
            int x1 = Math.Min(_map.Width, x0 + Size), y1 = Math.Min(_map.Height, y0 + Size);
            var cells = _map.Cells;
            for (var x = x0; x < x1; x++)
            {
                for (var y = y0; y < y1; y++)
                {
                    var objects = cells[x, y]?.Objects;
                    if (objects == null) continue;
                    for (var i = objects.Count - 1; i >= 0; i--)
                        if (objects[i] is MonsterObject monster) monster.WakeFromAlone();
                }
            }
        }

        /// <summary>
        /// [Optimisation] SectorGridVerify: compares every player's sector with its location and the map's player list.
        /// Returns the number of mismatches. Game thread, outside the mob pass.
        /// </summary>
        internal int Verify()
        {
            var bad = 0;
            var registered = 0;
            if (_sectors != null)
                for (var i = 0; i < _sectors.Length; i++)
                {
                    var list = _sectors[i];
                    if (list == null) continue;
                    registered += list.Count;
                    for (var j = 0; j < list.Count; j++)
                    {
                        var p = list[j];
                        if (p.GridMap != _map || p.GridSector != i || p.CurrentMap != _map) bad++;
                        else if (IndexOf(p.CurrentLocation) != i && p.GridFixPending == 0) bad++;
                    }
                }
            if (registered != _map.Players.Count) bad++;
            for (var i = 0; i < _map.Players.Count; i++)
                if (_map.Players[i].GridMap != _map) bad++;
            return bad;
        }
    }

    /// <summary>
    /// Allocation-free enumeration of the players a reader must check around a point (P9-07): the sectors covering
    /// <c>range</c> (plus one of slack during a mob pass) on a busy map with a grid, otherwise the whole player list.
    /// Callers still check the exact range. Iterates backwards by index, like the loops it replaces.
    /// </summary>
    public struct PlayersNear
    {
        private readonly Map _map;
        private readonly PlayerGrid _grid;
        private readonly int _sx0, _sx1, _sy1;
        private int _sx, _sy;
        private List<PlayerObject> _list;
        private int _i;

        public PlayersNear(Map map, Point center, int range)
        {
            _map = map;
            var grid = map.Grid;
            if (grid != null && map.Players.Count > PlayerGrid.MinPlayers)
            {
                _grid = grid;
                var slack = Envir.Main.InMobPass ? 1 : 0;
                _sx0 = Math.Max(0, grid.SectorX(center.X - range) - slack);
                _sx1 = Math.Min(grid.Columns - 1, grid.SectorX(center.X + range) + slack);
                var sy0 = Math.Max(0, grid.SectorY(center.Y - range) - slack);
                _sy1 = Math.Min(grid.Rows - 1, grid.SectorY(center.Y + range) + slack);
                _sx = _sx0 - 1;
                _sy = sy0;
                _list = null;
            }
            else
            {
                _grid = null;
                _sx0 = _sx1 = _sy1 = _sx = _sy = 0;
                _list = map.Players;
            }
            _i = _list?.Count ?? 0;
            Current = null;
        }

        public PlayerObject Current { get; private set; }

        public PlayersNear GetEnumerator() => this;

        public bool MoveNext()
        {
            while (true)
            {
                if (_list != null && --_i >= 0)
                {
                    if (_i >= _list.Count) continue; // the list shrank under a caller (defensive; callers don't remove)
                    Current = _list[_i];
                    return true;
                }

                if (_grid == null) return false;

                // next non-empty sector
                do
                {
                    if (++_sx > _sx1)
                    {
                        _sx = _sx0;
                        if (++_sy > _sy1) return false;
                    }
                    _list = _grid.Sector(_sx, _sy);
                } while (_list == null || _list.Count == 0);
                _i = _list.Count;
            }
        }
    }
}
