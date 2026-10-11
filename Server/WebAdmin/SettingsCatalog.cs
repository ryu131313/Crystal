using System.Globalization;
using System.Text.Json;

namespace Server.WebAdmin
{
    /// <summary>When a Setup.ini change takes effect.</summary>
    public enum SettingRestart
    {
        /// <summary>Read by the game at use time: applies immediately.</summary>
        None = 0,

        /// <summary>Read when the world starts (StartEnvir / StartNetwork / WorkLoop): applies after a world reboot.</summary>
        World = 1,

        /// <summary>Read once per process (Settings.Load at startup, Envir construction, web admin host start): needs a full server restart.</summary>
        Process = 2,
    }

    /// <summary>
    /// One Setup.ini key handled by <c>Settings.Load()</c> / <c>Settings.Save()</c>: where it lives in the ini, which
    /// <c>Settings</c> static member holds it (a field, or for GroupInviteDelay / TradeDelay a static property), and how to edit it.
    /// Pure metadata: building or reading a descriptor never touches the static <c>Settings</c> class (whose static
    /// constructor opens Configs\Setup.ini).
    /// </summary>
    public sealed class SettingDescriptor
    {
        public string Group { get; init; }
        public string Section { get; init; }
        public string Key { get; init; }

        /// <summary>Name of the public static member on <c>Server.Settings</c>.</summary>
        public string Field { get; init; }

        /// <summary>CLR type of the member.</summary>
        public Type Type { get; init; }

        /// <summary>True when the member is a static property (GroupInviteDelay, TradeDelay) instead of a field.</summary>
        public bool IsProperty { get; init; }

        public SettingRestart Restart { get; init; }
        public bool Secret { get; init; }
        public string Label { get; init; }
        public string Help { get; init; }

        /// <summary>Inclusive lower / upper bound (numbers); null = the CLR range of <see cref="Type"/>.</summary>
        public double? Min { get; init; }
        public double? Max { get; init; }

        /// <summary>Extra validation / UI hint for strings: ip, bind, url-prefix, path, path-list, monster, map, language, name.</summary>
        public string Format { get; init; }

        /// <summary>
        /// False for keys <c>Settings.Load()</c> reads but <c>Settings.Save()</c> never writes (GMPassword, MaxPacket, MonsterRecall*):
        /// the config route writes those to Setup.ini itself after Settings.Save().
        /// </summary>
        public bool SavedBySettingsSave { get; init; } = true;

        public bool RestartRequired => Restart != SettingRestart.None;

        /// <summary>UI type name: bool, int, float, string, enum, timespan, point.</summary>
        public string Kind => SettingsValueCodec.KindOf(Type);

        public string RestartName => Restart == SettingRestart.World ? "world" : Restart == SettingRestart.Process ? "process" : "none";

        public override string ToString() => "[" + Section + "] " + Key + " (" + Field + ")";
    }

    /// <summary>
    /// Catalogue of every Setup.ini key that <c>Settings.Load()</c> / <c>Settings.Save()</c> (Server\Settings.cs) handle,
    /// in display order. Excluded on purpose: <c>[Game] FishMonster</c> — Load() reads it into Settings.FishingMonster but
    /// LoadFishing() (called later in Load) overrides it from Configs\FishingSystem.ini, so editing it here would have no
    /// effect (B7's Systems › Fishing edits FishingMonster).
    /// </summary>
    public static class SettingsCatalog
    {
        public const string GroupGeneral = "General";
        public const string GroupNetwork = "Network";
        public const string GroupWebAdmin = "Web Admin";
        public const string GroupPermissions = "Permissions";
        public const string GroupOptional = "Optional";
        public const string GroupDatabase = "Database";
        public const string GroupRates = "Rates & Drops";
        public const string GroupGameplay = "Gameplay";
        public const string GroupRested = "Rested";
        public const string GroupItems = "Items";
        public const string GroupMonsters = "Monster Names";
        public const string GroupArchive = "Archive";

        /// <summary>Setup.ini keys Settings.Load() reads that are intentionally not in the catalogue (section, key).</summary>
        public static readonly IReadOnlyList<(string Section, string Key)> ExcludedKeys = new[]
        {
            ("Game", "FishMonster"),
        };

        public static readonly IReadOnlyList<SettingDescriptor> All = Build();

        private static readonly Dictionary<string, SettingDescriptor> ByFieldMap =
            All.ToDictionary(d => d.Field, StringComparer.OrdinalIgnoreCase);

        /// <summary>Descriptor by Settings member name (case-insensitive), or null.</summary>
        public static SettingDescriptor ByField(string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return null;
            return ByFieldMap.TryGetValue(field.Trim(), out var d) ? d : null;
        }

