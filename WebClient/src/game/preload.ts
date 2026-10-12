// Art warm-up: the WoOOLcraft web client's local-player warm and monster block warm, on the Crystal libraries. The
// desktop's MLibrary.Draw loads an image synchronously the first time it is drawn; the browser cannot, so every first
// step in a new direction or a monster's first swing drew nothing until its frames arrived. This loads ahead, at low
// priority and one batched read per block (libraries.ts prefetch):
//   - the user: the actions they see constantly, all eight directions, every layer (body, hair, weapons, mount),
//     again whenever their look, mount or transform changes;
//   - each monster image on the map: its common actions, all eight directions, once per image.
// While a block is still loading MapView holds its first frame (or the last frame drawn): mapView.ts heldFrame.

import { MirAction as A, MirClass, ObjectType } from "../protocol/enums.generated.ts";
import { frameOffset, type Frame } from "./frames.ts";
import { lib, monsterLibPath, prefetch } from "./libraries.ts";
import type { MapObj } from "./objects.ts";
import { mountAction } from "./riding.ts";

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

export class ArtWarmer {
  /** The user look the warm last ran for (null = not yet, or a library header was still loading). */
  private userKey: string | null = null;
  /** Monster images (+ the frame-set selectors) already warmed. */
  private readonly monsters = new Set<string>();

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
