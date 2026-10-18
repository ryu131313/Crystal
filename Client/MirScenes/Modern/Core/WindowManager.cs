using System.Text;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// The new UI's windows (port of core/wm.ts WindowManager + windowRegistry.ts): a catalog of window keys → factories (a
    /// window is built on first open), the front window, Esc closes the front window and Shift+Esc all of them, positions in HUD
    /// px saved in Settings.UiWindows ("key=x,y;…"), and a rebuild of every built window after a HUD scale or theme change.
    /// Z order is the order of ModernRoot.Windows' children (raise = BringToFront), so it is bounded by the window count.
    /// </summary>
    public sealed class WindowManager
    {
        private readonly ModernRoot _root;
        private readonly Dictionary<string, Func<ModernWindow>> _catalog = new Dictionary<string, Func<ModernWindow>>();
        private readonly Dictionary<string, ModernWindow> _built = new Dictionary<string, ModernWindow>();
        private readonly Dictionary<string, Point> _positions = new Dictionary<string, Point>();
        private ModernWindow _front;
        private int _themeVersion;

        public WindowManager(ModernRoot root)
        {
            _root = root;
            LoadPositions();
            _themeVersion = ModernTheme.Version;
            ModernTheme.Changed += OnThemeChanged;
        }

        /// <summary>Registers a window factory under its key (built on first open).</summary>
        public void Register(string key, Func<ModernWindow> factory) => _catalog[key] = factory;

        public bool IsRegistered(string key) => _catalog.ContainsKey(key);

        /// <summary>The built window for a key (builds it), or null for an unknown key.</summary>
        public ModernWindow Get(string key)
        {
            if (_built.TryGetValue(key, out ModernWindow w) && !w.IsDisposed) return w;
            if (!_catalog.TryGetValue(key, out Func<ModernWindow> factory)) return null;
            w = factory();
            if (_positions.TryGetValue(key, out Point p))
            {
                w.HudX = p.X;
                w.HudY = p.Y;
            }
            _built[key] = w;
            return w;
        }

        public void Open(string key) => Get(key)?.Open();
        public void Toggle(string key) => Get(key)?.Toggle();

        public void Close(string key)
        {
            if (_built.TryGetValue(key, out ModernWindow w)) w.Close();
        }

        public bool IsOpen(string key) => _built.TryGetValue(key, out ModernWindow w) && w.IsOpen;

        /// <summary>The front open window, or null.</summary>
        public ModernWindow Front => _front;

        /// <summary>A window opened, closed or was raised: recompute the front window.</summary>
        internal void WindowChanged(ModernWindow w)
        {
            ModernWindow top = null;
            var list = _root.Windows.Controls;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is ModernWindow mw && mw.IsOpen)
                {
                    top = mw;
                    break;
                }
            }
            if (top == _front) return;
            if (_front != null && !_front.IsDisposed) _front.Front = false;
            _front = top;
            if (_front != null) _front.Front = true;
            _root.KeepOnTop();
        }

        /// <summary>Esc closes the front window, Shift+Esc every window (wm.ts handleKey). True = consumed.</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            if (_root.ModalPromptOpen) return false; // a classic prompt is up: it has the keyboard
            if (_root.Popups != null && _root.Popups.HandleKey(e)) return true; // a modal popup takes every key first (popups.ts handleKey)
            if (_front != null && _front.IsOpen && _front.HandleKey(e)) return true;
            if (e.KeyCode != Keys.Escape) return false;
            return e.Shift ? CloseAll() : CloseTop();
        }

        public bool CloseTop()
        {
            if (_front == null || !_front.IsOpen) return false;
            _front.Close();
            return true;
        }

        public bool CloseAll()
        {
            bool any = false;
            foreach (ModernWindow w in Snapshot())
            {
                if (!w.IsOpen) continue;
                w.Close();
                any = true;
            }
            return any;
        }

        public void ResetPositions()
        {
            _positions.Clear();
            Settings.UiWindows = string.Empty;
            foreach (ModernWindow w in Snapshot()) w.ResetPosition();
        }

        /// <summary>Per-frame work of the open windows.</summary>
        public void Process()
        {
            var list = _root.Windows.Controls;
            ModernWindow top = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is ModernWindow w && w.IsOpen)
                {
                    w.Process();
                    top = w;
                }
            }
            // a press on a widget raises its window through MirControl.TrySort, which does not tell us: follow the z order
            if (top != _front) WindowChanged(top);
        }

        /// <summary>After a HUD scale change every built window is laid out again; otherwise only re-placed.</summary>
        public void Rescale(bool rebuild)
        {
            foreach (ModernWindow w in Snapshot())
            {
                if (w.IsDisposed) continue;
                if (rebuild) w.Layout();
                else w.Place();
            }
        }

        private void OnThemeChanged()
        {
            if (_themeVersion == ModernTheme.Version) return;
            _themeVersion = ModernTheme.Version;
            // Chrome and widgets cache their look: lay every built window out again (cheap: once per theme change).
            foreach (ModernWindow w in Snapshot())
                if (!w.IsDisposed) w.Layout();
        }

        /// <summary>A copy of the built windows: closing / building a window may change the dictionary.</summary>
        private ModernWindow[] Snapshot()
        {
            var copy = new ModernWindow[_built.Count];
            _built.Values.CopyTo(copy, 0);
            return copy;
        }

        internal void SavePosition(ModernWindow w)
        {
            _positions[w.Key] = new Point(w.HudX, w.HudY);
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, Point> kv in _positions)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append(':').Append(kv.Value.X).Append(',').Append(kv.Value.Y); // not '=': the ini reader splits on it
            }
            Settings.UiWindows = sb.ToString();
        }

        private void LoadPositions()
        {
            string s = Settings.UiWindows;
            if (string.IsNullOrEmpty(s)) return;
            foreach (string part in s.Split(';'))
            {
                int eq = part.IndexOf(':');
                if (eq <= 0) eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string[] xy = part.Substring(eq + 1).Split(',');
                if (xy.Length != 2 || !int.TryParse(xy[0], out int x) || !int.TryParse(xy[1], out int y)) continue;
                _positions[part.Substring(0, eq)] = new Point(x, y);
            }
        }

        internal void Forget(ModernWindow w)
        {
            if (_built.TryGetValue(w.Key, out ModernWindow b) && b == w) _built.Remove(w.Key);
            if (_front == w) _front = null;
        }

        public void Dispose()
        {
            ModernTheme.Changed -= OnThemeChanged;
            _built.Clear();
            _front = null;
        }
    }
}