        /// <summary>Descriptor by ini section + key (case-insensitive), or null.</summary>
        public static SettingDescriptor ByKey(string section, string key)
        {
            return All.FirstOrDefault(d => string.Equals(d.Section, section, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Display groups in catalogue order.</summary>
        public static IReadOnlyList<string> Groups()
        {
            var list = new List<string>();
            foreach (var d in All)
            {
                if (!list.Contains(d.Group)) list.Add(d.Group);
            }

            return list;
        }

        /// <summary>True when a changed version key requires <c>Settings.LoadVersion()</c> (client hash list).</summary>
        public static bool IsVersionKey(SettingDescriptor d)
        {
            return d != null && (d.Field == "VersionPath" || d.Field == "CheckVersion");
        }

        private static IReadOnlyList<SettingDescriptor> Build()
        {
            var list = new List<SettingDescriptor>();

            void Add(string group, string section, string key, string field, Type type, string label, string help,
                SettingRestart restart = SettingRestart.None, double? min = null, double? max = null, string format = null,
                bool secret = false, bool savedBySave = true, bool isProperty = false)
            {
                list.Add(new SettingDescriptor
                {
                    Group = group,
                    Section = section,
                    Key = key,
                    Field = field,
                    Type = type,
                    Label = label,
                    Help = help,
                    Restart = restart,
                    Min = min,
                    Max = max,
                    Format = format,
                    Secret = secret,
                    SavedBySettingsSave = savedBySave,
                    IsProperty = isProperty,
                });
            }

            var B = typeof(bool);
            var U8 = typeof(byte);
            var I16 = typeof(short);
            var U16 = typeof(ushort);
            var I32 = typeof(int);
            var U32 = typeof(uint);
            var I64 = typeof(long);
            var F32 = typeof(float);
            var S = typeof(string);

            // ---- General ------------------------------------------------------------------------------------------
            const string G = GroupGeneral;
            Add(G, "General", "VersionPath", "VersionPath", S, "Client version file(s)",
                "Comma-separated paths of the client executable(s) whose MD5 hashes are accepted when version checking is on. Hashes are recomputed on save.",
                format: "path-list");
            Add(G, "General", "CheckVersion", "CheckVersion", B, "Check client version",
                "Reject clients whose executable hash does not match one of the version files.");
            Add(G, "General", "RelogDelay", "RelogDelay", U16, "Relog delay (ms)",
                "Minimum delay between logging out and starting the game again. Currently unused by the server (the check is disabled in code).");
            Add(G, "General", "GMPassword", "GMPassword", S, "GM password",
                "Password for the in-game @LOGIN GM command (non-admin accounts). Applies to players who log in after the change.",
                secret: true, savedBySave: false);
            Add(G, "General", "TestServer", "TestServer", B, "Test server",
                "Test-server mode: every player may use the test commands and gets the test-server start-game behaviour.");
            Add(G, "General", "EnforceDBChecks", "EnforceDBChecks", B, "Enforce database checks",
                "Run the database consistency checks when the world starts (startup fails on errors).",
                SettingRestart.World);
            Add(G, "General", "MonsterProcessWhenAlone", "MonsterProcessWhenAlone", B, "Process monsters with no players nearby",
                "Keep processing monsters that have no player in range (costs CPU).");
            Add(G, "General", "Language", "Language", S, "Server language",
                "Localization file (Localization\\<name>.json) used for server messages. Loaded once when the server process starts.",
                SettingRestart.Process, format: "language");
            Add(G, "General", "Multithreaded", "Multithreaded", B, "Multithreaded monsters",
                "Process maps/monsters on worker threads.", SettingRestart.World);
            Add(G, "General", "ThreadLimit", "ThreadLimit", I32, "Thread limit",
                "Number of monster worker threads. The thread array is sized when the server process starts.",
                SettingRestart.Process, min: 1, max: 64);

            // ---- Network ------------------------------------------------------------------------------------------
            const string N = GroupNetwork;
            Add(N, "Network", "IPAddress", "IPAddress", S, "Game bind address",
                "IP address the game listener (and the status port 3100) binds to. 0.0.0.0 = all interfaces.",
                SettingRestart.World, format: "ip");
            Add(N, "Network", "Port", "Port", U16, "Game port",
                "TCP port of the game listener.", SettingRestart.World, min: 1, max: 65535);
            Add(N, "Network", "TimeOut", "TimeOut", U16, "Connection timeout (ms)",
                "A connection that sends nothing for this long is disconnected.", min: 1000);
            Add(N, "Network", "MaxUser", "MaxUser", U16, "Max connections",
                "Maximum simultaneous connections; new connections beyond it are refused.", min: 1);
            Add(N, "Network", "MaxIP", "MaxIP", U16, "Max connections per IP",
                "Connections allowed from one IP before it is temporarily blocked.", min: 1);
            Add(N, "Network", "MaxPacket", "MaxPacket", U16, "Max packets per interval",
                "Flood protection: a connection that sends more packets than this per processing interval is disconnected.",
                min: 1, savedBySave: false);
            Add(N, "Network", "IPBlockSeconds", "IPBlockSeconds", I32, "Reconnect block (s)",
                "Seconds an IP is blocked after each new connection or after exceeding Max connections per IP.", min: 0, max: 3600);
            Add(N, "Security", "PasswordHashV2", "PasswordHashV2", B, "Stronger password hashes (v2)",
                "Store new and changed passwords as PBKDF2-SHA256 (100k iterations), and upgrade each account on its next successful login. v1 and v2 are always accepted. Leave off until a build containing v2 verification has been running for a while (rollback safety); see roadmap P8.");
            Add(N, "Network", "StatusPortEnabled", "StatusPortEnabled", B, "Status port",
                "Plain-text status listener (player count etc.) on the status port. Turn off for scratch servers sharing a machine.", SettingRestart.World);
            Add(N, "Network", "StatusPort", "StatusPort", U16, "Status port number", "TCP port of the status listener (default 3100).", SettingRestart.World, min: 1);
            Add(N, "LoadTest", "ExemptLoopback", "LoadTestExemptLoopback", B, "Load test: exempt loopback",
                "SCRATCH SERVERS ONLY. Exempts 127.x / ::1 from IP blocks, Max connections per IP and the per-IP account/character limits so a local bot fleet can connect. Never enable behind a reverse proxy: every player would look like loopback.");
            Add(N, "LoadTest", "LoopbackMaxPacket", "LoadTestLoopbackMaxPacket", I32, "Load test: loopback packet limit",
                "Flood limit for loopback connections when the exemption is on (0 = same as Max packets per interval).", min: 0);
            Add(N, "Optimisation", "IdlePark", "IdlePark", B, "Idle game loop",
                "When an iteration has nothing to do, the game loop waits up to 1 ms (woken early by network and admin activity) instead of spinning a CPU core.");
            Add(N, "Optimisation", "MobThreadOnePass", "MobThreadOnePass", B, "Mob threads: one pass per tick",
                "Monster threads walk their list once per tick and then wait, instead of re-walking it for ~10 ms. Only matters with Multithreaded on.");
            Add(N, "Optimisation", "EncodeOnce", "EncodeOnce", B, "Encode shared packets once",
                "A packet sent to many players in the same connection pass (movement, chat, effects) is encoded once and the bytes are reused, instead of being encoded again for every recipient.");
            Add(N, "Optimisation", "HumanProcessMs", "HumanProcessMs", I32, "Player processing interval (ms)",
                "Players and heroes run their per-tick housekeeping (buffs, regen, poison, timers) at most every N ms instead of on every loop iteration. Delayed actions (attack damage, spells, NPC actions) still run on time. 0 = every iteration (old behaviour).", min: 0, max: 50);
            Add(N, "Optimisation", "DueScheduler", "DueScheduler", B, "Due-time object scheduler",
                "Players, monsters, NPCs, items and spells are processed from per-thread queues ordered by when each is next due, instead of walking every object on every loop iteration. Idle CPU then no longer grows with the number of monsters. Off = the old walk. Applies when the world starts.",
                SettingRestart.World);
            Add(N, "Optimisation", "MapRespawnCheckMs", "MapRespawnCheckMs", I32, "Map respawn check interval (ms)",
                "Each map checks its respawns at most every N ms (maps staggered) instead of on every loop iteration. Respawn delays are in minutes, so the extra delay is unnoticeable; a respawn added live spawns within N ms. Doors and map spell actions still run on time. 0 = every iteration (old behaviour).", min: 0, max: 5000);
            Add(N, "Diag", "LogLoopProfile", "LogLoopProfile", B, "Log loop profile",
                "Write one [Diag] Loop line per second to the Debug log (per-phase ms, iterations, packets, GC). The numbers are always on the dashboard status.");
            Add(N, "Network", "StartHTTPService", "StartHTTPService", B, "Legacy HTTP service",
                "Start Crystal's small HTTP service (new account / name list / broadcast URLs) with the world.",
                SettingRestart.World);
            Add(N, "Network", "HTTPIPAddress", "HTTPIPAddress", S, "Legacy HTTP prefix",
                "HttpListener prefix of the legacy HTTP service, e.g. http://127.0.0.1:5679/ (must end with /).",
                SettingRestart.World, format: "url-prefix");
            Add(N, "Network", "HTTPTrustedIPAddress", "HTTPTrustedIPAddress", S, "Legacy HTTP trusted IP",
                "Only requests from this IP are accepted by the legacy HTTP service.", format: "ip");

            // ---- Web admin ----------------------------------------------------------------------------------------
            const string W = GroupWebAdmin;
            Add(W, "WebAdmin", "Enabled", "WebAdminEnabled", B, "Web admin enabled",
                "Start this web admin panel with the server. Turning it off locks you out of the panel after the next restart.",
                SettingRestart.Process);
            Add(W, "WebAdmin", "Bind", "WebAdminBind", S, "Web admin bind address",
                "Address the panel listens on (127.0.0.1 = this machine only; 0.0.0.0 or * = all interfaces, needs a URL ACL).",
                SettingRestart.Process, format: "bind");
            Add(W, "WebAdmin", "Port", "WebAdminPort", U16, "Web admin port",
                "TCP port of this panel.", SettingRestart.Process, min: 1, max: 65535);
            Add(W, "WebAdmin", "WebRoot", "WebAdminWebRoot", S, "Web admin web root",
                "Folder with the panel's static files (relative to the server folder). Must contain admin\\index.html.",
                SettingRestart.Process, format: "path");
            Add(W, "WebAdmin", "SessionHours", "WebAdminSessionHours", I32, "Session lifetime (hours)",
                "How long a panel sign-in stays valid.", SettingRestart.Process, min: 1, max: 720);
            Add(W, "WebAdmin", "MaxBodyKB", "WebAdminMaxBodyKB", I32, "Max request body (KB)",
                "Largest request body the panel accepts (script files, bulk edits).", SettingRestart.Process, min: 16, max: 65536);
            Add(W, "WebAdmin", "ClientDataPath", "WebAdminClientDataPath", S, "Client Data folder (Live Monitor map art)",
                "A game client's Data folder (the one with MMap.Lib and the Map folder). The Live Monitor reads it, never writes it, to draw the "
                + "minimaps and real map tiles. Empty = walkability only.", SettingRestart.Process, format: "path");

            // ---- Permissions --------------------------------------------------------------------------------------
            const string P = GroupPermissions;
            Add(P, "Permission", "AllowNewAccount", "AllowNewAccount", B, "Allow new accounts", "Players may create accounts from the client.");
            Add(P, "Permission", "AllowChangePassword", "AllowChangePassword", B, "Allow password changes", "Players may change their account password from the client.");
            Add(P, "Permission", "AllowLogin", "AllowLogin", B, "Allow login", "Accounts may log in.");
            Add(P, "Permission", "AllowNewCharacter", "AllowNewCharacter", B, "Allow new characters", "Players may create characters.");
            Add(P, "Permission", "AllowDeleteCharacter", "AllowDeleteCharacter", B, "Allow character deletion", "Players may delete characters.");
            Add(P, "Permission", "AllowStartGame", "AllowStartGame", B, "Allow start game", "Non-admin accounts may enter the game (admins always can).");
            Add(P, "Permission", "AllowCreateAssassin", "AllowCreateAssassin", B, "Allow Assassin class", "The Assassin class can be chosen at character creation.");
            Add(P, "Permission", "AllowCreateArcher", "AllowCreateArcher", B, "Allow Archer class", "The Archer class can be chosen at character creation.");
            Add(P, "Permission", "RequireStoragePassword", "RequireStoragePassword", B, "Require storage password", "Storage with a password set must be unlocked before use.");
            Add(P, "Permission", "MaxResolution", "AllowedResolution", I32, "Max client resolution",
                "Largest client resolution (width) the server allows, e.g. 1024 / 1280 / 1366 / 1920.", min: 800, max: 7680);

            // ---- Optional -----------------------------------------------------------------------------------------
            const string O = GroupOptional;
            Add(O, "Optional", "SafeZoneBorder", "SafeZoneBorder", B, "Safe zone border", "Draw the border effect around safe zones (applies to maps as they are processed).");
            Add(O, "Optional", "SafeZoneHealing", "SafeZoneHealing", B, "Safe zone healing", "Players regenerate faster inside safe zones.");
            Add(O, "Optional", "GatherOrbsPerLevel", "GatherOrbsPerLevel", B, "Meditation orbs per level", "Meditation orbs are gathered per level instead of a fixed amount.");
            Add(O, "Optional", "ExpMobLevelDifference", "ExpMobLevelDifference", B, "Level-difference EXP penalty", "Reduce EXP from monsters far below the player's level.");
            Add(O, "Optional", "GameMasterEffect", "GameMasterEffect", B, "Show GM buff", "Show the Game Master buff icon/effect (buff list is built when the world starts).", SettingRestart.World);
            Add(O, "Optional", "LineMessageTimer", "LineMessageTimer", I32, "Line message interval (min)", "Minutes between the rotating line messages (Envir\\LineMessage.txt). Takes effect after the next message.", min: 1, max: 1440);

            // ---- Database -----------------------------------------------------------------------------------------
            const string D = GroupDatabase;
            Add(D, "Database", "SaveDelay", "SaveDelay", I32, "Save interval (min)", "Minutes between automatic saves of accounts, database, guilds and goods. Takes effect after the next save.", min: 1, max: 1440);
            Add(D, "Database", "CredxGold", "CredxGold", I16, "Credit to gold rate", "Gold price multiplier for game shop items bought with gold (GoldPrice = 1000 × rate).", min: 0);
            Add(D, "Backup", "RetentionEnabled", "BackupRetentionEnabled", B, "Prune old backups", "Delete old files from the Back Up accounts and database folders after each save (keeps all recent, then hourly, then daily copies). Off by default: it deletes files.");
            Add(D, "Backup", "KeepAllHours", "BackupKeepAllHours", I32, "Keep every backup for (hours)", "Every backup younger than this is kept.", min: 1, max: 8760);
            Add(D, "Backup", "KeepHourlyDays", "BackupKeepHourlyDays", I32, "Keep hourly backups for (days)", "After the keep-all window, the newest backup of each hour is kept for this many days.", min: 1, max: 3650);
            Add(D, "Backup", "KeepDailyDays", "BackupKeepDailyDays", I32, "Keep daily backups for (days)", "After the hourly window, the newest backup of each day is kept for this many days; older backups are deleted.", min: 1, max: 3650);
            Add(D, "LiveDb", "Enabled", "LiveDbEnabled", B, "LiveDb: live game-data edits", "Game Data edits are saved to Server.MirDB on every apply and, where safe (monsters, magic for now), applied to the running world at once. Off: the old flow (edit, then Save DB, changes after a world restart).");
            Add(D, "LiveDb", "BackupEveryMinutes", "LiveDbBackupEveryMinutes", I32, "LiveDb: timestamped backup every (min)", "A Game Data save keeps a timestamped copy of the previous Server.MirDB at most this often; saves in between replace one rolling 'Database livedb-previous.bak'. 0 = a timestamped copy on every save.", min: 0, max: 1440);
            Add(D, "LiveDb", "SweepPerTick", "LiveDbSweepPerTick", I32, "LiveDb: objects refreshed per tick", "After a live monster stat change, spawned monsters are refreshed in the background, scanning at most this many world objects per game tick.", min: 16, max: 100000);
            Add(D, "LiveDb", "MaxBatchRows", "LiveDbMaxBatchRows", I32, "LiveDb: max rows per batch", "Largest number of rows (edits, new rows and deletes) one Game Data apply may contain.", min: 1, max: 5000);
            Add(D, "LiveDb", "MaxPushRows", "LiveDbMaxPushRows", I32, "LiveDb: max rows pushed to clients", "Largest number of client-visible rows (item, monster, spell updates) one apply may push to connected clients. A larger batch is not pushed: its item changes are saved and take effect after a restart; monsters and spells still apply live.", min: 1, max: 100000);

            // ---- Rates & drops ------------------------------------------------------------------------------------
            const string R = GroupRates;
            Add(R, "Game", "DropRate", "DropRate", F32, "Drop rate", "Drop chance multiplier (2 = twice as likely). Must be above 0.", min: 0.01, max: 1000);
            Add(R, "Game", "ExpRate", "ExpRate", F32, "EXP rate", "Experience multiplier.", min: 0, max: 1000);
            Add(R, "Game", "ItemTimeOut", "ItemTimeOut", I32, "Dropped item lifetime (min)", "Minutes before an item on the ground disappears.", min: 1);
            Add(R, "Game", "PlayerDiedItemTimeOut", "PlayerDiedItemTimeOut", I32, "Death drop lifetime (min)", "Minutes before items dropped by a dying player disappear.", min: 1);
            Add(R, "DropGold", "DropGold", "DropGold", B, "Drop gold on the ground", "Monsters drop gold on the ground instead of giving it straight to the killer.");
            Add(R, "DropGold", "MaxDropGold", "MaxDropGold", U32, "Max gold per pile", "Largest gold pile; bigger amounts are split into several piles.", min: 1);

            // ---- Gameplay -----------------------------------------------------------------------------------------
            const string GP = GroupGameplay;
            Add(GP, "Game", "PetSave", "PetSave", B, "Save pets", "Pets are saved when the player logs out and return on login.");
            Add(GP, "Game", "MaxBossTames", "MaxBossTames", I32, "Max boss tames", "How many boss monsters a player may tame at once (0 = none).", min: 0, max: 100);
            Add(GP, "Game", "PKDelay", "PKDelay", I32, "PK point decay (s)", "Seconds between PK point reductions.", min: 1);
            Add(GP, "Game", "MonsterRecallEnabled", "MonsterRecallEnabled", B, "Monster recall", "Monsters pulled too far from their spawn are recalled.", savedBySave: false);
            Add(GP, "Game", "MonsterRecallRange", "MonsterRecallRange", I32, "Monster recall range (cells)", "Distance from home before a monster is recalled.", min: 1, max: 1000, savedBySave: false);
            Add(GP, "Game", "MonsterRecallCooldown", "MonsterRecallCooldown", I32, "Monster recall cooldown (ms)", "Minimum time between recalls of the same monster.", min: 0, savedBySave: false);
            Add(GP, "Game", "NewbieGuild", "NewbieGuild", S, "Newbie guild name", "Name of the guild new players join automatically.", format: "name");
            Add(GP, "Game", "NewbieGuildMaxSize", "NewbieGuildMaxSize", I32, "Newbie guild size", "Member cap of the newbie guild (applied when the guild is loaded or levels).", min: 1);
            Add(GP, "Game", "GroupInviteDelay", "GroupInviteDelay", I64, "Group invite delay (ms)", "Minimum time between group invites from one player.", min: 0, max: 3600000, isProperty: true);
            Add(GP, "Game", "TradeDelay", "TradeDelay", I64, "Trade request delay (ms)", "Minimum time between trade requests from one player.", min: 0, max: 3600000, isProperty: true);
            Add(GP, "Game", "TeleportToNPCCost", "TeleportToNPCCost", I32, "World map teleport cost", "Gold cost of teleporting to an NPC from the world map (sent to clients when they log in).", min: 0);
            Add(GP, "Observe", "AllowObserve", "AllowObserve", B, "Allow observing", "Players may observe other players.");
            Add(GP, "Bonus", "RangeAccuracyBonus", "RangeAccuracyBonus", U8, "Ranged accuracy bonus", "Flat bonus added to the hit chance of ranged attacks.");
            Add(GP, "PKTown", "PKTownMapName", "PKTownMapName", S, "PK town map", "Map file name where red-named players respawn.", format: "map");
            Add(GP, "PKTown", "PKTownPositionX", "PKTownPositionX", I32, "PK town X", "Respawn X coordinate on the PK town map.", min: 0, max: 10000);
            Add(GP, "PKTown", "PKTownPositionY", "PKTownPositionY", I32, "PK town Y", "Respawn Y coordinate on the PK town map.", min: 0, max: 10000);

            // ---- Rested -------------------------------------------------------------------------------------------
            const string RS = GroupRested;
            Add(RS, "Rested", "Period", "RestedPeriod", I32, "Rest period (min)", "Minutes of resting in a safe zone per rested-buff stack (0 = rested buff off).", min: 0);
            Add(RS, "Rested", "BuffLength", "RestedBuffLength", I32, "Buff length (min)", "Rested buff duration gained per stack.", min: 0);
            Add(RS, "Rested", "ExpBonus", "RestedExpBonus", I32, "EXP bonus (%)", "EXP rate bonus while rested.", min: 0, max: 1000);
            Add(RS, "Rested", "MaxBonus", "RestedMaxBonus", I32, "Max stacks", "Maximum number of rested stacks.", min: 0, max: 1000);

            // ---- Items --------------------------------------------------------------------------------------------
            const string IT = GroupItems;
            Add(IT, "Items", "HealRing", "HealRing", S, "Healing ring skill", "Skill granted by items with the Healing special mode.", format: "name");
            Add(IT, "Items", "FireRing", "FireRing", S, "Flame ring skill", "Skill granted by items with the Flame special mode.", format: "name");
            Add(IT, "Items", "BlinkSkill", "BlinkSkill", S, "Blink item skill", "Skill granted by items with the Blink special mode.", format: "name");
            Add(IT, "Items", "MagicResistWeight", "MagicResistWeight", U8, "Magic resist weight", "Random-stat weight for magic resist (also editable in Balance).");
            Add(IT, "Items", "PoisonResistWeight", "PoisonResistWeight", U8, "Poison resist weight", "Random-stat weight for poison resist.");
            Add(IT, "Items", "CriticalRateWeight", "CriticalRateWeight", U8, "Critical rate weight", "Random-stat weight for critical rate.");
            Add(IT, "Items", "CriticalDamageWeight", "CriticalDamageWeight", U8, "Critical damage weight", "Random-stat weight for critical damage (at least 1).", min: 1);
            Add(IT, "Items", "FreezingAttackWeight", "FreezingAttackWeight", U8, "Freezing weight", "Random-stat weight for freezing attack.");
            Add(IT, "Items", "PoisonAttackWeight", "PoisonAttackWeight", U8, "Poison attack weight", "Random-stat weight for poison attack.");
            Add(IT, "Items", "HealthRegenWeight", "HealthRegenWeight", U8, "Health regen weight", "Random-stat weight for health regeneration (at least 1).", min: 1);
            Add(IT, "Items", "ManaRegenWeight", "ManaRegenWeight", U8, "Mana regen weight", "Random-stat weight for mana regeneration (at least 1).", min: 1);
            Add(IT, "Items", "MaxLuck", "MaxLuck", U8, "Max luck", "Luck cap used by weapon luck/curse rolls.");
            Add(IT, "Items", "SealDelay", "ItemSealDelay", U16, "Item seal delay (min)", "Extra minutes added to an item seal.");
            Add(IT, "Items", "PvpCanResistMagic", "PvpCanResistMagic", B, "PvP: magic resist works", "Magic resist also applies against players.");
            Add(IT, "Items", "PvpCanResistPoison", "PvpCanResistPoison", B, "PvP: poison resist works", "Poison resist also applies against players.");
            Add(IT, "Items", "PvpCanFreeze", "PvpCanFreeze", B, "PvP: freezing works", "Freezing attack also applies against players.");
            Add(IT, "IntelligentCreatures", "CreatureBlackStoneName", "CreatureBlackStoneName", S, "Creature black stone item", "Item name used as the intelligent-creature black stone.", format: "name");

            // ---- Monster names ([Game]) ---------------------------------------------------------------------------
            const string M = GroupMonsters;
            void Mob(string key, string label, string field = null)
            {
                Add(M, "Game", key, field ?? key, S, label, "Monster (MonsterInfo name) used for: " + label + ".", format: "monster");
            }

            Mob("SkeletonName", "Summon Skeleton");
            Mob("BugBatName", "Bug bat (Summon pet)");
            Mob("ShinsuName", "Summon Shinsu");
            Mob("Zuma1", "Zuma statue 1");
            Mob("Zuma2", "Zuma guardian 2");
            Mob("Zuma3", "Zuma archer 3");
            Mob("Zuma4", "Zuma 4 (wedge moth)");
            Mob("Zuma5", "Zuma archer 5");
            Mob("Zuma6", "Zuma statue 6");
            Mob("Zuma7", "Zuma guardian 7");
            Mob("Turtle1", "Turtle 1");
            Mob("Turtle2", "Turtle 2");
            Mob("Turtle3", "Turtle 3");
            Mob("Turtle4", "Turtle 4 (tower)");
            Mob("Turtle5", "Turtle 5 (finial)");
            Mob("BoneMonster1", "Bone lord minion 1");
            Mob("BoneMonster2", "Bone lord minion 2");
            Mob("BoneMonster3", "Bone lord minion 3");
            Mob("BoneMonster4", "Bone lord minion 4");
            Mob("BehemothMonster1", "Behemoth minion 1");
            Mob("BehemothMonster2", "Behemoth minion 2");
            Mob("BehemothMonster3", "Behemoth minion 3");
            Mob("HellKnight1", "Hell lord knight 1");
            Mob("HellKnight2", "Hell lord knight 2");
            Mob("HellKnight3", "Hell lord knight 3");
            Mob("HellKnight4", "Hell lord knight 4");
            Mob("HellBomb1", "Hell lord bomb 1");
            Mob("HellBomb2", "Hell lord bomb 2");
            Mob("HellBomb3", "Hell lord bomb 3");
            Mob("GeneralMeowMeowMob1", "General MeowMeow minion 1");
            Mob("GeneralMeowMeowMob2", "General MeowMeow minion 2");
            Mob("GeneralMeowMeowMob3", "General MeowMeow minion 3");
            Mob("GeneralMeowMeowMob4", "General MeowMeow minion 4");
            Mob("KingHydraxMob", "King Hydrax minion");
            Mob("HornedCommanderMob", "Horned Commander minion");
            Mob("HornedCommanderBombMob", "Horned Commander bomb");
            Mob("SnowWolfKingMob", "Snow Wolf King minion");
            Mob("ScrollMob1", "Scroll monster 1 (warrior)");
            Mob("ScrollMob2", "Scroll monster 2 (taoist)");
            Mob("ScrollMob3", "Scroll monster 3 (wizard)");
            Mob("ScrollMob4", "Scroll monster 4 (assassin)");
            Mob("WhiteSnake", "White serpent");
            Mob("AngelName", "Summon Holy Deva");
            Mob("BombSpiderName", "Bomb spider");
            Mob("CloneName", "Mirroring clone");
            Mob("AssassinCloneName", "Assassin clone");
            Mob("VampireName", "Vampire spider");
            Mob("ToadName", "Spitting toad");
            Mob("SnakeTotemName", "Snake totem");
            Mob("SnakesName", "Charmed snakes");
            Mob("StoneName", "Stone trap");
            Mob("AncientBatName", "Ancient bat");
            Mob("TucsonGeneralEgg", "Tucson general egg");
            Mob("HeroName", "Hero");

            // ---- Archive ------------------------------------------------------------------------------------------
            const string A = GroupArchive;
            Add(A, "Archive", "InactiveCharacterMonths", "ArchiveInactiveCharacterAfterMonths", I32, "Archive inactive characters after (months)",
                "Characters that never logged in are archived after this many months (at least 1).", min: 1, max: 1200);
            Add(A, "Archive", "DeletedCharacterMonths", "ArchiveDeletedCharacterAfterMonths", I32, "Archive deleted characters after (months)",
                "Deleted characters are archived after this many months (at least 1).", min: 1, max: 1200);

            return list;
        }
    }

    /// <summary>Pure conversion / validation / formatting of Setup.ini values (no Settings access, unit-tested).</summary>
    public static class SettingsValueCodec
    {
        /// <summary>What a secret's value is shown as.</summary>
        public const string Mask = "••••••";

        public const int MaxStringLength = 512;

        public static string KindOf(Type type)
        {
            if (type == null) return "string";
            if (type == typeof(bool)) return "bool";
            if (type.IsEnum) return "enum";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "float";
            if (IsIntegral(type)) return "int";
            if (type == typeof(TimeSpan)) return "timespan";
            if (type == typeof(System.Drawing.Point)) return "point";
            return "string";
        }

        public static bool IsIntegral(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
        }

        /// <summary>The CLR range of an integral type as decimals (null for other types).</summary>
        public static (decimal Min, decimal Max)? ClrRange(Type type)
        {
            if (type == typeof(byte)) return (byte.MinValue, byte.MaxValue);
            if (type == typeof(sbyte)) return (sbyte.MinValue, sbyte.MaxValue);
            if (type == typeof(short)) return (short.MinValue, short.MaxValue);
            if (type == typeof(ushort)) return (ushort.MinValue, ushort.MaxValue);
            if (type == typeof(int)) return (int.MinValue, int.MaxValue);
            if (type == typeof(uint)) return (uint.MinValue, uint.MaxValue);
            if (type == typeof(long)) return (long.MinValue, long.MaxValue);
            if (type == typeof(ulong)) return (ulong.MinValue, ulong.MaxValue);
            return null;
        }

        /// <summary>Effective bounds for the UI (descriptor bounds narrowed to the CLR range); null for non-numbers.</summary>
        public static (double Min, double Max)? EffectiveRange(SettingDescriptor d)
        {
            if (d.Type == typeof(float) || d.Type == typeof(double))
            {
                var limit = d.Type == typeof(float) ? float.MaxValue : double.MaxValue;
                return (d.Min ?? -limit, d.Max ?? limit);
            }

            var clr = ClrRange(d.Type);
            if (clr == null) return null;

            var min = (double)clr.Value.Min;
            var max = (double)clr.Value.Max;
            if (d.Min.HasValue) min = Math.Max(min, d.Min.Value);
            if (d.Max.HasValue) max = Math.Min(max, d.Max.Value);
            return (min, max);
        }

        /// <summary>A JSON value (bool / number / string) as invariant text; null for null / undefined / objects / arrays.</summary>
        public static string RawText(JsonElement raw)
        {
            switch (raw.ValueKind)
            {
                case JsonValueKind.String:
                    return raw.GetString();
                case JsonValueKind.Number:
                    return raw.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return null;
            }
        }

        public static bool TryConvert(SettingDescriptor d, JsonElement raw, out object value, out string error)
        {
            var text = RawText(raw);
            if (text == null)
            {
                value = null;
                error = raw.ValueKind == JsonValueKind.Object || raw.ValueKind == JsonValueKind.Array
                    ? "Value must be a single " + KindOf(d.Type) + " value"
                    : "Value is required";
                return false;
            }

            return TryConvert(d, text, out value, out error);
        }

        /// <summary>
        /// Converts invariant text to the member's CLR type and validates it (CLR range, descriptor bounds, string rules, format).
        /// Returns false with a human-readable error.
        /// </summary>
        public static bool TryConvert(SettingDescriptor d, string text, out object value, out string error)
        {
            value = null;
            error = null;

            if (d == null)
            {
                error = "Unknown setting";
                return false;
            }

            if (text == null)
            {
                error = "Value is required";
                return false;
            }

            var type = d.Type;

            if (type == typeof(bool))
            {
                if (!TryParseBool(text, out var b))
                {
                    error = "Must be true or false";
                    return false;
                }

                value = b;
                return true;
            }

            if (type.IsEnum)
            {
                var t = text.Trim();
                object parsed = null;
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    var candidate = Enum.ToObject(type, n);
                    if (Enum.IsDefined(type, candidate)) parsed = candidate;
                }
                else if (Enum.TryParse(type, t, true, out var named) && Enum.IsDefined(type, named))
                {
                    parsed = named;
                }

                if (parsed == null)
                {
                    error = "Must be one of: " + string.Join(", ", Enum.GetNames(type));
                    return false;
                }

                value = parsed;
                return true;
            }

            if (IsIntegral(type))
            {
                var t = text.Trim();
                if (!decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
                {
                    error = "Must be a whole number";
                    return false;
                }

                if (dec != decimal.Truncate(dec))
                {
                    error = "Must be a whole number";
                    return false;
                }

                if (!CheckRange(d, dec, out error)) return false;

                value = Convert.ChangeType(dec, type, CultureInfo.InvariantCulture);
                return true;
            }

            if (type == typeof(float) || type == typeof(double))
            {
                var t = text.Trim();
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) || double.IsNaN(dbl) || double.IsInfinity(dbl))
                {
                    error = "Must be a number";
                    return false;
                }

                if (Math.Abs(dbl) > float.MaxValue && type == typeof(float))
                {
                    error = "Number is out of range";
                    return false;
                }

                var lo = d.Min ?? double.MinValue;
                var hi = d.Max ?? double.MaxValue;
                if (dbl < lo || dbl > hi)
                {
                    error = "Must be between " + FormatBound(d.Min ?? (type == typeof(float) ? float.MinValue : double.MinValue))
                        + " and " + FormatBound(d.Max ?? (type == typeof(float) ? float.MaxValue : double.MaxValue));
                    return false;
                }

                value = type == typeof(float) ? (object)(float)dbl : dbl;
                return true;
            }

            if (type == typeof(TimeSpan))
            {
                if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var ts) || ts < TimeSpan.Zero)
                {
                    error = "Must be a duration like 00:30:00 (hh:mm:ss)";
                    return false;
                }

                value = ts;
                return true;
            }

