// Colour grade post pass (web only): copies the finished canvas into a texture and draws it back through GRADE_FS. Built in the
// scene lab (play/lab.ts), used in game by Options > More "Cozy mode" (COZY_GRADE). Runs after MapView.render, so the 2D
// overlay (names, chat, damage numbers) and the DOM UI stay ungraded.

import type { WebGl2Renderer, GLTextureRef } from "./backend.ts";

type Rgb01 = [number, number, number];

/** GRADE_FS inputs, in the order the shader applies them. Colours are 0..1; tints are neutral at 0.5. */
export interface GradeParams {
  exposure: number; brightness: number; contrast: number; gamma: number; black: number; white: number;
  saturation: number; vibrance: number; hue: number; temperature: number; tint: number;
  gain: Rgb01; shadowTint: Rgb01; shadowAmount: number; highTint: Rgb01; highAmount: number;
  vignette: number; vignetteRadius: number; sharpen: number;
}

/** The identity grade (draws the frame unchanged). */
export const NEUTRAL_GRADE: Readonly<GradeParams> = {
  exposure: 0, brightness: 0, contrast: 1, gamma: 1, black: 0, white: 1, saturation: 1, vibrance: 0, hue: 0, temperature: 0, tint: 0,
  gain: [1, 1, 1], shadowTint: [0.5, 0.5, 0.5], shadowAmount: 0, highTint: [0.5, 0.5, 0.5], highAmount: 0, vignette: 0, vignetteRadius: 0.55, sharpen: 0,
};

/** Options > More "Cozy mode" (tuned in the scene lab, 2026-10-13). */
export const COZY_GRADE: Readonly<GradeParams> = { ...NEUTRAL_GRADE, contrast: 1.02, vibrance: 0.89 };

const GRADE_VS = `#version 300 es
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

const GRADE_FS = `#version 300 es
precision highp float;
uniform sampler2D uScene;
uniform vec2 uSize;
uniform float uSplit;
uniform float uExposure, uBrightness, uContrast, uGamma, uBlack, uWhite, uSaturation, uVibrance, uHue, uTemp, uTint;
uniform vec3 uGain, uShadowTint, uHighTint;
uniform float uShadowAmt, uHighAmt, uVignette, uVigRadius, uSharpen;
out vec4 outColor;
const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
vec3 hueRotate(vec3 c, float a) {
  const vec3 k = vec3(0.57735027);
  float ca = cos(a);
  return c * ca + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - ca);
}
vec3 tap(ivec2 p) { return texelFetch(uScene, clamp(p, ivec2(0), ivec2(uSize) - 1), 0).rgb; }
void main() {
  ivec2 px = ivec2(gl_FragCoord.xy);
  vec3 c = tap(px);
  if (gl_FragCoord.x < uSplit) { outColor = vec4(c, 1.0); return; }
  if (uSharpen > 0.0) {
    vec3 blur = (tap(px + ivec2(1, 0)) + tap(px - ivec2(1, 0)) + tap(px + ivec2(0, 1)) + tap(px - ivec2(0, 1))) * 0.25;
    c = max(c + (c - blur) * uSharpen, 0.0);
  }
  c *= exp2(uExposure);
  c *= vec3(1.0 + 0.25 * uTemp, 1.0 - 0.2 * uTint, 1.0 - 0.25 * uTemp) * uGain;
  c = (c - uBlack) / max(uWhite - uBlack, 0.001);
  c = (c - 0.5) * uContrast + 0.5 + uBrightness;
  c = pow(max(c, 0.0), vec3(1.0 / uGamma));
  float l = dot(c, LUMA);
  float chroma = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
  c = mix(vec3(l), c, uSaturation * (1.0 + uVibrance * (1.0 - clamp(chroma, 0.0, 1.0))));
  if (uHue != 0.0) c = hueRotate(c, uHue);
  l = clamp(dot(c, LUMA), 0.0, 1.0);
  c += (uShadowTint - 0.5) * uShadowAmt * (1.0 - l) * (1.0 - l);
  c += (uHighTint - 0.5) * uHighAmt * l * l;
  if (uVignette > 0.0) {
    vec2 d = (gl_FragCoord.xy / uSize - 0.5) * vec2(uSize.x / uSize.y, 1.0);
    c *= 1.0 - uVignette * smoothstep(uVigRadius, uVigRadius + 0.6, length(d));
  }
  outColor = vec4(clamp(c, 0.0, 1.0), 1.0);
}`;


export class Grade {
  private readonly gl: WebGL2RenderingContext;
  private readonly program: WebGLProgram;
  private readonly vao: WebGLVertexArrayObject;
  private readonly u = new Map<string, WebGLUniformLocation | null>();
  private copy: GLTextureRef | null = null;

  constructor(private readonly renderer: WebGl2Renderer) {
    const gl = (this.gl = renderer.gl);
    const sh = (type: number, src: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(`grade shader: ${gl.getShaderInfoLog(s)}`);
      return s;
    };
    const p = (this.program = gl.createProgram()!);
    gl.attachShader(p, sh(gl.VERTEX_SHADER, GRADE_VS));
    gl.attachShader(p, sh(gl.FRAGMENT_SHADER, GRADE_FS));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(`grade link: ${gl.getProgramInfoLog(p)}`);
    this.vao = gl.createVertexArray()!;
  }

  private loc(name: string): WebGLUniformLocation | null {
    if (!this.u.has(name)) this.u.set(name, this.gl.getUniformLocation(this.program, name));
    return this.u.get(name)!;
  }

  /** After MapView.render: the canvas is bound and holds the finished frame. Pixels left of `split` stay ungraded (the lab's compare). */
  apply(width: number, height: number, g: Readonly<GradeParams>, split = 0): void {
    const r = this.renderer, gl = this.gl;
    if (!this.copy || this.copy.width !== width || this.copy.height !== height) {
      r.releaseTexture(this.copy);
      this.copy = r.createCanvasStripTexture(width, height); // RGB8: the canvas is alpha: false
    }
    r.copyCanvasRegion(this.copy, 0, 0, width, height); // submits the sprite draws first
    r.beginExternal();
    const blend = gl.isEnabled(gl.BLEND);
    gl.disable(gl.BLEND);
    gl.useProgram(this.program);
    gl.bindVertexArray(this.vao);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.copy.glTexture);
    const f = (n: string, v: number) => gl.uniform1f(this.loc(n), v);
    const c = (n: string, v: Rgb01) => gl.uniform3f(this.loc(n), v[0], v[1], v[2]);
    gl.uniform1i(this.loc("uScene"), 0);
    gl.uniform2f(this.loc("uSize"), width, height);
    f("uSplit", split);
    f("uExposure", g.exposure); f("uBrightness", g.brightness); f("uContrast", g.contrast); f("uGamma", g.gamma);
    f("uBlack", g.black); f("uWhite", g.white); f("uSaturation", g.saturation); f("uVibrance", g.vibrance);
    f("uHue", (g.hue * Math.PI) / 180); f("uTemp", g.temperature); f("uTint", g.tint);
    c("uGain", g.gain); c("uShadowTint", g.shadowTint); c("uHighTint", g.highTint);
    f("uShadowAmt", g.shadowAmount); f("uHighAmt", g.highAmount);
    f("uVignette", g.vignette); f("uVigRadius", g.vignetteRadius); f("uSharpen", g.sharpen);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    if (blend) gl.enable(gl.BLEND);
    r.endExternal();
  }
}
