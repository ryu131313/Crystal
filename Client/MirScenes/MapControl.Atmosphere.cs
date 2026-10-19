using System.Runtime.InteropServices;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirObjects;
using Client.MirSounds;
using SlimDX;

namespace Client.MirScenes
{
    /// <summary>
    /// Atmosphere weather ([Graphics] Atmosphere; Desktop-Smooth-Mode-and-Weather Phases 1-2): shader rain, lightning, wet
    /// ground and puddle reflections on weather maps. D3D11 + enhanced lighting only; D3D9 and classic lighting keep the
    /// weather sprites untouched.
    ///
    /// The simulation is <see cref="WeatherFx"/> (one per map). Baked into the map texture in <see cref="CreateTexture"/>:
    /// the wet ground (puddles, ripples, splashes, eaves drips), footfall ripples, and the reflectivity alpha pass. Drawn
    /// over the cached map every frame in <see cref="DrawControl"/>: body splashes, foot spray, rain, reflections and the
    /// lightning strobe. Order in CreateTexture: floor -> wet ground -> foot ripples -> reflectivity mask -> objects ->
    /// weather -> snapshot of the scene alpha -> enhanced lights.
    /// </summary>
    public sealed partial class MapControl
    {
        private const int BodySplashMs = 260, MaxBodySplashes = 240, FootSplashMs = 520, MaxFootSplashes = 120;
        private const float BodySplashesPerSecond = 7f, FootPuddleMin = 0.2f;
        private const float Ripples = 0.7f, Splashes = 0.5f, DripRate = 0.6f;
        private const int MaskRefreshMs = 1500;

        private WeatherFx _weatherFx;
        private readonly Vector4[] _atmoInputs = new Vector4[128];

        /// <summary>Set by <see cref="CreateTexture"/>: the cached map went through the enhanced light pass, so
        /// <see cref="DXManager.EnhancedLightTexture"/> belongs to it (on Day without a pass the texture is stale).</summary>
        private bool _atmosphereLightDrawn;

        // ---------------------------------------------------------------- cell mask
        private byte[] _maskData;
        private int _maskCols, _maskRows, _maskOriginX, _maskOriginY, _maskCellX, _maskCellY;
        private object _maskMap;
        private string _maskKey = string.Empty;
        private long _maskAt;
        private object _atmosphereMap;

        // ---------------------------------------------------------------- splashes
        private struct BodySplash
        {
            public MapObject Ob;
            public int Ox, Oy;
            public long Born;
            public float Seed;
        }

        private struct FootSplash
        {
            public float Wx, Wy;
            public long Born;
            public float K, Seed, Dx, Dy;
            public bool Run;
        }

        /// <summary>A lamp for the puddle reflections, in map/screen pixels (y down): it shines from (X, EmitterY) and its
        /// object meets the ground at ContactY, so it mirrors to (X, 2 * ContactY - EmitterY). Radius = the light's width.</summary>
        private struct ReflectionSource
        {
            public float X, EmitterY, ContactY, Radius;
            public float R, G, B;
        }

        private readonly List<BodySplash> _bodySplashes = new List<BodySplash>();
        private readonly List<FootSplash> _footSplashes = new List<FootSplash>();
        private readonly Dictionary<uint, int> _lastFoot = new Dictionary<uint, int>();
        private readonly Random _splashRng = new Random();
        private long _lastSplashAt;
        private RenderTexture _dotTexture, _ringTexture;

        /// <summary>What a recorded light is, for its height above the ground. Lamp = a map cell light whose animated sprite
        /// lifted it by `lift` px; StaticLamp = a map cell light at its cell's foot.</summary>
        private enum ReflectKind { Carried, Effect, Lamp, StaticLamp }

        // Reflection heights (web postFx.ts reflectionTuning, the shipped defaults): px above / below the light-map centre.
        private const float CarriedHeight = 30F, CarriedFoot = 22F, EffectHeight = 0F, StaticLampHeight = 0F;
        private const float LampEmitter = 14F, LampContact = -46F, LampHeightScale = 1.05F;

        /// <summary>The light centred on (cx, cy), radius = half its width: where it shines from and where its object stands.</summary>
        private static ReflectionSource RecordReflection(float cx, float cy, float radius, Color colour, float intensity, ReflectKind kind, int lift)
        {
            float emitterY = cy, contactY = cy;
            switch (kind)
            {
                case ReflectKind.Carried: emitterY = cy - CarriedHeight; contactY = cy + CarriedFoot; break;
                case ReflectKind.Effect: emitterY = cy - EffectHeight; contactY = cy + CarriedFoot * 0.5F; break;
                case ReflectKind.Lamp: emitterY = cy + LampEmitter; contactY = cy + lift * LampHeightScale + LampContact; break;
                case ReflectKind.StaticLamp: emitterY = cy - StaticLampHeight; break;
            }
            float k = Math.Min(intensity, 2F) / 255F;
            return new ReflectionSource { X = cx, EmitterY = emitterY, ContactY = contactY, Radius = radius, R = colour.R * k, G = colour.G * k, B = colour.B * k };
        }

