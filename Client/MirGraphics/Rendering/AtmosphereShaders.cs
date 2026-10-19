namespace Client.MirGraphics.Rendering
{
    /// <summary>
    /// HLSL for the atmosphere weather passes ([Graphics] Atmosphere; D3D11 + enhanced lighting only). Compiled at startup by
    /// <see cref="D3D11Backend"/>: a compile failure turns the atmosphere off for the run (logged) and leaves the classic
    /// weather sprites. A pass runs through the sprite path as a fullscreen quad, so SpriteTex (t0) is the pass source and the
    /// Atmosphere constant buffer (b2, 128 float4s) holds the per-pass inputs. t1..t3 are extra sources (the wet mask and the
    /// scene), bound by <see cref="IRenderBackend.SetAtmosphereSource"/>.
    ///
    /// Ported from the web client's postFx.ts (RAINFALL_FS, GROUND_FS, SNAPSHOT_FS, REFLECT_FS) and atmosphere.ts. GL's
    /// gl_FragCoord.y is bottom-up; desktop SV_Position is top-down, so absolute screen positions need no flip. Directional
    /// offsets keep the web's meaning by negation (the object-mirror contact march runs up the screen, not down).
    /// </summary>
    internal static class AtmosphereShaders
    {
        public const string Source = @"
cbuffer Atmosphere : register(b2) { float4 Atmo[128]; };
Texture2D SpriteTex : register(t0);
Texture2D AtmoTex1 : register(t1);
Texture2D AtmoTex2 : register(t2);
Texture2D AtmoTex3 : register(t3);
SamplerState SpriteSmp : register(s0);
SamplerState LinearSmp1 : register(s1);
SamplerState LinearSmp2 : register(s2);
SamplerState LinearSmp3 : register(s3);

struct AIn { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 col : COLOR0; };

// ------------------------------------------------------------------ noise
float hash21(float2 p) { p = frac(p * float2(123.34f, 456.21f)); p += dot(p, p + 45.32f); return frac(p.x * p.y); }
float vnoise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0f - 2.0f * f);
    return lerp(lerp(hash21(i), hash21(i + float2(1.0f, 0.0f)), u.x),
                lerp(hash21(i + float2(0.0f, 1.0f)), hash21(i + float2(1.0f, 1.0f)), u.x), u.y);
}
float fbm(float2 p)
{
    float v = 0.0f, a = 0.5f;
    for (int i = 0; i < 4; i++) { v += a * vnoise(p); p = p * 2.03f + float2(17.1f, 9.2f); a *= 0.5f; }
    return v;
}

// ------------------------------------------------------------------ cell mask (GROUND_FS / RAINFALL_FS)
// One texel per map cell around the view: R = floor tile, G = water (fishing), B = eaves-drip base. Atmo[3] = (origin.x,
// origin.y, cell0.x, cell0.y), Atmo[4] = (cols, rows, -, -). The origin carries DrawFloor's '- OffSetX' pixel quirk.
#define CELL_W 48.0f
#define CELL_H 32.0f
float4 cellMask(float2 world)
{
    float2 cells = Atmo[4].xy;
    if (cells.x <= 0.0f || cells.y <= 0.0f) return float4(0, 0, 0, 0);
    float2 uv = (world - Atmo[3].xy) / (float2(CELL_W, CELL_H) * cells);
    if (uv.x < 0.0f || uv.y < 0.0f || uv.x > 1.0f || uv.y > 1.0f) return float4(0, 0, 0, 0);
    return SpriteTex.Sample(SpriteSmp, uv);
}
float4 cellAt(float2 rel)
{
    float2 cells = Atmo[4].xy;
    if (cells.x <= 0.0f || cells.y <= 0.0f) return float4(0, 0, 0, 0);
    if (rel.x < 0.0f || rel.y < 0.0f || rel.x >= cells.x || rel.y >= cells.y) return float4(0, 0, 0, 0);
    return SpriteTex.SampleLevel(SpriteSmp, (rel + 0.5f) / cells, 0);
}
// Column k of drip-base cell ac (absolute): x inside the cell, fall height h, period per, phase offset off; live = this
// cycle drips; t = seconds since this cycle's release; T = fall time.
void dripColumn(float2 ac, int k, out float x, out float h, out float t, out float T, out bool live)
{
    float2 id = ac + float2(float(k) * 17.3f, float(k) * 5.1f);
    x = (0.15f + 0.7f * hash21(id + 3.7f)) * CELL_W;
    h = 34.0f + 40.0f * hash21(id + 8.1f);
    float per = 0.7f + 1.1f * hash21(id + 1.9f);
    float cyc = Atmo[0].z / per + hash21(id + 5.3f);
    live = hash21(id + float2(0.37f, floor(cyc) * 0.013f)) < Atmo[2].w;
    t = frac(cyc) * per;
    T = sqrt(2.0f * h / 900.0f);
}

