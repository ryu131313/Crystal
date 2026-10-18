// Shared pieces of the phase 2 "chat / map" area (worker B): the chat channels and their colours (WebClient
// src/ui/modern/hud/chat.ts CSS .c-*), the outlined HUD text the chat, minimap and tracker draw (their text-shadow), a cached
// text measurer, a tinted ModernIcons glyph (icons.ts svg(): the stroke icons drawn white and tinted by the sprite colour), a
// small icon button (chat.ts .ib, minimap.ts .btns > div) and the chat's popover menu (chat.ts .pop) on the Menu layer.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    /// <summary>The chat's channels (ChatType grouped the way the tabs and colours use them; chat.ts Ch).</summary>
    public enum ChatChannel : byte
    {
        Normal, Shout, Group, Guild, Whisper, Mentor, Lover, System, Hint, Announcement, LevelUp,
    }

    /// <summary>Colours, outlined text and measuring for the chat / map HUD pieces. Paint-time only (never per frame).</summary>
    public static class ChatGfx
    {
        // chat.ts .c-normal … .c-levelup
        private static readonly Color[] ChannelColours =
        {
            ModernTheme.Hex("#eef2fa"), ModernTheme.Hex("#ffd84a"), ModernTheme.Hex("#8ad4ff"), ModernTheme.Hex("#6fe08a"),
            ModernTheme.Hex("#d9a6ff"), ModernTheme.Hex("#c9a0ff"), ModernTheme.Hex("#ff9cc4"), ModernTheme.Hex("#ff7a6a"),
            ModernTheme.Hex("#8fd8a8"), ModernTheme.Hex("#7fb0ff"), ModernTheme.Hex("#f2c94c"),
        };

        public static Color Channel(ChatChannel ch)
        {
            int i = (int)ch;
            return i >= 0 && i < ChannelColours.Length ? ChannelColours[i] : ChannelColours[0];
        }

        /// <summary>ChatType → channel (chat.ts channelOf).</summary>
        public static ChatChannel ChannelOf(ChatType type)
        {
            switch (type)
            {
                case ChatType.Shout:
                case ChatType.Shout2:
                case ChatType.Shout3:
                    return ChatChannel.Shout;
                case ChatType.System:
                case ChatType.System2:
                case ChatType.Trainer:
                    return ChatChannel.System;
                case ChatType.Hint: return ChatChannel.Hint;
                case ChatType.Announcement:
                case ChatType.LineMessage:
                    return ChatChannel.Announcement;
                case ChatType.Group: return ChatChannel.Group;
                case ChatType.WhisperIn:
                case ChatType.WhisperOut:
                    return ChatChannel.Whisper;
                case ChatType.Guild: return ChatChannel.Guild;
                case ChatType.LevelUp: return ChatChannel.LevelUp;
                case ChatType.Relationship: return ChatChannel.Lover;
                case ChatType.Mentor: return ChatChannel.Mentor;
            }
            return ChatChannel.Normal;
        }

        private static readonly Dictionary<int, Font> _italic = new Dictionary<int, Font>();

        /// <summary>An italic Segoe UI of a HUD px size (chat.ts .ln.sys); cached for the process like ModernSkin.Font.</summary>
        public static Font Italic(float hudPx)
        {
            int px = Math.Max(6, (int)Math.Round(hudPx * ModernRoot.K * 4));
            if (!_italic.TryGetValue(px, out Font font))
            {
                font = new Font(ModernSkin.FontName, px / 4f, FontStyle.Italic, GraphicsUnit.Pixel);
                _italic[px] = font;
            }
            return font;
        }

        private static Bitmap _measureBitmap;
        private static Graphics _measure;
        private static StringFormat _format;

        /// <summary>The typographic format every chat / map text uses (measured and drawn alike). Process lifetime.</summary>
        public static StringFormat Format
        {
            get
            {
                if (_format == null)
                {
                    _format = new StringFormat(StringFormat.GenericTypographic);
                    _format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                }
                return _format;
            }
        }

        /// <summary>Width of a run in device px (a cached 1x1 graphics: layout time, not per frame).</summary>
        public static float Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return 0;
            if (_measure == null)
            {
                _measureBitmap = new Bitmap(1, 1);
                _measure = Graphics.FromImage(_measureBitmap);
                ModernSkin.Prepare(_measure);
            }
            return _measure.MeasureString(text, font, PointF.Empty, Format).Width;
        }

        /// <summary>
        /// Text with the HUD's dark halo (chat.ts .ln text-shadow "0 1px 2px #000, 0 0 3px #000"): black copies around it, then the
        /// text. <paramref name="alpha"/> fades the whole run (the chat's line fade).
        /// </summary>
        public static void ShadowText(Graphics g, string text, Font font, Color colour, float x, float y, float alpha = 1f, float halo = 1f)
        {
            if (string.IsNullOrEmpty(text) || alpha <= 0.01f) return;
            float k = ModernRoot.K, d = Math.Max(1f, k);
            using (var shadow = new SolidBrush(Color.FromArgb((int)(150 * alpha * halo), 0, 0, 0)))
            {
                g.DrawString(text, font, shadow, x - d, y, Format);
                g.DrawString(text, font, shadow, x + d, y, Format);
                g.DrawString(text, font, shadow, x, y - d, Format);
                g.DrawString(text, font, shadow, x, y + d * 1.5f, Format);
            }
            using var brush = new SolidBrush(Color.FromArgb((int)(colour.A * alpha), colour.R, colour.G, colour.B));
            g.DrawString(text, font, brush, x, y, Format);
        }

        /// <summary>A pill / panel fill: the web's "linear-gradient(180deg, var(--pa), var(--pb))" with the frame border and an
        /// outer black ring (minimap .ban / .loc / .btns, tracker none).</summary>
        public static void Plate(Graphics g, RectangleF r, float radius, bool hot = false)
        {
            using (GraphicsPath outer = ModernSkin.RoundRect(RectangleF.Inflate(r, 1f, 1f), radius + 1))
                ModernSkin.Stroke(g, outer, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(r, radius);
            ModernSkin.FillVertical(g, p, r, new[] { ModernSkin.Opaque(ModernTheme.PanelA), ModernSkin.Opaque(ModernTheme.PanelB) });
            ModernSkin.Stroke(g, p, hot ? ModernTheme.Accent : ModernTheme.Frame);
        }

        /// <summary>A filled arrow pointing along <paramref name="angle"/> (radians, 0 = up, clockwise; minimap.ts arrow()).</summary>
        public static void Arrow(Graphics g, float x, float y, float angle, Color fill, float size, Color stroke, float strokeWidth)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(angle * 180f / MathF.PI);
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[]
                {
                    new PointF(0, -size), new PointF(size * 0.7f, size * 0.8f), new PointF(0, size * 0.35f), new PointF(-size * 0.7f, size * 0.8f),
                });
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using var pen = new Pen(stroke, strokeWidth) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
            }
            g.Restore(state);
        }

        /// <summary>A dot with a dark outline (minimap.ts dot()).</summary>
        public static void Dot(Graphics g, float x, float y, float radius, Color fill, float strokeWidth)
        {
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, x - radius, y - radius, radius * 2, radius * 2);
            using var pen = new Pen(Color.FromArgb(217, 0, 0, 0), strokeWidth);
            g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
        }

        /// <summary>The ObjectID of a visible player with this name (C.Inspect needs one), or 0 (chat.ts playerInView).</summary>
        public static uint PlayerInView(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                MapObject ob = list[i];
                if (ob != null && ob.Race == ObjectType.Player && ob.Name == name && ob != MapObject.User) return ob.ObjectID;
            }
            return 0;
        }

        /// <summary>An NPC's display name and place: "BichonProvince_Merchant_Herbalist" → ("Herbalist", "Bichon Province · Merchant")
        /// (format.ts npcName).</summary>
        public static (string Name, string Role) NpcName(string n)
        {
            if (string.IsNullOrEmpty(n)) return (string.Empty, string.Empty);
            string[] parts = n.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return (Pretty(n), string.Empty);
            string role = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
                role += (role.Length > 0 ? " · " : string.Empty) + Pretty(parts[i]);
            return (Pretty(parts[^1]), role);
        }

        private static readonly System.Text.RegularExpressions.Regex Camel = new System.Text.RegularExpressions.Regex("([a-z])([A-Z])");
        private static readonly System.Text.RegularExpressions.Regex Paren = new System.Text.RegularExpressions.Regex(@"([A-Za-z])\(");
        private static readonly System.Text.RegularExpressions.Regex Spaces = new System.Text.RegularExpressions.Regex(@"\s+");

        /// <summary>"BichonProvince_2" → "Bichon Province 2" (format.ts pretty).</summary>
        public static string Pretty(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.Replace('_', ' ');
            s = Camel.Replace(s, "$1 $2");
            s = Paren.Replace(s, "$1 (");
            return Spaces.Replace(s, " ").Trim();
        }
    }

    /// <summary>
    /// A ModernIcons.Lib stroke icon at any size, tinted (icons.ts svg + CSS color): drawn by the sprite with a scale transform,
    /// so it stays sharp at the HUD scale. Takes no mouse. Uses the 1x image up to 24 device px, the 2x image above.
    /// </summary>
    public sealed class ChatIconView : MirControl
    {
        private int _icon;
        private Color _colour = Color.White;

        public ChatIconView(int icon)
        {
            _icon = icon;
            NotControl = true;
            BackColour = Color.Transparent;
        }

        public int Icon
        {
            get => _icon;
            set
            {
                if (_icon == value) return;
                _icon = value;
                Redraw();
            }
        }

        public Color Colour
        {
            get => _colour;
            set
            {
                if (_colour == value) return;
                _colour = value;
                Redraw();
            }
        }

        protected internal override void DrawControl()
        {
            if (_icon < 0 || Size.Width <= 0 || Size.Height <= 0) return;
            int index = Size.Width > ModernIcons.IconSize ? _icon + 1 : _icon;
            Size src = Libraries.ModernIcons.GetSize(index);
            if (src.Width <= 0 || src.Height <= 0) return;
            float sx = Size.Width / (float)src.Width, sy = Size.Height / (float)src.Height;
            Point at = DisplayLocation;
            DXManager.SetSpriteTransform(SlimDX.Matrix.Scaling(sx, sy, 1f));
            try
            {
                Libraries.ModernIcons.Draw(index, new Point((int)Math.Round(at.X / sx), (int)Math.Round(at.Y / sy)), _colour, false);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] icon draw", ex);
            }
            finally
            {
                DXManager.SetSpriteTransform(SlimDX.Matrix.Identity);
            }
        }
    }

    /// <summary>A small icon button (chat.ts .ib: dim, white on an accent wash when hovered or on; minimap.ts .btns: a round
    /// plate). The icon is a <see cref="ChatIconView"/> child.</summary>
    public sealed class ChatIconButton : ModernControl
    {
        private readonly ChatIconView _icon;
        private readonly bool _round;
        private bool _on;
        private string _badgeText;
        private bool _badge;

        public ChatIconButton(int icon, int widthHud, int heightHud, int iconHud, bool round = false)
        {
            _round = round;
            Size = new Size(S(widthHud), S(heightHud));
            Sound = SoundList.ButtonA;
            int s = S(iconHud);
            _icon = new ChatIconView(icon) { Parent = this, Size = new Size(s, s), Location = new Point((Size.Width - s) / 2, (Size.Height - s) / 2) };
            UpdateColour();
        }

        public int Icon { get => _icon.Icon; set => _icon.Icon = value; }

        /// <summary>The ".on" state (accent wash, white icon).</summary>
        public bool On
        {
            get => _on;
            set
            {
                if (_on == value) return;
                _on = value;
                UpdateColour();
                Invalidate();
            }
        }

        /// <summary>The minimap mail button's red dot (minimap.ts .badge).</summary>
        public bool Badge
        {
            get => _badge;
            set
            {
                if (_badge == value) return;
                _badge = value;
                Invalidate();
            }
        }

        public string BadgeText { get => _badgeText; set { _badgeText = value; Invalidate(); } }

        private void UpdateColour()
        {
            bool hot = Hovered || _on;
            _icon.Colour = _round ? (hot ? Color.White : ModernTheme.AccentDim) : (hot ? Color.White : ModernTheme.Dim);
        }

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            UpdateColour();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            UpdateColour();
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            if (_round)
            {
                RectangleF c = RectangleF.Inflate(r, -1f, -1f);
                ChatGfx.Plate(g, c, c.Height / 2f, Hovered);
            }
            else if (Hovered || _on)
            {
                using GraphicsPath p = ModernSkin.RoundRect(r, 4f * k);
                using var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.25f));
                g.FillPath(b, p);
            }
            if (_badge)
            {
                float d = 9f * k, x = Size.Width - d + 1f * k, y = -1f * k + 1f;
                using (var b = new SolidBrush(ModernTheme.Bad)) g.FillEllipse(b, x - 1, y, d, d);
                using var pen = new Pen(Color.Black, 1f);
                g.DrawEllipse(pen, x - 1, y, d, d);
            }
        }
    }

    /// <summary>
    /// An alpha mask: drawn after the content below it with the multiply blend (DXManager.SetLightMultiplyBlend: destination
    /// times source, alpha included on both backends), so where the mask is transparent the content becomes transparent too —
    /// the web's border-radius: 50% clip (minimap disc) and mask-image fades (the announcement ticker's edges). The shape is
    /// painted white by <see cref="Shape"/>; outside it is fully transparent.
    /// </summary>
    public sealed class ChatAlphaMask : ModernControl
    {
        private readonly Action<Graphics, Size> _shape;

        public ChatAlphaMask(Action<Graphics, Size> shape)
        {
            _shape = shape;
            NotControl = true;
        }

        /// <summary>Outside the shape: black at alpha 1/255, not 0 — the sprite's alpha test (Greater 0, both backends) would
        /// drop zero-alpha texels, and those must multiply the content away.</summary>
        public static readonly Color Outside = Color.FromArgb(1, 0, 0, 0);

        protected override void Paint(Graphics g)
        {
            g.Clear(Outside);
            _shape?.Invoke(g, Size);
        }

        protected internal override void DrawControl()
        {
            DXManager.Flush();
            DXManager.SetLightMultiplyBlend();
            try
            {
                base.DrawControl();
            }
            finally
            {
                DXManager.RestartSpriteAlphaBlend();
            }
        }

        public static void Ellipse(Graphics g, Size s)
        {
            using var b = new SolidBrush(Color.White);
            g.FillEllipse(b, 0, 0, s.Width, s.Height);
        }

        /// <summary>A rounded rectangle of the theme radius (the square minimap).</summary>
        public static void Rounded(Graphics g, Size s)
        {
            using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0, 0, s.Width, s.Height), ModernTheme.Radius * ModernRoot.K);
            using var b = new SolidBrush(Color.White);
            g.FillPath(b, p);
        }

        /// <summary>mask-image: linear-gradient(90deg, transparent, #000 12%, #000 88%, transparent).</summary>
        public static void HorizontalFade(Graphics g, Size s)
        {
            if (s.Width <= 0 || s.Height <= 0) return;
            using var brush = new LinearGradientBrush(new RectangleF(-1, 0, s.Width + 2, s.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Outside, Color.White, Color.White, Outside },
                Positions = new[] { 0f, 0.12f, 0.88f, 1f },
            };
            g.FillRectangle(brush, 0, 0, s.Width, s.Height);
        }
    }

    /// <summary>
    /// Makes everything drawn before it in a render target opaque again, colours unchanged: an opaque black quad with the
    /// additive blend (DXManager.SetBlend: SourceAlpha, One) adds nothing to the colour and saturates the alpha. A render
    /// target's alpha drops wherever something translucent is drawn over opaque content (the blend has no separate alpha), so
    /// the world would show through a window there; windows add one of these over their content as the last child.
    /// </summary>
    public sealed class ChatAlphaFix : ModernControl
    {
        public ChatAlphaFix()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g) => g.Clear(Color.Black);

        protected internal override void DrawControl()
        {
            bool old = DXManager.Blending;
            DXManager.SetBlend(true);
            try
            {
                base.DrawControl();
            }
            finally
            {
                DXManager.SetBlend(old);
            }
        }
    }

    /// <summary>One entry of a <see cref="ChatPopover"/> (chat.ts item()).</summary>
    public sealed class ChatPopItem
    {
        public string Label = string.Empty;
        /// <summary>Right-aligned dim note (chat.ts .it small).</summary>
        public string Note;
        /// <summary>A coloured "●" before the label (the channel menu); Color.Empty = none.</summary>
        public Color Dot = Color.Empty;
        /// <summary>chat.ts .it.bad (Block).</summary>
        public bool Bad;
        public Action Act;
    }

    /// <summary>
    /// The chat's popover (chat.ts .pop): a small dark card with an optional upper-case header and clickable rows, on the root's
    /// Menu layer (so it can stand outside the chat box), placed with its bottom-left at an anchor point. Rows highlight on hover
    /// or by <see cref="Selected"/> (the whisper autocomplete's arrow keys). The owner closes it (outside clicks, Esc).
    /// </summary>
    public sealed class ChatPopover : ModernSurface
    {
        public readonly string Kind;
        private readonly ChatPopoverCard _card;

        public ChatPopover(string kind, string head, List<ChatPopItem> items, Point bottomLeft)
        {
            Kind = kind;
            Parent = ModernRoot.Instance?.Menu;
            _card = new ChatPopoverCard(head, items) { Parent = this, Location = Point.Empty };
            Size = _card.Size;
            int x = Math.Max(0, Math.Min(bottomLeft.X, Settings.ScreenWidth - Size.Width));
            int y = Math.Max(0, bottomLeft.Y - Size.Height);
            Location = new Point(x, y);
        }

        public int Count => _card.Items.Count;

        public int Selected { get => _card.Selected; set => _card.SetSelected(value); }

        public void Activate(int index) => _card.Run(index);
    }

    internal sealed class ChatPopoverCard : ModernControl
    {
        public readonly List<ChatPopItem> Items;
        private readonly string _head;
        private int _hover = -1, _selected = -1;
        private readonly int _pad, _headH, _rowH;

        public ChatPopoverCard(string head, List<ChatPopItem> items)
        {
            _head = head ?? string.Empty;
            Items = items ?? new List<ChatPopItem>();
            _pad = S(4);
            _headH = _head.Length > 0 ? S(17) : 0;
            _rowH = S(23);
            float w = S(150);
            Font f = ModernSkin.Font(12), small = ModernSkin.Font(10);
            for (int i = 0; i < Items.Count; i++)
            {
                ChatPopItem it = Items[i];
                float rw = S(16) + ChatGfx.Measure(it.Label, f) + (it.Dot.IsEmpty ? 0 : S(14)) + (string.IsNullOrEmpty(it.Note) ? 0 : S(14) + ChatGfx.Measure(it.Note, small));
                w = Math.Max(w, rw + _pad * 2);
            }
            if (_headH > 0) w = Math.Max(w, ChatGfx.Measure(_head.ToUpperInvariant(), ModernSkin.Font(9, true)) * 1.4f + S(24));
            w = Math.Min(w, S(260));
            Size = new Size((int)Math.Ceiling(w), _pad * 2 + _headH + Items.Count * _rowH);
            Sound = SoundList.ButtonA;
        }

        public int Selected => _selected;

        public void SetSelected(int i)
        {
            if (Items.Count == 0) return;
            i = ((i % Items.Count) + Items.Count) % Items.Count;
            if (i == _selected) return;
            _selected = i;
            Invalidate();
        }

        public void Run(int i)
        {
            if (i < 0 || i >= Items.Count) return;
            Items[i].Act?.Invoke();
        }

        private int RowAt(Point screen)
        {
            int y = screen.Y - DisplayLocation.Y - _pad - _headH;
            if (y < 0) return -1;
            int i = y / Math.Max(1, _rowH);
            return i >= 0 && i < Items.Count ? i : -1;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(CMain.MPoint);
            if (i == _hover) return;
            _hover = i;
            Invalidate();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            _hover = -1;
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            int i = RowAt(CMain.MPoint);
            if (i < 0) return;
            base.OnMouseClick(e);
            Run(i);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, rad))
            {
                using (var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.96f))))
                    g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            float y = _pad;
            if (_headH > 0)
            {
                ModernSkin.Text(g, _head.ToUpperInvariant(), ModernSkin.Font(9, true), ModernTheme.Dim, new RectangleF(_pad + S(8), y, Size.Width - _pad * 2 - S(16), _headH));
                y += _headH;
            }
            Font f = ModernSkin.Font(12), small = ModernSkin.Font(10);
            for (int i = 0; i < Items.Count; i++)
            {
                ChatPopItem it = Items[i];
                RectangleF row = new RectangleF(_pad, y, Size.Width - _pad * 2, _rowH);
                bool hot = i == _hover || i == _selected;
                if (hot)
                {
                    using GraphicsPath hp = ModernSkin.RoundRect(row, 4f * k);
                    using var hb = new SolidBrush(ModernSkin.Opaque(ModernTheme.Mix(ModernTheme.Accent, ModernSkin.Opaque(ModernTheme.PanelB), 0.25f)));
                    g.FillPath(hb, hp);
                }
                float x = row.X + S(8);
                if (!it.Dot.IsEmpty)
                {
                    float d = 7f * k;
                    using (var db = new SolidBrush(it.Dot)) g.FillEllipse(db, x, row.Y + (row.Height - d) / 2f, d, d);
                    x += S(14);
                }
                Color c = it.Bad ? ModernTheme.BadSoft : hot ? Color.White : ModernTheme.Text;
                float noteW = string.IsNullOrEmpty(it.Note) ? 0 : ChatGfx.Measure(it.Note, small);
                ModernSkin.Text(g, it.Label, f, c, new RectangleF(x, row.Y, row.Right - x - S(8) - (noteW > 0 ? noteW + S(8) : 0), row.Height));
                if (noteW > 0)
                    ModernSkin.Text(g, it.Note, small, ModernTheme.Dim, new RectangleF(row.Right - S(8) - noteW - 1, row.Y, noteW + 2, row.Height));
                y += _rowH;
            }
        }
    }
}