            if (type == typeof(System.Drawing.Point))
            {
                var parts = text.Split(',');
                if (parts.Length != 2
                    || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                {
                    error = "Must be a point like 100,200";
                    return false;
                }

                if (d.Min.HasValue && (x < d.Min.Value || y < d.Min.Value) || d.Max.HasValue && (x > d.Max.Value || y > d.Max.Value))
                {
                    error = "Coordinates must be between " + FormatBound(d.Min ?? int.MinValue) + " and " + FormatBound(d.Max ?? int.MaxValue);
                    return false;
                }

                value = new System.Drawing.Point(x, y);
                return true;
            }

            if (type == typeof(string))
            {
                if (!TryValidateString(d, text, out var s, out error)) return false;
                value = s;
                return true;
            }

            error = "This setting type cannot be edited here";
            return false;
        }

        private static bool CheckRange(SettingDescriptor d, decimal v, out string error)
        {
            error = null;
            var clr = ClrRange(d.Type);
            var min = clr?.Min ?? decimal.MinValue;
            var max = clr?.Max ?? decimal.MaxValue;

            if (d.Min.HasValue) min = Math.Max(min, (decimal)d.Min.Value);
            if (d.Max.HasValue) max = Math.Min(max, (decimal)d.Max.Value);

            if (v < min || v > max)
            {
                error = "Must be between " + FormatBound((double)min) + " and " + FormatBound((double)max);
                return false;
            }

            return true;
        }

