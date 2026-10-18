// Ported from WebClient/src/ui/modern/hud/minimap.ts (MinimapHud): the new UI's minimap, top right — the desktop's
// MiniMapDialog (MirScenes/Dialogs/MainDialogs.cs) in a themed round or square frame.
//   Picture  MMap.Lib[MapControl.MiniMap] around the user (the map's size from the loaded MapControl); a map without a minimap
//            switches to the small mode (the banner only), like DrawMiniMap.
//   Banner   map title + the time-of-day icon (GameScene.Lights; its name as the hint). Under the disc: the user's cell.
//   Marks    NPCs and exits from the map's ClientMapInfo (BigMapData: C.RequestMapInfo; exits beyond the edge as arrows on the
//            rim), the NPC quest "!" / "?" markers (NPCObject.QuestIcon, recomputed on quest changes and every 2 s), the objects
//            in view (players, monsters, NPCs and guards; items, the dead, spells and sneaking objects skipped like DrawMiniMap;
//            the user's pets and group members in the accent colour), group members on this map as diamonds
//            (GroupState.MemberLocations), the user as the centre arrow pointing the way the user faces (turns eased over 180 ms).
//   Smooth   the view centres on the user's drawn position (Movement - OffSetMove / cell), objects sit at their drawn positions.
//   Buttons  zoom (wheel / - / +), world map (the "bigmap" window), mail (the "mail" window when registered; a badge while
//            GameScene.NewMail), round / square, fold (KeybindOptions.Minimap: Toggle()).
// Hover shows names, a click walks there (MapControl.AutoPath, as the big map does). Desktop idiom: the disc is a nested
// surface (its own render target) so the scaled map picture is clipped to it; the round shape is an alpha mask. The disc's
// marks control composes three layers: the map-info marks (NPC dots, exits) and the vignette + "N" are cached GDI bitmaps
// (re-rendered only when the view centre / zoom / map data, resp. the shape / size / scale change) blitted under and over the
// live marks (objects, quest markers, group members, your arrow), which are the only thing painted per repaint. (Cached bitmaps
// rather than separate textures: GDI+ composites the vignette over the marks gamma-corrected, the sprite blend does not, so
// separate layers would dim the marks under the vignette.) Redrawn while anything inside the disc moves at most 15 times a
// second; else only when something changed (a cheap signature of the objects in the disc is checked at 10 Hz); never while
// folded. Labels, colours and the marker font are cached; hover labels are built only for the mark under the mouse.
// Zoom / shape / fold are kept for the session (statics).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;

namespace Client.MirScenes.Modern.Hud
{
    public sealed class MinimapHud : ModernSurface
    {
        private static readonly int[] Zooms = { 14, 24, 40, 64 }; // cells from the centre to the edge
        private const int DiscSize = 176, SlowMs = 100, TurnMs = 180, MoveMs = 66; // live repaints at most ~15 Hz

        private static int _zoom = 1;
        private static bool _shut, _square;

        static MinimapHud()
        {
            int?[] v = HudPrefs.Ints(Settings.UiMinimap, 3);
            _zoom = Math.Clamp(v[0] ?? _zoom, 0, Zooms.Length - 1);
            _square = (v[1] ?? 0) != 0;
            _shut = (v[2] ?? 0) != 0;
        }

        /// <summary>Settings.UiMinimap "zoom,square,shut".</summary>
        private static void SavePrefs() => Settings.UiMinimap = HudPrefs.Join(_zoom, _square ? 1 : 0, _shut ? 1 : 0);

        private readonly BigMapData _data;

        // state
        private int _mapIndex = int.MinValue, _miniIndex = -1;
        private string _title;
        private bool _small;
        private long _slowDue, _nextDraw, _markersDue;
        private bool _dirty, _staticDirty;
        private long _sig;
        private int _lastLight = -1;
        private Point _lastCell = new Point(int.MinValue, 0);
        internal float Cx = float.NaN, Cy = float.NaN, Angle;
        private int _arrowDir = -1;
        private float _arrowFrom, _arrowTo;
        private long _arrowStart;
        internal Dictionary<uint, (string Text, Color Colour)> Markers = new Dictionary<uint, (string, Color)>();
        private Dictionary<uint, (string Text, Color Colour)> _markersNext = new Dictionary<uint, (string, Color)>();
        private readonly Action _onGroup;

        // children
        private MinimapBanner _banner;
        private MinimapRing _ring;
        internal MinimapDisc Disc;
        private MinimapLoc _loc;
        private ChatIconButton _zoomOut, _zoomIn, _bigMap, _mail, _shape, _fold;

        public MinimapHud(BigMapData data)
        {
            _data = data;
            PassThrough = true;
            Parent = ModernRoot.Instance?.Hud;
            _onGroup = () => _dirty = true; // members joined / left / moved: their colours and diamonds
            Build();
            GameScene.Models?.Group.Changed.On(_onGroup);
            ModernEvents.ObjectPositions.On(OnObjects);
            ModernEvents.Quests.On(OnQuests);
            if (_data != null) _data.Changed += OnMapData;
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += OnRelayout;
        }

        public static int Radius => Zooms[Math.Clamp(_zoom, 0, Zooms.Length - 1)];
        public static bool Square => _square;
        public bool Shut => _shut;
        internal bool SmallMode => _small;
        internal BigMapData Data => _data;

        private static string T(ClientTextKeys k) => GameLanguage.ClientTextMap.GetLocalization(k);

        private void OnObjects() => _dirty = true;
        private void OnQuests() => _markersDue = 0;

        private void OnMapData(int index)
        {
            if (index == _mapIndex) { _dirty = true; _staticDirty = true; _markersDue = 0; }
        }

        private void OnRelayout(bool rebuild)
        {
            if (IsDisposed) return;
            if (rebuild) Build();
            else Place();
        }

        // ------------------------------------------------------------------ build

