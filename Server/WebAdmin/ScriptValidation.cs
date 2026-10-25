using System.Globalization;
using System.Text.RegularExpressions;

namespace Server.WebAdmin
{
    public sealed class ScriptDiagnostic
    {
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Info = "info";

        public int Line { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }

        public override string ToString() => Line + " " + Severity + ": " + Message;
    }

    /// <summary>
    /// What the validators may look up. Item / monster name sets are null when the database is not loaded (those checks are
    /// skipped). File readers take the path exactly as written in the script and return the file's lines, or null when it
    /// does not exist (or escapes its folder).
    /// </summary>
    public sealed class ScriptValidationContext
    {
        private HashSet<string> _items;
        private HashSet<string> _monsters;

        /// <summary>Item names as the game matches them (<c>Envir.GetItemInfo(string)</c>: name without spaces, case-insensitive).</summary>
        public IEnumerable<string> ItemNames
        {
            set => _items = value == null ? null : new HashSet<string>(value.Where(n => n != null).Select(n => n.Replace(" ", string.Empty)), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Monster names (<c>Envir.GetMonsterInfo(string)</c>: compared with and without spaces, case-insensitive).</summary>
        public IEnumerable<string> MonsterNames
        {
            set => _monsters = value == null ? null : new HashSet<string>(value.Where(n => n != null).Select(n => n.Replace(" ", string.Empty)), StringComparer.OrdinalIgnoreCase);
        }

        public bool HasItemList => _items != null;
        public bool HasMonsterList => _monsters != null;

        /// <summary>Drops: <c>#INSERT [x]</c> target relative to the Drops folder.</summary>
        public Func<string, string[]> ReadDropFile { get; set; }

        /// <summary>NPC scripts: <c>#INSERT</c> / <c>#INCLUDE</c> targets relative to the Envir folder.</summary>
        public Func<string, string[]> ReadEnvirFile { get; set; }

        /// <summary>NPC scripts: <c>CALL</c> target (relative to the NPCs folder, without ".txt") exists.</summary>
        public Func<string, bool> NpcScriptExists { get; set; }

        /// <summary>Name of the validated file without folder and ".txt" (recipes: the crafted item).</summary>
        public string FileName { get; set; }

        public bool HasItem(string token)
        {
            if (_items == null) return true;
            return !string.IsNullOrEmpty(token) && _items.Contains(token);
        }

        public bool HasMonster(string token)
        {
            if (_monsters == null) return true;
            return !string.IsNullOrEmpty(token) && _monsters.Contains(token.Replace(" ", string.Empty));
        }
    }

    /// <summary>
    /// Static checks for Crystal's text scripts, written against the real loaders: <c>DropInfo.FromLine/Load/ParseGroup/ParseInsert</c>
    /// (MonsterInfo.cs), <c>NPCScript.ParseInsert/ParseInclude/ParsePage/ParseSegment/ParseGoods/...</c>
    /// and <c>RecipeInfo.LoadIngredients</c>. "error" = the loader rejects the line or throws; "warning" = it is ignored or
    /// probably not what was meant.
    /// </summary>
    public static class ScriptValidation
    {
        public const int MaxDiagnostics = 1000;
        private const int MaxIncludeDepth = 6;

        private static readonly Regex DropInsertRegex = new Regex(@"#INSERT \[(.*?)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ButtonRegex = new Regex(@"<.*?/(\@.*?)>", RegexOptions.CultureInvariant);
        private static readonly Regex ArgumentRegex = new Regex(@"\((.*)\)", RegexOptions.CultureInvariant);
        private static readonly Regex QuoteRegex = new Regex("\"([^\"]*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex ChanceRegex = new Regex(@"^\d+/\d+$", RegexOptions.CultureInvariant);

        /// <summary>NPC page keys the server handles itself (NPCScript constants) plus the client's @Exit.</summary>
        public static readonly HashSet<string> BuiltInPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[@MAIN]", "[@BUY]", "[@SELL]", "[@BUYSELL]", "[@REPAIR]", "[@SREPAIR]", "[@REFINE]", "[@REFINECHECK]", "[@REFINECOLLECT]",
            "[@REPLACEWEDDINGRING]", "[@BUYBACK]", "[@STORAGE]", "[@CONSIGN]", "[@MARKET]", "[@CRAFT]", "[@CREATEGUILD]", "[@REQUESTWAR]",
            "[@SENDPARCEL]", "[@COLLECTPARCEL]", "[@AWAKENING]", "[@DISASSEMBLE]", "[@DOWNGRADE]", "[@RESET]", "[@PEARLBUY]", "[@BUYUSED]",
            "[@BUYNEW]", "[@BUYSELLNEW]", "[@CREATEHERO]", "[@MANAGEHERO]", "[@GUILDTERRITORY]", "[@EXIT]",
        };

        private static readonly HashSet<string> NpcSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[TRADE]", "[RECIPE]", "[TYPES]", "[USEDTYPES]", "[QUESTS]", "[SPEECH]",
        };

        /// <summary>Validates <paramref name="content"/> as the given kind ("drops", "npc", "recipe"). Unknown kind → empty list.</summary>
        public static List<ScriptDiagnostic> Validate(string kind, string content, ScriptValidationContext ctx)
        {
            ctx = ctx ?? new ScriptValidationContext();
            var lines = SplitLines(content);
            List<ScriptDiagnostic> result;

            switch (kind)
            {
                case "drops":
                    result = ValidateDrops(lines, ctx);
                    break;
                case DragonDropsKind:
                    result = ValidateDragonDrops(lines, ctx);
                    break;
                case "npc":
                    result = ValidateNpc(lines, ctx);
                    break;
                case "recipe":
                    result = ValidateRecipe(lines, ctx);
                    break;
                default:
                    return new List<ScriptDiagnostic>();
            }

            if ((kind == "drops" || kind == DragonDropsKind || kind == "recipe" || kind == "npc") && !ctx.HasItemList)
                result.Insert(0, Diag(0, ScriptDiagnostic.Info, "The item database is not loaded, so item names were not checked."));

            result.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : SeverityRank(a.Severity).CompareTo(SeverityRank(b.Severity)));
            if (result.Count > MaxDiagnostics)
            {
                result.RemoveRange(MaxDiagnostics, result.Count - MaxDiagnostics);
                result.Add(Diag(0, ScriptDiagnostic.Info, "Only the first " + MaxDiagnostics + " problems are shown."));
            }

            return result;
        }

        /// <summary>Validator kind for Drops\DragonItem.txt, which has its own line format (DragonInfo.DropInfo.FromLine).</summary>
        public const string DragonDropsKind = "dragondrops";

        /// <summary>The validator for a file: <see cref="DragonDropsKind"/> for DragonItem.txt at the top of the Drops root, else <paramref name="validator"/>.</summary>
        public static string KindFor(string validator, string normalizedPath)
        {
            if (validator == "drops" && string.Equals(normalizedPath, "DragonItem.txt", StringComparison.OrdinalIgnoreCase))
                return DragonDropsKind;
            return validator;
        }

        public static string[] SplitLines(string content)
        {
            return ScriptText.NormalizeNewlines(content ?? string.Empty).Split('\n');
        }

        private static int SeverityRank(string s) => s == ScriptDiagnostic.Error ? 0 : s == ScriptDiagnostic.Warning ? 1 : 2;

        private static ScriptDiagnostic Diag(int line, string severity, string message) => new ScriptDiagnostic { Line = line, Severity = severity, Message = message };

        // ================================================================== drops

        public static List<ScriptDiagnostic> ValidateDrops(string[] lines, ScriptValidationContext ctx)
        {
            return ValidateDrops(lines, ctx, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Mirrors <c>DragonInfo.LoadDrops</c> / <c>DragonInfo.DropInfo.FromLine</c>: '1/N ItemName level' or '1/N Gold amount level',
        /// level 1..Globals.MaxDragonLevel. No #INSERT, groups or Q.
        /// </summary>
        public static List<ScriptDiagnostic> ValidateDragonDrops(string[] lines, ScriptValidationContext ctx)
        {
            var diags = new List<ScriptDiagnostic>();
            var goldLevels = new Dictionary<int, int>();
            var stopped = false;
            var maxLevel = (int)Globals.MaxDragonLevel;

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var lineNo = i + 1;
                if (raw.StartsWith(";", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(raw)) continue;

                if (stopped)
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Not loaded: an earlier line has a level outside 1-" + maxLevel + ", which stops the dragon drop loader."));
                    continue;
                }

                if (raw.TrimStart().StartsWith(";", StringComparison.Ordinal))
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Comments must start in the first column; this line is read as a drop and rejected."));
                    continue;
                }

                var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0].Length < 2)
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Expected '1/<chance> <ItemName> <level>' or '1/<chance> Gold <amount> <level>'; this line makes the dragon drop loader throw."));
                    continue;
                }

