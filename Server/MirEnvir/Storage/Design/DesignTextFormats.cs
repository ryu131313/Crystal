using System.Globalization;
using System.Text.RegularExpressions;

namespace Server.MirEnvir.Storage
{
    // The four text design formats (roadmap P7 phase 7.4). Each types the lines whose canonical rendering is the line itself, with item ("I")
    // and monster ("M") names as Index links; every other line is kept verbatim (DesignTexts.Parse checks the round trip). The game keeps
    // reading the rendered text with its own parsers, so behaviour doesn't change.

    /// <summary>Envir\Routes\*.txt: <c>x,y[,delay]</c> per line (RouteInfo.FromText).</summary>
    internal sealed class RouteFormat : DesignTextFormat
    {
        private static readonly Regex Point = new Regex(@"^(-?\d+),(-?\d+)(?:,(-?\d+))?$", RegexOptions.CultureInvariant);

        public override string Kind => "routes";
        public override string Root => Settings.RoutePath;
        public override string Table => "route_points";

        public override (string Name, string SqlType)[] Columns { get; } =
        {
            ("x", "INTEGER"), ("y", "INTEGER"), ("delay", "INTEGER"), ("text", "TEXT"),
        };

        public override object[] Parse(string line, DesignNames names, ParseState state)
        {
            var m = Point.Match(line);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out var x) || !long.TryParse(m.Groups[2].Value, out var y)) return TextRow(line);
            object delay = null;
            if (m.Groups[3].Success)
            {
                if (!long.TryParse(m.Groups[3].Value, out var d)) return TextRow(line);
                delay = d;
            }

