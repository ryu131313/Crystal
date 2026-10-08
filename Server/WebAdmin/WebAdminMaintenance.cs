using Server.MirDatabase;
using Server.MirEnvir;
using S = ServerPackets;

namespace Server.WebAdmin
{
    /// <summary>Snapshot of the maintenance state (GET /api/admin/maintenance).</summary>
    public sealed class MaintenanceSnapshot
    {
        public bool Active { get; set; }
        public DateTime? Since { get; set; }
        public string By { get; set; }
        public string Message { get; set; }
        public bool KickNonAdmins { get; set; }
        public DateTime? KickScheduledUtc { get; set; }
        public int? SecondsUntilKick { get; set; }
        public DateTime? KickedUtc { get; set; }
        public int KickedCount { get; set; }
        public DateTime? LiftedUtc { get; set; }
        public string LiftedBy { get; set; }
        public int RefusedLogins { get; set; }
        public DateTime ServerTimeUtc { get; set; }
    }

    /// <summary>World side effects of maintenance (swappable for tests).</summary>
    public interface IMaintenanceWorld
    {
        bool IsRunning { get; }

        /// <summary>Fire-and-forget announcement to every online player.</summary>
        void Announce(string text);

        /// <summary>Fire-and-forget: disconnect every connection logged into a non-admin account; reports the count.</summary>
        void KickNonAdmins(Action<int> done);
    }

    internal sealed class EnvirMaintenanceWorld : IMaintenanceWorld
    {
        public bool IsRunning => WebAdminGame.IsRunning;

        public void Announce(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            WebAdminGame.Post(() =>
            {
                Envir.Main.Broadcast(new S.Chat { Message = text, Type = ChatType.Announcement });
                MessageQueue.Instance.EnqueueChat("[Web admin maintenance] " + text);
            });

            WebAdminChatLog.Append("broadcast", "[web] maintenance", text);
        }

        public void KickNonAdmins(Action<int> done)
        {
            var queued = WebAdminGame.Post(() =>
            {
                var envir = Envir.Main;
                MirNetwork.MirConnection[] connections;
                lock (envir.Connections) connections = envir.Connections.ToArray();

                var kicked = 0;
                foreach (var c in connections)
                {
                    if (c == null || c.Stage == MirNetwork.GameStage.Disconnected) continue;

                    var account = c.Account;
                    if (account == null || account.AdminAccount) continue;

                    c.SendDisconnect(0);
                    kicked++;
                }

                MessageQueue.Instance.Enqueue("[Web admin maintenance] disconnected " + kicked + " non-admin connection(s).");
                done?.Invoke(kicked);
            });

            if (!queued) done?.Invoke(0);
        }
    }

    /// <summary>
    /// Maintenance mode: a login gate that refuses non-admin accounts, an optional countdown with in-game announcements and a
    /// final disconnect of non-admin players. Process-wide; survives world restarts (the gate is a static hook).
    /// </summary>
    public sealed class MaintenanceController : IDisposable
    {
        public const string DefaultMessage = "The server is down for maintenance. Please try again later.";
        public const int MaxMinutes = 60;

        /// <summary>Countdown announcement points (seconds before the disconnect).</summary>
        public static readonly IReadOnlyList<int> AnnouncePoints = new[] { 3600, 2700, 1800, 1200, 900, 600, 300, 240, 180, 120, 60, 30, 10 };

        private readonly object _gate = new object();
        private readonly Func<DateTime> _clock;
        private readonly IMaintenanceWorld _world;
        private readonly Action<Func<AccountInfo, string>> _installGate;
        private readonly Func<AccountInfo, string> _gateFunc;
        private readonly bool _useTimer;

        private bool _active;
        private DateTime? _since;
        private string _by;
        private string _message;
        private bool _kick;
        private bool _announce;
        private DateTime? _kickAt;
        private int _nextAnnounce;
        private DateTime? _kickedUtc;
        private int _kickedCount;
        private DateTime? _liftedUtc;
        private string _liftedBy;
        private int _refused;
        private System.Threading.Timer _timer;

