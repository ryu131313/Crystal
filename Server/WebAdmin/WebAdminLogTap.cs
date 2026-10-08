namespace Server.WebAdmin
{
    /// <summary>One captured log line. Seq is monotonically increasing per log kind (process-local).</summary>
    public sealed class LogLine
    {
        public long Seq { get; set; }
        public DateTime Utc { get; set; }
        public string Text { get; set; }
    }

    /// <summary>Fixed-capacity ring of log lines with sequence ids, safe for concurrent writers/readers.</summary>
    public sealed class WebAdminLogRing
    {
        private readonly LogLine[] _items;
        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;
        private long _lastSeq;
        private int _start;
        private int _count;

        public WebAdminLogRing(int capacity, Func<DateTime> clock = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new LogLine[capacity];
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Capacity => _items.Length;

        public long LastSeq
        {
            get
            {
                lock (_gate) return _lastSeq;
            }
        }

        public int Count
        {
            get
            {
                lock (_gate) return _count;
            }
        }

        public long Append(string text)
        {
            text = Normalize(text);

            lock (_gate)
            {
                var line = new LogLine { Seq = ++_lastSeq, Utc = _clock(), Text = text };

                if (_count < _items.Length)
                {
                    _items[(_start + _count) % _items.Length] = line;
                    _count++;
                }
                else
                {
                    _items[_start] = line;
                    _start = (_start + 1) % _items.Length;
                }

                return line.Seq;
            }
        }

        /// <summary>
        /// afterSeq &lt;= 0: the newest <paramref name="max"/> lines (ascending). afterSeq &gt; 0: lines with Seq &gt; afterSeq, oldest first, up to max
        /// (if afterSeq is newer than anything known, e.g. after a server restart, it is treated as 0).
        /// lastSeq is the Seq of the last returned line, or the ring's latest Seq when nothing was returned.
        /// </summary>
        public (IReadOnlyList<LogLine> lines, long lastSeq) Get(long afterSeq, int max = 500)
        {
            if (max < 1) max = 1;

            lock (_gate)
            {
                if (afterSeq > _lastSeq) afterSeq = 0;

                var result = new List<LogLine>(Math.Min(max, _count));

                if (afterSeq <= 0)
                {
                    var take = Math.Min(max, _count);
                    for (var i = _count - take; i < _count; i++)
                        result.Add(_items[(_start + i) % _items.Length]);
                }
                else
                {
                    for (var i = 0; i < _count && result.Count < max; i++)
                    {
                        var line = _items[(_start + i) % _items.Length];
                        if (line.Seq > afterSeq) result.Add(line);
                    }
                }

                var last = result.Count > 0 ? result[result.Count - 1].Seq : _lastSeq;
                return (result, last);
            }
        }

        private static string Normalize(string text)
        {
            if (text == null) return string.Empty;
            return text.TrimEnd('\r', '\n');
        }
    }

    /// <summary>
    /// Ring buffers fed by MessageQueue.Enqueue* (tee; the WinForms consumer of MessageQueue is unaffected).
    /// Kinds: "server", "debug", "chat".
    /// </summary>
    public static class WebAdminLogTap
    {
        public const int DefaultCapacity = 2000;

        private static readonly WebAdminLogRing ServerRing = new WebAdminLogRing(DefaultCapacity);
        private static readonly WebAdminLogRing DebugRing = new WebAdminLogRing(DefaultCapacity);
        private static readonly WebAdminLogRing ChatRing = new WebAdminLogRing(DefaultCapacity);

        public static readonly IReadOnlyList<string> Kinds = new[] { "server", "debug", "chat" };

        internal static void AppendServer(string text)
        {
            try { ServerRing.Append(text); } catch { }
        }

        internal static void AppendDebug(string text)
        {
            try { DebugRing.Append(text); } catch { }
        }

        internal static void AppendChat(string text)
        {
            try { ChatRing.Append(text); } catch { }
        }

        /// <summary>Returns null for an unknown kind.</summary>
        public static WebAdminLogRing Ring(string kind)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "server":
                    return ServerRing;
                case "debug":
                    return DebugRing;
                case "chat":
                    return ChatRing;
                default:
                    return null;
            }
        }

        public static bool IsKnownKind(string kind) => Ring(kind) != null;

        /// <summary>See <see cref="WebAdminLogRing.Get"/>. Unknown kind → empty result.</summary>
        public static (IReadOnlyList<LogLine> lines, long lastSeq) Get(string kind, long afterSeq, int max = 500)
        {
            var ring = Ring(kind);
            if (ring == null) return (Array.Empty<LogLine>(), 0);
            return ring.Get(afterSeq, max);
        }
    }
}
