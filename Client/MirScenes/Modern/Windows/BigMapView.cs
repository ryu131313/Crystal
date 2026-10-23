// The big map's viewport and its pieces (WebClient/src/ui/modern/windows/bigMap.ts: .vp, .mk markers, .bar chrome, .li
// rows), ported as desktop controls: the viewport is a nested surface (its own render target, so the scaled picture is
// clipped to it) with the picture drawn by the sprite, a marker layer of small painted controls a pan only moves, and the
// chrome pills on top. Mouse: drag pans (pointer capture through Moving), the wheel zooms at the cursor, a click on your own
// map walks there, a right-click drops a waypoint.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;

namespace Client.MirScenes.Modern.Windows
{
    internal sealed class BigMapViewport : ModernSurface
    {
        private readonly BigMapWindow _w;
        public readonly BmLayer Layer;
        private bool _pan, _moved;
        private Point _start, _last;
        private bool _mouseWasDown;

        public BigMapViewport(BigMapWindow w)
        {
            _w = w;
            PassThrough = false;
            new BmBack { Parent = this };
            new BmPicture(w) { Parent = this };
            Layer = new BmLayer { Parent = this };
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();
            for (int i = 0; i < Controls.Count && i < 3; i++)
            {
                Controls[i].Location = Point.Empty;
                Controls[i].Size = Size;
            }
        }

        public override void Redraw()
        {
            TextureValid = false;
            Parent?.Redraw();
        }

        public void EndGestures()
        {
            _pan = false;
            Moving = false;
        }

        private Point Local => new Point(CMain.MPoint.X - DisplayLocation.X, CMain.MPoint.Y - DisplayLocation.Y);

        public override bool IsMouseOver(Point p) => Visible && !IsDisposed && (_pan || DisplayRectangle.Contains(p));

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pan = true;
            _moved = false;
            Moving = true;
            _start = _last = CMain.MPoint;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            if (_pan)
            {
                Point p = CMain.MPoint;
                if (!_moved && Math.Abs(p.X - _start.X) + Math.Abs(p.Y - _start.Y) > S(3)) _moved = true;
                if (_moved)
                {
                    _w.PanBy(p.X - _last.X, p.Y - _last.Y);
                    _last = p;
                }
                Hover();
                return;
            }
            base.OnMouseMove(e);
            Hover();
        }

