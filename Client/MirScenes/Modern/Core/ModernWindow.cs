using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// A new-UI window (port of core/wm.ts UiWindow): panel chrome, a title bar with the accent line and the close cross, drag by
    /// the title, front highlight, position memory (HUD px, Settings.UiWindows). The window and everything in it render into one
    /// render target, rebuilt only when something inside changed (<see cref="Redraw"/> stops here instead of climbing to the
    /// scene), then blitted each frame by ModernRoot.DrawOverlay.
    /// <para>
    /// Subclasses lay out their content in <see cref="Build"/> in HUD px (use <see cref="S"/>); Build runs again after a HUD
    /// scale change, so it must create everything from scratch. <see cref="Shown"/> / <see cref="Closed"/> run on open / close;
    /// subscribe to models in Shown and unsubscribe in Closed (a closed window costs nothing).
    /// </para>
    /// </summary>
    public abstract class ModernWindow : ModernSurface
    {
        public const int TitleHeight = 30;

        /// <summary>Position key (Settings.UiWindows) and catalog key.</summary>
        public readonly string Key;
        public string Title { get; private set; }

        /// <summary>Content size in HUD px (below the title bar), set by the subclass before / in Build.</summary>
        protected int ContentWidth = 300, ContentHeight = 200;

        /// <summary>Position in HUD px (x = -1: dock to the right edge on first show).</summary>
        internal int HudX = -1, HudY = 80;
        private readonly int _startX, _startY;

        private bool _built;
        private ModernWindowChrome _chrome;
        private ModernCloseButton _close;
        private bool _front;

        protected ModernWindow(string key, string title, int x = -1, int y = 80)
        {
            Key = key;
            Title = title ?? string.Empty;
            _startX = HudX = x;
            _startY = HudY = y;
            Movable = true;
            Sort = true;
            Visible = false;
            Opacity = float.IsFinite(Settings.UiOpacity) ? Settings.UiOpacity : 1F;

        }

        public bool IsOpen => Visible && !IsDisposed;

        /// <summary>The front open window gets the accent border (WindowManager sets it).</summary>
        internal bool Front
        {
            get => _front;
            set
            {
                if (_front == value) return;
                _front = value;
                _chrome?.Invalidate();
            }
        }

        public void SetTitle(string title)
        {
            title ??= string.Empty;
            if (Title == title) return;
            Title = title;
            _chrome?.Invalidate();
        }

        // ---- hooks ----

        /// <summary>Creates the content (HUD px through <see cref="S"/>), below the title bar: y starts at <see cref="ContentTop"/>.</summary>
        protected abstract void Build();
        protected virtual void Shown() { }
        protected virtual void Closed() { }
        /// <summary>Keys while this is the front window; true = consumed.</summary>
        public virtual bool HandleKey(KeyEventArgs e) => false;
        /// <summary>Per-frame work while open (ModernUi.Process); keep it cheap and change nothing unless a value changed.
        /// Overrides must call base.Process(): it runs the widgets' timers (confirm-twice, hold, search debounce: ModernTick).</summary>
        public override void Process() => Widgets.ModernTick.Run();

        /// <summary>Top of the content area in device px.</summary>
        protected int ContentTop => S(TitleHeight);

        // ---- lifecycle ----

        /// <summary>(Re)builds chrome and content at the current scale.</summary>
        internal void Layout()
        {
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            Size = new Size(S(ContentWidth), S(ContentHeight + TitleHeight));
            _chrome = new ModernWindowChrome(this) { Parent = this, Size = Size };
            _close = new ModernCloseButton { Parent = this, Size = new Size(S(18), S(18)), Location = new Point(Size.Width - S(26), S(6)) };
            _close.Click += (o, e) => Close();
            Build();
            Size = new Size(S(ContentWidth), S(ContentHeight + TitleHeight)); // Build may change the content size
            _chrome.Size = Size;
            _close.Location = new Point(Size.Width - S(26), S(6));
            _built = true;
            Place();
            TextureValid = false;
        }

        public void Open()
        {
            if (IsDisposed) return;
            ModernRoot root = ModernRoot.Instance;
            if (root == null) return;
            if (Parent != root.Windows) Parent = root.Windows;
            if (!_built) Layout();
            bool was = Visible;
            Visible = true;
            BringToFront();
            Place();
            root.Wm.WindowChanged(this);
            if (!was) Shown();
        }

        public void Close()
        {
            if (!Visible) return;
            Visible = false;
            if (ActiveControl != null && IsChildOf(ActiveControl)) ActiveControl = null;
            Closed();
            ModernRoot.Instance?.Wm.WindowChanged(this);
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        private bool IsChildOf(MirControl c)
        {
            for (MirControl p = c; p != null; p = p.Parent)
                if (p == this) return true;
            return false;
        }

        /// <summary>Applies HUD x / y, clamped so the title stays on screen (wm.ts place).</summary>
        internal void Place()
        {
            int W = Settings.ScreenWidth, H = Settings.ScreenHeight;
            int x = HudX < 0 ? (int)((W - Size.Width) / ModernRoot.K) - 40 : HudX;
            int px = S(x), py = S(HudY);
            int keep = S(60);
            px = Math.Min(Math.Max(px, keep - Size.Width), W - keep);
            py = Math.Min(Math.Max(py, 0), H - S(TitleHeight));
            Location = new Point(px, py);
        }

        internal void ResetPosition()
        {
            HudX = _startX;
            HudY = _startY;
            if (IsOpen) Place();
            ModernRoot.Instance?.Wm.SavePosition(this);
        }

        private void RememberPosition()
        {
            HudX = (int)Math.Round(Location.X / ModernRoot.K);
            HudY = (int)Math.Round(Location.Y / ModernRoot.K);
            ModernRoot.Instance?.Wm.SavePosition(this);
        }

        // ---- rendering ----

        /// <summary>Drag by the title bar only (wm.ts: the title is the handle), then the base raises and starts the move.</summary>
        public override void OnMouseDown(MouseEventArgs e)
        {
            bool onTitle = CMain.MPoint.Y - DisplayLocation.Y < S(TitleHeight);
            base.OnMouseDown(e);
            if (!onTitle) Moving = false; // Movable stays on (no setter flip and redraw per press); only the title drags
            ModernRoot.Instance?.Wm.WindowChanged(this);
        }

        /// <summary>The drag ended: remember the position (once, not per mouse move).</summary>
        public override void OnMouseUp(MouseEventArgs e)
        {
            bool wasMoving = Moving;
            base.OnMouseUp(e);
            if (wasMoving) RememberPosition();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // a window open at logout never got Closed(): run it so its subscriptions (static ItemMoves.SlotChanged, models) end
                if (Visible)
                {
                    try
                    {
                        Closed();
                    }
                    catch (Exception ex)
                    {
                        ModernLog.Once("[ModernUi] window closed on dispose " + Key, ex);
                    }
                }
                ModernRoot.Instance?.Wm.Forget(this);
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The window's panel + title bar, painted once per size / theme / front change.</summary>
    internal sealed class ModernWindowChrome : ModernControl
    {
        private readonly ModernWindow _window;

        public ModernWindowChrome(ModernWindow window)
        {
            _window = window;
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = ModernRoot.K, rad = ModernTheme.Radius * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF panel = RectangleF.Inflate(outer, -1f, -1f);

            using (GraphicsPath black = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, black, Color.Black);

            using (GraphicsPath page = ModernSkin.RoundRect(panel, rad))
            {
                ModernSkin.FillVertical(g, page, panel, ModernSkin.PageColours(), ModernSkin.PagePositions);

                RectangleF title = new RectangleF(panel.X, panel.Y, panel.Width, ModernWindow.TitleHeight * k);
                using (GraphicsPath tp = ModernSkin.RoundTop(title, Math.Max(0, rad - 1)))
                    ModernSkin.FillVertical(g, tp, title, new[] { ModernTheme.PanelC, ModernTheme.PanelA });

                ModernSkin.FadedLine(g, title.X, title.Bottom - 2 * k, title.Width, 2 * k, ModernTheme.Accent);

                ModernSkin.SpacedText(g, _window.Title.ToUpperInvariant(), ModernSkin.Font(13, true), ModernTheme.Accent,
                    new RectangleF(title.X + 30 * k, title.Y, title.Width - 60 * k, title.Height - 2 * k), 13 * 0.28f * k);

                // inset top highlight
                using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.FrameLight, 0.3f), 1f))
                    g.DrawLine(pen, panel.X + rad, panel.Y + 1, panel.Right - rad, panel.Y + 1);

                Color border = _window.Front ? ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.55f) : ModernTheme.Frame;
                ModernSkin.Stroke(g, page, border);
            }
        }
    }

    /// <summary>The title bar's close cross.</summary>
    internal sealed class ModernCloseButton : ModernControl
    {
        protected override void Paint(Graphics g)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0, 0, Size.Width, Size.Height);
            if (Hovered)
            {
                using GraphicsPath bg = ModernSkin.RoundRect(RectangleF.Inflate(r, -0.5f, -0.5f), ModernTheme.Radius * 0.6f * k);
                using var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.3f));
                g.FillPath(b, bg);
            }
            Color c = Hovered ? Color.White : ModernTheme.Dim;
            float pad = Size.Width * 0.3f;
            using var pen = new Pen(c, Math.Max(1.2f, 1.6f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, pad, pad, Size.Width - pad, Size.Height - pad);
            g.DrawLine(pen, Size.Width - pad, pad, pad, Size.Height - pad);
        }
    }
}
