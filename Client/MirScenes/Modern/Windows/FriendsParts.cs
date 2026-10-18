// Shared pieces of the phase 4 social windows (group, trade, friends, mentor, relationship, quests): the class medal / badge of
// the web CSS (.av / .med / .badge: a radial pc → pb disc ringed in the class colour, an online dot), pills, the boxed
// section labels with an icon (.lbl.sl), checklist lines (.ck), the in-view player lookup and the chat prefill. Area B only.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;

namespace Client.MirScenes.Modern.Windows
{
    internal static class Soc
    {
        public static ModernUi Ui => GameScene.Ui as ModernUi;

        /// <summary>The window manager, or null outside the modern UI.</summary>
        public static WindowManager Wm => ModernRoot.Instance?.Wm;

        public static string Tag(MirClass cls) => HudKit.ClassTag(cls);

        public static string ClassName(MirClass cls) => cls.ToLocalizedString();

        public static string Me => GameScene.User?.Name ?? string.Empty;

        public static ServerPackets.ServerRules Rules => GameScene.Models?.ServerRules.Rules;

        /// <summary>chat.ts prefill: opens the HUD chat input with the text (whisper "/name ", mentor "!#", lover ":)").</summary>
        public static void Prefill(string text)
        {
            ChatHud chat = Ui?.Chat;
            if (chat != null && !chat.IsDisposed) chat.Prefill(text);
        }

        /// <summary>A player of that name on screen (not a hero, not the user), or null. Walks the object list: call it on a
        /// tick or a change, never per frame.</summary>
        public static PlayerObject InView(string name)
        {
            if (string.IsNullOrEmpty(name) || GameScene.Scene?.MapControl == null) return null;
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is PlayerObject p && !(p is HeroObject) && !(p is UserObject) && p.Race == ObjectType.Player && p.Name == name) return p;
            return null;
        }

        /// <summary>The current map's title ("" before the first map).</summary>
        public static string MapTitle => GameScene.Scene?.MapControl?.Title ?? string.Empty;

        public static int MapIndex => GameScene.Scene?.MapControl?.Index ?? -1;

        /// <summary>fmtDate: a short date.</summary>
        public static string Date(DateTime d) => d.ToString("d MMM yyyy");

        /// <summary>fmtTimeOfDay: HH:mm.</summary>
        public static string Clock(DateTime d) => d.ToString("HH:mm");

        /// <summary>A DateTime.ToBinary value, or DateTime.MinValue when it does not decode.</summary>
        public static DateTime FromBinary(long v)
        {
            if (v == 0) return DateTime.MinValue;
            try
            {
                return DateTime.FromBinary(v);
            }
            catch (ArgumentException)
            {
                return DateTime.MinValue;
            }
        }

        // ---- painting (Paint time only; GDI objects disposed) ----

