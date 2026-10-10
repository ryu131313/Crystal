using SlimDX;

namespace Client.MirGraphics.Rendering
{
    /// <summary>Placeholder until roadmap C4: <see cref="Create"/> throws, so DXManager falls back to D3D9.</summary>
    internal sealed class D3D11Backend : IRenderBackend
    {
        private static NotSupportedException NotYet() => new NotSupportedException("The D3D11 backend is not implemented yet (roadmap C4).");

        public RendererType Type => RendererType.D3D11;
        public RenderTexture BackBuffer => throw NotYet();
        public void Create(IntPtr windowHandle, int width, int height, bool windowed, bool vsync) => throw NotYet();
        public RenderTexture CreateTexture(int width, int height) => throw NotYet();
        public RenderTexture CreateRenderTarget(int width, int height) => throw NotYet();
        public unsafe byte* LockTexture(RenderTexture texture) => throw NotYet();
        public void UnlockTexture(RenderTexture texture) => throw NotYet();
        public void DestroyTexture(RenderTexture texture) { }
        public void SetRenderTarget(RenderTexture target) => throw NotYet();
        public void Clear(Color colour) => throw NotYet();
        public void BeginScene() => throw NotYet();
        public void EndScene() => throw NotYet();
        public void Present() => throw NotYet();
        public void SpriteBegin(bool alphaBlend) => throw NotYet();
        public void SpriteEnd() => throw NotYet();
        public void SpriteFlush() => throw NotYet();
        public void SpriteDraw(RenderTexture texture, Rectangle? sourceRect, Vector3? position, Color4 colour) => throw NotYet();
        public void SetSpriteTransform(Matrix transform) => throw NotYet();
        public void SetAlphaBlendEnable(bool enable) => throw NotYet();
        public void SetSourceBlend(RenderBlend blend) => throw NotYet();
        public void SetDestinationBlend(RenderBlend blend) => throw NotYet();
        public void SetSourceBlendAlpha(RenderBlend blend) => throw NotYet();
        public void SetBlendOperationAdd() => throw NotYet();
        public void SetBlendFactor(Color factor) => throw NotYet();
        public bool IsPixelShaderAvailable(PixelShaderKind kind) => false;
        public bool IsPixelShaderActive(PixelShaderKind kind) => false;
        public void SetPixelShader(PixelShaderKind kind) => throw NotYet();
        public void SetPixelShaderConstants(Vector4 c0, Vector4 c1) => throw NotYet();
        public void DrawLines(Vector2[] points, Color colour) => throw NotYet();
        public DeviceStatus TestCooperativeLevel() => DeviceStatus.Lost;
        public void Reset(int width, int height, bool windowed, bool vsync) => throw NotYet();
        public void Recover() { }
        public bool IsDeviceLostException(Exception exception) => false;
        public byte[] CaptureRenderTarget(RenderTexture target) => throw NotYet();
        public Bitmap CaptureBackBuffer() => throw NotYet();
        public void Dispose() { }
    }
}
