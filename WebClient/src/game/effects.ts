// Effects: port of Client/MirObjects/Effect.cs (Effect, Missile) plus GameScene.Direction16. An effect plays
// `count` frames of `lib` from `baseIndex` over `duration` ms, either on an owner object (follows its DrawLocation,
// drawn from its effect list) or at a map point (drawn from the map's effect list). Blend = additive (DrawBlend).
// All times are the page clock (performance.now ms), standing in for CMain.Time.
// Web: an effect holds its first frame (its clock does not run, its light is off) until every image it will draw is on
// the GPU, at most HOLD_MAX_MS; the desktop loads synchronously, so a spell never plays half-loaded there. The art
// warm-up (preload.ts) usually has them resident already; this covers the rest (first casts, server effects).

import type { WirePoint } from "../protocol/binaryCodec.ts";
import { CELL_W, CELL_H, type MapObj } from "./objects.ts";
import { MirAction, Monster } from "../protocol/enums.generated.ts";
import { LibPath, sprite } from "./libraries.ts";

/** Data\*.Lib names for Libraries.Magic / Magic2 / ... (Client/MirGraphics/MLibrary.cs). */
export const Lib = {
  Magic: "Magic", Magic2: "Magic2", Magic3: "Magic3", MagicC: "MagicC", Effect: "Effect", Effect2: "Effect2",
  Prguse: "Prguse", Prguse2: "Prguse2", GuildSkill: "GuildSkill", Weather: "Weather", Dragon: "Dragon",
  Monster: (n: number) => `Monster/${String(n).padStart(3, "0")}`,
  CHumEffect: (n: number) => `CHumEffect/${String(n).padStart(2, "0")}`,
} as const;

/** The parts of the game an effect needs (set once by game.ts). */
export interface EffectWorld {
  now(): number;
  user(): MapObj | null;
  /** Owner DrawLocation (MapObject.DrawLocation, no library offset). */
  drawLocation(ob: MapObj): { x: number; y: number };
  offSetX: number;
  offSetY: number;
  draw(lib: string, index: number, x: number, y: number, offset: boolean, opacity?: number): void;
  drawBlend(lib: string, index: number, x: number, y: number, offset: boolean, rate: number): void;
  mapEffects: Effect[];
}

let world: EffectWorld | null = null;
export function setEffectWorld(w: EffectWorld): void {
  world = w;
}
export function effectWorld(): EffectWorld {
  if (!world) throw new Error("effect world not set");
  return world;
}

const idiv = (a: number, b: number) => Math.trunc(a / b);

/** The longest an effect waits for its images before playing anyway (a slow or failing read must not freeze a spell). */
const HOLD_MAX_MS = 3000;

export class Effect {
  lib: string;
  baseIndex: number;
  count: number;
  duration: number;
  start: number;
  currentFrame = 0;
  nextFrame: number;
  source: WirePoint;
  owner: MapObj | null;
  light = 6;
  /** MapObject.LightColour-style tint for the light layer, 0..255 RGB. */
  lightColour: [number, number, number] = [255, 255, 255];
  blend = true;
  rate = 1;
  drawOffset = { X: 0, Y: 0 };
  repeat = false;
  repeatUntil = 0;
  drawBehind = false;
  delay = 0;
  drawLocation = { x: 0, y: 0 };
  private readonly completeHandlers: (() => void)[] = [];
  private readonly playedHandlers: (() => void)[] = [];
  removed = false;
  /** Web: the frames have been checked resident (or the hold timed out); see the file header. */
  private playing = false;
  private holdUntil = 0;

