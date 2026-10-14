// The game view: port of MapControl's DrawFloor + DrawObjects (Client/MirScenes/GameScene.cs) over the lifted WebGL2
// sprite batch. Layer order per frame: floor (back/middle/front flat tiles) -> dead objects -> per row: tile animation,
// middle objects, front objects, then the row's live objects -> the user again at 40 % (see-through walls).
// Text (names, HP bars, chat) is drawn on a 2D overlay canvas.
// Players: PlayerObject.Draw incl. the mount (under the body) and the transform 19 wings.
// Lights, blindness and grayscale (dead user, paralysed bodies): lighting.ts. Not ported yet: the floor render-target cache.

import { SpriteBatch } from "../render/batcher.ts";
import type { WebGl2Renderer } from "../render/backend.ts";
import { LightSetting, MirAction as A, MirDirection as D, ObjectType } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { MAP_LIBS, LibPath, sprite, monsterLibPath, npcLibPath, visiblePixel } from "./libraries.ts";
import { CELL_W, CELL_H, type MapObj, type PlayerLibraries } from "./objects.ts";
import { SceneLights, effectiveSetting, type LightSource, type LightState } from "./lighting.ts";
import { poisonColour } from "./buffs.ts";
import type { Effect } from "./effects.ts";
import type { Doors } from "./doors.ts";
import { Atmosphere, type EmberSource, type GlowDraw } from "./atmosphere.ts";
import { gameSettings } from "./settings.ts";

const WHITE = { r: 1, g: 1, b: 1, a: 1 };
type Colour = { r: number; g: number; b: number; a: number };
type BlendKind = "normal" | "add" | "opacity";

/** Web smooth shadows tuning (shaders.ts SHADOW_FILL_PS), mutable like lightingEnhanced.ts lightTuning; the scene lab (play/lab.ts) drives it.
 *  The defaults are the shipped look. `oneShadow`: a player's weapon / hair layers drop their shadow, so only the body and mount cast one.
 *  `mapObjects`: the front layer (trees, walls, buildings) gets the fill too, not just object bodies. */
export const shadowTuning = { colour: [0, 0, 0] as [number, number, number], strength: 0.4, soft: true, oneShadow: false, mapObjects: true };
/** A body layer's shadow role in Painter.draw: true = casts the fill, "layer" = a player's weapon / hair (drops it under oneShadow). */
type ShadowRole = boolean | "layer";

export class Painter {
  readonly batch: SpriteBatch;
  private readonly renderer: WebGl2Renderer;
  private blend: BlendKind = "normal";
  private rate = 1;
  /** Web atmosphere: while set, every glowing additive draw is also recorded here for the glow pass (atmosphere.ts), and every normal draw that
   *  overlaps an earlier glow is recorded as an occluder. */
  glowCapture: GlowDraw[] | null = null;
  /** Bounds of this frame's glow draws so far (occluders outside them are not recorded). */
  private glowMinX = Infinity; private glowMinY = Infinity; private glowMaxX = -Infinity; private glowMaxY = -Infinity;
  width = 1024;
  height = 768;
  /** The ShadowFill uniforms last set (shadowFillParams), "" = not yet. */
  private shadowKey = "";

  constructor(renderer: WebGl2Renderer) {
    this.renderer = renderer;
    this.batch = new SpriteBatch(renderer);
  }

  private setBlend(kind: BlendKind, rate = 1): void {
    if (kind === this.blend && rate === this.rate) return;
    this.batch.Flush();
    if (kind === "normal") this.renderer.setBlend(false, 1, "NORMAL");
    else if (kind === "add") this.renderer.setBlend(true, rate, "COLORFY"); // Crystal SetBlend: SourceAlpha, One
    else this.renderer.setOpacity(rate);
    this.blend = kind;
    this.rate = rate;
  }

  begin(): void {
    this.renderer.setViewport(this.width, this.height);
    this.renderer.clear();
    this.batch.Reset();
    this.batch.Begin();
    this.batch.AlphaTest = true; // D3D11Backend SpriteBegin: alpha test Greater 0 (else opacity draws darken through transparent texels)
    this.blend = "add"; // force the first setBlend to bind
    this.setBlend("normal");
    this.glowMinX = this.glowMinY = Infinity;
    this.glowMaxX = this.glowMaxY = -Infinity;
  }

  /** Someone else changed GL blend state (the light layer): rebind on the next draw. */
  invalidateBlend(): void {
    this.blend = "invalid" as BlendKind;
  }

  end(): void {
    this.batch.End();
    this.renderer.submit();
  }

  /** DXManager.SetGrayscale(value): the Grayscale program (luma of the texel, vertex colour ignored) until switched back. */
  setGrayscale(on: boolean): void {
    this.batch.Flush();
    this.renderer.setShaderMode(on ? "Grayscale" : "Default");
  }

