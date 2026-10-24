using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.MirEnvir;

namespace Server.WebAdmin.Tools
{
    /// <summary>
    /// The Tools page (Monitoring group): runs the StressClient bot fleet, builds it, and starts/stops a scratch Server.exe
    /// through tools\start-scratch-server.ps1 / stop-scratch-server.ps1. Everything here is process management on HTTP and
    /// thread-pool threads: it never touches game state and never takes a game lock. Settings are only read (single static
    /// fields). One bot fleet, one build and one scratch operation at a time, guarded by <see cref="Gate"/>.
    ///
    /// Bots target either the scratch server or THIS server ("self"). Self needs [LoadTest] ExemptLoopback=True,
    /// CheckVersion=False, logins and StartGame allowed, a loopback/any bind and a non-default bot password: it is for the
    /// operator only (agents test against the scratch server, Knowledgebase/StressClient/stressclient.md §9).
    /// </summary>
    public static class WebAdminTools
    {
        private const int HistoryLength = 300;
        private const int StopGraceSeconds = 15;

        private static readonly object Gate = new object();
        private static ToolProcess _bots, _build, _scratchOp;
        private static StressBotConfig _botConfig;
        private static string _botsBy;
        private static JsonElement? _botStatus;
        private static readonly LinkedList<object> History = new LinkedList<object>();
        private static int _hooked;

        public static string ScratchFolder => Path.Combine(Path.GetTempPath(), "mir2-webadmin-scratch");
        private static string BotsFolder => Path.Combine(Path.GetTempPath(), "mir2-webadmin-bots");

        /// <summary>Stops a self-targeted fleet with the world, and every fleet with the process (stdin EOF also does that).</summary>
        internal static void Hook()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;

            WebAdminHooks.WorldStopped += () =>
            {
                // Game thread: StopBots only schedules the stdin write and the kill on the pool.
                if (_botConfig?.Target == StressBotConfig.TargetSelf) StopBots("world stopped");
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _bots?.CloseInput();
        }

        // ------------------------------------------------------------------ paths

        /// <summary>The source checkout: [WebAdmin] RepoPath, else the first folder above the exe with Legend of Mir.sln.</summary>
        public static string ResolveRepo(string configured, string baseDirectory, out string source)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                source = "setting";
                return File.Exists(Path.Combine(configured, "Legend of Mir.sln")) ? configured : null;
            }

            source = "search";
            for (var dir = new DirectoryInfo(baseDirectory ?? AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) return dir.FullName;
            return null;
        }

        private static string Repo(out string source) => ResolveRepo(WebAdminOptions.Current?.RepoPath, AppContext.BaseDirectory, out source);

        private static string RequireRepo()
        {
            var repo = Repo(out var source);
            if (repo == null)
                throw new WebAdminException(409, source == "setting"
                    ? "[WebAdmin] RepoPath does not point at a Crystal source folder (no Legend of Mir.sln)."
                    : "No Crystal source folder found above Server.exe. Set [WebAdmin] RepoPath in Server Config and restart Server.exe.");
            return repo;
        }

