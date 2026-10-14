// Web-only post-processing for the Enhanced atmosphere (game/atmosphere.ts): no desktop counterpart. Full-screen programs of its own, outside the
// sprite program table (they need samplers and uniforms the sprite blocks do not have): a separable 9-tap Gaussian (5 linear fetches) for the glow,
// world-anchored fog, the snow cover / wet ground, the snowfall and rainfall, and the final composite onto the canvas (heat-haze UV wobble, dungeon depth,
// weather tint, wet reflections of the light map, frost, glow). Every pass draws one attribute-less triangle (gl_VertexID) and reads its source at
// gl_FragCoord / size, so render-target and canvas rows line up without the sprite path's flipped rect.
// Raw GL runs between renderer.beginExternal() / endExternal(), which flush the deferred sprite draws first and restore the sprite program after.

import type { WebGl2Renderer, RenderTargetRef } from "./backend.ts";

const FULLSCREEN_VS = `#version 300 es
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
`;

const NOISE_GLSL = `
float hash(vec2 p) {
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}
float noise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float fbm(vec2 p) {
  float v = 0.0, a = 0.5;
  for (int i = 0; i < 4; i++) { v += a * noise(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; }
  return v;
}
`;

// The cell mask (atmosphere.ts buildCellMask): one texel per map cell around the player, R = floor tile (rain lands), G = water (fishing cells),
// B = eaves-drip base (the cell under a tall front object: wall, building, tree). uMaskOrigin = world pixel of the mask's top-left cell corner,
// uMaskCell0 = that cell's map coordinates (hashes use absolute cells, so nothing reshuffles when the mask moves with the player). Sampled LINEAR
// for soft cell edges (cellMask) or exact (cellAt). Eaves drips: DRIPS columns per drip-base cell, each releasing a drop from h px above the cell's
// top edge (the object's foot) once per period; a cycle is skipped unless its hash is under uDrip (so the rate follows the rain without the phase
// jumping). Both the rainfall (the falling drop) and the ground (its splash) use dripColumn, so they agree.
const MASK_GLSL = `
uniform sampler2D uMask;
uniform vec2 uMaskOrigin;
uniform vec2 uMaskCell0;
uniform vec2 uMaskCells;
uniform float uDrip;
const vec2 CELL = vec2(48.0, 32.0);
const int DRIPS = 3;
const float DRIP_G = 900.0;
vec4 cellMask(vec2 world) {
  vec2 uv = (world - uMaskOrigin) / (CELL * uMaskCells);
  if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) return vec4(0.0);
  return texture(uMask, uv);
}
vec4 cellAt(vec2 rel) {
  if (rel.x < 0.0 || rel.y < 0.0 || rel.x >= uMaskCells.x || rel.y >= uMaskCells.y) return vec4(0.0);
  return texelFetch(uMask, ivec2(rel), 0);
}
// Column k of drip-base cell ac (absolute): x inside the cell, fall height h, period per, phase offset off; live = this cycle drips; t = seconds
// since this cycle's release; T = fall time.
void dripColumn(vec2 ac, int k, out float x, out float h, out float t, out float T, out bool live) {
  vec2 id = ac + vec2(float(k) * 17.3, float(k) * 5.1);
  x = (0.15 + 0.7 * hash(id + 3.7)) * CELL.x;
  h = 34.0 + 40.0 * hash(id + 8.1);
  float per = 0.7 + 1.1 * hash(id + 1.9);
  float cyc = uTime / per + hash(id + 5.3);
  live = hash(id + vec2(0.37, floor(cyc) * 0.013)) < uDrip;
  t = fract(cyc) * per;
  T = sqrt(2.0 * h / DRIP_G);
}
`;

// One splash at sp (world px), drop radius rr, ph 0..1 through its life: a bright core that fades fast, a crown ring flaring out to 3x the drop
// radius, and two droplets thrown up and outward on a short arc that land as the crown dies. Squashed vertically for the isometric view.
const SPLASH_GLSL = `
float splashAt(vec2 world, vec2 sp, float rr, float ph, float seed) {
  float d = length((world - sp) * vec2(1.0, 1.7));
  float fade = 1.0 - ph;
  float acc = (1.0 - smoothstep(rr * 0.3, rr, d)) * (1.0 - smoothstep(0.0, 0.45, ph));
  acc += (1.0 - smoothstep(0.0, 0.9, abs(d - rr * (1.0 + 2.0 * ph)))) * fade * 0.7;
  float h = rr * 2.6 * 4.0 * ph * (1.0 - ph);
  for (int k = 0; k < 2; k++) {
    float s = (k == 0 ? -1.0 : 1.0) * (0.6 + 0.8 * hash(vec2(seed + 3.3 + float(k), seed)));
    vec2 dp = sp + vec2(s * rr * 2.4 * ph, -h);
    acc += (1.0 - smoothstep(0.35, 0.95, length(world - dp))) * fade;
  }
  return acc;
}
`;

// 9-tap Gaussian (sigma ~ 2) folded into 5 bilinear fetches; uStep = direction * spread / source size.
const BLUR_FS = `#version 300 es
precision highp float;
uniform sampler2D uSrc;
uniform vec2 uSize;
uniform vec2 uStep;
out vec4 outColor;
void main() {
  vec2 uv = gl_FragCoord.xy / uSize;
  vec3 c = texture(uSrc, uv).rgb * 0.2270270270;
  c += texture(uSrc, uv + uStep * 1.3846153846).rgb * 0.3162162162;
  c += texture(uSrc, uv - uStep * 1.3846153846).rgb * 0.3162162162;
  c += texture(uSrc, uv + uStep * 3.2307692308).rgb * 0.0702702703;
  c += texture(uSrc, uv - uStep * 3.2307692308).rgb * 0.0702702703;
  outColor = vec4(c, 1.0);
}
`;

