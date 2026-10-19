using System.Runtime.InteropServices;
using SlimDX;
using SlimDX.Direct3D9;

namespace Client.MirGraphics.Rendering
{
    /// <summary>
    /// SlimDX Direct3D 9 backend (roadmap C3): the original DXManager device code moved behind <see cref="IRenderBackend"/>
    /// without changing a single call, so its output is byte-identical to the pre-seam client (golden render tests).
    /// Removed with SlimDX in roadmap C5.
    /// </summary>
    internal sealed class D3D9Backend : IRenderBackend
    {
        private sealed class TextureData
        {
            public Texture Texture;
            public Surface Surface;     // level 0 of a render target, or the back buffer
        }

        private Direct3D _d3d;
        private Device _device;
        private Sprite _sprite;
        private Line _line;
        private PresentParameters _parameters;
        private PixelShader _grayScale, _normal, _magic;
        private RenderTexture _backBuffer;
        private Surface _readback;

        public RendererType Type => RendererType.D3D9;
        public RenderTexture BackBuffer => _backBuffer;

        public void Create(IntPtr windowHandle, int width, int height, bool windowed, bool vsync)
        {
            _parameters = new PresentParameters
            {
                BackBufferFormat = Format.X8R8G8B8,
                PresentFlags = PresentFlags.LockableBackBuffer,
                BackBufferWidth = width,
                BackBufferHeight = height,
                SwapEffect = SwapEffect.Discard,
                PresentationInterval = vsync ? PresentInterval.One : PresentInterval.Immediate,
                Windowed = windowed,
            };

            _d3d = new Direct3D();

            Capabilities devCaps = _d3d.GetDeviceCaps(0, DeviceType.Hardware);
            DeviceType devType = DeviceType.Reference;
            CreateFlags devFlags = CreateFlags.HardwareVertexProcessing;

            if (devCaps.VertexShaderVersion.Major >= 2 && devCaps.PixelShaderVersion.Major >= 2)
                devType = DeviceType.Hardware;

            if ((devCaps.DeviceCaps & DeviceCaps.HWTransformAndLight) != 0)
                devFlags = CreateFlags.HardwareVertexProcessing;

            if ((devCaps.DeviceCaps & DeviceCaps.PureDevice) != 0)
                devFlags |= CreateFlags.PureDevice;

            _device = new Device(_d3d, _d3d.Adapters.DefaultAdapter.Adapter, devType, windowHandle, devFlags, _parameters);
            _device.SetDialogBoxMode(true);

            CreateDeviceObjects();
            LoadPixelShaders();
        }

        private void CreateDeviceObjects()
        {
            _sprite = new Sprite(_device);
            _line = new Line(_device) { Width = 1F };

            var back = new RenderTexture(_parameters.BackBufferWidth, _parameters.BackBufferHeight, true, backBuffer: true);
            back.Native = new TextureData { Surface = _device.GetBackBuffer(0, 0) };
            _backBuffer = back;
            _device.SetRenderTarget(0, ((TextureData)back.Native).Surface);
        }

        private void LoadPixelShaders()
        {
            _normal = LoadShader(Settings.ShadersPath + "normal.ps");
            _grayScale = LoadShader(Settings.ShadersPath + "grayscale.ps");
            _magic = LoadShader(Settings.ShadersPath + "magic.ps");
        }

        private PixelShader LoadShader(string path)
        {
            if (!File.Exists(path)) return null;
            using (var bytecode = ShaderBytecode.AssembleFromFile(path, ShaderFlags.None))
                return new PixelShader(_device, bytecode);
        }

        // Enhanced lighting is D3D11 only (D3D9 stays golden-identical until C5 removes it).
        public bool SupportsEnhancedLighting => false;
        public RenderTexture CreateLightTarget(int width, int height) => throw new NotSupportedException("Enhanced lighting needs the D3D11 backend.");
        public void DrawLight(float x, float y, float width, float height, Color colour, float intensity) => throw new NotSupportedException("Enhanced lighting needs the D3D11 backend.");

        // Atmosphere weather is D3D11 only too; D3D9 keeps the classic weather sprites.
        public bool SupportsAtmosphere => false;
        public void SetAtmosphereConstants(ReadOnlySpan<Vector4> constants) { }
        public void SetAtmosphereSource(int slot, RenderTexture texture) => throw new NotSupportedException("The atmosphere needs the D3D11 backend.");
        public void DrawFullscreen(FullscreenKind kind, RenderTexture source, ReadOnlySpan<Vector4> inputs) => throw new NotSupportedException("The atmosphere needs the D3D11 backend.");
        public RenderTexture CreateRenderTarget(int width, int height, RenderTargetFormat format)
        {
            if (format == RenderTargetFormat.Rgba16F) throw new NotSupportedException("16-bit float targets need the D3D11 backend.");
            return CreateRenderTarget(width, height);
        }

