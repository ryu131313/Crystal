// Lifted from WoOOLcraft src/WebClient/src/render/backend.ts (2026-10-12) for the Crystal web client; the D3D11Backend it mirrors descends from Crystal's own.
// WebGL2 sprite backend: the browser counterpart of D3D11Backend for the six mandatory sprite programs. Implements the batcher's `BatchContext`:
// uniform blocks with the SAME std140 layout as the D3D cbuffers, the D3D blend-recipe table on blendFuncSeparate, point/clamp sampling, cull and
// depth off (painter's algorithm). BCn upload via WEBGL_compressed_texture_s3tc (`createBcTexture`) uses the same padded size as the RGBA path.
//
// Not mirrored: TileScratch and the orb composition, and the nine cosmetic programs (they degrade to Default by design). The one render-target path
// is the light layer's (createRenderTarget / bindRenderTarget / clearColour / releaseRenderTarget) plus its two blends (setAdditiveLightBlend,
// setLightModulateState).

// The type names are `import type` on purpose: a value-import of a type name survives `tsc` but NOT node's `--experimental-strip-types`, so
// `tools/render-selftest.ts` could not import this module and the optional-program policy went unpinned. Keep the split.
import type {
  BatchContext, PerBatchData, SpriteShaderMode, SpriteTextureRef, EffectOverlayUniforms,
} from "./batcher.ts";
import {
  BLEND_STATES, blendStateForMode, blendFactorFromRate, packPerBatch, packEffect, VERTEX_STRIDE,
  EFFECT_UBO_BYTES, MAX_SPRITES, bakeIndices,
} from "./batcher.ts";
import { VERTEX_SHADER_SHARED, FRAGMENT_PROGRAMS } from "./shaders.ts";
import { ShelfPage, atlasCell, atlasEligible, atlasGeometryFor, type AtlasGeometry } from "./spriteAtlas.ts";
import { screenProjection } from "./color.ts";

// BlendFactorName -> GL constants (name strings keep batcher.ts's symbolic table testable without a GL context)
const GL_BLEND_FACTOR: Record<string, number> = {
  Zero: 0x0000, // WebGL2 constants mirrored numerically
  One: 0x0001,
  SrcColor: 0x0300,
  InvSrcColor: 0x0301,
  SrcAlpha: 0x0302,
  InvSrcAlpha: 0x0303,
  DstAlpha: 0x0304,
  InvDstAlpha: 0x0305,
  DstColor: 0x0306,
  InvDstColor: 0x0307,
  BlendFactor: 0x8001, // CONSTANT_COLOR
  InvBlendFactor: 0x8002, // ONE_MINUS_CONSTANT_COLOR
};

export interface GLTextureRef extends SpriteTextureRef {
  glTexture: WebGLTexture;
  /** Bytes this texture holds on the GPU (4 a pixel RGBA8, half a byte BC1, one BC3); set by `createRgbaTexture` / `createBcTexture`, read by the page's texture budget
   *  (`textureGpuBytes`, which assumes RGBA8 at the allocated size when absent). */
  gpuBytes?: number;
  /** Set when the sprite lives on an ATLAS PAGE: `glTexture`/`key` and `allocWidth`/`allocHeight` are the page's, the sprite starts at (`originX`, `originY`).
   *  `releaseTexture` frees the sprite's place and deletes the page with its last sprite. */
  atlasPage?: AtlasPage;
  originX?: number;
  originY?: number;
}

/** One atlas page: a square texture in one format (side `shelf.size`), its shelves, and its pool's page list (so a release through another renderer over the same context still finds it). */
export interface AtlasPage {
  readonly glTexture: WebGLTexture;
  readonly shelf: ShelfPage;
  readonly format: "RGBA8" | BcFormat;
  readonly pool: string;
  readonly bytes: number;
  readonly pages: AtlasPage[];
}

/** The two compressed formats the sprite libraries ship (`PayloadFormat` minus DeflateBgra32). */
export type BcFormat = "BC1" | "BC3";

/** A colour render target: port of `D3D11Backend.CreateRenderTarget` (`:761-778`), for the light layer. `texture` has the shape of `createRgbaTexture`'s ref, so the
 * UNCHANGED sprite path (`DrawScaled` -> `bindSpriteTexture`) draws it. Draw it with `renderTargetSourceRect(width, height)` or it is upside down. RGBA8 where the
 * desktop's is B8G8R8A8_UNorm; channel order only affects storage (both 8 bits, so additive light clamps at 255). */
export interface RenderTargetRef {
  texture: GLTextureRef;
  framebuffer: WebGLFramebuffer;
  width: number;
  height: number;
}

/** Re-exported so the page imports the flip rect from the module of the render target it describes (defined in batcher.ts next to `DrawScaled`, the only draw it works through). */
export { renderTargetSourceRect } from "./batcher.ts";

/** Programs a link failure must not take the renderer down with. The C# compiles exactly these inside their own `try/catch` (`SpriteBatch2D.cs:1679-1714`) and
 * reports availability; Sprite_VS / Sprite_PS / the four other mandatory programs are compiled OUTSIDE any catch (a syntax error "would not start" the client, `:791-793`). */
const OPTIONAL_PROGRAMS: ReadonlySet<SpriteShaderMode> = new Set<SpriteShaderMode>(["ClawSlash", "RipClaw", "DruidFx", "SafeZoneSparkle", "Grayscale", "Light", "LightComposite"]);

/** The policy above as a PREDICATE the headless selftest can read (the set stays module-private; no tool can fail a link on purpose without a WebGL2 context, which left
 * the policy unpinned: emptying the set killed nothing). */
