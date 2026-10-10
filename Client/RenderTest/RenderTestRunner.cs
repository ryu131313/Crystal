using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirObjects;
using SlimDX;

namespace Client.RenderTest
{
    /// <summary>
    /// Golden render-test harness (roadmap C1). <c>Client.exe --rendertest --out &lt;dir&gt;</c> renders a fixed set of
    /// scenarios through the production DXManager / MLibrary / MirControl paths into a 1024x768 render target and writes
    /// each as raw BGRA (<c>.bgra</c>, exact bytes) plus a viewing PNG (alpha forced opaque). <c>--bench N</c> also renders
    /// every scenario N times to the back buffer with Present and reports per-scenario frame times.
    /// <c>Client.exe --rendercompare &lt;goldenDir&gt; &lt;candidateDir&gt; [--parity]</c> compares two runs.
    ///
    /// Test mode only: windowed 1024x768, no vsync, volume 0, no launcher, no network, Settings are never saved. Run it
    /// from a scratch folder whose Data/Map/Sound point at the client data (tools\rendertest.ps1 does this).
    /// </summary>
    internal static class RenderTestRunner
    {
        public const int Width = 1024, Height = 768;

        public static bool Enabled { get; private set; }
        /// <summary>--smoke N: run the real game loop (login scene) for N seconds, then capture and exit.</summary>
        public static bool SmokeMode { get; private set; }
        public static int SmokeSeconds;
        public static bool SmokeFullScreen;
        /// <summary>--qa (with --smoke): scripted QA steps on the real window (resolution, Alt+Enter fullscreen, minimise).</summary>
        public static bool QaMode;
        public static bool CompareMode { get; private set; }
        public static string OutDir = Path.GetFullPath("RenderTestOut");
        public static int BenchFrames;
        public static string CompareA, CompareB;
        public static bool ParityMode;
        /// <summary>--waive Name:N — a scenario may differ in at most N pixels (documented edge cases only).</summary>
        public static readonly Dictionary<string, int> Waivers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<string> _log = new List<string>();

