// Lifted from WoOOLcraft src/WebClient/src/render/shaders.ts (2026-10-12) for the Crystal web client; the D3D11Backend it mirrors descends from Crystal's own.
// WebGL2 sprite shaders: GLSL ES 3.0 ports of Client/Envir/Rendering/SpriteBatch2D.cs ShaderSource
// (Sprite_VS/PS, OrbLiquid, BlendKey, Mask, SafeZoneOutline, ClawSlash, RipClaw, DruidFx).
// The other six cosmetic PS (FrameSheen, MiniMap, ClusterBadge, LootSparkle, SafeZoneSparkle, BloodDrip) degrade to the plain shader.
//
// ClawSlash/RipClaw/DruidFx must NOT degrade: they draw a plain white quad coloured entirely by the shader, so the plain PS
// would paint a solid white rectangle (SpriteBatch2D.cs:1343-1348). Like the desktop, `WebGl2Renderer` treats a link failure
// as "program unavailable" and refuses the add.
//
// Translation rules:
//   cbuffer b0/b1/b2 -> std140 uniform blocks, same field order (offsets match the C# pins HueShift@88, AlphaRef@92, Recolour@96).
//   HLSL mul(v, M) -> column-vector P * T * v, with the 16 floats uploaded in C# memory order (see color.ts screenProjection).
//   SV_Position in a PS -> gl_FragCoord (SafeZoneOutline_PS anchors the map cell with it).
//   frac->fract, lerp->mix, saturate->clamp(x,0,1), atan2->atan, [unroll] dropped, .Sample -> texture().
// Quirks preserved (parity with the desktop is the spec, do not "fix"):
//   OrbLiquid's abs(below) reads the 0/1 comparison mask, not the signed distance;
//   BlendKey keys on the PRE-recolour colour so the cut set matches D3D.
// HLSL float and GLSL highp float are both binary32, so the arithmetic ports bit-for-bit.

export const VERTEX_SHADER_SHARED = `#version 300 es
precision highp float;
// GLSL ES defaults int/uint to highp in the vertex stage but mediump in the
// fragment stage. std140 blocks shared across stages must match member-for-member
// INCLUDING precision, so both stages state it explicitly — without this the
// PerBatch uint members (ColorOp, AlphaTest) fail to link.
precision highp int;

layout(std140) uniform PerFrame { mat4 Projection; };
layout(std140) uniform PerBatch {
  mat4 Transform;
  vec4 TintColor;   // reserved by the D3D cbuffer; the shaders never read it
  uint ColorOp;
  uint AlphaTest;
  float HueShift;   // hue rotation in turns (degrees/360); 0 = no-op
  float AlphaRef;   // shadow-only discard threshold; 0 = off
  vec4 Recolour;    // rgb = target colour, w = strength (0 = no-op)
};

in vec2 aPos;
in vec2 aUv;
in vec4 aCol; // R8G8B8A8_UNorm attribute → normalized 0..1

out vec2 vUv;
out vec4 vCol;

void main() {
  // Sprite_VS: o.pos = mul(mul(float4(i.pos,0,1), Transform), Projection)
  gl_Position = Projection * Transform * vec4(aPos, 0.0, 1.0);
  vUv = aUv;
  vCol = aCol;
}
`;

/** The `Effect` uniform block's members in declaration order: the single source of its std140 layout.
 * `EFFECT_UBO_BYTES` and the `batcher.ts` packer offsets derive from it; a selftest checks it against the GLSL block below. */
export const EFFECT_VEC4_FIELDS: readonly string[] = [
  "OrbParams", "OrbLipGlow", "BlendKeyParams", "ImageNorm", "MaskNorm",
  "SafeZoneParams", "SafeZoneAnchor", "SafeZoneColorA", "SafeZoneColorB",
  "SheenParams", "SheenTint", "SparkleParams", "SparkleTint", "SparkleEdge",
  "SafeZoneSparkleParams", "BloodParams", "BloodTint", "BloodEdge",
  "ClawParams", "ClawTint", "ClawShape",
  "DruidFxParams", "DruidFxTint", "DruidFxShape",
  "MiniMapRect", "MiniMapStyle", "MiniMapBorder", "ClusterBadgeStyle",
];

/** vec4 index of one `Effect` member — the packer's only way to address it. */
export function effectVec4Index(name: string): number {
  const index = EFFECT_VEC4_FIELDS.indexOf(name);
  if (index < 0) throw new Error(`Effect uniform block has no member ${name}`);
  return index;
}