export function isOptionalProgram(mode: SpriteShaderMode): boolean {
  return OPTIONAL_PROGRAMS.has(mode);
}

export class WebGl2Renderer implements BatchContext {
  readonly gl: WebGL2RenderingContext;
  private readonly programs = new Map<SpriteShaderMode, WebGLProgram>();
  private currentMode: SpriteShaderMode = "Default";
  private readonly vao: WebGLVertexArrayObject;
  private readonly vertexBuffer: WebGLBuffer;
  private readonly indexBuffer: WebGLBuffer;
  private readonly perFrameUbo: WebGLBuffer;
  private readonly perBatchUbo: WebGLBuffer;
  private readonly effectUbo: WebGLBuffer;
  private readonly vertexCapacityBytes: number;
  private maskTexture: WebGLTexture | null = null;
  drawCallCount = 0;
  /** BlendKey (dark-pixel cut) params: the desktop's `BeginBlendKeyShader` constants (D3D11Backend.cs:1229: threshold 0.08, pass 1, fail 0), applied to EVERY additive
   * DrawBlend because `Config.CutBlendDarkPixelsForAllEffects` defaults true (Config.cs:273). vec4 index 2 of the Effect block. */
  private readonly blendKeyParams = new Float32Array([0.08, 1, 0, 0]);
  /** Target `bindRenderTarget` last bound (null = canvas); only `releaseRenderTarget` reads it, so the renderer is never left bound to a deleted framebuffer. */
  private boundTarget: RenderTargetRef | null = null;
  /** Size `setViewport` last received (the canvas's); `bindRenderTarget(null)` restores it (`D3D11Backend.cs:788-789`). Null until the first `setViewport`. */
  private canvasSize: { width: number; height: number } | null = null;
  /** Why the LAST `createRenderTarget` returned null (null after a success); the page reports it when falling back to "no light layer". */
  lastRenderTargetError: string | null = null;
  /** SPRITE ATLAS pages by pool and format (`<pool>|<format>`); see `spriteAtlas.ts`. */
  private readonly atlasPools = new Map<string, AtlasPage[]>();
  /** Transparent block data a new BC page starts from (BC1: colour0 <= colour1, every index 3 = transparent black; BC3: all zero), made once per format. */
  private readonly atlasClearBlocks = new Map<string, Uint8Array>();
  /** `MAX_TEXTURE_SIZE`, asked on the atlas's first use (never in the constructor: render-selftest pins its GL sequence). */
  private atlasMaxTexture: number | undefined = undefined;

  // DEFERRED SPRITE DRAWS. Every sprite frame is its own texture, so the batch flushes at almost every quad (~1,800 runs a frame on a busy map; each used to cost a
  // vertex upload, an index re-bake and upload, and five binds: `tools/perf-masskill.ts`). Now a run's vertices are COPIED into `staging` (the CPU image of the GPU
  // ring) and its draw RECORDED; `submit` uploads the whole contiguous staged range in ONE call and issues the recorded draws against the STATIC index buffer.
  // GL order is unchanged because EVERY other method that can affect a draw (blend, program, uniforms, mask, viewport, clears, target switches, texture release,
  // canvas copy) calls `submit` FIRST, and the batch calls it at every public `Flush()` / `End()`. An upload that does not continue the staged range (ring wrap,
  // a second batch starting at 0) also submits first.
  private readonly staging: Uint8Array;
  private stagedStart = -1;
  private stagedEnd = 0;
  private recordTexture: WebGLTexture | null = null;
  private readonly drawTextures: Array<WebGLTexture | null> = [];
  private readonly drawCounts: number[] = [];
  private readonly drawFirsts: number[] = [];
  private drawsPending = 0;

