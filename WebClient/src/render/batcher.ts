// Lifted from WoOOLcraft src/WebClient/src/render/batcher.ts (2026-10-12) for the Crystal web client; the D3D11Backend it mirrors descends from Crystal's own.
// SpriteBatch2D -> WebGL2: strict port of `SpriteBatch2D.cs` (the ID3DXSprite replacement): a FIFO quad batcher that only coalesces ADJACENT draws
// with identical state and never reorders (painter's algorithm). Flush barriers: texture, transform, ColorOp, HueShift, Recolour, AlphaTest,
// AlphaRef, shader mode, ring capacity (the D3D set, 03-Gotchas §2.b).
//
// GL-free: it drives a `BatchContext` adapter (`WebGL2BatchContext` in backend.ts is the real one) so the state machine is headless-testable.
// Mirrors the C# field-for-field: MaxSprites 8192; VertexStride 20 (R32G32 pos, R32G32 uv @8, R8G8B8A8 color @16); index pattern per sprite
// 0,1,2,2,1,3; PackColor RGBA8 LE with +0.5 rounding; identity-transform draws round x/y to the integer grid; raw UVs, no half-texel inset;
// PerBatch/Effect uniform blocks at the HLSL cbuffers' std140 offsets (HueShift@88, AlphaRef@92, Recolour@96).

import { packColor } from "./color.ts";
import { EFFECT_VEC4_FIELDS, effectVec4Index } from "./shaders.ts";

export const MAX_SPRITES = 8192;
export const VERTEX_STRIDE = 20; // bytes: 4×f32 + 1×u32

export interface SpriteTextureRef {
  /** Stable identity the adapter binds (D3D's SRV swap barrier). */
  readonly key: object;
  readonly width: number;
  readonly height: number;
  /** Padded allocation size (BC textures pad to x4; RGBA = width/height). For an ATLAS PAGE sprite it is the PAGE's size and the sprite starts at
   *  (`originX`, `originY`); source rects are relative to the sprite and UVs add the origin. A page's sprites share its `key`, so consecutive ones are one run.
   *  OrbLiquid / Mask compute image-space coordinates from the allocation and must never be given an atlas sprite. */
  readonly allocWidth: number;
  readonly allocHeight: number;
  readonly originX?: number;
  readonly originY?: number;
}

export interface PerBatchData {
  transform: Float32Array; // 16 floats, C# memory order (see color.ts)
  colorOp: number;
  alphaTest: boolean;
  hueShift: number;
  alphaRef: number;
  recolour: Float32Array; // 4 floats
}

export type SpriteShaderMode =
  | "Default" | "OrbLiquid" | "BlendKey" | "Mask" | "SafeZoneOutline"
  // default safezone border: OPTIONAL like the desktop's (`IsSafeZoneSparkleShaderAvailable`); a failed link falls back to SafeZoneOutline
  | "SafeZoneSparkle"
  // The three overlay programs are NOT optional (no "degrade to Default"): they draw a plain white quad whose colour is procedural, so a fallback would
  // paint a solid white rectangle (SpriteBatch2D.cs:1343-1348). A failed link is recorded unavailable and the ADD is refused.
  | "ClawSlash" | "RipClaw" | "DruidFx"
  // Crystal D3D11Backend Grayscale_PS / Light_PS / LightComposite_PS (DXManager.SetGrayscale, the Enhanced lighting). OPTIONAL: a failed link means "no
  // grayscale" (D3D11Backend.Effective falls back to None) or "no Enhanced lighting" (the Classic lights draw instead).
  | "Grayscale" | "Light" | "LightComposite"
  // Web-only smooth shadows (shaders.ts SHADOW_FILL_PS). OPTIONAL: a failed link keeps the dithered shadows (Default).
  | "ShadowFill";

