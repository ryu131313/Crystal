// Buffs and poison: MapObject.AddBuffEffect / RemoveBuffEffect (buff visuals), PlayerObject.SetEffects (MagicShieldUp
// at spawn) and the SetAction tail that keeps the MagicShield / ElementalBarrier effects alive (struck flash),
// the poison DrawColour (PlayerObject / MonsterObject.Process) and the user's buff list (GameScene.AddBuff / RemoveBuff /
// PauseBuff, BuffDialog.BuffImage). Not ported: WingEffect >= 100 special effects, hero buffs, GM option texts.

import type { ClientBuff } from "../protocol/server.ts";
import { BuffType as B, MirAction as A, Monster, PoisonType as P, Spell, SpellEffect, StatName } from "../protocol/enums.generated.ts";
import { BuffEffect, Effect, Lib } from "./effects.ts";
import type { MapObj } from "./objects.ts";

// — Visuals on any player/monster —

/** MapObject.AddBuffEffect. `play` = SoundManager.PlaySound. */
export function addBuffEffect(ob: MapObj, type: number, play: (id: number) => void): void {
  if (ob.effects.some((e) => e instanceof BuffEffect && e.buffType === type)) return;
  const add = (e: Effect) => { ob.effects.push(e); return e; };
  const then = (first: Effect, next: () => Effect) => first.onComplete(() => add(next()));
  switch (type) {
    case B.Fury: add(new BuffEffect(Lib.Magic3, 190, 7, 1400, ob, true, type)); break;
    case B.ImmortalSkin: add(new BuffEffect(Lib.Magic3, 570, 5, 1400, ob, true, type)); break;
    case B.SwiftFeet: ob.sprint = true; break;
    case B.MoonLight: case B.DarkBody: ob.sneaking = true; break;
    case B.VampireShot: { const e = add(new BuffEffect(Lib.Magic3, 2110, 6, 1400, ob, true, type)); e.repeat = false; break; }
    case B.PoisonShot: { const e = add(new BuffEffect(Lib.Magic3, 2310, 7, 1400, ob, true, type)); e.repeat = false; break; }
    case B.EnergyShield: {
      const e = add(new BuffEffect(Lib.Magic2, 1880, 9, 900, ob, true, type));
      e.repeat = false;
      play(20000 + Spell.EnergyShield * 10 + 0);
      then(e, () => new BuffEffect(Lib.Magic2, 1900, 2, 800, ob, true, type));
      break;
    }
    case B.MagicBooster: add(new BuffEffect(Lib.Magic3, 90, 6, 1200, ob, true, type)); break;
    case B.PetEnhancer: add(new BuffEffect(Lib.Magic3, 230, 6, 1200, ob, true, type)); break;
    case B.GameMaster: add(new BuffEffect(Lib.CHumEffect(5), 0, 1, 1200, ob, true, type)); break;
    case B.GeneralMeowMeowShield: { const e = add(new BuffEffect(Lib.Monster(Monster.GeneralMeowMeow), 529, 7, 700, ob, true, type)); e.light = 1; play(8322); break; }
    case B.PowerBeadBuff: add(new BuffEffect(Lib.Monster(Monster.PowerUpBead), 64, 6, 600, ob, true, type)); break;
    case B.HornedArcherBuff: {
      const e = add(new BuffEffect(Lib.Monster(Monster.HornedArcher), 468, 6, 600, ob, true, type));
      e.repeat = false;
      then(e, () => new BuffEffect(Lib.Monster(Monster.HornedArcher), 474, 3, 1000, ob, true, type));
      break;
    }
    case B.ColdArcherBuff: {
      const e = add(new BuffEffect(Lib.Monster(Monster.HornedArcher), 477, 7, 700, ob, true, type));
      e.repeat = false;
      then(e, () => new BuffEffect(Lib.Monster(Monster.HornedArcher), 484, 3, 1000, ob, true, type));
      break;
    }
    case B.HornedWarriorShield: add(new BuffEffect(Lib.Monster(Monster.HornedWarrior), 912, 18, 1800, ob, true, type)); break;
    case B.HornedCommanderShield: {
      const e = add(new BuffEffect(Lib.Monster(Monster.HornedCommander), 1173, 1, 100, ob, true, type));
      e.repeat = false;
      e.light = 1;
      then(e, () => { const n = new BuffEffect(Lib.Monster(Monster.HornedCommander), 1174, 16, 1600, ob, true, type); n.light = 1; return n; });
      break;
    }
  }
}

