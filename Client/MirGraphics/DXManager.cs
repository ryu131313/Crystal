using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Client.MirControls;
using Client.MirGraphics.Rendering;
using Client.MirScenes;
using SlimDX;

namespace Client.MirGraphics
{
    /// <summary>
    /// Static graphics façade. Since roadmap C3 every device call goes through <see cref="Backend"/> (D3D9 or D3D11,
    /// [Graphics] Renderer); the state logic below is unchanged from the SlimDX-only version, including its quirks,
    /// because the golden render tests require identical output. Game code uses these helpers, never a backend.
    /// </summary>
    class DXManager
    {
        public static List<MImage> TextureList = new List<MImage>();
        public static List<MirControl> ControlList = new List<MirControl>();
        /// <summary>
        /// Plain textures a feature keeps outside TextureList/ControlList and makes again once Disposed (atmosphere splash
        /// sprites, NPC portrait frames, the modern character body). CleanUp disposes them on every device reset, so none
        /// keeps a view of a re-created D3D11 device; the D3D9 managed pool would have kept them, so the pixels are the same.
        /// </summary>
        public static List<RenderTexture> OwnedTextures = new List<RenderTexture>();

        public static IRenderBackend Backend;

        public static RenderTexture CurrentSurface;
        public static RenderTexture MainSurface;
        public static bool DeviceLost;
        public static float Opacity = 1F;
        public static bool Blending;
        public static float BlendingRate;
        public static BlendMode BlendingMode;


        public static RenderTexture RadarTexture;
        public static List<RenderTexture> Lights = new List<RenderTexture>();
        public static RenderTexture PoisonDotBackground;
        /// <summary>A generated dungeon's minimap picture (MirObjects.GeneratedMapPicture keeps the pixels and re-creates it).</summary>
        public static RenderTexture GeneratedMapTexture;

        public static RenderTexture FloorTexture, LightTexture;
        /// <summary>Float light map of the enhanced lighting (D3D11 only, [Graphics] Lighting=Enhanced); released in CleanUp.</summary>
        public static RenderTexture EnhancedLightTexture;
        /// <summary>Wet ground (D3D11 only): the reflectivity snapshot (R = scene alpha after the objects); released in CleanUp.</summary>
        public static RenderTexture AtmosphereWetMask;
        /// <summary>Wet ground (D3D11 only): the half-resolution object mirror; released in CleanUp.</summary>
        public static RenderTexture AtmosphereReflectMirror;
        /// <summary>Wet ground (D3D11 only): the half-resolution lamp reflections (RGBA16F); released in CleanUp.</summary>
        public static RenderTexture AtmosphereReflectLamps;
        /// <summary>Wet ground (D3D11 only): the per-cell floor/water/eaves-drip mask (CPU-written); released in CleanUp.</summary>
        public static RenderTexture AtmosphereCellMask;

        public static bool GrayScale;

        public static Point[] LightSizes =
        {
            new Point(125,95),
            new Point(205,156),
            new Point(285,217),
            new Point(365,277),
            new Point(445,338),
            new Point(525,399),
            new Point(605,460),
            new Point(685,521),
            new Point(765,581),
            new Point(845,642),
            new Point(925,703)
        };

        public static RendererType RendererType => Backend?.Type ?? Settings.Renderer;

        /// <summary>
        /// D3D11 fullscreen is a borderless window covering the screen with a game-sized back buffer that DXGI stretches
        /// (flip model, no exclusive mode). D3D9 keeps its exclusive fullscreen.
        /// </summary>
        public static bool UsesBorderlessFullscreen => Backend?.Type == RendererType.D3D11 && Settings.FullScreen;

        /// <summary>Back-buffer size for a reset: the client area, or the game resolution in borderless fullscreen.</summary>
        public static Size BackBufferSize => UsesBorderlessFullscreen ? new Size(Settings.ScreenWidth, Settings.ScreenHeight) : Program.Form.ClientSize;