/** The six `Effect` vec4s of the three overlay programs, keyed by declared name so `packEffect` resolves each offset through the ONE table in shaders.ts. Absent keys stay zero. */
export type EffectOverlayUniforms = Partial<Record<
  "ClawParams" | "ClawTint" | "ClawShape"
  | "DruidFxParams" | "DruidFxTint" | "DruidFxShape"
  // `SetSafeZoneSparkleParams` / `SetSafeZoneOutlineParams` (SpriteBatch2D.cs:1388-1429): same flush-then-upload contract
  | "SafeZoneParams" | "SafeZoneAnchor" | "SafeZoneColorA" | "SafeZoneColorB" | "SafeZoneSparkleParams",
  Float32Array
>>;

/** The adapter the batch drives (WebGL2 in backend.ts; a recording mock in tools/render-selftest.ts). */
export interface BatchContext {
  updatePerBatch(data: PerBatchData): void;
  updateEffect(imageNorm: Float32Array, overlay?: EffectOverlayUniforms): void;
  bindSpriteTexture(texture: SpriteTextureRef): void;
  /** Upload this flush's vertex bytes at the ring offset. */
  uploadVertices(bytes: Uint8Array, gpuByteOffset: number): void;
  /** Draw `indexCount` indices of the STATIC quad index buffer (`bakeIndices(0, MAX_SPRITES)`, uploaded once) from `firstIndex`. Sprite N's six indices
   *  already address vertices 4N..4N+3, so the ring's vertex base rides the offset (GL has no baseVertex). */
  drawIndexed(indexCount: number, firstIndex: number): void;
  /** Issue everything the context DEFERRED. A context may hold back the GPU work of `uploadVertices` / `bindSpriteTexture` / `drawIndexed` (WebGL2 does: texture
   *  switches become ONE vertex upload plus a draw list) and must issue it before any other call of its own, so GL order is the order asked for. Called at every
   *  PUBLIC `Flush()` (so at `End()` and every state change). */
  submit?(): void;
}

/** The D3D blend recipes (`D3D11Backend.CreateStateObjects`) as symbolic factors; backend.ts maps the names onto gl constants. */
export type BlendFactorName =
  | "Zero" | "One" | "SrcColor" | "InvSrcColor" | "SrcAlpha" | "InvSrcAlpha"
  | "DstAlpha" | "InvDstAlpha" | "DstColor" | "InvDstColor"
  | "BlendFactor" | "InvBlendFactor";

export const BLEND_STATES: Record<string, [BlendFactorName, BlendFactorName, BlendFactorName, BlendFactorName]> = {
  Normal: ["SrcAlpha", "InvSrcAlpha", "SrcAlpha", "InvSrcAlpha"],
  OpacityFactor: ["BlendFactor", "InvBlendFactor", "BlendFactor", "InvBlendFactor"],
  Light: ["BlendFactor", "One", "BlendFactor", "One"],
  InvLight: ["BlendFactor", "InvSrcColor", "BlendFactor", "InvSrcAlpha"],
  Colorfy: ["SrcAlpha", "One", "SrcAlpha", "One"],
  Mask: ["Zero", "SrcAlpha", "Zero", "SrcAlpha"],
  EffectMask: ["DstAlpha", "One", "DstAlpha", "One"],
  Modulated: ["SrcColor", "One", "SrcAlpha", "One"],
  InvDestColor: ["InvDstColor", "One", "InvDstAlpha", "One"],
  // SetLightModulateState's `_blendZeroSourceColor` (D3D11Backend.cs:478, :1246-1250): dst.rgb *= src.rgb, dst.a *= src.a. Reached ONLY through
  // WebGl2Renderer.setLightModulateState, never a mode name (`blendStateForMode` is unchanged; render-selftest §14 pins that).
  ZeroSourceColor: ["Zero", "SrcColor", "Zero", "SrcAlpha"],
  // Back-effect composite `_blendInvDestAlphaOverwrite` (D3D11Backend.cs:675), bound by SetInverseDestAlphaOverwriteBlend (:1434-1437): the incoming texel keeps
  // (1 − dst.a) of itself and REPLACES the destination. Never a mode name; reached only through WebGl2Renderer.setInverseDestAlphaOverwriteBlend.
  InvDestAlphaOverwrite: ["InvDstAlpha", "Zero", "InvDstAlpha", "Zero"],
  // Crystal DXManager.SetEnhancedLightAdditiveBlend (One, One): Enhanced lights add into the float light map. Reached only through
  // WebGl2Renderer.setEnhancedLightAdditiveBlend.
  AddOne: ["One", "One", "One", "One"],
  // Crystal DXManager.SetEnhancedLightCompositeBlend (DestinationColor, SourceColor): scene * 2 * (light / 2). Reached only through
  // WebGl2Renderer.setEnhancedLightCompositeBlend.
  DestColorSrcColor: ["DstColor", "SrcColor", "DstAlpha", "SrcAlpha"],
};