  /** Web smooth shadows: switches between Default and ShadowFill only (never over Grayscale or a light program), so consecutive body draws stay one
   *  run. Every draw that is not a body calls it with false; MapView.drawObjects ends with false before the atmosphere draws through the batch. */
  shadowFill(on: boolean): void {
    const mode = this.renderer.shaderMode;
    if (on ? mode !== "Default" : mode !== "ShadowFill") return;
    this.batch.Flush();
    this.renderer.setShaderMode(on ? "ShadowFill" : "Default");
  }

  /** The ShadowFill uniforms for the next body draw, from shadowTuning; flushes only when they change. */
  private shadowFillParams(drop: boolean): void {
    const t = shadowTuning, [r, g, b] = t.colour;
    const key = `${r},${g},${b},${t.strength},${t.soft},${drop}`;
    if (key === this.shadowKey) return;
    this.batch.Flush();
    this.renderer.setShadowFill(r, g, b, t.strength, t.soft, drop);
    this.shadowKey = key;
  }

  /** Width/height of an image, undefined while it loads. */
  size(lib: string | null, index: number): { w: number; h: number; x: number; y: number } | null | undefined {
    const s = sprite(lib, index);
    return s ? { w: s.width, h: s.height, x: s.x, y: s.y } : s;
  }

  /** MLibrary.Draw(index, x, y) / Draw(index, point, colour, offSet). `shadow`: an object body layer, drawn with smooth shadows when the option is on
   *  (the ShadowFill alpha must reach the blend, so its opacity rides in the vertex colour instead of the OpacityFactor blend; same result on opaque texels). */
  draw(lib: string | null, index: number, x: number, y: number, offset = false, opacity = 1, colour: { r: number; g: number; b: number; a: number } = WHITE, shadow: ShadowRole = false): void {
    const s = sprite(lib, index);
    if (!s || !s.tex) return;
    if (offset) { x += s.x; y += s.y; }
    if (x >= this.width || y >= this.height || x + s.width < 0 || y + s.height < 0) return;
    const fill = shadow !== false && gameSettings.SmoothShadows && this.renderer.programAvailable("ShadowFill");
    if (fill) this.shadowFillParams(shadow === "layer" && shadowTuning.oneShadow);
    this.shadowFill(fill);
    if (fill && opacity < 1) {
      this.setBlend("normal");
      colour = { r: colour.r, g: colour.g, b: colour.b, a: colour.a * opacity };
    } else this.setBlend(opacity < 1 ? "opacity" : "normal", opacity);
    this.batch.Draw(s.tex, null, x, y, colour);
    if (this.glowCapture && x < this.glowMaxX && y < this.glowMaxY && x + s.width > this.glowMinX && y + s.height > this.glowMinY)
      this.glowCapture.push({ tex: s.tex, x, y, w: s.width, h: s.height, colour: { r: 0, g: 0, b: 0, a: Math.min(opacity, colour.a) }, rate: 0 });
  }

  /** MLibrary.DrawBlend: additive (SourceAlpha, One) at `rate`, tinted by `colour` (weather particles). `glow` = false keeps it out of the
   *  atmosphere's glow (snow, rain, plain leaves). */
  drawBlend(lib: string | null, index: number, x: number, y: number, offset = false, rate = 1, colour: { r: number; g: number; b: number; a: number } = WHITE, glow = true): void {
    const s = sprite(lib, index);
    if (!s || !s.tex) return;
    if (offset) { x += s.x; y += s.y; }
    if (x >= this.width || y >= this.height || x + s.width < 0 || y + s.height < 0) return;
    this.shadowFill(false);
    this.setBlend("add", rate);
    this.batch.Draw(s.tex, null, x, y, colour);
    if (glow && this.glowCapture) {
      this.glowCapture.push({ tex: s.tex, x, y, w: s.width, h: s.height, colour, rate: Math.max(rate, 1e-3) });
      if (x < this.glowMinX) this.glowMinX = x;
      if (y < this.glowMinY) this.glowMinY = y;
      if (x + s.width > this.glowMaxX) this.glowMaxX = x + s.width;
      if (y + s.height > this.glowMaxY) this.glowMaxY = y + s.height;
    }
  }

  /** The hover outline's ring (from the WoOOLcraft web client's hoverOutline.ts, a port addition): the image drawn at
   *  every OUTLINE_OFFSETS offset as a flat `colour` silhouette (ColorOp 1 keeps alpha, replaces colour; AlphaRef drops
   *  the baked shadow and the faint rim), so the sprite drawn next on top leaves only the ring. */
  drawSilhouette(lib: string | null, index: number, x: number, y: number, colour: Colour): void {
    const s = sprite(lib, index);
    if (!s || !s.tex) return;
    x += s.x; y += s.y;
    if (x >= this.width || y >= this.height || x + s.width < 0 || y + s.height < 0) return;
    this.shadowFill(false);
    this.setBlend("normal");
    this.batch.ColorOp = 1;
    this.batch.AlphaRef = OUTLINE_ALPHA_REF;
    for (const o of OUTLINE_OFFSETS) this.batch.Draw(s.tex, null, x + o.x, y + o.y, colour);
    this.batch.ColorOp = 0;
    this.batch.AlphaRef = 0;
  }

