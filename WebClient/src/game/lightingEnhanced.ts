// Enhanced lighting: port of Client/MirScenes/MapControl.Lighting.cs ([Graphics] Lighting=Enhanced, in game @light new / @light old).
// Same light sources, rectangles and classic quirks as MapControl.DrawLights (lighting.ts), drawn differently: lights add (One, One)
// into a half-float light map through Light_PS (falloff (1 - r^2)^2, intensity = vertex alpha * 4), LightComposite_PS soft-knees the
// map and the (DstColor, SrcColor) blend makes it scene * light with light in 0..2. Ambient: the classic darkness colours, a moonlit
// night, warm evening, cool dawn, 4 s smoothstep fades (snap on map change). Warm flickering candles / torches / map lamps / spell objects.
// On the desktop this is D3D11 only; here it needs EXT_color_buffer_float (else the Classic lights draw, like D3D9).
// Not ported: MLibrary.TryGetLightTint (the spell-sprite colour tint of EffectLightColour / spell objects; their lights stay white
// unless the effect has an explicit LightColour).

import type { WebGl2Renderer, GLTextureRef, RenderTargetRef } from "../render/backend.ts";
import { renderTargetSourceRect } from "../render/batcher.ts";
import type { SpriteBatch } from "../render/batcher.ts";
import { ChatType, LightSetting, ObjectType } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { MAP_LIBS, sprite } from "./libraries.ts";
import { CELL_W, CELL_H } from "./objects.ts";
import { Missile, type Effect } from "./effects.ts";
import { gameSettings, setSetting } from "./settings.ts";
import { LIGHT_SIZES, getBlindLight, type LightContext, type Rgb } from "./lighting.ts";

const AMBIENT_FADE_MS = 4000;
const FLICKER_AMPLITUDE = 0.10, STRONG_FLICKER_AMPLITUDE = 0.16;
const WHITE: Rgb = [255, 255, 255];
const WHITE_COLOUR = { r: 1, g: 1, b: 1, a: 1 };
const sameRgb = (a: Rgb, b: Rgb) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];

let availableCache = false;

/** DXManager.EnhancedLightingAvailable, as last seen by an EnhancedLightLayer (false before one exists). */
export function enhancedLightingAvailable(): boolean {
  return availableCache;
}

/** MapControl.EnhancedAmbient: the classic darkness colours, with a moonlit night and tinted dawn/evening. */
function enhancedAmbient(setting: number, mapDarkLight: number): Rgb {
  switch (setting) {
    case LightSetting.Night:
      switch (mapDarkLight) {
        case 1: return [20, 20, 20];
        case 2: return [119, 136, 153]; // LightSlateGray
        case 3: return [135, 206, 235]; // SkyBlue
        case 4: return [218, 165, 32];  // Goldenrod
        default: return [6, 8, 16];
      }
    case LightSetting.Evening: return [62, 44, 34];
    case LightSetting.Dawn: return [44, 46, 64];
    default: return WHITE;
  }
}

/** MapControl.Flicker: smooth noise in -1..1 from three detuned sines; the seed de-phases neighbouring lights. */
function flicker(seed: number, now: number): number {
  const t = (Math.trunc(now) % 3600000) * 0.001;
  const s = (seed & 0xffff) * 0.6180339;
  return Math.sin(t * 7.3 + s * 11) * 0.5 + Math.sin(t * 13.1 + s * 23) * 0.3 + Math.sin(t * 2.9 + s * 5) * 0.2;
}

export class EnhancedLightLayer {
  private readonly renderer: WebGl2Renderer;
  /** DXManager.EnhancedLightingAvailable. */
  readonly available: boolean;
  private white: GLTextureRef | null = null; // D3D11Backend._white: Light_PS reads only the UVs and the vertex colour
  private target: RenderTargetRef | null = null;
  private targetSize = "";
  private ambientFrom: Rgb = WHITE;
  private ambientTo: Rgb = WHITE;
  private ambientFadeStart = -Infinity;
  private ambientMap: MapCells | null = null;

