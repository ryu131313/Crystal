using System.Text.Json;
using System.Text.RegularExpressions;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using S = ServerPackets;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Server control: start / stop / reboot / save the world, reload NPCs / drops / line messages / recipes, restart countdown,
    /// Server.exe shutdown, server info and control state. World lifecycle calls (Envir.Start/Stop/Reboot) run on HTTP / pool threads,
    /// exactly like the WinForms menu — never on the game thread (Stop waits for the game thread: deadlock).
    /// </summary>
    public sealed class ServerControlRoutes : IWebAdminRoutes
    {
        private static int _transition;
        private static int _hooked;

        public void Register(WebAdminRouter router)
        {
            if (Interlocked.Exchange(ref _hooked, 1) == 0)
                WebAdminHooks.WorldStopped += () => WebAdminServerCountdown.Cancel(null, null, "world stopped", announce: false);

            router.Post("/api/admin/server/start", StartWorld);
            router.Post("/api/admin/server/stop", StopWorld);
            router.Post("/api/admin/server/reboot", RebootWorld);
            router.Post("/api/admin/server/save", SaveNow);
            router.Post("/api/admin/server/reload/{kind}", Reload);
            router.Get("/api/admin/server/restart-countdown", GetCountdown);
            router.Post("/api/admin/server/restart-countdown", ArmCountdown);
            router.Post("/api/admin/server/restart-countdown/cancel", CancelCountdown);
            router.Post("/api/admin/server/shutdown", Shutdown);
            router.Get("/api/admin/server/info", GetInfo);
            router.Get("/api/admin/server/state", GetState);
        }

        internal static bool TransitionInProgress => Volatile.Read(ref _transition) != 0;

        private static bool TryBeginTransition() => Interlocked.CompareExchange(ref _transition, 1, 0) == 0;

        private static void EndTransition() => Interlocked.Exchange(ref _transition, 0);

        private static Task Accepted(WebAdminRequest req, string message, object data = null)
        {
            return req.Json(new WebAdminRequest.ApiResult { Ok = true, Message = message, Data = data }, 202);
        }

        private static async Task<string> ReadReason(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            return Trim(body.GetString("reason"), 500);
        }

        internal static string Trim(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // ------------------------------------------------------------------ lifecycle

        private static async Task StartWorld(WebAdminRequest req)
        {
            var reason = await ReadReason(req);

            if (!TryBeginTransition())
                throw new WebAdminException(409, "Another start/stop is in progress.");

            try
            {
                var state = WebAdminGame.WorldState;
                if (state != "stopped")
                    throw new WebAdminException(409, state == "running" ? "The world is already running." : "The world is " + state + "; wait for it to finish.", new { world = state });

                var crashed = WebAdminGame.WorldCrashed;

                var loaded = await Task.Run(() =>
                {
                    // A crashed loop leaves Running == true with no thread: Start() would be a no-op until Stop() resets it.
                    if (WebAdminGame.WorldCrashed) Envir.Main.Stop();

                    // SMain loads Envir.Edit before the first Start; do the same when the world is started from the web.
                    lock (WebAdminGame.EditLock)
                    {
                        if (Envir.Edit.MapInfoList.Count == 0 && Envir.Edit.ItemInfoList.Count == 0)
                        {
                            if (!Envir.Edit.LoadDB()) return false;
                        }
                    }

                    Envir.Main.Start();
                    return true;
                }).ConfigureAwait(false);

                if (!loaded)
                    throw new WebAdminException(500, "The game data (SystemData.db) could not be loaded; the world was not started. See the server log ([Store] lines).");

                WebAdminAudit.Record(req, "server.start", "world", crashed ? new { recoveredFromCrash = true } : null, reason);
                await Accepted(req, crashed ? "The world loop had crashed; restarting it." : "Starting the world…", new { world = "starting" });
            }
            finally
            {
                EndTransition();
            }
        }

        private static async Task StopWorld(WebAdminRequest req)
        {
            var reason = await ReadReason(req);

            if (!TryBeginTransition())
                throw new WebAdminException(409, "Another start/stop is in progress.");

            var handedOff = false;

            try
            {
                var state = WebAdminGame.WorldState;
                var crashed = WebAdminGame.WorldCrashed;

                if (state == "stopped" && !crashed)
                    throw new WebAdminException(409, "The world is already stopped.", new { world = state });
                if (state == "starting" || state == "stopping")
                    throw new WebAdminException(409, "The world is " + state + "; wait for it to finish.", new { world = state });

                WebAdminServerCountdown.Cancel(req.AdminName, req.ClientIp, "world stop", announce: false);
                WebAdminAudit.Record(req, "server.stop", "world", crashed ? new { crashed = true } : null, reason);

                handedOff = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        Envir.Main.Stop();
                        Envir.Main.MonsterCount = 0;
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "World stop failed");
                    }
                    finally
                    {
                        EndTransition();
                    }
                });

                await Accepted(req, "Stopping the world… players are disconnected and everything is saved.", new { world = "stopping" });
            }
            finally
            {
                if (!handedOff) EndTransition();
            }
        }

        private static async Task RebootWorld(WebAdminRequest req)
        {
            var reason = await ReadReason(req);

            if (!TryBeginTransition())
                throw new WebAdminException(409, "Another start/stop is in progress.");

            var handedOff = false;

            try
            {
                var state = WebAdminGame.WorldState;
                var crashed = WebAdminGame.WorldCrashed;
                if (state != "running" && !crashed)
                    throw new WebAdminException(409, state == "stopped" ? "The world is stopped; use Start." : "The world is " + state + "; wait for it to finish.", new { world = state });

                WebAdminServerCountdown.Cancel(req.AdminName, req.ClientIp, "reboot", announce: false);
                WebAdminAudit.Record(req, "server.reboot", "world", crashed ? new { crashed = true } : null, reason);

                handedOff = true;
                RunRebootHoldingTransition();

                await Accepted(req, "Rebooting the world… players are disconnected; it comes back in a moment.", new { world = "stopping" });
            }
            finally
            {
                if (!handedOff) EndTransition();
            }
        }

        /// <summary>Takes the start/stop/reboot guard for a reboot. Pair with <see cref="RunRebootHoldingTransition"/> or <see cref="EndRebootTransition"/>.</summary>
        internal static bool TryBeginRebootTransition() => TryBeginTransition();

        internal static void EndRebootTransition() => EndTransition();

        /// <summary>
        /// Envir.Reboot() replacement: Stop() then Start() on a pool thread while the caller's transition guard is held, released in finally.
        /// Envir.Reboot spawns an unguarded thread per call, so two overlapping reboots could race Start()'s non-atomic check (two WorkLoops)
        /// or restart a world an admin stopped meanwhile.
        /// </summary>
        internal static void RunRebootHoldingTransition()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    MessageQueue.Instance.Enqueue("[WebAdmin] Rebooting the world.");
                    Envir.Main.Stop();
                    Envir.Main.Start();
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "World reboot failed");
                }
                finally
                {
                    EndTransition();
                }
            });
        }

        private static async Task SaveNow(WebAdminRequest req)
        {
            var reason = await ReadReason(req);

            if (!WebAdminGame.IsRunning || WebAdminGame.WorldState != "running")
                throw new WorldNotRunningException(WebAdminGame.WorldState);

            Envir.Main.RequestSaveNow();
            WebAdminAudit.Record(req, "server.save", "world", null, reason);

            // Game data is saved by every Game Data save already (roadmap P7: Main's save only keeps the respawn counter).
            await req.Ok(null, "Save requested: accounts, guilds, goods, conquests and the respawn counter save on the next game tick.");
        }

        // ------------------------------------------------------------------ reloads

        internal static readonly IReadOnlyList<string> ReloadKinds = new[] { "npcs", "drops", "linemessages", "recipes" };

        private static async Task Reload(WebAdminRequest req)
        {
            var kind = (req.Route["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!ReloadKinds.Contains(kind))
                throw new WebAdminException(404, "Unknown reload: use npcs, drops, linemessages or recipes.");

            var reason = await ReadReason(req);

            string message;
            try
            {
                message = await WebAdminGame.RunAsync(() =>
                {
                    var envir = Envir.Main;

                    switch (kind)
                    {
                        case "npcs":
                            envir.ReloadNPCs();
                            return "NPC scripts reloaded.";
                        case "drops":
                            envir.ReloadDrops();
                            return "Drop files reloaded.";
                        case "linemessages":
                            envir.ReloadLineMessages();
                            return "Line messages reloaded (Envir\\LineMessage.txt).";
                        default:
                            return ReloadRecipes(envir);
                    }
                }, 30000);
            }
            catch (GameThreadTimeoutException ex) when (ex.Started)
            {
                // The reload is already running on the game thread and will finish: audit it before reporting the 504.
                WebAdminAudit.Record(req, "server.reload", kind, new { timedOut = true }, reason);
                throw;
            }

            WebAdminAudit.Record(req, "server.reload", kind, null, reason);
            await req.Ok(new { kind }, message);
        }

        /// <summary>Game thread. Mirrors StartEnvir's recipe loading: one RecipeInfo per Envir\Recipe\*.txt (file name = item name).</summary>
        private static string ReloadRecipes(Envir envir)
        {
            if (!Directory.Exists(Settings.RecipePath) && !DesignFiles.IsDesignFolder(Settings.RecipePath))
                throw new WebAdminException(422, "The recipe folder (Envir\\Recipe) does not exist.");

            var names = DesignFiles.EnumerateFiles(Settings.RecipePath, "*.txt", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .ToArray();

            envir.RecipeInfoList.Clear();
            foreach (var name in names)
                envir.RecipeInfoList.Add(new RecipeInfo(name));

            var missing = envir.RecipeInfoList.Count(r => r.Item == null);
            MessageQueue.Instance.Enqueue($"[WebAdmin] {envir.RecipeInfoList.Count} recipes reloaded.");

            return missing > 0
                ? $"Recipes reloaded ({envir.RecipeInfoList.Count}, {missing} with an unknown item — see the server log)."
                : $"Recipes reloaded ({envir.RecipeInfoList.Count}).";
        }

        // ------------------------------------------------------------------ restart countdown

        private static Task GetCountdown(WebAdminRequest req)
        {
            return req.Json(WebAdminServerCountdown.Snapshot());
        }

        private static async Task ArmCountdown(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            var minutes = body.GetInt("minutes", 0);
            var message = Trim(body.GetString("message"), 200);
            var reason = Trim(body.GetString("reason"), 500);

            if (minutes < 1 || minutes > 60)
                throw new WebAdminException(422, "Minutes must be between 1 and 60.");

            if (WebAdminGame.WorldState != "running")
                throw new WorldNotRunningException(WebAdminGame.WorldState);

            if (!WebAdminServerCountdown.TryArm(minutes, message, req.AdminName, req.ClientIp))
                throw new WebAdminException(409, "A restart countdown is already running; cancel it first.", WebAdminServerCountdown.Snapshot());

            WebAdminAudit.Record(req, "server.restart-countdown", "world", new { minutes, message }, reason);
            await req.Ok(WebAdminServerCountdown.Snapshot(), "Restart countdown armed: the world reboots in " + minutes + " minute" + (minutes == 1 ? "" : "s") + ".");
        }

        private static async Task CancelCountdown(WebAdminRequest req)
        {
            var reason = await ReadReason(req);

            if (!WebAdminServerCountdown.Cancel(req.AdminName, req.ClientIp, "cancelled by " + req.AdminName, announce: true))
                throw new WebAdminException(409, "No restart countdown is running.");

            WebAdminAudit.Record(req, "server.restart-countdown-cancel", "world", null, reason);
            await req.Ok(WebAdminServerCountdown.Snapshot(), "Restart countdown cancelled.");
        }

        // ------------------------------------------------------------------ process shutdown

        private static async Task Shutdown(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            var confirm = body.GetString("confirm");
            var reason = Trim(body.GetString("reason"), 500);

            if (!string.Equals(confirm?.Trim(), "SHUTDOWN", StringComparison.OrdinalIgnoreCase))
                throw new WebAdminException(400, "Shutdown needs {\"confirm\":\"SHUTDOWN\"}.");

            WebAdminServerCountdown.Cancel(req.AdminName, req.ClientIp, "shutdown", announce: false);
            WebAdminAudit.Record(req, "server.shutdown", "process", null, reason);
            MessageQueue.Instance.Enqueue("[WebAdmin] Server.exe shutdown requested by " + req.AdminName + ".");

            await Accepted(req, "Server.exe is shutting down: the world stops and saves, then the process exits. This site goes away with it.");

            // Let the response reach the browser before the host starts closing.
            _ = Task.Run(async () =>
            {
                await Task.Delay(750).ConfigureAwait(false);
                WebAdminHost.RequestShutdown();
            });
        }

        // ------------------------------------------------------------------ info / state

        private static Task GetInfo(WebAdminRequest req)
        {
            object settings;
            lock (WebAdminOptions.SettingsLock)
            {
                settings = new
                {
                    checkVersion = Settings.CheckVersion,
                    versionPath = Settings.VersionPath,
                    versionHashes = Settings.VersionHashes?.Count ?? 0,
                    gameIp = Settings.IPAddress,
                    gamePort = Settings.Port,
                    multithreaded = Settings.Multithreaded,
                    threadLimit = Settings.ThreadLimit,
                    saveDelayMinutes = Settings.SaveDelay,
                    httpService = Settings.StartHTTPService,
                    httpAddress = Settings.HTTPIPAddress,
                };
            }

            var options = WebAdminOptions.Current ?? WebAdminHost.Options;

            return req.Json(new
            {
                serverVersion = Envir.Version,
                customVersion = Envir.CustomVersion,
                minDbVersion = Envir.MinVersion,
                lastLoadedVersion = Envir.LoadVersion,
                dbFileVersion = ReadFileVersion(Envir.DatabasePath),
                accountsFileVersion = ReadUserDataVersion(), // meta.envir_version of UserData.db, through its writer
                settings,
                webAdmin = options == null ? null : new { bind = options.Bind, port = options.Port, url = options.DisplayUrl, webRoot = options.WebRoot },
                paths = new
                {
                    baseDirectory = AppContext.BaseDirectory,
                    workingDirectory = Environment.CurrentDirectory,
                    database = SafeFullPath(SqliteWorldStore.DatabasePath),
                    databaseImportSource = SafeFullPath(Envir.DatabasePath), // Server.MirDB: read once at the first start, never written
                    accounts = SafeFullPath(Envir.UserDataPath),
                    accountsImportSource = SafeFullPath(Envir.AccountPath), // Server.MirADB: imported once at the first start, then renamed *.imported
                    envir = SafeFullPath(Settings.EnvirPath),
                    backups = SafeFullPath(Envir.BackUpPath),
                    logs = WebAdminLogFiles.Root,
                },
                world = WebAdminGame.WorldState,
                crashed = WebAdminGame.WorldCrashed,
                worldUptimeMs = WebAdminGame.WorldUptimeMs,
                processStartUtc = ProcessStartUtc(),
                systemDataStale = Envir.SystemDataStale,
            });
        }

        private static Task GetState(WebAdminRequest req)
        {
            var (lines, _) = WebAdminLogTap.Get("server", 0, WebAdminLogTap.DefaultCapacity);
            var lastError = WebAdminServerLogScan.FindLastError(lines);

            return req.Json(new
            {
                world = WebAdminGame.WorldState,
                crashed = WebAdminGame.WorldCrashed,
                transition = TransitionInProgress,
                worldUptimeMs = WebAdminGame.WorldUptimeMs,
                countdown = WebAdminServerCountdown.Snapshot(),
                lastError = lastError == null ? null : new { seq = lastError.Seq, utc = lastError.Utc, text = WebAdminServerLogScan.Summary(lastError.Text, 2000) },
            });
        }

        /// <summary>UserData.db's meta.envir_version read through its writer (null when missing or unreadable).</summary>
        private static int? ReadUserDataVersion()
        {
            try
            {
                return Envir.UserStore.ReadEnvirVersion();
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Reading the UserData.db version failed");
                return null;
            }
        }

        /// <summary>The version int at the start of Server.MirDB (null when missing/unreadable).</summary>
        private static int? ReadFileVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 4) return null;
                    return reader.ReadInt32();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static DateTime? ProcessStartUtc()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    return p.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Finds the most recent error in the server log ring (pure; tested).</summary>
    internal static class WebAdminServerLogScan
    {
        // "System.NullReferenceException: ..." / "Npgsql.SomeError: ..." (MessageQueue.Enqueue(Exception) stores ex.ToString()).
        private static readonly Regex ExceptionHead = new Regex(@"^\s*[A-Za-z_][\w.`]*(Exception|Error)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsErrorLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (ExceptionHead.IsMatch(text)) return true;
            return text.IndexOf("Exception:", StringComparison.Ordinal) >= 0
                || text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase);
        }

        public static LogLine FindLastError(IReadOnlyList<LogLine> lines)
        {
            if (lines == null) return null;

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line != null && IsErrorLine(line.Text)) return line;
            }

            return null;
        }

        public static string Summary(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length > max ? text.Substring(0, max) + "…" : text;
        }
    }

    /// <summary>Restart countdown schedule and texts (pure; tested).</summary>
    internal static class WebAdminRestartPlan
    {
        private static readonly int[] StandardMarks = { 3600, 2700, 1800, 1200, 900, 600, 300, 240, 180, 120, 60, 30, 10 };

        /// <summary>Remaining-seconds marks at which players are warned, descending: the full length first, then the standard marks below it.</summary>
        public static List<int> Marks(int totalSeconds)
        {
            var marks = new List<int>();
            if (totalSeconds <= 0) return marks;

            marks.Add(totalSeconds);
            foreach (var m in StandardMarks)
            {
                if (m < totalSeconds) marks.Add(m);
            }

            return marks;
        }

        /// <summary>
        /// Removes every mark already reached (remaining &lt;= mark) from <paramref name="pending"/> (descending) and returns the smallest of
        /// them — the warning to show now — or null when no mark was reached.
        /// </summary>
        public static int? NextAnnouncement(List<int> pending, double remainingSeconds)
        {
            int? hit = null;
            while (pending != null && pending.Count > 0 && remainingSeconds <= pending[0])
            {
                hit = pending[0];
                pending.RemoveAt(0);
            }

            return hit;
        }

        public static string AnnouncementText(int remainingSeconds, string message)
        {
            string when;
            if (remainingSeconds >= 60)
            {
                var minutes = (int)Math.Round(remainingSeconds / 60.0, MidpointRounding.AwayFromZero);
                when = minutes + " minute" + (minutes == 1 ? "" : "s");
            }
            else
            {
                var seconds = Math.Max(1, remainingSeconds);
                when = seconds + " second" + (seconds == 1 ? "" : "s");
            }

            var text = "Server restarting in " + when + ".";
            if (!string.IsNullOrWhiteSpace(message)) text += " " + message.Trim();
            return text;
        }
    }

    /// <summary>
    /// The single restart countdown: warns players (Announcement chat) at the <see cref="WebAdminRestartPlan"/> marks, then reboots the world.
    /// Cancelled by the cancel endpoint, by Stop / Reboot / Shutdown from the web and when the world stops for any reason.
    /// </summary>
    internal static class WebAdminServerCountdown
    {
        private static readonly object Gate = new object();
        private static System.Threading.Timer _timer;
        private static bool _active;
        private static int _minutes;
        private static string _message;
        private static string _admin;
        private static string _ip;
        private static DateTime _startedUtc;
        private static DateTime _endsUtc;
        private static List<int> _pending = new List<int>();

        public static bool Active
        {
            get
            {
                lock (Gate) return _active;
            }
        }

        public static object Snapshot()
        {
            lock (Gate)
            {
                if (!_active) return new { active = false };

                var remaining = Math.Max(0, (int)Math.Ceiling((_endsUtc - DateTime.UtcNow).TotalSeconds));
                return new
                {
                    active = true,
                    minutes = _minutes,
                    message = _message,
                    admin = _admin,
                    startedUtc = _startedUtc,
                    endsUtc = _endsUtc,
                    remainingSec = remaining,
                };
            }
        }

        public static bool TryArm(int minutes, string message, string admin, string ip)
        {
            lock (Gate)
            {
                if (_active) return false;

                _active = true;
                _minutes = minutes;
                _message = message;
                _admin = admin;
                _ip = ip;
                _startedUtc = DateTime.UtcNow;
                _endsUtc = _startedUtc.AddMinutes(minutes);
                _pending = WebAdminRestartPlan.Marks(minutes * 60);
                _timer?.Dispose();
                _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }

            MessageQueue.Instance.Enqueue($"[WebAdmin] Restart countdown armed by {admin}: {minutes} minute(s).");
            return true;
        }

        /// <summary>Stops the countdown. Returns false when none was running.</summary>
        public static bool Cancel(string admin, string ip, string why, bool announce)
        {
            lock (Gate)
            {
                if (!_active) return false;
                ClearLocked();
            }

            MessageQueue.Instance.Enqueue("[WebAdmin] Restart countdown cancelled (" + (why ?? "cancelled") + ").");
            // An explicit cancel is audited (and shows in the activity feed from there); automatic ones are noted here.
            if (announce) Announce("The scheduled server restart was cancelled.");
            else WebAdminActivity.Add("admin", "Restart countdown cancelled (" + (why ?? "cancelled") + ")", null, admin);
            return true;
        }

        private static void ClearLocked()
        {
            _active = false;
            _pending = new List<int>();
            _timer?.Dispose();
            _timer = null;
        }

        private static void Tick()
        {
            string text = null;
            var fire = false;
            string admin = null, ip = null;

            lock (Gate)
            {
                if (!_active) return;

                var remaining = (_endsUtc - DateTime.UtcNow).TotalSeconds;
                var mark = WebAdminRestartPlan.NextAnnouncement(_pending, remaining);
                if (mark.HasValue && remaining > 0) text = WebAdminRestartPlan.AnnouncementText(mark.Value, _message);

                if (remaining <= 0)
                {
                    fire = true;
                    admin = _admin;
                    ip = _ip;
                    ClearLocked();
                }
            }

            try
            {
                if (text != null) Announce(text);
                if (fire) Fire(admin, ip);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Restart countdown tick failed");
            }
        }

        private static void Announce(string text)
        {
            MessageQueue.Instance.Enqueue("[WebAdmin] " + text);
            WebAdminGame.Post(() => Envir.Main.Broadcast(new S.Chat { Message = text, Type = ChatType.Announcement }));
        }

        private static void Fire(string admin, string ip)
        {
            if (!ServerControlRoutes.TryBeginRebootTransition())
            {
                MessageQueue.Instance.Enqueue("[WebAdmin] Restart countdown finished but another start/stop/reboot is in progress; skipping the reboot.");
                return;
            }

            try
            {
                var state = WebAdminGame.WorldState;
                if (state != "running" && !WebAdminGame.WorldCrashed)
                {
                    MessageQueue.Instance.Enqueue("[WebAdmin] Restart countdown finished but the world is " + state + "; nothing to reboot.");
                    ServerControlRoutes.EndRebootTransition();
                    return;
                }

                // Wait (briefly) for the final broadcast to run on the game thread: Stop() fails queued work that has not run yet.
                const string finalText = "Server is restarting now.";
                MessageQueue.Instance.Enqueue("[WebAdmin] " + finalText);
                try
                {
                    WebAdminGame.RunAsync(() => Envir.Main.Broadcast(new S.Chat { Message = finalText, Type = ChatType.Announcement }), 3000)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Final restart announcement was not delivered");
                }

                WebAdminAudit.Record(admin, ip, "server.reboot", "world", new { source = "restart-countdown" });
            }
            catch
            {
                ServerControlRoutes.EndRebootTransition();
                throw;
            }

            ServerControlRoutes.RunRebootHoldingTransition();
        }
    }
}
