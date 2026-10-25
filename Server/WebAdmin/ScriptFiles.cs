using Server.MirEnvir.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.WebAdmin
{
    /// <summary>One editable folder of text scripts (Envir\NPCs, Envir\Drops, ...).</summary>
    public sealed class ScriptRoot
    {
        public ScriptRoot(string id, string label, string directory, bool flat, string description = null)
        {
            Id = id;
            Label = label;
            Directory = directory;
            Flat = flat;
            Description = description;
        }

        /// <summary>Stable id used by the API ("npcs", "drops", ...).</summary>
        public string Id { get; }

        public string Label { get; }

        /// <summary>Absolute folder path.</summary>
        public string Directory { get; }

        /// <summary>True when only *.txt files directly inside the folder are editable (no subfolders).</summary>
        public bool Flat { get; }

        public string Description { get; }

        /// <summary>
        /// Shown, searched, validated and backed up, but not saved, created, deleted or renamed in the Scripts editor (drops: edited in
        /// Game Data › Drops since Drops-Tables-Plan phase 2). Restoring a backup is still allowed: it is the undo for a drops grid save.
        /// </summary>
        public bool ReadOnly { get; init; }

        /// <summary>Why a <see cref="ReadOnly"/> root refuses edits, with where to edit instead (null = the drops message).</summary>
        public string ReadOnlyMessage { get; init; }

        public bool Exists => System.IO.Directory.Exists(Directory) || DesignFiles.IsDesignFolder(Directory); // roadmap P7: design roots live in SystemData.db
    }

    public sealed class ScriptFileEntry
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
    }

    public sealed class ScriptTextFile
    {
        public string Content { get; set; }
        public string Etag { get; set; }
        public string Encoding { get; set; }
        public string LineEnding { get; set; }
        public bool MixedLineEndings { get; set; }
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
    }

    public sealed class ScriptBackupEntry
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
        /// <summary>The local time encoded in the backup name (yyyyMMdd-HHmmss).</summary>
        public DateTime Stamp { get; set; }
    }

    public sealed class ScriptSaveResult
    {
        public string Path { get; set; }
        public string Etag { get; set; }
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
        public string Backup { get; set; }
        public bool Unchanged { get; set; }
        public string Encoding { get; set; }
        public string LineEnding { get; set; }
    }

    public sealed class ScriptSearchHit
    {
        public string Path { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Text { get; set; }
    }

    public sealed class ScriptSearchResult
    {
        public List<ScriptSearchHit> Hits { get; } = new List<ScriptSearchHit>();
        public bool Truncated { get; set; }
        public int FilesScanned { get; set; }
        public int FilesSkipped { get; set; }
    }

    /// <summary>
    /// Pure path rules for script files: relative paths only, '/' separated in the API, every segment a plain Windows file
    /// name (no "..", no rooted paths, no ':' alternate data streams, no reserved device names, no '~' short-name aliases).
    /// </summary>
    public static class ScriptPaths
    {
        public const int MaxPathLength = 240;
        public const int MaxSegmentLength = 120;

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        };

        private static readonly char[] InvalidChars = BuildInvalidChars();

        private static char[] BuildInvalidChars()
        {
            var set = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars());
            foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '~' }) set.Add(c);
            for (var c = (char)0; c < 32; c++) set.Add(c);
            return set.ToArray();
        }

        /// <summary>True when the name (without extension) is a Windows device name such as CON or COM1.</summary>
        public static bool IsReservedName(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            var dot = segment.IndexOf('.');
            var stem = (dot >= 0 ? segment.Substring(0, dot) : segment).TrimEnd(' ');
            return ReservedNames.Contains(stem);
        }

        /// <summary>
        /// Validates and normalises a client-supplied relative path ("Folder\File.txt" or "Folder/File.txt") to
        /// "Folder/File.txt". Returns false with a human readable reason when the path is unsafe or malformed.
        /// </summary>
        public static bool TryNormalize(string raw, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "A file path is required.";
                return false;
            }

            if (raw.Length > MaxPathLength)
            {
                error = "The path is too long.";
                return false;
            }

            var path = raw.Trim().Replace('\\', '/');

            if (path.StartsWith("/", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(path) || (path.Length >= 2 && path[1] == ':'))
            {
                error = "Absolute paths are not allowed.";
                return false;
            }

            if (path.IndexOf(':') >= 0)
            {
                error = "':' is not allowed in a path.";
                return false;
            }

            var segments = path.Split('/');
            var clean = new List<string>(segments.Length);

            foreach (var segment in segments)
            {
                if (segment.Length == 0)
                {
                    error = "The path contains an empty folder name.";
                    return false;
                }

                if (segment == "." || segment == "..")
                {
                    error = "'.' and '..' are not allowed in a path.";
                    return false;
                }

                if (segment.Length > MaxSegmentLength)
                {
                    error = "A file or folder name is too long.";
                    return false;
                }

                if (segment.IndexOfAny(InvalidChars) >= 0)
                {
                    error = "The name '" + Printable(segment) + "' contains a character that is not allowed.";
                    return false;
                }

                if (segment.StartsWith(".", StringComparison.Ordinal))
                {
                    error = "Names may not start with '.'.";
                    return false;
                }

                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) || segment.StartsWith(" ", StringComparison.Ordinal))
                {
                    error = "Names may not start with a space or end with a space or '.'.";
                    return false;
                }

                if (IsReservedName(segment))
                {
                    error = "'" + segment + "' is a reserved Windows device name.";
                    return false;
                }

                clean.Add(segment);
            }

            normalized = string.Join("/", clean);
            return true;
        }

        /// <summary>Like <see cref="TryNormalize"/> but throws 400.</summary>
        public static string Normalize(string raw)
        {
            if (!TryNormalize(raw, out var normalized, out var error))
                throw new WebAdminException(400, error);
            return normalized;
        }

        /// <summary>True when the (normalised) path ends in .txt (the only editable script type).</summary>
        public static bool IsTextFile(string normalized)
        {
            return normalized != null && normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && normalized.Length > 4;
        }

        /// <summary>
        /// Combines a root folder and a normalised relative path and checks the result stays inside the root
        /// (prefix check with a trailing separator). Returns null when it escapes.
        /// </summary>
        public static string Combine(string rootDirectory, string normalized)
        {
            var root = System.IO.Path.GetFullPath(rootDirectory);
            var rootWithSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root : root + System.IO.Path.DirectorySeparatorChar;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar)));

            return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>
        /// True when any existing folder or file between the root (exclusive) and the full path (inclusive) is a reparse point
        /// (symbolic link / junction), which could point outside the root.
        /// </summary>
        public static bool CrossesReparsePoint(string rootDirectory, string fullPath)
        {
            var root = System.IO.Path.GetFullPath(rootDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var current = fullPath;

            while (!string.IsNullOrEmpty(current) && current.Length > root.Length)
            {
                try
                {
                    if (File.Exists(current) || System.IO.Directory.Exists(current))
                    {
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                    }
                }
                catch (Exception)
                {
                    return true;
                }

                current = System.IO.Path.GetDirectoryName(current);
            }

            return false;
        }

        private static string Printable(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(c < 32 ? '?' : c);
            return sb.ToString();
        }
    }

    /// <summary>Encoding / line-ending detection that lets a file be edited and written back byte-compatible.</summary>
    public static class ScriptText
    {
        public const string Utf8 = "utf-8";
        public const string Utf8Bom = "utf-8-bom";
        public const string Utf16Le = "utf-16le";
        public const string Utf16Be = "utf-16be";
        /// <summary>Not valid UTF-8: decoded byte-for-byte so saving writes every untouched byte back unchanged.</summary>
        public const string Latin1 = "latin1";

        public const string Crlf = "crlf";
        public const string Lf = "lf";
        public const string Cr = "cr";

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public sealed class Decoded
        {
            /// <summary>Text with every line ending normalised to "\n".</summary>
            public string Content;
            public string Encoding;
            public string LineEnding;
            public bool Mixed;
        }

        public static Decoded Decode(byte[] bytes)
        {
            bytes = bytes ?? Array.Empty<byte>();
            string text;
            string encoding;

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = Utf8Bom;
                text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Utf16Le;
                text = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Utf16Be;
                text = System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(bytes);
                    encoding = Utf8;
                }
                catch (DecoderFallbackException)
                {
                    text = System.Text.Encoding.Latin1.GetString(bytes);
                    encoding = Latin1;
                }
            }

            DetectLineEnding(text, out var lineEnding, out var mixed);

            return new Decoded
            {
                Content = NormalizeNewlines(text),
                Encoding = encoding,
                LineEnding = lineEnding,
                Mixed = mixed,
            };
        }

        /// <summary>Dominant line ending ("crlf" when the text has none, the Windows default the game files use).</summary>
        public static void DetectLineEnding(string text, out string lineEnding, out bool mixed)
        {
            int crlf = 0, lf = 0, cr = 0;

            if (!string.IsNullOrEmpty(text))
            {
                for (var i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '\r')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            crlf++;
                            i++;
                        }
                        else
                        {
                            cr++;
                        }
                    }
                    else if (c == '\n')
                    {
                        lf++;
                    }
                }
            }

            var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
            mixed = kinds > 1;

            if (lf > crlf && lf >= cr) lineEnding = Lf;
            else if (cr > crlf && cr > lf) lineEnding = Cr;
            else lineEnding = Crlf;
        }

        public static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        public static string LineEndingString(string lineEnding)
        {
            switch (lineEnding)
            {
                case Lf: return "\n";
                case Cr: return "\r";
                default: return "\r\n";
            }
        }

        public static bool IsKnownEncoding(string encoding)
        {
            return encoding == Utf8 || encoding == Utf8Bom || encoding == Utf16Le || encoding == Utf16Be || encoding == Latin1;
        }

        /// <summary>
        /// Encodes editor text (any newline style) with the given encoding and line ending. Throws
        /// <see cref="WebAdminException"/> 422 when the text has characters the encoding cannot hold (latin1).
        /// </summary>
        public static byte[] Encode(string content, string encoding, string lineEnding)
        {
            var text = NormalizeNewlines(content ?? string.Empty);
            var newline = LineEndingString(lineEnding);
            if (newline != "\n") text = text.Replace("\n", newline);

            switch (encoding)
            {
                case Utf8Bom:
                    return Concat(new byte[] { 0xEF, 0xBB, 0xBF }, System.Text.Encoding.UTF8.GetBytes(text));
                case Utf16Le:
                    return Concat(new byte[] { 0xFF, 0xFE }, System.Text.Encoding.Unicode.GetBytes(text));
                case Utf16Be:
                    return Concat(new byte[] { 0xFE, 0xFF }, System.Text.Encoding.BigEndianUnicode.GetBytes(text));
                case Latin1:
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (text[i] > 0xFF)
                        {
                            var line = 1;
                            for (var j = 0; j < i; j++) if (text[j] == '\n') line++;
                            throw new WebAdminException(422, "Line " + line.ToString(CultureInfo.InvariantCulture) +
                                " contains a character this file's encoding (" + Latin1 + ") cannot store. Remove it, or recreate the file as UTF-8.",
                                new { line });
                        }
                    }
                    return System.Text.Encoding.Latin1.GetBytes(text);
                default:
                    return new UTF8Encoding(false).GetBytes(text);
            }
        }

        /// <summary>Content hash used as an optimistic-concurrency token.</summary>
        public static string ComputeEtag(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes ?? Array.Empty<byte>());
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }

    /// <summary>
    /// File operations over a set of <see cref="ScriptRoot"/>s with backups under
    /// <c>&lt;backupBase&gt;\&lt;root&gt;\&lt;path&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak</c> and atomic writes. Instance based so tests can point it at
    /// temp folders; <see cref="Current"/> is the server's store built from Settings paths relative to the exe folder.
    /// </summary>
    public sealed class ScriptFileStore
    {
        public const long MaxFileBytes = 8L * 1024 * 1024;
        public const int MaxSearchHits = 500;
        public const int MaxTreeFiles = 50000;
        public const string StampFormat = "yyyyMMdd-HHmmss";

        private static readonly object CurrentLock = new object();
        private static ScriptFileStore _current;

        private readonly object _writeLock = new object();
        private readonly Dictionary<string, ScriptRoot> _roots;
        private readonly Func<DateTime> _now;

        public ScriptFileStore(IEnumerable<ScriptRoot> roots, string backupBase, Func<DateTime> localNow = null)
        {
            Roots = (roots ?? Enumerable.Empty<ScriptRoot>()).ToList();
            _roots = Roots.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            BackupBase = System.IO.Path.GetFullPath(backupBase);
            _now = localNow ?? (() => DateTime.Now);
        }

        public IReadOnlyList<ScriptRoot> Roots { get; }

        public string BackupBase { get; }

        /// <summary>Lock held by every write (exposed for multi-file tools such as the drop class toggle).</summary>
        public object WriteLock => _writeLock;

        /// <summary>The server's store (Settings paths resolved against the exe folder). Built on first use, never in Register.</summary>
        public static ScriptFileStore Current
        {
            get
            {
                lock (CurrentLock)
                {
                    return _current ??= CreateDefault();
                }
            }
        }

        public static ScriptFileStore CreateDefault()
        {
            var baseDir = AppContext.BaseDirectory;
            string Abs(string p) => System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, p));

            var envir = Abs(Settings.EnvirPath);
            var roots = new List<ScriptRoot>
            {
                new ScriptRoot("npcs", "NPC scripts", Abs(Settings.NPCPath), false, "Envir\\NPCs — NPC dialog scripts (Reload NPCs applies changes live)."),
                new ScriptRoot("drops", "Drops", Abs(Settings.DropPath), false, "Envir\\Drops — drop lists, read only here: edit them in Game Data › Drops.") { ReadOnly = true },
                // Quest V2 ship (D2, UserData-and-Quests-Plan.md, Authoring "No content freeze"): the legacy quest bodies only feed the
                // legacy re-import from here on; quests are edited in the Quests page.
                new ScriptRoot("quests", "Quests", Abs(Settings.QuestPath), false, "Envir\\Quests — legacy quest bodies, read only: edit quests in the Quests page.")
                {
                    ReadOnly = true,
                    ReadOnlyMessage = LegacyQuestsReadOnlyMessage,
                },
                new ScriptRoot("recipes", "Recipes", Abs(Settings.RecipePath), true, "Envir\\Recipe — crafting recipes, one file per crafted item."),
                new ScriptRoot("routes", "Routes", Abs(Settings.RoutePath), false, "Envir\\Routes — monster walking routes (applied on reboot)."),
                new ScriptRoot("values", "Values", Abs(Settings.ValuePath), false, "Envir\\Values — value files read and written by NPC scripts."),
                new ScriptRoot("envir", "Envir files", envir, true, "Text files directly in Envir (LineMessage, Notice, DisabledChars, ...)."),
            };

            var systemScripts = System.IO.Path.Combine(envir, "SystemScripts");
            if (System.IO.Directory.Exists(systemScripts))
                roots.Add(new ScriptRoot("systemscripts", "System scripts", systemScripts, false, "Envir\\SystemScripts — shared script blocks used by #INSERT / #INCLUDE."));

            return new ScriptFileStore(roots, System.IO.Path.Combine(baseDir, "Back Up", "WebAdmin", "Scripts"));
        }

        // ------------------------------------------------------------------ roots / paths

        public ScriptRoot GetRoot(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new WebAdminException(400, "A root is required.");
            if (!_roots.TryGetValue(id.Trim(), out var root)) throw new WebAdminException(404, "Unknown script root: " + id);
            return root;
        }

        /// <summary>Validates the relative path for the root and returns the absolute path (the file may not exist).</summary>
        public string ResolveFile(ScriptRoot root, string rawPath, out string normalized)
        {
            normalized = ScriptPaths.Normalize(rawPath);

            if (!ScriptPaths.IsTextFile(normalized))
                throw new WebAdminException(400, "Only .txt files can be edited here.");

            if (root.Flat && normalized.IndexOf('/') >= 0)
                throw new WebAdminException(400, "The " + root.Label + " root only holds files directly in its folder (no subfolders).");

            var full = ScriptPaths.Combine(root.Directory, normalized);
            if (full == null) throw new WebAdminException(400, "The path leaves the " + root.Label + " folder.");

            if (ScriptPaths.CrossesReparsePoint(root.Directory, full))
                throw new WebAdminException(400, "The path goes through a symbolic link or junction, which is not allowed.");

            return full;
        }

        private static void RequireRootFolder(ScriptRoot root)
        {
            if (!root.Exists) throw new WebAdminException(404, "The " + root.Label + " folder does not exist: " + root.Directory);
        }

        private string RelativeOf(ScriptRoot root, string fullPath)
        {
            var rel = System.IO.Path.GetRelativePath(root.Directory, fullPath);
            return rel.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        }

        // ------------------------------------------------------------------ listing

        public List<ScriptFileEntry> ListFiles(ScriptRoot root, out bool truncated)
        {
            truncated = false;
            var result = new List<ScriptFileEntry>();
            if (!root.Exists) return result;

            foreach (var file in EnumerateTextFiles(root))
            {
                if (result.Count >= MaxTreeFiles)
                {
                    truncated = true;
                    break;
                }

                try
                {
                    result.Add(new ScriptFileEntry
                    {
                        Path = RelativeOf(root, file),
                        Size = DesignFiles.Length(file),
                        MtimeUtc = DesignFiles.GetLastWriteTimeUtc(file),
                    });
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            result.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static IEnumerable<string> EnumerateTextFiles(ScriptRoot root)
        {
            if (DesignFiles.IsDesignFolder(root.Directory))
                return DesignFiles.EnumerateFiles(root.Directory, "*.txt", root.Flat ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
                    .Where(f => !System.IO.Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal));

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = !root.Flat,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
                MatchType = MatchType.Simple,
            };

            return System.IO.Directory.EnumerateFiles(root.Directory, "*.txt", options)
                .Where(f => string.Equals(System.IO.Path.GetExtension(f), ".txt", StringComparison.OrdinalIgnoreCase))
                .Where(f => !System.IO.Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ read

        public ScriptTextFile Read(ScriptRoot root, string rawPath)
        {
            RequireRootFolder(root);
            var full = ResolveFile(root, rawPath, out _);
            if (!DesignFiles.Exists(full)) throw new WebAdminException(404, "File not found.");

            var bytes = ReadAllBytesShared(full);
            var decoded = ScriptText.Decode(bytes);

            return new ScriptTextFile
            {
                Content = decoded.Content,
                Encoding = decoded.Encoding,
                LineEnding = decoded.LineEnding,
                MixedLineEndings = decoded.Mixed,
                Etag = ScriptText.ComputeEtag(bytes),
                Size = bytes.Length,
                MtimeUtc = DesignFiles.GetLastWriteTimeUtc(full),
            };
        }

        /// <summary>Reads a file's text for validation helpers (null when missing / unreadable / too large).</summary>
        public static string[] TryReadLines(string fullPath)
        {
            try
            {
                if (fullPath == null || !DesignFiles.Exists(fullPath)) return null;
                if (DesignFiles.Length(fullPath) > MaxFileBytes) return null;
                var decoded = ScriptText.Decode(ReadAllBytesShared(fullPath));
                return decoded.Content.Split('\n');
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static byte[] ReadAllBytesShared(string path)
        {
            if (DesignFiles.TryMap(path, out _, out _))
            {
                var stored = DesignFiles.ReadAllBytes(path); // roadmap P7: rendered from SystemData.db in the file's own layout
                if (stored.Length > MaxFileBytes) throw new WebAdminException(413, "The file is larger than " + (MaxFileBytes / (1024 * 1024)) + " MB and cannot be edited here.");
                return stored;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxFileBytes) throw new WebAdminException(413, "The file is larger than " + (MaxFileBytes / (1024 * 1024)) + " MB and cannot be edited here.");
                var buffer = new byte[stream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset != buffer.Length) Array.Resize(ref buffer, offset);
                return buffer;
            }
        }

        // ------------------------------------------------------------------ write

        /// <summary>
        /// Saves new editor text over an existing file. 409 when <paramref name="etag"/> does not match the file on disk (unless
        /// <paramref name="force"/>). Keeps the file's encoding and line ending, backs the previous version up first, writes atomically.
        /// </summary>
        public ScriptSaveResult Save(ScriptRoot root, string rawPath, string content, string etag, bool force)
        {
            RequireRootFolder(root);
            var full = ResolveFile(root, rawPath, out var normalized);

            lock (_writeLock)
            {
                if (!DesignFiles.Exists(full)) throw new WebAdminException(404, "File not found (it may have been deleted or renamed). Use Create to make a new file.");

                var current = ReadAllBytesShared(full);
                var currentEtag = ScriptText.ComputeEtag(current);

                if (!force && !string.Equals(etag ?? string.Empty, currentEtag, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WebAdminException(409, "The file changed on disk since you opened it. Reload it, or overwrite it anyway.",
                        new { etag = currentEtag, mtimeUtc = DesignFiles.GetLastWriteTimeUtc(full) });
                }

                var decoded = ScriptText.Decode(current);
                var bytes = ScriptText.Encode(content, decoded.Encoding, decoded.LineEnding);

                if (BytesEqual(bytes, current))
                {
                    return new ScriptSaveResult
                    {
                        Path = normalized,
                        Etag = currentEtag,
                        Size = current.Length,
                        MtimeUtc = DesignFiles.GetLastWriteTimeUtc(full),
                        Unchanged = true,
                        Encoding = decoded.Encoding,
                        LineEnding = decoded.LineEnding,
                    };
                }

                var backup = BackupBytes(root, normalized, current);
                WriteAtomic(full, bytes);

                return new ScriptSaveResult
                {
                    Path = normalized,
                    Etag = ScriptText.ComputeEtag(bytes),
                    Size = bytes.Length,
                    MtimeUtc = DesignFiles.GetLastWriteTimeUtc(full),
                    Backup = backup,
                    Encoding = decoded.Encoding,
                    LineEnding = decoded.LineEnding,
                };
            }
        }

        /// <summary>Creates a new (UTF-8, CRLF) .txt file. 409 when it already exists. Missing subfolders are created.</summary>
        public ScriptSaveResult Create(ScriptRoot root, string rawPath, string content = null)
        {
            RequireRootFolder(root);
            var full = ResolveFile(root, rawPath, out var normalized);

            lock (_writeLock)
            {
                if (DesignFiles.Exists(full)) throw new WebAdminException(409, "A file with that name already exists.");
                if (System.IO.Directory.Exists(full)) throw new WebAdminException(409, "A folder with that name already exists.");

                if (!DesignFiles.TryMap(full, out _, out _))
                {
                    var dir = System.IO.Path.GetDirectoryName(full);
                    System.IO.Directory.CreateDirectory(dir);
                    if (ScriptPaths.CrossesReparsePoint(root.Directory, full))
                        throw new WebAdminException(400, "The path goes through a symbolic link or junction, which is not allowed.");
                }

                var bytes = ScriptText.Encode(content ?? string.Empty, ScriptText.Utf8, ScriptText.Crlf);
                WriteAtomic(full, bytes);

                return new ScriptSaveResult
                {
                    Path = normalized,
                    Etag = ScriptText.ComputeEtag(bytes),
                    Size = bytes.Length,
                    MtimeUtc = DesignFiles.GetLastWriteTimeUtc(full),
                    Encoding = ScriptText.Utf8,
                    LineEnding = ScriptText.Crlf,
                };
            }
        }

        /// <summary>Deletes a file by moving it into the backup folder (restorable). Returns the backup name.</summary>
        public string Delete(ScriptRoot root, string rawPath)
        {
            RequireRootFolder(root);
            var full = ResolveFile(root, rawPath, out var normalized);

            lock (_writeLock)
            {
                if (!DesignFiles.Exists(full)) throw new WebAdminException(404, "File not found.");

                var target = NewBackupPath(root, normalized);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                if (DesignFiles.TryMap(full, out _, out _))
                {
                    WriteAtomic(target, DesignFiles.ReadAllBytes(full));
                    DesignFiles.Delete(full);
                }
                else File.Move(full, target);
                return System.IO.Path.GetFileName(target);
            }
        }

        /// <summary>Renames / moves a file inside the same root. 409 when the destination exists.</summary>
        public string Rename(ScriptRoot root, string rawPath, string rawNewPath)
        {
            RequireRootFolder(root);
            var full = ResolveFile(root, rawPath, out _);
            var target = ResolveFile(root, rawNewPath, out var newNormalized);

            lock (_writeLock)
            {
                if (!DesignFiles.Exists(full)) throw new WebAdminException(404, "File not found.");

                var sameFile = string.Equals(full, target, StringComparison.OrdinalIgnoreCase);
                if (string.Equals(full, target, StringComparison.Ordinal)) throw new WebAdminException(400, "The new name is the same as the old one.");
                if (!sameFile && (DesignFiles.Exists(target) || System.IO.Directory.Exists(target))) throw new WebAdminException(409, "A file with the new name already exists.");

                if (!DesignFiles.TryMap(target, out _, out _))
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    if (ScriptPaths.CrossesReparsePoint(root.Directory, target))
                        throw new WebAdminException(400, "The path goes through a symbolic link or junction, which is not allowed.");
                }

                DesignFiles.Move(full, target);
                return newNormalized;
            }
        }

        // ------------------------------------------------------------------ backups

        /// <summary>Folder holding the backups of one file.</summary>
        public string BackupFolder(ScriptRoot root, string normalized)
        {
            var rootBackup = System.IO.Path.Combine(BackupBase, root.Id);
            var dir = System.IO.Path.GetDirectoryName(normalized.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(dir) ? rootBackup : System.IO.Path.Combine(rootBackup, dir);
        }

        public static Regex BackupNamePattern(string fileName)
        {
            return new Regex("^" + Regex.Escape(fileName) + @"\.(\d{8}-\d{6})(?:-(\d+))?\.bak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>True when <paramref name="backupName"/> is a backup of <paramref name="fileName"/>; returns its stamp.</summary>
        public static bool TryParseBackupName(string fileName, string backupName, out DateTime stamp)
        {
            stamp = default;
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(backupName)) return false;
            var match = BackupNamePattern(fileName).Match(backupName);
            if (!match.Success) return false;
            return DateTime.TryParseExact(match.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
        }

        private string NewBackupPath(ScriptRoot root, string normalized)
        {
            var folder = BackupFolder(root, normalized);
            var fileName = System.IO.Path.GetFileName(normalized);
            var stamp = _now().ToString(StampFormat, CultureInfo.InvariantCulture);
            var candidate = System.IO.Path.Combine(folder, fileName + "." + stamp + ".bak");

            for (var n = 2; File.Exists(candidate); n++)
                candidate = System.IO.Path.Combine(folder, fileName + "." + stamp + "-" + n.ToString(CultureInfo.InvariantCulture) + ".bak");

            return candidate;
        }

        private string BackupBytes(ScriptRoot root, string normalized, byte[] bytes)
        {
            var target = NewBackupPath(root, normalized);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            WriteAtomic(target, bytes);
            return System.IO.Path.GetFileName(target);
        }

        /// <summary>Backs up the current content of a file (for multi-file tools). Returns the backup name.</summary>
        public string Backup(ScriptRoot root, string normalized, byte[] bytes)
        {
            lock (_writeLock)
            {
                return BackupBytes(root, normalized, bytes);
            }
        }

        public List<ScriptBackupEntry> ListBackups(ScriptRoot root, string rawPath)
        {
            ResolveFile(root, rawPath, out var normalized);
            var folder = BackupFolder(root, normalized);
            var fileName = System.IO.Path.GetFileName(normalized);
            var result = new List<ScriptBackupEntry>();

            if (!System.IO.Directory.Exists(folder)) return result;

            foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.bak", SearchOption.TopDirectoryOnly))
            {
                var name = System.IO.Path.GetFileName(file);
                if (!TryParseBackupName(fileName, name, out var stamp)) continue;

                try
                {
                    var info = new FileInfo(file);
                    result.Add(new ScriptBackupEntry { Name = name, Size = info.Length, MtimeUtc = info.LastWriteTimeUtc, Stamp = stamp });
                }
                catch (IOException)
                {
                }
            }

            result.Sort((a, b) =>
            {
                var c = b.MtimeUtc.CompareTo(a.MtimeUtc);
                return c != 0 ? c : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private string ResolveBackup(ScriptRoot root, string normalized, string backupName)
        {
            var fileName = System.IO.Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(backupName) || !TryParseBackupName(fileName, backupName.Trim(), out _))
                throw new WebAdminException(400, "That is not a backup of this file.");

            var path = System.IO.Path.Combine(BackupFolder(root, normalized), backupName.Trim());
            if (!File.Exists(path)) throw new WebAdminException(404, "Backup not found.");
            return path;
        }

        public ScriptTextFile ReadBackup(ScriptRoot root, string rawPath, string backupName)
        {
            ResolveFile(root, rawPath, out var normalized);
            var path = ResolveBackup(root, normalized, backupName);
            var bytes = ReadAllBytesShared(path);
            var decoded = ScriptText.Decode(bytes);

            return new ScriptTextFile
            {
                Content = decoded.Content,
                Encoding = decoded.Encoding,
                LineEnding = decoded.LineEnding,
                MixedLineEndings = decoded.Mixed,
                Etag = ScriptText.ComputeEtag(bytes),
                Size = bytes.Length,
                MtimeUtc = File.GetLastWriteTimeUtc(path),
            };
        }

        /// <summary>
        /// Restores a backup over the file (re-creating it when it was deleted). The current version is backed up first.
        /// Returns the new state; <c>Backup</c> is the name of the backup taken of the replaced version (null when none).
        /// </summary>
        public ScriptSaveResult Restore(ScriptRoot root, string rawPath, string backupName)
        {
            RequireRootFolder(root);
            var full = ResolveFile(root, rawPath, out var normalized);

            lock (_writeLock)
            {
                var source = ResolveBackup(root, normalized, backupName);
                var bytes = ReadAllBytesShared(source);
                string previousBackup = null;

                if (DesignFiles.Exists(full))
                {
                    var current = ReadAllBytesShared(full);
                    if (!BytesEqual(current, bytes))
                        previousBackup = BackupBytes(root, normalized, current);
                }
                else if (!DesignFiles.TryMap(full, out _, out _))
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
                    if (ScriptPaths.CrossesReparsePoint(root.Directory, full))
                        throw new WebAdminException(400, "The path goes through a symbolic link or junction, which is not allowed.");
                }

                WriteAtomic(full, bytes);
                var decoded = ScriptText.Decode(bytes);

                return new ScriptSaveResult
                {
                    Path = normalized,
                    Etag = ScriptText.ComputeEtag(bytes),
                    Size = bytes.Length,
                    MtimeUtc = DesignFiles.GetLastWriteTimeUtc(full),
                    Backup = previousBackup,
                    Encoding = decoded.Encoding,
                    LineEnding = decoded.LineEnding,
                };
            }
        }

        // ------------------------------------------------------------------ search

        /// <summary>Searches every .txt file of the root line by line (literal case-insensitive, or a .NET regex).</summary>
        public ScriptSearchResult Search(ScriptRoot root, string query, bool regex, int maxHits = MaxSearchHits)
        {
            if (string.IsNullOrEmpty(query)) throw new WebAdminException(400, "Enter something to search for.");
            if (query.Length > 500) throw new WebAdminException(400, "The search text is too long.");
            RequireRootFolder(root);

            maxHits = Math.Clamp(maxHits, 1, MaxSearchHits);
            Regex pattern = null;

            if (regex)
            {
                try
                {
                    pattern = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException)
                {
                    throw new WebAdminException(400, "That is not a valid regular expression.");
                }
            }

            var result = new ScriptSearchResult();

            foreach (var file in EnumerateTextFiles(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string[] lines;
                try
                {
                    if (DesignFiles.Length(file) > MaxFileBytes)
                    {
                        result.FilesSkipped++;
                        continue;
                    }
                    lines = ScriptText.Decode(ReadAllBytesShared(file)).Content.Split('\n');
                }
                catch (Exception)
                {
                    result.FilesSkipped++;
                    continue;
                }

                result.FilesScanned++;
                var rel = RelativeOf(root, file);

                for (var i = 0; i < lines.Length; i++)
                {
                    int column;
                    if (pattern != null)
                    {
                        Match m;
                        try
                        {
                            m = pattern.Match(lines[i]);
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            throw new WebAdminException(400, "The regular expression is too slow; simplify it.");
                        }
                        if (!m.Success) continue;
                        column = m.Index + 1;
                    }
                    else
                    {
                        var idx = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        column = idx + 1;
                    }

                    if (result.Hits.Count >= maxHits)
                    {
                        result.Truncated = true;
                        return result;
                    }

                    var text = lines[i].TrimEnd();
                    if (text.Length > 300) text = text.Substring(0, 300) + "…";
                    result.Hits.Add(new ScriptSearchHit { Path = rel, Line = i + 1, Column = column, Text = text });
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ helpers

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            return new ReadOnlySpan<byte>(a).SequenceEqual(new ReadOnlySpan<byte>(b));
        }

        /// <summary>Writes to a temp file in the same folder, then swaps it in (File.Replace / Move) so readers never see half a file.</summary>
        public static void WriteAtomic(string path, byte[] bytes)
        {
            if (DesignFiles.TryMap(path, out _, out _))
            {
                DesignFiles.WriteAllBytes(path, bytes); // roadmap P7: one transaction in SystemData.db, then the cache
                return;
            }

            var dir = System.IO.Path.GetDirectoryName(path);
            var temp = System.IO.Path.Combine(dir, "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null, true);
                    }
                    catch (IOException)
                    {
                        File.Copy(temp, path, true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        File.Copy(temp, path, true);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception)
                {
                    // best effort
                }
            }
        }

        /// <summary>The refusal for edits of the legacy quest bodies (Scripts editor, quest reload) since the quest V2 ship.</summary>
        public const string LegacyQuestsReadOnlyMessage =
            "Legacy quest files are read only since the quest V2 ship: edit quests in the Quests page (Game Data › Quests). Restoring a backup still works here; the files only feed the legacy re-import.";

        /// <summary>
        /// Which live reload applies a saved file: "npcs", "drops", "linemessages", "recipes" or null (legacy quest files are read only and
        /// no longer reloaded since the quest V2 ship).
        /// </summary>
        public static string ReloadKindFor(string rootId, string normalized)
        {
            switch ((rootId ?? string.Empty).ToLowerInvariant())
            {
                case "npcs":
                case "systemscripts":
                    return "npcs";
                case "drops":
                    return "drops";
                case "recipes":
                    return "recipes";
                case "envir":
                    return string.Equals(normalized, "LineMessage.txt", StringComparison.OrdinalIgnoreCase) ? "linemessages" : null;
                default:
                    return null;
            }
        }

        /// <summary>A short note on when an edit to this file takes effect.</summary>
        public static string EffectNote(string rootId, string normalized)
        {
            switch ((rootId ?? string.Empty).ToLowerInvariant())
            {
                case "npcs":
                case "systemscripts":
                    return "Reload NPCs to apply (NPC objects and their locations come from the database and need a reboot).";
                case "drops":
                    return "Reload Drops to apply.";
                case "recipes":
                    return "Reload Recipes to apply.";
                case "quests":
                    return "Read only: quests are edited in the Quests page, which applies them to the running world. These files only feed the legacy re-import.";
                case "routes":
                    return "Routes are read when maps are created: reboot the world to apply.";
                case "values":
                    return "Value files are read by NPC scripts when used: changes apply immediately.";
                case "envir":
                    if (string.Equals(normalized, "LineMessage.txt", StringComparison.OrdinalIgnoreCase)) return "Reload Line messages to apply.";
                    if (string.Equals(normalized, "DisabledChars.txt", StringComparison.OrdinalIgnoreCase)) return "Read when the world starts: reboot to apply.";
                    if (string.Equals(normalized, "Notice.txt", StringComparison.OrdinalIgnoreCase)) return "Read when the server program starts.";
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>The validator family for a root: "drops", "npc", "quest", "recipe" or null.</summary>
        public static string ValidatorFor(string rootId)
        {
            switch ((rootId ?? string.Empty).ToLowerInvariant())
            {
                case "drops": return "drops";
                case "npcs":
                case "systemscripts": return "npc";
                case "quests": return "quest";
                case "recipes": return "recipe";
                default: return null;
            }
        }
    }
}
