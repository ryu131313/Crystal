using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>
    /// A budgeted walk over a <see cref="LinkedList{T}"/> that survives the list changing between steps: when the cursor's node was
    /// removed (node.List == null) the walk restarts from the first node. A full pass, or a visit cap of twice the length at the start
    /// (+ 64) after restarts, ends it. Game thread only; pure, so tests drive it with plain values.
    /// </summary>
    internal sealed class LiveDbSweepCursor<T>
    {
        private LinkedList<T> _list;
        private LinkedListNode<T> _next;
        private int _visited;
        private int _cap;

        public bool Active => _list != null;

        /// <summary>Start (or restart) a pass from the first node.</summary>
        public void Restart(LinkedList<T> list)
        {
            _list = list;
            _next = list?.First;
            _visited = 0;
            _cap = list == null ? 0 : list.Count * 2 + 64;
            if (_next == null) _list = null;
        }

        public void Stop()
        {
            _list = null;
            _next = null;
        }

        /// <summary>Visits at most <paramref name="budget"/> nodes. Returns true while the pass is not finished.</summary>
        public bool Step(int budget, Action<T> visit)
        {
            if (_list == null) return false;
            if (budget < 1) budget = 1;

            for (var i = 0; i < budget; i++)
            {
                if (_next == null || _visited >= _cap)
                {
                    Stop();
                    return false;
                }

                if (_next.List == null) // removed since the last step: start over (visits are idempotent)
                {
                    _next = _list.First;
                    if (_next == null)
                    {
                        Stop();
                        return false;
                    }
                }

                var node = _next;
                _next = node.Next;
                _visited++;
                visit(node.Value);
            }

            if (_next == null)
            {
                Stop();
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Refreshes spawned monsters after a live MonsterInfo stat / speed change: walks <c>Envir.Main.Objects</c> at most
    /// <c>[LiveDb] SweepPerTick</c> nodes per game tick (right after DrainAdminActions, while every mob thread is parked) and calls
    /// <see cref="MonsterObject.RefreshAll"/> on each monster whose Info changed, then clamps HP to the new maximum (no scaling).
    /// Game thread only (Enqueue from the LiveDb live step, Tick from WorkLoop, Drop from the world hooks); the idle cost per tick is
    /// one volatile read. Monsters that spawn later read the new values themselves.
    /// </summary>
    internal static class LiveDbSweeper
    {
        private static readonly HashSet<MonsterInfo> Pending = new HashSet<MonsterInfo>();
        private static readonly LiveDbSweepCursor<MapObject> Cursor = new LiveDbSweepCursor<MapObject>();
        private static volatile bool _hasWork;
        private static int _pendingCount;
        private static long _refreshed;

        /// <summary>Volatile: WorkLoop skips the call entirely while false.</summary>
        public static bool HasWork => _hasWork;

        /// <summary>Monster kinds waiting for the current pass (status page; racy read is fine).</summary>
        public static int PendingKinds => Volatile.Read(ref _pendingCount);

        public static long RefreshedTotal => Interlocked.Read(ref _refreshed);

        /// <summary>Game thread: refresh every spawned monster of these infos (merged into a running pass, which restarts).</summary>
        public static void Enqueue(IEnumerable<MonsterInfo> infos, Envir envir)
        {
            if (infos == null || envir == null) return;
            foreach (var info in infos)
                if (info != null) Pending.Add(info);

            if (Pending.Count == 0) return;
            Cursor.Restart(envir.Objects);
            Volatile.Write(ref _pendingCount, Pending.Count);
            _hasWork = Cursor.Active;
            if (!_hasWork) Pending.Clear();
        }

        /// <summary>Game thread, once per WorkLoop iteration after DrainAdminActions (the caller wraps it in try/catch).</summary>
        public static void Tick(int budget)
        {
            if (!_hasWork) return;

            var more = Cursor.Step(budget, Visit);
            if (more) return;

            Pending.Clear();
            Volatile.Write(ref _pendingCount, 0);
            _hasWork = false;
        }

        private static void Visit(MapObject ob)
        {
            if (ob == null || ob.Race != ObjectType.Monster) return;
            if (!(ob is MonsterObject mob) || mob.Info == null || !Pending.Contains(mob.Info)) return;

            try
            {
                mob.RefreshAll();
                if (!mob.Dead)
                {
                    var max = mob.Stats[Stat.HP];
                    if (max > 0 && mob.HP > max) mob.SetHP(max);
                }

                Interlocked.Increment(ref _refreshed);
            }
            catch (Exception ex)
            {
                // One monster must not stop the pass (or the world): record it and move on.
                WorldErrors.Record("LiveDb refresh " + WorldErrors.Describe(mob), ex);
            }
        }

        /// <summary>Game thread (world hooks) or after a sweeper error: forget the queue (it refers to the stopped world's objects).</summary>
        public static void Drop()
        {
            _hasWork = false;
            Cursor.Stop();
            Pending.Clear();
            Volatile.Write(ref _pendingCount, 0);
        }
    }
}
