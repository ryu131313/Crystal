namespace Client.MirScenes.Modern.Core
{
    /// <summary>The base values one theme sets (port of WebClient/src/ui/modern/core/theme.ts ThemeColors).</summary>
    public sealed class ThemePreset
    {
        public readonly string Name;
        /// <summary>Panel hue (0-360), saturation (0-2) and brightness (0.3-2.5) multipliers.</summary>
        public readonly float Hue, Sat, Lit;
        public readonly Color Accent, Frame, Text;
        /// <summary>Glow strength (0-2).</summary>
        public readonly float Glow;
        /// <summary>Corner radius in HUD px (0-12).</summary>
        public readonly float Radius;

        public ThemePreset(string name, float hue, float sat, float lit, string accent, string frame, string text, float glow, float radius = 5)
        {
            Name = name;
            Hue = hue;
            Sat = sat;
            Lit = lit;
            Accent = ModernTheme.Hex(accent);
            Frame = ModernTheme.Hex(frame);
            Text = ModernTheme.Hex(text);
            Glow = glow;
            Radius = radius;
        }
    }

    /// <summary>
    /// The new UI's colours (port of core/theme.ts + the derived / semantic tokens of core/styles.ts ROOT_CSS). The 17 presets
    /// (names are saved in Settings.UiTheme: rename never, append freely), the custom overrides (Settings.UiCustom*), and every
    /// colour the kit draws with, recomputed by <see cref="Apply"/>. Changing the theme raises <see cref="Changed"/>; the window
    /// manager then repaints every window (chrome is cached per window, never per frame).
    /// </summary>
    public static class ModernTheme
    {
        public static readonly ThemePreset[] Presets =
        {
            new ThemePreset("Midnight electric", 218, 1, 1, "#3fb4ff", "#3a5a8c", "#d6e2f5", 1),
            new ThemePreset("Abyss", 222, 0.8f, 0.65f, "#00c8ff", "#24395e", "#c8d6ec", 1.2f),
            new ThemePreset("Neon", 224, 1.3f, 0.85f, "#19e3ff", "#1f4f9a", "#e0f4ff", 1.6f),
            new ThemePreset("Royal", 230, 1.1f, 1.15f, "#5c8dff", "#4a5fa8", "#e2e8ff", 1),
            new ThemePreset("Steel", 214, 0.45f, 1.1f, "#7cc4ff", "#5a6c86", "#dce4ee", 0.7f),
            new ThemePreset("Ice", 205, 0.6f, 1.3f, "#a6ecff", "#5d86a8", "#eef8ff", 0.9f),
            new ThemePreset("Royal gold", 224, 1.25f, 1.2f, "#e8b84a", "#8a6a2e", "#f2ead6", 0.9f),
            new ThemePreset("Dark brown / red", 14, 0.55f, 0.9f, "#c8402e", "#5e3a2a", "#ead8cc", 0.8f),
            new ThemePreset("Lost Ark red", 356, 0.8f, 0.9f, "#ff3b3b", "#6a2a30", "#f0dada", 1),
            new ThemePreset("Emerald", 152, 0.9f, 0.9f, "#2fe39a", "#2a6a52", "#d8f2e6", 1.1f),
            new ThemePreset("Forest", 112, 0.55f, 0.85f, "#9ad35a", "#4a6a34", "#e2ecd4", 0.8f),
            new ThemePreset("Toxic", 84, 1.1f, 0.8f, "#c6ff3a", "#4e6a1e", "#eef8d4", 1.4f),
            new ThemePreset("Amethyst", 276, 0.85f, 0.95f, "#b67cff", "#5a3c8a", "#ece2fa", 1.1f),
            new ThemePreset("Sakura", 332, 0.7f, 1.05f, "#ff7ab8", "#8a4266", "#fae4ee", 1),
            new ThemePreset("Ember", 22, 0.95f, 0.9f, "#ff8a2e", "#7a4424", "#f6e4d4", 1.2f),
            new ThemePreset("Desert sand", 38, 0.5f, 1.15f, "#e6c27a", "#7a6646", "#f4ecdc", 0.7f),
            new ThemePreset("Obsidian", 220, 0.08f, 0.8f, "#e6e6e6", "#4a4a4e", "#e4e4e6", 0.5f),
        };

        public const string DefaultTheme = "Midnight electric";

        // Knob ranges (theme.ts THEME_RANGES).
        public const float HueMax = 360, SatMax = 2, LitMin = 0.3f, LitMax = 2.5f, GlowMax = 2, RadiusMax = 12;

        // ---- Base (resolved) ----
        public static string Name { get; private set; } = DefaultTheme;
        public static float Hue, Sat, Lit, Glow, Radius;
        public static Color Accent, Frame, Text;

        // ---- Derived (styles.ts ROOT_CSS) ----
        /// <summary>--pa / --pb / --pc: the three panel tones.</summary>
        public static Color PanelA, PanelB, PanelC;
        /// <summary>--frl / --frd: frame light and dark.</summary>
        public static Color FrameLight, FrameDark;
        /// <summary>--acd: accent toward the text colour; --dim: label colour.</summary>
        public static Color AccentDim, Dim;
        /// <summary>--glow: the accent at the glow strength's alpha.</summary>
        public static Color GlowColour;
        /// <summary>--field: text field background.</summary>
        public static readonly Color Field = Hex("#05070b");

        // ---- Semantic ----
        public static readonly Color Ok = Hex("#6fe08a"), Bad = Hex("#ff5a4a"), BadSoft = Hex("#ff8a7a"), Warn = Hex("#f3c04a"),
            Gold = Hex("#f3e08a"), Info = Hex("#7cc4ff");

        private static readonly Color[] GradeColours =
        {
            Hex("#f3e08a"), Hex("#f3e08a"), Hex("#3fbfff"), Hex("#ff9a3c"), Hex("#e0a0ff"), Hex("#ff5a5a"),
        };
        private static readonly Color[] ClassColours =
        {
            Hex("#e0714f"), Hex("#6aa8ff"), Hex("#6fd38a"), Hex("#c58bff"), Hex("#e6c25a"),
        };

        /// <summary>Theme or scale changed: every window repaints its chrome.</summary>
        public static event Action Changed;

        /// <summary>A bumped number per change, so cached chrome knows it is stale.</summary>
        public static int Version { get; private set; }

        static ModernTheme()
        {
            Apply(DefaultTheme, null);
        }

        public static ThemePreset Preset(string name)
        {
            for (int i = 0; i < Presets.Length; i++)
                if (Presets[i].Name == name) return Presets[i];
            return Presets[0];
        }

        /// <summary>The colour of an ItemGrade (0 None … 5 Heroic).</summary>
        public static Color Grade(ItemGrade grade)
        {
            int i = (int)grade;
            return i >= 0 && i < GradeColours.Length ? GradeColours[i] : GradeColours[0];
        }

        /// <summary>The colour of a MirClass.</summary>
        public static Color Class(MirClass cls)
        {
            int i = (int)cls;
            return i >= 0 && i < ClassColours.Length ? ClassColours[i] : ClassColours[0];
        }

        /// <summary>Overrides on top of a preset. A negative number or null colour = not overridden.</summary>
        public sealed class Custom
        {
            public float Hue = -1, Sat = -1, Lit = -1, Glow = -1, Radius = -1;
            public Color? Accent, Frame, Text;
        }

        /// <summary>Resolves preset + overrides (numbers clamped) and recomputes every derived colour.</summary>
        public static void Apply(string preset, Custom custom)
        {
            ThemePreset p = Preset(preset);
            Name = p.Name;
            Hue = p.Hue; Sat = p.Sat; Lit = p.Lit; Glow = p.Glow; Radius = p.Radius;
            Accent = p.Accent; Frame = p.Frame; Text = p.Text;

            if (custom != null)
            {
                if (custom.Hue >= 0) Hue = Math.Min(HueMax, custom.Hue);
                if (custom.Sat >= 0) Sat = Math.Min(SatMax, custom.Sat);
                if (custom.Lit >= 0) Lit = Math.Clamp(custom.Lit, LitMin, LitMax);
                if (custom.Glow >= 0) Glow = Math.Min(GlowMax, custom.Glow);
                if (custom.Radius >= 0) Radius = Math.Min(RadiusMax, custom.Radius);
                if (custom.Accent.HasValue) Accent = custom.Accent.Value;
                if (custom.Frame.HasValue) Frame = custom.Frame.Value;
                if (custom.Text.HasValue) Text = custom.Text.Value;
            }

            PanelA = Hsl(Hue, 0.45f * Sat, 0.15f * Lit, 0.94f);
            PanelB = Hsl(Hue, 0.55f * Sat, 0.065f * Lit, 0.95f);
            PanelC = Hsl(Hue, 0.40f * Sat, 0.18f * Lit, 0.95f);
            FrameLight = Mix(Frame, Color.White, 0.55f);
            FrameDark = Mix(Frame, Color.Black, 0.45f);
            AccentDim = Mix(Accent, Text, 0.70f);
            Dim = Mix(Text, Frame, 0.55f);
            GlowColour = Color.FromArgb((int)(255 * Math.Min(1f, 0.6f * Glow)), Accent);

            Version++;
            Changed?.Invoke();
        }

        /// <summary>Applies the theme saved in Settings (UiTheme + UiCustom*).</summary>
        public static void ApplySettings()
        {
            Apply(Settings.UiTheme, new Custom
            {
                Hue = Settings.UiCustomHue,
                Sat = Settings.UiCustomSat,
                Lit = Settings.UiCustomLit,
                Glow = Settings.UiCustomGlow,
                Radius = Settings.UiCustomRadius,
                Accent = TryHex(Settings.UiCustomAccent),
                Frame = TryHex(Settings.UiCustomFrame),
                Text = TryHex(Settings.UiCustomText),
            });
        }

        // ---- Colour helpers ----

        /// <summary>CSS color-mix(in srgb, a weight, b): weight of a, the rest b (alpha mixed too).</summary>
        public static Color Mix(Color a, Color b, float weightA)
        {
            float w = Math.Clamp(weightA, 0f, 1f), v = 1f - w;
            return Color.FromArgb(
                (int)Math.Round(a.A * w + b.A * v),
                (int)Math.Round(a.R * w + b.R * v),
                (int)Math.Round(a.G * w + b.G * v),
                (int)Math.Round(a.B * w + b.B * v));
        }

        /// <summary>The colour at a new alpha (0-1).</summary>
        public static Color Alpha(Color c, float alpha) => Color.FromArgb((int)Math.Round(255 * Math.Clamp(alpha, 0f, 1f)), c.R, c.G, c.B);

        /// <summary>CSS hsl(h s l / a) with s and l in 0-1 (clamped, like CSS).</summary>
        public static Color Hsl(float h, float s, float l, float a = 1f)
        {
            s = Math.Clamp(s, 0f, 1f);
            l = Math.Clamp(l, 0f, 1f);
            h = ((h % 360f) + 360f) % 360f / 360f;

            float r, g, b;
            if (s == 0) r = g = b = l;
            else
            {
                float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
                float p = 2 * l - q;
                r = HueToRgb(p, q, h + 1f / 3f);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1f / 3f);
            }
            return Color.FromArgb((int)Math.Round(a * 255), (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
        }

        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1f / 6f) return p + (q - p) * 6 * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
            return p;
        }

        public static Color Hex(string hex)
        {
            return TryHex(hex) ?? Color.White;
        }

        /// <summary>"#rrggbb" → colour, or null when not that form.</summary>
        public static Color? TryHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return null;
            if (!int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int n)) return null;
            return Color.FromArgb(255, (n >> 16) & 255, (n >> 8) & 255, n & 255);
        }

        public static string ToHex(Color c) => "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
    }
}
