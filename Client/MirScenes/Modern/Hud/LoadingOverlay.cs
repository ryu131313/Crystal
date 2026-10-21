using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.Patching;

namespace Client.MirScenes.Modern.Hud
{
    /// <summary>
    /// The new UI's loading screen (port of WebClient/src/ui/modern/screens/loading.ts), shown over the game from the moment
    /// the modern UI is built until the player continues: "NOW ENTERING", the map title and its big map picture, facts (NPCs and
    /// exits from S.NewMapInfo, safe zones and monsters from S.MapDetails), a rotating tip, and a bar fed by the real steps:
    /// map (S.MapInformation; the .map file loads in the same handler), your character (S.UserInformation), the first frame,
    /// then the art (the in-game patcher's downloads finishing, at most 6 s after the first frame). Then "PRESS ANY KEY TO
    /// CONTINUE": a key or a click fades it out and disposes it. While it shows, game keys and clicks never reach the world.
    /// </summary>
    internal sealed class LoadingOverlay : ModernSurface
    {
        private const int ArtCapMs = 6000, TipMs = 4500, FadeMs = 700, SpinMs = 100, SpinFrames = 9, SpinIndex = 940;
        private const int StepMap = 0, StepUser = 1, StepFrame = 2, StepArt = 3, StepCount = 4;
        private static readonly int[] Weights = { 40, 15, 10, 35 };
        private static readonly ClientTextKeys[] StepNames = { ClientTextKeys.ModernLoadStepMap, ClientTextKeys.ModernLoadStepUser, ClientTextKeys.ModernLoadStepFrame, ClientTextKeys.ModernLoadStepArt };
        private static readonly ClientTextKeys[] Tips =
        {
            ClientTextKeys.ModernLoadTip1, ClientTextKeys.ModernLoadTip2, ClientTextKeys.ModernLoadTip3, ClientTextKeys.ModernLoadTip4,
            ClientTextKeys.ModernLoadTip5, ClientTextKeys.ModernLoadTip6, ClientTextKeys.ModernLoadTip7, ClientTextKeys.ModernLoadTip8,
        };
        /// <summary>Friendly guards by AI (bigMap.ts): left out of the monster list.</summary>
        private static readonly HashSet<byte> GuardAi = new HashSet<byte> { 6, 57, 58, 80, 113 };

        private readonly Windows.BigMapData _maps;
        private readonly LoadingArt _art;
        private readonly ModernLabel _title, _sub, _tip, _step, _percent, _press, _facts;
        private readonly ModernProgressBar _bar;
        private readonly ModernLabel[] _stepLabels = new ModernLabel[StepCount];
        private readonly bool[] _done = new bool[StepCount];

        private int _mapIndex = -1, _tipIndex, _spin, _percentShown = -1, _level, _nowShown = -2;
        private long _frameAt, _nextTip, _nextSpin, _fadeStart;
        private bool _ready, _leaving, _sawFrameTick;

        public bool Done => IsDisposed;

        public LoadingOverlay(Windows.BigMapData maps)
        {
            _maps = maps;
            int W = Settings.ScreenWidth, H = Settings.ScreenHeight;
            Size = new Size(W, H);
            Location = Point.Empty;
            _tipIndex = Environment.TickCount % Tips.Length;
            if (_tipIndex < 0) _tipIndex = -_tipIndex;

            new LoadingBlack { Parent = this, Size = Size };
            _art = new LoadingArt { Parent = this, Size = Size };
            new LoadingShade { Parent = this, Size = Size };

            int side = S(60);
            Label(ClientTextKeys.ModernLoadNowEntering, side, S(60), W - side * 2, S(16), 11, ModernTheme.Accent, true);
            _title = Label(string.Empty, side, S(80), W - side * 2, S(60), 40, Color.White, true);
            _sub = Label(string.Empty, side, S(142), W - side * 2, S(18), 13, ModernTheme.Dim);

            _facts = Label(string.Empty, side, H - S(300), S(380), S(160), 12, ModernTheme.Text, false, true);

            Label(ClientTextKeys.ModernLoadTip, W - side - S(330), H - S(250), S(330), S(14), 10, ModernTheme.Dim, true);
            _tip = Label(T(Tips[_tipIndex]), W - side - S(330), H - S(232), S(330), S(80), 13, Color.White, false, true);

            _step = Label(string.Empty, side + S(100), H - S(110), W - side * 2 - S(160), S(20), 13, Color.White);
            _percent = Label("0%", W - side - S(60), H - S(110), S(60), S(20), 14, ModernTheme.Accent, true);
            _percent.Align = StringAlignment.Far;
            _bar = new ModernProgressBar((int)((W - side * 2) / ModernRoot.K), 6) { Parent = this, Location = new Point(side, H - S(84)) };
            int x = side;
            for (int i = 0; i < StepCount; i++)
            {
                _stepLabels[i] = Label(T(StepNames[i]), x, H - S(70), S(150), S(14), 10, ModernTheme.Dim);
                x += S(150);
            }
            _press = Label(ClientTextKeys.ModernLoadPressKey, 0, H - S(40), W, S(20), 13, Color.White, true);
            _press.Align = StringAlignment.Center;
            _press.Visible = false;

            ModernEvents.MapChanged.On(OnMapChanged);
            ModernEvents.UserInformation.On(OnUser);
            if (_maps != null) _maps.Changed += OnMapData;
            GameScene.Models?.MapDetails.Changed.On(OnMapData);
            _nextTip = CMain.Time + TipMs;
            Update();
        }

