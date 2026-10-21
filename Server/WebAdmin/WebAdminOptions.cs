namespace Server.WebAdmin
{
    /// <summary>
    /// Web admin configuration snapshot. Built from Settings.WebAdmin* by <see cref="FromSettings"/> at host start;
    /// tests construct it directly so they never touch the static Settings / Setup.ini.
    /// </summary>
    public sealed class WebAdminOptions
    {
        /// <summary>Hold this while reading/writing Settings (InIReader) from web threads: it is not thread-safe.</summary>
        public static readonly object SettingsLock = new object();

        /// <summary>The options the running host was started with (null before the first start).</summary>
        public static WebAdminOptions Current { get; internal set; }

        public string Bind { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 5680;

        /// <summary>Absolute path of the static web root.</summary>
        public string WebRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        public int SessionHours { get; set; } = 8;
        public int MaxBodyKB { get; set; } = 2048;

        /// <summary>Absolute client Data folder for the Live Monitor's map art, or null when not configured.</summary>
        public string ClientDataPath { get; set; }

        /// <summary>Absolute source checkout for the Tools page, or null to search above the exe folder.</summary>
        public string RepoPath { get; set; }
        public int MaxConcurrentRequests { get; set; } = 32;
        public int LoginPerIpPerMinute { get; set; } = 10;
        public int LoginGlobalPerMinute { get; set; } = 120;
        public int LoginFailuresPerAccount { get; set; } = 10;
        public int LoginFailureWindowMinutes { get; set; } = 15;

        public long MaxBodyBytes => (long)Math.Clamp(MaxBodyKB, 16, 64 * 1024) * 1024;

        public TimeSpan SessionLifetime => TimeSpan.FromHours(Math.Clamp(SessionHours, 1, 24 * 30));

        /// <summary>The HttpListener prefix: 0.0.0.0 / * / + map to the strong wildcard '+'.</summary>
        public string Prefix => $"http://{ListenerHost(Bind)}:{Port}/";

        /// <summary>The URL shown in the log ("http://bind:port/admin/").</summary>
        public string DisplayUrl => $"http://{(string.IsNullOrWhiteSpace(Bind) ? "127.0.0.1" : Bind.Trim())}:{Port}/admin/";

        public static WebAdminOptions FromSettings()
        {
            lock (SettingsLock)
            {
                return new WebAdminOptions
                {
                    Bind = string.IsNullOrWhiteSpace(Settings.WebAdminBind) ? "127.0.0.1" : Settings.WebAdminBind.Trim(),
                    Port = Settings.WebAdminPort == 0 ? 5680 : Settings.WebAdminPort,
                    WebRoot = ResolveWebRoot(Settings.WebAdminWebRoot, AppContext.BaseDirectory),
                    SessionHours = Settings.WebAdminSessionHours <= 0 ? 8 : Settings.WebAdminSessionHours,
                    MaxBodyKB = Settings.WebAdminMaxBodyKB <= 0 ? 2048 : Settings.WebAdminMaxBodyKB,
                    ClientDataPath = ResolveClientDataPath(Settings.WebAdminClientDataPath, AppContext.BaseDirectory),
                    RepoPath = ResolveClientDataPath(Settings.WebAdminRepoPath, AppContext.BaseDirectory),
                };
            }
        }

        /// <summary>Absolute web root: <paramref name="configured"/> if rooted, otherwise relative to <paramref name="baseDirectory"/>.</summary>
        public static string ResolveWebRoot(string configured, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(configured)) configured = "wwwroot";

            configured = configured.Trim();

            var full = Path.IsPathRooted(configured) && !IsDriveRelative(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(baseDirectory, configured.TrimStart('\\', '/')));

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>Absolute client Data folder (relative paths against <paramref name="baseDirectory"/>), or null when empty.</summary>
        public static string ResolveClientDataPath(string configured, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(configured)) return null;
            return ResolveWebRoot(configured, baseDirectory);
        }

        internal static string ListenerHost(string bind)
        {
            if (string.IsNullOrWhiteSpace(bind)) return "127.0.0.1";

            bind = bind.Trim();
            if (bind == "0.0.0.0" || bind == "*" || bind == "+" || bind == "::" || bind == "[::]") return "+";

            if (System.Net.IPAddress.TryParse(bind, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !bind.StartsWith("["))
                return "[" + bind + "]";

            return bind;
        }

        private static bool IsDriveRelative(string path)
        {
            // "\wwwroot" or "/wwwroot" is "rooted" on Windows but relative to the current drive: treat as relative to the exe dir.
            return path.Length > 0 && (path[0] == '\\' || path[0] == '/') && !(path.Length > 1 && (path[1] == '\\' || path[1] == '/'));
        }
    }
}