        private static string FormatBound(double v)
        {
            return v.ToString(v == Math.Floor(v) ? "#,0" : "0.###", CultureInfo.InvariantCulture);
        }

        public static bool TryParseBool(string text, out bool value)
        {
            value = false;
            if (text == null) return false;

            var t = text.Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0" || t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// String rules imposed by Crystal's InIReader: no line breaks (one line per key), no '=' (FindValue splits on '=' and keeps
        /// only the second part), not empty (ReadString falls back to the default for an empty value). Secrets keep their
        /// whitespace; other strings are trimmed. Then the descriptor's format rule.
        /// </summary>
        public static bool TryValidateString(SettingDescriptor d, string text, out string value, out string error)
        {
            value = null;
            error = null;

            var s = d.Secret ? text : text.Trim();

            if (s.Length == 0 || (d.Secret && s.Trim().Length == 0))
            {
                error = "Cannot be empty (an empty value reverts to the default when the server loads Setup.ini)";
                return false;
            }

            if (s.Length > MaxStringLength)
            {
                error = "Too long (max " + MaxStringLength + " characters)";
                return false;
            }

            foreach (var c in s)
            {
                if (c == '\r' || c == '\n' || c == '\0' || char.IsControl(c))
                {
                    error = "Cannot contain line breaks or control characters";
                    return false;
                }
            }

            if (s.IndexOf('=') >= 0)
            {
                error = "Cannot contain '=' (Setup.ini would cut the value at it)";
                return false;
            }

            if (!CheckFormat(d.Format, s, out error)) return false;

            // Settings.LoadVersion() splits on ',' without trimming: store "a,b", not "a, b".
            if (d.Format == "path-list")
                s = string.Join(",", s.Split(',').Select(p => p.Trim()));

            value = s;
            return true;
        }

        /// <summary>Format rules for string settings. Unknown / null formats accept anything that passed the generic rules.</summary>
        public static bool CheckFormat(string format, string s, out string error)
        {
            error = null;
            switch (format)
            {
                case "ip":
                    if (!System.Net.IPAddress.TryParse(s, out _) || s.IndexOf(':') < 0 && s.Split('.').Length != 4)
                    {
                        error = "Must be an IP address such as 127.0.0.1 or 0.0.0.0";
                        return false;
                    }

                    return true;

                case "bind":
                    if (s == "*" || s == "+" || System.Net.IPAddress.TryParse(s, out _) && (s.IndexOf(':') >= 0 || s.Split('.').Length == 4))
                        return true;
                    if (IsHostName(s)) return true;
                    error = "Must be an IP address, a host name, * or +";
                    return false;

                case "url-prefix":
                    if (!IsHttpPrefix(s))
                    {
                        error = "Must be an http:// or https:// prefix ending with /, e.g. http://127.0.0.1:5679/";
                        return false;
                    }

                    return true;

                case "path":
                    if (!IsValidPath(s))
                    {
                        error = "Not a valid path";
                        return false;
                    }

                    return true;

                case "path-list":
                    foreach (var part in s.Split(','))
                    {
                        if (part.Trim().Length == 0 || !IsValidPath(part.Trim()))
                        {
                            error = "Must be one or more valid paths separated by commas";
                            return false;
                        }
                    }

                    return true;

                case "language":
                    if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.Contains(".."))
                    {
                        error = "Must be a localization file name without extension";
                        return false;
                    }

                    return true;

                default:
                    return true;
            }
        }