        /// <summary>Window client pixels → game pixels (identity unless the scene is stretched in borderless fullscreen).</summary>
        public static Point ClientToScene(Point point)
        {
            if (!UsesBorderlessFullscreen) return point;
            Size client = Program.Form.ClientSize;
            if (client.Width <= 0 || client.Height <= 0) return point;
            return new Point(point.X * Settings.ScreenWidth / client.Width, point.Y * Settings.ScreenHeight / client.Height);
        }

        /// <summary>Sizes the window for the current mode: covers the screen in borderless fullscreen, else the game resolution.</summary>
        public static void ApplyWindowBounds()
        {
            if (UsesBorderlessFullscreen)
                Program.Form.Bounds = Screen.FromControl(Program.Form).Bounds;
            else
                Program.Form.ClientSize = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
        }

        public static void Create()
        {
            if (Settings.Renderer == RendererType.D3D11)
            {
                try
                {
                    Backend = CreateD3D11Backend();
                    Backend.Create(Program.Form.Handle, Settings.ScreenWidth, Settings.ScreenHeight, !Settings.FullScreen, Settings.FPSCap);
                }
                catch (Exception ex)
                {
                    // D3D11 is opt-in during roadmap C4: fall back to D3D9 for this run (no device, missing Vortice DLLs, ...)
                    // rather than leaving the client without a renderer.
                    CMain.SaveError("D3D11 backend failed to start, falling back to D3D9: " + ex);
                    try { Backend?.Dispose(); } catch { }
                    Backend = null;
                }
            }

            if (Backend == null)
            {
                Backend = new D3D9Backend();
                Backend.Create(Program.Form.Handle, Settings.ScreenWidth, Settings.ScreenHeight, !Settings.FullScreen, Settings.FPSCap);
            }

            LoadTextures();
        }

        // Separate, non-inlined: compiling Create must not need the Vortice assemblies when D3D9 is selected.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static IRenderBackend CreateD3D11Backend() => new D3D11Backend();

        private static unsafe void LoadTextures()
        {
            MainSurface = Backend.BackBuffer;
            CurrentSurface = MainSurface;

            if (RadarTexture == null || RadarTexture.Disposed)
            {
                RadarTexture = Backend.CreateTexture(2, 2);

                byte* data = Backend.LockTexture(RadarTexture);
                using (Bitmap image = new Bitmap(2, 2, 8, PixelFormat.Format32bppArgb, (IntPtr)data))
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.Clear(Color.White);
                Backend.UnlockTexture(RadarTexture);
            }
            if (PoisonDotBackground == null || PoisonDotBackground.Disposed)
            {
                PoisonDotBackground = Backend.CreateTexture(5, 5);

                byte* data = Backend.LockTexture(PoisonDotBackground);
                using (Bitmap image = new Bitmap(5, 5, 20, PixelFormat.Format32bppArgb, (IntPtr)data))
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.Clear(Color.White);
                Backend.UnlockTexture(PoisonDotBackground);
            }
            CreateLights();
        }

        private unsafe static void CreateLights()
        {

            for (int i = Lights.Count - 1; i >= 0; i--)
                Lights[i].Dispose();

            Lights.Clear();

            for (int i = 1; i < LightSizes.Length; i++)
            {
                // int width = 125 + (57 *i);
                //int height = 110 + (57 * i);
                int width = LightSizes[i].X;
                int height = LightSizes[i].Y;

                RenderTexture light = Backend.CreateTexture(width, height);

                byte* data = Backend.LockTexture(light);
                using (Bitmap image = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, (IntPtr)data))
                {
                    using (Graphics graphics = Graphics.FromImage(image))
                    {
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            //path.AddEllipse(new Rectangle(0, 0, width, height));
                            //using (PathGradientBrush brush = new PathGradientBrush(path))
                            //{
                            //    graphics.Clear(Color.FromArgb(0, 0, 0, 0));
                            //    brush.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                            //    brush.CenterColor = Color.FromArgb(255, 255, 255, 255);
                            //    graphics.FillPath(brush, path);
                            //    graphics.Save();
                            //}

                            path.AddEllipse(new Rectangle(0, 0, width, height));
                            using (PathGradientBrush brush = new PathGradientBrush(path))
                            {
                                Color[] blendColours = { Color.White,
                                                     Color.FromArgb(255,210,210,210),
                                                     Color.FromArgb(255,160,160,160),
                                                     Color.FromArgb(255,70,70,70),
                                                     Color.FromArgb(255,40,40,40),
                                                     Color.FromArgb(0,0,0,0)};

                                float[] radiusPositions = { 0f, .20f, .40f, .60f, .80f, 1.0f };

                                ColorBlend colourBlend = new ColorBlend();
                                colourBlend.Colors = blendColours;
                                colourBlend.Positions = radiusPositions;

                                graphics.Clear(Color.FromArgb(0, 0, 0, 0));
                                brush.InterpolationColors = colourBlend;
                                brush.SurroundColors = blendColours;
                                brush.CenterColor = Color.White;
                                graphics.FillPath(brush, path);
                                graphics.Save();
                            }
                        }
                    }
                }