/** Common pixel-shader preamble: uniform blocks, samplers and the shared ApplyRecolour / ApplyAlphaTest helpers. */
export const FRAGMENT_PREAMBLE = `#version 300 es
precision highp float;
// Must match VERTEX_SHADER_SHARED — see the note there. The fragment stage's
// default for int/uint is mediump, so this line is the one that actually fixes
// the PerBatch link mismatch; the vertex-side declaration keeps them symmetric.
precision highp int;

layout(std140) uniform PerBatch {
  mat4 Transform;
  vec4 TintColor;
  uint ColorOp;
  uint AlphaTest;
  float HueShift;
  float AlphaRef;
  vec4 Recolour;
};
layout(std140) uniform Effect {
  vec4 OrbParams;
  vec4 OrbLipGlow;
  vec4 BlendKeyParams;
  vec4 ImageNorm;
  vec4 MaskNorm;
  vec4 SafeZoneParams;
  vec4 SafeZoneAnchor;
  vec4 SafeZoneColorA;
  vec4 SafeZoneColorB;
  vec4 SheenParams;
  vec4 SheenTint;
  vec4 SparkleParams;
  vec4 SparkleTint;
  vec4 SparkleEdge;
  vec4 SafeZoneSparkleParams;
  vec4 BloodParams;
  vec4 BloodTint;
  vec4 BloodEdge;
  vec4 ClawParams;
  vec4 ClawTint;
  vec4 ClawShape;
  vec4 DruidFxParams;
  vec4 DruidFxTint;
  vec4 DruidFxShape;
  vec4 MiniMapRect;
  vec4 MiniMapStyle;
  vec4 MiniMapBorder;
  vec4 ClusterBadgeStyle;
};

uniform sampler2D SpriteTex; // t0 + s0 (point, clamp)
uniform sampler2D MaskTex;   // t1 (bound only for Mask/others that need it)

in vec2 vUv;
in vec4 vCol;
out vec4 outColor;

// D3D9 AlphaTestEnable (Greater, ref 0) applied to the final PS output.
void ApplyAlphaTest(vec4 c) {
  if ((AlphaTest != 0u && c.a <= 0.0) || (AlphaRef > 0.0 && c.a <= AlphaRef)) discard;
}

// HSV hue rotation (Photoshop-style) — K-constant formulation, ported as-is.
vec3 RGBtoHSV(vec3 c) {
  vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
  vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
  vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
  float d = q.x - min(q.w, q.y);
  float e = 1.0e-10;
  return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}
vec3 HSVtoRGB(vec3 c) {
  vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
  vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
  return c.z * mix(vec3(1.0), clamp(p - K.xxx, 0.0, 1.0), c.y);
}

// Hue rotation + greyscale-then-tint recolour — shared by EVERY sprite PS. The
// recolour ramp scales with luminance (black stays black — additive effects treat
// black as transparent) and the cubed term pushes only the hottest texels to white.
vec3 ApplyRecolour(vec3 rgb) {
  if (HueShift != 0.0) {
    vec3 hsv = RGBtoHSV(rgb);
    hsv.x = fract(hsv.x + HueShift);
    rgb = HSVtoRGB(hsv);
  }
  if (Recolour.w != 0.0) {
    float lum = dot(rgb, vec3(0.299, 0.587, 0.114));
    float hot = lum * lum * lum;
    rgb = mix(rgb, Recolour.rgb * lum + hot * (1.0 - Recolour.rgb), Recolour.w);
  }
  return rgb;
}
`;

export const SPRITE_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec4 t = texture(SpriteTex, vUv);
  // ColorOp == 1: the legacy "colour" stage — vertex colour drives alpha only.
  vec4 c = (ColorOp == 1u) ? vec4(vCol.rgb, t.a * vCol.a) : t * vCol;
  c.rgb = ApplyRecolour(c.rgb);
  ApplyAlphaTest(c);
  outColor = c;
}
`;

export const ORB_LIQUID_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec2 iuv = vUv * ImageNorm.xy;
  float wave = cos((iuv.x * 12.0 + OrbParams.y) * 1.5) * 0.5
             + sin((iuv.y * 15.0 + OrbParams.y) * -2.0) * 0.5
             + cos((iuv.x + iuv.y) * 10.0 + OrbParams.y) * 0.2;
  // HLSL clamp broadcasts a scalar bound against a vector; GLSL has no
  // clamp(vec2, float, vec2) overload — only (genType,genType,genType) and
  // (genType,float,float). vec2(0.0) is the literal equivalent of the HLSL.
  vec2 suv = clamp(iuv + wave * 0.015, vec2(0.0), ImageNorm.zw);
  vec4 t = texture(SpriteTex, suv / ImageNorm.xy);
  float surfaceY = cos(iuv.x * OrbParams.z + OrbParams.y) * OrbParams.w + OrbParams.x;
  float below = (iuv.y - surfaceY) >= 0.0 ? 1.0 : 0.0;
  // QUIRK PRESERVED (do not fix): the abs() runs on the 0/1 comparison mask, not
  // the signed distance — lip-glow only ever contributes a constant 0.09 above
  // the surface where alpha is already 0 (G17 parity vs the DX9 asm port).
  float glow = clamp((0.045 - abs(below)) * 2.0, 0.0, 1.0);
  vec4 c = mix(t, OrbLipGlow, glow);
  c.a *= below;
  c *= vCol;
  ApplyAlphaTest(c);
  outColor = c;
}
`;

