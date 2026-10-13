// Art warm-up: the WoOOLcraft web client's local-player warm and monster block warm, on the Crystal libraries. The
// desktop's MLibrary.Draw loads an image synchronously the first time it is drawn; the browser cannot, so every first
// step in a new direction or a monster's first swing drew nothing until its frames arrived. This loads ahead, at low
// priority and one batched read per block (libraries.ts prefetch):
//   - the user: the actions they see constantly, all eight directions, every layer (body, hair, weapons, mount),
//     again whenever their look, mount or transform changes;
//   - each monster image on the map: its common actions, all eight directions, once per image;
//   - the user's skill-bar spells (keys 1-16): a dry run of spellVisuals.ts on a throwaway caster and target records
//     every effect the cast, the missile and the hit would create (and the warrior attack overlays), so the indices
//     stay the ones copied from the C# in one place. Effects the server starts (S.ObjectEffect / MapEffect / spell
//     objects) are not covered.
// While a block is still loading MapView holds its first frame (or the last frame drawn): mapView.ts heldFrame.

import { MirAction as A, MirClass, ObjectType } from "../protocol/enums.generated.ts";
import { frameOffset, type Frame } from "./frames.ts";
import { lib, monsterLibPath, prefetch } from "./libraries.ts";
import { MapObj } from "./objects.ts";
import { mountAction } from "./riding.ts";
import { Effect, Missile, effectWorld, setEffectWorld } from "./effects.ts";
import { drawActionEffects, spellComplete, spellStart, type SpellWorld } from "./spellVisuals.ts";

/** The user's warmed actions (mapped to the Mount* frames while riding, the Bow / transform frames by pickFrame). */
const USER_ACTIONS: readonly number[] = [A.Standing, A.Walking, A.Running, A.Struck, A.Attack1, A.Spell, A.Die];
/** Extra user actions for an Archer with a bow (the shot). */
const ARCHER_ACTIONS: readonly number[] = [A.AttackRange2];
/** A monster's warmed actions (an action its frame set lacks is skipped). */
const MONSTER_ACTIONS: readonly number[] = [A.Standing, A.Walking, A.Attack1, A.Struck, A.Die];

/** Every image of `frame` (all eight directions) at library offset `off`. */
function blockIndices(frame: Frame, off: number, out: number[]): void {
  const step = frameOffset(frame);
  for (let d = 0; d < 8; d++) for (let i = 0; i < frame.count; i++) out.push(frame.start + off + step * d + i);
}

function add(wanted: Map<string, number[]>, path: string | null, frame: Frame, off: number): void {
  if (path === null) return;
  let list = wanted.get(path);
  if (!list) wanted.set(path, (list = []));
  blockIndices(frame, off, list);
}

/** The images an effect can draw: Effect.Draw's BaseIndex + CurrentFrame, Missile.Draw's frame rows per direction. */
function effectImages(e: Effect, wanted: Map<string, Set<number>>): void {
  let set = wanted.get(e.lib);
  if (!set) wanted.set(e.lib, (set = new Set()));
  if (e instanceof Missile) {
    for (let d = 0; d < e.directions; d++) for (let f = 0; f < e.frameCount; f++) set.add(e.baseIndex + f + d * (e.skip + e.frameCount));
  } else for (let f = 0; f < e.count; f++) set.add(e.baseIndex + f);
}

/** Dry run of one spell's visuals (spellStart, spellComplete, the missile's hit, the attack overlays) on throwaway
 *  objects; returns library -> images. Nothing reaches the map: the world is a recorder and every effect is removed. */