/** `D3D11Backend.SetBlend`'s mode -> state switch; the default (NONE/NORMAL/LIGHTINV/...) lands on InvDestColor as the C# default arm does. */
export function blendStateForMode(mode: string): keyof typeof BLEND_STATES {
  switch (mode) {
    case "LIGHT": return "Light";
    case "INVLIGHT": return "InvLight";
    case "COLORFY":
    case "HIGHLIGHT": return "Colorfy";
    case "MASK": return "Mask";
    case "EFFECTMASK": return "EffectMask";
    case "MODULATED": return "Modulated";
    default: return "InvDestColor";
  }
}

/** DX9 factor packing: every channel = (byte)(255 * rate) / 255 — truncated. */
export function blendFactorFromRate(rate: number): number {
  return Math.trunc(255 * rate) / 255;
}

export class SpriteBatch {
  private readonly context: BatchContext;
  private readonly vertexBuffer: ArrayBuffer;
  private readonly vertexF32: Float32Array;
  private readonly vertexU32: Uint32Array;
  private spriteCount = 0;
  private ringSpriteOffset = 0; // sprites already uploaded this ring cycle
  private currentTexture: SpriteTextureRef | null = null;
  private transform: Float32Array = newIdentity();
  private transformIsIdentity = true;
  private colorOp = 0;
  private hueShift = 0;
  private recolour = new Float32Array([0, 0, 0, 0]);
  private alphaTest = false;
  private alphaRef = 0;
  private perBatchDirty = true;
  private inBatch = false;
  private shaderMode: SpriteShaderMode = "Default";
  private imageNorm = new Float32Array([1, 1, 1, 1]);
  private effectDirty = true;
  private overlay: EffectOverlayUniforms = {};
  private hasOverlay = false;

  constructor(context: BatchContext) {
    this.context = context;
    this.vertexBuffer = new ArrayBuffer(MAX_SPRITES * 4 * VERTEX_STRIDE);
    this.vertexF32 = new Float32Array(this.vertexBuffer);
    this.vertexU32 = new Uint32Array(this.vertexBuffer);
  }

  /** Back to a NEW batch's state WITHOUT reallocating the 640 KB vertex buffer (the page used to `new SpriteBatch` per frame, ~38 MB/s of garbage). Every field the
   *  setters and `Begin` touch is restored (uniforms marked dirty so the first flush re-uploads defaults). Pending quads are DISCARDED: `End()` first if they matter. */
  Reset(): void {
    this.spriteCount = 0;
    this.ringSpriteOffset = 0;
    this.currentTexture = null;
    this.transform = newIdentity();
    this.transformIsIdentity = true;
    this.colorOp = 0;
    this.hueShift = 0;
    this.recolour = new Float32Array([0, 0, 0, 0]);
    this.alphaTest = false;
    this.alphaRef = 0;
    this.perBatchDirty = true;
    this.inBatch = false;
    this.shaderMode = "Default";
    this.imageNorm = new Float32Array([1, 1, 1, 1]);
    this.effectDirty = true;
    this.overlay = {};
    this.hasOverlay = false;
  }

  get ShaderMode(): SpriteShaderMode {
    return this.shaderMode;
  }

  set ShaderMode(value: SpriteShaderMode) {
    if (this.shaderMode === value) return;
    this.Flush();
    this.shaderMode = value;
  }

  set Transform(value: Float32Array) {
    if (sameTransform(this.transform, value)) return;
    this.Flush();
    this.transform = value.slice();
    this.transformIsIdentity = isIdentity(this.transform);
    this.perBatchDirty = true;
  }