export const BLEND_KEY_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec4 c = texture(SpriteTex, vUv) * vCol;
  // Key computed from the ORIGINAL colour, BEFORE any recolour — recolouring
  // first would change the max(R,G,B) the threshold tests and could revive
  // padding the cut exists to remove. Order is the contract.
  float key = (max(max(c.r, c.g), c.b) - BlendKeyParams.x) >= 0.0 ? BlendKeyParams.y : BlendKeyParams.z;
  c *= key;
  c.rgb = ApplyRecolour(c.rgb);
  ApplyAlphaTest(c);
  outColor = c;
}
`;

export const MASK_PS = FRAGMENT_PREAMBLE + `
void main() {
  // circle.ps port: image RGB, alpha scaled by the mask texture's alpha at the
  // same image-space UV. No vertex-colour multiply (the asm never reads v0).
  vec4 img = texture(SpriteTex, vUv);
  img.a *= texture(MaskTex, vUv * ImageNorm.xy * MaskNorm.xy).a;
  ApplyAlphaTest(img);
  outColor = img;
}
`;

export const SAFE_ZONE_OUTLINE_PS = FRAGMENT_PREAMBLE + `
void main() {
  const vec2 RingOuter[8] = vec2[8](
    vec2( 1.00000,  0.00000), vec2( 0.70711,  0.70711),
    vec2( 0.00000,  1.00000), vec2(-0.70711,  0.70711),
    vec2(-1.00000,  0.00000), vec2(-0.70711, -0.70711),
    vec2( 0.00000, -1.00000), vec2( 0.70711, -0.70711)
  );
  const vec2 RingInner[8] = vec2[8]( // rotated 22.5° for interleaved coverage
    vec2( 0.92388,  0.38268), vec2( 0.38268,  0.92388),
    vec2(-0.38268,  0.92388), vec2(-0.92388,  0.38268),
    vec2(-0.92388, -0.38268), vec2(-0.38268, -0.92388),
    vec2( 0.38268, -0.92388), vec2( 0.92388, -0.38268)
  );
  vec2 ts = SafeZoneAnchor.xy * SafeZoneParams.y;
  // B = neutral (an unwalkable cell beside the zone, markSafeZoneNeutralCells): those taps are left out of the
  // average, so a zone/wall border has no coverage step and draws nothing.
  vec3 s0 = texture(SpriteTex, vUv).rgb;
  vec2 fill = s0.rg;
  float cover = 1.0 - s0.b;
  vec2 gx = vec2(0.0);
  vec2 gy = vec2(0.0);
  for (int t = 0; t < 8; ++t) {
    vec3 sOuter = texture(SpriteTex, vUv + RingOuter[t] * ts).rgb;
    vec3 sInner = texture(SpriteTex, vUv + RingInner[t] * ts * 0.5).rgb;
    fill += sOuter.rg + sInner.rg;
    cover += 2.0 - sOuter.b - sInner.b;
    gx += sOuter.rg * RingOuter[t].x;
    gy += sOuter.rg * RingOuter[t].y;
  }
  fill /= max(cover, 1e-3);
  vec2 band = clamp(1.0 - abs(fill * 2.0 - 1.0), 0.0, 1.0); // peaks at the boundary
  band *= clamp(cover, 0.0, 1.0);                           // fade out where (almost) every tap is neutral
  band = band * band * (3.0 - 2.0 * band);                  // smoothstep the stepped coverage
  vec2 world = gl_FragCoord.xy - SafeZoneAnchor.zw;         // SV_Position.xy equivalent
  vec3 rgb = vec3(0.0);
  float a = 0.0;
  for (int k = 0; k < 2; ++k) {
    vec2 grad = vec2(gx[k], gy[k]);
    vec2 tang = vec2(-grad.y, grad.x) / max(length(grad), 1e-4);
    float phase = dot(world, tang) * 0.07 - SafeZoneParams.x * SafeZoneParams.w;
    float flow = 0.6 + 0.4 * sin(phase)
               + 0.25 * sin(phase * 2.33 + SafeZoneParams.x * 1.7); // broken pulses
    vec4 zc = (k == 0) ? SafeZoneColorA : SafeZoneColorB;
    float glowK = band[k] * band[k];                 // wide soft aura
    float core = smoothstep(0.55, 0.92, band[k]);    // thin bright line on the edge itself
    float strength = clamp((glowK * 0.6 + core) * flow, 0.0, 1.0) * SafeZoneParams.z * zc.a;
    rgb += zc.rgb * strength + core * flow * 0.18;   // slight white-hot core
    a = max(a, strength);
  }
  vec4 c = vec4(rgb, a) * vCol;
  ApplyAlphaTest(c);
  outColor = c;
}
`;

// SafeZoneSparkle_PS (SpriteBatch2D.cs:983-1062), the default safezone border. Same mask input and 17-tap band as
// SAFE_ZONE_OUTLINE_PS; the band is a twinkling star field anchored in world space (map cell 0,0 via SafeZoneAnchor.zw).
// SparkleHash is SpriteBatch2D.cs:453-457. The C# reads top-down SV_Position, gl_FragCoord is bottom-up, so the page passes
// the anchor in the same bottom-up space (the field is just mirrored, which a random star field cannot show).
export const SAFE_ZONE_SPARKLE_PS = FRAGMENT_PREAMBLE + `
vec2 SparkleHash(vec2 p) {
  p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
  return fract(sin(p) * 43758.5453);
}
void main() {
  const vec2 RingOuter[8] = vec2[8](
    vec2( 1.00000,  0.00000), vec2( 0.70711,  0.70711),
    vec2( 0.00000,  1.00000), vec2(-0.70711,  0.70711),
    vec2(-1.00000,  0.00000), vec2(-0.70711, -0.70711),
    vec2( 0.00000, -1.00000), vec2( 0.70711, -0.70711)
  );
  const vec2 RingInner[8] = vec2[8]( // rotated 22.5° for interleaved coverage
    vec2( 0.92388,  0.38268), vec2( 0.38268,  0.92388),
    vec2(-0.38268,  0.92388), vec2(-0.92388,  0.38268),
    vec2(-0.92388, -0.38268), vec2(-0.38268, -0.92388),
    vec2( 0.38268, -0.92388), vec2( 0.92388, -0.38268)
  );
  vec2 ts = SafeZoneAnchor.xy * SafeZoneParams.y;
  // B = neutral taps, left out of the average exactly as in SAFE_ZONE_OUTLINE_PS.
  vec3 s0 = texture(SpriteTex, vUv).rgb;
  vec2 fill = s0.rg;
  float cover = 1.0 - s0.b;
  for (int t = 0; t < 8; ++t) {
    vec3 sOuter = texture(SpriteTex, vUv + RingOuter[t] * ts).rgb;
    vec3 sInner = texture(SpriteTex, vUv + RingInner[t] * ts * 0.5).rgb;
    fill += sOuter.rg + sInner.rg;
    cover += 2.0 - sOuter.b - sInner.b;
  }
  fill /= max(cover, 1e-3);
  vec2 band = clamp(1.0 - abs(fill * 2.0 - 1.0), 0.0, 1.0); // peaks at the boundary
  band *= clamp(cover, 0.0, 1.0);                           // fade out where (almost) every tap is neutral
  band = band * band * (3.0 - 2.0 * band);                  // smoothstep the stepped coverage

  float tw8 = SafeZoneParams.x * SafeZoneSparkleParams.y;
  vec2 g = (gl_FragCoord.xy - SafeZoneAnchor.zw) * SafeZoneSparkleParams.x;
  vec2 id = floor(g);
  vec2 f = g - id;
  id -= floor(id * (1.0 / 256.0)) * 256.0; // wrap before hashing (fp32 sin() precision)

  float sparkle = 0.0;
  for (int cy = -1; cy <= 1; ++cy)
  for (int cx = -1; cx <= 1; ++cx) {
    vec2 o = vec2(float(cx), float(cy));
    vec2 h = SparkleHash(id + o);
    vec2 d = f - (o + 0.15 + 0.7 * h);
    float ph = fract(h.x * 7.13 + tw8 * (0.6 + 0.8 * h.y));
    float tk = sin(ph * 6.2831853);
    tk = tk > 0.0 ? pow(tk, 8.0) : 0.0;
    if (tk <= 0.0) continue;
    float core = clamp(1.0 - length(d) * 7.0, 0.0, 1.0);
    float flare = clamp(1.0 - abs(d.x) * 24.0, 0.0, 1.0) * clamp(1.0 - abs(d.y) * 4.5, 0.0, 1.0)
                + clamp(1.0 - abs(d.y) * 24.0, 0.0, 1.0) * clamp(1.0 - abs(d.x) * 4.5, 0.0, 1.0);
    sparkle += (core * core * 0.65 + flare * 0.75) * tk;
  }
  sparkle = clamp(sparkle, 0.0, 1.0);

  vec3 rgb = vec3(0.0);
  float a = 0.0;
  for (int k = 0; k < 2; ++k) {
    vec4 zc = (k == 0) ? SafeZoneColorA : SafeZoneColorB;
    float rim = band[k] * band[k] * 0.22; // faint always-on rim between twinkles
    float strength = clamp(sparkle * band[k] * SafeZoneSparkleParams.z + rim, 0.0, 1.0) * SafeZoneParams.z * zc.a;
    rgb += zc.rgb * strength + clamp(sparkle * band[k] - 0.6, 0.0, 1.0) * 0.9; // white-hot cores
    a = max(a, strength);
  }
  vec4 c = vec4(rgb, a) * vCol;
  ApplyAlphaTest(c);
  outColor = c;
}
`;

// The three overlay programs (ClawSlash, RipClaw, DruidFx): a plain white quad (D3D11Backend.cs:861-870) whose visual comes
// from `i.uv`; only DruidFx style 7 samples t0 (a one-pixel-per-map-cell mask, :922). Sources: SpriteBatch2D.cs:593-638
// (ClawSlash_PS), :649-713 (RipClaw_PS), :723-981 (DruidFx_PS).
//
// Two non-mechanical translations, both load-bearing:
//   * GLSL ES 3.0 has no implicit int->float, so `k * 0.135f` is written `float(k) * 0.135`.
//   * HLSL smoothstep is well defined for e0 > e1 (descending ramp); GLSL ES 3.00 s8.3 leaves it UNDEFINED. DruidFx_PS has nine
//     reversed-edge calls (styles 0, 1, 2, 5, 6), so all 24 of its calls go through `hlslSmoothstep`. The other two programs'
//     smoothsteps are ascending and keep the built-in.
// RipClaw's local `cross` is renamed `crossAngle` and DruidFx style 7's `cross2`: `cross` is a GLSL built-in and shadowing it
// is driver-dependent.
export const CLAW_SLASH_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec2 p = vUv * 2.0 - 1.0;           // [-1,1] quad space, centre = impact point
  float c = ClawParams.y;
  float s = ClawParams.z;
  float a = dot(p, vec2(-s, c));      // along each gash (perpendicular to the facing)
  float b = dot(p, vec2( c, s));      // across the gash set (the facing axis)

  float prog = clamp(ClawParams.x, 0.0, 1.0);

  // Strike: carved in over the first 35% of the life, revealed along +a.
  float wipe   = clamp(prog * 2.857143, 0.0, 1.0);       // 1 / 0.35
  float front  = wipe * 2.4 - 1.2;                       // sweeps past both ends
  float reveal = clamp((front - a) * 6.0, 0.0, 1.0);
  float fade   = 1.0 - smoothstep(0.55, 1.0, prog);
  float spread = 1.0 + 0.3 * smoothstep(0.35, 1.0, prog);

  float marks = 0.0;
  for (int k = -1; k <= 1; ++k) {
    // Fanned like a paw and each gash bowed — see the HLSL comment (:615-616).
    float lane  = float(k) * ClawShape.x * spread * (0.7 + 0.45 * (a * 0.5 + 0.5));
    float bow   = ClawShape.y * (a * a - 0.35);
    float dist  = abs(b - lane - bow);
    float taper = pow(clamp(1.0 - a * a, 0.0, 1.0), 0.6) * (k == 0 ? 1.0 : 0.78);
    float w     = ClawShape.z * taper;
    float core  = w > 0.0001 ? clamp(1.0 - dist / w, 0.0, 1.0) : 0.0;
    marks += core * core;
  }
  marks = clamp(marks, 0.0, 1.0) * reveal * fade;
  if (marks <= 0.004) discard;

  // White-hot leading edge while the stroke is still being carved.
  float head = clamp(1.0 - abs(a - front) * 3.5, 0.0, 1.0) * (1.0 - wipe) * marks;

  // Composited additively (BlendMode.LIGHT) — only RGB reaches the frame, so the
  // fade has to live in the colour, not just the alpha (:632-633).
  vec3 rgb = ClawTint.rgb * marks * (0.55 + 0.85 * marks)
           + vec3(1.0, 0.82, 0.7) * (head * 1.4 + clamp(marks - 0.72, 0.0, 1.0) * 1.2);
  float alpha = clamp(marks * ClawParams.w + head * 0.5, 0.0, 1.0) * ClawTint.a;
  vec4 result = vec4(rgb * ClawParams.w, alpha) * vCol;
  ApplyAlphaTest(result);
  outColor = result;
}
`;