  /** MLibrary.DrawUp: bottom-anchored. */
  drawUp(lib: string | null, index: number, x: number, y: number, blend = false): void {
    const s = sprite(lib, index);
    if (!s || !s.tex) return;
    if (blend) this.drawBlend(lib, index, x, y - s.height);
    else this.draw(lib, index, x, y - s.height);
  }
}

export interface ViewState {
  map: MapCells;
  user: MapObj;
  objects: Iterable<MapObj>;
  animationCount: number;
  lights: LightState;
  /** MapControl.Effects. */
  mapEffects: Effect[];
  doors: Doors;
  now: number;
  /** Scene lab (play/lab.ts): `user` is only the camera (and its light), never drawn. */
  hideUser?: boolean;
}

/** MapObject.ApplyDrawColour: a DrawColour of Color.Gray (Paralysis / LRParalysis, unless an earlier poison colour wins) draws grayscale. */
const isGrayscalePoison = (poison: number): boolean => {
  const c = poisonColour(poison);
  return c !== null && c.r === 128 / 255 && c.g === c.r && c.b === c.r;
};

const isCellSize = (s: { w: number; h: number }) => (s.w === CELL_W && s.h === CELL_H) || (s.w === CELL_W * 2 && s.h === CELL_H * 2);

/** Hover outline (WoOOLcraft hoverOutline.ts): opaque red on a hovered live monster, DeepSkyBlue on a hovered NPC and
 *  always on a party or guild member. */
export const OUTLINE_RED: Colour = { r: 1, g: 0, b: 0, a: 1 };
export const OUTLINE_BLUE: Colour = { r: 0, g: 191 / 255, b: 1, a: 1 };
/** Silhouette texels at or below this alpha are dropped (WoOOLcraft measured 0.7: below it the ring traced shadows). */
const OUTLINE_ALPHA_REF = 0.7;
/** Every offset of a disc of radius 2 except the centre (a 2 px ring). */
const OUTLINE_OFFSETS: readonly { x: number; y: number }[] = (() => {
  const out: { x: number; y: number }[] = [];
  for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) if ((dx || dy) && dx * dx + dy * dy <= 5) out.push({ x: dx, y: dy });
  return out;
})();

/** One drawn library of an object and the offset added to its DrawFrame. */
type Layer = readonly [path: string | null, offset: number];

/** PlayerObject.Draw's frame layers (what drawPlayer draws at DrawFrame + offset; wings use the effect frame). */
function playerLayers(ob: MapObj, L: PlayerLibraries): Layer[] {
  const layers: Layer[] = [[L.body, L.armourOff], [L.hair, L.hairOff]];
  if (!ob.ridingMount && ob.look.weapon >= 0) layers.push([L.weapon1, L.weaponOff]);
  if (!ob.ridingMount && ob.look.weapon !== -1) layers.push([L.weapon2, L.weaponOff]);
  if (ob.mountType >= 0 && ob.ridingMount) layers.push([L.mount, L.mountOff]);
  return layers;
}

export class MapView {
  readonly painter: Painter;
  /** Lights (Classic / Enhanced by gameSettings.Lighting), blindness and the dead-user grayscale (lighting.ts). */
  readonly lights: SceneLights;
  /** PlayerObject.DrawEffects' per-action attack visuals (spellVisuals.drawActionEffects), set by game.ts. */
  actionEffects: (ob: MapObj) => void = () => {};
  /** NPCObject.QuestIcon: the Prguse image to draw above an NPC (quests.ts questIconImage), or null; set by game.ts. */
  npcQuestImage: (ob: MapObj) => number | null = () => null;
  /** SpellObjects (FireWall etc.), set by game.ts. */
  spellObjects: ReadonlyMap<number, { draw(): void } & LightSource> = new Map();
  /** GameScene.ParticleEngines (weather.ts Weather), drawn after the objects and before the lights; set by game.ts. */
  weather: ({ draw(painter: Painter, skipFog?: boolean, skipRain?: boolean): void; readonly setting: number } & EmberSource) | null = null;
  /** Web-only glow, flashes, lit particles and heat haze over Enhanced lighting (atmosphere.ts). */
  readonly atmosphere: Atmosphere;
  offSetX = 0; offSetY = 0; viewRangeX = 0; viewRangeY = 0;
  /** Frame blocks (first image + count + layers) whose every image is resident: they play normally. */
  private readonly settledBlocks = new Set<string>();
  /** The last frame each object drew in full (and the player libraries it drew with): the hold while a block loads. */
  private readonly lastGood = new WeakMap<MapObj, { f: number; L: PlayerLibraries | null }>();
  /** The outline colour for an object this frame, or null (game.ts: hover, party, guild; WoOOLcraft hoverOutline.ts). */
  outline: (ob: MapObj) => Colour | null = () => null;
  /** Render count, so `bodies` entries from an earlier frame (object out of view) are ignored. */
  private frameNo = 0;
  /** The Enhanced light pass ran last frame (its light map is then valid for this frame's fog). */
  private lightMapFresh = false;
  /** Each object's body as last drawn (BodyLibrary, DrawFrame, DrawLocation): what MouseOver tests. */
  private readonly bodies = new WeakMap<MapObj, { lib: string | null; f: number; x: number; y: number; frame: number }>();

