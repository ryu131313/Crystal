using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D11Blend = Vortice.Direct3D11.Blend;
using DxgiFormat = Vortice.DXGI.Format;
using SColor4 = SlimDX.Color4;
using SMatrix = SlimDX.Matrix;
using SVector2 = SlimDX.Vector2;
using SVector3 = SlimDX.Vector3;
using SVector4 = SlimDX.Vector4;
using VColor4 = Vortice.Mathematics.Color4;

namespace Client.MirGraphics.Rendering
{
    /// <summary>
    /// Direct3D 11 backend via Vortice (roadmap C4, [Graphics] Renderer=D3D11).
    ///
    /// It emulates the exact D3D9 + ID3DXSprite behaviour DXManager relies on, so the same DXManager state logic gives the
    /// same picture (golden parity, tools\rendertest.ps1 -Mode Parity):
    /// <list type="bullet">
    /// <item>A D3D9-style device state (alpha blend enable, source/destination factor, blend factor, pixel shader, alpha
    /// test) is tracked here and turned into D3D11 state only when sprites are flushed, as ID3DXSprite did.</item>
    /// <item>SpriteBegin applies the sprite defaults (SrcAlpha/InvSrcAlpha, no pixel shader; alpha test Greater 0 with AlphaBlend only) and,
    /// for AlphaBlend (no DoNotSaveState), saves the state that SpriteEnd restores.</item>
    /// <item>Sprites are drawn strictly in submission order; positions are transformed on the CPU when queued.</item>
    /// <item>D3D9 never enabled separate alpha blending, so the alpha channel blends with the alpha form of the colour
    /// factors (SourceColor → SourceAlpha, ...).</item>
    /// <item>Vertex colours are 8-bit, truncated like SlimDX Color4.ToArgb; textures are straight-alpha BGRA.</item>
    /// <item>No half-pixel offset anywhere (D3DX applied D3D9's internally); lines are 1-pixel quads shifted by +0.5.</item>
    /// </list>
    /// Fullscreen is a borderless window with a game-sized back buffer stretched by DXGI (flip model, no exclusive mode),
    /// see DXManager.UsesBorderlessFullscreen. Main (UI) thread only.
    /// </summary>
    internal sealed unsafe class D3D11Backend : IRenderBackend
    {
        private const int MaxSprites = 4096;          // per flush
        private const int RingSprites = MaxSprites * 4;
        private const int VertexStride = 20;

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public float X, Y, U, V;
            public uint Colour;     // R | G << 8 | B << 16 | A << 24 (R8G8B8A8_UNorm)
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameConstants
        {
            public Matrix4x4 Projection;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PixelConstants
        {
            public uint AlphaTest;
            public uint Pad0, Pad1, Pad2;
        }

        /// <summary>The atmosphere constant buffer (b2): up to 128 float4s per pass (ground, reflection lamps), read by AtmosphereShaders.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct AtmosphereConstants
        {
            public fixed float C[128 * 4];
        }

        private sealed class TextureData
        {
            public ID3D11Texture2D Texture;
            public ID3D11ShaderResourceView Srv;
            public ID3D11RenderTargetView Rtv;
            public byte* Cpu;       // CPU copy of a lockable texture (D3D9 managed-pool equivalent; MImage.Data points here)
        }

        /// <summary>The D3D9 render states DXManager drives.</summary>
        private struct DeviceState
        {
            public bool BlendEnable;
            public RenderBlend Source, Destination;
            public Color Factor;
            public PixelShaderKind Shader;
            public bool AlphaTest;
            /// <summary>Write the alpha channel only (the wet-ground reflectivity pass; RGB is left untouched).</summary>
            public bool WriteAlphaOnly;
        }

        private sealed class DeviceLostException : Exception
        {
            public DeviceLostException(string message) : base(message) { }
        }

        // Assumptions about undocumented D3DX behaviour, settled against the D3D9 goldens (see Knowledgebase notes).
        private static readonly bool SpriteBeginResetsPixelShader = Environment.GetEnvironmentVariable("MIR2_D3D11_SPRITE_PS") != "keep";
        private static readonly string SamplerMode = Environment.GetEnvironmentVariable("MIR2_D3D11_SAMPLER") ?? "anisotropic"; // point | linear | auto | anisotropic

        private IntPtr _hwnd;
        private bool _vsync;
        private bool _windowed = true;
        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private IDXGISwapChain1 _swapChain;
        private bool _allowTearing;
        private bool _bltModel;   // swap model chosen at Create: blit when uncapped, flip with vsync (see Create)
        private SwapChainFlags _swapChainFlags;

        private ID3D11VertexShader _vs;
        private ID3D11PixelShader _psModulate, _psGrayscale, _psLight, _psLightComposite, _psRainfall, _psLightning, _psGround, _psSnapshot, _psReflect;
        private RenderTexture _atmoSource1, _atmoSource2, _atmoSource3;
        private ID3D11InputLayout _layout;
        private ID3D11Buffer _vertexBuffer, _indexBuffer, _frameBuffer, _pixelBuffer, _atmosphereBuffer;
        private ID3D11SamplerState _samplerPoint, _samplerLinear, _samplerAnisotropic;
        private ID3D11RasterizerState _rasterizer;
        private ID3D11DepthStencilState _depthOff;
        private readonly Dictionary<int, ID3D11BlendState> _blendStates = new Dictionary<int, ID3D11BlendState>();
        private bool _grayscaleAvailable;
        private bool _atmosphereAvailable;   // the atmosphere HLSL compiled: [Graphics] Atmosphere can run

        private RenderTexture _backBuffer, _target, _white;
        private ID3D11Texture2D _lastFrame;   // copy of the back buffer taken just before Present (screenshots)
        private bool _lastFrameFresh;         // _lastFrame holds the most recently presented frame

        private DeviceState _state, _saved;
        private bool _hasSaved;
        private SMatrix _transform = SMatrix.Identity;
        private bool _transformIdentity = true;

        private readonly Vertex[] _vertices = new Vertex[MaxSprites * 4];
        private readonly RenderTexture[] _textures = new RenderTexture[MaxSprites];
        private readonly bool[] _scaled = new bool[MaxSprites];
        private readonly bool[] _linear = new bool[MaxSprites]; // sample this sprite with the linear sampler (BlitLinear)
        private int _count;
        private int _ringOffset;
        private uint _boundAlphaTest = uint.MaxValue;
        // Redundant-state filter: only changed bindings reach the context.
        private ID3D11BlendState _boundBlend;
        private Color _boundFactor;
        private ID3D11PixelShader _boundShader;
        private ID3D11SamplerState _boundSampler;
        private ID3D11ShaderResourceView _boundSrv;
        // ID3DXSprite builds its screen-space projection from the viewport at Begin, and D3D9 SetRenderTarget resets the
        // viewport to the new target: after a mid-sprite switch to a differently sized target, draws scale until the next
        // Begin (golden S14). The viewport always follows the current target; the projection follows this size.
        private Size _spriteProjectionSize, _boundProjectionSize, _boundViewportSize;

        public RendererType Type => RendererType.D3D11;
        public RenderTexture BackBuffer => _backBuffer;

        // ================================================================ device

        public void Create(IntPtr windowHandle, int width, int height, bool windowed, bool vsync)
        {
            _hwnd = windowHandle;
            _vsync = vsync;
            _windowed = windowed;

            DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
            if (D3D11.SdkLayersAvailable()) flags |= DeviceCreationFlags.Debug;
#endif
            FeatureLevel[] levels = { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out _device, out _, out _context).CheckError();

            // Before the swap chain: if shader compilation fails (d3dcompiler_47 missing...), no flip-model swap chain has
            // been bound to the window yet and the D3D9 fallback gets a clean HWND.
            CreatePipeline();

            ApplyFrameLatency();

            using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
            using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
            using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
            {
                // Swap model (2026-10-11 bench, uncapped login scene, Release): windowed flip 6,700-7,100 fps, blit 11,200-11,700,
                // D3D9 10,400-10,600; borderless fullscreen flip ~7,600, blit ~8,500. A windowed flip present costs ~0.13 ms against
                // ~0.08 ms for a blit, which is the whole frame at these rates. Uncapped (FPSCap=false) therefore uses the blit model,
                // like D3D9 windowed. With vsync the flip model stays: lower latency (independent flip in borderless fullscreen) and
                // variable refresh, neither of which matters when presenting far above the refresh rate. The blit model cannot use
                // the tearing flag; its Present(0) is not paced by DWM, so it needs none.
                _bltModel = !vsync;
                _allowTearing = !_bltModel && QueryTearing(factory);
                _swapChainFlags = _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None;
                var description = new SwapChainDescription1
                {
                    Width = (uint)Math.Max(1, width),
                    Height = (uint)Math.Max(1, height),
                    Format = DxgiFormat.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = _bltModel ? 1u : 3u,  // flip: a spare while DWM holds one (flip-model guidance)
                    Scaling = Scaling.Stretch,          // borderless fullscreen: DXGI stretches the game-sized buffer
                    SwapEffect = _bltModel ? SwapEffect.Discard : SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = _swapChainFlags,
                };
                _swapChain = factory.CreateSwapChainForHwnd(_device, windowHandle, description);
                factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter); // CMain owns Alt+Enter
            }

            CreateBackBuffer();
            _white = CreateTexture(1, 1);
            byte* w = LockTexture(_white);
            *(uint*)w = 0xFFFFFFFF;
            UnlockTexture(_white);

            // D3D9 device defaults.
            _state = new DeviceState { BlendEnable = false, Source = RenderBlend.One, Destination = RenderBlend.Zero, Factor = Color.White, Shader = PixelShaderKind.None, AlphaTest = false };
            BindPipeline();
        }