export const RIP_CLAW_PS = FRAGMENT_PREAMBLE + `
const float RipSecondDelay = 0.12; // fraction of the life the second rake lags (:649)

void main() {
  vec2 p = vUv * 2.0 - 1.0;           // [-1,1] quad space, centre = impact point
  float c = ClawParams.y;
  float s = ClawParams.z;

  float prog = clamp(ClawParams.x, 0.0, 1.0);

  // Second rake's rotation, folded into the frame vectors (:660-662). HLSL's
  // local 'cross' is renamed — see the section comment above.
  float crossAngle = ClawShape.w;
  float cc = cos(crossAngle);
  float sc = sin(crossAngle);

  float fade   = 1.0 - smoothstep(0.55, 1.0, prog);
  float spread = 1.0 + 0.3 * smoothstep(0.35, 1.0, prog);

  float marks = 0.0;
  float head  = 0.0;

  for (int rake = 0; rake < 2; ++rake) {
    float rc = rake == 0 ? c : c * cc - s * sc;
    float rs = rake == 0 ? s : s * cc + c * sc;

    float a = dot(p, vec2(-rs, rc));  // along each gash
    float b = dot(p, vec2( rc, rs));  // across the gash set

    float rakeProg = rake == 0 ? prog : clamp(prog - RipSecondDelay, 0.0, 1.0);
    float wipe   = clamp(rakeProg * 2.857143, 0.0, 1.0);   // 1 / 0.35
    float front  = wipe * 2.4 - 1.2;
    float reveal = clamp((front - a) * 6.0, 0.0, 1.0);

    float rakeMarks = 0.0;
    for (int k = -1; k <= 1; ++k) {
      float lane  = float(k) * ClawShape.x * spread * (0.7 + 0.45 * (a * 0.5 + 0.5));
      float bow   = ClawShape.y * (a * a - 0.35);
      float dist  = abs(b - lane - bow);
      float taper = pow(clamp(1.0 - a * a, 0.0, 1.0), 0.6) * (k == 0 ? 1.0 : 0.82);
      float w     = ClawShape.z * taper;
      float core  = w > 0.0001 ? clamp(1.0 - dist / w, 0.0, 1.0) : 0.0;
      rakeMarks += core * core;
    }
    rakeMarks = clamp(rakeMarks, 0.0, 1.0) * reveal * fade;

    // Union, not sum: where the two rakes cross the wound reads as one gouge
    // rather than blowing out to white (:698-701).
    marks = max(marks, rakeMarks);
    head  = max(head, clamp(1.0 - abs(a - front) * 3.5, 0.0, 1.0) * (1.0 - wipe) * rakeMarks);
  }

  if (marks <= 0.004) discard;

  // Hot RED highlight, not ClawSlash's bone white — Rip is the bleeding strike
  // (:709-710).
  vec3 rgb = ClawTint.rgb * marks * (0.65 + 0.95 * marks)
           + vec3(1.0, 0.24, 0.16) * (head * 1.5 + clamp(marks - 0.62, 0.0, 1.0) * 1.6);
  float alpha = clamp(marks * ClawParams.w + head * 0.5, 0.0, 1.0) * ClawTint.a;
  vec4 result = vec4(rgb * ClawParams.w, alpha) * vCol;
  ApplyAlphaTest(result);
  outColor = result;
}
`;