        private readonly List<ReflectionSource> _reflectionSources = new List<ReflectionSource>();
        private readonly List<ReflectionSource> _reflectionPick = new List<ReflectionSource>();

        /// <summary>This map build wrote the wet ground and its reflectivity snapshot.</summary>
        private bool _groundWet, _snapshotOk;
        /// <summary>This build's ripple share (the lamp reflections ripple with the same rings).</summary>
        private float _ripplesNow;

        private static readonly int[,] Directions =
        {
            { 0, -1 }, { 1, -1 }, { 1, 0 }, { 1, 1 }, { 0, 1 }, { -1, 1 }, { -1, 0 }, { -1, -1 }
        };

        /// <summary>True when the atmosphere may run here: [Graphics] Atmosphere, D3D11 with the shaders compiled, enhanced lighting.</summary>
        private static bool AtmosphereEnabled => Settings.Atmosphere && DXManager.AtmosphereAvailable && UseEnhancedLighting;

        /// <summary>True while the shader rain replaces the Weather.Lib rain sprites on this map.</summary>
        private bool AtmosphereRain => AtmosphereEnabled && (Weather & WeatherSetting.Rain) != 0;

        /// <summary>True while the ground is wet (raining or drying).</summary>
        private bool AtmosphereWet => AtmosphereEnabled && _weatherFx != null && _weatherFx.Wet;

        /// <summary>Once per spin (called from <see cref="Process"/>): advance the simulation, play the thunder, rebuild the
        /// cell mask and spawn the CPU splashes.</summary>
        public void AtmosphereProcess()
        {
            if (!AtmosphereEnabled || M2CellInfo == null || User == null)
            {
                _weatherFx = null;
                _bodySplashes.Clear();
                _footSplashes.Clear();
                _lastFoot.Clear();
                _maskMap = null;
                _atmosphereMap = null;
                _snapshotOk = false;
                return;
            }

            if (!ReferenceEquals(_atmosphereMap, M2CellInfo))
            {
                _atmosphereMap = M2CellInfo;
                _maskMap = null;
                _lastFoot.Clear();
                _bodySplashes.Clear();
                _footSplashes.Clear();
                _snapshotOk = false;
            }

            if (_weatherFx == null) _weatherFx = new WeatherFx();
            _weatherFx.Update(CMain.Time, Weather, M2CellInfo);

            if (_weatherFx.Thunder) SoundManager.PlaySound(WeatherFx.ThunderSound);

            if (!_weatherFx.Wet) return;

            UpdateCellMask();
            SpawnBodySplashes();
            SpawnFootSplashes();
        }

        /// <summary>Called on the 100 ms tick: the baked wet ground animates at 10 Hz (never per spin).</summary>
        public bool AtmosphereNeedsRebuild(out string reason)
        {
            reason = null;
            if (!AtmosphereEnabled || _weatherFx == null || !_weatherFx.Wet) return false;
            reason = "wet-ground";
            return true;
        }

        // ---------------------------------------------------------------- cell mask

