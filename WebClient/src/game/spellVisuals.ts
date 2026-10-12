// Spell visuals: port of the spell-effect parts of Client/MirObjects/PlayerObject.cs (SetAction's Spell case, ProcessFrames'
// Spell completion, CreateProjectile, DrawEffects' attack overlays), GameScene.cs (ObjectEffect, MapEffect, ObjectProjectile)
// and Client/MirObjects/SpellObject.cs. Library indices, counts and durations are copied verbatim from the C#.

import type { WirePoint } from "../protocol/binaryCodec.ts";
import type { PacketOf } from "../protocol/server.ts";
import { MirAction, MirGender, Monster, ObjectType, Spell, SpellEffect } from "../protocol/enums.generated.ts";
import { CELL_W, CELL_H, pointMove, type MapObj } from "./objects.ts";
import { DelayedExplosionEffect, Effect, Lib, Missile, effectWorld, maxDistance } from "./effects.ts";

export interface SpellWorld {
  getObject(id: number): MapObj | undefined;   // MapControl.GetObject
  validPoint(p: WirePoint): boolean;            // MapControl.ValidPoint
  user(): MapObj | null;                        // MapObject.User
  now(): number;                                // CMain.Time
  playSound(id: number): void;                  // SoundManager.PlaySound (no-op for now; keep the calls with the same ids)
  addMapEffect(e: Effect): void;                // MapControl.Effects.Add
}

/** SoundList.Teleport (Client/MirSounds/SoundList.cs). */
const SOUND_TELEPORT = 10110;

/** CMain.Random.Next(n). */
const randomNext = (n: number) => Math.floor(Math.random() * n);

/** PlayerObject state the web MapObj does not have yet (MagicShield, ElementalBarrier, ...). */
// TODO MapObj: shieldEffect, magicShield, elementalBarrierEffect, elementalBarrier, hasElements, slashingBurstTime, blizzardStopTime, reincarnationStopTime
const px = (ob: MapObj) => ob as any;

