using SlimDX;

namespace Client.MirGraphics.Rendering
{
    /// <summary>Which backend draws the game ([Graphics] Renderer in the client ini).</summary>
    public enum RendererType
    {
        D3D9,
        D3D11,
    }

    /// <summary>How the map lights are drawn ([Graphics] Lighting, in-game: @light new / @light old).</summary>
    public enum LightingMode
    {
        /// <summary>The original light textures; both backends, golden-tested.</summary>
        Classic,
        /// <summary>Float light map, shader falloff, brightening lights, flicker, day/night fades, spell-tinted lights. D3D11 only;
        /// D3D9 draws Classic.</summary>
        Enhanced,
    }

    /// <summary>D3D9 blend factors used by the client (values mirror D3DBLEND names, not numbers).</summary>
    public enum RenderBlend
    {
        Zero,
        One,
        SourceColor,
        InverseSourceColor,
        SourceAlpha,
        InverseSourceAlpha,
        DestinationAlpha,
        InverseDestinationAlpha,
        DestinationColor,
        InverseDestinationColor,
        BlendFactor,
        InverseBlendFactor,
    }

    /// <summary>The pixel shaders the client can bind (assembled from Data\Shaders on D3D9, HLSL on D3D11).</summary>
    public enum PixelShaderKind
    {
        None,
        Grayscale,
        Normal,
        Magic,
        /// <summary>Enhanced lighting (D3D11 only): an analytic elliptical falloff; colour from the vertex colour, intensity = alpha * 4.</summary>
        Light,
        /// <summary>Enhanced lighting (D3D11 only): samples the float light map, soft-knees it into 0..2 and outputs half of it
        /// for the 2x modulate blend (DestinationColor, SourceColor).</summary>
        LightComposite,
        /// <summary>Atmosphere (D3D11 only): the fullscreen rainfall pass; its source texture (t0) is the enhanced light map.</summary>
        Rainfall,
        /// <summary>Atmosphere (D3D11 only): a flat fullscreen lightning flash (colour in rgb, envelope in alpha), added to the scene.</summary>
        Lightning,
        /// <summary>Atmosphere wet ground (D3D11 only): the ground pass, used for both the wet colour and the alpha/reflectivity pass.</summary>
        Ground,
        /// <summary>Atmosphere wet ground (D3D11 only): the scene alpha into the wet-mask target.</summary>
        Snapshot,
        /// <summary>Atmosphere reflections (D3D11 only): objects and lamps mirrored in puddles.</summary>
        Reflect,
    }

    /// <summary>The colour format of a render target (<see cref="IRenderBackend.CreateRenderTarget(int, int, RenderTargetFormat)"/>).</summary>
    public enum RenderTargetFormat
    {
        /// <summary>8-bit BGRA (A8R8G8B8): the classic control and map targets.</summary>
        Rgba8,
        /// <summary>16-bit half float per channel: values above 1 do not clip (light maps, atmosphere captures).</summary>
        Rgba16F,
    }

    /// <summary>A fullscreen atmosphere pass ([Graphics] Atmosphere; D3D11 + enhanced lighting only).</summary>
    public enum FullscreenKind
    {
        Rainfall,
        Lightning,
        /// <summary>Wet ground (D3D11 only): puddles, ripples, splashes and eaves drips over the floor, alpha-blended.</summary>
        GroundWet,
        /// <summary>Wet ground (D3D11 only): the ground shader's alpha pass, writing the map target's alpha (reflectivity) only.</summary>
        GroundMask,
        /// <summary>Wet ground (D3D11 only): the scene's alpha into the wet-mask target's red channel.</summary>
        Snapshot,
        /// <summary>Wet ground (D3D11 only): objects mirrored in puddles, premultiplied, into a half-resolution target.</summary>
        ReflectObjects,
        /// <summary>Wet ground (D3D11 only): lamp reflections into a half-resolution float target.</summary>
        ReflectLamps,
        /// <summary>Wet ground (D3D11 only): samples the source with the linear sampler, using the caller's blend state
        /// (DXManager.SetBlendFactors), so half-resolution mirrors upsample smoothly.</summary>
        BlitLinear,
    }

    public enum DeviceStatus
    {
        Ok,
        Lost,
        NotReset,
    }

    /// <summary>
    /// A backend texture: a CPU-written sprite/text texture, a render target, or the back buffer. Replaces SlimDX
    /// Texture/Surface in game code (roadmap C3). Pixels are BGRA (A8R8G8B8); <see cref="DXManager.LockTexture"/> returns
    /// a pointer whose row pitch is Width * 4.
    /// </summary>
    public sealed class RenderTexture : IDisposable
    {
        public readonly int Width, Height;
        public readonly bool IsRenderTarget, IsBackBuffer;
        public bool Disposed { get; internal set; }

        /// <summary>Backend-owned data (D3D9: texture + surface; D3D11: texture, views, CPU copy).</summary>
        internal object Native;

        internal RenderTexture(int width, int height, bool renderTarget, bool backBuffer = false)
        {
            Width = width;
            Height = height;
            IsRenderTarget = renderTarget;
            IsBackBuffer = backBuffer;
        }

        public void Dispose()
        {
            if (Disposed) return;
            DXManager.Backend?.DestroyTexture(this);
            Disposed = true;
            Native = null;
        }
    }