        private static bool IsHostName(string s)
        {
            if (s.Length == 0 || s.Length > 253) return false;
            foreach (var label in s.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63) return false;
                if (label[0] == '-' || label[label.Length - 1] == '-') return false;
                foreach (var c in label)
                {
                    if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                }
            }

            return true;
        }

        public static bool IsHttpPrefix(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.EndsWith("/")) return false;

            string rest;
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) rest = s.Substring(7);
            else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rest = s.Substring(8);
            else return false;

            if (rest.IndexOf(' ') >= 0) return false;

            var slash = rest.IndexOf('/');
            var authority = rest.Substring(0, slash);
            if (authority.Length == 0) return false;

            string host = authority;
            if (authority.StartsWith("["))
            {
                var close = authority.IndexOf(']');
                if (close < 0) return false;
                host = authority.Substring(0, close + 1);
                var tail = authority.Substring(close + 1);
                if (tail.Length > 0 && !IsPortSuffix(tail)) return false;
                return true;
            }

            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                if (!IsPortSuffix(authority.Substring(colon))) return false;
                host = authority.Substring(0, colon);
            }

            return host == "*" || host == "+" || IsHostName(host) || System.Net.IPAddress.TryParse(host, out _);
        }

        private static bool IsPortSuffix(string tail)
        {
            return tail.Length > 1 && tail[0] == ':' && int.TryParse(tail.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port >= 1 && port <= 65535;
        }

        private static bool IsValidPath(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('<') >= 0 || s.IndexOf('>') >= 0 || s.IndexOf('|') >= 0) return false;

            // A ':' is only allowed as a drive separator (C:\...).
            var colon = s.IndexOf(':');
            if (colon >= 0 && (colon != 1 || !char.IsAsciiLetter(s[0]) || s.IndexOf(':', colon + 1) >= 0)) return false;

            return true;
        }

        /// <summary>The value as a JSON-friendly object: bool, long/ulong, double (floats without binary noise), string.</summary>
        public static object ToJsonValue(SettingDescriptor d, object value)
        {
            if (value == null) return null;

            switch (value)
            {
                case bool b:
                    return b;
                case float f:
                    return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                case double dbl:
                    return dbl;
                case decimal m:
                    return (double)m;
                case ulong ul:
                    return ul;
                case string s:
                    return s;
                case TimeSpan ts:
                    return ts.ToString("c", CultureInfo.InvariantCulture);
                case System.Drawing.Point p:
                    return p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture);
            }

            if (value.GetType().IsEnum) return value.ToString();
            if (IsIntegral(value.GetType())) return Convert.ToInt64(value, CultureInfo.InvariantCulture);

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Invariant text for comparisons and the audit trail; secrets are masked.</summary>
        public static string ToDisplay(SettingDescriptor d, object value)
        {
            if (d != null && d.Secret) return value == null || value as string == string.Empty ? "(not set)" : Mask;
            var j = ToJsonValue(d, value);
            switch (j)
            {
                case null:
                    return "";
                case bool b:
                    return b ? "true" : "false";
                case double dbl:
                    return dbl.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(j, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Unmasked invariant text (used to compare a client's "original" value with the current one).</summary>
        public static string ToComparable(SettingDescriptor d, object value)
        {
            var j = ToJsonValue(d, value);
            switch (j)
            {
                case null:
                    return "";
                case bool b:
                    return b ? "true" : "false";
                case double dbl:
                    return dbl.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(j, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// True when the client's "original" (the value it loaded) still matches the current value, so the edit is not based on a
        /// stale page. Numbers compare numerically, bools by meaning, strings ordinally.
        /// </summary>
        public static bool OriginalMatches(SettingDescriptor d, string original, object current)
        {
            if (original == null) return true;
            if (d.Secret) return true;

            if (!TryConvertLoose(d, original, out var orig)) return false;
            return ValuesEqual(d, orig, current);
        }

        private static bool TryConvertLoose(SettingDescriptor d, string text, out object value)
        {
            value = null;
            var type = d.Type;
            if (type == typeof(string))
            {
                value = text;
                return true;
            }

            if (type == typeof(bool))
            {
                if (!TryParseBool(text, out var b)) return false;
                value = b;
                return true;
            }

            if (IsIntegral(type) || type == typeof(float) || type == typeof(double))
            {
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)) return false;
                value = dbl;
                return true;
            }

            // enums / TimeSpan / Point: compare the canonical text
            value = text.Trim();
            return true;
        }

        public static bool ValuesEqual(SettingDescriptor d, object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;

            if (d.Type == typeof(string)) return string.Equals(a as string ?? a.ToString(), b as string ?? b.ToString(), StringComparison.Ordinal);
            if (d.Type == typeof(bool)) return Convert.ToBoolean(a, CultureInfo.InvariantCulture) == Convert.ToBoolean(b, CultureInfo.InvariantCulture);

            if (IsIntegral(d.Type) || d.Type == typeof(float) || d.Type == typeof(double))
            {
                var x = Convert.ToDouble(ToJsonValue(d, a), CultureInfo.InvariantCulture);
                var y = Convert.ToDouble(ToJsonValue(d, b), CultureInfo.InvariantCulture);
                if (d.Type == typeof(float)) return Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
                return x == y;
            }

            return string.Equals(ToComparable(d, a), ToComparable(d, b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Cross-key checks over the final values (current values merged with the accepted changes). Returns (field, error) pairs
        /// for the CHANGED fields that break a rule: the game port, the status port (3100) and the web admin port must differ.
        /// </summary>
        public static List<(string Field, string Error)> CrossValidate(IReadOnlyDictionary<string, object> finalValues, ICollection<string> changedFields)
        {
            var errors = new List<(string, string)>();
            if (finalValues == null || changedFields == null || changedFields.Count == 0) return errors;

            int? Port(string field)
            {
                if (!finalValues.TryGetValue(field, out var v) || v == null) return null;
                try
                {
                    return Convert.ToInt32(v, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    return null;
                }
            }

            const int statusPort = 3100;
            var game = Port("Port");
            var web = Port("WebAdminPort");

            bool Changed(string f) => changedFields.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase));

            if (game.HasValue && Changed("Port"))
            {
                if (game.Value == statusPort) errors.Add(("Port", "Port 3100 is used by the server's status listener"));
                else if (web.HasValue && web.Value == game.Value) errors.Add(("Port", "The game port cannot equal the web admin port"));
            }

            if (web.HasValue && Changed("WebAdminPort"))
            {
                if (web.Value == statusPort) errors.Add(("WebAdminPort", "Port 3100 is used by the server's status listener"));
                else if (game.HasValue && web.Value == game.Value) errors.Add(("WebAdminPort", "The web admin port cannot equal the game port"));
            }

            return errors;
        }
    }
}
