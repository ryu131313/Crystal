// Ported from WebClient/src/ui/modern/windows/character.ts (CharacterWindow + Paperdoll + dollLook): the new UI's character
// window. Name / level + class / guild / guild rank, a paperdoll between two columns of five equipment cells, four cells under
// it, and an arrow tab that slides the Stats panel out (STATS I / II formats, HP / MP included).
// Desktop changes: a Hero tab while the user has a hero (the hero's equipment, doll and stats, and its bag under the doll —
// the web's separate hero window is a later phase); the cells are ModernItemCell on the Equipment / HeroEquipment grids
// (click pick / drop through ItemMoves); the doll is drawn from the libraries (MLibrary draws with a sprite scale), the base
// body is the CharacterPage (Prguse 340 / 341) figure box with the page keyed out (as the web does), under the armour too; the Stats
// panel widens the window instead of hanging outside it. Live values are polled every 250 ms and re-read only when their
// signature moved (PlayerState has no change events).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using DxMatrix = SlimDX.Matrix;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>What the doll shows (CharacterPage.AfterDraw's inputs).</summary>
    internal struct DollLook : IEquatable<DollLook>
    {
        public bool Female;
        public int Wing, Armour, Weapon, Helmet, Hair, HairX, HairY;

        public bool Equals(DollLook o) => Female == o.Female && Wing == o.Wing && Armour == o.Armour && Weapon == o.Weapon && Helmet == o.Helmet && Hair == o.Hair && HairX == o.HairX && HairY == o.HairY;
        public override bool Equals(object obj) => obj is DollLook o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Female, Wing, Armour, Weapon, Helmet, Hair);

        /// <summary>CharacterPage.AfterDraw as data: armour / weapon by GetRealItem (the wearer's level and class), the helmet by its own
        /// Image, else the hair; wings (Prguse2 1202 / 1204 + gender) for WingEffect 1 / 2 with an armour on.</summary>
        public static DollLook Of(UserItem[] equipment, ushort level, MirClass cls, MirGender gender, byte hair, int wingEffect, MirGender wingGender)
        {
            var l = new DollLook { Female = gender != MirGender.Male, Wing = -1, Armour = -1, Weapon = -1, Helmet = -1, Hair = -1 };
            UserItem Slot(EquipmentSlot s) => equipment != null && (int)s < equipment.Length ? equipment[(int)s] : null;
            int Real(UserItem it)
            {
                if (it?.Info == null) return -1;
                ItemInfo real = Functions.GetRealItem(it.Info, level, cls, GameScene.ItemInfoList);
                return real?.Image ?? it.Info.Image;
            }
            UserItem armour = Slot(EquipmentSlot.Armour), weapon = Slot(EquipmentSlot.Weapon), helmet = Slot(EquipmentSlot.Helmet);
            if (armour != null)
            {
                l.Armour = Real(armour);
                if (wingEffect == 1 || wingEffect == 2) l.Wing = 1200 + (wingEffect == 1 ? 2 : 4) + (wingGender == MirGender.Male ? 0 : 1);
            }
            if (weapon != null) l.Weapon = Real(weapon);
            if (helmet != null) l.Helmet = helmet.Info?.Image ?? -1;
            else
            {
                bool assassin = cls == MirClass.Assassin, male = gender == MirGender.Male;
                l.Hair = 441 + hair + (assassin ? 20 : 0) + (male ? 0 : 40);
                l.HairX = assassin ? (male ? 6 : 4) : 0;
                l.HairY = assassin ? (male ? 25 : 18) : 0;
            }
            return l;
        }
    }

    /// <summary>
    /// The paperdoll (character.ts Paperdoll): the figure box's radial glow, then the base body, wings, armour, weapon and helmet
    /// / hair, placed so the body's centre sits on the box's centre line and scaled as large as fits (the weapon and wings may
    /// reach <see cref="Bleed"/> device px past the sides). Draws only when its window re-renders; <see cref="Set"/> redraws on a
    /// look change only.
    /// </summary>
    internal sealed class Paperdoll : ModernControl
    {
        // The CharacterPage figure box: Prguse 340 / 341 at (8, 90) in the dialog, the box (78, 52, 96, 184) inside it.
        private const int BodyX = 86, BodyY = 142, BodyW = 96, BodyH = 184;
        private DollLook _look;
        private bool _has;
        public int Bleed;

        public Paperdoll()
        {
            NotControl = true;
        }

        public void Set(DollLook look)
        {
            if (_has && look.Equals(_look)) return;
            _look = look;
            _has = true;
            Redraw();
        }

        protected override void Paint(Graphics g)
        {
            // radial-gradient(ellipse 60% 45% at 50% 55%, accent 14%, transparent 70%) + the floor shadow
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(Size.Width * 0.5f - Size.Width * 0.6f, Size.Height * 0.55f - Size.Height * 0.45f, Size.Width * 1.2f, Size.Height * 0.9f);
                using var b = new PathGradientBrush(p) { CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.16f), SurroundColors = new[] { Color.FromArgb(0, ModernTheme.Accent) } };
                b.CenterPoint = new PointF(Size.Width * 0.5f, Size.Height * 0.55f);
                g.FillRectangle(b, 0, 0, Size.Width, Size.Height);
            }
            using (var p = new GraphicsPath())
            {
                RectangleF e = new RectangleF(Size.Width * 0.05f, Size.Height * 0.86f, Size.Width * 0.9f, Size.Height * 0.14f);
                p.AddEllipse(e);
                using var b = new PathGradientBrush(p) { CenterColor = Color.FromArgb(150, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) } };
                g.FillPath(b, p);
            }
        }

        private static readonly RenderTexture[] _bodies = new RenderTexture[2];

        /// <summary>
        /// The base body (character.ts makeBaseBody): the figure's box of CharacterPage (Prguse 340 male / 341 female) with the
        /// dark page texture and grey slot frames keyed out (near black or unsaturated pixels). One texture per gender, made once
        /// (again after a device loss disposed it); null while the page cannot be read.
        /// </summary>
        private static unsafe RenderTexture BaseBody(bool female)
        {
            int g = female ? 1 : 0;
            RenderTexture t = _bodies[g];
            if (t != null && !t.Disposed) return t;
            if (Libraries.Prguse == null) return null;
            byte[] page = Libraries.Prguse.GetPixels(340 + g, out Size size);
            if (page == null) return null;
            const int cx = BodyX - 8, cy = BodyY - 90;
            if (size.Width < cx + BodyW || size.Height < cy + BodyH) return null;

            t = DXManager.CreateOwnedTexture(BodyW, BodyH);
            byte* dst = DXManager.LockTexture(t);
            try
            {
                for (int y = 0; y < BodyH; y++)
                    for (int x = 0; x < BodyW; x++)
                    {
                        int s = ((cy + y) * size.Width + cx + x) * 4, d = (y * BodyW + x) * 4;
                        byte b = page[s], gr = page[s + 1], r = page[s + 2];
                        int hi = Math.Max(r, Math.Max(gr, b)), lo = Math.Min(r, Math.Min(gr, b));
                        bool keep = hi >= 48 && hi - lo >= 14;
                        dst[d] = b; dst[d + 1] = gr; dst[d + 2] = r;
                        dst[d + 3] = keep ? page[s + 3] : (byte)0;
                    }
            }
            finally
            {
                DXManager.UnlockTexture(t);
            }
            _bodies[g] = t;
            return t;
        }

        private static void Grow(MLibrary lib, int index, int dx, int dy, ref float x0, ref float y0, ref float x1, ref float y1, bool core, ref float cx0, ref float cx1)
        {
            if (lib == null || index < 0) return;
            Size s = lib.GetSize(index);
            if (s.IsEmpty) return;
            Point o = lib.GetOffSet(index);
            float x = o.X + dx, y = o.Y + dy;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + s.Width); y1 = Math.Max(y1, y + s.Height);
            if (core) { cx0 = Math.Min(cx0, x); cx1 = Math.Max(cx1, x + s.Width); }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (!_has || Libraries.StateItems == null) return;
            DollLook l = _look;

            float x0 = BodyX, y0 = BodyY, x1 = BodyX + BodyW, y1 = BodyY + BodyH, cx0 = BodyX, cx1 = BodyX + BodyW;
            Grow(Libraries.Prguse2, l.Wing, 0, 0, ref x0, ref y0, ref x1, ref y1, false, ref cx0, ref cx1);
            Grow(Libraries.StateItems, l.Armour, 0, 0, ref x0, ref y0, ref x1, ref y1, true, ref cx0, ref cx1);
            Grow(Libraries.StateItems, l.Weapon, 0, 0, ref x0, ref y0, ref x1, ref y1, false, ref cx0, ref cx1);
            if (l.Helmet >= 0) Grow(Libraries.StateItems, l.Helmet, 0, 0, ref x0, ref y0, ref x1, ref y1, true, ref cx0, ref cx1);
            else Grow(Libraries.Prguse, l.Hair, l.HairX, l.HairY, ref x0, ref y0, ref x1, ref y1, true, ref cx0, ref cx1);

            float fw = Size.Width, fh = Size.Height, apex = BodyX + BodyW / 2f;
            float core = Math.Max(1f, Math.Max(apex - cx0, cx1 - apex));
            float k = Math.Min((fh - 4) / Math.Max(1f, y1 - y0), Math.Min((fw / 2 - 4) / core, (fw / 2 + Bleed - 2) / Math.Max(1f, Math.Max(apex - x0, x1 - apex))));
            if (k <= 0.05f) return;
            float ox = DisplayLocation.X + fw / 2 - apex * k, oy = DisplayLocation.Y + (fh - (y1 - y0) * k) / 2 - y0 * k;
            Point At(int dx, int dy) => new Point((int)Math.Round(ox / k) + dx, (int)Math.Round(oy / k) + dy);

            try
            {
                // base body: the CharacterPage's figure box with the page texture keyed out, always (the armour pictures have no
                // head: classic draws them over the whole page)
                RenderTexture body = BaseBody(l.Female);
                if (body != null)
                {
                    DXManager.SetSpriteTransform(DxMatrix.Scaling(k, k, 0));
                    Point b = At(BodyX, BodyY);
                    DXManager.Draw(body, new Rectangle(0, 0, BodyW, BodyH), new SlimDX.Vector3(b.X, b.Y, 0), Color.White);
                    DXManager.SetSpriteTransform(DxMatrix.Identity);
                }
                if (l.Wing >= 0)
                {
                    // wings are drawn additively (CharacterPage: Prguse2.DrawBlend)
                    DXManager.SetBlend(true, 1F);
                    DXManager.SetSpriteTransform(DxMatrix.Scaling(k, k, 0));
                    Libraries.Prguse2.Draw(l.Wing, At(0, 0), Color.White, true);
                    DXManager.SetSpriteTransform(DxMatrix.Identity);
                    DXManager.SetBlend(false);
                }

                DXManager.SetSpriteTransform(DxMatrix.Scaling(k, k, 0));
                if (l.Armour >= 0) Libraries.StateItems.Draw(l.Armour, At(0, 0), Color.White, true, 1F);
                if (l.Weapon >= 0) Libraries.StateItems.Draw(l.Weapon, At(0, 0), Color.White, true, 1F);
                if (l.Helmet >= 0) Libraries.StateItems.Draw(l.Helmet, At(0, 0), Color.White, true, 1F);
                else if (l.Hair >= 0) Libraries.Prguse.Draw(l.Hair, At(l.HairX, l.HairY), Color.White, true, 1F);
            }
            finally
            {
                DXManager.SetSpriteTransform(DxMatrix.Identity);
                DXManager.SetBlend(false);
            }
        }
    }

    public sealed class CharacterWindow : ModernWindow
    {
        public const string WindowKey = "character";
        private const int Width = 360, StatsW = 270, BaseHeight = 446;

        private static readonly (EquipmentSlot Slot, ClientTextKeys Name, int Icon)[] LeftSlots =
        {
            (EquipmentSlot.Weapon, ClientTextKeys.ModernWinSlotWeapon, ModernIcons.Sword), (EquipmentSlot.Armour, ClientTextKeys.ModernWinSlotArmour, ModernIcons.Shield),
            (EquipmentSlot.Torch, ClientTextKeys.ModernWinSlotTorch, ModernIcons.Sun), (EquipmentSlot.BraceletL, ClientTextKeys.ModernWinSlotBracelet, ModernIcons.Ring),
            (EquipmentSlot.RingL, ClientTextKeys.ModernWinSlotRing, ModernIcons.Ring),
        };
        private static readonly (EquipmentSlot Slot, ClientTextKeys Name, int Icon)[] RightSlots =
        {
            (EquipmentSlot.Helmet, ClientTextKeys.ModernWinSlotHelmet, ModernIcons.Cap), (EquipmentSlot.Necklace, ClientTextKeys.ModernWinSlotNecklace, ModernIcons.Gem),
            (EquipmentSlot.Amulet, ClientTextKeys.ModernWinSlotAmulet, ModernIcons.Tag), (EquipmentSlot.BraceletR, ClientTextKeys.ModernWinSlotBracelet, ModernIcons.Ring),
            (EquipmentSlot.RingR, ClientTextKeys.ModernWinSlotRing, ModernIcons.Ring),
        };
        private static readonly (EquipmentSlot Slot, ClientTextKeys Name, int Icon)[] BottomSlots =
        {
            (EquipmentSlot.Belt, ClientTextKeys.ModernWinSlotBelt, ModernIcons.Pouch), (EquipmentSlot.Boots, ClientTextKeys.ModernWinSlotBoots, ModernIcons.Walk),
            (EquipmentSlot.Stone, ClientTextKeys.ModernWinSlotStone, ModernIcons.Gem), (EquipmentSlot.Mount, ClientTextKeys.ModernWinSlotMount, ModernIcons.Horse),
        };

        /// <summary>The Stats rows (STATS I / II); a null label = a section break.</summary>
        private static readonly ClientTextKeys?[] StatLabels =
        {
            ClientTextKeys.ModernWinStatAC, ClientTextKeys.ModernWinStatMAC, ClientTextKeys.ModernWinStatDC, ClientTextKeys.ModernWinStatMC, ClientTextKeys.ModernWinStatSC, null,
            ClientTextKeys.ModernWinStatHP, ClientTextKeys.ModernWinStatMP, null,
            ClientTextKeys.ModernWinStatAccuracy, ClientTextKeys.ModernWinStatAgility, ClientTextKeys.ModernWinStatAttackSpeed, ClientTextKeys.ModernWinStatLuck,
            ClientTextKeys.ModernWinStatCritRate, ClientTextKeys.ModernWinStatCritDamage, null,
            ClientTextKeys.ModernWinStatMagicResist, ClientTextKeys.ModernWinStatPoisonResist, ClientTextKeys.ModernWinStatHealthRecovery, ClientTextKeys.ModernWinStatManaRecovery,
            ClientTextKeys.ModernWinStatPoisonRecovery, ClientTextKeys.ModernWinStatHoly, ClientTextKeys.ModernWinStatFreeze, ClientTextKeys.ModernWinStatPoisonAttack, null,
            ClientTextKeys.ModernWinStatBagWeight, ClientTextKeys.ModernWinStatWearWeight, ClientTextKeys.ModernWinStatHandWeight, null,
            ClientTextKeys.ModernWinStatExperience, ClientTextKeys.ModernWinStatExpRate, ClientTextKeys.ModernWinStatDropRate, ClientTextKeys.ModernWinStatGoldRate,
        };

        private int _tab; // 0 the user, 1 the hero
        private bool _statsOpen;

        private Tabs _tabs;
        private ModernLabel _name, _line, _guild, _rank;
        private Paperdoll _doll;
        private readonly List<(ModernItemCell Cell, CellDecor Decor, string Name)> _slots = new List<(ModernItemCell, CellDecor, string)>();
        private readonly List<ModernItemCell> _heroBag = new List<ModernItemCell>();
        private readonly List<ModernLabel> _statValues = new List<ModernLabel>();
        private PaintBox _arrow;
        private bool _builtHero;

        private long _nextPoll;
        private long _sig = long.MinValue;
        private readonly GridWatch _watch = new GridWatch(), _bagWatch = new GridWatch();
        private readonly Action<bool> _hasHero;
        private readonly Action _heroInfo;

        public CharacterWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinCharacter), 40, 70)
        {
            ContentWidth = Width;
            _hasHero = _ => { if (!IsDisposed && HeroTabWanted != _builtHero) Layout(); };
            _heroInfo = () => { if (!IsDisposed && HeroTabWanted != _builtHero) Layout(); else _sig = long.MinValue; };
        }

        private static bool HeroTabWanted => GameScene.Hero != null;

        private UserObject Actor => _tab == 1 && GameScene.Hero != null ? GameScene.Hero : GameScene.User;
        private MirGridType EquipGrid => _tab == 1 && GameScene.Hero != null ? MirGridType.HeroEquipment : MirGridType.Equipment;

        internal int ShownTab => _tab;
        internal bool StatsOpen => _statsOpen;

        protected override void Build()
        {
            _builtHero = HeroTabWanted;
            if (!_builtHero) _tab = 0;
            int shift = _builtHero ? 36 : 0;
            const int top = TitleHeight;
            ContentWidth = Width + (_statsOpen ? StatsW : 0);

            if (_builtHero)
            {
                _tabs = new Tabs(new[] { W.T(ClientTextKeys.ModernWinCharacterTab), W.T(ClientTextKeys.ModernWinHeroTab) }, _tab, Width - 28) { Parent = this, Location = new Point(S(14), S(top + 6)) };
                _tabs.Changed += i => { _tab = i; Layout(); };
            }

            // who
            int y = top + shift + 8;
            _name = W.Label(this, string.Empty, 0, y, Width, 20, 16, true, Color.White, StringAlignment.Center);
            _line = W.Label(this, string.Empty, 0, y + 20, Width, 14, 11, false, ModernTheme.Dim, StringAlignment.Center);
            _guild = W.Label(this, string.Empty, 0, y + 34, Width, 14, 11, false, ModernTheme.Dim, StringAlignment.Center);
            _rank = W.Label(this, string.Empty, 0, y + 48, Width, 16, 12, true, ModernTheme.Accent, StringAlignment.Center);

            // body: two columns of five around the doll, four under it
            int body = top + shift + 76;
            _doll = new Paperdoll { Parent = this, Location = new Point(S(22 + 52), S(body)), Size = new Size(S(Width - 44 - 104), S(300)), Bleed = S(30) };
            _slots.Clear();
            MirGridType grid = EquipGrid;
            for (int i = 0; i < LeftSlots.Length; i++) AddSlot(grid, LeftSlots[i], 22, body + 68 + i * 48);
            for (int i = 0; i < RightSlots.Length; i++) AddSlot(grid, RightSlots[i], Width - 22 - 40, body + 68 + i * 48);
            int bx = (Width - (4 * 40 + 3 * 6)) / 2;
            for (int i = 0; i < BottomSlots.Length; i++) AddSlot(grid, BottomSlots[i], bx + i * 46, body + 310);
            int h = BaseHeight + shift;

            // the hero's bag (HeroInventory after its 2 belt cells; the belt is on the HUD)
            _heroBag.Clear();
            if (_tab == 1 && GameScene.Hero != null)
            {
                int bagTop = body + 360;
                W.Caption(this, W.T(ClientTextKeys.ModernWinHeroBag), 22, bagTop, 200);
                var view = new ScrollView(Width - 44, 128) { Parent = this, Location = new Point(S(22), S(bagTop + 18)) };
                UserItem[] inv = GameScene.Hero.Inventory;
                int from = GameScene.User?.HeroBeltIdx ?? 2;
                for (int s = from; s < inv.Length; s++)
                {
                    int p = s - from;
                    var c = new ModernItemCell(MirGridType.HeroInventory, s, 36) { Parent = view.Content, Location = new Point(S(4 + p % 8 * 39), S(4 + p / 8 * 40)) };
                    _heroBag.Add(c);
                }
                view.FitContent();
                h = bagTop + 18 + 128 + 12 - top;
            }

            // the arrow tab on the right edge: opens / closes the Stats panel
            _arrow = new PaintBox(PaintArrow) { Parent = this, Location = new Point(S(Width - 3 - 14), S(top + 90 + shift)), Size = new Size(S(14), S(44)), Hint = W.T(ClientTextKeys.ModernWinStats) };
            _arrow.Click += (o, e) => { _statsOpen = !_statsOpen; Layout(); };

            _statValues.Clear();
            if (_statsOpen)
            {
                W.VDivider(this, Width, top, h);
                W.Label(this, W.T(ClientTextKeys.ModernWinStats).ToUpperInvariant(), Width, top + 4, StatsW, 20, 12, true, ModernTheme.Accent, StringAlignment.Center);
                W.Divider(this, Width + 10, top + 28, StatsW - 20);
                var list = new ScrollView(StatsW - 20, h - 44) { Parent = this, Location = new Point(S(Width + 10), S(top + 34)), Background = W.Page };
                list.Framed = false;
                int ry = 4, odd = 0;
                for (int i = 0; i < StatLabels.Length; i++)
                {
                    if (StatLabels[i] == null) { ry += 8; continue; }
                    bool shade = odd++ % 2 == 0;
                    new PaintBox((g, b) => { if (shade) using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.07f))) g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height); })
                    { Parent = list.Content, Location = new Point(0, S(ry)), Size = new Size(list.InnerWidth, S(21)), NotControl = true };
                    var name = new ModernLabel { Parent = list.Content, Location = new Point(S(8), S(ry)), Size = new Size(list.InnerWidth / 2, S(21)), Text = W.T(StatLabels[i].Value), FontSize = 12, Colour = ModernTheme.Dim };
                    var val = new ModernLabel { Parent = list.Content, Location = new Point(list.InnerWidth / 2, S(ry)), Size = new Size(list.InnerWidth / 2 - S(8), S(21)), FontSize = 12, Bold = true, Align = StringAlignment.Far };
                    _statValues.Add(val);
                    ry += 21;
                }
                list.ContentHeight = S(ry + 6);
            }
            ContentHeight = h;
            _sig = long.MinValue;
            _watch.Reset();
            _bagWatch.Reset();
            Refresh();
        }

        private void AddSlot(MirGridType grid, (EquipmentSlot Slot, ClientTextKeys Name, int Icon) d, int x, int y)
        {
            var c = new ModernItemCell(grid, (int)d.Slot, 40) { Parent = this, Location = new Point(S(x), S(y)) };
            _slots.Add((c, new CellDecor(c, d.Icon), W.T(d.Name)));
        }

        private void PaintArrow(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * 0.8f * k);
            ModernSkin.FillVertical(g, p, r, new[] { ModernSkin.Opaque(ModernTheme.PanelA), ModernSkin.Opaque(ModernTheme.PanelB) });
            ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : ModernTheme.Frame);
            float cx = b.Size.Width / 2f, cy = b.Size.Height / 2f, d = 3f * k;
            using var pen = new Pen(b.Hovered ? Color.White : ModernTheme.Accent, Math.Max(1.2f, 1.6f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float dir = _statsOpen ? -1 : 1;
            g.DrawLines(pen, new[] { new PointF(cx - d * dir * 0.6f, cy - d * 1.4f), new PointF(cx + d * dir * 0.6f, cy), new PointF(cx - d * dir * 0.6f, cy + d * 1.4f) });
        }

        protected override void Shown()
        {
            ModernEvents.HasHero.On(_hasHero);
            ModernEvents.HeroInformation.On(_heroInfo);
            ModernEvents.HeroInfo.On(_heroInfo);
            if (HeroTabWanted != _builtHero) Layout();
            _sig = long.MinValue;
            Refresh();
        }

        protected override void Closed()
        {
            ModernEvents.HasHero.Off(_hasHero);
            ModernEvents.HeroInformation.Off(_heroInfo);
            ModernEvents.HeroInfo.Off(_heroInfo);
        }

        public override void Process()
        {
            base.Process();
            if (CMain.Time < _nextPoll) return;
            _nextPoll = CMain.Time + 250;
            UserObject a = Actor;
            if (a == null) return;
            if (_watch.Poll(EquipGrid, a.Equipment, 0, int.MaxValue, true))
            {
                for (int i = 0; i < _slots.Count; i++) _slots[i].Cell.Refresh(false);
                _sig = long.MinValue;
            }
            if (_heroBag.Count > 0 && GameScene.Hero != null && _bagWatch.Poll(MirGridType.HeroInventory, GameScene.Hero.Inventory, 0, int.MaxValue, true))
                for (int i = 0; i < _heroBag.Count; i++) _heroBag[i].Refresh(false);
            Refresh();
        }

        /// <summary>A number that moves when anything the window shows changed (no strings are built unless it did).</summary>
        private long Signature(UserObject a)
        {
            long h = a.Level * 31L + (int)a.Class * 7 + (int)a.Gender * 3 + a.Hair + a.WingEffect * 101L + a.HP * 13L + a.MP * 17L;
            h = h * 1000003 + a.Experience + a.MaxExperience * 3;
            h = h * 1000003 + a.CurrentBagWeight + a.CurrentWearWeight * 7L + a.CurrentHandWeight * 11L;
            if (a.Stats != null)
                for (int s = 0; s <= (int)Stat.GoldDropRatePercent; s++) h = h * 31 + a.Stats[(Stat)s];
            h = h * 31 + (a.GuildName?.GetHashCode() ?? 0) + (a.GuildRankName?.GetHashCode() ?? 0) + (a.Name?.GetHashCode() ?? 0);
            if (a.Equipment != null)
                for (int i = 0; i < a.Equipment.Length; i++) h = h * 31 + (long)(a.Equipment[i]?.UniqueID ?? 0);
            return h;
        }

        private void Refresh()
        {
            UserObject a = Actor;
            if (a == null || _name == null || _name.IsDisposed) return;
            long sig = Signature(a);
            if (sig == _sig) return;
            _sig = sig;

            _name.Text = a.Name ?? string.Empty;
            _line.Text = W.T(ClientTextKeys.ModernWinLevelClass, a.Level, a.Class.ToLocalizedString());
            _line.Colour = ModernTheme.Class(a.Class);
            string guild = _tab == 0 ? a.GuildName ?? string.Empty : string.Empty;
            _guild.Text = guild;
            _rank.Text = guild.Length > 0 ? a.GuildRankName ?? string.Empty : string.Empty;

            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                s.Decor.Sync();
                string hint = s.Cell.Item == null ? W.T(ClientTextKeys.ModernWinSlotEmpty, s.Name) : string.Empty;
                if (s.Cell.Hint != hint) s.Cell.Hint = hint;
            }

            MirGender wingGender = a.Gender;
            _doll.Set(DollLook.Of(a.Equipment, a.Level, a.Class, a.Gender, a.Hair, a.WingEffect, wingGender));

            if (_statsOpen && _statValues.Count > 0 && a.Stats != null)
            {
                Stats st = a.Stats;
                double expPct = a.MaxExperience > 0 ? a.Experience * 100d / a.MaxExperience : 0;
                string[] v =
                {
                    st[Stat.MinAC] + "-" + st[Stat.MaxAC], st[Stat.MinMAC] + "-" + st[Stat.MaxMAC], st[Stat.MinDC] + "-" + st[Stat.MaxDC],
                    st[Stat.MinMC] + "-" + st[Stat.MaxMC], st[Stat.MinSC] + "-" + st[Stat.MaxSC],
                    a.HP + "/" + st[Stat.HP], a.MP + "/" + st[Stat.MP],
                    "+" + st[Stat.Accuracy], "+" + st[Stat.Agility], st[Stat.AttackSpeed].ToString(), st[Stat.Luck].ToString(),
                    st[Stat.CriticalRate] + "%", st[Stat.CriticalDamage].ToString(),
                    "+" + st[Stat.MagicResist], "+" + st[Stat.PoisonResist], "+" + st[Stat.HealthRecovery], "+" + st[Stat.SpellRecovery],
                    "+" + st[Stat.PoisonRecovery], "+" + st[Stat.Holy], "+" + st[Stat.Freezing], "+" + st[Stat.PoisonAttack],
                    a.CurrentBagWeight + "/" + st[Stat.BagWeight], a.CurrentWearWeight + "/" + st[Stat.WearWeight], a.CurrentHandWeight + "/" + st[Stat.HandWeight],
                    (Math.Round(expPct * 100) / 100).ToString("0.##") + "%", "+" + st[Stat.ExpRatePercent] + "%", "+" + st[Stat.ItemDropRatePercent] + "%", "+" + st[Stat.GoldDropRatePercent] + "%",
                };
                for (int i = 0; i < _statValues.Count && i < v.Length; i++) _statValues[i].Text = v[i];
            }
        }
    }
}
