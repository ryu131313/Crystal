// Per-monster specials: a mechanical port of the `switch (BaseImage)` blocks of Client/MirObjects/MonsterObject.cs —
// SetAction (action-start effects and the action sounds game.ts does not play yet), ProcessFrames (frame-specific effects,
// projectiles towards TargetID, effects at the target / in front of the monster, swing / dead sounds) and the
// "UpdateFrame() >= Frame.Count" action-end blocks. Library indices, counts and durations are copied verbatim from the C#.
// Not ported (the web MapObj has no such state): Stoned toggles and the Shinsu / Shinsu1 body swap (Show / Hide end),
// Remove() of the burrowing monsters at the Hide end, FrameLoop / LoopFrame (HornedCommander / HornedSorceror spins: the
// effects and sounds play, the body frames do not loop), SkipFrames. IntelligentCreature effects stay in
// effects.ts creatureFrameEffects (BabySnowMan, OlympicFlame), the Walking L/R sounds in game.ts spellHooks.frame.

import { MirAction as A, Monster as M, ObjectType, Spell } from "../protocol/enums.generated.ts";
import { pointMove, type MapObj } from "./objects.ts";
import { Effect, Lib, Missile, TrackableEffect } from "./effects.ts";
import { LibPath } from "./libraries.ts";
import { createProjectile, type SpellWorld } from "./spellVisuals.ts";
import { baseSound, monsterSound, petWorld, type MonsterSound, type SoundWorld } from "./sounds.ts";

/** The game seams the specials need: spellVisuals' SpellWorld (objects, map effects, user, clock) and sounds' SoundWorld. */
let worlds: { w: SpellWorld; s: SoundWorld } | null = null;
/** Register once from game.ts (the objects.ts frame / action-end hooks are no-ops until then). */
export function setMonsterSpecialsWorld(w: SpellWorld, s: SoundWorld): void {
  worlds = { w, s };
}

/** CMain.Random.Next(n) / Next(min, max) (max exclusive). */
const rnd = (n: number) => Math.floor(Math.random() * n);
const rnd2 = (min: number, max: number) => min + Math.floor(Math.random() * (max - min));
/** C# object initialiser `new Effect(...) { Blend = true, ... }`. */
function fx<T extends Effect>(e: T, init: { blend?: boolean; repeat?: boolean; repeatUntil?: number; light?: number; drawBehind?: boolean; start?: number }): T {
  if (init.blend !== undefined) e.blend = init.blend;
  if (init.repeat !== undefined) e.repeat = init.repeat;
  if (init.repeatUntil !== undefined) e.repeatUntil = init.repeatUntil;
  if (init.light !== undefined) e.light = init.light;
  if (init.drawBehind !== undefined) e.drawBehind = init.drawBehind;
  if (init.start !== undefined) e.start = init.start; // a plain field in the C# too (NextFrame keeps the constructor start)
  return e;
}

interface Ctx {
  self: MapObj; w: SpellWorld; f: number; fc: number; fi: number; now: number; user: MapObj | null; front: { X: number; Y: number }; bs: number;
  snd: (id: number, loop?: boolean, delay?: number) => void; ms: (k: MonsterSound) => void;
}
function ctx(self: MapObj, w: SpellWorld, s: SoundWorld): Ctx {
  return {
    self, w, f: self.frameIndex, fc: self.frame?.count ?? 0, fi: self.frame?.interval ?? 0, now: w.now(), user: w.user(),
    front: pointMove(self.location, self.direction, 1), bs: baseSound(self),
    snd: (id, loop = false, delay = 0) => petWorld(self, s).play(id, loop, delay), ms: (k) => monsterSound(self, k, s),
  };
}

