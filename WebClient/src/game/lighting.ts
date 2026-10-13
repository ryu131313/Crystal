// Lighting: port of MapControl.DrawLights (Classic, Client/MirScenes/GameScene.cs), GetBlindLight (blindness), the
// MapControl.CreateTexture / DrawControl light + grayscale sequence, and the switch to the Enhanced mode (lightingEnhanced.ts,
// MapControl.Lighting.cs) by gameSettings.Lighting.
// Classic: a screen-size light map is cleared to the darkness colour, the light textures are added on it
// (DXManager.SetLightAdditiveBlend = SourceAlpha, One) for objects, effects and lit map cells, then it multiplies the scene
// (SetLightMultiplyBlend = Zero, SourceColor). The textures are the GDI+ gradients of DXManager.CreateLights, pre-rendered
// to play/lights/light<k>.png by tools/gen-lights.ps1.
// Quirk mirrored: DXManager.Lights[k] has the size LightSizes[k + 1] (CreateLights starts at 1) but the draw offsets use LightSizes[k].
// Grayscale (DXManager.SetGrayscale while User.Dead, MapControl.DrawControl): the desktop draws its ControlTexture through
// Grayscale_PS; the web has no ControlTexture, so while dead the scene renders into a target that is then drawn grayscale.
// The web redraws every frame, so the desktop's NeedsTimedRebuild reasons ("blindness", "light-fade", "light-flicker") need no port.

import type { WebGl2Renderer, GLTextureRef, RenderTargetRef } from "../render/backend.ts";
import { renderTargetSourceRect } from "../render/batcher.ts";
import type { SpriteBatch } from "../render/batcher.ts";
import { LightSetting, ObjectType, PoisonType } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { MAP_LIBS, sprite } from "./libraries.ts";
import { CELL_W, CELL_H, type MapObj } from "./objects.ts";
import { Missile, type Effect } from "./effects.ts";
import { gameSettings } from "./settings.ts";
import { EnhancedLightLayer } from "./lightingEnhanced.ts";
import { webUrl } from "../assets/root.ts";

/** DXManager.LightSizes. */
export const LIGHT_SIZES: [number, number][] = [
  [125, 95], [205, 156], [285, 217], [365, 277], [445, 338], [525, 399], [605, 460], [685, 521], [765, 581], [845, 642], [925, 703],
];
const LIGHT_COUNT = LIGHT_SIZES.length - 1; // DXManager.Lights.Count

export type Rgb = [number, number, number];
const WHITE_RGB: Rgb = [255, 255, 255];
const WHITE = { r: 1, g: 1, b: 1, a: 1 };
const rgba = (c: Rgb): { r: number; g: number; b: number; a: number } => ({ r: c[0] / 255, g: c[1] / 255, b: c[2] / 255, a: 1 });

export interface LightState {
  /** MapInformation.Lights / MapChanged.Lights. */
  mapLights: number;
  /** S.TimeOfDay (GameScene.Lights). */
  timeOfDay: number;
  mapDarkLight: number;
}

/** MapControl: `Lights == LightSetting.Normal ? GameScene.Scene.Lights : Lights`. */
export function effectiveSetting(s: LightState): number {
  return s.mapLights === LightSetting.Normal ? s.timeOfDay : s.mapLights;
}

/** A light-carrying object that is not a MapObj: the SpellObjects (Race Spell on the desktop, lit even when "dead"). */
export interface LightSource {
  /** ObjectID: the Enhanced flicker seed. */
  readonly id?: number;
  light: number;
  drawLocation: { x: number; y: number };
}

/** Everything one frame's light pass reads. */
export interface LightContext {
  width: number;
  height: number;
  setting: number;
  mapDarkLight: number;
  map: MapCells;
  user: MapObj;
  objects: Iterable<MapObj>;
  spellObjects: Iterable<LightSource>;
  mapEffects: Effect[];
  drawLocation: (ob: MapObj) => { x: number; y: number };
  offSetX: number;
  offSetY: number;
  viewRangeX: number;
  viewRangeY: number;
  /** CMain.Time (the page clock, performance.now ms). */
  now: number;
}

