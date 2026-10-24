using Server.MirDatabase;

namespace Server.MirEnvir
{
    /// <summary>
    /// One exported character (UserData-and-Quests plan, Part A "Website export"). The whitelist by construction: name, class,
    /// gender, level, guild name and the public stats of <see cref="WebsiteExport.ExportedStats"/>. Nothing from AccountInfo.
    /// Rows are reused between exports; the game thread fills them only while the export thread is idle.
    /// </summary>
    internal sealed class WebsiteCharacterRow
    {
        public string Name;
        public MirClass Class;
        public MirGender Gender;
        public ushort Level;
        public string Guild; // null when guildless
        public readonly long[] Stats = new long[WebsiteExport.ExportedStats.Length];
    }

    internal sealed class WebsiteGuildRow
    {
        public string Name;
        public byte Level;
        public int Members;
    }

    /// <summary>The game thread's copy for one export. Owned by the game thread while <see cref="WebsiteExport"/> is idle, by
    /// the export thread while it writes; the lists and rows are reused (no steady-state allocation on the game thread).</summary>
    internal sealed class WebsiteExportSnapshot
    {
        public readonly List<WebsiteCharacterRow> Rows = new List<WebsiteCharacterRow>();
        public int RowCount;
        public readonly List<WebsiteGuildRow> Guilds = new List<WebsiteGuildRow>();
        public int GuildCount;
        public readonly Dictionary<int, string> GuildNames = new Dictionary<int, string>();
        public string Path;
        public DateTime Taken;
    }

    /// <summary>
    /// The website export: every <c>[Website] ExportMinutes</c> (0 or an empty <c>ExportPath</c> = off) the game thread copies
    /// the eligible characters into a <see cref="WebsiteExportSnapshot"/> (<see cref="Tick"/>) and hands it to an export thread
    /// of its own (never the UserData writer, so an export cannot delay a save), which writes the JSON files
    /// (<see cref="WebsiteExportWriter"/>). The game thread never waits: while the previous export still writes, a tick is skipped.
    /// </summary>
    internal static class WebsiteExport
    {
        /// <summary>The public stats and their JSON names. A whitelist: a new <see cref="PlayerStat"/> is exported only once it
        /// is added here.</summary>
        public static readonly PlayerStat[] ExportedStats =
        {
            PlayerStat.MonstersKilled, PlayerStat.Deaths, PlayerStat.GoldEarned, PlayerStat.GoldSpent, PlayerStat.ItemsCrafted,
            PlayerStat.QuestsCompleted, PlayerStat.HighestHit, PlayerStat.PlayersKilled, PlayerStat.DeathsToPlayers,
            PlayerStat.BestKillStreak, PlayerStat.Murders, PlayerStat.GuildWarKills, PlayerStat.PvpDamageDealt,
            PlayerStat.PvpDamageTaken, PlayerStat.HighestPkPoints,
        };

        public static readonly string[] ExportedStatNames =
        {
            "monstersKilled", "deaths", "goldEarned", "goldSpent", "itemsCrafted",
            "questsCompleted", "highestHit", "playersKilled", "deathsToPlayers",
            "bestKillStreak", "murders", "guildWarKills", "pvpDamageDealt",
            "pvpDamageTaken", "highestPkPoints",
        };

        private static readonly WebsiteExportSnapshot Snapshot = new WebsiteExportSnapshot();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        private static Thread _thread;
        private static int _busy;
        private static string _lastPathError;

        /// <summary>True while the export thread writes (tests, diagnostics).</summary>
        public static bool Busy => Volatile.Read(ref _busy) != 0;

        /// <summary>Exports completed since process start (diagnostics, tests).</summary>
        public static long Completed => Interlocked.Read(ref _completed);
        private static long _completed;

        /// <summary>Game thread, from Envir.Process every ExportMinutes.</summary>
        public static void Tick(Envir envir)
        {
            if (Settings.WebsiteExportMinutes <= 0 || string.IsNullOrWhiteSpace(Settings.WebsiteExportPath)) return;
            if (Volatile.Read(ref _busy) != 0) return; // the previous export is still writing: skip this one

            string path;
            try
            {
                path = System.IO.Path.GetFullPath(Settings.WebsiteExportPath); // string work only, no IO
            }
            catch (Exception ex)
            {
                if (_lastPathError != Settings.WebsiteExportPath)
                {
                    _lastPathError = Settings.WebsiteExportPath;
                    MessageQueue.Instance.Enqueue("[Website] ExportPath is not a valid path: " + ex.Message);
                }
                return;
            }

            Collect(Snapshot, envir.AccountList, envir.GuildList);
            Snapshot.Path = path;
            Snapshot.Taken = envir.Now;

            if (_thread == null)
            {
                _thread = new Thread(ExportLoop) { IsBackground = true, Name = "Website export", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }

            Volatile.Write(ref _busy, 1);
            Signal.Set();
        }

        /// <summary>
        /// Game thread: copies the eligible characters and the guilds into <paramref name="snapshot"/>. Never exported: admin
        /// and banned accounts, deleted characters, characters with HideFromRankings, anything from AccountInfo.
        /// </summary>
        public static void Collect(WebsiteExportSnapshot snapshot, IReadOnlyList<AccountInfo> accounts, IReadOnlyList<GuildInfo> guilds)
        {
            snapshot.GuildNames.Clear();
            snapshot.GuildCount = 0;
            for (var i = 0; i < guilds.Count; i++)
            {
                var guild = guilds[i];
                if (guild == null || string.IsNullOrEmpty(guild.Name)) continue;

                snapshot.GuildNames[guild.GuildIndex] = guild.Name;

                if (snapshot.GuildCount == snapshot.Guilds.Count) snapshot.Guilds.Add(new WebsiteGuildRow());
                var row = snapshot.Guilds[snapshot.GuildCount++];
                row.Name = guild.Name;
                row.Level = guild.Level;
                row.Members = guild.Membercount;
            }

            snapshot.RowCount = 0;
            var stats = ExportedStats;
            for (var a = 0; a < accounts.Count; a++)
            {
                var account = accounts[a];
                if (account == null || account.AdminAccount || account.Banned) continue;

                var characters = account.Characters;
                for (var c = 0; c < characters.Count; c++)
                {
                    var info = characters[c];
                    if (info == null || info.Deleted || info.HideFromRankings || string.IsNullOrEmpty(info.Name)) continue;

                    if (snapshot.RowCount == snapshot.Rows.Count) snapshot.Rows.Add(new WebsiteCharacterRow());
                    var row = snapshot.Rows[snapshot.RowCount++];
                    row.Name = info.Name;
                    row.Class = info.Class;
                    row.Gender = info.Gender;
                    row.Level = info.Level;
                    row.Guild = info.GuildIndex >= 0 && snapshot.GuildNames.TryGetValue(info.GuildIndex, out var guildName) ? guildName : null;

                    var source = info.Statistics;
                    for (var s = 0; s < stats.Length; s++)
                        row.Stats[s] = source == null ? 0 : source.Get(stats[s]);
                }
            }
        }

        private static void ExportLoop()
        {
            var writer = new WebsiteExportWriter();
            while (true)
            {
                Signal.WaitOne();
                try
                {
                    writer.Write(Snapshot);
                    Interlocked.Increment(ref _completed);
                }
                catch (Exception ex)
                {
                    MessageQueue.Instance.Enqueue("[Website] Export failed: " + ex.Message);
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
            }
        }
    }
}
