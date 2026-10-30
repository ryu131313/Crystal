using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// The Configs\*.ini design settings in SystemData.db (roadmap P7 phase 7.4): RandomItemStats, Mines, the exp and base-stat tables,
    /// the *System.ini files, Guild/Hero settings, MonsterRarity, WorldMap, and Setup.ini's [Game] and [Items] sections.
    /// <para>Rows are the ini's lines in order: a <c>key=value</c> line has its key and value (split at the first '='), any other line (a
    /// [section] header, a comment, a blank) has a NULL key and its text as the value, and every row carries its section. So a file comes
    /// back exactly as it was written, and the settings are queryable by section and key. Settings reads and writes through
    /// <see cref="Open"/> / <see cref="Exists"/> / <see cref="Delete"/>, which use the files while the store isn't active.</para>
    /// </summary>
    internal static class DesignIni
    {
        /// <summary>The Setup.ini sections that are design data; the rest of Setup.ini (network, paths, web admin, ...) stays in the file.</summary>
        public static readonly string[] SetupDesignSections = { "Game", "Items" };

        public static IEnumerable<(string Table, string Ddl)> Tables()
        {
            yield return ("ini_files", "CREATE TABLE ini_files (file TEXT PRIMARY KEY, present INTEGER NOT NULL, imported_utc TEXT NOT NULL) STRICT");
            yield return ("ini_lines", "CREATE TABLE ini_lines (file TEXT NOT NULL, position INTEGER NOT NULL, section TEXT, key TEXT, value TEXT NOT NULL, " +
                                       "PRIMARY KEY (file, position)) STRICT");
        }

        public static bool Exists(string path)
        {
            if (!DesignStore.Active) return File.Exists(path);
            var key = DesignStore.Key(path);
            return DesignStore.Guard(() => DesignStore.Run(conn => EnsureImported(conn, key, () => FileLines(path), path).Present), () => File.Exists(path));
        }

        /// <summary>The ini at <paramref name="path"/>: from the store when it is active (imported from the file on first use), else the file.</summary>
        public static InIReader Open(string path)
        {
            if (!DesignStore.Active) return new InIReader(path);
            var key = DesignStore.Key(path);
            var lines = DesignStore.Guard(() => DesignStore.Run(conn => EnsureImported(conn, key, () => FileLines(path), path).Lines), () => null);
            return lines == null ? new InIReader(path) : new StoreIniReader(path, key, lines);
        }

        public static void Delete(string path)
        {
            if (!DesignStore.Active)
            {
                File.Delete(path);
                return;
            }

            var key = DesignStore.Key(path);
            if (DesignStore.SavesBlockedUntilRestart) return;
            DesignStore.Post(conn => Write(conn, key, null), "design ini delete (" + key + ")"); // ordered before the Save that follows
        }

        // ------------------------------------------------------------------ store access (writer thread)

        internal sealed class Stored
        {
            public bool Present;
            public List<string> Lines = new List<string>();
        }

        /// <summary>The stored lines of <paramref name="key"/>, importing them from <paramref name="fromFile"/> first if the store has never seen it.</summary>
        /// <param name="diskPath">The file the key came from: a file that appeared after it was recorded as missing is imported, and one
        /// changed on disk after the import is reported (the store stays the record).</param>
        internal static Stored EnsureImported(SqliteConnection conn, string key, Func<List<string>> fromFile, string diskPath = null)
        {
            var stored = Read(conn, key);
            if (stored != null && diskPath != null && File.Exists(diskPath) && File.GetLastWriteTimeUtc(diskPath) > ImportedUtc(conn, key))
            {
                if (!stored.Present) stored = null; // recorded as missing, now there: import it
                else if (Reported.Add(key))
                    MessageQueue.Instance.Enqueue("[Store] " + key + " was changed on disk after it was imported; the server uses the stored copy. " +
                                                  "Make the change in the web admin, or re-import (Knowledgebase/Runbook.md §10).");
            }

            if (stored != null) return stored;

            var lines = fromFile();
            Write(conn, key, lines);
            if (lines != null) MessageQueue.Instance.Enqueue("[Store] Imported " + key + " (" + lines.Count.ToString(CultureInfo.InvariantCulture) + " lines)");
            return new Stored { Present = lines != null, Lines = lines ?? new List<string>() };
        }

        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static DateTime ImportedUtc(SqliteConnection conn, string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT imported_utc FROM ini_files WHERE file = $f";
            cmd.Parameters.AddWithValue("$f", key);
            return cmd.ExecuteScalar() is string s ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTime.MaxValue;
        }

        internal static Stored Read(SqliteConnection conn, string key)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT present FROM ini_files WHERE file = $f";
                cmd.Parameters.AddWithValue("$f", key);
                var present = cmd.ExecuteScalar();
                if (present == null || present is DBNull) return null;
                var stored = new Stored { Present = (long)present != 0 };
                if (!stored.Present) return stored;
                stored.Lines = ReadLines(conn, key);
                return stored;
            }
        }

        internal static List<string> ReadLines(SqliteConnection conn, string key)
        {
            var lines = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM ini_lines WHERE file = $f ORDER BY position";
            cmd.Parameters.AddWithValue("$f", key);
            using var r = cmd.ExecuteReader();
            while (r.Read()) lines.Add(r.IsDBNull(0) ? r.GetString(1) : r.GetString(0) + "=" + r.GetString(1));
            return lines;
        }

        /// <summary>Replaces <paramref name="key"/>'s lines in one transaction (null = the file doesn't exist).</summary>
        internal static void Write(SqliteConnection conn, string key, IList<string> lines)
        {
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM ini_lines WHERE file = $f";
                del.Parameters.AddWithValue("$f", key);
                del.ExecuteNonQuery();
            }

            using (var head = conn.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = "INSERT INTO ini_files (file, present, imported_utc) VALUES ($f, $p, $t) " +
                                   "ON CONFLICT(file) DO UPDATE SET present = excluded.present, imported_utc = excluded.imported_utc";
                head.Parameters.AddWithValue("$f", key);
                head.Parameters.AddWithValue("$p", lines == null ? 0L : 1L);
                head.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                head.ExecuteNonQuery();
            }

            if (lines != null)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO ini_lines (file, position, section, key, value) VALUES ($f, $p, $s, $k, $v)";
                var pf = ins.Parameters.Add("$f", SqliteType.Text);
                var pp = ins.Parameters.Add("$p", SqliteType.Integer);
                var ps = ins.Parameters.Add("$s", SqliteType.Text);
                var pk = ins.Parameters.Add("$k", SqliteType.Text);
                var pv = ins.Parameters.Add("$v", SqliteType.Text);
                pf.Value = key;
                string section = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i] ?? string.Empty;
                    string k = null, v = line;
                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) section = line.Substring(1, line.Length - 2);
                    else
                    {
                        var eq = line.IndexOf('=');
                        if (eq >= 0) { k = line.Substring(0, eq); v = line.Substring(eq + 1); }
                    }

                    pp.Value = (long)i;
                    ps.Value = (object)section ?? DBNull.Value;
                    pk.Value = (object)k ?? DBNull.Value;
                    pv.Value = v;
                    ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        private static List<string> FileLines(string path) => File.Exists(path) ? File.ReadAllLines(path).ToList() : null;

        // ------------------------------------------------------------------ Setup.ini: [Game] / [Items] in the store, the rest in the file

        /// <summary>Splits ini lines into the lines of <paramref name="sections"/> and the rest (a section runs to the next header).</summary>
        internal static (List<string> Inside, List<string> Outside) Split(IEnumerable<string> lines, ICollection<string> sections)
        {
            var inside = new List<string>();
            var outside = new List<string>();
            var current = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    current = sections.Contains(line.Substring(1, line.Length - 2));
                (current ? inside : outside).Add(line);
            }

            return (inside, outside);
        }
    }

    /// <summary>
    /// An ini held in SystemData.db (<see cref="DesignIni"/>): Save writes its lines back to the store, not to a file. InIReader saves after
    /// every key it writes, so saves are posted (never waited for, so a caller holding a lock the game thread wants never stalls behind the
    /// writer) and coalesced: only the latest snapshot is written. Reads of the store queue behind them on the same writer.
    /// </summary>
    internal sealed class StoreIniReader : InIReader
    {
        private readonly string _key;
        private long _version;

        public StoreIniReader(string path, string key, IEnumerable<string> lines) : base(path, lines)
        {
            _key = key;
        }

        public override void Save()
        {
            if (DesignStore.SavesBlockedUntilRestart) return;
            var version = Interlocked.Increment(ref _version);
            var lines = Lines.ToList();
            DesignStore.Post(conn =>
            {
                if (Interlocked.Read(ref _version) != version) return; // a newer snapshot follows
                DesignIni.Write(conn, _key, lines);
            }, "design ini save (" + _key + ")");
        }
    }

    /// <summary>
    /// Setup.ini (Settings.Reader): the file, with its design sections (<see cref="DesignIni.SetupDesignSections"/>) taken from and saved
    /// to SystemData.db once the store is active. <see cref="LoadDesignSections"/> runs at the start of Settings.Load.
    /// </summary>
    internal sealed class SetupIniReader : InIReader
    {
        private const string StoreKeySuffix = "#design";
        private const string BackupSuffix = ".bak";
        private const string TempSuffix = ".tmp";

        // False after a recovery from the backup: the first save must not rotate the damaged file over the only good copy.
        private bool _rotateBackup = true;
        private readonly bool _fileWritesBlocked;
        private bool _batching;

        /// <summary>
        /// Settings fall back to their defaults for any key that is missing and Settings.Save writes them all back, so a file that reads as
        /// empty would silently become a file of defaults (2026-10-28: a Windows restart killed the exit save mid-write). A file with no
        /// section is therefore never taken at face value: it is read again, then restored from the backup, and with neither the file is
        /// left alone until it is fixed by hand.
        /// </summary>
        public SetupIniReader(string path) : base(path)
        {
            if (!File.Exists(path) || HasSection(Lines)) return;

            // A sync tool or antivirus holding the file open reads the same as an empty file.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(200);
                var again = TryReadLines(path);
                if (!HasSection(again)) continue;
                Lines.Clear();
                Lines.AddRange(again);
                return;
            }

            var backup = TryReadLines(path + BackupSuffix);
            if (HasSection(backup))
            {
                Lines.Clear();
                Lines.AddRange(backup);
                _rotateBackup = false;
                MessageQueue.Instance.Enqueue("[Settings] " + path + " is empty or damaged: restored from " + path + BackupSuffix + ".");
                return;
            }

            _fileWritesBlocked = true;
            MessageQueue.Instance.Enqueue("[Settings] " + path + " is empty or damaged and has no usable backup: running on default settings and " +
                                          "not overwriting it. Restore the file, then restart.");
        }

        private static bool HasSection(IEnumerable<string> lines) => lines.Any(x => x.StartsWith('[') && x.TrimEnd().EndsWith(']'));

        private static List<string> TryReadLines(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            }
            catch (IOException ex)
            {
                MessageQueue.Instance.Enqueue("[Settings] Reading " + path + " failed: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageQueue.Instance.Enqueue("[Settings] Reading " + path + " failed: " + ex.Message);
            }

            return new List<string>();
        }

        // The exit save runs while Windows may be ending the process: a truncate-and-write killed halfway is what emptied the file.
        private void WriteFile(IEnumerable<string> lines)
        {
            if (_fileWritesBlocked) return;

            try
            {
                var temp = FileName + TempSuffix;
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    foreach (var line in lines) writer.WriteLine(line);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(FileName)) File.Replace(temp, FileName, _rotateBackup ? FileName + BackupSuffix : null, ignoreMetadataErrors: true);
                else File.Move(temp, FileName);
                _rotateBackup = true;
            }
            catch (IOException ex)
            {
                MessageQueue.Instance.Enqueue("[Store] Writing " + FileName + " failed: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageQueue.Instance.Enqueue("[Store] Writing " + FileName + " failed: " + ex.Message);
            }
        }

        /// <summary>An enum key by name or number, case-insensitive (P9-12); an unknown value reads as <paramref name="Default"/>.</summary>
        public T ReadEnum<T>(string section, string key, T Default) where T : struct, Enum
        {
            var text = ReadString(section, key, Default.ToString());
            return Enum.TryParse(text, true, out T value) && Enum.IsDefined(value) ? value : Default;
        }

        private string StoreKey => DesignStore.Key(FileName) + StoreKeySuffix;

        private long _version;

        /// <summary>The design sections as the store holds them now (after every queued save): what the web Config page verifies.</summary>
        public List<string> PersistedDesignLines() =>
            DesignStore.Active ? DesignStore.Run(conn => DesignIni.ReadLines(conn, StoreKey)) : null;

        /// <summary>Replaces the design sections read from the file with the store's (importing the file's on first use).</summary>
        public void LoadDesignSections()
        {
            if (!DesignStore.Active) return;
            var (fromFile, rest) = DesignIni.Split(Lines, DesignIni.SetupDesignSections);
            var stored = DesignStore.Guard(() => DesignStore.Run(conn => DesignIni.EnsureImported(conn, StoreKey, () => fromFile)), () => null);
            if (stored == null) return; // the store can't be used: keep the file's sections (logged)
            Lines.Clear();
            Lines.AddRange(rest);
            if (Lines.Count > 0 && stored.Lines.Count > 0 && Lines[Lines.Count - 1].Length > 0) Lines.Add(string.Empty);
            Lines.AddRange(stored.Lines);
        }

        // InIReader.Write saves after every key: without this one Settings.Save would rewrite the file (and flush it to disk) about 170 times,
        // and the backup would be the file from one key ago rather than the previous save.
        public void BeginBatch() => _batching = true;

        public void EndBatch()
        {
            _batching = false;
            Save();
        }

        public override void Save()
        {
            if (_batching) return;

            if (!DesignStore.Active)
            {
                WriteFile(Lines);
                return;
            }

            var (design, rest) = DesignIni.Split(Lines, DesignIni.SetupDesignSections);
            if (!DesignStore.SavesBlockedUntilRestart)
            {
                var version = Interlocked.Increment(ref _version);
                var key = StoreKey;
                DesignStore.Post(conn =>
                {
                    if (Interlocked.Read(ref _version) != version) return; // coalesced: a newer snapshot follows
                    DesignIni.Write(conn, key, design);
                }, "Setup.ini design sections save");
            }

            WriteFile(rest);
        }
    }
}