        private static bool QueryTearing(IDXGIFactory2 factory)
        {
            try
            {
                using (IDXGIFactory5 factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                    return factory5 != null && factory5.PresentAllowTearing;
            }
            catch
            {
                return false; // older OS/driver: keep vblank-paced presents
            }
        }

        private const string ShaderSource = @"
cbuffer Frame : register(b0) { row_major float4x4 Projection; };
cbuffer Pixel : register(b1) { uint AlphaTest; uint3 Pad; };
Texture2D SpriteTex : register(t0);
SamplerState SpriteSmp : register(s0);
struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 col : COLOR0; };
VSOut Sprite_VS(VSIn i) { VSOut o; o.pos = mul(float4(i.pos, 0.0f, 1.0f), Projection); o.uv = i.uv; o.col = i.col; return o; }
float4 ApplyAlphaTest(float4 c) { if (AlphaTest != 0 && c.a <= 0.0f) discard; return c; }
float4 Modulate_PS(VSOut i) : SV_Target { return ApplyAlphaTest(SpriteTex.Sample(SpriteSmp, i.uv) * i.col); }
float4 Grayscale_PS(VSOut i) : SV_Target
{
    float4 t = SpriteTex.Sample(SpriteSmp, i.uv);
    float l = saturate(dot(t.rgb, float3(0.3f, 0.59f, 0.11f)));
    return ApplyAlphaTest(float4(l, l, l, t.a));
}
// Enhanced lighting ([Graphics] Lighting=Enhanced). Light_PS: an elliptical falloff across the quad, (1 - r^2)^2, scaled
// by the light colour and an intensity of alpha * 4, added (One, One) into a float light map, so lights never clip.
float4 Light_PS(VSOut i) : SV_Target
{
    float2 d = i.uv * 2.0f - 1.0f;
    float f = 1.0f - saturate(dot(d, d));
    return float4(i.col.rgb * (i.col.a * 4.0f * f * f), 1.0f);
}
// LightComposite_PS: the light map soft-knees above 1 towards 2 (no hard clip) and is output halved; the blend
// (DestinationColor, SourceColor) doubles it again: scene * light, with light in 0..2 so lights can brighten the scene.
float4 LightComposite_PS(VSOut i) : SV_Target
{
    float3 l = max(SpriteTex.Sample(SpriteSmp, i.uv).rgb, 0.0f);
    float3 over = max(l - 1.0f, 0.0f);
    l = min(l, 1.0f) + over / (1.0f + over);
    return float4(l * 0.5f, 0.5f);
}
";