// ------------------------------------------------------------------ splashes (GROUND_FS)
// One splash at sp (world px), drop radius rr, ph 0..1 through its life: a bright core that fades fast, a crown ring flaring
// out to 3x the drop radius, and two droplets thrown up and outward on a short arc that land as the crown dies. Squashed
// vertically for the isometric view.
float splashAt(float2 world, float2 sp, float rr, float ph, float seed)
{
    float d = length((world - sp) * float2(1.0f, 1.7f));
    float fade = 1.0f - ph;
    float acc = (1.0f - smoothstep(rr * 0.3f, rr, d)) * (1.0f - smoothstep(0.0f, 0.45f, ph));
    acc += (1.0f - smoothstep(0.0f, 0.9f, abs(d - rr * (1.0f + 2.0f * ph)))) * fade * 0.7f;
    float h = rr * 2.6f * 4.0f * ph * (1.0f - ph);
    for (int k = 0; k < 2; k++)
    {
        float s = (k == 0 ? -1.0f : 1.0f) * (0.6f + 0.8f * hash21(float2(seed + 3.3f + float(k), seed)));
        float2 dp = sp + float2(s * rr * 2.4f * ph, -h);
        acc += (1.0f - smoothstep(0.35f, 0.95f, length(world - dp))) * fade;
    }
    return acc;
}

