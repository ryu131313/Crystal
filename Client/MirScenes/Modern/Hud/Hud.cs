// Ported from WebClient/src/ui/modern/hud/hud.ts (the bottom HUD as a pure view of the game state) and the parts of
// hud/extras.ts the bottom HUD shows (the poison row and the durability strip).
// Layout (HUD px, the web's rig): two wings of 240 around a 120 x 130 centre diamond, 6 apart, 3 above the screen's bottom.
// Each wing: three stat plates (StatPlates.cs), the HP (left) / MP (right) bar, a belt row (Belt.cs: player cells 1-8,
// hero cells 9 / 0) and a skill row (SkillRow.cs) whose outer slot is the attack mode (left) or the pet mode (right). The
// diamond shows the level and the EXP % with the EXP ring round its edge, a Log out corner (left) and a game menu corner
// (right); the menu strip sits above it, the buff row above the left wing, the poison row and the durability strip above
// the right wing.
// Desktop idiom: one ModernSurface (HudRig) on ModernRoot.Hud, PassThrough so the world stays clickable between the
// controls; each control caches its own GDI texture and repaints only when what it shows changed. Process (per frame)
// compares the user's HP / MP / level / experience / modes with what is drawn; plates, buffs, poisons and durability
// update ~4x a second; animations (bar trail, struck shake, low-HP pulse, level-up burst, floats, flashes) are quantised
// steps that change a Location / Opacity (a re-blit of cached textures) or repaint a small control.
// Not ported (hidden): the big-hit screen vignette (no full-screen layer below the windows), the level number's scale pop
// (no transforms: the burst rings and the float show the level up), poison timers (S.PoisonStatus is web-only: the
// desktop has no handler, so the poison row shows the S.Poisoned flags without a countdown).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class HudRig : ModernSurface
    {
        // surface size and rig geometry (HUD px)
        public const int Width = 720, Height = 290;
        private const int RigX = 54, WingW = 240, CoreW = 120, CoreH = 130, WingH = 136, Gap = 6;
        private const int RigBottom = Height - 3, WingTop = RigBottom - WingH, CoreTop = RigBottom - CoreH;
        private const int LeftX = RigX, CoreX = RigX + WingW + Gap, RightX = CoreX + CoreW + Gap;

        /// <summary>The window keys of the menu strip (gameMenu.ts MENU_WINDOWS with strip: true), in strip order.</summary>
        internal static readonly string[] StripKeys = { "inventory", "character", "skills", "quests", "guild", "group", "friends", "mail", "gameShop", "bigmap", "options" };

        private readonly HudPopover _pop;
        private readonly GameMenu _menu;
        internal StatPlates Plates;
        internal Belt Belt;
        internal SkillRow Skills;
        internal ModeSlot AttackSlot, PetSlot;
        internal HudBar HpBar, MpBar;
        private CoreDiamond _core;
        private LevelLabel _level;
        internal CornerButton LogoutCorner, MenuCorner;
        internal readonly List<MenuStripButton> Strip = new List<MenuStripButton>();
        private MenuStripButton _stripMenu;
        private BurstRing _burst;
        private readonly List<BuffCell> _buffs = new List<BuffCell>();
        private readonly List<BuffCell> _poisons = new List<BuffCell>();
        internal DuraStrip Dura;
        /// <summary>The frames beside the rig (ModernUi.BuildHud): their roster / spawn checks run from this Process even while hidden.</summary>
        internal HeroFrame HeroFrame;
        internal PartyFrames Party;
        internal HudOutputs Outputs;

        // what is drawn
        private int _hp = -1, _hpMax = -1, _mp = -1, _mpMax = -1, _levelShown = -1;
        private long _exp = -1, _maxExp = -1;
        private bool _psn;
        private string _buffKey = string.Empty;
        private PoisonType _poisonShown = (PoisonType)ushort.MaxValue;
        private bool _duraDirty = true;
        private long _nextSlow, _nextSecond;
        private bool _platesOn;
        private int _mailShown = -1;
        private bool _built;

        // events
        private readonly Action _markDura, _onUserInfo, _onBuffs, _markSkills;
        private readonly Action<UserItem> _onDura;
        private readonly Action<ItemSlotKey> _onSlot;
        private readonly Action<bool> _onRelayout;

        public HudRig(GameMenu menu, HudPopover pop)
        {
            _menu = menu;
            _pop = pop;
            PassThrough = true;
            _markDura = () => _duraDirty = true;
            _onDura = _ => _duraDirty = true;
            _onSlot = key => { if (key.Grid == MirGridType.Equipment) _duraDirty = true; };
            _onUserInfo = () => { _duraDirty = true; _buffKey = "\u0000"; Skills?.MarkDirty(); };
            _onBuffs = () => _buffKey = "\u0000";
            _markSkills = () => Skills?.MarkDirty();
            _onRelayout = rebuild => { if (rebuild) Build(); else Place(); };

            ModernEvents.Durability.On(_onDura);
            ModernEvents.ItemInfo.On(_onDura);
            ModernEvents.UserInformation.On(_onUserInfo);
            ModernEvents.SkillBars.On(_markSkills);
            ModernEvents.HeroInformation.On(_markSkills);
            ModernEvents.HeroSpawnState.On(OnHeroSpawn);
            ModernEvents.GameData.On(OnGameData);
            ItemMoves.SlotChanged.On(_onSlot);
            GameScene.Models?.Buffs.Changed.On(_onBuffs);
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
            Build();
        }

        private void OnHeroSpawn(HeroSpawnState s) => Skills?.MarkDirty();
        private void OnGameData((bool Magics, bool Shop, bool Hover) d)
        {
            if (d.Magics) Skills?.MarkDirty();
            _duraDirty = true;
        }

        /// <summary>(Re)builds every control at the current HUD scale (first build, scale or theme change).</summary>
        private void Build()
        {
            _pop.Close();
            for (int i = _floats.Count - 1; i >= 0; i--) _floats[i].Dispose(); // children too: drop them before the rebuild
            _floats.Clear();
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            Strip.Clear();
            _buffs.Clear();
            _poisons.Clear();
            Size = new Size(S(Width), S(Height));

            new PlateBackdrop { Parent = this, Location = new Point(S(RigX - 46), S(WingTop - 16)), Size = new Size(S(WingW * 2 + CoreW + Gap * 2 + 92), S(Height - WingTop + 16)) };

            // wings: plates, bars, belt, skills
            Plates = new StatPlates(this, _pop, slot => slot < 3 ? DisplayLocation.X + S(LeftX) : DisplayLocation.X + S(RightX + WingW));
            float plateW = (WingW - 4) / 3f;
            for (int i = 0; i < 6; i++)
            {
                int x0 = i < 3 ? LeftX : RightX;
                int j = i % 3;
                Plates.Place(i, new Rectangle(S(x0 + j * (plateW + 2)), S(WingTop), S(x0 + (j + 1) * (plateW + 2) - 2) - S(x0 + j * (plateW + 2)), S(19)));
            }
            _platesOn = Settings.UiPlates;
            Plates.SetVisible(_platesOn);

            HpBar = new HudBar(false) { Parent = this, Location = new Point(S(LeftX), S(WingTop + 22)), Size = new Size(S(WingW), S(28)) };
            MpBar = new HudBar(true) { Parent = this, Location = new Point(S(RightX), S(WingTop + 22)), Size = new Size(S(WingW), S(28)) };
            HpBar.Home = HpBar.Location;
            MpBar.Home = MpBar.Location;

            Belt = new Belt(this);
            for (int i = 0; i < Belt.Count; i++)
            {
                int x = i < 5 ? LeftX + i * 50 : RightX + (i - 5) * 50;
                Belt.Place(i, new Point(S(x), S(WingTop + 53)));
            }

            Skills = new SkillRow(this);
            AttackSlot = new ModeSlot(false, _pop, () => DisplayLocation.X + S(LeftX)) { Parent = this };
            AttackSlot.SetBounds(new Rectangle(S(LeftX), S(WingTop + 96), S(64), S(40)));
            for (int i = 0; i < 8; i++)
            {
                int x = i < 4 ? LeftX + 68 + i * 44 : RightX + (i - 4) * 44;
                Skills.Slots[i].SetBounds(new Rectangle(S(x), S(WingTop + 96), S(40), S(40)));
            }
            PetSlot = new ModeSlot(true, _pop, () => DisplayLocation.X + S(RightX + WingW)) { Parent = this };
            PetSlot.SetBounds(new Rectangle(S(RightX + WingW - 64), S(WingTop + 96), S(64), S(40)));

            // centre diamond
            _burst = new BurstRing { Parent = this, Location = new Point(S(CoreX - 60), S(CoreTop - 65)), Size = new Size(S(CoreW + 120), S(CoreH + 130)) };
            _core = new CoreDiamond { Parent = this, Location = new Point(S(CoreX - CoreDiamond.Margin), S(CoreTop - CoreDiamond.Margin)),
                Size = new Size(S(CoreW + CoreDiamond.Margin * 2), S(CoreH + CoreDiamond.Margin * 2)), Hint = HudKit.T(ClientTextKeys.ModernHudExpHint) };
            _level = new LevelLabel { Parent = this, Location = new Point(S(CoreX + 20), S(CoreTop + 30)), Size = new Size(S(CoreW - 40), S(70)) };
            string logoutKey = HudKit.KeyLabel(KeybindOptions.Logout);
            LogoutCorner = new CornerButton(false) { Parent = this, Location = new Point(S(CoreX), S(CoreTop + 74)), Size = new Size(S(52), S(56)),
                Hint = logoutKey.Length > 0 ? HudKit.T(ClientTextKeys.ModernHudLogOutKey, logoutKey) : HudKit.T(ClientTextKeys.ModernHudLogOut) };
            LogoutCorner.Click += (o, e) => { LogoutCorner.Flash(); HudActions.LogOut(); };
            MenuCorner = new CornerButton(true) { Parent = this, Location = new Point(S(CoreX + CoreW - 52), S(CoreTop + 74)), Size = new Size(S(52), S(56)),
                Hint = HudKit.T(ClientTextKeys.ModernHudGameMenuKey) };
            MenuCorner.Click += (o, e) => { MenuCorner.Flash(); _menu.Toggle(); };

            // menu strip (above the diamond)
            for (int i = 0; i < StripKeys.Length; i++)
            {
                MenuEntry w = MenuEntry.Find(StripKeys[i]);
                if (w == null) continue;
                var b = new MenuStripButton(w.Icon, w.Key == "mail") { Parent = this, Size = new Size(S(30), S(30)) };
                string key = w.Key;
                b.Click += (o, e) => { b.Flash(); ModernRoot.Instance?.Wm.Toggle(key); };
                b.Entry = w;
                Strip.Add(b);
            }
            _stripMenu = new MenuStripButton(ModernIcons.Menu, false) { Parent = this, Size = new Size(S(30), S(30)), Hint = HudKit.T(ClientTextKeys.ModernHudGameMenuKey) };
            _stripMenu.Click += (o, e) => { _stripMenu.Flash(); _menu.Toggle(); };
            LayoutStrip();

            // durability strip (above the right wing)
            Dura = new DuraStrip(_pop) { Parent = this, Visible = false };
            Dura.Click += (o, e) => ModernRoot.Instance?.Wm.Open("character");

            // reset the drawn state so the first Process writes everything
            _hp = _hpMax = _mp = _mpMax = _levelShown = -1;
            _exp = _maxExp = -1;
            _psn = false;
            _buffKey = "\u0000";
            _poisonShown = (PoisonType)ushort.MaxValue;
            _duraDirty = true;
            _nextSlow = 0;
            _mailShown = -1;
            _built = true;
            Place();
            TextureValid = false;
            if (GameScene.User != null) Process();
        }

        /// <summary>Bottom centre of the screen.</summary>
        private void Place()
        {
            Location = new Point((Settings.ScreenWidth - Size.Width) / 2, Settings.ScreenHeight - Size.Height);
        }

        /// <summary>Lays the strip out: registered windows only (later phases register theirs), then the game menu button.</summary>
        private void LayoutStrip()
        {
            WindowManager wm = ModernRoot.Instance?.Wm;
            int n = 0;
            for (int i = 0; i < Strip.Count; i++)
            {
                bool on = wm != null && wm.IsRegistered(Strip[i].Entry.Key);
                Strip[i].Visible = on;
                if (on) n++;
            }
            // boxes of 26 with 4 between, the game menu 6 further right; centred on the diamond, 14 above it
            float total = n * 26 + n * 4 + 6 + 26;
            float x = CoreX + CoreW / 2f - total / 2f, cy = CoreTop - 14 - 13;
            for (int i = 0; i < Strip.Count; i++)
            {
                if (!Strip[i].Visible) continue;
                Strip[i].Location = new Point(S(x + 13 - 15), S(cy - 15));
                x += 30;
            }
            x += 6;
            _stripMenu.Location = new Point(S(x + 13 - 15), S(cy - 15));
        }

        // ---- per frame ----

        public override void Process()
        {
            if (!_built || IsDisposed) return;
            _menu.Tick();
            UserObject u = GameScene.User;
            if (u == null) return;
            long now = CMain.Time;
            HudClock.Advance(now);
            HeroFrame?.CheckShown();
            Party?.CheckShown();
            Outputs?.Check();

            int hpMax = u.Stats != null ? u.Stats[Stat.HP] : 0, mpMax = u.Stats != null ? u.Stats[Stat.MP] : 0;
            if (u.HP != _hp || hpMax != _hpMax)
            {
                bool struck = _hp >= 0 && u.HP < _hp && hpMax == _hpMax;
                _hp = u.HP;
                _hpMax = hpMax;
                HpBar.Set(u.HP, hpMax, struck);
            }
            if (u.MP != _mp || mpMax != _mpMax)
            {
                _mp = u.MP;
                _mpMax = mpMax;
                MpBar.Set(u.MP, mpMax, false);
            }
            bool psn = (u.Poison & PoisonType.Green) != 0;
            if (psn != _psn)
            {
                _psn = psn;
                HpBar.Poisoned = psn;
            }
            if (u.Level != _levelShown)
            {
                bool up = _levelShown > 0 && u.Level > _levelShown;
                _levelShown = u.Level;
                _level.SetLevel(u.Level);
                if (up)
                {
                    _burst.Start();
                    Float(HudKit.T(ClientTextKeys.ModernHudLevelUp), true);
                }
            }
            if (u.Experience != _exp || u.MaxExperience != _maxExp) DrawExp(u.Experience, u.MaxExperience);

            Skills.SetView(CMain.Shift ? 2 : CMain.Ctrl ? 1 : 0);
            Skills.Process();
            AttackSlot.UpdateMode();
            PetSlot.UpdateMode();
            Belt.Process();
            if (HudClock.Ticked)
            {
                // time-driven steps only on the shared 16 Hz clock: their invalidations land in the same frames, so the rig
                // re-renders at most 16 times a second for animations (never per frame)
                HpBar.Tick();
                MpBar.Tick();
                _burst.Tick();
                Plates.Pulse();
                for (int i = _buffs.Count - 1; i >= 0; i--) _buffs[i].Blink();
                TickFloats();
            }

            if (now >= _nextSlow)
            {
                _nextSlow = now + 250;
                Slow(u);
            }
            if (now >= _nextSecond)
            {
                _nextSecond = now + 1000;
                Second(u);
            }
        }

        private void DrawExp(long exp, long max)
        {
            long prev = _exp, prevMax = _maxExp;
            _exp = exp;
            _maxExp = max;
            double pct = max > 0 ? Math.Floor(exp * 10000.0 / max) / 100.0 : 0;
            // "+x% EXP" on a gain within the same level (not on the first draw or a level change)
            if (prev >= 0 && max == prevMax && exp > prev && max > 0)
            {
                double gain = Math.Floor((exp - prev) * 10000.0 / max) / 100.0;
                if (gain >= 0.01) Float(HudKit.T(ClientTextKeys.ModernHudExpGain, gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), false);
            }
            float f = (float)Math.Min(100, Math.Max(0, pct));
            _core.SetExp(f);
            _level.SetPercent(f);
        }

        /// <summary>~4x a second: plates, buffs, poisons, durability.</summary>
        private void Slow(UserObject u)
        {
            if (_platesOn) Plates.Update();
            DrawBuffs();
            DrawPoisons(u.Poison);
            if (_duraDirty)
            {
                _duraDirty = false;
                Dura.Rebuild(u);
                Dura.Location = new Point(S(RightX + WingW) - Dura.Size.Width, S(WingTop - 50) - Dura.Size.Height);
            }
        }

        /// <summary>Once a second: later phases' windows (strip buttons), the Mail badge, the plates switch (Options).</summary>
        private void Second(UserObject u)
        {
            LayoutStrip();
            int unread = 0;
            if (u.Mail != null)
                for (int i = 0; i < u.Mail.Count; i++) if (!u.Mail[i].Opened) unread++;
            if (unread != _mailShown)
            {
                _mailShown = unread;
                for (int i = 0; i < Strip.Count; i++) if (Strip[i].Entry.Key == "mail") Strip[i].Badge = unread <= 0 ? string.Empty : unread > 9 ? "9+" : unread.ToString();
            }
            if (Settings.UiPlates != _platesOn)
            {
                _platesOn = Settings.UiPlates;
                Plates.SetVisible(_platesOn);
                if (!_platesOn && _pop.Card != null) _pop.Close();
            }
        }

        // ---- buffs (above the left wing) ----

        private void DrawBuffs()
        {
            List<ClientBuff> list = GameScene.Models?.Buffs.Items;
            if (list == null) return;
            // the key is rebuilt only after Buffs.Changed / UserInformation (marked "\0"), never per tick
            if (_buffKey == "\u0000")
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    ClientBuff b = list[i];
                    sb.Append((int)b.Type).Append(':').Append(b.Infinite ? 1 : 0).Append(b.Paused ? 1 : 0).Append(':').Append(b.ExpireTime).Append('|');
                }
                string key = sb.ToString();
                if (key != _buffKey)
                {
                    _buffKey = key;
                    for (int i = 0; i < _buffs.Count; i++) _buffs[i].Dispose();
                    _buffs.Clear();
                    for (int i = 0; i < list.Count; i++)
                    {
                        int row = i / 8, col = i % 8;
                        var cell = new BuffCell(list[i]) { Parent = this };
                        cell.Location = new Point(S(LeftX + col * 30), S(WingTop - 8 - BuffCell.H - row * (BuffCell.H + 4)));
                        _buffs.Add(cell);
                    }
                }
            }
            for (int i = 0; i < _buffs.Count; i++) _buffs[i].UpdateTime();
        }

        // ---- poisons (above the right wing): the S.Poisoned flags ----

        private static readonly PoisonType[] PoisonBits =
        {
            PoisonType.Green, PoisonType.Red, PoisonType.Slow, PoisonType.Frozen, PoisonType.Stun, PoisonType.Paralysis, PoisonType.DelayedExplosion,
            PoisonType.Bleeding, PoisonType.LRParalysis, PoisonType.Blindness, PoisonType.Dazed,
        };

        private void DrawPoisons(PoisonType flags)
        {
            if (flags == _poisonShown) return;
            _poisonShown = flags;
            for (int i = 0; i < _poisons.Count; i++) _poisons[i].Dispose();
            _poisons.Clear();
            int n = 0;
            for (int i = 0; i < PoisonBits.Length; i++)
            {
                if ((flags & PoisonBits[i]) == 0) continue;
                var cell = new BuffCell(PoisonBits[i]) { Parent = this };
                cell.Location = new Point(S(RightX + WingW - 26 - n * 30), S(WingTop - 8 - BuffCell.H));
                _poisons.Add(cell);
                n++;
            }
        }

        // ---- floats ----

        private readonly List<FloatText> _floats = new List<FloatText>();

        private void Float(string text, bool big)
        {
            var f = new FloatText(text, big) { Parent = this };
            f.Home = new Point(S(CoreX + CoreW / 2f) - f.Size.Width / 2, S(CoreTop - 6));
            f.Location = f.Home;
            _floats.Add(f);
        }

        private void TickFloats()
        {
            for (int i = _floats.Count - 1; i >= 0; i--)
            {
                if (_floats[i].Tick()) continue;
                _floats[i].Dispose();
                _floats.RemoveAt(i);
            }
        }

        // ---- keys ----

        /// <summary>Esc (the Closeall bind) when no window took it: the game menu opens / closes, an open list closes first.</summary>
        internal bool Escape()
        {
            if (_pop.IsOpen)
            {
                _pop.Close();
                return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ModernEvents.Durability.Off(_onDura);
                ModernEvents.ItemInfo.Off(_onDura);
                ModernEvents.UserInformation.Off(_onUserInfo);
                ModernEvents.SkillBars.Off(_markSkills);
                ModernEvents.HeroInformation.Off(_markSkills);
                ModernEvents.HeroSpawnState.Off(OnHeroSpawn);
                ModernEvents.GameData.Off(OnGameData);
                ItemMoves.SlotChanged.Off(_onSlot);
                GameScene.Models?.Buffs.Changed.Off(_onBuffs);
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            }
            base.Dispose(disposing);
        }

        // ---- test hooks ----
        internal int ShownHp => _hp;
        internal int BuffCount => _buffs.Count;
        internal int PoisonCount => _poisons.Count;
        internal float ExpPercent => _core?.Percent ?? -1;
        internal int FloatCount => _floats.Count;
    }

    /// <summary>The HUD's actions shared by the corners and the game menu.</summary>
    internal static class HudActions
    {
        /// <summary>Log out: GameScene.LogOut refuses inside the combat lock (its chat line); otherwise a Yes / No popup first.</summary>
        public static void LogOut()
        {
            GameScene scene = GameScene.Scene;
            if (scene == null) return;
            if (CMain.Time < GameScene.LogTime)
            {
                scene.LogOut(false); // posts "Cannot leave game for N seconds"
                return;
            }
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null)
            {
                scene.LogOut();
                return;
            }
            pop.Ask(HudKit.T(ClientTextKeys.LogOutTip), yes => { if (yes) GameScene.Scene?.LogOut(false); });
        }

        /// <summary>"Switch to classic interface": save UiStyle Classic and offer to log out now (the switch applies at the next log-in).</summary>
        public static void SwitchToClassic()
        {
            Settings.UiStyle = UiStyle.Classic;
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null) return;
            pop.Ask(HudKit.T(ClientTextKeys.ModernUiStyleSwitchPrompt), yes => { if (yes) GameScene.Scene?.LogOut(false); });
        }
    }

    // ------------------------------------------------------------------ controls

    /// <summary>.hm-plate: the soft radial backdrop behind the rig (genuinely translucent, like the web) with its accent hairline.</summary>
    internal sealed class PlateBackdrop : ModernControl
    {
        public PlateBackdrop()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float w = Size.Width, h = Size.Height;
            // radial-gradient(ellipse 60% 120% at 50% 100%, pb, pb 60% at 55%, transparent 80%)
            using (var path = new GraphicsPath())
            {
                float rx = w * 0.6f / 0.8f * 0.5f * 1.6f, ry = h * 1.2f;
                path.AddEllipse(w / 2f - rx, h - ry, rx * 2, ry * 2);
                using var brush = new PathGradientBrush(path)
                {
                    CenterPoint = new PointF(w / 2f, h),
                    CenterColor = ModernSkin.Opaque(ModernTheme.PanelB),
                    SurroundColors = new[] { Color.Transparent },
                };
                Color pb = ModernSkin.Opaque(ModernTheme.PanelB);
                brush.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, pb), Color.FromArgb(0, pb), HudKit.Sq(pb, 0.6f), pb },
                    Positions = new[] { 0f, 0.2f, 0.45f, 1f },
                };
                g.FillRectangle(brush, 0, 0, w, h);
            }
            ModernSkin.FadedLine(g, w * 0.12f, 6 * K, w * 0.76f, 1, ModernTheme.Alpha(ModernTheme.Accent, 0.45f));
        }
    }

    /// <summary>
    /// The HP (left) / MP (right) bar (.hm-bar): an angled outer cap, the fill anchored at the diamond side, a dark trail that
    /// follows a drop (after 450 ms, over 800 ms, 16 steps), the value text and the HP / MP tag; low HP pulses a light overlay,
    /// a hit shakes the bar (300 ms).
    /// </summary>
    internal sealed class HudBar : ModernControl
    {
        private readonly bool _mp;
        private int _value = -1, _max = -1;
        private float _fill, _lag, _lagFrom, _fillFrom, _fillTo;
        private HudAnim _lagAnim, _fillAnim, _shake;
        private string _text = string.Empty;
        private bool _poisoned, _low;
        private float _pulse;
        private int _pulseStep = -1;
        public Point Home;

        private static readonly int[] ShakeX = { 0, -3, 3, -2, 1, 0 };

        public HudBar(bool mp)
        {
            _mp = mp;
        }

        public float Fill => _fill;
        public bool Low => _low;
        public string Text => _text;

        public bool Poisoned
        {
            get => _poisoned;
            set
            {
                if (_poisoned == value) return;
                _poisoned = value;
                Invalidate();
            }
        }

        public void Set(int value, int max, bool struck)
        {
            _value = value;
            _max = max;
            float v = max > 0 ? Math.Clamp(value / (float)max, 0f, 1f) : 0f;
            if (v < _fill)
            {
                // a drop: the fill eases down (250 ms), the trail waits 450 ms then follows (800 ms)
                if (!_lagAnim.Running) _lagFrom = Math.Max(_lag, _fill);
                else _lagFrom = _lag;
                _lag = _lagFrom;
                _lagAnim.Begin(800, 450);
            }
            else
            {
                _lag = v;
                _lagAnim.Stop();
            }
            _fillFrom = _fill;
            _fillTo = v;
            if (_fillFrom > _fillTo && _value >= 0) _fillAnim.Begin(250);
            else
            {
                _fill = v;
                _fillAnim.Stop();
            }
            _text = value + " / " + max;
            bool low = !_mp && max > 0 && v < 0.3f;
            if (low != _low)
            {
                _low = low;
                _pulse = 0;
                _pulseStep = -1;
            }
            if (struck) _shake.Begin(300);
            Invalidate();
        }

        /// <summary>Per frame: the trail, the fill ease, the pulse and the shake — each writes only on a step change.</summary>
        public void Tick()
        {
            if (_fillAnim.Running)
            {
                int s = _fillAnim.Step(5);
                if (s >= 0 && s != _fillAnim.Last)
                {
                    _fillAnim.Last = s;
                    float t = s / 5f;
                    t = 1f - (1f - t) * (1f - t); // ease-out
                    _fill = _fillFrom + (_fillTo - _fillFrom) * t;
                    Invalidate();
                }
            }
            if (_lagAnim.Running)
            {
                int s = _lagAnim.Step(16);
                if (s >= 0 && s != _lagAnim.Last)
                {
                    _lagAnim.Last = s;
                    float t = s / 16f;
                    t *= t; // ease-in
                    _lag = _lagFrom + (_fill - _lagFrom) * t;
                    if (s >= 16) _lag = _fill;
                    Invalidate();
                }
            }
            if (_low)
            {
                int step = (int)(CMain.Time % 1000 / 100);
                if (step != _pulseStep)
                {
                    _pulseStep = step;
                    _pulse = step < 5 ? step / 5f : (10 - step) / 5f;
                    Invalidate(); // painted into the bar (opaque): 10 repaints a second while HP is low
                }
            }
            if (_shake.Running)
            {
                int s = _shake.Step(5);
                if (s >= 0 && s != _shake.Last)
                {
                    _shake.Last = s;
                    Location = new Point(Home.X + S(ShakeX[Math.Min(s, 5)]), Home.Y);
                }
            }
        }

        internal static GraphicsPath Shape(bool mp, float x, float y, float w, float h, float cap)
        {
            return mp
                ? HudKit.Polygon(new PointF(x, y), new PointF(x + w - cap, y), new PointF(x + w, y + h / 2f), new PointF(x + w - cap, y + h), new PointF(x, y + h))
                : HudKit.Polygon(new PointF(x + cap, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x + cap, y + h), new PointF(x, y + h / 2f));
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height, cap = 12 * k;
            using (GraphicsPath outer = Shape(_mp, 0, 0, w, h, cap))
                ModernSkin.FillVertical(g, outer, new RectangleF(0, 0, w, h), new[] { ModernTheme.FrameLight, ModernTheme.FrameDark });
            using GraphicsPath inner = Shape(_mp, 1, 1, w - 2, h - 2, cap - 1);
            using (var b = new SolidBrush(Color.FromArgb(255, 4, 6, 10))) g.FillPath(b, inner);
            Region old = g.Clip;
            g.SetClip(inner, CombineMode.Intersect);
            float iw = w - 2;
            // trail then fill, both anchored at the diamond side (HP: right, MP: left)
            DrawFill(g, _lag, iw, h, _mp ? new[] { Hex(0x0a1a40), Hex(0x06102a) } : new[] { Hex(0x2a0306), Hex(0x5c0a10) }, null);
            Color[] fill = _mp ? new[] { Hex(0x3a8ef0), Hex(0x1a50b8), Hex(0x0c2262) }
                : _poisoned ? new[] { Hex(0x0d3a18), Hex(0x1f8a3c), Hex(0x4fe07a) } : new[] { Hex(0x4a080e), Hex(0xa01a24), Hex(0xe0414a) };
            DrawFill(g, _fill, iw, h, fill, new[] { 0f, _mp ? 0.45f : 0.55f, 1f });
            // inset 0 2px 4px #000: a dark band at the top
            using (var sh = new LinearGradientBrush(new RectangleF(0, 0, w, 6 * k), Color.FromArgb(150, 0, 0, 0), Color.Transparent, LinearGradientMode.Vertical))
                g.FillRectangle(sh, 0, 1, w, 6 * k);
            // low HP: rgba(255,120,120,.35) pulsing (hudm-halo), composited here so the bar stays opaque
            if (_low && _pulse > 0)
                using (var pb = new SolidBrush(Color.FromArgb((int)(89 * _pulse), 255, 120, 120)))
                    g.FillRectangle(pb, 0, 0, w, h);
            g.Clip = old;
            old.Dispose();
            HudKit.ShadowText(g, _text, ModernSkin.Font(12, true), Color.White, new RectangleF(0, 0, w, h), StringAlignment.Center, StringAlignment.Center, 200);
            string tag = HudKit.T(_mp ? ClientTextKeys.ModernHudMp : ClientTextKeys.ModernHudHp);
            RectangleF tr = new RectangleF(14 * k, 0, w - 28 * k, h);
            HudKit.ShadowText(g, tag, ModernSkin.Font(10, true), Color.FromArgb(191, 255, 255, 255), tr, _mp ? StringAlignment.Far : StringAlignment.Near, StringAlignment.Center, 120);
        }

        private void DrawFill(Graphics g, float frac, float iw, float h, Color[] colours, float[] positions)
        {
            if (frac <= 0) return;
            // the fill element is the bar's full width, translated: its gradient moves with it
            float shift = (1f - frac) * iw;
            RectangleF el = _mp ? new RectangleF(1 - shift, 0, iw, h) : new RectangleF(1 + shift, 0, iw, h);
            RectangleF vis = _mp ? new RectangleF(1, 0, iw - shift, h) : new RectangleF(1 + shift, 0, iw - shift, h);
            if (vis.Width <= 0) return;
            using var brush = new LinearGradientBrush(new RectangleF(el.X - 0.5f, 0, el.Width + 1, h), colours[0], colours[^1], LinearGradientMode.Horizontal);
            if (colours.Length > 2)
                brush.InterpolationColors = new ColorBlend { Colors = colours, Positions = positions ?? new[] { 0f, 0.5f, 1f } };
            g.FillRectangle(brush, vis);
        }

        private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    }

    /// <summary>
    /// The centre diamond (svg.dia): fins, the outer metal line, the core fill with its black and frame strokes, the EXP ring from
    /// the top vertex clockwise with its glowing head, the quarter ticks (lit as they are passed), the inner hairline and the top
    /// gem. Repainted only when the shown EXP % changes. Drawn with a margin round the 120 x 130 box (the SVG overflows it).
    /// </summary>
    internal sealed class CoreDiamond : ModernControl
    {
        public const int Margin = 8;
        private float _pct = -1;

        private static readonly PointF[] Corners = { new PointF(60, 4), new PointF(116, 65), new PointF(60, 126), new PointF(4, 65), new PointF(60, 4) };
        private static readonly float[] TickAt = { 25, 50, 75 };
        private static readonly PointF[] TickXY = { new PointF(116, 65), new PointF(60, 126), new PointF(4, 65) };

        public float Percent => _pct;

        public void SetExp(float pct)
        {
            if (Math.Abs(pct - _pct) < 0.005f) return;
            _pct = pct;
            Invalidate();
        }

        /// <summary>Over the diamond only (the hit box is the rotated square of the web).</summary>
        public override bool IsMouseOver(Point p)
        {
            if (!Visible) return false;
            float k = K;
            float x = (p.X - DisplayLocation.X) / k - Margin - 60, y = (p.Y - DisplayLocation.Y) / k - Margin - 65;
            return Math.Abs(x) / 50f + Math.Abs(y) / 50f <= 1f;
        }

        private static PointF P(float x, float y, float k) => new PointF((x + Margin) * k, (y + Margin) * k);

        private static GraphicsPath Diamond(float top, float right, float bottom, float left, float cx, float cy, float k)
        {
            return HudKit.Polygon(P(cx, top, k), P(right, cy, k), P(cx, bottom, k), P(left, cy, k));
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            Color frl = ModernTheme.FrameLight, fr = ModernTheme.Frame, frd = ModernTheme.FrameDark;
            RectangleF box = new RectangleF(0, Margin * k, Size.Width, 130 * k);
            Color[] metal = { frl, fr, frd, frl };
            float[] metalPos = { 0f, 0.45f, 0.55f, 1f };

            // fins
            using (GraphicsPath f1 = HudKit.Polygon(P(12, 57, k), P(-6, 65, k), P(12, 73, k)))
            using (GraphicsPath f2 = HudKit.Polygon(P(108, 57, k), P(126, 65, k), P(108, 73, k)))
            {
                ModernSkin.FillVertical(g, f1, new RectangleF(0, P(0, 57, k).Y, Size.Width, 16 * k), metal, metalPos);
                ModernSkin.FillVertical(g, f2, new RectangleF(0, P(0, 57, k).Y, Size.Width, 16 * k), metal, metalPos);
                ModernSkin.Stroke(g, f1, Color.Black);
                ModernSkin.Stroke(g, f2, Color.Black);
            }
            // D_OUT, metal
            using (GraphicsPath dOut = Diamond(-2, 122, 132, -2, 60, 65, k))
            using (var b = new LinearGradientBrush(new RectangleF(0, P(0, -2, k).Y - 1, Size.Width, 134 * k + 2), frl, frl, LinearGradientMode.Vertical))
            {
                b.InterpolationColors = new ColorBlend { Colors = metal, Positions = metalPos };
                using var pen = new Pen(b, 2.5f * k);
                g.DrawPath(pen, dOut);
            }
            using GraphicsPath d = Diamond(4, 116, 126, 4, 60, 65, k);
            // the black 10 px outline (centred on D: half of it outside), then the fill, then the frame strokes
            using (var pen = new Pen(Color.Black, 10 * k) { LineJoin = LineJoin.Miter }) g.DrawPath(pen, d);
            ModernSkin.FillVertical(g, d, box, new[] { ModernSkin.Opaque(ModernTheme.PanelC), ModernSkin.Opaque(ModernTheme.PanelB) });
            using (var pen = new Pen(ModernTheme.Mix(fr, Color.Black, 0.7f), 6 * k) { LineJoin = LineJoin.Miter }) g.DrawPath(pen, d);
            using (var pen = new Pen(Color.FromArgb(153, 0, 0, 0), 2 * k)) g.DrawPath(pen, d);

            // EXP ring along D from the top vertex, clockwise
            float f = Math.Max(0, _pct);
            if (f > 0)
            {
                PointF[] pts = RingPoints(f, k, out PointF head);
                for (int pass = 0; pass < 3; pass++)
                {
                    float width = pass == 0 ? 12 * k : pass == 1 ? 9 * k : 6 * k;
                    Color c = pass == 0 ? ModernTheme.Alpha(ModernTheme.Accent, 0.18f) : pass == 1 ? ModernTheme.Alpha(ModernTheme.Accent, 0.35f) : ModernTheme.Accent;
                    using var pen = new Pen(c, width) { LineJoin = LineJoin.Miter, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                    if (pts.Length >= 2) g.DrawLines(pen, pts);
                }
                if (f > 0.2f)
                {
                    float r = 3.2f * k;
                    using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.35f))) g.FillEllipse(b, head.X - r * 2, head.Y - r * 2, r * 4, r * 4);
                    using (var b = new SolidBrush(ModernTheme.Mix(ModernTheme.Accent, Color.White, 0.3f))) g.FillEllipse(b, head.X - r, head.Y - r, r * 2, r * 2);
                }
            }
            // quarter ticks
            for (int i = 0; i < TickXY.Length; i++)
            {
                PointF c = TickXY[i];
                using GraphicsPath t = HudKit.Polygon(P(c.X, c.Y - 4, k), P(c.X + 4, c.Y, k), P(c.X, c.Y + 4, k), P(c.X - 4, c.Y, k));
                using (var b = new SolidBrush(f >= TickAt[i] ? ModernTheme.Accent : fr)) g.FillPath(b, t);
                ModernSkin.Stroke(g, t, Color.Black);
            }
            // D_IN hairline
            using (GraphicsPath dIn = Diamond(13, 108, 117, 12, 60, 65, k))
                ModernSkin.Stroke(g, dIn, ModernTheme.Alpha(frl, 0.4f));
            // top gem
            using (GraphicsPath gem = HudKit.Polygon(P(60, -6, k), P(65, 2, k), P(60, 9, k), P(55, 2, k)))
            {
                ModernSkin.FillVertical(g, gem, new RectangleF(0, P(0, -6, k).Y, Size.Width, 15 * k), metal, metalPos);
                ModernSkin.Stroke(g, gem, Color.Black);
            }
        }

        /// <summary>The ring's polyline for f % of D's perimeter (its four sides are equal: corners by arithmetic) and its head.</summary>
        private static PointF[] RingPoints(float f, float k, out PointF head)
        {
            float at = Math.Min(100, f) / 100f * 4f;
            int seg = Math.Min(3, (int)Math.Floor(at));
            float t = at - seg;
            var pts = new PointF[seg + 2];
            for (int i = 0; i <= seg; i++) pts[i] = P(Corners[i].X, Corners[i].Y, k);
            PointF a = Corners[seg], b = Corners[seg + 1];
            pts[seg + 1] = P(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, k);
            head = pts[seg + 1];
            return pts;
        }
    }

    /// <summary>The level-up burst (two rings of D growing to 1.9x and fading over 1 s, the second 150 ms later).</summary>
    internal sealed class BurstRing : ModernControl
    {
        private HudAnim _anim;
        private float _t = -1;

        public BurstRing()
        {
            NotControl = true;
            Visible = false;
        }

        public void Start()
        {
            _anim.Begin(1150);
            _t = 0;
            Visible = true;
            Invalidate();
        }

        public void Tick()
        {
            if (!_anim.Running) return;
            int s = _anim.Step(12);
            if (s < 0 || s == _anim.Last) return;
            _anim.Last = s;
            if (s >= 12)
            {
                Visible = false;
                return;
            }
            _t = s / 12f;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            if (_t < 0) return;
            float k = K, cx = Size.Width / 2f, cy = Size.Height / 2f;
            for (int ring = 0; ring < 2; ring++)
            {
                float t = (_t * 1150f - ring * 150f) / 1000f;
                if (t < 0 || t > 1) continue;
                float sc = 1f + 0.9f * t;
                Color c = ring == 0 ? ModernTheme.Mix(ModernTheme.Accent, Color.White, 0.5f) : ModernTheme.Accent;
                using GraphicsPath d = HudKit.Polygon(new PointF(cx, cy - 61 * k * sc), new PointF(cx + 56 * k * sc, cy), new PointF(cx, cy + 61 * k * sc), new PointF(cx - 56 * k * sc, cy));
                ModernSkin.Stroke(g, d, ModernTheme.Alpha(c, 1f - t), (ring == 0 ? 3 : 2) * k);
            }
        }
    }

    /// <summary>The level and EXP % inside the diamond (.lv: LEVEL, the number in Georgia, the percent).</summary>
    internal sealed class LevelLabel : ModernControl
    {
        private int _level;
        private string _levelText = string.Empty, _pct = "0.00%";
        private float _pctValue = -1;

        public LevelLabel()
        {
            NotControl = true;
        }

        public string LevelText => _levelText;
        public string PercentText => _pct;

        public void SetLevel(int level)
        {
            if (level == _level) return;
            _level = level;
            _levelText = level.ToString();
            Invalidate();
        }

        public void SetPercent(float f)
        {
            if (Math.Abs(f - _pctValue) < 0.005f) return;
            _pctValue = f;
            _pct = f.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width;
            ModernSkin.SpacedText(g, HudKit.T(ClientTextKeys.ModernHudLevel), ModernSkin.Font(9), ModernTheme.Dim, new RectangleF(0, 4 * k, w, 12 * k), 1.8f * k);
            Font big = HudKit.Serif(30);
            RectangleF nr = new RectangleF(0, 15 * k, w, 34 * k);
            // text-shadow: 0 0 var(--glr) var(--glow), 0 2px 2px #000
            for (int i = 3; i >= 1; i--)
                ModernSkin.Text(g, _levelText, big, ModernTheme.Alpha(ModernTheme.Accent, 0.12f * ModernTheme.Glow), RectangleF.Inflate(nr, i * k, i * k), StringAlignment.Center, StringAlignment.Center, false);
            ModernSkin.Text(g, _levelText, big, Color.FromArgb(200, 0, 0, 0), new RectangleF(nr.X, nr.Y + 2 * k, nr.Width, nr.Height), StringAlignment.Center, StringAlignment.Center, false);
            ModernSkin.Text(g, _levelText, big, Color.FromArgb(255, 242, 246, 255), nr, StringAlignment.Center, StringAlignment.Center, false);
            ModernSkin.Text(g, _pct, ModernSkin.Font(11, true), ModernTheme.AccentDim, new RectangleF(0, 50 * k, w, 15 * k), StringAlignment.Center, StringAlignment.Center, false);
        }
    }

    /// <summary>A corner button under the diamond (.tri): Log out (left, the return arrow) or the game menu (right, three lines).</summary>
    internal sealed class CornerButton : ModernControl
    {
        private readonly bool _right;
        private readonly HudFlash _flash;

        public CornerButton(bool right)
        {
            _right = right;
            _flash = new HudFlash { Parent = this, Shape = right ? FlashShape.TriangleRight : FlashShape.TriangleLeft };
        }

        public void Flash()
        {
            _flash.Size = Size;
            _flash.Start(350);
        }

        // the triangle in local HUD px: left (0,0)-(0,56)-(52,56), right (52,0)-(52,56)-(0,56)
        private PointF[] Tri(float k) => _right
            ? new[] { new PointF(52 * k, 0), new PointF(52 * k, 56 * k), new PointF(0, 56 * k) }
            : new[] { new PointF(0, 0), new PointF(0, 56 * k), new PointF(52 * k, 56 * k) };

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || !DisplayRectangle.Contains(p)) return false;
            float k = K, x = (p.X - DisplayLocation.X) / k, y = (p.Y - DisplayLocation.Y) / k;
            // inside when below the hypotenuse
            return _right ? y >= 56f - x * 56f / 52f : y >= x * 56f / 52f;
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            using GraphicsPath t = HudKit.Polygon(Tri(k));
            ModernSkin.FillVertical(g, t, new RectangleF(0, 0, Size.Width, Size.Height), new[] { ModernSkin.Opaque(ModernTheme.PanelC), ModernSkin.Opaque(ModernTheme.PanelB) });
            Color stroke = Hovered ? ModernTheme.Accent : ModernTheme.Frame;
            using (var pen = new Pen(stroke, 1.5f * k) { LineJoin = LineJoin.Round }) g.DrawPath(pen, t);
            // the icon (a 24-unit path at scale .82): left at (3, 32.3), right at (45.3, 32.3) in local HUD px
            // the web's translate(3 106.3) / translate(97.3 106.3) in the diamond box, i.e. (3, 32.3) / (29.3, 32.3) here
            float ox = _right ? 29.3f : 3f, oy = 32.3f, s = 0.82f;
            Color ic = Hovered ? Color.White : ModernTheme.AccentDim;
            using var ip = new Pen(ic, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            PointF Q(float x, float y) => new PointF((ox + x * s) * k, (oy + y * s) * k);
            if (_right)
            {
                g.DrawLine(ip, Q(4, 6), Q(20, 6));
                g.DrawLine(ip, Q(4, 12), Q(20, 12));
                g.DrawLine(ip, Q(4, 18), Q(20, 18));
            }
            else
            {
                // M9 14L4 9l5-5 M4 9h10a6 6 0 010 12h-3
                g.DrawLines(ip, new[] { Q(9, 14), Q(4, 9), Q(9, 4) });
                using var path = new GraphicsPath();
                path.AddLine(Q(4, 9), Q(14, 9));
                PointF c = Q(14, 15);
                float r = 6 * s * k;
                path.AddArc(c.X - r, c.Y - r, r * 2, r * 2, -90, 180);
                path.AddLine(Q(14, 21), Q(11, 21));
                g.DrawPath(ip, path);
            }
        }
    }

    /// <summary>A menu strip button (.hm-mbtn): a 26 px square turned 45° (scale .82), the window's stroke icon, a badge (Mail).</summary>
    internal sealed class MenuStripButton : ModernControl
    {
        private readonly int _icon;
        private readonly bool _hasBadge;
        private string _badge = string.Empty;
        private readonly HudFlash _flash;
        internal MenuEntry Entry;

        public MenuStripButton(int icon, bool badge)
        {
            _icon = icon;
            _hasBadge = badge;
            _flash = new HudFlash { Parent = this, Shape = FlashShape.Diamond };
        }

        public string Badge
        {
            get => _badge;
            set
            {
                value ??= string.Empty;
                if (_badge == value) return;
                _badge = value;
                Invalidate();
            }
        }

        public void Flash()
        {
            float k = K, half = 26 * 0.82f * 0.7071f * k;
            _flash.Size = new Size((int)(half * 2), (int)(half * 2));
            _flash.Location = new Point((int)(Size.Width / 2f - half), (int)(Size.Height / 2f - half));
            _flash.Start(350);
        }

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || !DisplayRectangle.Contains(p)) return false;
            float k = K, half = 26 * 0.82f * 0.7071f * k;
            float x = p.X - DisplayLocation.X - Size.Width / 2f, y = p.Y - DisplayLocation.Y - Size.Height / 2f;
            return Math.Abs(x) + Math.Abs(y) <= half + k;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, cx = Size.Width / 2f, cy = Size.Height / 2f, half = 26 * 0.82f * 0.7071f * k;
            using GraphicsPath d = HudKit.Polygon(new PointF(cx, cy - half), new PointF(cx + half, cy), new PointF(cx, cy + half), new PointF(cx - half, cy));
            using (GraphicsPath ring = HudKit.Polygon(new PointF(cx, cy - half - 1), new PointF(cx + half + 1, cy), new PointF(cx, cy + half + 1), new PointF(cx - half - 1, cy)))
                ModernSkin.Stroke(g, ring, Color.Black);
            ModernSkin.FillVertical(g, d, new RectangleF(cx - half, cy - half, half * 2, half * 2), new[] { ModernSkin.Opaque(ModernTheme.PanelA), ModernSkin.Opaque(ModernTheme.PanelB) });
            ModernSkin.Stroke(g, d, Hovered ? ModernTheme.Accent : ModernTheme.Frame);
            if (_hasBadge && _badge.Length > 0)
            {
                Font f = ModernSkin.Font(9, true);
                float bw = Math.Max(14 * k, ModernSkin.Measure(_badge, f) + 6 * k), bh = 14 * k;
                RectangleF r = new RectangleF(Size.Width - bw, 0, bw, bh);
                using (GraphicsPath bp = ModernSkin.RoundRect(r, bh / 2f))
                {
                    using (var b = new SolidBrush(ModernTheme.Accent)) g.FillPath(b, bp);
                    ModernSkin.Stroke(g, bp, Color.Black);
                }
                ModernSkin.Text(g, _badge, f, Color.White, r, StringAlignment.Center, StringAlignment.Center, false);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            float k = K, s = 15 * k;
            HudKit.DrawIcon(_icon, new RectangleF(DisplayLocation.X + (Size.Width - s) / 2f, DisplayLocation.Y + (Size.Height - s) / 2f, s, s), Hovered ? Color.White : ModernTheme.AccentDim);
        }

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            if (Entry != null)
            {
                string k = Entry.Bind.HasValue ? HudKit.KeyLabel(Entry.Bind.Value) : string.Empty;
                string name = HudKit.T(Entry.Name);
                Hint = k.Length > 0 ? HudKit.T(ClientTextKeys.ModernHudWindowKey, name, k) : name;
            }
        }
    }

    /// <summary>A buff (left row) or poison (right row) icon with its time left below (.hm-buff); ending buffs blink.</summary>
    internal sealed class BuffCell : ModernControl
    {
        public const int W = 26, H = 39;

        private readonly ClientBuff _buff;
        private readonly PoisonType _poison;
        private readonly MirGraphics.MLibrary _lib;
        private readonly int _index = -1;
        private string _text = string.Empty;
        private long _textKey = long.MinValue;
        private bool _ending;
        private int _blinkStep = -1;

        public BuffCell(ClientBuff buff)
        {
            _buff = buff;
            Size = new Size(S(W), S(H));
            HudKit.BuffIcon(buff.Type, out _lib, out _index);
            UpdateTime();
        }

        public BuffCell(PoisonType poison)
        {
            _poison = poison;
            Size = new Size(S(W), S(H));
            Hint = poison.ToLocalizedString();
        }

        public ClientBuff Buff => _buff;
        public string Text => _text;

        /// <summary>The time left text: rebuilt only when what it shows changes (once a second at most).</summary>
        public void UpdateTime()
        {
            if (_buff == null) return;
            long key;
            string text;
            bool ending = false;
            if (_buff.Infinite)
            {
                key = -1;
                text = "∞";
            }
            else if (_buff.Paused)
            {
                key = -2;
                text = "II";
            }
            else
            {
                double left = (_buff.ExpireTime - CMain.Time) / 1000.0;
                ending = left < 10;
                key = left >= 86400 ? 1000000 + (long)(left / 86400) : left >= 3600 ? 100000 + (long)(left / 3600) : left >= 60 ? 10000 + (long)(left / 60) : (long)Math.Max(0, Math.Ceiling(left));
                if (key == _textKey) text = _text;
                else text = HudKit.FmtShort(left);
            }
            if (ending != _ending)
            {
                _ending = ending;
                if (!ending) Opacity = 1f;
                _blinkStep = -1;
            }
            if (key == _textKey) return;
            _textKey = key;
            _text = text;
            Invalidate();
            if (Hovered) Hint = BuffText();
        }

        /// <summary>Ending (under 10 s): blinks (.6 s, two steps) by Opacity.</summary>
        public void Blink()
        {
            if (!_ending) return;
            int step = (int)(CMain.Time % 600 / 300);
            if (step == _blinkStep) return;
            _blinkStep = step;
            Opacity = step == 0 ? 1f : 0.35f;
        }

        protected override void OnMouseEnter()
        {
            if (_buff != null) Hint = BuffText();
            base.OnMouseEnter();
        }

        /// <summary>The classic BuffDialog's buff hint (name, effect, stats, expiry, caster).</summary>
        private string BuffText()
        {
            ClientBuff buff = _buff;
            string text = RegexFunctions.SeperateCamelCase(buff.Type.ToLocalizedString()) + "\n";
            bool overrideStats = false;
            int v0 = buff.Values != null && buff.Values.Length > 0 ? buff.Values[0] : 0;
            switch (buff.Type)
            {
                case BuffType.GameMaster:
                    GMOptions options = (GMOptions)v0;
                    if (options.HasFlag(GMOptions.GameMaster)) text += HudKit.T(ClientTextKeys.Invisible);
                    if (options.HasFlag(GMOptions.Superman)) text += HudKit.T(ClientTextKeys.Superman);
                    if (options.HasFlag(GMOptions.Observer)) text += HudKit.T(ClientTextKeys.Observer);
                    break;
                case BuffType.MentalState:
                    if (v0 == 0) text += HudKit.T(ClientTextKeys.AgressiveFullDamageCantShootOverWalls);
                    else if (v0 == 1) text += HudKit.T(ClientTextKeys.TrickShotMinimalDamage);
                    else if (v0 == 2) text += HudKit.T(ClientTextKeys.GroupModeMediumDamageDontStealAgro);
                    break;
                case BuffType.Hiding:
                case BuffType.ClearRing: text += HudKit.T(ClientTextKeys.InvisibleToManyMonsters); break;
                case BuffType.MoonLight: text += HudKit.T(ClientTextKeys.InvisibleToPlayersAndMonstersAtDistance); break;
                case BuffType.EnergyShield:
                    overrideStats = true;
                    text += HudKit.T(ClientTextKeys.ChanceGainHpWhenAttacked, buff.Stats[Stat.EnergyShieldPercent], buff.Stats[Stat.EnergyShieldHPGain]);
                    break;
                case BuffType.DarkBody: text += HudKit.T(ClientTextKeys.InvisibleToManyMonstersAbleToMove); break;
                case BuffType.VampireShot: text += HudKit.T(ClientTextKeys.GivesVampiricAbility); break;
                case BuffType.PoisonShot: text += HudKit.T(ClientTextKeys.GivesPoisonAbility); break;
                case BuffType.Concentration: text += HudKit.T(ClientTextKeys.IncreaseElementExtractionChance); break;
                case BuffType.MagicBooster:
                    overrideStats = true;
                    text += HudKit.T(ClientTextKeys.IncreaseMcAndConsumption, buff.Stats[Stat.MinMC], buff.Stats[Stat.MaxMC], buff.Stats[Stat.ManaPenaltyPercent]);
                    break;
                case BuffType.Transform: text += HudKit.T(ClientTextKeys.DisguisesYourAppearance); break;
                case BuffType.Mentee: text += HudKit.T(ClientTextKeys.LearnSkillPointsTwiceAsQuick); break;
                case BuffType.Blindness: text += HudKit.T(ClientTextKeys.ReducesVisibility); break;
                case BuffType.Newbie: text += HudKit.T(ClientTextKeys.GuildMemberBoost); break;
            }
            if (!overrideStats && buff.Stats != null)
            {
                foreach (var val in buff.Stats.Values)
                {
                    string c = HudKit.T(val.Value < 0 ? ClientTextKeys.Decreases : ClientTextKeys.Increases);
                    string name = val.Key.ToString();
                    string sign = name.Contains("Percent") ? "%" : name.Contains("Multiplier") ? "x" : "";
                    text += HudKit.T(ClientTextKeys.BuffEffect, c, val.Key.ToLocalizedString(), val.Value, sign);
                }
            }
            if (buff.Paused) text += HudKit.T(ClientTextKeys.ExpirePaused);
            else if (buff.Infinite) text += HudKit.T(ClientTextKeys.ExpireNever);
            else text += HudKit.T(ClientTextKeys.Expire, Functions.PrintTimeSpanFromSeconds(Math.Round((buff.ExpireTime - CMain.Time) / 1000D)));
            if (!string.IsNullOrEmpty(buff.Caster)) text += HudKit.T(ClientTextKeys.CasterName, buff.Caster);
            return text;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, s = W * k;
            RectangleF icon = new RectangleF(0.5f, 0.5f, s - 1f, s - 1f);
            float rad = ModernTheme.Radius * 0.7f * k;
            using (GraphicsPath ring = ModernSkin.RoundRect(icon, rad + 1)) ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(icon, -1, -1), rad);
            if (_buff == null)
            {
                Color pz = HudKit.PoisonColour(_poison);
                if (pz.IsEmpty) pz = ModernTheme.Bad;
                using (var bp = new GraphicsPath())
                {
                    bp.AddEllipse(icon.X - icon.Width * 0.25f, icon.Y - icon.Height * 0.4f, icon.Width * 1.5f, icon.Height * 1.5f);
                    using var brush = new PathGradientBrush(bp)
                    {
                        CenterColor = ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelC), ModernSkin.Opaque(ModernTheme.PanelB), 0.7f),
                        SurroundColors = new[] { Color.FromArgb(255, 5, 7, 11) },
                        CenterPoint = new PointF(icon.X + icon.Width / 2f, icon.Y + icon.Height * 0.35f),
                    };
                    g.FillPath(brush, p);
                }
                ModernSkin.Stroke(g, p, pz);
            }
            else
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 5, 7, 11))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            if (_text.Length > 0)
                HudKit.ShadowText(g, _text, ModernSkin.Font(9, true), ModernTheme.Text, new RectangleF(-4 * k, s + 1 * k, s + 8 * k, 11 * k), StringAlignment.Center, StringAlignment.Center, 200);
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            float k = K, s = W * k;
            if (_buff != null)
            {
                if (_lib != null && _index > 0) HudKit.DrawScaled(_lib, _index, new RectangleF(DisplayLocation.X + 1.5f, DisplayLocation.Y + 1.5f, s - 3f, s - 3f), Color.White);
                return;
            }
            Color pz = HudKit.PoisonColour(_poison);
            if (pz.IsEmpty) pz = ModernTheme.Bad;
            float i = 17 * k;
            HudKit.DrawIcon(ModernIcons.Drop, new RectangleF(DisplayLocation.X + (s - i) / 2f, DisplayLocation.Y + (s - i) / 2f, i, i), ModernTheme.Mix(pz, Color.White, 0.7f));
        }
    }

    /// <summary>
    /// The durability strip (.hdura): worn gear at or under half durability, worst first (CharacterDuraPanel: amulets by Count /
    /// StackSize, everything else by CurrentDura / MaxDura; dura-less items skipped), up to 8 cells. Hover shows the details
    /// card (the HUD popover, display only); a click opens the Character window.
    /// </summary>
    internal sealed class DuraStrip : ModernControl
    {
        private const float Warn = 0.5f, Danger = 0.2f;
        private readonly HudPopover _pop;
        private readonly List<(string Name, int Image, float Frac)> _low = new List<(string, int, float)>();
        private string _key = string.Empty;

        public DuraStrip(HudPopover pop)
        {
            _pop = pop;
            _bars = new DuraBars(this) { Parent = this };
        }

        public int Count => _low.Count;

        public void Rebuild(UserObject u)
        {
            _low.Clear();
            UserItem[] eq = u.Equipment;
            if (eq != null)
                for (int i = 0; i < eq.Length; i++)
                {
                    UserItem it = eq[i];
                    ItemInfo info = it?.Info;
                    if (info == null) continue;
                    bool amulet = info.Type == ItemType.Amulet;
                    int cur = amulet ? it.Count : it.CurrentDura, max = amulet ? info.StackSize : it.MaxDura;
                    if (max <= 0) continue;
                    float frac = Math.Min(1f, Math.Max(0, cur) / (float)max);
                    if (frac <= Warn) _low.Add((info.FriendlyName, info.Image, frac));
                }
            _low.Sort((a, b) => a.Frac.CompareTo(b.Frac));
            if (_low.Count > 8) _low.RemoveRange(8, _low.Count - 8);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _low.Count; i++) sb.Append(_low[i].Image).Append(':').Append((int)Math.Round(_low[i].Frac * 100)).Append('|');
            string key = sb.ToString();
            if (key == _key) return;
            _key = key;
            Visible = _low.Count > 0;
            int n = Math.Max(1, _low.Count);
            Size = new Size(S(n * 28 + (n - 1) * 4), S(28));
            _bars.Size = Size;
            _bars.Invalidate();
            Invalidate();
            if (_pop.IsFor(this)) ShowCard();
        }

        private static Color Col(float f) => f <= 0 ? ModernTheme.Dim : f <= Danger ? ModernTheme.Bad : ModernTheme.Warn;

        protected override void Paint(Graphics g)
        {
            float k = K, c = 28 * k;
            for (int i = 0; i < _low.Count; i++)
            {
                float x = i * 32 * k;
                RectangleF r = new RectangleF(x + 0.5f, 0.5f, c - 1, c - 1);
                using (GraphicsPath ring = ModernSkin.RoundRect(r, 8 * k)) ModernSkin.Stroke(g, ring, Color.Black);
                using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(r, -1, -1), 7 * k);
                using (var bp = new GraphicsPath())
                {
                    bp.AddEllipse(r.X - r.Width * 0.25f, r.Y - r.Height * 0.4f, r.Width * 1.5f, r.Height * 1.5f);
                    using var brush = new PathGradientBrush(bp) { CenterColor = HudKit.Sq(ModernTheme.PanelC), SurroundColors = new[] { HudKit.Sq(ModernTheme.PanelB) },
                        CenterPoint = new PointF(r.X + r.Width / 2f, r.Y + r.Height * 0.35f) };
                    g.FillPath(brush, p);
                }
                Color dc = Col(_low[i].Frac);
                ModernSkin.Stroke(g, p, dc);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            float k = K, c = 28 * k;
            for (int i = 0; i < _low.Count; i++)
            {
                float x = DisplayLocation.X + i * 32 * k;
                float s = 22 * k;
                bool broken = _low[i].Frac <= 0;
                HudKit.DrawScaled(Libraries.Items, _low[i].Image, new RectangleF(x + (c - s) / 2f, DisplayLocation.Y + (c - s) / 2f - k, s, s),
                    broken ? Color.FromArgb(255, 110, 110, 110) : Color.White);
            }
        }

        private readonly DuraBars _bars;

        internal List<(string Name, int Image, float Frac)> Low => _low;

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            ShowCard();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            if (_pop.IsFor(this)) _pop.Close();
        }

        private void ShowCard()
        {
            if (_low.Count == 0) return;
            const int width = 230, pad = 10, rowH = 18;
            var card = new PopCard();
            int y = 8;
            new ModernLabel { Parent = card, Location = new Point(S(pad), S(y)), Size = new Size(S(width - pad * 2), S(14)), Text = HudKit.T(ClientTextKeys.ModernHudDuraTitle),
                FontSize = 10, Bold = true, Colour = ModernTheme.Accent };
            y += 20;
            for (int i = 0; i < _low.Count; i++)
            {
                new DuraRow(_low[i].Name, _low[i].Frac, Col(_low[i].Frac)) { Parent = card, Location = new Point(S(pad), S(y)), Size = new Size(S(width - pad * 2), S(rowH)) };
                y += rowH;
            }
            var foot = new ModernLabel { Parent = card, Location = new Point(S(pad), S(y + 6)), Size = new Size(S(width - pad * 2), S(28)), Text = HudKit.T(ClientTextKeys.ModernHudDuraFoot),
                FontSize = 10, Colour = ModernTheme.Dim, Wrap = true };
            y += 6 + 28 + 8;
            card.Size = new Size(S(width), S(y));
            _pop.Show(this, card, DisplayLocation.X + Size.Width, true, false);
        }
    }

    /// <summary>The strip's mini bars and broken slashes (over the item pictures).</summary>
    internal sealed class DuraBars : ModernControl
    {
        private readonly DuraStrip _strip;

        public DuraBars(DuraStrip strip)
        {
            _strip = strip;
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, c = 28 * k;
            var low = _strip.Low;
            for (int i = 0; i < low.Count; i++)
            {
                float x = i * 32 * k, f = low[i].Frac;
                Color dc = f <= 0 ? ModernTheme.Dim : f <= 0.2f ? ModernTheme.Bad : ModernTheme.Warn;
                RectangleF bar = new RectangleF(x + 3 * k, c - 5 * k, c - 6 * k, 3 * k);
                using (var b = new SolidBrush(Color.FromArgb(255, 10, 10, 12))) g.FillRectangle(b, bar);
                using (var b = new SolidBrush(dc)) g.FillRectangle(b, bar.X, bar.Y, bar.Width * f, bar.Height);
                if (f <= 0)
                {
                    using var pen = new Pen(ModernTheme.Bad, 2 * k);
                    g.DrawLine(pen, x + 4 * k, c * 0.68f, x + c - 4 * k, c * 0.32f);
                }
            }
        }
    }

    /// <summary>A row of the durability card: name, bar, percent (or "broken").</summary>
    internal sealed class DuraRow : ModernControl
    {
        private readonly string _name;
        private readonly float _frac;
        private readonly Color _colour;

        public DuraRow(string name, float frac, Color colour)
        {
            _name = name ?? string.Empty;
            _frac = frac;
            _colour = colour;
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height;
            float pctW = 44 * k, barW = 70 * k, gap = 6 * k;
            Font f = ModernSkin.Font(11);
            ModernSkin.Text(g, _name, f, ModernTheme.Text, new RectangleF(0, 0, w - pctW - barW - gap * 2, h));
            RectangleF bar = new RectangleF(w - pctW - gap - barW, h / 2f - 2 * k, barW, 4 * k);
            using (GraphicsPath p = ModernSkin.RoundRect(bar, 2 * k))
            using (var b = new SolidBrush(Color.FromArgb(255, 4, 6, 10))) g.FillPath(b, p);
            if (_frac > 0)
                using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(bar.X, bar.Y, bar.Width * _frac, bar.Height), 2 * k))
                using (var b = new SolidBrush(_colour)) g.FillPath(b, p);
            string pct = _frac <= 0 ? HudKit.T(ClientTextKeys.ModernHudDuraBroken) : (int)Math.Round(_frac * 100) + "%";
            ModernSkin.Text(g, pct, f, _colour, new RectangleF(w - pctW, 0, pctW, h), StringAlignment.Far);
        }
    }

    /// <summary>A rising float over the diamond (.hm-float): "LEVEL UP!" (big, Georgia) or "+x% EXP"; 1.4 s, 14 steps.</summary>
    internal sealed class FloatText : ModernControl
    {
        private readonly string _text;
        private readonly bool _big;
        private HudAnim _anim;
        public Point Home;

        public FloatText(string text, bool big)
        {
            _text = text ?? string.Empty;
            _big = big;
            NotControl = true;
            Font f = big ? HudKit.Serif(18) : ModernSkin.Font(12, true);
            Size = new Size((int)Math.Ceiling(ModernSkin.Measure(_text, f)) + S(16), S(big ? 28 : 20));
            Opacity = 0f;
            _anim.Begin(1400);
        }

        /// <summary>Steps the rise / fade; false when done.</summary>
        public bool Tick()
        {
            int s = _anim.Step(14);
            if (s >= 14) return false;
            if (s < 0 || s == _anim.Last) return true;
            _anim.Last = s;
            float t = s / 14f;
            // hudm-rise: 0% opacity 0 at +8, 15% opacity 1, 100% opacity 0 at -34
            Opacity = t < 0.15f ? t / 0.15f : 1f - (t - 0.15f) / 0.85f;
            float dy = 8 - 42 * (1f - (1f - t) * (1f - t));
            Location = new Point(Home.X, Home.Y + S(dy));
            return true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0, 0, Size.Width, Size.Height);
            if (_big)
            {
                Font f = HudKit.Serif(18);
                for (int i = 3; i >= 1; i--)
                    ModernSkin.Text(g, _text, f, ModernTheme.Alpha(ModernTheme.Accent, 0.18f), RectangleF.Inflate(r, i * k, i * k), StringAlignment.Center, StringAlignment.Center, false);
                HudKit.ShadowText(g, _text, f, Color.White, r, StringAlignment.Center, StringAlignment.Center, 200);
            }
            else HudKit.ShadowText(g, _text, ModernSkin.Font(12, true), ModernTheme.AccentDim, r, StringAlignment.Center, StringAlignment.Center, 220);
        }
    }
}
