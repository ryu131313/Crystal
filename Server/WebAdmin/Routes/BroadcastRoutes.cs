using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Server.MirEnvir;
using S = ServerPackets;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Server-wide broadcasts (POST /api/admin/broadcast, GET /api/admin/broadcast/recent) and the login notice editor
    /// (GET/PUT /api/admin/broadcast/notice, Settings.NoticePath).
    /// </summary>
    public sealed class BroadcastRoutes : IWebAdminRoutes
    {
        public const int MaxMessageLength = 500;
        public const int MaxLines = 10;
        public const int MaxNoticeBytes = 64 * 1024;

        private static readonly object NoticeFileLock = new object();

        public void Register(WebAdminRouter router)
        {
            router.Post("/api/admin/broadcast", PostBroadcast);
            router.Get("/api/admin/broadcast/recent", GetRecent);
            router.Get("/api/admin/broadcast/notice", GetNotice);
            router.Put("/api/admin/broadcast/notice", PutNotice);
        }

        /// <summary>API type name → ChatType. Null for unknown names.</summary>
        public static ChatType? ParseType(string type)
        {
            switch ((type ?? "announcement").Trim().ToLowerInvariant())
            {
                case "":
                case "announcement":
                    return ChatType.Announcement;
                case "shout":
                    return ChatType.Shout3;
                case "system":
                    return ChatType.System;
                case "hint":
                    return ChatType.Hint;
                case "linemessage":
                    return ChatType.LineMessage;
                default:
                    return null;
            }
        }

        /// <summary>Splits a broadcast into the chat lines sent (blank lines dropped, control characters removed). Pure.</summary>
        public static List<string> SplitLines(string message)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(message)) return lines;

            foreach (var raw in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = ChatLogRing.Clean(raw, MaxMessageLength);
                if (line.Length > 0) lines.Add(line);
            }

            return lines;
        }

        private static async Task PostBroadcast(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");

            var message = body.GetString("message");
            var typeName = (body.GetString("type") ?? "announcement").Trim().ToLowerInvariant();
            if (typeName.Length == 0) typeName = "announcement";

            var type = ParseType(typeName) ?? throw new WebAdminException(400, "type must be announcement, shout, system, hint or linemessage");

            if (message != null && message.Length > MaxMessageLength * MaxLines)
                throw new WebAdminException(400, "The message is too long");

            var lines = SplitLines(message);
            if (lines.Count == 0) throw new WebAdminException(400, "message is required");
            if (lines.Count > MaxLines) throw new WebAdminException(400, "At most " + MaxLines + " lines per broadcast");

            var admin = req.AdminName;

            var recipients = await WebAdminGame.RunAsync(() =>
            {
                var envir = Envir.Main;
                foreach (var line in lines)
                {
                    envir.Broadcast(new S.Chat { Message = line, Type = type });
                    MessageQueue.Instance.EnqueueChat("[Web admin broadcast] " + admin + ": " + line);
                }

                return envir.Players.Count;
            }).ConfigureAwait(false);

            foreach (var line in lines)
                WebAdminChatLog.Append("broadcast", "[web] " + admin, line);

            var text = string.Join("\n", lines);
            WebAdminAudit.Record(req, "broadcast.send", typeName, new { message = text, type = typeName, lines = lines.Count, recipients });

            await req.Ok(new { recipients, lines = lines.Count, type = typeName },
                "Broadcast sent to " + recipients + " online player" + (recipients == 1 ? string.Empty : "s") + ".").ConfigureAwait(false);
        }

        private static Task GetRecent(WebAdminRequest req)
        {
            var limit = Math.Clamp(req.QueryInt("limit", 25), 1, 200);
            var days = Math.Clamp(req.QueryInt("days", 30), 1, 365);

            var entries = WebAdminAudit.Query(limit, "broadcast.send", null, null, days)
                .Select(e =>
                {
                    string message = null, type = e.Target;
                    int recipients = -1;
                    if (e.Details.HasValue && e.Details.Value.ValueKind == JsonValueKind.Object)
                    {
                        var d = e.Details.Value;
                        message = d.GetString("message");
                        type = d.GetString("type") ?? type;
                        recipients = d.GetInt("recipients", -1);
                    }

                    return new { id = e.Id, ts = e.Ts, admin = e.Admin, type, message, recipients };
                })
                .Where(e => !string.IsNullOrEmpty(e.message))
                .ToList();

            return req.Json(new { entries });
        }

        // ------------------------------------------------------------------------------------------------- notice

        private static string NoticeFullPath()
        {
            string path;
            lock (WebAdminOptions.SettingsLock) path = Settings.NoticePath;
            return Path.GetFullPath(path);
        }

        /// <summary>Weak validator for optimistic concurrency: SHA-256 of the file bytes (empty file / missing → hash of nothing).</summary>
        public static string ComputeEtag(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? Array.Empty<byte>());
                return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
            }
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            if (!File.Exists(path)) return Array.Empty<byte>();

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Decodes text, honouring a UTF-8 BOM, falling back to the system ANSI page when the bytes are not valid UTF-8.</summary>
        public static string DecodeText(byte[] bytes, out bool hadBom)
        {
            hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var offset = hadBom ? 3 : 0;

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes, offset, bytes.Length - offset);
            }
        }

        private static object NoticeDto(string path)
        {
            byte[] bytes;
            lock (NoticeFileLock) bytes = ReadAllBytesShared(path);

            var content = DecodeText(bytes, out _);
            Notice notice;
            lock (WebAdminOptions.SettingsLock) notice = Settings.Notice;

            return new
            {
                path = Path.GetFileName(path),
                fullPath = path,
                exists = File.Exists(path),
                content,
                etag = ComputeEtag(bytes),
                lastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null,
                loaded = notice == null ? null : new { title = notice.Title, message = notice.Message, lastUpdate = notice.LastUpdate },
            };
        }

        private static Task GetNotice(WebAdminRequest req)
        {
            return req.Json(NoticeDto(NoticeFullPath()));
        }

        private static async Task PutNotice(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");

            if (!body.TryGetPropertyIgnoreCase("content", out var c) || c.ValueKind != JsonValueKind.String)
                throw new WebAdminException(400, "content (string) is required");

            var content = c.GetString() ?? string.Empty;
            var etag = body.GetString("etag");
            var pushOnline = body.GetBool("pushOnline", false);

            // Normalise to CRLF like the other Envir text files.
            content = content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
            var newBytes = new UTF8Encoding(false).GetBytes(content);
            if (newBytes.Length > MaxNoticeBytes) throw new WebAdminException(413, "The notice is too large (max 64 KB)");

            var path = NoticeFullPath();
            string oldEtag;

            lock (NoticeFileLock)
            {
                var oldBytes = ReadAllBytesShared(path);
                oldEtag = ComputeEtag(oldBytes);

                if (!string.IsNullOrEmpty(etag) && !string.Equals(etag, oldEtag, StringComparison.OrdinalIgnoreCase))
                    throw new WebAdminException(409, "The notice changed on disk since you opened it. Reload it and re-apply your edit.", new { etag = oldEtag });

                DecodeText(oldBytes, out var hadBom);
                if (hadBom) newBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(newBytes).ToArray();

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var temp = path + ".webadmin.tmp";
                File.WriteAllBytes(temp, newBytes);

                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }

            // Settings.LoadNotice replaces Settings.Notice and then fills it line by line, while the game thread reads it at
            // login (PlayerObject). Reload on the game thread when the world is running so it never overlaps a login;
            // inline otherwise (no logins are processed then).
            Notice ReloadNotice()
            {
                lock (WebAdminOptions.SettingsLock)
                {
                    Settings.LoadNotice();
                    return Settings.Notice;
                }
            }

            Notice reloaded = null;
            var reloadedOnGameThread = false;
            if (WebAdminGame.IsRunning)
            {
                try
                {
                    reloaded = await WebAdminGame.RunAsync(ReloadNotice).ConfigureAwait(false);
                    reloadedOnGameThread = true;
                }
                catch (WorldNotRunningException)
                {
                    // raced with a stop: reload inline below
                }
            }
            if (!reloadedOnGameThread) reloaded = ReloadNotice();

            var pushed = 0;
            if (pushOnline && WebAdminGame.IsRunning && reloaded != null && !string.IsNullOrWhiteSpace(reloaded.Message))
            {
                pushed = await WebAdminGame.RunAsync(() =>
                {
                    var players = Envir.Main.Players;
                    for (var i = 0; i < players.Count; i++)
                        players[i].Enqueue(new S.UpdateNotice { Notice = reloaded });
                    return players.Count;
                }).ConfigureAwait(false);
            }

            WebAdminAudit.Record(req, "broadcast.notice", Path.GetFileName(path), new
            {
                bytes = newBytes.Length,
                previousEtag = oldEtag,
                title = reloaded?.Title,
                pushedTo = pushed,
            });

            await req.Ok(NoticeDto(path), pushed > 0
                ? "Notice saved and shown to " + pushed + " online player" + (pushed == 1 ? string.Empty : "s") + "."
                : "Notice saved. Players see it at their next login.").ConfigureAwait(false);
        }
    }
}