        /// <summary>The newest StressClient.exe build in the checkout (Debug or Release), or null.</summary>
        private static FileInfo BotExe(string repo)
        {
            if (repo == null) return null;
            return new[] { "Debug", "Release" }
                .Select(c => new FileInfo(Path.Combine(repo, "StressClient", "bin", c, "net10.0", "StressClient.exe")))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static string ScratchRun => Path.Combine(ScratchFolder, "run");

        // ------------------------------------------------------------------ status

        public static object Status()
        {
            var repo = Repo(out var repoSource);
            var exe = BotExe(repo);
            var scratch = ScratchDto(repo);
            var self = SelfDto();

            lock (Gate)
            {
                return new
                {
                    repo = new { path = repo, found = repo != null, source = repoSource },
                    bots = new
                    {
                        exe = exe?.FullName,
                        exeFound = exe != null,
                        exeBuiltUtc = exe?.LastWriteTimeUtc,
                        state = _bots == null ? "idle" : _bots.Running ? "running" : "exited",
                        pid = _bots?.Running == true ? _bots.Pid : (int?)null,
                        startedUtc = _bots?.StartedUtc,
                        exitedUtc = _bots?.ExitedUtc,
                        exitCode = _bots?.ExitCode,
                        by = _botsBy,
                        config = _botConfig?.ToDto(),
                        status = _botStatus,
                        history = History.ToArray(),
                        log = _bots?.Tail(250) ?? Array.Empty<string>(),
                    },
                    build = OpDto(_build),
                    scratch,
                    self,
                    maxBots = StressBotConfig.MaxBots,
                };
            }
        }

        private static object OpDto(ToolProcess p) => p == null ? null : new
        {
            kind = p.Kind,
            running = p.Running,
            startedUtc = p.StartedUtc,
            exitedUtc = p.ExitedUtc,
            exitCode = p.ExitCode,
            log = p.Tail(150),
        };

        // ------------------------------------------------------------------ self target checks

        internal sealed class Check
        {
            public string Label { get; set; }
            public bool Ok { get; set; }
            public bool Required { get; set; }
            public string Detail { get; set; }
        }

        internal static List<Check> SelfChecks(string worldState)
        {
            var bind = Settings.IPAddress ?? string.Empty;
            return new List<Check>
            {
                new Check { Label = "World running", Ok = worldState == "running", Required = true, Detail = "World is " + worldState + "." },
                new Check { Label = "[LoadTest] ExemptLoopback = True", Ok = Settings.LoadTestExemptLoopback, Required = true,
                    Detail = "Without it the bots hit MaxIP and the per-IP account/character limits (24 h IP blocks)." },
                new Check { Label = "[General] CheckVersion = False", Ok = !Settings.CheckVersion, Required = true,
                    Detail = "The bots send a dummy client hash." },
                new Check { Label = "Logins and StartGame allowed", Ok = Settings.AllowLogin && Settings.AllowStartGame, Required = true,
                    Detail = "[Permission] AllowLogin and AllowStartGame (bots are not admin accounts)." },
                new Check { Label = "Loopback or any bind", Ok = IsLoopbackOrAny(bind), Required = true,
                    Detail = "[Network] IPAddress is '" + bind + "'; the bots connect from 127.0.0.1." },
                new Check { Label = "New accounts and characters allowed", Ok = Settings.AllowNewAccount && Settings.AllowNewCharacter, Required = false,
                    Detail = "Otherwise only bot accounts that already exist can play." },
            };
        }

        internal static bool IsLoopbackOrAny(string bind)
        {
            if (!IPAddress.TryParse((bind ?? string.Empty).Trim(), out var ip)) return false;
            return IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any);
        }

        private static object SelfDto()
        {
            var checks = SelfChecks(WebAdminGame.WorldState);
            return new
            {
                port = (int)Settings.Port,
                maxUser = (int)Settings.MaxUser,
                allowAssassin = Settings.AllowCreateAssassin,
                allowArcher = Settings.AllowCreateArcher,
                ok = checks.All(c => c.Ok || !c.Required),
                checks = checks.Select(c => new { label = c.Label, ok = c.Ok, required = c.Required, detail = c.Detail }).ToArray(),
            };
        }

        private static int SelfMaxPacket() => Settings.LoadTestLoopbackMaxPacket > 0 ? Settings.LoadTestLoopbackMaxPacket : Settings.MaxPacket;

        // ------------------------------------------------------------------ bots

        public static object StartBots(StressBotConfig config, string admin)
        {
            var repo = RequireRepo();
            var exe = BotExe(repo) ?? throw new WebAdminException(409, "StressClient is not built yet. Use 'Build bots' first.");

            string host = "127.0.0.1";
            int port, maxPacket;
            string mapFolder;