  /** new Effect(library, baseIndex, count, duration, owner | source, starttime = 0, drawBehind = false). */
  constructor(lib: string, baseIndex: number, count: number, duration: number, at: MapObj | WirePoint, startTime = 0, drawBehind = false) {
    this.lib = lib;
    this.baseIndex = baseIndex;
    this.count = count === 0 ? 1 : count;
    this.duration = duration;
    this.start = startTime === 0 ? effectWorld().now() : startTime;
    this.nextFrame = this.start + idiv(this.duration, this.count) * (this.currentFrame + 1);
    if ("X" in at) { this.owner = null; this.source = { ...at }; }
    else { this.owner = at; this.source = { ...at.location }; }
    this.drawBehind = drawBehind;
  }

  /** Effect.Complete += ... */
  onComplete(fn: () => void): this {
    this.completeHandlers.push(fn);
    return this;
  }

  /** Effect.Played += ... (fires while CurrentFrame == 1). */
  onPlayed(fn: () => void): this {
    this.playedHandlers.push(fn);
    return this;
  }

  setStart(start: number): void {
    this.start = start;
    this.nextFrame = this.start + idiv(this.duration, this.count) * (this.currentFrame + 1);
  }

  /** Web: the images this effect draws (Effect.Draw: BaseIndex + CurrentFrame). */
  protected frameIndices(): number[] {
    const list: number[] = [];
    for (let f = 0; f < this.count; f++) list.push(this.baseIndex + f);
    return list;
  }

  /** Web: true while the effect waits for its images (no light, the clock restarts when they are in). */
  get held(): boolean {
    return !this.playing;
  }

  /** Web: checks the frames once the effect is due; asking for each one also starts its load. */
  protected holding(now: number): boolean {
    if (this.playing) return false;
    if (now < this.start) return true;
    if (this.holdUntil === 0) this.holdUntil = now + HOLD_MAX_MS;
    let ready = true;
    for (const i of this.frameIndices()) if (sprite(this.lib, i) === undefined) ready = false; // keep asking: every miss queues
    if (!ready && now < this.holdUntil) return true;
    this.playing = true;
    if (now > this.start + 50) this.setStart(now); // it was held: play the whole cycle from frame 0
    return false;
  }

  process(): void {
    const now = effectWorld().now();
    if (this.holding(now)) return;
    if (this.currentFrame === 1) for (const fn of this.playedHandlers) fn();
    if (now <= this.nextFrame) return;
    if (++this.currentFrame >= this.count) {
      if (this.repeat && (this.repeatUntil === 0 || now < this.repeatUntil)) {
        this.currentFrame = 0;
        this.start = now + this.delay;
        this.nextFrame = this.start + idiv(this.duration, this.count) * (this.currentFrame + 1);
      } else this.remove();
    } else this.nextFrame = this.start + idiv(this.duration, this.count) * (this.currentFrame + 1);
  }

  remove(): void {
    if (this.removed) return;
    this.removed = true;
    const list = this.owner ? this.owner.effects : effectWorld().mapEffects;
    const i = list.indexOf(this);
    if (i >= 0) list.splice(i, 1);
    for (const fn of this.completeHandlers) fn();
  }

  /** Clears the Complete/Played handlers (Effect.Clear). */
  clear(): void {
    this.completeHandlers.length = 0;
    this.playedHandlers.length = 0;
  }

  /** Point-effect / owner-effect position (Effect.Draw's DrawLocation). */
  protected locate(): { x: number; y: number } {
    const w = effectWorld();
    if (this.owner) return w.drawLocation(this.owner);
    const user = w.user()!;
    return {
      x: (this.source.X - user.movement.X + w.offSetX) * CELL_W + this.drawOffset.X + user.offsetMove.X,
      y: (this.source.Y - user.movement.Y + w.offSetY) * CELL_H + this.drawOffset.Y + user.offsetMove.Y,
    };
  }

  draw(): void {
    const w = effectWorld();
    if (w.now() < this.start) return;
    this.drawLocation = this.locate();
    const index = this.baseIndex + this.currentFrame;
    if (this.blend) w.drawBlend(this.lib, index, this.drawLocation.x, this.drawLocation.y, true, this.rate);
    else w.draw(this.lib, index, this.drawLocation.x, this.drawLocation.y, true);
  }
}

