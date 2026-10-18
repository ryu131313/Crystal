// Ported from WebClient/src/ui/modern/windows/fishingScene.ts (FishingScene): the fishing window's water scene, a night lake.
// The static layers (sky, moon and halo, hills and their reflection, water, mist, vignette) are cached to one bitmap per size;
// a repaint draws the moving parts: stars, clouds, waves, the moon's reflection, ripples, the bite glow, the line, the float, the
// leaping fish, the rod, reeds, splash / sparkle particles and fireflies. Purely visual: it follows the phase the window gives
// it (SetPhase) and the cast results (Outcome / Slip) and decides nothing.
// Desktop changes: GDI+ instead of canvas 2D (normal alpha blending where the web adds light: no "lighter" compositing), all
// brushes, pens, paths and particle slots are allocated once and reused; the scene repaints itself on a timer gate while its
// window is open (<see cref="Tick"/>: 40 ms while something moves, 125 ms when only the ambient loop runs), never per frame.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Windows
{
    internal sealed class FishingScene : ModernControl
    {
        public enum Phase { Idle, Wait, Bite }
        public enum Outcomes { Catch, Lost, Empty }
        private enum Anim { Hang, Cast, Float, Reel }

        private const float CastTime = 0.85f, ReelTime = 0.8f, Gravity = 620f;
        private const int MaxParticles = 160, MaxRings = 24;

        private struct Particle { public float X, Y, Vx, Vy, Life, Max, Size; public int Kind; }
        private struct Ring { public float X, Y, T, Max, R, A; }
        private struct Star { public float X, Y, S, Ph, Sp; }
        private struct Seg { public float X, Len; }
        private struct Wave { public float Y, D, Speed; public Seg[] Segs; }
        private struct Reed { public float X, H, Bend, W, Ph; public bool Head; }
        private struct Firefly { public float Ax, Ay, Ph, Sp; }

        // cached layers (device px, rebuilt on resize / theme change)
        private Bitmap _bg, _fg, _fireGlow, _sparkGlow, _biteGlow;
        private readonly Bitmap[] _clouds = new Bitmap[3];
        private Size _layerSize;

        // reusable GDI objects (never allocated per paint)
        private readonly SolidBrush _brush = new SolidBrush(Color.White);
        private readonly Pen _pen = new Pen(Color.White, 1f);
        private readonly GraphicsPath _path = new GraphicsPath();
        private readonly PointF[] _rodPts = new PointF[38];
        private readonly ImageAttributes _ia = new ImageAttributes();
        private readonly ColorMatrix _cm = new ColorMatrix();
        private readonly PointF[] _tail = new PointF[3];
        private readonly StringFormat _centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // layout (HUD px)
        private float _w, _h, _hy;
        private PointF _moon, _tip, _rest;
        private float _moonR;
        private Star[] _stars = Array.Empty<Star>();
        private Wave[] _waves = Array.Empty<Wave>();
        private Reed[] _reeds = Array.Empty<Reed>();
        private Firefly[] _flies = Array.Empty<Firefly>();

        // state
        private readonly Random _rnd = new Random(4242);
        private bool _primed;
        private Phase _phase = Phase.Idle;
        private Anim _anim = Anim.Hang;
        private float _animT, _time;
        private Outcomes? _pending;
        private PointF _from;
        private float _bobY, _bobV, _biteT, _ringClock, _tension, _shake, _flash;
        private Color _flashRgb = Color.FromArgb(255, 200, 90);
        private readonly Particle[] _parts = new Particle[MaxParticles];
        private int _partCount;
        private readonly Ring[] _rings = new Ring[MaxRings];
        private int _ringCount;
        private bool _fishOn;
        private float _fx, _fy, _fvx, _fvy, _ft;
        private PointF _fpos;
        private long _last, _nextPaint;

        public FishingScene()
        {
            NotControl = true;
        }

        public Phase Current => _phase;

        // ------------------------------------------------------------------ public API

        /// <summary>The next SetPhase jumps straight to that phase's steady pose (the window was hidden while things changed).</summary>
        public void Snap() => _primed = false;

        public void SetPhase(Phase p)
        {
            if (!_primed)
            {
                _primed = true;
                _phase = p;
                _anim = p == Phase.Idle ? Anim.Hang : Anim.Float;
                _biteT = p == Phase.Bite ? 0.5f : 0f;
                return;
            }
            if (p == _phase) return;
            Phase prev = _phase;
            _phase = p;
            if (prev == Phase.Idle) StartCast();
            else if (p == Phase.Idle) StartReel(_pending ?? Outcomes.Empty);
            if (p == Phase.Bite) StartBite();
            _pending = null;
            _nextPaint = 0;
        }

        /// <summary>The cast's result, worked out by the window just before the Fishing = false update reels the float in.</summary>
        public void Outcome(Outcomes kind)
        {
            _pending = kind;
            if (_phase == Phase.Idle && _anim == Anim.Hang) PlayOutcome(kind, _tip.X, _tip.Y + 40);
            _nextPaint = 0;
        }

        /// <summary>The bite ran out: the float bobs back up.</summary>
        public void Slip()
        {
            _bobV = -160;
            Splash(_fpos.X, _rest.Y, 6, 110);
            AddRing(_rest.X, _rest.Y, 26, 0.5f);
            _nextPaint = 0;
        }

        /// <summary>Advances the simulation and repaints on the gate (called from the window's Process while open).</summary>
        public void Tick(long now)
        {
            if (Size.Width < 4 || Size.Height < 4) return;
            if (now < _nextPaint) return;
            bool busy = _anim == Anim.Cast || _anim == Anim.Reel || _phase == Phase.Bite || _partCount > 0 || _ringCount > 0 || _fishOn || _flash > 0 || _shake > 0;
            _nextPaint = now + (busy ? 40 : 125);
            float dt = _last == 0 ? 0.016f : Math.Min(0.05f, Math.Max(0f, (now - _last) / 1000f));
            if (!busy) dt = Math.Min(dt, 0.13f);
            _last = now;
            _time += dt;
            if (_w <= 0) DoLayout();
            Update(dt);
            Invalidate();
        }

        // ------------------------------------------------------------------ layout and static layers

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static float EaseOut(float t) => 1 - (1 - t) * (1 - t);

        /// <summary>mulberry32 (fishingScene.ts rng): the same hills, stars and reeds on every rebuild.</summary>
        private sealed class Rng
        {
            private int _seed;
            public Rng(int seed) { _seed = seed; }
            public float Next()
            {
                unchecked
                {
                    _seed += 0x6d2b79f5;
                    uint t = (uint)_seed;
                    t = (t ^ (t >> 15)) * (1 | t);
                    t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
                    return ((t ^ (t >> 14)) >> 0) / 4294967296f;
                }
            }
        }

        private void DoLayout()
        {
            float k = K;
            _w = Size.Width / k;
            _h = Size.Height / k;
            float w = _w, h = _h;
            var r = new Rng(1234);
            _hy = (float)Math.Round(h * 0.33f);
            _moon = new PointF(w * 0.8f, h * 0.12f);
            _moonR = Math.Max(11, Math.Min(w, h) * 0.04f);
            _tip = new PointF(w * 0.15f, h * 0.17f);
            _rest = new PointF(w * 0.58f, _hy + (h - _hy) * 0.3f);
            _stars = new Star[80];
            for (int i = 0; i < _stars.Length; i++)
            {
                float y = r.Next() * (_hy - 18);
                _stars[i] = new Star { X = r.Next() * w, Y = y, S = 0.5f + r.Next() * r.Next() * 1.4f, Ph = r.Next() * 6.28f, Sp = 0.6f + r.Next() * 2.2f };
            }
            _waves = new Wave[30];
            for (int k2 = 0; k2 < _waves.Length; k2++)
            {
                float d = (k2 + 1) / 30f;
                int n = 3 + (int)Math.Round(r.Next() * 4);
                var segs = new Seg[n];
                for (int i = 0; i < n; i++) segs[i] = new Seg { X = r.Next() * (w + 120), Len = (8 + r.Next() * 36) * (0.35f + d * 1.1f) };
                _waves[k2] = new Wave { Y = _hy + (h - _hy) * (float)Math.Pow(d, 1.65), D = d, Segs = segs, Speed = (4 + d * 16) * (k2 % 2 == 1 ? 1 : -0.6f) };
            }
            _reeds = new Reed[21];
            for (int i = 0; i < 16; i++) _reeds[i] = new Reed { X = w * (0.8f + r.Next() * 0.24f), H = h * (0.28f + r.Next() * 0.34f), Bend = (r.Next() - 0.3f) * 26, W = 1.4f + r.Next() * 1.8f, Ph = r.Next() * 6.28f, Head = r.Next() < 0.45f };
            for (int i = 16; i < 21; i++) _reeds[i] = new Reed { X = w * (-0.04f + r.Next() * 0.1f), H = h * (0.16f + r.Next() * 0.2f), Bend = (r.Next() - 0.6f) * 18, W = 1.3f + r.Next() * 1.2f, Ph = r.Next() * 6.28f, Head = r.Next() < 0.3f };
            _flies = new Firefly[8];
            for (int i = 0; i < 8; i++) _flies[i] = new Firefly { Ax = w * (0.62f + r.Next() * 0.36f), Ay = _hy + (h - _hy) * (0.05f + r.Next() * 0.45f) - h * 0.08f, Ph = r.Next() * 6.28f, Sp = 0.3f + r.Next() * 0.5f };
            _layerSize = Size.Empty; // rebuild the bitmaps on the next paint
        }

        private void DisposeLayers()
        {
            _bg?.Dispose(); _bg = null;
            _fg?.Dispose(); _fg = null;
            _fireGlow?.Dispose(); _fireGlow = null;
            _sparkGlow?.Dispose(); _sparkGlow = null;
            _biteGlow?.Dispose(); _biteGlow = null;
            for (int i = 0; i < _clouds.Length; i++) { _clouds[i]?.Dispose(); _clouds[i] = null; }
        }

        /// <summary>A soft round glow sprite (radial falloff), device px.</summary>
        private static Bitmap GlowSprite(float rHud, Color c, float core)
        {
            int r = Math.Max(2, (int)Math.Round(rHud * ModernRoot.K));
            var bmp = new Bitmap(r * 2, r * 2, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new GraphicsPath();
            p.AddEllipse(0, 0, r * 2, r * 2);
            using var b = new PathGradientBrush(p)
            {
                CenterColor = Color.FromArgb((int)(255 * core), c),
                SurroundColors = new[] { Color.FromArgb(0, c) },
            };
            var blend = new ColorBlend(3)
            {
                Colors = new[] { Color.FromArgb(0, c), Color.FromArgb((int)(255 * core * 0.45f), c), Color.FromArgb((int)(255 * core), c) },
                Positions = new[] { 0f, 0.75f, 1f },
            };
            b.InterpolationColors = blend;
            g.FillPath(b, p);
            return bmp;
        }

        /// <summary>A wispy cloud sprite: overlapping soft blobs.</summary>
        private static Bitmap CloudSprite(float wHud, float hHud, int seed)
        {
            float k = ModernRoot.K;
            int w = Math.Max(2, (int)(wHud * k)), h = Math.Max(2, (int)(hHud * k));
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rng(seed);
            Color c = Color.FromArgb(150, 175, 215);
            for (int i = 0; i < 9; i++)
            {
                float x = w * (0.15f + r.Next() * 0.7f), y = h * (0.35f + r.Next() * 0.35f), rad = h * (0.25f + r.Next() * 0.3f);
                using var p = new GraphicsPath();
                p.AddEllipse(x - rad, y - rad, rad * 2, rad * 2);
                using var b = new PathGradientBrush(p) { CenterColor = Color.FromArgb(56, c), SurroundColors = new[] { Color.FromArgb(0, c) } };
                g.FillPath(b, p);
            }
            return bmp;
        }

        /// <summary>Hills as a closed polygon along the horizon (heights from summed sines, seeded), into <paramref name="p"/>.</summary>
        private void Hills(GraphicsPath p, float baseY, float amp, int seed, float jag)
        {
            var r = new Rng(seed);
            float w = _w, f1 = 1 + r.Next() * 2, f2 = 3 + r.Next() * 4, p1 = r.Next() * 6, p2 = r.Next() * 6;
            var pts = new List<PointF> { new PointF(0, baseY) };
            for (float x = 0; x <= w; x += 4)
            {
                float u = x / w;
                float y = baseY - amp * (0.55f + 0.3f * (float)Math.Sin(u * f1 * 6.28f + p1) + 0.15f * (float)Math.Sin(u * f2 * 6.28f + p2));
                if (jag > 0) y -= r.Next() * jag * ((int)x % 8 == 0 ? 1.6f : 0.4f);
                pts.Add(new PointF(x, y));
            }
            pts.Add(new PointF(w, baseY));
            p.AddPolygon(pts.ToArray());
        }

        private void BuildLayers()
        {
            DisposeLayers();
            _layerSize = Size;
            float k = K, w = _w, h = _h, hy = _hy;
            _fireGlow = GlowSprite(14, Color.FromArgb(210, 255, 140), 1f);
            _sparkGlow = GlowSprite(10, Color.FromArgb(255, 214, 120), 1f);
            _biteGlow = GlowSprite(34, Color.FromArgb(255, 196, 80), 0.7f);
            _clouds[0] = CloudSprite(220, 60, 7);
            _clouds[1] = CloudSprite(160, 44, 19);
            _clouds[2] = CloudSprite(260, 70, 31);

            _bg = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_bg))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(k, k);
                // sky
                using (var sky = new LinearGradientBrush(new RectangleF(0, -1, w, hy + 2), Color.Black, Color.Black, LinearGradientMode.Vertical))
                {
                    sky.InterpolationColors = new ColorBlend(3) { Colors = new[] { ModernTheme.Hex("#030713"), ModernTheme.Hex("#0b1a35"), ModernTheme.Hex("#1f3d66") }, Positions = new[] { 0f, 0.55f, 1f } };
                    g.FillRectangle(sky, 0, 0, w, hy + 1);
                }
                // moon: halo, disc, maria
                using (var p = new GraphicsPath())
                {
                    float hr = _moonR * 7;
                    p.AddEllipse(_moon.X - hr, _moon.Y - hr, hr * 2, hr * 2);
                    using var b = new PathGradientBrush(p);
                    b.InterpolationColors = new ColorBlend(3)
                    {
                        Colors = new[] { Color.FromArgb(0, 190, 210, 255), Color.FromArgb(20, 190, 210, 255), Color.FromArgb(72, 255, 240, 200) },
                        Positions = new[] { 0f, 0.65f, 1f },
                    };
                    g.SetClip(new RectangleF(0, 0, w, hy));
                    g.FillPath(b, p);
                    g.ResetClip();
                }
                using (var p = new GraphicsPath())
                {
                    p.AddEllipse(_moon.X - _moonR, _moon.Y - _moonR, _moonR * 2, _moonR * 2);
                    using var b = new PathGradientBrush(p) { CenterPoint = new PointF(_moon.X - _moonR * 0.35f, _moon.Y - _moonR * 0.35f) };
                    b.InterpolationColors = new ColorBlend(3) { Colors = new[] { ModernTheme.Hex("#d9c385"), ModernTheme.Hex("#f6e6b4"), ModernTheme.Hex("#fffdf0") }, Positions = new[] { 0f, 0.3f, 1f } };
                    g.FillPath(b, p);
                }
                using (var b = new SolidBrush(Color.FromArgb(41, 150, 130, 80)))
                {
                    float[,] m = { { -0.3f, -0.1f, 0.28f }, { 0.25f, 0.2f, 0.2f }, { 0.05f, -0.4f, 0.14f }, { 0.35f, -0.25f, 0.1f } };
                    for (int i = 0; i < 4; i++)
                    {
                        float rr = m[i, 2] * _moonR;
                        g.FillEllipse(b, _moon.X + m[i, 0] * _moonR - rr, _moon.Y + m[i, 1] * _moonR - rr, rr * 2, rr * 2);
                    }
                }
                // hills: far (misty blue), near (treeline)
                using (var p = new GraphicsPath())
                {
                    Hills(p, hy, h * 0.11f, 5, 0);
                    using (var b = new SolidBrush(ModernTheme.Hex("#14294a"))) g.FillPath(b, p);
                    using (var mist = new LinearGradientBrush(new RectangleF(0, hy - h * 0.1f - 1, w, h * 0.1f + 2), Color.FromArgb(0, 90, 130, 180), Color.FromArgb(89, 90, 130, 180), LinearGradientMode.Vertical))
                        g.FillPath(mist, p);
                }
                using (var p = new GraphicsPath())
                {
                    Hills(p, hy, h * 0.055f, 11, 3.5f);
                    using var b = new SolidBrush(ModernTheme.Hex("#07111f"));
                    g.FillPath(b, p);
                }
                // water
                using (var water = new LinearGradientBrush(new RectangleF(0, hy - 1, w, h - hy + 2), Color.Black, Color.Black, LinearGradientMode.Vertical))
                {
                    water.InterpolationColors = new ColorBlend(4)
                    {
                        Colors = new[] { ModernTheme.Hex("#1c3f66"), ModernTheme.Hex("#113052"), ModernTheme.Hex("#08203a"), ModernTheme.Hex("#030b18") },
                        Positions = new[] { 0f, 0.18f, 0.6f, 1f },
                    };
                    g.FillRectangle(water, 0, hy, w, h - hy);
                }
                // hill reflections (mirrored, squashed, faded)
                GraphicsState st = g.Save();
                g.TranslateTransform(0, hy * 1.62f);
                g.ScaleTransform(1, -0.62f);
                using (var p = new GraphicsPath())
                {
                    Hills(p, hy, h * 0.11f, 5, 0);
                    using var b = new SolidBrush(Color.FromArgb(115, ModernTheme.Hex("#0e2240")));
                    g.FillPath(b, p);
                }
                using (var p = new GraphicsPath())
                {
                    Hills(p, hy, h * 0.055f, 11, 3.5f);
                    using var b = new SolidBrush(Color.FromArgb(115, ModernTheme.Hex("#050d19")));
                    g.FillPath(b, p);
                }
                g.Restore(st);
                // moonlight on the water and the horizon line
                using (var p = new GraphicsPath())
                {
                    float rx = (h - hy) * 0.22f, ry = rx * 3.2f, cy = hy + (h - hy) * 0.35f;
                    p.AddEllipse(_moon.X - rx, cy - ry, rx * 2, ry * 2);
                    using var b = new PathGradientBrush(p) { CenterColor = Color.FromArgb(26, 255, 235, 180), SurroundColors = new[] { Color.FromArgb(0, 255, 235, 180) } };
                    g.SetClip(new RectangleF(0, hy, w, h - hy));
                    g.FillPath(b, p);
                    g.ResetClip();
                }
                using (var line = new LinearGradientBrush(new RectangleF(-1, 0, w + 2, 2), Color.FromArgb(0, 160, 200, 255), Color.FromArgb(0, 160, 200, 255), LinearGradientMode.Horizontal))
                {
                    line.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.FromArgb(0, 160, 200, 255), Color.FromArgb(71, 160, 200, 255), Color.FromArgb(0, 160, 200, 255) }, Positions = new[] { 0f, 0.5f, 1f } };
                    g.FillRectangle(line, 0, hy - 0.5f, w, 1.5f);
                }
                using (var fog = new LinearGradientBrush(new RectangleF(0, hy - 7, w, (h - hy) * 0.25f + 8), Color.FromArgb(46, 120, 155, 200), Color.FromArgb(0, 120, 155, 200), LinearGradientMode.Vertical))
                    g.FillRectangle(fog, 0, hy - 6, w, (h - hy) * 0.25f + 6);
            }

            // vignette and the dark band under the overlay controls, drawn over everything
            _fg = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_fg))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(k, k);
                using (var p = new GraphicsPath())
                {
                    float rad = Math.Max(w, h) * 0.78f;
                    p.AddEllipse(w * 0.5f - rad, h * 0.5f - rad, rad * 2, rad * 2);
                    using var b = new PathGradientBrush(p);
                    b.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.FromArgb(158, 0, 2, 8), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0) }, Positions = new[] { 0f, 0.55f, 1f } };
                    g.FillRectangle(b, 0, 0, w, h);
                }
                using (var b = new LinearGradientBrush(new RectangleF(0, h - 191, w, 192), Color.Black, Color.Black, LinearGradientMode.Vertical))
                {
                    b.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.FromArgb(0, 2, 6, 14), Color.FromArgb(140, 2, 6, 14), Color.FromArgb(224, 2, 6, 14) }, Positions = new[] { 0f, 0.55f, 1f } };
                    g.FillRectangle(b, 0, h - 190, w, 190);
                }
            }
        }

        // ------------------------------------------------------------------ simulation

        private PointF HangPos() => new PointF(_tip.X + (float)Math.Sin(_time * 1.4) * 4, _tip.Y + 38 + (float)Math.Cos(_time * 1.1) * 1.5f);

        private void StartCast()
        {
            _from = _anim == Anim.Hang ? HangPos() : _fpos;
            _anim = Anim.Cast;
            _animT = 0;
            _biteT = 0;
        }

        private void StartReel(Outcomes kind)
        {
            _from = new PointF(_fpos.X, _rest.Y);
            _anim = Anim.Reel;
            _animT = 0;
            PlayOutcome(kind, _fpos.X, _rest.Y);
        }

        private void PlayOutcome(Outcomes kind, float x, float y)
        {
            if (kind == Outcomes.Catch)
            {
                _fishOn = true;
                _fx = x; _fy = y + 2; _fvx = (_tip.X + 30 - x) / 1.05f; _fvy = -360; _ft = 0;
                Splash(x, y, 22, 210);
                AddRing(x, y, 40, 0.9f);
                AddRing(x, y, 24, 0.7f);
                _flash = 0.55f; _flashRgb = Color.FromArgb(255, 205, 110);
                _shake = Math.Max(_shake, 0.5f);
            }
            else if (kind == Outcomes.Lost)
            {
                Splash(x, y, 14, 170);
                AddRing(x, y, 34, 0.8f);
                _flash = 0.25f; _flashRgb = Color.FromArgb(255, 90, 80);
            }
            else AddRing(x, y, 20, 0.5f);
        }

        private void StartBite()
        {
            _biteT = 0;
            _bobV = 260;
            Splash(_fpos.X, _rest.Y, 16, 190);
            AddRing(_rest.X, _rest.Y, 36, 0.9f);
            _flash = 0.35f; _flashRgb = Color.FromArgb(255, 200, 90);
            _shake = 1;
        }

        private float R() => (float)_rnd.NextDouble();

        private void AddParticle(float x, float y, float vx, float vy, float life, float size, int kind)
        {
            if (_partCount >= MaxParticles) return;
            _parts[_partCount++] = new Particle { X = x, Y = y, Vx = vx, Vy = vy, Life = life, Max = life, Size = size, Kind = kind };
        }

        private void Splash(float x, float y, int n, float power)
        {
            for (int i = 0; i < n; i++)
            {
                float a = -(float)Math.PI / 2 + (R() - 0.5f) * 1.9f, s = power * (0.35f + R() * 0.75f);
                AddParticle(x + (R() - 0.5f) * 8, y, (float)Math.Cos(a) * s, (float)Math.Sin(a) * s, 0.5f + R() * 0.5f, 1 + R() * 1.8f, 0);
            }
        }

        private void Sparkles(float x, float y, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float a = R() * 6.2832f, s = 40 + R() * 130;
                AddParticle(x, y, (float)Math.Cos(a) * s, (float)Math.Sin(a) * s - 40, 0.6f + R() * 0.7f, 0.5f + R() * 0.7f, 1);
            }
        }

        private void AddRing(float x, float y, float r, float a)
        {
            if (_ringCount >= MaxRings) return;
            _rings[_ringCount++] = new Ring { X = x, Y = y, T = 0, Max = 1.4f + r / 40f, R = r, A = a };
        }

        private void Update(float dt)
        {
            _animT += dt;
            float tension = 0.1f;
            if (_anim == Anim.Hang)
            {
                _fpos = HangPos();
                tension = 0;
            }
            else if (_anim == Anim.Cast)
            {
                float u = Clamp01(_animT / CastTime), e = EaseOut(u);
                _fpos = new PointF(Lerp(_from.X, _rest.X, e), Lerp(_from.Y, _rest.Y, u) - (float)Math.Sin(Math.PI * u) * _h * 0.24f);
                tension = 0.25f * (1 - u);
                if (u >= 1)
                {
                    _anim = Anim.Float;
                    _bobY = 0; _bobV = 210;
                    Splash(_rest.X, _rest.Y, 12, 150);
                    AddRing(_rest.X, _rest.Y, 30, 0.85f);
                    AddRing(_rest.X, _rest.Y, 16, 0.6f);
                }
            }
            else if (_anim == Anim.Reel)
            {
                float u = Clamp01(_animT / ReelTime);
                PointF hp = HangPos();
                float skim = Clamp01(u / 0.6f);
                float x = Lerp(_from.X, hp.X, u < 0.6f ? skim * 0.7f : 0.7f + (u - 0.6f) / 0.4f * 0.3f);
                float y = u < 0.6f ? _from.Y : Lerp(_from.Y, hp.Y, EaseOut((u - 0.6f) / 0.4f));
                _fpos = new PointF(x, y);
                if (u < 0.6f && R() < dt * 40) AddParticle(x + 6, y, 20 + R() * 30, -20 - R() * 40, 0.4f, 1 + R(), 2);
                tension = 0.6f * (1 - u);
                if (u >= 1) _anim = Anim.Hang;
            }

            if (_anim == Anim.Float)
            {
                bool bite = _phase == Phase.Bite;
                if (bite) _biteT += dt;
                float target = bite ? 9 + (float)Math.Sin(_biteT * 21) * 3.5f : (float)Math.Sin(_time * 2.1) * 1.6f;
                _bobV += (-(_bobY - target) * (bite ? 260 : 60) - _bobV * (bite ? 10 : 5)) * dt;
                _bobY += _bobV * dt;
                float sway = bite ? (float)Math.Sin(_biteT * 17) * 2.8f : (float)Math.Sin(_time * 0.5) * 3;
                _fpos = new PointF(_rest.X + sway, _rest.Y);
                tension = bite ? 0.85f + (float)Math.Sin(_biteT * 21) * 0.1f : 0.12f;
                _ringClock -= dt;
                if (_ringClock <= 0)
                {
                    _ringClock = bite ? 0.33f : 1.9f;
                    AddRing(_fpos.X, _rest.Y, bite ? 26 : 16, bite ? 0.75f : 0.35f);
                    if (bite) Splash(_fpos.X, _rest.Y, 3, 110);
                }
            }
            _tension += (tension - _tension) * Math.Min(1, dt * 10);

            for (int i = _partCount - 1; i >= 0; i--)
            {
                ref Particle p = ref _parts[i];
                p.Life -= dt;
                if (p.Life <= 0) { _parts[i] = _parts[--_partCount]; continue; }
                p.Vy += (p.Kind == 1 ? 60 : Gravity) * dt;
                if (p.Kind == 1) { p.Vx *= 1 - dt * 2; p.Vy *= 1 - dt * 2; }
                p.X += p.Vx * dt; p.Y += p.Vy * dt;
            }
            for (int i = _ringCount - 1; i >= 0; i--)
            {
                _rings[i].T += dt;
                if (_rings[i].T >= _rings[i].Max) _rings[i] = _rings[--_ringCount];
            }
            if (_fishOn)
            {
                _ft += dt;
                _fvy += Gravity * 0.7f * dt;
                _fx += _fvx * dt; _fy += _fvy * dt;
                if (R() < dt * 30) AddParticle(_fx, _fy, (R() - 0.5f) * 40, 10, 0.5f, 1 + R(), 0);
                if (_ft > 0.95f) { Sparkles(_fx, _fy, 26); _flash = Math.Max(_flash, 0.3f); _fishOn = false; }
            }
            _shake = Math.Max(0, _shake - dt * 2.2f);
            _flash = Math.Max(0, _flash - dt * 1.6f);
        }

        // ------------------------------------------------------------------ drawing

        private Color A(Color c, float a) => Color.FromArgb((int)Math.Round(255 * Clamp01(a)), c.R, c.G, c.B);

        /// <summary>Draws a cached sprite at an opacity (one reused ImageAttributes, no allocation).</summary>
        private void DrawSprite(Graphics g, Bitmap bmp, float xHud, float yHud, float wHud, float hHud, float alpha)
        {
            if (bmp == null || alpha <= 0.01f) return;
            _cm.Matrix33 = Clamp01(alpha);
            _ia.SetColorMatrix(_cm);
            float k = K;
            g.DrawImage(bmp, new Rectangle((int)(xHud * k), (int)(yHud * k), (int)Math.Ceiling(wHud * k), (int)Math.Ceiling(hHud * k)), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, _ia);
        }

        protected override void Paint(Graphics g)
        {
            if (_w <= 0 || Math.Abs(_w - Size.Width / K) > 0.5f) DoLayout();
            if (_layerSize != Size || _bg == null) BuildLayers();
            float k = K, w = _w, h = _h, hy = _hy, time = _time;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImageUnscaled(_bg, 0, 0);
            GraphicsState state = g.Save();
            g.ScaleTransform(k, k);
            if (_shake > 0)
            {
                float a = _shake * _shake * 4;
                g.TranslateTransform((R() - 0.5f) * a, (R() - 0.5f) * a);
            }

            // stars and clouds
            for (int i = 0; i < _stars.Length; i++)
            {
                Star s = _stars[i];
                _brush.Color = A(Color.FromArgb(233, 240, 255), 0.25f + 0.6f * (0.5f + 0.5f * (float)Math.Sin(time * s.Sp + s.Ph)));
                g.FillRectangle(_brush, s.X, s.Y, s.S, s.S);
            }
            for (int i = 0; i < _clouds.Length; i++)
            {
                Bitmap c = _clouds[i];
                if (c == null) continue;
                float cw = c.Width / k, ch = c.Height / k, span = w + cw;
                float x = (time * (5 + i * 3) + i * span * 0.37f) % span - cw;
                DrawSprite(g, c, x, hy * (0.18f + i * 0.2f), cw, ch, 1f);
            }

            // waves (lighter and longer toward the viewer)
            _pen.StartCap = _pen.EndCap = LineCap.Round;
            for (int r = 0; r < _waves.Length; r++)
            {
                Wave row = _waves[r];
                _pen.Color = A(Color.FromArgb(150, 200, 255), 0.05f + row.D * 0.2f);
                _pen.Width = 0.6f + row.D * 1.4f;
                float span = w + 120;
                for (int i = 0; i < row.Segs.Length; i++)
                {
                    Seg s = row.Segs[i];
                    float x = (s.X + time * row.Speed) % span;
                    if (x < 0) x += span;
                    x -= 60;
                    float y = row.Y + (float)Math.Sin(time * 1.3 + s.X) * row.D * 1.5f;
                    g.DrawLine(_pen, x, y, x + s.Len, y);
                }
            }
            _pen.StartCap = _pen.EndCap = LineCap.Flat;

            // the moon's broken reflection
            for (int r = 0; r < 24; r++)
            {
                float d = r / 24f, y = hy + 3 + (h - hy) * 0.62f * (float)Math.Pow(d, 1.3);
                float width = (6 + 46 * d) * (0.45f + 0.55f * Math.Abs((float)Math.Sin(time * 1.6 + r * 1.31)));
                float x = _moon.X + (float)Math.Sin(time * 0.9 + r * 0.7) * (2 + 5 * d) - width / 2;
                _brush.Color = A(Color.FromArgb(255, 232, 170), (0.5f - 0.38f * d) * 0.8f);
                g.FillRectangle(_brush, x, y, width, 1 + d * 2);
            }

            // ripples (perspective ellipses)
            _pen.Width = 1.3f;
            for (int i = 0; i < _ringCount; i++)
            {
                Ring r = _rings[i];
                float u = r.T / r.Max, rad = r.R * (0.2f + EaseOut(u) * 1.6f);
                _pen.Color = A(Color.FromArgb(200, 230, 255), r.A * (1 - u));
                g.DrawEllipse(_pen, r.X - rad, r.Y + 1 - rad * 0.27f, rad * 2, rad * 0.54f);
            }

            // bite glow under the float
            bool biteFloat = _phase == Phase.Bite && _anim == Anim.Float;
            if (biteFloat) DrawSprite(g, _biteGlow, _fpos.X - 34, _rest.Y - 34 + 2, 68, 68, 0.45f + 0.35f * (float)Math.Sin(time * 12));

            DrawLine(g);
            DrawFloat(g);
            if (_fishOn) DrawFish(g);
            DrawRod(g);
            DrawReeds(g);

            // particles: drops / foam, then sparkles
            for (int i = 0; i < _partCount; i++)
            {
                Particle p = _parts[i];
                if (p.Kind == 1) continue;
                _brush.Color = A(p.Kind == 2 ? Color.FromArgb(207, 230, 255) : Color.FromArgb(169, 214, 255), Clamp01(p.Life / p.Max) * (p.Kind == 2 ? 0.5f : 0.85f));
                g.FillEllipse(_brush, p.X - p.Size, p.Y - p.Size, p.Size * 2, p.Size * 2);
            }
            for (int i = 0; i < _partCount; i++)
            {
                Particle p = _parts[i];
                if (p.Kind != 1) continue;
                float sz = 20 * p.Size;
                DrawSprite(g, _sparkGlow, p.X - sz / 2, p.Y - sz / 2, sz, sz, Clamp01(p.Life / p.Max));
            }
            // fireflies
            for (int i = 0; i < _flies.Length; i++)
            {
                Firefly f = _flies[i];
                float x = f.Ax + (float)Math.Sin(time * f.Sp + f.Ph) * 22, y = f.Ay + (float)Math.Sin(time * f.Sp * 1.7 + f.Ph * 2) * 10;
                DrawSprite(g, _fireGlow, x - 14, y - 14, 28, 28, Math.Max(0, (float)Math.Sin(time * f.Sp * 2.3 + f.Ph)) * 0.85f);
            }
            if (biteFloat) DrawBiteMark(g);

            g.Restore(state);
            g.DrawImageUnscaled(_fg, 0, 0);
            if (_flash > 0)
            {
                _brush.Color = A(_flashRgb, _flash * 0.35f);
                g.FillRectangle(_brush, 0, 0, Size.Width, Size.Height);
            }
        }

        /// <summary>The rod tip bent toward the float by the line's tension.</summary>
        private PointF RodTip() => new PointF(_tip.X + _tension * _w * 0.05f, _tip.Y + _tension * _h * 0.07f);

        private void DrawRod(Graphics g)
        {
            PointF tip = RodTip();
            float bx = -_w * 0.04f, by = _h * 1.04f;
            float cx = Lerp(bx, tip.X, 0.55f) - _tension * 6, cy = Lerp(by, tip.Y, 0.55f) - 18 - _tension * 26;
            const int n = 18;
            for (int i = 0; i <= n; i++)
            {
                float u = i / (float)n, a = (1 - u) * (1 - u), b = 2 * (1 - u) * u, c = u * u;
                float x = a * bx + b * cx + c * tip.X, y = a * by + b * cy + c * tip.Y;
                float dx = 2 * (1 - u) * (cx - bx) + 2 * u * (tip.X - cx), dy = 2 * (1 - u) * (cy - by) + 2 * u * (tip.Y - cy);
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0) len = 1;
                float wid = Lerp(5, 0.7f, u);
                _rodPts[i] = new PointF(x - dy / len * wid, y + dx / len * wid);
                _rodPts[2 * n + 1 - i] = new PointF(x + dy / len * wid, y - dx / len * wid);
            }
            _path.Reset();
            _path.AddPolygon(_rodPts);
            _brush.Color = Color.FromArgb(18, 12, 8);
            g.FillPath(_brush, _path);
            // moonlit edge
            _pen.Color = Color.FromArgb(89, 255, 225, 170);
            _pen.Width = 0.8f;
            for (int i = n + 1; i < 2 * n + 1; i++) g.DrawLine(_pen, _rodPts[i], _rodPts[i + 1]);
            // line guides
            _pen.Color = Color.FromArgb(128, 200, 210, 230);
            for (int j = 0; j < 3; j++)
            {
                float u = j == 0 ? 0.55f : j == 1 ? 0.75f : 0.9f, a = (1 - u) * (1 - u), b = 2 * (1 - u) * u, c = u * u;
                float x = a * bx + b * cx + c * tip.X, y = a * by + b * cy + c * tip.Y;
                g.DrawEllipse(_pen, x - 1.6f, y + 2 - 1.6f, 3.2f, 3.2f);
            }
        }

        private float FloatTopY() => _anim == Anim.Float ? _fpos.Y + _bobY - 24 : _fpos.Y - 24;

        private void DrawLine(Graphics g)
        {
            PointF tip = RodTip();
            float fx = _fpos.X, fy = FloatTopY();
            float sag = (1 - _tension) * (_anim == Anim.Float ? 46 : _anim == Anim.Hang ? 4 : 18);
            _pen.Color = _tension > 0.6f ? Color.FromArgb(204, 240, 248, 255) : Color.FromArgb(128, 220, 235, 255);
            _pen.Width = _tension > 0.6f ? 1.1f : 0.9f;
            // quadratic → cubic control points
            float qx = (tip.X + fx) / 2, qy = (tip.Y + fy) / 2 + sag;
            g.DrawBezier(_pen, tip.X, tip.Y, tip.X + 2f / 3f * (qx - tip.X), tip.Y + 2f / 3f * (qy - tip.Y), fx + 2f / 3f * (qx - fx), fy + 2f / 3f * (qy - fy), fx, fy);
        }

        private void FloatBody(Graphics g, float x, float y, float alpha)
        {
            _pen.Color = A(Color.FromArgb(233, 227, 210), alpha);
            _pen.Width = 1.4f;
            g.DrawLine(_pen, x, y - 24, x, y - 13);
            _brush.Color = A(Color.FromArgb(255, 59, 47), alpha);
            g.FillEllipse(_brush, x - 1.6f, y - 24 - 1.6f, 3.2f, 3.2f);
            _brush.Color = A(Color.FromArgb(230, 60, 50), alpha);
            g.FillPie(_brush, x - 6, y - 6 - 7.5f, 12, 15, 180, 180);
            _brush.Color = A(Color.FromArgb(225, 228, 232), alpha);
            g.FillPie(_brush, x - 6, y - 6 - 7.5f, 12, 15, 0, 180);
            _brush.Color = A(Color.White, 0.55f * alpha);
            g.FillEllipse(_brush, x - 2.2f - 1.3f, y - 9.5f - 2.4f, 2.6f, 4.8f);
            _pen.Color = A(Color.FromArgb(90, 95, 102), alpha);
            _pen.Width = 1.2f;
            g.DrawLine(_pen, x, y + 1.5f, x, y + 8);
        }

        private void DrawFloat(Graphics g)
        {
            float x = _fpos.X;
            bool onWater = _anim == Anim.Float;
            float y = onWater ? _fpos.Y + _bobY : _fpos.Y, wl = _fpos.Y;
            if (!onWater) { FloatBody(g, x, y, 1f); return; }
            // above the waterline at full strength, below it faded
            GraphicsState st = g.Save();
            g.SetClip(new RectangleF(x - 20, wl - 40, 40, 40), CombineMode.Intersect);
            FloatBody(g, x, y, 1f);
            g.Restore(st);
            st = g.Save();
            g.SetClip(new RectangleF(x - 20, wl, 40, 30), CombineMode.Intersect);
            FloatBody(g, x, y, 0.28f);
            g.Restore(st);
            _pen.Color = Color.FromArgb(140, 210, 235, 255);
            _pen.Width = 1;
            g.DrawEllipse(_pen, x - 8, wl - 2, 16, 4);
        }

        private void DrawFish(Graphics g)
        {
            float fade = Clamp01((0.95f - _ft) / 0.25f);
            GraphicsState st = g.Save();
            g.TranslateTransform(_fx, _fy);
            g.RotateTransform((float)(Math.Atan2(_fvy, _fvx) * 180 / Math.PI));
            _brush.Color = A(Color.FromArgb(143, 183, 201), fade);
            g.FillEllipse(_brush, -14, -5.5f, 28, 11);
            float flap = (float)Math.Sin(_ft * 40) * 3;
            _tail[0] = new PointF(-11, 0);
            _tail[1] = new PointF(-20, -6 + flap);
            _tail[2] = new PointF(-20, 6 + flap);
            _path.Reset();
            _path.AddPolygon(_tail);
            g.FillPath(_brush, _path);
            _brush.Color = A(Color.FromArgb(10, 16, 22), fade);
            g.FillEllipse(_brush, 8.5f - 1.2f, -1.5f - 1.2f, 2.4f, 2.4f);
            _pen.Color = A(Color.FromArgb(255, 240, 200), 0.6f * fade);
            _pen.Width = 0.8f;
            g.DrawArc(_pen, -14, -5.5f, 28, 11, 198, 144);
            g.Restore(st);
        }

        private void DrawReeds(Graphics g)
        {
            _pen.Color = Color.FromArgb(3, 8, 15);
            _brush.Color = Color.FromArgb(5, 11, 18);
            float h = _h;
            for (int i = 0; i < _reeds.Length; i++)
            {
                Reed r = _reeds[i];
                float sway = (float)Math.Sin(_time * 0.9 + r.Ph) * 5 * (r.H / h) + r.Bend;
                float tx = r.X + sway, ty = h + 4 - r.H;
                _pen.Width = r.W;
                float qx = r.X + sway * 0.2f, qy = h - r.H * 0.5f;
                g.DrawBezier(_pen, r.X, h + 4, r.X + 2f / 3f * (qx - r.X), h + 4 + 2f / 3f * (qy - h - 4), tx + 2f / 3f * (qx - tx), ty + 2f / 3f * (qy - ty), tx, ty);
                if (r.Head)
                {
                    GraphicsState st = g.Save();
                    g.TranslateTransform(tx - sway * 0.06f, ty + 12);
                    g.RotateTransform(sway * 0.012f * 57.3f);
                    g.FillEllipse(_brush, -2.6f, -9, 5.2f, 18);
                    g.Restore(st);
                }
            }
        }

        private void DrawBiteMark(Graphics g)
        {
            float x = _fpos.X, y = _rest.Y - 46, s = 1 + 0.12f * (float)Math.Sin(_time * 14);
            DrawSprite(g, _biteGlow, x - 34 * s, y - 34 * s, 68 * s, 68 * s, 0.8f);
            Font f = ModernSkin.Font(26 / K, true); // a cached font size: the scale pulse is the glow's only
            var r = new RectangleF(x - 20, y - 19, 40, 40);
            _brush.Color = Color.FromArgb(217, 40, 16, 0);
            for (int d = 0; d < 4; d++)
                g.DrawString("!", f, _brush, new RectangleF(r.X + (d == 0 ? -2 : d == 1 ? 2 : 0), r.Y + (d == 2 ? -2 : d == 3 ? 2 : 0), r.Width, r.Height), _centre);
            _brush.Color = Color.FromArgb(255, 214, 107);
            g.DrawString("!", f, _brush, r, _centre);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeLayers();
                _brush.Dispose();
                _pen.Dispose();
                _path.Dispose();
                _ia.Dispose();
                _centre.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
