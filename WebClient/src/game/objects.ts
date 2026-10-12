// Map objects (players, monsters, NPCs): the parts of Client/MirObjects/{MapObject,PlayerObject,MonsterObject,NPCObject}.cs
// the web client needs - an action feed processed frame by frame, DrawFrame = Start + OffSet * direction + frameIndex,
// the moving OffSetMove of Walking/Running, and the per-class body/hair/weapon library selection (PlayerObject.SetLibraries),
// including the transform body, the mount and the fishing rod (PlayerObject.MountUpdate / FishingUpdate, riding.ts rules).

import { MirAction as A, MirClass, MirGender, MirDirection, ObjectType, Spell } from "../protocol/enums.generated.ts";
import type { WirePoint } from "../protocol/binaryCodec.ts";
import * as Frames from "./frames.ts";
import { frameOffset, effectOffset, monsterFrames, type Frame, type FrameSet } from "./frames.ts";
import { LibPath, lib, monsterLibPath, npcLibPath } from "./libraries.ts";
import { creatureFrameEffects, type Effect } from "./effects.ts";
import { monsterActionEnd, monsterFrame } from "./monsterSpecials.ts";
import { hasFishingRod, isIdleAction, mountAction, transformShows } from "./riding.ts";

export const CELL_W = 48, CELL_H = 32;
const CLASS_WEAPON_COUNT = 100; // Globals.ClassWeaponCount
const NO_FRAMES: FrameSet = new Map();

export function pointMove(p: WirePoint, d: number, i: number): WirePoint {
  const dx = [0, 1, 1, 1, 0, -1, -1, -1][d] ?? 0;
  const dy = [-1, -1, 0, 1, 1, 1, 0, -1][d] ?? 0;
  return { X: p.X + dx * i, Y: p.Y + dy * i };
}

/** Functions.DirectionFromPoint. */
export function directionFromPoint(s: WirePoint, d: WirePoint): number {
  if (s.X < d.X) return s.Y < d.Y ? MirDirection.DownRight : s.Y > d.Y ? MirDirection.UpRight : MirDirection.Right;
  if (s.X > d.X) return s.Y < d.Y ? MirDirection.DownLeft : s.Y > d.Y ? MirDirection.UpLeft : MirDirection.Left;
  return s.Y < d.Y ? MirDirection.Down : MirDirection.Up;
}

/** QueuedAction.Params for MirAction.Spell / Attack (PlayerObject: Spell, TargetID, TargetPoint, Cast, SpellLevel, SecondaryTargetIDs). */
export interface SpellParams { spell: number; targetId: number; targetPoint: WirePoint; cast: boolean; level: number; secondaryTargetIds: number[] }
export interface QueuedAction { action: number; direction: number; location: WirePoint; spell?: SpellParams; /** Struck: the attacker's weapon (StruckWeapon). */ struckWeapon?: number }

/** Spell visual hooks (spellVisuals.ts via game.ts): PlayerObject.SetAction's Spell case and ProcessFrames' Spell completion. */
/** Map seam for the client-side jump checks (GetBackStepDistance / GetFlashDashDistance): ValidPoint and no blocking object. */
export const moveHooks = {
  canStand: (_p: WirePoint) => true,
};

/** Moving actions (PlayerObject.ProcessFrames "Moving OffSet" / SetAction MapLocation cases). */
const MOVING = new Set<number>([A.Walking, A.Running, A.MountWalking, A.MountRunning, A.Pushed, A.DashL, A.DashR, A.Sneek, A.Jump, A.DashAttack]);

/** Spells PlayerObject.SetAction plays with the AttackRange2 frames (and turns the action into AttackRange2). */
const RANGE2_SPELLS = new Set<number>([Spell.StraightShot, Spell.DoubleShot, Spell.DelayedExplosion, Spell.BindingShot, Spell.VampireShot, Spell.PoisonShot,
  Spell.CrippleShot, Spell.NapalmShot, Spell.SummonVampire, Spell.SummonToad, Spell.SummonSnakes, Spell.Stonetrap]);

const reverseDirection = (d: number) => (d + 4) & 7;

export const spellHooks = {
  start: (_ob: MapObj) => {},
  complete: (_ob: MapObj) => {},
  /** End of PlayerObject.SetAction (shield / barrier refresh, action sounds), every action. */
  action: (_ob: MapObj) => {},
  /** A new animation frame within the current action (ProcessFrames' per-frame sounds). */
  frame: (_ob: MapObj) => {},
};