/** Port of PlayerObject.SetAction's `case MirAction.AttackRange2: case MirAction.Spell:` switch (the cast-start effects). */
export function spellStart(ob: MapObj, w: SpellWorld): void {
  const sp = ob.spell;
  const dur = ob.actionDuration; // Frame.Count * FrameInterval
  const frameInterval = ob.frame?.interval ?? 0;
  const effectFrameInterval = ob.frame?.effectInterval ?? 0;
  const dir = ob.direction;
  const male = ob.look.gender === MirGender.Male;

  switch (sp) {
    case Spell.FireBall:
      ob.effects.push(new Effect(Lib.Magic, 0, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Healing:
      ob.effects.push(new Effect(Lib.Magic, 200, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Repulsion:
      ob.effects.push(new Effect(Lib.Magic, 900, 6, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.ElectricShock:
      ob.effects.push(new Effect(Lib.Magic, 1560, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Poisoning:
      ob.effects.push(new Effect(Lib.Magic, 600, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.GreatFireBall:
      ob.effects.push(new Effect(Lib.Magic, 400, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.HellFire:
      ob.effects.push(new Effect(Lib.Magic, 920, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.ThunderBolt:
      ob.effects.push(new Effect(Lib.Magic2, 20, 3, 300, ob));
      break;
    case Spell.SoulFireBall:
      w.playSound(20000 + sp * 10);
      break;
    case Spell.SummonSkeleton:
      ob.effects.push(new Effect(Lib.Magic, 1500, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.StormEscape:
      ob.effects.push(new Effect(Lib.Magic3, 590, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Teleport:
      ob.effects.push(new Effect(Lib.Magic, 1590, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Blink:
      ob.effects.push(new Effect(Lib.Magic, 1590, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Hiding:
      ob.effects.push(new Effect(Lib.Magic, 1520, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Haste:
      ob.effects.push(new Effect(Lib.Magic2, 2140 + dir * 10, 6, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Fury:
      ob.effects.push(new Effect(Lib.Magic3, 200, 8, 8 * frameInterval, ob));
      ob.effects.push(new Effect(Lib.Magic3, 187, 10, 10 * frameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.ImmortalSkin:
      ob.effects.push(new Effect(Lib.Magic3, 550, 17, dur * 4, ob));
      ob.effects.push(new Effect(Lib.Magic3, 570, 5, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.FireBang:
      ob.effects.push(new Effect(Lib.Magic, 1650, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.FireWall:
      ob.effects.push(new Effect(Lib.Magic, 1620, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.HealingCircle:
      ob.effects.push(new Effect(Lib.Magic3, 620, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.MoonMist:
      w.addMapEffect(new Effect(Lib.Magic3, 680, 25, 1800, ob.location));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.TrapHexagon:
      ob.effects.push(new Effect(Lib.Magic, 1380, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.EnergyRepulsor:
      ob.effects.push(new Effect(Lib.Magic2, 190, 6, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.FireBurst:
      ob.effects.push(new Effect(Lib.Magic2, 2320, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.FlameDisruptor:
      ob.effects.push(new Effect(Lib.Magic2, 130, 6, dur, ob));
      break;
    case Spell.SummonShinsu:
      ob.effects.push(new Effect(Lib.Magic2, 0, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.UltimateEnhancer:
      ob.effects.push(new Effect(Lib.Magic2, 160, 15, 1000, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.FrostCrunch:
      ob.effects.push(new Effect(Lib.Magic2, 400, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Purification:
      ob.effects.push(new Effect(Lib.Magic2, 600, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.FlameField:
      w.addMapEffect(new Effect(Lib.Magic2, 910, 23, 1800, ob.location));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Trap:
      ob.effects.push(new Effect(Lib.Magic2, 2340, 11, 11 * frameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.MoonLight:
      ob.effects.push(new Effect(Lib.Magic2, 2380, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.SwiftFeet:
      ob.effects.push(new Effect(Lib.Magic2, 2440, 16, 16 * effectFrameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.LightBody:
      ob.effects.push(new Effect(Lib.Magic2, 2470, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.PoisonSword:
      ob.effects.push(new Effect(Lib.Magic2, 2490 + dir * 10, 10, dur + 500, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.DarkBody:
      ob.effects.push(new Effect(Lib.Magic2, 2580, 10, 10 * frameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.ThunderStorm:
      w.addMapEffect(new Effect(Lib.Magic, 1680, 10, dur, ob.location));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.MassHealing:
      ob.effects.push(new Effect(Lib.Magic, 1790, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.IceStorm:
      ob.effects.push(new Effect(Lib.Magic, 3840, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.MagicShield:
      ob.effects.push(new Effect(Lib.Magic, 3880, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.TurnUndead:
      ob.effects.push(new Effect(Lib.Magic, 3920, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.MagicBooster:
      ob.effects.push(new Effect(Lib.Magic3, 80, 9, 9 * frameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.PetEnhancer:
      ob.effects.push(new Effect(Lib.Magic3, 200, 8, 8 * frameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Revelation:
      ob.effects.push(new Effect(Lib.Magic, 3960, 20, 1200, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.ProtectionField:
      ob.effects.push(new Effect(Lib.Magic2, 1520, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Rage:
      ob.effects.push(new Effect(Lib.Magic2, 1510, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Vampirism:
      ob.effects.push(new Effect(Lib.Magic2, 1040, 7, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.LionRoar:
    case Spell.BattleCry:
      ob.effects.push(new Effect(Lib.Magic2, 710, 20, 1200, ob));
      w.playSound(20000 + sp * 10 + (male ? 0 : 1));
      break;
    case Spell.TwinDrakeBlade:
      ob.effects.push(new Effect(Lib.Magic2, 210, 6, 500, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Entrapment:
      ob.effects.push(new Effect(Lib.Magic2, 990, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.BladeAvalanche:
      ob.effects.push(new Effect(Lib.Magic2, 740 + dir * 20, 15, 15 * frameInterval, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.SlashingBurst:
      w.addMapEffect(new Effect(Lib.Magic2, 1700 + dir * 10, 9, 9 * frameInterval, ob.location, w.now() + 300));
      w.playSound(20000 + sp * 10);
      px(ob).slashingBurstTime = w.now() + 2000; // TODO MapObj
      break;
    case Spell.CounterAttack:
      w.playSound(20000 + sp * 10 + 5);
      ob.effects.push(new Effect(Lib.Magic, 3480 + dir * 10, 10, 10 * frameInterval, ob));
      ob.effects.push(new Effect(Lib.Magic3, 140, 2, 2 * frameInterval, ob));
      break;
    case Spell.CrescentSlash:
      ob.effects.push(new Effect(Lib.Magic2, 2620 + dir * 20, 20, 20 * frameInterval, ob));
      w.playSound(20000 + sp * 10 + (male ? 0 : 1));
      break;
    case Spell.FlashDash:
      w.playSound(20000 + sp * 10 + (male ? 0 : 1));
      // not ported: FrameInterval/EffectFrameInterval scaled by User.AttackSpeed (web frames are shared FrameSet entries, no per-object interval yet)
      break;
    case Spell.Mirroring:
      ob.effects.push(new Effect(Lib.Magic2, 650, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Blizzard:
      ob.effects.push(new Effect(Lib.Magic2, 1540, 8, dur, ob));
      w.playSound(20000 + sp * 10);
      px(ob).blizzardStopTime = w.now() + 3000; // TODO MapObj
      break;
    case Spell.MeteorStrike:
      ob.effects.push(new Effect(Lib.Magic2, 1590, 10, dur, ob));
      w.playSound(20000 + sp * 10);
      px(ob).blizzardStopTime = w.now() + 3000; // TODO MapObj
      break;
    case Spell.Reincarnation:
      px(ob).reincarnationStopTime = w.now() + 6000; // TODO MapObj
      break;
    case Spell.HeavenlySword:
      ob.effects.push(new Effect(Lib.Magic2, 2230 + dir * 10, 8, 800, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.ElementalBarrier:
      if (px(ob).hasElements && !px(ob).elementalBarrier) { // TODO MapObj
        ob.effects.push(new Effect(Lib.Magic3, 1880, 8, dur, ob));
        w.playSound(20000 + sp * 10);
      }
      break;
    case Spell.PoisonShot:
      ob.effects.push(new Effect(Lib.Magic3, 2300, 8, 1000, ob));
      break;
    case Spell.OneWithNature:
      w.addMapEffect(new Effect(Lib.Magic3, 2710, 8, 1200, ob.location));
      w.playSound(20000 + 139 * 10);
      break;
    case Spell.FireBounce:
      ob.effects.push(new Effect(Lib.Magic, 400, 10, dur, ob));
      w.playSound(20000 + Spell.GreatFireBall * 10);
      break;
    case Spell.MeteorShower:
      ob.effects.push(new Effect(Lib.Magic, 400, 10, dur, ob));
      w.playSound(20000 + Spell.GreatFireBall * 10);
      break;
  }
}

/** Missile.Complete handler shared by the fire-ball style spells: a hit effect on a live target plus a sound. */
function onHit(missile: Missile, lib: string, baseIndex: number, count: number, duration: number, w: SpellWorld, sound: number): void {
  if (missile.target === null) return;
  missile.onComplete(() => {
    if (missile.target!.action === MirAction.Dead) return;
    missile.target!.effects.push(new Effect(lib, baseIndex, count, duration, missile.target!));
    w.playSound(sound);
  });
}

/** Port of PlayerObject.ProcessFrames' `case MirAction.Spell:` completion (`if (Cast) { switch (Spell) ... }`). */
export function spellComplete(ob: MapObj, w: SpellWorld): void {
  const sp = ob.spell;
  const target = w.getObject(ob.targetId) ?? null;
  const frameInterval = ob.frame?.interval ?? 0;
  let missile: Missile;
  let effect: Effect;

  switch (sp) {
    case Spell.FireBall:
      w.playSound(20000 + sp * 10 + 1);
      missile = createProjectile(ob, w, 10, Lib.Magic, true, 6, 30, 4);
      onHit(missile, Lib.Magic, 170, 10, 600, w, 20000 + sp * 10 + 2);
      break;
    case Spell.GreatFireBall:
      w.playSound(20000 + sp * 10 + 1);
      missile = createProjectile(ob, w, 410, Lib.Magic, true, 6, 30, 4);
      onHit(missile, Lib.Magic, 570, 10, 600, w, 20000 + Spell.GreatFireBall * 10 + 2);
      break;
    case Spell.Healing:
      w.playSound(20000 + sp * 10 + 1);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic, 370, 10, 800, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic, 370, 10, 800, target));
      break;
    case Spell.ElectricShock:
      w.playSound(20000 + sp * 10 + 1);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic, 1570, 10, 1000, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic, 1570, 10, 1000, target));
      break;
    case Spell.Poisoning:
      w.playSound(20000 + sp * 10 + 1);
      if (target !== null) target.effects.push(new Effect(Lib.Magic, 770, 10, 1000, target));
      break;
    case Spell.HellFire: {
      w.playSound(20000 + sp * 10 + 1);
      let dest = ob.location;
      for (let i = 0; i < 4; i++) {
        dest = pointMove(dest, ob.direction, 1);
        if (!w.validPoint(dest)) break;
        effect = new Effect(Lib.Magic, 930, 6, 500, dest);
        effect.rate = 0.7;
        effect.setStart(w.now() + i * 50);
        w.addMapEffect(effect);
      }
      if (ob.spellLevel === 3) {
        let dir = (ob.direction + 1) % 8;
        dest = ob.location;
        for (let r = 0; r < 4; r++) {
          dest = pointMove(dest, dir, 1);
          if (!w.validPoint(dest)) break;
          effect = new Effect(Lib.Magic, 930, 6, 500, dest);
          effect.rate = 0.7;
          effect.setStart(w.now() + r * 50);
          w.addMapEffect(effect);
        }
        dir = (ob.direction - 1 + 8) % 8;
        dest = ob.location;
        for (let r = 0; r < 4; r++) {
          dest = pointMove(dest, dir, 1);
          if (!w.validPoint(dest)) break;
          effect = new Effect(Lib.Magic, 930, 6, 500, dest);
          effect.rate = 0.7;
          effect.setStart(w.now() + r * 50);
          w.addMapEffect(effect);
        }
      }
      break;
    }
    case Spell.ThunderBolt:
      w.playSound(20000 + sp * 10);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic2, 10, 5, 400, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic2, 10, 5, 400, target));
      break;
    case Spell.SoulFireBall:
      w.playSound(20000 + sp * 10 + 1);
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      onHit(missile, Lib.Magic, 1360, 10, 600, w, 20000 + Spell.SoulFireBall * 10 + 2);
      break;
    case Spell.EnergyShield:
      // commented out in the C# (Magic2 1880)
      break;
    case Spell.FireBang:
      w.playSound(20000 + sp * 10 + 1);
      w.addMapEffect(new Effect(Lib.Magic, 1660, 10, 1000, ob.targetPoint));
      break;
    case Spell.MassHiding: {
      w.playSound(20000 + sp * 10);
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      missile.explode = true;
      missile.onComplete(() => {
        w.addMapEffect(new Effect(Lib.Magic, 1540, 10, 800, ob.targetPoint));
        w.playSound(20000 + Spell.MassHiding * 10 + 1);
      });
      break;
    }
    case Spell.SoulShield: {
      w.playSound(20000 + sp * 10);
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      missile.explode = true;
      missile.onComplete(() => {
        w.addMapEffect(new Effect(Lib.Magic, 1320, 15, 1200, ob.targetPoint));
        w.playSound(20000 + Spell.SoulShield * 10 + 1);
      });
      break;
    }
    case Spell.BlessedArmour: {
      w.playSound(20000 + sp * 10);
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      missile.explode = true;
      missile.onComplete(() => {
        w.addMapEffect(new Effect(Lib.Magic, 1340, 15, 1200, ob.targetPoint));
        w.playSound(20000 + Spell.BlessedArmour * 10 + 1);
      });
      break;
    }
    case Spell.FireWall:
      w.playSound(20000 + sp * 10 + 1);
      break;
    case Spell.HealingCircle:
      w.playSound(20000 + sp * 10 + 1);
      w.addMapEffect(new Effect(Lib.Magic3, 620, 10, 1200, ob.targetPoint));
      break;
    case Spell.MassHealing:
      w.playSound(20000 + sp * 10 + 1);
      w.addMapEffect(new Effect(Lib.Magic, 1800, 10, 1000, ob.targetPoint));
      break;
    case Spell.IceStorm:
      w.playSound(20000 + sp * 10 + 1);
      w.addMapEffect(new Effect(Lib.Magic, 3850, 20, 1300, ob.targetPoint));
      break;
    case Spell.TurnUndead:
      w.playSound(20000 + sp * 10 + 1);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic, 3930, 15, 1000, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic, 3930, 15, 1000, target));
      break;
    case Spell.IceThrust: {
      const location = pointMove(ob.location, ob.direction, 1);
      w.addMapEffect(new Effect(Lib.Magic2, 1790 + ob.direction * 10, 10, 10 * frameInterval, location));
      w.playSound(20000 + sp * 10);
      break;
    }
    case Spell.Revelation:
      w.playSound(20000 + sp * 10 + 1);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic, 3990, 10, 1000, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic, 3990, 10, 1000, target));
      break;
    case Spell.FlameDisruptor:
      w.playSound(20000 + sp * 10);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic2, 140, 10, 600, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic2, 140, 10, 600, target));
      break;
    case Spell.CatTongue:
      w.playSound(20000 + sp * 10);
      missile = createProjectile(ob, w, 260, Lib.Magic3, true, 6, 30, 4);
      onHit(missile, Lib.Magic3, 420, 8, 800, w, 20000 + Spell.CatTongue * 10 + 1);
      break;
    case Spell.FrostCrunch:
      w.playSound(20000 + sp * 10 + 1);
      missile = createProjectile(ob, w, 410, Lib.Magic2, true, 4, 30, 6);
      onHit(missile, Lib.Magic2, 570, 8, 600, w, 20000 + Spell.FrostCrunch * 10 + 2);
      break;
    case Spell.Purification:
      if (target === null) w.addMapEffect(new Effect(Lib.Magic2, 620, 10, 800, ob.targetPoint));
      else target.effects.push(new Effect(Lib.Magic2, 620, 10, 800, target));
      break;
    case Spell.Curse: {
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      missile.explode = true;
      missile.onComplete(() => {
        w.addMapEffect(new Effect(Lib.Magic2, 950, 24, 2000, ob.targetPoint));
        w.playSound(20000 + Spell.Curse * 10);
      });
      break;
    }
    case Spell.Hallucination:
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 48, 7);
      onHit(missile, Lib.Magic2, 1110, 10, 1000, w, 20000 + sp * 10);
      break;
    case Spell.Lightning:
      ob.effects.push(new Effect(Lib.Magic, 970 + ob.direction * 20, 6, ob.actionDuration, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.Vampirism:
      w.playSound(20000 + sp * 10 + 1);
      if (target === null) w.addMapEffect(new Effect(Lib.Magic2, 1060, 20, 1000, ob.targetPoint));
      else {
        target.effects.push(effect = new Effect(Lib.Magic2, 1060, 20, 1000, target));
        effect.onComplete(() => {
          w.playSound(20000 + Spell.Vampirism * 10 + 2);
          ob.effects.push(new Effect(Lib.Magic2, 1090, 10, 500, ob));
        });
      }
      break;
    case Spell.PoisonCloud:
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      missile.explode = true;
      missile.onComplete(() => {
        w.playSound(20000 + sp * 10);
      });
      break;
    case Spell.Blizzard:
      w.playSound(20000 + sp * 10 + 1);
      break;
    case Spell.MeteorStrike:
      w.playSound(20000 + sp * 10 + 1);
      w.playSound(20000 + sp * 10 + 2);
      break;
    case Spell.Reincarnation:
      px(ob).reincarnationStopTime = 0; // TODO MapObj
      break;
    case Spell.SummonHolyDeva:
      ob.effects.push(new Effect(Lib.Magic, 1500, 10, ob.actionDuration, ob));
      w.playSound(20000 + sp * 10);
      break;
    case Spell.UltimateEnhancer:
      if (target !== null && target !== w.user()) target.effects.push(new Effect(Lib.Magic2, 160, 15, 1000, target));
      break;
    case Spell.Plague: {
      missile = createProjectile(ob, w, 1160, Lib.Magic, true, 3, 30, 7);
      missile.explode = true;
      missile.onComplete(() => {
        w.addMapEffect(new Effect(Lib.Magic3, 110, 10, 1200, ob.targetPoint));
        w.playSound(20000 + Spell.Plague * 10 + 3);
      });
      break;
    }
    case Spell.TrapHexagon:
      if (target !== null) w.playSound(20000 + Spell.TrapHexagon * 10 + 1);
      break;
    case Spell.Trap:
      if (target !== null) w.playSound(20000 + Spell.Trap * 10 + 1);
      break;
    case Spell.CrescentSlash:
      w.playSound(20000 + sp * 10 + 2);
      break;
    case Spell.NapalmShot:
      w.playSound(20000 + sp * 10 + 1);
      w.addMapEffect(new Effect(Lib.Magic3, 1660, 10, 1000, ob.targetPoint));
      break;
    case Spell.FireBounce:
      w.playSound(20000 + Spell.GreatFireBall * 10 + 1);
      missile = createProjectile(ob, w, 410, Lib.Magic, true, 6, 30, 4);
      onHit(missile, Lib.Magic, 570, 10, 600, w, 20000 + Spell.GreatFireBall * 10 + 2);
      break;
    case Spell.MeteorShower: {
      w.playSound(20000 + Spell.GreatFireBall * 10 + 1);
      const targetIDs = [ob.targetId, ...ob.secondaryTargetIds];
      for (const targetID of targetIDs) {
        const m = createProjectile(ob, w, 410, Lib.Magic, true, 6, 30, 4, undefined, undefined, undefined, targetID);
        onHit(m, Lib.Magic, 570, 10, 600, w, 20000 + Spell.GreatFireBall * 10 + 2);
      }
      break;
    }
  }

  ob.cast = false;
}

/** Port of PlayerObject.CreateProjectile (MonsterObject.CreateProjectile also passes direction16 to the Missile; the player one does not). */
export function createProjectile(ob: MapObj, w: SpellWorld, baseIndex: number, lib: string, blend: boolean, count: number, interval: number, skip: number,
  lightDistance = 6, direction16 = true, lightColour?: [number, number, number], targetID = 0): Missile {
  if (targetID === 0) targetID = ob.targetId;
  const target = w.getObject(targetID) ?? null;
  let targetPoint = ob.targetPoint;
  if (target !== null) targetPoint = target.location;
  const duration = maxDistance(ob.location, targetPoint) * 50;
  const missile = new Missile(lib, baseIndex, Math.trunc(duration / interval), duration, ob, targetPoint, ob.race === ObjectType.Monster ? direction16 : true);
  missile.target = target;
  missile.interval = interval;
  missile.frameCount = count;
  missile.blend = blend;
  missile.skip = skip;
  missile.light = lightDistance;
  missile.lightColour = lightColour ?? [255, 255, 255];
  ob.effects.push(missile);
  return missile;
}

/** Port of the `switch (CurrentAction)` attack overlays at the end of PlayerObject.DrawEffects (caller draws ob.effects first). */
export function drawActionEffects(ob: MapObj, _w: SpellWorld): void {
  const ew = effectWorld();
  const at = ew.drawLocation(ob);
  const dir = ob.direction, lvl = ob.spellLevel, fi = ob.frameIndex;
  const blend = (lib: string, index: number) => ew.drawBlend(lib, index, at.x, at.y, true, 0.7);
  switch (ob.action) {
    case MirAction.Attack1:
      switch (ob.spell) {
        case Spell.Slaying: blend(Lib.Magic, 1820 + dir * 10 + lvl * 90 + fi); break;
        case Spell.DoubleSlash: blend(Lib.Magic2, 1960 + dir * 10 + fi); break;
        case Spell.Thrusting: blend(Lib.Magic, 2190 + dir * 10 + lvl * 90 + fi); break;
        case Spell.HalfMoon: blend(Lib.Magic, 2560 + dir * 10 + lvl * 90 + fi); break;
        case Spell.TwinDrakeBlade: blend(Lib.Magic2, 220 + dir * 20 + fi); break;
        case Spell.CrossHalfMoon: blend(Lib.Magic2, 40 + dir * 10 + fi); break;
        case Spell.FlamingSword: blend(Lib.Magic, 3480 + dir * 10 + fi); break;
      }
      break;
    case MirAction.Attack4:
      switch (ob.spell) {
        case Spell.DoubleSlash: blend(Lib.Magic2, 2050 + dir * 10 + fi); break;
        case Spell.TwinDrakeBlade: blend(Lib.Magic2, 226 + dir * 20 + fi); break;
        case Spell.FlamingSword: blend(Lib.Magic, 3480 + dir * 10 + fi); break;
      }
      break;
  }
}

/** Port of GameScene.ObjectEffect. */
export function objectEffect(p: PacketOf<"ObjectEffect">, w: SpellWorld): void {
  const ob = w.getObject(p.ObjectID);
  if (!ob) return;
  let e: Effect;
  switch (p.Effect) {
    case SpellEffect.FurbolgWarriorCritical:
      ob.effects.push(new Effect(Lib.Monster(Monster.FurbolgWarrior), 400, 6, 600, ob));
      w.playSound(20000 + Spell.FatalSword * 10);
      break;
    case SpellEffect.FatalSword:
      ob.effects.push(new Effect(Lib.Magic2, 1940, 4, 400, ob));
      w.playSound(20000 + Spell.FatalSword * 10);
      break;
    case SpellEffect.StormEscape:
      ob.effects.push(new Effect(Lib.Magic3, 610, 10, 600, ob));
      w.playSound(SOUND_TELEPORT);
      break;
    case SpellEffect.Teleport:
      ob.effects.push(new Effect(Lib.Magic, 1600, 10, 600, ob));
      w.playSound(SOUND_TELEPORT);
      break;
    case SpellEffect.Healing:
      w.playSound(20000 + Spell.Healing * 10 + 1);
      ob.effects.push(new Effect(Lib.Magic, 370, 10, 800, ob));
      break;
    case SpellEffect.RedMoonEvil:
      ob.effects.push(e = new Effect(Lib.Monster(Monster.RedMoonEvil), 32, 6, 400, ob));
      e.blend = false;
      break;
    case SpellEffect.TwinDrakeBlade:
      ob.effects.push(new Effect(Lib.Magic2, 380, 6, 800, ob));
      break;
    case SpellEffect.MPEater: {
      const ob2 = w.getObject(p.EffectType);
      if (ob2) ob2.effects.push(new Effect(Lib.Magic2, 2411, 19, 1900, ob2));
      ob.effects.push(new Effect(Lib.Magic2, 2400, 9, 900, ob));
      w.playSound(20000 + Spell.FatalSword * 10);
      break;
    }
    case SpellEffect.Bleeding:
      ob.effects.push(new Effect(Lib.Magic3, 60, 3, 400, ob));
      break;
    case SpellEffect.Hemorrhage:
      w.playSound(20000 + Spell.Hemorrhage * 10);
      ob.effects.push(new Effect(Lib.Magic3, 0, 4, 400, ob));
      ob.effects.push(new Effect(Lib.Magic3, 28, 6, 600, ob));
      ob.effects.push(new Effect(Lib.Magic3, 46, 8, 800, ob));
      break;
    case SpellEffect.MagicShieldUp: {
      if (ob.race !== ObjectType.Player && ob.race !== ObjectType.Hero) return;
      const player = px(ob); // TODO MapObj: shieldEffect, magicShield
      if (player.shieldEffect) {
        player.shieldEffect.clear();
        player.shieldEffect.remove();
      }
      player.magicShield = true;
      ob.effects.push(player.shieldEffect = e = new Effect(Lib.Magic, 3890, 3, 600, ob));
      e.repeat = true;
      break;
    }
    case SpellEffect.MagicShieldDown: {
      if (ob.race !== ObjectType.Player && ob.race !== ObjectType.Hero) return;
      const player = px(ob); // TODO MapObj: shieldEffect, magicShield
      if (player.shieldEffect) {
        player.shieldEffect.clear();
        player.shieldEffect.remove();
      }
      player.shieldEffect = null;
      player.magicShield = false;
      break;
    }
    case SpellEffect.GreatFoxSpirit:
      ob.effects.push(new Effect(Lib.Monster(Monster.GreatFoxSpirit), 375 + randomNext(3) * 20, 20, 1400, ob));
      w.playSound(Monster.GreatFoxSpirit * 10 + 5);
      break;
    case SpellEffect.Entrapment:
      ob.effects.push(new Effect(Lib.Magic2, 1010, 10, 1500, ob));
      ob.effects.push(new Effect(Lib.Magic2, 1020, 8, 1200, ob));
      break;
    case SpellEffect.Critical:
      // commented out in the C# (Libraries.CustomEffects)
      break;
    case SpellEffect.Reflect:
      ob.effects.push(new Effect(Lib.Effect, 580, 10, 70, ob));
      break;
    case SpellEffect.ElementalBarrierUp: {
      if (ob.race !== ObjectType.Player) return;
      const player = px(ob); // TODO MapObj: elementalBarrierEffect, elementalBarrier
      if (player.elementalBarrierEffect) {
        player.elementalBarrierEffect.clear();
        player.elementalBarrierEffect.remove();
      }
      player.elementalBarrier = true;
      ob.effects.push(player.elementalBarrierEffect = e = new Effect(Lib.Magic3, 1890, 10, 2000, ob));
      e.repeat = true;
      break;
    }
    case SpellEffect.ElementalBarrierDown: {
      if (ob.race !== ObjectType.Player) return;
      const player = px(ob); // TODO MapObj: elementalBarrierEffect, elementalBarrier
      if (player.elementalBarrierEffect) {
        player.elementalBarrierEffect.clear();
        player.elementalBarrierEffect.remove();
      }
      player.elementalBarrierEffect = null;
      player.elementalBarrier = false;
      ob.effects.push(player.elementalBarrierEffect = new Effect(Lib.Magic3, 1910, 7, 1400, ob));
      w.playSound(20000 + 131 * 10 + 5);
      break;
    }
    case SpellEffect.DelayedExplosion: {
      const effectid = DelayedExplosionEffect.getOwnerEffectID(ob.id);
      const snd = (id: number) => w.playSound(id);
      if (effectid < 0) {
        ob.effects.push(new DelayedExplosionEffect(Lib.Magic3, 1590, 8, 1200, ob, true, 0, 0, snd));
      } else if (effectid >= 0) {
        if (DelayedExplosionEffect.effectlist[effectid]!.stage < p.EffectType) {
          DelayedExplosionEffect.effectlist[effectid]!.remove();
          ob.effects.push(new DelayedExplosionEffect(Lib.Magic3, 1590 + p.EffectType * 10, 8, 1200, ob, true, p.EffectType, 0, snd));
        }
      }
      break;
    }
    case SpellEffect.AwakeningSuccess: {
      const ef = new Effect(Lib.Magic3, 900, 16, 1600, ob, w.now() + p.DelayTime);
      ef.onPlayed(() => w.playSound(50002));
      // not ported: ef.Complete -> MapControl.AwakeningAction = false (no awakening dialog yet)
      ob.effects.push(ef);
      ob.effects.push(e = new Effect(Lib.Magic3, 840, 16, 1600, ob, w.now() + p.DelayTime));
      e.blend = false;
      break;
    }
    case SpellEffect.AwakeningFail: {
      const ef = new Effect(Lib.Magic3, 920, 9, 900, ob, w.now() + p.DelayTime);
      ef.onPlayed(() => w.playSound(50003));
      // not ported: ef.Complete -> MapControl.AwakeningAction = false (no awakening dialog yet)
      ob.effects.push(ef);
      ob.effects.push(e = new Effect(Lib.Magic3, 860, 9, 900, ob, w.now() + p.DelayTime));
      e.blend = false;
      break;
    }
    case SpellEffect.AwakeningHit: {
      const ef = new Effect(Lib.Magic3, 880, 5, 500, ob, w.now() + p.DelayTime);
      ef.onPlayed(() => w.playSound(50001));
      ob.effects.push(ef);
      ob.effects.push(e = new Effect(Lib.Magic3, 820, 5, 500, ob, w.now() + p.DelayTime));
      e.blend = false;
      break;
    }
    case SpellEffect.AwakeningMiss: {
      const ef = new Effect(Lib.Magic3, 890, 5, 500, ob, w.now() + p.DelayTime);
      ef.onPlayed(() => w.playSound(50000));
      ob.effects.push(ef);
      ob.effects.push(e = new Effect(Lib.Magic3, 830, 5, 500, ob, w.now() + p.DelayTime));
      e.blend = false;
      break;
    }
    case SpellEffect.TurtleKing: {
      const ef = new Effect(Lib.Monster(Monster.TurtleKing), randomNext(2) === 0 ? 922 : 934, 12, 1200, ob);
      ef.onPlayed(() => w.playSound(20000 + Spell.HellFire * 10 + 1));
      ob.effects.push(ef);
      break;
    }
    case SpellEffect.Behemoth:
      w.addMapEffect(new Effect(Lib.Monster(Monster.Behemoth), 788, 10, 1500, ob.location));
      w.addMapEffect(e = new Effect(Lib.Monster(Monster.Behemoth), 778, 10, 1500, ob.location, 0, true));
      e.blend = false;
      break;
    case SpellEffect.Stunned:
      ob.effects.push(e = new Effect(Lib.Monster(Monster.StoningStatue), 632, 10, 1000, ob));
      e.repeat = p.Time > 0;
      e.repeatUntil = p.Time > 0 ? w.now() + p.Time : 0;
      break;
    case SpellEffect.IcePillar:
      ob.effects.push(new Effect(Lib.Monster(Monster.IcePillar), 18, 8, 800, ob));
      break;
    case SpellEffect.KingGuard:
      if (p.EffectType === 0) {
        ob.effects.push(e = new Effect(Lib.Monster(Monster.KingGuard), 753, 10, 1000, ob));
        e.blend = false;
      } else {
        ob.effects.push(e = new Effect(Lib.Monster(Monster.KingGuard), 763, 10, 1000, ob));
        e.blend = false;
      }
      break;
    case SpellEffect.FlamingMutantWeb:
      ob.effects.push(e = new Effect(Lib.Monster(Monster.FlamingMutant), 330, 10, 1000, ob));
      e.repeat = p.Time > 0;
      e.repeatUntil = p.Time > 0 ? w.now() + p.Time : 0;
      break;
    case SpellEffect.DeathCrawlerBreath:
      ob.effects.push(e = new Effect(Lib.Monster(Monster.DeathCrawler), 272 + ob.direction * 4, 4, 400, ob));
      e.blend = true;
      break;
    case SpellEffect.MoonMist:
      ob.effects.push(new Effect(Lib.Magic3, 705, 10, 800, ob));
      break;
  }
}

/** Port of GameScene.MapEffect. */
export function mapEffect(p: PacketOf<"MapEffect">, w: SpellWorld): void {
  switch (p.Effect) {
    case SpellEffect.Mine: {
      w.playSound(10091);
      const hitWall = new Effect(Lib.Effect, 8 * p.Value, 3, 240, p.Location);
      hitWall.light = 0;
      w.addMapEffect(hitWall);
      break;
    }
    case SpellEffect.Tester: {
      const eff = new Effect(Lib.Effect, 328, 10, 500, p.Location);
      eff.light = 0;
      w.addMapEffect(eff);
      break;
    }
  }
}

/** Port of GameScene.ObjectProjectile. */
export function objectProjectile(p: PacketOf<"ObjectProjectile">, w: SpellWorld): void {
  const source = w.getObject(p.Source);
  if (!source) return;
  switch (p.Spell) {
    case Spell.FireBounce: {
      w.playSound(20000 + Spell.GreatFireBall * 10 + 1);
      // MapObject.CreateProjectile returns null for non player/monster objects; the web helper always builds one.
      const missile = createProjectile(source, w, 410, Lib.Magic, true, 6, 30, 4, undefined, undefined, undefined, p.Destination);
      onHit(missile, Lib.Magic, 570, 10, 600, w, 20000 + Spell.GreatFireBall * 10 + 2);
      break;
    }
  }
}

/** Port of Client/MirObjects/SpellObject.cs (map spell objects from S.ObjectSpell: FireWall, PoisonCloud, Blizzard, ...). */
export class SpellObj {
  readonly id: number;
  location: WirePoint;
  light = 0;
  spell: number;
  direction: number;
  lib: string | null = null;     // BodyLibrary
  drawFrame = 0;
  animationOffset = { X: 0, Y: 0 };
  frameCount = 0;
  frameInterval = 0;
  frameIndex = 0;
  repeat = true;
  ended = false;
  blend = false;
  drawLocation = { x: 0, y: 0 };
  private nextMotion: number;
  private readonly w: SpellWorld;

  /** SpellObject(objectID) + Load(S.ObjectSpell). MapControl.AddObject is the caller's job. */
  constructor(p: PacketOf<"ObjectSpell">, w: SpellWorld) {
    this.w = w;
    this.id = p.ObjectID;
    this.location = { ...p.Location };
    this.spell = p.Spell;
    this.direction = p.Direction;
    this.repeat = true;
    this.ended = false;
    let e: Effect;

    switch (this.spell) {
      case Spell.TrapHexagon:
        this.lib = Lib.Magic; this.drawFrame = 1390; this.frameInterval = 100; this.frameCount = 10; this.blend = true;
        break;
      case Spell.FireWall:
        this.lib = Lib.Magic; this.drawFrame = 1630; this.frameInterval = 120; this.frameCount = 6; this.light = 3; this.blend = true;
        break;
      case Spell.PoisonCloud:
        this.lib = Lib.Magic2; this.drawFrame = 1650; this.frameInterval = 120; this.frameCount = 20; this.light = 3; this.blend = true;
        break;
      case Spell.DigOutZombie:
        // C# falls back to Libraries.Magic when Monsters has no DigOutZombie entry; not checked here.
        this.lib = Lib.Monster(Monster.DigOutZombie); this.drawFrame = 304 + this.direction; this.frameCount = 0; this.blend = false;
        break;
      case Spell.Blizzard:
        this.animationOffset = { X: 0, Y: -20 };
        this.lib = Lib.Magic2; this.drawFrame = 1550; this.frameInterval = 100; this.frameCount = 30; this.light = 3; this.blend = true; this.repeat = false;
        break;
      case Spell.MeteorStrike:
        this.animationOffset = { X: 0, Y: -20 };
        w.addMapEffect(e = new Effect(Lib.Magic2, 1600, 10, 800, this.location));
        e.repeat = true;
        e.repeatUntil = w.now() + 3000;
        this.lib = Lib.Magic2; this.drawFrame = 1610; this.frameInterval = 100; this.frameCount = 30; this.light = 3; this.blend = true; this.repeat = false;
        break;
      case Spell.Rubble:
        if (this.direction === 0) this.lib = null;
        else {
          this.lib = Lib.Effect; this.drawFrame = 64 + Math.min(4, this.direction - 1); this.frameCount = 1; this.frameInterval = 10000;
        }
        break;
      case Spell.Reincarnation:
        this.lib = Lib.Magic2; this.drawFrame = 1680; this.frameInterval = 100; this.frameCount = 10; this.light = 1; this.blend = true; this.repeat = true;
        break;
      case Spell.ExplosiveTrap:
        this.lib = Lib.Magic3;
        if (p.Param) {
          this.drawFrame = 1570; this.frameInterval = 100; this.frameCount = 9; this.repeat = false;
          w.playSound(20000 + 124 * 10 + 5); // Boom for all players in range
        } else {
          this.drawFrame = 1560; this.frameInterval = 100; this.frameCount = 10; this.repeat = true;
        }
        this.blend = true;
        break;
      case Spell.Trap:
        this.lib = Lib.Magic2; this.drawFrame = 2360; this.frameInterval = 100; this.frameCount = 8; this.blend = true;
        break;
      case Spell.MapLightning:
        w.addMapEffect(new Effect(Lib.Dragon, 400 + randomNext(3) * 10, 5, 600, this.location));
        w.playSound(8301);
        break;
      case Spell.MapLava:
        w.addMapEffect(e = new Effect(Lib.Dragon, 440, 20, 1600, this.location));
        e.blend = false;
        w.addMapEffect(new Effect(Lib.Dragon, 470, 10, 800, this.location));
        w.playSound(8302);
        break;
      case Spell.MapQuake1:
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.HellLord), 27, 12, 1200, this.location));
        e.blend = false;
        w.playSound(8304);
        break;
      case Spell.MapQuake2:
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.HellLord), 39, 13, 1300, this.location));
        e.blend = false;
        w.playSound(8304);
        break;
      case Spell.DigOutArmadillo:
        this.lib = Lib.Monster(Monster.Armadillo); this.drawFrame = 472 + this.direction; this.frameCount = 0; this.blend = false;
        break;
      case Spell.GeneralMeowMeowThunder:
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.GeneralMeowMeow), 522, 7, 700, this.location));
        e.blend = true;
        w.playSound(8321);
        break;
      case Spell.StoneGolemQuake:
        this.lib = Lib.Monster(Monster.StoneGolem); this.drawFrame = 368 + this.direction * 8; this.frameInterval = 100; this.frameCount = 8;
        this.light = 0; this.blend = false; this.repeat = false;
        w.playSound(8304);
        break;
      case Spell.EarthGolemPile:
        this.lib = Lib.Monster(Monster.EarthGolem); this.drawFrame = 441; this.frameInterval = 100; this.frameCount = 8;
        this.light = 0; this.blend = false; this.repeat = false;
        w.playSound(8331);
        break;
      case Spell.TreeQueenMassRoots:
        this.lib = Lib.Monster(Monster.TreeQueen); this.drawFrame = 82; this.frameInterval = 100; this.frameCount = 15; this.blend = false; this.repeat = false;
        w.playSound(8341);
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.TreeQueen), 97, 14, 1400, this.location));
        e.blend = true;
        break;
      case Spell.TreeQueenGroundRoots:
        this.lib = Lib.Monster(Monster.TreeQueen); this.drawFrame = 48; this.frameInterval = 100; this.frameCount = 9; this.blend = false; this.repeat = false;
        w.playSound(8342);
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.TreeQueen), 57, 9, 900, this.location));
        e.blend = true;
        break;
      case Spell.TreeQueenRoot:
        this.lib = Lib.Monster(Monster.TreeQueen); this.drawFrame = 111; this.frameInterval = 100; this.frameCount = 15; this.blend = false; this.repeat = false;
        w.playSound(8343);
        break;
      case Spell.TucsonGeneralRock:
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.TucsonGeneral), 552, 20, 2000, this.location));
        e.repeat = false;
        e.blend = false;
        this.lib = Lib.Monster(Monster.TucsonGeneral); this.drawFrame = 572; this.frameInterval = 100; this.frameCount = 20;
        this.light = 1; this.blend = true; this.repeat = false;
        break;
      case Spell.Portal:
        this.lib = Lib.Magic2; this.drawFrame = 2360; this.frameInterval = 100; this.frameCount = 8; this.blend = true;
        break;
      case Spell.HealingCircle:
        this.lib = Lib.Magic3; this.drawFrame = 630; this.frameInterval = 80; this.frameCount = 11; this.light = 3; this.blend = true;
        break;
      case Spell.FlyingStatueIceTornado:
        this.lib = Lib.Monster(Monster.FlyingStatue); this.drawFrame = 314; this.frameInterval = 100; this.frameCount = 20; this.blend = true; this.repeat = false;
        w.playSound(8303);
        break;
      case Spell.DarkOmaKingNuke:
        this.lib = Lib.Monster(Monster.DarkOmaKing); this.drawFrame = 1630 + this.direction * 9; this.frameInterval = 100; this.frameCount = 9;
        this.blend = true; this.repeat = false;
        w.playSound(Monster.DarkOmaKing * 10 + 9);
        break;
      case Spell.HornedSorcererDustTornado:
        this.lib = Lib.Monster(Monster.HornedSorceror); this.drawFrame = 634; this.frameInterval = 100; this.frameCount = 10; this.blend = true; this.repeat = true;
        w.playSound(8306);
        break;
      case Spell.HornedCommanderRockFall:
        this.lib = Lib.Monster(Monster.HornedCommander); this.drawFrame = 1066; this.frameInterval = 100; this.frameCount = 12; this.blend = true; this.repeat = true;
        w.playSound(8456);
        break;
      case Spell.HornedCommanderRockSpike:
        this.lib = Lib.Monster(Monster.HornedCommander); this.drawFrame = 1190; this.frameInterval = 100; this.frameCount = 9; this.blend = false; this.repeat = true;
        w.playSound(8457);
        w.addMapEffect(e = new Effect(Lib.Monster(Monster.HornedCommander), 1199, 9, 900, this.location));
        e.blend = true;
        break;
    }

    this.nextMotion = w.now() + this.frameInterval;
    this.nextMotion -= this.nextMotion % 100;
  }

  /** SpellObject.Process. */
  process(): void {
    const w = this.w;
    if (w.now() >= this.nextMotion) {
      if (++this.frameIndex >= this.frameCount && this.repeat) {
        this.frameIndex = 0;
        this.ended = true;
      }
      this.nextMotion = w.now() + this.frameInterval;

      switch (this.spell) {
        case Spell.TucsonGeneralRock:
          if (this.frameIndex === 10) w.playSound(8305);
          break;
        case Spell.HornedSorcererDustTornado:
          if (this.frameIndex === 0 && randomNext(3) === 0) w.playSound(8306);
          break;
        case Spell.HornedCommanderRockSpike:
          if (this.ended) {
            this.drawFrame = 1198;
            this.frameCount = 1;
            this.frameIndex = 0;
          }
          break;
      }
    }

    this.drawLocation = this.cellToScreen(this.location);
  }

  /** SpellObject.Draw (DrawColour = White). */
  draw(): void {
    if (this.frameIndex >= this.frameCount && !this.repeat) return;
    if (this.lib === null) return;
    const ew = effectWorld();
    const at = this.animationOffset.X === 0 && this.animationOffset.Y === 0
      ? this.drawLocation
      : this.cellToScreen({ X: this.location.X + this.animationOffset.X, Y: this.location.Y + this.animationOffset.Y });
    if (this.blend) ew.drawBlend(this.lib, this.drawFrame + this.frameIndex, at.x, at.y, true, 0.8);
    else ew.draw(this.lib, this.drawFrame + this.frameIndex, at.x, at.y, true);
  }

  /** (cell - User.Movement + OffSet) * CellSize + User.OffSetMove (GlobalDisplayLocationOffset is always (0, 0)). */
  private cellToScreen(c: WirePoint): { x: number; y: number } {
    const ew = effectWorld();
    const user = this.w.user();
    if (!user) return { x: 0, y: 0 };
    return {
      x: (c.X - user.movement.X + ew.offSetX) * CELL_W + user.offsetMove.X,
      y: (c.Y - user.movement.Y + ew.offSetY) * CELL_H + user.offsetMove.Y,
    };
  }
}