  private constructor(gl: WebGL2RenderingContext) {
    this.gl = gl;
    this.vertexCapacityBytes = MAX_SPRITES * 4 * VERTEX_STRIDE;
    this.staging = new Uint8Array(this.vertexCapacityBytes);

    for (const [mode, fragment] of Object.entries(FRAGMENT_PROGRAMS)) {
      // The overlay programs are compiled DEFENSIVELY: their caller refuses the draw when the program is missing, so a link failure must not take the renderer
      // down, but must ALSO not fall back to the plain sprite PS (it would paint the white quad over the victim, SpriteBatch2D.cs:1343-1348). Missing from
      // `programs` IS the "unavailable" state, reported through `programAvailable`.
      try {
        this.programs.set(mode as SpriteShaderMode, compileProgram(gl, VERTEX_SHADER_SHARED, fragment));
      } catch (error) {
        if (!isOptionalProgram(mode as SpriteShaderMode)) throw error;
        console.error(`[renderer] ${mode} failed to link — its add gate will refuse every spawn`, error);
      }
    }

    this.vao = gl.createVertexArray()!;
    this.vertexBuffer = gl.createBuffer()!;
    this.indexBuffer = gl.createBuffer()!;
    this.perFrameUbo = gl.createBuffer()!;
    this.perBatchUbo = gl.createBuffer()!;
    this.effectUbo = gl.createBuffer()!;

    gl.bindVertexArray(this.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, this.vertexCapacityBytes, gl.DYNAMIC_DRAW);
    // layout mirrors the D3D input layout: POSITION@0 R32G32, TEXCOORD@8 R32G32, COLOR@16 R8G8B8A8_UNorm, stride 20
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, VERTEX_STRIDE, 0);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 2, gl.FLOAT, false, VERTEX_STRIDE, 8);
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 4, gl.UNSIGNED_BYTE, true, VERTEX_STRIDE, 16);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
    // the D3D STATIC index pattern for the whole ring, uploaded once; `drawIndexed` offsets into it
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, bakeIndices(0, MAX_SPRITES), gl.STATIC_DRAW);
    gl.bindVertexArray(null);

    gl.bindBuffer(gl.UNIFORM_BUFFER, this.perFrameUbo);
    gl.bufferData(gl.UNIFORM_BUFFER, 64, gl.DYNAMIC_DRAW);
    gl.bindBuffer(gl.UNIFORM_BUFFER, this.perBatchUbo);
    gl.bufferData(gl.UNIFORM_BUFFER, 112, gl.DYNAMIC_DRAW);
    gl.bindBuffer(gl.UNIFORM_BUFFER, this.effectUbo);
    gl.bufferData(gl.UNIFORM_BUFFER, EFFECT_UBO_BYTES, gl.DYNAMIC_DRAW);
    gl.bindBuffer(gl.UNIFORM_BUFFER, null);

    // Point and clamp everywhere (the D3D9 client's only sampler config; sampler state is per texture unit, bound at use). Cull off (mirrored draws flip winding), depth off.
    gl.disable(gl.CULL_FACE);
    gl.disable(gl.DEPTH_TEST);
    gl.disable(gl.SCISSOR_TEST);

    for (const program of this.programs.values()) {
      // uniform block binding points: PerFrame=0, PerBatch=1, Effect=2
      bindBlock(gl, program, "PerFrame", 0);
      bindBlock(gl, program, "PerBatch", 1);
      bindBlock(gl, program, "Effect", 2);
      // uniform1i writes to the CURRENT program, so useProgram first or the samplers never bind (INVALID_OPERATION)
      gl.useProgram(program);
      gl.uniform1i(gl.getUniformLocation(program, "SpriteTex"), 0);
      gl.uniform1i(gl.getUniformLocation(program, "MaskTex"), 1);
    }
    gl.bindBufferBase(gl.UNIFORM_BUFFER, 0, this.perFrameUbo);
    gl.bindBufferBase(gl.UNIFORM_BUFFER, 1, this.perBatchUbo);
    gl.bindBufferBase(gl.UNIFORM_BUFFER, 2, this.effectUbo);

    // `currentMode` starts at "Default", so setShaderMode("Default") early-returns and would never useProgram, leaving NO program current (draws fail "no valid shader
    // program in use"). Bind Default here to make the declared initial state true.
    const defaultProgram = this.programs.get("Default");
    if (defaultProgram) gl.useProgram(defaultProgram);
  }

  /** Null when WebGL2 is unavailable (the page shows a message). */
  static create(canvas: HTMLCanvasElement): WebGl2Renderer | null {
    const gl = canvas.getContext("webgl2", { antialias: false, alpha: false });
    if (!gl) return null;
    return new WebGl2Renderer(gl);
  }

  get rendererInfo(): string {
    const gl = this.gl;
    return `${gl.getParameter(gl.VERSION)} · ${gl.getParameter(gl.RENDERER)}`;
  }

  /** PerFrame upload + viewport: call on resize and at frame start. */
  setViewport(width: number, height: number): void {
    this.submit();
    this.gl.viewport(0, 0, width, height);
    this.gl.bindBuffer(this.gl.UNIFORM_BUFFER, this.perFrameUbo);
    this.gl.bufferSubData(this.gl.UNIFORM_BUFFER, 0, screenProjection(width, height));
    this.canvasSize = { width, height }; // what bindRenderTarget(null) restores (no GL call)
  }

  clear(): void {
    this.submit();
    this.gl.clearColor(0, 0, 0, 1); // black, the desktop's: past the map's edge is black (`MapControl` clears to Color.Black), not the demo page's #10141f
    this.gl.clear(this.gl.COLOR_BUFFER_BIT);
  }

  /** `D3D11Backend.SetBlend`: mode -> state table, byte-truncated DX9 factor; leaving blending restores Normal at factor 1. */
  setBlend(blending: boolean, rate: number, mode: string): void {
    const gl = this.gl;
    if (!blending) {
      this.applyState("Normal", 1);
      return;
    }
    this.applyState(blendStateForMode(mode), rate);
  }

  /** Crystal `DXManager.SetOpacity` (DXManager.cs): BlendFactor / InvBlendFactor at the opacity, or plain alpha at >= 1. Crystal port addition. */
  setOpacity(opacity: number): void {
    if (opacity >= 1 || opacity < 0) this.applyState("Normal", 1);
    else this.applyState("OpacityFactor", opacity);
  }

  private applyState(stateName: keyof typeof BLEND_STATES, rate: number): void {
    this.submit(); // the ONE path of setBlend / setAdditiveLightBlend / setLightModulateState / setInverseDestAlphaOverwriteBlend
    const gl = this.gl;
    const [src, dst, srcA, dstA] = BLEND_STATES[stateName];
    const factor = blendFactorFromRate(rate);
    gl.blendColor(factor, factor, factor, factor);
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(GL_BLEND_FACTOR[src], GL_BLEND_FACTOR[dst], GL_BLEND_FACTOR[srcA], GL_BLEND_FACTOR[dstA]);
  }

  /** `D3D11Backend.SetAdditiveLightBlend` (`:1265-1269`): Colorfy (SrcAlpha, One | SrcAlpha, One) at factor 1, which the light RT's draws add under. No mode name reaches
   * it. Does NOT flush (the caller flushes first; the desktop's BindBlendState flushes, `:500-504`, but this renderer does not own the batch) and keeps no bookkeeping, so a later
   * `setBlend(false, 1, "NORMAL")` always rebinds Normal (render-selftest §14). */
  setAdditiveLightBlend(): void {
    this.applyState("Colorfy", 1);
  }

  /** `D3D11Backend.SetLightModulateState` (`:1246-1250`): ZeroSourceColor (Zero, SrcColor | Zero, SrcAlpha) at factor 1, so drawing the light RT MULTIPLIES what is under it
   * (light alpha is 255, so map alpha is unchanged). The desktop's `_spriteBatch.ColorOp = 0` (`:1249`) is left to the caller. Does not flush. Leave with
   * `setBlend(false, 1, "NORMAL")` (the blend half of `RestoreSpriteAlphaBatch`, `:1257-1263`; the desktop needs it because its SetBlend can early-return, `:508`; this port's never does). */
  setLightModulateState(): void {
    this.applyState("ZeroSourceColor", 1);
  }

  /** `D3D11Backend.SetInverseDestAlphaOverwriteBlend` (`:1434-1437`): InvDestAlphaOverwrite (InvDstAlpha, Zero | InvDstAlpha, Zero) at factor 1, the back-effect composite's
   * EFFECT draw (`MirLibrary.cs:503-504`): keeps (1 − dst.a) of the effect image and replaces the scratch under it. Does not flush. Leave with
   * `setBlend(false, 1, "NORMAL")` (the desktop's RestartSpriteBatch, `:1022-1028`). */
  setInverseDestAlphaOverwriteBlend(): void {
    this.applyState("InvDestAlphaOverwrite", 1);
  }

  /** Crystal `DXManager.SetEnhancedLightAdditiveBlend` (One, One) into the float light map; the caller also selects the "Light" program. Does not flush.
   *  Leave with `setBlend(false, 1, "NORMAL")`. */
  setEnhancedLightAdditiveBlend(): void {
    this.applyState("AddOne", 1);
  }

  /** Crystal `DXManager.SetEnhancedLightCompositeBlend` (DestinationColor, SourceColor); the caller also selects "LightComposite". Does not flush.
   *  Leave with `setBlend(false, 1, "NORMAL")`. */
  setEnhancedLightCompositeBlend(): void {
    this.applyState("DestColorSrcColor", 1);
  }

  /** `EXT_color_buffer_float`, asked once on first use (`undefined` = not asked). Makes RGBA16F colour-renderable (and blendable) in WebGL2. */
  private colorBufferFloat: object | null | undefined = undefined;

  /** Crystal `IRenderBackend.SupportsEnhancedLighting`: the Light / LightComposite programs linked and half-float targets are renderable. */
  supportsEnhancedLighting(): boolean {
    if (this.colorBufferFloat === undefined) this.colorBufferFloat = this.gl.getExtension("EXT_color_buffer_float");
    return this.colorBufferFloat !== null && this.programs.has("Light") && this.programs.has("LightComposite");
  }

  setShaderMode(mode: SpriteShaderMode): void {
    if (this.currentMode === mode) return;
    const program = this.programs.get(mode);
    // The six absent cosmetic modes degrade by keeping the current program. For the three overlay modes this is unreachable (the add gate refuses a spawn whose program
    // is missing) and deliberately NOT a fallback to Default, which would paint the white quad. `programAvailable` is the gate's input.
    if (!program) return;
    this.submit();
    this.currentMode = mode;
    this.gl.useProgram(program);
  }

  /** `DXManager.IsClawSlashShaderAvailable` / `IsRipClawShaderAvailable` / `IsDruidFxShaderAvailable` (`DXManager.cs:443-451`): false when the program failed to link, and
   * (one level up) when there is no renderer at all. */
  programAvailable(mode: SpriteShaderMode): boolean {
    return this.programs.has(mode);
  }

  useMaskTexture(texture: WebGLTexture | null): void {
    this.submit();
    this.maskTexture = texture;
    this.gl.activeTexture(this.gl.TEXTURE1);
    this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
    this.gl.activeTexture(this.gl.TEXTURE0);
  }

  /** RGBA8 upload from a decoded sprite; every upload BC payloads cannot take (`createBcTexture`), and the fallback. */
  createRgbaTexture(rgba: Uint8Array, width: number, height: number): GLTextureRef {
    const gl = this.gl;
    const glTexture = gl.createTexture()!;
    gl.bindTexture(gl.TEXTURE_2D, glTexture);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4); // RGBA rows are 4-aligned
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, rgba);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return { key: glTexture, glTexture, width, height, allocWidth: width, allocHeight: height, gpuBytes: width * height * 4 };
  }

  /** `WEBGL_compressed_texture_s3tc`, asked ONCE on first use (never in the constructor: render-selftest records its GL sequence). `undefined` = not asked; `null` = none. */
  private s3tc: WEBGL_compressed_texture_s3tc | null | undefined = undefined;
  /** A format whose first upload the GPU refused: never retried, later sprites of it are decoded instead. */
  private readonly bcRefused = new Set<BcFormat>();
  /** Formats whose first upload was checked with `getError` (one synchronous GPU round trip, paid once each). */
  private readonly bcVerified = new Set<BcFormat>();

  /** True when BC1/BC3 blocks can be uploaded as they are (`MirImage.CreateImage`'s `Bc1/Bc3` path): no JS decode and 1/8 (BC1) or 1/4 (BC3) of RGBA8's GPU memory. */
  supportsBcTextures(format?: BcFormat): boolean {
    if (this.s3tc === undefined) {
      const gl = this.gl as WebGL2RenderingContext & { getExtension?: (name: string) => unknown };
      let ext: unknown = null;
      try { ext = typeof gl.getExtension === "function" ? gl.getExtension("WEBGL_compressed_texture_s3tc") : null; } catch { ext = null; }
      this.s3tc = ext !== null && ext !== undefined && typeof ext === "object" ? ext as WEBGL_compressed_texture_s3tc : null;
    }
    return this.s3tc !== null && (format === undefined || !this.bcRefused.has(format));
  }

  /** Upload BC1/BC3 blocks as a compressed texture (DECODE-FREE). `width`/`height` are the PADDED size (multiples of 4, as `fetchBcPayload` returns and the RGBA path creates at), so
   *  the batcher's UV math is unchanged. BC1 goes up as DXT1 **RGBA** (its three-colour blocks carry the 1-bit transparent black the desktop's `Bc1` samples), BC3 as DXT5.
   *  Returns null (the caller decodes the SAME bytes to RGBA) when the extension is missing, the size is not whole blocks, the byte count is wrong, or the GPU refused the format's
   *  first upload (checked once per format; a refusal turns that format off for the session). Never throws for those. */
  createBcTexture(format: BcFormat, blocks: Uint8Array, width: number, height: number): GLTextureRef | null {
    if (!this.supportsBcTextures(format)) return null;
    if (width <= 0 || height <= 0 || width % 4 !== 0 || height % 4 !== 0) return null;
    const need = (width / 4) * (height / 4) * (format === "BC1" ? 8 : 16);
    if (blocks.length !== need) return null;
    const gl = this.gl;
    const ext = this.s3tc!;
    const verify = !this.bcVerified.has(format);
    // drain errors that are not this upload's (bounded: a lost context may keep answering)
    if (verify) for (let i = 0; i < 16 && gl.getError() !== gl.NO_ERROR; i++) { /* drained */ }
    const glTexture = gl.createTexture()!;
    gl.bindTexture(gl.TEXTURE_2D, glTexture);
    gl.compressedTexImage2D(gl.TEXTURE_2D, 0,
      format === "BC1" ? ext.COMPRESSED_RGBA_S3TC_DXT1_EXT : ext.COMPRESSED_RGBA_S3TC_DXT5_EXT, width, height, 0, blocks);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    if (verify) {
      const error = gl.getError();
      if (error !== gl.NO_ERROR) {
        gl.deleteTexture(glTexture);
        this.bcRefused.add(format);
        console.warn(`[renderer] the GPU refused a compressed ${format} upload (GL error ${error}) - ${format} sprites are decoded in JavaScript from now on`);
        return null;
      }
      this.bcVerified.add(format);
    }
    return { key: glTexture, glTexture, width, height, allocWidth: width, allocHeight: height, gpuBytes: need };
  }

  /** Frees an uploaded texture (counterpart of `ITexture2D.Dispose`, which the C# calls when a damage number's signature changes or it expires, `DamageInfo.cs:328`, `:198`).
   * Null-tolerant because callers hold `GLTextureRef | null` from a loader. */
  releaseTexture(texture: GLTextureRef | null): void {
    if (!texture) return;
    const page = texture.atlasPage;
    if (page !== undefined) {
      // an atlas sprite's place is not reused; the page goes with its last sprite
      if (!page.shelf.release()) return;
      this.submit(); // a recorded draw may still name the page
      this.gl.deleteTexture(page.glTexture);
      const at = page.pages.indexOf(page);
      if (at >= 0) page.pages.splice(at, 1);
      return;
    }
    this.submit(); // a recorded draw may still name it
    this.gl.deleteTexture(texture.glTexture);
  }

  // — Sprite atlas —
  // A sprite frame goes on a shared page of its pool, so consecutive sprites on one page are ONE draw (the batcher coalesces while `key` is unchanged). The page decides
  // the pool; placement is `spriteAtlas.ts`. Both methods return null (the caller makes the sprite its own texture) when the sprite is too big for a page, the format has not
  // had its first verifying upload, or a page cannot be made. Neither submits: a page only gains texels no recorded draw samples, like a new texture made mid-batch.

  /** An RGBA8 sprite onto a page of `pool`. */
  createAtlasedRgbaTexture(pool: string, rgba: Uint8Array, width: number, height: number): GLTextureRef | null {
    if (!atlasEligible(this.atlasGeometry("RGBA8"), width, height) || rgba.length !== width * height * 4) return null;
    const placed = this.atlasPlace(pool, "RGBA8", width, height);
    if (placed === null) return null;
    const gl = this.gl;
    gl.bindTexture(gl.TEXTURE_2D, placed.page.glTexture);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4); // RGBA rows are 4-aligned
    gl.texSubImage2D(gl.TEXTURE_2D, 0, placed.x, placed.y, width, height, gl.RGBA, gl.UNSIGNED_BYTE, rgba);
    return this.atlasRef(placed, width, height, width * height * 4);
  }

  /** BC1/BC3 blocks onto a page of `pool`, only once `createBcTexture` verified the format's first upload (so a GPU that refuses it never gets a compressed page). `width`/`height` are padded. */
  createAtlasedBcTexture(pool: string, format: BcFormat, blocks: Uint8Array, width: number, height: number): GLTextureRef | null {
    if (!this.supportsBcTextures(format) || !this.bcVerified.has(format)) return null;
    if (width % 4 !== 0 || height % 4 !== 0 || !atlasEligible(this.atlasGeometry(format), width, height)) return null;
    const need = (width / 4) * (height / 4) * (format === "BC1" ? 8 : 16);
    if (blocks.length !== need) return null;
    const placed = this.atlasPlace(pool, format, width, height);
    if (placed === null) return null;
    const gl = this.gl;
    const ext = this.s3tc!;
    gl.bindTexture(gl.TEXTURE_2D, placed.page.glTexture);
    gl.compressedTexSubImage2D(gl.TEXTURE_2D, 0, placed.x, placed.y, width, height,
      format === "BC1" ? ext.COMPRESSED_RGBA_S3TC_DXT1_EXT : ext.COMPRESSED_RGBA_S3TC_DXT5_EXT, blocks);
    return this.atlasRef(placed, width, height, need);
  }

  /** Atlas census: pages, GPU bytes, sprites, share of page area their cells cover, pages per pool. */
  atlasCensus(): { pages: number; bytes: number; sprites: number; fill: number; pools: Record<string, number> } {
    let pages = 0, bytes = 0, sprites = 0, used = 0, area = 0;
    const pools: Record<string, number> = {};
    for (const [key, list] of this.atlasPools) {
      if (list.length === 0) continue;
      pools[key] = list.length;
      for (const page of list) { pages++; bytes += page.bytes; sprites += page.shelf.live; used += page.shelf.used; area += page.shelf.size * page.shelf.size; }
    }
    return { pages, bytes, sprites, fill: area > 0 ? used / area : 0, pools };
  }

  /** A place for a `width` x `height` sprite: the first page of the pool with room, else a new page. */
  private atlasPlace(pool: string, format: "RGBA8" | BcFormat, width: number, height: number): { page: AtlasPage; x: number; y: number } | null {
    const key = `${pool}|${format}`;
    let pages = this.atlasPools.get(key);
    if (pages === undefined) { pages = []; this.atlasPools.set(key, pages); }
    const geometry = this.atlasGeometry(format);
    const cell = atlasCell(geometry, width, height);
    for (const page of pages) {
      const at = page.shelf.place(cell.w, cell.h);
      if (at !== null) return { page, x: at.x, y: at.y };
    }
    const page = this.createAtlasPage(key, format, pages, geometry.pageSize);
    if (page === null) return null;
    pages.push(page);
    const at = page.shelf.place(cell.w, cell.h);
    return at === null ? null : { page, x: at.x, y: at.y };
  }

  /** A new, fully TRANSPARENT page (NEAREST, CLAMP). RGBA8 starts zeroed (WebGL clears new storage); a BC page is made from transparent blocks. */
  private createAtlasPage(pool: string, format: "RGBA8" | BcFormat, pages: AtlasPage[], size: number): AtlasPage | null {
    const gl = this.gl;
    const glTexture = gl.createTexture();
    if (glTexture === null) return null;
    gl.bindTexture(gl.TEXTURE_2D, glTexture);
    let bytes: number;
    if (format === "RGBA8") {
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      bytes = size * size * 4;
    } else {
      const blocks = this.atlasClearBlocksFor(format, size);
      const ext = this.s3tc!;
      gl.compressedTexImage2D(gl.TEXTURE_2D, 0, format === "BC1" ? ext.COMPRESSED_RGBA_S3TC_DXT1_EXT : ext.COMPRESSED_RGBA_S3TC_DXT5_EXT, size, size, 0, blocks);
      bytes = blocks.length;
    }
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return { glTexture, shelf: new ShelfPage(size), format, pool, bytes, pages };
  }

  /** Transparent blocks for a whole BC page of `format`, made once. */
  private atlasClearBlocksFor(format: BcFormat, size: number): Uint8Array {
    const known = this.atlasClearBlocks.get(`${format}:${size}`);
    if (known !== undefined) return known;
    const blockBytes = format === "BC1" ? 8 : 16;
    const data = new Uint8Array((size / 4) * (size / 4) * blockBytes);
    // BC1: colour0 = colour1 = 0 (colour0 <= colour1, the punch-through mode) and every 2-bit index 3 (transparent black)
    if (format === "BC1") for (let i = 4; i < data.length; i += 8) { data[i] = 0xff; data[i + 1] = 0xff; data[i + 2] = 0xff; data[i + 3] = 0xff; }
    this.atlasClearBlocks.set(`${format}:${size}`, data);
    return data;
  }

  /** The page shape for `format` on this GPU (`spriteAtlas.ts`). */
  private atlasGeometry(format: "RGBA8" | BcFormat): AtlasGeometry {
    if (this.atlasMaxTexture === undefined) {
      const max = Number(this.gl.getParameter(this.gl.MAX_TEXTURE_SIZE));
      this.atlasMaxTexture = Number.isFinite(max) ? max : 0;
    }
    return atlasGeometryFor(format, this.atlasMaxTexture);
  }

  private atlasRef(placed: { page: AtlasPage; x: number; y: number }, width: number, height: number, gpuBytes: number): GLTextureRef {
    const page = placed.page;
    return {
      key: page.glTexture, glTexture: page.glTexture, width, height,
      allocWidth: page.shelf.size, allocHeight: page.shelf.size,
      gpuBytes, atlasPage: page, originX: placed.x, originY: placed.y,
    };
  }

  /** An EMPTY RGB8 texture for `copyCanvasRegion` (the seam blend's mirror band, port-only; see `seamBlend.ts`). RGB8, not RGBA8: the canvas is `alpha: false` and
   * `copyTexSubImage2D` only copies into a format that is a subset of the read framebuffer's, so RGBA8 would fail every copy (INVALID_OPERATION). Sampling reads alpha 1. */
  createCanvasStripTexture(width: number, height: number): GLTextureRef {
    const gl = this.gl;
    const glTexture = gl.createTexture()!;
    gl.bindTexture(gl.TEXTURE_2D, glTexture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGB8, width, height, 0, gl.RGB, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return { key: glTexture, glTexture, width, height, allocWidth: width, allocHeight: height };
  }

  /** Copies the region (x, yTop, width, height) of the BOUND surface (canvas, or the floor bake's target, whose height sets the row flip), in top-left page coordinates, into `texture`
   * at (0, 0). The caller flushes first. GL counts rows from the bottom, so the copy's row 0 is the region's BOTTOM row: draw it with a vertically flipped source rect.
   * No flush, no framebuffer switch. */
  copyCanvasRegion(texture: GLTextureRef, x: number, yTop: number, width: number, height: number): void {
    this.submit(); // the copy reads the canvas: everything drawn so far must be on it
    const gl = this.gl;
    const canvasHeight = this.boundTarget !== null ? this.boundTarget.height : (this.canvasSize?.height ?? gl.drawingBufferHeight);
    gl.bindTexture(gl.TEXTURE_2D, texture.glTexture);
    gl.copyTexSubImage2D(gl.TEXTURE_2D, 0, 0, 0, x, canvasHeight - yTop - height, width, height);
  }

  /** Sets the ALPHA of every pixel of the bound surface to 1, colour untouched (a colour-masked clear). The floor bake ends with it: the canvas is `alpha: false`, so what is
   *  drawn onto it acts as alpha 1, while the bake's RGBA8 target keeps whatever alpha its blends wrote. With alpha 1 everywhere, plain SrcAlpha/InvSrcAlpha copies the bake exactly. */
  fillTargetAlpha(): void {
    this.submit();
    const gl = this.gl;
    gl.colorMask(false, false, false, true);
    gl.clearColor(0, 0, 0, 1);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.colorMask(true, true, true, true);
  }

  // — Render targets (light layer) —

  /** `D3D11Backend.CreateRenderTarget` (`:761-778`): an EMPTY RGBA8 texture attached as COLOR_ATTACHMENT0 of a new framebuffer, NEAREST + CLAMP_TO_EDGE like `createRgbaTexture`
   * (`SpriteBatch2D.cs:1786-1792`); contents undefined until the first `clearColour`. Rebinds TEXTURE_2D and FRAMEBUFFER to set up, then restores the queried previous bindings;
   * never touches viewport or projection. FAILURE RETURNS NULL, it does not throw (a throw in the frame loop is one missed `catch` from a dead canvas): when
   * `checkFramebufferStatus` is not FRAMEBUFFER_COMPLETE both objects are deleted and `lastRenderTargetError` gives size and hex status. */
  createRenderTarget(width: number, height: number, format: "RGBA8" | "RGBA16F" = "RGBA8"): RenderTargetRef | null {
    const gl = this.gl;
    // RGBA16F = Crystal D3D11Backend.CreateLightTarget (R16G16B16A16_Float) for the Enhanced light map; needs supportsEnhancedLighting() first.
    if (format === "RGBA16F" && !this.supportsEnhancedLighting()) {
      this.lastRenderTargetError = "createRenderTarget: RGBA16F needs EXT_color_buffer_float";
      return null;
    }
    const previousTexture = gl.getParameter(gl.TEXTURE_BINDING_2D) as WebGLTexture | null;
    const previousFramebuffer = gl.getParameter(gl.FRAMEBUFFER_BINDING) as WebGLFramebuffer | null;
    const glTexture = gl.createTexture()!;
    gl.bindTexture(gl.TEXTURE_2D, glTexture);
    if (format === "RGBA16F") gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, width, height, 0, gl.RGBA, gl.HALF_FLOAT, null);
    else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const framebuffer = gl.createFramebuffer()!;
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, glTexture, 0);
    const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
    gl.bindFramebuffer(gl.FRAMEBUFFER, previousFramebuffer);
    gl.bindTexture(gl.TEXTURE_2D, previousTexture);
    if (status !== gl.FRAMEBUFFER_COMPLETE) {
      gl.deleteFramebuffer(framebuffer);
      gl.deleteTexture(glTexture);
      this.lastRenderTargetError = `createRenderTarget(${width}x${height}): framebuffer incomplete — checkFramebufferStatus 0x${(status >>> 0).toString(16)}`;
      return null;
    }
    this.lastRenderTargetError = null;
    return {
      texture: { key: glTexture, glTexture, width, height, allocWidth: width, allocHeight: height },
      framebuffer,
      width,
      height,
    };
  }

  /** `D3D11Backend.SetTarget` (`:780-793`): binds the target's framebuffer (null = canvas), then sets `gl.viewport` AND the projection (PerFrame upload, as `setViewport`)
   * to the bound surface's size; on unbind it uses the size `setViewport` last got, or the drawing buffer's, never a 0x0 projection.
   * Does NOT flush (no renderer state change here does): the desktop's `OnRenderTargetChanging` flush (`:784`) is the CALLER's job, or quads batched for one surface draw into the
   * other under its projection. Its SRV-slot-0 clear has no GL counterpart (every draw rebinds unit 0). Call `setViewport` only while the canvas is bound. */
  bindRenderTarget(rt: RenderTargetRef | null): void {
    this.submit();
    const gl = this.gl;
    this.boundTarget = rt;
    gl.bindFramebuffer(gl.FRAMEBUFFER, rt ? rt.framebuffer : null);
    const width = rt ? rt.width : (this.canvasSize?.width ?? gl.drawingBufferWidth);
    const height = rt ? rt.height : (this.canvasSize?.height ?? gl.drawingBufferHeight);
    gl.viewport(0, 0, width, height);
    gl.bindBuffer(gl.UNIFORM_BUFFER, this.perFrameUbo);
    gl.bufferSubData(gl.UNIFORM_BUFFER, 0, screenProjection(width, height));
  }

  /** Clears WHATEVER is bound to one colour in BYTES 0..255 (`b / 255` exactly); the desktop clears the light RT to its `BackColour` (`Light.CreateTexture`, `MapControl.cs:5556-5582`).
   * `clear()` with the demo's fixed #10141f is unchanged. */
  clearColour(r: number, g: number, b: number, a: number): void {
    this.submit();
    const gl = this.gl;
    gl.clearColor(r / 255, g / 255, b / 255, a / 255);
    gl.clear(gl.COLOR_BUFFER_BIT);
  }

  /** Deletes the target's framebuffer AND texture (null is a no-op). Releasing the CURRENTLY BOUND target first calls `bindRenderTarget(null)`; otherwise GL falls back to the
   * default framebuffer but keeps the target's viewport and projection. */
  releaseRenderTarget(rt: RenderTargetRef | null): void {
    if (!rt) return;
    this.submit();
    if (this.boundTarget === rt) this.bindRenderTarget(null);
    this.gl.deleteFramebuffer(rt.framebuffer);
    this.gl.deleteTexture(rt.texture.glTexture);
  }

  // — BatchContext —

  updatePerBatch(data: PerBatchData): void {
    this.submit(); // recorded draws were batched under the previous block
    this.gl.bindBuffer(this.gl.UNIFORM_BUFFER, this.perBatchUbo);
    this.gl.bufferSubData(this.gl.UNIFORM_BUFFER, 0, packPerBatch(data));
  }

  updateEffect(imageNorm: Float32Array, overlay?: EffectOverlayUniforms): void {
    this.submit();
    this.gl.bindBuffer(this.gl.UNIFORM_BUFFER, this.effectUbo);
    this.gl.bufferSubData(this.gl.UNIFORM_BUFFER, 0, packEffect(imageNorm, this.blendKeyParams, overlay));
  }

  /** RECORDS the texture the next `drawIndexed` uses; the bind happens in `submit`. */
  bindSpriteTexture(texture: SpriteTextureRef): void {
    this.recordTexture = (texture as GLTextureRef).glTexture;
  }

  /** COPIES the run's vertices into `staging` at their ring offset; `submit` uploads the range in one call. A range that does not continue the staged one (ring wrap, another batch at 0) submits first. */
  uploadVertices(bytes: Uint8Array, gpuByteOffset: number): void {
    if (this.stagedStart >= 0 && gpuByteOffset !== this.stagedEnd) this.submit();
    this.staging.set(bytes, gpuByteOffset);
    if (this.stagedStart < 0) this.stagedStart = gpuByteOffset;
    this.stagedEnd = gpuByteOffset + bytes.length;
  }

  /** RECORDS one draw of the static index buffer from `firstIndex`; `submit` issues it. */
  drawIndexed(indexCount: number, firstIndex: number): void {
    const n = this.drawsPending++;
    this.drawTextures[n] = this.recordTexture;
    this.drawCounts[n] = indexCount;
    this.drawFirsts[n] = firstIndex;
  }

  /** Issue every deferred upload and draw in order: ONE `bufferSubData` of the staged vertex range, then each draw against the static index buffer (a repeated texture is not
   *  rebound). No GL call at all when nothing is pending, so every method that calls it first keeps its own GL sequence (render-selftest §14). */
  submit(): void {
    if (this.stagedStart < 0 && this.drawsPending === 0) return;
    const gl = this.gl;
    if (this.stagedStart >= 0) {
      gl.bindBuffer(gl.ARRAY_BUFFER, this.vertexBuffer);
      gl.bufferSubData(gl.ARRAY_BUFFER, this.stagedStart, this.staging, this.stagedStart, this.stagedEnd - this.stagedStart);
      this.stagedStart = -1;
      this.stagedEnd = 0;
    }
    if (this.drawsPending === 0) return;
    // The VAO carries BOTH the attribute layout and the ELEMENT_ARRAY_BUFFER binding and setup leaves it unbound; without this bind drawElements runs against the default VAO
    // (no attributes, no index buffer): INVALID_OPERATION and nothing drawn. Bound per submit so it stays correct if a second VAO appears.
    gl.bindVertexArray(this.vao);
    gl.activeTexture(gl.TEXTURE0);
    let bound: WebGLTexture | null | undefined = undefined;
    for (let i = 0; i < this.drawsPending; i++) {
      const texture = this.drawTextures[i] ?? null;
      if (texture !== bound) { gl.bindTexture(gl.TEXTURE_2D, texture); bound = texture; }
      gl.drawElements(gl.TRIANGLES, this.drawCounts[i]!, gl.UNSIGNED_SHORT, this.drawFirsts[i]! * 2);
      this.drawTextures[i] = null; // never pin a texture the page may delete next
      this.drawCallCount++;
    }
    this.drawsPending = 0;
  }
}