  constructor(renderer: WebGl2Renderer) {
    this.renderer = renderer;
    this.available = renderer.supportsEnhancedLighting();
    availableCache = this.available;
  }

  /** MapControl.AmbientFading. */
  fading(now: number): boolean {
    return now < this.ambientFadeStart + AMBIENT_FADE_MS;
  }

  /** MapControl.UpdateAmbient: a target change starts a fade from the current colour; a new map snaps. */
  updateAmbient(setting: number, mapDarkLight: number, map: MapCells, now: number): void {
    const target = enhancedAmbient(setting, mapDarkLight);
    if (this.ambientMap !== map) {
      this.ambientMap = map;
      this.ambientFrom = this.ambientTo = target;
      this.ambientFadeStart = -Infinity;
      return;
    }
    if (sameRgb(target, this.ambientTo)) return;
    this.ambientFrom = this.currentAmbient(now);
    this.ambientTo = target;
    this.ambientFadeStart = now;
  }

  /** MapControl.CurrentAmbient (smoothstep). */
  private currentAmbient(now: number): Rgb {
    const elapsed = now - this.ambientFadeStart;
    if (elapsed >= AMBIENT_FADE_MS) return this.ambientTo;
    if (elapsed <= 0) return this.ambientFrom;
    let t = elapsed / AMBIENT_FADE_MS;
    t = t * t * (3 - 2 * t);
    const f = this.ambientFrom, to = this.ambientTo;
    return [Math.trunc(f[0] + (to[0] - f[0]) * t), Math.trunc(f[1] + (to[1] - f[1]) * t), Math.trunc(f[2] + (to[2] - f[2]) * t)];
  }

  /** MapControl.DrawEnhancedLights, then rebinds `restore` (null = canvas). Call after updateAmbient. */
  draw(batch: SpriteBatch, c: LightContext, blinded: boolean, restore: RenderTargetRef | null): void {
    const r = this.renderer;
    const { width, height, now } = c;
    const size = `${width}x${height}`;
    if (!this.target || this.targetSize !== size) {
      r.releaseRenderTarget(this.target);
      this.target = r.createRenderTarget(width, height, "RGBA16F"); // DXManager.CreateLightTarget
      this.targetSize = size;
      if (!this.target) return;
    }
    if (!this.white) this.white = r.createRgbaTexture(new Uint8Array([255, 255, 255, 255]), 1, 1);
    const blindLight = (colour: Rgb) => (blinded ? getBlindLight(colour, now) : colour);

    batch.Flush();
    r.bindRenderTarget(this.target);
    const ambient = blindLight(this.currentAmbient(now));
    r.clearColour(ambient[0], ambient[1], ambient[2], 255);

    // Lights fade out with the darkness: full strength from dawn/evening brightness down, none in daylight.
    const ambientLevel = Math.max(ambient[0], ambient[1], ambient[2]) / 255;
    const lightScale = Math.min(Math.max((1 - ambientLevel) / (1 - 64 / 255), 0), 1);

    if (lightScale > 0) {
      r.setShaderMode("Light");
      r.setEnhancedLightAdditiveBlend(); // SetBlend(true) + SetEnhancedLightAdditiveBlend
      this.drawObjectLights(batch, c, lightScale, blindLight);
      this.drawMapEffectLights(batch, c, lightScale, blindLight);
      this.drawCellLights(batch, c, lightScale, blindLight);
      batch.Flush();
      r.setShaderMode("Default"); // EndEnhancedLightBlend + SetBlend(false)
      r.setBlend(false, 1, "NORMAL");
    }

    r.bindRenderTarget(restore);
    r.setShaderMode("LightComposite");
    r.setEnhancedLightCompositeBlend();
    batch.DrawScaled(this.target.texture, renderTargetSourceRect(width, height), 0, 0, width, height, WHITE_COLOUR);
    batch.Flush();
    r.setShaderMode("Default");
    r.setBlend(false, 1, "NORMAL"); // RestartSpriteAlphaBlend
  }