// The final pass. Heat haze: the mask (R, half resolution, bilinear) scales a UV wobble of a few pixels from detuned sines in PIXEL space that rise
// over time (GL y is up). Then dungeon depth (darker away from the player), the weather tint, wet reflections, frost at the screen edges, and the
// glow screen-blended on top so a hot spell keeps its colour instead of clipping to white.
// Wet reflections (REFLECT_FS, drawn before the composite at HALF resolution into two targets the composite upsamples bilinearly: part 0 the object
// mirror, premultiplied RGBA8; part 1 the lamps, RGBA16F; web performance 2026-10-14), only on wet floor (uWetMask: the reflectivity snapshot, see
// GROUND_FS uPass 1; no snapshot = no reflections). Distances stay in screen pixels: a half-resolution texel shades the centre of its 2 x 2 pixels.
// - objects mirrored in puddles: march up from the pixel until the snapshot turns to an object (the contact line, at most 96 px), then sample the
//   scene as far above the contact as the pixel is below it, rippled sideways; fades with distance and only mirrors object pixels;
// - lamps (uLegacy, the first look, in the composite at full resolution, off by default): where a soft-limited over-1 light core above a pixel is brighter than the pixel's own, a short rippling streak
//   below each lamp;
// - lamp reflections (uRCount > 0): every lamp of the light pass (ReflectionSource) mirrored across the line where its object meets the ground.
//   Per lamp: a tight hot core at the mirror point (puddles only), a streak stretched towards the viewer (long and soft on rough wet ground, short
//   and sharp in puddles), a broad sheen on wet ground, and the lamp's own art (the bright flame / glass pixels around the emitter) mirrored into
//   the puddle. All of it is pushed around by the ground pass's own ripple rings (ringDisp: the same grids, hashes and timing as GROUND_FS drops,
//   so a reflection breaks up exactly where the visible rings run) and a slow wobble, fades in across the contact line, and adds in HDR (hot
//   cores run to white) before the glow screen. Knobs: reflectionTuning (the scene lab tunes them; the defaults are the shipped look).
/** At most this many lamps reflect at once (the strongest when more can reach the screen). */
export const MAX_REFLECTIONS = 48; // <= 64: the tile bins are two 32-bit masks
/** Lamp-reflection tile size in screen px (REFLECT_FS uTiles). */
const REFLECT_TILE = 32;
/** The largest ringDisp() length: 3 grids of 2x2 cells, each ring's push at most 2.3 * max(x exp(-x^2)) = 0.986, weighted 1 + 0.8 + 0.6. */
const RING_DISP_MAX = 4 * 0.986 * (1 + 0.8 + 0.6);
const REFLECT_FS = `#version 300 es
precision highp float;
uniform sampler2D uScene;
uniform sampler2D uWetMask;
uniform vec2 uSize;      // the scene (screen) size
uniform float uTime;
uniform vec3 uTint;      // weather colour grade (multiply)
uniform float uReflect;  // wet-ground reflection strength
uniform float uPart;     // 0 = objects mirrored in puddles, 1 = lamp reflections
uniform int uRCount;     // lamp reflections this frame
uniform vec4 uRPos[${MAX_REFLECTIONS}];  // x, mirror y, contact y, radius (screen px, y down)
uniform vec4 uRCol[${MAX_REFLECTIONS}];  // colour * intensity, emitter y
uniform vec4 uRT0;       // gain, streak length px, streak width px, core gain
uniform vec4 uRT1;       // flame mirror gain, sheen gain, ripple push px, wobble px
uniform vec4 uRT2;       // puddle reflectivity low, high, contact fade px, hot-to-white
uniform vec4 uRT3;       // rough wet ground strength, halo gain, colour warmth (saturation of the tail), -
uniform vec2 uCam;       // world pixel of the screen's top-left
uniform float uRipples;  // the ground pass's ripple share (0 = no rings)
uniform highp usampler2D uTiles; // per uTile x uTile screen tile (y down): bit i (R: lamps 0-31, G: 32-47) = lamp i can reach it (lampTiles)
uniform float uTile;
out vec4 outColor;
${NOISE_GLSL}
// One of GROUND_FS's ripple grids (drops: same cells, hashes and timing) as a sideways push in px: each ring's slope bends the reflection outward
// on its front and back in behind it. Only the 2x2 cells nearest the pixel (see GROUND_FS drops()).
vec2 ringGrid(vec2 world, vec2 cellSize, float seed, float maxR, float share, float width) {
  vec2 cell = floor(world / cellSize - 0.5);
  vec2 acc = vec2(0.0);
  for (int oy = 0; oy <= 1; oy++) {
    for (int ox = 0; ox <= 1; ox++) {
      vec2 c = cell + vec2(float(ox), float(oy));
      if (hash(c + seed) > uRipples * share) continue;
      float period = (0.6 + hash(c + seed + 3.1) * 1.0) * (0.7 + maxR / 20.0);
      float ph = fract(uTime / period + hash(c + seed + 9.7));
      float r = maxR * (0.35 + 0.65 * hash(c + seed + 6.6));
      vec2 centre = (c + vec2(hash(c + seed + 1.3), hash(c + seed + 5.9))) * cellSize;
      vec2 v = (world - centre) * vec2(1.0, 1.7);
      float d = length(v);
      float x = (d - ph * r) / width;
      acc += (d > 0.001 ? v / d : vec2(0.0)) * (x * exp(-x * x) * 2.3) * (1.0 - ph) * (1.0 - ph);
    }
  }
  return acc;
}
vec2 ringDisp(vec2 world) {
  if (uRipples <= 0.0) return vec2(0.0);
  return ringGrid(world, vec2(26.0, 17.0), 0.0, 6.0, 1.0, 1.1)
       + ringGrid(world, vec2(42.0, 27.0), 40.0, 11.0, 0.55, 1.35) * 0.8
       + ringGrid(world, vec2(66.0, 42.0), 80.0, 18.0, 0.3, 1.6) * 0.6;
}
// Every lamp's reflection at screen pixel sp (y down); wet = the snapshot's reflectivity there; bits = this pixel's tile (only those lamps can
// reach it: the rest would all hit the cull below).
vec3 lampReflections(vec2 sp, float wet, uvec2 bits) {
  float puddle = smoothstep(uRT2.x, uRT2.y, wet);
  float rough = 1.0 - puddle;
  vec2 world = sp + uCam;
  vec2 disp = ringDisp(world) * uRT1.z;
  disp.x += (noise(world * vec2(0.045, 0.16) + vec2(0.0, uTime * 1.3)) - 0.5) * uRT1.w * (0.6 + rough);
  disp.y += (noise(world * vec2(0.09, 0.05) - vec2(uTime * 0.7, 0.0)) - 0.5) * uRT1.w * 0.5;
  vec2 p = sp + disp;
  vec3 acc = vec3(0.0);
  for (int i = 0; i < ${MAX_REFLECTIONS}; i++) {
    if (i >= uRCount) break;
    if (i < 32 ? (bits.x >> uint(i)) == 0u && bits.y == 0u : (bits.y >> uint(i - 32)) == 0u) break; // no higher lamp reaches this tile
    if ((i < 32 ? (bits.x >> uint(i)) & 1u : (bits.y >> uint(i - 32)) & 1u) == 0u) continue;
    vec4 L = uRPos[i];
    vec4 C = uRCol[i];
    float k = clamp(L.w / 400.0, 0.35, 1.2); // a small light (a carried candle) reflects smaller than a map lamp
    // fades in below the contact line (the mirror image starts at the object's foot); the wide terms fade in more slowly
    float below = smoothstep(L.z - uRT2.z, L.z + uRT2.z, sp.y);
    if (below <= 0.0) continue;
    float belowSoft = smoothstep(L.z - uRT2.z, L.z + uRT2.z * 6.0, sp.y);
    vec2 q = p - L.xy;
    // the streak: down (towards the viewer) longer than up (towards the contact line), flaring a little with distance
    float lenD = 4.0 + uRT0.y * k * (0.3 + 0.7 * rough);
    float lenU = lenD * 0.4;
    float sy = q.y / (q.y > 0.0 ? lenD : lenU);
    // nothing reaches past 3 sheen radii or 4 streak lengths (exp() is below 1e-4 there); lampTiles bins with the same bounds
    if (abs(q.x) > max(210.0 * k, 6.0 + 3.0 * uRT0.z) || q.y > max(4.0 * lenD, 300.0 * k) || q.y < -max(4.0 * lenU, 300.0 * k)) continue;
    float wid = 1.0 + uRT0.z * k * (0.3 + 0.7 * rough) * (1.0 + 0.6 * abs(sy));
    float sx = q.x / wid;
    float streak = exp(-sx * sx) * (exp(-sy * sy * 1.6) + 0.3 * exp(-abs(sy) * 1.4));
    // the hot core at the mirror point: a sharp image of the flame in a puddle, a blurred one on wet ground
    vec2 qc = q / ((vec2(2.0, 3.0) + vec2(4.0, 6.0) * k) * (1.0 + 1.6 * rough));
    float core = exp(-dot(qc, qc)) * (puddle + 0.45 * rough);
    // the warm halo around the core (light scattered by the wet film), and a broad soft sheen on the wet ground
    vec2 qh = q / (vec2(14.0, 22.0) * k * (1.0 + rough));
    float halo = exp(-dot(qh, qh));
    vec2 qs = q / (vec2(70.0, 100.0) * k);
    float sheen = exp(-dot(qs, qs)) * (0.35 + 0.65 * rough);
    // the tail and the halo take the lamp's colour more saturated than the white-hot core
    float cm = max(C.r, max(C.g, C.b));
    vec3 warm = cm > 0.0 ? pow(C.rgb / cm, vec3(1.0 + uRT3.z)) * cm : C.rgb;
    vec3 col = (mix(warm, C.rgb, 0.35) * (core * uRT0.w) + warm * (streak * uRT0.x * (0.45 + 0.55 * puddle) + halo * uRT3.y)) * below
             + warm * (sheen * uRT1.y * belowSoft);
    // the lamp's own art mirrored across the contact line, keyed to its bright pixels near the emitter
    if (uRT1.x > 0.0) {
      vec2 src = vec2(sp.x + disp.x * 1.5, 2.0 * L.z - sp.y - disp.y);
      vec2 qe = (src - vec2(L.x, C.w)) / (vec2(6.0, 8.0) + vec2(22.0, 30.0) * k);
      float near = exp(-dot(qe, qe));
      if (near > 0.01 && src.y >= 0.0 && src.y < uSize.y) {
        vec3 s = texture(uScene, vec2(src.x, uSize.y - src.y) / uSize).rgb;
        float key = smoothstep(0.42, 0.9, max(s.r, max(s.g, s.b)));
        col += s * (key * near * uRT1.x * (0.35 + 0.65 * puddle) * below);
      }
    }
    acc += col;
  }
  // hot reflections run to white, like an over-exposed highlight
  float m = max(acc.r, max(acc.g, acc.b));
  acc += vec3(smoothstep(0.6, 2.2, m) * m * uRT2.w * 0.5);
  return acc * mix(uRT3.x, 1.0, puddle) * smoothstep(0.02, 0.12, wet);
}
void main() {
  // one half-resolution texel stands for the 2 x 2 screen pixels around frag (screen GL pixels, y up); all sizes stay in screen pixels
  vec2 frag = gl_FragCoord.xy * 2.0;
  float wetHere = clamp((0.5 - texture(uWetMask, frag / uSize).r) * 2.0, 0.0, 1.0);
  outColor = vec4(0.0);
  if (wetHere <= 0.01) return;
  if (uPart < 0.5) {
    // objects mirrored in puddles, premultiplied (rgb = mirror * k, a = k): the composite does scene * (1 - a) + rgb
    float contact = 0.0;
    for (int i = 1; i <= 32; i++) {
      float d = float(i) * 3.0;
      if (texture(uWetMask, (frag + vec2(0.0, d)) / uSize).r > 0.62) { contact = d; break; }
    }
    if (contact > 0.0) {
      float rip = noise(frag * vec2(0.08, 0.35) + vec2(0.0, uTime * 2.6)) - 0.5;
      vec2 src = frag + vec2(rip * 4.0, contact * 2.0);
      float obj = smoothstep(0.55, 0.8, texture(uWetMask, src / uSize).r);
      vec3 mirrored = texture(uScene, src / uSize).rgb * uTint * vec3(0.78, 0.84, 0.94);
      float k = wetHere * obj * (1.0 - contact / 100.0) * 0.55 * uReflect;
      outColor = vec4(mirrored * k, k);
    }
    return;
  }
  vec2 sp = vec2(frag.x, uSize.y - frag.y);
  uvec2 bits = texelFetch(uTiles, clamp(ivec2(floor(sp / uTile)), ivec2(0), textureSize(uTiles, 0) - 1), 0).rg;
  if ((bits.x | bits.y) == 0u) return;
  // eroded: the least reflective of this pixel and four around it, so floor seen through small gaps in a tree or a figure stays unlit
  float wetE = wetHere;
  for (int i = 0; i < 4; i++) {
    vec2 o = vec2(i == 0 ? 4.0 : i == 1 ? -4.0 : 0.0, i == 2 ? 4.0 : i == 3 ? -4.0 : 0.0);
    wetE = min(wetE, clamp((0.5 - texture(uWetMask, (frag + o) / uSize).r) * 2.0, 0.0, 1.0));
  }
  if (wetE > 0.01) outColor = vec4(lampReflections(sp, wetE, bits) * uTint * uReflect, 1.0);
}
`;

