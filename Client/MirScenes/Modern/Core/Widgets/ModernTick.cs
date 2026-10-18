namespace Client.MirScenes.Modern.Core.Widgets
{
    /// <summary>A widget with a timer (confirm-twice disarm, hold fill, search debounce, popup repeat / countdown bars).</summary>
    public interface IModernTick
    {
        /// <summary>Called once per spin while registered; read CMain.Time, change state only when it changed, and call
        /// <see cref="ModernTick.Remove"/> when done.</summary>
        void Tick();
    }

    /// <summary>
    /// The new UI's timers: widgets register only while a timer runs (an armed button, a held button, a pending search), so an
    /// idle UI costs one count check per spin. Run from ModernWindow.Process (every open window calls it; it runs once per
    /// CMain.Time) and ModernRoot.Process (ModernUi.Process: popups outside any window). UI thread only.
    /// </summary>
    public static class ModernTick
    {
        private static readonly List<IModernTick> _list = new List<IModernTick>();
        private static long _last = -1;

        public static int Count => _list.Count;

        public static void Add(IModernTick t)
        {
            if (t != null && !_list.Contains(t)) _list.Add(t);
        }

        public static void Remove(IModernTick t) => _list.Remove(t);

        public static void Run()
        {
            if (_list.Count == 0 || _last == CMain.Time) return;
            _last = CMain.Time;
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                if (i >= _list.Count) continue; // a tick removed others
                IModernTick t = _list[i];
                try
                {
                    t.Tick();
                }
                catch (Exception ex)
                {
                    _list.Remove(t);
                    ModernLog.Once("[ModernUi] tick " + t.GetType().Name, ex);
                }
            }
        }

        /// <summary>The root went away (log-out): drop every timer.</summary>
        public static void Clear()
        {
            _list.Clear();
            _last = -1;
        }
    }
}
