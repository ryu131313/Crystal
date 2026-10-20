using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;

namespace Client.MirScenes.Modern.Core.Widgets
{
    /// <summary>
    /// A scrolling viewport (Desktop-Modern-UI-Plan D3): its own render target the size of the viewport, holding a
    /// <see cref="Content"/> panel offset by the scroll value. Children of Content are laid out in content coordinates (device px)
    /// and draw relative to the target while it renders (the ModernWindow DisplayLocation trick); outside rendering their
    /// DisplayLocation includes the scroll, so hit-testing is plain MirControl — and Content takes the mouse only inside the
    /// viewport, so children scrolled out of view never get it. Wheel (also bubbled up from widgets inside, ModernControl), and a
    /// thin accent scrollbar you can drag. The target is re-rendered only when something inside changed or the view scrolled.
    /// <para>The target is cleared to <see cref="Background"/> (opaque by default: a darker well), so semi-transparent widgets
    /// inside composite correctly; Color.Transparent works too.</para>
    /// </summary>
    public class ScrollView : MirControl
    {
        public readonly ScrollContent Content;
        private readonly ScrollThumb _thumb;
        private readonly ScrollFrame _frame;
        private bool _rendering;
        private int _scroll, _contentHeight;
        private Color _background;

        public ScrollView(int widthHud, int heightHud)
        {
            DrawControlTexture = true;
            BackColour = Color.Transparent;
            Color well = ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.55f);
            _background = Color.FromArgb(255, well.R, well.G, well.B);
            Size = new Size(ModernRoot.S(widthHud), ModernRoot.S(heightHud));
            Content = new ScrollContent(this) { Parent = this, Location = Point.Empty, Size = new Size(Size.Width, Size.Height) };
            _frame = new ScrollFrame { Parent = this, Location = Point.Empty, Size = Size };
            _thumb = new ScrollThumb(this) { Parent = this };
            WheelStep = ModernRoot.S(40);
            UpdateThumb();
        }

        /// <summary>Raised after the scroll value changed (ListView rebinds its rows).</summary>
        public event Action Scrolled;

        /// <summary>The target's clear colour.</summary>
        public Color Background
        {
            get => _background;
            set
            {
                if (_background == value) return;
                _background = value;
                Redraw();
            }
        }

        /// <summary>Draw the thin frame around the viewport.</summary>
        public bool Framed
        {
            get => _frame.Visible;
            set => _frame.Visible = value;
        }

        /// <summary>Device px per wheel notch.</summary>
        public int WheelStep { get; set; }

        /// <summary>Width (device px) children should use so they do not sit under the scrollbar.</summary>
        public int InnerWidth => Size.Width - ModernRoot.S(10);

        public int ScrollY => _scroll;
        public int MaxScroll => Math.Max(0, _contentHeight - Size.Height);

        /// <summary>The content's height (device px). Set it after laying out the children, or call <see cref="FitContent"/>.</summary>
        public int ContentHeight
        {
            get => _contentHeight;
            set
            {
                value = Math.Max(0, value);
                if (_contentHeight == value) return;
                _contentHeight = value;
                Content.Size = new Size(Size.Width, Math.Max(Size.Height, value));
                ScrollTo(_scroll);
                UpdateThumb();
            }
        }

        /// <summary>Content height = the lowest child bottom (+ a little padding).</summary>
        public void FitContent(int padHud = 4)
        {
            int bottom = 0;
            for (int i = 0; i < Content.Controls.Count; i++)
            {
                MirControl c = Content.Controls[i];
                bottom = Math.Max(bottom, c.Location.Y + c.Size.Height);
            }
            ContentHeight = bottom + ModernRoot.S(padHud);
        }

        public void ScrollTo(int y)
        {
            y = Math.Clamp(y, 0, MaxScroll);
            if (y == _scroll) return;
            _scroll = y;
            Content.Location = new Point(0, -_scroll);
            UpdateThumb();
            Redraw();
            Scrolled?.Invoke();
        }

        public void ScrollBy(int dy) => ScrollTo(_scroll + dy);

        /// <summary>Scrolls the least needed to show the content band [top, top + height).</summary>
        public void EnsureVisible(int top, int height)
        {
            if (top < _scroll) ScrollTo(top);
            else if (top + height > _scroll + Size.Height) ScrollTo(top + height - Size.Height);
        }