        /// <summary>Reads the render-test switches. Returns true when the process runs in a test mode.</summary>
        public static bool ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--rendertest": Enabled = true; break;
                    case "--out": if (i + 1 < args.Length) OutDir = Path.GetFullPath(args[++i]); break;
                    case "--bench": if (i + 1 < args.Length) int.TryParse(args[++i], out BenchFrames); break;
                    case "--rendercompare":
                        CompareMode = true;
                        if (i + 2 < args.Length) { CompareA = args[++i]; CompareB = args[++i]; }
                        break;
                    case "--parity": ParityMode = true; break;
                    case "--smoke":
                        SmokeMode = true;
                        if (i + 1 < args.Length) int.TryParse(args[++i], out SmokeSeconds);
                        if (SmokeSeconds <= 0) SmokeSeconds = 10;
                        break;
                    case "--fullscreen": SmokeFullScreen = true; break;
                    case "--qa": QaMode = true; break;
                    case "--waive":
                        if (i + 1 < args.Length)
                        {
                            var parts = args[++i].Split(':');
                            if (parts.Length == 2 && int.TryParse(parts[1], out int n)) Waivers[parts[0]] = n;
                        }
                        break;
                }
            }
            return Enabled || CompareMode || SmokeMode;
        }

        /// <summary>Test-mode settings: applied after Settings.Load, never saved.</summary>
        public static void ApplySettings()
        {
            // Never reach a real server: port 1 on loopback is always refused (WoOOLcraft and others listen on 7000).
            Settings.IPAddress = "127.0.0.1";
            Settings.Port = 1;
            Settings.FullScreen = SmokeMode && SmokeFullScreen;
            Settings.Borderless = false;
            Settings.FPSCap = false;
            Settings.ScreenWidth = Width;
            Settings.ScreenHeight = Height;
            Settings.Volume = 0;
            Settings.MusicVolume = 0;
            Settings.P_Patcher = false;
        }

        private static void Log(string line)
        {
            _log.Add(line);
        }

        // ------------------------------------------------------------------ scenarios

        private sealed class Scenario
        {
            public string Name;
            public Action Draw;
            public bool ResetFirst;     // DXManager.ResetDevice() before rendering (device reset / swap-chain resize path)
        }

        private static List<Scenario> BuildScenarios()
        {
            return new List<Scenario>
            {
                new Scenario { Name = "S01_floor", Draw = DrawFloorScenario },
                new Scenario { Name = "S02_objects", Draw = DrawObjectsScenario },
                new Scenario { Name = "S03_blend_additive", Draw = DrawAdditiveScenario },
                new Scenario { Name = "S04_blend_invlight", Draw = DrawInvLightScenario },
                new Scenario { Name = "S05_opacity", Draw = DrawOpacityScenario },
                new Scenario { Name = "S06_grayscale", Draw = DrawGrayscaleScenario },
                new Scenario { Name = "S07_tinted", Draw = DrawTintedScenario },
                new Scenario { Name = "S08_lights", Draw = DrawLightsScenario },
                new Scenario { Name = "S09_scaled", Draw = DrawScaledScenario },
                new Scenario { Name = "S10_ui", Draw = DrawUiScenario },
                new Scenario { Name = "S11_stress_order", Draw = DrawStressScenario },
                new Scenario { Name = "S12_primitives", Draw = DrawPrimitivesScenario },
                new Scenario { Name = "S13_state_quirks", Draw = DrawStateQuirksScenario },
                new Scenario { Name = "S14_render_targets", Draw = DrawRenderTargetsScenario },
                new Scenario { Name = "S15_after_reset", Draw = DrawAfterResetScenario, ResetFirst = true },
            };
        }

        private const int CellWidth = 48, CellHeight = 32;
        private static MapReader _map;
        private static Point _mapCentre;

        private static MapReader Map
        {
            get
            {
                if (_map != null) return _map;
                var path = Path.Combine(Settings.MapPath, "0.map");
                if (!File.Exists(path)) return null;
                _map = new MapReader(path);
                _mapCentre = new Point(_map.Width / 2, _map.Height / 2);
                return _map;
            }
        }

        private static MLibrary MapLib(int index)
        {
            if (index < 0 || index >= Libraries.MapLibs.Length) return null;
            return Libraries.MapLibs[index];
        }

        private static void DrawMapTiles(bool objects)
        {
            var map = Map;
            if (map == null) return;
            int cols = Width / CellWidth + 2, rows = Height / CellHeight + 2;
            int startX = _mapCentre.X - cols / 2, startY = _mapCentre.Y - rows / 2;

            for (int y = startY; y < startY + rows; y++)
            {
                if (y <= 0 || y >= map.Height) continue;
                for (int x = startX; x < startX + cols; x++)
                {
                    if (x < 0 || x >= map.Width) continue;
                    var cell = map.MapCells[x, y];
                    int drawX = (x - startX) * CellWidth, drawY = (y - startY) * CellHeight;

                    if (!objects)
                    {
                        if (y % 2 == 0 && x % 2 == 0 && cell.BackImage != 0 && cell.BackIndex != -1)
                            MapLib(cell.BackIndex)?.Draw((cell.BackImage & 0x1FFFFFFF) - 1, drawX, drawY);

                        int mid = cell.MiddleImage - 1;
                        if (mid >= 0 && cell.MiddleIndex != -1)
                        {
                            var lib = MapLib(cell.MiddleIndex);
                            if (lib != null && IsTileSize(lib.GetSize(mid))) lib.Draw(mid, drawX, drawY);
                        }
                    }

                    int front = (cell.FrontImage & 0x7FFF) - 1;
                    if (front >= 0 && cell.FrontIndex != -1 && cell.FrontIndex != 200)
                    {
                        var lib = MapLib(cell.FrontIndex);
                        if (lib == null) continue;
                        var s = lib.GetSize(front);
                        if (!objects && IsTileSize(s)) lib.Draw(front, drawX, drawY);
                        else if (objects && !IsTileSize(s) && s.Width > 0) lib.Draw(front, drawX, drawY + CellHeight - s.Height);
                    }
                }
            }
        }

        private static bool IsTileSize(Size s) =>
            (s.Width == CellWidth && s.Height == CellHeight) || (s.Width == CellWidth * 2 && s.Height == CellHeight * 2);

        /// <summary>First <paramref name="count"/> indices at or after <paramref name="start"/> that have a real image.</summary>
        private static List<int> ValidIndices(MLibrary lib, int start, int count, int scanLimit = 4000)
        {
            var list = new List<int>();
            if (lib == null) return list;
            for (int i = start; i < start + scanLimit && list.Count < count; i++)
            {
                var s = lib.GetSize(i);
                if (s.Width > 4 && s.Height > 4) list.Add(i);
            }
            return list;
        }

        private static RenderTexture _floorRt;

        private static void DrawFloorScenario()
        {
            // Floor into its own render target first, then blit: the MapControl FloorTexture pattern.
            if (_floorRt == null) _floorRt = DXManager.CreateRenderTarget(Width, Height);
            RenderTexture old = DXManager.CurrentSurface;
            DXManager.SetSurface(_floorRt);
            DXManager.Clear(Color.Empty);
            DrawMapTiles(false);
            DXManager.SetSurface(old);
            DXManager.Draw(_floorRt, new Rectangle(0, 0, Width, Height), Vector3.Zero, Color.White);
        }

        private static void DrawObjectsScenario()
        {
            DrawFloorScenario();
            DrawMapTiles(true);

            int x = 60;
            for (int m = 0; m < Math.Min(Libraries.Monsters.Length, 40) && x < Width - 60; m += 3)
            {
                var frames = ValidIndices(Libraries.Monsters[m], 0, 1, 200);
                if (frames.Count == 0) continue;
                Libraries.Monsters[m].Draw(frames[0], new Point(x, 300 + (m % 4) * 90), Color.White, true);
                x += 70;
            }
        }

        private static void DrawAdditiveScenario()
        {
            DrawFloorScenario();
            var effects = ValidIndices(Libraries.Magic, 0, 24);
            for (int i = 0; i < effects.Count; i++)
            {
                var p = new Point(30 + (i % 8) * 120, 40 + (i / 8) * 230);
                Libraries.Magic.DrawBlend(effects[i], p, Color.White, true, i % 2 == 0 ? 1F : 0.3F);
            }
            var effects2 = ValidIndices(Libraries.Magic2, 0, 6);
            for (int i = 0; i < effects2.Count; i++)
                Libraries.Magic2.DrawUpBlend(effects2[i], new Point(80 + i * 150, 760));
        }

        private static void DrawInvLightScenario()
        {
            DrawFloorScenario();
            var effects = ValidIndices(Libraries.Magic3, 0, 12);
            DXManager.SetBlend(true, 0.5F, BlendMode.INVLIGHT);
            for (int i = 0; i < effects.Count; i++)
                Libraries.Magic3.Draw(effects[i], new Point(30 + (i % 6) * 160, 60 + (i / 6) * 300), Color.White, true);
            DXManager.SetBlend(true, 0.8F, BlendMode.INVLIGHT);
            for (int i = 0; i < effects.Count; i++)
                Libraries.Magic3.Draw(effects[i], new Point(90 + (i % 6) * 160, 160 + (i / 6) * 300), Color.FromArgb(255, 200, 180, 255), true);
            DXManager.SetBlend(false);
        }

        private static void DrawOpacityScenario()
        {
            DrawFloorScenario();
            var sprites = ValidIndices(Libraries.Prguse, 0, 12);

            DXManager.SetOpacity(0.4F);
            for (int i = 0; i < Math.Min(4, sprites.Count); i++)
                Libraries.Prguse.Draw(sprites[i], new Point(20 + i * 200, 20), Color.White, false);

            // D3D9 quirk captured on purpose: SetBlend(false) restarts the sprite with AlphaBlend and so drops the opacity
            // states while DXManager.Opacity still says 0.4. A backend must reproduce this exactly.
            var fx = ValidIndices(Libraries.Magic, 0, 2);
            if (fx.Count > 0) Libraries.Magic.DrawBlend(fx[0], new Point(500, 200), Color.White, true, 1F);
            for (int i = 4; i < Math.Min(8, sprites.Count); i++)
                Libraries.Prguse.Draw(sprites[i], new Point(20 + (i - 4) * 200, 300), Color.White, false);

            DXManager.SetOpacity(1F);
            DXManager.SetOpacity(0.7F);
            for (int i = 8; i < sprites.Count; i++)
                Libraries.Prguse.Draw(sprites[i], new Point(20 + (i - 8) * 200, 520), Color.White, false);
            DXManager.SetOpacity(1F);

            // Per-vertex alpha (DrawOpaque path).
            for (int i = 0; i < Math.Min(4, sprites.Count); i++)
                Libraries.Prguse.Draw(sprites[i], new Point(600 + i * 40, 600), Color.White, false, 0.5F);
        }

        private static void DrawGrayscaleScenario()
        {
            DrawObjectsScenario();
            DXManager.SetGrayscale(true);
            DXManager.Draw(_floorRt, new Rectangle(0, 0, Width / 2, Height), Vector3.Zero, Color.White);
            var sprites = ValidIndices(Libraries.Prguse, 0, 4);
            for (int i = 0; i < sprites.Count; i++)
                Libraries.Prguse.Draw(sprites[i], new Point(560 + i * 100, 100 + i * 120), Color.FromArgb(200, 255, 128, 64), false);
            DXManager.SetGrayscale(false);
        }

        private static void DrawTintedScenario()
        {
            DrawFloorScenario();
            if (Libraries.CArmours == null) return;
            int x = 40;
            for (int a = 0; a < Libraries.CArmours.Length && x < Width - 80; a++)
            {
                var frames = ValidIndices(Libraries.CArmours[a], 0, 2, 300);
                foreach (var f in frames)
                {
                    Libraries.CArmours[a].DrawTinted(f, new Point(x, 380), Color.White, Color.FromArgb(255, 255, 60, 60), true);
                    x += 90;
                }
            }
        }

        private static RenderTexture _lightRt;

        private static void DrawLightsScenario()
        {
            DrawObjectsScenario();
            if (DXManager.Lights == null || DXManager.Lights.Count == 0) return;

            // Same sequence as MapControl.DrawLights (raw states included).
            if (_lightRt == null) _lightRt = DXManager.CreateRenderTarget(Width, Height);
            RenderTexture old = DXManager.CurrentSurface;
            DXManager.SetSurface(_lightRt);
            DXManager.Clear(Color.FromArgb(255, 20, 20, 20));

            DXManager.SetBlend(true);
            DXManager.SetLightAdditiveBlend();
            Color[] tints = { Color.White, Color.FromArgb(255, 255, 220, 160), Color.FromArgb(255, 120, 160, 255), Color.FromArgb(255, 255, 80, 80) };
            for (int i = 0; i < 8; i++)
            {
                int l = (i * 3) % DXManager.Lights.Count;
                var p = new Point(60 + i * 115 - DXManager.LightSizes[l].X / 4, 200 + (i % 3) * 140 - DXManager.LightSizes[l].Y / 4);
                DXManager.Draw(DXManager.Lights[l], null, new Vector3(p.X, p.Y, 0F), tints[i % tints.Length]);
            }
            DXManager.SetBlend(false);
            DXManager.SetSurface(old);

            DXManager.SetLightMultiplyBlend();
            DXManager.Draw(_lightRt, new Rectangle(0, 0, Width, Height), Vector3.Zero, Color.White);
            DXManager.RestartSpriteAlphaBlend();
        }

        private static void DrawScaledScenario()
        {
            var imgs = ValidIndices(Libraries.Prguse, 0, 6);
            int x = 10;
            float[] scales = { 2F, 0.5F, 1.5F, 0.75F, 3F, 1.25F };
            for (int i = 0; i < imgs.Count; i++)
            {
                var s = Libraries.Prguse.GetSize(imgs[i]);
                var size = new Size(Math.Max(1, (int)(s.Width * scales[i])), Math.Max(1, (int)(s.Height * scales[i] * (i % 2 == 0 ? 1F : 0.6F))));
                Libraries.Prguse.Draw(imgs[i], new Point(x, 20 + (i % 2) * 360), size, Color.White);
                x += Math.Min(300, size.Width / 2 + 60);
            }
        }

        private sealed class TestScene : MirScene
        {
            public override void Process() { }
        }

        private static void DrawUiScenario()
        {
            var scene = new TestScene { BackColour = Color.FromArgb(255, 30, 30, 40) };
            var imgs = ValidIndices(Libraries.Prguse, 0, 6);
            int x = 10;
            foreach (var idx in imgs)
            {
                new MirImageControl { Parent = scene, Library = Libraries.Prguse, Index = idx, Location = new Point(x, 10) };
                x += Math.Min(320, Libraries.Prguse.GetSize(idx).Width + 10);
            }

            var panel = new MirControl
            {
                Parent = scene, Location = new Point(40, 420), Size = new Size(420, 260), BackColour = Color.FromArgb(255, 60, 20, 20),
                DrawControlTexture = true, Border = true, BorderColour = Color.Gold, Opacity = 0.8F
            };
            new MirLabel { Parent = panel, Location = new Point(10, 10), AutoSize = true, ForeColour = Color.White, Text = "Crystal render test - 0123456789" };
            new MirLabel { Parent = panel, Location = new Point(10, 40), Size = new Size(380, 80), ForeColour = Color.Yellow, OutLine = false,
                Text = "Word wrapped text without an outline, drawn through GDI into a locked texture and blitted." };
            new MirControl { Parent = panel, Location = new Point(20, 140), Size = new Size(120, 60), BackColour = Color.FromArgb(255, 0, 120, 200),
                DrawControlTexture = true, Border = true, BorderColour = Color.White };
            new MirControl { Parent = scene, Location = new Point(520, 420), Size = new Size(300, 200), BackColour = Color.FromArgb(255, 200, 200, 200),
                DrawControlTexture = true, Opacity = 0.4F, Border = true, BorderColour = Color.Red };

            scene.Draw();
            scene.Dispose();
        }

        private static void DrawStressScenario()
        {
            // Many overlapping sprites from different textures with blend switches in between: catches any reordering.
            var a = ValidIndices(Libraries.Prguse, 0, 10);
            var b = ValidIndices(Libraries.Magic, 0, 10);
            int n = 0;
            for (int i = 0; i < 300; i++)
            {
                int px = (i * 37) % (Width - 100), py = (i * 53) % (Height - 100);
                if (i % 25 == 24 && b.Count > 0)
                    Libraries.Magic.DrawBlend(b[n++ % b.Count], new Point(px, py), Color.White, true, 0.6F);
                else if (a.Count > 0)
                    Libraries.Prguse.Draw(a[i % a.Count], new Point(px, py), Color.FromArgb(255, 255 - i % 200, 128 + i % 127, 255), false);
            }
        }

        private static void DrawPrimitivesScenario()
        {
            // Radar/poison dots (2x2 and 5x5 white textures) and control borders (ID3DXLine).
            for (int i = 0; i < 40; i++)
            {
                DXManager.Draw(DXManager.RadarTexture, new Rectangle(0, 0, 2, 2), new Vector3(10 + i * 25 - 0.5F, 10.5F, 0F), Color.FromArgb(255, 255, i * 6, 0));
                DXManager.Draw(DXManager.PoisonDotBackground, new Rectangle(0, 0, 6, 6), new Vector3(10 + i * 25, 40, 0F), Color.Black);
                DXManager.Draw(DXManager.RadarTexture, new Rectangle(0, 0, 4, 4), new Vector3(11 + i * 25, 41, 0F), Color.Lime);
            }
            var scene = new TestScene { BackColour = Color.Transparent };
            for (int i = 0; i < 6; i++)
                new MirControl { Parent = scene, Location = new Point(40 + i * 160, 120 + i * 30), Size = new Size(120 + i * 7, 80 + i * 11),
                    Border = true, BorderColour = Color.FromArgb(255, 40 * i, 255 - 40 * i, 128), DrawControlTexture = true, BackColour = Color.FromArgb(255, 20, 20, 20) };
            scene.Draw();
            scene.Dispose();
        }

        private static void DrawStateQuirksScenario()
        {
            DrawFloorScenario();
            var sprites = ValidIndices(Libraries.Prguse, 0, 6);
            var fx = ValidIndices(Libraries.Magic, 0, 3);
            if (sprites.Count < 6 || fx.Count < 3) return;

            // 1. Grayscale across a blend switch: SetBlend restarts the sprite (does Begin drop the pixel shader?).
            DXManager.SetGrayscale(true);
            Libraries.Prguse.Draw(sprites[0], new Point(10, 10), Color.White, false);
            Libraries.Magic.DrawBlend(fx[0], new Point(300, 60), Color.White, true, 1F);
            Libraries.Prguse.Draw(sprites[1], new Point(10, 200), Color.White, false);
            DXManager.SetGrayscale(false);

            // 2. Opacity + grayscale, then a sprite restart in AlphaBlend mode (End restores the state saved at Begin).
            DXManager.SetOpacity(0.5F);
            DXManager.SetGrayscale(true);
            Libraries.Prguse.Draw(sprites[2], new Point(520, 10), Color.White, false);
            DXManager.RestartSpriteAlphaBlend();
            Libraries.Prguse.Draw(sprites[3], new Point(520, 200), Color.White, false);
            DXManager.SetGrayscale(false);
            DXManager.SetOpacity(1F);

            // 3. Opacity changed while blending, then blending off.
            DXManager.SetBlend(true, 0.3F);
            Libraries.Magic.Draw(fx[1], new Point(100, 420), Color.White, true);
            DXManager.SetOpacity(0.6F);
            Libraries.Magic.Draw(fx[2], new Point(400, 420), Color.White, true);
            DXManager.SetBlend(false);
            Libraries.Prguse.Draw(sprites[4], new Point(700, 420), Color.White, false);
            DXManager.SetOpacity(1F);

            // 4. INVLIGHT and additive back to back with different rates.
            DXManager.SetBlend(true, 0.4F, BlendMode.INVLIGHT);
            Libraries.Magic.Draw(fx[0], new Point(100, 560), Color.White, true);
            DXManager.SetBlend(true, 0.9F);
            Libraries.Magic.Draw(fx[1], new Point(350, 560), Color.White, true);
            DXManager.SetBlend(true, 0.9F, BlendMode.INVLIGHT);
            Libraries.Magic.Draw(fx[2], new Point(600, 560), Color.Yellow, true);
            DXManager.SetBlend(false);

            // 5. Grayscale set inside a DoNotSaveState sprite, then a blend-rate change: End does not restore, so only
            //    Begin itself can drop the pixel shader (the discriminating case for that D3DX assumption).
            DXManager.SetBlend(true, 0.5F);
            DXManager.SetGrayscale(true);
            Libraries.Magic.Draw(fx[0], new Point(700, 40), Color.White, true);
            DXManager.SetBlend(true, 0.8F);
            Libraries.Magic.Draw(fx[1], new Point(850, 40), Color.White, true);
            DXManager.SetBlend(false);
            DXManager.SetGrayscale(false);
        }

        private static RenderTexture _rtA, _rtB;

        private static void DrawRenderTargetsScenario()
        {
            var sprites = ValidIndices(Libraries.Prguse, 0, 4);
            var fx = ValidIndices(Libraries.Magic, 0, 2);
            if (_rtA == null) { _rtA = DXManager.CreateRenderTarget(300, 200); _rtB = DXManager.CreateRenderTarget(500, 400); }

            RenderTexture old = DXManager.CurrentSurface;
            // A: translucent clear, sprites, additive effect.
            DXManager.SetSurface(_rtA);
            DXManager.Clear(Color.FromArgb(128, 0, 64, 128));
            if (sprites.Count > 0) Libraries.Prguse.Draw(sprites[0], new Point(-20, -10), Color.White, false);
            if (fx.Count > 0) Libraries.Magic.DrawBlend(fx[0], new Point(100, 40), Color.White, true, 0.7F);
            // B: transparent clear, A drawn twice (opaque and with vertex alpha), a sprite on top.
            DXManager.SetSurface(_rtB);
            DXManager.Clear(Color.Empty);
            DXManager.Draw(_rtA, new Rectangle(0, 0, 300, 200), new SlimDX.Vector3(10, 10, 0), Color.White);
            DXManager.DrawOpaque(_rtA, new Rectangle(50, 50, 200, 100), new SlimDX.Vector3(150, 220, 0), Color.White, 0.5F);
            if (sprites.Count > 1) Libraries.Prguse.Draw(sprites[1], new Point(200, 100), Color.FromArgb(255, 255, 200, 200), false);
            // Back to the frame: draw B with SetOpacity, then A with grayscale.
            DXManager.SetSurface(old);
            DXManager.Clear(Color.FromArgb(255, 40, 40, 40));
            DXManager.SetOpacity(0.75F);
            DXManager.Draw(_rtB, new Rectangle(0, 0, 500, 400), new SlimDX.Vector3(20, 20, 0), Color.White);
            DXManager.SetOpacity(1F);
            DXManager.SetGrayscale(true);
            DXManager.Draw(_rtA, new Rectangle(0, 0, 300, 200), new SlimDX.Vector3(600, 400, 0), Color.White);
            DXManager.SetGrayscale(false);
        }

        private static void DrawAfterResetScenario()
        {
            // Everything (library textures, lights, control textures) was released and re-created by ResetDevice.
            DrawLightsScenario();
            DrawUiScenario();
        }

        /// <summary>Releases the harness's own render targets, resets the device (D3D9 Reset / D3D11 ResizeBuffers) and recreates them.</summary>
        private static void ResetDevice()
        {
            _target?.Dispose(); _target = null;
            _floorRt?.Dispose(); _floorRt = null;
            _lightRt?.Dispose(); _lightRt = null;
            _rtA?.Dispose(); _rtA = null;
            _rtB?.Dispose(); _rtB = null;
            DXManager.ResetDevice();
            DXManager.DeviceLost = false;
            _target = DXManager.CreateRenderTarget(Width, Height);
        }

        // ------------------------------------------------------------------ run

        private static RenderTexture _target;

        /// <summary>Runs the scenarios (and the benchmark) and returns the process exit code.</summary>
        public static int Run()
        {
            try
            {
                Directory.CreateDirectory(OutDir);
                var sw = Stopwatch.StartNew();
                while (!Libraries.Loaded && sw.ElapsedMilliseconds < 180000) Thread.Sleep(20); // test mode only, never in the game loop
                if (!Libraries.Loaded) { Log("FATAL libraries did not load"); return 2; }
                Log($"backend {DXManager.RendererType} (configured {Settings.Renderer}) libraries loaded in {sw.ElapsedMilliseconds} ms");

                _target = DXManager.CreateRenderTarget(Width, Height);

                var scenarios = BuildScenarios();
                foreach (var s in scenarios)
                {
                    if (s.ResetFirst) ResetDevice();
                    // Render twice so first-use texture uploads cannot influence the captured frame.
                    RenderScenario(s, toTarget: true);
                    RenderScenario(s, toTarget: true);
                    var bytes = DXManager.Backend.CaptureRenderTarget(_target);
                    File.WriteAllBytes(Path.Combine(OutDir, s.Name + ".bgra"), bytes);
                    SavePng(bytes, Path.Combine(OutDir, s.Name + ".png"));
                    Log($"{s.Name} captured sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}");
                }

                if (BenchFrames > 0)
                {
                    long memBefore = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
                    for (int round = 0; round < 3; round++)
                    {
                        // Churn: release every library/control texture and the harness targets, then rebuild (as Clean + ResetDevice do in play).
                        ResetDevice();
                        foreach (var s in scenarios) { RenderScenario(s, toTarget: true); }
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    long memAfterChurn = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
                    Log($"churn 3 rounds of reset + all scenarios: private {memBefore / (1024 * 1024)} MB -> {memAfterChurn / (1024 * 1024)} MB");

                    foreach (var s in scenarios)
                    {
                        var times = new double[BenchFrames];
                        for (int i = 0; i < BenchFrames; i++)
                        {
                            long t0 = Stopwatch.GetTimestamp();
                            RenderScenario(s, toTarget: false);
                            DXManager.Present();
                            times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        }
                        Array.Sort(times);
                        Log($"bench {s.Name} frames={BenchFrames} avg={times.Average():0.000}ms p50={times[BenchFrames / 2]:0.000}ms p99={times[Math.Min(BenchFrames - 1, BenchFrames * 99 / 100)]:0.000}ms max={times[^1]:0.000}ms");
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    Log($"after bench: private {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024)} MB");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("FATAL " + ex);
                return 2;
            }
            finally
            {
                try { File.WriteAllLines(Path.Combine(OutDir, "rendertest.log"), _log); } catch { }
                _target?.Dispose(); _floorRt?.Dispose(); _lightRt?.Dispose(); _rtA?.Dispose(); _rtB?.Dispose();
            }
        }

        private static void RenderScenario(Scenario s, bool toTarget)
        {
            DXManager.BeginFrame();
            if (toTarget)
            {
                DXManager.SetSurface(_target);
                DXManager.Clear(Color.Black);
            }

            s.Draw();

            DXManager.SetOpacity(1F);
            DXManager.SetBlend(false);
            DXManager.SetGrayscale(false);
            DXManager.SetSurface(DXManager.MainSurface);
            DXManager.EndFrame();
        }

        private static void SavePng(byte[] bgra, string path)
        {
            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var copy = (byte[])bgra.Clone();
            for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
            for (int y = 0; y < Height; y++)
                Marshal.Copy(copy, y * Width * 4, data.Scan0 + y * data.Stride, Width * 4);
            bmp.UnlockBits(data);
            bmp.Save(path, ImageFormat.Png);
        }

        public static bool MidSmokeDone;

        // ------------------------------------------------------------------ scripted QA (roadmap C4)

        private sealed class QaStep
        {
            public long AtMs;
            public string Name;
            public Action Act;
            public Size ExpectBackBuffer;   // screenshot steps only
        }

        private static List<QaStep> _qaSteps;
        private static int _qaNext;
        private static int _qaFailures;

        private static List<QaStep> BuildQaSteps()
        {
            bool d3d11 = DXManager.RendererType == RendererType.D3D11;
            var steps = new List<QaStep>();
            long t = 2000;
            void Act(string name, Action act) { steps.Add(new QaStep { AtMs = t, Name = name, Act = act }); t += 1200; }
            void Shot(string name, Size expect) { steps.Add(new QaStep { AtMs = t, Name = name, ExpectBackBuffer = expect }); t += 800; }

            Shot("01_login_1024", new Size(1024, 768));
            Act("resolution 1280x800", () => CMain.SetResolution(1280, 800));
            Shot("02_1280x800", new Size(1280, 800));
            Act("resolution 1366x768", () => CMain.SetResolution(1366, 768));
            Shot("03_1366x768", new Size(1366, 768));
            Act("resolution 1920x1080", () => CMain.SetResolution(1920, 1080));
            Shot("04_1920x1080", new Size(1920, 1080));
            Act("resolution 1024x768", () => CMain.SetResolution(1024, 768));
            Shot("05_back_1024", new Size(1024, 768));
            if (d3d11)
            {
                // Borderless fullscreen only: a D3D9 exclusive-mode switch would change this desktop's display mode.
                Act("Alt+Enter to fullscreen", CMain.ToggleFullScreen);
                Shot("06_fullscreen", new Size(1024, 768));
                Act("resolution 1280x800 in fullscreen", () => CMain.SetResolution(1280, 800));
                Shot("07_fullscreen_1280x800", new Size(1280, 800));
                Act("resolution 1024x768 in fullscreen", () => CMain.SetResolution(1024, 768));
                Act("Alt+Enter to windowed", CMain.ToggleFullScreen);
                Shot("08_windowed_again", new Size(1024, 768));
            }
            Act("minimise", () => Program.Form.WindowState = FormWindowState.Minimized);
            Act("restore", () => Program.Form.WindowState = FormWindowState.Normal);
            Shot("09_restored", new Size(1024, 768));
            return steps;
        }

        /// <summary>Runs due QA steps from the real game loop (UI thread).</summary>
        public static void QaTick(long time)
        {
            if (_qaSteps == null) _qaSteps = BuildQaSteps();
            while (_qaNext < _qaSteps.Count && time >= _qaSteps[_qaNext].AtMs)
            {
                var step = _qaSteps[_qaNext++];
                try
                {
                    if (step.Act != null)
                    {
                        step.Act();
                        _log.Add($"qa {step.Name}: done (client {Program.Form.ClientSize}, deviceLost={DXManager.DeviceLost})");
                        continue;
                    }

                    var size = new Size(DXManager.MainSurface?.Width ?? 0, DXManager.MainSurface?.Height ?? 0);
                    using var bmp = DXManager.CaptureBackBuffer();
                    Directory.CreateDirectory(OutDir);
                    bmp.Save(Path.Combine(OutDir, "qa-" + step.Name + ".png"), ImageFormat.Png);
                    double mean = MeanBrightness(bmp);
                    bool ok = size == step.ExpectBackBuffer && bmp.Width == step.ExpectBackBuffer.Width && mean > 10 && !DXManager.DeviceLost;
                    if (!ok) _qaFailures++;
                    _log.Add($"qa {step.Name}: {(ok ? "OK" : "FAIL")} backbuffer={size.Width}x{size.Height} image={bmp.Width}x{bmp.Height} mean={mean:0.0} deviceLost={DXManager.DeviceLost}");
                }
                catch (Exception ex)
                {
                    _qaFailures++;
                    _log.Add($"qa {step.Name}: FAIL {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static double MeanBrightness(Bitmap bmp)
        {
            long sum = 0; int n = 0;
            for (int y = 0; y < bmp.Height; y += 16)
                for (int x = 0; x < bmp.Width; x += 16)
                {
                    var c = bmp.GetPixel(x, y);
                    sum += c.R + c.G + c.B; n++;
                }
            return n == 0 ? 0 : sum / (3.0 * n);
        }

        /// <summary>Halfway through a smoke run: the in-game screenshot path, after which the loop keeps rendering.</summary>
        public static void MidSmokeScreenshot()
        {
            MidSmokeDone = true;
            try
            {
                Directory.CreateDirectory(OutDir);
                using (var bmp = DXManager.CaptureBackBuffer())
                    bmp.Save(Path.Combine(OutDir, "smoke-mid.png"), ImageFormat.Png);
                _log.Add("smoke mid-run screenshot ok");
            }
            catch (Exception ex)
            {
                _log.Add("FATAL smoke mid-run screenshot " + ex);
            }
        }

        /// <summary>End of a smoke run (called from the real game loop): capture the back buffer and the frame profile.</summary>
        public static void FinishSmoke()
        {
            try
            {
                Directory.CreateDirectory(OutDir);
                using (var bmp = DXManager.CaptureBackBuffer())
                    bmp.Save(Path.Combine(OutDir, "smoke.png"), ImageFormat.Png);
                _log.Add($"smoke backend {DXManager.RendererType} (configured {Settings.Renderer}) fullscreen={Settings.FullScreen} borderless={DXManager.UsesBorderlessFullscreen}");
                _log.Add($"smoke window client={Program.Form.ClientSize} backbuffer={DXManager.MainSurface?.Width}x{DXManager.MainSurface?.Height} scene={Settings.ScreenWidth}x{Settings.ScreenHeight}");
                _log.Add("smoke frame " + FrameProfiler.Summary + $" fps={CMain.FPS}");
                _log.Add($"smoke memory private={System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024)} MB");
                if (QaMode)
                {
                    int pending = (_qaSteps?.Count ?? 0) - _qaNext;
                    if (pending > 0) { _qaFailures++; _log.Add($"qa FAIL: {pending} steps did not run (raise --smoke seconds)"); }
                    _log.Add(_qaFailures == 0 ? "qa RESULT: PASS" : $"qa RESULT: FAIL ({_qaFailures})");
                }
                Environment.ExitCode = QaMode && _qaFailures > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                _log.Add("FATAL smoke " + ex);
                Environment.ExitCode = 2;
            }
            finally
            {
                try { File.WriteAllLines(Path.Combine(OutDir, "smoke.log"), _log); } catch { }
            }
        }

        // ------------------------------------------------------------------ compare

        /// <summary>
        /// Compares every <c>.bgra</c> in <see cref="CompareA"/> with the same file in <see cref="CompareB"/>. Exact mode
        /// (default) passes only byte-identical images. <c>--parity</c> (D3D11 vs D3D9) passes when every channel is within 2
        /// and at least 99.9 % of pixels are within 1. Writes compare.txt into the candidate folder; exit 0 pass, 1 fail, 2 error.
        /// </summary>
        public static int Compare()
        {
            var lines = new List<string>();
            int failures = 0;
            try
            {
                var files = Directory.GetFiles(CompareA, "*.bgra").OrderBy(f => f).ToList();
                if (files.Count == 0) { lines.Add("no .bgra files in " + CompareA); return 2; }
                foreach (var fa in files)
                {
                    var name = Path.GetFileNameWithoutExtension(fa);
                    var fb = Path.Combine(CompareB, name + ".bgra");
                    if (!File.Exists(fb)) { lines.Add($"{name}: MISSING"); failures++; continue; }
                    var a = File.ReadAllBytes(fa);
                    var b = File.ReadAllBytes(fb);
                    if (a.Length != b.Length) { lines.Add($"{name}: SIZE {a.Length} vs {b.Length}"); failures++; continue; }

                    if (a.AsSpan().SequenceEqual(b)) { lines.Add($"{name}: IDENTICAL"); continue; }

                    int maxRgb = 0, maxA = 0, over1 = 0, over2 = 0, differing = 0, pixels = a.Length / 4;
                    for (int i = 0; i < a.Length; i += 4)
                    {
                        int d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
                        int da = Math.Abs(a[i + 3] - b[i + 3]);
                        if (d > maxRgb) maxRgb = d;
                        if (da > maxA) maxA = da;
                        int m = Math.Max(d, da);
                        if (m > 0) differing++;
                        if (m > 1) over1++;
                        if (m > 2) over2++;
                    }
                    double within1 = 100.0 * (pixels - over1) / pixels;
                    bool pass = ParityMode && maxRgb <= 2 && maxA <= 2 && within1 >= 99.9;
                    bool waived = !pass && Waivers.TryGetValue(name, out int allowed) && differing <= allowed;
                    if (!pass && !waived) failures++;
                    lines.Add($"{name}: {(pass ? "PARITY" : waived ? "WAIVED" : "DIFF")} differing={differing} maxRGB={maxRgb} maxA={maxA} pixels>1={over1} pixels>2={over2} within1={within1:0.000}%");
                }
                lines.Add(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
                return failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                lines.Add("ERROR " + ex);
                return 2;
            }
            finally
            {
                try { File.WriteAllLines(Path.Combine(CompareB ?? ".", "compare.txt"), lines); } catch { }
            }
        }
    }
}