        public override void OnMouseUp(MouseEventArgs e)
        {
            bool click = _pan && !_moved && e.Button == MouseButtons.Left;
            _pan = false;
            Moving = false;
            base.OnMouseUp(e);
            if (click && DisplayRectangle.Contains(CMain.MPoint)) _w.ClickAt(Local);
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Right) _w.RightClickAt(Local);
        }

        public override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Point l = Local;
            _w.ZoomBy(e.Delta > 0 ? 1.2f : 1 / 1.2f, l.X, l.Y);
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            if (!_pan) _w.SetHoverCell(null);
        }

        private void Hover()
        {
            if (_w.CellAt(Local, out Point cell)) _w.SetHoverCell(cell);
            else _w.SetHoverCell(null);
        }
    }

    /// <summary>The marker layer: takes the mouse only over a marker.</summary>
    internal sealed class BmLayer : MirControl
    {
        public BmLayer()
        {
            BackColour = Color.Transparent;
        }

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || IsDisposed) return false;
            for (int i = Controls.Count - 1; i >= 0; i--)
                if (Controls[i].IsMouseOver(p)) return true;
            return false;
        }
    }

    /// <summary>.vp background: radial-gradient(ellipse, --pb toward black, black).</summary>
    internal sealed class BmBack : ModernControl
    {
        public BmBack() { NotControl = true; }

        protected override void Paint(Graphics g)
        {
            if (Size.Width <= 0 || Size.Height <= 0) return;
            using var path = new GraphicsPath();
            path.AddEllipse(-Size.Width * 0.2f, -Size.Height * 0.2f, Size.Width * 1.4f, Size.Height * 1.4f);
            using (var black = new SolidBrush(Color.Black)) g.FillRectangle(black, 0, 0, Size.Width, Size.Height);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = ModernSkin.Opaque(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.7f)),
                SurroundColors = new[] { Color.Black },
            };
            g.FillPath(brush, path);
        }
    }

    /// <summary>The map / world picture at the window's zoom and pan (the clouds additive on the world map).</summary>
    internal sealed class BmPicture : MirControl
    {
        private readonly BigMapWindow _w;

        public BmPicture(BigMapWindow w)
        {
            _w = w;
            NotControl = true;
            BackColour = Color.Transparent;
        }

        protected internal override void DrawControl()
        {
            MLibrary lib = _w.PicLib;
            if (((lib == null || _w.PicIndex < 0) && !_w.PicGenerated) || _w.Pic.IsEmpty) return;
            int dw = (int)Math.Round(_w.Pic.Width * _w.Z), dh = (int)Math.Round(_w.Pic.Height * _w.Z);
            if (dw <= 0 || dh <= 0) return;
            Point at = DisplayLocation;
            Point p = new Point(at.X + (int)Math.Round(_w.Ox), at.Y + (int)Math.Round(_w.Oy));
            try
            {
                if (_w.PicGenerated) GeneratedMapPicture.Draw(p, new Size(dw, dh), Color.White);
                else lib.Draw(_w.PicIndex, p, new Size(dw, dh), Color.White);
                if (_w.View == BigMapData.World && lib == Libraries.Prguse2)
                {
                    bool old = DXManager.Blending;
                    DXManager.SetBlend(true);
                    try
                    {
                        lib.Draw(1365, p, new Size(dw, dh), Color.White);
                    }
                    finally
                    {
                        DXManager.SetBlend(old);
                    }
                }
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] big map picture", ex);
            }
        }
    }

    internal enum BmKind { Npc, Exit, Zone, Safe, Mob, Waypoint, Member, Me, WorldPin, Region }

    /// <summary>
    /// One marker (bigMap.ts .mk): an icon centred on its cell with an optional label below (hidden while zoomed out unless
    /// hovered, focused or selected), an optional count badge; or an area sized in cells (spawn zones, safe zones, world
    /// regions). Takes the mouse over its icon only.
    /// </summary>
    internal class BmMarker : ModernControl
    {
        public readonly BmKind Kind;
        public readonly string Label;
        public float X, Y;
        public int Cells, Min;
        public Rectangle? Box;
        public string Badge;
        public bool Focus, Selected, Dimmed, Start, Keep, Mixed;
        public int Icon = -1, MobImage = -1, MobImage2 = -1;
        public float Angle;
        public BigMapWindow.Difficulty Diff;
        public BmMarker Linked;
        public readonly List<BmMarker> Zones = new List<BmMarker>();
        private bool _zoomedOut;
        private readonly bool _passive;
        private Rectangle _iconBox;
        private BmLibIcon _pic, _pic2;
        private ChatIconView _glyph;

        public BmMarker(BmKind kind, string label)
        {
            Kind = kind;
            Label = label;
            Sound = kind == BmKind.Me || kind == BmKind.Zone || kind == BmKind.Safe || kind == BmKind.Region ? SoundList.None : SoundList.ButtonA;
            _passive = kind == BmKind.Zone || kind == BmKind.Safe || kind == BmKind.Region || kind == BmKind.Me;
            if (_passive) NotControl = true;
        }

        public bool ZoomedOut
        {
            get => _zoomedOut;
            set
            {
                if (_zoomedOut == value) return;
                _zoomedOut = value;
                if (Label != null) Invalidate();
            }
        }

        private int IconSize => Kind switch
        {
            BmKind.Npc => Icon > 0 ? S(20) : S(8),
            BmKind.Exit => S(18),
            BmKind.Mob => S(28),
            BmKind.Waypoint => S(22),
            BmKind.Member => S(14),
            BmKind.Me => S(24),
            BmKind.WorldPin => S(14),
            _ => S(10),
        };

        private bool ShowLabel => Label != null && (!_zoomedOut || Hovered || Focus || Selected || Kind == BmKind.WorldPin);

        private Font LabelFont => ModernSkin.Font(Kind == BmKind.WorldPin ? 11 : 10, true);

        /// <summary>Sizes the control around its icon (margins for the badges / second portrait, the label below) and builds the
        /// sprite-drawn pieces once.</summary>
        private void EnsureLayout()
        {
            if (!_iconBox.IsEmpty) return;
            int ic = IconSize, m = Kind == BmKind.Mob ? S(10) : Kind == BmKind.Exit ? S(9) : S(4);
            int labelW = Label != null ? (int)Math.Ceiling(ChatGfx.Measure(Label, LabelFont)) + S(6) : 0;
            int labelH = Label != null ? S(14) : 0;
            int h = Kind == BmKind.Waypoint ? S(26) : ic;
            int w = Math.Max(ic + m * 2, labelW);
            _iconBox = new Rectangle((w - ic) / 2, m, ic, h);
            Size = new Size(w, m + h + (Kind == BmKind.Mob ? S(3) : S(1)) + labelH + S(2));
            if (Kind == BmKind.Npc && Icon > 0)
                _pic = new BmLibIcon { Parent = this, Lib = Libraries.MapLinkIcon, Index = Icon, Location = _iconBox.Location, Size = _iconBox.Size };
            else if (Kind == BmKind.Mob)
            {
                if (Mixed && MobImage2 >= 0)
                    _pic2 = new BmLibIcon { Parent = this, MonsterImage = MobImage2, Location = new Point(_iconBox.X - S(9) + S(2), _iconBox.Y - S(6) + S(2)), Size = new Size(S(16), S(16)) };
                _pic = new BmLibIcon { Parent = this, MonsterImage = MobImage, Location = new Point(_iconBox.X + S(2), _iconBox.Y + S(2)), Size = new Size(ic - S(4), ic - S(4)) };
            }
            else if (Kind == BmKind.Exit)
                _glyph = new ChatIconView(ModernIcons.ArrowRight) { Parent = this, Size = new Size(S(11), S(11)), Location = new Point(_iconBox.X + (ic - S(11)) / 2, _iconBox.Y + (ic - S(11)) / 2) };
        }

        public void PlaceAt(PointF p)
        {
            EnsureLayout();
            float ay = Kind == BmKind.Waypoint ? _iconBox.Bottom : _iconBox.Y + _iconBox.Height / 2f;
            Location = new Point((int)Math.Round(p.X - _iconBox.X - _iconBox.Width / 2f), (int)Math.Round(p.Y - ay));
        }

        public void SetBounds(Rectangle r)
        {
            if (Size != r.Size) { Size = r.Size; Invalidate(); }
            Location = r.Location;
        }

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || _passive || IsDisposed) return false;
            Rectangle b = _iconBox;
            b.Offset(DisplayLocation);
            return b.Contains(p);
        }

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            if (Linked != null) Linked.Visible = true;
            for (int i = 0; i < Zones.Count; i++) Zones[i].Visible = true;
            if (Kind == BmKind.Mob || Kind == BmKind.WorldPin) BringToFront();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            if (Linked != null) Linked.Visible = false;
            for (int i = 0; i < Zones.Count; i++) if (!Zones[i].Keep) Zones[i].Visible = false;
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K;
            switch (Kind)
            {
                case BmKind.Zone: PaintZone(g, k); return;
                case BmKind.Safe: PaintSafe(g, k); return;
                case BmKind.Region: return;
            }
            EnsureLayout();
            RectangleF ib = _iconBox;
            float cx = ib.X + ib.Width / 2f, cy = ib.Y + ib.Height / 2f;
            bool hot = Hovered;
            switch (Kind)
            {
                case BmKind.Npc:
                    if (Icon <= 0)
                    {
                        using (var b = new SolidBrush(ModernTheme.Gold)) g.FillEllipse(b, ib);
                        using (var pen = new Pen(Color.Black, Math.Max(1f, k))) g.DrawEllipse(pen, ib);
                    }
                    break;
                case BmKind.Exit:
                    using (var b = new SolidBrush(ModernTheme.GlowColour)) g.FillEllipse(b, RectangleF.Inflate(ib, 3 * k, 3 * k));
                    using (var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.7f)))) g.FillEllipse(b, ib);
                    using (var pen = new Pen(Color.White, Math.Max(1f, k))) g.DrawEllipse(pen, ib);
                    break;
                case BmKind.Mob:
                {
                    Color ring = BigMapWindow.DifficultyColour(Diff);
                    float scale = hot || Selected ? 1.12f : 1f;
                    RectangleF r = RectangleF.Inflate(ib, (scale - 1) * ib.Width / 2, (scale - 1) * ib.Height / 2);
                    if (Mixed && _pic2 != null)
                    {
                        RectangleF bk = new RectangleF(ib.X - 9 * k, ib.Y - 6 * k, 20 * k, 20 * k);
                        using (var b = new SolidBrush(ModernTheme.Hex("#0d1118"))) g.FillEllipse(b, bk);
                        using (var pen = new Pen(ring, Math.Max(1f, k))) g.DrawEllipse(pen, bk);
                    }
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(r);
                        using var brush = new PathGradientBrush(path) { CenterColor = ModernTheme.Hex("#2c3442"), SurroundColors = new[] { ModernTheme.Hex("#090c12") }, CenterPoint = new PointF(cx, r.Y + r.Height * 0.4f) };
                        g.FillPath(brush, path);
                    }
                    using (var pen = new Pen(Color.Black, Math.Max(1f, k))) g.DrawEllipse(pen, RectangleF.Inflate(r, 1.5f * k, 1.5f * k));
                    using (var pen = new Pen(Dimmed && !hot ? ModernSkin.Dim(ring, 0.35f) : ring, 2f * k)) g.DrawEllipse(pen, RectangleF.Inflate(r, -1f * k, -1f * k));
                    break;
                }
                case BmKind.Waypoint:
                {
                    // the pin (bigMap.ts PIN path, 22 x 26)
                    float s = ib.Width / 22f;
                    using var path = new GraphicsPath();
                    path.AddBezier(ib.X + 11 * s, ib.Bottom - 1 * s, ib.X + 11 * s, ib.Bottom - 1 * s, ib.X + 2 * s, ib.Y + 15 * s, ib.X + 2 * s, ib.Y + 9 * s);
                    path.AddArc(ib.X + 2 * s, ib.Y, 18 * s, 18 * s, 180, 180);
                    path.AddBezier(ib.X + 20 * s, ib.Y + 9 * s, ib.X + 20 * s, ib.Y + 15 * s, ib.X + 11 * s, ib.Bottom - 1 * s, ib.X + 11 * s, ib.Bottom - 1 * s);
                    path.CloseFigure();
                    using (var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.8f)))) g.FillPath(b, path);
                    using (var pen = new Pen(Color.White, 1.5f * k)) g.DrawPath(pen, path);
                    if (Badge != null) ModernSkin.Text(g, Badge, ModernSkin.Font(10, true), Color.White, new RectangleF(ib.X, ib.Y + 1 * k, ib.Width, 16 * k), StringAlignment.Center);
                    break;
                }
                case BmKind.Member:
                {
                    GraphicsState st = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(45);
                    float d = 5 * k;
                    using (var pen = new Pen(Color.White, Math.Max(1f, k))) g.DrawRectangle(pen, -d - k, -d - k, d * 2 + 2 * k, d * 2 + 2 * k);
                    using (var b = new SolidBrush(ModernTheme.Ok)) g.FillRectangle(b, -d, -d, d * 2, d * 2);
                    using (var pen = new Pen(Color.Black, Math.Max(1f, k * 0.8f))) g.DrawRectangle(pen, -d, -d, d * 2, d * 2);
                    g.Restore(st);
                    break;
                }
                case BmKind.Me:
                    using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.Accent, 0.45f), 2f * k)) g.DrawEllipse(pen, RectangleF.Inflate(ib, 4 * k, 4 * k));
                    ChatGfx.Arrow(g, cx, cy, Angle * MathF.PI / 180f, ModernTheme.Accent, ib.Width * 0.42f, Color.White, 1.5f * k);
                    break;
                case BmKind.WorldPin:
                    using (var b = new SolidBrush(ModernTheme.GlowColour)) g.FillEllipse(b, RectangleF.Inflate(ib, 4 * k, 4 * k));
                    using (var b = new SolidBrush(ModernTheme.Accent)) g.FillEllipse(b, ib);
                    using (var pen = new Pen(Color.White, 2f * k)) g.DrawEllipse(pen, RectangleF.Inflate(ib, -1 * k, -1 * k));
                    using (var pen = new Pen(Color.Black, Math.Max(1f, k))) g.DrawEllipse(pen, RectangleF.Inflate(ib, 0.5f * k, 0.5f * k));
                    break;
            }

            if (Focus)
                using (var pen = new Pen(Color.White, 2f * k)) g.DrawEllipse(pen, cx - 20 * k / 1.4f, cy - 20 * k / 1.4f, 40 * k / 1.4f, 40 * k / 1.4f);

            if (Badge != null && Kind != BmKind.Waypoint)
            {
                Font bf = ModernSkin.Font(Kind == BmKind.Mob ? 9 : 8, true);
                float bw = Math.Max((Kind == BmKind.Mob ? 15 : 13) * k, ChatGfx.Measure(Badge, bf) + 6 * k), bh = (Kind == BmKind.Mob ? 13 : 12) * k;
                RectangleF br = Kind == BmKind.Mob ? new RectangleF(ib.Right + 7 * k - bw, ib.Bottom + 5 * k - bh, bw, bh) : new RectangleF(ib.Right + 8 * k - bw, ib.Y - 6 * k, bw, bh);
                Color border = Kind == BmKind.Exit ? ModernTheme.Accent : BigMapWindow.DifficultyColour(Diff);
                using (GraphicsPath bp = ModernSkin.RoundRect(br, bh / 2f))
                {
                    using (var b = new SolidBrush(Color.Black)) g.FillPath(b, bp);
                    using var pen = new Pen(border, Math.Max(1f, k));
                    g.DrawPath(pen, bp);
                }
                ModernSkin.Text(g, Badge, bf, Color.White, br, StringAlignment.Center);
            }

            if (ShowLabel)
            {
                Font lf = LabelFont;
                float lw = ChatGfx.Measure(Label, lf);
                float ly = ib.Bottom + (Kind == BmKind.Mob ? 3 * k : 1 * k);
                ChatGfx.ShadowText(g, Label, lf, Color.White, (Size.Width - lw) / 2f, ly, 1f, 1.4f);
            }
        }

        private void PaintZone(Graphics g, float k)
        {
            Color c = BigMapWindow.DifficultyColour(Diff);
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, Math.Min(r.Width, r.Height) * 0.22f);
            using (var b = new SolidBrush(ModernTheme.Alpha(c, Diff == BigMapWindow.Difficulty.Easy ? 0.12f : 0.10f))) g.FillPath(b, p);
            using var pen = new Pen(ModernTheme.Alpha(c, Diff == BigMapWindow.Difficulty.Easy ? 0.5f : 0.72f), Math.Max(1f, k));
            g.DrawPath(pen, p);
        }

        private void PaintSafe(Graphics g, float k)
        {
            float w = 2f * k;
            RectangleF r = new RectangleF(w / 2, w / 2, Size.Width - w, Size.Height - w);
            using GraphicsPath p = ModernSkin.RoundRect(r, 3 * k);
            using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Ok, 0.10f))) g.FillPath(b, p);
            using var pen = new Pen(ModernTheme.Alpha(ModernTheme.Ok, 0.8f), w);
            if (!Start) pen.DashStyle = DashStyle.Dash;
            g.DrawPath(pen, p);
        }
    }

    /// <summary>A world-map region (the icon's MapLinkIcon region at its offset, scaled), shown while its pin is hovered.</summary>
    internal sealed class BmRegion : BmMarker
    {
        private readonly int _index;

        public BmRegion(int index) : base(BmKind.Region, null)
        {
            _index = index;
        }

        protected internal override void DrawControl()
        {
            if (Size.Width <= 0 || Size.Height <= 0) return;
            try
            {
                Libraries.MapLinkIcon.Draw(_index, DisplayLocation, Size, Color.White);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] big map region", ex);
            }
        }
    }

    /// <summary>A library image fitted into its box: a MapLinkIcon (NPC icons) or a monster's standing picture facing down-right
    /// (bigMap.ts monsterPortrait: Standing frame, direction 3; the first frame when the library has fewer directions).</summary>
    internal sealed class BmLibIcon : MirControl
    {
        public MLibrary Lib;
        public int Index = -1;
        private int _monster = -1;

        public BmLibIcon()
        {
            NotControl = true;
            BackColour = Color.Transparent;
        }

        public int MonsterImage
        {
            get => _monster;
            set
            {
                _monster = value;
                Lib = null;
                Index = -1;
                if (value < 0 || value >= Libraries.Monsters.Length) return;
                MLibrary lib = Libraries.Monsters[value];
                FrameSet frames;
                try { frames = lib.Frames ?? FrameSet.DefaultMonster; }
                catch (Exception ex) { ModernLog.Once("[ModernUi] monster frames", ex); return; }
                int index = 0;
                if (frames != null && frames.TryGetValue(MirAction.Standing, out Frame f))
                {
                    index = f.Start + 3 * f.OffSet;
                    if (lib.GetSize(index).IsEmpty) index = f.Start;
                }
                Lib = lib;
                Index = index;
            }
        }

        protected internal override void DrawControl()
        {
            if (Lib == null || Index < 0 || Size.Width <= 0) return;
            Size s = Lib.GetSize(Index);
            if (s.Width <= 0 || s.Height <= 0) return;
            float f = Math.Min(Size.Width / (float)s.Width, Size.Height / (float)s.Height);
            if (_monster < 0) f = Math.Min(f, 1.5f * ModernRoot.K);
            int w = Math.Max(1, (int)(s.Width * f)), h = Math.Max(1, (int)(s.Height * f));
            Point at = DisplayLocation;
            int y = _monster >= 0 ? at.Y + (int)((Size.Height - h) * 0.4f) : at.Y + (Size.Height - h) / 2;
            try
            {
                Lib.Draw(Index, new Point(at.X + (Size.Width - w) / 2, y), new Size(w, h), Color.White);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] big map icon", ex);
            }
        }
    }

    /// <summary>A rounded dark pill (bigMap.ts .crumb / .coords / .legend / .loading).</summary>
    internal sealed class BmPill : ModernControl
    {
        private readonly float _font;
        private readonly bool _bold, _mono;
        private readonly Color _colour;
        private string _text = string.Empty, _sub = string.Empty;
        private int _maxWidth = int.MaxValue;
        public bool Plain, Legend;

        public BmPill(float font, bool bold, Color colour, bool mono = false)
        {
            _font = font;
            _bold = bold;
            _colour = colour;
            _mono = mono;
        }

        private Font TextFont => _mono ? MonoFont() : ModernSkin.Font(_font, _bold);

        private static readonly Dictionary<int, Font> _monoFonts = new Dictionary<int, Font>();

        private Font MonoFont()
        {
            int px = Math.Max(6, (int)Math.Round(_font * K * 4));
            if (!_monoFonts.TryGetValue(px, out Font f)) _monoFonts[px] = f = new Font("Consolas", px / 4f, FontStyle.Bold, GraphicsUnit.Pixel);
            return f;
        }

        public string Text
        {
            get => _text;
            set
            {
                value ??= string.Empty;
                if (_text == value) return;
                _text = value;
                Measure();
            }
        }

        public string Sub
        {
            get => _sub;
            set
            {
                value ??= string.Empty;
                if (_sub == value) return;
                _sub = value;
                Measure();
            }
        }

        public int MaxWidth { get => _maxWidth; set { if (_maxWidth == value) return; _maxWidth = value; Measure(); } }

        private static readonly (ClientTextKeys Key, int Diff)[] LegendItems =
        {
            (ClientTextKeys.ModernChatMapEasy, 0), (ClientTextKeys.ModernChatMapYourLevel, 1), (ClientTextKeys.ModernChatMapHard, 2),
            (ClientTextKeys.ModernChatMapDeadly, 3), (ClientTextKeys.ModernChatMapGuard, 4),
        };

        private void Measure()
        {
            Font f = TextFont;
            float w;
            int h;
            if (Legend)
            {
                w = S(20);
                for (int i = 0; i < LegendItems.Length; i++) w += S(13) + ChatGfx.Measure(GameLanguage.ClientTextMap.GetLocalization(LegendItems[i].Key), f) + S(10);
                h = S(20);
            }
            else
            {
                w = ChatGfx.Measure(_text, f) + (Plain ? S(10) : S(24));
                if (_sub.Length > 0) w += S(8) + ChatGfx.Measure(_sub, ModernSkin.Font(10, true));
                h = Plain ? S(22) : _font >= 13 ? S(27) : S(22);
            }
            Size = new Size(Math.Min(_maxWidth, (int)Math.Ceiling(w)), h);
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            if (!Plain)
            {
                using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
                using (var b = new SolidBrush(Color.FromArgb(166, 0, 0, 0))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
            }
            Font f = TextFont;
            if (Legend)
            {
                float x = S(10);
                for (int i = 0; i < LegendItems.Length; i++)
                {
                    float d = 9 * k;
                    using (var b = new SolidBrush(BigMapWindow.DifficultyColour((BigMapWindow.Difficulty)LegendItems[i].Diff))) g.FillEllipse(b, x, (Size.Height - d) / 2f, d, d);
                    x += S(13);
                    string s = GameLanguage.ClientTextMap.GetLocalization(LegendItems[i].Key);
                    ModernSkin.Text(g, s, f, ModernTheme.Dim, new RectangleF(x, 0, ChatGfx.Measure(s, f) + 2, Size.Height));
                    x += ChatGfx.Measure(s, f) + S(10);
                }
                return;
            }
            float tx = Plain ? S(5) : S(12);
            float subW = _sub.Length > 0 ? ChatGfx.Measure(_sub, ModernSkin.Font(10, true)) + S(8) : 0;
            ModernSkin.Text(g, _text, f, _colour, new RectangleF(tx, 0, Size.Width - tx - S(Plain ? 5 : 12) - subW, Size.Height), Plain ? StringAlignment.Center : StringAlignment.Near);
            if (subW > 0)
                ModernSkin.Text(g, _sub, ModernSkin.Font(10, true), ModernTheme.Dim, new RectangleF(Size.Width - S(12) - subW + S(8), 0, subW, Size.Height));
        }
    }

    /// <summary>"Spawns of <b>name</b> ×" (bigMap.ts .selpill): the × shows no spawn areas.</summary>
    internal sealed class BmSelPill : ModernControl
    {
        private readonly BigMapWindow _w;
        private string _prefix = string.Empty, _name = string.Empty;

        public BmSelPill(BigMapWindow w)
        {
            _w = w;
            Sound = SoundList.ButtonA;
            Hint = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapNoSpawns);
        }

        public void Set(string prefix, string name)
        {
            _prefix = prefix ?? string.Empty;
            _name = name ?? string.Empty;
            Font f = ModernSkin.Font(11), b = ModernSkin.Font(11, true);
            Size = new Size((int)Math.Ceiling(S(10) + ChatGfx.Measure(_prefix, f) + ChatGfx.Measure(_name, b) + S(6) + S(18) + S(6)), S(26));
            Invalidate();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            // only the × (the last 24 px) clears the selection
            if (CMain.MPoint.X - DisplayLocation.X >= Size.Width - S(26)) _w.ClearMob();
        }

        protected override void Paint(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(Color.FromArgb(166, 0, 0, 0))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Accent);
            }
            Font f = ModernSkin.Font(11), bold = ModernSkin.Font(11, true);
            float x = S(10);
            ModernSkin.Text(g, _prefix, f, Color.White, new RectangleF(x, 0, ChatGfx.Measure(_prefix, f) + 2, Size.Height));
            x += ChatGfx.Measure(_prefix, f);
            ModernSkin.Text(g, _name, bold, ModernTheme.Accent, new RectangleF(x, 0, ChatGfx.Measure(_name, bold) + 2, Size.Height));
            ModernSkin.Text(g, "×", f, Hovered ? Color.White : ModernTheme.Dim, new RectangleF(Size.Width - S(24), 0, S(18), Size.Height), StringAlignment.Center);
        }
    }

    /// <summary>The zoom group (bigMap.ts .zoom: −, the percent, +, ⤢ fit).</summary>
    internal sealed class BmZoom : ModernControl
    {
        private readonly BigMapWindow _w;
        private int _percent = 100, _hover = -1;

        public BmZoom(BigMapWindow w)
        {
            _w = w;
            Sound = SoundList.ButtonA;
            Size = new Size(S(30 * 3 + 50), S(26));
        }

        public int Percent { get => _percent; set { if (_percent == value) return; _percent = value; Invalidate(); } }

        private int Part(Point screen)
        {
            int x = screen.X - DisplayLocation.X;
            if (x < S(30)) return 0;
            if (x < S(80)) return -1;
            if (x < S(110)) return 2;
            return 3;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int p = Part(CMain.MPoint);
            if (p == _hover) return;
            _hover = p;
            Hint = GameLanguage.ClientTextMap.GetLocalization(p switch
            {
                0 => ClientTextKeys.ModernChatMapZoomOut,
                2 => ClientTextKeys.ModernChatMapZoomIn,
                3 => ClientTextKeys.ModernChatMapFit,
                _ => ClientTextKeys.ModernChatMapZoomIn,
            });
            Invalidate();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            _hover = -1;
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            int p = Part(CMain.MPoint);
            if (p < 0) return;
            base.OnMouseClick(e);
            if (p == 0) _w.ZoomBy(1 / 1.4f);
            else if (p == 2) _w.ZoomBy(1.4f);
            else _w.FitAndApply();
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
            using (var b = new SolidBrush(Color.FromArgb(166, 0, 0, 0))) g.FillPath(b, p);
            float[] edges = { 0, S(30), S(80), S(110), Size.Width };
            string[] text = { "−", _percent + "%", "+", "⤢" };
            Region old = g.Clip;
            g.SetClip(p, CombineMode.Intersect);
            for (int i = 0; i < 4; i++)
            {
                RectangleF cell = new RectangleF(edges[i], 0, edges[i + 1] - edges[i], Size.Height);
                if (i == _hover && i != 1)
                    using (var hb = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.3f))) g.FillRectangle(hb, cell);
                if (i > 0)
                    using (var lp = new Pen(ModernTheme.Alpha(ModernTheme.Frame, 0.6f), 1f)) g.DrawLine(lp, cell.X, 0, cell.X, Size.Height);
                Font f = i == 1 ? ModernSkin.Font(11) : ModernSkin.Font(15, true);
                ModernSkin.Text(g, text[i], f, i == _hover ? Color.White : ModernTheme.Text, cell, StringAlignment.Center);
            }
            g.Clip = old;
            old.Dispose();
            ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
        }
    }

    /// <summary>The sidebar's divider (border-right of the viewport).</summary>
    internal sealed class BmDivider : ModernControl
    {
        public BmDivider() { NotControl = true; }

        protected override void Paint(Graphics g)
        {
            using var b = new SolidBrush(ModernSkin.Dim(ModernTheme.Frame, 0.6f));
            g.FillRectangle(b, 0, 0, Size.Width, Size.Height);
        }
    }

    /// <summary>A sidebar list row (bigMap.ts .li): an icon (map glyph / MapLinkIcon / monster portrait), the name and a dim
    /// line, an optional button (Teleport / View) or level badge; or a plain note (.empty-note).</summary>
    internal sealed class BmRow : ModernControl
    {
        public string Title = string.Empty, Sub, Note, ButtonText, ButtonHint, Level;
        public Color LevelColour;
        public int GlyphIcon = -1, LinkIcon = -1, MobImage = -1;
        public bool Selected;
        public Action ButtonAct;
        private ModernButton _button;

        public BmRow()
        {
            Sound = SoundList.ButtonA;
            Size = new Size(S(200), S(34));
        }

        public void Layout()
        {
            if (Note != null)
            {
                NotControl = true;
                Size = new Size(Size.Width, (int)Math.Ceiling(ModernSkin.MeasureWrapped(Note, ModernSkin.Font(11), Size.Width - S(12)).Height) + S(12));
                return;
            }
            int h = MobImage >= 0 ? S(38) : S(34);
            Size = new Size(Size.Width, h);
            int x = S(6);
            if (GlyphIcon >= 0)
                new ChatIconView(GlyphIcon) { Parent = this, Size = new Size(S(16), S(16)), Location = new Point(x, (h - S(16)) / 2), Colour = ModernTheme.Accent };
            else if (LinkIcon > 0)
                new BmLibIcon { Parent = this, Lib = Libraries.MapLinkIcon, Index = LinkIcon, Size = new Size(S(18), S(18)), Location = new Point(x, (h - S(18)) / 2) };
            else if (MobImage >= 0)
                new BmLibIcon { Parent = this, MonsterImage = MobImage, Size = new Size(S(22), S(22)), Location = new Point(x + S(2), (h - S(22)) / 2) };
            if (ButtonText != null)
            {
                _button = new ModernButton(ButtonText) { Parent = this, Hint = ButtonHint, FontSize = 10 };
                _button.AutoWidth(40);
                _button.Size = new Size(_button.Size.Width, S(20));
                _button.Location = new Point(Size.Width - S(6) - _button.Size.Width, (h - _button.Size.Height) / 2);
                _button.Click += (o, e) => ButtonAct?.Invoke();
            }
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (Note != null) return;
            base.OnMouseClick(e);
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            if (Note != null)
            {
                ModernSkin.Text(g, Note, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(S(6), S(6), Size.Width - S(12), Size.Height - S(12)), StringAlignment.Near, StringAlignment.Near, false, true);
                return;
            }
            if (Selected || Hovered)
            {
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, Selected ? 0.18f : 0.10f))) g.FillPath(b, p);
                if (Selected) using (var bar = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(bar, r.X, r.Y + 2 * k, 2 * k, r.Height - 4 * k);
            }
            if (MobImage >= 0)
            {
                RectangleF c = new RectangleF(S(6), (Size.Height - S(26)) / 2f, S(26), S(26));
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(c);
                    using var brush = new PathGradientBrush(path) { CenterColor = ModernTheme.Hex("#2c3442"), SurroundColors = new[] { ModernTheme.Hex("#090c12") } };
                    g.FillPath(brush, path);
                }
                using (var pen = new Pen(ModernTheme.Frame, 1f)) g.DrawEllipse(pen, c);
            }
            float left = S(6) + (MobImage >= 0 ? S(26) : S(18)) + S(8);
            float right = Size.Width - S(6) - (_button != null ? _button.Size.Width + S(8) : 0);
            if (Level != null)
            {
                Font lf = ModernSkin.Font(10, true);
                float lw = ChatGfx.Measure(Level, lf) + S(10), lh = S(16);
                RectangleF lr = new RectangleF(right - lw, (Size.Height - lh) / 2f, lw, lh);
                using (GraphicsPath lp = ModernSkin.RoundRect(lr, 6 * k)) ModernSkin.Stroke(g, lp, LevelColour);
                ModernSkin.Text(g, Level, lf, LevelColour, lr, StringAlignment.Center);
                right = lr.X - S(6);
            }
            bool hasSub = !string.IsNullOrEmpty(Sub);
            float th = S(16);
            float ty = hasSub ? Size.Height / 2f - th + S(1) : (Size.Height - th) / 2f;
            ModernSkin.Text(g, Title, ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(left, ty, right - left, th));
            if (hasSub) ModernSkin.Text(g, Sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(left, Size.Height / 2f + S(1), right - left, S(14)));
        }
    }
}