        // ------------------------------------------------------------------ progress

        private void OnMapChanged(int mapIndex)
        {
            if (_done[StepMap] && mapIndex == _mapIndex) return;
            _mapIndex = mapIndex;
            _done[StepMap] = true;
            MapControl map = GameScene.Scene?.MapControl;
            if (map != null)
            {
                _title.Text = (string.IsNullOrEmpty(map.Title) ? Path.GetFileNameWithoutExtension(map.FileName) : map.Title).ToUpperInvariant();
                _sub.Text = T(ClientTextKeys.ModernLoadMapLine, Path.GetFileNameWithoutExtension(map.FileName));
                _art.Index = map.BigMap > 0 ? map.BigMap : map.MiniMap;
            }
            _maps?.RequestMap(mapIndex);
            GameScene.Models?.MapDetails.Request(mapIndex, GameScene.Models.ServerRules);
            DrawFacts();
            Update();
        }

        private void OnUser()
        {
            _done[StepUser] = true;
            _level = GameScene.User?.Level ?? 0;
            DrawFacts();
            Update();
        }

        private void OnMapData(int mapIndex)
        {
            if (mapIndex == _mapIndex) DrawFacts();
        }

        public override void Process()
        {
            base.Process();
            if (IsDisposed) return;
            if (_leaving)
            {
                float t = (CMain.Time - _fadeStart) / (float)FadeMs;
                if (t >= 1f)
                {
                    Dispose();
                    return;
                }
                Opacity = 1f - t; // drawn with the opacity: no re-render
                return;
            }
            if (CMain.Time >= _nextTip)
            {
                _nextTip = CMain.Time + TipMs;
                _tipIndex = (_tipIndex + 1) % Tips.Length;
                _tip.Text = T(Tips[_tipIndex]);
            }
            if (_ready) return;
            if (CMain.Time >= _nextSpin)
            {
                _nextSpin = CMain.Time + SpinMs;
                _spin = (_spin + 1) % SpinFrames;
                GameScene.Models?.MapDetails.Retry(GameScene.Models.ServerRules);
                // the first frame: one tick after map and character are in (the world drew in between)
                if (!_done[StepFrame] && _done[StepMap] && _done[StepUser])
                {
                    if (_sawFrameTick)
                    {
                        _done[StepFrame] = true;
                        _frameAt = CMain.Time;
                    }
                    _sawFrameTick = true;
                }
                Update();
            }
        }