export interface Appearance {
  class: number; gender: number; hair: number; armour: number; weapon: number; weaponEffect: number;
}

/** C# integer division truncates towards zero (-1 / 100 == 0). */
const idiv = (a: number, b: number) => Math.trunc(a / b);

export class MapObj {
  readonly id: number;
  readonly race: number;
  name: string;
  nameColour: number;
  /** PlayerObject.GuildName (drawn under the name in the name colour). */
  guildName = "";
  /** HeroObject.OwnerName (S.ObjectHero): set on a hero, drawn as "{0}'s Hero" under its name. Heroes keep race Player. */
  ownerName = "";
  location: WirePoint;      // CurrentLocation
  movement: WirePoint;      // Movement (where the sprite is drawn from during a move)
  offsetMove = { X: 0, Y: 0 };
  direction: number;
  dead = false;
  hidden = false;
  /** MapObject.Light: ObjectPlayer/ObjectMonster.Light, NPCs 10, the user from equipment (min 3). */
  light = 0;
  /** Monster: Monster enum (body library: `monsterLibPath`, e.g. Data\Monster\NNN.Lib, Data\Pet\NN.Lib for creatures >= 10000);
   *  NPC: image (`npcLibPath`: Data\NPC\NN.Lib, Data\Flag\NN.Lib for 1000 .. 1099). */
  image = 0;
  /** MonsterObject.Stage (S.ObjectMonster.ExtraByte): picks the GreatFoxSpirit frame set. */
  stage = 0;
  /** The Direction at MonsterObject.Load (S.ObjectMonster.Direction): picks the DragonStatue / CaveStatue frame set. */
  readonly loadDirection: number;
  look: Appearance = { class: 0, gender: 0, hair: 0, armour: 0, weapon: -1, weaponEffect: 0 };
  healthPercent = -1;
  healthExpire = 0;
  chatText = ""; chatUntil = 0;

  readonly feed: QueuedAction[] = [];
  /** MapObject.Effects (owner-following effects). */
  readonly effects: Effect[] = [];
  // PlayerObject spell state
  spell = 0;
  targetId = 0;
  targetPoint: WirePoint = { X: 0, Y: 0 };
  cast = false;
  spellLevel = 0;
  secondaryTargetIds: number[] = [];
  // MapObject / PlayerObject buff, poison and shield state
  /** MonsterObject.AI (6 = guard: green on the minimap). */
  ai = 0;
  /** MapObject.Buffs (visible buff types). */
  buffs: number[] = [];
  /** MapObject.Poison (PoisonType flags). */
  poison = 0;
  /** PlayerObject.CurrentEffect (SpellEffect from S.ObjectPlayer.Effect). */
  currentEffect = 0;
  /** MapObject.StruckWeapon: the attacker's weapon shape, for the struck sound. */
  struckWeapon = 0;
  magicShield = false;
  shieldEffect: Effect | null = null;
  elementalBarrier = false;
  elementalBarrierEffect: Effect | null = null;
  hasElements = false;
  sprint = false;
  sneaking = false;
  slashingBurstTime = 0;
  /** MapObject.JumpDistance (Jump / DashAttack). */
  jumpDistance = 0;
  /** MonsterObject.SitDown, PercentMana (S.ObjectMana), PlayerObject.LevelEffects. */
  sitDown = false;
  percentMana = -1;
  levelEffects = 0;
  /** PlayerObject.Concentrating / ConcentrateInterrupted (S.SetConcentration). */
  concentrating = false;
  concentrateInterrupted = false;
  /** PlayerObject.ElementCasted / ElementsLevel / ElementOrbMax (S.SetElemental, ObjectPlayer orbs; HasElements is above). */
  elementCasted = false;
  elementsLevel = 0;
  elementOrbMax = 0;
  /** MapObject.InTrapRock (the user's S.InTrapRock): no walking, no jumps. */
  inTrapRock = false;
  /** NPCObject.Colour (S.NPCImageUpdate). */
  colour = -1;
  // PlayerObject mount / transform / fishing state (S.ObjectPlayer, S.MountUpdate, S.TransformUpdate, S.FishingUpdate)
  /** PlayerObject.MountType (-1 = none; Data/Mount/NN.Lib). */
  mountType = -1;
  /** PlayerObject.RidingMount: idle / walk / run / struck / attack play the Mount* frames, the mount draws under the body. */
  ridingMount = false;
  /** PlayerObject.MountTime (performance.now of the last MountUpdate). */
  mountTime = 0;
  /** PlayerObject.TransformType (-1 = none; Data/Transform/NN.Lib replaces body, hair and weapons). */
  transformType = -1;
  /** PlayerObject.Fishing: idles in FishingWait. */
  fishing = false;
  /** PlayerObject.FoundFish / FishingPoint (S.FishingUpdate). */
  foundFish = false;
  fishingPoint: WirePoint = { X: 0, Y: 0 };
  /** UserObject.FishingTime: when the user last sent C.FishingCast (GameScene.CanFish throttle; game.ts sets it). */
  fishingTime = 0;
  blizzardStopTime = 0;
  reincarnationStopTime = 0;
  action: number = A.Standing;
  frame: Frame | null = null;
  frameIndex = 0;
  effectFrameIndex = 0;
  private nextMotion = 0;
  private nextEffectMotion = 0;
  /** Fired when an action starts (the user object sends its packet from here, like PlayerObject.SetAction). */
  onActionStart: ((q: QueuedAction) => void) | null = null;
  /** Fired when a Walking/Running action finishes (the user: unlock running). */
  onActionEnd: ((action: number) => void) | null = null;