  set ColorOp(value: number) {
    if (this.colorOp === value) return;
    this.Flush();
    this.colorOp = value;
    this.perBatchDirty = true;
  }

  /** Hue rotation in turns (degrees/360); 0 = no-op. Set around a draw block. */
  set HueShift(value: number) {
    if (this.hueShift === value) return;
    this.Flush();
    this.hueShift = value;
    this.perBatchDirty = true;
  }

  /** Greyscale-then-tint recolour [r,g,b,strength]; w == 0 is a no-op. */
  set Recolour(value: Float32Array) {
    if (sameVec4(this.recolour, value)) return;
    this.Flush();
    this.recolour = value.slice();
    this.perBatchDirty = true;
  }

  /** Readable so a pass can save/restore it (`D3D11Backend.SetOpacity` toggles it per draw, `:592-605`, so "what the previous pass left" is real state). */
  get AlphaTest(): boolean {
    return this.alphaTest;
  }

  set AlphaTest(value: boolean) {
    if (this.alphaTest === value) return;
    this.Flush();
    this.alphaTest = value;
    this.perBatchDirty = true;
  }

  set AlphaRef(value: number) {
    if (this.alphaRef === value) return;
    this.Flush();
    this.alphaRef = value;
    this.perBatchDirty = true;
  }

  Begin(): void {
    this.shaderMode = "Default"; // frame start runs the plain sprite shader
    this.inBatch = true;
    this.perBatchDirty = true;
    this.effectDirty = true;
  }

  /** `BeginClawSlashShader` / `BeginRipClawShader` / `BeginDruidFxShader` (D3D11Backend.cs:1076-1092) forward to `SetClawSlashParams` / `SetRipClawParams` /
   * `SetDruidFxParams` (`:1535-1593`), whose FIRST statement is `this.Flush()`: the batch may hold quads drawn under the previous effect state and the uniform
   * block is uploaded per flush. That barrier is contract, not optimisation. Null clears the six vec4s to zero (the C# never resets its `_effect` struct; the
   * port clears explicitly rather than inherit a stale pass's values). */
  setOverlayUniforms(values: EffectOverlayUniforms | null): void {
    if (values === null) {
      if (!this.hasOverlay) return;
      this.Flush();
      this.overlay = {};
      this.hasOverlay = false;
      this.effectDirty = true;
      return;
    }
    this.Flush();
    this.overlay = values;
    this.hasOverlay = true;
    this.effectDirty = true;
  }

  End(): void {
    this.Flush();
    this.inBatch = false;
  }

  Draw(texture: SpriteTextureRef, source: { x: number; y: number; width: number; height: number } | null, x: number, y: number, colour: { r: number; g: number; b: number; a: number }): void {
    if (!this.inBatch) return;
    let srcX = 0, srcY = 0;
    let width = texture.width, height = texture.height;
    if (source) {
      srcX = source.x; srcY = source.y;
      width = source.width; height = source.height;
    }
    if (width <= 0 || height <= 0) return;

    if (texture.key !== (this.currentTexture?.key ?? null)) {
      this.flushRun();
      this.currentTexture = texture;
    }
    if (this.spriteCount === MAX_SPRITES) this.flushRun();

    // orb/mask shaders compute in image space: track the bound texture's padded-alloc normalization (identity for RGBA)
    if (this.shaderMode === "OrbLiquid" || this.shaderMode === "Mask") {
      const norm = new Float32Array([
        texture.allocWidth / texture.width,
        texture.allocHeight / texture.height,
        1 - 0.5 / texture.width,
        1 - 0.5 / texture.height,
      ]);
      if (!sameVec4(this.imageNorm, norm)) {
        this.Flush();
        this.imageNorm = norm;
        this.effectDirty = true;
      }
    }

    // round untransformed positions to the pixel grid; transforms legitimately give fractional coverage (03-Gotchas §1.3 rule 3)
    if (this.transformIsIdentity) {
      x = Math.round(x);
      y = Math.round(y);
    }

    // raw UVs, no half-texel inset under point sampling (03-Gotchas §2.a); an atlas sprite adds its origin on the page
    const ox = texture.originX ?? 0, oy = texture.originY ?? 0;
    const u0 = (ox + srcX) / texture.allocWidth;
    const v0 = (oy + srcY) / texture.allocHeight;
    const u1 = (ox + srcX + width) / texture.allocWidth;
    const v1 = (oy + srcY + height) / texture.allocHeight;
    const packed = packColor(colour.r, colour.g, colour.b, colour.a);

    const v = this.spriteCount * 4;
    const f = this.vertexF32, u = this.vertexU32;
    const w0 = v * 5; // 5 words per 20-byte vertex
    f[w0] = x; f[w0 + 1] = y; f[w0 + 2] = u0; f[w0 + 3] = v0; u[w0 + 4] = packed;
    f[w0 + 5] = x + width; f[w0 + 6] = y; f[w0 + 7] = u1; f[w0 + 8] = v0; u[w0 + 9] = packed;
    f[w0 + 10] = x; f[w0 + 11] = y + height; f[w0 + 12] = u0; f[w0 + 13] = v1; u[w0 + 14] = packed;
    f[w0 + 15] = x + width; f[w0 + 16] = y + height; f[w0 + 17] = u1; f[w0 + 18] = v1; u[w0 + 19] = packed;
    this.spriteCount++;
  }