            if (config.Target == StressBotConfig.TargetSelf)
            {
                var failed = SelfChecks(WebAdminGame.WorldState).Where(c => c.Required && !c.Ok).Select(c => c.Label).ToArray();
                if (failed.Length > 0) throw new WebAdminException(409, "This server is not ready for bots: " + string.Join("; ", failed) + ".");
                if (config.Password == StressBotConfig.DefaultPassword)
                    throw new WebAdminException(400, "Pick your own bot password for this server: the default one is public in the source.");
                if (config.Class == "assassin" && !Settings.AllowCreateAssassin || config.Class == "archer" && !Settings.AllowCreateArcher)
                    throw new WebAdminException(400, "This server does not allow new " + config.Class + " characters ([Permission] AllowCreate" +
                                                     (config.Class == "assassin" ? "Assassin" : "Archer") + "). Pick another class.");
                if (config.Bots > Settings.MaxUser)
                    throw new WebAdminException(400, "This server accepts at most " + Settings.MaxUser + " connections ([Network] MaxUser, players included). " +
                                                     "Raise it in Server Config to run " + config.Bots + " bots here.");
                port = Settings.Port;
                maxPacket = SelfMaxPacket();
                mapFolder = Path.GetFullPath(Settings.MapPath);
            }
            else
            {
                var scratch = ReadScratch();
                if (!scratch.Running) throw new WebAdminException(409, "The scratch server is not running. Start it first.");
                port = scratch.Port;
                maxPacket = scratch.LoopbackMaxPacket;
                mapFolder = Path.Combine(ScratchRun, "Maps");
            }

            Directory.CreateDirectory(BotsFolder);
            var psi = new ProcessStartInfo(exe.FullName) { WorkingDirectory = BotsFolder, RedirectStandardInput = true };
            foreach (var arg in config.BuildArgs(host, port, maxPacket, new[] { mapFolder })) psi.ArgumentList.Add(arg);
            psi.Environment[StressBotConfig.PasswordEnv] = config.Password;

            lock (Gate)
            {
                if (_bots?.Running == true) throw new WebAdminException(409, "A bot fleet is already running. Stop it first.");
                if (_build?.Running == true) throw new WebAdminException(409, "StressClient is being built. Wait for the build to finish.");

                History.Clear();
                _botStatus = null;
                _botConfig = config;
                _botsBy = admin;
                _bots = ToolProcess.Start("bots", psi, OnBotLine, maxLines: 600);
                _bots.Note("started by " + admin + ": " + config.Bots + " bots -> " + host + ":" + port + " (" + config.Target + ")");
            }

            WebAdminLog.Info("Tools: " + admin + " started " + config.Bots + " stress bots against " + config.Target + " (" + host + ":" + port + ")");
            return new { host, port, maxPacket, mapFolder, exe = exe.FullName };
        }

