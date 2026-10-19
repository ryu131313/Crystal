// Ported from WebClient/src/ui/modern/windows/options.ts (OptionsWindow): the new UI's Options window over the real settings,
// the way the classic OptionDialog changes them (same Settings fields, same side effects: the skill mode / HP-MP mode /
// movement style / lighting chat hints, SoundManager volumes through Settings.Volume / MusicVolume). Changes apply at once (no
// draft); a per-row reset arrow and a per-page "Restore defaults"; a search across every page; pages built on first visit.
// Pages: Graphics (lighting, effects), Interface (Classic interface switch with a log-out offer, HUD scale / auto scale /
// opacity, stat plates, the theme preset + custom colours and knobs through ModernTheme.Apply, world labels, the values only
// the classic interface reads), Gameplay, Audio, Chat (tabs, channels, filters), Social (the server's allow toggles: a click
// sends C.SetAllowToggle and the switch stays locked until S.AllowToggles; before it only Allow observing works, through
// @ALLOWOBSERVE like OptionDialog). Key bindings link to the "keybinds" window; a small "Kit test" button opens the kit window.
// Desktop changes: no resolution row (the desktop picks it at log-in), no web-only rows (smooth move, atmosphere, touch, auto
// potion); the stat plates are the desktop's on / off (Settings.UiPlates); the theme is a chip list (17 presets); colours are
// typed as #rrggbb.
using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class OptionsWindow : ModernWindow
    {
        public const string WindowKey = "options";
        private const int WidthHud = 860, HeightHud = 530, NavW = 176, CtlW = 290;

        private enum Cat { Graphics, Interface, Gameplay, Audio, Chat, Social }
        private enum Kind { Toggle, Slider, Seg, Chips, Colour, Action }

        private sealed class Def
        {
            public string Id;
            public Cat Cat;
            public ClientTextKeys Group, Label, Desc;
            public Kind Kind;
            public Func<bool> GetB;
            public Action<bool> SetB;
            public bool? DefB;
            public Func<int> GetI;
            public Action<int> SetI;
            public int Min, Max, Step, DefI = int.MinValue;
            public string Unit = string.Empty;
            public bool Deferred;
            public Choice<string>[] Options;
            public Func<string> GetS;
            public Action<string> SetS;
            public string DefS;
            public ClientTextKeys ActionText;
            public Action Run;
            public Func<string> Lock;
            public Func<bool> Mod;
            public Action Reset;

            public bool CanReset => Reset != null || DefB.HasValue || DefI != int.MinValue || DefS != null;

            public bool IsMod()
            {
                if (Mod != null) return Mod();
                if (DefB.HasValue && GetB != null) return GetB() != DefB.Value;
                if (DefI != int.MinValue && GetI != null) return GetI() != DefI;
                if (DefS != null && GetS != null) return GetS() != DefS;
                return false;
            }

            public void DoReset()
            {
                if (Reset != null) { Reset(); return; }
                if (DefB.HasValue) SetB?.Invoke(DefB.Value);
                else if (DefI != int.MinValue) SetI?.Invoke(DefI);
                else if (DefS != null) SetS?.Invoke(DefS);
            }

            public string Text => W.T(Label) + " " + W.T(Desc) + " " + W.T(Group) + " " + Cat;
        }

        private static readonly (Cat Cat, int Icon, ClientTextKeys Name, ClientTextKeys Info)[] Cats =
        {
            (Cat.Graphics, ModernIcons.Eye, ClientTextKeys.ModernWinOptGraphics, ClientTextKeys.ModernWinOptGraphicsInfo),
            (Cat.Interface, ModernIcons.Menu, ClientTextKeys.ModernWinOptInterface, ClientTextKeys.ModernWinOptInterfaceInfo),
            (Cat.Gameplay, ModernIcons.Sword, ClientTextKeys.ModernWinOptGameplay, ClientTextKeys.ModernWinOptGameplayInfo),
            (Cat.Audio, ModernIcons.Play, ClientTextKeys.ModernWinOptAudio, ClientTextKeys.ModernWinOptAudioInfo),
            (Cat.Chat, ModernIcons.Chat, ClientTextKeys.ModernWinOptChat, ClientTextKeys.ModernWinOptChatInfo),
            (Cat.Social, ModernIcons.Friends, ClientTextKeys.ModernWinOptSocial, ClientTextKeys.ModernWinOptSocialInfo),
        };

        private Cat _cat = Cat.Graphics;
        private string _query = string.Empty;
        private readonly List<Def> _defs;
        private readonly HashSet<AllowToggle> _pending = new HashSet<AllowToggle>();
        private int _scaleWanted = -1;

        private readonly List<PaintBox> _nav = new List<PaintBox>();
        private ScrollView _page;
        private readonly List<Action> _syncs = new List<Action>();
        private bool _dirty = true;
        private readonly Action _togglesChanged;
        private int _themeVersion;

        public OptionsWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinOptions), 52, 30)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _defs = BuildDefs();
            _togglesChanged = () => { _pending.Clear(); _dirty = true; };
        }

        internal string CategoryShown => _query.Length > 0 ? "search" : _cat.ToString();
        internal ScrollView Page => _page;

        // ------------------------------------------------------------------ layout

        protected override void Build()
        {
            const int top = TitleHeight;
            W.VDivider(this, NavW, top, HeightHud - 40);
            var search = W.At(new SearchBox(NavW - 16, W.T(ClientTextKeys.ModernWinSearchSettings)), this, 8, top + 10);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.Trim(); BuildPage(); };
            _nav.Clear();
            int y = top + 44;
            foreach (var c in Cats)
            {
                Cat cat = c.Cat;
                var it = NavItem(c.Icon, W.T(c.Name), string.Empty, y);
                it.Click += (o, e) =>
                {
                    if (_cat == cat && _query.Length == 0) return;
                    _cat = cat;
                    if (_query.Length > 0) search.SetQuery(string.Empty);
                    else BuildPage();
                };
                it.Index = (int)cat;
                _nav.Add(it);
                y += 35;
            }
            W.Divider(this, 12, y + 3, NavW - 24);
            y += 10;
            if (ModernRoot.Instance?.Wm.IsRegistered(KeybindsWindow.WindowKey) ?? false)
            {
                var keys = NavItem(ModernIcons.Keyboard, W.T(ClientTextKeys.ModernWinKeyBindings), W.CapLabel(W.Key(KeybindOptions.Keybind)), y);
                keys.Hint = W.T(ClientTextKeys.ModernWinKeyBindingsHint);
                keys.Index = -1;
                keys.Click += (o, e) => ModernRoot.Instance?.Wm.Open(KeybindsWindow.WindowKey);
                y += 35;
            }
            var kit = new ModernButton(W.T(ClientTextKeys.ModernWinKitTest)) { Parent = this, Tooltip = W.T(ClientTextKeys.ModernWinKitTestHint) };
            kit.AutoWidth(70);
            kit.Location = new Point(S(10), S(top + HeightHud - 40 - 30));
            kit.Click += (o, e) => ModernRoot.Instance?.Wm.Open(KitTestWindow.WindowKey);

            _page = new ScrollView(WidthHud - NavW - 2, HeightHud - 40) { Parent = this, Location = new Point(S(NavW + 1), S(top)), Background = W.Page };
            _page.Framed = false;
            _page.WheelStep = S(60);
            W.Divider(this, 0, top + HeightHud - 40, WidthHud);
            W.Label(this, W.T(ClientTextKeys.ModernWinOptFoot), 16, top + HeightHud - 32, WidthHud - 32, 24, 11, false, ModernTheme.Dim);
            _themeVersion = ModernTheme.Version;
            BuildPage();
        }

        private PaintBox NavItem(int icon, string label, string chip, int y)
        {
            var it = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                bool on = b.Index >= 0 && b.Index == (int)_cat && _query.Length == 0;
                W.Row(g, b.Size, false, b.Hovered, on);
                ModernSkin.Text(g, label, ModernSkin.Font(12, true), on ? Color.White : b.Hovered ? ModernTheme.Text : ModernTheme.Dim, new RectangleF(36 * k, 0, b.Size.Width - 40 * k, b.Size.Height));
                if (chip.Length > 0) ModernSkin.Text(g, chip, ModernSkin.Font(10, true), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 10 * k, b.Size.Height), StringAlignment.Far);
            })
            { Parent = this, Location = new Point(S(8), S(y)), Size = new Size(S(NavW - 16), S(32)) };
            it.AfterPaint = b =>
            {
                bool on = b.Index >= 0 && b.Index == (int)_cat && _query.Length == 0;
                W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(8), S(16), S(16)), on ? ModernTheme.Accent : ModernTheme.Dim);
            };
            return it;
        }

        private void BuildPage()
        {
            if (_page == null || _page.IsDisposed) return;
            for (int i = _page.Content.Controls.Count - 1; i >= 0; i--) _page.Content.Controls[i].Dispose();
            _syncs.Clear();
            for (int i = 0; i < _nav.Count; i++) _nav[i].Invalidate();
            int w = (int)Math.Round(_page.InnerWidth / ModernRoot.K) - 30;
            MirControl p = _page.Content;
            int y = 14;
            List<Def> defs = new List<Def>();
            if (_query.Length > 0)
            {
                string q = _query.ToLowerInvariant();
                W.Label(p, W.T(ClientTextKeys.ModernWinResultsFor, _query), 20, y, w, 24, 18, true, Color.White);
                y += 34;
                foreach (Def d in _defs) if (d.Text.ToLowerInvariant().Contains(q)) defs.Add(d);
                if (defs.Count == 0) { W.Label(p, W.T(ClientTextKeys.ModernWinNoSettingsMatch), 20, y + 20, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center); y += 60; }
            }
            else
            {
                var info = Array.Find(Cats, c => c.Cat == _cat);
                W.Label(p, W.T(info.Name), 20, y, 300, 24, 18, true, Color.White);
                W.Label(p, W.T(info.Info), 20, y + 24, w - 160, 16, 11, false, ModernTheme.Dim);
                foreach (Def d in _defs) if (d.Cat == _cat) defs.Add(d);
                List<Def> resettable = defs.FindAll(d => d.CanReset);
                if (resettable.Count > 0)
                {
                    var restore = new ModernButton(W.T(ClientTextKeys.ModernWinRestoreDefaults)) { Parent = p, Tooltip = W.T(ClientTextKeys.ModernWinRestoreDefaultsHint) };
                    restore.AutoWidth(110);
                    restore.Location = new Point(S(20 + w) - restore.Size.Width, S(y + 4));
                    string catName = W.T(info.Name);
                    restore.Click += (o, e) => ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernWinRestoreAsk, catName), yes =>
                    {
                        if (!yes || IsDisposed) return;
                        foreach (Def d in resettable) if (d.IsMod()) d.DoReset();
                        _dirty = true;
                    }, false, PopupKind.Question, W.T(ClientTextKeys.ModernWinRestoreDefaults));
                }
                y += 48;
            }
            ClientTextKeys? group = null;
            foreach (Def d in defs)
            {
                if (group != d.Group)
                {
                    group = d.Group;
                    string head = (_query.Length > 0 ? W.T(Cats[(int)d.Cat].Name) + " · " : string.Empty) + W.T(d.Group);
                    string text = head.ToUpperInvariant();
                    new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        Font f = ModernSkin.Font(10, true);
                        float tw = ModernSkin.Measure(text, f) + 10 * k;
                        ModernSkin.Text(g, text, f, ModernTheme.AccentDim, new RectangleF(0, 0, tw, b.Size.Height));
                        using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(tw - 1, 0, b.Size.Width - tw + 2, 2), W.Over(ModernTheme.Accent, 0.4f), Color.FromArgb(0, W.Page), 0f);
                        g.FillRectangle(br, tw, b.Size.Height / 2f, b.Size.Width - tw, 1);
                    }) { Parent = p, Location = new Point(S(20), S(y + 8)), Size = new Size(S(w), S(16)), NotControl = true };
                    y += 28;
                }
                y = Row(p, d, y, w);
            }
            if (_query.Length > 0 && (ModernRoot.Instance?.Wm.IsRegistered(KeybindsWindow.WindowKey) ?? false))
            {
                W.Label(p, W.T(ClientTextKeys.ModernWinLookingForKey), 30, y + 16, w - 160, 24, 11, false, ModernTheme.Dim);
                var open = new ModernButton(W.T(ClientTextKeys.ModernWinKeyBindings)) { Parent = p };
                open.AutoWidth(100);
                open.Location = new Point(S(20 + w) - open.Size.Width, S(y + 16));
                open.Click += (o, e) => ModernRoot.Instance?.Wm.Open(KeybindsWindow.WindowKey);
                y += 48;
            }
            _page.ContentHeight = S(y + 16);
            _page.ScrollTo(0);
            _dirty = true;
        }

        /// <summary>One setting row: label + description, the reset arrow, the control on the right. Returns the next y.</summary>
        private int Row(MirControl p, Def d, int y, int w)
        {
            int ctlH = 26;
            MirControl ctl = null;
            Action sync = null;
            switch (d.Kind)
            {
                case Kind.Toggle:
                {
                    var sw = new ToggleSwitch(d.GetB()) { Parent = p };
                    sw.Changed += v => { d.SetB(v); _dirty = true; };
                    string last = "\0";
                    sync = () =>
                    {
                        sw.Set(d.GetB());
                        string why = d.Lock?.Invoke() ?? string.Empty;
                        if (why != last) { last = why; sw.Lock(why.Length > 0, why); }
                    };
                    ctl = sw;
                    break;
                }
                case Kind.Slider:
                {
                    var sl = new Slider(d.Min, d.Max, d.GetI(), 230, d.Step) { Parent = p };
                    string unit = d.Unit;
                    sl.Format = v => v + unit;
                    sl.Changed += v =>
                    {
                        if (d.Deferred) _scaleWanted = v; // applied when the button is released (it rescales this window)
                        else { d.SetI(v); _dirty = true; }
                    };
                    string last = "\0";
                    sync = () =>
                    {
                        if (!d.Deferred || _scaleWanted < 0) sl.Set(d.GetI());
                        string why = d.Lock?.Invoke() ?? string.Empty;
                        if (why != last) { last = why; sl.Enabled = why.Length == 0; sl.Hint = why; }
                    };
                    ctl = sl;
                    break;
                }
                case Kind.Seg:
                {
                    var seg = new Segmented<string>(d.Options, d.GetS(), Math.Min(CtlW, 90 * d.Options.Length)) { Parent = p };
                    seg.Changed += v => { d.SetS(v); _dirty = true; };
                    sync = () => seg.Set(d.GetS());
                    ctl = seg;
                    break;
                }
                case Kind.Chips:
                {
                    var ch = new Chips<string>(d.Options, new[] { d.GetS() }, CtlW, false, false) { Parent = p };
                    ch.Changed += sel => { foreach (string s in sel) { if (s != d.GetS()) d.SetS(s); } _dirty = true; };
                    sync = () => { string v = d.GetS(); if (!ch.IsOn(v)) ch.Set(new[] { v }); };
                    ctl = ch;
                    ctlH = (int)Math.Ceiling(ch.Size.Height / ModernRoot.K);
                    break;
                }
                case Kind.Colour:
                {
                    var panel = new ModernPanel { Parent = p, Size = new Size(S(130), S(24)) };
                    var field = new ModernTextField(96, 24, 12) { Parent = panel, Location = Point.Empty, MaxLength = 7 };
                    field.Text = d.GetS();
                    var swatch = new PaintBox((g, b) =>
                    {
                        Color c = ModernTheme.TryHex(d.GetS()) ?? Color.Black;
                        using var p2 = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1), ModernTheme.Radius * 0.8f * ModernRoot.K);
                        using (var br = new SolidBrush(c)) g.FillPath(br, p2);
                        ModernSkin.Stroke(g, p2, ModernTheme.Frame);
                    }) { Parent = panel, Location = new Point(S(102), 0), Size = new Size(S(28), S(24)), NotControl = true };
                    void Apply(string v)
                    {
                        v = (v ?? string.Empty).Trim();
                        if (v.Length == 6 && v[0] != '#') v = "#" + v;
                        if (ModernTheme.TryHex(v) == null) return;
                        d.SetS(v.ToLowerInvariant());
                    }
                    field.Submitted += Apply;
                    field.TextChanged += v => { if ((v ?? string.Empty).Trim().Length == 7) Apply(v); };
                    sync = () =>
                    {
                        if (!field.Focused && !string.Equals(field.Text, d.GetS(), StringComparison.OrdinalIgnoreCase)) field.Text = d.GetS();
                        swatch.Invalidate();
                    };
                    ctl = panel;
                    break;
                }
                case Kind.Action:
                {
                    var b = new ModernButton(W.T(d.ActionText)) { Parent = p };
                    b.AutoWidth(90);
                    b.Click += (o, e) => { if ((d.Lock?.Invoke() ?? string.Empty).Length == 0) { d.Run(); _dirty = true; } };
                    string last = "\0";
                    sync = () =>
                    {
                        string why = d.Lock?.Invoke() ?? string.Empty;
                        if (why != last) { last = why; b.SetDisabled(why.Length > 0, why); }
                    };
                    ctl = b;
                    break;
                }
            }

            string desc = W.T(d.Desc);
            int lw = w - CtlW - 40;
            SizeF ds = ModernSkin.MeasureWrapped(desc, ModernSkin.Font(11), S(lw));
            int descH = Math.Max(14, (int)Math.Ceiling(ds.Height / ModernRoot.K) + 2);
            int rowH = Math.Max(18 + descH + 16, ctlH + 16);
            var bg = new PaintBox((g, b) =>
            {
                if (!b.Hovered) return;
                using var p2 = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1), ModernTheme.Radius * ModernRoot.K);
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p2);
                ModernSkin.Stroke(g, p2, W.Over(ModernTheme.Frame, 0.4f));
            }) { Parent = p, Location = new Point(S(20), S(y)), Size = new Size(S(w), S(rowH)) };
            bg.BringToFront();
            if (ctl != null) ctl.BringToFront();
            W.Label(p, W.T(d.Label), 30, y + 8, lw, 18, 13, true);
            W.Label(p, desc, 30, y + 26, lw, descH, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            if (ctl != null)
            {
                int cw = (int)Math.Round(ctl.Size.Width / ModernRoot.K);
                ctl.Location = new Point(S(20 + w - 10 - cw), S(y + (rowH - ctlH) / 2));
            }
            if (d.CanReset)
            {
                var rst = new PaintBox((g, b) =>
                {
                    if (b.Index == 0) return; // not modified: hidden
                    float k = ModernRoot.K;
                    if (b.Hovered)
                        using (var br = new SolidBrush(W.Over(ModernTheme.Accent, 0.3f))) g.FillEllipse(br, 0, 0, b.Size.Width - 1, b.Size.Height - 1);
                    ModernSkin.Text(g, "↺", ModernSkin.Font(13, true), b.Hovered ? Color.White : ModernTheme.Dim, new RectangleF(0, -1 * k, b.Size.Width, b.Size.Height), StringAlignment.Center);
                })
                { Parent = p, Location = new Point(S(20 + w - 10 - CtlW - 26), S(y + (rowH - 20) / 2)), Size = new Size(S(20), S(20)), Hint = W.T(ClientTextKeys.ModernWinBackToDefault) };
                rst.Click += (o, e) => { if (rst.Index != 0) { d.DoReset(); _dirty = true; } };
                Action inner = sync;
                sync = () =>
                {
                    inner?.Invoke();
                    int mod = d.IsMod() ? 1 : 0;
                    if (mod != rst.Index) { rst.Index = mod; rst.Invalidate(); }
                };
            }
            if (sync != null) _syncs.Add(sync);
            return y + rowH + 2;
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            GameScene.Models.AllowToggles.Changed.On(_togglesChanged);
            _dirty = true;
        }

        protected override void Closed()
        {
            GameScene.Models.AllowToggles.Changed.Off(_togglesChanged);
            _pending.Clear(); // a request still unanswered keeps no lock across a reopen
            _scaleWanted = -1;
        }

        private bool _observeKnown = GameScene.AllowObserve;

        public override void Process()
        {
            base.Process();
            if (_scaleWanted >= 0 && (Control.MouseButtons & MouseButtons.Left) == 0)
            {
                int v = _scaleWanted;
                _scaleWanted = -1;
                Settings.UiScale = v / 100f;
                ModernRoot.Instance?.ScreenChanged(); // lays this window out again at the new scale
                return;
            }
            if (_observeKnown != GameScene.AllowObserve)
            {
                _observeKnown = GameScene.AllowObserve;
                _pending.Remove(AllowToggle.Observe); // S.AllowObserve (older servers)
                _dirty = true;
            }
            if (!_dirty) return;
            _dirty = false;
            for (int i = 0; i < _syncs.Count; i++) _syncs[i]();
        }

        // ------------------------------------------------------------------ actions

        private static void ChatHint(ClientTextKeys key) => GameScene.Models.Chat.Add(W.T(key), ChatType.Hint);

        /// <summary>The "Classic interface" switch: UiStyle is read at log-in, so offer to log out now (OptionDialog.ChangeUiStyle).</summary>
        private void SetClassic(bool on)
        {
            if (!on)
            {
                Settings.UiStyle = UiStyle.Modern;
                return;
            }
            Settings.UiStyle = UiStyle.Classic;
            ModernRoot.Instance?.Popups.Confirm(W.T(ClientTextKeys.ModernUiStyleSwitchPrompt), PopupButtons.YesNo, PopupKind.Question, (a, _) =>
            {
                if (a == PopupAnswer.Yes) GameScene.Scene?.LogOut(false);
            }, false, W.T(ClientTextKeys.ModernWinOptClassicUi));
        }

        /// <summary>C.SetAllowToggle (server authoritative: locked until S.AllowToggles). Allow observing on a server without
        /// S.AllowToggles falls back to OptionDialog.ToggleObserve (C.Chat "@ALLOWOBSERVE").</summary>
        private void SetToggle(AllowToggle t, bool v)
        {
            var acc = GameScene.Models.AllowToggles;
            if (_pending.Contains(t)) return;
            if (!acc.Known)
            {
                if (t != AllowToggle.Observe || GameScene.AllowObserve == v) return;
                Network.Enqueue(new C.Chat { Message = "@ALLOWOBSERVE" });
            }
            else
            {
                if (acc.Get(t) == v) return;
                Network.Enqueue(new C.SetAllowToggle { Toggle = t, Value = v });
            }
            _pending.Add(t);
        }

        private bool ToggleState(AllowToggle t)
        {
            var acc = GameScene.Models.AllowToggles;
            return !acc.Known && t == AllowToggle.Observe ? GameScene.AllowObserve : acc.Get(t);
        }

        private string ToggleLock(AllowToggle t)
        {
            if (_pending.Contains(t)) return W.T(ClientTextKeys.ModernWinWaitingServer);
            if (!GameScene.Models.AllowToggles.Known && t != AllowToggle.Observe) return W.T(ClientTextKeys.ModernWinServerNoToggle);
            return string.Empty;
        }

        private static void ApplyTheme()
        {
            ModernTheme.ApplySettings(); // every built window is laid out again (WindowManager)
        }

        private static bool CustomTheme => Settings.UiCustomAccent.Length > 0 || Settings.UiCustomFrame.Length > 0 || Settings.UiCustomText.Length > 0 ||
            Settings.UiCustomHue >= 0 || Settings.UiCustomSat >= 0 || Settings.UiCustomLit >= 0 || Settings.UiCustomGlow >= 0 || Settings.UiCustomRadius >= 0;

        private static void ClearCustom()
        {
            Settings.UiCustomAccent = Settings.UiCustomFrame = Settings.UiCustomText = string.Empty;
            Settings.UiCustomHue = Settings.UiCustomSat = Settings.UiCustomLit = Settings.UiCustomGlow = Settings.UiCustomRadius = -1;
        }

        /// <summary>Window and HUD opacity now (ModernWindow reads Settings.UiOpacity when built).</summary>
        private static void ApplyOpacity()
        {
            ModernRoot root = ModernRoot.Instance;
            if (root == null) return;
            float o = Math.Clamp(Settings.UiOpacity, 0.2f, 1f);
            foreach (MirControl c in root.Windows.Controls) if (c is ModernWindow w) w.Opacity = o;
            foreach (MirControl c in root.Hud.Controls) if (c is ModernSurface s) s.Opacity = o;
        }

        // ------------------------------------------------------------------ the catalogue

        private List<Def> BuildDefs()
        {
            Def Bool(Cat cat, ClientTextKeys group, string id, ClientTextKeys label, ClientTextKeys desc, Func<bool> get, Action<bool> set, bool def) =>
                new Def { Id = id, Cat = cat, Group = group, Label = label, Desc = desc, Kind = Kind.Toggle, GetB = get, SetB = set, DefB = def };
            Def Colour(string id, ClientTextKeys label, ClientTextKeys desc, Func<Color> theme, Func<string> custom, Action<string> set) =>
                new Def
                {
                    Id = id, Cat = Cat.Interface, Group = ClientTextKeys.ModernWinOptGroupTheme, Label = label, Desc = desc, Kind = Kind.Colour,
                    GetS = () => ModernTheme.ToHex(theme()), SetS = v => { set(v); ApplyTheme(); },
                    Mod = () => custom().Length > 0, Reset = () => { set(string.Empty); ApplyTheme(); },
                };
            Def Knob(string id, ClientTextKeys label, ClientTextKeys desc, int min, int max, int step, string unit, float k, Func<float> theme, Func<float> custom, Action<float> set) =>
                new Def
                {
                    Id = id, Cat = Cat.Interface, Group = ClientTextKeys.ModernWinOptGroupTheme, Label = label, Desc = desc, Kind = Kind.Slider, Min = min, Max = max, Step = step, Unit = unit,
                    GetI = () => (int)Math.Round(theme() * k), SetI = v => { set(v / k); ApplyTheme(); },
                    Mod = () => custom() >= 0, Reset = () => { set(-1); ApplyTheme(); },
                };
            Def Allow(AllowToggle t, ClientTextKeys label, ClientTextKeys desc) =>
                new Def { Id = "allow" + t, Cat = Cat.Social, Group = ClientTextKeys.ModernWinOptGroupReach, Label = label, Desc = desc, Kind = Kind.Toggle, GetB = () => ToggleState(t), SetB = v => SetToggle(t, v), Lock = () => ToggleLock(t) };

            var themes = new Choice<string>[ModernTheme.Presets.Length];
            for (int i = 0; i < themes.Length; i++) themes[i] = new Choice<string>(ModernTheme.Presets[i].Name, ModernTheme.Presets[i].Name);

            const ClientTextKeys gLight = ClientTextKeys.ModernWinOptGroupLighting, gStyle = ClientTextKeys.ModernWinOptGroupStyle, gHud = ClientTextKeys.ModernWinOptGroupHud,
                gLabels = ClientTextKeys.ModernWinOptGroupLabels, gClassic = ClientTextKeys.ModernWinOptGroupClassicOnly, gCombat = ClientTextKeys.ModernWinOptGroupCombat,
                gMove = ClientTextKeys.ModernWinOptGroupMovement, gVol = ClientTextKeys.ModernWinOptGroupVolume, gTabs = ClientTextKeys.ModernWinOptGroupTabs,
                gChan = ClientTextKeys.ModernWinOptGroupChannels, gHide = ClientTextKeys.ModernWinOptGroupHide;

            return new List<Def>
            {
                // Graphics
                new Def
                {
                    Id = "Lighting", Cat = Cat.Graphics, Group = gLight, Label = ClientTextKeys.ModernWinOptLighting, Desc = ClientTextKeys.ModernWinOptLightingDesc, Kind = Kind.Seg,
                    Options = new[] { new Choice<string>("Classic", W.T(ClientTextKeys.ModernWinOptClassic)), new Choice<string>("Enhanced", W.T(ClientTextKeys.ModernWinOptEnhanced)) },
                    DefS = "Classic", GetS = () => Settings.Lighting == LightingMode.Enhanced ? "Enhanced" : "Classic",
                    SetS = v =>
                    {
                        // ChatDialog.TryLightingCommand (@LIGHT NEW / OLD)
                        LightingMode mode = v == "Enhanced" ? LightingMode.Enhanced : LightingMode.Classic;
                        if (Settings.Lighting == mode) return;
                        if (mode == LightingMode.Enhanced && !DXManager.EnhancedLightingAvailable) { ChatHint(ClientTextKeys.LightingEnhancedUnavailable); return; }
                        Settings.Lighting = mode;
                        if (GameScene.Scene?.MapControl != null) GameScene.Scene.MapControl.TextureValid = false;
                        ChatHint(mode == LightingMode.Enhanced ? ClientTextKeys.LightingEnhanced : ClientTextKeys.LightingClassic);
                    },
                },
                Bool(Cat.Graphics, gLight, "Effect", ClientTextKeys.ModernWinOptEffect, ClientTextKeys.ModernWinOptEffectDesc, () => Settings.Effect, v => Settings.Effect = v, true),
                Bool(Cat.Graphics, gLight, "LevelEffect", ClientTextKeys.ModernWinOptLevelEffect, ClientTextKeys.ModernWinOptLevelEffectDesc, () => Settings.LevelEffect, v => Settings.LevelEffect = v, true),
                // Desktop-Smooth-Mode-and-Weather: the classic OptionDialog has the same two rows.
                Bool(Cat.Graphics, gLight, "SmoothMove", ClientTextKeys.SmoothMove, ClientTextKeys.SmoothMoveDesc, () => Settings.SmoothMove, v => Settings.SmoothMove = v, true),
                Bool(Cat.Graphics, gLight, "Atmosphere", ClientTextKeys.Weather, ClientTextKeys.WeatherDesc, () => Settings.Atmosphere,
                    v => { Settings.Atmosphere = v; if (GameScene.Scene?.MapControl != null) GameScene.Scene.MapControl.TextureValid = false; }, true),
                Bool(Cat.Graphics, gLight, "Reflections", ClientTextKeys.Reflections, ClientTextKeys.ReflectionsDesc, () => Settings.Reflections, v => Settings.Reflections = v, true),
                // Interface
                new Def { Id = "UiStyle", Cat = Cat.Interface, Group = gStyle, Label = ClientTextKeys.ModernWinOptClassicUi, Desc = ClientTextKeys.ModernWinOptClassicUiDesc, Kind = Kind.Toggle,
                    GetB = () => Settings.UiStyle == UiStyle.Classic, SetB = SetClassic },
                new Def { Id = "UiScale", Cat = Cat.Interface, Group = gHud, Label = ClientTextKeys.ModernWinOptHudScale, Desc = ClientTextKeys.ModernWinOptHudScaleDesc, Kind = Kind.Slider,
                    Min = 50, Max = 200, Step = 5, Unit = "%", DefI = 100, Deferred = true, GetI = () => (int)Math.Round(Settings.UiScale * 100),
                    SetI = v => { Settings.UiScale = v / 100f; ModernRoot.Instance?.ScreenChanged(); } },
                Bool(Cat.Interface, gHud, "UiAutoScale", ClientTextKeys.ModernWinOptAutoScale, ClientTextKeys.ModernWinOptAutoScaleDesc, () => Settings.UiAutoScale,
                    v => { Settings.UiAutoScale = v; ModernRoot.Instance?.ScreenChanged(); }, true),
                new Def { Id = "UiOpacity", Cat = Cat.Interface, Group = gHud, Label = ClientTextKeys.ModernWinOptOpacity, Desc = ClientTextKeys.ModernWinOptOpacityDesc, Kind = Kind.Slider,
                    Min = 20, Max = 100, Step = 5, Unit = "%", DefI = 100, GetI = () => (int)Math.Round(Settings.UiOpacity * 100), SetI = v => { Settings.UiOpacity = v / 100f; ApplyOpacity(); } },
                Bool(Cat.Interface, gHud, "UiPlates", ClientTextKeys.ModernWinOptPlates, ClientTextKeys.ModernWinOptPlatesDesc, () => Settings.UiPlates,
                    v => { Settings.UiPlates = v; ModernRoot.Instance?.ScreenChanged(); }, true),
                new Def { Id = "UiTheme", Cat = Cat.Interface, Group = ClientTextKeys.ModernWinOptGroupTheme, Label = ClientTextKeys.ModernWinOptTheme, Desc = ClientTextKeys.ModernWinOptThemeDesc,
                    Kind = Kind.Chips, Options = themes, DefS = ModernTheme.DefaultTheme, GetS = () => ModernTheme.Preset(Settings.UiTheme).Name,
                    SetS = v => { ClearCustom(); Settings.UiTheme = v; ApplyTheme(); } },
                Colour("UiAccent", ClientTextKeys.ModernWinOptAccent, ClientTextKeys.ModernWinOptAccentDesc, () => ModernTheme.Accent, () => Settings.UiCustomAccent, v => Settings.UiCustomAccent = v),
                Colour("UiFrame", ClientTextKeys.ModernWinOptFrame, ClientTextKeys.ModernWinOptFrameDesc, () => ModernTheme.Frame, () => Settings.UiCustomFrame, v => Settings.UiCustomFrame = v),
                Colour("UiText", ClientTextKeys.ModernWinOptText, ClientTextKeys.ModernWinOptTextDesc, () => ModernTheme.Text, () => Settings.UiCustomText, v => Settings.UiCustomText = v),
                Knob("UiHue", ClientTextKeys.ModernWinOptHue, ClientTextKeys.ModernWinOptHueDesc, 0, 360, 1, "°", 1, () => ModernTheme.Hue, () => Settings.UiCustomHue, v => Settings.UiCustomHue = v),
                Knob("UiSat", ClientTextKeys.ModernWinOptSat, ClientTextKeys.ModernWinOptSatDesc, 0, 200, 5, "%", 100, () => ModernTheme.Sat, () => Settings.UiCustomSat, v => Settings.UiCustomSat = v),
                Knob("UiLit", ClientTextKeys.ModernWinOptLit, ClientTextKeys.ModernWinOptLitDesc, 30, 250, 5, "%", 100, () => ModernTheme.Lit, () => Settings.UiCustomLit, v => Settings.UiCustomLit = v),
                Knob("UiGlow", ClientTextKeys.ModernWinOptGlow, ClientTextKeys.ModernWinOptGlowDesc, 0, 200, 5, "%", 100, () => ModernTheme.Glow, () => Settings.UiCustomGlow, v => Settings.UiCustomGlow = v),
                Knob("UiRadius", ClientTextKeys.ModernWinOptRadius, ClientTextKeys.ModernWinOptRadiusDesc, 0, 12, 1, " px", 1, () => ModernTheme.Radius, () => Settings.UiCustomRadius, v => Settings.UiCustomRadius = v),
                new Def { Id = "UiCustomReset", Cat = Cat.Interface, Group = ClientTextKeys.ModernWinOptGroupTheme, Label = ClientTextKeys.ModernWinOptCustom, Desc = ClientTextKeys.ModernWinOptCustomDesc,
                    Kind = Kind.Action, ActionText = ClientTextKeys.ModernWinOptUseThemeColours, Run = () => { ClearCustom(); ApplyTheme(); },
                    Lock = () => CustomTheme ? string.Empty : W.T(ClientTextKeys.ModernWinOptNoCustom) },
                Bool(Cat.Interface, gLabels, "NameView", ClientTextKeys.ModernWinOptNames, ClientTextKeys.ModernWinOptNamesDesc, () => Settings.NameView, v => Settings.NameView = v, true),
                Bool(Cat.Interface, gLabels, "DropView", ClientTextKeys.ModernWinOptDropView, ClientTextKeys.ModernWinOptDropViewDesc, () => Settings.DropView, v => Settings.DropView = v, true),
                Bool(Cat.Interface, gLabels, "DisplayDamage", ClientTextKeys.ModernWinOptDamage, ClientTextKeys.ModernWinOptDamageDesc, () => Settings.DisplayDamage, v => Settings.DisplayDamage = v, true),
                Bool(Cat.Interface, gLabels, "DisplayBodyName", ClientTextKeys.ModernWinOptBodyName, ClientTextKeys.ModernWinOptBodyNameDesc, () => Settings.DisplayBodyName, v => Settings.DisplayBodyName = v, false),
                Bool(Cat.Interface, gLabels, "HighlightTarget", ClientTextKeys.ModernWinOptHighlight, ClientTextKeys.ModernWinOptHighlightDesc, () => Settings.HighlightTarget, v => Settings.HighlightTarget = v, true),
                Bool(Cat.Interface, gClassic, "SkillMode", ClientTextKeys.ModernWinOptSkillMode, ClientTextKeys.ModernWinOptSkillModeDesc, () => Settings.SkillMode,
                    v => { if (v != Settings.SkillMode) GameScene.Scene?.ChangeSkillMode(!v); }, false), // OptionDialog SkillModeOn / Off
                Bool(Cat.Interface, gClassic, "SkillBar", ClientTextKeys.ModernWinOptSkillBar, ClientTextKeys.ModernWinOptSkillBarDesc, () => Settings.SkillBar, v => Settings.SkillBar = v, true),
                Bool(Cat.Interface, gClassic, "HPView", ClientTextKeys.ModernWinOptHpView, ClientTextKeys.ModernWinOptHpViewDesc, () => Settings.HPView,
                    v => { Settings.HPView = v; ChatHint(v ? ClientTextKeys.HpMpMode1 : ClientTextKeys.HpMpMode2); }, true),
                Bool(Cat.Interface, gClassic, "ModeView", ClientTextKeys.ModernWinOptModeView, ClientTextKeys.ModernWinOptModeViewDesc, () => Settings.ModeView, v => Settings.ModeView = v, false),
                Bool(Cat.Interface, gClassic, "DuraView", ClientTextKeys.ModernWinOptDuraView, ClientTextKeys.ModernWinOptDuraViewDesc, () => Settings.DuraView, v => Settings.DuraView = v, false),
                Bool(Cat.Interface, gClassic, "TransparentChat", ClientTextKeys.ModernWinOptTransparentChat, ClientTextKeys.ModernWinOptTransparentChatDesc, () => Settings.TransparentChat, v => Settings.TransparentChat = v, false),
                Bool(Cat.Interface, gClassic, "ExpandedBuffWindow", ClientTextKeys.ModernWinOptExpandedBuffs, ClientTextKeys.ModernWinOptExpandedBuffsDesc, () => Settings.ExpandedBuffWindow, v => Settings.ExpandedBuffWindow = v, true),
                Bool(Cat.Interface, gClassic, "ExpandedHeroBuffWindow", ClientTextKeys.ModernWinOptExpandedHeroBuffs, ClientTextKeys.ModernWinOptExpandedHeroBuffsDesc, () => Settings.ExpandedHeroBuffWindow, v => Settings.ExpandedHeroBuffWindow = v, true),
                // Gameplay
                Bool(Cat.Gameplay, gCombat, "TargetDead", ClientTextKeys.ModernWinOptTargetDead, ClientTextKeys.ModernWinOptTargetDeadDesc, () => Settings.TargetDead, v => Settings.TargetDead = v, false),
                Bool(Cat.Gameplay, gMove, "NewMove", ClientTextKeys.ModernWinOptNewMove, ClientTextKeys.ModernWinOptNewMoveDesc, () => Settings.NewMove,
                    v => { Settings.NewMove = v; ChatHint(v ? ClientTextKeys.NewMovementStyle : ClientTextKeys.OldMovementStyle); }, false),
                // Audio
                new Def { Id = "volume", Cat = Cat.Audio, Group = gVol, Label = ClientTextKeys.ModernWinOptSound, Desc = ClientTextKeys.ModernWinOptSoundDesc, Kind = Kind.Slider,
                    Min = 0, Max = 100, Step = 1, Unit = "%", DefI = 100, GetI = () => Settings.Volume, SetI = v => Settings.Volume = (byte)v },
                new Def { Id = "musicVolume", Cat = Cat.Audio, Group = gVol, Label = ClientTextKeys.ModernWinOptMusic, Desc = ClientTextKeys.ModernWinOptMusicDesc, Kind = Kind.Slider,
                    Min = 0, Max = 100, Step = 1, Unit = "%", DefI = 100, GetI = () => Settings.MusicVolume, SetI = v => Settings.MusicVolume = (byte)v },
                // Chat
                Bool(Cat.Chat, gTabs, "ShowNormalChat", ClientTextKeys.ModernWinOptTabLocal, ClientTextKeys.ModernWinOptTabLocalDesc, () => Settings.ShowNormalChat, v => Settings.ShowNormalChat = v, true),
                Bool(Cat.Chat, gTabs, "ShowWhisperChat", ClientTextKeys.ModernWinOptTabWhisper, ClientTextKeys.ModernWinOptTabWhisperDesc, () => Settings.ShowWhisperChat, v => Settings.ShowWhisperChat = v, true),
                Bool(Cat.Chat, gTabs, "ShowGroupChat", ClientTextKeys.ModernWinOptTabGroup, ClientTextKeys.ModernWinOptTabGroupDesc, () => Settings.ShowGroupChat, v => Settings.ShowGroupChat = v, true),
                Bool(Cat.Chat, gTabs, "ShowGuildChat", ClientTextKeys.ModernWinOptTabGuild, ClientTextKeys.ModernWinOptTabGuildDesc, () => Settings.ShowGuildChat, v => Settings.ShowGuildChat = v, true),
                Bool(Cat.Chat, gChan, "ShowYellChat", ClientTextKeys.ModernWinOptChanShout, ClientTextKeys.ModernWinOptChanShoutDesc, () => Settings.ShowYellChat, v => Settings.ShowYellChat = v, true),
                Bool(Cat.Chat, gChan, "ShowMentorChat", ClientTextKeys.ModernWinOptChanMentor, ClientTextKeys.ModernWinOptChanMentorDesc, () => Settings.ShowMentorChat, v => Settings.ShowMentorChat = v, true),
                Bool(Cat.Chat, gChan, "ShowLoverChat", ClientTextKeys.ModernWinOptChanLover, ClientTextKeys.ModernWinOptChanLoverDesc, () => Settings.ShowLoverChat, v => Settings.ShowLoverChat = v, true),
                Bool(Cat.Chat, gHide, "FilterNormalChat", ClientTextKeys.ModernWinOptHideSay, ClientTextKeys.ModernWinOptHideSayDesc, () => Settings.FilterNormalChat, v => Settings.FilterNormalChat = v, false),
                Bool(Cat.Chat, gHide, "FilterShoutChat", ClientTextKeys.ModernWinOptHideShout, ClientTextKeys.ModernWinOptHideShoutDesc, () => Settings.FilterShoutChat, v => Settings.FilterShoutChat = v, false),
                Bool(Cat.Chat, gHide, "FilterWhisperChat", ClientTextKeys.ModernWinOptHideWhisper, ClientTextKeys.ModernWinOptHideWhisperDesc, () => Settings.FilterWhisperChat, v => Settings.FilterWhisperChat = v, false),
                Bool(Cat.Chat, gHide, "FilterGroupChat", ClientTextKeys.ModernWinOptHideGroup, ClientTextKeys.ModernWinOptHideGroupDesc, () => Settings.FilterGroupChat, v => Settings.FilterGroupChat = v, false),
                Bool(Cat.Chat, gHide, "FilterGuildChat", ClientTextKeys.ModernWinOptHideGuild, ClientTextKeys.ModernWinOptHideGuildDesc, () => Settings.FilterGuildChat, v => Settings.FilterGuildChat = v, false),
                Bool(Cat.Chat, gHide, "FilterSystemChat", ClientTextKeys.ModernWinOptHideSystem, ClientTextKeys.ModernWinOptHideSystemDesc, () => Settings.FilterSystemChat, v => Settings.FilterSystemChat = v, false),
                // Social (the server's; S.AllowToggles)
                Allow(AllowToggle.Group, ClientTextKeys.ModernWinOptAllowGroup, ClientTextKeys.ModernWinOptAllowGroupDesc),
                Allow(AllowToggle.Trade, ClientTextKeys.ModernWinOptAllowTrade, ClientTextKeys.ModernWinOptAllowTradeDesc),
                Allow(AllowToggle.GuildInvite, ClientTextKeys.ModernWinOptAllowGuild, ClientTextKeys.ModernWinOptAllowGuildDesc),
                Allow(AllowToggle.FriendRequests, ClientTextKeys.ModernWinOptAllowFriends, ClientTextKeys.ModernWinOptAllowFriendsDesc),
                Allow(AllowToggle.GroupRecall, ClientTextKeys.ModernWinOptAllowRecall, ClientTextKeys.ModernWinOptAllowRecallDesc),
                Allow(AllowToggle.Marriage, ClientTextKeys.ModernWinOptAllowMarriage, ClientTextKeys.ModernWinOptAllowMarriageDesc),
                Allow(AllowToggle.LoverRecall, ClientTextKeys.ModernWinOptAllowLoverRecall, ClientTextKeys.ModernWinOptAllowLoverRecallDesc),
                Allow(AllowToggle.Mentor, ClientTextKeys.ModernWinOptAllowMentor, ClientTextKeys.ModernWinOptAllowMentorDesc),
                Allow(AllowToggle.Observe, ClientTextKeys.ModernWinOptAllowObserve, ClientTextKeys.ModernWinOptAllowObserveDesc),
            };
        }
    }
}