/** MonsterObject.SetAction: the per-action `switch (BaseImage)` effects and the action-start sounds (a new action has started). */
export function monsterActionStart(mob: MapObj, ws?: SpellWorld, ss?: SoundWorld): void {
  if (mob.race !== ObjectType.Monster) return;
  if (!ws || !ss) { if (!worlds) return; ws = worlds.w; ss = worlds.s; }
  const c = ctx(mob, ws, ss);
  const { self, w, f, fc, fi, now, user, front, bs, snd, ms } = c;
  switch (self.action) {
    case A.Appear:
      ms("summon");
      switch (self.image) {
        case M.HellKnight1:
        case M.HellKnight2:
        case M.HellKnight3:
        case M.HellKnight4:
          self.effects.push(new Effect(Lib.Monster(self.image), 448, 10, 600, self));
          break;
      }
      break;
    case A.Show:
      ms("popup");
      break;
    case A.Jump:
      ms("jump");
      switch (self.image) {
        // Sanjian
        case M.FurbolgGuard:
          self.effects.push(new Effect(Lib.Monster(M.FurbolgGuard), 414 + self.direction * 6, 6, fc * fi, self));
          break;
        case M.Armadillo:
          w.addMapEffect(new Effect(Lib.Monster(self.image), 592, 12, 800, self.location, now + 500));
          break;
      }
      break;
    case A.DashAttack:
      ms("dash");
      break;
    // Running: PlayRunSound is game.ts spellHooks.action's
    case A.Attack1:
      // PlayAttackSound is game.ts spellHooks.action's; CurrentActionLevel = Params[1] is MapObj.spellLevel (ObjectAttack Level)
      switch (self.image) {
        case M.FlamingWooma:
          self.effects.push(new Effect(Lib.Monster(M.FlamingWooma), 224 + self.direction * 7, 7, fc * fi, self));
          break;
        case M.ZumaTaurus:
          self.effects.push(new Effect(Lib.Monster(M.ZumaTaurus), 244 + self.direction * 8, 8, 8 * fi, self));
          break;
        case M.MinotaurKing:
          self.effects.push(new Effect(Lib.Monster(M.MinotaurKing), 272 + self.direction * 6, 6, fc * fi, self));
          break;
        case M.FlamingMutant:
          w.addMapEffect(new Effect(Lib.Monster(M.FlamingMutant), 314, 6, 600, front));
          break;
        case M.DemonWolf:
          self.effects.push(new Effect(Lib.Monster(M.DemonWolf), 336 + self.direction * 9, 6, fc * fi, self));
          break;
        case M.AncientBringer:
          self.effects.push(new Effect(Lib.Monster(M.AncientBringer), 512 + self.direction * 6, 6, fc * fi, self));
          break;
        case M.Manticore:
          self.effects.push(new Effect(Lib.Monster(M.Manticore), 505 + self.direction * 3, 3, fc * fi, self));
          break;
        case M.YinDevilNode:
          self.effects.push(new Effect(Lib.Monster(M.YinDevilNode), 26, 26, fc * fi, self));
          break;
        case M.YangDevilNode:
          self.effects.push(new Effect(Lib.Monster(M.YangDevilNode), 26, 26, fc * fi, self));
          break;
        case M.GreatFoxSpirit:
          self.effects.push(new Effect(Lib.Monster(M.GreatFoxSpirit), 355, 20, fc * fi, self));
          break;
        case M.EvilMir:
          self.effects.push(new Effect(Lib.Dragon, 60, 8, 8 * fi, self));
          self.effects.push(new Effect(Lib.Dragon, 68, 14, 14 * fi, self));
          const random = rnd(7);
          for (let i = 0; i <= 7 + random; i++) {
            const source = { X: user!.location.X + rnd2(-7, 7), Y: user!.location.Y + rnd2(-7, 7) };
            w.addMapEffect(new Effect(Lib.Dragon, 230 + (rnd(5) * 10), 5, 400, source, now + rnd(1000)));
          }
          break;
        case M.CrawlerLave:
          self.effects.push(new Effect(Lib.Monster(M.CrawlerLave), 224 + self.direction * 6, 6, fc * fi, self));
          break;
        case M.HellKeeper:
          self.effects.push(new Effect(Lib.Monster(M.HellKeeper), 32, 8, 8 * fi, self));
          break;
        case M.IcePillar:
          self.effects.push(new Effect(Lib.Monster(M.IcePillar), 12, 6, 6 * 100, self));
          break;
        case M.TrollKing:
          self.effects.push(new Effect(Lib.Monster(M.TrollKing), 288, 6, 6 * fi, self));
          break;
        case M.HellBomb1:
          self.effects.push(new Effect(Lib.Monster(M.HellLord), 61, 7, 7 * fi, self));
          break;
        case M.HellBomb2:
          self.effects.push(new Effect(Lib.Monster(M.HellLord), 79, 9, 9 * fi, self));
          break;
        case M.HellBomb3:
          self.effects.push(new Effect(Lib.Monster(M.HellLord), 97, 8, 8 * fi, self));
          break;
        case M.WitchDoctor:
          self.effects.push(new Effect(Lib.Monster(M.WitchDoctor), 328, 20, 20 * fi, self));
          break;
        case M.SeedingsGeneral:
          w.addMapEffect(new Effect(Lib.Monster(M.SeedingsGeneral), 1064 + self.direction * 9, 9, 9 * fi, front, now));
          break;
        case M.RestlessJar:
          self.effects.push(new Effect(Lib.Monster(M.RestlessJar), 384, 7, 7 * fi, self));
          break;
        case M.AssassinBird:
          self.effects.push(new Effect(Lib.Monster(M.AssassinBird), 392, 9, 9 * fi, self));
          break;
        case M.Nadz:
          self.effects.push(new Effect(Lib.Monster(M.Nadz), 280 + self.direction * 5, 5, 5 * fi, self));
          break;
        case M.AvengingWarrior:
          self.effects.push(new Effect(Lib.Monster(M.AvengingWarrior), 272 + self.direction * 5, 5, 7 * fi, self));
          break;
        case M.FlyingStatue:
          self.effects.push(new Effect(Lib.Monster(M.FlyingStatue), 334, 10, 10 * 100, self));
          break;
        case M.HoodedSummoner:
          self.effects.push(new Effect(Lib.Monster(M.HoodedSummoner), 352, 12, 12 * fi, self));
          break;
      }
      break;
    case A.Attack2:
      ms("attack2");
      switch (self.image) {
        case M.CrystalSpider:
          self.effects.push(new Effect(Lib.Monster(M.CrystalSpider), 272 + self.direction * 10, 10, fc * fi, self));
          break;
        case M.Yimoogi:
        case M.RedYimoogi:
        case M.Snake10:
        case M.Snake11:
        case M.Snake12:
        case M.Snake13:
        case M.Snake14:
        case M.Snake15:
        case M.Snake16:
        case M.Snake17:
          self.effects.push(new Effect(Lib.Monster(self.image), 304, 6, fc * fi, self));
          self.effects.push(new Effect(Lib.Magic2, 1280, 10, fc * fi, self));
          break;
        case M.HellCannibal:
          self.effects.push(new Effect(Lib.Monster(M.HellCannibal), 310 + self.direction * 12, 12, 12 * fi, self));
          break;
        case M.ManectricKing:
          self.effects.push(new Effect(Lib.Monster(M.ManectricKing), 640 + self.direction * 10, 10, 10 * 100, self));
          break;
        case M.AncientBringer:
          self.effects.push(new Effect(Lib.Monster(M.AncientBringer), 568 + self.direction * 10, 10, 13 * fi, self));
          break;
        case M.DarkBeast:
          self.effects.push(new Effect(Lib.Monster(M.DarkBeast), 296 + self.direction * 4, 4, fc * fi, self));
          break;
        case M.LightBeast:
          self.effects.push(new Effect(Lib.Monster(M.LightBeast), 296 + self.direction * 4, 4, fc * fi, self));
          break;
        case M.FireCat:
          self.effects.push(new Effect(Lib.Monster(M.FireCat), 248 + self.direction * 10, 10, 10 * fi, self));
          break;
        case M.BloodBaboon:
          self.effects.push(new Effect(Lib.Monster(M.BloodBaboon), 312 + self.direction * 7, 7, 7 * fi, self));
          break;
        case M.TwinHeadBeast:
          self.effects.push(new Effect(Lib.Monster(M.TwinHeadBeast), 352 + self.direction * 7, 7, fc * fi, self));
          break;
        case M.Nadz:
          self.effects.push(new Effect(Lib.Monster(M.Nadz), 320 + self.direction * 7, 7, 7 * fi, self));
          break;
        case M.DarkCaptain:
          self.effects.push(new Effect(Lib.Monster(M.DarkCaptain), 1214, 10, 10 * fi, self));
          break;
        case M.DragonWarrior:
          self.effects.push(new Effect(Lib.Monster(M.DragonWarrior), 576, 7, fc * fi, self));
          break;
        case M.FlyingStatue:
          self.effects.push(new Effect(Lib.Monster(M.FlyingStatue), 352, 10, 10 * 100, self));
          break;
        case M.HornedSorceror:
          self.effects.push(new Effect(Lib.Monster(M.HornedSorceror), 552 + self.direction * 9, 9, 900, self));
          break;
        case M.HoodedSummoner:
          self.effects.push(new Effect(Lib.Monster(M.HoodedSummoner), 364, 10, 10 * fi, self));
          break;
      }
      if (self.image >= 10000) {
        ms("pet");
      }
      break;
    case A.Attack3:
      ms("attack3");
      switch (self.image) {
        case M.Yimoogi:
        case M.RedYimoogi:
        case M.Snake10:
        case M.Snake11:
        case M.Snake12:
        case M.Snake13:
        case M.Snake14:
        case M.Snake15:
        case M.Snake16:
        case M.Snake17:
          snd(bs + 9);
          self.effects.push(new Effect(Lib.Magic2, 1330, 10, fc * fi, self));
          break;
        case M.Behemoth:
          self.effects.push(new Effect(Lib.Monster(M.Behemoth), 697 + self.direction * 7, 7, fc * fi, self));
          break;
        case M.SandSnail:
          self.effects.push(fx(new Effect(Lib.Monster(M.SandSnail), 448, 9, 900, self), { blend: true }));
          break;
        case M.PeacockSpider:
          self.effects.push(new Effect(Lib.Monster(M.PeacockSpider), 755, 21, 21 * fi, self));
          break;
        case M.DragonWarrior:
          self.effects.push(new Effect(Lib.Monster(M.DragonWarrior), 632 + self.direction * 4, 4, 8 * fi, self));
          break;
      }
      break;
    case A.Attack4:
      ms("attack4");
      switch (self.image) {
        case M.DarkOmaKing:
          self.effects.push(new Effect(Lib.Monster(M.DarkOmaKing), 1702, 13, 13 * fi, self));
          break;
      }
      break;
    case A.Attack5:
      ms("attack5");
      break;
    case A.AttackRange1:
      ms("range");
      switch (self.image) {
        case M.KingScorpion:
          self.effects.push(new Effect(Lib.Monster(M.KingScorpion), 544 + self.direction * 8, 8, fc * fi, self));
          break;
        case M.DarkDevil:
          self.effects.push(new Effect(Lib.Monster(M.DarkDevil), 272 + self.direction * 8, 8, fc * fi, self));
          break;
        case M.ShamanZombie:
          self.effects.push(new Effect(Lib.Monster(M.ShamanZombie), 232 + self.direction * 12, 6, fc * fi, self));
          self.effects.push(new Effect(Lib.Monster(M.ShamanZombie), 328, 12, fc * fi, self));
          break;
        case M.GuardianRock:
          self.effects.push(new Effect(Lib.Monster(M.GuardianRock), 12, 10, fc * fi, self));
          break;
        case M.GreatFoxSpirit:
          const random = rnd(4);
          for (let i = 0; i <= 4 + random; i++) {
            const source = { X: user!.location.X + rnd2(-7, 7), Y: user!.location.Y + rnd2(-7, 7) };
            w.addMapEffect(new Effect(Lib.Monster(M.GreatFoxSpirit), 375 + (rnd(3) * 20), 20, 1400, source, now + rnd(600)));
          }
          break;
        case M.EvilMir:
          self.effects.push(new Effect(Lib.Dragon, 90 + self.direction * 10, 10, 10 * fi, self));
          break;
        case M.DragonStatue:
          self.effects.push(new Effect(Lib.Dragon, 310 + Math.trunc(self.direction / 3) * 20, 10, 10 * fi, self));
          break;
        case M.TurtleKing:
          if (user) user.effects.push(new Effect(Lib.Monster(M.TurtleKing), 946, 10, fc * fi, user)); // C#: the monster's Effects list, owner User (pushed to the owner here so Effect.remove finds it)
          break;
        case M.HellBolt:
          self.effects.push(new Effect(Lib.Monster(M.HellBolt), 304, 11, 11 * 100, self));
          break;
        case M.WitchDoctor:
          self.effects.push(new Effect(Lib.Monster(M.WitchDoctor), 304, 9, 9 * 100, self));
          break;
        case M.FlyingStatue:
          self.effects.push(new Effect(Lib.Monster(M.FlyingStatue), 304, 10, 10 * fi, self));
          break;
        case M.ManectricKing:
          self.effects.push(new Effect(Lib.Monster(M.ManectricKing), 720, 12, 12 * 100, self));
          break;
        case M.IcePillar:
          self.effects.push(fx(new Effect(Lib.Monster(M.IcePillar), 26, 6, 8 * 100, self), { start: now + 750 }));
          break;
        case M.ElementGuard:
          self.effects.push(new Effect(Lib.Monster(M.ElementGuard), 320 + self.direction * 5, 5, 5 * fi, self));
          break;
        case M.KingGuard:
          self.effects.push(new Effect(Lib.Monster(M.KingGuard), 737, 9, 9 * fi, self));
          break;
        case M.CatShaman:
          self.effects.push(new Effect(Lib.Monster(M.CatShaman), 738, 8, fc * fi, self));
          break;
        case M.SeedingsGeneral:
          self.effects.push(new Effect(Lib.Monster(M.SeedingsGeneral), 1184 + self.direction * 9, 9, fc * fi, self));
          break;
        case M.RestlessJar:
          self.effects.push(new Effect(Lib.Monster(M.RestlessJar), 471, 5, 500, self));
          break;
        case M.AvengingSpirit:
          self.effects.push(new Effect(Lib.Monster(M.AvengingSpirit), 344 + self.direction * 3, 3, 3 * fi, self));
          break;
        case M.ClawBeast:
          self.effects.push(new Effect(Lib.Monster(M.ClawBeast), 504 + self.direction * 8, 8, 8 * fi, self));
          break;
        case M.DarkCaptain:
          self.effects.push(new Effect(Lib.Monster(M.DarkCaptain), 1200, 13, 13 * fi, self));
          break;
        case M.FloatingRock:
          self.effects.push(new Effect(Lib.Monster(M.FloatingRock), 216, 10, 10 * fi, self));
          break;
        case M.FrozenKnight:
          self.effects.push(new Effect(Lib.Monster(M.FrozenKnight), 384 + self.direction * 9, 9, 9 * fi, self));
          break;
        case M.IcePhantom:
          self.effects.push(new Effect(Lib.Monster(M.IcePhantom), 672, 10, 10 * fi, self));
          break;
        case M.BlackTortoise:
          self.effects.push(new Effect(Lib.Monster(M.BlackTortoise), 404 + self.direction * 5, 5, 5 * fi, self));
          break;
        case M.AssassinScroll:
          self.effects.push(new Effect(Lib.Monster(M.AssassinScroll), 296, 3, 3 * fi, self));
          break;
        case M.PurpleFaeFlower:
          self.effects.push(new Effect(Lib.Monster(M.PurpleFaeFlower), 328, 3, 3 * fi, self));
          break;
        case M.WarriorScroll:
          self.effects.push(new Effect(Lib.Monster(M.WarriorScroll), 296, 8, 8 * fi, self));
          break;
        case M.WizardScroll:
          self.effects.push(new Effect(Lib.Monster(M.WizardScroll), 296, 4, 4 * fi, self));
          break;
      }
      break;
    case A.AttackRange2:
      ms("range2");
      switch (self.image) {
        case M.TurtleKing:
          const random = rnd(4);
          for (let i = 0; i <= 4 + random; i++) {
            const source = { X: user!.location.X + rnd2(-7, 7), Y: user!.location.Y + rnd2(-7, 7) };
            const ef = new Effect(Lib.Monster(M.TurtleKing), rnd(2) === 0 ? 922 : 934, 12, 1200, source, now + rnd(600));
            ef.onPlayed(() => snd(20000 + Spell.HellFire * 10 + 1));
            w.addMapEffect(ef);
          }
          break;
        case M.SeedingsGeneral:
          self.effects.push(new Effect(Lib.Monster(M.SeedingsGeneral), 1256, 9, 900, self));
          break;
        case M.PeacockSpider: //BROKEN
          self.effects.push(new Effect(Lib.Monster(M.PeacockSpider), 776, 30, 30 * fi, self));
          break;
        case M.DarkCaptain:
          self.effects.push(new Effect(Lib.Monster(M.DarkCaptain), 1234, 13, 13 * fi, self));
          break;
        case M.IcePhantom:
          self.effects.push(new Effect(Lib.Monster(M.IcePhantom), 672, 10, 10 * fi, self));
          break;
      }
      break;
    case A.AttackRange3:
      ms("range3");
      switch (self.image) {
        case M.TurtleKing:
          if (user) user.effects.push(new Effect(Lib.Monster(M.TurtleKing), 946, 10, fc * fi, user)); // C#: the monster's Effects list, owner User (pushed to the owner here so Effect.remove finds it)
          break;
      }
      break;
    case A.Struck:
      // StruckWeapon (QueuedAction.struckWeapon) and PlayFlinchSound / PlayStruckSound are objects.ts / game.ts's
      switch (self.image) {
        case M.GlacierBeast:
          self.effects.push(new Effect(Lib.Monster(M.GlacierBeast), 304, 6, 400, self));
          break;
      }
      break;
    case A.Die:
      switch (self.image) {
        case M.ManectricKing:
          self.effects.push(new Effect(Lib.Monster(M.ManectricKing), 504 + self.direction * 9, 9, fc * fi, self));
          break;
        case M.DarkDevil:
          self.effects.push(new Effect(Lib.Monster(M.DarkDevil), 336, 6, fc * fi, self));
          break;
        case M.ShamanZombie:
          self.effects.push(new Effect(Lib.Monster(M.ShamanZombie), 224, 8, fc * fi, self));
          break;
        case M.RoninGhoul:
          self.effects.push(new Effect(Lib.Monster(M.RoninGhoul), 224, 10, fc * fi, self));
          break;
        case M.BoneCaptain:
          self.effects.push(new Effect(Lib.Monster(M.BoneCaptain), 224 + self.direction * 10, 10, fc * fi, self));
          break;
        case M.RightGuard:
          self.effects.push(new Effect(Lib.Monster(M.RightGuard), 296, 5, fc * fi, self));
          break;
        case M.LeftGuard:
          self.effects.push(new Effect(Lib.Monster(M.LeftGuard), 296 + self.direction * 5, 5, 5 * fi, self));
          break;
        case M.FrostTiger:
          self.effects.push(new Effect(Lib.Monster(M.FrostTiger), 304, 10, fc * fi, self));
          break;
        case M.Yimoogi:
        case M.RedYimoogi:
        case M.Snake10:
        case M.Snake11:
        case M.Snake12:
        case M.Snake13:
        case M.Snake14:
        case M.Snake15:
        case M.Snake16:
        case M.Snake17:
          self.effects.push(new Effect(Lib.Monster(M.Yimoogi), 352, 10, fc * fi, self));
          break;
        case M.YinDevilNode:
          self.effects.push(new Effect(Lib.Monster(M.YinDevilNode), 52, 20, fc * fi, self));
          break;
        case M.YangDevilNode:
          self.effects.push(new Effect(Lib.Monster(M.YangDevilNode), 52, 20, fc * fi, self));
          break;
        case M.BlackFoxman:
          self.effects.push(new Effect(Lib.Monster(M.BlackFoxman), 224, 10, fc * fi, self));
          break;
        case M.VampireSpider:
          self.effects.push(new Effect(Lib.Monster(M.VampireSpider), 296, 5, fc * fi, self));
          break;
        case M.CharmedSnake:
          self.effects.push(new Effect(Lib.Monster(M.CharmedSnake), 40, 8, fc * fi, self));
          break;
        case M.Manticore:
          self.effects.push(new Effect(Lib.Monster(M.Manticore), 592, 9, fc * fi, self));
          break;
        case M.ValeBat:
          self.effects.push(new Effect(Lib.Monster(M.ValeBat), 224, 20, fc * fi, self));
          break;
        case M.SpiderBat:
          self.effects.push(new Effect(Lib.Monster(M.SpiderBat), 224, 20, fc * fi, self));
          break;
        case M.VenomWeaver:
          self.effects.push(new Effect(Lib.Monster(M.VenomWeaver), 224, 6, fc * fi, self));
          break;
        case M.HellBolt:
          self.effects.push(new Effect(Lib.Monster(M.HellBolt), 325, 10, fc * fi, self));
          break;
        case M.SabukGate:
          self.effects.push(fx(new Effect(LibPath.gate(M.SabukGate - 950), 24, 10, fc * fi, self), { light: -1 }));
          break;
        case M.WingedTigerLord:
          self.effects.push(new Effect(Lib.Monster(M.WingedTigerLord), 650 + self.direction * 5, 5, fc * fi, self));
          break;
        case M.HellKnight1:
        case M.HellKnight2:
        case M.HellKnight3:
        case M.HellKnight4:
          self.effects.push(new Effect(Lib.Monster(self.image), 448, 10, 600, self));
          break;
        case M.IceGuard:
          self.effects.push(new Effect(Lib.Monster(M.IceGuard), 256, 6, fc * fi, self));
          break;
        case M.DeathCrawler:
          self.effects.push(new Effect(Lib.Monster(M.DeathCrawler), 313, 11, fc * fi, self));
          w.addMapEffect(new Effect(Lib.Monster(M.DeathCrawler), 304, 9, 900, self.location, now + 900));
          break;
        case M.BurningZombie:
          self.effects.push(new Effect(Lib.Monster(M.BurningZombie), 373, 10, fc * fi, self));
          break;
        case M.FrozenZombie:
          self.effects.push(new Effect(Lib.Monster(M.FrozenZombie), 360, 10, fc * fi, self));
          break;
        case M.EarthGolem:
          self.effects.push(new Effect(Lib.Monster(M.EarthGolem), 432, 9, 9 * fi, self));
          break;
        case M.CreeperPlant:
          self.effects.push(new Effect(Lib.Monster(M.CreeperPlant), 266, 6, fc * fi, self));
          break;
        case M.SackWarrior:
          self.effects.push(new Effect(Lib.Monster(M.SackWarrior), 384, 9, fc * fi, self));
          self.effects.push(new Effect(Lib.Monster(M.SackWarrior), 393, 10, fc * fi, self));
          break;
        case M.FrozenSoldier:
          self.effects.push(new Effect(Lib.Monster(M.FrozenSoldier), 256, 10, fc * fi, self));
          break;
        case M.FrozenGolem:
          self.effects.push(new Effect(Lib.Monster(M.FrozenGolem), 456, 7, fc * fi, self));
          self.effects.push(new Effect(Lib.Monster(M.FrozenGolem), 463, 10, fc * fi, self));
          break;
        case M.DragonWarrior:
          self.effects.push(new Effect(Lib.Monster(M.DragonWarrior), 504 + self.direction * 6, 6, 6 * fi, self));
          break;
        case M.FloatingRock:
          self.effects.push(new Effect(Lib.Monster(M.FloatingRock), 152, 8, fc * fi, self));
          break;
        case M.AvengingSpirit:
          self.effects.push(new Effect(Lib.Monster(M.AvengingSpirit), 442 + self.direction * 10, 10, 10 * fi, self));
          break;
        case M.TaoistScroll:
          self.effects.push(new Effect(Lib.Monster(M.TaoistScroll), 282, 11, 11 * fi, self));
          break;
      }
      // PlayDieSound is game.ts spellHooks.action's
      break;
    case A.Dead:
      // Effects[i].Remove() for every effect (the target / mouse / minimap bookkeeping is game.ts's)
      for (let i = self.effects.length - 1; i >= 0; i--) self.effects[i]?.remove();
      break;
  }
}