// — Blindness: MapObject.User.BlindCount / BlindTime —

const blind = { count: 0, time: 0, active: false };

/** True while the user has PoisonType.Blindness. Resets the ramp when Blindness ends (GameScene.Poisoned ~L4199: BlindCount = 0);
 *  called once a frame, so the transition is seen whatever packet changed the poison. */
export function trackBlindness(poison: number): boolean {
  const on = (poison & PoisonType.Blindness) !== 0;
  if (blind.active && !on) blind.count = 0;
  blind.active = on;
  return on;
}

/** MapControl.GetBlindLight: every call may advance the ramp (one step per 100 ms, up to 25); each channel drops 10 a step, floor 20. */
export function getBlindLight(c: Rgb, now: number): Rgb {
  if (blind.time <= now && blind.count < 25) {
    blind.time = now + 100;
    blind.count++;
  }
  const d = blind.count * 10;
  return [Math.max(20, c[0] - d), Math.max(20, c[1] - d), Math.max(20, c[2] - d)];
}

// — Classic —

/** MapControl.DrawLights' darkness switch. */
function darkness(setting: number, mapDarkLight: number): Rgb {
  switch (setting) {
    case LightSetting.Night:
      switch (mapDarkLight) {
        case 1: return [20, 20, 20];
        case 2: return [119, 136, 153]; // LightSlateGray
        case 3: return [135, 206, 235]; // SkyBlue
        case 4: return [218, 165, 32];  // Goldenrod
        default: return [0, 0, 0];
      }
    case LightSetting.Evening:
    case LightSetting.Dawn:
      return [50, 50, 50];
    default:
      return [255, 255, 255];
  }
}

/** The classic light map (MapControl.DrawLights). */
export class LightLayer {
  private readonly renderer: WebGl2Renderer;
  private readonly textures: (GLTextureRef | null)[] = new Array(LIGHT_COUNT).fill(null);
  private target: RenderTargetRef | null = null;
  private targetSize = "";

  constructor(renderer: WebGl2Renderer) {
    this.renderer = renderer;
    for (let k = 0; k < LIGHT_COUNT; k++) void this.load(k);
  }

  private async load(k: number): Promise<void> {
    try {
      const res = await fetch(webUrl(`/play/lights/light${k}.png`));
      const bitmap = await createImageBitmap(await res.blob(), { premultiplyAlpha: "none", colorSpaceConversion: "none" });
      const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
      const g = canvas.getContext("2d")!;
      g.drawImage(bitmap, 0, 0);
      const data = g.getImageData(0, 0, bitmap.width, bitmap.height).data;
      this.textures[k] = this.renderer.createRgbaTexture(new Uint8Array(data.buffer), bitmap.width, bitmap.height);
    } catch (e) {
      console.warn(`[lights] light${k}.png:`, e);
    }
  }

