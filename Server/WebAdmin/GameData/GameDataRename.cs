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
        /// <summary>drops | recipes | quests | npcs.</summary>
        public string Root;

        /// <summary>Path relative to the root, '/' separated.</summary>
        public string Path;

        public string FullPath;
        public int Replacements;

        /// <summary>New relative path when the file itself is renamed (recipe named after the item, monster drop file).</summary>
        public string RenameTo;

        internal GameDataTextFile Content;
        internal bool ContentChanged;
    }

    /// <summary>
    /// Rename propagation for item / monster names, mirroring ItemInfoFormNew.ItemNameChange and MonsterInfoFormNew.MonsterNameChange
    /// (drops, recipes incl. the recipe file named after the item, quest item / kill sections, NPC [TRADE]/[RECIPE] sections), but
    /// replacing only the matched token (the WinForms code ran string.Replace over the whole line) and following the game parsers'
    /// section rules (a section runs until the next line starting with "[").
    /// </summary>
    internal static class GameDataRename
    {
        private static readonly Regex DropLine = new Regex(@"^(\s*\d+/\d+ +)([^\s]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LeadingToken = new Regex(@"^( *)([^\s|\[]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex FirstToken = new Regex(@"^([^ ]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string[] QuestItemSections = { "[@CARRYITEMS]", "[@ITEMTASKS]", "[@FIXEDREWARDS]", "[@SELECTREWARDS]" };
        private static readonly string[] QuestKillSections = { "[@KILLTASKS]" };

        /// <summary>A usable game-data name: non-empty, no whitespace (the text files split on spaces).</summary>
        public static string CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "is empty";
            if (name.Any(char.IsWhiteSpace)) return "contains spaces (drop, recipe and quest files split names on spaces)";
            return null;
        }

        private static int ReplaceGroup(List<string> lines, int i, Match m, int group, string newName)
        {
            var g = m.Groups[group];
            lines[i] = lines[i].Substring(0, g.Index) + newName + lines[i].Substring(g.Index + g.Length);
            return 1;
        }

        /// <summary>Drop lines "1/N Name [Q]" (also inside GROUP blocks).</summary>
        public static int RewriteDrops(List<string> lines, string oldName, string newName)
        {
            var count = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith(";", StringComparison.Ordinal)) continue;
                var m = DropLine.Match(line);
                if (m.Success && string.Equals(m.Groups[2].Value, oldName, StringComparison.OrdinalIgnoreCase))
                    count += ReplaceGroup(lines, i, m, 2, newName);
            }

            return count;
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

        /// <summary>Quest files: the first token of the lines in the given [@SECTION]s (until a line starting with "[" or "//").</summary>
        public static int RewriteQuest(List<string> lines, string oldName, string newName, bool monsters)
        {
            var sections = monsters ? QuestKillSections : QuestItemSections;
            var count = 0;
            var active = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    // Exactly as QuestInfo.ParseFile: the whole line (no trim, no trailing comment) must equal the header.
                    var header = line.ToUpperInvariant();
                    active = sections.Contains(header);
                    continue;
                }

                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    active = false;
                    continue;
                }

                if (!active || line.Length == 0) continue;

                var m = FirstToken.Match(line);
                if (m.Success && string.Equals(m.Groups[1].Value, oldName, StringComparison.OrdinalIgnoreCase))
                    count += ReplaceGroup(lines, i, m, 1, newName);
            }

            return count;
        }

        /// <summary>
        /// Quest reward lines ([@FIXEDREWARDS] / [@SELECTREWARDS]) whose first token is <paramref name="baseName"/>: QuestInfo.ParseReward
        /// resolves such a line to both "&lt;base&gt;(M)" and "&lt;base&gt;(F)", so renaming one gender variant cannot rewrite them safely.
        /// </summary>
        public static int CountGenderRewardRefs(List<string> lines, string baseName)
        {
            var count = 0;
            var active = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    var header = line.ToUpperInvariant();
                    active = header == "[@FIXEDREWARDS]" || header == "[@SELECTREWARDS]";
                    continue;
                }

                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    active = false;
                    continue;
                }

                if (!active || line.Length == 0) continue;
                var m = FirstToken.Match(line);
                if (m.Success && string.Equals(m.Groups[1].Value, baseName, StringComparison.OrdinalIgnoreCase)) count++;
            }

            return count;
        }

        /// <summary>"Sword(M)" / "Sword(F)" → "Sword"; null for other names.</summary>
        public static string GenderBaseName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length <= 3) return null;
            return name.EndsWith("(M)", StringComparison.OrdinalIgnoreCase) || name.EndsWith("(F)", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 3)
                : null;
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
        /// Plans the edits of a rename. <paramref name="roots"/> maps drops / recipes / quests / npcs to their full directory paths.
        /// <paramref name="renameMonsterDropFile"/>: the monster loads its drops from Drops\&lt;Name&gt;.txt (no DropPath) and no other
        /// monster still uses the old file, so that file follows the rename.
        /// </summary>
        public static List<GameDataRenameFile> Plan(string endpoint, string oldName, string newName, IReadOnlyDictionary<string, string> roots,
            bool renameMonsterDropFile, List<string> warnings)
        {
            var files = new List<GameDataRenameFile>();
            var items = string.Equals(endpoint, "items", StringComparison.OrdinalIgnoreCase);

            if (items)
            {
                Scan(files, roots, "drops", lines => RewriteDrops(lines, oldName, newName), warnings);
                Scan(files, roots, "recipes", lines => RewriteRecipe(lines, oldName, newName), warnings);
                Scan(files, roots, "quests", lines => RewriteQuest(lines, oldName, newName, false), warnings);
                Scan(files, roots, "npcs", lines => RewriteNpc(lines, oldName, newName), warnings);

                PlanFileRename(files, roots, "recipes", oldName, newName, warnings);
                WarnGenderRewards(roots, oldName, warnings);
            }
            else
            {
                Scan(files, roots, "quests", lines => RewriteQuest(lines, oldName, newName, true), warnings);
                if (renameMonsterDropFile) PlanFileRename(files, roots, "drops", oldName, newName, warnings);
            }

            return files.Where(f => f.Replacements > 0 || f.RenameTo != null).OrderBy(f => f.Root, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Warns about quest reward lines naming the base of a renamed (M)/(F) item: they are left as they are.</summary>
        private static void WarnGenderRewards(IReadOnlyDictionary<string, string> roots, string oldName, List<string> warnings)
        {
            var baseName = GenderBaseName(oldName);
            if (baseName == null) return;

            var probe = new List<GameDataRenameFile>();
            Scan(probe, roots, "quests", lines => CountGenderRewardRefs(lines, baseName), warnings);
            foreach (var f in probe)
                warnings.Add("quests/" + f.Path + " rewards \"" + baseName + "\" (" + f.Replacements + " line(s)), which the game resolves to both " + baseName +
                             "(M) and " + baseName + "(F); it was not changed, so check it by hand.");
        }

        private static void Scan(List<GameDataRenameFile> files, IReadOnlyDictionary<string, string> roots, string root, Func<List<string>, int> rewrite, List<string> warnings)
        {
            if (!roots.TryGetValue(root, out var dir) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var full = System.IO.Path.GetFullPath(dir);

            foreach (var path in SafeEnumerate(full, warnings))
            {
                GameDataTextFile content;
                try
                {
                    content = GameDataTextFile.Parse(File.ReadAllBytes(path));
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
            if (!roots.TryGetValue(root, out var dir) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var full = System.IO.Path.GetFullPath(dir);

            if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                warnings.Add("\"" + newName + "\" is not a valid file name, so " + root + "/" + oldName + ".txt was not renamed.");
                return;
            }

            // Recipes / drops are looked up as <root>\<Name>.txt (top level only).
            var source = Directory.EnumerateFiles(full, "*.txt", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), oldName, StringComparison.OrdinalIgnoreCase));
            if (source == null) return;

            var target = System.IO.Path.Combine(full, newName + ".txt");
            var sameFile = string.Equals(System.IO.Path.GetFullPath(source), System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            if (!sameFile && File.Exists(target))
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
            var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            foreach (var f in plan)
            {
                try
                {
                    var backup = System.IO.Path.Combine(backupRoot, f.Root, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar) + "." + stamp + ".bak");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
                    File.Copy(f.FullPath, backup, true);

                    if (f.ContentChanged && f.Content != null)
                        WriteAtomic(f.FullPath, f.Content.Serialize());

                    if (f.RenameTo != null)
                    {
                        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.FullPath), f.RenameTo);
                        if (!string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(f.FullPath), StringComparison.Ordinal))
                        {
                            if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(f.FullPath), StringComparison.OrdinalIgnoreCase))
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
