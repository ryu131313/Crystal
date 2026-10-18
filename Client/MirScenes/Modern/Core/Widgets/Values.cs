using System.Drawing.Drawing2D;

namespace Client.MirScenes.Modern.Core.Widgets
{
    /// <summary>
    /// A slider (popups.ts amount range, the options page knobs): drag the knob or the track, wheel steps. Integer values in
    /// <see cref="Min"/>..<see cref="Max"/> by <see cref="Step"/>; an optional value label on the right (<see cref="Format"/>).
    /// <see cref="Set"/> does not raise <see cref="Changed"/>. While dragged it keeps the mouse (MirControl.Moving routing).
    /// </summary>
    public sealed class Slider : ModernControl
    {
        private int _min, _max, _step, _value;
        private bool _drag;
        private readonly int _labelWidth;
        private Func<int, string> _format;
        private string _label = string.Empty;

        public Slider(int min, int max, int value, int widthHud, int step = 1, bool showValue = true)
        {
            _min = min;
            _max = Math.Max(min, max);
            _step = Math.Max(1, step);
            _value = Clamp(value);
            _labelWidth = showValue ? S(44) : 0;
            Size = new Size(S(widthHud), S(20));
            UpdateLabel();
        }

        public event Action<int> Changed;

        public int Min => _min;
        public int Max => _max;
        public int Step => _step;
        public int Value => _value;

        /// <summary>The value label (default: the number).</summary>
        public Func<int, string> Format
        {
            get => _format;
            set
            {
                _format = value;
                UpdateLabel();
                Invalidate();
            }
        }

        public void SetRange(int min, int max, int step = 1)
        {
            _min = min;
            _max = Math.Max(min, max);
            _step = Math.Max(1, step);
            Set(_value);
            Invalidate();
        }

        public void Set(int value)
        {
            value = Clamp(value);
            if (value == _value) return;
            _value = value;
            UpdateLabel();
            Invalidate();
        }

        private int Clamp(int v)
        {
            v = Math.Min(_max, Math.Max(_min, v));
            if (_step > 1) v = _min + (int)Math.Round((v - _min) / (double)_step) * _step;
            return Math.Min(_max, Math.Max(_min, v));
        }

        private void UpdateLabel()
        {
            if (_labelWidth == 0) return;
            _label = _format != null ? _format(_value) ?? string.Empty : _value.ToString();
        }

        private void Change(int value)
        {
            value = Clamp(value);
            if (value == _value) return;
            _value = value;
            UpdateLabel();
            Invalidate();
            Changed?.Invoke(_value);
        }

        private float TrackLeft => S(7);
        private float TrackRight => Size.Width - _labelWidth - S(7);

