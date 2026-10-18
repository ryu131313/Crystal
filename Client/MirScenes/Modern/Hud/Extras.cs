// Ported from WebClient/src/ui/modern/hud/extras.ts: the new UI's small HUD extras.
//   Output lines  GameScene.OutputMessage (up to 10, 5 s each, kept and expired by GameScene; Quest gold, Guild pink, else green)
//                 at the top left beside the party frames (phase 2).
//   Timer         TimerDialog: the nearest S.SetTimer counting down (S.ExpireTimer ends one), a pill with a ring for the time
//                 left, red in the last 10 s (or a type 2 timer). S.SetTimer has no label: the pill shows the countdown only.
//   Compass       CompassDialog: an arrow around the character towards the S.SetCompass cell, with the NPC there (the map's
//                 ClientMapInfo NPCs, from ModernUi.MapData) or the cell, and the steps left; hidden on the target cell (the
//                 target is kept, CompassDialog.Process).
//   Roll          RollDialog (S.Roll die / yut): the card shows the classic Prguse / Items pictures; click to roll unless it rolls
//                 itself, the die spins 4 frames x 6 and the yut throws once (100 ms a frame, the classic sounds), the result is
//                 shown and RollDialog.ReturnResult calls the NPC page (C.CallNPC "[page]", gated by GameScene.NPCTime); click
//                 again to close.
//   Poisons       a status line while frozen / stunned / paralysed (time left from S.PoisonStatus via Models.Poisons where the
//                 server sent one) and soft screen tints (blind, frost, paralysis, daze, slow, green poison), painted once per
//                 flag change. The poison icon row and the durability strip are the bottom HUD's (Hud.cs); its countdowns can
//                 use <see cref="HudExtras.PoisonTimeText"/>.
//   Notice board  S.UpdateNotice: the "notice" window (ExtrasNotice.cs).
// OpenBrowser and the NPC input popup are other areas' (GameScene / ModernUi.Npc).
using System.Drawing.Drawing2D;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;
using Client.MirSounds;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class HudOutputs : ModernSurface
    {
        public const int Width = 520, LineH = 17, MaxLines = 10;

        private readonly List<OutputLine> _lines = new List<OutputLine>();
        private int _count = -1;
        private object _first, _last;
        private readonly Action<bool> _onRelayout;

        public HudOutputs()
        {
            PassThrough = true;
            Visible = false;
            _onRelayout = rebuild =>
            {
                Size = new Size(S(Width), S(MaxLines * LineH + 4));
                Location = new Point(S(210), S(14));
                for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
                _lines.Clear();
                _count = -1;
                Check();
            };
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
            Size = new Size(S(Width), S(MaxLines * LineH + 4));
            Location = new Point(S(210), S(14));
        }

        public int LineCount => _lines.Count;

        /// <summary>Per frame from the HUD (also while hidden): a count and two reference compares; rebuilds only on a change.</summary>
        internal void Check()
        {
            List<GameScene.OutPutMessage> list = GameScene.Scene?.OutputMessages;
            int n = list?.Count ?? 0;
            object first = n > 0 ? list[0] : null, last = n > 0 ? list[n - 1] : null;
            if (n == _count && first == _first && last == _last) return;
            _count = n;
            _first = first;
            _last = last;
            for (int i = 0; i < _lines.Count; i++) _lines[i].Dispose();
            _lines.Clear();
            for (int i = 0; i < n && i < MaxLines; i++)
            {
                var line = new OutputLine(list[i].Message, list[i].Type) { Parent = this, Location = new Point(0, S(2 + i * LineH)), Size = new Size(S(Width), S(LineH)) };
                _lines.Add(line);
            }
            Visible = n > 0;
            TextureValid = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            base.Dispose(disposing);
        }
    }

    /// <summary>One output line (.houts div): 12 px semi-bold with a dark shadow, coloured by type.</summary>
    internal sealed class OutputLine : ModernControl
    {
        private readonly string _text;
        private readonly Color _colour;

        public OutputLine(string text, OutputMessageType type)
        {
            NotControl = true;
            _text = text ?? string.Empty;
            _colour = type == OutputMessageType.Quest ? Color.FromArgb(255, 255, 216, 74)
                : type == OutputMessageType.Guild ? Color.FromArgb(255, 255, 111, 192) : Color.FromArgb(255, 111, 224, 138);
        }

        protected override void Paint(Graphics g)
        {
            HudKit.ShadowText(g, _text, ModernSkin.Font(12, true), _colour, new RectangleF(1, 0, Size.Width - 2, Size.Height), StringAlignment.Near, StringAlignment.Center, 200);
        }
    }

    /// <summary>
    /// The phase 4 HUD extras' controller (an invisible, zero-size surface on the Hud layer so ModernRoot.Process runs it every
    /// frame): the timer, compass, roll and poison pieces it owns are their own surfaces on the Hud layer, built below the bottom
    /// HUD (ModernUi.Guild registers before BuildHud). Every repaint happens only on a value change.
    /// </summary>
    internal sealed class HudExtras : ModernSurface
    {
        private sealed class Timer
        {
            public string Key;
            public byte Type;
            public long Relative;
            public bool Refresh;
        }

        private const PoisonType Locks = PoisonType.Frozen | PoisonType.Stun | PoisonType.Paralysis | PoisonType.LRParalysis;
        private static readonly PoisonType[] LockOrder = { PoisonType.Stun, PoisonType.Frozen, PoisonType.Paralysis, PoisonType.LRParalysis };

        private readonly ModernLayer _layer;
        private readonly Action<bool> _onRelayout;
        private TimerPill _timerPill;
        private CompassView _compass;
        private RollCard _roll;
        private PoisonLine _poisonLine;
        private PoisonTint _tint;

        // TimerDialog
        private readonly List<Timer> _timers = new List<Timer>();
        private Timer _current;
        private bool _timerStarted;
        private int _counter, _timerMax = 1, _lastCounter = -1;
        private long _timerTime;

        // CompassDialog
        private Point _compassTarget = Point.Empty;

        // poisons
        private PoisonType _flags = (PoisonType)ushort.MaxValue;
        private long _nextPoison;

        public HudExtras(ModernLayer layer)
        {
            _layer = layer;
            PassThrough = true;
            Size = Size.Empty;
            Parent = layer;
            BuildPieces();
            _onRelayout = rebuild =>
            {
                if (rebuild) BuildPieces();
                else PlacePieces();
            };
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
        }

        // test hooks
        internal int TimerCounter => _timerPill != null && _timerPill.Visible ? _counter : -1;
        internal bool CompassShown => _compass != null && _compass.Visible;
        internal string CompassText => _compass?.Text ?? string.Empty;
        internal RollCard RollCard => _roll;
        internal string PoisonText => _poisonLine != null && _poisonLine.Visible ? _poisonLine.Text : string.Empty;
        internal bool TintShown => _tint != null && _tint.Visible;

        private void BuildPieces()
        {
            _tint?.Dispose();
            _timerPill?.Dispose();
            _compass?.Dispose();
            _poisonLine?.Dispose();
            var keepRoll = _roll?.State;
            _roll?.Dispose();
            // the tint goes below every other HUD piece
            _tint = new PoisonTint();
            _layer.InsertControl(0, _tint);
            _timerPill = new TimerPill { Parent = _layer, Visible = false };
            _compass = new CompassView { Parent = _layer, Visible = false };
            _poisonLine = new PoisonLine { Parent = _layer, Visible = false };
            _roll = new RollCard { Parent = _layer, Visible = false };
            if (keepRoll != null) _roll.Restore(keepRoll);
            _flags = (PoisonType)ushort.MaxValue;
            _lastCounter = -1;
            if (_current != null && _timerStarted) ShowTimer();
            PlacePieces();
        }

        private void PlacePieces()
        {
            int w = Settings.ScreenWidth, h = Settings.ScreenHeight;
            _tint.Size = new Size(w, h);
            _tint.Location = Point.Empty;
            _timerPill.Location = new Point((w - _timerPill.Size.Width) / 2, S(44));
            _compass.Location = new Point(w / 2 - _compass.Size.Width / 2, (int)(h * 0.47f) - _compass.Size.Height / 2);
            _poisonLine.Location = new Point((w - _poisonLine.Size.Width) / 2, (int)(h * 0.52f));
            _roll.Location = new Point((w - _roll.Size.Width) / 2, (int)(h * 0.40f) - _roll.Size.Height / 2);
            _tint.Repaint(_flags == (PoisonType)ushort.MaxValue ? 0 : _flags);
        }

        // ------------------------------------------------------------------ seam (ModernUi.Guild)

        /// <summary>TimerDialog.AddTimer.</summary>
        public void SetTimer(S.SetTimer p)
        {
            if (p == null) return;
            long relative = p.Seconds + CMain.Time / 1000;
            for (int i = 0; i < _timers.Count; i++)
            {
                if (_timers[i].Key != p.Key) continue;
                _timers[i].Relative = relative;
                _timers[i].Type = p.Type;
                _timers[i].Refresh = true;
                return;
            }
            _timers.Add(new Timer { Key = p.Key, Type = p.Type, Relative = relative, Refresh = true });
        }

        /// <summary>TimerDialog.ExpireTimer.</summary>
        public void ExpireTimer(string key)
        {
            for (int i = 0; i < _timers.Count; i++)
            {
                if (_timers[i].Key != key) continue;
                _timers[i].Relative = 0;
                if (_timers[i] == _current) _counter = 0;
                return;
            }
        }

        /// <summary>CompassDialog.SetPoint (0, 0 = clear, as the classic Point.Empty check).</summary>
        public void SetCompass(Point location) => _compassTarget = location;

        /// <summary>RollDialog.Setup.</summary>
        public void Roll(S.Roll p)
        {
            if (p == null || _roll == null) return;
            _roll.Setup(p.Type, p.Page ?? string.Empty, p.Result, p.AutoRoll);
        }

        /// <summary>The short time left of a poison's S.PoisonStatus timer ("12s", "3.2s"), or "" when the server sent none (for the
        /// bottom HUD's poison row).</summary>
        public static string PoisonTimeText(PoisonType type)
        {
            Models.PoisonTimer t = GameScene.Models?.Poisons.Get(type);
            if (t == null) return string.Empty;
            long ms = Models.PoisonTimers.TimeLeft(t);
            return ms >= 10000 ? (ms / 1000) + "s" : (ms / 1000.0).ToString("0.0") + "s";
        }

        // ------------------------------------------------------------------ per frame

        public override void Process()
        {
            if (GameScene.User == null || _timerPill == null || _timerPill.IsDisposed) return;
            ProcessTimers();
            ProcessCompass();
            _roll.Tick();
            ProcessPoisons();
        }

        /// <summary>TimerDialog.Process.</summary>
        private void ProcessTimers()
        {
            Timer best = null;
            for (int i = 0; i < _timers.Count; i++) if (best == null || _timers[i].Relative < best.Relative) best = _timers[i];
            long now = CMain.Time;
            if (best != null && (best != _current || best.Refresh))
            {
                _current = best;
                best.Refresh = false;
                _timerStarted = true;
                _timerTime = now + 1000;
                _counter = (int)(best.Relative - now / 1000);
                ShowTimer();
            }
            if (_current == null || !_timerStarted || now < _timerTime) return;
            _counter--;
            _timerTime = now + 1000;
            if (_counter < 0)
            {
                _timerStarted = false;
                _timers.Remove(_current);
                _current = null;
                _lastCounter = -1;
                if (_timerPill.Visible) _timerPill.Visible = false;
                return;
            }
            ShowTimer();
        }

        private void ShowTimer()
        {
            // TimerDialog has no length: the ring runs from the counter seen when the timer started (or was refreshed)
            if (_counter > _lastCounter) _timerMax = Math.Max(1, _counter);
            _lastCounter = _counter;
            _timerPill.Set(Math.Max(0, _counter), _timerMax, _counter <= 10 || _current?.Type == 2);
            if (!_timerPill.Visible) _timerPill.Visible = true;
        }

        /// <summary>CompassDialog.Process: hidden without a target and on the target cell.</summary>
        private void ProcessCompass()
        {
            Point d = _compassTarget, u = GameScene.User.CurrentLocation;
            bool show = d != Point.Empty && d != u;
            if (_compass.Visible != show) _compass.Visible = show;
            if (!show) return;
            int dx = d.X - u.X, dy = d.Y - u.Y;
            _compass.Set(dx, dy, d);
        }

        /// <summary>The status line (4 Hz while frozen / stunned / paralysed) and the tints (on a flag change).</summary>
        private void ProcessPoisons()
        {
            PoisonType flags = GameScene.User.Poison;
            bool changed = flags != _flags;
            if (changed)
            {
                _flags = flags;
                _tint.Repaint(flags);
            }
            if (!changed && CMain.Time < _nextPoison) return;
            _nextPoison = CMain.Time + 250;
            PoisonType lockFlag = 0;
            for (int i = 0; i < LockOrder.Length; i++) if ((flags & LockOrder[i] & Locks) != 0) { lockFlag = LockOrder[i]; break; }
            bool show = lockFlag != 0;
            if (_poisonLine.Visible != show) _poisonLine.Visible = show;
            if (!show) return;
            string name = lockFlag.ToLocalizedString().ToUpperInvariant(), time = PoisonTimeText(lockFlag);
            _poisonLine.Set(time.Length > 0 ? name + " · " + time : name, PoisonColour(lockFlag));
        }

        private static Color PoisonColour(PoisonType p) => p switch
        {
            PoisonType.Stun => Color.FromArgb(255, 0xff, 0xe1, 0x4a),
            PoisonType.Frozen => Color.FromArgb(255, 0x5a, 0xa0, 0xff),
            _ => Color.FromArgb(255, 0xc4, 0xc4, 0xc4),
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // the pieces are the layer's children: the layer disposes them (disposing siblings here would shrink the
                // list the layer is walking)
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The timer pill (.htm): a 30 px ring for the time left and the countdown, red when hot.</summary>
    internal sealed class TimerPill : ModernSurface
    {
        private readonly PaintBox _bg;
        private readonly ProgressRing _ring;
        private string _text = string.Empty;
        private bool _hot;

        public TimerPill()
        {
            PassThrough = true;
            Size = new Size(S(116), S(40));
            _bg = new PaintBox(Paint) { Parent = this, Location = Point.Empty, Size = Size, NotControl = true };
            _ring = new ProgressRing(30, 3) { Parent = this, Location = new Point(S(5), S(5)) };
        }

        internal string Text => _text;

        public void Set(int counter, int max, bool hot)
        {
            string text = Gd.Clock(counter * 1000L);
            _ring.Set(counter / (float)Math.Max(1, max));
            if (hot != _hot) { _hot = hot; _ring.Colour = hot ? ModernTheme.Bad : Color.Empty; _bg.Invalidate(); }
            if (text != _text) { _text = text; _bg.Invalidate(); }
        }

        private void Paint(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(1.5f, 1.5f, b.Size.Width - 3f, b.Size.Height - 3f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f))
            {
                using (var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), ModernTheme.Alpha(ModernTheme.PanelC, 0.8f), ModernTheme.Alpha(ModernTheme.PanelB, 0.88f), LinearGradientMode.Vertical)) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, _hot ? ModernTheme.Bad : ModernTheme.Frame);
            }
            using (GraphicsPath o = ModernSkin.RoundRect(RectangleF.Inflate(r, 1, 1), r.Height / 2f + 1)) ModernSkin.Stroke(g, o, Color.Black);
            ModernSkin.Text(g, _text, ModernSkin.Font(15, true), _hot ? Color.FromArgb(255, 0xff, 0xb3, 0xa6) : Color.White, new RectangleF(42 * k, 0, b.Size.Width - 50 * k, b.Size.Height), StringAlignment.Center);
        }
    }

    /// <summary>The compass (.hcomp): an accent arrow 104 px from the character, turned towards the target, and the label below.</summary>
    internal sealed class CompassView : ModernSurface
    {
        private readonly PaintBox _box;
        private int _angle = int.MinValue, _steps = -1;
        private Point _target = new Point(-1, -1);
        private string _name = string.Empty, _dist = string.Empty;
        private static readonly string[] Dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        public CompassView()
        {
            PassThrough = true;
            Size = new Size(S(300), S(300));
            _box = new PaintBox(Paint) { Parent = this, Location = Point.Empty, Size = Size, NotControl = true };
        }

        internal string Text => _name + " " + _dist;

        public void Set(int dx, int dy, Point target)
        {
            double ang = Math.Atan2(dx * 48.0, -dy * 32.0) * 180 / Math.PI; // cells are 48 x 32 px on screen
            int a = (int)Math.Round(ang);
            int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (a == _angle && steps == _steps && target == _target) return;
            bool newTarget = target != _target;
            _angle = a;
            _steps = steps;
            _target = target;
            string dir = Dirs[(int)Math.Round(((ang + 360) % 360) / 45) % 8];
            if (newTarget) _name = NameAt(target);
            _dist = W.T(steps == 1 ? ClientTextKeys.ModernGuildCompassStep : ClientTextKeys.ModernGuildCompassSteps, steps, dir);
            _box.Invalidate();
        }

        /// <summary>The NPC at (or next to) the target on the current map, else the cell.</summary>
        private static string NameAt(Point d)
        {
            ModernUi ui = GameScene.Ui as ModernUi;
            int map = GameScene.Scene?.MapControl?.Index ?? -1;
            ClientMapInfo info = map >= 0 ? ui?.MapData?.Map(map) : null;
            if (info?.NPCs != null)
                foreach (ClientNPCInfo n in info.NPCs)
                    if (n != null && Math.Abs(n.Location.X - d.X) <= 1 && Math.Abs(n.Location.Y - d.Y) <= 1)
                        return NpcFmt.Split(n.Name).Name;
            return d.X + ", " + d.Y;
        }

        private void Paint(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, cx = b.Size.Width / 2f, cy = b.Size.Height / 2f;
            GraphicsState st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(_angle);
            // the arrow (icons.ts "M12 2l7 14-7-4-7 4z" in a 24 box), 30 px wide, centred 104 - 15 px above the character
            float s = 30 * k / 24f, ox = -15 * k, oy = -104 * k;
            PointF P(float x, float y) => new PointF(ox + x * s, oy + y * s);
            using (var p = new GraphicsPath())
            {
                p.AddPolygon(new[] { P(12, 2), P(19, 16), P(12, 12), P(5, 16) });
                using (var br = new SolidBrush(ModernTheme.Accent)) g.FillPath(br, p);
                using (var pen = new Pen(Color.White, 1f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            }
            g.Restore(st);
            // the label: name + steps
            Font fb = ModernSkin.Font(11, true), f = ModernSkin.Font(11, true);
            float nw = ModernSkin.Measure(_name, fb), dw = ModernSkin.Measure(_dist, f), w = nw + dw + 26 * k, h = 20 * k;
            RectangleF r = new RectangleF(cx - w / 2f, cy + 66 * k, w, h);
            using (GraphicsPath p = ModernSkin.RoundRect(r, h / 2f))
            {
                using (var br = new SolidBrush(Color.FromArgb(178, 8, 10, 16))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.5f));
            }
            ModernSkin.Text(g, _name, fb, Color.White, new RectangleF(r.X + 10 * k, r.Y, nw + 2, h), StringAlignment.Near, StringAlignment.Center, false);
            ModernSkin.Text(g, _dist, f, ModernTheme.Accent, new RectangleF(r.X + 16 * k + nw, r.Y, dw + 2, h), StringAlignment.Near, StringAlignment.Center, false);
        }
    }

    /// <summary>RollDialog as a card (.hdice): the classic die / yut pictures, the result and the click hint.</summary>
    internal sealed class RollCard : ModernSurface
    {
        internal sealed class RollState
        {
            public int Type, Result, Frame;
            public string Page = string.Empty;
            public bool Rolling, Rolled;
            public long Next;
        }

        private readonly PaintBox _box;
        private RollState _s;

        public RollCard()
        {
            Size = new Size(S(260), S(206));
            _box = new PaintBox(Paint) { Parent = this, Location = Point.Empty, Size = Size };
            _box.Click += (o, e) => Clicked();
            _box.AfterPaint = DrawStage;
        }

        internal RollState State => _s;
        internal bool Rolling => _s != null && _s.Rolling;
        internal bool Rolled => _s != null && _s.Rolled;

        internal void Restore(RollState s)
        {
            _s = s;
            Visible = s != null;
            _box.Invalidate();
        }

        /// <summary>RollDialog.Setup.</summary>
        public void Setup(int type, string page, int result, bool autoRoll)
        {
            _s = new RollState { Type = type == 1 ? 1 : 0, Page = page, Result = result };
            Visible = true;
            BringToFront();
            _box.Invalidate();
            if (autoRoll) DoRoll();
        }

        /// <summary>RollDialog's image click: roll before the result, close after it.</summary>
        internal void Clicked()
        {
            if (_s == null || _s.Rolling) return;
            if (_s.Rolled)
            {
                _s = null;
                Visible = false;
                return;
            }
            DoRoll();
        }

        /// <summary>RollDialog.Roll.</summary>
        private void DoRoll()
        {
            _s.Rolling = true;
            _s.Frame = 0;
            _s.Next = CMain.Time + 100;
            SoundManager.PlaySound(_s.Type == 0 ? 10600 : 10601);
            _box.Invalidate();
        }

        /// <summary>The animation: 100 ms a frame (die 4 frames x 6 spins, yut 6 frames once), then ReturnResult.</summary>
        public void Tick()
        {
            if (_s == null || !_s.Rolling || CMain.Time < _s.Next) return;
            _s.Next = CMain.Time + 100;
            int total = _s.Type == 0 ? 24 : 6;
            if (_s.Frame < total - 1)
            {
                _s.Frame++;
                _box.Invalidate();
                return;
            }
            _s.Rolling = false;
            _s.Rolled = true;
            _box.Invalidate();
            // RollDialog.ReturnResult: C.CallNPC "[page]" on the last NPC, 5 s apart
            if (CMain.Time <= GameScene.NPCTime) return;
            GameScene.NPCTime = CMain.Time + 5000;
            Network.Enqueue(new C.CallNPC { ObjectID = GameScene.NPCID, Key = "[" + _s.Page + "]" });
        }

        private void Paint(Graphics g, PaintBox b)
        {
            if (_s == null) return;
            float k = ModernRoot.K, w = b.Size.Width, h = b.Size.Height;
            RectangleF r = new RectangleF(1.5f, 1.5f, w - 3f, h - 3f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, 14 * k))
            {
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(-w * 0.3f, -h * 0.2f, w * 1.6f, h * 1.2f);
                    using var br = new PathGradientBrush(path)
                    {
                        CenterColor = ModernTheme.Mix(ModernTheme.Accent, ModernSkin.Opaque(ModernTheme.PanelB), 0.14f),
                        SurroundColors = new[] { ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.92f) },
                        CenterPoint = new PointF(w / 2f, h * 0.4f),
                    };
                    g.FillPath(br, p);
                }
                ModernSkin.Stroke(g, p, ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.5f));
            }
            using (GraphicsPath o = ModernSkin.RoundRect(RectangleF.Inflate(r, 1, 1), 15 * k)) ModernSkin.Stroke(g, o, Color.Black);
            string title = W.T(_s.Type == 0 ? ClientTextKeys.ModernGuildRollDie : ClientTextKeys.ModernGuildRollYut);
            ModernSkin.SpacedText(g, title, ModernSkin.Font(13, true), ModernTheme.Accent, new RectangleF(0, 14 * k, w, 18 * k), 3f * k);
            string res = !_s.Rolled ? string.Empty : W.T(_s.Type == 0 ? ClientTextKeys.ModernGuildRollYouRolled : ClientTextKeys.ModernGuildRollResult, _s.Result);
            ModernSkin.Text(g, res, ModernSkin.Font(18, true), Color.White, new RectangleF(0, h - 54 * k, w, 26 * k), StringAlignment.Center);
            string hint = W.T(_s.Rolling ? ClientTextKeys.ModernGuildRolling : _s.Rolled ? ClientTextKeys.ModernGuildRollClose : ClientTextKeys.ModernGuildRollClick);
            ModernSkin.Text(g, hint, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(0, h - 26 * k, w, 16 * k), StringAlignment.Center);
        }

        /// <summary>The stage picture (render time): Setup / Roll / result images of the classic RollDialog.</summary>
        private void DrawStage(PaintBox b)
        {
            if (_s == null) return;
            MLibrary lib;
            int index;
            if (_s.Type == 0)
            {
                lib = Libraries.Prguse;
                index = _s.Rolling ? 290 + _s.Frame % 4 : _s.Rolled ? 281 + _s.Result : 282;
            }
            else
            {
                lib = Libraries.Items;
                index = _s.Rolling ? 2581 + Math.Min(_s.Frame, 5) : _s.Rolled ? 2587 + _s.Result : 2581;
            }
            if (lib == null || index < 0) return;
            Size s = lib.GetSize(index);
            if (s.IsEmpty) return;
            float k = ModernRoot.K, boxH = 110 * k, scale = Math.Min(1.4f * k, Math.Min(b.Size.Width * 0.8f / s.Width, boxH / s.Height));
            float dw = s.Width * scale, dh = s.Height * scale;
            HudKit.DrawScaled(lib, index, new RectangleF(b.DisplayLocation.X + (b.Size.Width - dw) / 2f, b.DisplayLocation.Y + 40 * k + (boxH - dh) / 2f, dw, dh), Color.White);
        }
    }

    /// <summary>The poison status line (.hstat): "FROZEN · 3.2s" in a dark pill with the poison's colour as its border.</summary>
    internal sealed class PoisonLine : ModernSurface
    {
        private readonly PaintBox _box;
        private string _text = string.Empty;
        private Color _colour = Color.Empty;

        public PoisonLine()
        {
            PassThrough = true;
            Size = new Size(S(220), S(24));
            _box = new PaintBox(Paint) { Parent = this, Location = Point.Empty, Size = Size, NotControl = true };
        }

        internal string Text => _text;

        public void Set(string text, Color colour)
        {
            if (text == _text && colour == _colour) return;
            _text = text;
            _colour = colour;
            _box.Invalidate();
        }

        private void Paint(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(12, true);
            float tw = ModernSkin.Measure(_text, f) + _text.Length * 1f * k + 26 * k, h = b.Size.Height - 2;
            RectangleF r = new RectangleF((b.Size.Width - tw) / 2f, 1, tw, h);
            using (GraphicsPath p = ModernSkin.RoundRect(r, h / 2f))
            {
                using (var br = new SolidBrush(Color.FromArgb(191, 8, 10, 16))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, _colour.IsEmpty ? ModernTheme.Frame : _colour);
            }
            ModernSkin.SpacedText(g, _text, f, Color.White, r, 1f * k);
        }
    }

    /// <summary>The poison screen tints (.hfx): gradients only, painted once per flag change, never taking the mouse.</summary>
    internal sealed class PoisonTint : ModernSurface
    {
        private readonly PaintBox _box;
        private PoisonType _flags;

        public PoisonTint()
        {
            PassThrough = true;
            Visible = false;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            _box = new PaintBox(Paint) { Parent = this, Location = Point.Empty, Size = Size, NotControl = true };
        }

        /// <summary>The CSS alpha, lifted: the widget texture is alpha-blended into the surface's target and that target again over
        /// the scene, so a translucent pixel lands with about alpha squared; the square root restores the web's strength.</summary>
        private static Color Lift(Color c) => Color.FromArgb((int)Math.Round(Math.Sqrt(c.A / 255.0) * 255), c.R, c.G, c.B);

        private const PoisonType Tints = PoisonType.Green | PoisonType.Slow | PoisonType.Blindness | PoisonType.Frozen | PoisonType.Paralysis | PoisonType.LRParalysis | PoisonType.Stun | PoisonType.Dazed;

        public void Repaint(PoisonType flags)
        {
            flags &= Tints;
            bool show = flags != 0;
            if (_box.Size != Size) _box.Size = Size;
            if (Visible != show) Visible = show;
            if (flags == _flags) return;
            _flags = flags;
            if (show) _box.Invalidate();
        }

        private void Paint(Graphics g, PaintBox b)
        {
            int w = b.Size.Width, h = b.Size.Height;
            // radial-gradient(ellipse rx ry at 50% 50%, transparent <clear>, edge): the edge colour everywhere outside the ellipse
            // and a gradient inside it (SourceCopy into a layer, so the clear centre replaces the fill), then the layer over g
            void Ring(float rx, float ry, float clear, Color edge)
            {
                edge = Lift(edge);
                using var layer = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics lg = Graphics.FromImage(layer))
                {
                    using (var fill = new SolidBrush(edge)) lg.FillRectangle(fill, 0, 0, w, h);
                    lg.CompositingMode = CompositingMode.SourceCopy;
                    using var path = new GraphicsPath();
                    path.AddEllipse(w / 2f - w * rx, h / 2f - h * ry, w * rx * 2f, h * ry * 2f);
                    using var br = new PathGradientBrush(path)
                    {
                        CenterColor = Color.FromArgb(0, edge),
                        SurroundColors = new[] { edge },
                        CenterPoint = new PointF(w / 2f, h / 2f),
                        FocusScales = new PointF(clear, clear),
                    };
                    lg.FillPath(br, path);
                }
                g.DrawImageUnscaled(layer, 0, 0);
            }
            if ((_flags & PoisonType.Blindness) != 0) Ring(0.28f, 0.37f, 0.3f, Color.FromArgb(235, 0, 0, 0));
            if ((_flags & PoisonType.Frozen) != 0)
            {
                Ring(0.7f, 0.65f, 0.55f, Color.FromArgb(107, 120, 180, 255));
                Ring(0.5f, 0.5f, 0.82f, Color.FromArgb(90, 170, 215, 255)); // the inset glow along the edges
            }
            if ((_flags & (PoisonType.Paralysis | PoisonType.LRParalysis | PoisonType.Stun)) != 0)
                using (var br = new SolidBrush(Lift(Color.FromArgb(71, 110, 110, 118)))) g.FillRectangle(br, 0, 0, w, h);
            if ((_flags & PoisonType.Dazed) != 0) Ring(0.75f, 0.7f, 0.55f, Color.FromArgb(46, 255, 225, 74));
            if ((_flags & PoisonType.Slow) != 0) Ring(0.75f, 0.7f, 0.6f, Color.FromArgb(77, 140, 70, 220));
            if ((_flags & PoisonType.Green) != 0) Ring(0.75f, 0.7f, 0.62f, Color.FromArgb(56, 40, 200, 90));
        }
    }
}
