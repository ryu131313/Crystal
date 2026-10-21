using System.Collections.Concurrent;
using System.Diagnostics;

namespace Server.MirNetwork
{
    /// <summary>
    /// Socket sends off the game thread (P9-09, [Optimisation] SendThreads). The game thread still encodes and copies each
    /// connection's flush into one byte[] (the encode-once cache stays game-thread only) and hands the array to
    /// <see cref="MirConnection.QueueSend"/>; a dedicated sender thread then issues the socket send. Each connection has at most
    /// one send in flight, its arrays go out in the order they were queued, and arrays that queued up while a send was in
    /// flight leave together in the next send. Sender threads and IO completions touch only the connection's send fields
    /// (outbox, counters, socket, <see cref="SocketAsyncEventArgs"/>), never game state. No locks: the game thread only
    /// enqueues into <see cref="ConcurrentQueue{T}"/>s and sets an event when a sender sleeps.
    /// </summary>
    public static class SocketSender
    {
        /// <summary>Upper bound for [Optimisation] SendThreads.</summary>
        public const int MaxThreads = 4;
        /// <summary>A connection being closed keeps its socket at most this long for queued sends to finish.</summary>
        internal const long CloseDrainMs = 2000;

        private static readonly Worker[] Workers = new Worker[MaxThreads];
        private static readonly object StartLock = new object(); // taken only when a sender thread is first started

        private static long _sends, _arrays, _busyTicks;

        /// <summary>Socket sends issued by sender threads and completions, arrays they carried, and sender thread busy
        /// time, since the last call (read and reset once per second by the loop profiler).</summary>
        public static void TakeStats(out long sends, out long arrays, out double busyMs)
        {
            sends = Interlocked.Exchange(ref _sends, 0);
            arrays = Interlocked.Exchange(ref _arrays, 0);
            busyMs = Interlocked.Exchange(ref _busyTicks, 0) * 1000.0 / Stopwatch.Frequency;
        }

        internal static void CountSend(int arrays)
        {
            Interlocked.Increment(ref _sends);
            Interlocked.Add(ref _arrays, arrays);
        }

        private static Worker For(MirConnection c)
        {
            var count = Math.Clamp(Settings.SendThreads, 1, MaxThreads);
            var index = (int)((uint)c.SessionID % (uint)count);
            var w = Volatile.Read(ref Workers[index]);
            if (w != null) return w;
            lock (StartLock) // once per worker for the process lifetime
            {
                w = Workers[index];
                if (w == null)
                {
                    w = new Worker(index);
                    Volatile.Write(ref Workers[index], w);
                }
            }
            return w;
        }

        /// <summary>Puts a connection whose outbox just became non-empty on its sender's ready list. Any thread.</summary>
        internal static void Schedule(MirConnection c)
        {
            var w = For(c);
            w.Ready.Enqueue(c);
            w.Wake();
        }

        /// <summary>Disposes the connection's socket once its queued sends finish, or after <see cref="CloseDrainMs"/>. Any thread.</summary>
        internal static void CloseWhenDrained(MirConnection c)
        {
            var w = For(c);
            w.Closing.Enqueue(c);
            w.Wake();
        }

        private sealed class Worker
        {
            public readonly ConcurrentQueue<MirConnection> Ready = new ConcurrentQueue<MirConnection>();
            public readonly ConcurrentQueue<MirConnection> Closing = new ConcurrentQueue<MirConnection>();
            private readonly List<MirConnection> _closing = new List<MirConnection>(); // sender thread only
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private int _sleeping;

            public Worker(int index)
            {
                var t = new Thread(Run) { IsBackground = true, Name = "Mir2 sender " + index };
                t.Start();
            }

            /// <summary>Wakes the thread if it sleeps: at most one kernel signal per sleep, nothing while it runs.</summary>
            public void Wake()
            {
                if (Volatile.Read(ref _sleeping) == 1 && Interlocked.Exchange(ref _sleeping, 0) == 1)
                    _signal.Set();
            }

            private static readonly long SpinTicks = Stopwatch.Frequency * SpinMicroseconds / 1_000_000;
            private const long SpinMicroseconds = 50;

            /// <summary>Spins up to <see cref="SpinMicroseconds"/> for new ready work. True when some arrived.</summary>
            private bool SpinForWork()
            {
                var until = Stopwatch.GetTimestamp() + SpinTicks;
                do
                {
                    if (!Ready.IsEmpty) return true;
                    Thread.SpinWait(20);
                }
                while (Stopwatch.GetTimestamp() < until);
                return !Ready.IsEmpty;
            }

            private void Run()
            {
                while (true)
                {
                    try
                    {
                        var start = Stopwatch.GetTimestamp();
                        while (Ready.TryDequeue(out var c))
                            c.PumpSends();

                        while (Closing.TryDequeue(out var c))
                            _closing.Add(c);
                        var now = Environment.TickCount64;
                        for (var i = _closing.Count - 1; i >= 0; i--)
                        {
                            if (!_closing[i].TryFinishClose(now)) continue;
                            _closing[i] = _closing[^1];
                            _closing.RemoveAt(_closing.Count - 1);
                        }
                        Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - start);

                        // The game thread queues connections one after another during a flush pass, a few µs apart: wait
                        // briefly for the next one, so a pass costs it about one wake signal instead of one per connection.
                        if (SpinForWork()) continue;

                        // Full fence (not Volatile.Write): the flag must be visible before the re-check below, or a producer
                        // could read 0, skip the signal, and leave its connection waiting for the timeout.
                        Interlocked.Exchange(ref _sleeping, 1);
                        if (!Ready.IsEmpty || !Closing.IsEmpty)
                        {
                            // Work arrived after the drain: stay awake. If a producer already took the flag it also sets the
                            // event, which only costs one extra empty pass.
                            Interlocked.Exchange(ref _sleeping, 0);
                            continue;
                        }
                        _signal.WaitOne(_closing.Count > 0 ? 50 : 1000);
                        Interlocked.Exchange(ref _sleeping, 0);
                    }
                    catch (Exception ex)
                    {
                        // PumpSends and TryFinishClose catch their own socket errors; this only keeps the thread alive.
                        MessageQueue.Instance.Enqueue(ex);
                    }
                }
            }
        }
    }
}