  /** Draw with an explicit destination size: scale baked into the vertices, so it batches normally (no transform flush). */
  DrawScaled(texture: SpriteTextureRef, source: { x: number; y: number; width: number; height: number }, x: number, y: number, destWidth: number, destHeight: number, colour: { r: number; g: number; b: number; a: number }): void {
    this.DrawScaledVertexColours(texture, source, x, y, destWidth, destHeight, colour, colour, colour, colour);
  }

  /** DrawScaled with an independent colour per corner (TL, TR, BL, BR), interpolated across the quad. The vertex layout always carried a per-vertex R8G8B8A8
   *  at offset 16 and the shader always multiplied by it (Draw/DrawScaled pack ONE colour into all four slots), so this needs no shader or format change and
   *  the six-program contract stays intact. DrawScaled routes through here so ring, flush and MAX_SPRITES handling exist once. Used for alpha ramps (back-seam blend). */
  DrawScaledVertexColours(
    texture: SpriteTextureRef,
    source: { x: number; y: number; width: number; height: number },
    x: number, y: number, destWidth: number, destHeight: number,
    topLeft: { r: number; g: number; b: number; a: number },
    topRight: { r: number; g: number; b: number; a: number },
    bottomLeft: { r: number; g: number; b: number; a: number },
    bottomRight: { r: number; g: number; b: number; a: number },
  ): void {
    if (!this.inBatch) return;
    if (texture.key !== (this.currentTexture?.key ?? null)) {
      this.flushRun();
      this.currentTexture = texture;
    }
    if (this.spriteCount === MAX_SPRITES) this.flushRun();
    const ox = texture.originX ?? 0, oy = texture.originY ?? 0; // an atlas sprite's origin on its page
    const u0 = (ox + source.x) / texture.allocWidth;
    const v0 = (oy + source.y) / texture.allocHeight;
    const u1 = (ox + source.x + source.width) / texture.allocWidth;
    const v1 = (oy + source.y + source.height) / texture.allocHeight;
    const pTL = packColor(topLeft.r, topLeft.g, topLeft.b, topLeft.a);
    const pTR = packColor(topRight.r, topRight.g, topRight.b, topRight.a);
    const pBL = packColor(bottomLeft.r, bottomLeft.g, bottomLeft.b, bottomLeft.a);
    const pBR = packColor(bottomRight.r, bottomRight.g, bottomRight.b, bottomRight.a);
    const v = this.spriteCount * 4;
    const f = this.vertexF32, u = this.vertexU32;
    const w0 = v * 5;
    f[w0] = x; f[w0 + 1] = y; f[w0 + 2] = u0; f[w0 + 3] = v0; u[w0 + 4] = pTL;
    f[w0 + 5] = x + destWidth; f[w0 + 6] = y; f[w0 + 7] = u1; f[w0 + 8] = v0; u[w0 + 9] = pTR;
    f[w0 + 10] = x; f[w0 + 11] = y + destHeight; f[w0 + 12] = u0; f[w0 + 13] = v1; u[w0 + 14] = pBL;
    f[w0 + 15] = x + destWidth; f[w0 + 16] = y + destHeight; f[w0 + 17] = u1; f[w0 + 18] = v1; u[w0 + 19] = pBR;
    this.spriteCount++;
  }

