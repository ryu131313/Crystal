using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Server.MirEnvir
{
    /// <summary>
    /// Export thread only (<see cref="WebsiteExport"/>): sorts a <see cref="WebsiteExportSnapshot"/> and writes, under its path,
    /// <c>meta.json</c>, <c>guilds.json</c>, <c>leaderboards/&lt;stat&gt;.json</c> (top <see cref="LeaderboardSize"/>, plus
    /// <c>level</c>) and <c>characters/&lt;id&gt;.json</c>. Each file is written to a temp file and swapped in with
    /// <see cref="File.Replace(string, string, string)"/> (no folder swap: a web host holding a file open would make that fail);
    /// a file whose bytes did not change since the last export is not rewritten, and character files nobody owns any more
    /// (deleted, hidden, banned, renamed) are deleted afterwards. Touches only the snapshot, never game state.
    /// </summary>
    internal sealed class WebsiteExportWriter
    {
        public const int LeaderboardSize = 100;
        public const string LevelBoard = "level";

        private static readonly JsonWriterOptions JsonOptions = new JsonWriterOptions { Indented = false };

        private readonly ArrayBufferWriter<byte> _buffer = new ArrayBufferWriter<byte>(4096);
        private readonly Dictionary<string, ulong> _written = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _failures;
        private string _firstFailure;

        /// <summary>Files written (changed) by the last <see cref="Write"/> (tests, diagnostics).</summary>
        public int LastWritten { get; private set; }

        public void Write(WebsiteExportSnapshot snapshot)
        {
            _failures = 0;
            _firstFailure = null;
            LastWritten = 0;

            var root = snapshot.Path;
            var characterDir = Path.Combine(root, "characters");
            var boardDir = Path.Combine(root, "leaderboards");
            Directory.CreateDirectory(characterDir);
            Directory.CreateDirectory(boardDir);

            var count = snapshot.RowCount;
            var rows = new WebsiteCharacterRow[count];
            for (var i = 0; i < count; i++) rows[i] = snapshot.Rows[i];
            Array.Sort(rows, (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

            // Ids (file stems) first: leaderboards link to them.
            _ids.Clear();
            var ids = new string[count];
            for (var i = 0; i < count; i++)
            {
                var id = FileId(rows[i].Name);
                var unique = id;
                for (var n = 2; !_ids.Add(unique); n++) unique = id + "-" + n.ToString(CultureInfo.InvariantCulture);
                ids[i] = unique;
            }

            for (var i = 0; i < count; i++)
            {
                WriteCharacter(Begin(), rows[i]);
                Commit(Path.Combine(characterDir, ids[i] + ".json"));
            }

            var order = new int[count];
            WriteBoard(boardDir, LevelBoard, rows, ids, order, -1);
            for (var s = 0; s < WebsiteExport.ExportedStats.Length; s++)
                WriteBoard(boardDir, WebsiteExport.ExportedStatNames[s], rows, ids, order, s);

            WriteGuilds(Begin(), snapshot);
            Commit(Path.Combine(root, "guilds.json"));

            WriteMeta(Begin(), snapshot, count);
            Commit(Path.Combine(root, "meta.json"));

            DeleteStale(characterDir);

            if (_failures > 0)
                MessageQueue.Instance.Enqueue("[Website] Export: " + _failures.ToString(CultureInfo.InvariantCulture) + " file(s) not written; first: " + _firstFailure);
        }

        // ---- JSON (the whitelist lives here: every property name the site can see is written below) ----

        internal static void WriteCharacter(Utf8JsonWriter json, WebsiteCharacterRow row)
        {
            json.WriteStartObject();
            json.WriteString("name", row.Name);
            json.WriteString("class", row.Class.ToString());
            json.WriteString("gender", row.Gender.ToString());
            json.WriteNumber("level", row.Level);
            if (row.Guild == null) json.WriteNull("guild"); else json.WriteString("guild", row.Guild);
            json.WriteStartObject("stats");
            for (var s = 0; s < WebsiteExport.ExportedStatNames.Length; s++)
                json.WriteNumber(WebsiteExport.ExportedStatNames[s], row.Stats[s]);
            json.WriteEndObject();
            json.WriteEndObject();
            json.Flush();
        }

        internal static void WriteBoardJson(Utf8JsonWriter json, string stat, WebsiteCharacterRow[] rows, string[] ids, int[] order, int length, int statIndex)
        {
            json.WriteStartObject();
            json.WriteString("stat", stat);
            json.WriteStartArray("entries");
            for (var i = 0; i < length; i++)
            {
                var row = rows[order[i]];
                json.WriteStartObject();
                json.WriteNumber("rank", i + 1);
                json.WriteString("id", ids[order[i]]);
                json.WriteString("name", row.Name);
                json.WriteString("class", row.Class.ToString());
                json.WriteNumber("level", row.Level);
                if (row.Guild == null) json.WriteNull("guild"); else json.WriteString("guild", row.Guild);
                json.WriteNumber("value", statIndex < 0 ? row.Level : row.Stats[statIndex]);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
        }

        internal static void WriteGuilds(Utf8JsonWriter json, WebsiteExportSnapshot snapshot)
        {
            var guilds = new WebsiteGuildRow[snapshot.GuildCount];
            for (var i = 0; i < guilds.Length; i++) guilds[i] = snapshot.Guilds[i];
            Array.Sort(guilds, (x, y) =>
            {
                var c = y.Level.CompareTo(x.Level);
                if (c == 0) c = y.Members.CompareTo(x.Members);
                return c != 0 ? c : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });

            json.WriteStartObject();
            json.WriteStartArray("guilds");
            for (var i = 0; i < guilds.Length; i++)
            {
                json.WriteStartObject();
                json.WriteString("name", guilds[i].Name);
                json.WriteNumber("level", guilds[i].Level);
                json.WriteNumber("members", guilds[i].Members);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
        }

        internal static void WriteMeta(Utf8JsonWriter json, WebsiteExportSnapshot snapshot, int characters)
        {
            json.WriteStartObject();
            json.WriteString("updated", DateTime.SpecifyKind(snapshot.Taken, DateTimeKind.Utc));
            json.WriteNumber("characters", characters);
            json.WriteNumber("guilds", snapshot.GuildCount);
            json.WriteNumber("leaderboardSize", LeaderboardSize);
            json.WriteStartArray("leaderboards");
            json.WriteStringValue(LevelBoard);
            for (var s = 0; s < WebsiteExport.ExportedStatNames.Length; s++) json.WriteStringValue(WebsiteExport.ExportedStatNames[s]);
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
        }

        /// <summary>
        /// A safe, case-folded file stem for a character name: ASCII letters and digits kept (lower case), every other character
        /// as <c>_xxxx</c> (its UTF-16 code in hex), Windows device names prefixed with <c>_</c>.
        /// </summary>
        internal static string FileId(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else if (ch >= 'A' && ch <= 'Z') sb.Append((char)(ch + 32));
                else sb.Append('_').Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            }

            var id = sb.Length == 0 ? "_" : sb.ToString();
            switch (id)
            {
                case "con": case "prn": case "aux": case "nul":
                case "com1": case "com2": case "com3": case "com4": case "com5": case "com6": case "com7": case "com8": case "com9":
                case "lpt1": case "lpt2": case "lpt3": case "lpt4": case "lpt5": case "lpt6": case "lpt7": case "lpt8": case "lpt9":
                    return "_" + id;
            }
            return id;
        }

        // ---- files ----

        private void WriteBoard(string dir, string stat, WebsiteCharacterRow[] rows, string[] ids, int[] order, int statIndex)
        {
            var length = 0;
            for (var i = 0; i < rows.Length; i++)
                if (statIndex < 0 || rows[i].Stats[statIndex] > 0) order[length++] = i;

            Array.Sort(order, 0, length, Comparer<int>.Create((x, y) =>
            {
                var vx = statIndex < 0 ? rows[x].Level : rows[x].Stats[statIndex];
                var vy = statIndex < 0 ? rows[y].Level : rows[y].Stats[statIndex];
                var c = vy.CompareTo(vx);
                return c != 0 ? c : x.CompareTo(y); // rows are sorted by name, so ties list by name
            }));

            WriteBoardJson(Begin(), stat, rows, ids, order, Math.Min(length, LeaderboardSize), statIndex);
            Commit(Path.Combine(dir, stat + ".json"));
        }

        private Utf8JsonWriter _json;

        private Utf8JsonWriter Begin()
        {
            _buffer.Clear();
            if (_json == null) _json = new Utf8JsonWriter(_buffer, JsonOptions);
            else _json.Reset(_buffer);
            return _json;
        }

        /// <summary>Writes the buffer to <paramref name="target"/> through a temp file, unless its bytes did not change.</summary>
        private void Commit(string target)
        {
            var bytes = _buffer.WrittenSpan;
            var hash = Fnv1a(bytes);
            if (_written.TryGetValue(target, out var previous) && previous == hash && File.Exists(target)) return;

            var temp = target + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.Write(bytes);

                if (File.Exists(target)) File.Replace(temp, target, null, ignoreMetadataErrors: true);
                else File.Move(temp, target);

                _written[target] = hash;
                LastWritten++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _written.Remove(target); // retried next export
                _failures++;
                _firstFailure ??= Path.GetFileName(target) + ": " + ex.Message;
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException) { /* counted above */ }
            }
        }

        private void DeleteStale(string characterDir)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(characterDir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _failures++;
                _firstFailure ??= "characters/: " + ex.Message;
                return;
            }

            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var name = Path.GetFileName(file);
                var keep = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && _ids.Contains(name.Substring(0, name.Length - 5));
                if (keep) continue;

                try
                {
                    File.Delete(file);
                    _written.Remove(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _failures++;
                    _firstFailure ??= name + ": " + ex.Message;
                }
            }
        }

        private static ulong Fnv1a(ReadOnlySpan<byte> bytes)
        {
            var hash = 14695981039346656037UL;
            for (var i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
