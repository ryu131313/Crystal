using System.Drawing.Drawing2D;
using Client.MirSounds;

namespace Client.MirScenes.Modern.Core.Widgets
{
    /// <summary>The look of a <see cref="ModernButton"/> (styles.ts .btn / .btn.main / .btn.warn).</summary>
    public enum ButtonKind
    {
        Normal,
        Main,
        Warn,
    }

    /// <summary>
    /// The new UI's button (core/widgets.ts button(), CONTROL_CSS .btn): pc→pb gradient, 1px frame, black ring, hover = accent
    /// border + inner glow, main / warn colourings, big size. Disabled keeps the mouse so its hint (the reason) still shows, and
    /// ignores clicks. Sizes in device px; <see cref="AutoWidth"/> fits the text.
    /// </summary>
    public class ModernButton : ModernControl
    {
        private string _text;
        private ButtonKind _kind;
        private bool _big, _disabled;
        private string _hint = string.Empty, _why = string.Empty;

        public ModernButton(string text = "", ButtonKind kind = ButtonKind.Normal, bool big = false)
        {
            _text = text ?? string.Empty;
            _kind = kind;
            _big = big;
            Sound = SoundList.ButtonA;
            Size = new Size(S(84), BarHeight(big));
        }

        /// <summary>The standard height (device px) of a normal / big button.</summary>
        public static int BarHeight(bool big) => ModernRoot.S(big ? 30 : 22);

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

        public ButtonKind Kind { get => _kind; set { if (_kind == value) return; _kind = value; Invalidate(); } }

        public bool Big { get => _big; set { if (_big == value) return; _big = value; Invalidate(); } }

        public bool Disabled => _disabled;

        /// <summary>The hover hint while enabled (widgets.ts title).</summary>
        public string Tooltip
        {
            get => _hint;
            set
            {
                _hint = value ?? string.Empty;
                Hint = _disabled && _why.Length > 0 ? _why : _hint;
            }
        }

        /// <summary>Disables the button (still hoverable; the hint shows <paramref name="why"/>).</summary>
        public void SetDisabled(bool disabled, string why = null)
        {
            _why = why ?? string.Empty;
            Hint = disabled && _why.Length > 0 ? _why : _hint;
            if (_disabled == disabled) return;
            _disabled = disabled;
            Invalidate();
        }

        /// <summary>Sizes the button to its text (device px), at least <paramref name="minHud"/> HUD px wide.</summary>
        public void AutoWidth(int minHud = 0)
        {
            Size = new Size(Math.Max(S(minHud), MeasureText(_text)), Size.Height);
        }

        protected int MeasureText(string text) => (int)Math.Ceiling(ModernSkin.Measure(text, TextFont)) + S(_big ? 32 : 24) + 2;

        /// <summary>Label size in HUD px (0 = 11, or 12 when big).</summary>
        public float FontSize { get => _fontSize; set { if (_fontSize == value) return; _fontSize = value; Invalidate(); } }
        private float _fontSize;