        private void Build()
        {
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            Size = new Size(S(DiscSize + 24), S(240));

            Disc = new MinimapDisc(this) { Parent = this, Size = new Size(S(DiscSize), S(DiscSize)) };
            _ring = new MinimapRing(this) { Parent = this };
            _banner = new MinimapBanner(this) { Parent = this };
            _loc = new MinimapLoc { Parent = this };

            _zoomOut = Button(ModernIcons.Minus, ClientTextKeys.ModernChatMapZoomOut, 11, () => SetZoom(_zoom + 1));
            _zoomIn = Button(ModernIcons.Plus, ClientTextKeys.ModernChatMapZoomIn, 11, () => SetZoom(_zoom - 1));
            _bigMap = Button(ModernIcons.Map, ClientTextKeys.ModernChatMapWorld, 12, () => ModernRoot.Instance?.Wm.Toggle(BigMapWindow.WindowKey));
            _mail = Button(ModernIcons.Mail, ClientTextKeys.Mail, 12, () => ModernRoot.Instance?.Wm.Toggle("mail"));
            _shape = Button(ModernIcons.Square, ClientTextKeys.ModernChatMapShape, 12, () =>
            {
                _square = !_square;
                SavePrefs();
                Disc.ShapeChanged();
                _ring.Invalidate();
            });
            _fold = Button(ModernIcons.ChevronDown, ClientTextKeys.ModernChatMapFold, 12, Toggle);
            _bigMap.Hint = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapWorld) + KeyHint(KeybindOptions.Bigmap);
            _fold.Hint = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapFold) + KeyHint(KeybindOptions.Minimap);