  /** DrawRotated (`MirLibrary.DrawRotated:798-886`): the DrawScaledVertexColours quad with its corners rotated about the sprite's own centre (SetSpriteTransform =
   *  Scale·RotZ·Translate, D3D11Backend.cs:810-817; pivot after the art offset, MirLibrary.cs:876-880). Baked into the CPU vertices: no shader change, no transform
   *  flush, six-program contract intact; angle 0 packs IDENTICALLY to the un-rotated path (pinned both ways in tools/render-selftest.ts). */
  DrawRotated(
    texture: SpriteTextureRef,
    source: { x: number; y: number; width: number; height: number },
    x: number, y: number, angle: number,
    colour: { r: number; g: number; b: number; a: number },
  ): void {
    if (!this.inBatch) return;
    if (source.width <= 0 || source.height <= 0) return;
    if (texture.key !== (this.currentTexture?.key ?? null)) {
      this.flushRun();
      this.currentTexture = texture;
    }
    if (this.spriteCount === MAX_SPRITES) this.flushRun();
    const ox = texture.originX ?? 0, oy = texture.originY ?? 0; // an atlas sprite's origin on its page
    const u0 = (ox + source.x) / texture.allocWidth;
    const v0 = (oy + source.y) / texture.allocHeight;
    const u1 = (ox + source.x + source.width) / texture.allocWidth;
    const v1 = (oy + source.y + source.height) / texture.allocHeight;
    const packed = packColor(colour.r, colour.g, colour.b, colour.a);
    const corners = rotatedQuadCorners(x, y, source.width, source.height, angle);
    const v = this.spriteCount * 4;
    const f = this.vertexF32, u = this.vertexU32;
    const w0 = v * 5;
    f[w0] = corners[0]![0]; f[w0 + 1] = corners[0]![1]; f[w0 + 2] = u0; f[w0 + 3] = v0; u[w0 + 4] = packed;
    f[w0 + 5] = corners[1]![0]; f[w0 + 6] = corners[1]![1]; f[w0 + 7] = u1; f[w0 + 8] = v0; u[w0 + 9] = packed;
    f[w0 + 10] = corners[2]![0]; f[w0 + 11] = corners[2]![1]; f[w0 + 12] = u0; f[w0 + 13] = v1; u[w0 + 14] = packed;
    f[w0 + 15] = corners[3]![0]; f[w0 + 16] = corners[3]![1]; f[w0 + 17] = u1; f[w0 + 18] = v1; u[w0 + 19] = packed;
    this.spriteCount++;
  }

  /** Upload and draw the pending quads, then have the context ISSUE what it deferred (`BatchContext.submit`). Never reorders. */
  Flush(): void {
    this.flushRun();
    this.context.submit?.();
  }

  /** Texture-switch / ring-capacity barrier: the run goes to the context, which may defer GPU work until the next public `Flush()` or its own state change (per-sprite
   *  textures made ~1,800 flushes a frame). The uniform blocks are still uploaded first, here. */
  private flushRun(): void {
    if (this.spriteCount === 0) {
      this.updatePerBatchIfDirty();
      this.updateEffectIfDirty();
      return;
    }
    this.updatePerBatchIfDirty();
    this.updateEffectIfDirty();

    if (this.ringSpriteOffset + this.spriteCount > MAX_SPRITES)
      this.ringSpriteOffset = 0; // ring wrap: the D3D Map(WriteDiscard) equivalent

    // The CPU vertex array is scratch that always starts at slot 0 (the C# copies _vertices[0..count] to mapped+ringOffset); the RING offset exists only on the
    // GPU side. Uploading the ring-offset CPU slice shipped never-written slots.
    this.context.uploadVertices(
      new Uint8Array(this.vertexBuffer, 0, this.spriteCount * 4 * VERTEX_STRIDE),
      this.ringSpriteOffset * 4 * VERTEX_STRIDE,
    );
    // GL lacks baseVertex: the STATIC index buffer's slice from sprite `ringSpriteOffset` addresses exactly the triangles D3D's static buffer + base vertex does
    this.context.bindSpriteTexture(this.currentTexture!);
    this.context.drawIndexed(this.spriteCount * 6, this.ringSpriteOffset * 6);

    this.ringSpriteOffset += this.spriteCount;
    this.spriteCount = 0;
  }

