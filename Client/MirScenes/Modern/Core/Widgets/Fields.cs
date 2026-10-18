using System.Drawing.Drawing2D;
using Client.MirControls;

namespace Client.MirScenes.Modern.Core.Widgets
{
    /// <summary>
    /// A themed text field (styles.ts input.fld): a dark rounded frame with the accent border while focused, around a real
    /// <see cref="MirTextBox"/> (the native box stays off screen and is texture-rendered, client.md §3.5). The font is made fresh
    /// for the box (its Font setter scales and disposes what it is given). A placeholder is painted over the box while it is
    /// empty and unfocused. Keys typed into the box never reach the game (the native box has focus); Enter / Esc raise
    /// <see cref="Submitted"/> / <see cref="Escaped"/>. By default the field does not take focus when it first shows
    /// (<see cref="AutoFocus"/>), so opening a window does not swallow the hotkeys.
    /// </summary>
    public class ModernTextField : ModernControl
    {
        public readonly MirTextBox Box;
        private readonly ModernLabel _placeholder;
        private readonly int _padLeft, _padRight;
        private bool _focused;

        public ModernTextField(int widthHud, int heightHud = 24, float fontHud = 12, int padLeftHud = 8, int padRightHud = 8, bool bold = false)
        {
            _padLeft = S(padLeftHud);
            _padRight = S(padRightHud);
            Size = new Size(S(widthHud), S(heightHud));

            float px = fontHud * K;
            Box = new MirTextBox
            {
                Parent = this,
                BackColour = ModernTheme.Field,
                ForeColour = Color.White,
                CanLoseFocus = true,
                // MirTextBox.Font → ScaleFont: new Font(size * 96 / dpi) in points; px = pt * dpi / 72, so size = px * 0.75
                Font = new Font(ModernSkin.FontName, px * 0.75f, bold ? FontStyle.Bold : FontStyle.Regular),
            };
            int boxH = Math.Max(1, Box.TextBox.PreferredHeight);
            Box.Size = new Size(Math.Max(1, Size.Width - _padLeft - _padRight), boxH);
            Box.Location = new Point(_padLeft, Math.Max(1, (Size.Height - boxH) / 2));

            _placeholder = new ModernLabel
            {
                Parent = this,
                Location = Box.Location,
                Size = Box.Size,
                FontSize = fontHud,
                Colour = ModernTheme.Dim,
                Visible = false,
            };

            Box.Shown += (o, e) => DropFocusUnlessAuto();
            Box.TextBox.VisibleChanged += (o, e) => DropFocusUnlessAuto();
            Box.TextBox.GotFocus += (o, e) => FocusChanged();
            Box.TextBox.LostFocus += (o, e) => FocusChanged();
            Box.TextBox.TextChanged += (o, e) =>
            {
                UpdatePlaceholder();
                OnTextChanged();
                TextChanged?.Invoke(Text);
            };
            Box.TextBox.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    OnSubmitted();
                    Submitted?.Invoke(Text);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    Escaped?.Invoke();
                }
            };
        }

        /// <summary>The text changed (typed or set).</summary>
        public event Action<string> TextChanged;

        /// <summary>Enter in the box.</summary>
        public event Action<string> Submitted;

        /// <summary>Esc in the box (the box also drops focus).</summary>
        public event Action Escaped;

        /// <summary>Take the keyboard when first shown (popups' fields do; window fields do not).</summary>
        public bool AutoFocus { get; set; }

        public string Text
        {
            get => Box.Text ?? string.Empty;
            set => Box.Text = value ?? string.Empty;
        }

        public int MaxLength
        {
            get => Box.MaxLength;
            set => Box.MaxLength = value;
        }

        public bool Password
        {
            get => Box.Password;
            set => Box.Password = value;
        }

        public string Placeholder
        {
            get => _placeholder.Text;
            set
            {
                _placeholder.Text = value;
                UpdatePlaceholder();
            }
        }

        public bool Focused => _focused;

        public void Focus() => Box.SetFocus();

        /// <summary>Drops the keyboard focus (back to the game).</summary>
        public void Blur()
        {
            if (Box.TextBox.Focused) Program.Form.ActiveControl = null;
        }

        protected virtual void OnTextChanged() { }
        protected virtual void OnSubmitted() { }

        private void DropFocusUnlessAuto()
        {
            if (!AutoFocus && Box.TextBox.Focused) Program.Form.ActiveControl = null;
            else if (AutoFocus && !Box.IsDisposed && Box.Visible && Box.TextBox.Visible && !Box.TextBox.Focused) Box.SetFocus();
            FocusChanged();
        }

        private void FocusChanged()
        {
            bool f = !Box.IsDisposed && Box.TextBox.Focused;
            UpdatePlaceholder();
            if (f == _focused) return;
            _focused = f;
            Invalidate();
        }

        private void UpdatePlaceholder()
        {
            if (_placeholder.IsDisposed || Box.IsDisposed) return;
            bool show = !string.IsNullOrEmpty(_placeholder.Text) && string.IsNullOrEmpty(Box.TextBox.Text) && !Box.TextBox.Focused;
            _placeholder.Visible = show; // the setter early-outs; the getter is effective visibility (false while a parent is hidden)
        }

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && e.Button == MouseButtons.Left) Box.SetFocus();
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * 0.8f * k))
            {
                using (var b = new SolidBrush(ModernTheme.Field))
                    g.FillPath(b, p);
                // inset shadow (box-shadow: inset 0 1px 3px #000)
                using (var pen = new Pen(Color.FromArgb(160, 0, 0, 0), Math.Max(1f, 2f * k)))
                    g.DrawLine(pen, r.X + 3 * k, r.Y + 1.5f * k, r.Right - 3 * k, r.Y + 1.5f * k);
                if (_focused)
                {
                    Region old = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Accent, 0.3f * Math.Min(1f, ModernTheme.Glow)), Math.Max(2f, 5f * k));
                    g.Clip = old;
                    old.Dispose();
                }
                ModernSkin.Stroke(g, p, _focused ? ModernTheme.Accent : Hovered ? ModernTheme.Mix(ModernTheme.Frame, ModernTheme.Accent, 0.5f) : ModernTheme.Frame);
            }
            PaintExtras(g);
        }

        /// <summary>Glyphs in the padding (SearchBox's magnifier and clear cross).</summary>
        protected virtual void PaintExtras(Graphics g) { }
    }

    /// <summary>
    /// A search box (widgets.ts searchBox, .ui-search): a magnifier, the field, a clear cross while it has text. Calls
    /// <see cref="Search"/> with the trimmed text at most every <see cref="DebounceMs"/> while typing (CMain.Time, ModernTick),
    /// and at once on Enter or clear.
    /// </summary>
    public sealed class SearchBox : ModernTextField, IModernTick
    {
        private long _due = -1;
        private bool _clearHot;

        public SearchBox(int widthHud, string placeholder = null) : base(widthHud, 24, 12, 24, 20)
        {
            Placeholder = placeholder ?? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernSearch);
        }

        public event Action<string> Search;

        public int DebounceMs { get; set; } = 100;

        public string Query => Text.Trim();

        protected override void OnTextChanged()
        {
            _due = CMain.Time + DebounceMs;
            ModernTick.Add(this);
            Invalidate(); // the clear cross
        }

        protected override void OnSubmitted() => Fire();

        private void Fire()
        {
            _due = -1;
            ModernTick.Remove(this);
            Search?.Invoke(Query);
        }

        /// <summary>Sets the text without waiting for the debounce (and runs the search now).</summary>
        public void SetQuery(string text)
        {
            Text = text;
            Fire();
        }

        void IModernTick.Tick()
        {
            if (IsDisposed)
            {
                ModernTick.Remove(this);
                return;
            }
            if (_due >= 0 && CMain.Time >= _due) Fire();
        }

        private bool OverClear => Text.Length > 0 && CMain.MPoint.X - DisplayLocation.X >= Size.Width - S(20);

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hot = OverClear;
            if (hot == _clearHot) return;
            _clearHot = hot;
            Invalidate();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || !OverClear) return;
            Text = string.Empty;
            Fire();
        }

        protected override void PaintExtras(Graphics g)
        {
            float k = K, cy = Size.Height / 2f;
            using (var pen = new Pen(ModernTheme.Dim, Math.Max(1.2f, 1.6f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float rr = 4f * k, cx = 11f * k;
                g.DrawEllipse(pen, cx - rr, cy - rr - 1 * k, rr * 2, rr * 2);
                g.DrawLine(pen, cx + rr * 0.72f, cy + rr * 0.72f - 1 * k, cx + rr * 1.6f, cy + rr * 1.6f - 1 * k);
            }
            if (Text.Length == 0) return;
            using (var pen = new Pen(_clearHot && Hovered ? Color.White : ModernTheme.Dim, Math.Max(1.2f, 1.5f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = Size.Width - 10f * k, s = 3.5f * k;
                g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTick.Remove(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>A plain container for grouping widgets (tab pages, rows): draws nothing itself, takes the mouse over its area.</summary>
    public class ModernPanel : MirControl
    {
        public ModernPanel()
        {
            BackColour = Color.Transparent;
        }
    }

    /// <summary>A labelled row (widgets.ts row, .ui-row): a dim upper-case label on the left, the controls right-aligned.</summary>
    public static class ModernRow
    {
        /// <summary>Creates the row at (x, y) HUD px in <paramref name="parent"/>; the controls are re-parented into it.</summary>
        public static ModernPanel Create(MirControl parent, int xHud, int yHud, int widthHud, string label, params MirControl[] controls)
        {
            int h = ModernRoot.S(26), gap = ModernRoot.S(8);
            var row = new ModernPanel
            {
                Parent = parent,
                Location = new Point(ModernRoot.S(xHud), ModernRoot.S(yHud)),
                Size = new Size(ModernRoot.S(widthHud), h),
            };
            int right = row.Size.Width;
            for (int i = controls.Length - 1; i >= 0; i--)
            {
                MirControl c = controls[i];
                if (c == null) continue;
                c.Parent = row;
                right -= c.Size.Width;
                c.Location = new Point(right, Math.Max(0, (h - c.Size.Height) / 2));
                right -= gap;
            }
            new ModernLabel
            {
                Parent = row,
                Location = new Point(0, 0),
                Size = new Size(Math.Max(1, right), h),
                Text = (label ?? string.Empty).ToUpperInvariant(),
                FontSize = 10,
                Bold = true,
                Colour = ModernTheme.Dim,
            };
            return row;
        }
    }
}