            _title = null;
            _lastLight = -1;
            _lastCell = new Point(int.MinValue, 0);
            _mapIndex = int.MinValue;
            Slow();
            Arrange();
            Place();
        }

        private static string KeyHint(KeybindOptions k)
        {
            string key = CMain.InputKeys.GetKey(k);
            return string.IsNullOrEmpty(key) ? string.Empty : " (" + key + ")";
        }

        private ChatIconButton Button(int icon, ClientTextKeys hint, int iconHud, Action act)
        {
            var b = new ChatIconButton(icon, 22, 22, iconHud, true) { Parent = this, Hint = T(hint) };
            b.Click += (o, e) => act();
            return b;
        }

        private void Place()
        {
            Location = new Point(Settings.ScreenWidth - S(12) - Size.Width, S(10));
        }

        /// <summary>Lays the pieces out for the mode (minimap.ts CSS: .ban over the disc top, .loc over its bottom, the buttons
        /// below; .shut / .small hide the disc and some buttons).</summary>
        private void Arrange()
        {
            bool noDisc = _shut || _small;
            int w = Size.Width, y = 0;
            _banner.Location = new Point((w - _banner.Size.Width) / 2, 0);
            y = _banner.Size.Height;
            if (noDisc)
            {
                if (Disc.Visible) Disc.Visible = false;
                if (_ring.Visible) _ring.Visible = false;
                y += S(4);
                _loc.Round = true;
                _loc.Location = new Point((w - _loc.Size.Width) / 2, y);
            }
            else
            {
                y -= S(10);
                Disc.Location = new Point((w - Disc.Size.Width) / 2, y);
                if (!Disc.Visible) Disc.Visible = true;
                _ring.Location = new Point(Disc.Location.X - S(6), Disc.Location.Y - S(6));
                _ring.Size = new Size(Disc.Size.Width + S(12), Disc.Size.Height + S(12));
                if (!_ring.Visible) _ring.Visible = true;
                y += Disc.Size.Height - S(16);
                _loc.Round = false;
                _loc.Location = new Point((w - _loc.Size.Width) / 2, y);
            }
            y = _loc.Location.Y + _loc.Size.Height + S(6);

            ChatIconButton[] all = { _zoomOut, _zoomIn, _bigMap, _mail, _shape, _fold };
            WindowManager wm = ModernRoot.Instance?.Wm;
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                ChatIconButton b = all[i];
                bool keep = b == _mail || b == _fold;
                bool show = (!_shut || keep) && !(_small && (b == _zoomIn || b == _zoomOut));
                if (b == _bigMap && (wm == null || !wm.IsRegistered(BigMapWindow.WindowKey))) show = false;
                if (b == _mail && (wm == null || !wm.IsRegistered("mail"))) show = false;
                if (b.Visible != show) b.Visible = show;
                if (show) count++;
            }
            int bw = S(22), gap = S(5), x = (w - (count * bw + Math.Max(0, count - 1) * gap)) / 2;
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].Visible) continue;
                all[i].Location = new Point(x, y);
                x += bw + gap;
            }
            _fold.Icon = _shut ? ModernIcons.ChevronUp : ModernIcons.ChevronDown;
        }

        /// <summary>KeybindOptions.Minimap / the fold button: folds the minimap to its banner (MiniMapDialog.Toggle).</summary>
        public void Toggle()
        {
            _shut = !_shut;
            SavePrefs();
            Disc.HideTip();
            Arrange();
            _slowDue = 0;
            _dirty = true;
            _staticDirty = true;
        }

        private void SetZoom(int z)
        {
            int next = Math.Clamp(z, 0, Zooms.Length - 1);
            if (next == _zoom) return;
            _zoom = next;
            SavePrefs();
            _dirty = true;
            _staticDirty = true;
            _nextDraw = 0;
        }

        internal void Wheel(int delta) => SetZoom(_zoom + (delta < 0 ? 1 : -1));

        // ------------------------------------------------------------------ per frame

        /// <summary>The cell an object is drawn at, fractional while it walks (minimap.ts drawnX / drawnY).</summary>
        internal static float DrawnX(MapObject ob) => ob.Movement.X - ob.OffSetMove.X / (float)MapControl.CellWidth;
        internal static float DrawnY(MapObject ob) => ob.Movement.Y - ob.OffSetMove.Y / (float)MapControl.CellHeight;

        /// <summary>The banner, the cell and the light at 10 Hz (the slow half of minimap.ts tick).</summary>
        private void Slow()
        {
            MapControl map = GameScene.Scene?.MapControl;
            UserObject user = GameScene.User;
            if (map == null) return;

            if (map.Index != _mapIndex || map.MiniMap != _miniIndex || map.Title != _title)
            {
                _mapIndex = map.Index;
                _miniIndex = map.MiniMap;
                _title = map.Title;
                _banner.SetTitle(ChatGfx.Pretty(map.Title));
                bool small = map.MiniMap <= 0;
                if (small != _small)
                {
                    _small = small;
                    Arrange();
                }
                _banner.Location = new Point((Size.Width - _banner.Size.Width) / 2, 0);
                _data?.RequestMap(map.Index);
                _markersDue = 0;
                _dirty = true;
                _staticDirty = true;
                Cx = float.NaN;
            }

            if (user != null && user.CurrentLocation != _lastCell)
            {
                _lastCell = user.CurrentLocation;
                _loc.Text = user.CurrentLocation.X + ", " + user.CurrentLocation.Y;
                _loc.Location = new Point((Size.Width - _loc.Size.Width) / 2, _loc.Location.Y);
            }

            int light = (int)(GameScene.Scene?.Lights ?? LightSetting.Day);
            if (light != _lastLight)
            {
                _lastLight = light;
                _banner.SetLight((LightSetting)light);
            }

            bool newMail = GameScene.Scene != null && GameScene.Scene.NewMail;
            _mail.Badge = newMail;
            string mailHint = T(newMail ? ClientTextKeys.ModernChatMapMailNew : ClientTextKeys.Mail);
            if (_mail.Hint != mailHint) _mail.Hint = mailHint;

            // window buttons follow the registry (a later phase registers "mail")
            WindowManager wm = ModernRoot.Instance?.Wm;
            bool bigOk = wm != null && wm.IsRegistered(BigMapWindow.WindowKey), mailOk = wm != null && wm.IsRegistered("mail");
            if (_bigMap.Visible != (bigOk && !_shut) || _mail.Visible != mailOk) Arrange();
        }

        public override void Process()
        {
            long now = CMain.Time;
            bool slow = now >= _slowDue;
            if (slow)
            {
                _slowDue = now + SlowMs;
                Slow(); // a map change marks the disc dirty itself
            }
            if (_shut || _small) return;
            UserObject user = GameScene.User;
            if (user == null || GameScene.Scene?.MapControl == null) return;

            if (now >= _markersDue)
            {
                _markersDue = now + 2000; // NPCs coming into view get their markers within 2 s
                if (ComputeMarkers()) _dirty = true;
            }

            float cx = DrawnX(user), cy = DrawnY(user), angle = ArrowAngle((int)user.Direction, now);
            bool centre = cx != Cx || cy != Cy || _staticDirty;
            bool moving = centre || angle != Angle || _dirty;
            if (!moving) moving = AnyWalkingInDisc(cx, cy);
            // spawns, removals, deaths and the like without a position event: a cheap signature of the disc's objects at 10 Hz
            if (!moving && slow) moving = Signature(cx, cy) != _sig;
            if (!moving) return;
            if (now < _nextDraw) return;
            _nextDraw = now + MoveMs;
            _dirty = false;
            _staticDirty = false;
            Cx = cx;
            Cy = cy;
            Angle = angle;
            _sig = Signature(cx, cy);
            Disc.Repaint(centre);
        }

        private static bool InDisc(MapObject ob, float cx, float cy, float r)
        {
            float dx = DrawnX(ob) - cx, dy = DrawnY(ob) - cy;
            return dx >= -r && dx <= r && dy >= -r && dy <= r;
        }

        /// <summary>Whether an object inside the disc (a cell of margin) is between cells (walking / running).</summary>
        private static bool AnyWalkingInDisc(float cx, float cy)
        {
            float r = Radius + 1;
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                MapObject ob = list[i];
                if (ob == null || (ob.OffSetMove.X == 0 && ob.OffSetMove.Y == 0)) continue;
                if (InDisc(ob, cx, cy, r)) return true;
            }
            return false;
        }

        /// <summary>The objects the live layer would draw inside the disc (id, cell, kind), mixed into one number.</summary>
        private static long Signature(float cx, float cy)
        {
            float r = Radius + 1;
            long h = 17;
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                MapObject ob = list[i];
                if (ob == null || ob == MapObject.User || ob.Race == ObjectType.Item || ob.Dead || ob.Race == ObjectType.Spell || ob.Sneaking) continue;
                if (!InDisc(ob, cx, cy, r)) continue;
                h = h * 31 + ob.ObjectID;
                h = h * 31 + ob.CurrentLocation.X;
                h = h * 31 + ob.CurrentLocation.Y;
                h = h * 31 + (int)ob.Race;
            }
            return h;
        }

        /// <summary>The centre arrow's angle for a MirDirection (Up = 0, clockwise by 45°): a new facing turns the short way round
        /// over 180 ms, smoothstep (minimap.ts arrowAngle).</summary>
        private float ArrowAngle(int dir, long now)
        {
            float target = dir * MathF.PI / 4f;
            if (_arrowDir < 0)
            {
                _arrowDir = dir;
                _arrowFrom = _arrowTo = target;
                _arrowStart = now;
                return target;
            }
            float t = Math.Clamp((now - _arrowStart) / (float)TurnMs, 0f, 1f);
            float cur = _arrowFrom + (_arrowTo - _arrowFrom) * t * t * (3 - 2 * t);
            if (dir != _arrowDir)
            {
                float d = (target - cur) % (MathF.PI * 2);
                if (d > MathF.PI) d -= MathF.PI * 2;
                else if (d < -MathF.PI) d += MathF.PI * 2;
                _arrowDir = dir;
                _arrowFrom = cur;
                _arrowTo = cur + d;
                _arrowStart = now;
                return cur;
            }
            if (t >= 1f) cur = _arrowFrom = _arrowTo = target;
            return cur;
        }

        /// <summary>NPC ObjectID → quest marker (MiniMapDialog's "!" / "?" by NPCObject.QuestIcon); true when they changed.</summary>
        private bool ComputeMarkers()
        {
            Dictionary<uint, (string Text, Color Colour)> next = _markersNext;
            next.Clear();
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not NPCObject npc || npc.QuestIcon == QuestIcon.None) continue;
                (string, Color) m = npc.QuestIcon switch
                {
                    QuestIcon.ExclamationBlue => ("!", Color.DodgerBlue),
                    QuestIcon.ExclamationYellow => ("!", Color.Yellow),
                    QuestIcon.ExclamationGreen => ("!", Color.Green),
                    QuestIcon.QuestionBlue => ("?", Color.DodgerBlue),
                    QuestIcon.QuestionWhite => ("?", Color.White),
                    QuestIcon.QuestionYellow => ("?", Color.Yellow),
                    QuestIcon.QuestionGreen => ("?", Color.Green),
                    _ => (null, Color.Empty),
                };
                if (m.Item1 != null) next[npc.ObjectID] = m;
            }
            bool same = next.Count == Markers.Count;
            if (same)
                foreach (KeyValuePair<uint, (string Text, Color Colour)> kv in next)
                    if (!Markers.TryGetValue(kv.Key, out (string Text, Color Colour) old) || old.Text != kv.Value.Text || old.Colour != kv.Value.Colour) { same = false; break; }
            if (same) return false;
            _markersNext = Markers;
            Markers = next;
            return true;
        }

        /// <summary>The cell under a disc point (device px inside the disc), or false.</summary>
        internal bool CellAt(float px, float py, out Point cell)
        {
            cell = Point.Empty;
            MapControl map = GameScene.Scene?.MapControl;
            UserObject user = GameScene.User;
            if (user == null || map == null || map.Width <= 0 || _small) return false;
            float r = Radius, size = Disc.Size.Width;
            float cx = float.IsNaN(Cx) ? user.CurrentLocation.X : Cx, cy = float.IsNaN(Cy) ? user.CurrentLocation.Y : Cy;
            int x = (int)Math.Round(cx + (px / size * 2 - 1) * r), y = (int)Math.Round(cy + (py / size * 2 - 1) * r);
            if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) return false;
            cell = new Point(x, y);
            return true;
        }

        /// <summary>A click on the disc walks there (MapControl.AutoPath, as BigMapViewPort.OnMouseClick does).</summary>
        internal static void WalkTo(Point cell)
        {
            MapControl map = GameScene.Scene?.MapControl;
            UserObject user = GameScene.User;
            if (map?.PathFinder == null || user == null) return;
            List<Node> path = map.PathFinder.FindPath(user.CurrentLocation, cell);
            if (path == null || path.Count == 0)
            {
                GameScene.Models?.Chat.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CouldNotFindPath), ChatType.System);
                return;
            }
            map.CurrentPath = path;
            map.AutoPath = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GameScene.Models?.Group.Changed.Off(_onGroup);
                ModernEvents.ObjectPositions.Off(OnObjects);
                ModernEvents.Quests.Off(OnQuests);
                if (_data != null) _data.Changed -= OnMapData;
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= OnRelayout;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The map title pill with the time-of-day icon (minimap.ts .ban).</summary>
    internal sealed class MinimapBanner : ModernControl
    {
        private readonly MinimapHud _hud;
        private readonly ChatIconView _icon;
        private string _title = string.Empty;

        public MinimapBanner(MinimapHud hud)
        {
            _hud = hud;
            _icon = new ChatIconView(ModernIcons.Sun) { Parent = this, Size = new Size(S(13), S(13)), Colour = ModernTheme.AccentDim };
            Size = new Size(S(60), S(25));
        }

        public void SetTitle(string title)
        {
            _title = title ?? string.Empty;
            float tw = ChatGfx.Measure(_title, ModernSkin.Font(12, true)) * 1.04f;
            int w = (int)Math.Ceiling(Math.Min(S(200) - S(2), tw + S(24) + S(6) + S(13)));
            Size = new Size(w, S(25));
            _icon.Location = new Point(Size.Width - S(12) - _icon.Size.Width, (Size.Height - _icon.Size.Height) / 2);
            Invalidate();
        }

        public void SetLight(LightSetting light)
        {
            _icon.Icon = light == LightSetting.Night ? ModernIcons.Moon : light == LightSetting.Dawn || light == LightSetting.Evening ? ModernIcons.Dusk : ModernIcons.Sun;
            Hint = GameLanguage.ClientTextMap.GetLocalization(light switch
            {
                LightSetting.Dawn => ClientTextKeys.ModernChatMapDawn,
                LightSetting.Evening => ClientTextKeys.ModernChatMapEvening,
                LightSetting.Night => ClientTextKeys.ModernChatMapNight,
                _ => ClientTextKeys.ModernChatMapDay,
            });
        }

        protected override void Paint(Graphics g)
        {
            RectangleF r = new RectangleF(1.5f, 1.5f, Size.Width - 3f, Size.Height - 3f);
            ChatGfx.Plate(g, r, S(12));
            Font f = ModernSkin.Font(12, true);
            float textRight = _icon.Location.X - S(6);
            ModernSkin.SpacedText(g, _title, f, ModernTheme.Accent, new RectangleF(S(12), 0, textRight - S(12), Size.Height - S(1)), 12 * 0.04f * K);
        }
    }

    /// <summary>The cell pill under the disc (minimap.ts .loc).</summary>
    internal sealed class MinimapLoc : ModernControl
    {
        private string _text = string.Empty;
        private bool _round;

        public MinimapLoc()
        {
            NotControl = true;
            Size = new Size(S(60), S(20));
        }

        public bool Round { get => _round; set { if (_round == value) return; _round = value; Invalidate(); } }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value ?? string.Empty;
                Size = new Size((int)Math.Ceiling(ChatGfx.Measure(_text, ModernSkin.Font(10))) + S(24), S(20));
                Invalidate();
            }
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(1.5f, 1.5f, Size.Width - 3f, Size.Height - 3f);
            if (_round) ChatGfx.Plate(g, r, S(10));
            else
            {
                // border-radius: 10px 10px 4px 4px
                using (GraphicsPath outer = TopRound(RectangleF.Inflate(r, 1f, 1f), 11 * k, 5 * k)) ModernSkin.Stroke(g, outer, Color.Black);
                using GraphicsPath p = TopRound(r, 10 * k, 4 * k);
                ModernSkin.FillVertical(g, p, r, new[] { ModernSkin.Opaque(ModernTheme.PanelA), ModernSkin.Opaque(ModernTheme.PanelB) });
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            ModernSkin.Text(g, _text, ModernSkin.Font(10), ModernTheme.Dim, r, StringAlignment.Center);
        }

        private static GraphicsPath TopRound(RectangleF r, float top, float bottom)
        {
            var path = new GraphicsPath();
            float t = Math.Min(top * 2, Math.Min(r.Width, r.Height)), b = Math.Min(bottom * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, t, t, 180, 90);
            path.AddArc(r.Right - t, r.Y, t, t, 270, 90);
            path.AddArc(r.Right - b, r.Bottom - b, b, b, 0, 90);
            path.AddArc(r.X, r.Bottom - b, b, b, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>The disc's frame rings (minimap.ts .disc box-shadow: 2 px black, 4 px frame, 5 px black).</summary>
    internal sealed class MinimapRing : ModernControl
    {
        private readonly MinimapHud _hud;

        public MinimapRing(MinimapHud hud)
        {
            _hud = hud;
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, inset = S(6);
            RectangleF disc = new RectangleF(inset, inset, Size.Width - inset * 2, Size.Height - inset * 2);
            float rad = MinimapHud.Square ? ModernTheme.Radius * k : disc.Width / 2f;
            void Ring(float grow, float width, Color c)
            {
                RectangleF rr = RectangleF.Inflate(disc, grow - width / 2f, grow - width / 2f);
                using GraphicsPath p = ModernSkin.RoundRect(rr, MinimapHud.Square ? rad + grow : rr.Width / 2f);
                ModernSkin.Stroke(g, p, c, width);
            }
            Ring(5f * k, 1.2f * k, Color.Black);
            Ring(4f * k, 2.2f * k, ModernTheme.Frame);
            Ring(2f * k, 2f * k, Color.Black);
        }
    }

    /// <summary>
    /// The disc: a nested surface (its own render target, so the scaled picture is clipped to it) holding the dark fill, the
    /// MMap picture (sprite-drawn), the marks (GDI), the hover tip and the round / square alpha mask. Mouse: hover names, wheel
    /// zoom, click walk.
    /// </summary>
    internal sealed class MinimapDisc : ModernSurface
    {
        private readonly MinimapHud _hud;
        private readonly MinimapPicture _picture;
        private readonly MinimapMarks _marks;
        private readonly MinimapTip _tip;
        private ChatAlphaMask _mask;
        private readonly ChatAlphaFix _fix;
        private bool _hoverOn;
        private int _tipKind = -1, _tipIndex = -1, _tipDist = int.MinValue;
        private uint _tipId;
        private string _tipLabel;
        private Point _tipCell = new Point(int.MinValue, 0);

        public MinimapDisc(MinimapHud hud)
        {
            _hud = hud;
            PassThrough = false;
            new MinimapFill { Parent = this };
            _picture = new MinimapPicture(hud) { Parent = this };
            _marks = new MinimapMarks(hud) { Parent = this };
            _tip = new MinimapTip { Parent = this, Visible = false };
            _fix = new ChatAlphaFix { Parent = this };
            _mask = new ChatAlphaMask(MinimapHud.Square ? ChatAlphaMask.Rounded : ChatAlphaMask.Ellipse) { Parent = this };
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();
            for (int i = 0; i < Controls.Count; i++)
                if (Controls[i] != _tip) { Controls[i].Location = Point.Empty; Controls[i].Size = Size; }
        }

        public void ShapeChanged()
        {
            _mask?.Dispose();
            _mask = new ChatAlphaMask(MinimapHud.Square ? ChatAlphaMask.Rounded : ChatAlphaMask.Ellipse) { Parent = this, Location = Point.Empty, Size = Size };
            _marks.VignetteDirty = true;
            Repaint(true);
        }

        /// <summary>A change inside also re-renders the minimap surface around this one.</summary>
        public override void Redraw()
        {
            TextureValid = false;
            Parent?.Redraw();
        }

        /// <summary>Repaints the live marks; <paramref name="info"/>: the map-info marks too (the view centre / zoom / data moved).</summary>
        public void Repaint(bool info)
        {
            if (info) _marks.Info.Dirty = true;
            _marks.Invalidate();
            Redraw();
            if (_hoverOn) Hover();
        }

        public override bool IsMouseOver(Point p)
        {
            if (!base.IsMouseOver(p)) return false;
            if (MinimapHud.Square) return true;
            float r = Size.Width / 2f, dx = p.X - DisplayLocation.X - r, dy = p.Y - DisplayLocation.Y - r;
            return dx * dx + dy * dy <= r * r;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hoverOn = true;
            Hover();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            HideTip();
        }

        public override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _hud.Wheel(e.Delta);
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_hud.CellAt(CMain.MPoint.X - DisplayLocation.X, CMain.MPoint.Y - DisplayLocation.Y, out Point cell)) MinimapHud.WalkTo(cell);
        }

        public void HideTip()
        {
            _hoverOn = false;
            _tipKind = -1;
            _tipCell = new Point(int.MinValue, 0);
            if (_tip.Visible) _tip.Visible = false;
        }

        /// <summary>The nearest mark within 9 HUD px, else the cell (minimap.ts hover). The label is built only when the mark (or
        /// cell) under the mouse changed.</summary>
        private void Hover()
        {
            float mx = CMain.MPoint.X - DisplayLocation.X, my = CMain.MPoint.Y - DisplayLocation.Y;
            if (mx < 0 || my < 0 || mx >= Size.Width || my >= Size.Height) { HideTip(); return; }
            float best = S(9);
            List<MinimapMark> bestList = null;
            int bestAt = -1;
            Nearest(_marks.Info.Marks, mx, my, ref best, ref bestList, ref bestAt);
            Nearest(_marks.Marks, mx, my, ref best, ref bestList, ref bestAt);
            float bx = mx, by = my;
            if (bestList == null)
            {
                if (!_hud.CellAt(mx, my, out Point cell)) { _tipKind = -1; if (_tip.Visible) _tip.Visible = false; return; }
                if (_tipKind != 99 || cell != _tipCell)
                {
                    _tipKind = 99;
                    _tipCell = cell;
                    _tip.Text = cell.X + ", " + cell.Y;
                }
            }
            else
            {
                MinimapMark m = bestList[bestAt];
                bx = m.X;
                by = m.Y;
                if (m.Kind != _tipKind || m.Index != _tipIndex || m.Id != _tipId || m.Dist != _tipDist || !ReferenceEquals(m.Label, _tipLabel))
                {
                    _tipLabel = m.Label;
                    _tipKind = m.Kind;
                    _tipIndex = m.Index;
                    _tipId = m.Id;
                    _tipDist = m.Dist;
                    _tipCell = new Point(int.MinValue, 0);
                    _tip.Text = m.Kind == MinimapMark.OffExit ? _marks.Info.OffExitLabel(m.Index, m.Dist)
                        : m.Kind == MinimapMark.ObjectNpc ? _marks.NpcLabel(m.Id)
                        : m.Label ?? string.Empty;
                }
            }
            _tip.Location = new Point((int)(bx - _tip.Size.Width / 2f), (int)(by - _tip.Size.Height * 1.3f));
            if (!_tip.Visible) _tip.Visible = true;
        }

        private static void Nearest(List<MinimapMark> marks, float mx, float my, ref float best, ref List<MinimapMark> bestList, ref int bestAt)
        {
            for (int i = 0; i < marks.Count; i++)
            {
                float dx = marks[i].X - mx, dy = marks[i].Y - my, d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= best) continue;
                best = d;
                bestList = marks;
                bestAt = i;
            }
        }
    }

    internal sealed class MinimapFill : ModernControl
    {
        private static readonly Color Fill = ModernTheme.Hex("#05070b");

        public MinimapFill() { NotControl = true; }

        protected override void Paint(Graphics g)
        {
            using var b = new SolidBrush(Fill);
            g.FillRectangle(b, 0, 0, Size.Width, Size.Height);
        }
    }

    /// <summary>MMap.Lib[MapControl.MiniMap] scaled around the view centre (minimap.ts draw: drawImage of the region).</summary>
    internal sealed class MinimapPicture : MirControl
    {
        private readonly MinimapHud _hud;

        public MinimapPicture(MinimapHud hud)
        {
            _hud = hud;
            NotControl = true;
            BackColour = Color.Transparent;
        }

        protected internal override void DrawControl()
        {
            MapControl map = GameScene.Scene?.MapControl;
            if (map == null || map.MiniMap <= 0 || map.Width <= 0 || map.Height <= 0 || float.IsNaN(_hud.Cx)) return;
            Size img = Libraries.MiniMap.GetSize(map.MiniMap);
            if (img.Width <= 0 || img.Height <= 0) return;
            float r = MinimapHud.Radius, size = Size.Width;
            float cellPx = size / (2 * r);                      // device px per cell on the disc
            int dw = (int)Math.Round(map.Width * cellPx), dh = (int)Math.Round(map.Height * cellPx);
            if (dw <= 0 || dh <= 0) return;
            Point at = DisplayLocation;
            int ox = at.X + (int)Math.Round(-(_hud.Cx - r) * cellPx), oy = at.Y + (int)Math.Round(-(_hud.Cy - r) * cellPx);
            try
            {
                Libraries.MiniMap.Draw(map.MiniMap, new Point(ox, oy), new Size(dw, dh), Color.White);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] minimap picture", ex);
            }
        }
    }

    /// <summary>A hover target on the disc: its label ready, or resolved on hover only (an exit beyond the edge: its distance;
    /// an NPC object: its display name).</summary>
    internal struct MinimapMark
    {
        public const int Ready = 0, OffExit = 1, ObjectNpc = 2;
        public float X, Y;
        public int Kind, Index, Dist;
        public uint Id;
        public string Label;

        public MinimapMark(float x, float y, string label)
        {
            X = x; Y = y; Label = label; Kind = Ready; Index = -1; Dist = 0; Id = 0;
        }
    }

    /// <summary>Colours of the minimap's marks (minimap.ts draw), parsed once.</summary>
    internal static class MinimapColours
    {
        public static readonly Color Npc = ModernTheme.Hex("#ffd75a");
        public static readonly Color Player = ModernTheme.Hex("#9fd8ff");
        public static readonly Color Monster = ModernTheme.Hex("#ff5a4a");
        public static readonly Color Outline = Color.FromArgb(217, 0, 0, 0);
    }

    /// <summary>The map-info layer (minimap.ts draw, first half): the NPC dots and the exits of the map's ClientMapInfo, arrows
    /// on the map or on the rim pointing at the nearest three beyond the edge, rendered into a cached bitmap only when
    /// <see cref="Dirty"/> (the view centre, the zoom or the map data changed); names and exit labels are cached per map data.</summary>
    internal sealed class MinimapInfoLayer : IDisposable
    {
        private readonly MinimapHud _hud;
        public readonly List<MinimapMark> Marks = new List<MinimapMark>();
        public bool Dirty = true;
        private Bitmap _bitmap;

        // per map data: display names, exit titles and their "to …" labels; the beyond-the-edge labels by distance
        private ClientMapInfo _cachedFor;
        private int _cachedNpcs = -1, _cachedExits = -1;
        private string[] _npcNames = Array.Empty<string>(), _exitTitles = Array.Empty<string>(), _exitLabels = Array.Empty<string>();
        private string[] _offLabels = Array.Empty<string>();
        private int[] _offDists = Array.Empty<int>();
        private readonly int[] _nearIdx = new int[3];
        private readonly float[] _nearDist = new float[3];

        public MinimapInfoLayer(MinimapHud hud)
        {
            _hud = hud;
        }

        /// <summary>The layer at this size (re-rendered first when dirty or resized).</summary>
        public Bitmap Get(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return null;
            if (_bitmap == null || _bitmap.Size != size)
            {
                _bitmap?.Dispose();
                _bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Dirty = true;
            }
            if (Dirty)
            {
                Dirty = false;
                using Graphics g = Graphics.FromImage(_bitmap);
                ModernSkin.Prepare(g);
                g.Clear(Color.Transparent);
                try
                {
                    Paint(g, size);
                }
                catch (Exception ex)
                {
                    if (Settings.LogErrors) ModernLog.Once("[ModernUi] minimap info layer", ex);
                }
            }
            return _bitmap;
        }

        public void Dispose()
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }

        private void Cache(ClientMapInfo info)
        {
            if (info == _cachedFor && info.NPCs.Count == _cachedNpcs && info.Movements.Count == _cachedExits) return;
            _cachedFor = info;
            _cachedNpcs = info.NPCs.Count;
            _cachedExits = info.Movements.Count;
            _npcNames = new string[_cachedNpcs];
            for (int i = 0; i < _cachedNpcs; i++) _npcNames[i] = ChatGfx.NpcName(info.NPCs[i].Name).Name;
            _exitTitles = new string[_cachedExits];
            _exitLabels = new string[_cachedExits];
            _offLabels = new string[_cachedExits];
            _offDists = new int[_cachedExits];
            string to = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapToExit);
            for (int i = 0; i < _cachedExits; i++)
            {
                _exitTitles[i] = ChatGfx.Pretty(info.Movements[i].Title);
                _exitLabels[i] = string.Format(to, _exitTitles[i]);
                _offDists[i] = int.MinValue;
            }
        }

        /// <summary>"{title} ({n} cells)" for an exit beyond the edge (built on hover, cached by distance).</summary>
        public string OffExitLabel(int exit, int dist)
        {
            if (exit < 0 || exit >= _offLabels.Length) return string.Empty;
            if (_offDists[exit] != dist || _offLabels[exit] == null)
            {
                _offDists[exit] = dist;
                _offLabels[exit] = string.Format(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapExitCells), _exitTitles[exit], dist);
            }
            return _offLabels[exit];
        }

        private void Paint(Graphics g, Size disc)
        {
            Marks.Clear();
            float k = ModernRoot.K, size = disc.Width, r = MinimapHud.Radius;
            float cx = _hud.Cx, cy = _hud.Cy;
            MapControl map = GameScene.Scene?.MapControl;
            if (GameScene.User == null || map == null || float.IsNaN(cx)) return;
            ClientMapInfo info = _hud.Data?.Map(map.Index);
            if (info == null) return;
            Cache(info);
            Color accent = ModernTheme.Accent;
            float half = size / 2f;
            PointF ToPx(float x, float y) => new PointF(((x - cx) / r + 1) * half, ((y - cy) / r + 1) * half);
            bool Inside(PointF p) => p.X >= -6 && p.Y >= -6 && p.X <= size + 6 && p.Y <= size + 6;
            float sw = Math.Max(1f, k);

            for (int i = 0; i < info.NPCs.Count && i < _npcNames.Length; i++)
            {
                ClientNPCInfo npc = info.NPCs[i];
                PointF p = ToPx(npc.Location.X, npc.Location.Y);
                if (!Inside(p)) continue;
                ChatGfx.Dot(g, p.X, p.Y, 2.5f * k, MinimapColours.Npc, sw);
                Marks.Add(new MinimapMark(p.X, p.Y, _npcNames[i]));
            }
            // exits: arrows on the map, or on the rim pointing at the nearest three beyond the edge
            int near = 0;
            for (int i = 0; i < info.Movements.Count && i < _exitLabels.Length; i++)
            {
                ClientMovementInfo mv = info.Movements[i];
                PointF p = ToPx(mv.Location.X, mv.Location.Y);
                if (p.X >= 0 && p.Y >= 0 && p.X <= size && p.Y <= size)
                {
                    ChatGfx.Arrow(g, p.X, p.Y, 0, accent, 3.5f * k, MinimapColours.Outline, sw);
                    Marks.Add(new MinimapMark(p.X, p.Y, _exitLabels[i]));
                    continue;
                }
                float dx = mv.Location.X - cx, dy = mv.Location.Y - cy, dist = MathF.Sqrt(dx * dx + dy * dy);
                // keep the nearest three, nearest first (insertion into a fixed array: no sort, no allocation)
                int at = near;
                while (at > 0 && dist < _nearDist[at - 1]) at--;
                if (at >= 3) continue;
                for (int j = Math.Min(near, 2); j > at; j--)
                {
                    _nearDist[j] = _nearDist[j - 1];
                    _nearIdx[j] = _nearIdx[j - 1];
                }
                _nearDist[at] = dist;
                _nearIdx[at] = i;
                if (near < 3) near++;
            }
            for (int n = 0; n < near; n++)
            {
                ClientMovementInfo mv = info.Movements[_nearIdx[n]];
                float ang = MathF.Atan2(mv.Location.Y - cy, mv.Location.X - cx);
                float rr = half - 6 * k, px = half + MathF.Cos(ang) * rr, py = half + MathF.Sin(ang) * rr;
                ChatGfx.Arrow(g, px, py, ang + MathF.PI / 2, accent, 4.5f * k, MinimapColours.Outline, sw);
                Marks.Add(new MinimapMark(px, py, null) { Kind = MinimapMark.OffExit, Index = _nearIdx[n], Dist = (int)Math.Round(_nearDist[n]) });
            }
        }
    }

    /// <summary>The disc's marks (minimap.ts draw; canvas units halved: the web draws a 2x canvas): the cached map-info layer,
    /// then the live marks painted per repaint (the objects in view, the NPC quest markers, the group members, your arrow), then
    /// the cached vignette + "N". The marker font, the pet suffix and the NPC labels are cached.</summary>
    internal sealed class MinimapMarks : ModernControl
    {
        private readonly MinimapHud _hud;
        public readonly MinimapInfoLayer Info;
        public readonly List<MinimapMark> Marks = new List<MinimapMark>();
        public bool VignetteDirty = true;
        private Bitmap _vignette;
        private float _vignetteK;
        private Color _vignetteAccent;
        private Font _markerFont;
        private float _markerK = -1f, _wBang, _wQuery;
        private string _petSuffix = string.Empty, _petFor;
        private readonly Dictionary<string, string> _npcNames = new Dictionary<string, string>();

        public MinimapMarks(MinimapHud hud)
        {
            _hud = hud;
            Info = new MinimapInfoLayer(hud);
            NotControl = true;
        }

        /// <summary>Blits a cached layer 1:1 (SourceCopy for the first one into the cleared bitmap: an exact copy).</summary>
        private static void Blit(Graphics g, Bitmap layer, bool copy)
        {
            if (layer == null) return;
            InterpolationMode im = g.InterpolationMode;
            PixelOffsetMode pm = g.PixelOffsetMode;
            CompositingMode cm = g.CompositingMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (copy) g.CompositingMode = CompositingMode.SourceCopy;
            try
            {
                g.DrawImage(layer, new Rectangle(0, 0, layer.Width, layer.Height), 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel);
            }
            finally
            {
                g.InterpolationMode = im;
                g.PixelOffsetMode = pm;
                g.CompositingMode = cm;
            }
        }

        /// <summary>The vignette + "N" at this size (re-rendered on a shape / size / scale / accent change).</summary>
        private Bitmap Vignette(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return null;
            if (_vignette == null || _vignette.Size != size)
            {
                _vignette?.Dispose();
                _vignette = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                VignetteDirty = true;
            }
            if (VignetteDirty || _vignetteK != K || _vignetteAccent != ModernTheme.Accent)
            {
                VignetteDirty = false;
                _vignetteK = K;
                _vignetteAccent = ModernTheme.Accent;
                using Graphics g = Graphics.FromImage(_vignette);
                ModernSkin.Prepare(g);
                g.Clear(Color.Transparent);
                PaintVignette(g, size);
            }
            return _vignette;
        }

        /// <summary>An NPC object's display name (built on hover, cached by its name).</summary>
        public string NpcLabel(uint id)
        {
            if (!MapControl.Objects.TryGetValue(id, out MapObject ob) || ob?.Name == null) return string.Empty;
            if (!_npcNames.TryGetValue(ob.Name, out string label))
            {
                if (_npcNames.Count > 256) _npcNames.Clear();
                label = ChatGfx.NpcName(ob.Name).Name;
                _npcNames[ob.Name] = label;
            }
            return label;
        }

        protected override void Paint(Graphics g)
        {
            Marks.Clear();
            float k = K, size = Size.Width, r = MinimapHud.Radius;
            float cx = _hud.Cx, cy = _hud.Cy;
            UserObject user = GameScene.User;
            MapControl map = GameScene.Scene?.MapControl;
            if (user == null || map == null || float.IsNaN(cx))
            {
                Blit(g, Vignette(Size), false);
                return;
            }
            Blit(g, Info.Get(Size), true);
            Color accent = ModernTheme.Accent;
            float half = size / 2f;
            PointF ToPx(float x, float y) => new PointF(((x - cx) / r + 1) * half, ((y - cy) / r + 1) * half);
            bool Inside(PointF p) => p.X >= -6 && p.Y >= -6 && p.X <= size + 6 && p.Y <= size + 6;
            float sw = Math.Max(1f, k);
            ClientMapInfo info = _hud.Data?.Map(map.Index);

            // objects in view (DrawMiniMap: items, the dead, spells and sneaking objects skipped)
            Models.GroupState group = GameScene.Models?.Group;
            if (!ReferenceEquals(_petFor, user.Name))
            {
                _petFor = user.Name;
                _petSuffix = "(" + user.Name + ")";
            }
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
            {
                MapObject ob = list[i];
                if (ob == null || ob == user || ob.Race == ObjectType.Item || ob.Dead || ob.Race == ObjectType.Spell || ob.Sneaking) continue;
                if (ob.Race == ObjectType.Player && group != null && group.Contains(ob.Name) && group.MemberLocations.ContainsKey(ob.Name)) continue;
                bool npc = ob.Race == ObjectType.Merchant || ob.AI == 6;
                if (ob.Race == ObjectType.Merchant && info != null) continue; // drawn from the map info
                PointF p = ToPx(MinimapHud.DrawnX(ob), MinimapHud.DrawnY(ob));
                if (!Inside(p)) continue;
                Color fill = (ob.Name != null && ob.Name.EndsWith(_petSuffix, StringComparison.Ordinal)) || (ob.Race == ObjectType.Player && group != null && group.Contains(ob.Name)) ? accent
                    : ob.Race == ObjectType.Player ? MinimapColours.Player : npc ? MinimapColours.Npc : MinimapColours.Monster;
                ChatGfx.Dot(g, p.X, p.Y, (ob.Race == ObjectType.Player ? 2.25f : 2f) * k, fill, sw);
                if (npc) Marks.Add(new MinimapMark(p.X, p.Y, null) { Kind = MinimapMark.ObjectNpc, Id = ob.ObjectID });
            }

            // NPC quest markers ("!" / "?" over the NPC)
            if (_hud.Markers.Count > 0)
            {
                if (_markerFont == null || _markerK != k)
                {
                    _markerFont?.Dispose();
                    _markerFont = new Font("Arial", 9f * k, FontStyle.Bold, GraphicsUnit.Pixel);
                    _markerK = k;
                    _wBang = g.MeasureString("!", _markerFont, PointF.Empty, ChatGfx.Format).Width;
                    _wQuery = g.MeasureString("?", _markerFont, PointF.Empty, ChatGfx.Format).Width;
                }
                Font f = _markerFont;
                foreach (KeyValuePair<uint, (string Text, Color Colour)> m in _hud.Markers)
                {
                    PointF at;
                    ClientNPCInfo npcInfo = null;
                    if (info != null) for (int i = 0; i < info.NPCs.Count; i++) if (info.NPCs[i].ObjectID == m.Key) { npcInfo = info.NPCs[i]; break; }
                    if (npcInfo != null) at = ToPx(npcInfo.Location.X, npcInfo.Location.Y);
                    else if (MapControl.Objects.TryGetValue(m.Key, out MapObject ob)) at = ToPx(MinimapHud.DrawnX(ob), MinimapHud.DrawnY(ob));
                    else continue;
                    if (!Inside(at)) continue;
                    float tw = m.Value.Text == "!" ? _wBang : _wQuery;
                    ChatGfx.ShadowText(g, m.Value.Text, f, m.Value.Colour, at.X - tw / 2f, at.Y - 4 * k - f.Height, 1f, 1.6f);
                }
            }

            // group members on this map: accent diamonds (S.SendMemberLocation)
            if (group != null)
            {
                foreach (KeyValuePair<string, Point> kv in group.MemberLocations)
                {
                    if (kv.Key == user.Name) continue;
                    PointF p = ToPx(kv.Value.X, kv.Value.Y);
                    if (!Inside(p)) continue;
                    GraphicsState st = g.Save();
                    g.TranslateTransform(p.X, p.Y);
                    g.RotateTransform(45);
                    float d = 2.5f * k;
                    using (var b = new SolidBrush(accent)) g.FillRectangle(b, -d, -d, d * 2, d * 2);
                    using (var pen = new Pen(Color.White, sw)) g.DrawRectangle(pen, -d, -d, d * 2, d * 2);
                    g.Restore(st);
                    Marks.Add(new MinimapMark(p.X, p.Y, kv.Key));
                }
            }

            // you: an arrow at the centre, pointing the way you face
            ChatGfx.Arrow(g, half, half, _hud.Angle, accent, 5.5f * k, Color.White, sw);
            Blit(g, Vignette(Size), false);
        }

        /// <summary>.vig (inset 0 0 22px rgba(0,0,0,.85)) and the "N" (minimap.ts .n).</summary>
        private static void PaintVignette(Graphics g, Size disc)
        {
            float k = ModernRoot.K, size = disc.Width;
            using (var path = new GraphicsPath())
            {
                if (MinimapHud.Square) path.AddRectangle(new RectangleF(0, 0, size, disc.Height));
                else path.AddEllipse(0, 0, size, disc.Height);
                using var brush = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(0, 0, 0, 0),
                    SurroundColors = new[] { Color.FromArgb(217, 0, 0, 0) },
                    FocusScales = new PointF(1f - 22f * k * 2 / size, 1f - 22f * k * 2 / size),
                };
                g.FillPath(brush, path);
            }
            Font f = ModernSkin.Font(10, true);
            string north = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatMapNorth);
            float w = ChatGfx.Measure(north, f);
            ChatGfx.ShadowText(g, north, f, ModernTheme.Accent, (size - w) / 2f, 13 * k - f.Height / 2f, 1f, 1.2f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _markerFont?.Dispose();
                _markerFont = null;
                _vignette?.Dispose();
                _vignette = null;
                Info.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The hover tip (minimap.ts .tip).</summary>
    internal sealed class MinimapTip : ModernControl
    {
        private string _text = string.Empty;

        public MinimapTip() { NotControl = true; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value ?? string.Empty;
                Size = new Size((int)Math.Ceiling(ChatGfx.Measure(_text, ModernSkin.Font(10, true))) + S(16), S(17));
                Invalidate();
            }
        }

        protected override void Paint(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, 6 * K);
            using (var b = new SolidBrush(Color.FromArgb(204, 0, 0, 0))) g.FillPath(b, p);
            ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Accent, 0.5f));
            ModernSkin.Text(g, _text, ModernSkin.Font(10, true), Color.White, r, StringAlignment.Center);
        }
    }
}