        /// <summary>
        /// The class medal (.av / .med / .badge): a disc with the pc → pb radial fill ringed in <paramref name="ring"/>, the tag
        /// in the middle, and an online dot at the lower right (<paramref name="dot"/>: -1 none, 0 offline, 1 online).
        /// </summary>
        public static void Medal(Graphics g, RectangleF r, string tag, Color ring, int dot = -1, bool off = false, float ringWidth = 1f, float font = 10, Color? textColour = null)
        {
            float k = ModernRoot.K;
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(r);
                using var br = new PathGradientBrush(p) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } };
                br.CenterPoint = new PointF(r.X + r.Width / 2, r.Y + r.Height * 0.35f);
                g.FillEllipse(br, r);
            }
            using (var pen = new Pen(off ? ModernTheme.Frame : ring, Math.Max(1f, ringWidth * k))) g.DrawEllipse(pen, r);
            Color tc = textColour ?? (off ? ModernTheme.Dim : ring == ModernTheme.Frame ? ModernTheme.Text : ring);
            ModernSkin.Text(g, tag, ModernSkin.Font(font, true), tc, r, StringAlignment.Center, StringAlignment.Center, false);
            if (dot < 0) return;
            float d = Math.Max(6f, r.Width * 0.28f);
            RectangleF dr = new RectangleF(r.Right - d * 0.85f, r.Bottom - d * 0.85f, d, d);
            using (var br = new SolidBrush(dot > 0 ? ModernTheme.Ok : Color.FromArgb(0x59, 0x60, 0x6e))) g.FillEllipse(br, dr);
            using (var pen = new Pen(ModernSkin.Opaque(ModernTheme.PanelB), Math.Max(1.5f, 2f * k))) g.DrawEllipse(pen, dr);
        }

        /// <summary>
        /// The banner's person block (mentor.ts / relationship.ts .who + .med): a 56 px medal (class ring, or the frame when
        /// off / unknown) with a black outer ring, an online dot unless it is you, an optional tag pill over the medal ("MENTOR"),
        /// then "YOU", the name and up to two small lines beside it; <paramref name="right"/> mirrors it (medal on the right).
        /// </summary>
        public static void Who(Graphics g, RectangleF area, bool right, string medal, Color ring, bool online, bool you, string tag, string name, string sub, string sub2, bool dot = true)
        {
            float k = ModernRoot.K, d = 56 * k;
            float mx = right ? area.Right - d - 3 * k : area.X + 3 * k, my = area.Y + (area.Height - d) / 2f;
            RectangleF m = new RectangleF(mx, my, d, d);
            using (var pen = new Pen(Color.Black, 3 * k)) g.DrawEllipse(pen, RectangleF.Inflate(m, 2.5f * k, 2.5f * k));
            bool off = !online;
            Medal(g, m, medal, ring.IsEmpty ? ModernTheme.Accent : ring, you || !dot ? -1 : online ? 1 : 0, off, 2f, 13, off ? ModernTheme.Dim : Color.White);
            if (!string.IsNullOrEmpty(tag))
            {
                Font tf = ModernSkin.Font(8, true);
                float tw = ModernSkin.Measure(tag, tf) + 12 * k, th = 13 * k;
                RectangleF tr = new RectangleF(m.X + (d - tw) / 2f, m.Y - 9 * k, tw, th);
                using (GraphicsPath tp = ModernSkin.RoundRect(tr, 6 * k))
                using (var br = new SolidBrush(ModernTheme.Accent)) g.FillPath(br, tp);
                ModernSkin.Text(g, tag, tf, Color.White, tr, StringAlignment.Center, StringAlignment.Center, false);
            }
            float tx = right ? area.X : mx + d + 10 * k, tw2 = area.Width - d - 16 * k;
            StringAlignment al = right ? StringAlignment.Far : StringAlignment.Near;
            float lines = (you ? 14 : 0) + 20 + (string.IsNullOrEmpty(sub) ? 0 : 15) + (string.IsNullOrEmpty(sub2) ? 0 : 15);
            float y = area.Y + (area.Height - lines * k) / 2f;
            if (you)
            {
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernSocialYouTag), ModernSkin.Font(10, true), ModernTheme.Accent, new RectangleF(tx, y, tw2, 14 * k), al);
                y += 14 * k;
            }
            ModernSkin.Text(g, name, ModernSkin.Font(15, true), Color.White, new RectangleF(tx, y, tw2, 20 * k), al);
            y += 20 * k;
            if (!string.IsNullOrEmpty(sub)) { ModernSkin.Text(g, sub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(tx, y, tw2, 15 * k), al); y += 15 * k; }
            if (!string.IsNullOrEmpty(sub2)) ModernSkin.Text(g, sub2, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(tx, y, tw2, 15 * k), al);
        }

        /// <summary>The banner's soft accent glow (radial-gradient(ellipse wx% hy% at 50% 50%, accent 14%, transparent 70%)) over the page.</summary>
        public static void BannerGlow(Graphics g, Size size, float wFrac, float hFrac)
        {
            float w = size.Width * wFrac * 2f / 0.7f, h = size.Height * hFrac * 2f / 0.7f;
            RectangleF e = new RectangleF((size.Width - w) / 2f, (size.Height - h) / 2f, w, h);
            using var p = new GraphicsPath();
            p.AddEllipse(e);
            using var br = new PathGradientBrush(p) { CenterColor = W.Over(ModernTheme.Accent, 0.14f), SurroundColors = new[] { W.Page } };
            g.FillRectangle(br, 0, 0, size.Width, size.Height);
        }

        /// <summary>A chip (.bf / .chip): an outlined pill, lit (accent fill) when on. Returns its width (device px).</summary>
        public static float Chip(Graphics g, float x, float y, string text, bool on)
        {
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(10, true);
            float w = ModernSkin.Measure(text, f) + 18 * k, h = 18 * k;
            RectangleF r = new RectangleF(x, y, w, h);
            using (GraphicsPath p = ModernSkin.RoundRect(r, h / 2))
            {
                if (on) using (var br = new SolidBrush(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Accent), ModernSkin.Opaque(ModernTheme.PanelB), 0.28f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, on ? ModernTheme.Accent : ModernSkin.Opaque(ModernTheme.Frame));
            }
            ModernSkin.Text(g, text, f, on ? Color.White : ModernTheme.Dim, r, StringAlignment.Center, StringAlignment.Center, false);
            return w;
        }

        public static float ChipWidth(string text) => ModernSkin.Measure(text, ModernSkin.Font(10, true)) + 18 * ModernRoot.K;

        /// <summary>A small rounded pill (.pill / .st / .chip): text in <paramref name="colour"/>, a soft border, an optional fill.
        /// Returns its width (device px). <paramref name="right"/> = x is the right edge.</summary>
        public static float Pill(Graphics g, float x, float y, string text, Color colour, float fontHud = 9, bool filled = false, bool right = false, float h = 14)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(fontHud, true);
            string t = text.ToUpperInvariant();
            float w = ModernSkin.Measure(t, f) + 12 * k, hh = h * k;
            if (right) x -= w;
            RectangleF r = new RectangleF(x, y, w, hh);
            using (GraphicsPath p = ModernSkin.RoundRect(r, hh / 2))
            {
                if (filled)
                    using (var br = new SolidBrush(W.Over(colour, 0.35f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, filled ? colour : W.Over(colour, 0.45f));
            }
            ModernSkin.Text(g, t, f, filled ? Color.White : colour, r, StringAlignment.Center, StringAlignment.Center, false);
            return w;
        }

        /// <summary>A .box: a faint frame-light card.</summary>
        public static void Box(Graphics g, Size size) => W.Card(g, size);

        /// <summary>A HP / MP style bar (.b): a dark well, a horizontal gradient fill to <paramref name="frac"/> (-1 = empty), centred text.</summary>
        public static void Bar(Graphics g, RectangleF r, float frac, Color from, Color to, string text)
        {
            float k = ModernRoot.K;
            using (var br = new SolidBrush(Color.FromArgb(0x04, 0x06, 0x0a))) g.FillRectangle(br, r);
            if (frac > 0)
            {
                RectangleF f = new RectangleF(r.X, r.Y, Math.Max(1f, r.Width * Math.Min(1f, frac)), r.Height);
                using var lg = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), from, to, 0f);
                g.FillRectangle(lg, f);
            }
            using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0), 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            if (!string.IsNullOrEmpty(text))
                ModernSkin.Text(g, text, ModernSkin.Font(8, true), Color.White, r, StringAlignment.Center, StringAlignment.Center, false);
        }

        /// <summary>A checklist line (.ck): a disc with ✓ / ✕ / ? and the text. ok: 1 yes, 0 no, -1 unknown.</summary>
        public static void Check(Graphics g, float x, float y, float w, int ok, string text)
        {
            float k = ModernRoot.K, d = 16 * k;
            RectangleF disc = new RectangleF(x, y + 2 * k, d, d);
            Color fill = ok > 0 ? ModernTheme.Mix(ModernTheme.Ok, Color.Black, 0.6f) : ok == 0 ? ModernTheme.Mix(ModernTheme.Bad, Color.Black, 0.6f) : ModernSkin.Opaque(ModernTheme.Frame);
            using (var br = new SolidBrush(fill)) g.FillEllipse(br, disc);
            ModernSkin.Text(g, ok > 0 ? "✓" : ok == 0 ? "✕" : "?", ModernSkin.Font(10, true), Color.White, disc, StringAlignment.Center, StringAlignment.Center, false);
            ModernSkin.Text(g, text, ModernSkin.Font(12), ok == 0 ? ModernTheme.Dim : ModernTheme.Text, new RectangleF(x + d + 8 * k, y, w - d - 8 * k, 20 * k));
        }

        /// <summary>The section label with an icon (.lbl.sl: icon, upper-case caption, an optional value on the right). HUD px.</summary>
        public static void Section(MirControl parent, int icon, string text, int x, int y, int w, string right = null)
        {
            if (icon >= 0) new IconView(icon, 14, ModernTheme.Accent) { Parent = parent, Location = new Point(W.S(x), W.S(y)) };
            W.Caption(parent, text, x + (icon >= 0 ? 20 : 0), y, w - 20);
            if (!string.IsNullOrEmpty(right)) W.Label(parent, right, x, y, w, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
        }

        /// <summary>Wraps text in a column (HUD px width) and returns the height in HUD px (layout time only).</summary>
        public static int WrapHeight(string text, float fontHud, int widthHud, bool bold = false)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            SizeF s = ModernSkin.MeasureWrapped(text, ModernSkin.Font(fontHud, bold), W.S(widthHud));
            return (int)Math.Ceiling(s.Height / ModernRoot.K) + 2;
        }

        /// <summary>A wrapped label sized to its text (HUD px); returns its bottom (HUD px).</summary>
        public static int Para(MirControl parent, string text, int x, int y, int w, float size = 12, Color? colour = null, bool bold = false)
        {
            int h = Math.Max(14, WrapHeight(text, size, w, bold));
            W.Label(parent, text, x, y, w, h, size, bold, colour, StringAlignment.Near, true);
            return y + h;
        }

        /// <summary>A .box card with wrapped text (an optional bold lead line in the accent); returns its bottom (HUD px).</summary>
        public static int TextBox(MirControl parent, int x, int y, int w, string text, string lead = null, Color? colour = null)
        {
            int pad = 10, inner = w - pad * 2;
            int lh = string.IsNullOrEmpty(lead) ? 0 : WrapHeight(lead, 12, inner, true);
            int th = WrapHeight(text, 12, inner);
            int h = pad * 2 + lh + th;
            Color c = colour ?? ModernTheme.Text;
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                W.Card(g, b.Size);
                float ty = pad * k;
                if (lh > 0)
                {
                    ModernSkin.Text(g, lead, ModernSkin.Font(12, true), ModernTheme.Accent, new RectangleF(pad * k, ty, inner * k, lh * k), StringAlignment.Near, StringAlignment.Near, false, true);
                    ty += lh * k;
                }
                ModernSkin.Text(g, text, ModernSkin.Font(12), c, new RectangleF(pad * k, ty, inner * k, th * k), StringAlignment.Near, StringAlignment.Near, false, true);
            })
            { Parent = parent, Location = new Point(W.S(x), W.S(y)), Size = new Size(W.S(w), W.S(h)), NotControl = true };
            return y + h;
        }

        /// <summary>Disposes every child of a container (a pane rebuilt on a change).</summary>
        public static void Clear(MirControl c)
        {
            if (c == null) return;
            for (int i = c.Controls.Count - 1; i >= 0; i--) c.Controls[i].Dispose();
        }
    }
}