export class Missile extends Effect {
  target: MapObj | null = null;
  destination: WirePoint;
  interval = 1;
  frameCount = 1;
  skip = 0;
  direction: number;
  /** 16 or 8: the frame rows the missile picks from (the art warm-up loads them all, preload.ts). */
  readonly directions: number;
  explode = false;
  /** Owner for the "flying away" target fallback; a point-sourced missile has none. */
  private readonly caster: MapObj | null;

  constructor(lib: string, baseIndex: number, count: number, duration: number, from: MapObj | WirePoint, target: WirePoint, direction16 = true) {
    super(lib, baseIndex, count, duration, from);
    this.caster = "X" in from ? null : from;
    this.destination = { ...target };
    this.directions = direction16 ? 16 : 8;
    this.direction = direction16 ? direction16Of(this.source, this.destination) : direction8Of(this.source, this.destination);
  }

  /** Missile.Draw's frame row for the current direction. */
  protected override frameIndices(): number[] {
    const list: number[] = [];
    for (let f = 0; f < this.frameCount; f++) list.push(this.baseIndex + f + this.direction * (this.skip + this.frameCount));
    return list;
  }

  override process(): void {
    const now = effectWorld().now();
    if (now < this.start) return;
    if (this.holding(now)) return;
    if (this.target) this.destination = { ...this.target.location };
    else if (!this.explode && this.caster) {
      const dist = maxDistance(this.caster.location, this.destination);
      if (dist < DATA_RANGE) this.destination = { X: this.destination.X + (this.destination.X - this.source.X), Y: this.destination.Y + (this.destination.Y - this.source.Y) };
    }
    this.duration = maxDistance(this.source, this.destination) * 50;
    this.count = idiv(this.duration, this.interval);
    if (this.count === 0) this.count = 1;
    super.process();
  }

  override draw(): void {
    const w = effectWorld();
    if (w.now() < this.start) return;
    const index = this.baseIndex + (this.currentFrame % this.frameCount) + this.direction * (this.skip + this.frameCount);
    const user = w.user()!;
    let x = (this.source.X - user.movement.X + w.offSetX) * CELL_W + user.offsetMove.X;
    let y = (this.source.Y - user.movement.Y + w.offSetY) * CELL_H + user.offsetMove.Y;
    const dx = (this.destination.X - this.source.X) * CELL_W, dy = (this.destination.Y - this.source.Y) * CELL_H;
    x += idiv(dx * this.currentFrame, this.count);
    y += idiv(dy * this.currentFrame, this.count);
    this.drawLocation = { x, y };
    if (!this.blend) w.draw(this.lib, index, x, y, true);
    else w.drawBlend(this.lib, index, x, y, true, this.rate);
  }
}

/** Globals.DataRange (the server's view radius in cells). */
export const DATA_RANGE = 16;

export function maxDistance(a: WirePoint, b: WirePoint): number {
  return Math.max(Math.abs(a.X - b.X), Math.abs(a.Y - b.Y));
}

function direction8Of(s: WirePoint, d: WirePoint): number {
  if (s.X < d.X) return s.Y < d.Y ? 3 : s.Y > d.Y ? 1 : 2;
  if (s.X > d.X) return s.Y < d.Y ? 5 : s.Y > d.Y ? 7 : 6;
  return s.Y < d.Y ? 4 : 0;
}

/** GameScene.Direction16 (law of cosines against "up", float maths as the C#). */
export function direction16Of(source: WirePoint, destination: WirePoint): number {
  let cx = source.X, cy = source.Y;
  const ax = cx, ay = 0;
  let bx = destination.X, by = destination.Y;
  const bc = Math.fround(Math.hypot(bx - cx, by - cy));
  const ac = bc;
  by -= cy;
  cy += bc;
  by += bc;
  const ab = Math.fround(Math.hypot(bx - ax, by - ay));
  const x = (ac * ac + bc * bc - ab * ab) / (2 * ac * bc);
  let angle = (Math.acos(x) * 180) / Math.PI;
  if (destination.X < cx) angle = 360 - angle;
  angle += 11.25;
  if (angle > 360) angle -= 360;
  const dir = Math.trunc(angle / 22.5);
  return Number.isFinite(dir) ? dir : 0; // source == destination: C# NaN -> (int)NaN = int.MinValue; draw nothing sensible either way
}