  constructor(renderer: WebGl2Renderer, width: number, height: number) {
    this.painter = new Painter(renderer);
    this.lights = new SceneLights(renderer);
    this.atmosphere = new Atmosphere(renderer);
    this.resize(width, height);
  }

  resize(width: number, height: number): void {
    this.painter.width = width;
    this.painter.height = height;
    this.offSetX = Math.trunc(width / 2 / CELL_W);
    this.offSetY = Math.trunc(height / 2 / CELL_H) - 1;
    this.viewRangeX = this.offSetX + 6;
    this.viewRangeY = this.offSetY + 6;
  }

  /** MapObject DrawLocation (no library offset): user-relative, minus the object's own OffSetMove unless it is the user. */
  drawLocation(ob: MapObj, user: MapObj): { x: number; y: number } {
    let x = (ob.movement.X - user.movement.X + this.offSetX) * CELL_W;
    let y = (ob.movement.Y - user.movement.Y + this.offSetY) * CELL_H;
    if (ob !== user) {
      x += user.offsetMove.X - ob.offsetMove.X;
      y += user.offsetMove.Y - ob.offsetMove.Y;
    }
    return { x, y };
  }

  /** ItemObject.Process: centred in its cell (DNItems frame size), moving with the user's OffSetMove only. Undefined while the frame loads. */
  itemRect(ob: MapObj, user: MapObj): { x: number; y: number; w: number; h: number } | undefined {
    const s = this.painter.size(LibPath.floorItems, ob.image);
    if (!s) return undefined;
    const { x, y } = this.drawLocation(ob, user);
    return { x: x + Math.trunc((CELL_W - s.w) / 2), y: y + Math.trunc((CELL_H - s.h) / 2), w: s.w, h: s.h };
  }

  /** MapControl.MapLocation: the cell under a canvas pixel. */
  mapLocation(px: number, py: number, user: MapObj): { X: number; Y: number } {
    return { X: Math.trunc(px / CELL_W) - this.offSetX + user.location.X, Y: Math.trunc(py / CELL_H) - this.offSetY + user.location.Y };
  }

  render(v: ViewState): void {
    const p = this.painter;
    // game.ts passes objects.values(), a ONE-SHOT iterator: materialise it once so every pass (objects, effects, lights) sees all objects
    if (!Array.isArray(v.objects)) v = { ...v, objects: [...v.objects] };
    this.frameNo++;
    p.begin();
    const atmos = this.atmosphere;
    // web atmosphere: the scene renders into its target (alive, Enhanced, option on); otherwise the desktop path, incl. the dead grayscale
    if (atmos.begin(p.batch, p.width, p.height, this.lights.useEnhanced, v.user.dead)) p.glowCapture = atmos.glowDraws;
    else this.lights.beginScene(p.batch, p.width, p.height, v.user.dead); // MapControl.DrawControl: SetGrayscale(true) while User.Dead
    atmos.setView(v.user, this.offSetX, this.offSetY, v.map, this.weather?.setting ?? 0, v.lights.mapLights === LightSetting.Night, v.objects as MapObj[], v.now);
    const lightMap = this.lightMapFresh ? this.lights.enhancedLightMap : null;
    this.drawFloor(v);
    if (atmos.drawGround(p.batch, p.width, p.height, lightMap)) p.invalidateBlend(); // web: snow cover, footprints, wet ground, ground mist
    this.drawObjects(v);
    this.weather?.draw(p, atmos.shaderFog, atmos.shaderRain); // MapControl.CreateTexture: "render weather" after DrawObjects, before the lights
    if (atmos.drawSky(p.batch, p.width, p.height, lightMap)) p.invalidateBlend(); // web: snowfall and the high fog layer
    p.glowCapture = null;
    const drawLocation = (ob: MapObj) => this.drawLocation(ob, v.user);
    atmos.collect({
      user: v.user, objects: v.objects as MapObj[], mapEffects: v.mapEffects, spellObjects: this.spellObjects.values(), offSetX: this.offSetX,
      offSetY: this.offSetY, drawLocation, body: (ob) => this.bodyOf(ob), embers: this.weather, now: v.now,
    });
    // MapControl.CreateTexture: Classic DrawLights / Enhanced DrawEnhancedLights (lighting.ts SceneLights.draw decides, incl. blindness)
    const lit = this.lights.draw(p.batch, {
      width: p.width, height: p.height, setting: effectiveSetting(v.lights), mapDarkLight: v.lights.mapDarkLight, map: v.map, user: v.user,
      objects: v.objects, spellObjects: this.spellObjects.values(), mapEffects: v.mapEffects, drawLocation,
      offSetX: this.offSetX, offSetY: this.offSetY, viewRangeX: this.viewRangeX, viewRangeY: this.viewRangeY, now: v.now,
      extraLights: atmos.lights, screenFlash: atmos.screenFlash, forceLightPass: atmos.running && atmos.forcesLightPass,
    }, atmos.sceneTarget);
    if (lit) p.invalidateBlend();
    this.lightMapFresh = lit && this.lights.useEnhanced;
    const enhancedLit = lit && this.lights.useEnhanced;
    if (atmos.finish(p.batch, enhancedLit ? this.lights.enhancedLightMap : null, enhancedLit ? this.lights.enhancedReflections : [])) p.invalidateBlend();
    else if (this.lights.endScene(p.batch, p.width, p.height)) p.invalidateBlend();
    p.end();
  }