const COMPOSITE_FS = `#version 300 es
precision highp float;
uniform sampler2D uScene;
uniform sampler2D uGlow;
uniform sampler2D uHaze;
uniform sampler2D uLight;
uniform float uHasLight;
uniform vec2 uSize;
uniform float uTime;
uniform float uGlowStrength;
uniform float uHazePixels;
uniform vec2 uCentre;    // the player, in GL pixels (y up)
uniform float uDepth;    // darkening with distance at the screen edge (0 = off; dungeons)
uniform vec3 uTint;      // weather colour grade (multiply)
uniform float uReflect;  // wet-ground reflections of lights (0 = off)
uniform float uFrost;    // frost at the screen edges (0 = off)
uniform sampler2D uWetMask;
uniform float uHasWet;   // uWetMask holds this frame's snapshot
uniform float uLegacy;   // the first lamp streak's strength
uniform sampler2D uMirror; // REFLECT_FS part 0 (half resolution, bilinear): objects mirrored in puddles, premultiplied
uniform sampler2D uLamps;  // REFLECT_FS part 1 (half resolution, bilinear, HDR): lamp reflections
uniform float uHasMirror;
uniform float uHasLamps;
uniform vec2 uHalf;      // the reflection targets' size
out vec4 outColor;
${NOISE_GLSL}
void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 uv = frag / uSize;
  float h = texture(uHaze, uv).r;
  vec2 offset = vec2(0.0);
  if (h > 0.003) {
    float t = uTime;
    float wx = sin(frag.y * 0.115 - t * 7.1 + sin(frag.x * 0.043 + t * 1.7) * 1.6)
             + 0.5 * sin(frag.y * 0.231 - t * 11.3 + frag.x * 0.021);
    float wy = sin(frag.x * 0.097 + t * 5.3 + sin(frag.y * 0.051 - t * 2.3) * 1.4) * 0.6;
    offset = vec2(wx, wy) * (h * uHazePixels / 1.5);
  }
  vec3 scene = texture(uScene, (frag + offset) / uSize).rgb;
  if (uDepth > 0.0) {
    vec2 d = (frag - uCentre) / (uSize * vec2(0.5, 0.56));
    scene *= 1.0 - uDepth * smoothstep(0.22, 1.1, length(d));
  }
  scene *= uTint;
  float wetHere = 1.0;
  if (uReflect > 0.0 && uHasWet > 0.5) wetHere = clamp((0.5 - texture(uWetMask, uv).r) * 2.0, 0.0, 1.0);
  vec2 huv = frag * 0.5 / uHalf;
  if (uHasMirror > 0.5) {
    vec4 m = texture(uMirror, huv);
    scene = scene * (1.0 - m.a) + m.rgb;
  }
  if (uHasLamps > 0.5) scene += texture(uLamps, huv).rgb;
  if (uLegacy > 0.0 && uReflect > 0.0 && uHasLight > 0.5 && wetHere > 0.01) {
    vec3 refl = vec3(0.0);
    vec3 here = max(texture(uLight, uv).rgb - 1.0, vec3(0.0));
    here = here / (1.0 + here);
    float wob = sin(frag.y * 0.35 + uTime * 4.0) * 1.6;
    for (int k = 1; k <= 7; k++) {
      float fk = float(k);
      vec3 s = max(texture(uLight, (frag + vec2(wob * fk * 0.4, fk * 9.0)) / uSize).rgb - 1.0, vec3(0.0));
      refl += max(s / (1.0 + s) - here, vec3(0.0)) * (1.0 - fk / 8.0);
    }
    scene += refl * (uReflect * 0.12 * uLegacy * (uHasWet > 0.5 ? 0.4 + 1.2 * wetHere : 1.0));
  }
  if (uFrost > 0.0) {
    vec2 q = abs(uv - 0.5) * 2.0;
    float e = smoothstep(0.6, 1.0, max(q.x, q.y * 1.05));
    if (e > 0.0) { // the fBm only at the edges
      e *= 0.5 + 0.7 * fbm(frag / 9.0);
      scene = mix(scene, vec3(0.86, 0.93, 1.0), clamp(e * uFrost, 0.0, 0.7));
    }
  }
  vec3 glow = texture(uGlow, uv).rgb * uGlowStrength;
  glow = glow / (1.0 + glow * 0.35);
  outColor = vec4(1.0 - (1.0 - clamp(scene, 0.0, 1.0)) * (1.0 - clamp(glow, 0.0, 1.0)), 1.0);
}
`;

// World-anchored fog: two fBm value-noise layers in WORLD pixels (screen + camera), carried by the wind drift, alpha-blended over the bound target
// before the light pass (so the light map multiplies it: dark at night, glowing around lamps). The previous frame's Enhanced light map thins it in
// the brightest light cores, so torches burn a hole and keep a lit rim.
const FOG_FS = `#version 300 es
precision highp float;
uniform sampler2D uLight;
uniform float uHasLight;
uniform vec2 uSize;
uniform vec2 uCam;       // world pixel of the screen's top-left
uniform vec2 uDrift;     // world pixels the wind has carried this layer
uniform float uScale;    // noise cells per world pixel
uniform float uDensity;  // peak opacity
uniform float uFloor;    // share of the density that never thins out
uniform vec3 uColour;
out vec4 outColor;
${NOISE_GLSL}
void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 world = vec2(frag.x, uSize.y - frag.y) + uCam;
  float n = fbm((world - uDrift) * uScale) * 0.62
          + fbm((world - uDrift * 0.6 + vec2(531.0, 217.0)) * (uScale * 1.9)) * 0.38;
  float d = uDensity * (uFloor + (1.0 - uFloor) * smoothstep(0.28, 0.78, n));
  if (uHasLight > 0.5) {
    float lum = dot(texture(uLight, frag / uSize).rgb, vec3(0.3, 0.59, 0.11));
    d *= 1.0 - 0.75 * smoothstep(0.9, 2.4, lum);
  }
  outColor = vec4(uColour, d);
}
`;

