using System.Collections.Concurrent;
using Server.MirObjects;

namespace Server.MirEnvir
{
    /// <summary>
    /// Due-time scheduler for one processing owner (roadmap P9-05, <c>[Optimisation] DueScheduler</c>): the game thread, or one
    /// mob thread. Replaces walking every object every tick: an object is popped only when <c>Envir.Time &gt; OperateTime</c>,
    /// i.e. at due = OperateTime + 1.
    /// <para>Contract:</para>
    /// <list type="bullet">
    /// <item>The heap is touched only by its owner thread (the game thread, or the mob thread during the barrier).</item>
    /// <item><see cref="Wake"/> is safe from any thread. The <see cref="MapObject.OperateTime"/> setter calls it (via
    /// <see cref="Envir.WakeObject"/>) when a write makes an object due earlier than its scheduled entry, and
    /// <see cref="MapObject.Spawned"/> always does.</item>
    /// <item>Entries are lazy: an entry is live only while its due equals <see cref="MapObject.ScheduledDue"/> and the object is
    /// spawned (<c>Node != null</c>). Stale entries are skipped when popped, and dropped by <see cref="Compact"/>.</item>
    /// <item>Ownership (game thread vs mob thread) is checked when an entry is popped; an entry in the wrong scheduler is
    /// forwarded to the right one, so a <c>Master</c> change takes effect at the object's next due time, as with the old walk.</item>
    /// </list>
    /// </summary>
    public sealed class ObjectScheduler
    {
        private readonly PriorityQueue<MapObject, long> _heap = new PriorityQueue<MapObject, long>();
        private readonly ConcurrentQueue<MapObject> _wakes = new ConcurrentQueue<MapObject>();
        private int _compactAbove = MinCompactAbove;

        private const int MinCompactAbove = 4096;

        /// <summary>Owner thread only. Worst lateness (ms past due) of a processed object since the last <see cref="TakeMaxLateMs"/>.</summary>
        public long MaxLateMs;

        public int Count => _heap.Count;
        public int PendingWakes => _wakes.Count;

        /// <summary>Any thread. The owner pushes (or forwards) the object at its current OperateTime on its next pass.</summary>
        public void Wake(MapObject ob) => _wakes.Enqueue(ob);

        internal bool TryDequeueWake(out MapObject ob) => _wakes.TryDequeue(out ob);

        /// <summary>
        /// Owner thread, or the game thread while this scheduler's mob thread is parked at the barrier: true when a wake is
        /// pending or an entry is due at <paramref name="time"/>. Lets the game loop skip waking a mob thread with nothing to do.
        /// </summary>
        internal bool HasWork(long time) => !_wakes.IsEmpty || (_heap.TryPeek(out _, out var due) && due <= time);

        /// <summary>
        /// Owner thread only. Schedules <paramref name="ob"/> at its current OperateTime, never before <paramref name="floor"/>;
        /// this becomes its live entry.
        /// <para>Pairs with the <see cref="MapObject.OperateTime"/> setter (write value, full fence, read ScheduledDue): ScheduledDue
        /// is published with a full fence before OperateTime is read again, so an earlier write racing this push is either seen
        /// here (and pushed) or sees the new ScheduledDue and wakes.</para>
        /// </summary>
        internal void Schedule(MapObject ob, long floor)
        {
            var due = Math.Max(DueOf(ob.OperateTime), floor);
            Interlocked.Exchange(ref ob.ScheduledDue, due);
            _heap.Enqueue(ob, due);

            var again = Math.Max(DueOf(ob.OperateTime), floor);
            if (again < due)
            {
                Interlocked.Exchange(ref ob.ScheduledDue, again);
                _heap.Enqueue(ob, again);
            }

            if (_heap.Count > _compactAbove) Compact();
        }

        private static long DueOf(long operateTime) => operateTime == long.MaxValue ? long.MaxValue : operateTime + 1;

        /// <summary>Owner thread only. Pops the earliest entry if it is due at <paramref name="time"/>.</summary>
        internal bool TryPopDue(long time, out MapObject ob, out long due)
        {
            if (_heap.TryPeek(out ob, out due) && due <= time)
            {
                _heap.Dequeue();
                return true;
            }

            ob = null;
            due = 0;
            return false;
        }

        /// <summary>
        /// Hands every scheduled and woken object to <paramref name="target"/> (as wakes). Only for a scheduler whose owner thread
        /// has ended (a dead mob thread), called from the thread that now owns its objects.
        /// </summary>
        internal void MoveAllTo(ObjectScheduler target)
        {
            while (_heap.TryDequeue(out var ob, out _))
                if (ob.Node != null) target.Wake(ob);
            while (_wakes.TryDequeue(out var ob))
                if (ob.Node != null) target.Wake(ob);
        }

        public long TakeMaxLateMs()
        {
            var late = MaxLateMs;
            MaxLateMs = 0;
            return late;
        }

        /// <summary>
        /// Drops stale entries (an object woken many times leaves one stale entry per wake until its old due time). Runs when the
        /// heap outgrows twice its live size, so it is rare and amortised.
        /// </summary>
        private void Compact()
        {
            foreach (var (ob, due) in _heap.UnorderedItems)
            {
                if (ob.Node == null || due != Volatile.Read(ref ob.ScheduledDue)) continue;
                if (!_compactSeen.Add(ob)) continue;
                _compactLive.Add((ob, due));
            }

            _heap.Clear();
            _heap.EnqueueRange(_compactLive);
            _compactAbove = Math.Max(MinCompactAbove, _compactLive.Count * 2);
            _compactLive.Clear();
            _compactSeen.Clear();
        }

        // Compact scratch, reused (owner thread only) so a compaction doesn't allocate large collections on the hot thread.
        private readonly List<(MapObject, long)> _compactLive = new List<(MapObject, long)>();
        private readonly HashSet<MapObject> _compactSeen = new HashSet<MapObject>(ReferenceEqualityComparer.Instance);
    }
}