/** MonsterObject.ProcessFrames: the `switch (FrameIndex)` specials, called once per new frame within an action. */
export function monsterFrame(mob: MapObj): void {
  if (mob.race !== ObjectType.Monster || !worlds) return;
  const c = ctx(mob, worlds.w, worlds.s);
  const { self, w, f, fc, fi, now, user, front, bs, snd, ms } = c;
  switch (self.action) {
    // Walking: PlayWalkSound(true / false) on frames 1 / 4 is game.ts spellHooks.frame's
    // case A.Pushed: (no frame specials)
    case A.DashAttack: {
      switch (f) {
        case 4: {
            switch (self.image) {
              case M.HornedSorceror:
                w.addMapEffect(new Effect(Lib.Monster(M.HornedSorceror), 644 + self.direction * 5, 5, 500, self.location));
                break;
            }
          }
          break;
      }
    }
    break;
    case A.Show: {
      switch (f) {
        case 1: {
            switch (self.image) {
              case M.CreeperPlant:
                self.effects.push(new Effect(Lib.Monster(M.CreeperPlant), 250, 6, 6 * 100, self));
                break;
            }
            break;
          }
      }
    }
    break;
    case A.Attack1: {
      switch (f) {
        case 1: {
            switch (self.image) {
              case M.Kirin:
                self.effects.push(new Effect(Lib.Monster(M.Kirin), 776 + self.direction * 5, 5, 500, self));
                break;
              case M.Bear:
                self.effects.push(new Effect(Lib.Monster(M.Bear), 321 + self.direction * 4, 4, fc * fi, self));
                break;
              case M.Jar1:
                self.effects.push(new Effect(Lib.Monster(M.Jar1), 128 + self.direction * 3, 3, 300, self));
                break;
              case M.Jar2:
                self.effects.push(new Effect(Lib.Monster(M.Jar2), 624 + self.direction * 8, 8, 800, self));
                break;
              case M.AntCommander:
                self.effects.push(new Effect(Lib.Monster(M.AntCommander), 368 + self.direction * 8, 8, 8 * fi, self));
                break;
            }
            break;
          }
        case 2: {
            switch (self.image) {
              // Sanjian
              case M.FurbolgCommander:
                self.effects.push(new Effect(Lib.Monster(M.FurbolgCommander), 325 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.StainHammerCat:
                self.effects.push(new Effect(Lib.Monster(M.StainHammerCat), 240 + self.direction * 4, 4, fc * fi, self));
                break;
              case M.HornedWarrior:
                self.effects.push(new Effect(Lib.Monster(M.HornedWarrior), 752 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.FrozenFighter:
                self.effects.push(new Effect(Lib.Monster(M.FrozenFighter), 336 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.PurpleFaeFlower: // Slash effect on mob
                self.effects.push(new Effect(Lib.Monster(M.PurpleFaeFlower), 436 + self.direction * 6, 6, 6 * fi, self));
                break;
            }
          }
          break;
        case 3: {
            ms("swing");
            switch (self.image) {
              // Sanjian
              case M.Furball:
                self.effects.push(new Effect(Lib.Monster(M.Furball), 256 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.FurbolgWarrior:
                self.effects.push(new Effect(Lib.Monster(M.FurbolgWarrior), 320 + self.direction * 5, 5, 5 * fi, self));
                break;
              case M.RightGuard:
                self.effects.push(new Effect(Lib.Monster(M.RightGuard), 272 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.LeftGuard:
                self.effects.push(new Effect(Lib.Monster(M.LeftGuard), 272 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.Shinsu1:
                self.effects.push(new Effect(Lib.Monster(M.Shinsu1), 224 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.DeathCrawler:
                self.effects.push(new Effect(Lib.Monster(M.DeathCrawler), 248 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.FrozenZombie:
                self.effects.push(new Effect(Lib.Monster(M.FrozenZombie), 312 + self.direction * 5, 5, 2 * fi, self));
                break;
              case M.TucsonWarrior:
                self.effects.push(new Effect(Lib.Monster(M.TucsonWarrior), 296 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.ArmadilloElder:
                self.effects.push(new Effect(Lib.Monster(M.ArmadilloElder), 488 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.Mandrill:
                self.effects.push(new Effect(Lib.Monster(M.Mandrill), 264 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.ArmedPlant:
                self.effects.push(new Effect(Lib.Monster(M.ArmedPlant), 256 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.AvengerPlant:
                self.effects.push(new Effect(Lib.Monster(M.AvengerPlant), 224 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.AvengingSpirit:
                self.effects.push(new Effect(Lib.Monster(M.AvengingSpirit), 280 + self.direction * 8, 8, 8 * fi, self));
                break;
              case M.SackWarrior:
                self.effects.push(new Effect(Lib.Monster(M.SackWarrior), 344 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.Bear:
                self.effects.push(new Effect(Lib.Monster(M.Bear), 321 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.FrozenKnight:
                self.effects.push(new Effect(Lib.Monster(M.FrozenKnight), 360 + self.direction * 3, 3, 6 * fi, self));
                break;
              case M.IcePhantom:
                self.effects.push(new Effect(Lib.Monster(M.IcePhantom), 640 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.DragonWarrior:
                self.effects.push(new Effect(Lib.Monster(M.DragonWarrior), 552 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.BurningZombie:
                self.effects.push(new Effect(Lib.Monster(M.BurningZombie), 312 + self.direction * 5, 5, 2 * fi, self));
                break;
              case M.DarkCaptain:
                self.effects.push(new Effect(Lib.Monster(M.DarkCaptain), 1168 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.BlackTortoise:
                w.addMapEffect(new Effect(Lib.Monster(M.BlackTortoise), 360, 6, 6 * fi, front, now));
                break;
            }
            break;
          }
        case 4: {
            switch (self.image) {
              case M.GeneralMeowMeow:
                self.effects.push(new Effect(Lib.Monster(M.GeneralMeowMeow), 416 + self.direction * 5, 5, 5 * fi, self));
                break;
              case M.Armadillo:
                self.effects.push(new Effect(Lib.Monster(M.Armadillo), 480 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.StainHammerCat:
                self.effects.push(new Effect(Lib.Monster(M.StainHammerCat), 272 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.StrayCat:
                self.effects.push(new Effect(Lib.Monster(M.StrayCat), 528 + self.direction * 5, 5, 5 * fi, self));
                break;
              case M.OmaSlasher:
                self.effects.push(new Effect(Lib.Monster(M.OmaSlasher), 304 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.AvengerPlant:
                w.addMapEffect(new Effect(Lib.Monster(M.AvengerPlant), 248, 6, 600, front, now));
                break;
              case M.ManTree:
                self.effects.push(new Effect(Lib.Monster(M.ManTree), 472 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.HornedMage:
                self.effects.push(new Effect(Lib.Monster(M.HornedMage), 783, 9, 800, self));
                break;
              case M.DarkWraith:
                self.effects.push(new Effect(Lib.Monster(M.DarkWraith), 720 + self.direction * 3, 3, 300, self));
                const darkWraithEffect = new Effect(Lib.Monster(M.DarkWraith), 744, 6, 600, front, now);
                w.addMapEffect(darkWraithEffect);
                break;
              case M.FightingCat:
                self.effects.push(fx(new Effect(Lib.Monster(M.FightingCat), 208 + self.direction * 3, 3, 4 * fi, self), { blend: true }));
                break;
              case M.FlamingMutant:
                self.effects.push(new Effect(Lib.Monster(M.FlamingMutant), 304, 6, 6 * fi, self));
                break;
              case M.DarkOmaKing:
                self.effects.push(new Effect(Lib.Monster(M.DarkOmaKing), 1568 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.WaterDragon:
                const waterDragonEffect = new Effect(Lib.Monster(M.WaterDragon), 905, 9, 900, front, now + 300);
                w.addMapEffect(waterDragonEffect);
                break;
              case M.PurpleFaeFlower: // Animation on the target.
                const purpleFaeFlowerEffect = new Effect(Lib.Monster(M.PurpleFaeFlower), 483, 7, 700, front, now + 300);
                w.addMapEffect(purpleFaeFlowerEffect);
                break;
            }
          }
          break;
        case 5: {
            switch (self.image) {
              // Sanjian
              case M.FurbolgGuard:
                const ob = w.getObject(self.targetId);
                if (ob != null)
                  ob.effects.push(new Effect(Lib.Monster(M.FurbolgGuard), 384, 7, 600, ob));
                break;
              case M.FlyingStatue:
                w.addMapEffect(new Effect(Lib.Monster(M.FlyingStatue), 344, 8, 800, front, now));
                break;
              case M.OmaAssassin:
                self.effects.push(new Effect(Lib.Monster(M.OmaAssassin), 312 + self.direction * 5, 5, 5 * fi, self));
                break;
              case M.PlagueCrab:
                self.effects.push(new Effect(Lib.Monster(M.PlagueCrab), 488 + self.direction * 7, 7, 7 * fi, self));
                break;
              case M.ScalyBeast:
                self.effects.push(new Effect(Lib.Monster(M.ScalyBeast), 344 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.HornedSorceror:
                self.effects.push(new Effect(Lib.Monster(M.HornedSorceror), 536 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.SnowWolfKing:
                self.effects.push(new Effect(Lib.Monster(M.SnowWolfKing), 456 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.FrozenAxeman:
                self.effects.push(new Effect(Lib.Monster(M.FrozenAxeman), 528 + self.direction * 3, 3, 300, self));
                break;
              case M.FrozenMagician:
                self.effects.push(new Effect(Lib.Monster(M.FrozenMagician), 840 + self.direction * 4, 4, 400, self));
                const frozenMagicianEffect = new Effect(Lib.Monster(M.FrozenMagician), 872, 6, 600, front, now + 300);
                w.addMapEffect(frozenMagicianEffect);
                break;
            }
            break;
          }
        case 6: {
            switch (self.image) {
              case M.FrozenMiner:
                self.effects.push(new Effect(Lib.Monster(M.FrozenMiner), 432 + self.direction * 3, 3, 300, self));
                const source = pointMove(self.location, self.direction, 1);
                const ef = new Effect(Lib.Monster(M.FrozenMiner), 456, 6, 600, source, now + 300);
                w.addMapEffect(ef);
                break;
              case M.SnowYeti:
                self.effects.push(new Effect(Lib.Monster(M.SnowYeti), 504 + self.direction * 4, 4, 400, self));
                break;
              case M.IceCrystalSoldier:
                const source2 = pointMove(self.location, self.direction, 1);
                w.addMapEffect(new Effect(Lib.Monster(M.IceCrystalSoldier), 464, 6, 600, source2, now)); // C# reuses FrozenMiner's source / ef locals
                break;
            }
            break;
          }
        case 7: {
            switch (self.image) {
              case M.AxePlant:
                self.effects.push(new Effect(Lib.Monster(M.AxePlant), 256 + self.direction * 10, 10, 10 * fi, self));
                break;
              case M.TreeQueen://Fire Bombardment
                self.effects.push(fx(new Effect(Lib.Monster(M.TreeQueen), 35, 13, 1300, self), { blend: true }));
                break;
              case M.HornedCommander:
                self.effects.push(fx(new Effect(Lib.Monster(M.HornedCommander), 784 + self.direction * 3, 3, 300, self), { blend: true }));
                break;
            }
            break;
          }
        case 8: {
            const ob = w.getObject(self.targetId);
            switch (self.image) {
              case M.FurbolgCommander:
                if (ob != null)
                  ob.effects.push(new Effect(Lib.Monster(M.FurbolgCommander), 320, 5, 500, ob));
                break;
            }
            break;
          }
        case 9: {
            switch (self.image) {
              case M.SeedingsGeneral:
                self.effects.push(new Effect(Lib.Monster(M.SeedingsGeneral), 736 + self.direction * 9, 9, 900, self));
                break;
            }
            break;
          }
      }
    }
    break;
    case A.Attack2: {
      switch (f) {
        case 1: {
            switch (self.image) {
              // BabySnowMan "SnowmanSnow": effects.ts creatureFrameEffects
              case M.CannibalTentacles:
                self.effects.push(new Effect(Lib.Monster(M.CannibalTentacles), 400 + self.direction * 9, 9, 9 * fi, self));
                break;
              case M.DarkOmaKing:
                snd(bs + 6, false, 800);
                snd(bs + 6, false, 1500);
                self.effects.push(new Effect(Lib.Monster(M.DarkOmaKing), 1600, 30, 30 * fi, self));
                break;
            }
          }
          break;
        case 2: {
            switch (self.image) {
              // Sanjian
              case M.FurbolgWarrior:
                self.effects.push(new Effect(Lib.Monster(M.FurbolgWarrior), 360 + self.direction * 5, 5, 5 * fi, self));
                break;
              case M.BlackHammerCat:
                self.effects.push(new Effect(Lib.Monster(M.BlackHammerCat), 648 + self.direction * 11, 11, 11 * fi, self));
                break;
              case M.HornedCommander:
                self.effects.push(new Effect(Lib.Monster(M.HornedCommander), 1142 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.SnowWolfKing:
                self.effects.push(new Effect(Lib.Monster(M.SnowWolfKing), 480, 9, 9 * fi, self));
                break;
            }
          }
          break;
        case 3: {
            switch (self.image) {
              // Sanjian
              case M.GlacierBeast:
                self.effects.push(new Effect(Lib.Monster(M.GlacierBeast), 310 + self.direction * 4, 4, 4 * fi, self));
                break;
              case M.KingGuard:
                self.effects.push(fx(new Effect(Lib.Monster(M.KingGuard), 773, 10, 1000, self), { blend: true }));
                break;
              case M.Behemoth:
                self.effects.push(new Effect(Lib.Monster(M.Behemoth), 768, 10, fc * fi, self));
                break;
              case M.FlameQueen:
                self.effects.push(new Effect(Lib.Monster(M.FlameQueen), 720, 9, fc * fi, self));
                break;
              case M.DemonGuard:
                self.effects.push(new Effect(Lib.Monster(M.DemonGuard), 288 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.TucsonMage:
                self.effects.push(new Effect(Lib.Monster(M.TucsonMage), 296 + self.direction * 10, 10, 10 * fi, self));
                break;
              case M.Armadillo:
                self.effects.push(new Effect(Lib.Monster(M.Armadillo), 496 + self.direction * 12, 12, 6 * fi, self));
                break;
              case M.CatWidow:
                self.effects.push(new Effect(Lib.Monster(M.CatWidow), 256 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.SnowWolf:
                self.effects.push(new Effect(Lib.Monster(M.SnowWolf), 328, 9, fc * fi, self));
                break;
              case M.BlackTortoise:
                self.effects.push(new Effect(Lib.Monster(M.BlackTortoise), 366 + self.direction * 4, 4, 4 * fi, self));
                w.addMapEffect(new Effect(Lib.Monster(M.BlackTortoise), 398, 7, 7 * fi, front, now));
                break;
              case M.TreeQueen:
                self.effects.push(new Effect(Lib.Monster(M.TreeQueen), 66, 16, 16 * fi, self));
                break;
              case M.RhinoWarrior:
                w.addMapEffect(new Effect(Lib.Monster(M.RhinoWarrior), 376, 8, 800, front, now));
                break;
              case M.FrozenFighter:
                self.effects.push(new Effect(Lib.Monster(M.FrozenFighter), 384 + self.direction * 5, 5, 5 * fi, self));
                break;
            }
          }
          break;
        case 4: {
            switch (self.image) {
              // Sanjian
              case M.GlacierSnail:
                self.effects.push(new Effect(Lib.Monster(M.GlacierSnail), 344 + self.direction * 5, 5, 5 * fi, self));
                break;
              case M.DemonWolf:
                self.effects.push(new Effect(Lib.Monster(M.DemonWolf), 312 + self.direction * 3, 3, 300, self));
                break;
              case M.TucsonWarrior:
                self.effects.push(new Effect(Lib.Monster(M.TucsonWarrior), 344 + self.direction * 9, 9, 9 * fi, self));
                break;
              case M.PeacockSpider:
                self.effects.push(new Effect(Lib.Monster(M.PeacockSpider), 592 + self.direction * 9, 9, 9 * fi, self));
                break;
              case M.SackWarrior:
                self.effects.push(new Effect(Lib.Monster(M.SackWarrior), 368 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.ManTree:
                self.effects.push(new Effect(Lib.Monster(M.ManTree), 488 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.DarkWraith:
                self.effects.push(new Effect(Lib.Monster(M.DarkWraith), 750 + self.direction * 5, 5, 500, self));
                const ef = new Effect(Lib.Monster(M.DarkWraith), 790, 6, 600, front, now);
                w.addMapEffect(ef);
                break;
              case M.HornedWarrior:
                self.effects.push(new Effect(Lib.Monster(M.HornedWarrior), 832 + self.direction * 10, 10, 10 * fi, self));
                break;
              case M.Turtlegrass:
                self.effects.push(new Effect(Lib.Monster(M.Turtlegrass), 360 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.AntCommander:
                const ef1 = new Effect(Lib.Monster(M.AntCommander), 484, 6, 600, front, now);
                w.addMapEffect(ef1);
                break;
            }
            break;
          }
        case 5: {
            switch (self.image) {
              case M.GeneralMeowMeow:
                self.effects.push(fx(new Effect(Lib.Monster(M.GeneralMeowMeow), 456 + self.direction * 7, 7, 7 * fi, self), { blend: true }));
                break;
              case M.TucsonGeneral:
                self.effects.push(new Effect(Lib.Monster(M.TucsonGeneral), 544, 8, 8 * fi, self));
                break;
              case M.FrozenMiner:
                self.effects.push(new Effect(Lib.Monster(M.FrozenMiner), 462 + self.direction * 5, 5, 500, self));
                break;
              case M.IceCrystalSoldier:
                const ef = new Effect(Lib.Monster(M.IceCrystalSoldier), 470, 6, 600, front, now);
                w.addMapEffect(ef);
                break;
              case M.Bear:
                const bleedEffect = new Effect(Lib.Monster(M.Bear), 312, 9, 900, front, now);
                w.addMapEffect(bleedEffect);
                break;
              case M.FlyingStatue:
                const flyingStatueEffect1 = new Effect(Lib.Monster(M.FlyingStatue), 362, 8, 800, front, now);
                w.addMapEffect(flyingStatueEffect1);
                break;
              case M.HornedCommander:
                self.effects.push(fx(new Effect(Lib.Monster(M.HornedCommander), 784 + self.direction * 3, 3, 300, self), { blend: true }));
                break;
            }
            break;
          }
        case 6: {
            switch (self.image) {
              case M.SeedingsGeneral:
                self.effects.push(new Effect(Lib.Monster(M.SeedingsGeneral), 1136 + self.direction * 6, 6, 600, self));
                break;
              case M.OmaBlest:
                self.effects.push(new Effect(Lib.Monster(M.OmaBlest), 392 + self.direction * 5, 5, 500, self));
                break;
              case M.WereTiger:
                self.effects.push(new Effect(Lib.Monster(M.WereTiger), 344 + self.direction * 6, 6, 600, self));
                break;
              case M.Kirin:
                const source2 = pointMove(self.location, self.direction, 2);
                const ef = new Effect(Lib.Monster(M.Kirin), 816, 8, 800, source2, now);
                w.addMapEffect(ef);
                break;
              case M.FrozenAxeman:
                self.effects.push(new Effect(Lib.Monster(M.FrozenAxeman), 558 + self.direction * 3, 3, 300, self));
                break;
              case M.SnowYeti:
                self.effects.push(new Effect(Lib.Monster(M.SnowYeti), 536 + self.direction * 3, 3, 300, self));
                break;
            }
          }
          break;
        case 7: {
            switch (self.image) {
              case M.ElephantMan:
                self.effects.push(fx(new Effect(Lib.Monster(M.ElephantMan), 368, 9, 900, self), { blend: true }));
                break;
              case M.ScalyBeast:
                self.effects.push(fx(new Effect(Lib.Monster(M.ScalyBeast), 368 + self.direction * 3, 3, 3 * fi, self), { blend: false }));
                break;
            }
          }
          break;
        case 8: {
            switch (self.image) {
              case M.StrayCat:
                self.effects.push(new Effect(Lib.Monster(M.StrayCat), 584 + self.direction * 6, 6, 6 * fi, self));
                break;
            }
          }
          break;
        case 9: {
            switch (self.image) {
              case M.ScalyBeast:
                self.effects.push(fx(new Effect(Lib.Monster(M.ScalyBeast), 392, 3, 300, self), { blend: true }));
                break;
            }
          }
          break;
        case 10: {
            switch (self.image) {
              case M.StoningStatue:
                self.effects.push(new Effect(Lib.Monster(M.StoningStatue), 642, 15, 15 * 100, self));
                snd(bs + 7);
                break;
            }
          }
          break;
        case 11: {
            switch (self.image) {
              case M.BlackHammerCat:
                self.effects.push(new Effect(Lib.Monster(M.BlackHammerCat), 736 + self.direction * 8, 8, 800, self));
                break;
            }
          }
          break;
        case 19: {
            switch (self.image) {
              case M.StoningStatue:
                self.effects.push(new Effect(Lib.Monster(M.StoningStatue), 624, 8, 8 * 100, self));
                break;
            }
          }
          break;
      }
      if (f === 3) ms("swing");
    }
    break;
    case A.Attack3: {
      switch (f) {
        case 1:
          switch (self.image) {
            // OlympicFlame "CreatureFlame": effects.ts creatureFrameEffects
            case M.GasToad:
              self.effects.push(new Effect(Lib.Monster(M.GasToad), 440, 9, 9 * fi, self));
              break;
            case M.HornedCommander: {
                //Spin
                const loops = self.spellLevel;
                const duration = 7 * fi;
                const totalDuration = loops * duration;
                if (true /* FrameLoop == null: no frame loops in the web client */) {
                  for (let i = 0; i < loops; i++) {
                    snd(8451, false, 0 + (i * duration));
                  }
                  self.effects.push(fx(new Effect(Lib.Monster(M.HornedCommander), 808 + self.direction * 7, 7, duration, self), { repeat: true, repeatUntil: now + totalDuration }));
                }
                // LoopFrame(FrameIndex, 3, fi, totalDuration); (frame loops not ported)
              }
              break;
            case M.SnowWolfKing:
              self.effects.push(new Effect(Lib.Monster(M.SnowWolfKing), 489 + self.direction * 9, 9, 9 * fi, self));
              break;
          }
          break;
        case 2:
          break;
        case 3: {
            switch (self.image) {
              case M.WingedTigerLord:
                self.effects.push(new Effect(Lib.Monster(M.WingedTigerLord), 632, 8, 600, self, 0, true));
                break;
              case M.DarkWraith:
                self.effects.push(new Effect(Lib.Monster(M.DarkWraith), 796 + self.direction * 5, 5, 500, self));
                break;
              case M.HornedSorceror: {
                  const loops = self.spellLevel;
                  const duration = 5 * fi;
                  const totalDuration = loops * duration;
                  if (true /* FrameLoop == null: no frame loops in the web client */) {
                    for (let i = 0; i < loops; i++) {
                      snd(bs + 7, false, 0 + (i * duration));
                    }
                    self.effects.push(fx(new Effect(Lib.Monster(M.HornedSorceror), 971 + self.direction * 5, 5, duration, self), { repeat: true, repeatUntil: now + totalDuration }));
                  }
                  // LoopFrame(FrameIndex, 1, fi, totalDuration); (frame loops not ported)
                }
                break;
            }
          }
          break;
        case 4:
          switch (self.image) {
            // OlympicFlame "CreatureSmoke": effects.ts creatureFrameEffects
            case M.Kirin:
              self.effects.push(new Effect(Lib.Monster(M.Kirin), 824 + self.direction * 7, 7, 700, self));
              break;
            case M.FrozenAxeman:
              self.effects.push(new Effect(Lib.Monster(M.FrozenAxeman), 588 + self.direction * 3, 3, 300, self));
              break;
            case M.ManTree:
              self.effects.push(new Effect(Lib.Monster(M.ManTree), 504 + self.direction * 2, 2, 200, self));
              break;
            case M.DragonWarrior:
              const source = pointMove(self.location, self.direction, 1);
              const effect = new Effect(Lib.Monster(M.DragonWarrior), 664, 6, 600, source, now + 300);
              w.addMapEffect(effect);
              break;
          }
          break;
        case 5:
          switch (self.image) {
            case M.WhiteMammoth:
              self.effects.push(new Effect(Lib.Monster(M.WhiteMammoth), 376, 5, fc * fi, self));
              break;
            case M.ManTree:
              const source = pointMove(self.location, self.direction, 1);
              const ef = new Effect(Lib.Monster(M.ManTree), 520, 8, 800, source, now, true);
              w.addMapEffect(ef);
              break;
            case M.HornedSorceror:
              snd(bs + 5);
              self.effects.push(new Effect(Lib.Monster(M.HornedSorceror), 624, 10, 10 * fi, self));
              break;
            case M.HornedCommander:
              snd(8452, false);
              self.effects.push(new Effect(Lib.Monster(M.HornedCommander), 864, 8, 8 * fi, self));
              break;
          }
          break;
        case 6:
          switch (self.image) {
            case M.RestlessJar:
              self.effects.push(new Effect(Lib.Monster(M.RestlessJar), 512, 7, 700, self));
              break;
          }
          break;
        case 10:
          switch (self.image) {
            case M.StrayCat:
              self.effects.push(new Effect(Lib.Monster(M.StrayCat), 728 + self.direction * 10, 10, 1000, self));
              break;
          }
          break;
      }
    }
    break;
    case A.Attack4: {
      switch (f) {
        case 1:
          switch (self.image) {
            case M.HornedCommander: {
                //Rockfall
                const loops = self.spellLevel;
                const duration = 5 * fi;
                const totalDuration = loops * duration;
                if (true /* FrameLoop == null: no frame loops in the web client */) {
                  for (let i = 0; i < loops; i++) {
                    snd(8453, false, 0 + (i * duration));
                  }
                  self.effects.push(fx(new Effect(Lib.Monster(M.HornedCommander), 1026 + self.direction * 5, 5, duration, self), { repeat: true, repeatUntil: now + totalDuration }));
                }
                // LoopFrame(FrameIndex, 3, fi, totalDuration); (frame loops not ported)
              }
              break;
          }
          break;
        case 3:
          switch (self.image) {
            case M.SnowWolfKing:
              self.effects.push(new Effect(Lib.Monster(M.SnowWolfKing), 581 + self.direction * 3, 3, 3 * fi, self));
              break;
          }
          break;
        case 5:
          switch (self.image) {
            case M.HornedCommander:
              snd(8454);
              self.effects.push(new Effect(Lib.Monster(M.HornedCommander), 1078 + self.direction * 8, 8, 8 * fi, self));
              break;
          }
          break;
      }
    }
    break;
    case A.Attack5: {
      switch (f) {
        case 4: {
            switch (self.image) {
              case M.HornedCommander:
                // LoopFrame(FrameIndex, 2, fi, self.spellLevel * 1000); (frame loops not ported)
                break;
            }
            break;
          }
      }
    }
    break;
    case A.AttackRange1: {
      if (f === 2) ms("swing");
      let ob: MapObj | undefined;
      let missile: Missile;
      switch (f) {
        case 1: {
            switch (self.image) {
              case M.GuardianRock:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Magic2, 1410, 10, 400, ob));
                  snd(bs + 6);
                }
                break;
              case M.OmaWitchDoctor:
                self.effects.push(new Effect(Lib.Monster(M.OmaWitchDoctor), 792 + self.direction * 7, 7, 7 * fi, self));
                break;
              case M.FloatingRock:
                self.effects.push(new Effect(Lib.Monster(M.FloatingRock), 159 + self.direction * 7, 7, 7 * fi, self));
                break;
              case M.KingHydrax:
                self.effects.push(new Effect(Lib.Monster(M.KingHydrax), 368 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.BlueSoul:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.BlueSoul), 240, 10, 700, ob));
                  snd(bs + 7);
                }
                break;
              case M.HornedCommander:
                self.effects.push(new Effect(Lib.Monster(M.HornedCommander), 872 + self.direction * 7, 7, 7 * fi, self));
                break;
            }
            break;
          }
        case 2: {
            switch (self.image) {
              case M.LeftGuard:
                self.effects.push(new Effect(Lib.Monster(M.LeftGuard), 336 + self.direction * 3, 3, 3 * fi, self));
                break;
              case M.DreamDevourer:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.DreamDevourer), 264, 7, 7 * fi, ob));
                }
                break;
              case M.DarkDevourer:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.DarkDevourer), 480, 7, 7 * fi, ob));
                }
                break;
              case M.ManectricClaw:
                self.effects.push(new Effect(Lib.Monster(M.ManectricClaw), 304 + self.direction * 10, 10, 10 * fi, self));
                break;
              case M.FlameSpear:
                self.effects.push(new Effect(Lib.Monster(M.FlameSpear), 544 + self.direction * 10, 10, 10 * 100, self));
                break;
              case M.FrozenMagician:
                self.effects.push(new Effect(Lib.Monster(M.FrozenMagician), 512 + self.direction * 6, 6, 6 * fi, self));
                break;
              case M.PurpleFaeFlower:
                missile = createProjectile(self, w, 331, Lib.Monster(M.PurpleFaeFlower), true, 6, 60, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.PurpleFaeFlower), 427, 9, 900, missile.target!), { blend: true }));
                  });
                }
                break;
            }
            break;
          }
        case 3: {
            switch (self.image) {
              case M.Jar2:
                missile = createProjectile(self, w, 688, Lib.Monster(M.Jar2), true, 4, 50, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.Jar2), 752, 8, 500, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.HardenRhino:
                self.effects.push(new Effect(Lib.Monster(M.HardenRhino), 392, 5, 5 * fi, self));
                break;
              case M.HornedArcher:
                self.effects.push(new Effect(Lib.Monster(M.HornedArcher), 336 + self.direction * 3, 3, 3 * fi, self, 0, true));
                break;
              case M.ColdArcher:
                self.effects.push(new Effect(Lib.Monster(M.ColdArcher), 336 + self.direction * 4, 4, 4 * fi, self, 0, true));
                break;
            }
            break;
          }
        case 4: {
            switch (self.image) {
              // Sanjian
              case M.FurbolgArcher:
                if (w.getObject(self.targetId) != null) {
                  createProjectile(self, w, 344, Lib.Monster(M.FurbolgArcher), false, 5, 30, 0);
                  snd(bs + 6);
                }
                break;
              case M.FurbolgGuard:
                missile = createProjectile(self, w, 391, Lib.Monster(M.FurbolgGuard), false, 1, 30, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(new Effect(Lib.Monster(M.FurbolgGuard), 407, 7, 600, missile.target!));
                    snd(bs + 6);
                  });
                }
                break;
              case M.AxeSkeleton:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 224, Lib.Monster(M.AxeSkeleton), false, 3, 30, 0);
                break;
              case M.Dark:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 224, Lib.Monster(M.Dark), false, 3, 30, 0);
                break;
              case M.ZumaArcher:
              case M.BoneArcher:
                if (w.getObject(self.targetId) != null) {
                  createProjectile(self, w, 224, Lib.Monster(M.ZumaArcher), false, 1, 30, 0);
                  snd(bs + 6);
                }
                break;
              case M.RedThunderZuma:
              case M.FrozenRedZuma:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Dragon, 400 + rnd(3) * 10, 5, 300, ob));
                  snd(bs + 6);
                }
                break;
              case M.BoneLord:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 784, Lib.Monster(M.BoneLord), true, 6, 30, 0, 6, false);
                break;
              case M.RightGuard:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Magic2, 10, 5, 300, ob));
                }
                break;
              case M.LeftGuard:
                if (w.getObject(self.targetId) != null) {
                  createProjectile(self, w, 10, Lib.Magic, true, 6, 30, 4);
                }
                break;
              case M.MinotaurKing:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.MinotaurKing), 320, 20, 1000, ob));
                }
                break;
              case M.FrostTiger:
                if (w.getObject(self.targetId) != null) {
                  createProjectile(self, w, 410, Lib.Magic2, true, 4, 30, 6);
                }
                break;
              case M.Yimoogi:
              case M.RedYimoogi:
              case M.Snake10:
              case M.Snake11:
              case M.Snake12:
              case M.Snake13:
              case M.Snake14:
              case M.Snake15:
              case M.Snake16:
              case M.Snake17:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Magic2, 1250, 15, 1000, ob));
                  snd(bs + 6);
                }
                break;
              case M.HolyDeva:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Magic2, 10, 5, 300, ob));
                  snd(bs + 6);
                }
                break;
              case M.CrossbowOma:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 38, Lib.Monster(M.CrossbowOma), false, 1, 30, 6);
                break;
              case M.DarkCrossbowOma:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 38, Lib.Monster(M.DarkCrossbowOma), false, 1, 30, 6);
                break;
              case M.WingedOma:
              case M.DarkWingedOma:
                missile = createProjectile(self, w, 224, Lib.Monster(M.WingedOma), false, 6, 30, 0, 6, false);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.WingedOma), 272, 2, 150, missile.target!), { blend: false }));
                  });
                }
                break;
              case M.FlamingMutant:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  w.addMapEffect(fx(new Effect(Lib.Monster(M.FlamingMutant), 320, 10, 1000, ob.location, now), { blend: true }));
                  //snd(bs + 6);
                }
                break;
              case M.PoisonHugger:
                missile = createProjectile(self, w, 208, Lib.Monster(M.PoisonHugger), true, 1, 30, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.PoisonHugger), 224, 5, 150, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.RedFoxman:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.RedFoxman), 224, 9, 300, ob));
                  snd(bs + 6);
                }
                break;
              case M.WhiteFoxman:
                missile = createProjectile(self, w, 1160, Lib.Magic, true, 3, 30, 7);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(new Effect(Lib.Monster(M.WhiteFoxman), 352, 10, 600, missile.target!));
                    snd(bs + 6);
                  });
                }
                break;
              case M.TrapRock:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.TrapRock), 26, 10, 600, ob));
                  snd(bs + 6);
                }
                break;
              case M.HedgeKekTal:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 38, Lib.Monster(M.HedgeKekTal), false, 4, 30, 6);
                break;
              case M.BigHedgeKekTal:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 38, Lib.Monster(M.BigHedgeKekTal), false, 4, 30, 6);
                break;
              case M.EvilMir:
                missile = createProjectile(self, w, 60, Lib.Dragon, true, 10, 10, 0);
                if (missile.direction > 12)
                  missile.direction = 12;
                if (missile.direction < 7)
                  missile.direction = 7;
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(new Effect(Lib.Dragon, 200, 20, 600, missile.target!));
                  });
                }
                break;
              case M.ArcherGuard:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 38, Lib.Monster(M.ArcherGuard), false, 3, 30, 6);
                break;
              case M.SpittingToad:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 280, Lib.Monster(M.SpittingToad), true, 6, 30, 0);
                break;
              case M.ArcherGuard2:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 38, Lib.Monster(M.ArcherGuard), false, 3, 30, 6);
                break;
              case M.ArcherGuard3:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 104, Lib.Monster(M.ArcherGuard3), false, 3, 30, 0);
                break;
              case M.FinialTurtle:
                missile = createProjectile(self, w, 272, Lib.Monster(M.FinialTurtle), true, 3, 30, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.FinialTurtle), 320, 10, 500, missile.target!), { blend: true }));
                    snd(20000 + Spell.FrostCrunch * 10 + 2);
                  });
                }
                break;
              case M.HellBolt:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.HellBolt), 315, 10, 600, ob));
                  snd(bs + 6);
                }
                break;
              case M.WitchDoctor:
                if (w.getObject(self.targetId) != null) {
                  missile = createProjectile(self, w, 313, Lib.Monster(M.WitchDoctor), true, 5, 30, -5, 6, false);
                  if (missile.target !== null) {
                    missile.onComplete(() => {
                      if (missile.target!.action === A.Dead) return;
                      missile.target!.effects.push(new Effect(Lib.Monster(M.WitchDoctor), 318, 10, 600, missile.target!));
                      snd(bs + 6);
                    });
                  }
                }
                break;
              case M.WingedTigerLord:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.WingedTigerLord), 640, 10, 800, ob, now + 400, false));
                }
                break;
              case M.TrollBomber:
                missile = createProjectile(self, w, 208, Lib.Monster(M.TrollBomber), false, 4, 40, -4, 6, false);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    snd(bs + 9);
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.TrollBomber), 212, 6, 600, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.TrollStoner:
                snd(bs + 9);
                missile = createProjectile(self, w, 208, Lib.Monster(M.TrollStoner), false, 4, 40, -4, 6, false);
                break;
              case M.FlameMage:
                missile = createProjectile(self, w, 544, Lib.Monster(M.FlameMage), true, 3, 20, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    missile.target!.effects.push(new Effect(Lib.Monster(M.FlameMage), 592, 10, 1000, missile.target!));
                  });
                }
                break;
              case M.FlameScythe:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.FlameScythe), 586, 9, 900, ob));
                }
                break;
              case M.FlameAssassin:
                missile = createProjectile(self, w, 592, Lib.Monster(M.FlameAssassin), true, 3, 20, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    missile.target!.effects.push(new Effect(Lib.Monster(M.FlameAssassin), 640, 6, 600, missile.target!));
                  });
                }
                break;
              case M.FlameQueen:
                self.effects.push(new Effect(Lib.Monster(M.FlameQueen), 729, 10, fc * fi, self));
                break;
              case M.AncientBringer:
                missile = createProjectile(self, w, 688, Lib.Monster(M.AncientBringer), true, 4, 50, 0, 6, false);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.AncientBringer), 720, 10, 1000, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.RestlessJar:
                missile = createProjectile(self, w, 476, Lib.Monster(M.RestlessJar), true, 2, 100, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.RestlessJar), 508, 3, 300, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.IceGuard:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.IceGuard), 262, 6, 600, ob), { blend: true }));
                  snd(bs + 6);
                }
                break;
              case M.ElementGuard:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.ElementGuard), 360 + ob.direction * 7, 7, 7 * fi, ob), { blend: true }));
                  snd(bs + 6);
                }
                break;
              case M.KingGuard:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.KingGuard), 746, 7, 700, ob), { blend: true }));
                }
                break;
              case M.BurningZombie:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.BurningZombie), 361, 12, 1000, ob));
                }
                break;
              case M.FrozenZombie:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.FrozenZombie), 352, 8, 1000, ob));
                }
                break;
              case M.CatShaman:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.CatShaman), 720, 12, 1500, ob), { blend: false }));
                }
                break;
              case M.GeneralMeowMeow:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.GeneralMeowMeow), 512, 10, 1000, ob), { blend: true }));
                }
                break;
              case M.CannibalTentacles:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 472, Lib.Monster(M.CannibalTentacles), true, 8, 100, 0, 6, false);
                break;
              case M.SwampWarrior:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.SwampWarrior), 392, 8, 800, ob), { blend: true }));
                }
                break;
              case M.PeacockSpider:
                missile = createProjectile(self, w, 664, Lib.Monster(M.PeacockSpider), true, 5, 100, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.PeacockSpider), 744, 11, 1100, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.RhinoPriest:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.RhinoPriest), 376, 9, 900, ob));
                }
                break;
              case M.TreeGuardian:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.TreeGuardian), 544, 8, 800, ob));
                }
                break;
              case M.CreeperPlant:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  w.addMapEffect(fx(new Effect(Lib.Monster(M.CreeperPlant), 250, 6, 600, ob.location, now), { blend: true }));
                  w.addMapEffect(fx(new Effect(Lib.Monster(M.CreeperPlant), 256, 10, 1000, ob.location, now), { blend: false }));
                }
                break;
              case M.FloatingWraith:
                missile = createProjectile(self, w, 248, Lib.Monster(M.FloatingWraith), true, 2, 20, 0, 6, true);
                break;
              case M.AvengingSpirit:
                missile = createProjectile(self, w, 368, Lib.Monster(M.AvengingSpirit), true, 4, 40, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.AvengingSpirit), 432, 10, 1000, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.AvengingWarrior:
                missile = createProjectile(self, w, 312, Lib.Monster(M.AvengingWarrior), true, 5, 50, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.AvengingWarrior), 392, 7, 700, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.LightningBead:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.LightningBead), 65, 12, 1200, ob));
                }
                break;
              case M.HealingBead:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.HealingBead), 61, 11, 1100, ob));
                }
                break;
              case M.PowerUpBead:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.PowerUpBead), 64, 6, 600, ob));
                }
                break;
              case M.DarkOmaKing:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  w.addMapEffect(new Effect(Lib.Monster(M.DarkOmaKing), 1715, 13, 1300, ob.location));
                }
                break;
              case M.ChieftainArcher:
                if (w.getObject(self.targetId) != null) {
                  if (self.spellLevel === 0) {
                    missile = createProjectile(self, w, 312, Lib.Monster(M.ChieftainArcher), true, 5, 50, 0, 6, true);
                    if (missile.target !== null) {
                      missile.onComplete(() => {
                        if (missile.target!.action === A.Dead) return;
                        missile.target!.effects.push(new Effect(Lib.Monster(M.ChieftainArcher), 392, 6, 600, missile.target!));
                      });
                    }
                  }
                  else if (self.spellLevel === 1) {
                    missile = createProjectile(self, w, 398, Lib.Monster(M.ChieftainArcher), true, 5, 50, 0, 6, true);
                    if (missile.target !== null) {
                      missile.onComplete(() => {
                        if (missile.target!.action === A.Dead) return;
                        missile.target!.effects.push(new Effect(Lib.Monster(M.ChieftainArcher), 478, 6, 600, missile.target!));
                      });
                    }
                  }
                  else {
                    missile = createProjectile(self, w, 484, Lib.Monster(M.ChieftainArcher), true, 5, 50, 0, 6, true);
                  }
                }
                break;
              case M.ManTree:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.ManTree), 520, 8, 1000, ob));
                }
                break;
              case M.ClawBeast:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.ClawBeast), 568, 7, 700, ob));
                }
                break;
              case M.BlackTortoise:
                const source = pointMove(self.location, self.direction, 2);
                missile = createProjectile(self, w, 444, Lib.Monster(M.BlackTortoise), true, 6, 60, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(new Effect(Lib.Monster(M.BlackTortoise), 540, 6, 600, missile.target!));
                  });
                }
                break;
              case M.DragonArcher:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 416, Lib.Monster(M.DragonArcher), true, 5, 50, 0);
                break;
              case M.HornedMage:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.HornedMage), 768, 9, 800, ob), { blend: false }));
                }
                break;
              case M.HornedArcher:
                missile = createProjectile(self, w, 360, Lib.Monster(M.HornedArcher), true, 3, 50, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.HornedArcher), 408, 6, 500, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.KingHydrax:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.KingHydrax), 416, 9, 900, ob), { drawBehind: true }));
                  snd(bs + 6);
                }
                break;
              case M.WaterDragon:
                missile = createProjectile(self, w, 800, Lib.Monster(M.WaterDragon), true, 6, 60, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.WaterDragon), 896, 9, 900, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.AntCommander:
                missile = createProjectile(self, w, 432, Lib.Monster(M.AntCommander), true, 3, 100, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.AntCommander), 480, 3, 300, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.AssassinScroll:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.AssassinScroll), 299, 8, 800, ob));
                }
                break;
              case M.TaoistScroll:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.TaoistScroll), 272, 10, 1000, ob), { blend: true }));
                }
                break;
              case M.WarriorScroll:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.WarriorScroll), 304, 11, 1100, ob), { blend: true }));
                }
                break;
              case M.WizardScroll:
                missile = createProjectile(self, w, 300, Lib.Monster(M.WizardScroll), true, 5, 50, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.WizardScroll), 380, 8, 800, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.Catapult:
                missile = createProjectile(self, w, 256, LibPath.siege(M.Catapult - 940), false, 4, 40, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(LibPath.siege(M.Catapult - 940), 288, 10, 1000, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.ChariotBallista:
                missile = createProjectile(self, w, 38, LibPath.siege(M.ChariotBallista - 940), false, 3, 30, 6);
                break;
            }
            break;
          }//end of case 4
        case 5:
          switch (self.image) {
            case M.OmaCannibal:
              missile = createProjectile(self, w, 360, Lib.Monster(M.OmaCannibal), true, 6, 60, 0);
              if (missile.target !== null) {
                missile.onComplete(() => {
                  if (missile.target!.action === A.Dead) return;
                  missile.target!.effects.push(fx(new Effect(Lib.Monster(M.OmaCannibal), 456, 7, 700, missile.target!), { blend: true }));
                });
              }
              break;
            case M.OmaMage:
              missile = createProjectile(self, w, 392, Lib.Monster(M.OmaMage), true, 8, 80, 0, 6, true);
              if (missile.target !== null) {
                missile.onComplete(() => {
                  if (missile.target!.action === A.Dead) return;
                  missile.target!.effects.push(fx(new Effect(Lib.Monster(M.OmaMage), 520, 9, 600, missile.target!), { blend: true }));
                });
              }
              break;
            case M.OmaWitchDoctor:
              ob = w.getObject(self.targetId);
              if (ob != null) {
                snd(bs + 7);
                ob.effects.push(fx(new Effect(Lib.Monster(M.OmaWitchDoctor), 848, 11, 1100, ob), { blend: true }));
              }
              break;
            case M.SnowYeti:
              missile = createProjectile(self, w, 560, Lib.Monster(M.SnowYeti), true, 6, 20, 0);
              break;
            case M.MudZombie:
              ob = w.getObject(self.targetId);
              if (ob != null) {
                ob.effects.push(fx(new Effect(Lib.Monster(M.MudZombie), 304, 7, 700, ob), { blend: false }));
              }
              break;
            case M.DarkSpirit:
              missile = createProjectile(self, w, 512, Lib.Monster(M.DarkSpirit), true, 6, 60, 0);
              if (missile.target !== null) {
                missile.onComplete(() => {
                  if (missile.target!.action === A.Dead) return;
                  missile.target!.effects.push(new Effect(Lib.Monster(M.DarkSpirit), 608, 10, 1000, missile.target!));
                });
              }
              break;
          }
          break;
        case 6: {
            switch (self.image) {
              // Sanjian
              case M.FurbolgCommander:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.FurbolgCommander), 357, 6, 600, ob), { drawBehind: true }));
                }
                break;
              case M.HornedMage:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.HornedMage), 777, 6, 800, ob) );
                }
                break;
              case M.FloatingRock:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.FloatingRock), 226, 10, 1000, ob) );
                }
                break;
              case M.FrozenArcher:
                if (w.getObject(self.targetId) != null)
                  createProjectile(self, w, 264, Lib.Monster(M.FrozenArcher), true, 5, 80, 0);
                break;
              case M.FrozenMagician:
                missile = createProjectile(self, w, 560, Lib.Monster(M.FrozenMagician), true, 6, 30, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    missile.target!.effects.push(new Effect(Lib.Monster(M.FrozenMagician), 656, 6, 600, missile.target!));
                  });
                }
                break;
              case M.IceCrystalSoldier:
                const source = pointMove(self.location, self.direction, 1);
                const ef = new Effect(Lib.Monster(M.IceCrystalSoldier), 476, 8, 800, source, now);
                w.addMapEffect(ef);
                break;
              case M.ColdArcher:
                self.effects.push(new Effect(Lib.Monster(M.ColdArcher), 368 + self.direction * 2, 2, 2 * fi, self));
                break;
              case M.HoodedSummoner:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.HoodedSummoner), 374, 13, 1300, ob), { blend: true }));
                }
                break;
            }
            break;
          }
        case 7: {
            switch (self.image) {
              case M.FrozenKnight:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.FrozenKnight), 456, 10, 1000, ob));
                }
                break;
              case M.IcePhantom:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.IcePhantom), 682, 10, 1000, ob), { blend: true }));
                  snd(bs + 6); //is this the correct sound for this mob attack?
                }
                break;
              case M.FloatingRock:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.FloatingRock), 226, 10, 1000, ob));
                  snd(bs + 6);
                }
                break;
            }
            break;
          }
        case 8: {
            break;
          }
        case 9: {
            switch (self.image) {
              case M.ColdArcher:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.ColdArcher), 384, 10, 1000, ob));
                }
                break;
            }
            break;
          }
      }
    }
    break;
    case A.AttackRange2: {
      let ob: MapObj | undefined;
      let missile: Missile;
      switch (f) {
        case 1: {
            switch (self.image) {
              case M.RestlessJar:
                const point = pointMove(self.location, self.direction, 2);
                w.addMapEffect(new Effect(Lib.Monster(M.RestlessJar), 391 + self.direction * 10, 10, 10 * fi, point));
                break;
              case M.KingHydrax:
                self.effects.push(new Effect(Lib.Monster(M.KingHydrax), 425 + self.direction * 6, 6, 600, self));
                break;
            }
            break;
          }
        case 2: {
            switch (self.image) {
              case M.FrozenMagician:
                self.effects.push(new Effect(Lib.Monster(M.FrozenMagician), 662 + self.direction * 9, 9, 9 * fi, self));
                break;
            }
            break;
          }
        case 4: {
            switch (self.image) {
              case M.FurbolgArcher:
                if (w.getObject(self.targetId) != null) {
                  missile = createProjectile(self, w, 429, Lib.Monster(M.FurbolgArcher), false, 5, 30, 0);
                  if (missile.target !== null) {
                    missile.onComplete(() => {
                      if (missile.target!.action === A.Dead) return;
                      missile.target!.effects.push(new Effect(Lib.Monster(M.FurbolgArcher), 424, 5, 500, missile.target!));
                      snd(bs + 7);
                    });
                  }
                }
                break;
              case M.RedFoxman:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.RedFoxman), 233, 10, 400, ob));
                  snd(bs + 7);
                }
                break;
              case M.WhiteFoxman:
                missile = createProjectile(self, w, 1160, Lib.Magic, true, 3, 30, 7);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(new Effect(Lib.Monster(M.WhiteFoxman), 362, 15, 1800, missile.target!));
                    snd(bs + 7);
                  });
                }
                break;
              case M.TrollKing:
                missile = createProjectile(self, w, 294, Lib.Monster(M.TrollKing), false, 4, 40, -4, 6, false);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    snd(bs + 9);
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.TrollKing), 298, 6, 600, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.AncientBringer:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.AncientBringer), 740, 14, 2000, ob));
                  snd(bs + 6);
                }
                break;
              case M.IceGuard:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.IceGuard), 268, 5, 500, ob), { blend: true }));
                  snd(bs + 6);
                }
                break;
              case M.CatShaman:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(new Effect(Lib.Monster(M.CatShaman), 732, 6, 500, ob));
                  snd(bs + 6);
                }
                break;
              case M.TucsonGeneral:
                missile = createProjectile(self, w, 592, Lib.Monster(M.TucsonGeneral), true, 9, 30, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.TucsonGeneral), 736, 9, 900, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.RhinoPriest:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.RhinoPriest), 448, 7, 700, ob), { blend: true }));
                }
                break;
              case M.TreeGuardian:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  w.addMapEffect(new Effect(Lib.Monster(M.TreeGuardian), 648 + (self.direction * 10), 10, 1000, ob.location));
                }
                break;
              case M.OmaWitchDoctor:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.OmaWitchDoctor), 859, 10, 1000, ob), { blend: true }));
                }
                break;
              case M.KingHydrax:
                missile = createProjectile(self, w, 473, Lib.Monster(M.KingHydrax), true, 4, 50, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.KingHydrax), 537, 6, 600, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.IcePhantom:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.IcePhantom), 772, 7, 700, ob), { blend: true }));
                  snd(bs + 6); //is this the correct sound for this mob attack?
                }
                break;
              case M.ColdArcher:
                missile = createProjectile(self, w, 394, Lib.Monster(M.ColdArcher), true, 3, 50, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.ColdArcher), 442, 6, 500, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.HornedArcher:
                missile = createProjectile(self, w, 414, Lib.Monster(M.HornedArcher), true, 3, 50, 0, 6, true);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    if (missile.target!.action === A.Dead) return;
                    missile.target!.effects.push(fx(new Effect(Lib.Monster(M.HornedArcher), 462, 6, 500, missile.target!), { blend: true }));
                  });
                }
                break;
              case M.HornedCommander:
                self.effects.push(new Effect(Lib.Monster(M.HornedCommander), 938 + self.direction * 9, 9, 9 * fi, self));
                self.effects.push(new Effect(Lib.Monster(M.HornedCommander), 1010 + self.direction * 2, 2, 2 * fi, self));
                break;
            }
            break;
          }
        case 8: {
            switch (self.image) {
              case M.FrozenMagician:
                missile = createProjectile(self, w, 734, Lib.Monster(M.FrozenMagician), true, 6, 30, 0);
                if (missile.target !== null) {
                  missile.onComplete(() => {
                    missile.target!.effects.push(new Effect(Lib.Monster(M.FrozenMagician), 830, 10, 1000, missile.target!));
                  });
                }
                break;
            }
            break;
          }
      }
    }
    break;
    case A.AttackRange3: {
      let ob: MapObj | undefined;
      switch (f) {
        case 4: {
            switch (self.image) {
              case M.TucsonGeneral:
                ob = w.getObject(self.targetId);
                if (ob != null) {
                  ob.effects.push(fx(new Effect(Lib.Monster(M.TucsonGeneral), 745, 17, 1000, ob), { blend: true }));
                }
                break;
            }
          }
          break;
      }
    }
    break;
    case A.Die: {
      switch (f) {
        case 1:
          switch (self.image) {
            // Sanjian
            case M.FurbolgCommander:
              self.effects.push(new Effect(Lib.Monster(M.FurbolgCommander), 320, 5, 5 * fi, self));
              break;
            case M.Furball:
              self.effects.push(new Effect(Lib.Monster(M.Furball), 288, 8, fc * fi, self));
              break;
            case M.GlacierBeast:
              self.effects.push(fx(new Effect(Lib.Monster(M.GlacierBeast), 342, 12, 1200, self), { blend: true }));
              break;
            case M.PoisonHugger:
              self.effects.push(new Effect(Lib.Monster(M.PoisonHugger), 224, 5, fc * fi, self));
              break;
            case M.Hugger:
              self.effects.push(new Effect(Lib.Monster(M.Hugger), 256, 8, fc * fi, self));
              break;
            case M.MutatedHugger:
              self.effects.push(new Effect(Lib.Monster(M.MutatedHugger), 128, 7, fc * fi, self));
              break;
            case M.CyanoGhast:
              self.effects.push(new Effect(Lib.Monster(M.CyanoGhast), 681, 7, fc * fi, self));
              break;
            case M.Hydrax:
              self.effects.push(new Effect(Lib.Monster(M.Hydrax), 240, 5, 5 * fi, self));
              break;
          }
          break;
        case 3:
          ms("dead");
          switch (self.image) {
            case M.BoneSpearman:
            case M.BoneBlademan:
            case M.BoneArcher:
              self.effects.push(new Effect(Lib.Monster(M.BoneSpearman), 224, 8, fc * fi, self));
              break;
            case M.WoodBox:
              self.effects.push(fx(new Effect(Lib.Monster(M.WoodBox), 104, 6, 6 * fi, self), { blend: true }));
              break;
            case M.BoulderSpirit:
              self.effects.push(fx(new Effect(Lib.Monster(M.BoulderSpirit), 64, 8, 8 * fi, self), { blend: true }));
              break;
          }
          break;
        // Sanjian
        case 4:
          ms("dead");
          break;
        case 5:
          switch (self.image) {
            case M.KingHydrax:
              self.effects.push(new Effect(Lib.Monster(M.KingHydrax), 543 + self.direction * 7, 7, 700, self));
              break;
            case M.Bear:
              self.effects.push(new Effect(Lib.Monster(M.Bear), 353, 9, fc * fi, self));
              break;
            case M.SnowWolfKing:
              self.effects.push(fx(new Effect(Lib.Monster(M.SnowWolfKing), 605, 10, 10 * fi, self), { drawBehind: true }));
              break;
          }
          break;
        case 9:
          switch (self.image) {
            case M.IcePhantom:
              w.addMapEffect(new Effect(Lib.Monster(M.IcePhantom), 692 + self.direction * 10, 10, 10 * fi, self.location));
              break;
          }
          break;
      }
    }
    break;
    // Revive: the C# plays PlayReviveSound on frame 3; game.ts spellHooks.action plays it at the action start instead
    // case A.Dead: (no frame specials)
  }
}

