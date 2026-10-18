namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// Small parse / format helpers for the modern UI's [Interface] preference strings (Settings.UiChat, UiMinimap, UiBigMap,
    /// UiWaypoints, UiHelpRead, UiTrackerFolded; phase 4). Values are comma / semicolon / bar separated and never contain '='
    /// (the ini reader splits on it). Parsing never throws: a bad value falls back to the caller's default.
    /// </summary>
    internal static class HudPrefs
    {
        /// <summary>The comma-separated ints of a pref string (missing / bad entries = null).</summary>
        public static int?[] Ints(string s, int count)
        {
            var result = new int?[count];
            if (string.IsNullOrEmpty(s)) return result;
            string[] parts = s.Split(',');
            for (int i = 0; i < count && i < parts.Length; i++)
                if (int.TryParse(parts[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v))
                    result[i] = v;
            return result;
        }

        public static string Join(params int[] values)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>A comma-separated list of names / ids (empty entries dropped).</summary>
        public static List<string> Names(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (string part in s.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>Drops the separators a value must not carry (',', ';', '|', '=' and line breaks).</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (c != ',' && c != ';' && c != '|' && c != '=' && c != '\r' && c != '\n') sb.Append(c);
            return sb.ToString();
        }
    }
}
