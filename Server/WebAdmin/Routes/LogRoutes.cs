using System.Text;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Logs: live rings (GET /api/admin/logs/{server|debug|chat}?after=&amp;max=), log4net files under Logs\{Server,Chat,Debug,Player,Spawn}
    /// (GET /api/admin/logs/files, /api/admin/logs/file?kind=&amp;name=&amp;lines=, /api/admin/logs/download?kind=&amp;name=).
    /// File access is confined to those folders (plain file names only).
    /// </summary>
    public sealed class LogRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/logs/files", ListFiles);
            router.Get("/api/admin/logs/file", TailFile);
            router.Get("/api/admin/logs/download", Download);
            router.Get("/api/admin/logs/{kind}", Live);
        }

        private static Task Live(WebAdminRequest req)
        {
            var kind = (req.Route["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!WebAdminLogTap.IsKnownKind(kind))
                throw new WebAdminException(404, "Unknown log: use server, debug or chat.");

            var after = req.QueryLong("after", 0);
            var max = Math.Clamp(req.QueryInt("max", 500), 1, WebAdminLogTap.DefaultCapacity);
            var (lines, last) = WebAdminLogTap.Get(kind, after, max);

            return req.Json(new
            {
                kind,
                lines = lines.Select(l => new { seq = l.Seq, utc = l.Utc, text = l.Text }).ToList(),
                last,
            });
        }

        private static Task ListFiles(WebAdminRequest req)
        {
            var root = WebAdminLogFiles.Root;
            var kinds = WebAdminLogFiles.Folders.Select(pair => new
            {
                kind = pair.Key,
                folder = pair.Value,
                files = WebAdminLogFiles.List(root, pair.Key).Select(f => new { name = f.Name, size = f.Length, modifiedUtc = f.LastWriteTimeUtc }).ToList(),
            }).ToList();

            return req.Json(new { root, kinds });
        }

        private static async Task TailFile(WebAdminRequest req)
        {
            var file = RequireFile(req);
            var lines = Math.Clamp(req.QueryInt("lines", 1000), 1, WebAdminLogFiles.MaxTailLines);

            var result = await Task.Run(() =>
            {
                using (var stream = WebAdminLogFiles.OpenShared(file.FullName))
                    return WebAdminLogFiles.Tail(stream, lines, WebAdminLogFiles.MaxTailBytes);
            }).ConfigureAwait(false);

            await req.Json(new
            {
                kind = req.Query["kind"].Trim().ToLowerInvariant(),
                name = file.Name,
                size = file.Length,
                modifiedUtc = file.LastWriteTimeUtc,
                lines = result.Lines,
                truncated = result.Truncated,
            });
        }

        private static async Task Download(WebAdminRequest req)
        {
            var file = RequireFile(req);
            if (file.Length > WebAdminLogFiles.MaxDownloadBytes)
                throw new WebAdminException(413, "The log file is too large to download here (" + (file.Length / (1024 * 1024)) + " MB); copy it from the server's Logs folder.");

            var bytes = await Task.Run(() =>
            {
                using (var stream = WebAdminLogFiles.OpenShared(file.FullName))
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }).ConfigureAwait(false);

            await req.Bytes(bytes, "text/plain; charset=utf-8", file.Name);
        }

        private static FileInfo RequireFile(WebAdminRequest req)
        {
            var kind = req.Query["kind"];
            var name = req.Query["name"];

            if (string.IsNullOrWhiteSpace(kind) || !WebAdminLogFiles.Folders.ContainsKey(kind.Trim().ToLowerInvariant()))
                throw new WebAdminException(400, "Unknown log kind: use server, chat, debug, player or spawn.");

            var path = WebAdminLogFiles.Resolve(WebAdminLogFiles.Root, kind, name);
            if (path == null)
                throw new WebAdminException(400, "Invalid log file name.");

            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new WebAdminException(404, "Log file not found.");

            return info;
        }
    }

    /// <summary>log4net file access for the log viewer (path confinement + tail are pure and unit tested).</summary>
    internal static class WebAdminLogFiles
    {
        public const int MaxTailLines = 20000;
        public const long MaxTailBytes = 8L * 1024 * 1024;
        public const long MaxDownloadBytes = 64L * 1024 * 1024;

        /// <summary>API kind → folder under Logs\ (log4net.config RollingFileAppender folders).</summary>
        public static readonly IReadOnlyDictionary<string, string> Folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "Server",
            ["chat"] = "Chat",
            ["debug"] = "Debug",
            ["player"] = "Player",
            ["spawn"] = "Spawn",
            ["quests"] = "Quests", // quest V2 telemetry (lane F)
        };

        /// <summary>Logs folder next to the exe (log4net resolves its relative paths against the application base directory).</summary>
        public static string Root => Path.Combine(AppContext.BaseDirectory, "Logs");

        /// <summary>Files of one kind, newest first (empty when the folder does not exist).</summary>
        public static List<FileInfo> List(string root, string kind)
        {
            var list = new List<FileInfo>();
            if (!Folders.TryGetValue(kind ?? string.Empty, out var folder)) return list;

            try
            {
                var dir = new DirectoryInfo(Path.Combine(root, folder));
                if (!dir.Exists) return list;

                foreach (var f in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    list.Add(f);
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Listing log files failed");
            }

            list.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return list;
        }

        /// <summary>
        /// Full path of <paramref name="name"/> inside Logs\&lt;folder of kind&gt;, or null when the kind is unknown or the name is not a plain
        /// file name (separators, "..", ':' / invalid characters, too long) or would resolve outside the folder. The file need not exist.
        /// </summary>
        public static string Resolve(string root, string kind, string name)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(name)) return null;
            if (!Folders.TryGetValue((kind ?? string.Empty).Trim(), out var folder)) return null;

            if (name.Length > 255) return null;
            if (name == "." || name == "..") return null;
            if (name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0) return null;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) || name.StartsWith(" ", StringComparison.Ordinal)) return null;
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) return null;
            if (WebAdminStaticFiles.IsReservedDeviceName(name)) return null;

            try
            {
                var dir = Path.GetFullPath(Path.Combine(root, folder));
                var full = Path.GetFullPath(Path.Combine(dir, name));
                var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                if (full.IndexOf(Path.DirectorySeparatorChar, prefix.Length) >= 0) return null;
                return full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        public sealed class TailResult
        {
            public List<string> Lines { get; set; } = new List<string>();

            /// <summary>True when older lines exist that were not returned.</summary>
            public bool Truncated { get; set; }
        }

        /// <summary>
        /// The last <paramref name="maxLines"/> lines of a (UTF-8) stream, reading at most the last <paramref name="maxBytes"/> bytes.
        /// A partial first line (when reading started mid-file) is dropped; CRLF and LF both end lines; a trailing newline does not produce
        /// an empty last line.
        /// </summary>
        public static TailResult Tail(Stream stream, int maxLines, long maxBytes)
        {
            var result = new TailResult();
            if (stream == null || maxLines < 1) return result;
            if (maxBytes < 1) maxBytes = 1;

            var length = stream.Length;
            var start = Math.Max(0, length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);

            var toRead = (int)(length - start);
            var buffer = new byte[toRead];
            var total = 0;
            while (total < toRead)
            {
                var read = stream.Read(buffer, total, toRead - total);
                if (read <= 0) break;
                total += read;
            }

            var offset = start == 0 && total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            var text = Encoding.UTF8.GetString(buffer, offset, total - offset);

            var lines = text.Split('\n').Select(l => l.EndsWith("\r", StringComparison.Ordinal) ? l.Substring(0, l.Length - 1) : l).ToList();

            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            var truncated = false;
            if (start > 0 && lines.Count > 0)
            {
                lines.RemoveAt(0);
                truncated = true;
            }

            if (lines.Count > maxLines)
            {
                lines.RemoveRange(0, lines.Count - maxLines);
                truncated = true;
            }

            result.Lines = lines;
            result.Truncated = truncated;
            return result;
        }
    }
}