        private void CreatePipeline()
        {
            ReadOnlyMemory<byte> vs = Compiler.Compile(ShaderSource, "Sprite_VS", "Sprite.hlsl", "vs_4_0");
            ReadOnlyMemory<byte> psModulate = Compiler.Compile(ShaderSource, "Modulate_PS", "Sprite.hlsl", "ps_4_0");
            ReadOnlyMemory<byte> psGrayscale = Compiler.Compile(ShaderSource, "Grayscale_PS", "Sprite.hlsl", "ps_4_0");
            ReadOnlyMemory<byte> psLight = Compiler.Compile(ShaderSource, "Light_PS", "Sprite.hlsl", "ps_4_0");
            ReadOnlyMemory<byte> psLightComposite = Compiler.Compile(ShaderSource, "LightComposite_PS", "Sprite.hlsl", "ps_4_0");
            _vs = _device.CreateVertexShader(vs.Span);
            _psModulate = _device.CreatePixelShader(psModulate.Span);
            _psGrayscale = _device.CreatePixelShader(psGrayscale.Span);
            _psLight = _device.CreatePixelShader(psLight.Span);
            _psLightComposite = _device.CreatePixelShader(psLightComposite.Span);

            // D3D9 parity: the grayscale effect exists only when Data\Shaders\grayscale.ps does.
            _grayscaleAvailable = File.Exists(Settings.ShadersPath + "grayscale.ps");

            _layout = _device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, DxgiFormat.R32G32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, DxgiFormat.R32G32_Float, 8, 0),
                new InputElementDescription("COLOR", 0, DxgiFormat.R8G8B8A8_UNorm, 16, 0),
            }, vs.Span);

            _vertexBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(RingSprites * 4 * VertexStride),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            });

            var indices = new ushort[MaxSprites * 6];
            for (int s = 0; s < MaxSprites; s++)
            {
                int v = s * 4, i = s * 6;
                indices[i] = (ushort)v; indices[i + 1] = (ushort)(v + 1); indices[i + 2] = (ushort)(v + 2);
                indices[i + 3] = (ushort)(v + 2); indices[i + 4] = (ushort)(v + 1); indices[i + 5] = (ushort)(v + 3);
            }
            _indexBuffer = _device.CreateBuffer<ushort>(indices, new BufferDescription
            {
                ByteWidth = (uint)(indices.Length * 2),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.IndexBuffer,
            });

            _frameBuffer = _device.CreateBuffer(new BufferDescription { ByteWidth = (uint)Marshal.SizeOf<FrameConstants>(), Usage = ResourceUsage.Default, BindFlags = BindFlags.ConstantBuffer });
            _pixelBuffer = _device.CreateBuffer(new BufferDescription { ByteWidth = (uint)Marshal.SizeOf<PixelConstants>(), Usage = ResourceUsage.Default, BindFlags = BindFlags.ConstantBuffer });
            _atmosphereBuffer = _device.CreateBuffer(new BufferDescription { ByteWidth = (uint)Marshal.SizeOf<AtmosphereConstants>(), Usage = ResourceUsage.Default, BindFlags = BindFlags.ConstantBuffer });

            _samplerPoint = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never, MinLOD = 0f, MaxLOD = 0f,
            });
            _samplerLinear = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never, MinLOD = 0f, MaxLOD = 0f,
            });
            // ID3DXSprite sets anisotropic min/mag filtering when the device supports it (D3D9 golden S09: non-uniformly shrunk
            // sprites match only with anisotropic, 1:1 sprites are identical under any filter).
            _samplerAnisotropic = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.Anisotropic, MaxAnisotropy = 16,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never, MinLOD = 0f, MaxLOD = 0f,
            });
            _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, ScissorEnable = false, DepthClipEnable = true, MultisampleEnable = false,
            });
            _depthOff = _device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, StencilEnable = false });

            TryCompileAtmosphere();
        }

        /// <summary>
        /// Compiles the atmosphere weather passes ([Graphics] Atmosphere). A compile failure disables them for the run (logged)
        /// and leaves the already-built sprite pipeline and the classic weather sprites untouched.
        /// </summary>
        private void TryCompileAtmosphere()
        {
            try
            {
                ReadOnlyMemory<byte> psRain = Compiler.Compile(AtmosphereShaders.Source, "Rainfall_PS", "Atmosphere.hlsl", "ps_4_0");
                ReadOnlyMemory<byte> psLightning = Compiler.Compile(AtmosphereShaders.Source, "Lightning_PS", "Atmosphere.hlsl", "ps_4_0");
                ReadOnlyMemory<byte> psGround = Compiler.Compile(AtmosphereShaders.Source, "Ground_PS", "Atmosphere.hlsl", "ps_4_0");
                ReadOnlyMemory<byte> psSnapshot = Compiler.Compile(AtmosphereShaders.Source, "Snapshot_PS", "Atmosphere.hlsl", "ps_4_0");
                ReadOnlyMemory<byte> psReflect = Compiler.Compile(AtmosphereShaders.Source, "Reflect_PS", "Atmosphere.hlsl", "ps_4_0");
                _psRainfall = _device.CreatePixelShader(psRain.Span);
                _psLightning = _device.CreatePixelShader(psLightning.Span);
                _psGround = _device.CreatePixelShader(psGround.Span);
                _psSnapshot = _device.CreatePixelShader(psSnapshot.Span);
                _psReflect = _device.CreatePixelShader(psReflect.Span);
                _atmosphereAvailable = true;
            }
            catch (Exception ex)
            {
                _atmosphereAvailable = false;
                CMain.SaveError("[D3D11] atmosphere shaders failed to compile, weather runs as classic sprites: " + ex.Message);
            }
        }

        /// <summary>Binds the whole fixed pipeline (never trust sticky context state after ClearState/resize).</summary>
        private void BindPipeline()
        {
            _boundProjectionSize = Size.Empty;
            _boundViewportSize = Size.Empty;
            _boundBlend = null; _boundShader = null; _boundSampler = null; _boundSrv = null;
            _context.IASetInputLayout(_layout);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetVertexBuffer(0, _vertexBuffer, VertexStride, 0);
            _context.IASetIndexBuffer(_indexBuffer, DxgiFormat.R16_UInt, 0);
            _context.VSSetShader(_vs);
            _context.VSSetConstantBuffer(0, _frameBuffer);
            _context.PSSetConstantBuffer(1, _pixelBuffer);
            _context.PSSetConstantBuffer(2, _atmosphereBuffer);
            _context.RSSetState(_rasterizer);
            _context.OMSetDepthStencilState(_depthOff);
            _boundAlphaTest = uint.MaxValue;
            BindTarget(_target ?? _backBuffer);
        }

        /// <summary>
        /// Frames the CPU may queue ahead of the GPU. With vsync, one: every queued frame is a whole refresh of input lag, and
        /// D3D9's windowed Discard swap had no extra queue (review: up to 2 frames more input lag). Uncapped, three (the D3D9
        /// driver default): a queued frame is a fraction of a millisecond, and with one the CPU waited for the GPU on every
        /// Present, which held D3D11 to ~1,700 fps where D3D9 ran ~10,000 on the login scene (2026-10-11 bench: 1 = 1,704 fps,
        /// 2 = 2,846, 3 = 5,287, 4+ = ~6,000).
        /// </summary>
        private void ApplyFrameLatency()
        {
            using (IDXGIDevice1 dxgiDevice1 = _device.QueryInterfaceOrNull<IDXGIDevice1>())
                dxgiDevice1?.SetMaximumFrameLatency(_vsync ? 1u : 3u);
        }

        private void CreateBackBuffer()
        {
            using (ID3D11Texture2D buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                var desc = buffer.Description;
                var back = new RenderTexture((int)desc.Width, (int)desc.Height, true, backBuffer: true);
                back.Native = new TextureData { Rtv = _device.CreateRenderTargetView(buffer) };
                _backBuffer = back;
            }
            _target = _backBuffer;
        }

        private void KeepLastFrame()
        {
            using (ID3D11Texture2D buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                var desc = buffer.Description;
                if (_lastFrame == null || _lastFrame.Description.Width != desc.Width || _lastFrame.Description.Height != desc.Height)
                {
                    _lastFrame?.Dispose();
                    desc.BindFlags = BindFlags.None;
                    desc.Usage = ResourceUsage.Default;
                    desc.CPUAccessFlags = CpuAccessFlags.None;
                    desc.MiscFlags = ResourceOptionFlags.None;
                    _lastFrame = _device.CreateTexture2D(desc);
                }
                _context.CopyResource(_lastFrame, buffer);
            }
        }

        private void ReleaseBackBuffer()
        {
            if (_backBuffer == null) return;
            if (_backBuffer.Native is TextureData data) data.Rtv?.Dispose();
            _backBuffer.Native = null;
            _backBuffer.Disposed = true;
            _backBuffer = null;
            _target = null;
        }

        // ================================================================ textures

        public RenderTexture CreateTexture(int width, int height)
        {
            var texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = DxgiFormat.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            var t = new RenderTexture(width, height, false);
            t.Native = new TextureData
            {
                Texture = texture,
                Srv = _device.CreateShaderResourceView(texture),
                Cpu = (byte*)NativeMemory.AllocZeroed((nuint)((long)width * height * 4)),
            };
            return t;
        }

        public bool SupportsEnhancedLighting => true;

        public RenderTexture CreateLightTarget(int width, int height)
        {
            var texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = DxgiFormat.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            var t = new RenderTexture(width, height, true);
            t.Native = new TextureData { Texture = texture, Srv = _device.CreateShaderResourceView(texture), Rtv = _device.CreateRenderTargetView(texture) };
            return t;
        }

        public RenderTexture CreateRenderTarget(int width, int height)
        {
            var texture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = DxgiFormat.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            var t = new RenderTexture(width, height, true);
            t.Native = new TextureData { Texture = texture, Srv = _device.CreateShaderResourceView(texture), Rtv = _device.CreateRenderTargetView(texture) };
            return t;
        }

        public bool SupportsAtmosphere => _atmosphereAvailable;

        public RenderTexture CreateRenderTarget(int width, int height, RenderTargetFormat format) =>
            format == RenderTargetFormat.Rgba16F ? CreateLightTarget(width, height) : CreateRenderTarget(width, height);

        public byte* LockTexture(RenderTexture texture) => ((TextureData)texture.Native).Cpu;

        public void UnlockTexture(RenderTexture texture)
        {
            var data = (TextureData)texture.Native;
            // Queued sprites sample the texture when they are flushed, after this upload: the same as D3DX deferring draws
            // past a D3D9 Lock/Unlock.
            _context.UpdateSubresource(data.Texture, 0, null, (IntPtr)data.Cpu, (uint)(texture.Width * 4), 0);
        }

        public void DestroyTexture(RenderTexture texture)
        {
            if (texture.Native is not TextureData data) return;
            if (_count > 0 && !DeviceRemoved) Flush(); // queued sprites may still reference it (D3DX held a reference)
            else if (_count > 0) { Array.Clear(_textures, 0, _count); _count = 0; }
            if (_target == texture) { _target = _backBuffer; if (_backBuffer != null) BindTarget(_backBuffer); }
            if (data.Srv != null && data.Srv == _boundSrv) { _context.PSSetShaderResource(0, null); _boundSrv = null; }
            data.Rtv?.Dispose();
            data.Srv?.Dispose();
            data.Texture?.Dispose();
            if (data.Cpu != null) { NativeMemory.Free(data.Cpu); data.Cpu = null; }
        }

        // ================================================================ frame

        private void BindTarget(RenderTexture target)
        {
            if (target?.Native is not TextureData data || data.Rtv == null) return;
            _context.PSSetShaderResource(0, null); // an SRV still bound would be silently unbound when set as RTV
            _boundSrv = null;
            _context.OMSetRenderTargets(data.Rtv);
            _context.RSSetViewport(0f, 0f, target.Width, target.Height);
            if (_spriteProjectionSize.IsEmpty) _spriteProjectionSize = new Size(target.Width, target.Height);
        }

        private void BindProjection(Size size)
        {
            Size viewport = CurrentTargetSize;
            if ((size == _boundProjectionSize && viewport == _boundViewportSize) || size.Width <= 0 || size.Height <= 0 || viewport.IsEmpty) return;

            // D3DX shifted sprites by -0.5 in projection space (D3D9's pixel-centre offset). When the projection size S and the
            // viewport V agree that cancels D3D11's +0.5 pixel centre exactly; when they differ (mid-sprite target switch) the
            // shift is scaled by V/S, so add d = -0.5 + 0.5 * S/V per axis. d = 0 for every normal draw.
            float dx = -0.5f + 0.5f * size.Width / viewport.Width;
            float dy = -0.5f + 0.5f * size.Height / viewport.Height;
            var frame = new FrameConstants
            {
                Projection = new Matrix4x4(
                    2f / size.Width, 0f, 0f, 0f,
                    0f, -2f / size.Height, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    -1f + 2f * dx / size.Width, 1f - 2f * dy / size.Height, 0f, 1f),
            };
            _context.UpdateSubresource(in frame, _frameBuffer);
            _boundProjectionSize = size;
            _boundViewportSize = viewport;
        }

        private Size CurrentTargetSize => _target == null ? Size.Empty : new Size(_target.Width, _target.Height);

        public void SetRenderTarget(RenderTexture target)
        {
            Flush();
            if (target == _target) return; // DXManager.SetBlend re-sets the current target on every call; nothing to rebind
            _target = target;
            BindTarget(target);
        }

        public void Clear(Color colour)
        {
            Flush();
            if (_target?.Native is not TextureData data || data.Rtv == null) return;
            _context.ClearRenderTargetView(data.Rtv, new VColor4(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f));
        }

        public void BeginScene() => BindPipeline();

        public void EndScene() => Flush();

        public void Present(bool keepFrame)
        {
            Flush();
            _lastFrameFresh = keepFrame;
            if (keepFrame) KeepLastFrame();
            // Uncapped presents use the blit model (see Create), which is never paced by DWM. A flip-model swap chain (vsync)
            // would need the tearing flag to run uncapped windowed: without it DWM paces it to the refresh rate. _allowTearing
            // is false for the blit model, where the flag is invalid.
            PresentFlags flags = !_vsync && _allowTearing ? PresentFlags.AllowTearing : PresentFlags.None;
            Result result = _swapChain.Present(_vsync ? 1u : 0u, flags);
            if (result.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || result.Code == Vortice.DXGI.ResultCode.DeviceReset.Code)
                throw new DeviceLostException("D3D11 device removed/reset at Present (0x" + result.Code.ToString("X8") + ")");
            result.CheckError(); // DXGI_STATUS_OCCLUDED is a success code and passes
        }

        // ================================================================ sprite (ID3DXSprite emulation)

        public void SpriteBegin(bool alphaBlend)
        {
            if (alphaBlend)
            {
                _saved = _state;    // SpriteFlags.AlphaBlend without DoNotSaveState: End restores this
                _hasSaved = true;
            }
            else
                _hasSaved = false;  // DoNotSaveState

            _state.BlendEnable = alphaBlend;
            _state.Source = RenderBlend.SourceAlpha;
            _state.Destination = RenderBlend.InverseSourceAlpha;
            // Alpha test (Greater 0) comes with the AlphaBlend flag only: golden S13 shows DoNotSaveState sprites drawing
            // zero-alpha texels under the opacity factor blend.
            _state.AlphaTest = alphaBlend;
            if (SpriteBeginResetsPixelShader) _state.Shader = PixelShaderKind.None;
            _spriteProjectionSize = CurrentTargetSize;
        }

        public void SpriteEnd()
        {
            Flush();
            if (_hasSaved) _state = _saved;
            _hasSaved = false;
        }

        public void SpriteFlush() => Flush();

        public void SetSpriteTransform(SMatrix transform)
        {
            _transform = transform;
            _transformIdentity = transform == SMatrix.Identity;
        }

        public void SpriteDraw(RenderTexture texture, Rectangle? sourceRect, SVector3? position, SColor4 colour)
        {
            if (texture == null || texture.Disposed || texture.Native == null) return;
            if (_count >= MaxSprites) Flush();

            Rectangle src = sourceRect ?? new Rectangle(0, 0, texture.Width, texture.Height);
            float px = position?.X ?? 0f, py = position?.Y ?? 0f;
            float x0 = px, y0 = py, x1 = px + src.Width, y1 = py + src.Height;
            float u0 = (float)src.Left / texture.Width, v0 = (float)src.Top / texture.Height;
            float u1 = (float)src.Right / texture.Width, v1 = (float)src.Bottom / texture.Height;

            uint c = PackColour(colour);
            int v = _count * 4;
            if (_transformIdentity)
            {
                _vertices[v] = new Vertex { X = x0, Y = y0, U = u0, V = v0, Colour = c };
                _vertices[v + 1] = new Vertex { X = x1, Y = y0, U = u1, V = v0, Colour = c };
                _vertices[v + 2] = new Vertex { X = x0, Y = y1, U = u0, V = v1, Colour = c };
                _vertices[v + 3] = new Vertex { X = x1, Y = y1, U = u1, V = v1, Colour = c };
            }
            else
            {
                _vertices[v] = Transformed(x0, y0, u0, v0, c);
                _vertices[v + 1] = Transformed(x1, y0, u1, v0, c);
                _vertices[v + 2] = Transformed(x0, y1, u0, v1, c);
                _vertices[v + 3] = Transformed(x1, y1, u1, v1, c);
            }
            _textures[_count] = texture;
            _scaled[_count] = !_transformIdentity;
            _linear[_count] = false;
            _count++;
        }

        public void DrawLight(float x, float y, float width, float height, Color colour, float intensity)
        {
            if (_white == null || width <= 0f || height <= 0f) return;
            if (_count >= MaxSprites) Flush();

            float x1 = x + width, y1 = y + height;
            uint c = (uint)colour.R | ((uint)colour.G << 8) | ((uint)colour.B << 16) | (ToByte(intensity * 0.25f) << 24);
            int v = _count * 4;
            if (_transformIdentity)
            {
                _vertices[v] = new Vertex { X = x, Y = y, U = 0f, V = 0f, Colour = c };
                _vertices[v + 1] = new Vertex { X = x1, Y = y, U = 1f, V = 0f, Colour = c };
                _vertices[v + 2] = new Vertex { X = x, Y = y1, U = 0f, V = 1f, Colour = c };
                _vertices[v + 3] = new Vertex { X = x1, Y = y1, U = 1f, V = 1f, Colour = c };
            }
            else
            {
                _vertices[v] = Transformed(x, y, 0f, 0f, c);
                _vertices[v + 1] = Transformed(x1, y, 1f, 0f, c);
                _vertices[v + 2] = Transformed(x, y1, 0f, 1f, c);
                _vertices[v + 3] = Transformed(x1, y1, 1f, 1f, c);
            }
            _textures[_count] = _white; // Light_PS reads only the UVs and the vertex colour
            _scaled[_count] = false;
            _linear[_count] = false;
            _count++;
        }

        public void SetAtmosphereConstants(ReadOnlySpan<SVector4> constants)
        {
            var c = new AtmosphereConstants();
            int n = Math.Min(constants.Length, 128);
            for (int i = 0; i < n; i++)
            {
                c.C[i * 4] = constants[i].X;
                c.C[i * 4 + 1] = constants[i].Y;
                c.C[i * 4 + 2] = constants[i].Z;
                c.C[i * 4 + 3] = constants[i].W;
            }
            _context.UpdateSubresource(in c, _atmosphereBuffer);
        }

        public void SetAtmosphereSource(int slot, RenderTexture texture)
        {
            switch (slot)
            {
                case 1: _atmoSource1 = texture; break;
                case 2: _atmoSource2 = texture; break;
                case 3: _atmoSource3 = texture; break;
            }
        }

        private void BindAtmosphereSources()
        {
            _context.PSSetSampler(1, _samplerLinear);
            _context.PSSetSampler(2, _samplerLinear);
            _context.PSSetSampler(3, _samplerLinear);
            BindAtmosphereSource(1, _atmoSource1);
            BindAtmosphereSource(2, _atmoSource2);
            BindAtmosphereSource(3, _atmoSource3);
        }

        private void BindAtmosphereSource(int slot, RenderTexture texture)
        {
            var srv = texture != null && !texture.Disposed && texture.Native is TextureData data ? data.Srv : null;
            _context.PSSetShaderResource((uint)slot, srv);
        }

        private void UnbindAtmosphereSources()
        {
            _context.PSSetShaderResource(1, null);
            _context.PSSetShaderResource(2, null);
            _context.PSSetShaderResource(3, null);
        }

        public void DrawFullscreen(FullscreenKind kind, RenderTexture source, ReadOnlySpan<SVector4> inputs)
        {
            if (!_atmosphereAvailable || _white == null) return;
            Size size = CurrentTargetSize;
            if (size.Width <= 0 || size.Height <= 0) return;

            SetAtmosphereConstants(inputs);
            Flush();
            if (_count >= MaxSprites) Flush();

            // One fullscreen quad through the sprite path; the pass samples its source at t0 and reads the atmosphere cbuffer.
            float w = size.Width, h = size.Height;
            const uint c = 0xFFFFFFFFu;
            int v = _count * 4;
            _vertices[v] = new Vertex { X = 0f, Y = 0f, U = 0f, V = 0f, Colour = c };
            _vertices[v + 1] = new Vertex { X = w, Y = 0f, U = 1f, V = 0f, Colour = c };
            _vertices[v + 2] = new Vertex { X = 0f, Y = h, U = 0f, V = 1f, Colour = c };
            _vertices[v + 3] = new Vertex { X = w, Y = h, U = 1f, V = 1f, Colour = c };
            _textures[_count] = source ?? _white;
            _scaled[_count] = false;
            _linear[_count] = kind == FullscreenKind.BlitLinear;
            _count++;

            // The pass owns its blend, shader and projection for one flush; the DXManager-visible state is restored after.
            DeviceState savedState = _state;
            SMatrix savedTransform = _transform;
            bool savedIdentity = _transformIdentity;
            Size savedProjection = _spriteProjectionSize;
            _transform = SMatrix.Identity;
            _transformIdentity = true;
            _spriteProjectionSize = size;

            switch (kind)
            {
                case FullscreenKind.Lightning:
                    // the flash adds (SourceAlpha, One) so it only brightens
                    _state = new DeviceState { BlendEnable = true, Source = RenderBlend.SourceAlpha, Destination = RenderBlend.One, Factor = Color.White, Shader = PixelShaderKind.Lightning, AlphaTest = false };
                    break;
                case FullscreenKind.GroundWet:
                    // puddles and ripples tint/darken the floor; the depth fields carry the alpha
                    _state = new DeviceState { BlendEnable = true, Source = RenderBlend.SourceAlpha, Destination = RenderBlend.InverseSourceAlpha, Factor = Color.White, Shader = PixelShaderKind.Ground, AlphaTest = false };
                    break;
                case FullscreenKind.GroundMask:
                    // reflectivity goes into the map target's alpha only (colour masked off)
                    _state = new DeviceState { BlendEnable = false, Source = RenderBlend.One, Destination = RenderBlend.Zero, Factor = Color.White, Shader = PixelShaderKind.Ground, AlphaTest = false, WriteAlphaOnly = true };
                    break;
                case FullscreenKind.Snapshot:
                    _state = new DeviceState { BlendEnable = false, Source = RenderBlend.One, Destination = RenderBlend.Zero, Factor = Color.White, Shader = PixelShaderKind.Snapshot, AlphaTest = false };
                    break;
                case FullscreenKind.ReflectObjects:
                case FullscreenKind.ReflectLamps:
                    _state = new DeviceState { BlendEnable = false, Source = RenderBlend.One, Destination = RenderBlend.Zero, Factor = Color.White, Shader = PixelShaderKind.Reflect, AlphaTest = false };
                    break;
                case FullscreenKind.BlitLinear:
                    // the caller owns the blend (DXManager.SetBlendFactors); pass the texture through unchanged
                    _state = new DeviceState { BlendEnable = savedState.BlendEnable, Source = savedState.Source, Destination = savedState.Destination, Factor = savedState.Factor, Shader = PixelShaderKind.None, AlphaTest = false };
                    break;
                default:
                    // rain is alpha-blended over the scene
                    _state = new DeviceState { BlendEnable = true, Source = RenderBlend.SourceAlpha, Destination = RenderBlend.InverseSourceAlpha, Factor = Color.White, Shader = PixelShaderKind.Rainfall, AlphaTest = false };
                    break;
            }

            BindAtmosphereSources();
            Flush();
            UnbindAtmosphereSources();

            _state = savedState;
            _transform = savedTransform;
            _transformIdentity = savedIdentity;
            _spriteProjectionSize = savedProjection;
        }

        private Vertex Transformed(float x, float y, float u, float v, uint c) => new Vertex
        {
            X = x * _transform.M11 + y * _transform.M21 + _transform.M41,
            Y = x * _transform.M12 + y * _transform.M22 + _transform.M42,
            U = u,
            V = v,
            Colour = c,
        };

        /// <summary>Color4 → 8-bit like SlimDX Color4.ToArgb (truncating), packed R8G8B8A8.</summary>
        private static uint PackColour(SColor4 colour)
        {
            uint r = ToByte(colour.Red), g = ToByte(colour.Green), b = ToByte(colour.Blue), a = ToByte(colour.Alpha);
            return r | (g << 8) | (b << 16) | (a << 24);
        }

        private static uint ToByte(float value)
        {
            float scaled = value * 255.0f;
            if (scaled <= 0f) return 0;
            if (scaled >= 255f) return 255;
            return (uint)scaled;
        }

        private void Flush()
        {
            if (_count == 0) return;

            try
            {
                FlushQueued();
            }
            finally
            {
                // Always empty the queue, even when the device was removed mid-flush (TDR), so cleanup and Reset can run.
                Array.Clear(_textures, 0, _count);
                Array.Clear(_linear, 0, _count);
                _count = 0;
            }
        }

        private void FlushQueued()
        {
            ApplyState(_state);
            BindProjection(_spriteProjectionSize);

            int offset = _ringOffset;
            MapMode mode = MapMode.WriteNoOverwrite;
            if (offset + _count > RingSprites)
            {
                offset = 0;
                mode = MapMode.WriteDiscard;
            }
            MappedSubresource mapped = _context.Map(_vertexBuffer, 0, mode);
            fixed (Vertex* src = _vertices)
            {
                long bytes = (long)_count * 4 * VertexStride;
                Buffer.MemoryCopy(src, (byte*)mapped.DataPointer + (long)offset * 4 * VertexStride, bytes, bytes);
            }
            _context.Unmap(_vertexBuffer, 0);

            // One DrawIndexed per run of sprites sharing a texture and sampler, in submission order.
            int start = 0;
            while (start < _count)
            {
                int end = start + 1;
                while (end < _count && _textures[end] == _textures[start] && _scaled[end] == _scaled[start] && _linear[end] == _linear[start]) end++;

                var sampler = SamplerFor(_scaled[start], _linear[start]);
                if (sampler != _boundSampler) { _context.PSSetSampler(0, sampler); _boundSampler = sampler; }
                var srv = ((TextureData)_textures[start].Native).Srv;
                if (srv != _boundSrv) { _context.PSSetShaderResource(0, srv); _boundSrv = srv; }
                _context.DrawIndexed((uint)((end - start) * 6), 0, (offset + start) * 4);
                start = end;
            }

            _ringOffset = offset + _count;
        }

        private ID3D11SamplerState SamplerFor(bool scaled, bool linear) => linear ? _samplerLinear : SamplerMode switch
        {
            "linear" => _samplerLinear,
            "point" => _samplerPoint,
            "anisotropic" => _samplerAnisotropic,
            "auto-linear" => scaled ? _samplerLinear : _samplerPoint,
            "auto" => scaled ? _samplerAnisotropic : _samplerPoint,
            _ => _samplerAnisotropic, // ID3DXSprite: anisotropic for every draw (exact for 1:1 sprites, golden S09/S14)
        };

        private void ApplyState(DeviceState state)
        {
            var blend = BlendStateFor(state);
            if (blend != _boundBlend || state.Factor != _boundFactor)
            {
                _context.OMSetBlendState(blend, new VColor4(state.Factor.R / 255f, state.Factor.G / 255f, state.Factor.B / 255f, state.Factor.A / 255f), uint.MaxValue);
                _boundBlend = blend;
                _boundFactor = state.Factor;
            }
            var shader = state.Shader switch
            {
                PixelShaderKind.Grayscale => _psGrayscale,
                PixelShaderKind.Light => _psLight,
                PixelShaderKind.LightComposite => _psLightComposite,
                PixelShaderKind.Rainfall => _psRainfall,
                PixelShaderKind.Lightning => _psLightning,
                PixelShaderKind.Ground => _psGround,
                PixelShaderKind.Snapshot => _psSnapshot,
                PixelShaderKind.Reflect => _psReflect,
                _ => _psModulate,
            };
            if (shader != _boundShader) { _context.PSSetShader(shader); _boundShader = shader; }
            uint alphaTest = state.AlphaTest ? 1u : 0u;
            if (alphaTest != _boundAlphaTest)
            {
                var pixel = new PixelConstants { AlphaTest = alphaTest };
                _context.UpdateSubresource(in pixel, _pixelBuffer);
                _boundAlphaTest = alphaTest;
            }
        }

        private ID3D11BlendState BlendStateFor(DeviceState state)
        {
            bool over = _alphaOver && state.BlendEnable;
            int key = (state.BlendEnable ? 1 + ((int)state.Source << 1) + ((int)state.Destination << 8) : 0) | (state.WriteAlphaOnly ? 1 << 16 : 0) | (over ? 1 << 17 : 0);
            if (_blendStates.TryGetValue(key, out var blend)) return blend;

            var description = new BlendDescription();
            description.RenderTarget[0].BlendEnable = state.BlendEnable;
            description.RenderTarget[0].SourceBlend = ToD3D11(state.Source, false);
            description.RenderTarget[0].DestinationBlend = ToD3D11(state.Destination, false);
            description.RenderTarget[0].BlendOperation = BlendOperation.Add;
            description.RenderTarget[0].SourceBlendAlpha = over ? D11Blend.One : ToD3D11(state.Source, true);
            description.RenderTarget[0].DestinationBlendAlpha = over ? D11Blend.InverseSourceAlpha : ToD3D11(state.Destination, true);
            description.RenderTarget[0].BlendOperationAlpha = BlendOperation.Add;
            description.RenderTarget[0].RenderTargetWriteMask = state.WriteAlphaOnly ? ColorWriteEnable.Alpha : ColorWriteEnable.All;
            blend = _device.CreateBlendState(description);
            _blendStates[key] = blend;
            return blend;
        }

        /// <summary>D3D9 factor → D3D11; for the alpha slot use the alpha form (no separate alpha blending in the client).</summary>
        private static D11Blend ToD3D11(RenderBlend blend, bool alpha) => blend switch
        {
            RenderBlend.Zero => D11Blend.Zero,
            RenderBlend.One => D11Blend.One,
            RenderBlend.SourceColor => alpha ? D11Blend.SourceAlpha : D11Blend.SourceColor,
            RenderBlend.InverseSourceColor => alpha ? D11Blend.InverseSourceAlpha : D11Blend.InverseSourceColor,
            RenderBlend.SourceAlpha => D11Blend.SourceAlpha,
            RenderBlend.InverseSourceAlpha => D11Blend.InverseSourceAlpha,
            RenderBlend.DestinationAlpha => D11Blend.DestinationAlpha,
            RenderBlend.InverseDestinationAlpha => D11Blend.InverseDestinationAlpha,
            RenderBlend.DestinationColor => alpha ? D11Blend.DestinationAlpha : D11Blend.DestinationColor,
            RenderBlend.InverseDestinationColor => alpha ? D11Blend.InverseDestinationAlpha : D11Blend.InverseDestinationColor,
            RenderBlend.BlendFactor => D11Blend.BlendFactor,
            RenderBlend.InverseBlendFactor => D11Blend.InverseBlendFactor,
            _ => D11Blend.One,
        };

        // ================================================================ render states

        public void SetAlphaBlendEnable(bool enable) => _state.BlendEnable = enable;
        public void SetSourceBlend(RenderBlend blend) => _state.Source = blend;
        public void SetDestinationBlend(RenderBlend blend) => _state.Destination = blend;
        public void SetSourceBlendAlpha(RenderBlend blend) { } // ignored by D3D9 too: SeparateAlphaBlendEnable is only set by SetAlphaOver

        // Outside DeviceState: sprite Begin / End save and restore the state, alpha over stays as set.
        private bool _alphaOver;
        public void SetAlphaOver(bool enable) => _alphaOver = enable;
        public void SetBlendOperationAdd() { }                 // the only blend operation the client uses
        public void SetBlendFactor(Color factor) => _state.Factor = factor;

        private PixelShaderKind Effective(PixelShaderKind kind) => kind switch
        {
            PixelShaderKind.Grayscale => _grayscaleAvailable ? kind : PixelShaderKind.None,
            PixelShaderKind.Light or PixelShaderKind.LightComposite => kind,
            _ => PixelShaderKind.None,
        };
        public bool IsPixelShaderAvailable(PixelShaderKind kind) => kind == PixelShaderKind.None || Effective(kind) == kind;
        public bool IsPixelShaderActive(PixelShaderKind kind) => _state.Shader == Effective(kind);
        public void SetPixelShader(PixelShaderKind kind) => _state.Shader = Effective(kind);
        public void SetPixelShaderConstants(SVector4 c0, SVector4 c1) { } // only normal.ps/magic.ps read them; neither exists

        /// <summary>ID3DXLine.Draw: its own Begin/End sets plain alpha blending and restores the device state afterwards.</summary>
        public void DrawLines(SVector2[] points, Color colour)
        {
            if (points == null || points.Length < 2) return;
            Flush();

            DeviceState saved = _state;
            SMatrix savedTransform = _transform;
            bool savedIdentity = _transformIdentity;
            Size savedProjection = _spriteProjectionSize;
            _spriteProjectionSize = CurrentTargetSize; // ID3DXLine begins its own pass on every Draw
            _state = new DeviceState { BlendEnable = true, Source = RenderBlend.SourceAlpha, Destination = RenderBlend.InverseSourceAlpha, Factor = saved.Factor, Shader = PixelShaderKind.None, AlphaTest = false };
            _transform = SMatrix.Identity;
            _transformIdentity = true;

            uint c = PackColour(new SColor4(colour));
            for (int i = 0; i + 1 < points.Length; i++)
            {
                float ax = points[i].X + 0.5f, ay = points[i].Y + 0.5f, bx = points[i + 1].X + 0.5f, by = points[i + 1].Y + 0.5f;
                float dx = bx - ax, dy = by - ay;
                float length = MathF.Sqrt(dx * dx + dy * dy);
                if (length <= 0f) continue;
                float nx = -dy / length * 0.5f, ny = dx / length * 0.5f;
                // ID3DXLine includes the end pixel of a segment (golden S10/S12: the last corner of every border).
                bx += dx / length * 0.5f;
                by += dy / length * 0.5f;

                if (_count >= MaxSprites) Flush();
                int v = _count * 4;
                _vertices[v] = new Vertex { X = ax + nx, Y = ay + ny, U = 0, V = 0, Colour = c };
                _vertices[v + 1] = new Vertex { X = bx + nx, Y = by + ny, U = 1, V = 0, Colour = c };
                _vertices[v + 2] = new Vertex { X = ax - nx, Y = ay - ny, U = 0, V = 1, Colour = c };
                _vertices[v + 3] = new Vertex { X = bx - nx, Y = by - ny, U = 1, V = 1, Colour = c };
                _textures[_count] = _white;
                _scaled[_count] = false;
                _linear[_count] = false;
                _count++;
            }
            Flush();

            _state = saved;
            _transform = savedTransform;
            _transformIdentity = savedIdentity;
            _spriteProjectionSize = savedProjection;
        }

        // ================================================================ device lost / reset

        private bool DeviceRemoved
        {
            get
            {
                try { return _device == null || _device.DeviceRemovedReason.Failure; }
                catch { return true; }
            }
        }

        public DeviceStatus TestCooperativeLevel()
        {
            // A failed re-create or ResizeBuffers leaves the backend half-built: report NotReset so ResetDevice retries.
            if (DeviceRemoved || _swapChain == null || _backBuffer == null) return DeviceStatus.NotReset;
            return DeviceStatus.Ok;
        }

        public void Reset(int width, int height, bool windowed, bool vsync)
        {
            _vsync = vsync;
            _windowed = windowed;
            _count = 0;
            Array.Clear(_textures, 0, _textures.Length);

            if (DeviceRemoved || _swapChain == null || _pixelBuffer == null || _bltModel != !vsync)
            {
                // TDR / driver reset: the only real device loss in D3D11. Rebuild everything; DXManager re-creates its textures.
                // Also when vsync changed: ResizeBuffers cannot switch between the blit and flip swap models.
                ReleaseAll();
                Create(_hwnd, width, height, windowed, vsync);
                return;
            }

            _context.ClearState();
            ReleaseBackBuffer();
            _context.Flush();
            ApplyFrameLatency(); // vsync may have changed
            // Re-pass the creation flags: ResizeBuffers replaces them and would drop ALLOW_TEARING.
            _swapChain.ResizeBuffers(0, (uint)Math.Max(1, width), (uint)Math.Max(1, height), DxgiFormat.Unknown, _swapChainFlags);
            CreateBackBuffer();
            BindPipeline();
        }

        public void Recover()
        {
            try
            {
                _count = 0;
                Array.Clear(_textures, 0, _textures.Length);
                _hasSaved = false;
                _context?.ClearState();
                _target = _backBuffer;
                BindPipeline();
            }
            catch
            {
            }
        }

        public bool IsDeviceLostException(Exception exception)
        {
            if (exception is DeviceLostException) return true;
            if (exception is not SharpGenException sharp) return false;
            int code = sharp.ResultCode.Code;
            return code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || code == Vortice.DXGI.ResultCode.DeviceReset.Code
                || code == Vortice.DXGI.ResultCode.DeviceHung.Code || code == Vortice.DXGI.ResultCode.DriverInternalError.Code;
        }

        // ================================================================ capture

        private byte[] Read(ID3D11Texture2D texture, int width, int height)
        {
            Flush();
            using (ID3D11Texture2D staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = DxgiFormat.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
            }))
            {
                _context.CopyResource(staging, texture);
                MappedSubresource mapped = _context.Map(staging, 0, MapMode.Read);
                try
                {
                    var bytes = new byte[width * height * 4];
                    for (int y = 0; y < height; y++)
                        Marshal.Copy((IntPtr)((byte*)mapped.DataPointer + (long)y * mapped.RowPitch), bytes, y * width * 4, width * 4);
                    return bytes;
                }
                finally
                {
                    _context.Unmap(staging, 0);
                }
            }
        }

        public byte[] CaptureRenderTarget(RenderTexture target) => Read(((TextureData)target.Native).Texture, target.Width, target.Height);

        public Bitmap CaptureBackBuffer()
        {
            byte[] bytes;
            bool useCopy = _lastFrame != null && _lastFrameFresh;
            if (useCopy)
                bytes = Read(_lastFrame, (int)_lastFrame.Description.Width, (int)_lastFrame.Description.Height);
            else
                using (ID3D11Texture2D buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                    bytes = Read(buffer, _backBuffer.Width, _backBuffer.Height);
            int width = useCopy ? (int)_lastFrame.Description.Width : _backBuffer.Width;
            int height = useCopy ? (int)_lastFrame.Description.Height : _backBuffer.Height;
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255; // the swap chain ignores alpha
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(bytes, y * bitmap.Width * 4, data.Scan0 + y * data.Stride, bitmap.Width * 4);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        // ================================================================ teardown

        private void ReleaseAll()
        {
            // Flip-model teardown order matters: drop every context reference to the back buffer and drain the GPU before
            // releasing the swap chain, then the context, then the device.
            try
            {
                _context?.ClearState();
                _context?.Flush();
            }
            catch
            {
            }

            _count = 0;
            Array.Clear(_textures, 0, _textures.Length);
            if (_white != null) { DestroyTexture(_white); _white = null; }
            _lastFrame?.Dispose(); _lastFrame = null;
            foreach (var blend in _blendStates.Values) blend.Dispose();
            _blendStates.Clear();
            _depthOff?.Dispose(); _rasterizer?.Dispose(); _samplerAnisotropic?.Dispose(); _samplerLinear?.Dispose(); _samplerPoint?.Dispose();
            _pixelBuffer?.Dispose(); _frameBuffer?.Dispose(); _atmosphereBuffer?.Dispose(); _indexBuffer?.Dispose(); _vertexBuffer?.Dispose();
            _layout?.Dispose(); _psReflect?.Dispose(); _psSnapshot?.Dispose(); _psGround?.Dispose(); _psLightning?.Dispose(); _psRainfall?.Dispose(); _psLightComposite?.Dispose(); _psLight?.Dispose(); _psGrayscale?.Dispose(); _psModulate?.Dispose(); _vs?.Dispose();
            _depthOff = null; _rasterizer = null; _samplerAnisotropic = null; _samplerLinear = null; _samplerPoint = null;
            _pixelBuffer = null; _frameBuffer = null; _atmosphereBuffer = null; _indexBuffer = null; _vertexBuffer = null;
            _layout = null; _psReflect = null; _psSnapshot = null; _psGround = null; _psLightning = null; _psRainfall = null; _psLightComposite = null; _psLight = null; _psGrayscale = null; _psModulate = null; _vs = null;
            _atmoSource1 = _atmoSource2 = _atmoSource3 = null;
            _atmosphereAvailable = false;

            ReleaseBackBuffer();
            _swapChain?.Dispose(); _swapChain = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
            _ringOffset = 0;
        }

        public void Dispose() => ReleaseAll();
    }
}