        private void Update()
        {
            if (_ready) return;
            bool downloading = PatchStatus.Visible;
            if (_done[StepFrame] && !_done[StepArt] && (!downloading || CMain.Time - _frameAt >= ArtCapMs)) _done[StepArt] = true;

            float p = 0;
            int now = -1;
            for (int i = 0; i < StepCount; i++)
            {
                if (_done[i]) p += Weights[i];
                else if (now < 0) now = i;
            }
            if (now == StepArt && _done[StepFrame])
                p += Weights[StepArt] * Math.Clamp((CMain.Time - _frameAt) / (float)ArtCapMs, 0f, downloading ? 0.95f : 1f);
            int percent = Math.Min(100, (int)Math.Round(p));
            if (percent != _percentShown)
            {
                _percentShown = percent;
                _percent.Text = percent + "%";
                _bar.Set(percent / 100f);
            }
            if (now == StepArt)
            {
                // the patcher's line changes while it downloads: re-read it (the setter ignores an unchanged string)
                _step.Text = downloading && !string.IsNullOrEmpty(PatchStatus.Text) ? PatchStatus.Text : T(ClientTextKeys.ModernLoadWarmingArt);
            }
            if (now == _nowShown) return; // the step labels change only when the current step does
            _nowShown = now;
            for (int i = 0; i < StepCount; i++)
            {
                Color c = _done[i] ? ModernTheme.Ok : i == now ? Color.White : ModernTheme.Dim;
                _stepLabels[i].Colour = c;
                _stepLabels[i].Text = (_done[i] ? "✓ " : i == now ? "● " : "○ ") + T(StepNames[i]);
            }
            switch (now)
            {
                case StepMap:
                    _step.Text = T(ClientTextKeys.ModernLoadEnteringWorld);
                    break;
                case StepUser:
                    _step.Text = T(ClientTextKeys.ModernLoadLoadingCharacter);
                    break;
                case StepFrame:
                    _step.Text = T(ClientTextKeys.ModernLoadDrawingWorld);
                    break;
                case StepArt:
                    break;
                default:
                    _step.Text = T(ClientTextKeys.ModernLoadReady);
                    _ready = true;
                    _press.Visible = true;
                    break;
            }
        }

        /// <summary>NPC / exit counts, safe zones and monsters, your level against the monsters' (loading.ts drawFacts).</summary>
        private void DrawFacts()
        {
            if (IsDisposed || _mapIndex < 0) return;
            var sb = new System.Text.StringBuilder();
            ClientMapInfo map = _maps?.Map(_mapIndex);
            if (map != null)
                sb.Append(T(ClientTextKeys.ModernLoadNpcsExits, map.NPCs?.Count ?? 0, map.Movements?.Count ?? 0));

            Models.MapDetailsState.Entry details = GameScene.Models?.MapDetails.Get(_mapIndex);
            if (details != null)
            {
                var kinds = new Dictionary<string, (int Level, int Count)>();
                int spawns = 0;
                List<ClientMonsterInfo> infos = GameScene.MonsterInfoList;
                for (int i = 0; i < details.Respawns.Count; i++)
                {
                    ClientMapRespawn r = details.Respawns[i];
                    ClientMonsterInfo info = null;
                    for (int j = 0; j < infos.Count; j++)
                        if (infos[j].Index == r.MonsterIndex) { info = infos[j]; break; }
                    if (info == null || GuardAi.Contains(info.AI) || info.Level >= 255 || info.Level == 0) continue;
                    string name = (string.IsNullOrEmpty(info.GameName) ? info.Name : info.GameName).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                    spawns += r.Count;
                    kinds[name] = kinds.TryGetValue(name, out var cur) ? (Math.Max(cur.Level, info.Level), cur.Count + r.Count) : (info.Level, r.Count);
                }
                if (sb.Length > 0) sb.Append("   ");
                sb.Append(T(ClientTextKeys.ModernLoadZonesMonsters, details.SafeZones.Count, spawns));
                if (kinds.Count > 0)
                {
                    var list = new List<KeyValuePair<string, (int Level, int Count)>>(kinds);
                    list.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
                    int lo = int.MaxValue, hi = 0;
                    sb.Append("\n\n").Append(T(ClientTextKeys.ModernLoadWhoLives).ToUpperInvariant()).Append('\n');
                    for (int i = 0; i < list.Count; i++)
                    {
                        lo = Math.Min(lo, list[i].Value.Level);
                        hi = Math.Max(hi, list[i].Value.Level);
                        if (i < 8) sb.Append(i == 0 ? string.Empty : " · ").Append(list[i].Key).Append(' ').Append(T(ClientTextKeys.ModernLoadLv, list[i].Value.Level));
                    }
                    string range = lo == hi ? T(ClientTextKeys.ModernLoadLv, lo) : T(ClientTextKeys.ModernLoadLvRange, lo, hi);
                    sb.Append("\n\n").Append(_level > 0
                        ? T(hi > _level + 5 ? ClientTextKeys.ModernLoadCareful : ClientTextKeys.ModernLoadYoureReady, range, _level)
                        : T(ClientTextKeys.ModernLoadMonstersRange, range));
                }
                else if (details.Complete) sb.Append("\n\n").Append(T(ClientTextKeys.ModernLoadNoMonsters));
            }
            else if (_level > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(T(ClientTextKeys.ModernLoadYouAre, _level));
            }
            _facts.Text = sb.ToString();
        }