                Backend.UnlockTexture(light);
                //light.Disposing += (o, e) => Lights.Remove(light);
                Lights.Add(light);
            }
        }

        // ------------------------------------------------------------------ resources (roadmap C3 helpers)

        /// <summary>A CPU-written BGRA texture (sprites, text). Write it between <see cref="LockTexture"/>/<see cref="UnlockTexture"/>.</summary>
        public static RenderTexture CreateTexture(int width, int height) => Backend.CreateTexture(width, height);

        /// <summary><see cref="CreateTexture"/> registered in <see cref="OwnedTextures"/>: the owner re-makes it when it finds it Disposed.</summary>
        public static RenderTexture CreateOwnedTexture(int width, int height)
        {
            RenderTexture texture = Backend.CreateTexture(width, height);
            OwnedTextures.Add(texture);
            return texture;
        }

        /// <summary>A render target. Must be a control texture registered in <see cref="ControlList"/> or a DXManager static released in CleanUp.</summary>
        public static RenderTexture CreateRenderTarget(int width, int height) => Backend.CreateRenderTarget(width, height);

        /// <summary>Pointer to the texture's BGRA pixels, row pitch Width * 4. Always pair with <see cref="UnlockTexture"/>.</summary>
        public static unsafe byte* LockTexture(RenderTexture texture) => Backend.LockTexture(texture);

        public static void UnlockTexture(RenderTexture texture) => Backend.UnlockTexture(texture);

        // ------------------------------------------------------------------ frame

        /// <summary>Start of a frame: clear, begin scene, begin the sprite (AlphaBlend) and target the back buffer.</summary>
        public static void BeginFrame()
        {
            Backend.Clear(Color.Black);
            Backend.BeginScene();
            Backend.SpriteBegin(true);
            SetSurface(MainSurface);
        }

        /// <summary>End of a frame before Present: end the sprite and the scene.</summary>
        public static void EndFrame()
        {
            Backend.SpriteEnd();
            Backend.EndScene();
        }

        /// <summary>Keep a copy of every presented frame for <see cref="CaptureBackBuffer"/> (render tests read frames after Present).</summary>
        public static bool KeepFrames;
        private static bool _captureNextFrame;

        /// <summary>Keep the next presented frame for <see cref="CaptureBackBuffer"/> (screenshots); read it after that Present.</summary>
        public static void CaptureNextFrame() => _captureNextFrame = true;

        /// <summary>Frames presented so far (a capture requested with <see cref="CaptureNextFrame"/> is ready once this advances).</summary>
        public static long PresentCount { get; private set; }

        public static void Present()
        {
            bool keep = KeepFrames || _captureNextFrame;
            _captureNextFrame = false;
            Backend.Present(keep);
            PresentCount++;
        }

        /// <summary>Clears the current render target.</summary>
        public static void Clear(Color colour) => Backend.Clear(colour);

        /// <summary>Draws the queued sprites now (ID3DXSprite.Flush). Every state helper already flushes.</summary>
        public static void Flush() => Backend.SpriteFlush();

        public static void SetSpriteTransform(SlimDX.Matrix transform) => Backend.SetSpriteTransform(transform);

        /// <summary>1-pixel line strip through <paramref name="points"/> (control borders). Flushes the sprite first.</summary>
        public static void DrawLines(Vector2[] points, Color colour)
        {
            Backend.SpriteFlush();
            Backend.DrawLines(points, colour);
        }

        /// <summary>MapControl.DrawLights: lights add into the light map (SourceAlpha, One). Call after SetBlend(true).</summary>
        public static void SetLightAdditiveBlend()
        {
            Backend.SetSourceBlend(RenderBlend.SourceAlpha);
            Backend.SetDestinationBlend(RenderBlend.One);
        }

        /// <summary>MapControl.DrawLights: the light map multiplies the scene (Zero, SourceColor). Undo with <see cref="RestartSpriteAlphaBlend"/>.</summary>
        public static void SetLightMultiplyBlend()
        {
            Backend.SetSourceBlend(RenderBlend.Zero);
            Backend.SetDestinationBlend(RenderBlend.SourceColor);
        }

        /// <summary>True when the backend can draw the enhanced lighting (D3D11).</summary>
        public static bool EnhancedLightingAvailable => Backend != null && Backend.SupportsEnhancedLighting;

        /// <summary>A float render target for <see cref="EnhancedLightTexture"/> (D3D11 only).</summary>
        public static RenderTexture CreateLightTarget(int width, int height) => Backend.CreateLightTarget(width, height);

        /// <summary>Enhanced lights add (One, One) into the float light map through the Light shader. Call after SetBlend(true);
        /// undo with <see cref="EndEnhancedLightBlend"/> before SetBlend(false).</summary>
        public static void SetEnhancedLightAdditiveBlend()
        {
            Backend.SpriteFlush();
            Backend.SetSourceBlend(RenderBlend.One);
            Backend.SetDestinationBlend(RenderBlend.One);
            Backend.SetPixelShader(PixelShaderKind.Light);
        }

        /// <summary>Back to the SetBlend(true) state (SourceAlpha, One, no shader), as the classic lights leave it.</summary>
        public static void EndEnhancedLightBlend()
        {
            Backend.SpriteFlush();
            Backend.SetPixelShader(PixelShaderKind.None);
            Backend.SetSourceBlend(RenderBlend.SourceAlpha);
            Backend.SetDestinationBlend(RenderBlend.One);
        }

        /// <summary>One enhanced light quad (see <see cref="IRenderBackend.DrawLight"/>).</summary>
        public static void DrawLight(float x, float y, float width, float height, Color colour, float intensity)
        {
            Backend.DrawLight(x, y, width, height, colour, intensity);
            CMain.DPSCounter++;
        }

        /// <summary>True when the backend can run the atmosphere weather passes (D3D11 with the atmosphere shaders compiled).</summary>
        public static bool AtmosphereAvailable => Backend != null && Backend.SupportsAtmosphere;

        /// <summary>A render target in the given colour format (Rgba8 = A8R8G8B8).</summary>
        public static RenderTexture CreateRenderTarget(int width, int height, RenderTargetFormat format) => Backend.CreateRenderTarget(width, height, format);

        /// <summary>Runs a fullscreen atmosphere pass ([Graphics] Atmosphere) over the current target, sampling <paramref name="source"/> at t0.</summary>
        public static void DrawFullscreen(FullscreenKind kind, RenderTexture source, ReadOnlySpan<Vector4> inputs)
        {
            Backend.DrawFullscreen(kind, source, inputs);
            CMain.DPSCounter++;
        }

        /// <summary>A fullscreen atmosphere pass with extra sources on t1/t2 (scene, wet mask, cell mask).</summary>
        public static void DrawFullscreen(FullscreenKind kind, RenderTexture source, RenderTexture source1, RenderTexture source2, ReadOnlySpan<Vector4> inputs)
        {
            Backend.SetAtmosphereSource(1, source1);
            Backend.SetAtmosphereSource(2, source2);
            Backend.DrawFullscreen(kind, source, inputs);
            Backend.SetAtmosphereSource(1, null);
            Backend.SetAtmosphereSource(2, null);
            CMain.DPSCounter++;
        }

        /// <summary>Binds a pass source on t1..t3 for the next <see cref="DrawFullscreen(FullscreenKind, RenderTexture, ReadOnlySpan{Vector4})"/>.</summary>
        public static void SetAtmosphereSource(int slot, RenderTexture texture) => Backend.SetAtmosphereSource(slot, texture);

        /// <summary>Alpha blending with explicit factors (flushes first). Restore with <see cref="RestartSpriteAlphaBlend"/>.</summary>
        public static void SetBlendFactors(RenderBlend source, RenderBlend destination)
        {
            Backend.SpriteFlush();
            Backend.SetAlphaBlendEnable(true);
            Backend.SetSourceBlend(source);
            Backend.SetDestinationBlend(destination);
        }

        /// <summary>A fullscreen pass composited with explicit blend factors (reflection mirror/lamps); the caller restores
        /// the sprite state with <see cref="RestartSpriteAlphaBlend"/>.</summary>
        public static void DrawFullscreenBlend(FullscreenKind kind, RenderTexture source, RenderBlend blendSource, RenderBlend blendDestination, ReadOnlySpan<Vector4> inputs)
        {
            SetBlendFactors(blendSource, blendDestination);
            DrawFullscreen(kind, source, inputs);
        }

        /// <summary>The enhanced light map modulates the scene 2x (DestinationColor, SourceColor) through the LightComposite shader,
        /// so lights can brighten it. Undo with <see cref="RestartSpriteAlphaBlend"/>.</summary>
        public static void SetEnhancedLightCompositeBlend()
        {
            Backend.SpriteFlush();
            Backend.SetSourceBlend(RenderBlend.DestinationColor);
            Backend.SetDestinationBlend(RenderBlend.SourceColor);
            Backend.SetPixelShader(PixelShaderKind.LightComposite);
        }

        /// <summary>True while a new-UI render target is drawn: blended draws write alpha as "over" (see IRenderBackend.SetAlphaOver).</summary>
        public static bool AlphaOver { get; private set; }

        /// <summary>Alpha "over" on / off (flushes first). The new UI's render targets then hold premultiplied colour: composite
        /// them with (One, InverseSourceAlpha).</summary>
        public static void SetAlphaOver(bool enable)
        {
            if (AlphaOver == enable) return;
            Backend.SpriteFlush();
            Backend.SetAlphaOver(enable);
            AlphaOver = enable;
        }

        /// <summary>Ends and restarts the sprite in AlphaBlend mode, restoring the default sprite states.</summary>
        public static void RestartSpriteAlphaBlend()
        {
            Backend.SpriteEnd();
            Backend.SpriteBegin(true);
        }

        public static bool IsDeviceLostException(Exception exception) => Backend != null && Backend.IsDeviceLostException(exception);

        /// <summary>The back buffer as a bitmap (screenshots).</summary>
        public static Bitmap CaptureBackBuffer() => Backend.CaptureBackBuffer();

        public static void SetSurface(RenderTexture surface)
        {
            if (CurrentSurface == surface)
                return;

            Backend.SpriteFlush();
            CurrentSurface = surface;
            Backend.SetRenderTarget(surface);
        }
        public static void SetGrayscale(bool value)
        {
            GrayScale = value;

            if (value == true)
            {
                if (Backend.IsPixelShaderActive(PixelShaderKind.Grayscale)) return;
                Backend.SpriteFlush();
                Backend.SetPixelShader(PixelShaderKind.Grayscale);
            }
            else
            {
                if (Backend.IsPixelShaderActive(PixelShaderKind.None)) return;
                Backend.SpriteFlush();
                Backend.SetPixelShader(PixelShaderKind.None);
            }
        }

        public static void DrawOpaque(RenderTexture texture, Rectangle? sourceRect, Vector3? position, Color4 color, float opacity)
        {
            color.Alpha = opacity;
            Draw(texture, sourceRect, position, color);
        }

        public static void Draw(RenderTexture texture, Rectangle? sourceRect, Vector3? position, Color4 color)
        {
            Backend.SpriteDraw(texture, sourceRect, position, color);
            CMain.DPSCounter++;
        }

        public static void AttemptReset()
        {
            try
            {
                DeviceStatus status = Backend.TestCooperativeLevel();

                if (status == DeviceStatus.Lost) return;

                if (status == DeviceStatus.NotReset)
                {
                    DXManager.ResetDevice();
                    return;
                }

                DXManager.DeviceLost = false;
            }
            catch
            {
            }
        }

        public static void ResetDevice()
        {
            DXManager.CleanUp();
            DXManager.DeviceLost = true;

            if (DXManager.Backend == null) return;

            Size clientSize = DXManager.BackBufferSize;

            if (clientSize.Width == 0 || clientSize.Height == 0) return;

            DXManager.Backend.Reset(clientSize.Width, clientSize.Height, !Settings.FullScreen, Settings.FPSCap);

            DXManager.LoadTextures();
        }

        public static void AttemptRecovery()
        {
            Backend.Recover();
            MainSurface = Backend.BackBuffer;
            CurrentSurface = MainSurface;
        }
        public static void SetOpacity(float opacity)
        {
            if (Opacity == opacity)
                return;

            Backend.SpriteFlush();
            Backend.SetAlphaBlendEnable(true);
            if (opacity >= 1 || opacity < 0)
            {
                Backend.SetSourceBlend(RenderBlend.SourceAlpha);
                Backend.SetDestinationBlend(RenderBlend.InverseSourceAlpha);
                Backend.SetSourceBlendAlpha(RenderBlend.One);
                Backend.SetBlendFactor(Color.FromArgb(255, 255, 255, 255));
            }
            else
            {
                Backend.SetSourceBlend(RenderBlend.BlendFactor);
                Backend.SetDestinationBlend(RenderBlend.InverseBlendFactor);
                Backend.SetSourceBlendAlpha(RenderBlend.SourceAlpha);
                Backend.SetBlendFactor(Color.FromArgb((byte)(255 * opacity), (byte)(255 * opacity), (byte)(255 * opacity), (byte)(255 * opacity)));
            }
            Opacity = opacity;
            Backend.SpriteFlush();
        }
        public static void SetBlend(bool value, float rate = 1F, BlendMode mode = BlendMode.NORMAL)
        {
            if (value == Blending && BlendingRate == rate && BlendingMode == mode) return;

            Blending = value;
            BlendingRate = rate;
            BlendingMode = mode;

            Backend.SpriteFlush();

            Backend.SpriteEnd();

            if (Blending)
            {
                Backend.SpriteBegin(false);
                Backend.SetAlphaBlendEnable(true);

                switch (BlendingMode)
                {
                    case BlendMode.INVLIGHT:
                        Backend.SetBlendOperationAdd();
                        Backend.SetSourceBlend(RenderBlend.BlendFactor);
                        Backend.SetDestinationBlend(RenderBlend.InverseSourceColor);
                        break;
                    default:
                        Backend.SetSourceBlend(RenderBlend.SourceAlpha);
                        Backend.SetDestinationBlend(RenderBlend.One);
                        break;
                }

                Backend.SetBlendFactor(Color.FromArgb((byte)(255 * BlendingRate), (byte)(255 * BlendingRate),
                                                      (byte)(255 * BlendingRate), (byte)(255 * BlendingRate)));
            }
            else
                Backend.SpriteBegin(true);

            Backend.SetRenderTarget(CurrentSurface);
        }

        public static void SetNormal(float blend, Color tintcolor)
        {
            if (Backend.IsPixelShaderActive(PixelShaderKind.Normal))
                return;

            Backend.SpriteFlush();
            Backend.SetPixelShader(PixelShaderKind.Normal);
            Backend.SetPixelShaderConstants(new Vector4(1.0F, 1.0F, 1.0F, blend), new Vector4(tintcolor.R / 255, tintcolor.G / 255, tintcolor.B / 255, 1.0F));
            Backend.SpriteFlush();
        }

        public static void SetGrayscale(float blend, Color tintcolor)
        {
            if (Backend.IsPixelShaderActive(PixelShaderKind.Grayscale))
                return;

            Backend.SpriteFlush();
            Backend.SetPixelShader(PixelShaderKind.Grayscale);
            Backend.SetPixelShaderConstants(new Vector4(1.0F, 1.0F, 1.0F, blend), new Vector4(tintcolor.R / 255, tintcolor.G / 255, tintcolor.B / 255, 1.0F));
            Backend.SpriteFlush();
        }

        public static void SetBlendMagic(float blend, Color tintcolor)
        {
            if (Backend.IsPixelShaderActive(PixelShaderKind.Magic) || !Backend.IsPixelShaderAvailable(PixelShaderKind.Magic))
                return;

            Backend.SpriteFlush();
            Backend.SetPixelShader(PixelShaderKind.Magic);
            Backend.SetPixelShaderConstants(new Vector4(1.0F, 1.0F, 1.0F, blend), new Vector4(tintcolor.R / 255, tintcolor.G / 255, tintcolor.B / 255, 1.0F));
            Backend.SpriteFlush();
        }

        public static void Clean()
        {
            for (int i = TextureList.Count - 1; i >= 0; i--)
            {
                MImage m = TextureList[i];

                if (m == null)
                {
                    TextureList.RemoveAt(i);
                    continue;
                }

                if (CMain.Time <= m.CleanTime) continue;

                m.DisposeTexture();
            }

            for (int i = ControlList.Count - 1; i >= 0; i--)
            {
                MirControl c = ControlList[i];

                if (c == null)
                {
                    ControlList.RemoveAt(i);
                    continue;
                }

                if (CMain.Time <= c.CleanTime) continue;

                c.DisposeTexture();
            }

            for (int i = OwnedTextures.Count - 1; i >= 0; i--)
            {
                if (OwnedTextures[i] == null || OwnedTextures[i].Disposed) OwnedTextures.RemoveAt(i); // disposed by its owner
            }
        }


        private static void DisposeStatic(ref RenderTexture texture)
        {
            if (texture == null) return;
            if (!texture.Disposed) texture.Dispose();
            texture = null;
        }

        private static void CleanUp()
        {
            // The back buffer belongs to the backend (released in Backend.Reset/Dispose); only forget it here.
            CurrentSurface = null;
            MainSurface = null;

            if (PoisonDotBackground != null)
            {
                if (!PoisonDotBackground.Disposed)
                {
                    PoisonDotBackground.Dispose();
                }

                PoisonDotBackground = null;
            }

            if (RadarTexture != null)
            {
                if (!RadarTexture.Disposed)
                {
                    RadarTexture.Dispose();
                }

                RadarTexture = null;
            }

            if (FloorTexture != null)
            {
                if (!FloorTexture.Disposed)
                {
                    FloorTexture.Dispose();
                }

                DXManager.FloorTexture = null;
            }

            if (LightTexture != null)
            {
                if (!LightTexture.Disposed)
                    LightTexture.Dispose();

                DXManager.LightTexture = null;
            }

            if (EnhancedLightTexture != null)
            {
                if (!EnhancedLightTexture.Disposed)
                    EnhancedLightTexture.Dispose();

                EnhancedLightTexture = null;
            }

            DisposeStatic(ref AtmosphereWetMask);
            DisposeStatic(ref AtmosphereReflectMirror);
            DisposeStatic(ref AtmosphereReflectLamps);
            DisposeStatic(ref AtmosphereCellMask);
            DisposeStatic(ref GeneratedMapTexture);

            if (Lights != null)
            {
                for (int i = 0; i < Lights.Count; i++)
                {
                    if (!Lights[i].Disposed)
                        Lights[i].Dispose();
                }
                Lights.Clear();
            }

            for (int i = TextureList.Count - 1; i >= 0; i--)
            {
                MImage m = TextureList[i];

                if (m == null) continue;

                m.DisposeTexture();
            }
            TextureList.Clear();


            for (int i = ControlList.Count - 1; i >= 0; i--)
            {
                MirControl c = ControlList[i];

                if (c == null) continue;

                c.DisposeTexture();
            }
            ControlList.Clear();

            for (int i = OwnedTextures.Count - 1; i >= 0; i--)
            {
                RenderTexture t = OwnedTextures[i];
                if (t != null && !t.Disposed) t.Dispose();
            }
            OwnedTextures.Clear();
        }

        public static void Dispose()
        {
            CleanUp();

            Backend?.Dispose();
            Backend = null;
        }
    }
}
