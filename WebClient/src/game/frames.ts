// Port of Client/MirObjects/Frames.cs: the hard-coded FrameSets (Player, DefaultNPC, DefaultMonster). Monster and NPC
// libraries usually carry their own table (v3 .Lib frame table, `CrystalLib.frames`), which wins when present.
// DrawFrame = Start + OffSet * direction + frameIndex, OffSet = Count + Skip (same for the Effect* triple).

import type { LibFrame } from "../assets/crystalLib.ts";
import { MirAction as A, Monster } from "../protocol/enums.generated.ts";

export type Frame = LibFrame;
export type FrameSet = ReadonlyMap<number, Frame>;

function f(start: number, count: number, skip: number, interval: number, effectStart = 0, effectCount = 0, effectSkip = 0, effectInterval = 0, reverse = false): Frame {
  return { start, count, skip, interval, effectStart, effectCount, effectSkip, effectInterval, reverse, blend: false };
}

/** A frame with Frame.Blend set (`{ Blend = true }` in Frames.cs). */
function fb(start: number, count: number, skip: number, interval: number, blend: boolean, reverse = false): Frame {
  return { ...f(start, count, skip, interval, 0, 0, 0, 0, reverse), blend };
}

export const frameOffset = (fr: Frame) => fr.count + fr.skip;
export const effectOffset = (fr: Frame) => fr.effectCount + fr.effectSkip;

export const DefaultNPC: FrameSet = new Map([
  [A.Standing, f(0, 4, 0, 450)],
  [A.Harvest, f(12, 10, 0, 200)],
]);

export const DefaultMonster: FrameSet = new Map([
  [A.Standing, f(0, 4, 0, 500)],
  [A.Walking, f(32, 6, 0, 100)],
  [A.Attack1, f(80, 6, 0, 100)],
  [A.Struck, f(128, 2, 0, 200)],
  [A.Die, f(144, 10, 0, 100)],
  [A.Dead, f(153, 1, 9, 1000)],
  [A.Revive, f(144, 10, 0, 100, 0, 0, 0, 0, true)],
]);

export const Player: FrameSet = new Map([
  [A.Standing, f(0, 4, 0, 500, 0, 8, 0, 250)],
  [A.Walking, f(32, 6, 0, 100, 64, 6, 0, 100)],
  [A.Running, f(80, 6, 0, 100, 112, 6, 0, 100)],
  [A.Stance, f(128, 1, 0, 1000, 160, 1, 0, 1000)],
  [A.Stance2, f(300, 1, 5, 1000, 332, 1, 5, 1000)],
  [A.Attack1, f(136, 6, 0, 100, 168, 6, 0, 100)],
  [A.Attack2, f(184, 6, 0, 100, 216, 6, 0, 100)],
  [A.Attack3, f(232, 8, 0, 100, 264, 8, 0, 100)],
  [A.Attack4, f(416, 6, 0, 100, 448, 6, 0, 100)],
  [A.Spell, f(296, 6, 0, 100, 328, 6, 0, 100)],
  [A.Harvest, f(344, 2, 0, 300, 376, 2, 0, 300)],
  [A.Struck, f(360, 3, 0, 100, 392, 3, 0, 100)],
  [A.Die, f(384, 4, 0, 100, 416, 4, 0, 100)],
  [A.Dead, f(387, 1, 3, 1000, 419, 1, 3, 1000)],
  [A.Revive, f(384, 4, 0, 100, 416, 4, 0, 100, true)],
  [A.Mine, f(184, 6, 0, 100, 216, 6, 0, 100)],
  [A.Lunge, f(139, 1, 5, 1000, 300, 1, 5, 1000)],
  [A.Sneek, f(464, 6, 0, 100, 496, 6, 0, 100)],
  [A.DashAttack, f(80, 3, 3, 100, 112, 3, 3, 100)],
  [A.WalkingBow, f(0, 6, 0, 100, 0, 6, 0, 100)],
  [A.RunningBow, f(48, 6, 0, 100, 48, 6, 0, 100)],
  [A.AttackRange1, f(96, 8, 0, 100, 96, 8, 0, 100)],
  [A.AttackRange2, f(160, 8, 0, 100, 160, 8, 0, 100)],
  [A.AttackRange3, f(224, 8, 0, 100, 224, 8, 0, 100)],
  [A.Jump, f(288, 8, 0, 100, 288, 8, 0, 100)],
  [A.MountStanding, f(416, 4, 0, 500, 448, 4, 0, 500)],
  [A.MountWalking, f(448, 8, 0, 100, 480, 8, 0, 500)],
  [A.MountRunning, f(512, 6, 0, 100, 544, 6, 0, 100)],
  [A.MountStruck, f(560, 3, 0, 100, 592, 3, 0, 100)],
  [A.MountAttack, f(584, 6, 0, 100, 616, 6, 0, 100)],
  [A.FishingCast, f(632, 8, 0, 100)],
  [A.FishingWait, f(696, 6, 0, 120)],
  [A.FishingReel, f(744, 8, 0, 100)],
]);