export function spellImages(user: MapObj, spell: number, level: number): Map<string, Set<number>> {
  const wanted = new Map<string, Set<number>>();
  const caster = new MapObj(-1, ObjectType.Player, user.location, user.direction);
  caster.look = { ...user.look };
  const target = new MapObj(-2, ObjectType.Monster, { X: user.location.X + 3, Y: user.location.Y + 2 }, 0);
  target.frame = null;
  const mapEffects: Effect[] = [];
  const w: SpellWorld = {
    getObject: (id) => (id === -2 ? target : id === -1 ? caster : undefined),
    validPoint: () => true,
    user: () => caster,
    now: () => performance.now(),
    playSound: () => {},
    addMapEffect: (e) => { mapEffects.push(e); },
  };
  caster.spell = spell;
  caster.spellLevel = level;
  caster.targetId = target.id;
  caster.targetPoint = { ...target.location };
  caster.cast = true;
  caster.action = A.Spell;
  caster.frame = user.pickFrame(A.Spell);
  const seen = new Set<Effect>();
  const drain = () => {
    // remove() fires Complete (a missile's hit adds the hit effect to the target), so repeat until nothing is new
    for (let round = 0; round < 6; round++) {
      const fresh = [...caster.effects, ...target.effects, ...mapEffects].filter((e) => !seen.has(e));
      if (!fresh.length) return;
      for (const e of fresh) { seen.add(e); effectImages(e, wanted); }
      for (const e of fresh) e.remove();
      for (let i = mapEffects.length - 1; i >= 0; i--) if (mapEffects[i]!.removed) mapEffects.splice(i, 1);
    }
  };
  try {
    spellStart(caster, w);
    drain();
    spellComplete(caster, w);
    drain();
    // PlayerObject.DrawEffects' attack overlays (warrior skills): record the draws through a stand-in effect world
    const real = effectWorld();
    setEffectWorld({ ...real, drawLocation: () => ({ x: 0, y: 0 }), draw: () => {},
      drawBlend: (l, i) => { let set = wanted.get(l); if (!set) wanted.set(l, (set = new Set())); set.add(i); } });
    try {
      for (const action of [A.Attack1, A.Attack4]) {
        caster.action = action;
        for (let d = 0; d < 8; d++) for (let fi = 0; fi < 10; fi++) { caster.direction = d; caster.frameIndex = fi; drawActionEffects(caster, w); }
      }
    } finally { setEffectWorld(real); }
  } catch (e) {
    console.warn(`[preload] spell ${spell}:`, e);
  }
  return wanted;
}

export class ArtWarmer {
  /** The user look the warm last ran for (null = not yet, or a library header was still loading). */
  private userKey: string | null = null;
  /** Monster images (+ the frame-set selectors) already warmed. */
  private readonly monsters = new Set<string>();
  /** Spell + level pairs already warmed. */
  private readonly spells = new Set<string>();

  /** The skill-bar spells (UserMagic.Key 1-16; 17-24 are the hero's): warm each spell + level once. */
  warmSpells(user: MapObj | null, magics: readonly { Spell: number; Level: number; Key: number }[]): void {
    if (!user) return;
    for (const m of magics) {
      if (m.Key < 1 || m.Key > 16) continue;
      const k = m.Spell + ":" + m.Level;
      if (this.spells.has(k)) continue;
      this.spells.add(k);
      for (const [path, set] of spellImages(user, m.Spell, m.Level)) prefetch(path, [...set]);
    }
  }

  /** Called once per rendered frame: cheap when nothing changed (a string compare + one Set lookup per monster). */
  warm(user: MapObj | null, objects: Iterable<MapObj>): void {
    if (user) this.warmUser(user);
    for (const ob of objects) if (ob.race === ObjectType.Monster && !ob.dead) this.warmMonster(ob);
  }

  private warmUser(u: MapObj): void {
    const k = `${u.id}|${u.look.class}|${u.look.gender}|${u.look.hair}|${u.look.armour}|${u.look.weapon}|${u.ridingMount}|${u.mountType}|${u.transformType}`;
    if (k === this.userKey) return;
    const wanted = new Map<string, number[]>();
    const actions = u.look.class === MirClass.Archer && u.hasClassWeapon ? [...USER_ACTIONS, ...ARCHER_ACTIONS] : USER_ACTIONS;
    for (const base of actions) {
      const act = u.ridingMount ? mountAction(base) : base;
      const L = u.playerLibraries(act);
      // playerLibraries swaps a missing armour / hair library for 00 / none once its header has answered: wait for it
      if ((L.body && lib(L.body) === undefined) || (L.hair && lib(L.hair) === undefined)) return;
      const frame = u.pickFrame(act);
      if (!frame) continue;
      add(wanted, L.body, frame, L.armourOff);
      add(wanted, L.hair, frame, L.hairOff);
      if (u.look.weapon >= 0) add(wanted, L.weapon1, frame, L.weaponOff);
      if (u.look.weapon !== -1) add(wanted, L.weapon2, frame, L.weaponOff);
      if (u.ridingMount && u.mountType >= 0) add(wanted, L.mount, frame, L.mountOff);
    }
    this.userKey = k;
    for (const [path, indices] of wanted) prefetch(path, indices);
  }

  private warmMonster(ob: MapObj): void {
    const k = `${ob.image}|${ob.stage}|${ob.loadDirection}`;
    if (this.monsters.has(k)) return;
    const path = monsterLibPath(ob.image);
    if (path === null) { this.monsters.add(k); return; }
    if (lib(path) === undefined) return; // the v3 frame table comes with the header: ask again next frame
    this.monsters.add(k);
    const fs = ob.frames;
    const wanted = new Map<string, number[]>();
    for (const a of MONSTER_ACTIONS) {
      const frame = fs.get(a);
      if (frame) add(wanted, path, frame, 0);
    }
    for (const [p, indices] of wanted) prefetch(p, indices);
  }
}