  private updatePerBatchIfDirty(): void {
    if (!this.perBatchDirty) return;
    this.context.updatePerBatch({
      transform: this.transform,
      colorOp: this.colorOp,
      alphaTest: this.alphaTest,
      hueShift: this.hueShift,
      alphaRef: this.alphaRef,
      recolour: this.recolour,
    });
    this.perBatchDirty = false;
  }

  private updateEffectIfDirty(): void {
    if (!this.effectDirty) return;
    this.context.updateEffect(this.imageNorm, this.overlay);
    this.effectDirty = false;
  }
}

function newIdentity(): Float32Array {
  const m = new Float32Array(16);
  m[0] = m[5] = m[10] = m[15] = 1;
  return m;
}

/** Source rect that draws a render target's colour texture the RIGHT WAY UP: `{ x: 0, y: height, width, height: -height }` (the FBO row-order flip).
 *  `screenProjection` maps screen y = 0 to NDC +1 and a framebuffer's row 0 is its BOTTOM, so what a pass drew at screen y = 0 lands in the texture's LAST row
 *  (v = 1); the quad's TOP vertices use v0 = srcY / allocHeight, so a `{0, 0, w, h}` rect would be upside down (D3D render targets have row 0 on top, hence no
 *  source rect in the desktop's `DrawLightTexture`). With `y = h`, `height = -h` the top vertices sample v = 1; only UVs flip, not positions or winding.
 *  Works ONLY through `DrawScaled` / `DrawScaledVertexColours` (no source-size guard): `Draw` and `DrawRotated` refuse a non-positive height and would draw
 *  NOTHING. render-selftest §14 pins both halves. */
export function renderTargetSourceRect(width: number, height: number): { x: 0; y: number; width: number; height: number } {
  return { x: 0, y: height, width, height: -height };
}

/** Rotated-quad corners, the exact composition of `D3D11Backend.SetSpriteTransform` (:810-817): Scale · RotationZ(angle) · Translate on a sprite whose
 * DrawTexture offset is (−w/2, −h/2) (MirLibrary.cs:880-881), i.e. rotate the centred rect about the pivot (x + w/2, y + h/2). Row-vector convention:
 * p' = pivot + R·(p − pivot), x' = dx·cos − dy·sin, y' = dx·sin + dy·cos. Angle 0 reproduces the axis-aligned corners EXACTLY (pinned in render-selftest). */
export function rotatedQuadCorners(
  x: number, y: number, w: number, h: number, angle: number,
): [[number, number], [number, number], [number, number], [number, number]] {
  const cx = x + w / 2;
  const cy = y + h / 2;
  const c = Math.cos(angle);
  const s = Math.sin(angle);
  const rot = (px: number, py: number): [number, number] => {
    const dx = px - cx, dy = py - cy;
    return [cx + dx * c - dy * s, cy + dx * s + dy * c];
  };
  return [rot(x, y), rot(x + w, y), rot(x, y + h), rot(x + w, y + h)];
}

function isIdentity(m: Float32Array): boolean {
  return m[0] === 1 && m[5] === 1 && m[10] === 1 && m[15] === 1
    && m[1] === 0 && m[2] === 0 && m[3] === 0 && m[4] === 0 && m[6] === 0
    && m[7] === 0 && m[8] === 0 && m[9] === 0 && m[11] === 0 && m[12] === 0
    && m[13] === 0 && m[14] === 0;
}

