// Mount, transform and fishing rules, as pure functions over the state they need: GameScene.CanFish / CanRideAttack /
// CanRun's mount distance, the RidingMount action swap of PlayerObject.SetAction, the transform show flags of
// PlayerObject.SetLibraries, UserObject.RefreshBuffs' TransformType, and the RidingMount / Fishing input gates of
// GameScene.CheckInput / UseSpell. game.ts wires them; MapObj (objects.ts) keeps the per-object state.

import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { UserItem } from "../protocol/server.ts";
import { BuffType, MirAction as A } from "../protocol/enums.generated.ts";
import type { MapCells } from "../map/mapReader.ts";

/** Globals.FishingRodShapes. */
export const FISHING_ROD_SHAPES: readonly number[] = [49, 50];
/** Shared/Enums.cs MountSlot.Bells (MountSlot is not in enums.generated.ts). */
export const MOUNT_SLOT_BELLS = 1;
/** GameScene.CanFish: the cast lands three cells ahead. */
export const FISHING_CAST_RANGE = 3;
/** GameScene.CanFish: User.FishingTime + 1000 > CMain.Time refuses. */
export const FISHING_CAST_DELAY = 1000;

/** PlayerObject.HasFishingRod: Globals.FishingRodShapes.Contains(Weapon) (Weapon = the equipped weapon's Shape). */
export function hasFishingRod(weaponShape: number): boolean {
  return FISHING_ROD_SHAPES.includes(weaponShape);
}

/** PlayerObject.SetAction `if (RidingMount)` switch: the mounted action for a queued / idle action. */
export function mountAction(action: number): number {
  switch (action) {
    case A.Standing: return A.MountStanding;
    case A.Walking: return A.MountWalking;
    case A.Running: return A.MountRunning;
    case A.Struck: return A.MountStruck;
    case A.Attack1: return A.MountAttack;
    default: return action;
  }
}

export function isFishingAction(action: number): boolean {
  return action === A.FishingCast || action === A.FishingWait || action === A.FishingReel;
}

/** The looping idle actions of PlayerObject.SetAction's empty-feed branch (Standing / Stance, MountStanding when riding,
 *  FishingWait while fishing). game.ts's "user is idle" input gate should use this instead of Standing || Stance. */
export function isIdleAction(action: number): boolean {
  return action === A.Standing || action === A.Stance || action === A.MountStanding || action === A.FishingWait;
}

/** PlayerObject.SetLibraries transform switch: transforms that hide the mount and / or the fishing rod. */
export function transformShows(transformType: number): { showMount: boolean; showFishing: boolean } {
  if (transformType < 0) return { showMount: true, showFishing: true };
  switch (transformType) {
    case 4: case 5: case 7: case 8: case 26: case 28: case 29: case 30: case 31: case 32: case 33: case 34: case 35: case 36: case 37: case 38:
      return { showMount: true, showFishing: false };
    case 6: case 9:
      return { showMount: false, showFishing: false };
    default:
      return { showMount: true, showFishing: true };
  }
}

/** UserObject.RefreshBuffs: the user's TransformType comes from an unpaused BuffType.Transform (Values[0]), else -1.
 *  The server broadcasts S.TransformUpdate to the others only, so this is how the user's own transform is known. */
export function transformTypeFromBuffs(buffs: readonly { Type: number; Paused: boolean; Values: readonly number[] }[]): number {
  let t = -1;
  for (const b of buffs) {
    if (b.Type !== BuffType.Transform || b.Paused) continue;
    t = (b.Values[0] ?? -1) << 16 >> 16; // (short)buff.Values[0]
  }
  return t;
}

/** GameScene.CanRideAttack: a riding user may attack only with Bells in the mount's MountSlot.Bells slot.
 *  `mountItem` = the user's Equipment[EquipmentSlot.Mount]. */