  private drawFloor(v: ViewState): void {
    const { map, user } = v;
    const p = this.painter;
    const startX = user.movement.X - this.viewRangeX, endX = user.movement.X + this.viewRangeX;
    const startY = user.movement.Y - this.viewRangeY, endY = user.movement.Y + this.viewRangeY;
    for (let y = startY; y <= endY + 5; y++) {
      if (y <= 0) continue;
      if (y >= map.height) break;
      const drawY = (y - user.movement.Y + this.offSetY) * CELL_H + user.offsetMove.Y;
      for (let x = startX; x <= endX; x++) {
        if (x < 0) continue;
        if (x >= map.width) break;
        const drawX = (x - user.movement.X + this.offSetX) * CELL_W - this.offSetX + user.offsetMove.X;
        const i = x * map.height + y;

        if (y % 2 === 0 && x % 2 === 0 && y <= endY) {
          const back = map.backImage[i], bi = map.backIndex[i];
          if (back !== 0 && bi !== -1) p.draw(MAP_LIBS[bi], (back & 0x1fffffff) - 1, drawX, drawY);
        }

        const mid = map.middleImage[i] - 1, mi = map.middleIndex[i];
        if (mid >= 0 && mi !== -1) {
          const s = p.size(MAP_LIBS[mi], mid);
          if (s && isCellSize(s)) p.draw(MAP_LIBS[mi], mid, drawX, drawY);
        }

        let front = (map.frontImage[i] & 0x7fff) - 1;
        const fi = map.frontIndex[i];
        if (front !== -1 && fi !== -1 && fi !== 200) {
          const s = p.size(MAP_LIBS[fi], front); // size of the closed frame (DrawFloor reads it before the door offset)
          if (map.doorIndex[i] > 0) front += v.doors.frameOffset(map.doorIndex[i], map.doorOffset[i], v.now);
          if (front >= 0 && s && isCellSize(s)) p.draw(MAP_LIBS[fi], front, drawX, drawY);
        }
      }
    }
  }