        /// <summary>Rebuilds the per-cell mask around the view when the user's cell or the map changes, and every 1.5 s
        /// (tall objects' sizes arrive as their libraries load). One texel per cell: R = floor, G = water, B = eaves drip.</summary>
        private void UpdateCellMask()
        {
            int cols = ViewRangeX * 2 + 10, rows = ViewRangeY * 2 + 14;
            int x0 = User.Movement.X - ViewRangeX - 4, y0 = User.Movement.Y - ViewRangeY - 4;
            string key = x0 + "," + y0 + "," + cols + "," + rows;
            bool moved = !ReferenceEquals(_maskMap, M2CellInfo) || key != _maskKey;
            if (!moved && CMain.Time - _maskAt < MaskRefreshMs) return;

            _maskMap = M2CellInfo;
            _maskKey = key;
            _maskAt = CMain.Time;

            int n = cols * rows * 4;
            if (_maskData == null || _maskData.Length != n) _maskData = new byte[n];
            byte[] d = _maskData;
            Array.Clear(d, 0, n);

            for (int r = 0; r < rows; r++)
            {
                int y = y0 + r;
                if (y < 0 || y >= Height) continue;
                for (int c = 0; c < cols; c++)
                {
                    int x = x0 + c;
                    if (x < 0 || x >= Width) continue;

                    CellInfo cell = M2CellInfo[x, y];
                    // The texture is BGRA (DXManager.CreateTexture): R = floor, G = water, B = eaves drip.
                    int o = (r * cols + c) * 4;
                    if (IsFloorCell(x, y)) d[o + 2] = 255;
                    if (cell.FishingCell) d[o + 2] = d[o + 1] = 255;
                    d[o + 3] = 255;
                }
            }

            // B: the cell under a tall, non-blended front object (the row below it, across its width) drips.
            for (int y = Math.Max(0, y0 - 1); y < Math.Min(Height, y0 + rows - 1); y++)
            {
                int r = y + 1 - y0;
                if (r < 0 || r >= rows) continue;
                for (int x = Math.Max(0, x0 - 6); x < Math.Min(Width, x0 + cols); x++)
                {
                    CellInfo cell = M2CellInfo[x, y];
                    int index = (cell.FrontImage & 0x7FFF) - 1;
                    int fileIndex = cell.FrontIndex;
                    if (index < 0 || fileIndex == -1 || (cell.FrontAnimationFrame & 0x80) != 0) continue; // blended tiles do not drip
                    if (fileIndex < 0 || fileIndex >= Libraries.MapLibs.Length) continue;
                    MLibrary lib = Libraries.MapLibs[fileIndex];
                    if (lib == null) continue;
                    Size s = lib.GetTrueSize(index);
                    if (s.Height < CellHeight * 2 || s.Width < CellWidth / 2) continue;
                    int width = Math.Max(1, (int)Math.Round((double)s.Width / CellWidth));
                    for (int k = 0; k < width; k++)
                    {
                        int c = x + k - x0;
                        if (c < 0 || c >= cols) continue;
                        d[(r * cols + c) * 4] = 255; // B: eaves drip
                    }
                }
            }

            // DrawFloor's '- OffSetX' pixel quirk (the floor sits that far left of the object mapping).
            _maskOriginX = x0 * CellWidth - OffSetX;
            _maskOriginY = y0 * CellHeight;
            _maskCellX = x0;
            _maskCellY = y0;
            _maskCols = cols;
            _maskRows = rows;

            if (DXManager.AtmosphereCellMask == null || DXManager.AtmosphereCellMask.Disposed ||
                DXManager.AtmosphereCellMask.Width != cols || DXManager.AtmosphereCellMask.Height != rows)
            {
                if (DXManager.AtmosphereCellMask != null && !DXManager.AtmosphereCellMask.Disposed) DXManager.AtmosphereCellMask.Dispose();
                DXManager.AtmosphereCellMask = DXManager.CreateTexture(cols, rows);
            }
            UploadTexture(DXManager.AtmosphereCellMask, d);
        }