  constructor(id: number, race: number, location: WirePoint, direction: number) {
    this.id = id;
    this.race = race;
    this.name = "";
    this.nameColour = -1;
    this.location = { ...location };
    this.movement = { ...location };
    this.direction = direction;
    this.loadDirection = direction;
  }

  get frames(): FrameSet {
    if (this.race === ObjectType.Item) return NO_FRAMES;
    if (this.race === ObjectType.Player) return Frames.Player;
    if (this.race === ObjectType.Monster) {
      // MonsterObject.Load "Library" + "Frames" switches (BodyLibrary.Frames ?? FrameSet.DefaultMonster)
      const body = monsterLibPath(this.image);
      return monsterFrames(this.image, this.loadDirection, this.stage, body === null ? null : lib(body)?.frames);
    }
    // NPCObject.LoadLibrary, Frames = BodyLibrary.Frames ?? FrameSet.DefaultNPC
    const body = npcLibPath(this.image);
    return (body === null ? null : lib(body)?.frames) ?? Frames.DefaultNPC;
  }

  /** The cell row the object is sorted into (MapObject.DrawY). */
  get drawY(): number {
    return Math.max(this.movement.Y, this.location.Y);
  }

  queue(action: number, direction: number, location: WirePoint, spell?: SpellParams, struckWeapon?: number): void {
    this.feed.push({ action, direction, location: { ...location }, spell, struckWeapon });
  }

  /** Frame.Count * FrameInterval: the cast effects' duration. */
  get actionDuration(): number {
    return (this.frame?.count ?? 0) * (this.frame?.interval ?? 0);
  }

  /** Jump straight to an idle state at a location (spawn, teleport, server correction). */
  snap(location: WirePoint, direction: number): void {
    this.feed.length = 0;
    this.location = { ...location };
    this.direction = direction;
    this.setAction(0, true);
  }

  /** PlayerObject.SetAction, empty feed: Standing (MountStanding when riding), FishingWait while fishing. */
  private idleAction(): number {
    if (this.dead) return A.Dead;
    if (this.race !== ObjectType.Player) return A.Standing;
    if (this.fishing) return A.FishingWait;
    return this.ridingMount ? A.MountStanding : A.Standing;
  }

  /** PlayerObject.HasFishingRod. */
  get hasFishingRod(): boolean {
    return hasFishingRod(this.look.weapon);
  }

  /** PlayerObject.MountUpdate: new mount state, a Standing action first in the feed (becomes MountStanding when riding).
   *  The caller does the rest of GameScene.MountUpdate: setEffects (none while riding), the mount sound
   *  (playerSound(ob, "mount")), and for the user CanRun = false, RefreshStats, MountDialog. */
  mountUpdate(p: { MountType: number; RidingMount: boolean }, now = performance.now()): void {
    this.mountType = p.MountType;
    this.ridingMount = p.RidingMount;
    this.feed.unshift({ action: A.Standing, direction: this.direction, location: { ...this.location } });
    this.mountTime = now;
    // SetLibraries: playerLibraries() is evaluated per draw
  }

