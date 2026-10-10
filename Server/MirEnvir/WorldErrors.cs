using Server.MirNetwork;
using Server.MirObjects;

namespace Server.MirEnvir
{
    /// <summary>
    /// Isolated-failure recorder for the world loop (roadmap P5). Code that catches an exception at an isolation boundary
    /// (a connection, an object, a map, an Envir.Process block, a mob thread) calls <see cref="Record"/>: it counts the
    /// error, keeps the last few for the dashboard, and logs with identity. Logging is rate-limited because
    /// MessageQueue writes synchronously on the calling thread and a repeatedly-throwing object must not flood the tick.
    /// Called from the game thread and from mob threads; the error path is rare, so a short lock is acceptable here.
    /// </summary>
    public static class WorldErrors
    {
        public const int KeepLast = 20;
        private const int MaxLoggedPerSecond = 10;

        /// <summary>A quarantined object is not processed again for this long (unless other code re-arms it).</summary>
        public const long QuarantineMs = 10 * 60 * 1000;

        private static readonly object Gate = new object();
        private static readonly Queue<string> Last = new Queue<string>(KeepLast);
        private static long _count;
        private static long _windowStart;
        private static int _loggedInWindow, _suppressedInWindow;

        /// <summary>Isolated errors since process start.</summary>
        public static long Count => Interlocked.Read(ref _count);

        /// <summary>The most recent errors, newest last (one line each).</summary>
        public static string[] Recent()
        {
            lock (Gate) return Last.ToArray();
        }

        public static void Record(string where, Exception ex)
        {
            Interlocked.Increment(ref _count);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {where}: {ex.GetType().Name}: {ex.Message}";
            bool log;
            int suppressed = 0;
            lock (Gate)
            {
                if (Last.Count == KeepLast) Last.Dequeue();
                Last.Enqueue(line);

                var now = Environment.TickCount64;
                if (now - _windowStart >= 1000)
                {
                    suppressed = _suppressedInWindow;
                    _windowStart = now;
                    _loggedInWindow = 0;
                    _suppressedInWindow = 0;
                }
                log = _loggedInWindow < MaxLoggedPerSecond;
                if (log) _loggedInWindow++; else _suppressedInWindow++;
            }

            if (suppressed > 0)
                MessageQueue.Instance.Enqueue($"[World] {suppressed} further isolated errors were not logged in the previous second (see the dashboard counter).");
            if (log)
                MessageQueue.Instance.Enqueue($"[World] Isolated error in {where} (world keeps running): {ex}");
        }

        public static string Describe(MapObject ob)
        {
            if (ob == null) return "object <null>";
            try
            {
                return $"{ob.Race} '{ob.Name}' #{ob.ObjectID} on {ob.CurrentMap?.Info?.FileName ?? "?"} at {ob.CurrentLocation}";
            }
            catch
            {
                return $"{ob.GetType().Name} #{ob.ObjectID}";
            }
        }

        public static string Describe(MirConnection c)
        {
            if (c == null) return "connection <null>";
            try
            {
                return $"connection {c.SessionID} {c.IPAddress} account '{c.Account?.AccountID}' player '{c.Player?.Name}' stage {c.Stage} last packet {c.LastPacketIndex}";
            }
            catch
            {
                return $"connection {c.SessionID}";
            }
        }
    }
}