                if (raw.IndexOf('\t') >= 0)
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Use spaces, not tabs: the dragon drop loader splits on spaces only."));
                    continue;
                }

                if (!int.TryParse(parts[0].Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chance))
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Invalid chance '" + parts[0] + "': expected 1/<number>. This drop is skipped."));
                    continue;
                }
                if (!ChanceRegex.IsMatch(parts[0]) || !parts[0].StartsWith("1/", StringComparison.Ordinal))
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Only the number after the first two characters is used: '" + parts[0] + "' is read as 1/" + chance + "."));

                string levelToken;
                var isGold = string.Equals(parts[1], "Gold", StringComparison.OrdinalIgnoreCase);
                if (isGold)
                {
                    if (parts.Length < 4)
                    {
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Gold needs an amount and a level: 1/<chance> Gold <amount> <level>. This drop is skipped."));
                        continue;
                    }
                    if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gold) || gold == 0)
                    {
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Invalid gold amount '" + parts[2] + "': expected a whole number above 0. This drop is skipped."));
                        continue;
                    }
                    levelToken = parts[3];
                    if (parts.Length > 4) diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Text after the level is ignored."));
                }
                else
                {
                    if (parts.Length < 3)
                    {
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Missing level: 1/<chance> <ItemName> <level>. This drop is skipped."));
                        continue;
                    }
                    if (!ctx.HasItem(parts[1]))
                    {
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Unknown item '" + parts[1] + "': this drop is skipped (item names are written without spaces)."));
                        continue;
                    }
                    levelToken = parts[2];
                    if (parts.Length > 3) diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Text after the level is ignored."));
                }

                if (!byte.TryParse(levelToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Invalid level '" + levelToken + "': expected 1-" + maxLevel + ". This drop is skipped."));
                    continue;
                }
                if (level == 0 || level > maxLevel)
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Level " + level + " is outside 1-" + maxLevel + ": the dragon drop loader stops here and ignores this and every later line."));
                    stopped = true;
                    continue;
                }

                if (isGold)
                {
                    goldLevels.TryGetValue(level, out var seen);
                    goldLevels[level] = seen + 1;
                    if (seen == 1)
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "A second Gold line for level " + level + ": DragonInfo sorts each level's drops by item type and throws on two Gold drops."));
                }
            }

            return diags;
        }

        private static List<ScriptDiagnostic> ValidateDrops(string[] lines, ScriptValidationContext ctx, int depth, HashSet<string> visited)
        {
            var diags = new List<ScriptDiagnostic>();
            var consumed = new bool[lines.Length];

            for (var i = 0; i < lines.Length; i++)
            {
                if (consumed[i]) continue;
                var raw = lines[i];
                var lineNo = i + 1;

                if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith(";", StringComparison.Ordinal)) continue;

                if (raw.ToUpperInvariant().StartsWith("#INSERT", StringComparison.Ordinal))
                {
                    CheckDropInsert(raw, lineNo, ctx, depth, visited, diags);
                    continue;
                }

                if (raw.TrimStart().StartsWith(";", StringComparison.Ordinal))
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Comments must start in the first column; this line is read as a drop and rejected."));
                    continue;
                }

                if (DropInsertRegex.IsMatch(raw))
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "#INSERT must be at the start of the line; the file is inserted but this line is also read as a drop."));

                var trimmed = raw.Trim();
                if (trimmed == "{" || trimmed == "}")
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "'" + trimmed + "' without a GROUP line before it."));
                    continue;
                }

                var group = CheckDropLine(raw, lineNo, ctx, diags);
                if (group != null)
                    SimulateGroup(lines, i + 1, lineNo, consumed, ctx, diags);
            }

            return diags;
        }

        private static void CheckDropInsert(string raw, int lineNo, ScriptValidationContext ctx, int depth, HashSet<string> visited, List<ScriptDiagnostic> diags)
        {
            var match = DropInsertRegex.Match(raw);
            if (!match.Success)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "#INSERT needs a path in brackets: #INSERT [Folder\\File.txt]. This line is ignored."));
                return;
            }

            var sub = match.Groups[1].Value;
            if (ctx.ReadDropFile == null) return;

            var inserted = ctx.ReadDropFile(sub);
            if (inserted == null)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "#INSERT target '" + sub + "' was not found in the Drops folder; the drop loader throws on this file."));
                return;
            }

            if (depth >= MaxIncludeDepth || !visited.Add(sub.Replace('/', '\\')))
                return;

            var nested = ValidateDrops(inserted, ctx, depth + 1, visited);
            Summarise(nested, "#INSERT [" + sub + "]", lineNo, diags);
        }

        private static void Summarise(List<ScriptDiagnostic> nested, string what, int lineNo, List<ScriptDiagnostic> diags)
        {
            var errors = nested.Count(d => d.Severity == ScriptDiagnostic.Error);
            var warnings = nested.Count(d => d.Severity == ScriptDiagnostic.Warning);
            if (errors + warnings == 0) return;

            var first = nested.First(d => d.Severity != ScriptDiagnostic.Info);
            diags.Add(Diag(lineNo, errors > 0 ? ScriptDiagnostic.Error : ScriptDiagnostic.Warning,
                what + ": the inserted file has " + errors + " error(s) and " + warnings + " warning(s); first at its line " + first.Line + ": " + first.Message));
        }

        /// <summary>Checks one drop line like <c>DropInfo.FromLine</c>. Returns the group token ("GROUP", "GROUP*", "GROUP^") for a group line.</summary>
        private static string CheckDropLine(string line, int lineNo, ScriptValidationContext ctx, List<ScriptDiagnostic> diags)
        {
            if (line.IndexOf('\t') >= 0)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Use spaces, not tabs: the drop loader splits on spaces only."));
                return null;
            }

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 0 && parts[0].Length < 2)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Expected a chance like 1/100 first; '" + parts[0] + "' makes the drop loader throw."));
                return null;
            }

            if (parts.Length < 2)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Expected '1/<chance> <ItemName> [Q]', '1/<chance> Gold <amount>' or '1/<chance> GROUP'."));
                return null;
            }

            if (!int.TryParse(parts[0].Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chance))
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Invalid chance '" + parts[0] + "': expected 1/<number>."));
                return null;
            }

            if (!ChanceRegex.IsMatch(parts[0]) || !parts[0].StartsWith("1/", StringComparison.Ordinal))
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Only the number after the first two characters is used: '" + parts[0] + "' is read as 1/" + chance + "."));
            else if (chance <= 0)
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "A chance of 1/" + chance + " always drops."));

            if (string.Equals(parts[1], "Gold", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 3)
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Gold needs an amount: 1/<chance> Gold <amount>."));
                    return null;
                }

                if (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gold) || gold == 0)
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Invalid gold amount '" + parts[2] + "': expected a whole number above 0."));
                    return null;
                }

                if (parts.Length > 3)
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Text after the gold amount is ignored."));
                return null;
            }

            if (parts[1].ToUpperInvariant().StartsWith("GROUP", StringComparison.Ordinal))
            {
                var suffix = parts[1].Substring(5);
                if (suffix.Length > 0 && suffix != "*" && suffix != "^")
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "'" + parts[1] + "' is read as a plain GROUP (use GROUP, GROUP* for one random drop or GROUP^ for the first hit)."));
                return parts[1];
            }

            if (!ctx.HasItem(parts[1]))
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Unknown item '" + parts[1] + "': this drop is skipped (item names are written without spaces)."));
                return null;
            }

            if (parts.Length > 2 && !string.Equals(parts[2], "Q", StringComparison.OrdinalIgnoreCase))
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "'" + parts[2] + "' is ignored: only Q (quest required) may follow the item name."));
            else if (parts.Length > 3)
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Text after 'Q' is ignored."));

            return null;
        }

        /// <summary>Mirrors <c>DropInfo.ParseGroup</c>: consumes lines up to the matching '}' (nested groups recurse).</summary>
        private static int SimulateGroup(string[] lines, int start, int groupLineNo, bool[] consumed, ScriptValidationContext ctx, List<ScriptDiagnostic> diags)
        {
            bool opened = false, closed = false;
            var j = start;

            for (; j < lines.Length; j++)
            {
                if (consumed[j]) continue;
                consumed[j] = true;
                var line = lines[j].Trim();
                var lineNo = j + 1;

                if (line == "{")
                {
                    if (opened)
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "A second '{' in the same GROUP is ignored."));
                    opened = true;
                    continue;
                }

                if (line == "}")
                {
                    closed = true;
                    break;
                }

                if (line.StartsWith(";", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line)) continue;

                if (!opened)
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "This line comes before the GROUP's '{' but is still read as part of the group."));

                if (line.ToUpperInvariant().StartsWith("#INSERT", StringComparison.Ordinal))
                {
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "#INSERT inside a GROUP inserts the file at the end of the drop list, not into the group."));
                    continue;
                }

                var nested = CheckDropLine(line, lineNo, ctx, diags);
                if (nested != null)
                    SimulateGroup(lines, j + 1, lineNo, consumed, ctx, diags);
            }

            if (!closed)
                diags.Add(Diag(groupLineNo, ScriptDiagnostic.Error, "This GROUP is never closed with '}': the loader discards all of its drops."));
            else if (!opened)
                diags.Add(Diag(groupLineNo, ScriptDiagnostic.Error, "This GROUP has no '{': the loader discards all of its drops."));

            return j;
        }

        // ================================================================== NPC scripts

        /// <summary>Normalises a page key like the loader: upper case, "(args)" → "()" (except default "[@_" pages).</summary>
        public static string PageKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            key = key.Trim().ToUpperInvariant();
            if (key.StartsWith("[@_", StringComparison.Ordinal)) return key;
            return ArgumentRegex.IsMatch(key) ? ArgumentRegex.Replace(key, "()") : key;
        }

        private static string HeaderKey(string raw)
        {
            var close = raw.IndexOf(']');
            return close > 0 ? raw.Substring(0, close + 1) : raw;
        }

        public static List<ScriptDiagnostic> ValidateNpc(string[] lines, ScriptValidationContext ctx)
        {
            var diags = new List<ScriptDiagnostic>();
            var pages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) #INSERT / #INCLUDE targets (both relative to Envir); labels they bring in count as defined pages.
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var upper = raw.ToUpperInvariant();
                if (upper.StartsWith("#INSERT", StringComparison.Ordinal))
                    CheckNpcInsert(raw, i + 1, ctx, pages, visited, 0, diags);
                else if (upper.StartsWith("#INCLUDE", StringComparison.Ordinal))
                    CheckNpcInclude(raw, i + 1, ctx, pages, diags);
                else if (upper.TrimStart().StartsWith("#INSERT", StringComparison.Ordinal) || upper.TrimStart().StartsWith("#INCLUDE", StringComparison.Ordinal))
                    diags.Add(Diag(i + 1, ScriptDiagnostic.Warning, "#INSERT / #INCLUDE must start in the first column; with leading spaces it is not processed."));
            }

            // 2) page / section headers defined in this file.
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();
                if (!trimmed.StartsWith("[", StringComparison.Ordinal)) continue;

                if (!raw.StartsWith("[", StringComparison.Ordinal))
                {
                    diags.Add(Diag(i + 1, ScriptDiagnostic.Warning, "Section headers must start in the first column; this one is not found by the loader."));
                    continue;
                }

                if (!raw.EndsWith("]", StringComparison.Ordinal))
                {
                    diags.Add(Diag(i + 1, ScriptDiagnostic.Warning, raw.TrimEnd().EndsWith("]", StringComparison.Ordinal)
                        ? "Trailing spaces after ']': the page before does not end here and runs into this one."
                        : "Text after ']': the page before does not end here and runs into this one."));
                }

                var header = HeaderKey(raw.TrimEnd());
                if (header.StartsWith("[@", StringComparison.Ordinal))
                {
                    var key = PageKey(header);
                    if (pages.TryGetValue(key, out var firstLine) && firstLine > 0)
                        diags.Add(Diag(i + 1, ScriptDiagnostic.Warning, "Page " + header + " is already defined on line " + firstLine + "; only the first one is used."));
                    else
                        pages[key] = i + 1;
                }
                else if (!NpcSections.Contains(header.ToUpperInvariant()))
                {
                    diags.Add(Diag(i + 1, ScriptDiagnostic.Warning, "Unknown section " + header + ": known sections are [@PAGE] pages, " + string.Join(", ", NpcSections) + "."));
                }
            }

            // 3) page bodies: directives, buttons, gotos, CALL.
            string currentSection = null;
            string mode = "SAY";

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var lineNo = i + 1;

                if (raw.StartsWith("[", StringComparison.Ordinal))
                {
                    currentSection = HeaderKey(raw.TrimEnd()).ToUpperInvariant();
                    mode = "SAY";

                    if (NpcSections.Contains(currentSection))
                        CheckNpcSectionBody(lines, i, currentSection, ctx, diags);
                    continue;
                }

                if (currentSection == null || !currentSection.StartsWith("[@", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(raw) || raw.StartsWith(";", StringComparison.Ordinal)) continue;

                var upper = raw.ToUpperInvariant();
                if (upper.StartsWith("#INSERT", StringComparison.Ordinal) || upper.StartsWith("#INCLUDE", StringComparison.Ordinal)) continue;

                if (raw.StartsWith("#", StringComparison.Ordinal))
                {
                    var directive = raw.Substring(1).ToUpperInvariant().Trim().Split(' ')[0];
                    switch (directive)
                    {
                        case "IF":
                        case "SAY":
                        case "ACT":
                        case "ELSESAY":
                        case "ELSEACT":
                            mode = directive;
                            break;
                        default:
                            diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Unknown directive #" + directive + ": the NPC loader throws on this page (use #IF, #SAY, #ACT, #ELSESAY or #ELSEACT)."));
                            break;
                    }
                    continue;
                }

                if (raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    var directive = raw.TrimStart().Substring(1).ToUpperInvariant().Trim().Split(' ')[0];
                    if (directive == "IF" || directive == "SAY" || directive == "ACT" || directive == "ELSESAY" || directive == "ELSEACT")
                    {
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "#" + directive + " has leading spaces, so it is read as text, not as a directive."));
                        continue;
                    }
                }

                CheckQuestKeyword(raw, mode, lineNo, diags);

                if (mode == "IF") continue;

                // buttons <Text/@Page>
                var match = ButtonRegex.Match(raw);
                while (match.Success)
                {
                    var target = match.Groups[1].Value.Split('/')[0];
                    CheckNpcTarget("[" + target + "]", lineNo, pages, diags);
                    match = match.NextMatch();
                }

                var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    switch (parts[0].ToUpperInvariant())
                    {
                        case "GOTO":
                        case "GROUPGOTO":
                        case "ROLLDIE":
                        case "ROLLYUT":
                            CheckNpcTarget("[" + parts[1] + "]", lineNo, pages, diags);
                            break;
                        case "TIMERECALL":
                        case "DELAYGOTO":
                        case "TIMERECALLGROUP":
                            if (parts.Length > 2) CheckNpcTarget("[" + parts[2] + "]", lineNo, pages, diags);
                            break;
                        case "CALL":
                            if (mode == "ACT" || mode == "ELSEACT") CheckNpcCall(raw, parts, lineNo, ctx, diags);
                            break;
                    }
                }
            }

            return diags;
        }

        // Quest V2 script keywords (lane F): keyword â†’ (minimum words incl. the keyword, usage, is a check). The NPC parser drops a
        // line with too few words silently, and a non-numeric quest / step / objective id never matches.
        private static readonly Dictionary<string, (int Words, string Usage, bool Check)> QuestKeywords = new Dictionary<string, (int, string, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["CHECKQUESTSTEP"] = (3, "CHECKQUESTSTEP quest step", true),
            ["CHECKQUESTVAR"] = (5, "CHECKQUESTVAR quest var op value", true),
            ["CHECKQUESTOUTCOME"] = (3, "CHECKQUESTOUTCOME quest outcome", true),
            ["CHECKCONDITION"] = (2, "CHECKCONDITION condition", true),
            ["QUESTCOUNTER"] = (3, "QUESTCOUNTER quest objective [n]", false),
            ["SETQUESTVAR"] = (4, "SETQUESTVAR quest var value", false),
            ["QUESTSTEP"] = (3, "QUESTSTEP quest step", false),
            ["OFFERQUEST"] = (2, "OFFERQUEST quest", false),
        };

        private static void CheckQuestKeyword(string raw, string mode, int lineNo, List<ScriptDiagnostic> diags)
        {
            var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !QuestKeywords.TryGetValue(parts[0], out var k)) return;
            var checkMode = mode == "IF";
            var actMode = mode == "ACT" || mode == "ELSEACT";
            if (k.Check && !checkMode) { diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, parts[0].ToUpperInvariant() + " is a check: put it under #IF.")); return; }
            if (!k.Check && !actMode) { diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, parts[0].ToUpperInvariant() + " is an action: put it under #ACT or #ELSEACT.")); return; }
            if (parts.Length < k.Words) { diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Too few arguments (use " + k.Usage + "); the line is ignored.")); return; }
            if (!int.TryParse(parts[1], out _))
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, parts[0].ToUpperInvariant() + " needs a quest (or condition) number, not '" + parts[1] + "'."));
        }

        private static void CheckNpcTarget(string target, int lineNo, Dictionary<string, int> pages, List<ScriptDiagnostic> diags)
        {
            var key = PageKey(target);
            if (key.Length <= 2 || BuiltInPages.Contains(key) || pages.ContainsKey(key)) return;
            diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Page " + target + " is not defined in this script."));
        }

        private static void CheckNpcCall(string raw, string[] parts, int lineNo, ScriptValidationContext ctx, List<ScriptDiagnostic> diags)
        {
            if (ctx.NpcScriptExists == null) return;
            var target = parts[1];
            var quote = QuoteRegex.Match(raw);
            if (quote.Success) target = quote.Groups[1].Value;

            if (!ctx.NpcScriptExists(target))
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "CALL target '" + target + ".txt' was not found in the NPCs folder; the action is dropped."));
        }

        /// <summary>Extracts the bracketed path of <c>#INSERT [path]</c> / <c>#INCLUDE [path] @Page</c> like the loader (null when it would throw).</summary>
        private static string BracketPath(string token)
        {
            if (token == null || token.Length < 2) return null;
            return token.Substring(1, token.Length - 2);
        }

        private static void CheckNpcInsert(string raw, int lineNo, ScriptValidationContext ctx, Dictionary<string, int> pages, HashSet<string> visited, int depth, List<ScriptDiagnostic> diags)
        {
            var split = raw.Split(' ');
            if (split.Length < 2)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "#INSERT needs a path: #INSERT [Folder\\File.txt]. This line is ignored."));
                return;
            }

            var path = BracketPath(split[1]);
            if (path == null)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "#INSERT path '" + split[1] + "' is malformed; the NPC loader throws on it."));
                return;
            }

            if (!split[1].StartsWith("[", StringComparison.Ordinal) || !split[1].EndsWith("]", StringComparison.Ordinal))
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "The #INSERT path should be in brackets and contain no spaces: [Folder\\File.txt]."));

            if (ctx.ReadEnvirFile == null) return;
            var inserted = ctx.ReadEnvirFile(path);
            if (inserted == null)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "#INSERT target '" + path + "' was not found under Envir; nothing is inserted."));
                return;
            }

            if (depth >= MaxIncludeDepth || !visited.Add(path.Replace('/', '\\'))) return;

            for (var i = 0; i < inserted.Length; i++)
            {
                var line = inserted[i];
                if (line.StartsWith("[@", StringComparison.Ordinal))
                {
                    var key = PageKey(HeaderKey(line.TrimEnd()));
                    if (!pages.ContainsKey(key)) pages[key] = 0;
                }
                else if (line.ToUpperInvariant().StartsWith("#INSERT", StringComparison.Ordinal))
                {
                    var nestedDiags = new List<ScriptDiagnostic>();
                    CheckNpcInsert(line, lineNo, ctx, pages, visited, depth + 1, nestedDiags);
                    foreach (var d in nestedDiags)
                        diags.Add(Diag(lineNo, d.Severity, "In inserted '" + path + "': " + d.Message));
                }
            }
        }

        private static void CheckNpcInclude(string raw, int lineNo, ScriptValidationContext ctx, Dictionary<string, int> pages, List<ScriptDiagnostic> diags)
        {
            var split = raw.Split(' ');
            if (split.Length < 3)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "#INCLUDE needs a path and a page: #INCLUDE [Folder\\File.txt] @Page. The NPC loader throws on this line."));
                return;
            }

            var path = BracketPath(split[1]);
            if (path == null)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "#INCLUDE path '" + split[1] + "' is malformed; the NPC loader throws on it."));
                return;
            }

            if (ctx.ReadEnvirFile == null) return;
            var included = ctx.ReadEnvirFile(path);
            if (included == null)
            {
                diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "#INCLUDE target '" + path + "' was not found under Envir: the whole script then loads EMPTY."));
                return;
            }

            var page = ("[" + split[2] + "]").ToUpperInvariant();
            bool found = false, opened = false, closed = false;

            for (var j = 0; j < included.Length && !closed; j++)
            {
                if (!included[j].ToUpperInvariant().StartsWith(page, StringComparison.Ordinal)) continue;
                found = true;

                for (var x = j + 1; x < included.Length; x++)
                {
                    var t = included[x].Trim();
                    if (t == "{")
                    {
                        opened = true;
                        continue;
                    }

                    if (t == "}")
                    {
                        closed = true;
                        break;
                    }

                    if (included[x].StartsWith("[@", StringComparison.Ordinal))
                    {
                        var key = PageKey(HeaderKey(included[x].TrimEnd()));
                        if (!pages.ContainsKey(key)) pages[key] = 0;
                    }
                }
            }

            if (!found)
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Block " + page + " was not found in '" + path + "'; nothing is included."));
            else if (!opened || !closed)
                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Block " + page + " in '" + path + "' needs '{' and '}' around its lines; nothing is included."));
        }

        private static void CheckNpcSectionBody(string[] lines, int headerIndex, string section, ScriptValidationContext ctx, List<ScriptDiagnostic> diags)
        {
            for (var i = headerIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var lineNo = i + 1;
                if (line.StartsWith("[", StringComparison.Ordinal)) return;
                if (string.IsNullOrEmpty(line)) continue;

                switch (section)
                {
                    case "[TRADE]":
                    case "[RECIPE]":
                        {
                            var data = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (data.Length == 0) continue;
                            if (data[0].StartsWith(";", StringComparison.Ordinal))
                            {
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Info, "';' comments are not supported here; this line is looked up as an item and skipped."));
                                continue;
                            }

                            if (!ctx.HasItem(data[0]))
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown item '" + data[0] + "' in " + section + ": it is skipped."));
                            else if (section == "[TRADE]" && data.Length == 2 && !ushort.TryParse(data[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Count '" + data[1] + "' is not a number from 0 to 65535."));
                            else if (section == "[TRADE]" && data.Length > 2)
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Only '<Item> [count]' is read; the count is ignored when more follows."));
                            break;
                        }
                    case "[TYPES]":
                    case "[USEDTYPES]":
                        if (!int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        {
                            diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "'" + line.Trim() + "' is not a number: the " + section + " list stops here."));
                            return;
                        }
                        break;
                    case "[QUESTS]":
                        // Quest V2 wave 4: the section is no longer read (quest givers are questendpoints rows); one warning on its first line.
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "[QUESTS] is ignored since quest V2: set quest givers in the Quests page and remove this section."));
                        return;
                    case "[SPEECH]":
                        {
                            var parts = line.Split(' ');
                            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                            {
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Expected '<weight> <message>': the [SPEECH] list stops here."));
                                return;
                            }
                            break;
                        }
                }
            }
        }

        // ================================================================== recipes

        public static List<ScriptDiagnostic> ValidateRecipe(string[] lines, ScriptValidationContext ctx)
        {
            var diags = new List<ScriptDiagnostic>();

            if (!string.IsNullOrEmpty(ctx.FileName) && !ctx.HasItem(ctx.FileName))
                diags.Add(Diag(1, ScriptDiagnostic.Error, "No item is named '" + ctx.FileName + "': the file name must be the crafted item's name (without spaces). The recipe is not loaded."));

            var mode = "ingredients";
            var sawRecipe = false;
            var sawIngredient = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var lineNo = i + 1;
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    if (line.Length < 2)
                    {
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "A lone '[' makes the recipe loader throw."));
                        continue;
                    }

                    mode = line.Substring(1, line.Length - 2).ToLowerInvariant();
                    if (!line.EndsWith("]", StringComparison.Ordinal))
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Section headers must end with ']' (no trailing spaces): this is read as '" + mode + "'."));
                    else if (mode != "recipe" && mode != "tools" && mode != "ingredients" && mode != "criteria")
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown section [" + mode + "]: its lines are ignored. Known: [Recipe] [Tools] [Ingredients] [Criteria]."));
                    if (mode == "recipe") sawRecipe = true;
                    continue;
                }

                var data = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                switch (mode)
                {
                    case "recipe":
                        if (data.Length < 2) continue;
                        switch (data[0].ToLowerInvariant())
                        {
                            case "amount":
                                if (!ushort.TryParse(data[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Amount must be a number from 0 to 65535; the recipe loader throws on '" + data[1] + "'."));
                                break;
                            case "chance":
                                if (!byte.TryParse(data[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chance))
                                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Chance must be a number from 0 to 100; the recipe loader throws on '" + data[1] + "'."));
                                else if (chance > 100)
                                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Chance above 100 is capped at 100."));
                                break;
                            case "gold":
                                if (!uint.TryParse(data[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                                    diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "Gold must be a whole number; the recipe loader throws on '" + data[1] + "'."));
                                break;
                            default:
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown [Recipe] key '" + data[0] + "' (use Amount, Chance or Gold)."));
                                break;
                        }
                        break;
                    case "tools":
                    case "ingredients":
                        if (data.Length == 0)
                        {
                            diags.Add(Diag(lineNo, ScriptDiagnostic.Error, "A line with only spaces makes the recipe loader throw; delete it."));
                            continue;
                        }

                        if (!ctx.HasItem(data[0]))
                        {
                            diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown item '" + data[0] + "' in [" + mode + "]: it is skipped."));
                            continue;
                        }

                        if (mode == "ingredients")
                        {
                            sawIngredient = true;
                            if (data.Length >= 2 && !ushort.TryParse(data[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Count '" + data[1] + "' is not a number (it becomes 0)."));
                            if (data.Length >= 3 && !ushort.TryParse(data[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                                diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Durability '" + data[2] + "' is not a number (it becomes 0)."));
                        }
                        break;
                    case "criteria":
                        if (data.Length < 2) continue;
                        CheckRecipeCriteria(data, lineNo, diags);
                        break;
                }
            }

            if (!sawRecipe)
                diags.Add(Diag(1, ScriptDiagnostic.Info, "No [Recipe] section: Amount 1, Chance 100 and Gold 0 are used."));
            if (!sawIngredient)
                diags.Add(Diag(1, ScriptDiagnostic.Warning, "No known ingredients: NPCs that sell this recipe report it as having no ingredients and skip it."));

            return diags;
        }

        private static void CheckRecipeCriteria(string[] data, int lineNo, List<ScriptDiagnostic> diags)
        {
            var value = data[1];
            switch (data[0].ToLowerInvariant())
            {
                case "level":
                    if (!ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Level must be a number from 0 to 65535; this criterion is ignored."));
                    break;
                case "class":
                    if (!Enum.TryParse<MirClass>(value, true, out _) && !byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown class '" + value + "' (use " + string.Join(", ", Enum.GetNames(typeof(MirClass))) + "); this criterion is ignored."));
                    break;
                case "gender":
                    if (!Enum.TryParse<MirGender>(value, true, out _) && !byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown gender '" + value + "' (use " + string.Join(", ", Enum.GetNames(typeof(MirGender))) + "); this criterion is ignored."));
                    break;
                case "flag":
                case "quest":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "'" + value + "' is not a number; this criterion is ignored."));
                    break;
                default:
                    diags.Add(Diag(lineNo, ScriptDiagnostic.Warning, "Unknown [Criteria] key '" + data[0] + "' (use Level, Class, Gender, Flag or Quest)."));
                    break;
            }
        }
    }

    /// <summary>
    /// The ConfigForm "Remove / Re-add Assassin (Archer) drops" tool for one drop file: comments out (prefix ';') or restores
    /// drop lines whose item requires the class. Unlike ConfigForm it only touches lines shaped like drops ("1/N Item ...") and
    /// keeps indentation, so section comments such as ";Weapons" are never uncommented.
    /// </summary>
    public static class DropClassToggle
    {
        private static readonly Regex ChanceRegex = new Regex(@"^\d+/\d+$", RegexOptions.CultureInvariant);

        public static bool TryParseClass(string text, out RequiredClass cls)
        {
            cls = RequiredClass.None;
            if (string.Equals(text, "Assassin", StringComparison.OrdinalIgnoreCase))
            {
                cls = RequiredClass.Assassin;
                return true;
            }

            if (string.Equals(text, "Archer", StringComparison.OrdinalIgnoreCase))
            {
                cls = RequiredClass.Archer;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Applies the toggle to "\n"-separated text. <paramref name="classOfItem"/> returns the item's RequiredClass for an item
        /// token (as written in drop files) or null when unknown. <paramref name="disable"/> = comment the lines out.
        /// Returns the new text and the 1-based numbers of changed lines.
        /// </summary>
        public static string Apply(string content, Func<string, RequiredClass?> classOfItem, RequiredClass target, bool disable, out List<int> changedLines)
        {
            changedLines = new List<int>();
            var lines = ScriptText.NormalizeNewlines(content ?? string.Empty).Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var indent = line.Substring(0, line.Length - line.TrimStart().Length);
                var commented = trimmed.StartsWith(";", StringComparison.Ordinal);
                var working = commented ? trimmed.Substring(1).TrimStart() : trimmed;
                var parts = working.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 2 || !ChanceRegex.IsMatch(parts[0])) continue;

                var cls = classOfItem(parts[1]);
                if (cls == null) continue;
                if (cls.Value == RequiredClass.None) continue;
                if ((cls.Value & target) != target) continue;

                if (disable && !commented)
                {
                    lines[i] = indent + ";" + line.Substring(indent.Length);
                    changedLines.Add(i + 1);
                }
                else if (!disable && commented)
                {
                    lines[i] = indent + working + TrailingWhitespace(line);
                    changedLines.Add(i + 1);
                }
            }

            return string.Join("\n", lines);
        }

        private static string TrailingWhitespace(string line)
        {
            var trimmedEnd = line.TrimEnd();
            return line.Substring(trimmedEnd.Length);
        }
    }
}