function sameTransform(a: Float32Array, b: Float32Array): boolean {
  for (let i = 0; i < 16; i++) if (a[i] !== b[i]) return false;
  return true;
}

function sameVec4(a: Float32Array, b: Float32Array): boolean {
  return a[0] === b[0] && a[1] === b[1] && a[2] === b[2] && a[3] === b[3];
}

/** The D3D static index pattern (0,1,2,2,1,3 per sprite) rebased for the ring. */
export function bakeIndices(firstSprite: number, spriteCount: number): Uint16Array {
  const indices = new Uint16Array(spriteCount * 6);
  for (let i = 0; i < spriteCount; i++) {
    const vertex = (firstSprite + i) * 4;
    const o = i * 6;
    indices[o] = vertex;
    indices[o + 1] = vertex + 1;
    indices[o + 2] = vertex + 2;
    indices[o + 3] = vertex + 2;
    indices[o + 4] = vertex + 1;
    indices[o + 5] = vertex + 3;
  }
  return indices;
}

// — std140 uniform-block packers (offsets match the HLSL cbuffers) —

/** PerBatch block, 112 bytes: mat4 @0, vec4 @64, u32 @80, u32 @84, f32 @88, f32 @92, vec4 @96. */
export function packPerBatch(data: PerBatchData): Uint8Array {
  const buffer = new ArrayBuffer(112);
  const f32 = new Float32Array(buffer);
  const u32 = new Uint32Array(buffer);
  f32.set(data.transform, 0); // mat4 @ 0
  f32[16] = 1; f32[17] = 1; f32[18] = 1; f32[19] = 1; // TintColor @ 64 (reserved, = One)
  u32[20] = data.colorOp >>> 0; // @ 80
  u32[21] = data.alphaTest ? 1 : 0; // @ 84
  f32[22] = data.hueShift; // @ 88
  f32[23] = data.alphaRef; // @ 92
  f32[24] = data.recolour[0]; f32[25] = data.recolour[1];
  f32[26] = data.recolour[2]; f32[27] = data.recolour[3]; // @ 96
  return new Uint8Array(buffer);
}

/** std140 size of the `Effect` uniform block: DERIVED, never typed twice. `EFFECT_VEC4_FIELDS` in shaders.ts is the declaration list and a selftest re-parses
 * the GLSL block to assert they agree (a size appearing twice was already a bug once: 432 vs 448).
 * MUST equal the block's real size: a smaller UBO makes every draw INVALID_OPERATION and nothing renders. Keep the renderer's `bufferData` allocation on this. */
export const EFFECT_UBO_BYTES = EFFECT_VEC4_FIELDS.length * 16; // 28 vec4s = 448

/** Effect block: `ImageNorm` (image-space clamp for orb/mask), `BlendKeyParams` ((threshold, pass, fail), which `BeginBlendKeyShader` pins to (0.08, 1, 0) for every
 * additive draw) and the six overlay vec4s. Offsets resolve through `effectVec4Index`, so field ORDER in shaders.ts alone decides layout. Absent keys stay zero. */
export function packEffect(
  imageNorm: Float32Array,
  blendKeyParams?: Float32Array,
  overlay?: EffectOverlayUniforms,
): Uint8Array {
  const buffer = new ArrayBuffer(EFFECT_UBO_BYTES);
  const f32 = new Float32Array(buffer);
  writeEffectVec4(f32, "ImageNorm", imageNorm);
  if (blendKeyParams) writeEffectVec4(f32, "BlendKeyParams", blendKeyParams);
  if (overlay) {
    for (const name of Object.keys(overlay) as (keyof EffectOverlayUniforms)[]) {
      const v = overlay[name];
      if (v) writeEffectVec4(f32, name, v);
    }
  }
  return new Uint8Array(buffer);
}

function writeEffectVec4(f32: Float32Array, name: string, v: Float32Array): void {
  const base = effectVec4Index(name) * 4;
  f32[base] = v[0]!;
  f32[base + 1] = v[1]!;
  f32[base + 2] = v[2]!;
  f32[base + 3] = v[3]!;
}