        private PixelShader ShaderFor(PixelShaderKind kind) => kind switch
        {
            PixelShaderKind.Grayscale => _grayScale,
            PixelShaderKind.Normal => _normal,
            PixelShaderKind.Magic => _magic,
            _ => null,
        };

        // ---------------------------------------------------------------- textures

        public RenderTexture CreateTexture(int width, int height)
        {
            var t = new RenderTexture(width, height, false);
            t.Native = new TextureData { Texture = new Texture(_device, width, height, 1, Usage.None, Format.A8R8G8B8, Pool.Managed) };
            return t;
        }

        public RenderTexture CreateRenderTarget(int width, int height)
        {
            var t = new RenderTexture(width, height, true);
            var tex = new Texture(_device, width, height, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
            t.Native = new TextureData { Texture = tex, Surface = tex.GetSurfaceLevel(0) };
            return t;
        }

        public unsafe byte* LockTexture(RenderTexture texture)
        {
            DataRectangle rect = ((TextureData)texture.Native).Texture.LockRectangle(0, LockFlags.Discard);
            return (byte*)rect.Data.DataPointer;
        }

        public void UnlockTexture(RenderTexture texture)
        {
            ((TextureData)texture.Native).Texture.UnlockRectangle(0);
        }

        public void DestroyTexture(RenderTexture texture)
        {
            if (texture.Native is not TextureData data) return;
            if (data.Surface != null && !data.Surface.Disposed) data.Surface.Dispose();
            if (data.Texture != null && !data.Texture.Disposed) data.Texture.Dispose();
        }

        private static Surface SurfaceOf(RenderTexture target) => ((TextureData)target.Native).Surface;
        private static Texture TextureOf(RenderTexture texture) => ((TextureData)texture.Native).Texture;

        // ---------------------------------------------------------------- frame

        public void SetRenderTarget(RenderTexture target) => _device.SetRenderTarget(0, SurfaceOf(target));
        public void Clear(Color colour) => _device.Clear(ClearFlags.Target, colour, 0, 0);
        public void BeginScene() => _device.BeginScene();
        public void EndScene() => _device.EndScene();
        public void Present(bool keepFrame) => _device.Present(); // the lockable back buffer still holds the frame afterwards

        public void SpriteBegin(bool alphaBlend) => _sprite.Begin(alphaBlend ? SpriteFlags.AlphaBlend : SpriteFlags.DoNotSaveState);
        public void SpriteEnd() => _sprite.End();
        public void SpriteFlush() => _sprite.Flush();

        public void SpriteDraw(RenderTexture texture, Rectangle? sourceRect, Vector3? position, Color4 colour)
        {
            _sprite.Draw(TextureOf(texture), sourceRect, Vector3.Zero, position, colour);
        }

        public void SetSpriteTransform(Matrix transform) => _sprite.Transform = transform;

        public void SetAlphaBlendEnable(bool enable) => _device.SetRenderState(RenderState.AlphaBlendEnable, enable);
        public void SetSourceBlend(RenderBlend blend) => _device.SetRenderState(RenderState.SourceBlend, Map(blend));
        public void SetDestinationBlend(RenderBlend blend) => _device.SetRenderState(RenderState.DestinationBlend, Map(blend));
        public void SetSourceBlendAlpha(RenderBlend blend) => _device.SetRenderState(RenderState.SourceBlendAlpha, Map(blend));
        public void SetBlendOperationAdd() => _device.SetRenderState(RenderState.BlendOperation, BlendOperation.Add);
        public void SetBlendFactor(Color factor) => _device.SetRenderState(RenderState.BlendFactor, factor.ToArgb());

        private static Blend Map(RenderBlend blend) => blend switch
        {
            RenderBlend.Zero => Blend.Zero,
            RenderBlend.One => Blend.One,
            RenderBlend.SourceColor => Blend.SourceColor,
            RenderBlend.InverseSourceColor => Blend.InverseSourceColor,
            RenderBlend.SourceAlpha => Blend.SourceAlpha,
            RenderBlend.InverseSourceAlpha => Blend.InverseSourceAlpha,
            RenderBlend.DestinationAlpha => Blend.DestinationAlpha,
            RenderBlend.InverseDestinationAlpha => Blend.InverseDestinationAlpha,
            RenderBlend.DestinationColor => Blend.DestinationColor,
            RenderBlend.InverseDestinationColor => Blend.InverseDestinationColor,
            RenderBlend.BlendFactor => Blend.BlendFactor,
            RenderBlend.InverseBlendFactor => Blend.InverseBlendFactor,
            _ => Blend.One,
        };

        public bool IsPixelShaderAvailable(PixelShaderKind kind) => kind == PixelShaderKind.None || ShaderFor(kind) != null;
        public bool IsPixelShaderActive(PixelShaderKind kind) => _device.PixelShader == ShaderFor(kind);
        public void SetPixelShader(PixelShaderKind kind) => _device.PixelShader = ShaderFor(kind);

        public void SetPixelShaderConstants(Vector4 c0, Vector4 c1)
        {
            _device.SetPixelShaderConstant(0, new[] { c0 });
            _device.SetPixelShaderConstant(1, new[] { c1 });
        }

        public void DrawLines(Vector2[] points, Color colour) => _line.Draw(points, colour);

        // ---------------------------------------------------------------- device lost / reset

        public DeviceStatus TestCooperativeLevel()
        {
            Result result = _device.TestCooperativeLevel();
            if (result.Code == ResultCode.DeviceLost.Code) return DeviceStatus.Lost;
            if (result.Code == ResultCode.DeviceNotReset.Code) return DeviceStatus.NotReset;
            if (result.Code != ResultCode.Success.Code) return DeviceStatus.Lost;
            return DeviceStatus.Ok;
        }

        private void ReleaseDeviceObjects()
        {
            if (_sprite != null) { if (!_sprite.Disposed) _sprite.Dispose(); _sprite = null; }
            if (_line != null) { if (!_line.Disposed) _line.Dispose(); _line = null; }
            if (_backBuffer != null) { _backBuffer.Dispose(); _backBuffer = null; }
        }

        public void Reset(int width, int height, bool windowed, bool vsync)
        {
            ReleaseDeviceObjects();
            if (_parameters == null) return;

            _parameters.Windowed = windowed;
            _parameters.BackBufferWidth = width;
            _parameters.BackBufferHeight = height;
            // Kept from the original ResetDevice: Default here, One at Create (behaviour unchanged by the seam).
            _parameters.PresentationInterval = vsync ? PresentInterval.Default : PresentInterval.Immediate;
            _device.Reset(_parameters);

            CreateDeviceObjects();
        }

        public void Recover()
        {
            try { _sprite.End(); } catch { }
            try { _device.EndScene(); } catch { }
            try
            {
                var back = new RenderTexture(_parameters.BackBufferWidth, _parameters.BackBufferHeight, true, backBuffer: true);
                back.Native = new TextureData { Surface = _device.GetBackBuffer(0, 0) };
                _backBuffer = back;
                _device.SetRenderTarget(0, ((TextureData)back.Native).Surface);
            }
            catch { }
        }

        public bool IsDeviceLostException(Exception exception) => exception is Direct3D9Exception;

        // ---------------------------------------------------------------- capture

        public byte[] CaptureRenderTarget(RenderTexture target)
        {
            if (_readback == null || _readback.Disposed || _readback.Description.Width != target.Width || _readback.Description.Height != target.Height)
            {
                _readback?.Dispose();
                _readback = Surface.CreateOffscreenPlain(_device, target.Width, target.Height, Format.A8R8G8B8, Pool.SystemMemory);
            }
            _device.GetRenderTargetData(SurfaceOf(target), _readback);
            var rect = _readback.LockRectangle(LockFlags.ReadOnly);
            var bytes = new byte[target.Width * target.Height * 4];
            for (int y = 0; y < target.Height; y++)
                Marshal.Copy(rect.Data.DataPointer + y * rect.Pitch, bytes, y * target.Width * 4, target.Width * 4);
            _readback.UnlockRectangle();
            return bytes;
        }

        public Bitmap CaptureBackBuffer()
        {
            // Never dispose this surface: SlimDX hands back the same managed object as the cached back buffer (review C1).
            Surface backbuffer = SurfaceOf(_backBuffer);
            using (var stream = Surface.ToStream(backbuffer, ImageFileFormat.Png))
            using (var image = new Bitmap(stream))
                return new Bitmap(image); // detach from the stream (GDI+ needs a stream-backed bitmap's stream kept open)
        }

        public void Dispose()
        {
            ReleaseDeviceObjects();
            _readback?.Dispose();
            _readback = null;

            _device?.Direct3D?.Dispose();
            _device?.Dispose();
            _device = null;

            _normal?.Dispose();
            _grayScale?.Dispose();
            _magic?.Dispose();
        }
    }
}