/** Port of Effect.cs DelayedExplosionEffect (SpellEffect.DelayedExplosion stages on an owner; removed when the owner dies). */
export class DelayedExplosionEffect extends Effect {
  static readonly effectlist: DelayedExplosionEffect[] = [];
  stage: number;

  /** `playSound` stands in for SoundManager.PlaySound (effects.ts has no sound seam). `until` is unused, as in the C#. */
  constructor(lib: string, baseIndex: number, count: number, duration: number, owner: MapObj, blend: boolean, stage: number, _until: number, playSound: (id: number) => void = () => {}) {
    super(lib, baseIndex, count, duration, owner);
    this.repeat = stage === 2 ? false : true;
    this.blend = blend;
    this.stage = stage;
    DelayedExplosionEffect.effectlist.push(this);
    if (this.stage === 1) playSound(20000 + 125 * 10);
    if (this.stage === 2) playSound(20000 + 125 * 10 + 5);
  }

  override remove(): void {
    super.remove();
    const i = DelayedExplosionEffect.effectlist.indexOf(this);
    if (i >= 0) DelayedExplosionEffect.effectlist.splice(i, 1);
  }

  override draw(): void {
    if (this.owner!.dead) {
      this.remove();
      return;
    }
    super.draw();
  }

  static getOwnerEffectID(objectID: number): number {
    for (let i = 0; i < DelayedExplosionEffect.effectlist.length; i++) {
      if (DelayedExplosionEffect.effectlist[i]!.owner!.id !== objectID) continue;
      return i;
    }
    return -1;
  }
}

/** BuffEffect (Effect.cs): a repeating, light-less effect tagged with its BuffType so RemoveBuffEffect can stop it. */
export class BuffEffect extends Effect {
  readonly buffType: number;

  constructor(lib: string, baseIndex: number, count: number, duration: number, owner: MapObj, blend: boolean, buffType: number) {
    super(lib, baseIndex, count, duration, owner, 0);
    this.repeat = true;
    this.blend = blend;
    this.buffType = buffType;
    this.light = -1;
  }
}

/** Effect.cs InterruptionEffect: the Concentration loop on an archer; hidden while interrupted, gone when concentration ends. */
export class InterruptionEffect extends Effect {
  static readonly effectlist: InterruptionEffect[] = [];
  private noProcess = false;

  constructor(lib: string, baseIndex: number, count: number, duration: number, owner: MapObj, blend: boolean) {
    super(lib, baseIndex, count, duration, owner);
    this.repeat = true;
    this.blend = blend;
    InterruptionEffect.effectlist.push(this);
  }

  override process(): void {
    if (!this.noProcess) super.process();
  }

  override remove(): void {
    super.remove();
    const i = InterruptionEffect.effectlist.indexOf(this);
    if (i >= 0) InterruptionEffect.effectlist.splice(i, 1);
  }

  override draw(): void {
    const owner = this.owner!;
    if (!owner.concentrating) this.remove();
    else this.noProcess = owner.concentrateInterrupted;
    if (!this.noProcess) super.draw();
  }

  static getOwnerEffectID(objectID: number): number {
    return InterruptionEffect.effectlist.findIndex((e) => e.owner?.id === objectID);
  }
}

/** Effect.cs ElementsEffect: an archer's element orb (1 green, 2 blue, 3 red, 4 mixed) with its looping sound. `sound` stands in
 *  for SoundManager (effects.ts has no sound seam). Removed when the owner loses its elements or reaches `killAt` (types 1-3). */