export const DRUID_FX_PS = FRAGMENT_PREAMBLE + `
// SpriteBatch2D.cs:453-457 — the shared hash the sparkle/smoke styles seed from.
vec2 SparkleHash(vec2 p) {
  p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
  return fract(sin(p) * 43758.5453);
}

// HLSL's smoothstep, verbatim. NOT the GLSL built-in: this program has NINE
// calls with edge0 > edge1, in SEVEN statements (style 0 one, style 1 one,
// style 2 one, style 5 one, style 6 three), where GLSL ES 3.00 leaves the result
// UNDEFINED while HLSL returns the descending ramp the C# is drawing.
float hlslSmoothstep(float e0, float e1, float x) {
  float t = clamp((x - e0) / (e1 - e0), 0.0, 1.0);
  return t * t * (3.0 - 2.0 * t);
}

void main() {
  vec2 p     = vUv * 2.0 - 1.0;   // [-1,1] quad space
  int  style = int(DruidFxParams.x + 0.5);
  float prog  = clamp(DruidFxParams.y, 0.0, 1.0);
  float c     = DruidFxParams.z;
  float s     = DruidFxParams.w;
  float squash = max(0.05, DruidFxShape.y);

  float amount = 0.0;   // coverage 0..1
  float hot    = 0.0;   // white-hot core, added on top of the tint

  if (style == 0) {
    // THORNS — a ring of barbs snapping outward around the protected ally (:737-757).
    vec2 q = vec2(p.x, p.y / squash);
    float r = length(q);
    float a = atan(q.y, q.x);

    float ring  = 0.34 + 0.20 * hlslSmoothstep(0.0, 0.35, prog);
    float spin  = prog * 0.12;
    float sa    = fract(a * (1.0 / 6.2831853) * 11.0 + spin);  // 11 barbs
    float tri   = abs(sa * 2.0 - 1.0);
    float barb  = 0.30 * (1.0 - tri) * (1.0 - tri);

    float outer = ring + barb;
    float band  = hlslSmoothstep(outer, outer - 0.05, r) * hlslSmoothstep(ring - 0.15, ring - 0.03, r);
    float rim   = clamp(1.0 - abs(r - ring) * 26.0, 0.0, 1.0);

    amount = clamp(band * 0.85 + rim * 0.7, 0.0, 1.0);
    hot    = rim * 0.5;
  } else if (style == 1) {
    // MARK OF THE WILD — a six-petal bloom that opens and drifts upward (:761-777).
    vec2 q = vec2(p.x, (p.y + prog * 0.35) / squash);
    float r = length(q);
    float a = atan(q.y, q.x);

    float open  = hlslSmoothstep(0.0, 0.45, prog);
    float petal = abs(cos(a * 3.0));
    float edge  = (0.20 + 0.52 * open) * (0.45 + 0.55 * petal);

    float body  = hlslSmoothstep(edge, edge - 0.10, r);
    float outline = clamp(1.0 - abs(r - edge) * 14.0, 0.0, 1.0);
    float seed  = clamp(1.0 - r * 7.0, 0.0, 1.0) * (1.0 - open);

    amount = clamp(body * 0.30 + outline * 0.95 + seed, 0.0, 1.0);
    hot    = clamp(outline * 0.45 + seed * 0.9, 0.0, 1.0);
  } else if (style == 2) {
    // LEADER OF THE PACK — a rune ring stamped on the GROUND (:781-804).
    vec2 q = vec2(p.x, p.y / squash);
    float r = length(q);
    float a = atan(q.y, q.x);

    float pulse = hlslSmoothstep(0.0, 0.7, prog);
    float ring  = 0.25 + 0.62 * pulse;
    float band  = clamp(1.0 - abs(r - ring) * 11.0, 0.0, 1.0);
    float ta    = fract(a * (1.0 / 6.2831853) * 8.0);
    float tick  = clamp(1.0 - abs(ta * 2.0 - 1.0) * 5.0, 0.0, 1.0);
    float ticks = band * tick;
    float fill  = hlslSmoothstep(ring, ring - 0.35, r) * (1.0 - pulse) * 0.35;

    amount = clamp(band * 0.7 + ticks * 0.8 + fill, 0.0, 1.0);
    hot    = ticks * 0.55;
  } else if (style == 3) {
    // SWIPE — the Maim rake bent into an arc covering the facing (:806-844).
    // UNREACHABLE in this client: its only desktop producer is the Attack arm's
    // MagicType switch (MapObject.cs:2587-2590), which the port does not model
    // (9b-3d's Q5 first called this 'no producer' — corrected in D10). Ported
    // anyway — the branch is inside this shader regardless.
    vec2 q = vec2(p.x, p.y / squash);
    float r = length(q);

    float ang = atan(q.y, q.x) - atan(s, c);
    ang = ang - 6.2831853 * floor((ang + 3.14159265) * (1.0 / 6.2831853));

    const float ArcHalf = 1.30;   // ~75 degrees either side: a 150-degree sweep
    if (abs(ang) > ArcHalf) discard;

    float wipe   = clamp(prog * 2.857143, 0.0, 1.0);
    float front  = wipe * (2.4 * ArcHalf) - 1.2 * ArcHalf;
    float reveal = clamp((front - ang) * 5.0, 0.0, 1.0);
    float fade3  = 1.0 - hlslSmoothstep(0.55, 1.0, prog);
    float grow   = 1.0 + 0.18 * hlslSmoothstep(0.0, 0.6, prog);

    float t3 = ang / ArcHalf;
    for (int k = -1; k <= 1; ++k) {
      float lane  = (0.56 + float(k) * 0.135) * grow;
      float taper = pow(clamp(1.0 - t3 * t3, 0.0, 1.0), 0.6) * (k == 0 ? 1.0 : 0.8);
      float w     = 0.058 * taper;
      float d     = abs(r - lane);
      float core  = w > 0.0001 ? clamp(1.0 - d / w, 0.0, 1.0) : 0.0;
      amount += core * core;
    }
    amount = clamp(amount, 0.0, 1.0) * reveal * fade3;

    hot = clamp(1.0 - abs(ang - front) * 2.2, 0.0, 1.0) * (1.0 - wipe) * amount;
  } else if (style == 4) {
    // BLOOD SPLATTER — one of the two styles composited with normal alpha
    // (style 5 is the other; the blend is chosen by the op, :846-881).
    // It returns straight from here: the shared additive tail below would
    // premultiply the colour by coverage and wash it out to nothing.
    float r = length(p);
    float a = atan(p.y, p.x);
    float burst = hlslSmoothstep(0.0, 0.30, prog);
    float dry   = 1.0 - hlslSmoothstep(0.45, 1.0, prog);

    const float Drops = 13.0;
    float ga = a * (1.0 / 6.2831853) * Drops;
    float id = floor(ga);
    float fa = ga - id;
    vec2  h  = SparkleHash(vec2(id, 11.3));

    float reach  = (0.30 + 0.62 * h.x) * burst;
    float narrow = clamp(1.0 - abs(fa - 0.5) * (4.0 + 5.0 * h.y), 0.0, 1.0);
    float along  = clamp(1.0 - abs(r - reach * 0.78) / max(0.06, 0.30 * (0.35 + 0.65 * h.y)), 0.0, 1.0);
    float drops  = narrow * along;

    float blot = clamp(1.0 - r / max(0.02, 0.21 * burst), 0.0, 1.0);

    float wet = clamp((drops * 1.5 + blot * 1.1) * dry * DruidFxShape.x, 0.0, 1.0);
    if (wet <= 0.01) discard;

    // Thicker toward the blot core so a fresh splash reads deeper (:878-879).
    vec3 blood = DruidFxTint.rgb * (0.60 + 0.40 * clamp(blot + drops * 0.4, 0.0, 1.0));
    vec4 result = vec4(blood, wet * DruidFxTint.a) * vCol;
    ApplyAlphaTest(result);
    outColor = result;
    return;
  } else if (style == 5) {
    // SHAPESHIFT SMOKE — overlapping soft puffs, curling upward (:882-916).
    // DEFERRED producer (needs CatFormActive/UpdateLibraries); branch ported.
    float appear = hlslSmoothstep(0.0, 0.12, prog);
    float vanish = 1.0 - hlslSmoothstep(0.52, 1.0, prog);
    float spread = hlslSmoothstep(0.0, 0.72, prog);
    float smoke = 0.0;

    for (int puffIndex = 0; puffIndex < 9; ++puffIndex) {
      vec2 h = SparkleHash(vec2(float(puffIndex), 37.1));
      float angle = (float(puffIndex) + h.x * 0.75) * 0.6981317;
      float radius = (0.08 + 0.42 * h.x) * spread;
      vec2 centre = vec2(cos(angle), sin(angle) * squash) * radius;
      centre.y -= prog * (0.08 + 0.22 * h.y);
      centre.x += sin(prog * 5.0 + h.y * 6.2831853) * 0.035;

      float puffRadius = 0.20 + 0.10 * h.y + 0.14 * prog;
      vec2 local = vec2(p.x - centre.x, (p.y - centre.y) / squash);
      float d = length(local);
      float softPuff = hlslSmoothstep(puffRadius, puffRadius * 0.28, d);
      smoke += softPuff * (0.32 + 0.30 * h.x);
    }

    float billow = 0.82 + 0.18 * sin(p.x * 18.0 + prog * 4.0)
                            * sin(p.y * 15.0 - prog * 3.0);
    smoke = clamp(smoke * billow, 0.0, 1.0) * appear * vanish * DruidFxShape.x;
    if (smoke <= 0.008) discard;

    float depth = 0.62 + 0.30 * smoke;
    vec3 smokeColour = DruidFxTint.rgb * depth;
    vec4 result = vec4(smokeColour, smoke * DruidFxTint.a) * vCol;
    ApplyAlphaTest(result);
    outColor = result;
    return;
  } else if (style == 7) {
    // BIG-MAP SPAWN SMOKE — t0 is a one-pixel-per-map-cell mask (:917-936).
    // UNREACHABLE in this client: no producer, and druidSpec(7) returns
    // Thorns' row (no Specs entry). Ported regardless — branch parity.
    // The C# tests style 7 BEFORE style 6 (:917 then :937); the two arms are
    // disjoint on one uniform, but the ORDER is kept so a diff of the two files
    // stays statement-for-statement.
    float mask = texture(SpriteTex, vUv).a;
    if (mask <= 0.001) discard;

    const float Tau = 6.2831853;
    vec2 uv = vUv;
    float broad = sin((uv.x * 7.0 + uv.y * 4.0) * Tau + prog * Tau);
    float cross2 = sin((uv.x * 13.0 - uv.y * 9.0) * Tau - prog * Tau * 2.0);
    float curl = sin((uv.x * 23.0 + uv.y * 17.0) * Tau + prog * Tau * 3.0);
    float cloud = clamp(0.52 + broad * 0.24 + cross2 * 0.17 + curl * 0.09, 0.0, 1.0);
    cloud = hlslSmoothstep(0.16, 0.92, cloud);
    float breathe = 0.82 + 0.18 * sin(prog * Tau + (uv.x + uv.y) * Tau * 3.0);
    float smoke = mask * (0.30 + 0.70 * cloud) * breathe * DruidFxShape.x;
    vec3 smokeColour = DruidFxTint.rgb * (0.62 + 0.30 * cloud);
    vec4 result = vec4(smokeColour, clamp(smoke, 0.0, 1.0) * DruidFxTint.a) * vCol;
    ApplyAlphaTest(result);
    outColor = result;
    return;
  } else if (style == 6) {
    // DASH SPARKLES — small green motes bursting outward (:937-968).
    vec2 q = vec2(p.x, p.y / squash);
    float appear = hlslSmoothstep(0.0, 0.08, prog);
    float vanish = 1.0 - hlslSmoothstep(0.42, 1.0, prog);

    for (int sparkleIndex = 0; sparkleIndex < 12; ++sparkleIndex) {
      vec2 h = SparkleHash(vec2(float(sparkleIndex), 73.9));
      float angle = (float(sparkleIndex) + h.x * 0.85) * 0.5235988;
      float travel = (0.08 + prog * (0.48 + 0.34 * h.y));
      vec2 centre = vec2(cos(angle), sin(angle)) * travel;
      centre.y -= prog * (0.03 + 0.12 * h.x);

      vec2 local = q - centre;
      float core = hlslSmoothstep(0.050, 0.006, length(local));
      float rayX = hlslSmoothstep(0.105, 0.020, abs(local.x))
                 * hlslSmoothstep(0.014, 0.002, abs(local.y));
      float rayY = hlslSmoothstep(0.105, 0.020, abs(local.y))
                 * hlslSmoothstep(0.014, 0.002, abs(local.x));
      float twinkle = 0.52 + 0.48 * sin(prog * 31.0 + h.x * 12.5663706);
      float star = clamp(core + (rayX + rayY) * 0.72, 0.0, 1.0) * twinkle;
      amount += star;
      hot += core * twinkle;
    }

    amount = clamp(amount, 0.0, 1.0) * appear * vanish;
    hot = clamp(hot, 0.0, 1.0) * appear * vanish;
  }

  // Shared tail-off: every remaining style holds, then dissolves over the last
  // 40% of its life (:970-980).
  float fade = 1.0 - hlslSmoothstep(0.6, 1.0, prog);
  amount = clamp(amount * fade * DruidFxShape.x, 0.0, 1.0);
  if (amount <= 0.004) discard;

  vec3 rgb = DruidFxTint.rgb * amount * (0.55 + 0.85 * amount)
           + vec3(1.0, 0.97, 0.86) * hot * fade * amount;
  vec4 result = vec4(rgb, clamp(amount, 0.0, 1.0) * DruidFxTint.a) * vCol;
  ApplyAlphaTest(result);
  outColor = result;
}
`;

