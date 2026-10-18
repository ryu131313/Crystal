// Ported from WebClient/src/ui/modern/hud/skillRow.ts: the HUD's skill row, four slots under each belt row, plus the attack
// mode (left) and pet mode (right) slots at its outer ends.
// Skill row: bar 1 (magic keys 1-8, Bar1Skill1-8); while Ctrl is held, bar 2 (keys 9-16); while Shift is held, the hero's
// skills (keys 17-24). Spells come from User.Magics / Hero.Magics by ClientMagic.Key, icons MagIcon2[Icon * 2] (as the
// classic skill bar), the cooldown sweep from CastTime + Delay quantised to 64 steps (repainted only on a step change), the
// digits per value change, the no-MP tint from BaseCost + LevelCost * Level against the user's / hero's MP, and the armed
// glow only for Crystal's toggle spells (UserObject.Slaying … FlamingSword) while they are on. A slot click casts like the
// classic SkillBarDialog: GameScene.UseSpell(key), with every check it has.
// Mode slots: GameScene.AMode / PMode (set by S.ChangeAMode / S.ChangePMode); a pick sends C.ChangeAMode / C.ChangePMode
// spaced by GameScene.ChangeAModeTime / ChangePModeTime (the keys' spacing) and the slot shows the server's echo only.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class SkillRow
    {
        public readonly SkillSlot[] Slots = new SkillSlot[8];
        private int _view;
        private bool _dirty = true;
        private long _nextBind;
        private bool _noHero;

        public SkillRow(MirControl parent)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                int index = i;
                Slots[i] = new SkillSlot { Parent = parent };
                Slots[i].Click += (o, e) => { if (!(e is MouseEventArgs me) || me.Button == MouseButtons.Left) Cast(index); }; // right-click does nothing (web: contextmenu prevented)
            }
        }

        /// <summary>0 = bar 1, 1 = bar 2 (Ctrl held), 2 = the hero's skills (Shift held).</summary>
        public int View => _view;

        public void SetView(int v)
        {
            if (v == _view) return;
            _view = v;
            _dirty = true;
        }

        public void MarkDirty() => _dirty = true;

        /// <summary>The magic key slot i casts in the current view (1-16, hero 17-24).</summary>
        public int KeyFor(int i) => _view == 2 ? 17 + i : _view * 8 + i + 1;

        public static KeybindOptions SlotOption(int view, int i) => view == 2 ? KeybindOptions.HeroSkill1 + i : KeybindOptions.Bar1Skill1 + view * 8 + i;

        /// <summary>Per frame: re-binds after a change (and once a second: a magic level or key may change without an event), then
        /// the cooldown sweep, the no-MP tint and the armed glow (each slot writes only on a change).</summary>
        public void Process()
        {
            if (_dirty || CMain.Time >= _nextBind) Bind();
            UserObject actor = _view == 2 ? GameScene.Hero : GameScene.User;
            int mp = actor?.MP ?? 0;
            for (int i = 0; i < Slots.Length; i++) Slots[i].Process(actor, mp);
        }

        private void Bind()
        {
            bool force = _dirty;
            _dirty = false;
            _nextBind = CMain.Time + 1000; // the key hints are strings: at most once a second
            UserObject actor = _view == 2 ? GameScene.Hero : GameScene.User;
            bool noHero = _view == 2 && (GameScene.Hero == null || Belt.HeroOff());
            if (noHero != _noHero) force = true;
            _noHero = noHero;
            List<ClientMagic> list = actor?.Magics;
            for (int i = 0; i < Slots.Length; i++)
            {
                int key = KeyFor(i);
                ClientMagic m = null;
                if (list != null)
                    for (int j = 0; j < list.Count; j++)
                        if (list[j].Key == key) { m = list[j]; break; }
                Slots[i].Bind(m, _view, i, noHero, force);
            }
        }

        private void Cast(int i)
        {
            SkillSlot s = Slots[i];
            if (s.Magic == null || GameScene.Scene == null) return;
            s.Flash();
            GameScene.Scene.UseSpell(KeyFor(i));
        }
    }

    /// <summary>
    /// One skill slot (.hm-slot): the well, the spell icon, the cooldown sweep and digits, the key hint, the armed halo.
    /// Everything that covers the icon is drawn on opaque pixels (a translucent overlay in a surface's render target would let
    /// the world show through): the no-MP tint darkens the icon by its sprite colour, and the cooldown sweep redraws the
    /// remaining pie of the icon darkened, as 1 px high runs computed once per sweep step.
    /// </summary>
    internal sealed class SkillSlot : ModernControl
    {
        public const int SweepSteps = 64;

        private ClientMagic _magic;
        private int _icon = -1, _view, _level = -1, _cost = -1;
        private bool _armed, _noMp, _noHero, _cooling;
        private string _shownKey;
        private readonly SlotOverlay _overlay;
        private readonly HudFlash _flash;
        private int _sweepStep = -1, _digitKey = int.MinValue, _haloStep = -1;
        // the sweep's darkened runs in icon pixels: (x, y, length) triples
        private int[] _runs = new int[384];
        private int _runCount;

        public SkillSlot()
        {
            Size = new Size(S(40), S(40));
            _overlay = new SlotOverlay { Parent = this, Size = Size };
            _flash = new HudFlash { Parent = this, Size = Size };
        }

        public ClientMagic Magic => _magic;
        public bool Cooling => _cooling;
        public bool NoMp => _noMp;
        public bool Armed => _armed;

        /// <summary>Sizes the slot and its overlays (device px).</summary>
        public void SetBounds(Rectangle r)
        {
            Location = r.Location;
            Size = r.Size;
            _overlay.Size = r.Size;
            _flash.Size = r.Size;
        }

        public void Flash() => _flash.Start(350);

        /// <summary>Which spell the slot shows (on a magic / key / view change).</summary>
        public void Bind(ClientMagic m, int view, int i, bool noHero, bool force)
        {
            int icon = m != null ? m.Icon * 2 : -1;
            int level = m?.Level ?? -1;
            int cost = m != null ? m.BaseCost + m.LevelCost * m.Level : -1;
            string key = HudKit.ShortKey(HudKit.KeyLabel(SkillRow.SlotOption(view, i)));
            if (!force && m == _magic && icon == _icon && level == _level && cost == _cost && view == _view && noHero == _noHero && key == _shownKey) return;
            bool viewChanged = view != _view || noHero != _noHero;
            bool changed = m != _magic || icon != _icon;
            _magic = m;
            _icon = icon;
            _level = level;
            _cost = cost;
            _view = view;
            _noHero = noHero;
            _shownKey = key;
            if (changed)
            {
                // a new spell: clear the old one's sweep / tint / glow (the next frame redraws what applies, without a flash)
                _cooling = false;
                _armed = false;
                _noMp = false;
                _sweepStep = -1;
                _digitKey = int.MinValue;
                _runCount = 0;
                _haloStep = -1;
                _overlay.SetDigits(string.Empty);
            }
            _overlay.SetKey(key);
            if (m != null)
                Hint = (cost > 0 ? HudKit.T(ClientTextKeys.ModernHudSkillHintMp, m.Name, m.Level, cost) : HudKit.T(ClientTextKeys.ModernHudSkillHint, m.Name, m.Level))
                    + "\n" + HudKit.T(ClientTextKeys.ModernHudSkillCast);
            else
                Hint = noHero ? HudKit.T(ClientTextKeys.ModernHudNoHero)
                    : HudKit.T(ClientTextKeys.ModernHudSkillEmpty, HudKit.T(view == 2 ? ClientTextKeys.ModernHudHeroSkill : view == 1 ? ClientTextKeys.ModernHudSkillBar2 : ClientTextKeys.ModernHudSkillBar1));
            if (changed || viewChanged) Invalidate();
        }

        /// <summary>Per frame: cooldown (none under 100 ms left, magicBar.ts), no-MP tint, armed glow — writes only on a change.</summary>
        public void Process(UserObject actor, int mp)
        {
            ClientMagic m = _magic;
            if (m == null) return;
            long left = m.Delay > 0 ? m.CastTime + m.Delay - CMain.Time : 0;
            if (left >= 100 && m.Delay > 0)
            {
                _cooling = true;
                // the sweep and the digits step on the HUD clock (16 Hz, shared by every slot so the re-renders coalesce);
                // the first step of a new cooldown shows at once
                bool tick = HudClock.Ticked || _sweepStep < 0;
                int step = (int)Math.Ceiling(Math.Min(1.0, left / (double)m.Delay) * SweepSteps);
                int digitKey = left >= 1000 ? (int)Math.Ceiling(left / 1000.0) : -(int)Math.Round(left / 100.0);
                if (tick && digitKey != _digitKey)
                {
                    _digitKey = digitKey;
                    _overlay.SetDigits(left >= 1000 ? digitKey.ToString() : (-digitKey / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (tick && step != _sweepStep)
                {
                    _sweepStep = step;
                    BuildRuns(step / (float)SweepSteps);
                    Redraw(); // the icon runs are sprite draws: re-blit the surface, no repaint
                }
            }
            else if (_cooling)
            {
                _cooling = false;
                _sweepStep = -1;
                _digitKey = int.MinValue;
                _runCount = 0;
                _overlay.SetDigits(string.Empty);
                Redraw();
                _flash.Start(500); // the "ready" glint
            }
            bool noMp = m.BaseCost + m.LevelCost * m.Level > mp;
            if (noMp != _noMp)
            {
                _noMp = noMp;
                Redraw();
            }
            bool armed = actor != null && IsArmed(actor, m.Spell);
            if (armed != _armed)
            {
                _armed = armed;
                _haloStep = -1;
                Invalidate();
            }
            if (_armed)
            {
                // the halo pulses (1.6 s, 8 steps): repainted into the well on a step change
                int hs = (int)(CMain.Time % 1600 / 200);
                if (hs != _haloStep && (HudClock.Ticked || _haloStep < 0))
                {
                    _haloStep = hs;
                    Invalidate();
                }
            }
        }

        /// <summary>The remaining pie (from 12 o'clock clockwise, the last <paramref name="remaining"/> of the turn) as runs of icon pixels.</summary>
        private void BuildRuns(float remaining)
        {
            _runCount = 0;
            if (_icon < 0 || Libraries.MagIcon2 == null) return;
            Size s = Libraries.MagIcon2.GetSize(_icon);
            if (s.Width <= 0 || s.Height <= 0) return;
            float cx = s.Width / 2f, cy = s.Height / 2f, from = (1f - remaining) * 360f;
            for (int y = 0; y < s.Height; y++)
            {
                int start = -1;
                for (int x = 0; x <= s.Width; x++)
                {
                    bool covered = false;
                    if (x < s.Width)
                    {
                        double a = Math.Atan2(x + 0.5 - cx, cy - (y + 0.5)) * 180.0 / Math.PI;
                        if (a < 0) a += 360.0;
                        covered = remaining >= 1f || a >= from;
                    }
                    if (covered && start < 0) start = x;
                    else if (!covered && start >= 0)
                    {
                        if (_runCount + 3 > _runs.Length) Array.Resize(ref _runs, _runs.Length * 2);
                        _runs[_runCount++] = start;
                        _runs[_runCount++] = y;
                        _runs[_runCount++] = x - start;
                        start = -1;
                    }
                }
            }
        }

        /// <summary>Crystal's toggle spells (Magic's toggle flags): only these glow.</summary>
        private static bool IsArmed(UserObject u, Spell spell)
        {
            switch (spell)
            {
                case Spell.Slaying: return u.Slaying;
                case Spell.Thrusting: return u.Thrusting;
                case Spell.HalfMoon: return u.HalfMoon;
                case Spell.CrossHalfMoon: return u.CrossHalfMoon;
                case Spell.DoubleSlash: return u.DoubleSlash;
                case Spell.TwinDrakeBlade: return u.TwinDrakeBlade;
                case Spell.FlamingSword: return u.FlamingSword;
                default: return false;
            }
        }

        protected override void Paint(Graphics g)
        {
            float halo = _armed && _haloStep >= 0 ? (_haloStep < 4 ? _haloStep / 4f : (8 - _haloStep) / 4f) : -1f;
            SlotPainter.Well(g, Size, K, Hovered && _magic != null || _armed, _view == 2, _armed, halo);
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            MirGraphics.MLibrary lib = Libraries.MagIcon2;
            if (_icon < 0 || lib == null) return;
            Size s = lib.GetSize(_icon);
            if (s.Width <= 0 || s.Height <= 0) return;
            float k = K, w = s.Width * k, h = s.Height * k;
            float x = DisplayLocation.X + (Size.Width - w) / 2f, y = DisplayLocation.Y + (Size.Height - h) / 2f;
            Color c = _noHero ? Color.FromArgb(255, 110, 110, 110) : _noMp ? Color.FromArgb(255, 92, 98, 116) : Color.White;
            HudKit.DrawScaled(lib, _icon, new RectangleF(x, y, w, h), c);
            if (_runCount == 0) return;
            // the sweep: the remaining pie of the icon again, at ~32 % (rgba(0,0,0,.68) over the icon)
            Color dark = Color.FromArgb(255, c.R * 82 / 255, c.G * 82 / 255, c.B * 82 / 255);
            DXManager.SetSpriteTransform(SlimDX.Matrix.Scaling(k, k, 1f) * SlimDX.Matrix.Translation(x, y, 0f));
            try
            {
                for (int i = 0; i + 2 < _runCount; i += 3)
                    lib.Draw(_icon, new Rectangle(_runs[i], _runs[i + 1], _runs[i + 2], 1), new Point(_runs[i], _runs[i + 1]), dark, false);
            }
            finally
            {
                DXManager.SetSpriteTransform(SlimDX.Matrix.Identity);
            }
        }
    }

    /// <summary>The well every HUD slot shares (.hm-slot: radial pc→pb, frame, black ring, the sheen and inner shadow).</summary>
    internal static class SlotPainter
    {
        /// <param name="halo">The armed halo's pulse (0-1), or below 0 for none.</param>
        public static void Well(Graphics g, Size size, float k, bool hot, bool heroTint, bool armed, float halo = -1f)
        {
            float rad = ModernTheme.Radius * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            RectangleF cell = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1)) ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(cell, rad);
            using (var bp = new GraphicsPath())
            {
                bp.AddEllipse(cell.X - cell.Width * 0.25f, cell.Y - cell.Height * 0.4f, cell.Width * 1.5f, cell.Height * 1.5f);
                using var brush = new PathGradientBrush(bp) { CenterColor = HudKit.Sq(ModernTheme.PanelC), SurroundColors = new[] { HudKit.Sq(ModernTheme.PanelB) } };
                brush.CenterPoint = new PointF(cell.X + cell.Width / 2f, cell.Y + cell.Height * 0.35f);
                g.FillPath(brush, p);
            }
            using (GraphicsPath ip = ModernSkin.RoundRect(RectangleF.Inflate(cell, -1f, -1f), Math.Max(0, rad - 1)))
            {
                ModernSkin.Stroke(g, ip, Color.FromArgb(230, 0, 0, 0));
                Region old = g.Clip;
                g.SetClip(ip, CombineMode.Intersect);
                ModernSkin.Stroke(g, ip, Color.FromArgb(90, 0, 0, 0), Math.Max(3f, 6f * k));
                g.Clip = old;
                old.Dispose();
            }
            // ::before: the frame-light sheen along the top-left and bottom-right edges
            using (var sheen = new LinearGradientBrush(new RectangleF(0, 0, size.Width, size.Height), Color.Transparent, Color.Transparent, 160f - 90f))
            {
                sheen.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { ModernTheme.Alpha(ModernTheme.FrameLight, 0.7f), Color.Transparent, Color.Transparent, ModernTheme.Alpha(ModernTheme.FrameLight, 0.5f) },
                    Positions = new[] { 0f, 0.4f, 0.6f, 1f },
                };
                using var pen = new Pen(sheen, 1f);
                g.DrawPath(pen, p);
            }
            if (halo > 0)
            {
                // the armed halo: soft accent rings inside the edge (an inset glow at the pulse's strength)
                for (int i = 3; i >= 0; i--)
                {
                    using GraphicsPath hp = ModernSkin.RoundRect(RectangleF.Inflate(cell, -1f - i * k, -1f - i * k), Math.Max(0, rad - 1 - i * k));
                    ModernSkin.Stroke(g, hp, ModernTheme.Alpha(ModernTheme.Accent, (0.65f - i * 0.15f) * halo), Math.Max(1f, k));
                }
            }
            Color border = ModernTheme.Frame;
            if (heroTint) border = ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.45f);
            if (hot) border = ModernTheme.Accent;
            ModernSkin.Stroke(g, p, border, armed ? 1.5f : 1f);
        }
    }

    /// <summary>A slot's text overlays: the cooldown digits and the key hint.</summary>
    internal sealed class SlotOverlay : ModernControl
    {
        private string _key = string.Empty, _digits = string.Empty;

        public SlotOverlay()
        {
            NotControl = true;
        }

        public string Digits => _digits;

        public void SetKey(string key)
        {
            key ??= string.Empty;
            if (key == _key) return;
            _key = key;
            Invalidate();
        }

        public void SetDigits(string digits)
        {
            digits ??= string.Empty;
            if (digits == _digits) return;
            _digits = digits;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height;
            if (_digits.Length > 0)
                HudKit.ShadowText(g, _digits, ModernSkin.Font(14, true), Color.White, new RectangleF(0, 0, w, h), StringAlignment.Center, StringAlignment.Center, 200);
            if (_key.Length > 0)
                HudKit.ShadowText(g, _key, ModernSkin.Font(9, true), ModernTheme.Text, new RectangleF(2 * k, 0, w - 3 * k, 12 * k), StringAlignment.Near, StringAlignment.Near, 200);
        }
    }

    internal enum FlashShape { Rect, Diamond, TriangleLeft, TriangleRight }

    /// <summary>
    /// A short accent flash on a control's edge (slot click / ready, mode change, menu buttons): an opaque ring whose colour
    /// fades from the accent to the frame in 8 repaints, then hides (opaque, so the world never shows through the surface).
    /// </summary>
    internal sealed class HudFlash : ModernControl, Core.Widgets.IModernTick
    {
        private HudAnim _anim;
        private float _t;
        public FlashShape Shape;

        public HudFlash()
        {
            NotControl = true;
            Visible = false;
        }

        public bool Running => Visible;

        public void Start(int ms)
        {
            _anim.Begin(ms);
            _t = 0;
            Visible = true;
            Invalidate();
            Core.Widgets.ModernTick.Add(this);
        }

        void Core.Widgets.IModernTick.Tick()
        {
            if (IsDisposed)
            {
                Core.Widgets.ModernTick.Remove(this);
                return;
            }
            int step = _anim.Step(8);
            if (step < 0 || step == _anim.Last) return;
            _anim.Last = step;
            if (step >= 8)
            {
                Visible = false;
                Core.Widgets.ModernTick.Remove(this);
                return;
            }
            _t = step / 8f;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height, width = Math.Max(1.5f, 2.5f * k * (1f - _t * 0.5f));
            Color c = ModernTheme.Mix(ModernTheme.Mix(ModernTheme.Accent, Color.White, 0.75f), ModernTheme.Frame, 1f - _t);
            GraphicsPath p;
            float i = width / 2f + 0.5f;
            switch (Shape)
            {
                case FlashShape.Diamond:
                    p = HudKit.Polygon(new PointF(w / 2f, i), new PointF(w - i, h / 2f), new PointF(w / 2f, h - i), new PointF(i, h / 2f));
                    break;
                case FlashShape.TriangleLeft:
                    p = HudKit.Polygon(new PointF(i, i * 2), new PointF(i, h - i), new PointF(w - i * 2, h - i));
                    break;
                case FlashShape.TriangleRight:
                    p = HudKit.Polygon(new PointF(w - i, i * 2), new PointF(w - i, h - i), new PointF(i * 2, h - i));
                    break;
                default:
                    p = ModernSkin.RoundRect(new RectangleF(i, i, w - i * 2, h - i * 2), Math.Max(0, ModernTheme.Radius * k - i));
                    break;
            }
            using (p)
            using (var pen = new Pen(c, width) { LineJoin = LineJoin.Round })
                g.DrawPath(pen, p);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Core.Widgets.ModernTick.Remove(this);
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ mode slots

    /// <summary>The attack mode (left) or pet mode (right) slot with its list (skillRow.ts ModeSlot).</summary>
    internal sealed class ModeSlot : ModernControl
    {
        private static readonly AttackMode[] AttackModes = { AttackMode.Peace, AttackMode.Group, AttackMode.Guild, AttackMode.EnemyGuild, AttackMode.RedBrown, AttackMode.All };
        private static readonly KeybindOptions[] AttackBinds =
        {
            KeybindOptions.AttackmodePeace, KeybindOptions.AttackmodeGroup, KeybindOptions.AttackmodeGuild, KeybindOptions.AttackmodeEnemyguild,
            KeybindOptions.AttackmodeRedbrown, KeybindOptions.AttackmodeAll,
        };
        private static readonly ClientTextKeys[] AttackNames =
        {
            ClientTextKeys.ModernHudAModePeace, ClientTextKeys.ModernHudAModeGroup, ClientTextKeys.ModernHudAModeGuild, ClientTextKeys.ModernHudAModeEnemyGuild,
            ClientTextKeys.ModernHudAModeRedBrown, ClientTextKeys.ModernHudAModeAll,
        };
        private static readonly PetMode[] PetModes = { PetMode.Both, PetMode.MoveOnly, PetMode.AttackOnly, PetMode.None, PetMode.FocusMasterTarget };
        private static readonly KeybindOptions[] PetBinds =
        {
            KeybindOptions.PetmodeBoth, KeybindOptions.PetmodeMoveonly, KeybindOptions.PetmodeAttackonly, KeybindOptions.PetmodeNone, KeybindOptions.PetmodeFocusMasterTarget,
        };
        private static readonly ClientTextKeys[] PetNames =
        {
            ClientTextKeys.ModernHudPModeBoth, ClientTextKeys.ModernHudPModeMoveOnly, ClientTextKeys.ModernHudPModeAttackOnly, ClientTextKeys.ModernHudPModeNone,
            ClientTextKeys.ModernHudPModeFocus,
        };
        private static readonly ClientTextKeys[] PetFullNames =
        {
            ClientTextKeys.ModernHudPModeBothFull, ClientTextKeys.ModernHudPModeMoveOnlyFull, ClientTextKeys.ModernHudPModeAttackOnlyFull, ClientTextKeys.ModernHudPModeNoneFull,
            ClientTextKeys.ModernHudPModeFocusFull,
        };

        private readonly bool _pet;
        private readonly HudPopover _pop;
        private readonly HudFlash _flash;
        private readonly Func<int> _alignX;
        private int _shown = -1;
        private string _name = string.Empty;

        public ModeSlot(bool pet, HudPopover pop, Func<int> alignX)
        {
            _pet = pet;
            _pop = pop;
            _alignX = alignX;
            Size = new Size(S(64), S(40));
            _flash = new HudFlash { Parent = this, Size = Size };
            Click += (o, e) => Toggle();
        }

        public bool Pet => _pet;
        public int ShownMode => _shown;

        public void SetBounds(Rectangle r)
        {
            Location = r.Location;
            Size = r.Size;
            _flash.Size = r.Size;
        }

        private int Current
        {
            get
            {
                GameScene scene = GameScene.Scene;
                if (scene == null) return 0;
                return _pet ? Array.IndexOf(PetModes, scene.PMode) : Array.IndexOf(AttackModes, scene.AMode);
            }
        }

        private string FullName(int i) => i < 0 ? "?" : HudKit.T(_pet ? PetFullNames[i] : AttackNames[i]);

        /// <summary>Shows the server's mode (cheap when unchanged; the HUD calls it each frame).</summary>
        public void UpdateMode()
        {
            int cur = Current;
            if (cur == _shown) return;
            bool first = _shown < 0;
            _shown = cur;
            _name = cur < 0 ? "?" : HudKit.T(_pet ? PetNames[cur] : AttackNames[cur]);
            string bind = HudKit.KeyLabel(_pet ? KeybindOptions.ChangePetmode : KeybindOptions.ChangeAttackmode);
            string cycles = bind.Length > 0 ? HudKit.T(ClientTextKeys.ModernHudModeCycles, bind) : string.Empty;
            Hint = HudKit.T(_pet ? ClientTextKeys.ModernHudPetModeHint : ClientTextKeys.ModernHudAttackModeHint, FullName(cur)) + "\n" + HudKit.T(ClientTextKeys.ModernHudModeListHint, cycles);
            Invalidate();
            if (!first) _flash.Start(350);
        }

        internal void Toggle()
        {
            if (_pop.IsFor(this))
            {
                _pop.Close();
                return;
            }
            const int width = 170, pad = 4, rowH = 22;
            int n = _pet ? PetModes.Length : AttackModes.Length;
            var card = new PopCard { Size = new Size(S(width), S(pad * 2 + n * rowH)) };
            int cur = Current;
            for (int i = 0; i < n; i++)
            {
                int index = i;
                var row = new PopRow(FullName(i), HudKit.KeyLabel(_pet ? PetBinds[i] : AttackBinds[i]), i == cur, false, true, width - pad * 2, rowH)
                {
                    Parent = card,
                    Location = new Point(S(pad), S(pad + i * rowH)),
                };
                row.Click += (o, e) =>
                {
                    _pop.Close();
                    Send(index);
                };
            }
            _pop.Show(this, card, _alignX(), _pet, true);
        }

        /// <summary>C.ChangeAMode / C.ChangePMode; the slot changes when the server answers.</summary>
        internal void Send(int index)
        {
            if (index == Current) return;
            if (_pet)
            {
                if (CMain.Time <= GameScene.ChangePModeTime) return;
                GameScene.ChangePModeTime = CMain.Time + 500;
                MirNetwork.Network.Enqueue(new C.ChangePMode { Mode = PetModes[index] });
            }
            else
            {
                if (CMain.Time <= GameScene.ChangeAModeTime) return;
                GameScene.ChangeAModeTime = CMain.Time + 300;
                MirNetwork.Network.Enqueue(new C.ChangeAMode { Mode = AttackModes[index] });
            }
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height;
            SlotPainter.Well(g, Size, k, Hovered || _pop.IsFor(this), false, false);
            ModernSkin.SpacedText(g, HudKit.T(_pet ? ClientTextKeys.ModernHudPetLabel : ClientTextKeys.ModernHudModeLabel).ToUpperInvariant(), ModernSkin.Font(9, true),
                ModernTheme.Dim, new RectangleF(0, h / 2f - 12 * k, w, 11 * k), 0.9f * k);
            ModernSkin.Text(g, _name, ModernSkin.Font(10.5f, true), ModernTheme.Accent, new RectangleF(1, h / 2f, w - 2, 13 * k), StringAlignment.Center, StringAlignment.Center);
        }
    }
}