        public MaintenanceController(IMaintenanceWorld world, Action<Func<AccountInfo, string>> installGate, Func<DateTime> clock = null, bool useTimer = true)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _installGate = installGate ?? (_ => { });
            _clock = clock ?? (() => DateTime.UtcNow);
            _useTimer = useTimer;
            _gateFunc = Gate;
        }

        /// <summary>The login gate function installed while maintenance is active.</summary>
        public Func<AccountInfo, string> GateFunction => _gateFunc;

        public bool Active
        {
            get
            {
                lock (_gate) return _active;
            }
        }

        /// <summary>Refusal message for non-admin accounts while active; null (allowed) for admins or when inactive.</summary>
        public string Gate(AccountInfo account)
        {
            if (account == null || account.AdminAccount) return null;

            lock (_gate)
            {
                if (!_active) return null;
                _refused++;
                return string.IsNullOrWhiteSpace(_message) ? DefaultMessage : _message;
            }
        }

        public MaintenanceSnapshot Snapshot()
        {
            lock (_gate)
            {
                var now = _clock();
                return new MaintenanceSnapshot
                {
                    Active = _active,
                    Since = _since,
                    By = _by,
                    Message = _message,
                    KickNonAdmins = _kick,
                    KickScheduledUtc = _kickAt,
                    SecondsUntilKick = _kickAt.HasValue ? (int?)Math.Max(0, (int)Math.Ceiling((_kickAt.Value - now).TotalSeconds)) : null,
                    KickedUtc = _kickedUtc,
                    KickedCount = _kickedCount,
                    LiftedUtc = _liftedUtc,
                    LiftedBy = _liftedBy,
                    RefusedLogins = _refused,
                    ServerTimeUtc = now,
                };
            }
        }

        /// <summary>
        /// Starts maintenance: installs the login gate now; with <paramref name="kickNonAdmins"/> announces a countdown of
        /// <paramref name="minutes"/> (0 = disconnect immediately) and then disconnects non-admin players.
        /// Throws <see cref="InvalidOperationException"/> when already active.
        /// </summary>
        public MaintenanceSnapshot Start(string by, string message, bool kickNonAdmins, int minutes, bool announce = true)
        {
            if (minutes < 0 || minutes > MaxMinutes) throw new ArgumentOutOfRangeException(nameof(minutes), "minutes must be 0-" + MaxMinutes);

            message = string.IsNullOrWhiteSpace(message) ? DefaultMessage : ChatLogRing.Clean(message, 300);
            bool kickNow;

            lock (_gate)
            {
                if (_active) throw new InvalidOperationException("Maintenance is already active");

                var now = _clock();
                _active = true;
                _since = now;
                _by = by;
                _message = message;
                _kick = kickNonAdmins;
                _announce = announce;
                _kickedUtc = null;
                _kickedCount = 0;
                _liftedUtc = null;
                _liftedBy = null;
                _refused = 0;
                _nextAnnounce = 0;
                _kickAt = kickNonAdmins && minutes > 0 ? now.AddMinutes(minutes) : (DateTime?)null;
                kickNow = kickNonAdmins && minutes == 0;

                if (_kickAt.HasValue)
                {
                    var total = minutes * 60;
                    while (_nextAnnounce < AnnouncePoints.Count && AnnouncePoints[_nextAnnounce] >= total) _nextAnnounce++;
                }
            }

            _installGate(_gateFunc);

            if (announce && _world.IsRunning)
            {
                if (kickNow)
                    _world.Announce("Server maintenance has started: " + message);
                else if (kickNonAdmins)
                    _world.Announce("Server maintenance begins in " + DescribeSeconds(minutes * 60) + ": " + message + " You will be disconnected then.");
                else
                    _world.Announce("Server maintenance has started: " + message + " New logins are closed.");
            }

            if (kickNow) DoKick();
            else if (kickNonAdmins && _useTimer) StartTimer();

            return Snapshot();
        }