  private drawObjects(v: ViewState): void {
    const { map, user, animationCount } = v;
    const p = this.painter;
    const startY = user.movement.Y - this.viewRangeY, endY = user.movement.Y + this.viewRangeY + 25;
    const startX = user.movement.X - this.viewRangeX, endX = user.movement.X + this.viewRangeX;

    // Bucket objects by sort row/column (CellInfo.CellObjects; items/spells first, then dead, then by ObjectID).
    const rows = new Map<number, MapObj[]>();
    const dead: MapObj[] = [];
    for (const ob of v.objects) {
      if (ob.location.X < startX || ob.location.X > endX || ob.drawY < startY || ob.drawY > endY) continue;
      if (v.hideUser && ob === user) continue;
      if (ob.dead) { dead.push(ob); continue; }
      let r = rows.get(ob.drawY);
      if (!r) rows.set(ob.drawY, (r = []));
      r.push(ob);
    }
    // MapControl.DrawObjects: map effects that draw behind come first
    for (let i = v.mapEffects.length - 1; i >= 0; i--) if (v.mapEffects[i]!.drawBehind) v.mapEffects[i]!.draw();
    // Spell objects lie on the ground (CellInfo.Sort puts them before live objects; drawn here, ahead of all rows)
    for (const so of this.spellObjects.values()) so.draw();
    dead.sort((a, b) => a.drawY - b.drawY || a.location.X - b.location.X || a.id - b.id);
    for (const ob of dead) this.drawObject(ob, user);

    for (let y = startY; y <= endY; y++) {
      if (y <= 0) continue;
      if (y >= map.height) break;
      const drawY = (y - user.movement.Y + this.offSetY + 1) * CELL_H + user.offsetMove.Y;
      for (let x = startX; x <= endX; x++) {
        if (x < 0) continue;
        if (x >= map.width) break;
        const drawX = (x - user.movement.X + this.offSetX) * CELL_W - this.offSetX + user.offsetMove.X;
        const i = x * map.height + y;

        // Shanda tile animation layer
        let index = map.tileAnimationImage[i];
        let animation = map.tileAnimationFrames[i];
        if (index > 0 && animation > 0) {
          index--;
          const animOffset = map.tileAnimationOffset[i] ^ 0x2000;
          index += animOffset * (animationCount % animation);
          p.drawUp(MAP_LIBS[190], index, drawX, drawY);
        }

        // Mir3 middle layer
        const mi = map.middleIndex[i];
        if (mi >= 0) {
          index = map.middleImage[i] - 1;
          if (index > 0) {
            animation = map.middleAnimationFrame[i];
            let blend = false;
            if (animation > 0 && animation < 255) {
              if ((animation & 0x0f) > 0) { blend = true; animation &= 0x0f; }
              if (animation > 0) {
                const tick = map.middleAnimationTick[i];
                index += Math.trunc((animationCount % (animation + animation * tick)) / (1 + tick));
                p.drawUp(MAP_LIBS[mi], index, drawX, drawY, blend && (animation === 10 || animation === 8));
              }
            }
            const s = p.size(MAP_LIBS[mi], index);
            if (s && !isCellSize(s) && !blend) p.drawUp(MAP_LIBS[mi], index, drawX, drawY);
          }
        }

        // Front layer
        index = (map.frontImage[i] & 0x7fff) - 1;
        const fileIndex = map.frontIndex[i];
        if (index < 0 || fileIndex === -1) continue;
        animation = map.frontAnimationFrame[i];
        let blend = false;
        if ((animation & 0x80) > 0) { blend = true; animation &= 0x7f; }
        if (animation > 0) {
          const tick = map.frontAnimationTick[i];
          index += Math.trunc((animationCount % (animation + animation * tick)) / (1 + tick));
        }
        if (map.doorIndex[i] > 0) index += v.doors.frameOffset(map.doorIndex[i], map.doorOffset[i], v.now);
        const lib = MAP_LIBS[fileIndex];
        const s = p.size(lib, index);
        if (!s) continue;
        if (animation === 0 && isCellSize(s)) continue;
        if (blend) {
          if (fileIndex === 14 || fileIndex === 27 || (fileIndex > 99 && fileIndex < 199)) p.drawBlend(lib, index, drawX, drawY - 3 * CELL_H, true);
          else p.drawBlend(lib, index, drawX, drawY - s.h, index >= 2723 && index <= 2732);
        } else if (fileIndex === 28 && (s.x !== 0 || s.y !== 0)) {
          p.draw(lib, index, drawX, drawY - CELL_H, true, 1, WHITE, shadowTuning.mapObjects);
        } else {
          p.draw(lib, index, drawX, drawY - s.h, false, 1, WHITE, shadowTuning.mapObjects);
        }
      }

      const row = rows.get(y);
      if (row) {
        // CellInfo.Sort: items first in a cell, then by ObjectID
        row.sort((a, b) => a.location.X - b.location.X || Number(b.race === ObjectType.Item) - Number(a.race === ObjectType.Item) || a.id - b.id);
        for (const ob of row) this.drawObject(ob, user);
      }
    }

    // MapControl.DrawObjects tail: the user's body and head again at 40 % so walls never hide them.
    if (!user.dead && !v.hideUser) this.drawPlayer(user, user, 0.4, true);

    // Then the other map effects, then every object's own effects (MapObject.DrawEffects)
    for (let i = v.mapEffects.length - 1; i >= 0; i--) if (!v.mapEffects[i]!.drawBehind) v.mapEffects[i]!.draw();
    for (const ob of v.objects) {
      for (const e of ob.effects) if (!e.drawBehind) e.draw();
      if (ob.race === ObjectType.Player) this.actionEffects(ob);
    }
    p.shadowFill(false); // web smooth shadows: the atmosphere and weather passes draw through the batch with the Default program
  }

  /** The frame to draw while an animation block streams in (the WoOOLcraft web client's hold; the desktop loads
   *  synchronously): the current frame once every image of the block (this direction, every layer) is resident, else
   *  the block's first frame, else -1 (the caller keeps the last frame it drew). Asking kicks the block's loads. A dead
   *  body shows its current frame when ready (the first frame of Die is still upright). */
  private heldFrame(ob: MapObj, layers: readonly Layer[]): number {
    const f = ob.drawFrame;
    const fr = ob.frame;
    if (!fr) return f;
    const first = f - ob.frameIndex;
    let key = first + ":" + fr.count;
    for (const [path, off] of layers) if (path !== null) key += "|" + path + "@" + off;
    if (this.settledBlocks.has(key)) return f;
    const ready = (n: number) => {
      for (const [path, off] of layers) if (path !== null && sprite(path, n + off) === undefined) return false;
      return true;
    };
    let all = true;
    for (let i = 0; i < fr.count; i++) if (!ready(first + i)) all = false; // no early exit: every frame's load is kicked
    if (all) { this.settledBlocks.add(key); return f; }
    if (ob.action === A.Dead) return ready(f) ? f : -1;
    return ready(first) ? first : -1;
  }

