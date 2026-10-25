using System.Text;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// File-shaped access to the text design data (roadmap P7 phase 7.4): the game's loaders and the editors call these instead of
    /// <c>File.*</c> for paths under the drop, recipe and route folders. While the store is active such paths are served by
    /// <see cref="DesignTexts"/> (the cache, so the game thread never queries the store); any other path, or every path while the store
    /// isn't active (first start before the import, tests and tools on Server.MirDB), goes to the disk as before.
    /// Names compare case-insensitively, like Windows paths.
    /// </summary>
    internal static class DesignFiles
    {
        /// <summary>The format and file name (relative to its folder, '/'-separated) of a path under a design folder, while the store is active.</summary>
        public static bool TryMap(string path, out DesignTextFormat format, out string name)
        {
            format = null;
            name = null;
            if (string.IsNullOrEmpty(path) || !DesignStore.Active) return false;
            var full = Path.GetFullPath(path);
            foreach (var f in DesignTexts.Formats)
            {
                var root = Path.GetFullPath(f.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                name = full.Substring(root.Length).Replace('\\', '/');
                if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) { name = null; return false; } // only *.txt files are design files
                format = f;
                return true;
            }

            return false;
        }

        /// <summary>True when <paramref name="path"/> is (or is inside) a design folder while the store is active.</summary>
        public static bool IsDesignFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !DesignStore.Active) return false;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return DesignTexts.Formats.Any(f => full.StartsWith(Path.GetFullPath(f.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        public static bool Exists(string path) => TryMap(path, out var f, out var n) ? DesignTexts.Exists(f, n) : File.Exists(path);

        /// <summary>As File.ReadAllLines (throws FileNotFoundException for a missing file).</summary>
        public static string[] ReadAllLines(string path)
        {
            if (!TryMap(path, out var f, out var n)) return File.ReadAllLines(path);
            return DesignTexts.Read(f, n)?.ToArray() ?? throw new FileNotFoundException("[Store] " + n + " is not in the " + f.Kind + " store", path);
        }

        /// <summary>As File.ReadAllBytes: a store file comes back in its own layout (line ending, final newline, BOM; UTF-8).</summary>
        public static byte[] ReadAllBytes(string path)
        {
            if (!TryMap(path, out var f, out var n)) return File.ReadAllBytes(path);
            return DesignTexts.ReadBytes(f, n) ?? throw new FileNotFoundException("[Store] " + n + " is not in the " + f.Kind + " store", path);
        }

        /// <summary>As File.WriteAllBytes: a store file keeps the layout of <paramref name="bytes"/>. Returns the unresolved line count.</summary>
        public static int WriteAllBytes(string path, byte[] bytes)
        {
            if (!TryMap(path, out var f, out var n))
            {
                File.WriteAllBytes(path, bytes);
                return 0;
            }

            var (lines, layout) = TextLayout.Decode(bytes);
            return DesignTexts.Write(f, n, lines, layout);
        }

        public static string ReadAllText(string path)
        {
            if (!TryMap(path, out _, out _)) return File.ReadAllText(path);
            var bytes = ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        /// <summary>As File.WriteAllLines; a store write returns the number of lines naming something that doesn't exist (kept as text).</summary>
        public static int WriteAllLines(string path, IEnumerable<string> lines)
        {
            if (!TryMap(path, out var f, out var n))
            {
                File.WriteAllLines(path, lines);
                return 0;
            }

            return DesignTexts.Write(f, n, lines.ToList());
        }

        /// <summary>As File.WriteAllText, split into lines the way File.ReadAllLines would read the file back.</summary>
        public static int WriteAllText(string path, string text)
        {
            if (!TryMap(path, out _, out _))
            {
                File.WriteAllText(path, text);
                return 0;
            }

            return WriteAllLines(path, SplitLines(text));
        }

        public static void Delete(string path)
        {
            if (TryMap(path, out var f, out var n)) DesignTexts.Delete(f, n);
            else File.Delete(path);
        }

        public static void Move(string source, string target)
        {
            if (!TryMap(source, out var f, out var n))
            {
                File.Move(source, target);
                return;
            }

            if (!TryMap(target, out var tf, out var tn) || tf != f) throw new IOException("[Store] can't move " + n + " out of the " + f.Kind + " store");
            if (!DesignTexts.Exists(f, n)) throw new FileNotFoundException("[Store] " + n + " is not in the " + f.Kind + " store", source);
            if (!string.Equals(n, tn, StringComparison.OrdinalIgnoreCase) && DesignTexts.Exists(f, tn)) throw new IOException("[Store] " + tn + " already exists");
            DesignTexts.Rename(f, n, tn); // one transaction; also a case-only rename
        }

        public static DateTime GetLastWriteTimeUtc(string path) =>
            TryMap(path, out var f, out var n) ? DesignTexts.UpdatedUtc(f, n) ?? DateTime.MinValue : File.GetLastWriteTimeUtc(path);

        /// <summary>The file's size in bytes (a store file in its own layout).</summary>
        public static long Length(string path) => TryMap(path, out _, out _) ? ReadAllBytes(path).Length : new FileInfo(path).Length;

        /// <summary>As Directory.Exists; a design folder exists while the store is active.</summary>
        public static bool DirectoryExists(string directory) => Directory.Exists(directory) || IsDesignFolder(directory);

        public static string[] GetFiles(string directory, string pattern) => EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray();

        public static string[] GetFiles(string directory, string pattern, SearchOption option) => EnumerateFiles(directory, pattern, option).ToArray();

        /// <summary>As Directory.EnumerateFiles for "*" / "*.ext" patterns. Design folders list the store's files (full paths).</summary>
        public static IEnumerable<string> EnumerateFiles(string directory, string pattern, SearchOption option)
        {
            if (!IsDesignFolder(directory))
                return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, option) : Enumerable.Empty<string>();

            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            var f = DesignTexts.Formats.First(x => (full + Path.DirectorySeparatorChar).StartsWith(Path.GetFullPath(x.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var root = Path.GetFullPath(f.Root).TrimEnd(Path.DirectorySeparatorChar);
            var prefix = full.Length > root.Length ? full.Substring(root.Length + 1).Replace('\\', '/') + "/" : string.Empty;
            var ext = pattern == "*" || pattern == "*.*" ? null : pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern.Substring(1) : throw new NotSupportedException("[Store] pattern " + pattern);

            return DesignTexts.List(f)
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(n => option == SearchOption.AllDirectories || n.IndexOf('/', prefix.Length) < 0)
                .Where(n => ext == null || n.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                .Select(n => Path.Combine(root, n.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
        }

        /// <summary>Lines as File.ReadAllLines reads them (CRLF, LF or CR; no trailing empty line for a final newline).</summary>
        public static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            using var reader = new StringReader(text ?? string.Empty);
            string line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
            return lines;
        }
    }
}