        private void FromMouse()
        {
            float x = CMain.MPoint.X - DisplayLocation.X;
            float t = Math.Clamp((x - TrackLeft) / Math.Max(1f, TrackRight - TrackLeft), 0f, 1f);
            Change(_min + (int)Math.Round(t * (_max - _min)));
        }

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            _drag = true;
            Moving = true; // the scene keeps sending moves here while the button is down
            FromMouse();
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag)
            {
                FromMouse();
                return;
            }
            base.OnMouseMove(e);
        }

        public override void OnMouseUp(MouseEventArgs e)
        {
            bool was = _drag;
            _drag = false;
            Moving = false;
            base.OnMouseUp(e);
            if (was) Invalidate();
        }

        protected override bool OnWheel(MouseEventArgs e)
        {
            Change(_value + (e.Delta > 0 ? _step : -_step));
            return true;
        }

        public override bool IsMouseOver(Point p) => Visible && (DisplayRectangle.Contains(p) || _drag);

        protected override void Paint(Graphics g)
        {
            float k = K, cy = Size.Height / 2f, l = TrackLeft, r = TrackRight, th = 4 * k;
            float t = _max > _min ? (_value - _min) / (float)(_max - _min) : 0f;
            float kx = l + t * (r - l);

            RectangleF track = new RectangleF(l, cy - th / 2f, Math.Max(1, r - l), th);
            using (GraphicsPath p = ModernSkin.RoundRect(track, th / 2f))
            {
                using (var b = new SolidBrush(ModernTheme.Field))
                    g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.8f));
            }
            if (kx > l)
            {
                RectangleF fill = new RectangleF(l, cy - th / 2f, kx - l, th);
                using GraphicsPath p = ModernSkin.RoundRect(fill, th / 2f);
                using var b = new LinearGradientBrush(new RectangleF(l - 1, fill.Y, kx - l + 2, th), ModernTheme.AccentDim, ModernTheme.Accent, LinearGradientMode.Horizontal);
                g.FillPath(b, p);
            }
            float d = 12 * k;
            RectangleF knob = new RectangleF(kx - d / 2f, cy - d / 2f, d, d);
            if (Hovered || _drag)
                using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.3f)))
                    g.FillEllipse(b, RectangleF.Inflate(knob, 3 * k, 3 * k));
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, knob);
            using (var pen = new Pen(ModernTheme.Accent, Math.Max(1f, 2f * k)))
                g.DrawEllipse(pen, knob);

            if (_labelWidth > 0)
                ModernSkin.Text(g, _label, ModernSkin.Font(11, true), ModernTheme.Text,
                    new RectangleF(Size.Width - _labelWidth, 0, _labelWidth, Size.Height), StringAlignment.Far);
        }
    }

    /// <summary>A horizontal progress bar with an optional centred caption. Repaints only when the filled width (in pixels) or
    /// the caption changes, so a per-frame <see cref="Set"/> is cheap.</summary>
    public sealed class ModernProgressBar : ModernControl
    {
        private float _value;
        private int _px = -1;
        private string _text = string.Empty;
        private Color _colour = Color.Empty;

        public ModernProgressBar(int widthHud, int heightHud = 12)
        {
            Size = new Size(S(widthHud), S(heightHud));
            NotControl = true;
        }

        public float Value => _value;

        /// <summary>Fill colour; Color.Empty = the accent.</summary>
        public Color Colour
        {
            get => _colour;
            set
            {
                if (_colour == value) return;
                _colour = value;
                Invalidate();
            }
        }

        public void Set(float fraction, string text = null)
        {
            if (float.IsNaN(fraction)) fraction = 0;
            fraction = Math.Clamp(fraction, 0f, 1f);
            text ??= string.Empty;
            int px = (int)Math.Round(fraction * (Size.Width - 2));
            _value = fraction;
            if (px == _px && text == _text) return;
            _px = px;
            _text = text;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            float rad = Math.Min(r.Height / 2f, ModernTheme.Radius * 0.8f * k);
            using (GraphicsPath p = ModernSkin.RoundRect(r, rad))
            {
                using (var b = new SolidBrush(ModernTheme.Field))
                    g.FillPath(b, p);
                if (_px > 0)
                {
                    Color c = _colour.IsEmpty ? ModernTheme.Accent : _colour;
                    RectangleF fill = new RectangleF(r.X, r.Y, Math.Min(r.Width, _px + 1), r.Height);
                    Region old = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height + 1), ModernTheme.Mix(c, Color.White, 0.8f), ModernTheme.Mix(c, Color.Black, 0.7f), LinearGradientMode.Vertical))
                        g.FillRectangle(b, fill);
                    g.Clip = old;
                    old.Dispose();
                }
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            if (_text.Length > 0)
            {
                Font f = ModernSkin.Font(Math.Max(8f, Size.Height / k - 3), true);
                // a 1px shadow for legibility on the fill
                ModernSkin.Text(g, _text, f, Color.FromArgb(200, 0, 0, 0), new RectangleF(1, 1, Size.Width, Size.Height), StringAlignment.Center);
                ModernSkin.Text(g, _text, f, Color.White, new RectangleF(0, 0, Size.Width, Size.Height), StringAlignment.Center);
            }
        }
    }

    /// <summary>
    /// A progress ring (widgets.ts progressRing, .ui-ring): a background circle, an accent arc from 12 o'clock, a big and a small
    /// caption in the middle. Quantised to 64 steps: it repaints only when the step or a caption changes.
    /// </summary>
    public sealed class ProgressRing : ModernControl
    {
        public const int Steps = 64;
        private readonly float _strokeHud;
        private int _step = -1;
        private string _big = string.Empty, _small = string.Empty;
        private Color _colour = Color.Empty;

        public ProgressRing(int sizeHud = 62, float strokeHud = 6)
        {
            _strokeHud = strokeHud;
            Size = new Size(S(sizeHud), S(sizeHud));
            NotControl = true;
        }

        /// <summary>Arc colour; Color.Empty = the accent (widgets.ts --ring).</summary>
        public Color Colour
        {
            get => _colour;
            set
            {
                if (_colour == value) return;
                _colour = value;
                Invalidate();
            }
        }

        public int Step => _step;

        public void Set(float fraction, string big = null, string small = null)
        {
            if (float.IsNaN(fraction) || float.IsInfinity(fraction)) fraction = 0;
            int step = (int)Math.Round(Math.Clamp(fraction, 0f, 1f) * Steps);
            big ??= string.Empty;
            small ??= string.Empty;
            if (step == _step && big == _big && small == _small) return;
            _step = step;
            _big = big;
            _small = small;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, sw = _strokeHud * k, d = Math.Min(Size.Width, Size.Height) - sw - 1;
            RectangleF r = new RectangleF((Size.Width - d) / 2f, (Size.Height - d) / 2f, d, d);
            using (var pen = new Pen(Color.FromArgb(0x05, 0x07, 0x0c), sw))
                g.DrawEllipse(pen, r);
            if (_step > 0)
            {
                Color c = _colour.IsEmpty ? ModernTheme.Accent : _colour;
                using var pen = new Pen(c, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                float sweep = 360f * _step / Steps;
                if (_step >= Steps) g.DrawEllipse(pen, r);
                else g.DrawArc(pen, r, -90f, sweep);
            }
            Font bf = ModernSkin.Font(13, true), sf = ModernSkin.Font(8);
            if (_small.Length == 0)
            {
                ModernSkin.Text(g, _big, bf, Color.White, new RectangleF(0, 0, Size.Width, Size.Height), StringAlignment.Center, StringAlignment.Center, false);
            }
            else
            {
                float bh = bf.GetHeight(g), sh = sf.GetHeight(g), top = (Size.Height - bh - sh) / 2f;
                ModernSkin.Text(g, _big, bf, Color.White, new RectangleF(0, top, Size.Width, bh), StringAlignment.Center, StringAlignment.Center, false);
                ModernSkin.Text(g, _small, sf, ModernTheme.Dim, new RectangleF(0, top + bh, Size.Width, sh), StringAlignment.Center, StringAlignment.Center, false);
            }
        }
    }
}