  /** MapControl.DrawLights: draws the light map over the scene, then rebinds `restore` (null = canvas). The batch must be in a frame; it is
   *  flushed around the target switch. */
  draw(batch: SpriteBatch, c: LightContext, blinded: boolean, restore: RenderTargetRef | null): void {
    const r = this.renderer;
    const { width, height, user, map, now } = c;
    const size = `${width}x${height}`;
    if (!this.target || this.targetSize !== size) {
      r.releaseRenderTarget(this.target);
      this.target = r.createRenderTarget(width, height);
      this.targetSize = size;
      if (!this.target) return;
    }
    const tint = (colour: Rgb) => rgba(blinded ? getBlindLight(colour, now) : colour);

    batch.Flush();
    r.bindRenderTarget(this.target);
    const [dr, dg, db] = blinded ? getBlindLight(darkness(c.setting, c.mapDarkLight), now) : darkness(c.setting, c.mapDarkLight);
    r.clearColour(dr, dg, db, 255);
    r.setBlend(true, 1, "COLORFY"); // SetBlend(true) + SetLightAdditiveBlend: SourceAlpha, One

    const drawLight = (k: number, x: number, y: number, colour: { r: number; g: number; b: number; a: number }, extraX = 0) => {
      const tex = this.textures[k];
      if (!tex) return;
      const [lw, lh] = LIGHT_SIZES[k]!;
      batch.Draw(tex, null, x - Math.trunc(lw / 2) - CELL_W / 2 + extraX, y - Math.trunc(lh / 2) - CELL_H / 2 - 5, colour);
    };
    const effects = gameSettings.Effect; // Settings.Effect gates the effect lights

    // Object lights (player / monster / NPC) and each object's effect lights
    for (const ob of c.objects) {
      if (ob.light > 0 && (!ob.dead || ob === user)) {
        let range = ob.light % 15;
        if (range >= LIGHT_COUNT) range = LIGHT_COUNT - 1;
        let colour = WHITE_RGB; // MapObject.LightColour default
        if (ob.race === ObjectType.Player) {
          const v = [60, 120, 180, 240][Math.trunc(ob.light / 15)] ?? 255;
          colour = [v, v, v];
        } else if (ob.race === ObjectType.Merchant) colour = [120, 120, 120];
        const p = c.drawLocation(ob);
        drawLight(range, p.x, p.y, tint(colour));
      }
      if (!effects) continue;
      // Object effect lights: blended effects that have started; non-missiles only when brighter than the owner
      for (const e of ob.effects) {
        if (!e.blend || now < e.start || (!(e instanceof Missile) && e.light < ob.light)) continue;
        const k = Math.min(Math.max(e.light, 0), LIGHT_COUNT - 1);
        drawLight(k, e.drawLocation.x, e.drawLocation.y, tint(e.lightColour));
      }
    }
    // SpellObject lights (Race Spell: lit whatever Dead says; LightColour stays White)
    for (const so of c.spellObjects) {
      if (so.light <= 0) continue;
      drawLight(Math.min(so.light % 15, LIGHT_COUNT - 1), so.drawLocation.x, so.drawLocation.y, tint(WHITE_RGB));
    }
    // Map effect lights (white)
    if (effects) {
      for (const e of c.mapEffects) {
        if (!e.blend || now < e.start || e.light === 0) continue;
        drawLight(Math.min(Math.max(e.light, 0), LIGHT_COUNT - 1), e.drawLocation.x, e.drawLocation.y, tint(WHITE_RGB));
      }
    }

    // Map lights: lit cells around the view
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
        if (cellLight <= 0 || cellLight >= 10) continue;
        let k = (cellLight % 10) * 3;
        const colour: Rgb = ([[255, 255, 255], [255, 255, 255], [120, 180, 255], [255, 180, 120], [22, 160, 5]] as Rgb[])[Math.trunc(cellLight / 10)] ?? WHITE_RGB;
        let px = (x + c.offSetX - user.movement.X) * CELL_W + user.offsetMove.X;
        let py = (y + c.offSetY - user.movement.Y) * CELL_H + user.offsetMove.Y + 32;
        if (map.frontAnimationFrame[i] > 0) {
          const s = sprite(MAP_LIBS[fileIndex] ?? null, imageIndex);
          if (s) { px += s.x; py += s.y; }
        }
        if (k >= LIGHT_COUNT) k = LIGHT_COUNT - 1;
        drawLight(k, px, py, tint(colour), 10);
      }
    }

    batch.Flush();
    r.bindRenderTarget(restore);
    r.setLightModulateState(); // SetLightMultiplyBlend: Zero, SourceColor
    batch.DrawScaled(this.target.texture, renderTargetSourceRect(width, height), 0, 0, width, height, WHITE);
    batch.Flush();
    r.setBlend(false, 1, "NORMAL"); // RestartSpriteAlphaBlend
  }
}

