using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// GDI+ drawing for the new UI's chrome (Desktop-Modern-UI-Plan "ModernSkin"): rounded rectangles, the panel / title /
    /// button gradients and frames of core/styles.ts, glow rings, and antialiased Segoe UI text. Everything here runs only when a
    /// control repaints its cached texture (ModernControl.Paint), never per frame. Coordinates are in device pixels: callers
    /// scale HUD px with ModernRoot.S / ModernRoot.K first. Every GDI+ object made here is disposed (fonts are cached for the
    /// process lifetime by size and style).
    /// </summary>
    public static class ModernSkin
    {
        public const string FontName = "Segoe UI";

        private static readonly Dictionary<(int, FontStyle), Font> _fonts = new Dictionary<(int, FontStyle), Font>();

        /// <summary>A cached Segoe UI font of a HUD px size at the current scale. Never pass it to MirLabel.Font (that disposes it).</summary>
        public static Font Font(float hudPx, bool bold = false)
        {
            int px = Math.Max(6, (int)Math.Round(hudPx * ModernRoot.K * 4)); // quarter-pixel buckets
            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            if (!_fonts.TryGetValue((px, style), out Font font))
            {
                font = new Font(FontName, px / 4f, style, GraphicsUnit.Pixel);
                _fonts[(px, style)] = font;
            }
            return font;
        }

        /// <summary>Graphics settings for every paint: antialiased shapes and text with proper alpha on a transparent target.</summary>
        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>A path with only the top corners rounded (title bars).</summary>
        public static GraphicsPath RoundTop(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height * 2));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
            return path;
        }

        /// <summary>Fills a path with a vertical gradient through evenly or explicitly placed stops.</summary>
        public static void FillVertical(Graphics g, GraphicsPath path, RectangleF bounds, Color[] colours, float[] positions = null)
        {
            if (bounds.Height <= 0 || bounds.Width <= 0) return;
            RectangleF b = new RectangleF(bounds.X, bounds.Y - 0.5f, bounds.Width, bounds.Height + 1f);
            using var brush = new LinearGradientBrush(b, colours[0], colours[^1], LinearGradientMode.Vertical);
            if (colours.Length > 2 || positions != null)
            {
                float[] pos = positions;
                if (pos == null)
                {
                    pos = new float[colours.Length];
                    for (int i = 0; i < pos.Length; i++) pos[i] = i / (float)(pos.Length - 1);
                }
                brush.InterpolationColors = new ColorBlend { Colors = colours, Positions = pos };
            }
            g.FillPath(brush, path);
        }

        /// <summary>A horizontal line that fades out at both ends (the title accent line).</summary>
        public static void FadedLine(Graphics g, float x, float y, float width, float height, Color colour)
        {
            if (width <= 0 || height <= 0) return;
            RectangleF r = new RectangleF(x, y, width, height);
            using var brush = new LinearGradientBrush(new RectangleF(x - 1, y, width + 2, height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.Transparent, ModernTheme.Alpha(colour, 0.35f), colour, ModernTheme.Alpha(colour, 0.35f), Color.Transparent },
                Positions = new[] { 0f, 0.22f, 0.5f, 0.78f, 1f },
            };
            g.FillRectangle(brush, r);
        }

        public static void Stroke(Graphics g, GraphicsPath path, Color colour, float width = 1f)
        {
            using var pen = new Pen(colour, width);
            g.DrawPath(pen, path);
        }

        /// <summary>A soft glow around a rounded rectangle, drawn as rings of falling alpha (no blur pass).</summary>
        public static void Glow(Graphics g, RectangleF r, float radius, Color colour, float spread)
        {
            int rings = Math.Max(1, (int)Math.Round(spread));
            for (int i = rings; i >= 1; i--)
            {
                float t = i / (float)rings;
                Color c = ModernTheme.Alpha(colour, colour.A / 255f * (1f - t) * 0.5f);
                if (c.A == 0) continue;
                RectangleF rr = RectangleF.Inflate(r, i, i);
                using GraphicsPath p = RoundRect(rr, radius + i);
                Stroke(g, p, c, 1.5f);
            }
        }

        /// <summary>
        /// The window page (styles.ts --page): pb toward pa at the top, toward black at the bottom. Opaque on purpose: the page is
        /// drawn into the window's transparent render target and that target is alpha-blended again, so a 0.95 alpha would end up
        /// near 0.90 with darkened colours; opaque here lands close to the web's look.
        /// </summary>
        public static Color[] PageColours()
        {
            Color pb = Opaque(ModernTheme.PanelB), pa = Opaque(ModernTheme.PanelA);
            return new[]
            {
                ModernTheme.Mix(pb, pa, 0.85f),
                ModernTheme.Mix(pb, Color.Black, 0.68f),
                ModernTheme.Mix(pb, Color.Black, 0.42f),
                ModernTheme.Mix(pb, Color.Black, 0.25f),
            };
        }

        public static Color Opaque(Color c) => Color.FromArgb(255, c.R, c.G, c.B);

        /// <summary>
        /// A disabled / faded colour: blended toward the window page instead of made transparent. A translucent pixel drawn into
        /// a window's render target lowers that target's alpha, so the world would show through the window there.
        /// </summary>
        public static Color Dim(Color c, float amount)
        {
            if (amount >= 1f) return c;
            Color page = ModernTheme.Mix(Opaque(ModernTheme.PanelB), Color.Black, 0.55f);
            Color mixed = ModernTheme.Mix(Opaque(c), page, amount);
            return Color.FromArgb(c.A, mixed.R, mixed.G, mixed.B);
        }

        public static readonly float[] PagePositions = { 0f, 0.3f, 0.7f, 1f };

        // ---- Text ----

        /// <summary>Draws text in a rectangle with GDI+ antialiasing (no outline: the new UI's text style).</summary>
        public static void Text(Graphics g, string text, Font font, Color colour, RectangleF rect, StringAlignment h = StringAlignment.Near,
            StringAlignment v = StringAlignment.Center, bool ellipsis = true, bool wrap = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = h,
                LineAlignment = v,
                Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
            };
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            if (!wrap) format.FormatFlags |= StringFormatFlags.NoWrap;
            using var brush = new SolidBrush(colour);
            g.DrawString(text, font, brush, rect, format);
        }

        /// <summary>Text with extra spacing between letters (the title bar's letter-spacing), centred in rect.</summary>
        public static void SpacedText(Graphics g, string text, Font font, Color colour, RectangleF rect, float spacing)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var format = new StringFormat(StringFormat.GenericTypographic);
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            float total = 0;
            float[] widths = new float[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                widths[i] = g.MeasureString(text[i].ToString(), font, PointF.Empty, format).Width;
                total += widths[i] + (i < text.Length - 1 ? spacing : 0);
            }
            float x = rect.X + (rect.Width - total) / 2f;
            float y = rect.Y + (rect.Height - font.GetHeight(g)) / 2f;
            using var brush = new SolidBrush(colour);
            for (int i = 0; i < text.Length; i++)
            {
                g.DrawString(text[i].ToString(), font, brush, x, y, format);
                x += widths[i] + spacing;
            }
        }

        /// <summary>The width a string needs (device px), measured with a throwaway 1x1 graphics.</summary>
        public static float Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            using var bmp = new Bitmap(1, 1);
            using Graphics g = Graphics.FromImage(bmp);
            Prepare(g);
            using var format = new StringFormat(StringFormat.GenericTypographic);
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return g.MeasureString(text, font, PointF.Empty, format).Width;
        }

        /// <summary>The size wrapped text needs in a column of <paramref name="width"/> device px (layout time only).</summary>
        public static SizeF MeasureWrapped(string text, Font font, float width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return SizeF.Empty;
            using var bmp = new Bitmap(1, 1);
            using Graphics g = Graphics.FromImage(bmp);
            Prepare(g);
            using var format = new StringFormat(StringFormat.GenericTypographic);
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            return g.MeasureString(text, font, new SizeF(width, 100000f), format);
        }
    }
}