  /** PlayerObject.FishingUpdate: a change of Fishing queues FishingCast (cast out) or FishingReel (reel in) facing the
   *  fishing point; FishingPoint / FoundFish always update. The FishingDialog / FishingStatusDialog parts are the caller's. */
  fishingUpdate(p: { Fishing: boolean; FishingPoint: WirePoint; FoundFish: boolean }): void {
    if (this.fishing !== p.Fishing) {
      const dir = directionFromPoint(this.location, p.FishingPoint);
      this.feed.push({ action: p.Fishing ? A.FishingCast : A.FishingReel, direction: dir, location: { ...this.location } });
      this.fishing = p.Fishing;
    }
    this.fishingPoint = { ...p.FishingPoint };
    this.foundFish = p.FoundFish;
  }

  setAction(now: number, forceIdle = false): void {
    const next = forceIdle ? undefined : this.feed.shift();
    if (next) {
      const oldDirection = this.direction;
      this.action = this.race === ObjectType.Player && this.ridingMount ? mountAction(next.action) : next.action;
      this.direction = next.direction;
      this.location = { ...next.location };
      if (next.action === A.Spell && next.spell && this.race === ObjectType.Player) this.spellAction(next.spell, oldDirection);
      if (next.action === A.Die) this.dead = true;
      if (next.action === A.Revive) this.dead = false;
      if (next.struckWeapon !== undefined) this.struckWeapon = next.struckWeapon;
      if (next.spell) {
        this.spell = next.spell.spell;
        this.targetId = next.spell.targetId;
        this.targetPoint = { ...next.spell.targetPoint };
        this.cast = next.spell.cast;
        this.spellLevel = next.spell.level;
        this.secondaryTargetIds = next.spell.secondaryTargetIds;
      }
      this.onActionStart?.(next);
    } else {
      this.action = this.idleAction();
    }
    this.frame = this.frameFor(this.action);
    if (!this.frame && this.action === A.Dead) this.frame = this.frames.get(A.Die) ?? null;
    this.frameIndex = this.action === A.Dead && this.frame && !this.frames.has(A.Dead) ? Math.max(0, this.frame.count - 1) : 0;
    this.effectFrameIndex = 0;
    this.nextMotion = now + (this.frame?.interval ?? 500);
    this.nextEffectMotion = now + (this.frame?.effectInterval || 250);
    this.updateMovement();
    if (next && (next.action === A.Spell || next.action === A.AttackRange2)) spellHooks.start(this);
    spellHooks.action(this);
  }

  /** The frame PlayerObject.SetAction picks: Pushed walks, dashes run, DashFail stands, the per-spell overrides (`frameAction`),
   *  an Archer with a bow walks / runs with the Bow frames. Monsters and NPCs use their own table. */
  private frameAction = -1;
  private frameFor(action: number): Frame | null {
    const override = this.frameAction;
    this.frameAction = -1;
    return this.pickFrame(action, override);
  }

  /** frameFor without its side effect: the frame `action` plays now (`override` = a spell's frameAction). Also asked
   *  by the art warm-up (preload.ts). */
  pickFrame(action: number, override = -1): Frame | null {
    const fs = this.frames;
    let a = action;
    if (this.race === ObjectType.Player) {
      switch (action) {
        case A.Pushed: a = A.Walking; break;
        case A.DashL: case A.DashR: a = A.Running; break;
        case A.DashFail: a = this.ridingMount ? A.MountStanding : A.Standing; break;
      }
      if (override >= 0) a = override;
      if (this.look.class === MirClass.Archer && this.hasClassWeapon) {
        if (action === A.Walking) a = A.WalkingBow;
        else if (action === A.Running) a = A.RunningBow;
      }
      // PlayerObject.SetLibraries transform region: a transformed body has one stand / walk / run / attack set
      if (this.transformType > -1) {
        switch (action) { // SetLibraries reads Action, which SetAction has just set to this action
          case A.Standing: case A.Jump: a = A.Standing; break;
          case A.Walking: case A.WalkingBow: a = A.Walking; break;
          case A.Running: case A.RunningBow: a = A.Running; break;
          case A.Attack1: case A.Attack2: case A.Attack3: case A.Attack4: case A.AttackRange1: case A.AttackRange2: case A.AttackRange3: a = A.Attack1; break;
        }
      }
    }
    return fs.get(a) ?? fs.get(A.Standing) ?? null;
  }

