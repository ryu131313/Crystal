namespace Client.MirGraphics.Rendering
{
    /// <summary>
    /// HLSL for the atmosphere weather passes ([Graphics] Atmosphere; D3D11 + enhanced lighting only). Compiled at startup by
    /// <see cref="D3D11Backend"/>: a compile failure turns the atmosphere off for the run (logged) and leaves the classic
    /// weather sprites. A pass runs through the sprite path as a fullscreen quad, so SpriteTex (t0) is the pass source (the
    /// enhanced light map) and the Atmosphere constant buffer (b2) holds the per-pass inputs.
    ///
    /// Ported from the web client's RAINFALL_FS (WebClient/src/render/postFx.ts): three parallax streak layers plus a near
    /// out-of-focus layer, gust sheets, a grey wash, and lamp glints from the light map. The eaves-drip layer needs the
    /// cell mask (Phase 2) and is left out. GL's gl_FragCoord.y is bottom-up; D3D11 SV_Position is top-down, so the screen
    /// y needs no flip.
    /// </summary>
    internal static class AtmosphereShaders
    {
        public const string Source = @"
cbuffer Atmosphere : register(b2) { float4 Atmo[4]; };
Texture2D SpriteTex : register(t0);
SamplerState SpriteSmp : register(s0);

struct AIn { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 col : COLOR0; };

float hash21(float2 p) { p = frac(p * float2(123.34f, 456.21f)); p += dot(p, p + 45.32f); return frac(p.x * p.y); }
float vnoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0f - 2.0f * f);
    return lerp(lerp(hash21(i), hash21(i + float2(1.0f, 0.0f)), u.x),
                lerp(hash21(i + float2(0.0f, 1.0f)), hash21(i + float2(1.0f, 1.0f)), u.x), u.y);
}

// soft 0 = a crisp streak brightest at its head; 1 = an out-of-focus one, soft-edged and brightest mid-length.
float streaks(float2 screen, float2 cam, float time, float amount, float parallax, float colW, float cellH, float speed, float len, float thick, float soft, float slant, float seed)
{
    float2 p = screen + cam * parallax;
    p.x -= screen.y * slant;
    float col = floor(p.x / colW);
    float y = p.y - fmod(time * speed, cellH * 4096.0f) + hash21(float2(col, seed)) * 997.0f;
    float seg = floor(y / cellH);
    float2 id = float2(col, seg + seed * 13.0f);
    if (hash21(id) > amount) return 0.0f;
    float x0 = (0.2f + 0.6f * hash21(id + 2.3f)) * colW;
    float dx = abs(p.x - col * colW - x0);
    float along = (y - seg * cellH - hash21(id + 7.1f) * (cellH - len)) / len;
    if (along < 0.0f || along > 1.0f) return 0.0f;
    float w = thick * (0.45f + 0.55f * lerp(along, 1.0f, soft));
    return (1.0f - smoothstep(w * 0.5f * (1.0f - soft), w, dx)) * lerp(along, sin(along * 3.14159f), soft);
}

// Atmo[0] = (size.x, size.y, time seconds, amount)
// Atmo[1] = (cam.x, cam.y, slant, wash)
// Atmo[2] = (sheet drift, near, hasLight, unused)
float4 Rainfall_PS(AIn i) : SV_Target
{
    float2 size = Atmo[0].xy;
    float time = Atmo[0].z;
    float amount = Atmo[0].w;
    float2 cam = Atmo[1].xy;
    float slant = Atmo[1].z;
    float washAmount = Atmo[1].w;
    float sheetDrift = Atmo[2].x;
    float near = Atmo[2].y;
    float hasLight = Atmo[2].z;

    float2 frag = i.pos.xy;      // D3D11: top-down, no GL flip
    float2 screen = frag;
    float2 world = screen + cam;

    float sheet = smoothstep(0.3f, 0.72f, vnoise(float2((world.x - world.y * slant) * 0.0032f - sheetDrift, world.y * 0.0011f)));
    float amt = amount * (0.55f + 0.8f * sheet);

    float a = streaks(screen, cam, time, amt, 0.55f, 19.0f, 220.0f, 360.0f, 20.0f, 1.5f, 0.0f, slant, 0.0f) * 0.38f
            + streaks(screen, cam, time, amt, 0.8f, 30.0f, 300.0f, 510.0f, 34.0f, 2.1f, 0.0f, slant, 5.0f) * 0.55f
            + streaks(screen, cam, time, amt, 1.1f, 46.0f, 400.0f, 700.0f, 54.0f, 2.9f, 0.0f, slant, 11.0f) * 0.72f;
    a += streaks(screen, cam, time, amt * near, 1.6f, 150.0f, 900.0f, 1150.0f, 150.0f, 11.0f, 1.0f, slant, 17.0f) * 0.32f;

    // Drawn over the already lit map, so the rain is lit here: the light map through LightComposite_PS's soft knee (0..2),
    // and glints where it is past white. Without a light pass this frame (Day) the rain is unlit (x1) and has no glints.
    float glint = 0.0f;
    float3 lit = float3(1.0f, 1.0f, 1.0f);
    if (hasLight > 0.5f)
    {
        float3 l = max(SpriteTex.Sample(SpriteSmp, frag / size).rgb, 0.0f);
        glint = smoothstep(0.9f, 2.4f, dot(l, float3(0.3f, 0.59f, 0.11f)));
        float3 over = max(l - 1.0f, 0.0f);
        lit = min(l, 1.0f) + over / (1.0f + over);
    }
    a = saturate(a * (1.0f + 1.3f * glint)) * 0.6f;

    float3 streakCol = lerp(float3(0.74f, 0.79f, 0.88f), float3(1.0f, 0.96f, 0.88f), glint);
    float wash = washAmount * (0.6f + 0.8f * sheet);
    float total = a + wash * (1.0f - a);
    float3 col = (streakCol * a + float3(0.55f, 0.58f, 0.63f) * wash * (1.0f - a)) / max(total, 1e-4f);
    return float4(col * lit, total);
}

// A flat full-screen flash: colour in rgb, envelope (0..1) in alpha, added to the scene.
// Atmo[0] = (r, g, b, envelope)
float4 Lightning_PS(AIn i) : SV_Target
{
    return Atmo[0];
}
";

        /// <summary>The pixel-shader entry points compiled from <see cref="Source"/>.</summary>
        public static readonly string[] EntryPoints = { "Rainfall_PS", "Lightning_PS" };
    }
}