        // ------------------------------------------------------------------ input

        /// <summary>Every key while it shows (the world behind gets none); once ready, a key continues. Modifiers alone pass.</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            if (IsDisposed || _leaving) return false;
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.CapsLock) return true;
            if (_ready) Continue();
            return true;
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            Continue();
        }

        private void Continue()
        {
            if (!_ready || _leaving || IsDisposed) return;
            _leaving = true;
            _fadeStart = CMain.Time;
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_ready || _leaving || Libraries.Prguse == null) return;
            // the spinner is drawn after the target (not inside it), so it never re-renders the whole screen texture
            Libraries.Prguse.Draw(SpinIndex + _spin, new Point(DisplayLocation.X + S(60), Size.Height - S(112)), Color.White, false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ModernEvents.MapChanged.Off(OnMapChanged);
                ModernEvents.UserInformation.Off(OnUser);
                if (_maps != null) _maps.Changed -= OnMapData;
                GameScene.Models?.MapDetails.Changed.Off(OnMapData);
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ helpers

        private static string T(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);
        private static string T(ClientTextKeys key, params object[] args) => GameLanguage.ClientTextMap.GetLocalization(key, args);

        private ModernLabel Label(ClientTextKeys key, int x, int y, int w, int h, float size, Color colour, bool bold = false, bool wrap = false) =>
            Label(T(key), x, y, w, h, size, colour, bold, wrap);

        private ModernLabel Label(string text, int x, int y, int w, int h, float size, Color colour, bool bold = false, bool wrap = false) =>
            new ModernLabel { Parent = this, Location = new Point(x, y), Size = new Size(w, h), Text = text, FontSize = size, Colour = colour, Bold = bold, Wrap = wrap };
    }

    /// <summary>The map's big map picture (MMap.Lib), scaled to cover the screen and darkened; black when there is none.</summary>
    internal sealed class LoadingArt : MirControl
    {
        private int _index;

        public LoadingArt()
        {
            NotControl = true;
            DrawControlTexture = false;
        }

        public int Index
        {
            get => _index;
            set
            {
                if (_index == value) return;
                _index = value;
                Redraw();
            }
        }

        protected internal override void DrawControl()
        {
            if (_index <= 0 || Libraries.MiniMap == null) return;
            try
            {
                Size s = Libraries.MiniMap.GetSize(_index);
                if (s.Width <= 0 || s.Height <= 0) return;
                float k = Math.Max(Size.Width / (float)s.Width, Size.Height / (float)s.Height) * 1.08f;
                Size d = new Size((int)Math.Ceiling(s.Width * k), (int)Math.Ceiling(s.Height * k));
                Point at = new Point(DisplayLocation.X + (Size.Width - d.Width) / 2, DisplayLocation.Y + (Size.Height - d.Height) / 2);
                Libraries.MiniMap.Draw(_index, at, d, Color.FromArgb(255, 120, 120, 120));
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] loading art", ex);
            }
        }
    }

    /// <summary>The opaque black under the art (the world must not show through before the player continues).</summary>
    internal sealed class LoadingBlack : ModernControl
    {
        public LoadingBlack()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g) => g.Clear(Color.Black);
    }

    /// <summary>The loading screen's shade (loading.ts .shade): dark at the bottom and the left, so the text reads over any map.</summary>
    internal sealed class LoadingShade : ModernControl
    {
        public LoadingShade()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            RectangleF r = new RectangleF(0, 0, Size.Width, Size.Height);
            using (var v = new LinearGradientBrush(new RectangleF(0, -1, Size.Width, Size.Height + 2), Color.Black, Color.Black, LinearGradientMode.Vertical))
            {
                v.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(140, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(90, 0, 0, 0), Color.FromArgb(235, 0, 0, 0) },
                    Positions = new[] { 0f, 0.4f, 0.65f, 1f },
                };
                g.FillRectangle(v, r);
            }
            using var h = new LinearGradientBrush(new RectangleF(-1, 0, Size.Width + 2, Size.Height), Color.Black, Color.Black, LinearGradientMode.Horizontal);
            h.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0) },
                Positions = new[] { 0f, 0.45f, 1f },
            };
            g.FillRectangle(h, r);
        }
    }
}