  /** PlayerObject.SetAction's `case MirAction.Spell` frame switch: some spells play other frames, become another action and move. */
  private spellAction(sp: { spell: number; level: number }, oldDirection: number): void {
    switch (sp.spell) {
      case Spell.ShoulderDash:
        this.frameAction = A.Running;
        this.action = A.DashL;
        this.direction = oldDirection;
        this.location = pointMove(this.location, this.direction, 1);
        break;
      case Spell.BladeAvalanche: case Spell.CrescentSlash: this.frameAction = A.Attack3; break;
      case Spell.SlashingBurst: case Spell.CounterAttack: case Spell.PoisonSword: this.frameAction = A.Attack1; break;
      case Spell.HeavenlySword: this.frameAction = A.Attack2; break;
      case Spell.FlashDash:
        this.jumpDistance = this.jumpTravel(this.direction, sp.level <= 1 ? 0 : 1); // GetFlashDashDistance
        if (this.jumpDistance !== 0) {
          this.frameAction = A.DashAttack;
          this.action = A.DashAttack;
          this.location = pointMove(this.location, this.direction, this.jumpDistance);
        } else this.frameAction = Math.random() * 100 >= 40 ? A.Attack1 : A.Attack4;
        break;
      case Spell.ExplosiveTrap: this.frameAction = A.Harvest; this.action = A.Harvest; break;
      case Spell.BackStep:
        this.jumpDistance = this.jumpTravel(reverseDirection(this.direction), sp.level === 0 ? 1 : sp.level); // GetBackStepDistance
        this.frameAction = A.Jump;
        this.action = A.Jump;
        this.location = pointMove(this.location, reverseDirection(this.direction), this.jumpDistance);
        break;
      case Spell.ElementalShot:
        if (this.hasElements && !this.elementCasted) { this.frameAction = A.AttackRange2; this.action = A.AttackRange2; }
        if (this.elementCasted) this.elementCasted = false;
        break;
      default:
        if (RANGE2_SPELLS.has(sp.spell)) { this.frameAction = A.AttackRange2; this.action = A.AttackRange2; }
    }
  }

  /** GetBackStepDistance / GetFlashDashDistance: free cells walked in `dir`, up to `dist`, none while InTrapRock. */
  private jumpTravel(dir: number, dist: number): number {
    if (this.inTrapRock) return 0;
    let travel = 0, at = this.location;
    for (let i = 0; i < dist; i++) {
      at = pointMove(at, dir, 1);
      if (!moveHooks.canStand(at)) break;
      travel++;
    }
    return travel;
  }

  process(now: number): void {
    for (let i = this.effects.length - 1; i >= 0; i--) this.effects[i]?.process();
    if (this.race === ObjectType.Item) return; // ItemObject: a still DNItems frame
    if (!this.frame) { this.setAction(now); return; }
    const idle = isIdleAction(this.action) || this.action === A.Dead;
    if (idle && this.feed.length) { this.setAction(now); return; }

    if (this.frame.effectCount > 0 && now >= this.nextEffectMotion) {
      this.effectFrameIndex = (this.effectFrameIndex + 1) % this.frame.effectCount;
      this.nextEffectMotion = now + (this.frame.effectInterval || 250);
    }

    if (now < this.nextMotion) return;
    // Catch up at most a few frames after a stall (background tab), never spin.
    if (now - this.nextMotion > 2000) this.nextMotion = now;
    this.nextMotion += this.frame.interval || 100;
    this.frameIndex++;
    if (this.frameIndex >= this.frame.count) {
      if (this.action === A.Dead) { this.frameIndex = this.frame.count - 1; return; }
      if ((this.action === A.Spell || this.action === A.AttackRange2) && this.cast) spellHooks.complete(this);
      if (this.race === ObjectType.Monster) monsterActionEnd(this); // MonsterObject.ProcessFrames "UpdateFrame() >= Frame.Count" specials
      const ended = this.action;
      // PlayerObject.SetAction re-picks the idle action each loop (mount / fishing state may have changed)
      if (idle && !this.feed.length && (this.action === A.Stance || this.action === A.Dead || this.idleAction() === this.action)) this.frameIndex = 0;
      else this.setAction(now);
      if (!idle) this.onActionEnd?.(ended);
      return;
    }
    this.updateMovement();
    if (this.race === ObjectType.Monster && this.image >= 10000) creatureFrameEffects(this); // MonsterObject.ProcessFrames (IntelligentCreature)
    if (this.race === ObjectType.Monster) monsterFrame(this); // MonsterObject.ProcessFrames per-monster frame specials (monsterSpecials.ts)
    spellHooks.frame(this);
  }