/** MonsterObject.ProcessFrames: the `UpdateFrame() >= Frame.Count` blocks (the action's last frame has played). */
export function monsterActionEnd(mob: MapObj): void {
  if (mob.race !== ObjectType.Monster || !worlds) return;
  const c = ctx(mob, worlds.w, worlds.s);
  const { self, w, f, fc, fi, now, user, front, bs, snd, ms } = c;
  switch (self.action) {
    // Show / Hide ends: Stoned toggles, the Shinsu -> Shinsu1 body swap and Remove() of the burrowers are not ported (no MapObj state)
    case A.Standing:
      switch (self.image) {
        case M.SnakeTotem: // SummonSnakes Totem
          if (TrackableEffect.getOwnerEffectID(self.id, "SnakeTotem") < 0)
            self.effects.push(new TrackableEffect(fx(new Effect(Lib.Monster(M.SnakeTotem), 2, 10, 1500, self), { repeat: true }), "SnakeTotem"));
          break;
      }
      break;
    case A.Attack1:
      // C#: after SetAction() succeeded (the next action's direction); here before it (the attack's direction)
      switch (self.image) {
        case M.EvilCentipede:
          self.effects.push(new Effect(Lib.Monster(M.EvilCentipede), 42, 10, 600, self));
          break;
        case M.ToxicGhoul:
          snd(bs + 4);
          self.effects.push(new Effect(Lib.Monster(M.ToxicGhoul), 224 + self.direction * 6, 6, 600, self));
          break;
      }
      break;
    case A.AttackRange1:
      switch (self.image) {
        case M.DragonStatue: {
          const ob = w.getObject(self.targetId);
          if (ob != null) {
            ob.effects.push(new Effect(Lib.Dragon, 350, 35, 1200, ob));
            snd(bs + 6);
          }
          break;
        }
      }
      break;
  }
}
