using System.Drawing.Drawing2D;
using Client.MirSounds;

namespace Client.MirScenes.Modern.Core.Widgets
{
    /// <summary>One option of a <see cref="Segmented{T}"/> or <see cref="Chips{T}"/> (widgets.ts Choice).</summary>
    public readonly struct Choice<T>
    {
        public readonly T Value;
        public readonly string Label;
        /// <summary>A small dim suffix (segmented only), e.g. a count.</summary>
        public readonly string Note;
        /// <summary>Hover hint.</summary>
        public readonly string Hint;

        public Choice(T value, string label, string note = null, string hint = null)
        {
            Value = value;
            Label = label ?? string.Empty;
            Note = note;
            Hint = hint;
        }
    }

    /// <summary>A widget made of hit regions (segments, chips, tabs): tracks the hovered region and repaints when it changes.</summary>
    public abstract class RegionWidget : ModernControl
    {
        private int _hot = -1;

        /// <summary>The hovered region, or -1.</summary>
        protected int Hot => Hovered ? _hot : -1;

        /// <summary>The region index under a point local to the widget (device px), or -1.</summary>
        protected abstract int RegionAt(Point local);

        /// <summary>The region under a local point (device px), or -1 (tests, hosts that place things over regions).</summary>
        public int HitTest(Point local) => RegionAt(local);

        /// <summary>The hint of a region (null = none).</summary>
        protected virtual string RegionHint(int index) => null;

        /// <summary>A region was clicked.</summary>
        protected abstract void RegionClicked(int index);

        private Point Local => new Point(CMain.MPoint.X - DisplayLocation.X, CMain.MPoint.Y - DisplayLocation.Y);

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateHot();
        }

        protected override void OnMouseEnter()
        {
            UpdateHot();
            base.OnMouseEnter();
        }