  /** The body image `ob` drew this frame (BodyLibrary, DrawFrame), or null (not drawn this frame). */
  bodyOf(ob: MapObj): { lib: string | null; f: number } | null {
    const b = this.bodies.get(ob);
    return b && b.frame === this.frameNo ? b : null;
  }

  /** MonsterObject / NPCObject / PlayerObject.MouseOver: the mouse is on the object's cell, or within 2 px of a visible
   *  pixel of its body as last drawn (BodyLibrary.VisiblePixel(DrawFrame, p - FinalDrawLocation, false)). Items: the
   *  cell only (ItemObject.MouseOver); Deco: never. */
  mouseOver(ob: MapObj, cell: { X: number; Y: number }, px: number, py: number): boolean {
    if (ob.race === ObjectType.Deco) return false;
    if (ob.location.X === cell.X && ob.location.Y === cell.Y) return true;
    if (ob.race === ObjectType.Item) return false;
    const b = this.bodies.get(ob);
    if (!b || b.frame !== this.frameNo || b.lib === null) return false;
    const s = sprite(b.lib, b.f);
    if (!s) return false;
    const lx = Math.trunc(px) - (b.x + s.x), ly = Math.trunc(py) - (b.y + s.y);
    for (let dx = -2; dx <= 2; dx++) for (let dy = -2; dy <= 2; dy++) if (visiblePixel(s, lx + dx, ly + dy)) return true;
    return false;
  }

  /** MapControl.CheckMouseOver -> MapObject.MouseObject: the cells 2 around the mouse cell, bottom-right first, each
   *  cell's objects last-sorted first (CellInfo order: items, then the dead, then by ObjectID), the first whose
   *  MouseOver holds; never the user; the dead only with Settings.TargetDead. */
  mouseObject(px: number, py: number, user: MapObj, objects: Iterable<MapObj>, targetDead: boolean): MapObj | null {
    const cell = this.mapLocation(px, py, user);
    const near = new Map<number, MapObj[]>();
    for (const ob of objects) {
      if (ob === user || Math.abs(ob.location.X - cell.X) > 2 || Math.abs(ob.location.Y - cell.Y) > 2) continue;
      const k = (ob.location.Y - cell.Y + 2) * 5 + (ob.location.X - cell.X + 2);
      let list = near.get(k);
      if (!list) near.set(k, (list = []));
      list.push(ob);
    }
    if (!near.size) return null;
    const rank = (o: MapObj) => (o.race === ObjectType.Item ? 0 : o.dead ? 1 : 2);
    for (let y = 4; y >= 0; y--) {
      for (let x = 4; x >= 0; x--) {
        const list = near.get(y * 5 + x);
        if (!list) continue;
        list.sort((a, b) => rank(a) - rank(b) || a.id - b.id);
        for (let i = list.length - 1; i >= 0; i--) {
          const ob = list[i]!;
          if (!this.mouseOver(ob, cell, px, py)) continue;
          if (ob.dead && !targetDead) continue;
          return ob;
        }
      }
    }
    return null;
  }

  drawObject(ob: MapObj, user: MapObj): void {
    for (const e of ob.effects) if (e.drawBehind) e.draw(); // DrawBehindEffects
    if (ob.race === ObjectType.Item) {
      const r = this.itemRect(ob, user);
      if (r) this.painter.draw(LibPath.floorItems, ob.image, r.x, r.y);
      return;
    }
    if (ob.race === ObjectType.Deco) {
      // DecoObject.Draw: Deco.Lib image at the cell, no frames
      const { x, y } = this.drawLocation(ob, user);
      this.painter.draw("Deco", ob.image, x, y, true);
      return;
    }
    if (ob.race === ObjectType.Player) this.drawPlayer(ob, user, ob.hidden ? 0.5 : 1, false);
    else {
      const { x, y } = this.drawLocation(ob, user);
      // MonsterObject.Load "Library" switch / NPCObject.LoadLibrary (null = no body: Draw returns early)
      const lib = ob.race === ObjectType.Monster ? monsterLibPath(ob.image) : npcLibPath(ob.image);
      const gray = ob.race === ObjectType.Monster && isGrayscalePoison(ob.poison); // MonsterObject.Draw: ApplyDrawColour
      let f = this.heldFrame(ob, [[lib, 0]]);
      if (f >= 0) this.lastGood.set(ob, { f, L: null });
      else f = this.lastGood.get(ob)?.f ?? ob.drawFrame;
      this.bodies.set(ob, { lib, f, x, y, frame: this.frameNo });
      const outline = ob.hidden ? null : this.outline(ob);
      if (outline) this.painter.drawSilhouette(lib, f, x, y, outline);
      if (gray) this.painter.setGrayscale(true);
      if (ob.frame?.blend) this.painter.drawBlend(lib, f, x, y, true);
      else this.painter.draw(lib, f, x, y, true, ob.hidden ? 0.5 : 1, ob.race === ObjectType.Monster && !gray ? poisonColour(ob.poison) ?? undefined : undefined, true);
      if (gray) this.painter.setGrayscale(false);
      if (ob.race === ObjectType.Merchant) {
        // NPCObject.Draw: the quest icon at DrawLocation + GetOffSet(BaseIndex 0) + (Size.Width / 2 - 28, -40), no image offset
        const image = this.npcQuestImage(ob);
        const base = image === null ? null : sprite(lib, 0);
        if (image !== null && base) this.painter.draw(LibPath.prguse, image, x + base.x + Math.trunc(base.width / 2) - 28, y + base.y - 40, false);
      }
    }
  }

