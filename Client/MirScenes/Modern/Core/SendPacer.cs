using Client.MirNetwork;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// A token bucket for the new UI's bulk item requests (port of WebClient/src/game/sendPacer.ts). The server counts socket
    /// reads and IP-blocks a connection for 24 h past Settings.MaxPacket (50) reads in 5 s (Server/MirNetwork/MirConnection.cs
    /// ReceiveData), so a modern action that sends several requests per click (sell all, repair all, sort, move a stack set)
    /// goes through here: a burst of <see cref="Burst"/>, then about <see cref="PerSecond"/> a second. Single requests from a
    /// click may still use Network.Enqueue directly (the classic UI does). Drained by <see cref="Process"/> — ModernUi.Process
    /// calls it once per spin (the integrator wires it). UI thread only; no timers, CMain.Time only.
    /// </summary>
    public sealed class SendPacer
    {
        private readonly Queue<Packet> _queue = new Queue<Packet>();
        private readonly Action<Packet> _send;
        private readonly double _interval;
        private double _tokens;
        private long _last;
        private bool _disposed;

        public SendPacer(int burst = 5, double perSecond = 3, Action<Packet> send = null)
        {
            Burst = Math.Max(1, burst);
            PerSecond = Math.Max(0.1, perSecond);
            _interval = 1000.0 / PerSecond;
            _tokens = Burst;
            _last = CMain.Time;
            _send = send ?? Network.Enqueue;
        }

        public int Burst { get; }
        public double PerSecond { get; }

        /// <summary>The number of queued packets after it changed (a "waiting…" state on the cells / buttons).</summary>
        public event Action<int> QueuedChanged;

        /// <summary>Packets waiting for a token.</summary>
        public int Queued => _queue.Count;

        /// <summary>Sends now when a token is free and nothing is queued, else queues the packet (sent in order by Process).</summary>
        public void Send(Packet p)
        {
            if (_disposed || p == null) return;
            if (_queue.Count == 0 && Take())
            {
                _send(p);
                return;
            }
            _queue.Enqueue(p);
            QueuedChanged?.Invoke(_queue.Count);
        }

        /// <summary>Sends now only when a token is free and nothing is queued; false = not sent (and not queued).</summary>
        public bool TrySend(Packet p)
        {
            if (_disposed || p == null || _queue.Count > 0 || !Take()) return false;
            _send(p);
            return true;
        }

        /// <summary>Per spin: sends what the refilled tokens allow. Cheap when the queue is empty.</summary>
        public void Process()
        {
            if (_disposed || _queue.Count == 0) return;
            bool sent = false;
            while (_queue.Count > 0 && Take())
            {
                _send(_queue.Dequeue());
                sent = true;
            }
            if (sent) QueuedChanged?.Invoke(_queue.Count);
        }

        /// <summary>Drops every queued packet (packets already sent stay sent).</summary>
        public void Cancel()
        {
            if (_queue.Count == 0) return;
            _queue.Clear();
            QueuedChanged?.Invoke(0);
        }

        /// <summary>Cancels the queue and stops for good (the game scene's dispose).</summary>
        public void Dispose()
        {
            Cancel();
            _disposed = true;
        }

        private void Refill()
        {
            long t = CMain.Time;
            long elapsed = t - _last;
            if (elapsed <= 0) return;
            _tokens = Math.Min(Burst, _tokens + elapsed / _interval);
            _last = t;
        }

        private bool Take()
        {
            Refill();
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