        private void UpdateHot()
        {
            int hot = RegionAt(Local);
            if (hot == _hot) return;
            _hot = hot;
            Hint = hot >= 0 ? RegionHint(hot) : null;
            Invalidate();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled || e.Button != MouseButtons.Left) return;
            int i = RegionAt(Local);
            if (i < 0) return;
            if (Sound != SoundList.None) SoundManager.PlaySound(Sound);
            RegionClicked(i);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);
    }

    /// <summary>
    /// A segmented control: one choice of several, as a joined row of buttons (widgets.ts segmented, .ui-seg). <see cref="Set"/>
    /// changes the selection without raising <see cref="Changed"/>.
    /// </summary>
    public sealed class Segmented<T> : RegionWidget
    {
        private readonly Choice<T>[] _choices;
        private int _index;

        public Segmented(IList<Choice<T>> choices, T value, int widthHud)
        {
            _choices = new Choice<T>[choices.Count];
            choices.CopyTo(_choices, 0);
            _index = IndexOf(value);
            Sound = SoundList.ButtonA;
            Size = new Size(S(widthHud), S(24));
        }

        public event Action<T> Changed;

        public T Value => _index >= 0 ? _choices[_index].Value : default;

        public void Set(T value)
        {
            int i = IndexOf(value);
            if (i == _index) return;
            _index = i;
            Invalidate();
        }

        private int IndexOf(T value)
        {
            for (int i = 0; i < _choices.Length; i++)
                if (EqualityComparer<T>.Default.Equals(_choices[i].Value, value)) return i;
            return -1;
        }

        private float SegWidth => _choices.Length == 0 ? 0 : (Size.Width - 1f) / _choices.Length;

        protected override int RegionAt(Point local)
        {
            if (_choices.Length == 0 || local.Y < 0 || local.Y >= Size.Height || local.X < 0) return -1;
            int i = (int)(local.X / SegWidth);
            return i >= 0 && i < _choices.Length ? i : -1;
        }

        protected override string RegionHint(int index) => _choices[index].Hint;

        protected override void RegionClicked(int index)
        {
            if (index == _index) return;
            _index = index;
            Invalidate();
            Changed?.Invoke(_choices[index].Value);
        }

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * 0.8f * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            float w = SegWidth;
            using GraphicsPath path = ModernSkin.RoundRect(outer, rad);
            using (var bg = new SolidBrush(ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.6f)))
                g.FillPath(bg, path);

            Region old = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            for (int i = 0; i < _choices.Length; i++)
            {
                RectangleF r = new RectangleF(outer.X + i * w, outer.Y, w, outer.Height);
                bool on = i == _index, hot = i == Hot;
                if (on)
                {
                    using (var b = new SolidBrush(ModernTheme.Mix(ModernTheme.Accent, ModernTheme.PanelB, 0.24f)))
                        g.FillRectangle(b, r);
                    using (var b = new SolidBrush(ModernTheme.Accent))
                        g.FillRectangle(b, r.X, r.Bottom - 2 * k, r.Width, 2 * k);
                }
                if (i > 0)
                    using (var pen = new Pen(ModernTheme.Frame, 1f))
                        g.DrawLine(pen, r.X, r.Y, r.X, r.Bottom);

                Choice<T> c = _choices[i];
                Color tc = on || hot ? Color.White : ModernTheme.Dim;
                Font f = ModernSkin.Font(11, true);
                if (string.IsNullOrEmpty(c.Note))
                {
                    ModernSkin.Text(g, c.Label, f, tc, RectangleF.Inflate(r, -4 * k, 0), StringAlignment.Center);
                }
                else
                {
                    Font nf = ModernSkin.Font(10);
                    float lw = ModernSkin.Measure(c.Label, f), nw = ModernSkin.Measure(c.Note, nf), gap = 4 * k;
                    float x = r.X + Math.Max(4 * k, (r.Width - lw - gap - nw) / 2f);
                    ModernSkin.Text(g, c.Label, f, tc, new RectangleF(x, r.Y, lw + 2, r.Height));
                    ModernSkin.Text(g, c.Note, nf, ModernTheme.Alpha(tc, 0.7f), new RectangleF(x + lw + gap, r.Y, nw + 2, r.Height));
                }
            }
            g.Clip = old;
            old.Dispose();
            ModernSkin.Stroke(g, path, ModernTheme.Frame);
        }
    }

    /// <summary>
    /// Chips: toggles in a wrapping row (widgets.ts chips, .ui-chips). Multi (default) lets several be on; single mode
    /// replaces the selection and allows none only with <paramref name="allowNone"/>. The height follows the rows.
    /// </summary>
    public sealed class Chips<T> : RegionWidget
    {
        private readonly Choice<T>[] _choices;
        private readonly RectangleF[] _rects;
        private readonly HashSet<T> _selected = new HashSet<T>();
        private readonly bool _multi, _allowNone;

        public Chips(IList<Choice<T>> choices, IEnumerable<T> selected, int widthHud, bool multi = true, bool allowNone = false)
        {
            _choices = new Choice<T>[choices.Count];
            choices.CopyTo(_choices, 0);
            _multi = multi;
            _allowNone = allowNone;
            if (selected != null)
                foreach (T v in selected) _selected.Add(v);
            Sound = SoundList.ButtonA;

            // wrap layout (device px): chips 20 high, 9 padding, 4 gap
            _rects = new RectangleF[_choices.Length];
            float width = S(widthHud), x = 0, y = 0, h = S(20), gap = S(4);
            Font f = ModernSkin.Font(11);
            for (int i = 0; i < _choices.Length; i++)
            {
                float w = (float)Math.Ceiling(ModernSkin.Measure(_choices[i].Label, f)) + S(18) + 2;
                if (x > 0 && x + w > width)
                {
                    x = 0;
                    y += h + gap;
                }
                _rects[i] = new RectangleF(x, y, w, h);
                x += w + gap;
            }
            Size = new Size((int)width, (int)Math.Ceiling(y + h) + 1);
        }

        /// <summary>The new selection (a copy).</summary>
        public event Action<HashSet<T>> Changed;

        public IReadOnlyCollection<T> Selected => _selected;

        public bool IsOn(T value) => _selected.Contains(value);

        public void Set(IEnumerable<T> selection)
        {
            _selected.Clear();
            if (selection != null)
                foreach (T v in selection) _selected.Add(v);
            Invalidate();
        }

        protected override int RegionAt(Point local)
        {
            for (int i = 0; i < _rects.Length; i++)
                if (_rects[i].Contains(local)) return i;
            return -1;
        }

        protected override string RegionHint(int index) => _choices[index].Hint;

        protected override void RegionClicked(int index)
        {
            T v = _choices[index].Value;
            if (_selected.Contains(v))
            {
                if (!_multi && !_allowNone) return;
                _selected.Remove(v);
            }
            else
            {
                if (!_multi) _selected.Clear();
                _selected.Add(v);
            }
            Invalidate();
            Changed?.Invoke(new HashSet<T>(_selected));
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            Font f = ModernSkin.Font(11);
            for (int i = 0; i < _choices.Length; i++)
            {
                RectangleF r = _rects[i];
                r = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
                bool on = _selected.Contains(_choices[i].Value), hot = i == Hot;
                using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
                using (var b = new SolidBrush(on ? ModernTheme.Mix(ModernTheme.Accent, ModernTheme.PanelB, 0.28f) : ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.7f)))
                    g.FillPath(b, p);
                ModernSkin.Stroke(g, p, on || hot ? ModernTheme.Accent : ModernTheme.Frame);
                ModernSkin.Text(g, _choices[i].Label, f, on || hot ? Color.White : ModernTheme.Dim, r, StringAlignment.Center);
            }
        }
    }

    /// <summary>A toggle switch (widgets.ts toggleSwitch, .ui-tog). <see cref="Lock"/> shows it but refuses clicks (hint = why).
    /// <see cref="Set"/> does not raise <see cref="Changed"/>.</summary>
    public sealed class ToggleSwitch : ModernControl
    {
        private bool _on, _locked;
        private readonly bool _small;
        private string _hint = string.Empty;

        public ToggleSwitch(bool on, bool small = false)
        {
            _on = on;
            _small = small;
            Sound = SoundList.ButtonA;
            Size = small ? new Size(S(30), S(16)) : new Size(S(38), S(20));
        }

        public event Action<bool> Changed;

        public bool On => _on;
        public bool Locked => _locked;

        public string Tooltip
        {
            get => _hint;
            set
            {
                _hint = value ?? string.Empty;
                if (!_locked) Hint = _hint;
            }
        }

        public void Set(bool on)
        {
            if (_on == on) return;
            _on = on;
            Invalidate();
        }

        public void Lock(bool locked, string why = null)
        {
            Hint = locked && !string.IsNullOrEmpty(why) ? why : _hint;
            if (_locked == locked) return;
            _locked = locked;
            Invalidate();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (_locked || !Enabled || e.Button != MouseButtons.Left) return;
            base.OnMouseClick(e);
            Set(!_on);
            Changed?.Invoke(_on);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K, a = _locked ? 0.6f : 1f;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f))
            {
                Color bg = _on ? ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f) : ModernTheme.Mix(ModernTheme.Frame, Color.Black, 0.6f);
                using (var b = new SolidBrush(Fade(bg, a)))
                    g.FillPath(b, p);
                Color border = _on ? ModernTheme.Accent : ModernTheme.Frame;
                if (Hovered && !_locked) border = ModernTheme.Mix(border, Color.White, 0.75f);
                ModernSkin.Stroke(g, p, Fade(border, a));
            }
            float d = (_small ? 10 : 14) * k, pad = (r.Height - d) / 2f;
            float x = _on ? r.Right - pad - d : r.X + pad;
            RectangleF knob = new RectangleF(x, r.Y + pad, d, d);
            if (_on)
            {
                for (int i = 3; i >= 1; i--)
                    using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.12f * a)))
                        g.FillEllipse(b, RectangleF.Inflate(knob, i * k, i * k));
            }
            using (var b = new SolidBrush(Fade(_on ? Color.White : ModernTheme.Dim, a)))
                g.FillEllipse(b, knob);
        }

        private static Color Fade(Color c, float a) => ModernSkin.Dim(c, a);
    }

    /// <summary>Tabs over a row of names with badge counts (widgets.ts tabs, .ui-tabs). <see cref="Select"/> does not raise
    /// <see cref="Changed"/>. The tab row spans the widget's width with the frame line under it.</summary>
    public sealed class Tabs : RegionWidget
    {
        private readonly string[] _names;
        private readonly int[] _badges;
        private readonly RectangleF[] _rects;
        private int _active;

        public Tabs(IList<string> names, int active, int widthHud)
        {
            _names = new string[names.Count];
            names.CopyTo(_names, 0);
            _badges = new int[_names.Length];
            _rects = new RectangleF[_names.Length];
            _active = active;
            Sound = SoundList.ButtonA;
            Size = new Size(S(widthHud), S(26));
            Measure();
        }

        public event Action<int> Changed;

        public int Active => _active;

        public void Select(int i)
        {
            if (i == _active || i < 0 || i >= _names.Length) return;
            _active = i;
            Invalidate();
        }

        /// <summary>Shows a count on a tab (0 hides it; over 99 shows 99+).</summary>
        public void Badge(int i, int n)
        {
            if (i < 0 || i >= _badges.Length || _badges[i] == n) return;
            _badges[i] = n;
            Measure();
            Invalidate();
        }

        public void Rename(int i, string name)
        {
            if (i < 0 || i >= _names.Length || _names[i] == name) return;
            _names[i] = name ?? string.Empty;
            Measure();
            Invalidate();
        }

        private static string BadgeText(int n) => n > 99 ? "99+" : n.ToString();

        /// <summary>A tab's area relative to the widget (device px; tests click it).</summary>
        public Rectangle TabBounds(int i) => i >= 0 && i < _rects.Length ? Rectangle.Round(_rects[i]) : Rectangle.Empty;

        private void Measure()
        {
            float x = 0, gap = S(2);
            Font f = ModernSkin.Font(11, true), bf = ModernSkin.Font(9, true);
            for (int i = 0; i < _names.Length; i++)
            {
                float w = (float)Math.Ceiling(ModernSkin.Measure(_names[i], f)) + S(24);
                if (_badges[i] > 0) w += ModernSkin.Measure(BadgeText(_badges[i]), bf) + S(15);
                _rects[i] = new RectangleF(x, 0, w, Size.Height - 1);
                x += w + gap;
            }
        }

        protected override int RegionAt(Point local)
        {
            for (int i = 0; i < _rects.Length; i++)
                if (_rects[i].Contains(local)) return i;
            return -1;
        }

        protected override void RegionClicked(int index)
        {
            if (index == _active) return;
            _active = index;
            Invalidate();
            Changed?.Invoke(index);
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            using (var pen = new Pen(ModernTheme.Frame, 1f))
                g.DrawLine(pen, 0, Size.Height - 0.5f, Size.Width, Size.Height - 0.5f);
            Font f = ModernSkin.Font(11, true), bf = ModernSkin.Font(9, true);
            for (int i = 0; i < _names.Length; i++)
            {
                RectangleF r = _rects[i];
                bool on = i == _active, hot = i == Hot;
                Color c = on ? ModernTheme.Accent : hot ? Color.White : ModernTheme.Dim;
                float tw = ModernSkin.Measure(_names[i], f);
                float bw = _badges[i] > 0 ? ModernSkin.Measure(BadgeText(_badges[i]), bf) + S(10) : 0;
                float x = r.X + S(12);
                ModernSkin.Text(g, _names[i], f, c, new RectangleF(x, r.Y, tw + 2, r.Height), StringAlignment.Near, StringAlignment.Center, false);
                if (bw > 0)
                {
                    RectangleF br = new RectangleF(x + tw + S(5), r.Y + (r.Height - S(14)) / 2f, bw, S(14));
                    using (GraphicsPath p = ModernSkin.RoundRect(br, br.Height / 2f))
                    using (var b = new SolidBrush(ModernTheme.Bad))
                        g.FillPath(b, p);
                    ModernSkin.Text(g, BadgeText(_badges[i]), bf, Color.White, br, StringAlignment.Center, StringAlignment.Center, false);
                }
                if (on)
                {
                    RectangleF line = new RectangleF(r.X + 6 * k, Size.Height - 2 * k, r.Width - 12 * k, 2 * k);
                    using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.35f)))
                        g.FillRectangle(b, RectangleF.Inflate(line, 1 * k, 1 * k));
                    using (var b = new SolidBrush(ModernTheme.Accent))
                        g.FillRectangle(b, line);
                }
            }
        }
    }
}