export class ElementsEffect extends Effect {
  private readonly myType: number;
  private readonly killAt: number;
  private readonly sound: { play(id: number, loop: boolean): void; stop(id: number): void };

  constructor(lib: string, baseIndex: number, count: number, duration: number, owner: MapObj, blend: boolean, elementType: number, killTime: number,
    loopOn: boolean, sound: { play(id: number, loop: boolean): void; stop(id: number): void }) {
    super(lib, baseIndex, count, duration, owner);
    this.repeat = true;
    this.blend = blend;
    this.myType = elementType;
    this.killAt = killTime;
    this.sound = sound;
    for (let i = 0; i <= 3; i++) sound.stop(20000 + 126 * 10 + 5 + i); // StopSounds
    sound.play(20000 + 126 * 10 + 4 + this.myType, loopOn); // StartSound
  }

  override remove(): void {
    this.sound.stop(20000 + 126 * 10 + 4 + this.myType);
    super.remove();
  }

  override draw(): void {
    const owner = this.owner!;
    if (!owner.hasElements) this.remove();
    if (owner.elementsLevel >= this.killAt && this.myType < 4) this.remove();
    super.draw();
  }
}

/** Effect.cs TrackableEffect: a named copy of an effect, removed when its owner dies (Binding Shot's net stages). */
export class TrackableEffect extends Effect {
  static readonly effectlist: TrackableEffect[] = [];
  readonly effectName: string;

  constructor(base: Effect, effectName = "null") {
    super(base.lib, base.baseIndex, base.count, base.duration, base.owner ?? base.source, base.start);
    this.repeat = base.repeat;
    this.repeatUntil = base.repeatUntil;
    this.blend = base.blend;
    this.effectName = effectName;
    TrackableEffect.effectlist.push(this);
  }

  /** TrackableEffect.GetOwnerEffectID: the list index of the owner's live effect with that name ("null" = any), else -1. */
  static getOwnerEffectID(objectID: number, effectName = "null"): number {
    const list = TrackableEffect.effectlist;
    for (let i = 0; i < list.length; i++) {
      if (list[i]!.owner?.id !== objectID) continue;
      if (effectName !== "null" && list[i]!.effectName !== effectName) continue;
      return i;
    }
    return -1;
  }

  override process(): void {
    super.process();
    if (!this.owner || this.owner.dead) this.remove();
  }

  override remove(): void {
    super.remove();
    const i = TrackableEffect.effectlist.indexOf(this);
    if (i >= 0) TrackableEffect.effectlist.splice(i, 1);
  }
}

/** The IntelligentCreature parts of MonsterObject.ProcessFrames, called on each new frame of a monster's action (after the
 *  frame index advanced and before the action ends, like the C# `switch (FrameIndex)` blocks): BabySnowMan's snow on
 *  Attack2 frame 1, OlympicFlame's flame on Attack3 frame 1 and smoke on frame 4. One live effect per name and owner
 *  (TrackableEffect.GetOwnerEffectID), removed when the creature dies. */
export function creatureFrameEffects(ob: MapObj): void {
  const pet = (index: number, count: number, duration: number, name: string) => {
    if (TrackableEffect.getOwnerEffectID(ob.id, name) >= 0) return;
    ob.effects.push(new TrackableEffect(new Effect(LibPath.pet(ob.image - 10000), index, count, duration, ob), name));
  };
  if (ob.action === MirAction.Attack2 && ob.frameIndex === 1 && ob.image === Monster.BabySnowMan) pet(208, 11, 1500, "SnowmanSnow");
  if (ob.action === MirAction.Attack3 && ob.image === Monster.OlympicFlame) {
    if (ob.frameIndex === 1) pet(280, 4, 800, "CreatureFlame");
    else if (ob.frameIndex === 4) pet(256, 3, 1000, "CreatureSmoke");
  }
}