        /// <summary>From ModernControl.OnMouseWheel: the nearest scroll view around <paramref name="from"/> scrolls.</summary>
        public static void Bubble(MirControl from, MouseEventArgs e)
        {
            for (MirControl p = from.Parent; p != null; p = p.Parent)
            {
                if (p is ScrollView sv)
                {
                    sv.Wheel(e);
                    return;
                }
            }
        }

        private void Wheel(MouseEventArgs e)
        {
            if (!Enabled || MaxScroll == 0) return;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            ScrollBy(-notches * WheelStep);
        }

        public override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Wheel(e);
        }

        private void UpdateThumb()
        {
            if (_thumb == null) return;
            int max = MaxScroll;
            _thumb.Visible = max > 0;
            if (max == 0) return;
            int h = Size.Height, th = Math.Max(ModernRoot.S(18), (int)((long)h * h / Math.Max(1, _contentHeight)));
            int y = (int)((long)(h - th) * _scroll / max);
            _thumb.Size = new Size(ModernRoot.S(6), th);
            _thumb.Location = new Point(Size.Width - ModernRoot.S(8), y);
        }

        /// <summary>The scrollbar thumb relative to the view (device px; empty when nothing scrolls).</summary>
        public Rectangle ThumbBounds => _thumb != null && MaxScroll > 0 ? new Rectangle(_thumb.Location, _thumb.Size) : Rectangle.Empty;

        /// <summary>Thumb drag: scroll to follow a thumb top (viewport px).</summary>
        internal void DragThumbTo(int thumbTop)
        {
            int range = Size.Height - _thumb.Size.Height;
            if (range <= 0) return;
            ScrollTo((int)((long)MaxScroll * Math.Clamp(thumbTop, 0, range) / range));
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();
            if (Content == null) return;
            Content.Size = new Size(Size.Width, Math.Max(Size.Height, _contentHeight));
            _frame.Size = Size;
            ScrollTo(_scroll);
            UpdateThumb();
        }

        // ---- rendering (ModernWindow pattern) ----

        /// <summary>While rendering into the target the content draws relative to it.</summary>
        public override Point DisplayLocation => _rendering ? Point.Empty : base.DisplayLocation;

        public override void Redraw()
        {
            TextureValid = false;
            Parent?.Redraw();
        }

        public override void Draw()
        {
            if (IsDisposed || !Visible || Size.Width <= 0 || Size.Height <= 0) return;
            BeforeDrawControl();
            DrawControl();
            AfterDrawControl();
        }

        protected override void CreateTexture()
        {
            if (Size != TextureSize) DisposeTexture();
            if (ControlTexture == null || ControlTexture.Disposed)
            {
                DXManager.ControlList.Add(this);
                ControlTexture = DXManager.CreateRenderTarget(Size.Width, Size.Height);
                TextureSize = Size;
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(ControlTexture);
            DXManager.RestartSpriteAlphaBlend(); // the sprite projection follows the target only at Begin (see ModernWindow)
            Color bg = _background; // premultiplied (ModernSurface.Composite)
            DXManager.Clear(Color.FromArgb(bg.A, bg.R * bg.A / 255, bg.G * bg.A / 255, bg.B * bg.A / 255));
            bool oldOver = DXManager.AlphaOver;
            DXManager.SetAlphaOver(true);
            _rendering = true;
            try
            {
                DrawChildControls();
            }
            finally
            {
                _rendering = false;
                DXManager.Flush();
                DXManager.SetAlphaOver(oldOver);
                DXManager.SetSurface(oldSurface);
                DXManager.RestartSpriteAlphaBlend();
            }
            TextureValid = true;
        }

        /// <summary>Premultiplied, like every new-UI target (ModernSurface.Composite).</summary>
        protected internal override void DrawControl()
        {
            if (!TextureValid) CreateTexture();
            if (ControlTexture == null || ControlTexture.Disposed) return;
            ModernSurface.Composite(ControlTexture, Size, DisplayLocation, Opacity);
            CleanTime = CMain.Time + Settings.CleanDelay;
        }

        public override bool IsMouseOver(Point p) => Visible && !IsDisposed && DisplayRectangle.Contains(p);

        /// <summary>The scrolled panel: children in content coordinates; takes the mouse only inside the viewport.</summary>
        public sealed class ScrollContent : MirControl
        {
            private readonly ScrollView _view;

            internal ScrollContent(ScrollView view)
            {
                _view = view;
                BackColour = Color.Transparent;
            }

            public override bool IsMouseOver(Point p) => Visible && !IsDisposed && _view.DisplayRectangle.Contains(p);

            /// <summary>Taller than the screen is fine: only the children draw (MirControl.Draw skips over-size controls).</summary>
            public override void Draw()
            {
                if (IsDisposed || !Visible) return;
                DrawChildControls();
            }

            public override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                _view.Wheel(e);
            }
        }

        /// <summary>The scrollbar thumb (scrollbar-thumb: frame→accent, accent on hover / drag).</summary>
        private sealed class ScrollThumb : ModernControl
        {
            private readonly ScrollView _view;
            private bool _drag;
            private int _grab;

            public ScrollThumb(ScrollView view)
            {
                _view = view;
            }

            public override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                _drag = true;
                Moving = true;
                _grab = CMain.MPoint.Y - DisplayLocation.Y;
            }

            public override void OnMouseMove(MouseEventArgs e)
            {
                if (_drag)
                {
                    _view.DragThumbTo(CMain.MPoint.Y - _view.DisplayLocation.Y - _grab);
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

            public override bool IsMouseOver(Point p) => Visible && !IsDisposed && (DisplayRectangle.Contains(p) || _drag);

            protected override bool OnWheel(MouseEventArgs e)
            {
                _view.Wheel(e);
                return true;
            }

            protected override void Paint(Graphics g)
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, r.Width / 2f);
                Color c = Hovered || _drag ? ModernTheme.Accent : ModernTheme.Mix(ModernTheme.Frame, ModernTheme.Accent, 0.8f);
                using (var b = new SolidBrush(c))
                    g.FillPath(b, p);
                ModernSkin.Stroke(g, p, Color.Black);
            }
        }

        /// <summary>The viewport's 1px frame (drawn over the content).</summary>
        private sealed class ScrollFrame : ModernControl
        {
            public ScrollFrame()
            {
                NotControl = true;
            }

            protected override void Paint(Graphics g)
            {
                using var pen = new Pen(ModernTheme.Alpha(ModernTheme.Frame, 0.8f), 1f);
                g.DrawRectangle(pen, 0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            }
        }
    }

    /// <summary>One recycled row of a <see cref="ListView{T}"/>: shows whatever item it is bound to.</summary>
    public abstract class ListRow<T> : ModernControl
    {
        internal ListView<T> Owner;

        public T Item { get; private set; }
        public int Index { get; private set; } = -1;
        public bool Selected { get; private set; }

        /// <summary>Binds the row; repaints only when the item, index or selection changed (override <see cref="SameItem"/> for
        /// value comparisons).</summary>
        internal void Bind(T item, int index, bool selected)
        {
            if (index == Index && selected == Selected && SameItem(Item, item)) return;
            Item = item;
            Index = index;
            Selected = selected;
            OnBind();
            Invalidate();
        }

        protected virtual bool SameItem(T a, T b) => EqualityComparer<T>.Default.Equals(a, b);

        /// <summary>The item changed (cache derived text here, not in Paint).</summary>
        protected virtual void OnBind() { }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (Index >= 0) Owner?.RowClicked(this, e);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Index >= 0) Owner?.RowActivated(this);
        }

        /// <summary>The standard row background: hover and selection tints, a faint separator.</summary>
        protected void PaintRowBackground(Graphics g)
        {
            if (Selected)
            {
                using (var b = new SolidBrush(ModernTheme.Mix(ModernTheme.Accent, ModernTheme.PanelB, 0.22f)))
                    g.FillRectangle(b, 0, 0, Size.Width, Size.Height);
                using (var b = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(b, 0, 0, Math.Max(2, S(2)), Size.Height);
            }
            else if (Hovered)
            {
                using var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.1f));
                g.FillRectangle(b, 0, 0, Size.Width, Size.Height);
            }
            using var pen = new Pen(ModernTheme.Alpha(ModernTheme.Frame, 0.35f), 1f);
            g.DrawLine(pen, 0, Size.Height - 0.5f, Size.Width, Size.Height - 0.5f);
        }
    }

    /// <summary>A row showing one line of text for its item.</summary>
    public sealed class TextListRow<T> : ListRow<T>
    {
        private readonly Func<T, string> _text;
        private string _line = string.Empty;

        public TextListRow(Func<T, string> text)
        {
            _text = text;
        }

        protected override void OnBind() => _line = Index >= 0 ? _text?.Invoke(Item) ?? string.Empty : string.Empty;

        protected override void Paint(Graphics g)
        {
            PaintRowBackground(g);
            ModernSkin.Text(g, _line, ModernSkin.Font(12), Selected ? Color.White : ModernTheme.Text,
                new RectangleF(S(10), 0, Size.Width - S(14), Size.Height));
        }
    }

    /// <summary>
    /// A list on a <see cref="ScrollView"/>: rows from a data list with single selection, recycling a fixed set of row controls
    /// (one per visible row + 1) re-bound as it scrolls — never one control per item. Rows are fixed height (HUD px).
    /// <see cref="SetItems"/> replaces the data (keeps the selection by index when still in range).
    /// </summary>
    public sealed class ListView<T> : ScrollView
    {
        private readonly Func<ListRow<T>> _factory;
        private readonly List<ListRow<T>> _rows = new List<ListRow<T>>();
        private readonly int _rowHeight;
        private IReadOnlyList<T> _items = Array.Empty<T>();
        private int _selected = -1;
        private readonly ModernLabel _empty;

        public ListView(int widthHud, int heightHud, int rowHeightHud, Func<ListRow<T>> rowFactory) : base(widthHud, heightHud)
        {
            _factory = rowFactory;
            _rowHeight = Math.Max(1, ModernRoot.S(rowHeightHud));
            WheelStep = _rowHeight * 3;
            Scrolled += Rebind;
            _empty = new ModernLabel
            {
                Parent = this,
                Location = new Point(0, 0),
                Size = new Size(Size.Width, Math.Min(Size.Height, ModernRoot.S(56))),
                Text = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernNothingHere),
                Colour = ModernTheme.Dim,
                FontSize = 11,
                Align = StringAlignment.Center,
            };
            int count = Size.Height / _rowHeight + 2;
            for (int i = 0; i < count; i++)
            {
                ListRow<T> row = _factory();
                row.Owner = this;
                row.Parent = Content;
                row.Size = new Size(InnerWidth, _rowHeight);
                row.Visible = false;
                _rows.Add(row);
            }
            Rebind();
        }

        /// <summary>A row was clicked (selection already moved).</summary>
        public event Action<int, T> SelectionChanged;

        /// <summary>A row was double-clicked.</summary>
        public event Action<int, T> Activated;

        public IReadOnlyList<T> Items => _items;
        public int SelectedIndex => _selected;
        public T SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : default;
        public int RowHeight => _rowHeight;

        /// <summary>The empty-list note (default ModernNothingHere).</summary>
        public string EmptyText { get => _empty.Text; set => _empty.Text = value; }

        public void SetItems(IReadOnlyList<T> items)
        {
            _items = items ?? Array.Empty<T>();
            if (_selected >= _items.Count) _selected = -1;
            ContentHeight = _items.Count * _rowHeight;
            Rebind();
        }

        /// <summary>Selects a row from code (no event) and scrolls it into view; -1 clears.</summary>
        public void Select(int index)
        {
            if (index < -1 || index >= _items.Count) index = -1;
            _selected = index;
            if (index >= 0) EnsureVisible(index * _rowHeight, _rowHeight);
            Rebind();
        }

        /// <summary>Re-binds the visible rows (call after the items' contents changed in place).</summary>
        public void Refresh()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Invalidate();
            Rebind();
        }

        private void Rebind()
        {
            int first = ScrollY / _rowHeight;
            for (int i = 0; i < _rows.Count; i++)
            {
                ListRow<T> row = _rows[i];
                int index = first + i;
                if (index < _items.Count)
                {
                    row.Location = new Point(0, index * _rowHeight);
                    row.Bind(_items[index], index, index == _selected);
                    row.Visible = true; // the setter early-outs on its own flag (the getter is effective visibility)
                }
                else
                {
                    row.Visible = false;
                }
            }
            bool empty = _items.Count == 0;
            _empty.Visible = empty;
        }

        internal void RowClicked(ListRow<T> row, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || row.Index < 0 || row.Index >= _items.Count) return;
            if (_selected != row.Index)
            {
                _selected = row.Index;
                Rebind();
            }
            SelectionChanged?.Invoke(_selected, _items[_selected]);
        }

        internal void RowActivated(ListRow<T> row)
        {
            if (row.Index < 0 || row.Index >= _items.Count) return;
            Activated?.Invoke(row.Index, _items[row.Index]);
        }
    }
}