  /** PlayerObject.ProcessFrames "Moving OffSet": Movement = CurrentLocation stepped back, OffSetMove grows each frame. */
  updateMovement(): void {
    if (!MOVING.has(this.action) || !this.frame) {
      this.offsetMove = { X: 0, Y: 0 };
      this.movement = { ...this.location };
      return;
    }
    let i = this.action === A.MountRunning ? 3 : this.action === A.Running ? 2 : 1; // Sprint (3) is not ported
    if (this.action === A.Jump) i = -this.jumpDistance;
    if (this.action === A.DashAttack) i = this.jumpDistance;
    this.movement = pointMove(this.location, this.direction, this.action === A.Pushed ? 0 : -i);
    let count = this.frame.count, index = this.frameIndex;
    if (this.action === A.DashL || this.action === A.DashR) { count = 3; index %= 3; } // a dash covers its cell in 3 frames
    const fx = Math.trunc((CELL_W * i / count) * (index + 1));
    const fy = Math.trunc((CELL_H * i / count) * (index + 1));
    let x = 0, y = 0;
    switch (this.direction) {
      case MirDirection.Up: y = fy; break;
      case MirDirection.UpRight: x = -fx; y = fy; break;
      case MirDirection.Right: x = -fx; break;
      case MirDirection.DownRight: x = -fx; y = -fy; break;
      case MirDirection.Down: y = -fy; break;
      case MirDirection.DownLeft: x = fx; y = -fy; break;
      case MirDirection.Left: x = fx; break;
      case MirDirection.UpLeft: x = fx; y = fy; break;
    }
    // C#: OffSetMove.X % 2 + OffSetMove.X (remainder keeps the sign)
    this.offsetMove = { X: (x % 2) + x, Y: (y % 2) + y };
  }

  get drawFrame(): number {
    if (!this.frame) return 0;
    return this.frame.start + frameOffset(this.frame) * this.direction + this.frameIndex;
  }

  get drawEffectFrame(): number {
    if (!this.frame) return 0;
    return this.frame.effectStart + effectOffset(this.frame) * this.direction + this.effectFrameIndex;
  }

  // — Player libraries (PlayerObject.SetLibraries; wings / weapon effects not ported except the transform 19 wings) —

  get hasClassWeapon(): boolean {
    switch (idiv(this.look.weapon, CLASS_WEAPON_COUNT)) {
      case 1: return this.look.class === MirClass.Assassin;
      case 2: return this.look.class === MirClass.Archer;
      default: return this.look.class === MirClass.Wizard || this.look.class === MirClass.Warrior || this.look.class === MirClass.Taoist;
    }
  }

