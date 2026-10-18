// Small pieces the phase 4 companion windows share (hero.ts, creatures.ts, mount.ts, fishing.ts CSS): the .bar3 / .bar2 / .cbar
// meters with a label and a value, the dashed .note box, the state chips, and a library picture fitted into a box. Area-specific
// (Hero / Creatures / Mount / Fishing windows only); the general helpers stay in WindowParts.cs.
using System.Drawing.Drawing2D;
using Client.MirGraphics;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;

namespace Client.MirScenes.Modern.Windows
{
    internal static class CompKit
    {
        /// <summary>A library picture scaled to fit inside <paramref name="box"/> (device px, aspect kept, centred, never grown
        /// past <paramref name="maxScale"/>). Render time only.</summary>
        public static void Fit(MLibrary lib, int index, RectangleF box, float maxScale = 8f)
        {
            if (lib == null || index < 0 || box.Width <= 0 || box.Height <= 0) return;
            Size s = lib.GetSize(index);
            if (s.Width <= 0 || s.Height <= 0) return;
            float k = Math.Min(maxScale, Math.Min(box.Width / s.Width, box.Height / s.Height));
            float w = s.Width * k, h = s.Height * k;
            HudKit.DrawScaled(lib, index, new RectangleF(box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h), Color.White);
        }

        /// <summary>The .chip badge: small bold upper-case text in a rounded box.</summary>
        public static void Chip(Graphics g, RectangleF r, string text, Color fg, Color border, Color fill, float fontHud = 8)
        {
            float k = ModernRoot.K;
            using (GraphicsPath p = ModernSkin.RoundRect(r, Math.Min(r.Height / 2f, 6 * k)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, border);
            }
            ModernSkin.Text(g, (text ?? string.Empty).ToUpperInvariant(), ModernSkin.Font(fontHud, true), fg, r, StringAlignment.Center, StringAlignment.Center, false);
        }

        /// <summary>The width a chip needs for its text (device px).</summary>
        public static int ChipWidth(string text, float fontHud = 8) =>
            (int)Math.Ceiling(ModernSkin.Measure((text ?? string.Empty).ToUpperInvariant(), ModernSkin.Font(fontHud, true))) + ModernRoot.S(12);

        /// <summary>The hero state chip colours (.chip / .chip.Summoned / .chip.Fallen).</summary>
        public static void StateColours(int state, out Color fg, out Color border, out Color fill)
        {
            Color pb = ModernSkin.Opaque(ModernTheme.PanelB);
            switch (state)
            {
                case 1: fg = Color.White; border = ModernTheme.Accent; fill = ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Accent), pb, 0.45f); break;
                case 2: fg = Color.White; border = ModernTheme.Bad; fill = ModernTheme.Mix(ModernTheme.Bad, pb, 0.4f); break;
                default: fg = ModernTheme.Text; border = ModernTheme.Frame; fill = pb; break;
            }
        }
    }

    /// <summary>
    /// A labelled bar (.bar3 / .bar2 / .cbar): a dark well, a gradient fill, the label on the left and the value on the right (or
    /// only the fill when both are empty). Repaints only when the filled width or a text changes.
    /// </summary>
    internal sealed class CompMeter : ModernControl
    {
        private string _label = string.Empty, _value = string.Empty;
        private int _px = -1;
        private float _fraction;
        private Color _a, _b;
        private readonly bool _horizontal;

        public CompMeter(int widthHud, int heightHud, Color a, Color b, bool horizontalGradient = false)
        {
            Size = new Size(S(widthHud), S(heightHud));
            _a = a;
            _b = b;
            _horizontal = horizontalGradient;
            NotControl = true;
        }

        public void SetColours(Color a, Color b)
        {
            if (a == _a && b == _b) return;
            _a = a;
            _b = b;
            Invalidate();
        }

        public void Set(float fraction, string label, string value)
        {
            if (float.IsNaN(fraction) || float.IsInfinity(fraction)) fraction = 0;
            fraction = Math.Clamp(fraction, 0f, 1f);
            label ??= string.Empty;
            value ??= string.Empty;
            int px = (int)Math.Round(fraction * (Size.Width - 2));
            _fraction = fraction;
            if (px == _px && label == _label && value == _value) return;
            _px = px;
            _label = label;
            _value = value;
            Invalidate();
        }

        public float Fraction => _fraction;
        public string ValueText => _value;

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            float rad = Math.Min(r.Height / 2f, 4 * k);
            using GraphicsPath p = ModernSkin.RoundRect(r, rad);
            using (var b = new SolidBrush(Color.FromArgb(4, 6, 10))) g.FillPath(b, p);
            if (_px > 0)
            {
                Region old = g.Clip;
                g.SetClip(p, CombineMode.Intersect);
                RectangleF fill = new RectangleF(r.X, r.Y, Math.Min(r.Width, _px + 1), r.Height);
                using (var b = new LinearGradientBrush(_horizontal ? new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height) : new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height + 1),
                    _a, _b, _horizontal ? LinearGradientMode.Horizontal : LinearGradientMode.Vertical))
                    g.FillRectangle(b, fill);
                g.Clip = old;
                old.Dispose();
            }
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
            if (_label.Length == 0 && _value.Length == 0) return;
            Font f = ModernSkin.Font(Math.Max(8f, Math.Min(10f, Size.Height / k - 4)), true);
            RectangleF t = new RectangleF(6 * k, 0, Size.Width - 12 * k, Size.Height);
            for (int pass = 0; pass < 2; pass++)
            {
                Color c = pass == 0 ? Color.FromArgb(200, 0, 0, 0) : Color.White;
                RectangleF tr = pass == 0 ? new RectangleF(t.X, t.Y + 1, t.Width, t.Height) : t;
                if (_label.Length > 0) ModernSkin.Text(g, _label, f, c, tr, StringAlignment.Near, StringAlignment.Center, false);
                if (_value.Length > 0) ModernSkin.Text(g, _value, f, c, tr, _label.Length > 0 ? StringAlignment.Far : StringAlignment.Center, StringAlignment.Center, false);
            }
        }
    }

    /// <summary>The dashed .note box: dim wrapped text (the "summon first" notes, the empty states).</summary>
    internal sealed class CompNote : ModernControl
    {
        private string _text = string.Empty;
        private readonly bool _box;

        public CompNote(int widthHud, int heightHud, bool box = true)
        {
            Size = new Size(S(widthHud), S(heightHud));
            _box = box;
            NotControl = true;
        }

        public string Text
        {
            get => _text;
            set
            {
                value ??= string.Empty;
                if (_text == value) return;
                _text = value;
                Invalidate();
            }
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            if (_box)
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using var pen = new Pen(W.Over(ModernTheme.Frame, 0.7f), 1f) { DashStyle = DashStyle.Dash };
                g.DrawPath(pen, p);
            }
            float pad = _box ? 8 * k : 0;
            ModernSkin.Text(g, _text, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(pad, _box ? 5 * k : 0, Size.Width - pad * 2, Size.Height - (_box ? 10 * k : 0)),
                StringAlignment.Near, _box ? StringAlignment.Center : StringAlignment.Near, false, true);
        }
    }
}