  /** MapControl.DrawEnhancedObjectLights (+ the SpellObjects, Race Spell on the desktop). */
  private drawObjectLights(batch: SpriteBatch, c: LightContext, lightScale: number, blindLight: (c: Rgb) => Rgb): void {
    const effects = gameSettings.Effect;
    for (const ob of c.objects) {
      if (ob.light > 0 && (!ob.dead || ob === c.user)) {
        const light = ob.light;
        let colour: Rgb = WHITE; // ob.LightColour
        let intensity = 1;
        let flickerSeed = 0;
        if (ob.race === ObjectType.Player) {
          switch (Math.trunc(light / 15)) {
            case 0: colour = [60, 60, 60]; break; // no light source
            case 1: colour = [120, 120, 120]; break;
            case 2: colour = [230, 190, 140]; intensity = 1.15; flickerSeed = ob.id; break; // Candle
            case 3: colour = [255, 205, 150]; intensity = 1.3; flickerSeed = ob.id; break; // Torch
            default: colour = [255, 215, 165]; intensity = 1.4; flickerSeed = ob.id; break; // Peddler Torch
          }
        } else if (ob.race === ObjectType.Merchant) colour = [120, 120, 120];
        const p = c.drawLocation(ob);
        this.addLight(batch, c.now, p.x, p.y, light % 15, blindLight(colour), intensity * lightScale, flickerSeed, FLICKER_AMPLITUDE, 0);
      }
      if (!effects) continue;
      for (const e of ob.effects) {
        if (!e.blend || c.now < e.start || e.held || (!(e instanceof Missile) && e.light < ob.light)) continue;
        this.addLight(batch, c.now, e.drawLocation.x, e.drawLocation.y, e.light, blindLight(effectLightColour(e)), 1.2 * lightScale, 0, 0, 0);
      }
    }
    for (const so of c.spellObjects) {
      if (so.light <= 0) continue;
      // tint from the spell's sprite frame not ported (white); intensity 1.2, strong flicker
      this.addLight(batch, c.now, so.drawLocation.x, so.drawLocation.y, so.light % 15, blindLight(WHITE), 1.2 * lightScale, so.id ?? 1, STRONG_FLICKER_AMPLITUDE, 0);
    }
  }

  /** MapControl.DrawEnhancedMapEffectLights. */
  private drawMapEffectLights(batch: SpriteBatch, c: LightContext, lightScale: number, blindLight: (c: Rgb) => Rgb): void {
    if (!gameSettings.Effect) return;
    for (const e of c.mapEffects) {
      if (!e.blend || c.now < e.start || e.held || e.light === 0) continue;
      this.addLight(batch, c.now, e.drawLocation.x, e.drawLocation.y, e.light, blindLight(effectLightColour(e)), 1.2 * lightScale, 0, 0, 0);
    }
  }

  /** MapControl.DrawEnhancedCellLights: warm flickering lamps (Light < 10, so the classic colour bands never apply). */
  private drawCellLights(batch: SpriteBatch, c: LightContext, lightScale: number, blindLight: (c: Rgb) => Rgb): void {
    const { map, user } = c;
    for (let y = user.movement.Y - c.viewRangeY - 24; y <= user.movement.Y + c.viewRangeY + 24; y++) {
      if (y < 0) continue;
      if (y >= map.height) break;
      for (let x = user.movement.X - c.viewRangeX - 24; x < user.movement.X + c.viewRangeX + 24; x++) {
        if (x < 0) continue;
        if (x >= map.width) break;
        const i = x * map.height + y;
        const imageIndex = (map.frontImage[i] & 0x7fff) - 1;
        if (imageIndex === -1) continue;
        const fileIndex = map.frontIndex[i];
        if (fileIndex === -1) continue;
        const cellLight = map.light[i];
        if (cellLight === 0 || cellLight >= 10) continue;
        let px = (x + c.offSetX - user.movement.X) * CELL_W + user.offsetMove.X;
        let py = (y + c.offSetY - user.movement.Y) * CELL_H + user.offsetMove.Y + 32;
        if (map.frontAnimationFrame[i] > 0) {
          const s = sprite(MAP_LIBS[fileIndex] ?? null, imageIndex);
          if (s) { px += s.x; py += s.y; }
        }
        this.addLight(batch, c.now, px, py, (cellLight % 10) * 3, blindLight([255, 236, 210]), 1.15 * lightScale, x * 7919 + y * 104729 + 1, FLICKER_AMPLITUDE, 10);
      }
    }
  }