// Ground weather, after the floor (before the objects and lights). Mode 0, snow cover: a two-scale noise mask whose threshold falls as uAmount
// rises, so drifts grow into a blanket. Mode 1, wet ground, only where the cell mask has a floor tile (never the black past the map's edge):
// puddles (a noise threshold that falls as uWet rises, so they spread the longer it rains; water cells count as one) darker than the soaked ground
// around them; ripple rings at three drop sizes (drops() on three grids, each drop with its own radius; mostly in puddles and on water, doubled on
// water); splashes() where drops land; and the splash of every eaves drip (dripColumn) at the foot of walls and trees. Output colour * alpha +
// scene * (1 - alpha) = ring / splash tint + darkened scene.
// uPass 1 (wet only, drawn colour-masked with blending off) writes the target's ALPHA instead: 0.5 - 0.5 * reflectivity on floor (puddles and
// water mirror most), 1 elsewhere. Everything drawn after it raises alpha towards 1 (opaque sprites write 1), so a snapshot taken after the objects
// tells the composite which pixels are wet floor and which are objects standing on it (screen-space puddle reflections).
const GROUND_FS = `#version 300 es
precision highp float;
uniform vec2 uSize;
uniform vec2 uCam;
uniform float uTime;
uniform float uMode;
uniform float uAmount;
uniform float uRipples;
uniform float uSplash;
uniform float uWet;
uniform float uPass;
out vec4 outColor;
${NOISE_GLSL}
${MASK_GLSL}
${SPLASH_GLSL}
// Expanding rings on one grid: each cell holds a drop with probability uRipples * share, its own max radius (maxR, px), period and phase; the ring
// (width px) fades as it grows, and inner adds a weaker second ring trailing at 0.55 of the radius. Wider drops spread slower. strength scales
// the grid's contribution. Only the 2x2 cells nearest the pixel: every grid's ring (maxR + width, y squashed by 1.7) stays inside half a cell of
// its centre, so a drop in any other cell cannot reach the pixel (2.25x fewer cells than a 3x3 scan, same picture). ringGrid uses the same cells.
float drops(vec2 world, vec2 cellSize, float seed, float maxR, float share, float strength, float width, float inner) {
  vec2 cell = floor(world / cellSize - 0.5);
  float acc = 0.0;
  for (int oy = 0; oy <= 1; oy++) {
    for (int ox = 0; ox <= 1; ox++) {
      vec2 c = cell + vec2(float(ox), float(oy));
      if (hash(c + seed) > uRipples * share) continue;
      float period = (0.6 + hash(c + seed + 3.1) * 1.0) * (0.7 + maxR / 20.0);
      float ph = fract(uTime / period + hash(c + seed + 9.7));
      float r = maxR * (0.35 + 0.65 * hash(c + seed + 6.6));
      vec2 centre = (c + vec2(hash(c + seed + 1.3), hash(c + seed + 5.9))) * cellSize;
      float d = length((world - centre) * vec2(1.0, 1.7));
      float fade = (1.0 - ph) * (1.0 - ph);
      acc += (1.0 - smoothstep(0.0, width, abs(d - ph * r))) * fade;
      acc += (1.0 - smoothstep(0.0, width * 0.8, abs(d - ph * r * 0.55))) * fade * inner;
    }
  }
  return acc * strength;
}
// Drop impacts on one grid (cells under uSplash), each live for the first 30% of its own period.
float splashes(vec2 world, vec2 sc) {
  vec2 cell = floor(world / sc);
  float acc = 0.0;
  for (int oy = -1; oy <= 1; oy++) {
    for (int ox = -1; ox <= 1; ox++) {
      vec2 c = cell + vec2(float(ox), float(oy));
      if (hash(c + 21.7) >= uSplash) continue;
      float per = 0.45 + hash(c + 4.4) * 0.5;
      float ph = fract(uTime / per + hash(c + 8.8)) / 0.3;
      if (ph >= 1.0) continue;
      vec2 sp = (c + vec2(hash(c + 1.1), hash(c + 2.2))) * sc;
      acc += splashAt(world, sp, 1.2 + 2.3 * hash(c + 6.6), ph, hash(c + 0.7) * 50.0);
    }
  }
  return acc;
}
// The splashes of the eaves drips landing at the top edge of this pixel's cell (or the cell below, for the droplets thrown up).
float dripSplashes(vec2 world) {
  vec2 rel = floor((world - uMaskOrigin) / CELL);
  float acc = 0.0;
  for (int r = 0; r <= 1; r++) {
    vec2 rc = rel + vec2(0.0, float(r));
    if (cellAt(rc).b < 0.5) continue;
    vec2 ac = rc + uMaskCell0;
    float baseY = uMaskOrigin.y + rc.y * CELL.y;
    for (int k = 0; k < DRIPS; k++) {
      float x, h, t, T; bool live;
      dripColumn(ac, k, x, h, t, T, live);
      float ph = (t - T) / 0.3;
      if (!live || ph < 0.0 || ph >= 1.0) continue;
      acc += splashAt(world, vec2(uMaskOrigin.x + rc.x * CELL.x + x, baseY + 3.0), 2.8, ph, ac.x * 0.37 + float(k));
    }
  }
  return acc;
}
// atmosphere.ts puddleAt mirrors this on the CPU (footfall splashes): change both together.
float puddle(vec2 world) {
  float edge = 0.72 - 0.24 * uWet;
  return smoothstep(edge, edge + 0.09, fbm(world / 150.0 + 3.1));
}
void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 world = vec2(frag.x, uSize.y - frag.y) + uCam;
  if (uMode < 0.5) {
    float n = fbm(world / 110.0) * 0.7 + fbm(world / 31.0 + 7.3) * 0.3;
    float edge = 1.0 - uAmount;
    float a = smoothstep(edge - 0.12, edge + 0.12, n) * 0.82;
    outColor = vec4(0.9, 0.93, 0.98, a);
    return;
  }
  vec4 m = cellMask(world);
  float floorM = smoothstep(0.3, 0.7, m.r);
  float water = smoothstep(0.3, 0.7, m.g);
  float pud = max(puddle(world), water);
  if (uPass > 0.5) {
    float refl = uWet * (0.22 + 0.78 * pud);
    outColor = vec4(0.0, 0.0, 0.0, mix(1.0, 0.5 - 0.5 * refl, floorM));
    return;
  }
  if (floorM <= 0.0) { outColor = vec4(0.0); return; }
  float dark = uAmount * (0.13 + 0.2 * pud) * (0.75 + 0.5 * fbm(world / 140.0)) * (1.0 - 0.7 * water);
  // Drop impacts at three scales: many small rings, fewer medium, a few wide puddle rings with a trailing inner ring.
  float ring = drops(world, vec2(26.0, 17.0), 0.0, 6.0, 1.0, 1.0, 1.1, 0.0)
             + drops(world, vec2(42.0, 27.0), 40.0, 11.0, 0.55, 0.7, 1.35, 0.45)
             + drops(world, vec2(66.0, 42.0), 80.0, 18.0, 0.3, 0.45, 1.6, 0.6);
  ring *= (0.3 + 0.7 * pud) * (1.0 + water);
  float splash = splashes(world, vec2(20.0, 13.0)) * (1.0 - 0.6 * water) + dripSplashes(world);
  ring = clamp(clamp(ring, 0.0, 1.0) * 0.32 + clamp(splash, 0.0, 1.0) * 0.45, 0.0, 1.0);
  float a = clamp(dark + ring, 0.0, 1.0);
  vec3 col = a > 0.0 ? vec3(0.85, 0.9, 1.0) * (ring / a) : vec3(0.0);
  outColor = vec4(col, a * floorM);
}
`;

// Scene alpha -> the wet mask target (R): the reflectivity snapshot taken after the objects (see GROUND_FS uPass 1).
const SNAPSHOT_FS = `#version 300 es
precision highp float;
uniform sampler2D uSrc;
uniform vec2 uSize;
out vec4 outColor;
void main() {
  float a = texture(uSrc, gl_FragCoord.xy / uSize).a;
  outColor = vec4(a, a, a, 1.0);
}
`;

// Snowfall: three layers of round flakes on jittered grids (far: small, slow, 0.6 parallax; near: big, fast, 1.15 parallax), falling with time,
// carried sideways by the wind drift and swaying; alpha-blended before the lights so lamps light the flakes.
const SNOWFALL_FS = `#version 300 es
precision highp float;
uniform vec2 uSize;
uniform vec2 uCam;
uniform float uTime;
uniform vec2 uDrift;
uniform float uAmount;
out vec4 outColor;
${NOISE_GLSL}
float flakes(vec2 screen, float cellSize, float speed, float parallax, float radius, float seed) {
  vec2 p = screen + uCam * parallax - vec2(uDrift.x * parallax, uTime * speed);
  p.x += sin(p.y * 0.021 + seed) * 7.0;
  vec2 c = floor(p / cellSize);
  if (hash(c + seed) > uAmount) return 0.0;
  vec2 pos = (vec2(hash(c + seed + 1.7), hash(c + seed + 4.1)) * 0.8 + 0.1) * cellSize;
  float d = length(p - c * cellSize - pos);
  return 1.0 - smoothstep(radius * 0.35, radius, d);
}
void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 screen = vec2(frag.x, uSize.y - frag.y);
  float a = flakes(screen, 34.0, 38.0, 0.6, 1.3, 0.0) * 0.5
          + flakes(screen, 56.0, 64.0, 0.85, 2.0, 11.0) * 0.75
          + flakes(screen, 96.0, 104.0, 1.15, 3.1, 23.0) * 0.9;
  outColor = vec4(0.95, 0.97, 1.0, clamp(a, 0.0, 1.0) * 0.9);
}
`;

