using System.Text;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Name lists (B5): plain text files under <c>Settings.NameListPath</c> (Envir\NameLists, one name per line) used by NPC scripts
    /// (CHECKNAMELIST / ADDNAMELIST / DELNAMELIST …). Mirrors Server.MirForms Account\Namelists.cs, but also shows extensionless
    /// lists, because scripts pass the path verbatim (<c>CHECKNAMELIST NewbieGuild</c> reads the file "NewbieGuild", not ".txt").
    /// <code>
    /// GET  /api/admin/namelists                 → {lists:[{path, count, size, mtimeUtc, tooLarge}], totals:{lists, players, unique}}
    /// GET  /api/admin/namelists/file?path=      → {path, names[], untrimmed[], count, size, mtimeUtc}
    /// GET  /api/admin/namelists/find?name=&amp;exact= → {name, lists:[{path, matches[]}]}
    /// POST /api/admin/namelists/create {path, extensionless?}
    /// POST /api/admin/namelists/delete {path}   (moved to Back Up\WebAdmin\NameLists)
    /// POST /api/admin/namelists/add {path, name}   (also repairs a whitespace-padded line that CHECKNAMELIST would not match)
    /// POST /api/admin/namelists/remove {path, name}
    /// </code>
    /// Reads take no lock (writes replace files atomically and readers open with FileShare.ReadWrite|Delete), so a slow scan can
    /// never hold up the game thread. Writes are serialised with NPC script access by running on the game thread while the world
    /// runs; FileLock only orders those against inline writes made while the world is not running.
    /// </summary>
    public sealed class NamelistRoutes : IWebAdminRoutes
    {
        private static readonly object FileLock = new object();

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/namelists", ListAll);
            router.Get("/api/admin/namelists/file", GetFile);
            router.Get("/api/admin/namelists/find", Find);
            router.Post("/api/admin/namelists/create", Create);
            router.Post("/api/admin/namelists/delete", Delete);
            router.Post("/api/admin/namelists/add", AddName);
            router.Post("/api/admin/namelists/remove", RemoveName);
        }

        private static NamelistStore Store()
        {
            var root = Path.GetFullPath(Settings.NameListPath);
            var backup = Path.GetFullPath(Path.Combine(".", "Back Up", "WebAdmin", "NameLists"));
            return new NamelistStore(root, backup);
        }

        /// <summary>Runs file work, mapping IO failures to 409 (exception text is only logged). Takes no lock.</summary>
        private static T RunIo<T>(Func<T> fn)
        {
            try
            {
                return fn();
            }
            catch (IOException ex)
            {
                WebAdminLog.Error(ex, "Name list file operation failed");
                throw new WebAdminException(409, "The file is in use or could not be written; try again");
            }
            catch (UnauthorizedAccessException ex)
            {
                WebAdminLog.Error(ex, "Name list file operation denied");
                throw new WebAdminException(409, "Access to the file was denied");
            }
        }

        private static T RunWriteLocked<T>(Func<T> fn)
        {
            lock (FileLock)
            {
                return RunIo(fn);
            }
        }

        /// <summary>
        /// Writes: on the game thread while the world runs (NPC scripts read/append these files there, so this serialises with
        /// them), else inline. Only writes take FileLock and each write touches a single file, so the game thread never waits on
        /// a web-thread scan (reads are lock-free).
        /// </summary>
        private static async Task<T> RunSerializedAsync<T>(Func<T> fn)
        {
            Func<T> locked = () => RunWriteLocked(fn);

            if (WebAdminGame.IsRunning)
            {
                try
                {
                    return await WebAdminGame.RunAsync(locked).ConfigureAwait(false);
                }
                catch (WorldNotRunningException)
                {
                    // world stopping/starting: plain file work is still safe inline
                }
            }

            return locked();
        }

        private static async Task ListAll(WebAdminRequest req)
        {
            var store = Store();
            var result = RunIo(() => store.List());

            await req.Json(new
            {
                root = store.Root,
                exists = Directory.Exists(store.Root),
                lists = result.Lists,
                totals = new { lists = result.Lists.Count, players = result.TotalNames, unique = result.UniqueNames },
            }).ConfigureAwait(false);
        }

        private static async Task GetFile(WebAdminRequest req)
        {
            var store = Store();
            var path = store.Normalise(req.Query["path"]);
            var file = RunIo(() => store.Read(path));
            await req.Json(file).ConfigureAwait(false);
        }

        private static async Task Find(WebAdminRequest req)
        {
            var name = (req.Query["name"] ?? string.Empty).Trim();
            if (name.Length == 0) throw new WebAdminException(400, "name is required");

            var exact = req.QueryBool("exact");
            var store = Store();
            var hits = RunIo(() => store.Find(name, exact));

            await req.Json(new { name, exact, lists = hits }).ConfigureAwait(false);
        }

        private static async Task Create(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var store = Store();
            var path = store.Normalise(body.GetString("path"), body.GetBool("extensionless"));

            await RunSerializedAsync(() =>
            {
                store.Create(path);
                return true;
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, "namelist.create", path);
            await req.Ok(new { path, reloadHint = true },
                "Created name list " + path + ". NPC scripts loaded while it was missing skip its check until NPCs are reloaded.").ConfigureAwait(false);
        }

        private static async Task Delete(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var store = Store();
            var path = store.Normalise(body.GetString("path"));

            var deleted = await RunSerializedAsync(() => store.Delete(path)).ConfigureAwait(false);

            WebAdminAudit.Record(req, "namelist.delete", path, new { names = deleted.Count, backup = deleted.BackupPath });
            await req.Ok(new { path, names = deleted.Count }, "Deleted name list " + path + " (" + deleted.Count + " name(s), backup kept).").ConfigureAwait(false);
        }

        private static async Task AddName(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var store = Store();
            var path = store.Normalise(body.GetString("path"));
            var name = NamelistStore.ValidateName(body.GetString("name"));

            var outcome = await RunSerializedAsync(() => store.AddName(path, name)).ConfigureAwait(false);
            var added = outcome == NamelistStore.AddOutcome.Added;
            var repaired = outcome == NamelistStore.AddOutcome.Repaired;

            if (added || repaired) WebAdminAudit.Record(req, repaired ? "namelist.repair" : "namelist.add", path, new { name });

            var message = added ? "Added " + name + " to " + path + "."
                : repaired ? "Fixed the spacing around " + name + " in " + path + " so CHECKNAMELIST matches it."
                : name + " is already in " + path + ".";
            await req.Ok(new { path, name, added, repaired }, message).ConfigureAwait(false);
        }

        private static async Task RemoveName(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var store = Store();
            var path = store.Normalise(body.GetString("path"));
            var name = (body.GetString("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw new WebAdminException(400, "name is required");

            var removed = await RunSerializedAsync(() => store.Remove(path, name)).ConfigureAwait(false);
            if (removed == 0) throw new WebAdminException(404, name + " is not in " + path);

            WebAdminAudit.Record(req, "namelist.remove", path, new { name, lines = removed });
            await req.Ok(new { path, name, removed }, "Removed " + name + " from " + path + ".").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Path-confined name-list file store (pure file system; unit-tested against a temp folder).
    /// Paths are relative to <see cref="Root"/> and '/'-separated. A list is a ".txt" file or an extensionless file (NPC scripts use
    /// the path verbatim). A client path without an extension resolves to an existing extensionless file, else to "&lt;path&gt;.txt"
    /// (unless the caller asks for an extensionless list explicitly).
    /// </summary>
    public sealed class NamelistStore
    {
        public const int MaxPathLength = 180;
        public const int MaxNameLength = 50;
        public const int MaxFileBytes = 4 * 1024 * 1024;

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public NamelistStore(string root, string backupRoot)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root is required", nameof(root));

            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            BackupRoot = string.IsNullOrWhiteSpace(backupRoot) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupRoot));
        }

        public string Root { get; }

        public string BackupRoot { get; }

        public enum AddOutcome
        {
            AlreadyPresent,
            Added,
            /// <summary>The name was only present with surrounding whitespace (which CHECKNAMELIST does not match); the line was rewritten.</summary>
            Repaired,
        }

        public sealed class ListEntry
        {
            public string Path { get; set; }
            public int Count { get; set; }
            public long Size { get; set; }
            public DateTime MtimeUtc { get; set; }
            /// <summary>Over <see cref="MaxFileBytes"/>: not read (Count is 0) and left out of the totals and Find.</summary>
            public bool TooLarge { get; set; }
        }

        public sealed class ListResult
        {
            public List<ListEntry> Lists { get; set; } = new List<ListEntry>();
            public int TotalNames { get; set; }
            public int UniqueNames { get; set; }
        }

        public sealed class FileResult
        {
            public string Path { get; set; }
            public List<string> Names { get; set; } = new List<string>();
            /// <summary>Names whose line has leading/trailing whitespace: CHECKNAMELIST compares raw lines, so these never match.</summary>
            public List<string> Untrimmed { get; set; } = new List<string>();
            public int Count { get; set; }
            public long Size { get; set; }
            public DateTime MtimeUtc { get; set; }
        }

        public sealed class FindHit
        {
            public string Path { get; set; }
            public List<string> Matches { get; set; } = new List<string>();
        }

        public sealed class DeleteResult
        {
            public int Count { get; set; }
            public string BackupPath { get; set; }
        }

        // ------------------------------------------------------------------ path rules

        /// <summary>
        /// Validates a client path and returns the canonical relative form ("Sub/List.txt" or "Sub/List"). Without an extension
        /// the path resolves to an existing extensionless list, else to ".txt" — or stays extensionless when
        /// <paramref name="extensionless"/> is set (creating a list that scripts reference without ".txt").
        /// </summary>
        public string Normalise(string path, bool extensionless = false)
        {
            var bare = NormaliseRelative(path, false);
            var rel = bare;

            if (string.IsNullOrEmpty(System.IO.Path.GetExtension(bare)) && !extensionless && !File.Exists(FullPathOf(bare)))
                rel = bare + ".txt";

            if (rel.Length > MaxPathLength) throw new WebAdminException(400, "Name list path is too long");
            FullPathOf(rel); // confinement + symlink check
            return rel;
        }

        /// <summary>
        /// Canonical relative form. 400 on anything that could escape the root: empty, rooted, drive/ADS ':', "." / ".." segments,
        /// invalid characters, reserved device names, trailing dot/space, an extension other than .txt, or over
        /// <see cref="MaxPathLength"/> characters. ".txt" is appended to an extensionless path when <paramref name="appendTxt"/>.
        /// </summary>
        public static string NormaliseRelative(string path, bool appendTxt = true)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new WebAdminException(400, "path is required");

            var p = path.Trim().Replace('\\', '/');
            if (p.StartsWith("/", StringComparison.Ordinal) || p.Contains(':') || p.IndexOf('\0') >= 0)
                throw new WebAdminException(400, "Invalid name list path");

            var segments = p.Split('/');
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var clean = new List<string>(segments.Length);

            for (var i = 0; i < segments.Length; i++)
            {
                var s = segments[i];
                if (s.Length == 0 || s == "." || s == "..") throw new WebAdminException(400, "Invalid name list path");
                if (s.IndexOfAny(invalid) >= 0) throw new WebAdminException(400, "Invalid character in name list path");
                if (s.EndsWith(".", StringComparison.Ordinal) || s.EndsWith(" ", StringComparison.Ordinal) || s.StartsWith(" ", StringComparison.Ordinal))
                    throw new WebAdminException(400, "Name list path segments cannot start/end with a space or end with a dot");

                var stem = s;
                var dot = stem.IndexOf('.');
                if (dot >= 0) stem = stem.Substring(0, dot);
                if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) throw new WebAdminException(400, "Reserved device name in path");

                clean.Add(s);
            }

            var last = clean[clean.Count - 1];
            var ext = System.IO.Path.GetExtension(last);
            if (string.IsNullOrEmpty(ext))
            {
                if (appendTxt) clean[clean.Count - 1] = last + ".txt";
            }
            else if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                throw new WebAdminException(400, "Name lists must be .txt or extensionless files");
            }

            var result = string.Join("/", clean);
            if (result.Length > MaxPathLength) throw new WebAdminException(400, "Name list path is too long");
            return result;
        }

        /// <summary>Full path of a canonical relative path; 400 when it resolves outside the root or through a reparse point.</summary>
        public string FullPathOf(string rel)
        {
            var full = Path.GetFullPath(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new WebAdminException(400, "Path escapes the name list folder");

            // Refuse symlinks / junctions anywhere between the root and the file.
            var current = Path.GetDirectoryName(full);
            while (current != null && current.Length > Root.Length)
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new WebAdminException(400, "Path goes through a link");
                current = Path.GetDirectoryName(current);
            }

            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new WebAdminException(400, "Path is a link");

            return full;
        }

        public string RelativeOf(string full)
        {
            return Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>Trimmed name; 400/422 when empty, too long or containing control characters / path separators.</summary>
        public static string ValidateName(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) throw new WebAdminException(400, "name is required");
            if (name.Length > MaxNameLength) throw new WebAdminException(422, "Names are limited to " + MaxNameLength + " characters");
            if (name.Any(char.IsControl)) throw new WebAdminException(422, "Names cannot contain control characters or line breaks");
            return name;
        }

        /// <summary>True for files the store treats as name lists: "*.txt" or no extension at all.</summary>
        public static bool IsListFile(string fileName)
        {
            var ext = System.IO.Path.GetExtension(fileName);
            return string.IsNullOrEmpty(ext) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ operations

        private IEnumerable<string> ListFiles()
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            return Directory.EnumerateFiles(Root, "*", options).Where(f => IsListFile(Path.GetFileName(f)));
        }

        /// <summary>
        /// Every list with its name count. A file over <see cref="MaxFileBytes"/> is listed with TooLarge (not read); a file that
        /// vanishes or is locked mid-scan is skipped, so one bad file never fails the whole listing.
        /// </summary>
        public ListResult List()
        {
            var result = new ListResult();
            if (!Directory.Exists(Root)) return result;

            var unique = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in ListFiles())
            {
                ListEntry entry;
                List<string> names = null;

                try
                {
                    var fi = new FileInfo(file);
                    entry = new ListEntry
                    {
                        Path = RelativeOf(file),
                        Size = fi.Length,
                        MtimeUtc = fi.LastWriteTimeUtc,
                        TooLarge = fi.Length > MaxFileBytes,
                    };

                    if (!entry.TooLarge)
                    {
                        names = ReadNames(file);
                        entry.Count = names.Count;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                result.Lists.Add(entry);

                if (names == null) continue;
                result.TotalNames += names.Count;
                foreach (var n in names) unique.Add(n);
            }

            result.Lists.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            result.UniqueNames = unique.Count;
            return result;
        }

        public FileResult Read(string rel)
        {
            var full = FullPathOf(rel);
            if (!File.Exists(full)) throw new WebAdminException(404, "Name list not found: " + rel);

            var fi = new FileInfo(full);
            var lines = ReadLines(full);
            var names = new List<string>();
            var untrimmed = new List<string>();

            foreach (var line in lines)
            {
                var n = line.Trim();
                if (n.Length == 0) continue;
                names.Add(n);
                if (n.Length != line.Length) untrimmed.Add(n);
            }

            return new FileResult
            {
                Path = rel,
                Names = names,
                Untrimmed = untrimmed,
                Count = names.Count,
                Size = fi.Length,
                MtimeUtc = fi.LastWriteTimeUtc,
            };
        }

        /// <summary>Lists containing the name (ordinal exact, or case-insensitive substring). Oversized or unreadable files are skipped.</summary>
        public List<FindHit> Find(string name, bool exact)
        {
            var hits = new List<FindHit>();
            if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(Root)) return hits;

            name = name.Trim();

            foreach (var file in ListFiles())
            {
                List<string> names;
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes) continue;
                    names = ReadNames(file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var matches = names.Where(n => exact
                        ? string.Equals(n, name, StringComparison.Ordinal)
                        : n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (matches.Count > 0) hits.Add(new FindHit { Path = RelativeOf(file), Matches = matches });
            }

            hits.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return hits;
        }

        public void Create(string rel)
        {
            var full = FullPathOf(rel);
            if (File.Exists(full)) throw new WebAdminException(409, "A name list called " + rel + " already exists");
            if (Directory.Exists(full)) throw new WebAdminException(409, "A folder called " + rel + " already exists");

            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
            }
        }

        public DeleteResult Delete(string rel)
        {
            var full = FullPathOf(rel);
            if (!File.Exists(full)) throw new WebAdminException(404, "Name list not found: " + rel);

            var count = ReadNames(full).Count;
            string backup = null;

            if (BackupRoot != null)
            {
                // Millisecond stamp plus a counter: never overwrite an earlier deleted copy of the same list.
                var stem = Path.Combine(BackupRoot, rel.Replace('/', Path.DirectorySeparatorChar) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(Path.GetDirectoryName(stem));

                backup = stem + ".deleted.bak";
                for (var n = 2; File.Exists(backup); n++) backup = stem + "-" + n + ".deleted.bak";
                File.Move(full, backup, false);
            }
            else
            {
                File.Delete(full);
            }

            return new DeleteResult { Count = count, BackupPath = backup };
        }

        /// <summary>True when the name was added or a whitespace-padded copy was repaired; false when it was already present.</summary>
        public bool Add(string rel, string name)
        {
            return AddName(rel, name) != AddOutcome.AlreadyPresent;
        }

        /// <summary>
        /// Appends the name unless a line equal to it exists (ordinal, raw line — as CHECKNAMELIST / ADDNAMELIST compare). When the
        /// name is present only with surrounding whitespace, that line is rewritten as the exact name (Repaired) instead.
        /// </summary>
        public AddOutcome AddName(string rel, string name)
        {
            name = ValidateName(name);
            var full = FullPathOf(rel);
            if (!File.Exists(full)) throw new WebAdminException(404, "Name list not found: " + rel);

            var lines = ReadLines(full);
            if (lines.Any(l => string.Equals(l, name, StringComparison.Ordinal))) return AddOutcome.AlreadyPresent;

            var padded = lines.FindIndex(l => string.Equals(l.Trim(), name, StringComparison.Ordinal));
            if (padded >= 0)
            {
                lines[padded] = name;
                for (var i = lines.Count - 1; i > padded; i--)
                {
                    if (string.Equals(lines[i].Trim(), name, StringComparison.Ordinal)) lines.RemoveAt(i);
                }

                WriteLines(rel, full, lines);
                return AddOutcome.Repaired;
            }

            lines.Add(name);
            WriteLines(rel, full, lines);
            return AddOutcome.Added;
        }

        /// <summary>Removes every line equal to the name (ordinal, ignoring surrounding whitespace). Returns how many lines went.</summary>
        public int Remove(string rel, string name)
        {
            name = (name ?? string.Empty).Trim();
            var full = FullPathOf(rel);
            if (!File.Exists(full)) throw new WebAdminException(404, "Name list not found: " + rel);

            var lines = ReadLines(full);
            var removed = lines.RemoveAll(l => string.Equals(l.Trim(), name, StringComparison.Ordinal));
            if (removed > 0) WriteLines(rel, full, lines);
            return removed;
        }

        // ------------------------------------------------------------------ io

        private static List<string> ReadLines(string full)
        {
            var fi = new FileInfo(full);
            if (fi.Length > MaxFileBytes) throw new WebAdminException(413, "Name list file is too large");

            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines;
            }
        }

        /// <summary>Non-blank names (trimmed), in file order.</summary>
        public static List<string> ReadNames(string full)
        {
            return ReadLines(full).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }

        /// <summary>Atomic rewrite (temp file + replace), keeping a rolling backup copy outside the root. Always ends with a newline
        /// so NPC ADDNAMELIST (File.AppendText + WriteLine) appends on its own line.</summary>
        private void WriteLines(string rel, string full, List<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) continue; // drop blank lines while rewriting
                sb.Append(l).Append("\r\n");
            }

            if (BackupRoot != null && File.Exists(full))
            {
                var backup = Path.Combine(BackupRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".bak");
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(full, backup, true);
            }

            var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, sb.ToString(), Utf8NoBom);
                File.Move(temp, full, true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }
}