  /** MapControl.AddEnhancedLight: the classic rectangle (offset by LightSizes[range], size LightSizes[range + 1]); flickerSeed 0 = steady.
   *  The quad is D3D11Backend.DrawLight: UVs 0..1, colour rgb, alpha = intensity / 4. */
  private addLight(batch: SpriteBatch, now: number, lx: number, ly: number, range: number, colour: Rgb, intensity: number,
    flickerSeed: number, amplitude: number, extraX: number): void {
    if (intensity <= 0 || !this.white) return;
    const last = LIGHT_SIZES.length - 2;
    if (range < 0) range = 0;
    if (range > last) range = last;
    const [ox, oy] = LIGHT_SIZES[range]!;
    const [sw, sh] = LIGHT_SIZES[range + 1]!;
    let x = lx - Math.trunc(ox / 2) - CELL_W / 2 + extraX;
    let y = ly - Math.trunc(oy / 2) - CELL_H / 2 - 5;
    let w = sw, h = sh;
    if (flickerSeed !== 0) {
      const n = flicker(flickerSeed, now);
      intensity *= 1 + amplitude * n;
      const grow = 1 + 0.03 * n; // the flame breathes a little too
      const cx = x + w / 2, cy = y + h / 2;
      w *= grow;
      h *= grow;
      x = cx - w / 2;
      y = cy - h / 2;
    }
    batch.DrawScaled(this.white, { x: 0, y: 0, width: 1, height: 1 }, x, y, w, h,
      { r: colour[0] / 255, g: colour[1] / 255, b: colour[2] / 255, a: Math.min(1, Math.max(0, intensity * 0.25)) });
  }
}

/** MapControl.EffectLightColour without the sprite tint (MLibrary.TryGetLightTint is not ported): the effect's LightColour. */
function effectLightColour(e: Effect): Rgb {
  return e.lightColour;
}

/** ChatDialog.TryLightingCommand: the client-only `@light new` / `@light old` chat command (never sent to the server). Returns true when
 *  `msg` was the command (the caller then clears the chat box and does not send it). `chat` shows a hint line (ChatType.Hint). */
export function tryLightingCommand(msg: string, chat: (text: string, type: number) => void): boolean {
  const parts = msg.split(" ").filter((s) => s.length > 0);
  if (parts.length === 0 || parts[0]!.toUpperCase() !== "@LIGHT") return false;
  const arg = parts.length > 1 ? parts[1]!.toUpperCase() : "";
  if (arg === "NEW") {
    if (!enhancedLightingAvailable()) {
      chat("Enhanced lighting needs WebGL2 half-float render targets (EXT_color_buffer_float).", ChatType.Hint); // LightingEnhancedUnavailable
      return true;
    }
    setSetting("Lighting", "Enhanced");
  } else if (arg === "OLD") {
    setSetting("Lighting", "Classic");
  } else {
    chat("Use @light new (enhanced) or @light old (classic).", ChatType.Hint); // LightingUsage
    return true;
  }
  chat(gameSettings.Lighting === "Enhanced" ? "[Lighting: Enhanced]" : "[Lighting: Classic]", ChatType.Hint); // LightingEnhanced / LightingClassic
  return true;
}
