using SlimDX;

namespace Client.MirGraphics.Rendering
{
    /// <summary>Which backend draws the game ([Graphics] Renderer in the client ini).</summary>
    public enum RendererType
    {
        D3D9,
        D3D11,
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
        void Present();

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