// Rainfall (replaces the Weather.Lib rain tiles): three layers of tapered streaks in sparse columns (far: short, faint, slow, 0.55 parallax; near:
// long, thick, bright, fast, 1.1 parallax) plus a fourth just in front of the camera (1.6 parallax: a few big soft out-of-focus streaks), all
// sheared by the wind (uSlant). Gust sheets: a world-anchored noise band, carried by the wind drift (uSheet), thickens the rain and the grey wash
// (uWash) where it passes, so curtains of heavier rain sweep across the screen. Lamp glints: where last frame's light map (uLight) is past full
// white, streaks brighten and warm, so rain sparkles around torches. Eaves drips: drops falling from tall objects to their foot (dripColumn).
// Alpha-blended before the lights, so lamps light nearby rain and it stays dark at night. Fall offsets wrap every 4096 cells per layer.
const RAINFALL_FS = `#version 300 es
precision highp float;
uniform sampler2D uLight;
uniform float uHasLight;
uniform vec2 uSize;
uniform vec2 uCam;
uniform float uTime;
uniform float uAmount;
uniform float uSlant;
uniform float uWash;
uniform float uSheet;
uniform float uNear;
out vec4 outColor;
${NOISE_GLSL}
${MASK_GLSL}
// soft 0 = a crisp streak brightest at its head; 1 = an out-of-focus one, soft-edged and brightest mid-length.
float streaks(vec2 screen, float amount, float parallax, float colW, float cellH, float speed, float len, float thick, float soft, float seed) {
  vec2 p = screen + uCam * parallax;
  p.x -= screen.y * uSlant;
  float col = floor(p.x / colW);
  float y = p.y - mod(uTime * speed, cellH * 4096.0) + hash(vec2(col, seed)) * 997.0;
  float seg = floor(y / cellH);
  vec2 id = vec2(col, seg + seed * 13.0);
  if (hash(id) > amount) return 0.0;
  float x0 = (0.2 + 0.6 * hash(id + 2.3)) * colW;
  float dx = abs(p.x - col * colW - x0);
  float along = (y - seg * cellH - hash(id + 7.1) * (cellH - len)) / len;
  if (along < 0.0 || along > 1.0) return 0.0;
  float w = thick * (0.45 + 0.55 * mix(along, 1.0, soft)); // tapers to a thin tail above the head
  return (1.0 - smoothstep(w * 0.5 * (1.0 - soft), w, dx)) * mix(along, sin(along * 3.14159), soft);
}
// Eaves drips over this pixel: drip-base cells up to 3 rows below (the fall is at most 74 px), each column's drop a short streak while it falls.
float drips(vec2 world) {
  vec2 rel = floor((world - uMaskOrigin) / CELL);
  float acc = 0.0;
  for (int r = 0; r <= 3; r++) {
    vec2 rc = rel + vec2(0.0, float(r));
    if (cellAt(rc).b < 0.5) continue;
    vec2 ac = rc + uMaskCell0;
    float baseY = uMaskOrigin.y + rc.y * CELL.y;
    for (int k = 0; k < DRIPS; k++) {
      float x, h, t, T; bool live;
      dripColumn(ac, k, x, h, t, T, live);
      if (!live || t >= T) continue;
      float y = baseY - h + 0.5 * DRIP_G * t * t;
      float len = 3.0 + DRIP_G * t * 0.03;
      float along = (world.y - (y - len)) / len;
      if (along < 0.0 || along > 1.0) continue;
      float dx = abs(world.x - (uMaskOrigin.x + rc.x * CELL.x + x));
      acc += (1.0 - smoothstep(0.5, 1.4, dx)) * (0.35 + 0.65 * along) * smoothstep(0.0, 0.2, t / T);
    }
  }
  return acc;
}
void main() {
  vec2 frag = gl_FragCoord.xy;
  vec2 screen = vec2(frag.x, uSize.y - frag.y);
  vec2 world = screen + uCam;
  float sheet = smoothstep(0.3, 0.72, noise(vec2((world.x - world.y * uSlant) * 0.0032 - uSheet, world.y * 0.0011)));
  float amount = uAmount * (0.55 + 0.8 * sheet);
  float a = streaks(screen, amount, 0.55, 19.0, 220.0, 360.0, 20.0, 1.5, 0.0, 0.0) * 0.38
          + streaks(screen, amount, 0.8, 30.0, 300.0, 510.0, 34.0, 2.1, 0.0, 5.0) * 0.55
          + streaks(screen, amount, 1.1, 46.0, 400.0, 700.0, 54.0, 2.9, 0.0, 11.0) * 0.72;
  a += streaks(screen, amount * uNear, 1.6, 150.0, 900.0, 1150.0, 150.0, 11.0, 1.0, 17.0) * 0.32;
  float glint = 0.0;
  if (uHasLight > 0.5) glint = smoothstep(0.9, 2.4, dot(texture(uLight, frag / uSize).rgb, vec3(0.3, 0.59, 0.11)));
  a = clamp(a * (1.0 + 1.3 * glint), 0.0, 1.0) * 0.6;
  float d = clamp(drips(world), 0.0, 1.0) * 0.7;
  vec3 streakCol = mix(vec3(0.74, 0.79, 0.88), vec3(1.0, 0.96, 0.88), glint);
  a = a + d * (1.0 - a);
  float wash = uWash * (0.6 + 0.8 * sheet);
  float total = a + wash * (1.0 - a);
  vec3 col = (streakCol * a + vec3(0.55, 0.58, 0.63) * wash * (1.0 - a)) / max(total, 1e-4);
  outColor = vec4(col, total);
}
`;

/** One fog layer's look (FOG_FS uniforms); `wind` is its speed in world pixels per second at gust 1. */
export interface FogLayer {
  density: number;
  floor: number;
  scale: number;
  wind: [number, number];
  colour: [number, number, number];
}

/** The composite's weather and depth inputs. */
export interface CompositeOptions {
  /** The player in screen pixels (y down). */
  centre: { x: number; y: number };
  depth: number;
  tint: [number, number, number];
  /** This frame's Enhanced light map (wet reflections), or null. */
  light: RenderTargetRef | null;
  reflect: number;
  frost: number;
  /** This frame's reflectivity snapshot (snapshotAlpha), or null (lamp reflections then cover everything, as before the mask). */
  wetMask: RenderTargetRef | null;
  /** The half-resolution reflection targets (REFLECT_FS part 0: RGBA8, part 1: RGBA16F, both bilinear, the same size), or null (no reflections). */
  reflectTargets: { mirror: RenderTargetRef; lamps: RenderTargetRef } | null;
  /** The light pass's lamps (EnhancedLightLayer.reflections), mirrored in the wet ground; empty = none. */
  lamps: readonly ReflectionSource[];
  /** World pixel of the screen's top-left and the ground pass's ripple share (the reflections ripple with the same rings). */
  camX: number; camY: number;
  ripples: number;
}

/** One lamp for the puddle reflections, in screen pixels (y down): it shines from (x, emitterY) and its object meets the ground on contactY, so
 *  it mirrors to (x, 2 * contactY - emitterY). `radius` = the light's horizontal radius; rgb = colour * intensity (0..2). */
export interface ReflectionSource {
  x: number; emitterY: number; contactY: number; radius: number;
  r: number; g: number; b: number;
}

/** Puddle reflection knobs (the scene lab, play/lab.ts, tunes them; the game never sets them, so these defaults are the shipped look, tuned in
 *  the lab 2026-10-14). Streak
 *  length / width in px on rough wet ground (puddles shorten and sharpen them). Placement in px from the light's centre in the light map (which
 *  sits below the flame, a desktop quirk): an animated map lamp shines from `lampEmitter` and stands on its sprite lift * `lampHeightScale` +
 *  `lampContact`; a static map lamp (the light at its cell's foot) shines from `staticLampHeight` above; a carried light / effect shines from its
 *  height above and stands `carriedFoot` below. `puddleLow` / `puddleHigh`: the wet-mask reflectivity from rough wet ground to a mirror puddle.
 *  `legacy` = the first light-map streak (always full when `enabled` is off). */
export const reflectionTuning = {
  enabled: true,
  gain: 4,
  streakLength: 0,
  streakWidth: 0,
  coreGain: 0,
  haloGain: 0.46,
  roughGain: 0,
  warmth: 2,
  flameGain: 1.58,
  sheenGain: 1,
  rippleDisp: 8,
  wobble: 6,
  puddleLow: 0,
  puddleHigh: 1,
  contactFade: 14,
  hotWhite: 0.35,
  legacy: 0,
  carriedHeight: 30,
  carriedFoot: 22,
  effectHeight: 0,
  staticLampHeight: 0,
  lampEmitter: 14,
  lampContact: -46,
  lampHeightScale: 1.05,
};

/** Where the cell mask (setCellMask) sits: world pixel of its top-left cell corner, that cell's map coordinates, and its size in cells. */
export interface CellMaskView {
  originX: number; originY: number;
  cellX: number; cellY: number;
  cols: number; rows: number;
}

