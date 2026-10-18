// Ported from WebClient/src/ui/modern/hud/heroFrame.ts: the summoned hero's frame on the left of the screen (the desktop's
// HeroInfoPanel + HeroBehaviourPanel), over GameScene.Hero (level, HP / MP, experience, Stats after RefreshStats),
// GameScene.HeroSpawnState and the behaviour from ModernEvents.HeroBehaviour. Portrait = GameScene.HeroAvatar (Prguse
// 1400 + class + 10 x gender) with the level, HP / MP / EXP bars, and a behaviour chip that asks the server for the next
// HeroBehaviour (C.SetHeroBehaviour; the chip changes when S.SetHeroBehaviour answers, never before; a refused change never
// answers, so another click is allowed after 1.5 s). Shown while the hero is summoned, greyed when it died. A click on the
// portrait opens the "hero" window once one is registered (a later phase).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class HeroFrame : ModernSurface
    {
        public const int Width = 196, Height = 52;
        private static readonly HeroBehaviour[] Behaviours = { HeroBehaviour.Attack, HeroBehaviour.CounterAttack, HeroBehaviour.Follow, HeroBehaviour.Custom };

        private HeroFrameBody _body;
        private HeroChip _chip;
        private HeroBehaviour _behaviour = HeroBehaviour.Attack;
        private int _asked = -1;
        private long _askedUntil;
        private long _nextPoll;
        private readonly Action<HeroBehaviour> _onBehaviour;
        private readonly Action<bool> _onRelayout;

        public HeroFrame()
        {
            PassThrough = true;
            Visible = false;
            _onBehaviour = b =>
            {
                _behaviour = b;
                if (_asked == (int)b) _asked = -1;
                UpdateChip();
            };
            _onRelayout = rebuild => { if (rebuild) Build(); else Place(); };
            ModernEvents.HeroBehaviour.On(_onBehaviour);
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
            Build();
        }

        public HeroBehaviour Behaviour => _behaviour;
        public bool Waiting => _asked >= 0;

        private void Build()
        {
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            Size = new Size(S(Width), S(Height));
            _body = new HeroFrameBody { Parent = this, Size = Size };
            _body.Portrait.Click += (o, e) =>
            {
                WindowManager wm = ModernRoot.Instance?.Wm;
                if (wm != null && wm.IsRegistered("hero")) wm.Toggle("hero");
            };
            _chip = new HeroChip { Parent = this, Hint = HudKit.T(ClientTextKeys.ModernHudHeroBehaviourHint) };
            _chip.Click += (o, e) => NextBehaviour();
            UpdateChip();
            _nextPoll = 0;
            Place();
            TextureValid = false;
        }

        private void Place()
        {
            Location = new Point(S(12), (int)(Settings.ScreenHeight * 0.41f));
        }

        private void UpdateChip()
        {
            if (_chip == null || _chip.IsDisposed) return;
            string text = _behaviour == HeroBehaviour.CounterAttack ? HudKit.T(ClientTextKeys.ModernHudHeroCounter) : _behaviour.ToLocalizedString();
            _chip.Set(text.ToUpperInvariant(), _asked >= 0);
            Font f = ModernSkin.Font(8, true);
            int w = (int)Math.Ceiling(ModernSkin.Measure(_chip.Text, f) + _chip.Text.Length * 0.5f * ModernRoot.K) + S(12);
            _chip.Size = new Size(w, S(13));
            _chip.Location = new Point(S(Width - 5) - w - S(2), S(6));
        }

        /// <summary>Per frame (cheap): visibility from the spawn state, the values ~4x a second, the behaviour wait timeout.</summary>
        public override void Process()
        {
            if (_asked >= 0 && CMain.Time >= _askedUntil)
            {
                _asked = -1;
                UpdateChip();
            }
            if (CMain.Time < _nextPoll) return;
            _nextPoll = CMain.Time + 250;
            _body.Update(_chip.Size.Width);
        }

        /// <summary>The visibility check runs even while hidden (ModernRoot.Process calls only visible surfaces).</summary>
        internal void CheckShown()
        {
            GameScene scene = GameScene.Scene;
            bool show = GameScene.Hero != null && scene != null && scene.HeroSpawnState >= HeroSpawnState.Summoned;
            if (show == Visible) return;
            Visible = show;
            if (show)
            {
                _nextPoll = 0;
                Process();
            }
        }

        /// <summary>HeroBehaviourPanel: C.SetHeroBehaviour for the next behaviour (the server answers S.SetHeroBehaviour).</summary>
        internal void NextBehaviour()
        {
            GameScene scene = GameScene.Scene;
            if (GameScene.Hero == null || scene == null || scene.HeroSpawnState != HeroSpawnState.Summoned || GameScene.Hero.Dead || _asked >= 0) return;
            int i = Array.IndexOf(Behaviours, _behaviour);
            HeroBehaviour next = Behaviours[(i + 1) % Behaviours.Length];
            _asked = (int)next;
            _askedUntil = CMain.Time + 1500;
            MirNetwork.Network.Enqueue(new C.SetHeroBehaviour { Behaviour = next });
            UpdateChip();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ModernEvents.HeroBehaviour.Off(_onBehaviour);
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The frame's panel, portrait, name and bars (.hframe).</summary>
    internal sealed class HeroFrameBody : ModernControl
    {
        public readonly HeroPortrait Portrait;
        private string _name = string.Empty, _title = string.Empty, _baseName;
        private float _hp, _mp, _xp;
        private bool _fallen;
        private int _chipW;

        public HeroFrameBody()
        {
            NotControl = false;
            Portrait = new HeroPortrait { Parent = this };
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();
            if (Portrait == null) return;
            Portrait.Location = new Point(S(5), S(5));
            Portrait.Size = new Size(S(42), S(42));
        }

        public void Update(int chipW)
        {
            UserHeroObject h = GameScene.Hero;
            if (h == null) return;
            bool fallen = h.Dead || GameScene.Scene?.HeroSpawnState == HeroSpawnState.Dead;
            int maxHp = h.Stats != null ? h.Stats[Stat.HP] : 0, maxMp = h.Stats != null ? h.Stats[Stat.MP] : 0;
            float hp = maxHp > 0 ? Math.Clamp(h.HP / (float)maxHp, 0f, 1f) : 0f;
            float mp = maxMp > 0 ? Math.Clamp(h.MP / (float)maxMp, 0f, 1f) : 0f;
            float xp = h.MaxExperience > 0 ? Math.Clamp(h.Experience / (float)h.MaxExperience, 0f, 1f) : 0f;
            // quantised to the bar's pixel width: no repaint for sub-pixel changes
            float px = 140 * K;
            bool changed = !ReferenceEquals(h.Name, _baseName) || fallen != _fallen || chipW != _chipW
                || (int)(hp * px) != (int)(_hp * px) || (int)(mp * px) != (int)(_mp * px) || (int)(xp * px) != (int)(_xp * px);
            Portrait.Set(GameScene.Scene != null ? GameScene.Scene.HeroAvatar(h.Class, h.Gender) : -1, h.Level, fallen);
            if (!changed) return;
            _baseName = h.Name;
            _name = fallen ? h.Name + " ✝" : h.Name;
            _fallen = fallen;
            _chipW = chipW;
            _hp = hp;
            _mp = mp;
            _xp = xp;
            string title = HudKit.T(ClientTextKeys.ModernHudHeroTitle, h.Name, h.Level) + "\n"
                + HudKit.T(ClientTextKeys.ModernHudHeroVitals, HudKit.Fmt(h.HP), HudKit.Fmt(maxHp), HudKit.Fmt(h.MP), HudKit.Fmt(maxMp)) + "\n"
                + HudKit.T(ClientTextKeys.ModernHudHeroExp, (xp * 100).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (title != _title)
            {
                _title = title;
                Hint = title;
            }
            Opacity = fallen ? 0.6f : 1f;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath ring = ModernSkin.RoundRect(r, rad + 1)) ModernSkin.Stroke(g, ring, Color.FromArgb(153, 0, 0, 0));
            using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(r, -1, -1), rad);
            Color pb = ModernSkin.Opaque(ModernTheme.PanelB);
            using (var b = new LinearGradientBrush(new RectangleF(0, 0, w, h), HudKit.Sq(pb, 0.88f), HudKit.Sq(pb, 0.45f), LinearGradientMode.Horizontal)) g.FillPath(b, p);
            ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
            float x = 54 * k, bw = w - x - 5 * k;
            HudKit.ShadowText(g, _name, ModernSkin.Font(11, true), Color.White, new RectangleF(x, 4 * k, bw - _chipW - 6 * k, 16 * k), StringAlignment.Near, StringAlignment.Center, 150);
            Bar(g, x, 22 * k, bw, 7 * k, _hp, Color.FromArgb(255, 210, 58, 58), k);
            Bar(g, x, 31 * k, bw, 7 * k, _mp, Color.FromArgb(255, 58, 114, 224), k);
            Bar(g, x, 40 * k, bw, 3 * k, _xp, ModernTheme.Accent, k);
        }

        private static void Bar(Graphics g, float x, float y, float w, float h, float f, Color c, float k)
        {
            using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(x, y, w, h), h / 2f))
            using (var b = new SolidBrush(Color.FromArgb(255, 4, 6, 10)))
                g.FillPath(b, p);
            if (f <= 0) return;
            using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(x, y, Math.Max(h, w * f), h), h / 2f))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }
    }

    /// <summary>The round portrait (GameScene.HeroAvatar) with the accent ring and the level tag.</summary>
    internal sealed class HeroPortrait : ModernControl
    {
        private int _avatar = -1, _level = -1;
        private bool _fallen;
        private readonly PortraitRing _ring;

        public HeroPortrait()
        {
            _ring = new PortraitRing(this) { Parent = this };
        }

        public void Set(int avatar, int level, bool fallen)
        {
            if (avatar == _avatar && level == _level && fallen == _fallen) return;
            _avatar = avatar;
            _level = level;
            _fallen = fallen;
            _ring.Size = Size;
            _ring.Invalidate();
            Invalidate();
        }

        internal int Level => _level;

        protected override void Paint(Graphics g)
        {
            using var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.PanelB));
            g.FillEllipse(b, 1, 1, Size.Width - 2, Size.Height - 2); // the portrait is drawn over it as a disc (HudKit.DrawCircle)
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_avatar < 0 || Libraries.Prguse == null) return;
            float k = K;
            HudKit.DrawCircle(Libraries.Prguse, _avatar, new RectangleF(DisplayLocation.X + 2 * k, DisplayLocation.Y + 2 * k, Size.Width - 4 * k, Size.Height - 4 * k),
                _fallen ? Color.FromArgb(255, 120, 120, 120) : Color.White);
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            WindowManager wm = ModernRoot.Instance?.Wm;
            if (wm == null || !wm.IsRegistered("hero")) return; // the portrait is a button only once the Hero window exists
            base.OnMouseClick(e);
        }

        protected override void OnMouseEnter()
        {
            WindowManager wm = ModernRoot.Instance?.Wm;
            Hint = wm != null && wm.IsRegistered("hero") ? HudKit.T(ClientTextKeys.ModernHudHeroOpen) : string.Empty;
            base.OnMouseEnter();
        }
    }

    /// <summary>Masks the portrait's square corners with the frame colour and draws the 2 px accent ring and the level tag.</summary>
    internal sealed class PortraitRing : ModernControl
    {
        private readonly HeroPortrait _portrait;

        public PortraitRing(HeroPortrait portrait)
        {
            _portrait = portrait;
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height;
            using (var pen = new Pen(ModernTheme.Accent, 2 * k)) g.DrawEllipse(pen, k, k, w - 2 * k, h - 2 * k);
            if (_portrait.Level >= 0)
            {
                string lv = _portrait.Level.ToString();
                Font f = ModernSkin.Font(9, true);
                float tw = ModernSkin.Measure(lv, f) + 8 * k;
                RectangleF r = new RectangleF((w - tw) / 2f, h - 12 * k, tw, 12 * k);
                using (GraphicsPath p = ModernSkin.RoundRect(r, 5 * k))
                using (var b = new SolidBrush(Color.FromArgb(204, 0, 0, 0)))
                    g.FillPath(b, p);
                ModernSkin.Text(g, lv, f, Color.White, r, StringAlignment.Center, StringAlignment.Center, false);
            }
        }
    }

    /// <summary>The behaviour chip (.md): accent outline, upper case; "wait" while the server has not answered.</summary>
    internal sealed class HeroChip : ModernControl
    {
        private string _text = string.Empty;
        private bool _wait;

        public string Text => _text;

        public void Set(string text, bool wait)
        {
            if (text == _text && wait == _wait) return;
            _text = text ?? string.Empty;
            _wait = wait;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1, Size.Height - 1);
            using GraphicsPath p = ModernSkin.RoundRect(r, 6 * k);
            Color c = _wait ? ModernSkin.Dim(ModernTheme.Accent, 0.6f) : ModernTheme.Accent;
            if (Hovered && !_wait)
                using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.35f))) g.FillPath(b, p);
            ModernSkin.Stroke(g, p, c);
            ModernSkin.SpacedText(g, _text, ModernSkin.Font(8, true), Hovered && !_wait ? Color.White : c, r, 0.5f * k);
        }
    }
}