// Expanding rings on one grid (only the 2x2 cells nearest the pixel, as in GROUND_FS).
float drops(float2 world, float2 cellSize, float seed, float maxR, float share, float strength, float width, float inner)
{
    float ripples = Atmo[1].w;
    float2 cell = floor(world / cellSize - 0.5f);
    float acc = 0.0f;
    for (int oy = 0; oy <= 1; oy++)
    {
        for (int ox = 0; ox <= 1; ox++)
        {
            float2 c = cell + float2(float(ox), float(oy));
            if (hash21(c + seed) > ripples * share) continue;
            float period = (0.6f + hash21(c + seed + 3.1f) * 1.0f) * (0.7f + maxR / 20.0f);
            float ph = frac(Atmo[0].z / period + hash21(c + seed + 9.7f));
            float r = maxR * (0.35f + 0.65f * hash21(c + seed + 6.6f));
            float2 centre = (c + float2(hash21(c + seed + 1.3f), hash21(c + seed + 5.9f))) * cellSize;
            float d = length((world - centre) * float2(1.0f, 1.7f));
            float fade = (1.0f - ph) * (1.0f - ph);
            acc += (1.0f - smoothstep(0.0f, width, abs(d - ph * r))) * fade;
            acc += (1.0f - smoothstep(0.0f, width * 0.8f, abs(d - ph * r * 0.55f))) * fade * inner;
        }
    }
    return acc * strength;
}
// Drop impacts on one grid (cells under uSplash), each live for the first 30% of its own period.
float splashes(float2 world, float2 sc)
{
    float splash = Atmo[2].x;
    float2 cell = floor(world / sc);
    float acc = 0.0f;
    for (int oy = -1; oy <= 1; oy++)
    {
        for (int ox = -1; ox <= 1; ox++)
        {
            float2 c = cell + float2(float(ox), float(oy));
            if (hash21(c + 21.7f) >= splash) continue;
            float per = 0.45f + hash21(c + 4.4f) * 0.5f;
            float ph = frac(Atmo[0].z / per + hash21(c + 8.8f)) / 0.3f;
            if (ph >= 1.0f) continue;
            float2 sp = (c + float2(hash21(c + 1.1f), hash21(c + 2.2f))) * sc;
            acc += splashAt(world, sp, 1.2f + 2.3f * hash21(c + 6.6f), ph, hash21(c + 0.7f) * 50.0f);
        }
    }
    return acc;
}
// The splashes of the eaves drips landing at the top edge of this pixel's cell (or the cell below, for the droplets thrown up).
float dripSplashes(float2 world)
{
    float2 rel = floor((world - Atmo[3].xy) / float2(CELL_W, CELL_H));
    float acc = 0.0f;
    for (int r = 0; r <= 1; r++)
    {
        float2 rc = rel + float2(0.0f, float(r));
        if (cellAt(rc).b < 0.5f) continue;
        float2 ac = rc + Atmo[3].zw;
        float baseY = Atmo[3].y + rc.y * CELL_H;
        for (int k = 0; k < 3; k++)
        {
            float x, h, t, T; bool live;
            dripColumn(ac, k, x, h, t, T, live);
            float ph = (t - T) / 0.3f;
            if (!live || ph < 0.0f || ph >= 1.0f) continue;
            acc += splashAt(world, float2(Atmo[3].x + rc.x * CELL_W + x, baseY + 3.0f), 2.8f, ph, ac.x * 0.37f + float(k));
        }
    }
    return acc;
}
// AtmosphereNoise.PuddleAt mirrors this on the CPU (footfall splashes): change both together.
float puddle(float2 world)
{
    float edge = 0.72f - 0.24f * Atmo[2].y;
    return smoothstep(edge, edge + 0.09f, fbm(world / 150.0f + 3.1f));
}

// ------------------------------------------------------------------ wet ground
// Atmo[0] = (size.x, size.y, time seconds, mode)   mode 0 = wet, mode 1 = snow (Phase 3)
// Atmo[1] = (cam.x, cam.y, amount, ripples)
// Atmo[2] = (splash, wet, pass, drip)   pass 0 = colour, 1 = reflectivity alpha, 2 = alpha back to 1 (after the snapshot)
// Atmo[3] = mask origin + cell0, Atmo[4] = mask cells
float4 Ground_PS(AIn i) : SV_Target
{
    float2 size = Atmo[0].xy;
    float mode = Atmo[0].w;
    float2 cam = Atmo[1].xy;
    float amount = Atmo[1].z;
    float2 frag = i.pos.xy;
    float2 world = frag + cam;
    if (mode >= 0.5f)
    {
        // snow cover (Phase 3; unused until then)
        float n = fbm(world / 110.0f) * 0.7f + fbm(world / 31.0f + 7.3f) * 0.3f;
        float edge = 1.0f - amount;
        float a = smoothstep(edge - 0.12f, edge + 0.12f, n) * 0.82f;
        return float4(0.9f, 0.93f, 0.98f, a);
    }

    // The map texture is drawn to the screen alpha-blended (DXManager.DrawOpaque), so once the snapshot has read the
    // reflectivity, the alpha must go back to 1 or wet floor turns see-through.
    if (Atmo[2].z > 1.5f) return float4(0.0f, 0.0f, 0.0f, 1.0f);

    float4 m = cellMask(world);
    float floorM = smoothstep(0.3f, 0.7f, m.r);
    float water = smoothstep(0.3f, 0.7f, m.g);
    float pud = max(puddle(world), water);
    if (Atmo[2].z > 0.5f)
    {
        // reflectivity: 1 outside floor, 0.5 - 0.5 * reflectivity on wet floor (puddles and water mirror most)
        float refl = Atmo[2].y * (0.22f + 0.78f * pud);
        return float4(0.0f, 0.0f, 0.0f, lerp(1.0f, 0.5f - 0.5f * refl, floorM));
    }
    if (floorM <= 0.0f) return float4(0, 0, 0, 0);

    float dark = amount * (0.13f + 0.2f * pud) * (0.75f + 0.5f * fbm(world / 140.0f)) * (1.0f - 0.7f * water);
    float ring = drops(world, float2(26.0f, 17.0f), 0.0f, 6.0f, 1.0f, 1.0f, 1.1f, 0.0f)
               + drops(world, float2(42.0f, 27.0f), 40.0f, 11.0f, 0.55f, 0.7f, 1.35f, 0.45f)
               + drops(world, float2(66.0f, 42.0f), 80.0f, 18.0f, 0.3f, 0.45f, 1.6f, 0.6f);
    ring *= (0.3f + 0.7f * pud) * (1.0f + water);
    float splash = splashes(world, float2(20.0f, 13.0f)) * (1.0f - 0.6f * water) + dripSplashes(world);
    ring = clamp(clamp(ring, 0.0f, 1.0f) * 0.32f + clamp(splash, 0.0f, 1.0f) * 0.45f, 0.0f, 1.0f);
    float a = clamp(dark + ring, 0.0f, 1.0f);
    float3 col = a > 0.0f ? float3(0.85f, 0.9f, 1.0f) * (ring / a) : float3(0.0f, 0.0f, 0.0f);
    return float4(col, a * floorM);
}

