// The game view: port of MapControl's DrawFloor + DrawObjects (Client/MirScenes/GameScene.cs) over the lifted WebGL2
// sprite batch. Layer order per frame: floor (back/middle/front flat tiles) -> dead objects -> per row: tile animation,
// middle objects, front objects, then the row's live objects -> the user again at 40 % (see-through walls).
// Text (names, HP bars, chat) is drawn on a 2D overlay canvas.
// Players: PlayerObject.Draw incl. the mount (under the body) and the transform 19 wings.
// Lights, blindness and grayscale (dead user, paralysed bodies): lighting.ts. Not ported yet: the floor render-target cache.

import { SpriteBatch } from "../render/batcher.ts";
import type { WebGl2Renderer } from "../render/backend.ts";
import { MirAction as A, MirDirection as D, ObjectType } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";
import { MAP_LIBS, LibPath, sprite, monsterLibPath, npcLibPath } from "./libraries.ts";
import { CELL_W, CELL_H, type MapObj } from "./objects.ts";
import { SceneLights, effectiveSetting, type LightSource, type LightState } from "./lighting.ts";
import { poisonColour } from "./buffs.ts";
import type { Effect } from "./effects.ts";
import type { Doors } from "./doors.ts";

const WHITE = { r: 1, g: 1, b: 1, a: 1 };
type BlendKind = "normal" | "add" | "opacity";

export class Painter {
  readonly batch: SpriteBatch;
  private readonly renderer: WebGl2Renderer;
  private blend: BlendKind = "normal";
  private rate = 1;
  width = 1024;
  height = 768;

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

  /** Width/height of an image, undefined while it loads. */
  size(lib: string | null, index: number): { w: number; h: number; x: number; y: number } | null | undefined {
    const s = sprite(lib, index);
    return s ? { w: s.width, h: s.height, x: s.x, y: s.y } : s;
  }

  /** MLibrary.Draw(index, x, y) / Draw(index, point, colour, offSet). */
  draw(lib: string | null, index: number, x: number, y: number, offset = false, opacity = 1, colour: { r: number; g: number; b: number; a: number } = WHITE): void {
    const s = sprite(lib, index);
    if (!s || !s.tex) return;
    if (offset) { x += s.x; y += s.y; }
    if (x >= this.width || y >= this.height || x + s.width < 0 || y + s.height < 0) return;
    this.setBlend(opacity < 1 ? "opacity" : "normal", opacity);
    this.batch.Draw(s.tex, null, x, y, colour);
  }

  /** MLibrary.DrawBlend: additive (SourceAlpha, One) at `rate`, tinted by `colour` (weather particles). */
  drawBlend(lib: string | null, index: number, x: number, y: number, offset = false, rate = 1, colour: { r: number; g: number; b: number; a: number } = WHITE): void {
    const s = sprite(lib, index);
    if (!s || !s.tex) return;
    if (offset) { x += s.x; y += s.y; }
    if (x >= this.width || y >= this.height || x + s.width < 0 || y + s.height < 0) return;
    this.setBlend("add", rate);
    this.batch.Draw(s.tex, null, x, y, colour);
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
}

/** MapObject.ApplyDrawColour: a DrawColour of Color.Gray (Paralysis / LRParalysis, unless an earlier poison colour wins) draws grayscale. */
const isGrayscalePoison = (poison: number): boolean => {
  const c = poisonColour(poison);
  return c !== null && c.r === 128 / 255 && c.g === c.r && c.b === c.r;
};

const isCellSize = (s: { w: number; h: number }) => (s.w === CELL_W && s.h === CELL_H) || (s.w === CELL_W * 2 && s.h === CELL_H * 2);

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
  weather: { draw(painter: Painter): void } | null = null;
  offSetX = 0; offSetY = 0; viewRangeX = 0; viewRangeY = 0;

