// Shared helpers of the HUD area (no single TS source): the formatters of WebClient/src/ui/modern/core/format.ts the HUD
// uses (fmtShort, fmtCompact, fmtDuration, classTag), belt.ts shortKey, the buff / poison icon tables of the classic
// BuffDialog (game/buffs.ts buffIcon / poisonColour on the web), scaled library drawing and the small animation clock.
using System.Drawing.Drawing2D;
using Client.MirGraphics;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal static class HudKit
    {
        public static string T(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);
        public static string T(ClientTextKeys key, params object[] args) => GameLanguage.ClientTextMap.GetLocalization(key, args);

        /// <summary>The key label of a binding ("" when unbound): CMain.InputKeys.GetKey.</summary>
        public static string KeyLabel(KeybindOptions option) => CMain.InputKeys?.GetKey(option) ?? string.Empty;

        /// <summary>belt.ts shortKey: a key label short enough for a 40 px slot ("Ctrl + F1" → "^F1", "Shift + 3" → "↑3" (the web's ⇧ is not in Segoe UI), "NumPad1" → "N1", "D1" → "1").</summary>
        public static string ShortKey(string label)
        {
            if (string.IsNullOrEmpty(label)) return string.Empty;
            string s = label.Replace("Ctrl + ", "^").Replace("Shift + ", "↑").Replace("Alt + ", "Alt").Replace("~ + ", "~");
            if (s.StartsWith("NumPad", StringComparison.Ordinal)) s = "N" + s.Substring(6);
            if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) s = s.Substring(1);
            return s;
        }

        // ---- formatting (core/format.ts) ----

        public static string Fmt(long n) => n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>fmtShort: seconds as one unit ("2d", "3h", "4m", "12s").</summary>
        public static string FmtShort(double secs)
        {
            if (secs >= 86400) return Math.Floor(secs / 86400) + "d";
            if (secs >= 3600) return Math.Floor(secs / 3600) + "h";
            if (secs >= 60) return Math.Floor(secs / 60) + "m";
            return Math.Max(0, Math.Ceiling(secs)) + "s";
        }

        /// <summary>fmtCompact: 950 → "950", 15000 → "15k", 2300000 → "2.3m".</summary>
        public static string FmtCompact(long v)
        {
            long a = Math.Abs(v);
            if (a < 10000) return Fmt(v);
            double d = a < 1000000 ? 1e3 : a < 1000000000 ? 1e6 : 1e9;
            string unit = a < 1000000 ? "k" : a < 1000000000 ? "m" : "b";
            double x = v / d;
            return (Math.Abs(x) < 100 ? x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : Fmt((long)Math.Round(x))) + unit;
        }

        /// <summary>fmtDuration: "1h 02m", "2m 05s", "42s", "1d 1h".</summary>
        public static string FmtDuration(double secs)
        {
            long s = Math.Max(0, (long)Math.Floor(secs));
            long d = s / 86400, hh = s / 3600 % 24, m = s / 60 % 60, ss = s % 60;
            if (d > 0) return d + "d " + hh + "h";
            if (hh > 0) return hh + "h " + m.ToString("00") + "m";
            if (m > 0) return m + "m " + ss.ToString("00") + "s";
            return ss + "s";
        }

        /// <summary>classTag: the three-letter class tag on avatars ("WAR", "WIZ", …).</summary>
        public static string ClassTag(MirClass cls)
        {
            switch (cls)
            {
                case MirClass.Warrior: return T(ClientTextKeys.ModernHudClassTagWarrior);
                case MirClass.Wizard: return T(ClientTextKeys.ModernHudClassTagWizard);
                case MirClass.Taoist: return T(ClientTextKeys.ModernHudClassTagTaoist);
                case MirClass.Assassin: return T(ClientTextKeys.ModernHudClassTagAssassin);
                case MirClass.Archer: return T(ClientTextKeys.ModernHudClassTagArcher);
                default: return "?";
            }
        }

        // ---- icons ----

        /// <summary>The classic BuffDialog's buff icon (BuffIcon.Lib; ≥ 20000 = MagIcon, ≥ 10000 = Prguse2).</summary>
        public static void BuffIcon(BuffType type, out MLibrary lib, out int index)
        {
            index = BuffImage(type);
            lib = Libraries.BuffIcon;
            if (index >= 20000)
            {
                index -= 20000;
                lib = Libraries.MagIcon;
            }
            if (index >= 10000)
            {
                index -= 10000;
                lib = Libraries.Prguse2;
            }
        }

        private static int BuffImage(BuffType type)
        {
            switch (type)
            {
                case BuffType.Fury: return 76;
                case BuffType.Rage: return 49;
                case BuffType.ImmortalSkin: return 80;
                case BuffType.CounterAttack: return 7;
                case BuffType.MagicBooster: return 73;
                case BuffType.MagicShield: return 30;
                case BuffType.Hiding:
                case BuffType.ClearRing: return 17;
                case BuffType.Haste: return 60;
                case BuffType.SoulShield: return 13;
                case BuffType.BlessedArmour: return 14;
                case BuffType.ProtectionField: return 50;
                case BuffType.UltimateEnhancer: return 35;
                case BuffType.Curse: return 45;
                case BuffType.EnergyShield: return 57;
                case BuffType.SwiftFeet: return 67;
                case BuffType.LightBody: return 68;
                case BuffType.MoonLight: return 65;
                case BuffType.DarkBody: return 70;
                case BuffType.Concentration: return 96;
                case BuffType.VampireShot: return 100;
                case BuffType.PoisonShot: return 102;
                case BuffType.MentalState: return 199;
                case BuffType.RhinoPriestDebuff: return 217;
                case BuffType.Blindness: return 226;
                case BuffType.GameMaster: return 173;
                case BuffType.General: return 182;
                case BuffType.Exp: return 260;
                case BuffType.Drop: return 162;
                case BuffType.Gold: return 168;
                case BuffType.Knapsack:
                case BuffType.BagWeight: return 235;
                case BuffType.Transform: return 241;
                case BuffType.Mentor:
                case BuffType.Mentee: return 248;
                case BuffType.Lover: return 201;
                case BuffType.Guild: return 203;
                case BuffType.Rested: return 240;
                case BuffType.TemporalFlux: return 261;
                case BuffType.Skill: return 200;
                case BuffType.Newbie: return 182;
                case BuffType.Impact: return 249;
                case BuffType.Magic: return 165;
                case BuffType.Taoist: return 250;
                case BuffType.Storm: return 170;
                case BuffType.HealthAid: return 161;
                case BuffType.ManaAid: return 169;
                case BuffType.Defence: return 166;
                case BuffType.MagicDefence: return 158;
                case BuffType.WonderDrug: return 252;
                default: return 0;
            }
        }

        /// <summary>game/buffs.ts poisonColour: the poison's draw colour (first match wins), or Empty.</summary>
        public static Color PoisonColour(PoisonType p)
        {
            if ((p & PoisonType.DelayedExplosion) != 0) return Color.FromArgb(255, 165, 0);
            if ((p & (PoisonType.Paralysis | PoisonType.LRParalysis)) != 0) return Color.FromArgb(128, 128, 128);
            if ((p & PoisonType.Frozen) != 0) return Color.FromArgb(0, 0, 255);
            if ((p & PoisonType.Blindness) != 0) return Color.FromArgb(199, 21, 133);
            if ((p & (PoisonType.Stun | PoisonType.Dazed)) != 0) return Color.FromArgb(255, 255, 0);
            if ((p & PoisonType.Slow) != 0) return Color.FromArgb(128, 0, 128);
            if ((p & PoisonType.Bleeding) != 0) return Color.FromArgb(139, 0, 0);
            if ((p & PoisonType.Red) != 0) return Color.FromArgb(255, 0, 0);
            if ((p & PoisonType.Green) != 0) return Color.FromArgb(0, 128, 0);
            return Color.Empty;
        }

        /// <summary>
        /// Draws a library image scaled into <paramref name="dest"/> (device px, already offset by the caller's DisplayLocation),
        /// tinted by <paramref name="colour"/> (the sprite colour: white icons take it as their colour). The scale and the
        /// position both go into the sprite transform, so the library's own screen culling sees (0, 0).
        /// </summary>
        public static void DrawScaled(MLibrary lib, int index, RectangleF dest, Color colour)
        {
            if (lib == null || index < 0 || dest.Width <= 0 || dest.Height <= 0) return;
            Size s = lib.GetSize(index);
            if (s.Width <= 0 || s.Height <= 0) return;
            float sx = dest.Width / s.Width, sy = dest.Height / s.Height;
            if (Math.Abs(sx - 1f) < 0.001f && Math.Abs(sy - 1f) < 0.001f)
            {
                lib.Draw(index, new Point((int)Math.Round(dest.X), (int)Math.Round(dest.Y)), colour);
                return;
            }
            DXManager.SetSpriteTransform(SlimDX.Matrix.Scaling(sx, sy, 1f) * SlimDX.Matrix.Translation(dest.X, dest.Y, 0f));
            try
            {
                lib.Draw(index, Point.Empty, colour);
            }
            finally
            {
                DXManager.SetSpriteTransform(SlimDX.Matrix.Identity);
            }
        }

        /// <summary>
        /// A library image scaled into <paramref name="dest"/> and cut to the ellipse inscribed in it: drawn as one 1 px high source
        /// run per row (sprites cannot clip; a mask over the corners would be translucent in a surface). Allocation free.
        /// </summary>
        public static void DrawCircle(MLibrary lib, int index, RectangleF dest, Color colour)
        {
            if (lib == null || index < 0 || dest.Width <= 0 || dest.Height <= 0) return;
            Size s = lib.GetSize(index);
            if (s.Width <= 0 || s.Height <= 0) return;
            float sx = dest.Width / s.Width, sy = dest.Height / s.Height;
            DXManager.SetSpriteTransform(SlimDX.Matrix.Scaling(sx, sy, 1f) * SlimDX.Matrix.Translation(dest.X, dest.Y, 0f));
            try
            {
                float cx = s.Width / 2f, cy = s.Height / 2f;
                for (int y = 0; y < s.Height; y++)
                {
                    float dy = (y + 0.5f - cy) / cy;
                    if (dy <= -1f || dy >= 1f) continue;
                    float half = cx * (float)Math.Sqrt(1f - dy * dy);
                    int x0 = Math.Max(0, (int)Math.Round(cx - half)), x1 = Math.Min(s.Width, (int)Math.Round(cx + half));
                    if (x1 <= x0) continue;
                    lib.Draw(index, new Rectangle(x0, y, x1 - x0, 1), new Point(x0, y), colour, false);
                }
            }
            finally
            {
                DXManager.SetSpriteTransform(SlimDX.Matrix.Identity);
            }
        }

        /// <summary>A stroke icon of ModernIcons.Lib (1x, or 2x when drawn larger than 24 px), tinted.</summary>
        public static void DrawIcon(int icon, RectangleF dest, Color colour)
        {
            if (icon < 0) return;
            DrawScaled(Libraries.ModernIcons, dest.Width > ModernIcons.IconSize ? icon + 1 : icon, dest, colour);
        }

        /// <summary>An image drawn 1:1 at HUD scale 1 (centred in <paramref name="box"/>), scaled with the HUD otherwise.</summary>
        public static void DrawCentred(MLibrary lib, int index, RectangleF box, Color colour)
        {
            if (lib == null || index < 0) return;
            Size s = lib.GetSize(index);
            if (s.Width <= 0 || s.Height <= 0) return;
            float k = ModernRoot.K, w = s.Width * k, h = s.Height * k;
            DrawScaled(lib, index, new RectangleF(box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h), colour);
        }

        // ---- GDI ----

        private static readonly Dictionary<int, Font> _serif = new Dictionary<int, Font>();

        /// <summary>A cached Georgia bold of a HUD px size (the level number); never pass it to MirLabel.Font.</summary>
        public static Font Serif(float hudPx)
        {
            int px = Math.Max(6, (int)Math.Round(hudPx * ModernRoot.K * 4));
            if (!_serif.TryGetValue(px, out Font f))
            {
                f = new Font("Georgia", px / 4f, FontStyle.Bold, GraphicsUnit.Pixel);
                _serif[px] = f;
            }
            return f;
        }

        public static GraphicsPath Polygon(params PointF[] points)
        {
            var p = new GraphicsPath();
            p.AddPolygon(points);
            return p;
        }

        /// <summary>Text with a soft black shadow (text-shadow: 0 0 2px #000): the 8 neighbours at low alpha, then the text.</summary>
        public static void ShadowText(Graphics g, string text, Font font, Color colour, RectangleF rect, StringAlignment h = StringAlignment.Center,
            StringAlignment v = StringAlignment.Center, int shadowAlpha = 150)
        {
            if (string.IsNullOrEmpty(text)) return;
            Color sh = Color.FromArgb(shadowAlpha, 0, 0, 0);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        ModernSkin.Text(g, text, font, sh, new RectangleF(rect.X + dx, rect.Y + dy, rect.Width, rect.Height), h, v, false);
            ModernSkin.Text(g, text, font, colour, rect, h, v, false);
        }

        /// <summary>
        /// A translucent colour for a fill that stays translucent in the final picture (nothing opaque below it in the same
        /// texture): a surface blends it twice (into its render target, then onto the screen), which squares the alpha and
        /// scales the colour by it; this returns the alpha's square root and the colour divided by it, so the result on
        /// screen matches the web's single blend.
        /// </summary>
        public static Color Sq(Color c)
        {
            if (c.A >= 255 || c.A == 0) return c;
            double a = Math.Sqrt(c.A / 255.0);
            return Color.FromArgb((int)Math.Round(a * 255), Math.Min(255, (int)Math.Round(c.R / a)), Math.Min(255, (int)Math.Round(c.G / a)), Math.Min(255, (int)Math.Round(c.B / a)));
        }

        public static Color Sq(Color c, float alpha) => Sq(Color.FromArgb((int)Math.Round(255 * Math.Clamp(alpha, 0f, 1f)), c.R, c.G, c.B));

        /// <summary>The pressed / hovered accent tint of HUD buttons (color-mix(in srgb, a w, b)).</summary>
        public static Color Mix(Color a, Color b, float weightA) => ModernTheme.Mix(a, b, weightA);
    }

    /// <summary>
    /// The bottom HUD's shared animation clock (16 Hz): HudRig.Process advances it once per frame and its time-driven
    /// animations (bar ease / trail / low-HP pulse / shake, plate glow, buff blink, level-up burst, floats, skill sweeps,
    /// cooldown digits, armed halo) step only on a tick, so their invalidations coalesce into at most 16 re-renders of the
    /// rig surface a second instead of one per frame (value changes — HP, buffs, items — still show at once).
    /// </summary>
    internal static class HudClock
    {
        public const int Hz = 16;
        private static long _bucket = long.MinValue;

        /// <summary>True during the frame a new 1/16 s step began (read after <see cref="Advance"/>).</summary>
        public static bool Ticked { get; private set; }

        public static void Advance(long now)
        {
            long b = now * Hz / 1000;
            Ticked = b != _bucket;
            _bucket = b;
        }
    }

    /// <summary>
    /// A short animation driven from a surface's Process: <see cref="Step"/> returns the current step (0..steps) of a run that
    /// started at <see cref="Start"/>, so callers change state only when the step changes (quantised, never per frame).
    /// </summary>
    internal struct HudAnim
    {
        public long Start;
        public int Duration;
        public int Last;

        public bool Running => Start > 0;

        public void Begin(int durationMs, long delayMs = 0)
        {
            Start = CMain.Time + delayMs;
            if (Start <= 0) Start = 1;
            Duration = Math.Max(1, durationMs);
            Last = -1;
        }

        public void Stop()
        {
            Start = 0;
            Last = -1;
        }

        /// <summary>The step (0..steps) now, -1 before the start; stops itself after the end (returns steps once).</summary>
        public int Step(int steps)
        {
            if (Start <= 0) return -1;
            long t = CMain.Time - Start;
            if (t < 0) return -1;
            if (t >= Duration)
            {
                Start = 0;
                return steps;
            }
            return (int)(t * steps / Duration);
        }
    }
}
