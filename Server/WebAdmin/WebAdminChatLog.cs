using System.Globalization;
using System.Text;

namespace Server.WebAdmin
{
    /// <summary>One chat line seen by the web admin (live ring or parsed from a log4net chat file).</summary>
    public sealed class ChatLogEntry
    {
        public long Seq { get; set; }
        public DateTime Utc { get; set; }
        public string Channel { get; set; }
        public string Sender { get; set; }
        public string Target { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Filter for chat queries. Every member is optional (null/empty = no filter).</summary>
    public sealed class ChatLogFilter
    {
        /// <summary>Case-insensitive substring of the message.</summary>
        public string Text { get; set; }

        /// <summary>Accepted channels (case-insensitive); empty = all.</summary>
        public ISet<string> Channels { get; set; }

        /// <summary>Case-insensitive substring of the sender or the target.</summary>
        public string Name { get; set; }

        /// <summary>Exact (case-insensitive) sender/target match instead of a substring.</summary>
        public bool ExactName { get; set; }

        public static ChatLogFilter Create(string text, string channels, string name, bool exactName = false)
        {
            var f = new ChatLogFilter
            {
                Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                ExactName = exactName,
            };

            if (!string.IsNullOrWhiteSpace(channels))
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in channels.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var ch = c.Trim();
                    if (ch.Length > 0 && !ch.Equals("all", StringComparison.OrdinalIgnoreCase)) set.Add(ch);
                }

                if (set.Count > 0) f.Channels = set;
            }

            return f;
        }

        public bool IsEmpty => Text == null && Name == null && (Channels == null || Channels.Count == 0);

