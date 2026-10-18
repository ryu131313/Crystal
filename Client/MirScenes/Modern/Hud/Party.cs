// Ported from WebClient/src/ui/modern/hud/party.ts: one frame per other group member down the left of the screen, over
// GameScene.Models.Group (members, leader = Members[0], S.GroupMembersMap, S.SendMemberLocation). The web's per-member
// status packet (S.GroupMemberStatus: class, level, HP % / MP %, buffs) is web-only, so on the desktop a member in view
// shows its PlayerObject (class, level, the S.ObjectHealth % while unexpired) and a member out of view shows empty bars.
// The second line shows the member's map when it is not the user's, else the distance and direction. Hidden while the user
// is not grouped. A click opens the "group" window once one is registered (a later phase). Frames rebuild only when the
// roster changes (Group.Changed); values update twice a second and repaint only what changed.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class PartyFrames : ModernSurface
    {
        public const int FrameW = 186, FrameH = 36, FrameGap = 5, CrownRoom = 7;

        private readonly List<PartyFrame> _frames = new List<PartyFrame>();
        private string _roster = string.Empty;
        private bool _dirty = true;
        private long _nextUpdate;
        private readonly Action _onGroup;
        private readonly Action<bool> _onRelayout;

        public PartyFrames()
        {
            PassThrough = true;
            Visible = false;
            _onGroup = () => _dirty = true;
            _onRelayout = rebuild =>
            {
                if (rebuild)
                {
                    _roster = "\u0000";
                    _dirty = true;
                    CheckShown();
                }
                else Location = new Point(S(12), S(12));
            };
            GameScene.Models?.Group.Changed.On(_onGroup);
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
        }

        public int FrameCount => _frames.Count;
        internal PartyFrame Frame(int i) => i >= 0 && i < _frames.Count ? _frames[i] : null;

        /// <summary>Called per frame by the HUD (also while hidden): rebuilds after a roster change.</summary>
        internal void CheckShown()
        {
            if (!_dirty) return;
            _dirty = false;
            List<string> list = GameScene.Models?.Group.Members;
            string me = GameScene.User?.Name ?? string.Empty;
            bool grouped = list != null && list.Count > 1;
            if (!grouped)
            {
                Visible = false;
                _roster = string.Empty;
                for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
                _frames.Clear();
                return;
            }
            string key = string.Join("\u0001", list);
            if (key != _roster)
            {
                _roster = key;
                for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
                _frames.Clear();
                int n = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == me) continue;
                    var f = new PartyFrame(list[i], i == 0) { Parent = this, Location = new Point(0, S(8 - CrownRoom + n * (FrameH + FrameGap))), Size = new Size(S(FrameW), S(FrameH + CrownRoom)) };
                    f.Click += (o, e) => { WindowManager wm = ModernRoot.Instance?.Wm; if (wm != null && wm.IsRegistered("group")) wm.Open("group"); };
                    _frames.Add(f);
                    n++;
                }
                Size = new Size(S(FrameW), S(8 + Math.Max(1, n) * (FrameH + FrameGap)));
                Location = new Point(S(12), S(12) - S(8));
                TextureValid = false;
            }
            Visible = true;
            _nextUpdate = 0;
            Process();
        }

        public override void Process()
        {
            if (CMain.Time < _nextUpdate) return;
            _nextUpdate = CMain.Time + 500;
            UserObject user = GameScene.User;
            GroupStateView view = default;
            view.MyMap = GameScene.Scene?.MapControl?.Title ?? string.Empty;
            view.Me = user != null ? user.CurrentLocation : Point.Empty;
            view.HasMe = user != null;
            for (int i = 0; i < _frames.Count; i++) _frames[i].Update(ref view);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GameScene.Models?.Group.Changed.Off(_onGroup);
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            }
            base.Dispose(disposing);
        }
    }

    internal struct GroupStateView
    {
        public string MyMap;
        public Point Me;
        public bool HasMe;
    }

    /// <summary>One member's frame (.pf): class badge (crown for the leader), name, level / place, HP / MP bars.</summary>
    internal sealed class PartyFrame : ModernControl
    {
        private static readonly ClientTextKeys[] Dirs =
        {
            ClientTextKeys.ModernHudDirN, ClientTextKeys.ModernHudDirNE, ClientTextKeys.ModernHudDirE, ClientTextKeys.ModernHudDirSE,
            ClientTextKeys.ModernHudDirS, ClientTextKeys.ModernHudDirSW, ClientTextKeys.ModernHudDirW, ClientTextKeys.ModernHudDirNW,
        };

        public readonly string Name;
        private readonly bool _leader;
        private int _cls = -1, _level, _hp = -1, _dist = -1, _dir = -1;
        private string _map = string.Empty, _where = string.Empty;
        private bool _far, _dead;

        public PartyFrame(string name, bool leader)
        {
            Name = name ?? string.Empty;
            _leader = leader;
        }

        public string Where => _where;
        public int HealthPercent => _hp;

        private PlayerObject InView()
        {
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is PlayerObject p && p.Race == ObjectType.Player && p.Name == Name) return p;
            return null;
        }

        public void Update(ref GroupStateView v)
        {
            PlayerObject ob = InView();
            int cls = ob != null ? (int)ob.Class : -1;
            int level = ob?.Level ?? 0;
            int hp = ob != null && ob.HealthTime > CMain.Time ? ob.PercentHealth : -1;
            bool dead = ob != null && ob.Dead || hp == 0;
            var group = GameScene.Models?.Group;
            string map = null;
            group?.MemberMaps.TryGetValue(Name, out map);
            int dist = -1, dir = -1;
            bool far = !string.IsNullOrEmpty(map) && map != v.MyMap;
            if (!far && group != null && v.HasMe && group.MemberLocations.TryGetValue(Name, out Point loc))
            {
                int dx = loc.X - v.Me.X, dy = loc.Y - v.Me.Y;
                dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (dist > 0) dir = ((int)Math.Round(Math.Atan2(dx, -dy) / (Math.PI / 4)) + 8) % 8;
            }
            map ??= string.Empty;
            if (cls == _cls && level == _level && hp == _hp && dead == _dead && far == _far && map == _map && dist == _dist && dir == _dir) return;
            _cls = cls;
            _level = level;
            _hp = hp;
            _dead = dead;
            _far = far;
            _map = map;
            _dist = dist;
            _dir = dir;
            string lv = level > 0 ? HudKit.T(ClientTextKeys.ModernHudPartyLevel, level) : string.Empty;
            if (far) _where = map;
            else if (dist > 0) _where = (lv.Length > 0 ? lv + " · " : string.Empty) + dist + " " + HudKit.T(Dirs[dir]);
            else _where = lv;
            string clsName = cls >= 0 ? " · " + ((MirClass)cls).ToLocalizedString() + (level > 0 ? " " + level : string.Empty) : string.Empty;
            Hint = Name + clsName + (far ? " · " + map : string.Empty) + "\n" + HudKit.T(ClientTextKeys.ModernHudPartyHint);
            Opacity = dead ? 0.55f : 1f;
            Invalidate();
        }

        /// <summary>The frame without the transparent room above it (the leader's crown).</summary>
        public override bool IsMouseOver(Point p)
        {
            if (!base.IsMouseOver(p)) return false;
            return p.Y - DisplayLocation.Y >= S(PartyFrames.CrownRoom);
        }

        protected override void Paint(Graphics g)
        {
            float k = K, top = PartyFrames.CrownRoom * k;
            g.TranslateTransform(0, top);
            float w = Size.Width, h = Size.Height - top, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath ring = ModernSkin.RoundRect(r, rad + 1)) ModernSkin.Stroke(g, ring, Color.FromArgb(153, 0, 0, 0));
            using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(r, -1, -1), rad);
            Color pb = ModernSkin.Opaque(ModernTheme.PanelB);
            using (var b = new LinearGradientBrush(new RectangleF(0, 0, w, h), HudKit.Sq(pb, 0.85f), HudKit.Sq(pb, 0.4f), LinearGradientMode.Horizontal)) g.FillPath(b, p);
            ModernSkin.Stroke(g, p, Hovered ? ModernTheme.Accent : ModernTheme.Alpha(ModernTheme.Frame, 0.6f));

            // badge
            Color cc = _cls >= 0 ? ModernTheme.Class((MirClass)_cls) : ModernTheme.Text;
            RectangleF bd = new RectangleF(4 * k, (h - 26 * k) / 2f, 26 * k, 26 * k);
            using (var bp = new GraphicsPath())
            {
                bp.AddEllipse(bd);
                using var b = new PathGradientBrush(bp) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { pb },
                    CenterPoint = new PointF(bd.X + bd.Width / 2f, bd.Y + bd.Height * 0.35f) };
                g.FillEllipse(b, bd);
            }
            using (var pen = new Pen(_cls >= 0 ? ModernTheme.Mix(cc, ModernTheme.Frame, 0.6f) : ModernTheme.Frame, 1f)) g.DrawEllipse(pen, bd);
            ModernSkin.Text(g, _cls >= 0 ? HudKit.ClassTag((MirClass)_cls) : "?", ModernSkin.Font(8, true), cc, bd, StringAlignment.Center, StringAlignment.Center, false);
            if (_leader)
            {
                // the crown (M3 8l4 4 5-7 5 7 4-4-2 11H5z at 12 px) over the badge
                float s = 12 * k / 24f, cx = bd.X + bd.Width / 2f - 6 * k, cy = bd.Y - 8 * k;
                PointF Q(float x, float y) => new PointF(cx + x * s, cy + y * s);
                using GraphicsPath crown = HudKit.Polygon(Q(3, 8), Q(7, 12), Q(12, 5), Q(17, 12), Q(21, 8), Q(19, 19), Q(5, 19));
                using (var b = new SolidBrush(Color.FromArgb(255, 255, 215, 90))) g.FillPath(b, crown);
                ModernSkin.Stroke(g, crown, Color.Black);
            }

            float x0 = bd.Right + 7 * k, x1 = w - 7 * k, iw = x1 - x0;
            Font nf = ModernSkin.Font(10, true);
            Font sf = ModernSkin.Font(10, true);
            float whereW = Math.Min(90 * k, _where.Length > 0 ? ModernSkin.Measure(_where, sf) + 2 : 0);
            HudKit.ShadowText(g, Name, nf, Color.White, new RectangleF(x0, 4 * k, iw - whereW - 6 * k, 13 * k), StringAlignment.Near, StringAlignment.Center, 150);
            if (_where.Length > 0)
                ModernSkin.Text(g, _where, sf, _far ? Color.FromArgb(255, 255, 176, 138) : ModernTheme.Dim, new RectangleF(x1 - whereW, 4 * k, whereW, 13 * k), StringAlignment.Far);
            float hpF = _hp < 0 ? 0 : _hp / 100f;
            Bar(g, x0, 19 * k, iw, 5 * k, hpF, Color.FromArgb(255, 106, 12, 20), Color.FromArgb(255, 208, 52, 62));
            Bar(g, x0, 26 * k, iw, 5 * k, 0, Color.FromArgb(255, 18, 48, 110), Color.FromArgb(255, 58, 126, 224));
            if (_hp >= 0 && _hp < 30 && !_dead)
                using (GraphicsPath gl = ModernSkin.RoundRect(new RectangleF(x0 - 1, 19 * k - 1, iw + 2, 5 * k + 2), 3 * k))
                    ModernSkin.Stroke(g, gl, Color.FromArgb(204, 208, 52, 62), 1.5f);
        }

        private static void Bar(Graphics g, float x, float y, float w, float h, float f, Color a, Color b)
        {
            using (GraphicsPath p = ModernSkin.RoundRect(new RectangleF(x, y, w, h), 2))
            using (var bg = new SolidBrush(Color.FromArgb(255, 4, 6, 10)))
                g.FillPath(bg, p);
            if (f <= 0) return;
            RectangleF fr = new RectangleF(x, y, w * f, h);
            using var br = new LinearGradientBrush(new RectangleF(x - 0.5f, y, w + 1, h), a, b, LinearGradientMode.Horizontal);
            using GraphicsPath fp = ModernSkin.RoundRect(fr, 2);
            g.FillPath(br, fp);
        }
    }
}