            return new object[] { x, y, delay, null };
        }

        public override string Render(object[] c, Func<string, long, string> nameOf)
        {
            if (c[3] is string text) return text;
            var s = Convert.ToString(c[0], CultureInfo.InvariantCulture) + "," + Convert.ToString(c[1], CultureInfo.InvariantCulture);
            return c[2] == null ? s : s + "," + Convert.ToString(c[2], CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Envir\Drops\**\*.txt (monster drops, the fishing / awakening / strongbox / blackstone lists, DragonItem.txt, NPC DROP lists):
    /// <c>1/N ItemName [Q|level]</c>, <c>1/N Gold amount</c>, <c>1/N GROUP[*|^]</c> with <c>{ }</c>, <c>#INSERT [file]</c>, comments
    /// (DropInfo.FromLine, DragonInfo). Item lines keep the item as an Index link and the rest of the line verbatim.
    /// </summary>
    internal sealed class DropFormat : DesignTextFormat
    {
        private static readonly Regex Line = new Regex(@"^1/(\d+)( +)(\S+)(.*)$", RegexOptions.CultureInvariant); // the game splits on any run of spaces
        private static readonly Regex GoldRest = new Regex(@"^ (\d+)$", RegexOptions.CultureInvariant);

        public override string Kind => "drops";
        public override string Root => Settings.DropPath;
        public override string Table => "drop_lines";

        public override (string Name, string SqlType)[] Columns { get; } =
        {
            ("line_kind", "TEXT"), ("chance", "INTEGER"), ("sep", "TEXT"), ("item_index", "INTEGER"), ("item_name", "TEXT"), ("gold", "INTEGER"),
            ("rest", "TEXT"), ("text", "TEXT"),
        };

        public override object[] Parse(string line, DesignNames names, ParseState state)
        {
            var m = Line.Match(line);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out var chance)) return TextRow(line);
            var sep = m.Groups[2].Value;
            var token = m.Groups[3].Value;
            var rest = m.Groups[4].Value;

            if (string.Equals(token, "Gold", StringComparison.OrdinalIgnoreCase)) // DropInfo.FromLine: case-insensitive, before any item
            {
                var g = GoldRest.Match(rest);
                if (!g.Success || !long.TryParse(g.Groups[1].Value, out var gold)) return TextRow(line);
                return new object[] { "gold", chance, sep, null, token, gold, null, null };
            }

            if (token.ToUpperInvariant().StartsWith("GROUP", StringComparison.Ordinal)) return new object[] { "group", chance, sep, null, token, null, rest, null };

            var item = names.Item(token);
            if (item == null)
            {
                state.Unresolved++;
                return TextRow(line);
            }

            return new object[] { "item", chance, sep, item.Value, token, null, rest, null };
        }

        public override string Render(object[] c, Func<string, long, string> nameOf)
        {
            if (c[7] is string text) return text;
            var chance = "1/" + Convert.ToString(c[1], CultureInfo.InvariantCulture) + (string)c[2];
            switch ((string)c[0])
            {
                case "gold": return chance + ((string)c[4] ?? "Gold") + " " + Convert.ToString(c[5], CultureInfo.InvariantCulture);
                case "group": return chance + (string)c[4] + (string)c[6];
                default: return chance + DesignTexts.CurrentName("I", c[3], (string)c[4], nameOf) + (string)c[6];
            }
        }
    }

    /// <summary>
    /// Lines in [sections], with an item or monster name as the first token in some sections, the rest of the line verbatim (quests,
    /// recipes). <see cref="RefKind"/> says which sections carry which reference.
    /// </summary>
    internal abstract class SectionFormat : DesignTextFormat
    {
        public override (string Name, string SqlType)[] Columns { get; } =
        {
            ("section", "TEXT"), ("ref_kind", "TEXT"), ("ref_index", "INTEGER"), ("ref_name", "TEXT"), ("rest", "TEXT"), ("text", "TEXT"),
        };

        /// <summary>The section a header line opens, or null when the line isn't a header.</summary>
        protected abstract string Header(string line);

        /// <summary>"I" / "M" when the first token of a line in <paramref name="section"/> names an item / monster, else null.</summary>
        protected abstract string RefKind(string section);

        /// <summary>The section in force before the first header.</summary>
        protected virtual string FirstSection => null;

        /// <summary>True when a line ends the current section without opening another (quests: "//").</summary>
        protected virtual bool EndsSection(string line) => false;

        public override object[] Parse(string line, DesignNames names, ParseState state)
        {
            state.Section ??= FirstSection;
            var header = Header(line);
            if (header != null)
            {
                state.Section = header;
                return Typed(header, null, null, null, null, line);
            }

            if (EndsSection(line)) state.Section = null;
            var kind = state.Section == null ? null : RefKind(state.Section);
            if (kind == null || line.Length == 0) return Typed(state.Section, null, null, null, null, line);

            var space = line.IndexOf(' ');
            var token = space < 0 ? line : line.Substring(0, space);
            var rest = space < 0 ? string.Empty : line.Substring(space);
            var index = token.Length == 0 ? null : kind == "I" ? names.Item(token) : names.Monster(token);
            if (index == null)
            {
                if (token.Length > 0) state.Unresolved++;
                return Typed(state.Section, null, null, null, null, line);
            }

            return Typed(state.Section, kind, index.Value, token, rest, null);
        }

        private static object[] Typed(string section, string kind, long? index, string name, string rest, string text) =>
            new object[] { section, kind, index, name, rest, text };

        public override string Render(object[] c, Func<string, long, string> nameOf)
        {
            if (c[5] is string text) return text;
            return DesignTexts.CurrentName((string)c[1], c[2], (string)c[3], nameOf) + (string)c[4];
        }
    }

    /// <summary>Envir\Quests\*.txt (QuestInfo.ParseFile): [@SECTION] headers; item tasks / rewards and kill tasks name items / monsters.</summary>
    internal sealed class QuestFormat : SectionFormat
    {
        public override string Kind => "quests";
        public override string Root => Settings.QuestPath;
        public override string Table => "quest_lines";

        private static readonly HashSet<string> ItemSections = new HashSet<string>(StringComparer.Ordinal)
            { "[@CARRYITEMS]", "[@ITEMTASKS]", "[@FIXEDREWARDS]", "[@SELECTREWARDS]" };

        protected override string Header(string line) => line.StartsWith("[", StringComparison.Ordinal) ? line.ToUpperInvariant() : null;

        protected override bool EndsSection(string line) => line.StartsWith("//", StringComparison.Ordinal);

        protected override string RefKind(string section) => section == "[@KILLTASKS]" ? "M" : ItemSections.Contains(section) ? "I" : null;
    }

    /// <summary>Envir\Recipe\*.txt (RecipeInfo): [recipe]/[tools]/[ingredients]/[criteria]; tool and ingredient lines name items.</summary>
    internal sealed class RecipeFormat : SectionFormat
    {
        public override string Kind => "recipes";
        public override string Root => Settings.RecipePath;
        public override string Table => "recipe_lines";

        protected override string FirstSection => "ingredients"; // RecipeInfo's mode before any header

        protected override string Header(string line) =>
            line.StartsWith("[", StringComparison.Ordinal) && line.Length >= 2 ? line.Substring(1, line.Length - 2).ToLowerInvariant() : null;

        protected override string RefKind(string section) => section == "tools" || section == "ingredients" ? "I" : null;
    }
}