  /** PlayerObject.Draw: mount, weapon / body / head (+ wings for others) / weapon by direction, no weapons while riding;
   *  `ghost` = MapControl.DrawObjects' 40 % user redraw (body, head + wings; the mount is commented out there). */
  private drawPlayer(ob: MapObj, user: MapObj, opacity: number, ghost: boolean): void {
    const { x, y } = this.drawLocation(ob, user);
    let L = ob.playerLibraries();
    let f = this.heldFrame(ob, playerLayers(ob, L));
    if (f >= 0) this.lastGood.set(ob, { f, L });
    else {
      const last = this.lastGood.get(ob);
      if (last?.L) { f = last.f; L = last.L; } else f = ob.drawFrame;
    }
    const p = this.painter;
    if (!ghost) {
      this.bodies.set(ob, { lib: L.body, f: f + L.armourOff, x, y, frame: this.frameNo });
      // the outline: every layer's silhouette first, so the layers drawn on top cover the whole inside
      const outline = ob === user || ob.hidden ? null : this.outline(ob);
      if (outline) for (const [path, off] of playerLayers(ob, L)) if (path !== null) p.drawSilhouette(path, f + off, x, y, outline);
    }
    const d = ob.direction;
    const weapon = () => { if (ob.look.weapon >= 0 && L.weapon1) p.draw(L.weapon1, f + L.weaponOff, x, y, true, opacity, undefined, "layer"); };
    const weapon2 = () => { if (ob.look.weapon !== -1 && L.weapon2) p.draw(L.weapon2, f + L.weaponOff, x, y, true, opacity, undefined, "layer"); };
    // PlayerObject.DrawBody uses DrawColour (the poison tint); weapon and head use it too
    const tint = poisonColour(ob.poison) ?? undefined;
    // PlayerObject.DrawBody: ApplyDrawColour turns the Gray (paralysis) tint into SetGrayscale(true) around the body only
    const gray = isGrayscalePoison(ob.poison);
    const body = () => {
      if (!L.body) return;
      if (gray) p.setGrayscale(true);
      p.draw(L.body, f + L.armourOff, x, y, true, opacity, gray ? undefined : tint, true);
      if (gray) p.setGrayscale(false);
    };
    const head = () => { if (L.hair) p.draw(L.hair, f + L.hairOff, x, y, true, opacity, undefined, "layer"); };
    // PlayerObject.DrawMount: DrawFrame - 416 + MountOffset (the Mount* frames start at 416)
    const mount = () => { if (ob.mountType >= 0 && ob.ridingMount && L.mount) p.draw(L.mount, f + L.mountOff, x, y, true, opacity, tint, true); };
    // PlayerObject.DrawWings: DrawBlend at DrawWingFrame + WingOffset (only the transform 19 wings are ported)
    const wings = () => { if (L.wing) p.drawBlend(L.wing, ob.drawEffectFrame + L.wingOff, x, y, true, 1); };
    const headFirst = d === D.Up || d === D.UpLeft || d === D.UpRight || d === D.Right || d === D.Left;
    if (ghost) {
      body();
      if (headFirst) { head(); wings(); } else { wings(); head(); }
      return;
    }
    mount();
    if (!ob.ridingMount) { if (d === D.Left || d === D.Up || d === D.UpLeft || d === D.DownLeft) weapon(); else weapon2(); }
    body();
    if (headFirst) { head(); if (ob !== user) wings(); } else { if (ob !== user) wings(); head(); }
    if (!ob.ridingMount) { if (d === D.UpRight || d === D.Right || d === D.DownRight || d === D.Down) weapon(); else weapon2(); }
  }
}

export { A as MirActionIds };