  constructor(renderer: WebGl2Renderer, width: number, height: number) {
    this.painter = new Painter(renderer);
    this.lights = new SceneLights(renderer);
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
    p.begin();
    this.lights.beginScene(p.batch, p.width, p.height, v.user.dead); // MapControl.DrawControl: SetGrayscale(true) while User.Dead
    this.drawFloor(v);
    this.drawObjects(v);
    this.weather?.draw(p); // MapControl.CreateTexture: "render weather" after DrawObjects, before the lights
    // MapControl.CreateTexture: Classic DrawLights / Enhanced DrawEnhancedLights (lighting.ts SceneLights.draw decides, incl. blindness)
    if (this.lights.draw(p.batch, {
      width: p.width, height: p.height, setting: effectiveSetting(v.lights), mapDarkLight: v.lights.mapDarkLight, map: v.map, user: v.user,
      objects: v.objects, spellObjects: this.spellObjects.values(), mapEffects: v.mapEffects, drawLocation: (ob) => this.drawLocation(ob, v.user),
      offSetX: this.offSetX, offSetY: this.offSetY, viewRangeX: this.viewRangeX, viewRangeY: this.viewRangeY, now: v.now,
    })) p.invalidateBlend();
    if (this.lights.endScene(p.batch, p.width, p.height)) p.invalidateBlend();
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
          p.draw(lib, index, drawX, drawY - CELL_H, true);
        } else {
          p.draw(lib, index, drawX, drawY - s.h);
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
    if (!user.dead) this.drawPlayer(user, user, 0.4, true);

    // Then the other map effects, then every object's own effects (MapObject.DrawEffects)
    for (let i = v.mapEffects.length - 1; i >= 0; i--) if (!v.mapEffects[i]!.drawBehind) v.mapEffects[i]!.draw();
    for (const ob of v.objects) {
      for (const e of ob.effects) if (!e.drawBehind) e.draw();
      if (ob.race === ObjectType.Player) this.actionEffects(ob);
    }
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
      if (gray) this.painter.setGrayscale(true);
      if (ob.frame?.blend) this.painter.drawBlend(lib, ob.drawFrame, x, y, true);
      else this.painter.draw(lib, ob.drawFrame, x, y, true, ob.hidden ? 0.5 : 1, ob.race === ObjectType.Monster && !gray ? poisonColour(ob.poison) ?? undefined : undefined);
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
    const L = ob.playerLibraries();
    const f = ob.drawFrame;
    const p = this.painter;
    const d = ob.direction;
    const weapon = () => { if (ob.look.weapon >= 0 && L.weapon1) p.draw(L.weapon1, f + L.weaponOff, x, y, true, opacity); };
    const weapon2 = () => { if (ob.look.weapon !== -1 && L.weapon2) p.draw(L.weapon2, f + L.weaponOff, x, y, true, opacity); };
    // PlayerObject.DrawBody uses DrawColour (the poison tint); weapon and head use it too
    const tint = poisonColour(ob.poison) ?? undefined;
    // PlayerObject.DrawBody: ApplyDrawColour turns the Gray (paralysis) tint into SetGrayscale(true) around the body only
    const gray = isGrayscalePoison(ob.poison);
    const body = () => {
      if (!L.body) return;
      if (gray) p.setGrayscale(true);
      p.draw(L.body, f + L.armourOff, x, y, true, opacity, gray ? undefined : tint);
      if (gray) p.setGrayscale(false);
    };
    const head = () => { if (L.hair) p.draw(L.hair, f + L.hairOff, x, y, true, opacity); };
    // PlayerObject.DrawMount: DrawFrame - 416 + MountOffset (the Mount* frames start at 416)
    const mount = () => { if (ob.mountType >= 0 && ob.ridingMount && L.mount) p.draw(L.mount, f + L.mountOff, x, y, true, opacity, tint); };
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

/** Mouse hit test: the object whose cell is under the cursor (front-most first). */
export function objectAt(objects: Iterable<MapObj>, cell: { X: number; Y: number }, user: MapObj): MapObj | null {
  let best: MapObj | null = null;
  for (const ob of objects) {
    if (ob === user || ob.race === ObjectType.Deco || ob.location.X !== cell.X || ob.location.Y !== cell.Y) continue; // DecoObject.MouseOver: never
    const rank = (o: MapObj) => (o.race === ObjectType.Item ? 0 : o.dead ? 1 : 2);
    if (!best || rank(ob) > rank(best)) best = ob;
  }
  return best;
}

export { A as MirActionIds };