    /// <summary>
    /// The render seam (roadmap C3). Shaped like the Direct3D 9 + ID3DXSprite subset the client uses, so DXManager keeps
    /// its exact state logic and every backend reproduces D3D9 behaviour, including D3DX sprite quirks (Begin applies
    /// sprite defaults; End restores the state captured at Begin unless DoNotSaveState). Main (UI) thread only.
    /// No other file may touch SlimDX.Direct3D9 or Vortice types.
    /// </summary>
    public interface IRenderBackend : IDisposable
    {
        RendererType Type { get; }

        /// <summary>Creates the device for <paramref name="windowHandle"/>. Throws on failure.</summary>
        void Create(IntPtr windowHandle, int width, int height, bool windowed, bool vsync);

        RenderTexture BackBuffer { get; }

        RenderTexture CreateTexture(int width, int height);
        RenderTexture CreateRenderTarget(int width, int height);
        unsafe byte* LockTexture(RenderTexture texture);
        void UnlockTexture(RenderTexture texture);
        void DestroyTexture(RenderTexture texture);

        void SetRenderTarget(RenderTexture target);
        void Clear(Color colour);
        void BeginScene();
        void EndScene();
        /// <summary>
        /// Presents the frame. <paramref name="keepFrame"/>: keep a copy for <see cref="CaptureBackBuffer"/> (a flip-model back
        /// buffer is undefined after Present). Only requested for screenshots and render tests: a full-screen copy every frame
        /// cost D3D11 most of its uncapped frame rate.
        /// </summary>
        void Present(bool keepFrame);

        /// <summary>ID3DXSprite.Begin: <paramref name="alphaBlend"/> = SpriteFlags.AlphaBlend (saves state), otherwise
        /// SpriteFlags.DoNotSaveState (blending off, nothing saved).</summary>
        void SpriteBegin(bool alphaBlend);
        void SpriteEnd();
        void SpriteFlush();
        void SpriteDraw(RenderTexture texture, Rectangle? sourceRect, Vector3? position, Color4 colour);
        void SetSpriteTransform(Matrix transform);

        void SetAlphaBlendEnable(bool enable);
        void SetSourceBlend(RenderBlend blend);
        void SetDestinationBlend(RenderBlend blend);
        void SetSourceBlendAlpha(RenderBlend blend);
        void SetBlendOperationAdd();
        void SetBlendFactor(Color factor);

        bool IsPixelShaderAvailable(PixelShaderKind kind);
        /// <summary>True when the device's current pixel shader is <paramref name="kind"/> (None = no shader bound).</summary>
        bool IsPixelShaderActive(PixelShaderKind kind);
        void SetPixelShader(PixelShaderKind kind);
        void SetPixelShaderConstants(Vector4 c0, Vector4 c1);

        /// <summary>ID3DXLine.Draw (width 1): a line strip through <paramref name="points"/>.</summary>
        void DrawLines(Vector2[] points, Color colour);

        /// <summary>
        /// Enhanced lighting ([Graphics] Lighting=Enhanced): a float light target, the <see cref="PixelShaderKind.Light"/> and
        /// <see cref="PixelShaderKind.LightComposite"/> shaders and <see cref="DrawLight"/>. D3D11 only; D3D9 keeps the classic
        /// lights until C5 removes it.
        /// </summary>
        bool SupportsEnhancedLighting { get; }
        /// <summary>A render target with a 16-bit float colour per channel (light values above 1 do not clip).</summary>
        RenderTexture CreateLightTarget(int width, int height);
        /// <summary>Queues one light quad (UV 0..1 across it) for the <see cref="PixelShaderKind.Light"/> shader.
        /// <paramref name="intensity"/> is 0..4, carried in the vertex alpha.</summary>
        void DrawLight(float x, float y, float width, float height, Color colour, float intensity);

        /// <summary>
        /// Atmosphere weather passes ([Graphics] Atmosphere): fullscreen shader rain and lightning over the enhanced-lighting
        /// path. D3D11 only; D3D9 keeps the classic weather sprites and reports false.
        /// </summary>
        bool SupportsAtmosphere { get; }
        /// <summary>A render target in the requested colour format (Rgba8 = A8R8G8B8).</summary>
        RenderTexture CreateRenderTarget(int width, int height, RenderTargetFormat format);
        /// <summary>Uploads up to 128 float4s to the atmosphere constant buffer (b2) read by <see cref="DrawFullscreen"/>.</summary>
        void SetAtmosphereConstants(ReadOnlySpan<Vector4> constants);
        /// <summary>Binds a pass source on texture slot 1..3 (t1..t3, linear clamp) for the next <see cref="DrawFullscreen"/>.
        /// t0 is the <c>source</c> argument. Pass null to clear the slot.</summary>
        void SetAtmosphereSource(int slot, RenderTexture texture);
        /// <summary>Runs a fullscreen atmosphere pass over the current render target, sampling <paramref name="source"/> at t0
        /// (null binds a white 1x1). <paramref name="inputs"/> is uploaded as the atmosphere constants first.</summary>
        void DrawFullscreen(FullscreenKind kind, RenderTexture source, ReadOnlySpan<Vector4> inputs);

        DeviceStatus TestCooperativeLevel();
        /// <summary>Resets/resizes the device. DXManager has already released every texture it owns.</summary>
        void Reset(int width, int height, bool windowed, bool vsync);
        /// <summary>Best-effort end of a broken frame (AttemptRecovery).</summary>
        void Recover();
        bool IsDeviceLostException(Exception exception);

        /// <summary>BGRA bytes of a render target (row pitch Width * 4).</summary>
        byte[] CaptureRenderTarget(RenderTexture target);
        /// <summary>The back buffer as a bitmap (screenshots).</summary>
        Bitmap CaptureBackBuffer();
    }
}