/** The wet-ground inputs shared by the ground and rainfall passes. */
export interface WetInputs {
  /** 0..1 how soaked the ground is (puddle spread, reflectivity). */
  wetness: number;
  /** 0..1 share of eaves-drip cycles that drip. */
  drip: number;
  mask: CellMaskView;
}

/** The rainfall pass's extras. */
export interface RainExtras {
  /** Last frame's Enhanced light map (lamp glints), or null. */
  light: RenderTargetRef | null;
  /** Gust-sheet drift in noise units (scrolls the bands). */
  sheet: number;
  /** 0..1 fill of the out-of-focus layer in front of the camera. */
  near: number;
  wet: WetInputs | null;
}

interface Program {
  program: WebGLProgram;
  uniforms: Record<string, WebGLUniformLocation | null>;
}

export class PostFx {
  readonly available: boolean;
  private readonly renderer: WebGl2Renderer;
  private readonly gl: WebGL2RenderingContext;
  private blur: Program | null = null;
  private composite: Program | null = null;
  private reflect: Program | null = null;
  private fogProgram: Program | null = null;
  private groundProgram: Program | null = null;
  private snowProgram: Program | null = null;
  private rainProgram: Program | null = null;
  private snapshotProgram: Program | null = null;
  private maskTexture: WebGLTexture | null = null;
  private maskSize = "";
  private vao: WebGLVertexArrayObject | null = null;
  private readonly rPos = new Float32Array(MAX_REFLECTIONS * 4);
  private readonly rCol = new Float32Array(MAX_REFLECTIONS * 4);
  private readonly rPick: ReflectionSource[] = [];
  private tileTexture: WebGLTexture | null = null;
  private tileCols = 0;
  private tileRows = 0;
  private tileBits = new Uint32Array(0);

  constructor(renderer: WebGl2Renderer) {
    this.renderer = renderer;
    this.gl = renderer.gl;
    let ok = false;
    try {
      this.blur = link(this.gl, BLUR_FS, ["uSrc", "uSize", "uStep"]);
      this.composite = link(this.gl, COMPOSITE_FS, ["uScene", "uGlow", "uHaze", "uLight", "uHasLight", "uSize", "uTime", "uGlowStrength",
        "uHazePixels", "uCentre", "uDepth", "uTint", "uReflect", "uFrost", "uWetMask", "uHasWet", "uLegacy", "uMirror", "uLamps", "uHasMirror",
        "uHasLamps", "uHalf"]);
      this.reflect = link(this.gl, REFLECT_FS, ["uScene", "uWetMask", "uSize", "uTime", "uTint", "uReflect", "uPart", "uRCount", "uRPos", "uRCol",
        "uRT0", "uRT1", "uRT2", "uRT3", "uCam", "uRipples", "uTiles", "uTile"]);
      this.fogProgram = link(this.gl, FOG_FS, ["uLight", "uHasLight", "uSize", "uCam", "uDrift", "uScale", "uDensity", "uFloor", "uColour"]);
      this.groundProgram = link(this.gl, GROUND_FS, ["uSize", "uCam", "uTime", "uMode", "uAmount", "uRipples", "uSplash", "uPass", ...MASK_UNIFORMS]);
      this.rainProgram = link(this.gl, RAINFALL_FS, ["uLight", "uHasLight", "uSize", "uCam", "uTime", "uAmount", "uSlant", "uWash", "uSheet", "uNear",
        ...MASK_UNIFORMS]);
      this.snapshotProgram = link(this.gl, SNAPSHOT_FS, ["uSrc", "uSize"]);
      this.snowProgram = link(this.gl, SNOWFALL_FS, ["uSize", "uCam", "uTime", "uDrift", "uAmount"]);
      this.vao = this.gl.createVertexArray();
      ok = this.vao !== null;
    } catch (error) {
      console.error("[postfx] programs failed to link - the atmosphere pass is off", error);
    } finally {
      renderer.endExternal(); // link() leaves its program current
    }
    this.available = ok;
  }

  /** Bilinear sampling on a target this module samples smoothly (glow, haze); the sprite path's targets stay NEAREST. */
  setLinear(rt: RenderTargetRef): void {
    const gl = this.gl;
    this.renderer.beginExternal();
    gl.bindTexture(gl.TEXTURE_2D, rt.texture.glTexture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    this.renderer.endExternal();
  }

  /** One Gaussian pass from `src` into `dst` (same size), along (dx, dy) at `spread` texels. Leaves `dst` bound. */
  blurPass(src: RenderTargetRef, dst: RenderTargetRef, dx: number, dy: number, spread: number): void {
    const gl = this.gl, p = this.blur!;
    this.renderer.bindRenderTarget(dst);
    this.renderer.beginExternal();
    gl.disable(gl.BLEND);
    gl.useProgram(p.program);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, src.texture.glTexture);
    gl.uniform1i(p.uniforms.uSrc!, 0);
    gl.uniform2f(p.uniforms.uSize!, dst.width, dst.height);
    gl.uniform2f(p.uniforms.uStep!, (dx * spread) / src.width, (dy * spread) / src.height);
    this.drawTriangle();
    this.renderer.endExternal();
  }