        protected Font TextFont => ModernSkin.Font(_fontSize > 0 ? _fontSize : _big ? 12 : 11, true);

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (_disabled) return;
            base.OnMouseClick(e);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_disabled) return;
            OnMouseClick(e); // a fast second click is a click, not a double click
        }

        /// <summary>The kind the button paints as now (ConfirmTwiceButton shows Warn while armed).</summary>
        protected virtual ButtonKind PaintKind => _kind;

        /// <summary>The label painted now.</summary>
        protected virtual string PaintText => _text;

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * 0.8f * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF inner = RectangleF.Inflate(outer, -1f, -1f);
            bool hot = Hovered && !_disabled;
            float a = _disabled ? 0.45f : 1f;

            Color top, bottom, border, text;
            switch (PaintKind)
            {
                case ButtonKind.Main:
                    top = ModernTheme.Mix(ModernTheme.Accent, ModernTheme.PanelC, 0.5f);
                    bottom = ModernTheme.Mix(ModernTheme.Accent, ModernTheme.PanelB, 0.25f);
                    border = ModernTheme.Accent;
                    text = Color.White;
                    break;
                case ButtonKind.Warn:
                    top = ModernTheme.Mix(ModernTheme.Bad, ModernTheme.PanelC, 0.45f);
                    bottom = ModernTheme.Mix(ModernTheme.Bad, ModernTheme.PanelB, 0.22f);
                    border = ModernTheme.Bad;
                    text = Color.White;
                    break;
                default:
                    top = ModernTheme.PanelC;
                    bottom = ModernTheme.PanelB;
                    border = ModernTheme.Frame;
                    text = ModernTheme.Text;
                    break;
            }
            if (_disabled)
            {
                border = ModernTheme.Frame;
                text = ModernTheme.Text;
            }
            else if (hot)
            {
                border = PaintKind == ButtonKind.Warn ? ModernTheme.Mix(ModernTheme.Bad, Color.White, 0.8f) : ModernTheme.Accent;
                text = Color.White;
            }
            if (Pressed && !_disabled)
            {
                Color t = top;
                top = bottom;
                bottom = t;
            }

            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Fade(Color.Black, a));

            using (GraphicsPath path = ModernSkin.RoundRect(inner, rad))
            {
                ModernSkin.FillVertical(g, path, inner, new[] { Fade(top, a), Fade(bottom, a) });
                PaintFill(g, path, inner);
                if (hot)
                {
                    // the hover glow (box-shadow 0 0 var(--glr) var(--glow)) drawn inside the bounds: an inner accent haze
                    Region old = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    ModernSkin.Stroke(g, path, ModernTheme.Alpha(border, 0.35f * Math.Min(1f, ModernTheme.Glow)), Math.Max(2f, 5f * k));
                    g.Clip = old;
                    old.Dispose();
                }
                ModernSkin.Stroke(g, path, Fade(border, a));
            }

            ModernSkin.Text(g, PaintText, TextFont, Fade(text, a), new RectangleF(inner.X + 4 * k, inner.Y, inner.Width - 8 * k, inner.Height), StringAlignment.Center);
        }

        /// <summary>Extra fill between the gradient and the frame (HoldButton's progress).</summary>
        protected virtual void PaintFill(Graphics g, GraphicsPath path, RectangleF inner) { }

        protected static Color Fade(Color c, float a) => ModernSkin.Dim(c, a);
    }

    /// <summary>
    /// A destructive button that needs a second click within <see cref="TimeoutMs"/> (widgets.ts confirmTwice, the mocks'
    /// "Click again to …"). The first click arms it (red, <see cref="ConfirmText"/>); the timeout or <see cref="Reset"/> disarms.
    /// </summary>
    public sealed class ConfirmTwiceButton : ModernButton, IModernTick
    {
        private bool _armed;
        private long _until;

        public ConfirmTwiceButton(string text, ButtonKind kind = ButtonKind.Normal, bool big = false) : base(text, kind, big)
        {
            ConfirmText = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernConfirmAgain);
        }

        /// <summary>The second click.</summary>
        public event Action Confirmed;

        public string ConfirmText { get; set; }
        public int TimeoutMs { get; set; } = 3000;
        public bool Armed => _armed;

        protected override ButtonKind PaintKind => _armed ? ButtonKind.Warn : Kind;
        protected override string PaintText => _armed ? ConfirmText : Text;

        /// <summary>Fits both labels.</summary>
        public new void AutoWidth(int minHud = 0)
        {
            Size = new Size(Math.Max(S(minHud), Math.Max(MeasureText(Text), MeasureText(ConfirmText))), Size.Height);
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (Disabled || !Enabled) return;
            if (Sound != SoundList.None) SoundManager.PlaySound(Sound);
            if (_armed)
            {
                Reset();
                Confirmed?.Invoke();
                return;
            }
            _armed = true;
            _until = CMain.Time + TimeoutMs;
            ModernTick.Add(this);
            Invalidate();
        }

        /// <summary>Disarms (the row it belongs to changed).</summary>
        public void Reset()
        {
            ModernTick.Remove(this);
            if (!_armed) return;
            _armed = false;
            Invalidate();
        }

        void IModernTick.Tick()
        {
            if (IsDisposed || !Visible || CMain.Time >= _until) Reset();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTick.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Hold-to-confirm (widgets.ts holdButton): the left button must be held <see cref="HoldMs"/>; a fill runs across; release or
    /// leaving early cancels. Timed from CMain.Time by ModernTick (the window's Process); repaints only when the fill moves a step.
    /// </summary>
    public sealed class HoldButton : ModernButton, IModernTick
    {
        private const int Steps = 40;
        private long _start = -1;
        private int _step;

        public HoldButton(string text, bool danger = false, bool big = false) : base(text, ButtonKind.Normal, big)
        {
            Danger = danger;
        }

        /// <summary>Held long enough.</summary>
        public event Action Done;

        public int HoldMs { get; set; } = 1200;
        public bool Danger { get; set; }
        public bool Holding => _start >= 0;

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Disabled || !Enabled || e.Button != MouseButtons.Left || _start >= 0) return;
            _start = CMain.Time;
            _step = 0;
            ModernTick.Add(this);
        }

        public override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Cancel();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            Cancel();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            // a click is not a hold (only the sound)
            if (!Disabled && Enabled && Sound != SoundList.None) SoundManager.PlaySound(Sound);
        }

        public void Cancel()
        {
            ModernTick.Remove(this);
            if (_start < 0) return;
            _start = -1;
            if (_step != 0)
            {
                _step = 0;
                Invalidate();
            }
        }

        void IModernTick.Tick()
        {
            if (IsDisposed || !Visible)
            {
                Cancel();
                return;
            }
            long held = CMain.Time - _start;
            if (held >= HoldMs)
            {
                Cancel();
                Done?.Invoke();
                return;
            }
            int step = (int)(held * Steps / Math.Max(1, HoldMs));
            if (step == _step) return;
            _step = step;
            Invalidate();
        }

        protected override void PaintFill(Graphics g, GraphicsPath path, RectangleF inner)
        {
            if (_step <= 0) return;
            float w = inner.Width * _step / Steps;
            RectangleF fill = new RectangleF(inner.X, inner.Y, w, inner.Height);
            Color from, to;
            if (Kind != ButtonKind.Normal)
            {
                // a coloured button (popups.ts .b.main .fillh): a white haze runs across
                from = to = Color.FromArgb(56, 255, 255, 255);
            }
            else
            {
                from = Danger ? ModernTheme.Alpha(ModernTheme.Bad, 0.4f * 0.55f) : ModernTheme.Alpha(ModernTheme.Accent, 0.4f * 0.55f);
                to = Danger ? ModernTheme.Alpha(ModernTheme.Bad, 0.55f) : ModernTheme.Alpha(ModernTheme.Gold, 0.55f);
            }
            Region old = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            using (var brush = new LinearGradientBrush(new RectangleF(inner.X - 1, inner.Y, inner.Width + 2, inner.Height), from, to, LinearGradientMode.Horizontal))
                g.FillRectangle(brush, fill);
            g.Clip = old;
            old.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTick.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>A button that steps once on press, then faster while held (popups.ts amount - / +).</summary>
    public sealed class RepeatButton : ModernButton, IModernTick
    {
        private long _next = -1;
        private int _count;

        public RepeatButton(string text) : base(text)
        {
            Sound = SoundList.None;
        }

        /// <summary>One step; the argument is how many steps this hold has made so far (0 first).</summary>
        public event Action<int> Stepped;

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Disabled || !Enabled || e.Button != MouseButtons.Left) return;
            _count = 0;
            Step();
            ModernTick.Add(this);
        }

        public override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Stop();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            Stop();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
        }

        private void Step()
        {
            Stepped?.Invoke(_count);
            _count++;
            _next = CMain.Time + (_count < 6 ? 220 : 70);
        }

        private void Stop()
        {
            _next = -1;
            ModernTick.Remove(this);
        }

        void IModernTick.Tick()
        {
            if (IsDisposed || !Visible || _next < 0)
            {
                Stop();
                return;
            }
            if (CMain.Time >= _next) Step();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTick.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>A tick box with a label (popups.ts .chk: "Don't ask again").</summary>
    public sealed class ModernCheckBox : ModernControl
    {
        private bool _checked;
        private string _text;

        public ModernCheckBox(string text, bool isChecked = false)
        {
            _text = text ?? string.Empty;
            _checked = isChecked;
            Size = new Size((int)Math.Ceiling(ModernSkin.Measure(_text, ModernSkin.Font(11))) + S(24), S(18));
        }

        public event Action<bool> Changed;

        public bool Checked => _checked;

        public void Set(bool value)
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled) return;
            base.OnMouseClick(e);
            Set(!_checked);
            Changed?.Invoke(_checked);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K, box = 14 * k, y = (Size.Height - box) / 2f;
            RectangleF r = new RectangleF(0.5f, y, box, box);
            using (GraphicsPath p = ModernSkin.RoundRect(r, 4 * k))
            {
                using (var b = new SolidBrush(_checked ? ModernTheme.Accent : ModernTheme.Field))
                    g.FillPath(b, p);
                ModernSkin.Stroke(g, p, _checked || Hovered ? ModernTheme.Accent : ModernTheme.Frame);
            }
            if (_checked)
            {
                using var pen = new Pen(Color.White, Math.Max(1.5f, 2f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new[] { new PointF(r.X + box * 0.25f, r.Y + box * 0.52f), new PointF(r.X + box * 0.43f, r.Y + box * 0.7f), new PointF(r.X + box * 0.76f, r.Y + box * 0.3f) });
            }
            ModernSkin.Text(g, _text, ModernSkin.Font(11), Hovered ? ModernTheme.Text : ModernTheme.Dim,
                new RectangleF(box + 7 * k, 0, Size.Width - box - 7 * k, Size.Height));
        }
    }
}
