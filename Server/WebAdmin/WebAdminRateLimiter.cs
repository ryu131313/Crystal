using System.Collections.Concurrent;

namespace Server.WebAdmin
{
    /// <summary>
    /// Fixed-window counters keyed by an arbitrary string (e.g. "login|127.0.0.1"). Thread-safe. Fails closed when the key table is full.
    /// </summary>
    public sealed class WebAdminRateLimiter
    {
        private sealed class Counter
        {
            public long Window;
            public int Count;
        }

        private readonly ConcurrentDictionary<string, Counter> _counters = new ConcurrentDictionary<string, Counter>(StringComparer.Ordinal);
        private readonly Func<DateTime> _clock;
        private readonly int _maxKeys;
        private int _ops;

        public static WebAdminRateLimiter Shared { get; } = new WebAdminRateLimiter();

        public WebAdminRateLimiter(Func<DateTime> clock = null, int maxKeys = 100000)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            _maxKeys = Math.Max(16, maxKeys);
        }

        public int KeyCount => _counters.Count;

        /// <summary>Counts one hit; false (with seconds until the window resets) when the limit for this window is already reached.</summary>
        public bool TryAcquire(string key, int limit, TimeSpan window, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (limit <= 0) return true;

            var counter = GetCounter(key);
            if (counter == null)
            {
                retryAfterSeconds = SecondsToReset(window);
                return false;
            }

            var current = WindowIndex(window);

            lock (counter)
            {
                if (counter.Window != current)
                {
                    counter.Window = current;
                    counter.Count = 0;
                }

                if (counter.Count >= limit)
                {
                    retryAfterSeconds = SecondsToReset(window);
                    return false;
                }

                counter.Count++;
                return true;
            }
        }

        /// <summary>Peeks without counting: true when <paramref name="limit"/> hits were already recorded in the current window.</summary>
        public bool IsExhausted(string key, int limit, TimeSpan window, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (limit <= 0 || string.IsNullOrEmpty(key)) return false;
            if (!_counters.TryGetValue(key, out var counter)) return false;

            var current = WindowIndex(window);

            lock (counter)
            {
                if (counter.Window != current || counter.Count < limit) return false;
            }

            retryAfterSeconds = SecondsToReset(window);
            return true;
        }

        /// <summary>Records one hit without checking (e.g. a failed password).</summary>
        public void Hit(string key, TimeSpan window)
        {
            var counter = GetCounter(key);
            if (counter == null) return;

            var current = WindowIndex(window);

            lock (counter)
            {
                if (counter.Window != current)
                {
                    counter.Window = current;
                    counter.Count = 0;
                }

                counter.Count++;
            }
        }

        public void Reset(string key)
        {
            if (!string.IsNullOrEmpty(key)) _counters.TryRemove(key, out _);
        }

        /// <summary>Removes counters whose window started more than <paramref name="maxAge"/> ago (ended for every window size up to maxAge).</summary>
        public void Sweep(TimeSpan maxAge)
        {
            var cutoff = _clock().Ticks - Math.Max(TimeSpan.TicksPerSecond, maxAge.Ticks);

            foreach (var pair in _counters)
            {
                long started;
                lock (pair.Value) started = pair.Value.Window;

                if (started < cutoff)
                    _counters.TryRemove(pair.Key, out _);
            }
        }

        private Counter GetCounter(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "?";

            if (_counters.TryGetValue(key, out var existing)) return existing;

            if (Interlocked.Increment(ref _ops) % 4096 == 0)
                Sweep(TimeSpan.FromHours(1));

            if (_counters.Count >= _maxKeys)
            {
                Sweep(TimeSpan.FromMinutes(1));
                if (_counters.Count >= _maxKeys) return null; // fail closed
            }

            return _counters.GetOrAdd(key, _ => new Counter { Window = long.MinValue });
        }

        /// <summary>Absolute start tick of the current window (comparable across window sizes).</summary>
        private long WindowIndex(TimeSpan window)
        {
            var ticks = Math.Max(TimeSpan.TicksPerSecond, window.Ticks);
            var now = _clock().Ticks;
            return now - (now % ticks);
        }

        private int SecondsToReset(TimeSpan window)
        {
            var ticks = Math.Max(TimeSpan.TicksPerSecond, window.Ticks);
            var now = _clock().Ticks;
            var remaining = ticks - (now % ticks);
            return (int)Math.Max(1, Math.Ceiling(remaining / (double)TimeSpan.TicksPerSecond));
        }
    }
}