        /// <summary>
        /// "This server" only: bot accounts in the run's range that already exist must take the run's password, or every
        /// bot would hit the wrong-password lockout (6 attempts = 2 min ban, Envir.Login). The accounts are read in one
        /// game-thread step; up to <see cref="PasswordSamples"/> hashes are checked here on the HTTP thread.
        /// </summary>
        public static async Task CheckSelfAccountsAsync(StressBotConfig config)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.Bots; i++)
                ids.Add(config.Prefix + (config.FirstIndex + i).ToString("D5", CultureInfo.InvariantCulture));

            var existing = await WebAdminGame.RunAsync(() =>
            {
                var list = new List<ExistingBotAccount>();
                var now = Envir.Main.Now;
                foreach (var a in Envir.Main.AccountList)
                {
                    if (!ids.Contains(a.AccountID)) continue;
                    list.Add(new ExistingBotAccount { Id = a.AccountID, Salt = a.Salt, Hash = a.Password, BannedUntil = a.Banned && a.ExpiryDate > now ? a.ExpiryDate : (DateTime?)null });
                }
                return list;
            }).ConfigureAwait(false);

            if (existing.Count == 0) return;

            var wrong = existing.Take(PasswordSamples).Count(a => !Server.Utils.Crypto.Verify(config.Password, a.Salt, a.Hash));
            if (wrong > 0)
                throw new WebAdminException(409, existing.Count + " of these bot accounts already exist (" + existing[0].Id + "…) with a different password from an " +
                                                 "earlier run. Use that run's password, or another account prefix / first index.");

            var banned = existing.Where(a => a.BannedUntil != null).ToList();
            if (banned.Count > 0)
                throw new WebAdminException(409, banned.Count + " of these bot accounts are banned right now (e.g. " + banned[0].Id + " until " +
                                                 banned.Max(a => a.BannedUntil.Value).ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                                                 "; wrong-password lockouts last 2 minutes). Try again after that, or unban them in Accounts.");
        }

        private const int PasswordSamples = 3;

        private sealed class ExistingBotAccount
        {
            public string Id;
            public byte[] Salt;
            public string Hash;
            public DateTime? BannedUntil;
        }

        private static bool OnBotLine(string line)
        {
            if (!line.StartsWith("@status ", StringComparison.Ordinal)) return false;

            JsonElement status;
            try
            {
                using var doc = JsonDocument.Parse(line.AsMemory(8));
                status = doc.RootElement.Clone();
            }
            catch (JsonException) { return true; }

            object Pick(string name) => status.TryGetProperty(name, out var v) ? v.GetDouble() : 0d;
            var sample = new
            {
                t = Pick("t"),
                inGame = Pick("inGame"),
                connecting = Pick("connecting"),
                sentPps = Pick("sentPps"),
                recvPps = Pick("recvPps"),
                walkPs = Pick("walkPs"),
                attackPs = Pick("attackPs"),
                rttMs = status.TryGetProperty("rttMs", out var rtt) ? rtt.GetDouble() : (double?)null,
            };

            lock (Gate)
            {
                _botStatus = status;
                History.AddLast(sample);
                while (History.Count > HistoryLength) History.RemoveFirst();
            }
            return true;
        }

        /// <summary>Asks the fleet to stop (graceful disconnects), and kills it if it is still there after the grace time.
        /// Non-blocking: safe from the game thread (WorldStopped hook).</summary>
        public static bool StopBots(string reason)
        {
            ToolProcess bots;
            lock (Gate) bots = _bots;
            if (bots == null || !bots.Running) return false;

            bots.Note("stop requested (" + reason + ")");
            // The stdin write blocks while the pipe is full (a hung fleet stops reading), so it runs on the pool, never on the
            // caller's thread; the kill below ends the process and with it a stuck write.
            _ = Task.Run(() => { if (!bots.WriteLine("stop")) bots.CloseInput(); });
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(StopGraceSeconds)).ConfigureAwait(false);
                if (bots.Running)
                {
                    bots.Note("still running after " + StopGraceSeconds + " s: killed");
                    bots.Kill();
                }
            });
            return true;
        }

        // ------------------------------------------------------------------ build

        public static void BuildBots(string admin)
        {
            var repo = RequireRepo();
            var project = Path.Combine(repo, "StressClient", "StressClient.csproj");
            if (!File.Exists(project)) throw new WebAdminException(409, "No StressClient project in " + repo + ".");

            var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = repo };
            foreach (var arg in new[] { "build", project, "-c", "Debug", "-nologo", "-v:minimal" }) psi.ArgumentList.Add(arg);

            lock (Gate)
            {
                if (_build?.Running == true) throw new WebAdminException(409, "A build is already running.");
                if (_bots?.Running == true) throw new WebAdminException(409, "Stop the bot fleet first: its exe would be locked.");
                try { _build = ToolProcess.Start("build", psi); }
                catch (System.ComponentModel.Win32Exception) { throw new WebAdminException(409, "The dotnet SDK was not found on PATH."); }
                _build.Note("dotnet build StressClient (Debug), by " + admin);
            }
        }

        // ------------------------------------------------------------------ scratch server

        internal sealed class ScratchState
        {
            public bool Running { get; set; }
            public int? Pid { get; set; }
            public int Port { get; set; }
            public int LoopbackMaxPacket { get; set; } = 400;
            public bool WorldExists { get; set; }
            public int MapFiles { get; set; }
            public bool ServerBuilt { get; set; }
        }

        internal static ScratchState ReadScratch()
        {
            var s = new ScratchState
            {
                ServerBuilt = File.Exists(Path.Combine(ScratchFolder, "build", "Server", "Server.exe")),
                WorldExists = File.Exists(Path.Combine(ScratchRun, "Server.MirDB")) || File.Exists(Path.Combine(ScratchRun, "SystemData.db")),
            };

            var maps = Path.Combine(ScratchRun, "Maps");
            if (Directory.Exists(maps)) s.MapFiles = Directory.GetFiles(maps, "*.map").Length;

            var ini = Path.Combine(ScratchRun, "Configs", "Setup.ini");
            if (File.Exists(ini))
            {
                var text = File.ReadAllText(ini);
                s.Port = IniInt(text, "Port", 0);
                s.LoopbackMaxPacket = IniInt(text, "LoopbackMaxPacket", 400);
            }

            var pidFile = Path.Combine(ScratchFolder, "server.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadLines(pidFile).FirstOrDefault(), out var pid))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    var path = p.MainModule?.FileName;
                    s.Running = !p.HasExited && path != null && Path.GetFileName(path).Equals("Server.exe", StringComparison.OrdinalIgnoreCase) &&
                                Path.GetFullPath(path).StartsWith(Path.GetFullPath(ScratchFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                    if (s.Running) s.Pid = pid;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return s;
        }

        private static int IniInt(string text, string key, int fallback)
        {
            var m = Regex.Match(text, @"^\s*" + Regex.Escape(key) + @"\s*=\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static object ScratchDto(string repo)
        {
            var s = ReadScratch();
            return new
            {
                folder = ScratchFolder,
                running = s.Running,
                pid = s.Pid,
                port = s.Port,
                worldExists = s.WorldExists,
                mapFiles = s.MapFiles,
                serverBuilt = s.ServerBuilt,
                generatorBuilt = repo != null && File.Exists(GeneratorPath(repo)),
                liveMaps = Path.GetFullPath(Settings.MapPath),
                op = OpDto(Volatile.Read(ref _scratchOp)),
            };
        }

        private static string GeneratorPath(string repo) => Path.Combine(repo, "tools", "scratch-world", "bin", "Debug", "net10.0", "ScratchWorld.exe");

        /// <summary>Runs tools\start-scratch-server.ps1. <paramref name="copyLiveMaps"/> copies this server's map files
        /// (read-only) instead of generating synthetic ones; both only take effect when the world is (re)generated.</summary>
        public static void StartScratch(int port, int maps, bool reset, bool rebuild, bool copyLiveMaps, string admin)
        {
            var repo = RequireRepo();
            if (port < 17000 || port > 17999) throw new WebAdminException(400, "port must be between 17000 and 17999");
            if (maps < 1 || maps > 20) throw new WebAdminException(400, "maps must be between 1 and 20");
            if (port == Settings.Port || port == (WebAdminOptions.Current?.Port ?? 0)) throw new WebAdminException(400, "That port belongs to this server.");

            var state = ReadScratch();
            if (state.Running) throw new WebAdminException(409, "The scratch server is already running.");

            bool build = rebuild || !state.ServerBuilt || !File.Exists(GeneratorPath(repo));
            var args = new List<string> { "-Port", port.ToString(CultureInfo.InvariantCulture), "-Maps", maps.ToString(CultureInfo.InvariantCulture) };
            if (reset) args.Add("-Reset");
            if (!build) args.Add("-NoBuild");
            if (copyLiveMaps)
            {
                args.Add("-MapsFrom");
                args.Add(Path.GetFullPath(Settings.MapPath));
            }

            RunScript(repo, "start-scratch-server.ps1", args, "scratch-start", admin);
        }

        public static void StopScratch(string admin)
        {
            var repo = RequireRepo();
            lock (Gate)
            {
                if (_bots?.Running == true && _botConfig?.Target == StressBotConfig.TargetScratch)
                    throw new WebAdminException(409, "Stop the bot fleet first: it is playing on the scratch server.");
            }
            RunScript(repo, "stop-scratch-server.ps1", new List<string>(), "scratch-stop", admin);
        }

        private static void RunScript(string repo, string script, List<string> args, string kind, string admin)
        {
            var path = Path.Combine(repo, "tools", script);
            if (!File.Exists(path)) throw new WebAdminException(409, "Missing " + path + ".");

            var psi = new ProcessStartInfo("powershell.exe") { WorkingDirectory = repo };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path, "-Scratch", ScratchFolder }) psi.ArgumentList.Add(a);
            foreach (var a in args) psi.ArgumentList.Add(a);

            lock (Gate)
            {
                if (_scratchOp?.Running == true) throw new WebAdminException(409, "A scratch server operation is already running.");
                _scratchOp = ToolProcess.Start(kind, psi);
                _scratchOp.Note(script + " " + string.Join(" ", args) + " (by " + admin + ")");
            }
        }
    }
}