        public bool Matches(ChatLogEntry e)
        {
            if (e == null) return false;

            if (Channels != null && Channels.Count > 0 && !Channels.Contains(e.Channel ?? string.Empty)) return false;

            if (Name != null)
            {
                if (ExactName)
                {
                    if (!string.Equals(e.Sender, Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(e.Target, Name, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if ((e.Sender ?? string.Empty).IndexOf(Name, StringComparison.OrdinalIgnoreCase) < 0 &&
                         (e.Target ?? string.Empty).IndexOf(Name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            if (Text != null && (e.Message ?? string.Empty).IndexOf(Text, StringComparison.OrdinalIgnoreCase) < 0) return false;

            return true;
        }
    }

    /// <summary>Result of a ring query.</summary>
    public sealed class ChatLogPage
    {
        /// <summary>Matching lines, oldest first.</summary>
        public List<ChatLogEntry> Lines { get; set; } = new List<ChatLogEntry>();

        /// <summary>The cursor for the next incremental query (?after=).</summary>
        public long Last { get; set; }

        /// <summary>The caller's cursor was older than the oldest line still in the ring (lines were lost in between).</summary>
        public bool Gap { get; set; }

        /// <summary>The caller's cursor was newer than anything known (process restarted): the result is a fresh snapshot.</summary>
        public bool Reset { get; set; }

        /// <summary>More matching lines exist after <see cref="Last"/> (the limit cut the page).</summary>
        public bool More { get; set; }
    }

    /// <summary>
    /// Fixed-size ring of chat lines with monotonically increasing sequence ids. Thread-safe; Append is O(1) (it runs on the
    /// game thread from <see cref="WebAdminHooks.Chat"/>).
    /// </summary>
    public sealed class ChatLogRing
    {
        private readonly object _gate = new object();
        private readonly ChatLogEntry[] _items;
        private readonly Func<DateTime> _clock;
        private int _start;
        private int _count;
        private long _lastSeq;

        public ChatLogRing(int capacity = 5000, Func<DateTime> clock = null)
        {
            if (capacity < 1) capacity = 1;
            _items = new ChatLogEntry[capacity];
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int Capacity => _items.Length;

        public int Count
        {
            get
            {
                lock (_gate) return _count;
            }
        }

        public long LastSeq
        {
            get
            {
                lock (_gate) return _lastSeq;
            }
        }

        public long Append(string channel, string sender, string message, string target = null)
        {
            var entry = new ChatLogEntry
            {
                Channel = string.IsNullOrWhiteSpace(channel) ? "normal" : channel.Trim().ToLowerInvariant(),
                Sender = Clean(sender, 64),
                Target = string.IsNullOrWhiteSpace(target) ? null : Clean(target, 64),
                Message = Clean(message, 2000),
            };

            lock (_gate)
            {
                entry.Seq = ++_lastSeq;
                entry.Utc = _clock();

                if (_count < _items.Length)
                {
                    _items[(_start + _count) % _items.Length] = entry;
                    _count++;
                }
                else
                {
                    _items[_start] = entry;
                    _start = (_start + 1) % _items.Length;
                }

                return entry.Seq;
            }
        }

        /// <summary>
        /// afterSeq &lt;= 0: the newest <paramref name="limit"/> matching lines (oldest first). afterSeq &gt; 0: matching lines with
        /// Seq &gt; afterSeq, oldest first, up to <paramref name="limit"/>. <see cref="ChatLogPage.Last"/> always advances past
        /// lines the filter skipped so a filtered poll does not rescan them.
        /// </summary>
        public ChatLogPage Query(long afterSeq, ChatLogFilter filter, int limit = 500)
        {
            if (limit < 1) limit = 1;
            filter ??= new ChatLogFilter();

            var page = new ChatLogPage();

            lock (_gate)
            {
                if (afterSeq > _lastSeq)
                {
                    page.Reset = true;
                    afterSeq = 0;
                }

                if (afterSeq > 0 && _count > 0)
                {
                    var oldest = _items[_start].Seq;
                    if (afterSeq < oldest - 1) page.Gap = true;
                }

                if (afterSeq <= 0)
                {
                    // newest first scan, then reverse to oldest first
                    var picked = new List<ChatLogEntry>(Math.Min(limit, _count));
                    for (var i = _count - 1; i >= 0 && picked.Count < limit; i--)
                    {
                        var e = _items[(_start + i) % _items.Length];
                        if (filter.Matches(e)) picked.Add(e);
                    }

                    picked.Reverse();
                    page.Lines = picked;
                    page.Last = _lastSeq;
                    return page;
                }

                for (var i = 0; i < _count; i++)
                {
                    var e = _items[(_start + i) % _items.Length];
                    if (e.Seq <= afterSeq || !filter.Matches(e)) continue;

                    if (page.Lines.Count >= limit)
                    {
                        page.More = true;
                        break;
                    }

                    page.Lines.Add(e);
                }

                page.Last = page.More && page.Lines.Count > 0 ? page.Lines[page.Lines.Count - 1].Seq : _lastSeq;
                return page;
            }
        }

        internal static string Clean(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(Math.Min(value.Length, max));
            foreach (var c in value)
            {
                if (sb.Length >= max) break;
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    // Line breaks/tabs become a single space; a "\r\n" pair (or a break after a space) must not yield a double space.
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                else if (!char.IsControl(c)) sb.Append(c);
            }

            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// Parser for log4net chat files (<c>Logs\Chat\Chat (dd-MM-yyyy).log</c>, layout <c>%date - %message</c>, where the message is
    /// <c>"{Name}: {raw chat}"</c> written by PlayerObject.Chat before any prefix handling).
    /// </summary>
    public static class ChatLogFileParser
    {
        public const string RedactedPassword = "[redacted: text typed after @login]";

        /// <summary>File name for a day: <c>Chat (dd-MM-yyyy).log</c>.</summary>
        public static string FileNameFor(DateTime date) => "Chat (" + date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) + ").log";

        /// <summary>Inverse of <see cref="FileNameFor"/>; false for anything else.</summary>
        public static bool TryParseFileName(string fileName, out DateTime date)
        {
            date = default;
            if (string.IsNullOrEmpty(fileName)) return false;

            fileName = Path.GetFileName(fileName);
            if (!fileName.StartsWith("Chat (", StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(").log", StringComparison.OrdinalIgnoreCase))
                return false;

            var inner = fileName.Substring(6, fileName.Length - 6 - 5);
            return DateTime.TryParseExact(inner, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        /// <summary>Parses "yyyy-MM-dd" (the API's date parameter).</summary>
        public static bool TryParseDate(string text, out DateTime date)
        {
            return DateTime.TryParseExact((text ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        /// <summary>
        /// Parses one line. Returns null for blank or unparseable lines. <paramref name="localToUtc"/> converts the file's local
        /// timestamp (default: <see cref="DateTime.ToUniversalTime"/>).
        /// </summary>
        public static ChatLogEntry ParseLine(string line, Func<DateTime, DateTime> localToUtc = null)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            // "2026-10-02 18:01:02,123 - Name: message"
            var sep = line.IndexOf(" - ", StringComparison.Ordinal);
            if (sep < 10) return null;

            var stamp = line.Substring(0, sep).Trim();
            if (!DateTime.TryParseExact(stamp, new[] { "yyyy-MM-dd HH:mm:ss,fff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
                return null;

            var utc = localToUtc != null ? localToUtc(DateTime.SpecifyKind(local, DateTimeKind.Local)) : local.ToUniversalTime();
            var body = line.Substring(sep + 3);

            var entry = new ChatLogEntry { Utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc) };

            var colon = body.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 || colon > 40 || body.Substring(0, colon).IndexOf(' ') >= 0)
            {
                entry.Channel = "system";
                entry.Sender = string.Empty;
                entry.Message = ChatLogRing.Clean(body, 2000);
                return entry;
            }

            entry.Sender = body.Substring(0, colon);
            var raw = body.Substring(colon + 2);
            Classify(raw, entry);
            return entry;
        }

        /// <summary>Derives channel / target / text from the raw chat text, mirroring the prefixes PlayerObject.Chat handles.</summary>
        internal static void Classify(string raw, ChatLogEntry entry)
        {
            raw ??= string.Empty;
            string channel, target = null, text;

            if (raw.StartsWith("/", StringComparison.Ordinal))
            {
                var rest = raw.Substring(1);
                var space = rest.IndexOf(' ');
                channel = "whisper";
                target = space < 0 ? rest.Trim() : rest.Substring(0, space).Trim();
                text = space < 0 ? string.Empty : rest.Substring(space + 1).Trim();
            }
            else if (raw.StartsWith("!!", StringComparison.Ordinal))
            {
                channel = "group";
                text = raw.Substring(2).Trim();
            }
            else if (raw.StartsWith("!~", StringComparison.Ordinal))
            {
                channel = "guild";
                text = raw.Substring(2).Trim();
            }
            else if (raw.StartsWith("!#", StringComparison.Ordinal))
            {
                channel = "mentor";
                text = raw.Substring(2).Trim();
            }
            else if (raw.StartsWith("!", StringComparison.Ordinal))
            {
                channel = "shout";
                text = raw.Substring(1).Trim();
            }
            else if (raw.StartsWith(":)", StringComparison.Ordinal))
            {
                channel = "relationship";
                text = raw.Substring(2).Trim();
            }
            else if (raw.StartsWith("@!", StringComparison.Ordinal))
            {
                channel = "announcement";
                text = raw.Substring(2).Trim();
            }
            else if (raw.StartsWith("@", StringComparison.Ordinal))
            {
                channel = "command";
                text = raw.Trim();
            }
            else
            {
                channel = "normal";
                text = raw.Trim();
            }

            entry.Channel = channel;
            entry.Target = string.IsNullOrEmpty(target) ? null : ChatLogRing.Clean(target, 64);
            entry.Message = ChatLogRing.Clean(text, 2000);
        }

        /// <summary>
        /// Parses lines in order and returns the matching ones (oldest first), keeping only the newest <paramref name="limit"/>.
        /// The line a player types right after "@login" (the GM password prompt) is redacted. Seq = 1-based line number.
        /// </summary>
        public static List<ChatLogEntry> ParseLines(IEnumerable<string> lines, ChatLogFilter filter, int limit, out int total, out bool truncated,
            Func<DateTime, DateTime> localToUtc = null)
        {
            if (limit < 1) limit = 1;
            filter ??= new ChatLogFilter();

            var keep = new Queue<ChatLogEntry>();
            var awaitingPassword = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            total = 0;
            truncated = false;
            long lineNo = 0;

            foreach (var line in lines)
            {
                lineNo++;
                var e = ParseLine(line, localToUtc);
                if (e == null) continue;
                e.Seq = lineNo;

                if (!string.IsNullOrEmpty(e.Sender))
                {
                    if (awaitingPassword.Remove(e.Sender))
                    {
                        e.Channel = "command";
                        e.Target = null;
                        e.Message = RedactedPassword;
                    }
                    else if (e.Channel == "command" && IsLoginCommand(e.Message))
                    {
                        awaitingPassword.Add(e.Sender);
                    }
                }

                if (!filter.Matches(e)) continue;

                total++;
                keep.Enqueue(e);
                if (keep.Count > limit)
                {
                    keep.Dequeue();
                    truncated = true;
                }
            }

            return keep.ToList();
        }

        private static bool IsLoginCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var t = text.Trim();
            return t.Equals("@login", StringComparison.OrdinalIgnoreCase) || t.StartsWith("@login ", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Process-wide live chat log: <see cref="WebAdminHooks.Chat"/> → ring (5000). Web admin broadcasts and GM messages are
    /// appended too (channels "broadcast" / "admin") so moderators see what the staff said.
    /// </summary>
    public static class WebAdminChatLog
    {
        public const int DefaultCapacity = 5000;

        /// <summary>Known channels (chips in the UI). Hook channels first, then the web admin ones, then history-only ones.</summary>
        public static readonly IReadOnlyList<string> Channels = new[]
        {
            "normal", "shout", "shout-map", "shout-server", "whisper", "group", "guild", "mentor", "relationship", "announcement",
            "broadcast", "admin",
        };

        /// <summary>Channels that only appear in log-file history.</summary>
        public static readonly IReadOnlyList<string> HistoryOnlyChannels = new[] { "command", "system" };

        private static ChatLogRing _ring = new ChatLogRing(DefaultCapacity);
        private static int _subscribed;

        internal static ChatLogRing Ring
        {
            get => _ring;
            set => _ring = value ?? new ChatLogRing(DefaultCapacity);
        }

        /// <summary>Subscribes the chat hook once per process.</summary>
        internal static void EnsureSubscribed()
        {
            if (Interlocked.Exchange(ref _subscribed, 1) != 0) return;
            WebAdminHooks.Chat += OnChat;
        }

        private static void OnChat(string channel, string sender, string message, string target)
        {
            _ring.Append(channel, sender, message, target);
        }

        public static long Append(string channel, string sender, string message, string target = null) => _ring.Append(channel, sender, message, target);

        public static ChatLogPage Query(long afterSeq, ChatLogFilter filter, int limit) => _ring.Query(afterSeq, filter, limit);

        /// <summary>Directory of the log4net chat files (<c>&lt;exe&gt;\Logs\Chat</c>).</summary>
        public static string HistoryDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "Chat");
    }
}