export function canRideAttack(ridingMount: boolean, mountItem: UserItem | null | undefined): boolean {
  if (ridingMount) {
    if (!mountItem || mountItem.Slots.length < 4 || mountItem.Slots[MOUNT_SLOT_BELLS] == null) return false;
  }
  return true;
}

/** The parts of the user GameScene.CanFish reads. */
export interface Angler {
  /** User.HasFishingRod (hasFishingRod(look.weapon) / MapObj.hasFishingRod). */
  hasFishingRod: boolean;
  /** User.FishingTime: when the last C.FishingCast was sent (0 = never). */
  fishingTime: number;
  /** User.CurrentAction. */
  action: number;
  direction: number;
  transformType: number;
  location: WirePoint;
}

/** GameScene.CanFish(dir): a rod, 1 s since the last cast, standing (not mounted: MountStanding refuses), facing `dir`,
 *  not transformed into 6..9, and the cell three ahead is a fishing cell (MapCode: Light 100..119).
 *  The desktop indexes M2CellInfo without a bounds check (an exception off the map); here off the map is false. */
export function canFish(user: Angler, dir: number, map: MapCells, now: number): boolean {
  if (!user.hasFishingRod || user.fishingTime + FISHING_CAST_DELAY > now) return false;
  if (user.action !== A.Standing) return false;
  if (user.direction !== dir) return false;
  if (user.transformType >= 6 && user.transformType <= 9) return false;
  const dx = [0, 1, 1, 1, 0, -1, -1, -1][dir] ?? 0, dy = [-1, -1, 0, 1, 1, 1, 0, -1][dir] ?? 0;
  const x = user.location.X + dx * FISHING_CAST_RANGE, y = user.location.Y + dy * FISHING_CAST_RANGE;
  if (x < 0 || y < 0 || x >= map.width || y >= map.height) return false;
  return map.fishing[x * map.height + y] === 1;
}

/** GameScene.CheckInput / CanRun / AutoPath: a run covers 3 cells when riding (or sprinting, not ported), else 2. */
export function runDistance(ridingMount: boolean, sprint = false, sneaking = false): number {
  return ridingMount || (sprint && !sneaking) ? 3 : 2;
}

/** The parts of a player the input gates read. */
export interface Rider { dead: boolean; ridingMount: boolean; fishing: boolean }

/** GameScene.UseSpell: `if (actor.Dead || actor.RidingMount || actor.Fishing) return;` (spell keys / skill bar). */
export function canUseSpell(actor: Rider): boolean {
  return !actor.dead && !actor.ridingMount && !actor.fishing;
}

/** GameScene.CheckInput: `... || User.Fishing) return;` - no map input at all while fishing. */
export function inputBlockedByFishing(user: Rider): boolean {
  return user.fishing;
}

/** GameScene.CheckInput: `if (User.NextMagic != null && !User.RidingMount) UseMagic(...)` - a queued spell waits while riding. */
export function canCastNextMagic(user: Rider): boolean {
  return !user.ridingMount;
}

/** GameScene.CheckInput: `if (!CanRideAttack()) AutoHit = false;` and `if (AutoHit && !User.RidingMount)` (mining). */
export function canAutoHit(ridingMount: boolean, mountItem: UserItem | null | undefined): boolean {
  return !ridingMount && canRideAttack(ridingMount, mountItem);
}

/** GameScene.CheckInput: `if (CMain.Alt && !User.RidingMount)` Harvest. */
export function canHarvest(user: Rider): boolean {
  return !user.ridingMount;
}

/** GameScene.CheckInput archer range attacks: Class == Archer && HasClassWeapon && !RidingMount (the TargetObject path
 *  also needs !Fishing; Shift+left also needs !Dazed). A riding archer falls through to melee (Attack1 -> MountAttack,
 *  gated by canRideAttack). */
export function canRangeAttack(isArcherWithBow: boolean, user: Rider): boolean {
  return isArcherWithBow && !user.ridingMount && !user.fishing;
}