// — The hard-coded monster FrameSet lists (Frames.cs: DragonStatue, GreatFoxSpirit, HellBomb, CaveStatue). Skip = -Count,
//   so OffSet = 0 and the direction does not move the frame; the list index is the direction / stage / bomb instead. —

const dragonStatue = (start: number): FrameSet => new Map([
  [A.Standing, f(start, 1, -1, 1000)],
  [A.AttackRange1, f(start, 1, -1, 120)],
  [A.Struck, f(start, 1, -1, 200)],
]);
export const DragonStatue: FrameSet[] = [300, 301, 302, 320, 321, 322].map(dragonStatue);

const greatFoxSpirit = (base: number): FrameSet => new Map([
  [A.Standing, f(base, 20, -20, 100)],
  [A.Attack1, f(base + 22, 8, -8, 120)],
  [A.Struck, f(base + 20, 2, -2, 200)],
  [A.Die, f(300, 18, -18, 120)],
  [A.Dead, f(317, 1, -1, 1000)],
  [A.Revive, f(300, 18, -18, 150, 0, 0, 0, 0, true)],
]);
export const GreatFoxSpirit: FrameSet[] = [0, 60, 120, 180, 240].map(greatFoxSpirit);

const hellBomb = (start: number): FrameSet => new Map([
  [A.Standing, fb(start, 9, -9, 100, true)],
  [A.Attack1, fb(999, 1, -1, 120, true)],
  [A.Struck, fb(start, 9, -9, 100, true)],
]);
export const HellBomb: FrameSet[] = [52, 70, 88].map(hellBomb);

const caveStatue = (start: number): FrameSet => new Map([
  [A.Standing, fb(start, 1, -1, 100, false)],
  [A.Struck, fb(start, 1, -1, 100, false)],
  [A.Die, fb(start + 2, 8, -8, 100, false)],
  [A.Dead, fb(start + 9, 1, -1, 100, false)],
]);
export const CaveStatue: FrameSet[] = [0, 18].map(caveStatue);

/** MonsterObject.Load's "Frames" switch: the hard-coded lists for GreatFoxSpirit (by Stage = ExtraByte), DragonStatue and
 *  CaveStatue (by the Direction at Load), the HellBombs (by image); otherwise the body library's v3 frame table
 *  (`BodyLibrary.Frames ?? FrameSet.DefaultMonster`). An index past a list (the desktop throws) falls back to the library. */
export function monsterFrames(image: number, loadDirection: number, stage: number, libFrames: FrameSet | null | undefined): FrameSet {
  let fixed: FrameSet | undefined;
  switch (image) {
    case Monster.GreatFoxSpirit: fixed = GreatFoxSpirit[stage]; break;
    case Monster.DragonStatue: fixed = DragonStatue[loadDirection]; break;
    case Monster.HellBomb1: case Monster.HellBomb2: case Monster.HellBomb3: fixed = HellBomb[image - 903]; break;
    case Monster.CaveStatue: fixed = CaveStatue[loadDirection]; break;
  }
  return fixed ?? libFrames ?? DefaultMonster;
}