  /** One fog layer alpha-blended over the BOUND target (`width` x `height`); `light` = the Enhanced light map to thin it by, or null. The caller flushes
   *  its sprite batch first and rebinds sprite blending after. */
  fog(width: number, height: number, layer: FogLayer, camX: number, camY: number, driftX: number, driftY: number, light: RenderTargetRef | null): void {
    const gl = this.gl, p = this.fogProgram!;
    this.beginOver(p);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, light ? light.texture.glTexture : null);
    gl.uniform1i(p.uniforms.uLight!, 0);
    gl.uniform1f(p.uniforms.uHasLight!, light ? 1 : 0);
    gl.uniform2f(p.uniforms.uSize!, width, height);
    gl.uniform2f(p.uniforms.uCam!, camX, camY);
    gl.uniform2f(p.uniforms.uDrift!, driftX, driftY);
    gl.uniform1f(p.uniforms.uScale!, layer.scale);
    gl.uniform1f(p.uniforms.uDensity!, layer.density);
    gl.uniform1f(p.uniforms.uFloor!, layer.floor);
    gl.uniform3f(p.uniforms.uColour!, layer.colour[0], layer.colour[1], layer.colour[2]);
    this.drawTriangle();
    this.renderer.endExternal();
  }

  /** The cell mask texture (RGBA8, `cols` x `rows`, one texel per cell; see MASK_GLSL), bilinear. */
  setCellMask(data: Uint8Array, cols: number, rows: number): void {
    const gl = this.gl;
    this.renderer.beginExternal();
    const size = `${cols}x${rows}`;
    if (!this.maskTexture || this.maskSize !== size) {
      if (this.maskTexture) gl.deleteTexture(this.maskTexture);
      this.maskTexture = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, this.maskTexture);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, cols, rows, 0, gl.RGBA, gl.UNSIGNED_BYTE, data);
      this.maskSize = size;
    } else {
      gl.bindTexture(gl.TEXTURE_2D, this.maskTexture);
      gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, cols, rows, gl.RGBA, gl.UNSIGNED_BYTE, data);
    }
    this.renderer.endExternal();
  }

  /** Snow cover (`mode` "snow", amount = coverage) or wet ground (`mode` "wet", amount = darkening, ripples / splash = share of cells rippling /
   *  splashing, `wet` = soaking, drips and the cell mask) over the BOUND target. Wet ground then also writes the target's alpha (GROUND_FS uPass 1). */
  ground(width: number, height: number, mode: "snow" | "wet", amount: number, ripples: number, splash: number, camX: number, camY: number, time: number,
    wet: WetInputs | null = null): void {
    const gl = this.gl, p = this.groundProgram!;
    this.beginOver(p);
    gl.uniform2f(p.uniforms.uSize!, width, height);
    gl.uniform2f(p.uniforms.uCam!, camX, camY);
    gl.uniform1f(p.uniforms.uTime!, time);
    gl.uniform1f(p.uniforms.uMode!, mode === "snow" ? 0 : 1);
    gl.uniform1f(p.uniforms.uAmount!, amount);
    gl.uniform1f(p.uniforms.uRipples!, ripples);
    gl.uniform1f(p.uniforms.uSplash!, splash);
    gl.uniform1f(p.uniforms.uPass!, 0);
    this.bindMask(p, mode === "wet" ? wet : null);
    this.drawTriangle();
    if (mode === "wet" && wet) {
      gl.disable(gl.BLEND);
      gl.colorMask(false, false, false, true);
      gl.uniform1f(p.uniforms.uPass!, 1);
      this.drawTriangle();
      gl.colorMask(true, true, true, true);
    }
    this.unbindUnits();
    this.renderer.endExternal();
  }

  /** The bound target's alpha (`scene`, the wet-ground reflectivity after the objects) into `dst`'s R; rebinds `scene` after. */
  snapshotAlpha(scene: RenderTargetRef, dst: RenderTargetRef): void {
    const gl = this.gl, p = this.snapshotProgram!;
    this.renderer.bindRenderTarget(dst);
    this.renderer.beginExternal();
    gl.disable(gl.BLEND);
    gl.useProgram(p.program);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, scene.texture.glTexture);
    gl.uniform1i(p.uniforms.uSrc!, 0);
    gl.uniform2f(p.uniforms.uSize!, dst.width, dst.height);
    this.drawTriangle();
    this.renderer.endExternal();
    this.renderer.bindRenderTarget(scene);
  }

  /** The cell mask on unit 2 and its uniforms (`wet` null: no floor anywhere, no drips). Leaves unit 0 active. */
  private bindMask(p: Program, wet: WetInputs | null): void {
    const gl = this.gl;
    const on = wet !== null && this.maskTexture !== null;
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, on ? this.maskTexture : null);
    gl.uniform1i(p.uniforms.uMask!, 2);
    const m = wet?.mask;
    gl.uniform2f(p.uniforms.uMaskOrigin!, m?.originX ?? 0, m?.originY ?? 0);
    gl.uniform2f(p.uniforms.uMaskCell0!, m?.cellX ?? 0, m?.cellY ?? 0);
    gl.uniform2f(p.uniforms.uMaskCells!, on ? m!.cols : 0, on ? m!.rows : 0);
    gl.uniform1f(p.uniforms.uDrip!, wet?.drip ?? 0);
    gl.uniform1f(p.uniforms.uWet!, wet?.wetness ?? 0);
    gl.activeTexture(gl.TEXTURE0);
  }

  /** Clears units 2 and 3 (the mask, the light map) so no later pass samples a stale binding; leaves unit 0 active. */
  private unbindUnits(): void {
    const gl = this.gl;
    gl.activeTexture(gl.TEXTURE3);
    gl.bindTexture(gl.TEXTURE_2D, null);
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, null);
    gl.activeTexture(gl.TEXTURE0);
  }

  /** Falling snow over the BOUND target; `amount` = share of grid cells holding a flake. */
  snowfall(width: number, height: number, amount: number, camX: number, camY: number, driftX: number, time: number): void {
    const gl = this.gl, p = this.snowProgram!;
    this.beginOver(p);
    gl.uniform2f(p.uniforms.uSize!, width, height);
    gl.uniform2f(p.uniforms.uCam!, camX, camY);
    gl.uniform1f(p.uniforms.uTime!, time);
    gl.uniform2f(p.uniforms.uDrift!, driftX, 0);
    gl.uniform1f(p.uniforms.uAmount!, amount);
    this.drawTriangle();
    this.renderer.endExternal();
  }

  /** Falling rain over the BOUND target: `amount` = share of streak slots filled, `slant` = horizontal lean per pixel of fall, `wash` = grey air;
   *  `x` = lamp glints, gust sheets, the near layer and eaves drips. */
  rainfall(width: number, height: number, amount: number, slant: number, wash: number, camX: number, camY: number, time: number, x: RainExtras): void {
    const gl = this.gl, p = this.rainProgram!;
    this.beginOver(p);
    this.bindMask(p, x.wet);
    gl.activeTexture(gl.TEXTURE3);
    gl.bindTexture(gl.TEXTURE_2D, x.light ? x.light.texture.glTexture : null);
    gl.uniform1i(p.uniforms.uLight!, 3);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1f(p.uniforms.uHasLight!, x.light ? 1 : 0);
    gl.uniform1f(p.uniforms.uSheet!, x.sheet);
    gl.uniform1f(p.uniforms.uNear!, x.near);
    gl.uniform2f(p.uniforms.uSize!, width, height);
    gl.uniform2f(p.uniforms.uCam!, camX, camY);
    gl.uniform1f(p.uniforms.uTime!, time);
    gl.uniform1f(p.uniforms.uAmount!, amount);
    gl.uniform1f(p.uniforms.uSlant!, slant);
    gl.uniform1f(p.uniforms.uWash!, wash);
    this.drawTriangle();
    this.unbindUnits();
    this.renderer.endExternal();
  }

  /** The final pass onto the canvas (binds it); the half-resolution reflection passes first. */
  compositeToCanvas(scene: RenderTargetRef, glow: RenderTargetRef, haze: RenderTargetRef, time: number, glowStrength: number, hazePixels: number,
    o: CompositeOptions): void {
    const gl = this.gl, p = this.composite!, t = reflectionTuning;
    const refl = o.reflect > 0 && o.wetMask ? o.reflectTargets : null;
    const lamps = refl ? this.reflectionPasses(scene, o, refl, time) : 0;
    this.renderer.bindRenderTarget(null);
    this.renderer.beginExternal();
    gl.disable(gl.BLEND);
    gl.useProgram(p.program);
    gl.activeTexture(gl.TEXTURE6);
    gl.bindTexture(gl.TEXTURE_2D, refl ? refl.lamps.texture.glTexture : null);
    gl.activeTexture(gl.TEXTURE5);
    gl.bindTexture(gl.TEXTURE_2D, refl ? refl.mirror.texture.glTexture : null);
    gl.activeTexture(gl.TEXTURE4);
    gl.bindTexture(gl.TEXTURE_2D, o.wetMask ? o.wetMask.texture.glTexture : null);
    gl.activeTexture(gl.TEXTURE3);
    gl.bindTexture(gl.TEXTURE_2D, o.light ? o.light.texture.glTexture : null);
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, haze.texture.glTexture);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, glow.texture.glTexture);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, scene.texture.glTexture);
    gl.uniform1i(p.uniforms.uScene!, 0);
    gl.uniform1i(p.uniforms.uGlow!, 1);
    gl.uniform1i(p.uniforms.uHaze!, 2);
    gl.uniform1i(p.uniforms.uLight!, 3);
    gl.uniform1i(p.uniforms.uWetMask!, 4);
    gl.uniform1i(p.uniforms.uMirror!, 5);
    gl.uniform1i(p.uniforms.uLamps!, 6);
    gl.uniform1f(p.uniforms.uHasMirror!, refl ? 1 : 0);
    gl.uniform1f(p.uniforms.uHasLamps!, lamps > 0 ? 1 : 0);
    gl.uniform2f(p.uniforms.uHalf!, refl ? refl.mirror.width : 1, refl ? refl.mirror.height : 1);
    gl.uniform1f(p.uniforms.uLegacy!, t.enabled ? t.legacy : 1);
    gl.uniform1f(p.uniforms.uHasLight!, o.light ? 1 : 0);
    gl.uniform1f(p.uniforms.uHasWet!, o.wetMask ? 1 : 0);
    gl.uniform2f(p.uniforms.uSize!, scene.width, scene.height);
    gl.uniform1f(p.uniforms.uTime!, time);
    gl.uniform1f(p.uniforms.uGlowStrength!, glowStrength);
    gl.uniform1f(p.uniforms.uHazePixels!, hazePixels);
    gl.uniform2f(p.uniforms.uCentre!, o.centre.x, scene.height - o.centre.y);
    gl.uniform1f(p.uniforms.uDepth!, o.depth);
    gl.uniform3f(p.uniforms.uTint!, o.tint[0], o.tint[1], o.tint[2]);
    gl.uniform1f(p.uniforms.uReflect!, o.reflect);
    gl.uniform1f(p.uniforms.uFrost!, o.frost);
    this.drawTriangle();
    for (const unit of [gl.TEXTURE6, gl.TEXTURE5, gl.TEXTURE4, gl.TEXTURE3, gl.TEXTURE2]) {
      gl.activeTexture(unit);
      gl.bindTexture(gl.TEXTURE_2D, null);
    }
    this.renderer.endExternal(); // rebinds unit 1 (the sprite path's MaskTex) and unit 0 active
  }

  /** REFLECT_FS into the half-resolution targets: part 0 (the object mirror) always, part 1 (the lamps) when a lamp can reach the screen. Returns
   *  the lamp count (0 = the lamps target was not drawn this frame). */
  private reflectionPasses(scene: RenderTargetRef, o: CompositeOptions, targets: { mirror: RenderTargetRef; lamps: RenderTargetRef }, time: number): number {
    const gl = this.gl, p = this.reflect!;
    let n = 0;
    for (let part = 0; part < 2; part++) {
      this.renderer.bindRenderTarget(part === 0 ? targets.mirror : targets.lamps);
      this.renderer.beginExternal();
      gl.disable(gl.BLEND);
      gl.useProgram(p.program);
      if (part === 0) {
        gl.activeTexture(gl.TEXTURE4);
        gl.bindTexture(gl.TEXTURE_2D, o.wetMask!.texture.glTexture);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, scene.texture.glTexture);
        gl.uniform1i(p.uniforms.uScene!, 0);
        gl.uniform1i(p.uniforms.uWetMask!, 4);
        gl.uniform1i(p.uniforms.uTiles!, 5);
        gl.uniform1f(p.uniforms.uTile!, REFLECT_TILE);
        gl.uniform2f(p.uniforms.uSize!, scene.width, scene.height);
        gl.uniform1f(p.uniforms.uTime!, time);
        gl.uniform3f(p.uniforms.uTint!, o.tint[0], o.tint[1], o.tint[2]);
        gl.uniform1f(p.uniforms.uReflect!, o.reflect);
        n = this.setReflections(p, o, scene.width, scene.height); // fills the tile masks and leaves them bound on unit 5
      } else {
        // endExternal (part 0) rebound the sprite path's units 0 and 1 only: unit 0 needs the scene again (flame mirror)
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, scene.texture.glTexture);
      }
      if (part === 1 && n === 0) {
        this.renderer.endExternal();
        break;
      }
      gl.uniform1f(p.uniforms.uPart!, part);
      this.drawTriangle();
      this.renderer.endExternal();
    }
    gl.activeTexture(gl.TEXTURE5);
    gl.bindTexture(gl.TEXTURE_2D, null);
    gl.activeTexture(gl.TEXTURE4);
    gl.bindTexture(gl.TEXTURE_2D, null);
    gl.activeTexture(gl.TEXTURE0);
    return n;
  }

  /** The lamp reflection uniforms of REFLECT_FS `p` (current): the lamps whose reflection can reach the screen, the strongest first when more than
   *  MAX_REFLECTIONS do. Returns how many. */
  private setReflections(p: Program, o: CompositeOptions, width: number, height: number): number {
    const gl = this.gl, t = reflectionTuning;
    const pick = this.rPick;
    pick.length = 0;
    if (t.enabled && o.wetMask) {
      for (const l of o.lamps) {
        const my = 2 * l.contactY - l.emitterY, reach = 4 * (4 + t.streakLength) + 100;
        if (l.x < -l.radius || l.x > width + l.radius || my + reach < 0 || l.contactY > height + 4 || l.r + l.g + l.b <= 0.01) continue;
        pick.push(l);
      }
      if (pick.length > MAX_REFLECTIONS) pick.sort((a, b) => (b.r + b.g + b.b) * b.radius - (a.r + a.g + a.b) * a.radius);
    }
    const n = Math.min(pick.length, MAX_REFLECTIONS);
    for (let i = 0; i < n; i++) {
      const l = pick[i]!, j = i * 4;
      this.rPos[j] = l.x; this.rPos[j + 1] = 2 * l.contactY - l.emitterY; this.rPos[j + 2] = l.contactY; this.rPos[j + 3] = l.radius;
      this.rCol[j] = l.r; this.rCol[j + 1] = l.g; this.rCol[j + 2] = l.b; this.rCol[j + 3] = l.emitterY;
    }
    gl.uniform1i(p.uniforms.uRCount!, n);
    if (n > 0) {
      gl.uniform4fv(p.uniforms.uRPos!, this.rPos);
      gl.uniform4fv(p.uniforms.uRCol!, this.rCol);
    }
    this.lampTiles(n, width, height, o.ripples > 0);
    gl.uniform4f(p.uniforms.uRT0!, t.gain, t.streakLength, t.streakWidth, t.coreGain);
    gl.uniform4f(p.uniforms.uRT1!, t.flameGain, t.sheenGain, t.rippleDisp, t.wobble);
    gl.uniform4f(p.uniforms.uRT2!, t.puddleLow, t.puddleHigh, Math.max(0.5, t.contactFade), t.hotWhite);
    gl.uniform4f(p.uniforms.uRT3!, t.roughGain, t.haloGain, t.warmth, 0);
    gl.uniform2f(p.uniforms.uCam!, o.camX, o.camY);
    gl.uniform1f(p.uniforms.uRipples!, o.ripples);
    return n;
  }

  /** Bins the first `n` lamps of rPos / rCol into REFLECT_TILE screen tiles (uTiles on unit 5, left bound there): lamp i's bit is set on every tile
   *  a pixel of which can pass REFLECT_FS lampReflections' cull for it. The bounds are the shader's own (sheen / streak reach around the mirror
   *  point, at the roughest ground; the contact-line fade) grown by the most the ripple push and the wobble can move a pixel, so no lit pixel is
   *  dropped: the picture is unchanged, and pixels far from every lamp skip the lamp loop and the ripple grids altogether. */
  private lampTiles(n: number, width: number, height: number, rippling: boolean): void {
    const gl = this.gl, t = reflectionTuning;
    const cols = Math.max(1, Math.ceil(width / REFLECT_TILE)), rows = Math.max(1, Math.ceil(height / REFLECT_TILE));
    if (this.tileCols !== cols || this.tileRows !== rows || !this.tileTexture) {
      if (this.tileTexture) gl.deleteTexture(this.tileTexture);
      this.tileTexture = gl.createTexture();
      gl.activeTexture(gl.TEXTURE5);
      gl.bindTexture(gl.TEXTURE_2D, this.tileTexture);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); // integer textures must be NEAREST
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RG32UI, cols, rows);
      this.tileCols = cols;
      this.tileRows = rows;
      this.tileBits = new Uint32Array(cols * rows * 2);
    }
    const bits = this.tileBits;
    bits.fill(0);
    const push = (rippling ? RING_DISP_MAX * Math.abs(t.rippleDisp) : 0);
    const pushX = push + 0.8 * Math.abs(t.wobble), pushY = push + 0.25 * Math.abs(t.wobble);
    const fade = Math.max(0.5, t.contactFade);
    for (let i = 0; i < n; i++) {
      const j = i * 4;
      const x = this.rPos[j]!, my = this.rPos[j + 1]!, contactY = this.rPos[j + 2]!, radius = this.rPos[j + 3]!;
      const k = Math.min(Math.max(radius / 400, 0.35), 1.2);
      const lenD = Math.max(Math.abs(4 + t.streakLength * k), Math.abs(4 + t.streakLength * k * 0.3));
      const halfX = Math.max(210 * k, 6 + 3 * t.streakWidth);
      const x0 = x - halfX - pushX, x1 = x + halfX + pushX;
      const y0 = Math.max(my - Math.max(1.6 * lenD, 300 * k) - pushY, contactY - fade);
      const y1 = my + Math.max(4 * lenD, 300 * k) + pushY;
      const c0 = Math.max(0, Math.floor(x0 / REFLECT_TILE)), c1 = Math.min(cols - 1, Math.floor(x1 / REFLECT_TILE));
      const r0 = Math.max(0, Math.floor(y0 / REFLECT_TILE)), r1 = Math.min(rows - 1, Math.floor(y1 / REFLECT_TILE));
      const word = i < 32 ? 0 : 1, bit = (1 << (i & 31)) >>> 0;
      for (let r = r0; r <= r1; r++) {
        for (let c = c0; c <= c1; c++) { const at = (r * cols + c) * 2 + word; bits[at] = bits[at]! | bit; }
      }
    }
    gl.activeTexture(gl.TEXTURE5);
    gl.bindTexture(gl.TEXTURE_2D, this.tileTexture);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, cols, rows, gl.RG_INTEGER, gl.UNSIGNED_INT, bits);
    gl.activeTexture(gl.TEXTURE0);
  }

  /** A pass alpha-blended over the bound target (colour only; the target's alpha is kept). */
  private beginOver(p: Program): void {
    const gl = this.gl;
    this.renderer.beginExternal();
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ZERO, gl.ONE);
    gl.useProgram(p.program);
  }

  private drawTriangle(): void {
    const gl = this.gl;
    gl.bindVertexArray(this.vao);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);
  }
}

const MASK_UNIFORMS = ["uMask", "uMaskOrigin", "uMaskCell0", "uMaskCells", "uDrip", "uWet"];

function link(gl: WebGL2RenderingContext, fsSource: string, uniformNames: string[]): Program {
  const compile = (type: number, source: string) => {
    const shader = gl.createShader(type)!;
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(`postfx compile failed: ${gl.getShaderInfoLog(shader)}`);
    return shader;
  };
  const vs = compile(gl.VERTEX_SHADER, FULLSCREEN_VS);
  const fs = compile(gl.FRAGMENT_SHADER, fsSource);
  const program = gl.createProgram()!;
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  gl.deleteShader(vs);
  gl.deleteShader(fs);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(`postfx link failed: ${gl.getProgramInfoLog(program)}`);
  const uniforms: Record<string, WebGLUniformLocation | null> = {};
  for (const name of uniformNames) uniforms[name] = gl.getUniformLocation(program, name);
  return { program, uniforms };
}
