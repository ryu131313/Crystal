// Ported from WebClient/src/ui/modern/hud/gameMenu.ts: the pause-style game menu, a modern take on the desktop's MenuDialog,
// opened by Esc with no window open (the Closeall bind reaching ModernUi.HudKeyBind) or the game menu buttons of the HUD.
// Left: the character card (name, level, class, guild, map, session time) and the actions (Resume, Options, Help, Log out,
// Switch to classic interface). Right: a searchable launcher of every registered window with its hotkey and badge (unread
// mail). Windows that are not registered yet (later phases) have no tile (WindowManager.IsRegistered).
// Log out: HudActions.LogOut (GameScene.LogOut refuses inside the 10 s combat lock, else a Yes / No popup). Not ported (as on
// the web): the online count and ping (no packet), the crafting tile (an NPC page). Exit to desktop is not on the web menu
// either (the desktop keeps it on its Exit bind). The clock and the session time tick once a second only while open
// (HudRig.Process calls Tick: the Menu layer has no per-frame hook of its own).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Hud
{
    /// <summary>A window the menu strip / launcher can open (gameMenu.ts MenuWindow): registry key, name, icons, toggle bind.</summary>
    internal sealed class MenuEntry
    {
        public readonly string Key;
        public readonly ClientTextKeys Name;
        public readonly int Icon, Emblem;
        public readonly KeybindOptions? Bind;

        private MenuEntry(string key, ClientTextKeys name, int icon, int emblem, KeybindOptions? bind)
        {
            Key = key;
            Name = name;
            Icon = icon;
            Emblem = emblem;
            Bind = bind;
        }

        /// <summary>Every window of the strip and the launcher, strip order first (desktop registry keys: "bigmap" is lower case).</summary>
        public static readonly MenuEntry[] All =
        {
            new MenuEntry("inventory", ClientTextKeys.ModernHudWinBag, ModernIcons.Bag, ModernIcons.EmblemBag, KeybindOptions.Inventory),
            new MenuEntry("character", ClientTextKeys.ModernHudWinCharacter, ModernIcons.Person, ModernIcons.EmblemCharacter, KeybindOptions.Equipment),
            new MenuEntry("skills", ClientTextKeys.ModernHudWinSkills, ModernIcons.Star, ModernIcons.EmblemSkills, KeybindOptions.Skills),
            new MenuEntry("quests", ClientTextKeys.ModernHudWinQuests, ModernIcons.Scroll, ModernIcons.EmblemQuests, KeybindOptions.Quests),
            new MenuEntry("guild", ClientTextKeys.ModernHudWinGuild, ModernIcons.Guild, ModernIcons.EmblemGuild, KeybindOptions.Guilds),
            new MenuEntry("group", ClientTextKeys.ModernHudWinGroup, ModernIcons.Group, ModernIcons.EmblemGroup, KeybindOptions.Group),
            new MenuEntry("friends", ClientTextKeys.ModernHudWinFriends, ModernIcons.Friends, ModernIcons.EmblemFriends, KeybindOptions.Friends),
            new MenuEntry("mail", ClientTextKeys.ModernHudWinMail, ModernIcons.Mail, ModernIcons.EmblemMail, null),
            new MenuEntry("gameShop", ClientTextKeys.ModernHudWinGameShop, ModernIcons.Shop, ModernIcons.EmblemGameShop, KeybindOptions.GameShop),
            new MenuEntry("bigmap", ClientTextKeys.ModernHudWinWorldMap, ModernIcons.Map, ModernIcons.EmblemWorldMap, KeybindOptions.Bigmap),
            new MenuEntry("options", ClientTextKeys.ModernHudWinOptions, ModernIcons.Gear, ModernIcons.EmblemOptions, KeybindOptions.Options),
            new MenuEntry("hero", ClientTextKeys.ModernHudWinHero, ModernIcons.Hero, ModernIcons.EmblemHero, KeybindOptions.HeroInventory),
            new MenuEntry("ranking", ClientTextKeys.ModernHudWinRanking, ModernIcons.Ranking, ModernIcons.EmblemRanking, KeybindOptions.Ranking),
            new MenuEntry("territories", ClientTextKeys.ModernHudWinTerritories, ModernIcons.Flag, ModernIcons.EmblemTerritories, null),
            new MenuEntry("creatures", ClientTextKeys.ModernHudWinCreatures, ModernIcons.Creature, ModernIcons.EmblemCreatures, KeybindOptions.Creature),
            new MenuEntry("mount", ClientTextKeys.ModernHudWinMount, ModernIcons.Mount, ModernIcons.EmblemMount, KeybindOptions.MountWindow),
            new MenuEntry("fishing", ClientTextKeys.ModernHudWinFishing, ModernIcons.Fishing, ModernIcons.EmblemFishing, KeybindOptions.Fishing),
            new MenuEntry("mentor", ClientTextKeys.ModernHudWinMentor, ModernIcons.Mentor, ModernIcons.EmblemMentor, KeybindOptions.Mentor),
            new MenuEntry("relationship", ClientTextKeys.ModernHudWinRelationship, ModernIcons.Heart, ModernIcons.EmblemRelationship, KeybindOptions.Relationship),
            new MenuEntry("keybinds", ClientTextKeys.ModernHudWinKeybinds, ModernIcons.Keyboard, ModernIcons.EmblemKeyBindings, KeybindOptions.Keybind),
            new MenuEntry("help", ClientTextKeys.ModernHudWinHelp, ModernIcons.Help, ModernIcons.EmblemHelp, KeybindOptions.Help),
        };

        public static MenuEntry Find(string key)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Key == key) return All[i];
            return null;
        }

        /// <summary>Whether a keybind toggles one of the menu's windows (the menu closes and the key carries on).</summary>
        public static bool IsWindowBind(KeybindOptions f)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Bind == f) return true;
            return f == KeybindOptions.Inventory2 || f == KeybindOptions.Equipment2 || f == KeybindOptions.Skills2 || f == KeybindOptions.Options2;
        }
    }

    internal sealed class GameMenu : ModernSurface
    {
        private readonly long _started = CMain.Time;
        private readonly List<ActRow> _acts = new List<ActRow>();
        private readonly List<LauncherTile> _tiles = new List<LauncherTile>();
        private int _sel;
        private SearchBox _search;
        private CharacterCard _card;
        private ModernLabel _clock;
        private long _nextTick;
        private string _clockText = string.Empty;
        private long _sessionShown = -1;

        private readonly Action<bool> _onRelayout;

        public GameMenu()
        {
            Visible = false;
            _onRelayout = _ => { if (IsOpen) Build(); };
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            base.Dispose(disposing);
        }

        public bool IsOpen => Visible && !IsDisposed;

        public void Toggle()
        {
            if (IsOpen) Close();
            else Show();
        }

        public new void Show()
        {
            ModernRoot root = ModernRoot.Instance;
            if (root == null || IsDisposed) return;
            if (Parent != root.Menu) Parent = root.Menu;
            Build();
            _sel = 0;
            Mark();
            Visible = true;
            BringToFront();
            Tick(true);
        }

        public void Close()
        {
            if (!Visible) return;
            Visible = false;
            _search?.Blur();
            if (ActiveControl != null && IsChild(ActiveControl)) ActiveControl = null;
        }

        private bool IsChild(MirControl c)
        {
            for (MirControl p = c; p != null; p = p.Parent)
                if (p == this) return true;
            return false;
        }

        /// <summary>Once a second while open: the session time and the clock (called per frame from HudRig.Process).</summary>
        public void Tick(bool force = false)
        {
            if (!IsOpen || (!force && CMain.Time < _nextTick)) return;
            _nextTick = CMain.Time + 1000;
            long secs = (CMain.Time - _started) / 1000;
            if (secs != _sessionShown)
            {
                _sessionShown = secs;
                _card?.SetSession(HudKit.T(ClientTextKeys.ModernHudMenuSession, HudKit.FmtDuration(secs)));
            }
            string clock = CMain.Now.ToString("HH:mm");
            if (clock != _clockText)
            {
                _clockText = clock;
                if (_clock != null) _clock.Text = HudKit.T(ClientTextKeys.ModernHudMenuLocalTime, clock);
            }
        }

        // ---- build ----

        private void Build()
        {
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            _acts.Clear();
            _tiles.Clear();
            _sessionShown = -1;
            _clockText = string.Empty;
            Location = Point.Empty;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            new MenuVeil(this) { Parent = this, Size = Size };

            WindowManager wm = ModernRoot.Instance?.Wm;
            UserObject u = GameScene.User;

            // the action list first (its height sets both columns)
            var acts = new List<(string Label, string Hint, int Emblem, int Icon, bool Leave, Action Run)>
            {
                (HudKit.T(ClientTextKeys.ModernHudMenuResume), HudKit.T(ClientTextKeys.ModernWinCapEsc), -1, ModernIcons.Play, false, Close),
            };
            void Win(string key, KeybindOptions bind)
            {
                MenuEntry e = MenuEntry.Find(key);
                if (e == null || wm == null || !wm.IsRegistered(key)) return;
                acts.Add((HudKit.T(e.Name), HudKit.KeyLabel(bind), e.Emblem, e.Icon, false, () => { Close(); ModernRoot.Instance?.Wm.Open(key); }));
            }
            Win("options", KeybindOptions.Options);
            Win("help", KeybindOptions.Help);
            int sepAfter = acts.Count;
            acts.Add((HudKit.T(ClientTextKeys.ModernHudLogOut), HudKit.KeyLabel(KeybindOptions.Logout), -1, ModernIcons.Logout, true, () => { Close(); HudActions.LogOut(); }));
            acts.Add((HudKit.T(ClientTextKeys.ModernHudMenuSwitchClassic), string.Empty, -1, ModernIcons.Swap, false, () => { Close(); HudActions.SwitchToClassic(); }));

            const int colL = 280, colR = 470, gapCols = 18, logoH = 40, gap = 12, cardH = 76, actH = 40, actGap = 3, actPad = 8;
            int actsH = actPad * 2 + acts.Count * actH + (acts.Count - 1) * actGap + 12;
            int leftH = logoH + gap + cardH + gap + actsH;

            // tiles (right column)
            var entries = new List<MenuEntry>();
            if (wm != null)
                for (int i = 0; i < MenuEntry.All.Length; i++)
                {
                    MenuEntry e = MenuEntry.All[i];
                    if (e.Key == "help" || !wm.IsRegistered(e.Key)) continue; // help is in the action list
                    entries.Add(e);
                }
            const int tilePad = 14, tileGap = 8, tileH = 74, headH = 28;
            int tileW = (colR - tilePad * 2 - tileGap * 4) / 5;
            int rows = Math.Max(1, (entries.Count + 4) / 5);
            int launchNatural = tilePad + headH + 10 + rows * tileH + (rows - 1) * tileGap + 30 + tilePad;
            int rightH = logoH + gap + launchNatural;
            int total = Math.Max(leftH, rightH);
            int launchH = total - logoH - gap;
            actsH = total - logoH - gap - cardH - gap;

            int totalW = colL + gapCols + colR;
            int x0 = (int)Math.Round((Settings.ScreenWidth / ModernRoot.K - totalW) / 2f);
            int y0 = Math.Max(8, (int)Math.Round((Settings.ScreenHeight / ModernRoot.K - total) / 2f));
            int xr = x0 + colL + gapCols;

            // logo
            new MenuLogo { Parent = this, Location = new Point(S(x0), S(y0)), Size = new Size(S(colL), S(logoH)) };

            // character card
            _card = new CharacterCard(u, GameScene.Scene?.MapControl?.Title ?? string.Empty) { Parent = this, Location = new Point(S(x0), S(y0 + logoH + gap)), Size = new Size(S(colL), S(cardH)) };

            // actions
            int ay = y0 + logoH + gap + cardH + gap;
            new MenuPanel { Parent = this, Location = new Point(S(x0), S(ay)), Size = new Size(S(colL), S(actsH)) };
            int y = ay + actPad;
            for (int i = 0; i < acts.Count; i++)
            {
                if (i == sepAfter)
                {
                    // the separator sits at the bottom group (margin: auto 6px 4px)
                    int bottomCount = acts.Count - sepAfter;
                    int bottomY = ay + actsH - actPad - bottomCount * actH - (bottomCount - 1) * actGap;
                    new MenuSeparator { Parent = this, Location = new Point(S(x0 + 6), S(bottomY - 5)), Size = new Size(S(colL - 12), Math.Max(1, S(1))) };
                    y = bottomY;
                }
                var (label, hint, emblem, icon, leave, run) = acts[i];
                int index = i;
                var row = new ActRow(label, hint, emblem, icon, leave) { Parent = this, Location = new Point(S(x0 + actPad), S(y)), Size = new Size(S(colL - actPad * 2), S(actH)) };
                row.Click += (o, e) => Run(index);
                row.MouseEnter += (o, e) => { _sel = index; Mark(); };
                row.Run = run;
                _acts.Add(row);
                y += actH + actGap;
            }

            // launcher
            int ly = y0 + logoH + gap;
            new MenuPanel { Parent = this, Location = new Point(S(xr), S(ly)), Size = new Size(S(colR), S(launchH)) };
            new ModernLabel { Parent = this, Location = new Point(S(xr + tilePad), S(ly + tilePad)), Size = new Size(S(160), S(24)),
                Text = HudKit.T(ClientTextKeys.ModernHudMenuWindows), FontSize = 11, Bold = true, Colour = ModernTheme.Accent };
            _search = new SearchBox(190, HudKit.T(ClientTextKeys.ModernHudMenuFind)) { Parent = this };
            _search.Location = new Point(S(xr + colR - tilePad - 190), S(ly + tilePad + 2));
            _search.Search += Filter;
            _search.Submitted += _ => LaunchFirst();
            int ty = ly + tilePad + headH + 10;
            int unread = 0;
            if (u?.Mail != null) for (int i = 0; i < u.Mail.Count; i++) if (!u.Mail[i].Opened) unread++;
            string unreadText = unread <= 0 ? string.Empty : unread > 9 ? "9+" : unread.ToString();
            for (int i = 0; i < entries.Count; i++)
            {
                MenuEntry e = entries[i];
                int col = i % 5, row = i / 5;
                var tile = new LauncherTile(e, e.Key == "mail" ? unreadText : string.Empty)
                {
                    Parent = this,
                    Location = new Point(S(xr + tilePad + col * (tileW + tileGap)), S(ty + row * (tileH + tileGap))),
                    Size = new Size(S(tileW), S(tileH)),
                };
                tile.Click += (o, a) => Launch(e);
                _tiles.Add(tile);
            }
            if (entries.Count == 0)
                new ModernLabel { Parent = this, Location = new Point(S(xr + tilePad), S(ty)), Size = new Size(S(colR - tilePad * 2), S(20)),
                    Text = HudKit.T(ClientTextKeys.ModernHudMenuNoWindows), FontSize = 11, Colour = ModernTheme.Dim };
            _clock = new ModernLabel { Parent = this, Location = new Point(S(xr + tilePad + 4), S(ly + launchH - tilePad - 18)), Size = new Size(S(200), S(18)),
                FontSize = 10.5f, Colour = ModernTheme.Dim };
            TextureValid = false;
        }

        private void Filter(string q)
        {
            bool first = true;
            for (int i = 0; i < _tiles.Count; i++)
            {
                LauncherTile t = _tiles[i];
                bool m = q.Length == 0 || t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                t.SetFilter(!m, q.Length > 0 && m && first);
                if (q.Length > 0 && m) first = false;
            }
        }

        private void LaunchFirst()
        {
            LauncherTile hit = null;
            for (int i = 0; i < _tiles.Count && hit == null; i++) if (_tiles[i].Hit) hit = _tiles[i];
            for (int i = 0; i < _tiles.Count && hit == null; i++) if (!_tiles[i].Dimmed) hit = _tiles[i];
            if (hit != null) Launch(hit.Entry);
        }

        private void Launch(MenuEntry e)
        {
            Close();
            ModernRoot.Instance?.Wm.Open(e.Key);
        }

        private void Mark()
        {
            for (int i = 0; i < _acts.Count; i++) _acts[i].On = i == _sel;
        }

        private void Run(int i)
        {
            if (i >= 0 && i < _acts.Count) _acts[i].Run?.Invoke();
        }

        // ---- keys: Esc resumes, Up / Down / Enter work the actions (children first, so these come before the game's binds) ----

        public override void OnKeyDown(KeyEventArgs e)
        {
            if (!IsOpen || ModernRoot.Instance?.Popups?.IsOpen == true || ModernRoot.Instance?.ModalPromptOpen == true)
            {
                base.OnKeyDown(e);
                return;
            }
            bool typing = _search != null && _search.Focused;
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                case Keys.Down:
                case Keys.Up:
                    if (typing) break;
                    if (_acts.Count > 0)
                    {
                        _sel = (_sel + (e.KeyCode == Keys.Down ? 1 : _acts.Count - 1)) % _acts.Count;
                        Mark();
                    }
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                case Keys.Enter:
                    if (typing) break;
                    Run(_sel);
                    e.Handled = true;
                    e.SuppressKeyPress = true; // its '\r' must not open the chat input after the action ran (Resume closes the menu)
                    return;
            }
            base.OnKeyDown(e);
        }

        // ---- test hooks ----
        internal int ActCount => _acts.Count;
        internal int TileCount => _tiles.Count;
        internal int Selected => _sel;
        internal SearchBox SearchField => _search;
        internal ActRow Act(int i) => i >= 0 && i < _acts.Count ? _acts[i] : null;
        internal LauncherTile Tile(string key)
        {
            for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].Entry.Key == key) return _tiles[i];
            return null;
        }
    }

    /// <summary>The veil (.gmenu: a plain darker radial fill over the world, no blur); a press on it resumes.</summary>
    internal sealed class MenuVeil : ModernControl
    {
        private readonly GameMenu _menu;

        public MenuVeil(GameMenu menu)
        {
            _menu = menu;
        }

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _menu.Close();
        }

        protected override void Paint(Graphics g)
        {
            float w = Size.Width, h = Size.Height;
            using (var b = new SolidBrush(HudKit.Sq(Color.FromArgb(224, 2, 4, 10)))) g.FillRectangle(b, 0, 0, w, h);
            using var path = new GraphicsPath();
            path.AddEllipse(w * 0.4f - w * 0.75f, h * 0.5f - h * 0.85f, w * 1.5f, h * 1.7f);
            using var brush = new PathGradientBrush(path)
            {
                CenterPoint = new PointF(w * 0.4f, h * 0.5f),
                CenterColor = HudKit.Sq(Color.FromArgb(158, 6, 12, 24)),
                SurroundColors = new[] { HudKit.Sq(Color.FromArgb(224, 2, 4, 10)) },
            };
            // the centre is lighter than the plain fill: repaint it (Copy keeps the exact alpha instead of blending over)
            CompositingMode old = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillPath(brush, path);
            g.CompositingMode = old;
        }
    }

    /// <summary>The logo over the left column ("CRYSTAL", "LEGEND OF MIR").</summary>
    internal sealed class MenuLogo : ModernControl
    {
        public MenuLogo()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width;
            Font f = ModernSkin.Font(22, true);
            string title = HudKit.T(ClientTextKeys.ModernHudLogoTitle);
            for (int i = 4; i >= 1; i--)
                ModernSkin.SpacedText(g, title, f, ModernTheme.Alpha(ModernTheme.Accent, 0.07f * ModernTheme.Glow), new RectangleF(-i * k, -i * k, w + 2 * i * k, 28 * k + 2 * i * k), 11 * k);
            ModernSkin.SpacedText(g, title, f, Color.White, new RectangleF(0, 0, w, 28 * k), 11 * k);
            ModernSkin.SpacedText(g, HudKit.T(ClientTextKeys.ModernHudLogoSub), ModernSkin.Font(9, true), ModernTheme.Accent, new RectangleF(0, 28 * k, w, 12 * k), 3.6f * k);
        }
    }

    /// <summary>The menu's panels (.card, .acts, .launch): pc→pb gradient, frame, black ring, a top highlight.</summary>
    internal class MenuPanel : ModernControl
    {
        protected override void Paint(Graphics g) => PaintPanel(g, Size, K);

        public static void PaintPanel(Graphics g, Size size, float k)
        {
            float rad = (ModernTheme.Radius + 2) * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            RectangleF inner = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1)) ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(inner, rad);
            Color top = HudKit.Sq(ModernSkin.Opaque(ModernTheme.PanelC), 0.88f);
            Color bottom = HudKit.Sq(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.92f), 0.92f);
            ModernSkin.FillVertical(g, p, inner, new[] { top, bottom });
            using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.FrameLight, 0.25f), 1f)) g.DrawLine(pen, inner.X + rad, inner.Y + 1, inner.Right - rad, inner.Y + 1);
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
        }
    }

    internal sealed class MenuSeparator : ModernControl
    {
        public MenuSeparator()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g) => ModernSkin.FadedLine(g, 0, 0, Size.Width, Size.Height, ModernTheme.Frame);
    }

    /// <summary>The character card: class avatar, name, "Level N Class · guild", "map · session …".</summary>
    internal sealed class CharacterCard : MenuPanel
    {
        private readonly string _name, _line2, _guild, _map;
        private readonly MirClass _cls;
        private string _session = string.Empty;

        public CharacterCard(UserObject u, string map)
        {
            _cls = u?.Class ?? MirClass.Warrior;
            _name = u?.Name ?? string.Empty;
            _line2 = HudKit.T(ClientTextKeys.ModernHudMenuLevelClass, u?.Level ?? 0, _cls.ToLocalizedString());
            _guild = u?.GuildName ?? string.Empty;
            _map = string.IsNullOrEmpty(map) ? "…" : map;
        }

        public void SetSession(string s)
        {
            if (s == _session) return;
            _session = s;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            base.Paint(g);
            float k = K, h = Size.Height;
            // ::before: an accent glow from the left edge
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-Size.Width * 0.5f, -h * 0.4f, Size.Width, h * 1.8f);
                using var b = new PathGradientBrush(path) { CenterPoint = new PointF(0, h / 2f), CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.18f), SurroundColors = new[] { Color.Transparent } };
                Region old = g.Clip;
                using (GraphicsPath clip = ModernSkin.RoundRect(new RectangleF(1.5f, 1.5f, Size.Width - 3f, h - 3f), (ModernTheme.Radius + 2) * k)) g.SetClip(clip, CombineMode.Intersect);
                g.FillPath(b, path);
                g.Clip = old;
                old.Dispose();
            }
            Color cls = ModernTheme.Class(_cls);
            RectangleF av = new RectangleF(16 * k, (h - 48 * k) / 2f, 48 * k, 48 * k);
            using (var glow = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.12f))) g.FillEllipse(glow, RectangleF.Inflate(av, 5 * k, 5 * k));
            using (var bp = new GraphicsPath())
            {
                bp.AddEllipse(av);
                using var b = new PathGradientBrush(bp) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) },
                    CenterPoint = new PointF(av.X + av.Width / 2f, av.Y + av.Height * 0.35f) };
                g.FillEllipse(b, av);
            }
            using (var pen = new Pen(cls, 2 * k)) g.DrawEllipse(pen, av);
            ModernSkin.Text(g, HudKit.ClassTag(_cls), ModernSkin.Font(13, true), Color.White, av, StringAlignment.Center, StringAlignment.Center, false);
            float x = av.Right + 12 * k, w = Size.Width - x - 12 * k, y = (h - 50 * k) / 2f;
            ModernSkin.Text(g, _name, ModernSkin.Font(15, true), Color.White, new RectangleF(x, y, w, 20 * k));
            Font small = ModernSkin.Font(11);
            if (_guild.Length > 0)
            {
                string first = _line2 + " · ";
                float fw = ModernSkin.Measure(first, small);
                ModernSkin.Text(g, first, small, ModernTheme.Dim, new RectangleF(x, y + 20 * k, Math.Min(w, fw + 2), 16 * k));
                ModernSkin.Text(g, _guild, small, ModernTheme.Accent, new RectangleF(x + fw, y + 20 * k, Math.Max(0, w - fw), 16 * k));
            }
            else ModernSkin.Text(g, _line2, small, ModernTheme.Dim, new RectangleF(x, y + 20 * k, w, 16 * k));
            ModernSkin.Text(g, _map + " · " + _session, small, ModernTheme.Dim, new RectangleF(x, y + 36 * k, w, 16 * k));
        }
    }

    /// <summary>An action row (.act): emblem or stroke icon, label, key hint; "on" = the keyboard / hover selection.</summary>
    internal sealed class ActRow : ModernControl
    {
        private readonly string _label, _hint;
        private readonly int _emblem, _icon;
        private readonly bool _leave;
        private bool _on;
        public Action Run;

        public ActRow(string label, string hint, int emblem, int icon, bool leave)
        {
            _label = label ?? string.Empty;
            _hint = hint ?? string.Empty;
            _emblem = emblem;
            _icon = icon;
            _leave = leave;
            Sound = MirSounds.SoundList.ButtonA;
        }

        public string Label => _label;

        public bool On
        {
            get => _on;
            set
            {
                if (_on == value) return;
                _on = value;
                Invalidate();
            }
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height, rad = ModernTheme.Radius * k;
            if (_on)
            {
                Color c = _leave ? ModernTheme.Bad : ModernTheme.Accent;
                using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), rad);
                using (var b = new LinearGradientBrush(new RectangleF(0, 0, w, h), ModernTheme.Alpha(c, _leave ? 0.28f : 0.3f), Color.Transparent, LinearGradientMode.Horizontal)) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Mix(c, ModernTheme.Frame, 0.55f));
                using (var b = new SolidBrush(c)) g.FillRectangle(b, 1, 3 * k, 3 * k, h - 6 * k);
            }
            float x = (_on ? 16 : 12) * k + 22 * k + 12 * k;
            float hintW = _hint.Length > 0 ? ModernSkin.Measure(_hint, ModernSkin.Font(10)) + 30 * k : 8 * k;
            ModernSkin.Text(g, _label, ModernSkin.Font(13, true), _on ? Color.White : ModernTheme.Text, new RectangleF(x, 0, w - x - hintW, h));
            if (_hint.Length > 0)
            {
                Font f = ModernSkin.Font(10);
                float hw = ModernSkin.Measure(_hint, f) + 12 * k;
                RectangleF r = new RectangleF(w - 12 * k - hw, h / 2f - 9 * k, hw, 18 * k);
                using (GraphicsPath p = ModernSkin.RoundRect(r, 4 * k)) ModernSkin.Stroke(g, p, ModernTheme.Frame);
                ModernSkin.Text(g, _hint, f, ModernTheme.Dim, r, StringAlignment.Center, StringAlignment.Center, false);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            float k = K, x = DisplayLocation.X + (_on ? 16 : 12) * k;
            if (_emblem >= 0)
            {
                float s = (_on ? 23.8f : 22f) * k;
                HudKit.DrawScaled(Libraries.ModernIcons, _emblem, new RectangleF(x + (22 * k - s) / 2f, DisplayLocation.Y + (Size.Height - s) / 2f, s, s), Color.White);
            }
            else
            {
                float s = 18 * k;
                HudKit.DrawIcon(_icon, new RectangleF(x + 2 * k, DisplayLocation.Y + (Size.Height - s) / 2f, s, s), _on ? ModernTheme.Accent : ModernTheme.AccentDim);
            }
        }
    }

    /// <summary>A launcher tile (.tile): emblem, name, hotkey (top right), badge (top left); dimmed by the search, "hit" = Enter's pick.</summary>
    internal sealed class LauncherTile : ModernControl
    {
        public readonly MenuEntry Entry;
        public readonly string Name;
        private readonly string _key, _badge;
        private bool _dim, _hit;

        public LauncherTile(MenuEntry entry, string badge)
        {
            Entry = entry;
            Name = HudKit.T(entry.Name);
            _key = entry.Bind.HasValue ? HudKit.KeyLabel(entry.Bind.Value) : string.Empty;
            _badge = badge ?? string.Empty;
            Hint = _key.Length > 0 ? HudKit.T(ClientTextKeys.ModernHudWindowKey, Name, _key) : Name;
            Sound = MirSounds.SoundList.ButtonA;
        }

        public bool Dimmed => _dim;
        public bool Hit => _hit;

        public void SetFilter(bool dim, bool hit)
        {
            if (dim == _dim && hit == _hit) return;
            _dim = dim;
            _hit = hit;
            Opacity = dim ? 0.25f : 1f;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height, rad = ModernTheme.Radius * k;
            bool hot = Hovered || _hit;
            using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), rad);
            using (var bp = new GraphicsPath())
            {
                bp.AddEllipse(w * 0.1f, -h * 0.4f, w * 0.8f, h * 1.4f);
                using var b = new PathGradientBrush(bp) { CenterColor = ModernTheme.Alpha(ModernTheme.PanelC, 0.7f), SurroundColors = new[] { Color.Transparent },
                    CenterPoint = new PointF(w / 2f, h * 0.3f) };
                g.FillPath(b, p);
            }
            ModernSkin.Stroke(g, p, hot ? ModernTheme.Accent : ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
            ModernSkin.Text(g, Name, ModernSkin.Font(11, true), ModernTheme.Text, new RectangleF(2, h - 26 * k, w - 4, 16 * k), StringAlignment.Center, StringAlignment.Center);
            if (_key.Length > 0)
                ModernSkin.Text(g, _key, ModernSkin.Font(9), ModernTheme.Dim, new RectangleF(4 * k, 3 * k, w - 9 * k, 12 * k), StringAlignment.Far, StringAlignment.Near, false);
            if (_badge.Length > 0)
            {
                Font f = ModernSkin.Font(9, true);
                float bw = Math.Max(14 * k, ModernSkin.Measure(_badge, f) + 6 * k);
                RectangleF r = new RectangleF(5 * k, 4 * k, bw, 14 * k);
                using (GraphicsPath bp = ModernSkin.RoundRect(r, 7 * k))
                using (var b = new SolidBrush(ModernTheme.Accent)) g.FillPath(b, bp);
                ModernSkin.Text(g, _badge, f, Color.White, r, StringAlignment.Center, StringAlignment.Center, false);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            float k = K, s = ((Hovered || _hit) ? 36.7f : 34f) * k;
            float cy = DisplayLocation.Y + Size.Height / 2f - 9 * k - ((Hovered || _hit) ? 1 * k : 0);
            HudKit.DrawScaled(Libraries.ModernIcons, Entry.Emblem, new RectangleF(DisplayLocation.X + (Size.Width - s) / 2f, cy - s / 2f, s, s), Color.White);
        }
    }
}