        /// <summary>
        /// True when <see cref="DrawFloor"/> paints ground on this cell: a back tile (2 x 2 cells from the even cell), or a
        /// middle or front tile of one or two cells' size (front file 200 excepted), from this cell or, for a 2 x 2 tile, from
        /// the cell up-left of it. Front ground tiles matter: town paving (e.g. around the lamps) is drawn on the front layer.
        /// </summary>
        private bool IsFloorCell(int x, int y)
        {
            CellInfo back = M2CellInfo[x & ~1, y & ~1];
            if (back.BackImage != 0 && back.BackIndex != -1 && (back.BackImage & 0x1FFFFFFF) > 0) return true;

            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = 0; dx <= 1; dx++)
                {
                    int cx = x - dx, cy = y - dy;
                    if (cx < 0 || cy < 0) continue;
                    CellInfo cell = M2CellInfo[cx, cy];
                    bool single = dx == 0 && dy == 0;
                    if (FloorTile(cell.MiddleIndex, cell.MiddleImage - 1, single)) return true;
                    if (cell.FrontIndex != 200 && FloorTile(cell.FrontIndex, (cell.FrontImage & 0x7FFF) - 1, single)) return true;
                }
            }
            return false;
        }

        /// <summary>A middle/front image DrawFloor draws as ground: one cell in size (only on its own cell) or 2 x 2 cells.</summary>
        private static bool FloorTile(int fileIndex, int index, bool ownCell)
        {
            if (index < 0 || fileIndex < 0 || fileIndex >= Libraries.MapLibs.Length) return false;
            MLibrary lib = Libraries.MapLibs[fileIndex];
            if (lib == null) return false;
            Size s = lib.GetSize(index);
            if (s.Width == CellWidth * 2 && s.Height == CellHeight * 2) return true;
            return ownCell && s.Width == CellWidth && s.Height == CellHeight;
        }

        /// <summary>0..1 how much puddle the ground is at world pixel (wx, wy): 1 on water, the shader's puddle() on floor,
        /// 0 off the floor or past the mask. Mirrors AtmosphereNoise.PuddleAt / Ground_PS puddle().</summary>
        private float PuddleAt(float wx, float wy)
        {
            if (_maskData == null) return 0F;
            int c = (int)MathF.Floor((wx - _maskOriginX) / CellWidth);
            int r = (int)MathF.Floor((wy - _maskOriginY) / CellHeight);
            if (c < 0 || r < 0 || c >= _maskCols || r >= _maskRows) return 0F;
            int o = (r * _maskCols + c) * 4;
            if (_maskData[o] == 0) return 0F;
            if (_maskData[o + 1] != 0) return 1F;
            return AtmosphereNoise.PuddleAt(wx, wy, _weatherFx.Wetness);
        }

        // ---------------------------------------------------------------- splashes

        /// <summary>Raindrops bursting on figures (Player, Monster and Merchant): at the first visible pixel of a random body
        /// column, i.e. on the head, shoulders and arms.</summary>
        private void SpawnBodySplashes()
        {
            long now = CMain.Time;
            long dt = _lastSplashAt == 0 ? 0 : Math.Clamp(now - _lastSplashAt, 0, 100);
            _lastSplashAt = now;
            if (_weatherFx == null || !_weatherFx.Rain || dt == 0) return;

            float chance = BodySplashesPerSecond * _weatherFx.RainIntensity(now) * dt / 1000F;
            if (chance <= 0F) return;

            for (int i = 0; i < ObjectsList.Count; i++)
            {
                if (_bodySplashes.Count >= MaxBodySplashes) break;
                MapObject ob = ObjectsList[i];
                if (ob.Dead || ob.Hidden || !IsNearView(ob.CurrentLocation, ViewMargin)) continue;
                if (ob.Race != ObjectType.Player && ob.Race != ObjectType.Monster && ob.Race != ObjectType.Merchant) continue;
                if (ob.BodyLibrary == null) continue;
                if (_splashRng.NextDouble() >= chance) continue;

                Size s = ob.BodyLibrary.GetSize(ob.DrawFrame);
                if (s.Width <= 0 || s.Height <= 0) continue;

                int lx = _splashRng.Next(s.Width);
                int ly = -1;
                for (int y = 0; y < s.Height; y++)
                {
                    if (ob.BodyLibrary.VisiblePixel(ob.DrawFrame, new Point(lx, y), true)) { ly = y; break; }
                }
                if (ly < 0) continue;

                Point off = ob.BodyLibrary.GetOffSet(ob.DrawFrame);
                _bodySplashes.Add(new BodySplash
                {
                    Ob = ob,
                    Ox = off.X + lx,
                    Oy = off.Y + ly + 1,
                    Born = now,
                    Seed = (float)_splashRng.NextDouble(),
                });
            }
        }

        /// <summary>A player entering frame 1 (left foot) or 4 (right foot) of a step, where the foot lands in a puddle or water.</summary>
        private void SpawnFootSplashes()
        {
            if (_weatherFx == null || !_weatherFx.Wet) return;

            float camX = (User.Movement.X - OffSetX) * CellWidth - User.OffSetMove.X;
            float camY = (User.Movement.Y - OffSetY) * CellHeight - User.OffSetMove.Y;

            for (int i = 0; i < ObjectsList.Count; i++)
            {
                if (ObjectsList[i] is not PlayerObject player) continue;

                int fi = player.FrameIndex;
                int prev = _lastFoot.TryGetValue(player.ObjectID, out int seen) ? seen : -1;
                _lastFoot[player.ObjectID] = fi;
                if (player.Dead || fi == prev || (fi != 1 && fi != 4)) continue;

                MirAction action = player.CurrentAction;
                if (action != MirAction.Walking && action != MirAction.Running && action != MirAction.WalkingBow &&
                    action != MirAction.RunningBow && action != MirAction.Sneek && action != MirAction.MountWalking &&
                    action != MirAction.MountRunning) continue;
                if (_footSplashes.Count >= MaxFootSplashes) continue;

                int dir = (int)player.Direction & 7;
                float sx = Directions[dir, 0] * CellWidth, sy = Directions[dir, 1] * CellHeight;
                float len = MathF.Sqrt(sx * sx + sy * sy);
                if (len <= 0F) continue;
                float dx = sx / len, dy = sy / len;
                int side = fi == 1 ? -1 : 1;

                Point at = player.DrawLocation;
                float wx = at.X + CellWidth / 2F - dy * 5F * side + camX;
                float wy = at.Y + CellHeight / 2F + 2F + dx * 3F * side + camY;
                float k = PuddleAt(wx, wy);
                if (k < FootPuddleMin) continue;

                bool run = action == MirAction.Running || action == MirAction.RunningBow || action == MirAction.MountRunning;
                _footSplashes.Add(new FootSplash { Wx = wx, Wy = wy, Born = CMain.Time, K = k, Seed = (float)_splashRng.NextDouble(), Dx = dx, Dy = dy, Run = run });
            }
        }

        // ---------------------------------------------------------------- baked passes (CreateTexture)

        /// <summary>After the floor, before the objects: wet ground, footfall ripples, then the reflectivity alpha pass.</summary>
        private void DrawAtmosphereGround()
        {
            _groundWet = false;
            _snapshotOk = false;
            if (!AtmosphereEnabled || !AtmosphereWet || User == null || _maskData == null || DXManager.AtmosphereCellMask == null) return;

            float camX = (User.Movement.X - OffSetX) * CellWidth - User.OffSetMove.X;
            float camY = (User.Movement.Y - OffSetY) * CellHeight - User.OffSetMove.Y;
            float intensity = _weatherFx.RainIntensity(CMain.Time);
            _ripplesNow = Ripples * (_weatherFx.Rain ? 0.25F + 0.75F * intensity : 0F);
            float splash = Splashes * (_weatherFx.Rain ? 0.25F + 0.75F * intensity : 0F);
            float drip = _weatherFx.Rain ? DripRate * (0.3F + 0.7F * intensity) : DripRate * 0.5F * _weatherFx.Wetness;

            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, CMain.Time / 1000F, 0F); // mode 0 = wet
            _atmoInputs[1] = new Vector4(camX, camY, 0.5F + 0.5F * _weatherFx.Wetness, _ripplesNow);
            _atmoInputs[2] = new Vector4(splash, _weatherFx.Wetness, 0F, drip);
            _atmoInputs[3] = new Vector4(_maskOriginX, _maskOriginY, _maskCellX, _maskCellY);
            _atmoInputs[4] = new Vector4(_maskCols, _maskRows, 0F, 0F);

            DXManager.DrawFullscreen(FullscreenKind.GroundWet, DXManager.AtmosphereCellMask, _atmoInputs.AsSpan(0, 5));

            DrawFootRipples(camX, camY);

            // Alpha pass: blending off, alpha-only write. Floor pixels become 0.5 - 0.5 * reflectivity; everything else 1.
            _atmoInputs[2] = new Vector4(splash, _weatherFx.Wetness, 1F, drip);
            DXManager.DrawFullscreen(FullscreenKind.GroundMask, DXManager.AtmosphereCellMask, _atmoInputs.AsSpan(0, 5));
            _groundWet = true;
        }

        /// <summary>After the objects and weather, before the lights: the scene alpha into the wet-mask target.</summary>
        private void DrawAtmosphereSnapshot()
        {
            if (!_groundWet || ControlTexture == null || ControlTexture.Disposed) return;

            int width = Settings.ScreenWidth, height = Settings.ScreenHeight;
            if (DXManager.AtmosphereWetMask == null || DXManager.AtmosphereWetMask.Disposed ||
                DXManager.AtmosphereWetMask.Width != width || DXManager.AtmosphereWetMask.Height != height)
            {
                if (DXManager.AtmosphereWetMask != null && !DXManager.AtmosphereWetMask.Disposed) DXManager.AtmosphereWetMask.Dispose();
                DXManager.AtmosphereWetMask = DXManager.CreateRenderTarget(width, height, RenderTargetFormat.Rgba8);
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(DXManager.AtmosphereWetMask);
            _atmoInputs[0] = new Vector4(width, height, 0F, 0F);
            DXManager.DrawFullscreen(FullscreenKind.Snapshot, ControlTexture, _atmoInputs.AsSpan(0, 1));
            DXManager.SetSurface(oldSurface);
            _snapshotOk = true;

            // The reflectivity lives in the map texture's alpha only until the snapshot: put it back to 1, because the map is
            // drawn to the screen alpha-blended and wet floor would otherwise show the previous frame through it.
            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, 0F, 0F);
            _atmoInputs[2] = new Vector4(0F, 0F, 2F, 0F);
            DXManager.DrawFullscreen(FullscreenKind.GroundMask, null, _atmoInputs.AsSpan(0, 3));
        }

        /// <summary>Footfall ripples on the puddle, before the objects (so the foot stands in them).</summary>
        private void DrawFootRipples(float camX, float camY)
        {
            if (_footSplashes.Count == 0) return;
            EnsureSplashTextures();
            long now = CMain.Time;
            for (int i = 0; i < _footSplashes.Count; i++)
            {
                FootSplash s = _footSplashes[i];
                if (now - s.Born >= FootSplashMs) continue;
                float x = s.Wx - camX, y = s.Wy - camY;
                if (x < -40 || y < -40 || x > Size.Width + 40 || y > Size.Height + 40) continue;

                float age = (now - s.Born) / (float)FootSplashMs;
                for (int ring = 0; ring < 2; ring++)
                {
                    float delay = ring == 0 ? 0F : 0.25F;
                    float scale = ring == 0 ? 1F : 0.65F;
                    float strength = ring == 0 ? 0.55F : 0.35F;
                    float t = (age - delay) / (1F - delay);
                    if (t <= 0F || t >= 1F) continue;
                    float rx = (4F + (12F + 8F * s.K) * MathF.Sqrt(t)) * scale * (s.Run ? 1.15F : 1F);
                    float ry = rx / 1.7F;
                    int alpha = (int)(strength * s.K * (1F - t) * (1F - t) * 255F);
                    DrawScaledSprite(_ringTexture, x - rx, y - ry, rx * 2F, ry * 2F, Color.FromArgb(alpha, 217, 230, 255));
                }
            }
        }

        // ---------------------------------------------------------------- overlay (DrawControl, every frame)

        /// <summary>DrawControl, after the cached map: rain, splashes, reflections then the lightning strobe. Alive only.</summary>
        private void DrawAtmosphereOverlay()
        {
            if (_weatherFx == null || User == null || User.Dead) return;
            DrawAtmosphereRain();
            DrawAtmosphereSplashes();
            DrawAtmosphereReflections();
            DrawAtmosphereLightning();
        }

        /// <summary>The fullscreen rainfall pass over the cached map; its source is the light map that map was lit with.</summary>
        private void DrawAtmosphereRain()
        {
            if (!AtmosphereRain) return;

            RenderTexture light = DXManager.EnhancedLightTexture;
            bool hasLight = _atmosphereLightDrawn && light != null && !light.Disposed && light != DXManager.CurrentSurface;

            float intensity = _weatherFx.RainIntensity(CMain.Time);
            float camX = (User.Movement.X - OffSetX) * CellWidth - User.OffSetMove.X;
            float camY = (User.Movement.Y - OffSetY) * CellHeight - User.OffSetMove.Y;

            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, CMain.Time / 1000F, 0.5F * (0.3F + 0.7F * intensity));
            _atmoInputs[1] = new Vector4(camX, camY, _weatherFx.RainSlant, 0.07F * intensity);
            _atmoInputs[2] = new Vector4(_weatherFx.DriftUnit * 0.45F, 0.55F * (0.2F + 0.8F * intensity), hasLight ? 1F : 0F, 0F);
            DXManager.DrawFullscreen(FullscreenKind.Rainfall, hasLight ? light : null, _atmoInputs.AsSpan(0, 3));
        }

        /// <summary>After the rain: the lightning screen strobe (colour 170,195,255).</summary>
        private void DrawAtmosphereLightning()
        {
            if (!AtmosphereEnabled || _weatherFx == null) return;

            float flash = _weatherFx.StrikeFlash(CMain.Time);
            if (flash <= 0F) return;

            _atmoInputs[0] = new Vector4(170F / 255F, 195F / 255F, 1F, flash * 0.45F);
            DXManager.DrawFullscreen(FullscreenKind.Lightning, null, _atmoInputs.AsSpan(0, 1));
        }

        /// <summary>Body splashes and footfall spray over the map (with the dot texture); retires expired splashes.</summary>
        private void DrawAtmosphereSplashes()
        {
            if (_bodySplashes.Count == 0 && _footSplashes.Count == 0) return;
            EnsureSplashTextures();
            long now = CMain.Time;

            for (int i = _bodySplashes.Count - 1; i >= 0; i--)
            {
                BodySplash sp = _bodySplashes[i];
                float age = (now - sp.Born) / (float)BodySplashMs;
                if (age >= 1F || sp.Ob == null || sp.Ob.Dead) { _bodySplashes.RemoveAt(i); continue; }

                Point at = sp.Ob.DrawLocation;
                float x = at.X + sp.Ox, y = at.Y + sp.Oy, fade = 1F - age;
                int alpha = (int)(0.8F * fade * fade * 255F);
                DrawScaledSprite(_dotTexture, x - 2.5F, y - 1.5F, 5F, 3F, Color.FromArgb(alpha, 230, 240, 255));
                for (int side = -1; side <= 1; side += 2)
                {
                    float dx = side * (3F + 5F * sp.Seed) * age;
                    float dy = -8F * 4F * age * (1F - age) + 3F * age;
                    DrawScaledSprite(_dotTexture, x + dx - 1F, y + dy - 1F, 2F, 2F, Color.FromArgb((int)(0.85F * fade * 255F), 224, 235, 255));
                }
            }

            float camX = (User.Movement.X - OffSetX) * CellWidth - User.OffSetMove.X;
            float camY = (User.Movement.Y - OffSetY) * CellHeight - User.OffSetMove.Y;
            for (int i = _footSplashes.Count - 1; i >= 0; i--)
            {
                FootSplash s = _footSplashes[i];
                float age = (now - s.Born) / (float)FootSplashMs;
                if (age >= 1F) { _footSplashes.RemoveAt(i); continue; }

                float x = s.Wx - camX, y = s.Wy - camY;
                if (x < -40 || y < -60 || x > Size.Width + 40 || y > Size.Height + 40) continue;

                float fade = 1F - age, power = (0.6F + 0.4F * s.K) * (s.Run ? 1.3F : 1F);
                if (age < 0.3F)
                {
                    int alpha = (int)(0.55F * s.K * (1F - age / 0.3F) * 255F);
                    DrawScaledSprite(_dotTexture, x - 6F * power, y - 2.5F, 12F * power, 5F, Color.FromArgb(alpha, 230, 240, 255));
                }
                float t = Math.Min(1F, age / 0.75F); // droplets land at 75% of the life
                for (int j = 0; j < 8; j++)
                {
                    float h1 = s.Seed * 97.31F + j * 0.618F; h1 -= MathF.Floor(h1);
                    float h2 = s.Seed * 41.7F + j * 0.377F; h2 -= MathF.Floor(h2);
                    float spread = (j / 7F - 0.5F) * 2.4F + (h1 - 0.5F) * 0.5F;
                    float reach = (7F + 9F * h2) * power;
                    float ox = (MathF.Sin(spread) + s.Dx * 0.6F) * reach * t;
                    float oy = (MathF.Cos(spread) * 0.35F + s.Dy * 0.4F) * reach * t * 0.6F;
                    float height = (6F + 8F * h1) * power * 4F * t * (1F - t);
                    float size = 1.5F + h2;
                    int alpha = (int)(0.85F * s.K * fade * 255F);
                    DrawScaledSprite(_dotTexture, x + ox - size / 2F, y + oy - height - size / 2F, size, size, Color.FromArgb(alpha, 224, 235, 255));
                }
            }
        }

        // ---------------------------------------------------------------- reflections

        /// <summary>Renders the half-resolution object mirror and lamp reflections from the cached map and the wet-mask
        /// snapshot, then composites them over the screen.</summary>
        private void DrawAtmosphereReflections()
        {
            if (!Settings.Reflections || !AtmosphereEnabled || _weatherFx == null || !_weatherFx.Wet || !_snapshotOk) return;
            if (ControlTexture == null || ControlTexture.Disposed || DXManager.AtmosphereWetMask == null || DXManager.AtmosphereWetMask.Disposed) return;

            EnsureReflectTargets();

            float camX = (User.Movement.X - OffSetX) * CellWidth - User.OffSetMove.X;
            float camY = (User.Movement.Y - OffSetY) * CellHeight - User.OffSetMove.Y;
            float reflect = 0.4F + 0.6F * _weatherFx.Wetness;
            float time = CMain.Time / 1000F;

            // rain tint (atmosphere.ts RAIN_TINT at full strength)
            const float tintR = 0.86F, tintG = 0.89F, tintB = 0.94F;

            RenderTexture oldSurface = DXManager.CurrentSurface;

            // objects mirrored in puddles
            DXManager.SetSurface(DXManager.AtmosphereReflectMirror);
            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, time, 0F);
            _atmoInputs[1] = new Vector4(camX, camY, reflect, _ripplesNow);
            _atmoInputs[2] = new Vector4(tintR, tintG, tintB, 0F);
            DXManager.DrawFullscreen(FullscreenKind.ReflectObjects, ControlTexture, DXManager.AtmosphereWetMask, null, _atmoInputs.AsSpan(0, 3));

            // lamps (the strongest that can reach the screen)
            int count = PickReflections();
            DXManager.SetSurface(DXManager.AtmosphereReflectLamps);
            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, time, 1F);
            _atmoInputs[2] = new Vector4(tintR, tintG, tintB, count);
            _atmoInputs[3] = new Vector4(4F, 0F, 0F, 0F);          // gain, streak length, streak width, core gain
            _atmoInputs[4] = new Vector4(1.58F, 1F, 8F, 6F);       // flame gain, sheen gain, ripple push, wobble
            _atmoInputs[5] = new Vector4(0F, 1F, 14F, 0.35F);      // puddle low/high, contact fade, hot-white
            _atmoInputs[6] = new Vector4(0F, 0.46F, 2F, 0F);       // rough gain, halo gain, warmth
            int uniforms = 7 + count * 2;
            DXManager.DrawFullscreen(FullscreenKind.ReflectLamps, ControlTexture, DXManager.AtmosphereWetMask, null, _atmoInputs.AsSpan(0, uniforms));

            DXManager.SetSurface(oldSurface);

            // composite: mirror premultiplied (One, InverseSourceAlpha), then lamps added (One, One), linear upsample
            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, 0F, 0F);
            DXManager.DrawFullscreenBlend(FullscreenKind.BlitLinear, DXManager.AtmosphereReflectMirror, RenderBlend.One, RenderBlend.InverseSourceAlpha, _atmoInputs.AsSpan(0, 1));
            DXManager.DrawFullscreenBlend(FullscreenKind.BlitLinear, DXManager.AtmosphereReflectLamps, RenderBlend.One, RenderBlend.One, _atmoInputs.AsSpan(0, 1));
            DXManager.RestartSpriteAlphaBlend();
        }

        /// <summary>Fills the lamp uniforms (Atmo[7..]) from the recorded lights; returns how many reach the screen.</summary>
        private int PickReflections()
        {
            _reflectionPick.Clear();
            if (!_atmosphereLightDrawn) return 0; // no lamp pass this frame (Day): only the object mirror reflects

            int width = Size.Width, height = Size.Height;
            for (int i = 0; i < _reflectionSources.Count; i++)
            {
                ReflectionSource l = _reflectionSources[i];
                float mirrorY = 2F * l.ContactY - l.EmitterY;
                float reach = 4F * (4F + 0F) + 100F; // 4 * (4 + streak length) + 100
                if (l.X < -l.Radius || l.X > width + l.Radius || mirrorY + reach < 0F || l.ContactY > height + 4F || l.R + l.G + l.B <= 0.01F) continue;
                _reflectionPick.Add(l);
            }

            int count = Math.Min(_reflectionPick.Count, 48);
            for (int i = 0; i < count; i++)
            {
                ReflectionSource l = _reflectionPick[i];
                int pos = 7 + i * 2;
                _atmoInputs[pos] = new Vector4(l.X, 2F * l.ContactY - l.EmitterY, l.ContactY, l.Radius);
                _atmoInputs[pos + 1] = new Vector4(l.R, l.G, l.B, l.EmitterY);
            }
            return count;
        }

        private void EnsureReflectTargets()
        {
            int width = Math.Max(1, (Settings.ScreenWidth + 1) / 2), height = Math.Max(1, (Settings.ScreenHeight + 1) / 2);
            if (DXManager.AtmosphereReflectMirror == null || DXManager.AtmosphereReflectMirror.Disposed ||
                DXManager.AtmosphereReflectMirror.Width != width || DXManager.AtmosphereReflectMirror.Height != height)
            {
                if (DXManager.AtmosphereReflectMirror != null && !DXManager.AtmosphereReflectMirror.Disposed) DXManager.AtmosphereReflectMirror.Dispose();
                DXManager.AtmosphereReflectMirror = DXManager.CreateRenderTarget(width, height, RenderTargetFormat.Rgba8);
            }
            if (DXManager.AtmosphereReflectLamps == null || DXManager.AtmosphereReflectLamps.Disposed ||
                DXManager.AtmosphereReflectLamps.Width != width || DXManager.AtmosphereReflectLamps.Height != height)
            {
                if (DXManager.AtmosphereReflectLamps != null && !DXManager.AtmosphereReflectLamps.Disposed) DXManager.AtmosphereReflectLamps.Dispose();
                DXManager.AtmosphereReflectLamps = DXManager.CreateRenderTarget(width, height, RenderTargetFormat.Rgba16F);
            }
        }

        // ---------------------------------------------------------------- textures

        /// <summary>16 px soft dot and 48 px soft ring, generated once (white, BGRA, soft alpha).</summary>
        private void EnsureSplashTextures()
        {
            if (_dotTexture != null && !_dotTexture.Disposed && _ringTexture != null && !_ringTexture.Disposed) return;
            if (_dotTexture == null || _dotTexture.Disposed)
            {
                _dotTexture = DXManager.CreateTexture(16, 16);
                UploadTexture(_dotTexture, SoftDot(16));
            }
            if (_ringTexture == null || _ringTexture.Disposed)
            {
                _ringTexture = DXManager.CreateTexture(48, 48);
                UploadTexture(_ringTexture, SoftRing(48));
            }
        }

        private static byte[] SoftDot(int size)
        {
            byte[] data = new byte[size * size * 4];
            float c = (size - 1) / 2F;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 0.5F);
                    float a = Math.Max(0F, 1F - d * d);
                    int i = (y * size + x) * 4;
                    data[i] = data[i + 1] = data[i + 2] = 255;
                    data[i + 3] = (byte)Math.Round(a * a * 255F);
                }
            return data;
        }

        private static byte[] SoftRing(int size)
        {
            byte[] data = new byte[size * size * 4];
            float c = (size - 1) / 2F;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (c + 0.5F);
                    float ring = Math.Abs(d - 0.8F) / 0.14F;
                    int i = (y * size + x) * 4;
                    data[i] = data[i + 1] = data[i + 2] = 255;
                    data[i + 3] = (byte)Math.Round(Math.Max(0F, 1F - ring * ring) * 255F);
                }
            return data;
        }

        private static unsafe void UploadTexture(RenderTexture texture, byte[] data)
        {
            byte* dst = DXManager.LockTexture(texture);
            int pitch = texture.Width * 4;
            for (int y = 0; y < texture.Height; y++)
                Marshal.Copy(data, y * pitch, (IntPtr)(dst + y * pitch), pitch);
            DXManager.UnlockTexture(texture);
        }

        private static void DrawScaledSprite(RenderTexture texture, float x, float y, float width, float height, Color colour)
        {
            if (texture == null || texture.Disposed || width <= 0F || height <= 0F) return;
            float scaleX = width / texture.Width, scaleY = height / texture.Height;
            DXManager.SetSpriteTransform(Matrix.Scaling(scaleX, scaleY, 0F));
            DXManager.Draw(texture, new Rectangle(0, 0, texture.Width, texture.Height), new Vector3(x / scaleX, y / scaleY, 0F), colour);
            DXManager.SetSpriteTransform(Matrix.Identity);
        }
    }
}