// Crystal Client/MirGraphics/Rendering/D3D11Backend.cs Grayscale_PS (DXManager.SetGrayscale): luma of the TEXEL only (0.3/0.59/0.11, the
// grayscale.ps weights), texel alpha kept; the vertex colour is ignored, as on the desktop (opacity still applies through the blend factor).
export const GRAYSCALE_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec4 t = texture(SpriteTex, vUv);
  float l = clamp(dot(t.rgb, vec3(0.3, 0.59, 0.11)), 0.0, 1.0);
  vec4 c = vec4(l, l, l, t.a);
  ApplyAlphaTest(c);
  outColor = c;
}
`;

// Crystal D3D11Backend.cs Light_PS (Enhanced lighting): an elliptical falloff (1 - r^2)^2 across the quad (UVs 0..1), scaled by the vertex colour
// and an intensity of alpha * 4; added (One, One) into the half-float light map.
export const LIGHT_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec2 d = vUv * 2.0 - 1.0;
  float f = 1.0 - clamp(dot(d, d), 0.0, 1.0);
  outColor = vec4(vCol.rgb * (vCol.a * 4.0 * f * f), 1.0);
}
`;

// Crystal D3D11Backend.cs LightComposite_PS: the light map soft-knees above 1 towards 2 and is output halved; the (DstColor, SrcColor) blend doubles it,
// so the result is scene * light with light in 0..2.
export const LIGHT_COMPOSITE_PS = FRAGMENT_PREAMBLE + `
void main() {
  vec3 l = max(texture(SpriteTex, vUv).rgb, vec3(0.0));
  vec3 over = max(l - 1.0, vec3(0.0));
  l = min(l, vec3(1.0)) + over / (1.0 + over);
  outColor = vec4(l * 0.5, 0.5);
}
`;

/** SpriteShaderMode -> fragment source, mirroring the D3D11 PS selection. The six cosmetic modes are absent by design
 * (they degrade to Default like the desktop's compile-failure path); the three overlay programs are not in that set. */
export const FRAGMENT_PROGRAMS: Record<string, string> = {
  Default: SPRITE_PS,
  OrbLiquid: ORB_LIQUID_PS,
  BlendKey: BLEND_KEY_PS,
  Mask: MASK_PS,
  SafeZoneOutline: SAFE_ZONE_OUTLINE_PS,
  SafeZoneSparkle: SAFE_ZONE_SPARKLE_PS,
  ClawSlash: CLAW_SLASH_PS,
  RipClaw: RIP_CLAW_PS,
  DruidFx: DRUID_FX_PS,
  Grayscale: GRAYSCALE_PS,
  Light: LIGHT_PS,
  LightComposite: LIGHT_COMPOSITE_PS,
};