        /// <summary>Lifts maintenance: removes the gate, cancels a pending disconnect, optionally announces. Idempotent.</summary>
        public MaintenanceSnapshot Lift(string by, bool announce = true)
        {
            bool wasActive;
            lock (_gate)
            {
                wasActive = _active;
                _active = false;
                _kickAt = null;
                if (wasActive)
                {
                    _liftedUtc = _clock();
                    _liftedBy = by;
                }
            }

            StopTimer();
            _installGate(null);

            if (wasActive && announce && _world.IsRunning)
                _world.Announce("Server maintenance is over. Logins are open again.");

            return Snapshot();
        }

        /// <summary>Countdown step: announces due points and performs the disconnect when the time is up. Called every second by the timer.</summary>
        public void Tick()
        {
            string announcement = null;
            var kick = false;
            bool announce;

            lock (_gate)
            {
                if (!_active || !_kickAt.HasValue) return;
                announce = _announce;

                var remaining = (_kickAt.Value - _clock()).TotalSeconds;

                if (remaining <= 0)
                {
                    _kickAt = null;
                    kick = true;
                }
                else
                {
                    // Announce the latest due point only (a late tick never spams several announcements).
                    var due = -1;
                    while (_nextAnnounce < AnnouncePoints.Count && remaining <= AnnouncePoints[_nextAnnounce])
                    {
                        due = AnnouncePoints[_nextAnnounce];
                        _nextAnnounce++;
                    }

                    if (due > 0 && announce) announcement = "Server maintenance in " + DescribeSeconds(due) + ": " + _message + " You will be disconnected.";
                }
            }

            if (announcement != null && _world.IsRunning) _world.Announce(announcement);

            if (kick)
            {
                StopTimer();
                if (announce && _world.IsRunning) _world.Announce("Server maintenance has started: " + Snapshot().Message);
                DoKick();
            }
        }

        private void DoKick()
        {
            if (!_world.IsRunning)
            {
                lock (_gate) _kickedUtc = _clock();
                return;
            }

            _world.KickNonAdmins(count =>
            {
                lock (_gate)
                {
                    _kickedUtc = _clock();
                    _kickedCount += count;
                }
            });
        }

        private void StartTimer()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        Tick();
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "Maintenance countdown failed");
                    }
                }, null, 1000, 1000);
            }
        }

        private void StopTimer()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>"1 hour", "45 minutes", "1 minute", "30 seconds" (pure).</summary>
        public static string DescribeSeconds(int seconds)
        {
            if (seconds >= 3600 && seconds % 3600 == 0) return seconds / 3600 == 1 ? "1 hour" : seconds / 3600 + " hours";
            if (seconds >= 60)
            {
                var m = (int)Math.Round(seconds / 60.0);
                return m == 1 ? "1 minute" : m + " minutes";
            }

            return seconds == 1 ? "1 second" : seconds + " seconds";
        }

        public void Dispose()
        {
            StopTimer();
        }
    }

    /// <summary>Process-wide maintenance facade (owns <see cref="WebAdminHooks.LoginGate"/>).</summary>
    public static class WebAdminMaintenance
    {
        private static MaintenanceController _controller;

        internal static MaintenanceController Controller
        {
            get => _controller ??= CreateController(new EnvirMaintenanceWorld());
            set => _controller = value;
        }

        /// <summary>A controller wired to the real <see cref="WebAdminHooks.LoginGate"/> (tests pass a fake world).</summary>
        internal static MaintenanceController CreateController(IMaintenanceWorld world, Func<DateTime> clock = null, bool useTimer = true)
            => new MaintenanceController(world, InstallGate, clock, useTimer);

        private static void InstallGate(Func<AccountInfo, string> gate)
        {
            if (gate != null)
            {
                WebAdminHooks.LoginGate = gate;
                return;
            }

            // Only remove our own gate.
            var ours = _controller?.GateFunction;
            if (ours != null && WebAdminHooks.LoginGate == ours) WebAdminHooks.LoginGate = null;
        }

        public static MaintenanceSnapshot Snapshot() => Controller.Snapshot();

        public static MaintenanceSnapshot Start(string by, string message, bool kickNonAdmins, int minutes, bool announce = true)
            => Controller.Start(by, message, kickNonAdmins, minutes, announce);

        public static MaintenanceSnapshot Lift(string by, bool announce = true) => Controller.Lift(by, announce);
    }
}