/** MapObject.RemoveBuffEffect: lets the buff's effects finish their loop. */
export function removeBuffEffect(ob: MapObj, type: number): void {
  for (const e of ob.effects) if (e instanceof BuffEffect && e.buffType === type) e.repeat = false;
  if (type === B.SwiftFeet) ob.sprint = false;
  if (type === B.MoonLight || type === B.DarkBody) ob.sneaking = false;
}

/** MapObject.ProcessBuffs (spawn). */
export function processBuffs(ob: MapObj, play: (id: number) => void): void {
  for (const b of ob.buffs) addBuffEffect(ob, b, play);
}

/** PlayerObject.SetEffects' MagicShieldUp part (spawn with a shield up). */
export function setEffects(ob: MapObj): void {
  if (ob.currentEffect !== SpellEffect.MagicShieldUp) return;
  if (ob.shieldEffect) { ob.shieldEffect.clear(); ob.shieldEffect.remove(); }
  ob.magicShield = true;
  const e = new Effect(Lib.Magic, 3890, 3, 600, ob);
  e.repeat = true;
  ob.effects.push((ob.shieldEffect = e));
}

/** The end of PlayerObject.SetAction: re-create the barrier/shield loop, or flash it on Struck. */
export function actionRefresh(ob: MapObj): void {
  const struck = ob.action === A.Struck || ob.action === A.MountStruck;
  const loop = (lib: string, base: number, count: number, duration: number) => { const e = new Effect(lib, base, count, duration, ob); e.repeat = true; return e; };
  if (ob.elementalBarrier) {
    if (struck) {
      if (ob.elementalBarrierEffect) { ob.elementalBarrierEffect.clear(); ob.elementalBarrierEffect.remove(); }
      const flash = new Effect(Lib.Magic3, 1910, 5, 600, ob);
      flash.onComplete(() => ob.effects.push((ob.elementalBarrierEffect = loop(Lib.Magic3, 1890, 16, 3200))));
      ob.effects.push((ob.elementalBarrierEffect = flash));
    } else if (!ob.elementalBarrierEffect || ob.elementalBarrierEffect.removed) {
      ob.effects.push((ob.elementalBarrierEffect = loop(Lib.Magic3, 1890, 16, 3200)));
    }
  }
  if (ob.magicShield) {
    if (struck) {
      if (ob.shieldEffect) { ob.shieldEffect.clear(); ob.shieldEffect.remove(); }
      const flash = new Effect(Lib.Magic, 3900, 3, 600, ob);
      flash.onComplete(() => ob.effects.push((ob.shieldEffect = loop(Lib.Magic, 3890, 3, 600))));
      ob.effects.push((ob.shieldEffect = flash));
    } else if (!ob.shieldEffect || ob.shieldEffect.removed) {
      ob.effects.push((ob.shieldEffect = loop(Lib.Magic, 3890, 3, 600)));
    }
  }
}

/** PlayerObject / MonsterObject.Process: the poison DrawColour (first match wins). "gray" = DXManager grayscale (drawn as a grey tint here). */
export function poisonColour(poison: number): { r: number; g: number; b: number; a: number } | null {
  const c = (r: number, g: number, b: number) => ({ r: r / 255, g: g / 255, b: b / 255, a: 1 });
  if (poison & P.DelayedExplosion) return c(255, 165, 0);   // Orange
  if (poison & (P.Paralysis | P.LRParalysis)) return c(128, 128, 128); // Gray (grayscale on the desktop)
  if (poison & P.Frozen) return c(0, 0, 255);               // Blue
  if (poison & P.Blindness) return c(199, 21, 133);         // MediumVioletRed
  if (poison & (P.Stun | P.Dazed)) return c(255, 255, 0);   // Yellow
  if (poison & P.Slow) return c(128, 0, 128);               // Purple
  if (poison & P.Bleeding) return c(139, 0, 0);             // DarkRed
  if (poison & P.Red) return c(255, 0, 0);                  // Red
  if (poison & P.Green) return c(0, 128, 0);                // Green
  return null;
}