  /** `act` defaults to the current action; the art warm-up (preload.ts) asks for others. */
  playerLibraries(act = this.action): PlayerLibraries {
    const { class: cls, gender, hair, armour, weapon } = this.look;
    const female = gender === MirGender.Female;
    let altAnim = false;
    let body: string | null, hairLib: string | null, w1: string | null = null, w2: string | null = null, wing: string | null = null;
    let armourOff: number, hairOff: number, weaponOff: number;
    const { showMount, showFishing } = transformShows(this.transformType);
    if (this.transformType > -1) {
      // Transform: one body library, no hair / weapons; riding a big mount (MountType > 6) uses TransformRide2 at -416.
      // A missing library falls back to 00 (TransformType < Length ? ... : [0]).
      const t = this.transformType;
      if (this.mountType > 6 && this.ridingMount) {
        armourOff = -416;
        body = lib(LibPath.transformMount(t)) === null ? LibPath.transformMount(0) : LibPath.transformMount(t);
      } else {
        armourOff = 0;
        body = lib(LibPath.transform(t)) === null ? LibPath.transform(0) : LibPath.transform(t);
      }
      hairLib = null;
      // TransformType 19: WingEffect = 2 -> TransformEffect[1] (the desktop leaves WingEffect at 2 afterwards; not copied)
      if (t === 19) wing = LibPath.transformEffect(1);
      hairOff = weaponOff = 0;
    } else if (cls === MirClass.Archer) {
      if (this.hasClassWeapon && (act === A.Walking || act === A.Running || act === A.AttackRange1 || act === A.AttackRange2)) altAnim = true;
      if (act === A.Jump) altAnim = true;
      body = altAnim ? LibPath.arArmour(armour) : LibPath.cArmour(armour);
      hairLib = altAnim ? LibPath.arHair(hair) : LibPath.cHair(hair);
      if (this.hasClassWeapon) w2 = altAnim ? LibPath.arWeaponS(weapon - 200) : LibPath.arWeapon(weapon - 200);
      else if (weapon >= 0) w1 = LibPath.cWeapon(weapon);
      armourOff = hairOff = !female ? 0 : altAnim ? 352 : 808;
      weaponOff = !female ? 0 : altAnim ? 352 : 416;
    } else if (cls === MirClass.Assassin) {
      if ((this.hasClassWeapon || weapon < 0) && [A.Standing, A.Stance, A.Walking, A.Running, A.Die, A.Struck, A.Attack1, A.Attack2, A.Attack3, A.Attack4, A.Sneek, A.Spell, A.DashAttack].includes(act as never)) altAnim = true;
      body = altAnim ? LibPath.aArmour(armour) : LibPath.cArmour(armour);
      hairLib = altAnim ? LibPath.aHair(hair) : LibPath.cHair(hair);
      if (this.hasClassWeapon) { w1 = LibPath.aWeaponR(weapon - 100); w2 = LibPath.aWeaponL(weapon - 100); }
      else if (weapon >= 0) w1 = LibPath.cWeapon(weapon);
      armourOff = hairOff = !female ? 0 : altAnim ? 512 : 808;
      weaponOff = !female ? 0 : altAnim ? 512 : 416;
    } else {
      body = LibPath.cArmour(armour);
      hairLib = LibPath.cHair(hair);
      if (weapon >= 0) w1 = LibPath.cWeapon(weapon);
      armourOff = hairOff = female ? 808 : 0;
      weaponOff = female ? 416 : 0;
    }
    // A missing armour library falls back to armour 0 (Armour < CArmours.Length ? ... : CArmours[0]); lib() === null means 404.
    if (this.transformType < 0 && body && lib(body) === null) body = body.replace(/\/\d+$/, "/00");
    if (hairLib && lib(hairLib) === null) hairLib = null;

    // Common: Harvest shows CWeapons[1] (not transformed)
    if (act === A.Harvest && this.transformType < 0) w1 = LibPath.cWeapon(1);
    // Mounts (a missing Mount/NN.Lib draws nothing)
    const mount = this.mountType > -1 && this.ridingMount && showMount ? LibPath.mount(this.mountType) : null;
    // Fishing: the rod library (Fishing/00 or 01 = Weapon - 49) replaces the weapon during the Fishing* frames (at -632)
    if (this.hasFishingRod && showFishing && (act === A.FishingCast || act === A.FishingWait || act === A.FishingReel)) {
      w1 = LibPath.fishing(weapon - 49);
      w2 = null;
      weaponOff = -632;
    }
    return { body, hair: hairLib, weapon1: w1, weapon2: w2, armourOff, hairOff, weaponOff, mount, mountOff: -416, wing, wingOff: 0 };
  }
}

/** MonsterObject.DrawName / DrawChat "//IntelligentCreature" y offsets: Chick name -10 / chat +30, the other creatures
 *  name -20 / chat +20, other monsters 0. The desktop applies the name offset only to a name containing "_" (drawn as
 *  one line per "_" part); the chat offset always. */
export function creatureLabelOffset(image: number): { name: number; chat: number } {
  if (image === 10001) return { name: -10, chat: 30 };                 // Monster.Chick
  if (image >= 10000 && image <= 10014) return { name: -20, chat: 20 }; // BabyPig .. MedicalRat
  return { name: 0, chat: 0 };
}

/** PlayerObject's BodyLibrary / HairLibrary / WeaponLibrary1/2 / MountLibrary / WingLibrary and their offsets
 *  (DrawFrame + offset; the mount draws DrawFrame - 416 + MountOffset, wings DrawWingFrame + WingOffset). */
export interface PlayerLibraries {
  body: string | null; hair: string | null; weapon1: string | null; weapon2: string | null;
  armourOff: number; hairOff: number; weaponOff: number;
  mount: string | null; mountOff: number;
  wing: string | null; wingOff: number;
}
