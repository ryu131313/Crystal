// Lifted from WoOOLcraft src/WebClient/src/render/color.ts (2026-10-12) for the Crystal web client; the D3D11Backend it mirrors descends from Crystal's own.
// Sprite colour math: port of the shared shader helpers in SpriteBatch2D.cs (RGBtoHSV / HSVtoRGB / ApplyRecolour) plus PackColor. The GLSL shaders
// carry the same formulas; this module exists so the math can be golden-tested headlessly. Bit-faithful to the shader:
//  - HueShift in turns (degrees/360); 0 short-circuits;
//  - Recolour.w == 0 short-circuits;
//  - the recolour ramp scales with luminance so black stays black (additive effects treat black as transparent) and the cubed term pushes only
//    the hottest texels to white.

const saturate = (x: number): number => Math.min(1, Math.max(0, x));
const fract = (x: number): number => x - Math.floor(x);

/** RGB->HSV, the shader's K-constant formulation unrolled:
 *  p = step(c.b, c.g) ? (g, b, K.x, K.y) : (b, g, K.w, K.z)   (step(c.b, c.g) = g >= b)
 *  q = step(p.x, c.r) ? (r, p.y, p.z, p.x) : (p.x, p.y, p.w, r)
 *  h = |q.z + (q.w - q.y) / (6d + e)|, s = d / (q.x + e), v = q.x. */
export function rgbToHsv(r: number, g: number, b: number): [number, number, number] {
  const gGteB = g >= b;
  const px = gGteB ? g : b;
  const py = gGteB ? b : g;
  const pz = gGteB ? 0 : -1;
  const pw = gGteB ? -1 / 3 : 2 / 3;
  const rGtePx = r >= px;
  const qx = rGtePx ? r : px;
  const qy = py;
  const qz = rGtePx ? pz : pw;
  const qw = rGtePx ? px : r;
  const d = qx - Math.min(qw, qy);
  const e = 1.0e-10;
  return [Math.abs(qz + (qw - qy) / (6 * d + e)), d / (qx + e), qx];
}

/** HSV->RGB, the shader's K-constant formulation:
 *  p = abs(frac(h + K.xyz) * 6 - K.www); rgb = v * lerp(1, saturate(p - 1), s). */
export function hsvToRgb(h: number, s: number, v: number): [number, number, number] {
  const p0 = Math.abs(fract(h + 1) * 6 - 3);
  const p1 = Math.abs(fract(h + 2 / 3) * 6 - 3);
  const p2 = Math.abs(fract(h + 1 / 3) * 6 - 3);
  return [
    v * (1 + s * (saturate(p0 - 1) - 1)),
    v * (1 + s * (saturate(p1 - 1) - 1)),
    v * (1 + s * (saturate(p2 - 1) - 1)),
  ];
}

/** Hue rotation + greyscale-then-tint recolour (ApplyRecolour). The shader applies it in EVERY sprite pixel shader; the C# ordering rules (BlendKey keys
 *  on the PRE-recolour colour) are structural in the shader source and duplicated here only so tests can pin the math. */
export function applyRecolour(
  r: number, g: number, b: number,
  hueShiftTurns: number,
  recolourR: number, recolourG: number, recolourB: number, recolourW: number,
): [number, number, number] {
  let out: [number, number, number] = [r, g, b];
  if (hueShiftTurns !== 0) {
    const [h, s, v] = rgbToHsv(out[0], out[1], out[2]);
    out = hsvToRgb(fract(h + hueShiftTurns), s, v);
  }
  if (recolourW !== 0) {
    const lum = 0.299 * out[0] + 0.587 * out[1] + 0.114 * out[2];
    const hot = lum * lum * lum;
    const target = [recolourR, recolourG, recolourB];
    // lerp(rgb, Recolour.rgb * lum + hot * (1 - Recolour.rgb), Recolour.w)
    out = [
      out[0] + (target[0] * lum + hot * (1 - target[0]) - out[0]) * recolourW,
      out[1] + (target[1] * lum + hot * (1 - target[1]) - out[1]) * recolourW,
      out[2] + (target[2] * lum + hot * (1 - target[2]) - out[2]) * recolourW,
    ];
  }
  return out;
}

/** PackColor: RGBA8 little-endian (r | g<<8 | b<<16 | a<<24), clamped with +0.5 rounding; identical to the D3D batch's R8G8B8A8_UNorm vertex colour. */
export function packColor(r: number, g: number, b: number, a: number): number {
  const c = (x: number): number => Math.trunc(saturate(x) * 255 + 0.5);
  return (c(r) | (c(g) << 8) | (c(b) << 16) | (c(a) << 24)) >>> 0;
}

/** ScreenProjection: y-down ortho, no half-pixel offset, as a column-major GLSL mat4 array: `Projection * vec4(x, y, 0, 1)` = (2x/w - 1, 1 - 2y/h, z, 1), matching the D3D row-vector product. */
export function screenProjection(width: number, height: number): Float32Array {
  return new Float32Array([
    2 / width, 0, 0, 0, // column 0
    0, -2 / height, 0, 0, // column 1
    0, 0, 1, 0, // column 2
    -1, 1, 0, 1, // column 3
  ]);
}

/** Identity transform in the same column-major upload order. */
export function identityTransform(): Float32Array {
  return new Float32Array([
    1, 0, 0, 0,
    0, 1, 0, 0,
    0, 0, 1, 0,
    0, 0, 0, 1,
  ]);
}