/** The per-frame light + grayscale sequence of MapControl.CreateTexture / DrawControl. One per MapView. */
export class SceneLights {
  private readonly renderer: WebGl2Renderer;
  private readonly classic: LightLayer;
  private readonly enhanced: EnhancedLightLayer;
  private scene: RenderTargetRef | null = null;
  private sceneSize = "";
  private grayscale = false;

  constructor(renderer: WebGl2Renderer) {
    this.renderer = renderer;
    this.classic = new LightLayer(renderer);
    this.enhanced = new EnhancedLightLayer(renderer);
  }

  /** MapControl.UseEnhancedLighting: Enhanced is selected and the renderer can draw it (EXT_color_buffer_float + the Light programs). */
  get useEnhanced(): boolean {
    return gameSettings.Lighting === "Enhanced" && this.enhanced.available;
  }

  /** DXManager.EnhancedLightingAvailable (the @light new check). */
  get enhancedAvailable(): boolean {
    return this.enhanced.available;
  }

  /** Call right after the frame's begin(): while the user is dead (MapControl.DrawControl: `if (User.Dead) SetGrayscale(true)`) the scene renders
   *  into a target that `endScene` draws grayscale. No-op when alive or when the Grayscale program is missing (the desktop's "no grayscale.ps"). */
  beginScene(batch: SpriteBatch, width: number, height: number, userDead: boolean): void {
    this.grayscale = false;
    const r = this.renderer;
    if (!userDead || !r.programAvailable("Grayscale")) return;
    const size = `${width}x${height}`;
    if (!this.scene || this.sceneSize !== size) {
      r.releaseRenderTarget(this.scene);
      this.scene = r.createRenderTarget(width, height);
      this.sceneSize = size;
      if (!this.scene) return;
    }
    batch.Flush();
    r.bindRenderTarget(this.scene);
    r.clearColour(0, 0, 0, 255); // MapControl.CreateTexture: Clear(BackColour = Black)
    this.grayscale = true;
  }

  /** MapControl.CreateTexture's light step. Classic: DrawLights unless it is Day (blindness forces it). Enhanced: UpdateAmbient every frame,
   *  DrawEnhancedLights unless it is Day with no fade running (blindness forces it). Returns true when it changed GL blend state. */
  draw(batch: SpriteBatch, c: LightContext): boolean {
    const blinded = trackBlindness(c.user.poison);
    const restore = this.grayscale ? this.scene : null;
    if (this.useEnhanced) {
      this.enhanced.updateAmbient(c.setting, c.mapDarkLight, c.map, c.now);
      if (c.setting === LightSetting.Day && !this.enhanced.fading(c.now) && !blinded) return false;
      this.enhanced.draw(batch, c, blinded, restore);
      return true;
    }
    if (c.setting === LightSetting.Day && !blinded) return false;
    this.classic.draw(batch, c, blinded, restore);
    return true;
  }

  /** Call before the frame's end(): draws the dead user's scene target grayscale (DXManager.DrawOpaque of the ControlTexture under
   *  SetGrayscale(true)). Returns true when it changed GL blend state. */
  endScene(batch: SpriteBatch, width: number, height: number): boolean {
    if (!this.grayscale || !this.scene) return false;
    this.grayscale = false;
    const r = this.renderer;
    batch.Flush();
    r.fillTargetAlpha(); // DrawOpaque: the target's alpha is whatever the blends left; force 1 so Normal blending copies it
    r.bindRenderTarget(null);
    r.setBlend(false, 1, "NORMAL");
    r.setShaderMode("Grayscale");
    batch.DrawScaled(this.scene.texture, renderTargetSourceRect(width, height), 0, 0, width, height, WHITE);
    batch.Flush();
    r.setShaderMode("Default"); // SetGrayscale(false)
    return true;
  }
}