// Scene alpha -> the wet-mask target (R): the reflectivity snapshot taken after the objects.
// Atmo[0] = (size.x, size.y, -, -)
float4 Snapshot_PS(AIn i) : SV_Target
{
    float2 size = Atmo[0].xy;
    float a = SpriteTex.Sample(SpriteSmp, i.pos.xy / size).a;
    return float4(a, a, a, 1.0f);
}

// ------------------------------------------------------------------ puddle reflections
// Atmo[0] = (size.x, size.y, time, part)         part 0 = objects, 1 = lamps
// Atmo[1] = (cam.x, cam.y, reflect, ripples)
// Atmo[2] = (tint.r, tint.g, tint.b, lamp count)
// Atmo[3..6] = tuning (gain, streak length, streak width, core gain | flame, sheen, ripple push, wobble |
//             puddle low, puddle high, contact fade, hot white | rough gain, halo gain, warmth, -)
// Atmo[7 + 2i] = (x, mirror y, contact y, radius), Atmo[8 + 2i] = (r, g, b, emitter y)
float2 ringGrid(float2 world, float2 cellSize, float seed, float maxR, float share, float width)
{
    float ripples = Atmo[1].w;
    float2 cell = floor(world / cellSize - 0.5f);
    float2 acc = float2(0.0f, 0.0f);
    for (int oy = 0; oy <= 1; oy++)
    {
        for (int ox = 0; ox <= 1; ox++)
        {
            float2 c = cell + float2(float(ox), float(oy));
            if (hash21(c + seed) > ripples * share) continue;
            float period = (0.6f + hash21(c + seed + 3.1f) * 1.0f) * (0.7f + maxR / 20.0f);
            float ph = frac(Atmo[0].z / period + hash21(c + seed + 9.7f));
            float r = maxR * (0.35f + 0.65f * hash21(c + seed + 6.6f));
            float2 centre = (c + float2(hash21(c + seed + 1.3f), hash21(c + seed + 5.9f))) * cellSize;
            float2 v = (world - centre) * float2(1.0f, 1.7f);
            float d = length(v);
            float x = (d - ph * r) / width;
            acc += (d > 0.001f ? v / d : float2(0.0f, 0.0f)) * (x * exp(-x * x) * 2.3f) * (1.0f - ph) * (1.0f - ph);
        }
    }
    return acc;
}
float2 ringDisp(float2 world)
{
    if (Atmo[1].w <= 0.0f) return float2(0.0f, 0.0f);
    return ringGrid(world, float2(26.0f, 17.0f), 0.0f, 6.0f, 1.0f, 1.1f)
         + ringGrid(world, float2(42.0f, 27.0f), 40.0f, 11.0f, 0.55f, 1.35f) * 0.8f
         + ringGrid(world, float2(66.0f, 42.0f), 80.0f, 18.0f, 0.3f, 1.6f) * 0.6f;
}
float3 lampReflections(float2 sp, float wet, float2 size)
{
    float4 RT0 = Atmo[3], RT1 = Atmo[4], RT2 = Atmo[5], RT3 = Atmo[6];
    int count = (int)Atmo[2].w;
    float2 cam = Atmo[1].xy;
    float puddle = smoothstep(RT2.x, RT2.y, wet);
    float rough = 1.0f - puddle;
    float2 world = sp + cam;
    float2 disp = ringDisp(world) * RT1.z;
    disp.x += (vnoise(world * float2(0.045f, 0.16f) + float2(0.0f, Atmo[0].z * 1.3f)) - 0.5f) * RT1.w * (0.6f + rough);
    disp.y += (vnoise(world * float2(0.09f, 0.05f) - float2(Atmo[0].z * 0.7f, 0.0f)) - 0.5f) * RT1.w * 0.5f;
    float2 p = sp + disp;
    float3 acc = float3(0.0f, 0.0f, 0.0f);
    for (int n = 0; n < 48; n++)
    {
        if (n >= count) break;
        float4 L = Atmo[7 + n * 2];
        float4 C = Atmo[8 + n * 2];
        float k = clamp(L.w / 400.0f, 0.35f, 1.2f);
        float below = smoothstep(L.z - RT2.z, L.z + RT2.z, sp.y);
        if (below <= 0.0f) continue;
        float belowSoft = smoothstep(L.z - RT2.z, L.z + RT2.z * 6.0f, sp.y);
        float2 q = p - L.xy;
        float lenD = 4.0f + RT0.y * k * (0.3f + 0.7f * rough);
        float lenU = lenD * 0.4f;
        float sy = q.y / (q.y > 0.0f ? lenD : lenU);
        if (abs(q.x) > max(210.0f * k, 6.0f + 3.0f * RT0.z) || q.y > max(4.0f * lenD, 300.0f * k) || q.y < -max(4.0f * lenU, 300.0f * k)) continue;
        float wid = 1.0f + RT0.z * k * (0.3f + 0.7f * rough) * (1.0f + 0.6f * abs(sy));
        float sx = q.x / wid;
        float streak = exp(-sx * sx) * (exp(-sy * sy * 1.6f) + 0.3f * exp(-abs(sy) * 1.4f));
        float2 qc = q / ((float2(2.0f, 3.0f) + float2(4.0f, 6.0f) * k) * (1.0f + 1.6f * rough));
        float core = exp(-dot(qc, qc)) * (puddle + 0.45f * rough);
        float2 qh = q / (float2(14.0f, 22.0f) * k * (1.0f + rough));
        float halo = exp(-dot(qh, qh));
        float2 qs = q / (float2(70.0f, 100.0f) * k);
        float sheen = exp(-dot(qs, qs)) * (0.35f + 0.65f * rough);
        float cm = max(C.r, max(C.g, C.b));
        float3 warm = cm > 0.0f ? pow(C.rgb / cm, float3(1.0f + RT3.z, 1.0f + RT3.z, 1.0f + RT3.z)) * cm : C.rgb;
        float3 col = (lerp(warm, C.rgb, 0.35f) * (core * RT0.w) + warm * (streak * RT0.x * (0.45f + 0.55f * puddle) + halo * RT3.y)) * below
                   + warm * (sheen * RT1.y * belowSoft);
        if (RT1.x > 0.0f)
        {
            // the lamp's own art mirrored across the contact line (desktop scene target is top-down: sample src directly)
            float2 src = float2(sp.x + disp.x * 1.5f, 2.0f * L.z - sp.y - disp.y);
            float2 qe = (src - float2(L.x, C.w)) / (float2(6.0f, 8.0f) + float2(22.0f, 30.0f) * k);
            float near = exp(-dot(qe, qe));
            if (near > 0.01f && src.y >= 0.0f && src.y < size.y)
            {
                float3 s = SpriteTex.Sample(SpriteSmp, src / size).rgb;
                float key = smoothstep(0.42f, 0.9f, max(s.r, max(s.g, s.b)));
                col += s * (key * near * RT1.x * (0.35f + 0.65f * puddle) * below);
            }
        }
        acc += col;
    }
    float m = max(acc.r, max(acc.g, acc.b));
    acc += float3(smoothstep(0.6f, 2.2f, m) * m * RT2.w * 0.5f, smoothstep(0.6f, 2.2f, m) * m * RT2.w * 0.5f, smoothstep(0.6f, 2.2f, m) * m * RT2.w * 0.5f);
    return acc * lerp(RT3.x, 1.0f, puddle) * smoothstep(0.02f, 0.12f, wet);
}
float4 Reflect_PS(AIn i) : SV_Target
{
    float2 size = Atmo[0].xy;
    float part = Atmo[0].w;
    float3 tint = Atmo[2].rgb;
    float reflect = Atmo[1].z;
    // one half-resolution texel stands for the 2 x 2 screen pixels around it; sizes stay in screen pixels
    float2 frag = i.pos.xy * 2.0f;
    float wetHere = saturate((0.5f - AtmoTex1.Sample(LinearSmp1, frag / size).r) * 2.0f);
    if (wetHere <= 0.01f) return float4(0.0f, 0.0f, 0.0f, 0.0f);
    if (part < 0.5f)
    {
        // objects mirrored in puddles, premultiplied. The web marches +y (up in GL); desktop y is down, so march -y.
        float contact = 0.0f;
        for (int k = 1; k <= 32; k++)
        {
            float d = float(k) * 3.0f;
            if (AtmoTex1.Sample(LinearSmp1, (frag - float2(0.0f, d)) / size).r > 0.62f) { contact = d; break; }
        }
        if (contact > 0.0f)
        {
            float rip = vnoise(frag * float2(0.08f, 0.35f) + float2(0.0f, Atmo[0].z * 2.6f)) - 0.5f;
            float2 src = frag + float2(rip * 4.0f, -contact * 2.0f);
            float obj = smoothstep(0.55f, 0.8f, AtmoTex1.Sample(LinearSmp1, src / size).r);
            float3 mirrored = SpriteTex.Sample(SpriteSmp, src / size).rgb * tint * float3(0.78f, 0.84f, 0.94f);
            float k = wetHere * obj * (1.0f - contact / 100.0f) * 0.55f * reflect;
            return float4(mirrored * k, k);
        }
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }
    // eroded: the least reflective of this pixel and four around it, so floor seen through small gaps in a tree or a figure
    // stays unlit (web REFLECT_FS)
    float wetE = wetHere;
    for (int e = 0; e < 4; e++)
    {
        float2 o = float2(e == 0 ? 4.0f : e == 1 ? -4.0f : 0.0f, e == 2 ? 4.0f : e == 3 ? -4.0f : 0.0f);
        wetE = min(wetE, saturate((0.5f - AtmoTex1.Sample(LinearSmp1, (frag + o) / size).r) * 2.0f));
    }
    if (wetE <= 0.01f) return float4(0.0f, 0.0f, 0.0f, 0.0f);
    return float4(lampReflections(frag, wetE, size) * tint * reflect, 1.0f);
}

// ------------------------------------------------------------------ rainfall (Phase 1)
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
        public static readonly string[] EntryPoints = { "Rainfall_PS", "Lightning_PS", "Ground_PS", "Snapshot_PS", "Reflect_PS" };
    }
}