function compileProgram(gl: WebGL2RenderingContext, vsSource: string, fsSource: string): WebGLProgram {
  const vs = gl.createShader(gl.VERTEX_SHADER)!;
  gl.shaderSource(vs, vsSource);
  gl.compileShader(vs);
  if (!gl.getShaderParameter(vs, gl.COMPILE_STATUS))
    throw new Error(`Sprite_VS compile failed: ${gl.getShaderInfoLog(vs)}`);
  const fs = gl.createShader(gl.FRAGMENT_SHADER)!;
  gl.shaderSource(fs, fsSource);
  gl.compileShader(fs);
  if (!gl.getShaderParameter(fs, gl.COMPILE_STATUS))
    throw new Error(`sprite FS compile failed: ${gl.getShaderInfoLog(fs)}`);
  const program = gl.createProgram()!;
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  gl.deleteShader(vs);
  gl.deleteShader(fs);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS))
    throw new Error(`sprite program link failed: ${gl.getProgramInfoLog(program)}`);
  return program;
}

function bindBlock(gl: WebGL2RenderingContext, program: WebGLProgram, name: string, binding: number): void {
  const index = gl.getUniformBlockIndex(program, name);
  if (index !== gl.INVALID_INDEX) gl.uniformBlockBinding(program, index, binding);
}
