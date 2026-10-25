using Server.MirEnvir.Storage;
using Server.MirEnvir;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// A text file split into lines with each line's own terminator kept, so a rewrite changes only the edited tokens (encoding,
    /// BOM and line endings are preserved). Files that are not valid UTF-8 are read as Latin-1, which round-trips every byte.
    /// </summary>
    internal sealed class GameDataTextFile
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public List<string> Lines = new List<string>();
        public List<string> Endings = new List<string>();
        public Encoding Encoding = new UTF8Encoding(false);
        public byte[] Preamble = Array.Empty<byte>();

        public static GameDataTextFile Parse(byte[] bytes)
        {
            var file = new GameDataTextFile();
            bytes ??= Array.Empty<byte>();
            string text;

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                file.Preamble = new byte[] { 0xEF, 0xBB, 0xBF };
                file.Encoding = new UTF8Encoding(false);
                text = file.Encoding.GetString(bytes, 3, bytes.Length - 3);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                file.Preamble = new byte[] { 0xFF, 0xFE };
                file.Encoding = new UnicodeEncoding(false, false);
                text = file.Encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                file.Preamble = new byte[] { 0xFE, 0xFF };
                file.Encoding = new UnicodeEncoding(true, false);
                text = file.Encoding.GetString(bytes, 2, bytes.Length - 2);
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(bytes);
                    file.Encoding = new UTF8Encoding(false);
                }
                catch (DecoderFallbackException)
                {
                    file.Encoding = Encoding.Latin1;
                    text = file.Encoding.GetString(bytes);
                }
            }

            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch != '\r' && ch != '\n') continue;

                var ending = ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : ch.ToString();
                file.Lines.Add(text.Substring(start, i - start));
                file.Endings.Add(ending);
                i += ending.Length - 1;
                start = i + 1;
            }

            if (start < text.Length)
            {
                file.Lines.Add(text.Substring(start));
                file.Endings.Add(string.Empty);
            }

            return file;
        }

        public byte[] Serialize()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Lines.Count; i++)
            {
                sb.Append(Lines[i]);
                sb.Append(i < Endings.Count ? Endings[i] : string.Empty);
            }

            var body = Encoding.GetBytes(sb.ToString());
            if (Preamble.Length == 0) return body;
            var all = new byte[Preamble.Length + body.Length];
            Buffer.BlockCopy(Preamble, 0, all, 0, Preamble.Length);
            Buffer.BlockCopy(body, 0, all, Preamble.Length, body.Length);
            return all;
        }
    }

    /// <summary>One file a rename touches.</summary>
    internal sealed class GameDataRenameFile
    {
        /// <summary>recipes | npcs.</summary>
        public string Root;

        /// <summary>Path relative to the root, '/' separated.</summary>
        public string Path;

        public string FullPath;
        public int Replacements;

        /// <summary>New relative path when the file itself is renamed (the recipe named after the item).</summary>
        public string RenameTo;

        internal GameDataTextFile Content;
        internal bool ContentChanged;
    }

    /// <summary>
    /// Rename propagation for item / monster names, mirroring ItemInfoFormNew.ItemNameChange and MonsterInfoFormNew.MonsterNameChange
    /// (recipes incl. the recipe file named after the item, NPC [TRADE]/[RECIPE] sections; the legacy quest files are gone since
    /// quest V2 wave 4: quest objectives and rewards link items and monsters by Index), but
    /// replacing only the matched token (the WinForms code ran string.Replace over the whole line) and following the game parsers'
    /// section rules (a section runs until the next line starting with "["). Drops are not touched (Drops-Tables-Plan phase 2): drop rows
    /// link items by Index and render under the current name, and a monster finds its own drop list by Index.
    /// </summary>
    internal static class GameDataRename
    {
        private static readonly Regex LeadingToken = new Regex(@"^( *)([^\s|\[]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex FirstToken = new Regex(@"^([^ ]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>A usable game-data name: non-empty, no whitespace (the text files split on spaces).</summary>
        public static string CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "is empty";
            if (name.Any(char.IsWhiteSpace)) return "contains spaces (recipe and NPC files split names on spaces)";
            return null;
        }

        private static int ReplaceGroup(List<string> lines, int i, Match m, int group, string newName)
        {
            var g = m.Groups[group];
            lines[i] = lines[i].Substring(0, g.Index) + newName + lines[i].Substring(g.Index + g.Length);
            return 1;
        }

        /// <summary>Recipe files: the first token of every line outside the [Recipe] key section (ingredients and tools).</summary>
        public static int RewriteRecipe(List<string> lines, string oldName, string newName)
        {
            var count = 0;
            var mode = "ingredients";
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    var close = line.IndexOf(']');
                    mode = (close > 1 ? line.Substring(1, close - 1) : line.Substring(1)).Trim().ToLowerInvariant();
                    continue;
                }

                if (mode == "recipe") continue;

                var m = LeadingToken.Match(line);
                if (m.Success && string.Equals(m.Groups[2].Value, oldName, StringComparison.OrdinalIgnoreCase))
                    count += ReplaceGroup(lines, i, m, 2, newName);
            }

            return count;
        }

        /// <summary>NPC scripts: the first token of the lines in [TRADE] / [RECIPE] sections.</summary>
        public static int RewriteNpc(List<string> lines, string oldName, string newName)
        {
            var count = 0;
            var active = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    var upper = line.ToUpperInvariant();
                    active = upper.StartsWith("[TRADE]", StringComparison.Ordinal) || upper.StartsWith("[RECIPE]", StringComparison.Ordinal);
                    continue;
                }

                if (!active || string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";", StringComparison.Ordinal)) continue;

                var m = LeadingToken.Match(line);
                if (m.Success && string.Equals(m.Groups[2].Value, oldName, StringComparison.OrdinalIgnoreCase))
                    count += ReplaceGroup(lines, i, m, 2, newName);
            }

            return count;
        }

        /// <summary>
        /// Plans the edits of a rename. <paramref name="roots"/> maps recipes / npcs to their full directory paths. A monster rename
        /// has nothing to rewrite (no text file names monsters since the legacy quest files went, quest V2 wave 4).
        /// </summary>
        public static List<GameDataRenameFile> Plan(string endpoint, string oldName, string newName, IReadOnlyDictionary<string, string> roots,
            List<string> warnings)
        {
            var files = new List<GameDataRenameFile>();
            var items = string.Equals(endpoint, "items", StringComparison.OrdinalIgnoreCase);

            if (items)
            {
                Scan(files, roots, "recipes", lines => RewriteRecipe(lines, oldName, newName), warnings);
                Scan(files, roots, "npcs", lines => RewriteNpc(lines, oldName, newName), warnings);

                PlanFileRename(files, roots, "recipes", oldName, newName, warnings);
            }

            return files.Where(f => f.Replacements > 0 || f.RenameTo != null).OrderBy(f => f.Root, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Scan(List<GameDataRenameFile> files, IReadOnlyDictionary<string, string> roots, string root, Func<List<string>, int> rewrite, List<string> warnings)
        {
            if (!roots.TryGetValue(root, out var dir) || string.IsNullOrEmpty(dir) || !(Directory.Exists(dir) || DesignFiles.IsDesignFolder(dir))) return;
            var full = System.IO.Path.GetFullPath(dir);

            foreach (var path in SafeEnumerate(full, warnings))
            {
                GameDataTextFile content;
                try
                {
                    content = GameDataTextFile.Parse(DesignFiles.ReadAllBytes(path)); // roadmap P7: recipes are in SystemData.db
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add("Could not read " + root + "/" + Relative(full, path) + ": " + ex.GetType().Name);
                    continue;
                }

                var n = rewrite(content.Lines);
                if (n <= 0) continue;

                files.Add(new GameDataRenameFile
                {
                    Root = root,
                    Path = Relative(full, path),
                    FullPath = path,
                    Replacements = n,
                    Content = content,
                    ContentChanged = true,
                });
            }
        }

        private static void PlanFileRename(List<GameDataRenameFile> files, IReadOnlyDictionary<string, string> roots, string root, string oldName,
            string newName, List<string> warnings)
        {
            if (!roots.TryGetValue(root, out var dir) || string.IsNullOrEmpty(dir) || !(Directory.Exists(dir) || DesignFiles.IsDesignFolder(dir))) return;
            var full = System.IO.Path.GetFullPath(dir);

            if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                warnings.Add("\"" + newName + "\" is not a valid file name, so " + root + "/" + oldName + ".txt was not renamed.");
                return;
            }

            // Recipes are looked up as <root>\<Name>.txt (top level only).
            var source = DesignFiles.EnumerateFiles(full, "*.txt", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), oldName, StringComparison.OrdinalIgnoreCase));
            if (source == null) return;

            var target = System.IO.Path.Combine(full, newName + ".txt");
            var sameFile = string.Equals(System.IO.Path.GetFullPath(source), System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            if (!sameFile && DesignFiles.Exists(target))
            {
                warnings.Add(root + "/" + newName + ".txt already exists, so " + root + "/" + System.IO.Path.GetFileName(source) + " was not renamed.");
                return;
            }

            var rel = Relative(full, source);
            var entry = files.FirstOrDefault(f => f.Root == root && string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new GameDataRenameFile { Root = root, Path = rel, FullPath = source };
                files.Add(entry);
            }

            entry.RenameTo = newName + ".txt";
        }

        private static List<string> SafeEnumerate(string root, List<string> warnings)
        {
            if (DesignFiles.IsDesignFolder(root)) return DesignFiles.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).ToList(); // the store's files

            var result = new List<string>();
            List<string> all;
            try
            {
                all = Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add("Could not list " + root + ": " + ex.GetType().Name);
                return result;
            }

            var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root : root + System.IO.Path.DirectorySeparatorChar;
            foreach (var f in all)
            {
                var full = System.IO.Path.GetFullPath(f);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue; // confinement (symlink / junction escape)

                try
                {
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                result.Add(full);
            }

            return result;
        }

        private static string Relative(string root, string path)
        {
            return System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        /// <summary>
        /// Writes a plan: each changed file is backed up to <paramref name="backupRoot"/>\&lt;root&gt;\&lt;path&gt;.&lt;stamp&gt;.bak, then replaced
        /// atomically (temp file + File.Replace / Move); renamed files are moved after their content was written.
        /// </summary>
        public static void Execute(IEnumerable<GameDataRenameFile> plan, string backupRoot, DateTime now, List<string> warnings)
        {
            // Store files (roadmap P7) resolve the rewritten names against the editor copy, where the rename already is: the store only
            // learns it at the next Game Data save, and resolving against it would turn the renamed lines into plain text.
            DesignTexts.WithParseNames(DesignNames.FromEnvir(Envir.Edit), () =>
            {
                ExecuteFiles(plan, backupRoot, now, warnings);
                return 0;
            });
        }

        private static void ExecuteFiles(IEnumerable<GameDataRenameFile> plan, string backupRoot, DateTime now, List<string> warnings)
        {
            var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            foreach (var f in plan)
            {
                try
                {
                    var backup = System.IO.Path.Combine(backupRoot, f.Root, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar) + "." + stamp + ".bak");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
                    if (DesignFiles.TryMap(f.FullPath, out _, out _)) File.WriteAllBytes(backup, DesignFiles.ReadAllBytes(f.FullPath));
                    else File.Copy(f.FullPath, backup, true);

                    if (f.ContentChanged && f.Content != null)
                        WriteAtomic(f.FullPath, f.Content.Serialize());

                    if (f.RenameTo != null)
                    {
                        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.FullPath), f.RenameTo);
                        if (!string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(f.FullPath), StringComparison.Ordinal))
                        {
                            if (DesignFiles.TryMap(f.FullPath, out _, out _))
                            {
                                DesignFiles.Move(f.FullPath, target); // case-only renames are a plain rename in the store
                            }
                            else if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(f.FullPath), StringComparison.OrdinalIgnoreCase))
                            {
                                // case-only rename on a case-insensitive file system: go through a temp name
                                var temp = f.FullPath + ".renaming";
                                File.Move(f.FullPath, temp);
                                File.Move(temp, target);
                            }
                            else
                            {
                                File.Move(f.FullPath, target);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add("Could not update " + f.Root + "/" + f.Path + ": " + ex.GetType().Name);
                    WebAdminLog.Error(ex, "Rename propagation failed for " + f.FullPath);
                }
            }
        }

        internal static void WriteAtomic(string path, byte[] bytes)
        {
            if (DesignFiles.TryMap(path, out _, out _))
            {
                DesignFiles.WriteAllBytes(path, bytes);
                return;
            }

            var temp = path + ".webadmin.tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                    // File.Replace can fail on some file systems; fall back to an overwrite move
                }

                File.Move(temp, path, true);
                return;
            }

            File.Move(temp, path);
        }
    }
}
