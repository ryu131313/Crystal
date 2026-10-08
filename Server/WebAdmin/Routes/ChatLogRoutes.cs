using System.Text;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Live chat log (ring fed by WebAdminHooks.Chat): GET /api/admin/chatlog; history from the log4net chat files:
    /// GET /api/admin/chatlog/history?date=yyyy-MM-dd and GET /api/admin/chatlog/dates.
    /// </summary>
    public sealed class ChatLogRoutes : IWebAdminRoutes
    {
        public const int MaxHistoryLines = 20000;
        public const long MaxHistoryFileBytes = 256L * 1024 * 1024;

        public void Register(WebAdminRouter router)
        {
            WebAdminChatLog.EnsureSubscribed();

            router.Get("/api/admin/chatlog", GetLive);
            router.Get("/api/admin/chatlog/history", GetHistory);
            router.Get("/api/admin/chatlog/dates", GetDates);
        }

        private static object Dto(ChatLogEntry e) => new
        {
            seq = e.Seq,
            utc = e.Utc,
            channel = e.Channel,
            sender = e.Sender,
            target = e.Target,
            message = e.Message,
        };

        private static ChatLogFilter FilterFrom(WebAdminRequest req)
        {
            var channels = string.Join(",", req.Query.GetAll("channel"));
            return ChatLogFilter.Create(req.Query["q"], channels, req.Query["name"], req.QueryBool("exact", false));
        }

        private static Task GetLive(WebAdminRequest req)
        {
            var after = Math.Max(0, req.QueryLong("after", 0));
            var limit = Math.Clamp(req.QueryInt("limit", 500), 1, 5000);

            var page = WebAdminChatLog.Query(after, FilterFrom(req), limit);

            return req.Json(new
            {
                lines = page.Lines.Select(Dto).ToList(),
                last = page.Last,
                gap = page.Gap,
                reset = page.Reset,
                more = page.More,
                channels = WebAdminChatLog.Channels,
                capacity = WebAdminChatLog.DefaultCapacity,
            });
        }

        private static Task GetHistory(WebAdminRequest req)
        {
            var dateText = req.Query["date"];
            if (!ChatLogFileParser.TryParseDate(dateText, out var date))
                throw new WebAdminException(400, "date must be yyyy-MM-dd");

            var limit = Math.Clamp(req.QueryInt("limit", 2000), 1, MaxHistoryLines);
            var dir = WebAdminChatLog.HistoryDirectory;
            var fileName = ChatLogFileParser.FileNameFor(date);
            var path = Path.Combine(dir, fileName);

            if (!File.Exists(path))
            {
                return req.Json(new
                {
                    date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    file = fileName,
                    exists = false,
                    lines = Array.Empty<object>(),
                    total = 0,
                    truncated = false,
                });
            }

            var info = new FileInfo(path);
            if (info.Length > MaxHistoryFileBytes)
                throw new WebAdminException(413, "The chat log for that day is too large to read here (" + (info.Length / (1024 * 1024)) + " MB)");

            List<ChatLogEntry> lines;
            int total;
            bool truncated;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8, true))
            {
                lines = ChatLogFileParser.ParseLines(ReadLines(reader), FilterFrom(req), limit, out total, out truncated);
            }

            return req.Json(new
            {
                date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                file = fileName,
                exists = true,
                size = info.Length,
                lines = lines.Select(Dto).ToList(),
                total,
                truncated,
            });
        }

        private static IEnumerable<string> ReadLines(StreamReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
                yield return line;
        }

        private static Task GetDates(WebAdminRequest req)
        {
            var dir = WebAdminChatLog.HistoryDirectory;
            var found = new List<(DateTime date, long size)>();

            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "Chat (*).log", SearchOption.TopDirectoryOnly))
                {
                    if (!ChatLogFileParser.TryParseFileName(file, out var d)) continue;
                    found.Add((d, new FileInfo(file).Length));
                }
            }

            var ordered = found.OrderByDescending(x => x.date)
                .Take(400)
                .Select(x => new { date = x.date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), size = x.size })
                .ToList();

            return req.Json(new { dates = ordered });
        }
    }
}