/** MapObject.DrawPoison: the dot colours, in draw order. */
export function poisonDots(poison: number): string[] {
  const out: string[] = [];
  if (poison & P.Green) out.push("#008000");
  if (poison & P.Red) out.push("#ff0000");
  if (poison & P.Bleeding) out.push("#8b0000");
  if (poison & P.Slow) out.push("#800080");
  if (poison & (P.Stun | P.Dazed)) out.push("#ffff00");
  if (poison & P.Blindness) out.push("#c71585");
  if (poison & P.Frozen) out.push("#0000ff");
  if (poison & (P.Paralysis | P.LRParalysis)) out.push("#808080");
  if (poison & P.DelayedExplosion) out.push("#ffa500");
  return out;
}

// — The user's buff list (BuffDialog) —

export interface UserBuff extends Omit<ClientBuff, "ExpireTime"> {
  /** Page-clock ms when it expires (GameScene.AddBuff adds CMain.Time), or ms left while paused. */
  expire: number;
}

export function userBuff(b: ClientBuff, now: number): UserBuff {
  const { ExpireTime, ...rest } = b;
  return { ...rest, expire: Number(ExpireTime) + (b.Paused ? 0 : now) };
}

/** BuffDialog.BuffImage -> [library, index] (>= 20000 MagIcon, >= 10000 Prguse2, else BuffIcon). */
export function buffIcon(type: number): [string, number] {
  const IMAGES: Record<number, number> = {
    [B.Fury]: 76, [B.Rage]: 49, [B.ImmortalSkin]: 80, [B.CounterAttack]: 7, [B.MagicBooster]: 73, [B.MagicShield]: 30, [B.Hiding]: 17, [B.ClearRing]: 17,
    [B.Haste]: 60, [B.SoulShield]: 13, [B.BlessedArmour]: 14, [B.ProtectionField]: 50, [B.UltimateEnhancer]: 35, [B.Curse]: 45, [B.EnergyShield]: 57,
    [B.SwiftFeet]: 67, [B.LightBody]: 68, [B.MoonLight]: 65, [B.DarkBody]: 70, [B.Concentration]: 96, [B.VampireShot]: 100, [B.PoisonShot]: 102,
    [B.MentalState]: 199, [B.RhinoPriestDebuff]: 217, [B.Blindness]: 226, [B.GameMaster]: 173, [B.General]: 182, [B.Exp]: 260, [B.Drop]: 162,
    [B.Gold]: 168, [B.Knapsack]: 235, [B.BagWeight]: 235, [B.Transform]: 241, [B.Mentor]: 248, [B.Mentee]: 248, [B.Lover]: 201, [B.Guild]: 203,
    [B.Rested]: 240, [B.TemporalFlux]: 261, [B.Skill]: 200, [B.Newbie]: 182, [B.Impact]: 249, [B.Magic]: 165, [B.Taoist]: 250, [B.Storm]: 170,
    [B.HealthAid]: 161, [B.ManaAid]: 169, [B.Defence]: 166, [B.MagicDefence]: 158, [B.WonderDrug]: 252,
  };
  let image = IMAGES[type] ?? 0;
  if (image >= 20000) return ["MagIcon", image - 20000];
  if (image >= 10000) { image -= 10000; return ["Prguse2", image]; }
  return ["BuffIcon", image];
}

const camel = (s: string) => s.replace(/([a-z])([A-Z])/g, "$1 $2"); // RegexFunctions.SeperateCamelCase

/** A short BuffDialog.BuffString: name, stats, time left. */
export function buffText(b: UserBuff, typeName: string, now: number): string {
  const lines = [camel(typeName)];
  for (const [k, v] of Object.entries(b.Stats)) if (v) lines.push(`${camel(StatName[Number(k)] ?? `Stat ${k}`)} ${v > 0 ? "+" : ""}${v}`);
  if (b.Infinite) lines.push("Permanent");
  else {
    const left = Math.max(0, b.Paused ? b.expire : b.expire - now);
    const s = Math.ceil(left / 1000);
    lines.push(`${b.Paused ? "Paused, " : ""}${s >= 3600 ? `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m` : s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`} left`);
  }
  return lines.join("\n");
}
