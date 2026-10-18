using Client.MirControls;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// Phase 1b's kit test window (Desktop-Modern-UI-Plan "Phase 1b … done when"): every widget, the theme switch, the HUD
    /// scale, each popup kind and two item cells in one place, on three tabs. Opened by the options keybind in modern mode until
    /// the modern options window exists (phase 2). Test / showcase only: its labels are hard-coded test text, not game text
    /// (the widgets' own strings come from ClientTextKeys).
    /// </summary>
    public sealed class KitTestWindow : ModernWindow
    {
        public const string WindowKey = "kittest";

        // state that survives a rebuild (theme / scale change)
        private int _tab;
        private string _status = "Ready";
        private int _ringPercent = 40;
        private string _segment = "all";
        private readonly HashSet<string> _chips = new HashSet<string> { "Weapons", "Rings" };
        private bool _toggle = true;
        private int _slider = 35;
        private int _badge;

        private ModernLabel _statusLabel;
        private ModernPanel[] _pages;

        // test hooks (UiTestRunner.Modern.cs drives these)
        internal Tabs TabsControl;
        internal ModernButton MainButton, DisabledButton;
        internal ConfirmTwiceButton ConfirmButton;
        internal HoldButton HoldControl;
        internal Segmented<string> SegmentedControl;
        internal Chips<string> ChipsControl;
        internal ToggleSwitch Toggle, LockedToggle;
        internal Slider SliderControl;
        internal ModernProgressBar Bar;
        internal ProgressRing Ring;
        internal SearchBox Search, ListSearch;
        internal ListView<string> List;
        internal ModernItemCell Cell8, Cell9;
        internal string LastSearch = string.Empty, LastPopup = string.Empty;
        /// <summary>The scale and popup buttons by label (tests click them).</summary>
        internal readonly Dictionary<string, ModernButton> NamedButtons = new Dictionary<string, ModernButton>();

        private static readonly string[] AllRows = BuildRows();

        public KitTestWindow() : base(WindowKey, "Kit test", 40, 60)
        {
            ContentWidth = 560;
            ContentHeight = 420;
        }

        public string Status => _status;

        private static string[] BuildRows()
        {
            string[] names = { "Sword", "Dagger", "Ring", "Bracelet", "Necklace", "Helmet", "Armour", "Boots", "Belt", "Potion" };
            var rows = new string[50];
            for (int i = 0; i < rows.Length; i++) rows[i] = $"Row {i + 1:00} - test {names[i % names.Length]}";
            return rows;
        }

        private void SetStatus(string text)
        {
            _status = text ?? string.Empty;
            if (_statusLabel != null && !_statusLabel.IsDisposed) _statusLabel.Text = _status;
        }

        private static ModernLabel Section(MirControl parent, string text, int x, int y, int w)
        {
            return new ModernLabel
            {
                Parent = parent,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(14)),
                Text = text.ToUpperInvariant(),
                FontSize = 10,
                Bold = true,
                Colour = ModernTheme.Dim,
            };
        }

        private static T At<T>(T c, MirControl parent, int x, int y) where T : MirControl
        {
            c.Parent = parent;
            c.Location = new Point(S(x), S(y));
            return c;
        }

        protected override void Build()
        {
            int top = ContentTop;
            TabsControl = new Tabs(new[] { "Controls", "List", "Theme / popups / items" }, _tab, 532) { Parent = this, Location = new Point(S(14), top + S(8)) };
            TabsControl.Badge(1, _badge);
            TabsControl.Changed += i => ShowTab(i);

            _pages = new ModernPanel[3];
            for (int i = 0; i < _pages.Length; i++)
                _pages[i] = new ModernPanel { Parent = this, Location = new Point(S(14), top + S(44)), Size = new Size(S(532), S(344)), Visible = false };

            BuildControls(_pages[0]);
            BuildList(_pages[1]);
            BuildThemeAndPopups(_pages[2]);

            _statusLabel = new ModernLabel
            {
                Parent = this,
                Location = new Point(S(14), top + S(394)),
                Size = new Size(S(532), S(18)),
                FontSize = 11,
                Colour = ModernTheme.AccentDim,
                Text = _status,
            };
            ShowTab(_tab);
        }

        private void ShowTab(int i)
        {
            _tab = i;
            for (int p = 0; p < _pages.Length; p++) _pages[p].Visible = p == i;
        }

        // ---------------------------------------------------------------- tab 1: controls

        private void BuildControls(ModernPanel page)
        {
            Section(page, "Buttons", 0, 0, 250);
            var normal = At(new ModernButton("Normal"), page, 0, 18);
            normal.AutoWidth(70);
            normal.Click += (o, e) => SetStatus("Clicked: Normal");
            MainButton = At(new ModernButton("Main", ButtonKind.Main), page, S0(normal) + 6, 18);
            MainButton.AutoWidth(70);
            MainButton.Click += (o, e) => SetStatus("Clicked: Main");
            var warn = At(new ModernButton("Warn", ButtonKind.Warn), page, S0(MainButton) + 6, 18);
            warn.AutoWidth(70);
            warn.Click += (o, e) => SetStatus("Clicked: Warn");

            var big = At(new ModernButton("Big main", ButtonKind.Main, true), page, 0, 48);
            big.AutoWidth(100);
            big.Click += (o, e) => SetStatus("Clicked: Big");
            DisabledButton = At(new ModernButton("Disabled", ButtonKind.Normal, true), page, S0(big) + 6, 48);
            DisabledButton.AutoWidth(90);
            DisabledButton.SetDisabled(true, "Disabled: the reason shows as the hint");
            DisabledButton.Click += (o, e) => SetStatus("Disabled button clicked (should not happen)");

            ConfirmButton = At(new ConfirmTwiceButton("Delete item"), page, 0, 88);
            ConfirmButton.AutoWidth(110);
            ConfirmButton.Confirmed += () => SetStatus("Confirmed: Delete item");
            HoldControl = At(new HoldButton("Hold to awaken", true), page, 0, 116);
            HoldControl.AutoWidth(150);
            HoldControl.Done += () => SetStatus("Held: awaken");

            Section(page, "Segmented", 0, 150, 250);
            SegmentedControl = At(new Segmented<string>(new[]
            {
                new Choice<string>("all", "All"),
                new Choice<string>("mine", "Mine"),
                new Choice<string>("party", "Party", "3"),
            }, _segment, 250), page, 0, 166);
            SegmentedControl.Changed += v =>
            {
                _segment = v;
                SetStatus("Segment: " + v);
            };

            Section(page, "Chips", 0, 200, 250);
            ChipsControl = At(new Chips<string>(new[]
            {
                new Choice<string>("Weapons", "Weapons"),
                new Choice<string>("Armour", "Armour"),
                new Choice<string>("Rings", "Rings"),
                new Choice<string>("Potions", "Potions"),
                new Choice<string>("Scrolls", "Scrolls"),
                new Choice<string>("Gems", "Gems"),
            }, _chips, 250), page, 0, 216);
            ChipsControl.Changed += sel =>
            {
                _chips.Clear();
                foreach (string s in sel) _chips.Add(s);
                SetStatus("Chips: " + string.Join(", ", sel));
            };

            Toggle = new ToggleSwitch(_toggle);
            Toggle.Changed += on =>
            {
                _toggle = on;
                SetStatus("Toggle: " + (on ? "on" : "off"));
            };
            ModernRow.Create(page, 0, 270, 250, "Show names", Toggle);
            LockedToggle = new ToggleSwitch(false, true);
            LockedToggle.Lock(true, "Locked: needs a guild");
            ModernRow.Create(page, 0, 298, 250, "Locked switch", LockedToggle);

            // right column
            Section(page, "Slider", 276, 0, 256);
            SliderControl = At(new Slider(0, 100, _slider, 256, 5), page, 276, 18);
            SliderControl.Format = v => v + "%";
            SliderControl.Changed += v =>
            {
                _slider = v;
                Bar.Set(v / 100f, v + " / 100");
                SetStatus("Slider: " + v);
            };

            Section(page, "Progress", 276, 50, 256);
            Bar = At(new ModernProgressBar(256, 14), page, 276, 68);
            Bar.Set(_slider / 100f, _slider + " / 100");
            Ring = At(new ProgressRing(62, 6), page, 276, 92);
            Ring.Set(_ringPercent / 100f, _ringPercent + "%", "craft");
            var less = At(new ModernButton("-10%"), page, 350, 100);
            less.AutoWidth(56);
            less.Click += (o, e) => SetRing(_ringPercent - 10);
            var more = At(new ModernButton("+10%"), page, 350, 126);
            more.AutoWidth(56);
            more.Click += (o, e) => SetRing(_ringPercent + 10);
            var badge = At(new ModernButton("Badge +1"), page, 420, 100);
            badge.AutoWidth(80);
            badge.Click += (o, e) =>
            {
                _badge++;
                TabsControl.Badge(1, _badge);
                SetStatus("Badge: " + _badge);
            };

            Section(page, "Search", 276, 170, 256);
            Search = At(new SearchBox(256), page, 276, 188);
            Search.Search += q =>
            {
                LastSearch = q;
                SetStatus("Search: \"" + q + "\"");
            };
        }

        private static int S0(MirControl c) => (int)Math.Round((c.Location.X + c.Size.Width) / ModernRoot.K);

        private void SetRing(int percent)
        {
            _ringPercent = Math.Clamp(percent, 0, 100);
            Ring.Set(_ringPercent / 100f, _ringPercent + "%", "craft");
            SetStatus("Ring: " + _ringPercent + "%");
        }

        // ---------------------------------------------------------------- tab 2: list

        private void BuildList(ModernPanel page)
        {
            Section(page, "Filter", 0, 0, 260);
            ListSearch = At(new SearchBox(260, "Filter rows"), page, 0, 18);
            List = At(new ListView<string>(532, 280, 24, () => new TextListRow<string>(s => s)), page, 0, 52);
            List.SetItems(AllRows);
            List.SelectionChanged += (i, s) => SetStatus("Selected: " + s);
            List.Activated += (i, s) => SetStatus("Activated: " + s);
            ListSearch.Search += q =>
            {
                if (q.Length == 0)
                {
                    List.SetItems(AllRows);
                }
                else
                {
                    var hits = new List<string>();
                    foreach (string r in AllRows)
                        if (r.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(r);
                    List.SetItems(hits);
                }
                SetStatus("Filter: \"" + q + "\" -> " + List.Items.Count + " rows");
            };
        }

        // ---------------------------------------------------------------- tab 3: theme, scale, popups, items

        private void BuildThemeAndPopups(ModernPanel page)
        {
            Section(page, "Theme", 0, 0, 532);
            var themes = new List<Choice<string>>();
            string[] names = { "Midnight electric", "Abyss", "Neon", "Royal gold", "Lost Ark red", "Emerald", "Amethyst", "Ember", "Obsidian" };
            foreach (string n in names) themes.Add(new Choice<string>(n, n));
            var themeChips = At(new Chips<string>(themes, new[] { ModernTheme.Name }, 532, false, false), page, 0, 18);
            themeChips.Changed += sel =>
            {
                foreach (string n in sel)
                {
                    _status = "Theme: " + n;
                    Settings.UiTheme = n;
                    ModernTheme.Apply(n, null); // rebuilds this window (WindowManager.OnThemeChanged)
                }
            };

            Section(page, "HUD scale", 0, 76, 532);
            int x = 0;
            foreach ((string label, float scale, bool auto) in new[] { ("0.75x", 0.75f, false), ("1x", 1f, false), ("1.25x", 1.25f, false), ("1.5x", 1.5f, false), ("Auto", 1f, true) })
            {
                bool on = auto ? Settings.UiAutoScale : !Settings.UiAutoScale && Math.Abs(Settings.UiScale - scale) < 0.01f;
                var b = At(new ModernButton(label, on ? ButtonKind.Main : ButtonKind.Normal), page, x, 94);
                NamedButtons[label] = b;
                b.AutoWidth(60);
                b.Click += (o, e) =>
                {
                    _status = "Scale: " + label;
                    Settings.UiScale = scale;
                    Settings.UiAutoScale = auto;
                    ModernRoot.Instance?.ScreenChanged(); // rebuilds this window at the new scale
                };
                x = S0(b) + 6;
            }

            Section(page, "Popups", 0, 134, 532);
            Popups pop = ModernRoot.Instance?.Popups;
            x = 0;
            void PopupButton(string label, Action open)
            {
                var b = At(new ModernButton(label), page, x, 152);
                NamedButtons[label] = b;
                b.AutoWidth(60);
                b.Click += (o, e) => open();
                x = S0(b) + 6;
            }
            PopupButton("Confirm", () => pop?.Confirm("Drop the Sword of Testing? It will vanish from the ground in 60 seconds.", PopupButtons.OkCancel, PopupKind.Warning,
                (a, dont) => Popup("confirm: " + a + (dont ? " (don't ask)" : "")), true, "Drop item"));
            PopupButton("Danger", () => pop?.Confirm("Destroy this item for good? This cannot be undone.", PopupButtons.YesNo, PopupKind.Danger,
                (a, _) => Popup("danger: " + a), false, "Destroy item", 1000));
            PopupButton("Ask", () => pop?.Ask("Join the group of Tester?", yes => Popup("ask: " + yes)));
            PopupButton("Input", () => pop?.Input("Rename", "Tester", 14, s => Popup("input: " + (s ?? "(cancelled)")), "A new name for your creature."));
            PopupButton("Amount", () => pop?.Amount("Split stack", Cell8?.Item?.Info?.Image ?? 1, 20, n => Popup("amount: " + n), 10, 1, () => Popup("amount: (cancelled)")));
            PopupButton("Gold", () => pop?.Amount("Drop gold", -1, 100000, n => Popup("gold: " + n), 5000, 0, () => Popup("gold: (cancelled)")));
            PopupButton("Request", () => pop?.Request("Tester", "invites you to a group.", 20, () => Popup("request: accepted"), () => Popup("request: declined")));

            Section(page, "Item cells (inventory 8, 9)", 0, 194, 532);
            Cell8 = At(new ModernItemCell(MirGridType.Inventory, 8), page, 0, 212);
            Cell9 = At(new ModernItemCell(MirGridType.Inventory, 9), page, 46, 212);
            new ModernLabel
            {
                Parent = page,
                Location = new Point(S(96), S(212)),
                Size = new Size(S(430), S(40)),
                FontSize = 11,
                Colour = ModernTheme.Dim,
                Wrap = true,
                Text = "Left click picks up / drops (ItemMoves), right click uses, hover shows the tooltip (hold Shift to compare with the worn item).",
            };
        }

        private void Popup(string text)
        {
            LastPopup = text;
            SetStatus("Popup " + text);
        }
    }
}
