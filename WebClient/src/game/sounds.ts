// Per-object sound selection: a mechanical port of Client/MirObjects/MonsterObject.cs (PlaySummonSound .. PlayPetSound) and
// Client/MirObjects/PlayerObject.cs (PlayStepSound + the Wemade/Shanda/WemadeMir3 tile tables, PlayStruckSound, PlayFlinchSound,
// PlayAttackSound, PlayDieSound, PlayMountSound, the fishing frame sounds + ripples of ProcessFrames). Sound ids are
// Client/MirSounds/SoundList.cs values (table SL below).

import { Monster as M, MirClass, MirGender, MirAction, ObjectType } from "../protocol/enums.generated.ts";
import type { MapObj } from "./objects.ts";
import type { MapCells } from "../map/mapReader.ts";
import { Effect, Lib, effectWorld } from "./effects.ts";

export interface SoundWorld {
  play(id: number, loop?: boolean, delay?: number, gain?: number): void;  // SoundManager.PlaySound(index, loop, delay); web-only gain
  stop(id: number): void;                                  // SoundManager.StopSound
  user(): MapObj | null;                                   // MapObject.User
  map(): MapCells;                                         // MapControl.M2CellInfo (+ Width/Height via map.width/height)
}

export type MonsterSound = "summon" | "walkL" | "walkR" | "appear" | "popup" | "run" | "jump" | "dash" | "flinch" | "struck" | "attack" | "attack2" | "attack3"
  | "attack4" | "attack5" | "swing" | "die" | "dead" | "revive" | "range" | "range2" | "range3" | "pickup" | "pet";
export type PlayerSound = "step" | "struck" | "flinch" | "attack" | "die" | "mount";

/** Client/MirSounds/SoundList.cs (ZombieRevive is the C# literal 0705 = decimal 705). */
const SL = {
  None: 0, Music: 0, IntroMusic: 10146, SelectMusic: 10147, LoginEffect: 10100, ButtonA: 10103, ButtonB: 10104, ButtonC: 10105, Gold: 10106,
  EatDrug: 10107, ClickDrug: 10108, Teleport: 10110, LevelUp: 10156, ClickWeapon: 10111, ClickArmour: 10112, ClickRing: 10113, ClickBracelet: 10114,
  ClickNecklace: 10115, ClickHelmet: 10116, ClickBoots: 10117, ClickItem: 10118, WalkGroundL: 10001, WalkGroundR: 10002, RunGroundL: 10003,
  RunGroundR: 10004, WalkStoneL: 10005, WalkStoneR: 10006, RunStoneL: 10007, RunStoneR: 10008, WalkLawnL: 10009, WalkLawnR: 10010, RunLawnL: 10011,
  RunLawnR: 10012, WalkRoughL: 10013, WalkRoughR: 10014, RunRoughL: 10015, RunRoughR: 10016, WalkWoodL: 10017, WalkWoodR: 10018, RunWoodL: 10019,
  RunWoodR: 10020, WalkCaveL: 10021, WalkCaveR: 10022, RunCaveL: 10023, RunCaveR: 10024, WalkRoomL: 10025, WalkRoomR: 10026, RunRoomL: 10027,
  RunRoomR: 10028, WalkWaterL: 10029, WalkWaterR: 10030, RunWaterL: 10031, RunWaterR: 10032, HorseWalkL: 10033, HorseWalkR: 10034, HorseRun: 10035,
  WalkSnowL: 10036, WalkSnowR: 10037, RunSnowL: 10038, RunSnowR: 10039, SwingShort: 10050, SwingWood: 10051, SwingSword: 10052, SwingSword2: 10053,
  SwingAxe: 10054, SwingClub: 10055, SwingLong: 10056, SwingFist: 10056, StruckShort: 10060, StruckWooden: 10061, StruckSword: 10062,
  StruckSword2: 10063, StruckAxe: 10064, StruckClub: 10065, StruckBodySword: 10070, StruckBodyAxe: 10071, StruckBodyLongStick: 10072,
  StruckBodyFist: 10073, StruckArmourSword: 10080, StruckArmourAxe: 10081, StruckArmourLongStick: 10082, StruckArmourFist: 10083, StruckEvilMir: 10090,
  MaleFlinch: 10138, FemaleFlinch: 10139, MaleDie: 10144, FemaleDie: 10145, Revive: 20791, ZombieRevive: 705, MountWalkL: 10176, MountWalkR: 10177,
  MountRun: 10178, TigerStruck1: 10179, TigerStruck2: 10180, TigerAttack1: 10181, TigerAttack2: 10182, TigerAttack3: 10183, FishingThrow: 10184,
  FishingPull: 10185, Fishing: 10186, WolfRide1: 10188, WolfRide2: 10189, WolfAttack1: 10190, WolfAttack2: 10191, WolfAttack3: 10192, WolfStruck1: 10193,
  WolfStruck2: 10194, TigerRide1: 10218, TigerRide2: 10219, PetPig: 10500, PetChick: 10501, PetKitty: 10502, PetSkeleton: 10503, PetPigman: 10504,
  PetWemade: 10505, PetBlackKitten: 10506, PetDragon: 10507, PetOlympic: 10508, PetFrog: 10510, PetMonkey: 10511, PetAngryBird: 10512, PetFoxey: 10513,
  PetMedicalRat: 10514, PetPickup: 10520
} as const;

const CLASS_WEAPON_COUNT = 100; // Globals.ClassWeaponCount

/** CMain.Random.Next(min, max): max exclusive. */
const randomNext = (min: number, max: number) => min + Math.floor(Math.random() * (max - min));

// TODO MapObj: struckWeapon (MapObject.StruckWeapon, set from the attacker's weapon in the Struck action)
const struckWeapon = (ob: MapObj): number => (ob as any).struckWeapon ?? -1;

/** MonsterObject.BaseSound = (ushort)BaseImage * 10 (Load, and the Shinsu/Shinsu1 swaps in ProcessFrames/SetAction recompute it from BaseImage). */
export function baseSound(ob: MapObj): number {
  return (ob.image & 0xffff) * 10;
}

// ---------------------------------------------------------------- MonsterObject

/** Web-only: pets (a monster with a master, "Name(Owner)", or an intelligent creature) play at this gain; the user found them loud (2026-10-16). */
const PET_GAIN = 0.5;

const isPet = (ob: MapObj): boolean => ob.name.endsWith(")") || (ob.image & 0xffff) >= 10000;

/** `w` with every sound at PET_GAIN when `ob` is a pet. */
export function petWorld(ob: MapObj, w: SoundWorld): SoundWorld {
  return isPet(ob) ? { ...w, play: (id, loop, delay) => w.play(id, loop, delay, PET_GAIN) } : w;
}

/** MonsterObject.PlaySummonSound .. PlayPetSound (dispatch by kind). */
export function monsterSound(ob: MapObj, kind: MonsterSound, w: SoundWorld): void {
  w = petWorld(ob, w);
  switch (kind) {
    case "summon": playSummonSound(ob, w); return;
    case "walkL": playWalkSound(ob, w, true); return;
    case "walkR": playWalkSound(ob, w, false); return;
    case "appear": playAppearSound(ob, w); return;
    case "popup": playPopupSound(ob, w); return;
    case "run": playRunSound(ob, w); return;
    case "jump": playJumpSound(ob, w); return;
    case "dash": playDashSound(ob, w); return;
    case "flinch": playMonsterFlinchSound(ob, w); return;
    case "struck": playMonsterStruckSound(ob, w); return;
    case "attack":
      playMonsterAttackSound(ob, w);
      if ((ob.image & 0xffff) >= 10000) playPetSound(ob, w); // MonsterObject.SetAction Attack1: PlayPetSound for intelligent creatures
      return;
    case "attack2": playSecondAttackSound(ob, w); return;
    case "attack3": playThirdAttackSound(ob, w); return;
    case "attack4": playFourthAttackSound(ob, w); return;
    case "attack5": playFithAttackSound(ob, w); return;
    case "swing": playSwingSound(ob, w); return;
    case "die": playMonsterDieSound(ob, w); return;
    case "dead": playDeadSound(ob, w); return;
    case "revive": playReviveSound(ob, w); return;
    case "range": playRangeSound(ob, w); return;
    case "range2": playSecondRangeSound(ob, w); return;
    case "range3": playThirdRangeSound(ob, w); return;
    case "pickup": playPickupSound(ob, w); return;
    case "pet": playPetSound(ob, w); return;
  }
}

// MonsterObject.PlaySummonSound
function playSummonSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.HellKnight1:
    case M.HellKnight2:
    case M.HellKnight3:
    case M.HellKnight4:
    case M.LightningBead:
    case M.HealingBead:
    case M.PowerUpBead:
      w.play(BaseSound + 0);
      return;
    case M.BoneFamiliar:
    case M.Shinsu:
    case M.HolyDeva:
      w.play(BaseSound + 5);
      return;
  }
}

// MonsterObject.PlayWalkSound(bool left = true)
function playWalkSound(ob: MapObj, w: SoundWorld, left = true): void {
  const BaseSound = baseSound(ob);
  if (left) {
    switch (ob.image) {
      case M.WingedTigerLord:
      case M.PoisonHugger:
      case M.SnowWolfKing:
      case M.Catapult:
      case M.ChariotBallista:
        w.play(BaseSound + 8);
        return;
    }
  } else {
    switch (ob.image) {
      case M.WingedTigerLord:
      case M.AvengerPlant:
        w.play(BaseSound + 8);
        return;
      case M.PoisonHugger:
      case M.SnowWolfKing:
        w.play(BaseSound + 9);
        return;
    }
  }
}

// MonsterObject.PlayAppearSound
function playAppearSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.CannibalPlant:
    case M.WaterDragon:
    case M.EvilCentipede:
    case M.CreeperPlant:
      return;
    case M.ZumaArcher:
    case M.ZumaStatue:
    case M.ZumaGuardian:
    case M.RedThunderZuma:
    case M.FrozenRedZuma:
    case M.FrozenZumaStatue:
    case M.FrozenZumaGuardian:
    case M.ZumaTaurus:
    case M.DemonGuard:
    case M.Turtlegrass:
    case M.ManTree:
    case M.EarthGolem:
    case M.AssassinScroll:
    case M.WarriorScroll:
    case M.TaoistScroll:
    case M.WizardScroll:
    case M.PurpleFaeFlower:
      if ((ob as any).stoned ?? false) return; // TODO MapObj: stoned (MonsterObject.Stoned, default false)
      break;
    case M.DragonStatue:
      w.play(BaseSound + 6);
      return;
  }

  w.play(BaseSound);
}

// MonsterObject.PlayPopupSound
function playPopupSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.ZumaTaurus:
    case M.DigOutZombie:
    case M.Armadillo:
    case M.ArmadilloElder:
      w.play(BaseSound + 5);
      return;
    case M.Shinsu:
      w.play(BaseSound + 6);
      return;
  }
  w.play(BaseSound);
}

// MonsterObject.PlayRunSound
function playRunSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.HardenRhino:
      w.play(BaseSound + 8);
      break;
  }
}

// MonsterObject.PlayJumpSound
function playJumpSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.Armadillo:
    case M.ArmadilloElder:
    case M.ChieftainArcher:
      w.play(BaseSound + 8);
      break;
  }
}

// MonsterObject.PlayDashSound
function playDashSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.HornedSorceror:
      w.play(BaseSound + 9);
      break;
  }
}

// MonsterObject.PlayFlinchSound
function playMonsterFlinchSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    default:
      w.play(BaseSound + 2);
      break;
  }
}

// MonsterObject.PlayStruckSound
function playMonsterStruckSound(ob: MapObj, w: SoundWorld): void {
  switch (ob.image) {
    case M.EvilMir:
      w.play(SL.StruckEvilMir);
      return;
  }

  switch (struckWeapon(ob)) {
    case 0:
    case 23:
    case 28:
    case 40:
      w.play(SL.StruckWooden);
      break;
    case 1:
    case 12:
      w.play(SL.StruckShort);
      break;
    case 2:
    case 8:
    case 11:
    case 15:
    case 18:
    case 20:
    case 25:
    case 31:
    case 33:
    case 34:
    case 37:
    case 41:
      w.play(SL.StruckSword);
      break;
    case 3:
    case 5:
    case 7:
    case 9:
    case 13:
    case 19:
    case 24:
    case 26:
    case 29:
    case 32:
    case 35:
      w.play(SL.StruckSword2);
      break;
    case 4:
    case 14:
    case 16:
    case 38:
      w.play(SL.StruckAxe);
      break;
    case 6:
    case 10:
    case 17:
    case 22:
    case 27:
    case 30:
    case 36:
    case 39:
      w.play(SL.StruckShort);
      break;
    case 21:
      w.play(SL.StruckClub);
      break;
  }
}

// MonsterObject.PlayAttackSound
function playMonsterAttackSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    default:
      w.play(BaseSound + 1);
      break;
  }
}

// MonsterObject.PlaySecondAttackSound
function playSecondAttackSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    default:
      w.play(BaseSound + 6);
      break;
  }
}

// MonsterObject.PlayThirdAttackSound
function playThirdAttackSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.DarkCaptain:
    case M.HornedSorceror:
    case M.HornedCommander:
      return;
    default:
      w.play(BaseSound + 7);
      return;
  }
}

// MonsterObject.PlayFourthAttackSound
function playFourthAttackSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.HornedCommander:
      return;
    case M.SnowWolfKing:
      w.play(BaseSound + 5);
      return;
    default:
      w.play(BaseSound + 8);
      return;
  }
}

// MonsterObject.PlayFithAttackSound
function playFithAttackSound(ob: MapObj, w: SoundWorld): void {
  w.play(baseSound(ob) + 9);
}

// MonsterObject.PlaySwingSound
function playSwingSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.DarkCaptain:
    case M.EvilMir:
    case M.DragonStatue:
      return;
    default:
      w.play(BaseSound + 4);
      return;
  }
}

// MonsterObject.PlayDieSound
function playMonsterDieSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    default:
      w.play(BaseSound + 3);
      return;
  }
}

// MonsterObject.PlayDeadSound
function playDeadSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.CaveBat:
    case M.HellKnight1:
    case M.HellKnight2:
    case M.HellKnight3:
    case M.HellKnight4:
    case M.CyanoGhast:
    case M.WoodBox:
      w.play(BaseSound + 5);
      return;
  }
}

// MonsterObject.PlayReviveSound
function playReviveSound(ob: MapObj, w: SoundWorld): void {
  switch (ob.image) {
    case M.ClZombie:
    case M.NdZombie:
    case M.CrawlerZombie:
      w.play(SL.ZombieRevive);
      return;
  }
}

// MonsterObject.PlayRangeSound
function playRangeSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.RedThunderZuma:
    case M.FrozenRedZuma:
    case M.KingScorpion:
    case M.DarkDevil:
    case M.Khazard:
    case M.BoneLord:
    case M.LeftGuard:
    case M.RightGuard:
    case M.FrostTiger:
    case M.GreatFoxSpirit:
    case M.BoneSpearman:
    case M.MinotaurKing:
    case M.WingedTigerLord:
    case M.ManectricClaw:
    case M.ManectricKing:
    case M.HellBolt:
    case M.WitchDoctor:
    case M.FlameSpear:
    case M.FlameMage:
    case M.FlameScythe:
    case M.FlameAssassin:
    case M.FlameQueen:
    case M.DarkDevourer:
    case M.DreamDevourer:
    case M.FlyingStatue:
    case M.IceGuard:
    case M.ElementGuard:
    case M.KingGuard:
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
    case M.BurningZombie:
    case M.MudZombie:
    case M.FrozenZombie:
    case M.UndeadWolf:
    case M.CatShaman:
    case M.CannibalTentacles:
    case M.SwampWarrior:
    case M.GeneralMeowMeow:
    case M.RhinoPriest:
    case M.HardenRhino:
    case M.TreeGuardian:
    case M.OmaCannibal:
    case M.OmaMage:
    case M.OmaWitchDoctor:
    case M.CreeperPlant:
    case M.AvengingSpirit:
    case M.AvengingWarrior:
    case M.PeacockSpider:
    case M.FlamingMutant:
    case M.KingHydrax:
    case M.DarkCaptain:
    case M.DarkOmaKing:
    case M.HornedMage:
    case M.FrozenKnight:
    case M.IcePhantom:
    case M.WaterDragon:
    case M.BlackTortoise:
    case M.EvilMir:
    case M.DragonStatue:
      w.play(BaseSound + 5);
      return;
    case M.AncientBringer:
    case M.SeedingsGeneral:
      w.play(BaseSound + 7);
      return;
    case M.RestlessJar:
      w.play(BaseSound + 8);
      return;
    case M.TucsonGeneral:
      return;
    default:
      playMonsterAttackSound(ob, w);
      return;
  }
}

// MonsterObject.PlaySecondRangeSound
function playSecondRangeSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.TucsonGeneral:
      w.play(BaseSound + 5);
      return;
    case M.TurtleKing:
      return;
    case M.KingGuard:
    case M.TreeGuardian:
    case M.DarkCaptain:
    case M.HornedCommander:
      w.play(BaseSound + 7);
      return;
    case M.AncientBringer:
    case M.SeedingsGeneral:
      w.play(BaseSound + 8);
      return;
    default:
      playSecondAttackSound(ob, w);
      return;
  }
}

// MonsterObject.PlayThirdRangeSound
function playThirdRangeSound(ob: MapObj, w: SoundWorld): void {
  const BaseSound = baseSound(ob);
  switch (ob.image) {
    case M.TucsonGeneral:
      w.play(BaseSound + 7);
      return;
    default:
      playThirdAttackSound(ob, w);
      return;
  }
}

/** GameScene.IntelligentCreaturePickup (S.IntelligentCreaturePickup { ObjectID }): MonsterObject.PlayPickupSound, only
 *  when the object is a monster (`ob` = the map object with that id, or undefined). */
export function intelligentCreaturePickupSound(ob: MapObj | undefined, w: SoundWorld): void {
  if (ob && ob.race === ObjectType.Monster) playPickupSound(ob, petWorld(ob, w));
}

// MonsterObject.PlayPickupSound
function playPickupSound(_ob: MapObj, w: SoundWorld): void {
  w.play(SL.PetPickup);
}

// MonsterObject.PlayPetSound
function playPetSound(ob: MapObj, w: SoundWorld): void {
  const petSound = (ob.image & 0xffff) - 10000 + 10500;

  switch (ob.image) {
    case M.Chick:
    case M.BabyPig:
    case M.Kitten:
    case M.BabySkeleton:
    case M.Baekdon:
    case M.Wimaen:
    case M.BlackKitten:
    case M.BabyDragon:
    case M.OlympicFlame:
    case M.BabySnowMan:
    case M.Frog:
    case M.BabyMonkey:
    case M.AngryBird:
    case M.Foxey:
    case M.MedicalRat:
      w.play(petSound);
      break;
  }
}

// ---------------------------------------------------------------- PlayerObject

/** PlayerObject.PlayStepSound / PlayStruckSound / PlayFlinchSound / PlayAttackSound / PlayDieSound (dispatch by kind). */
export function playerSound(ob: MapObj, kind: PlayerSound, w: SoundWorld): void {
  switch (kind) {
    case "step": playStepSound(ob, w); return;
    case "struck": playPlayerStruckSound(ob, w); return;
    case "flinch": playPlayerFlinchSound(ob, w); return;
    case "attack": playPlayerAttackSound(ob, w); return;
    case "die": playPlayerDieSound(ob, w); return;
    case "mount": playMountSound(ob, w); return;
  }
}

// PlayerObject.PlayMountSound (after S.MountUpdate): mount up / down per mount family
function playMountSound(ob: MapObj, w: SoundWorld): void {
  const MountType = mountType(ob);
  if (ridingMount(ob)) {
    if (MountType < 7)
      w.play(10218);
    else if (MountType < 12)
      w.play(10188);
  } else {
    if (MountType < 7)
      w.play(10219);
    else if (MountType < 12)
      w.play(10189);
  }
}

/** The map / owner effects PlayerObject.ProcessFrames adds while fishing (Libraries.Effect / Prguse, Light = 0). */
export interface FishingEffects {
  /** MapControl.Effects.Add(new Effect(Libraries.Effect, baseIndex, 6, 720, FishingPoint) { Light = 0 }). */
  mapEffect(baseIndex: number, at: { X: number; Y: number }): void;
  /** Effects.Add(new Effect(Libraries.Prguse, 1350, 2, 720, this) { Light = 0, Blend = false }) on the player. */
  ownerEffect(ob: MapObj): void;
}

/** The desktop's fishing effects on the effect world (game.ts has set it before any frame runs). */
export const fishingEffects: FishingEffects = {
  mapEffect(baseIndex, at) {
    const e = new Effect(Lib.Effect, baseIndex, 6, 720, { X: at.X, Y: at.Y });
    e.light = 0;
    effectWorld().mapEffects.push(e);
  },
  ownerEffect(ob) {
    const e = new Effect(Lib.Prguse, 1350, 2, 720, ob);
    e.light = 0;
    e.blend = false;
    ob.effects.push(e);
  },
};

/** PlayerObject.ProcessFrames, case FishingCast / FishingReel / FishingWait at FrameIndex 1: the throw / pull sounds,
 *  and each FishingWait loop the ripple at FishingPoint (a bite: 671 + 665, the Fishing sound and the Prguse 1350 mark
 *  over the player; else 650 + 640). Returns FoundFish for FishingWait (the FishingStatusDialog fish button), else null.
 *  Call from the per-frame hook when ob.frameIndex === 1. */
export function fishingFrame(ob: MapObj, w: SoundWorld, fx: FishingEffects = fishingEffects): boolean | null {
  switch (ob.action) {
    case MirAction.FishingCast:
      w.play(SL.FishingThrow);
      return null;
    case MirAction.FishingReel:
      w.play(SL.FishingPull);
      return null;
    case MirAction.FishingWait:
      if (ob.foundFish) {
        fx.mapEffect(671, ob.fishingPoint);
        fx.mapEffect(665, ob.fishingPoint);
        w.play(SL.Fishing);
        fx.ownerEffect(ob);
      } else {
        fx.mapEffect(650, ob.fishingPoint);
        fx.mapEffect(640, ob.fishingPoint);
      }
      return ob.foundFish;
  }
  return null;
}

const ridingMount = (ob: MapObj): boolean => ob.ridingMount; // PlayerObject.RidingMount
const mountType = (ob: MapObj): number => ob.mountType;     // PlayerObject.MountType

// PlayerObject.HasClassWeapon
function hasClassWeapon(ob: MapObj): boolean {
  const Class = ob.look.class;
  switch (Math.trunc(ob.look.weapon / CLASS_WEAPON_COUNT)) {
    default:
      return Class == MirClass.Wizard || Class == MirClass.Warrior || Class == MirClass.Taoist;
    case 1:
      return Class == MirClass.Assassin;
    case 2:
      return Class == MirClass.Archer;
  }
}

// PlayerObject.PlayStepSound
function playStepSound(ob: MapObj, w: SoundWorld): void {
  const m = w.map();
  const x = ob.location.X - ob.location.X % 2;
  const y = ob.location.Y - ob.location.Y % 2;
  const i = x * m.height + y;
  if (m.frontIndex[i] > 199) return; //prevents any move sounds on non mir2 maps atm
  if (m.middleIndex[i] > 199) return; //prevents any move sounds on non mir2 maps atm
  if (m.backIndex[i] > 199) return; //prevents any move sounds on non mir2 maps atm

  let moveSound: number;

  if (m.backIndex[i] > 99 && m.backIndex[i] < 199) { //shanda tiles
    moveSound = playShandaStepSound(m, i);
  } else if (m.backIndex[i] > 199 && m.backIndex[i] < 299) { //mir3 tiles
    moveSound = playWemadeMir3StepSound(m, i);
  } else { //wemade tiles
    moveSound = playWemadeStepSound(m, i);
  }

  if (ridingMount(ob)) moveSound = SL.MountWalkL;

  if (ob.action == MirAction.Running) moveSound += 2;
  if (ob.frameIndex == 4) moveSound++;

  w.play(moveSound);
}

// PlayerObject.PlayStruckSound
function playPlayerStruckSound(ob: MapObj, w: SoundWorld): void {
  if (ridingMount(ob)) {
    const MountType = mountType(ob);
    if (MountType < 7)
      w.play(randomNext(10179, 10181));
    else if (MountType < 12)
      w.play(randomNext(10193, 10194));

    return;
  }

  let add = 0;
  if (ob.look.class != MirClass.Assassin) //Archer to add?
    switch (ob.look.armour) {
      case 3:
      case 6:
      case 9:
        add = 10;
        break;
    }

  switch (struckWeapon(ob)) {
    case 0:
    case 23:
    case 1:
    case 12:
    case 28:
    case 40:
      w.play(SL.StruckBodySword + add);
      break;
    case 2:
    case 8:
    case 11:
    case 15:
    case 18:
    case 20:
    case 25:
    case 31:
    case 33:
    case 34:
    case 37:
    case 41:
      w.play(SL.StruckBodySword + add);
      break;
    case 3:
    case 5:
    case 7:
    case 9:
    case 13:
    case 19:
    case 24:
    case 26:
    case 29:
    case 32:
    case 35:
      w.play(SL.StruckBodySword + add);
      break;
    case 4:
    case 14:
    case 16:
    case 38:
      w.play(SL.StruckBodyAxe + add);
      break;
    case 6:
    case 10:
    case 17:
    case 22:
    case 27:
    case 30:
    case 36:
    case 39:
      w.play(SL.StruckBodyLongStick + add);
      break;
    case 21:
      w.play(SL.StruckBodyLongStick + add);
      break;
    case -1:
      w.play(SL.StruckBodyFist + add);
      break;
  }
}

// PlayerObject.PlayFlinchSound (FlinchSound = Gender == MirGender.Male ? SoundList.MaleFlinch : SoundList.FemaleFlinch, from SetLibraries)
function playPlayerFlinchSound(ob: MapObj, w: SoundWorld): void {
  const FlinchSound = ob.look.gender == MirGender.Male ? SL.MaleFlinch : SL.FemaleFlinch;
  w.play(FlinchSound);
}

// PlayerObject.PlayAttackSound
function playPlayerAttackSound(ob: MapObj, w: SoundWorld): void {
  if (ridingMount(ob)) {
    const MountType = mountType(ob);
    if (MountType < 7)
      w.play(randomNext(10181, 10184));
    else if (MountType < 12)
      w.play(randomNext(10190, 10193));

    return;
  }

  const Weapon = ob.look.weapon, Class = ob.look.class;
  if (Weapon >= 0 && Class == MirClass.Assassin) {
    w.play(SL.SwingShort);
    return;
  }

  if (Class == MirClass.Archer && hasClassWeapon(ob)) {
    return;
  }

  switch (Weapon) {
    case 0:
    case 23:
    case 28:
    case 40:
      w.play(SL.SwingWood);
      break;
    case 1:
    case 12:
      w.play(SL.SwingShort);
      break;
    case 2:
    case 8:
    case 11:
    case 15:
    case 18:
    case 20:
    case 25:
    case 31:
    case 33:
    case 34:
    case 37:
    case 41:
      w.play(SL.SwingSword);
      break;
    case 3:
    case 5:
    case 7:
    case 9:
    case 13:
    case 19:
    case 24:
    case 26:
    case 29:
    case 32:
    case 35:
      w.play(SL.SwingSword2);
      break;
    case 4:
    case 14:
    case 16:
    case 38:
      w.play(SL.SwingAxe);
      break;
    case 6:
    case 10:
    case 17:
    case 22:
    case 27:
    case 30:
    case 36:
    case 39:
      w.play(SL.SwingLong);
      break;
    case 21:
      w.play(SL.SwingClub);
      break;
    default:
      w.play(SL.SwingFist);
      break;
  }
}

// PlayerObject.PlayDieSound
function playPlayerDieSound(ob: MapObj, w: SoundWorld): void {
  if (ob.look.gender == 0) { w.play(SL.MaleDie); }
  else { w.play(SL.FemaleDie); }
}

// ---------------------------------------------------------------- PlayerObject step tile tables (mechanical port; m = M2CellInfo, i = x * map.height + y)
// PlayerObject.PlayWemadeStepSound / PlayShandaStepSound / PlayWemadeMir3StepSound: the C# `out int moveSound` is the return value.

function playWemadeStepSound(m: MapCells, i: number): number
{
  let moveSound: number;
  let index = (m.backImage[i] & 0x1FFFF) - 1;
  //index = (m.frontIndex[i] - 2) * 10000 + index;

  if (index >= 0 && index <= 10000)
  {
    if ((index >= 330 && index <= 349) || (index >= 450 && index <= 454) || (index >= 550 && index <= 554) ||
      (index >= 750 && index <= 754) || (index >= 950 && index <= 954) || (index >= 1250 && index <= 1254) ||
      (index >= 1400 && index <= 1424) || (index >= 1455 && index <= 1474) || (index >= 1500 && index <= 1524) ||
      (index >= 1550 && index <= 1574))
      moveSound = SL.WalkLawnL;
    else if ((index >= 250 && index <= 254) || (index >= 1005 && index <= 1009) || (index >= 1050 && index <= 1054) ||
      (index >= 1060 && index <= 1064) || (index >= 1450 && index <= 1454) || (index >= 1650 && index <= 1654))
      moveSound = SL.WalkRoughL;
    else if ((index >= 605 && index <= 609) || (index >= 650 && index <= 654) || (index >= 660 && index <= 664) ||
      (index >= 2000 && index <= 2049) || (index >= 3025 && index <= 3049) || (index >= 2400 && index <= 2424) ||
      (index >= 4625 && index <= 4649) || (index >= 4675 && index <= 4678))
      moveSound = SL.WalkStoneL;
    else if ((index >= 1825 && index <= 1924) || (index >= 2150 && index <= 2174) || (index >= 3075 && index <= 3099) ||
      (index >= 3325 && index <= 3349) || (index >= 3375 && index <= 3399))
      moveSound = SL.WalkCaveL;
    else if (index == 3230 || index == 3231 || index == 3246 || index == 3277 || (index >= 3780 && index <= 3799))
      moveSound = SL.WalkWoodL;
    else if (index >= 3825 && index <= 4434)
      switch (index % 25)
      {
        case 0:
          moveSound = SL.WalkWoodL;
          break;
        default:
          moveSound = SL.WalkGroundL;
          break;
      }
    else if ((index >= 2075 && index <= 2099) || (index >= 2125 && index <= 2149))
      moveSound = SL.WalkRoomL;
    else if (index >= 1800 && index <= 1824)
      moveSound = SL.WalkWaterL;
    else moveSound = SL.WalkGroundL;

    if ((index >= 825 && index <= 1349) && Math.trunc((index - 825) / 25) % 2 == 0) moveSound = SL.WalkStoneL;
    if ((index >= 1375 && index <= 1799) && Math.trunc((index - 1375) / 25) % 2 == 0) moveSound = SL.WalkCaveL;
    if (index == 1385 || index == 1386 || index == 1391 || index == 1392) moveSound = SL.WalkWoodL;

    index = (m.middleImage[i] & 0x7FFF) - 1;
    if (index >= 0 && index <= 115)
      moveSound = SL.WalkGroundL;
    else if (index >= 120 && index <= 124)
      moveSound = SL.WalkLawnL;

    index = (m.frontImage[i] & 0x7FFF) - 1;
    if ((index >= 221 && index <= 289) || (index >= 583 && index <= 658) || (index >= 1183 && index <= 1206) ||
      (index >= 7163 && index <= 7295) || (index >= 7404 && index <= 7414))
      moveSound = SL.WalkStoneL;
    else if ((index >= 3125 && index <= 3267) || (index >= 3757 && index <= 3948) || (index >= 6030 && index <= 6999))
      moveSound = SL.WalkWoodL;
    if (index >= 3316 && index <= 3589)
      moveSound = SL.WalkRoomL;
  }
  else
    moveSound = SL.WalkGroundL;
  return moveSound;
}

function playShandaStepSound(m: MapCells, i: number): number
{
  let moveSound: number;
  let index = (m.backImage[i] & 0x1FFFF) - 1;
  //index = (m.backIndex[i] - 100) * 100000 + index;


  //CMain.SendDebugMessage(string.Format("BackImage : {0}. BackIndex : {1}. MiddleImage : {2}. MiddleIndex {3}", tt.BackImage, tt.BackIndex, tt.MiddleImage, tt.MiddleIndex));

  // #region Back Tiles (Tiles)
  switch (m.backIndex[i])
  {
    case 100:
      {
        //Tiles1
        if ((index >= 0 && index <= 39) || (index >= 50 && index <= 74) || (index >= 80 && index <= 84) || (index >= 100 && index <= 204) ||
          (index >= 210 && index <= 249) || (index >= 255 && index <= 259) || (index >= 265 && index <= 329) || (index >= 350 && index <= 404) ||
          (index >= 410 && index <= 449) || (index >= 455 && index <= 459) || (index >= 465 && index <= 504) ||  (index >= 510 && index <= 549) ||
          (index >= 555 && index <= 559) || (index >= 565 && index <= 574) || (index >= 581 && index <= 581) || (index >= 586 && index <= 587) ||
          (index >= 600 && index <= 604) || (index >= 610 && index <= 614) || (index >= 615 && index <= 649) || (index >= 655 && index <= 659) ||
          (index >= 665 && index <= 704) || (index >= 710 && index <= 749) || (index >= 755 && index <= 759) || (index >= 765 && index <= 824) ||
          (index >= 850 && index <= 874) || (index >= 900 && index <= 924) || (index >= 950 && index <= 974) || (index >= 1100 && index <= 1124) ||
          (index >= 1150 && index <= 1174) || (index >= 1200 && index <= 1204) || (index >= 1210 && index <= 1214) || (index >= 1255 && index <= 1259) ||
          (index >= 1300 && index <= 1324) || (index >= 1340 && index <= 1374) || (index >= 1560 && index <= 1604) || (index >= 1610 && index <= 1614) ||
          (index >= 1655 && index <= 1659) || (index >= 1665 && index <= 1674) || (index >= 1700 && index <= 1724) || (index >= 1750 && index <= 1774) ||
          (index >= 1850 && index <= 1874) || (index >= 2050 && index <= 2074) || (index >= 2100 && index <= 2124) || (index >= 2150 && index <= 2174) ||
          (index >= 2200 && index <= 2224) ||(index >= 2250 && index <= 2274) ||(index >= 2300 && index <= 2324) ||(index >= 2350 && index <= 2374) ||
          (index >= 2500 && index <= 2524) ||(index >= 2750 && index <= 2774) ||(index >= 3250 && index <= 3274) ||(index >= 3300 && index <= 3349) ||
          (index >= 3375 && index <= 3404) ||(index >= 3425 && index <= 3449) ||(index >= 3475 && index <= 3499) ||(index >= 3525 && index <= 3549) ||
          (index >= 3575 && index <= 3599) ||(index >= 3625 && index <= 3649) ||(index >= 3675 && index <= 3749) ||(index >= 3775 && index <= 3780) ||
          (index >= 4475 && index <= 4624) ||(index >= 4680 && index <= 4824) ||(index >= 6225 && index <= 7924) ||(index >= 11079 && index <= 11079) ||
          (index >= 11083 && index <= 11084) ||(index >= 11120 && index <= 11127) ||(index >= 11129 && index <= 11130) ||(index >= 11132 && index <= 11133) ||
          (index >= 11135 && index <= 11136) ||(index >= 13770 && index <= 14019) ||(index >= 14170 && index <= 14219) ||(index >= 14820 && index <= 14869) ||
          (index >= 20470 && index <= 21619) ||(index >= 25000 && index <= 31724))
          moveSound = SL.WalkGroundL;
        else if ((index >= 205 && index <= 209) ||(index >= 260 && index <= 264) ||(index >= 330 && index <= 349) ||(index >= 405 && index <= 409) ||
          (index >= 460 && index <= 464) ||(index >= 505 && index <= 509) ||(index >= 560 && index <= 564) ||(index >= 575 && index <= 580) ||
          (index >= 582 && index <= 585) ||(index >= 588 && index <= 599) ||(index >= 705 && index <= 709) ||(index >= 750 && index <= 754) ||
          (index >= 760 && index <= 764) ||(index >= 1205 && index <= 1209) ||(index >= 1215 && index <= 1224) ||(index >= 1250 && index <= 1254) ||
          (index >= 1260 && index <= 1274) ||(index >= 1400 && index <= 1424) ||(index >= 1455 && index <= 1459) ||(index >= 1500 && index <= 1524) ||
          (index >= 1550 && index <= 1574) ||(index >= 2555 && index <= 2559) ||(index >= 2605 && index <= 2609) ||(index >= 2655 && index <= 2659) ||
          (index >= 2705 && index <= 2709) ||(index >= 10320 && index <= 10324) ||(index >= 10329 && index <= 10329) ||(index >= 10334 && index <= 10334) ||
          (index >= 10372 && index <= 10372) ||(index >= 10381 && index <= 10381) ||(index >= 10420 && index <= 10422) ||(index >= 10425 && index <= 10424) ||
          (index >= 10429 && index <= 10429) ||(index >= 10470 && index <= 10470) ||(index >= 10472 && index <= 10472) ||(index >= 10475 && index <= 10475) ||
          (index >= 10479 && index <= 10519) ||(index >= 10530 && index <= 10530) ||(index >= 10535 && index <= 10535) ||(index >= 10539 && index <= 10539) ||
          (index >= 10623 && index <= 10624) ||(index >= 10628 && index <= 10629) ||(index >= 10634 && index <= 10634) ||(index >= 10639 && index <= 10639) ||
          (index >= 10644 && index <= 10669) ||(index >= 10673 && index <= 10677) ||(index >= 10680 && index <= 10681) ||(index >= 10684 && index <= 10685) ||
          (index >= 10688 && index <= 10690) ||(index >= 10721 && index <= 10723) ||(index >= 10725 && index <= 10727) ||(index >= 10729 && index <= 10731) ||
          (index >= 10733 && index <= 10770) ||(index >= 10772 && index <= 10772) ||(index >= 10775 && index <= 10775) ||(index >= 10779 && index <= 10779) ||
          (index >= 10784 && index <= 10784) ||(index >= 10835 && index <= 10836) ||(index >= 10840 && index <= 10869) ||(index >= 10874 && index <= 10874) ||
          (index >= 10890 && index <= 10890) ||(index >= 10920 && index <= 10929) ||(index >= 10931 && index <= 10934) ||(index >= 10936 && index <= 10939) ||
          (index >= 10941 && index <= 10970) ||(index >= 10974 && index <= 10974) ||(index >= 10977 && index <= 11019) ||(index >= 11021 && index <= 11021) ||
          (index >= 11023 && index <= 11027) ||(index >= 11029 && index <= 11033) ||(index >= 11037 && index <= 11038) ||(index >= 11070 && index <= 11071) ||
          (index >= 11075 && index <= 11078) ||(index >= 11080 && index <= 11082) ||(index >= 11086 && index <= 11089) ||(index >= 11091 && index <= 11119) ||
          (index >= 11128 && index <= 11128) ||(index >= 11131 && index <= 11131) ||(index >= 11134 && index <= 11134) ||(index >= 11137 && index <= 11171) ||
          (index >= 11175 && index <= 11177) ||(index >= 11180 && index <= 11182) ||(index >= 11185 && index <= 11219) ||(index >= 11223 && index <= 11224) ||
          (index >= 11227 && index <= 11229) ||(index >= 11232 && index <= 11291) ||(index >= 11296 && index <= 11299) ||(index >= 11305 && index <= 11308) ||
          (index >= 11314 && index <= 11317) ||(index >= 11322 && index <= 11327) ||(index >= 11332 && index <= 11369))
          moveSound = SL.WalkLawnL;
        else if ((index >= 250 && index <= 254) ||(index >= 450 && index <= 454) ||(index >= 550 && index <= 554) ||(index >= 1000 && index <= 1024) ||
          (index >= 1050 && index <= 1074) ||(index >= 1450 && index <= 1454) ||(index >= 1460 && index <= 1464) ||(index >= 1465 && index <= 1474) ||
          (index >= 1605 && index <= 1609) ||(index >= 1615 && index <= 1624) ||(index >= 1650 && index <= 1654) ||(index >= 1660 && index <= 1664))
          moveSound = SL.WalkRoughL;
        else if ((index >= 2800 && index <= 2824) ||(index >= 3130 && index <= 3130) ||(index >= 3137 && index <= 3138) ||(index >= 3177 && index <= 3178) ||
          (index >= 3189 && index <= 3190) ||(index >= 3230 && index <= 3231) ||(index >= 3240 && index <= 3242) ||(index >= 3246 && index <= 3246) ||
          (index >= 3276 && index <= 3278) ||(index >= 3280 && index <= 3299) ||(index >= 5178 && index <= 5179) ||(index >= 5182 && index <= 5185) ||
          (index >= 5188 && index <= 5191) ||(index >= 5194 && index <= 5195) ||(index >= 5780 && index <= 6060) ||(index >= 6080 && index <= 6084) ||
          (index >= 6145 && index <= 6224) ||(index >= 14020 && index <= 14169) ||(index >= 14220 && index <= 14819))
          moveSound = SL.WalkWoodL;
        else if ((index >= 825 && index <= 849) ||(index >= 875 && index <= 899) ||(index >= 925 && index <= 949) ||(index >= 975 && index <= 999) ||
          (index >= 1025 && index <= 1049) ||(index >= 1075 && index <= 1099) ||(index >= 1125 && index <= 1149) ||(index >= 1175 && index <= 1199) ||
          (index >= 1225 && index <= 1249) ||(index >= 1275 && index <= 1299) ||(index >= 1325 && index <= 1339) ||(index >= 2025 && index <= 2049) ||
          (index >= 2075 && index <= 2099) ||(index >= 2125 && index <= 2149) ||(index >= 2900 && index <= 2924) ||(index >= 3781 && index <= 4474) ||
          (index >= 6085 && index <= 6144))
          moveSound = SL.WalkWoodL;
        else if ((index >= 40 && index <= 49) ||(index >= 75 && index <= 79) ||(index >= 85 && index <= 99) ||(index >= 1375 && index <= 1399) ||
          (index >= 1425 && index <= 1449) ||(index >= 1475 && index <= 1499) ||(index >= 1525 && index <= 1549) ||(index >= 1575 && index <= 1559) ||
          (index >= 1625 && index <= 1649) ||(index >= 1675 && index <= 1699) ||(index >= 1725 && index <= 1749) ||(index >= 1775 && index <= 1799) ||
          (index >= 1825 && index <= 1849) ||(index >= 1875 && index <= 2024) ||(index >= 2175 && index <= 2199) ||(index >= 2225 && index <= 2249) ||
          (index >= 2275 && index <= 2299) ||(index >= 2325 && index <= 2349) ||(index >= 2375 && index <= 2499) ||(index >= 2525 && index <= 2549) ||
          (index >= 2575 && index <= 2599) ||(index >= 2625 && index <= 2649) ||(index >= 2675 && index <= 2699) ||(index >= 2725 && index <= 2749) ||
          (index >= 2775 && index <= 2799) ||(index >= 2825 && index <= 2849) ||(index >= 2875 && index <= 2899) ||(index >= 2925 && index <= 2994) ||
          (index >= 2995 && index <= 2999) ||(index >= 3025 && index <= 3129) ||(index >= 3131 && index <= 3136) ||(index >= 3139 && index <= 3176) ||
          (index >= 3179 && index <= 3188) ||(index >= 3191 && index <= 3229) ||(index >= 3232 && index <= 3239) ||(index >= 3243 && index <= 3245) ||
          (index >= 3247 && index <= 3249) ||(index >= 3275 && index <= 3275) ||(index >= 3279 && index <= 3279) ||(index >= 3350 && index <= 3374) ||
          (index >= 3450 && index <= 3474) ||(index >= 3500 && index <= 3524) ||(index >= 3550 && index <= 3574) ||(index >= 3600 && index <= 3624) ||
          (index >= 3650 && index <= 3674) ||(index >= 3750 && index <= 3774) ||(index >= 4825 && index <= 5177) ||(index >= 5180 && index <= 5181) ||
          (index >= 5186 && index <= 5187) ||(index >= 5192 && index <= 5193) ||(index >= 5196 && index <= 5779) ||(index >= 7925 && index <= 10305) ||
          (index >= 11370 && index <= 13769) ||(index >= 14870 && index <= 20469) ||(index >= 21620 && index <= 24999))
          moveSound = SL.WalkCaveL;
        else if ((index >= 605 && index <= 609) ||(index >= 650 && index <= 654) ||(index >= 660 && index <= 664) ||(index >= 2550 && index <= 2554) ||
          (index >= 2560 && index <= 2564) ||(index >= 2565 && index <= 2574) ||(index >= 2600 && index <= 2604) ||(index >= 2610 && index <= 2624) ||
          (index >= 2650 && index <= 2654) ||(index >= 2660 && index <= 2674) ||(index >= 2700 && index <= 2704) ||(index >= 2710 && index <= 2724) ||
          (index >= 2850 && index <= 2874) ||(index >= 3405 && index <= 3424) ||(index >= 4625 && index <= 4679) ||(index >= 6075 && index <= 6079) ||
          (index >= 10325 && index <= 10328) ||(index >= 10330 && index <= 10333) ||(index >= 10335 && index <= 10371) ||(index >= 10373 && index <= 10380) ||
          (index >= 10382 && index <= 10419) ||(index >= 10423 && index <= 10424) ||(index >= 10426 && index <= 10428) ||(index >= 10430 && index <= 10469) ||
          (index >= 10471 && index <= 10471) ||(index >= 10473 && index <= 10474) ||(index >= 10476 && index <= 10478) ||(index >= 10520 && index <= 10529) ||
          (index >= 10531 && index <= 10534) ||(index >= 10536 && index <= 10538) ||(index >= 10540 && index <= 10622) ||(index >= 10625 && index <= 10627) ||
          (index >= 10630 && index <= 10633) ||(index >= 10635 && index <= 10638) ||(index >= 10640 && index <= 10643) ||(index >= 10670 && index <= 10672) ||
          (index >= 10771 && index <= 10771) ||(index >= 10773 && index <= 10774) ||(index >= 10776 && index <= 10778) ||(index >= 10780 && index <= 10783) ||
          (index >= 10785 && index <= 10834) ||(index >= 10837 && index <= 10839) ||(index >= 10870 && index <= 10873) ||(index >= 10875 && index <= 10889) ||
          (index >= 10891 && index <= 10919) ||(index >= 10930 && index <= 10930) ||(index >= 10935 && index <= 10935) ||(index >= 10940 && index <= 10940) ||
          (index >= 10971 && index <= 10973) ||(index >= 10975 && index <= 10976) ||(index >= 11020 && index <= 11020) ||(index >= 11022 && index <= 11022) ||
          (index >= 11028 && index <= 11028) ||(index >= 11072 && index <= 11074))
          moveSound = SL.WalkStoneL;
        else if ((index >= 1800 && index <= 1824) || (index >= 3000 && index <= 3024) ||(index >= 10678 && index <= 10679) ||
          (index >= 10682 && index <= 10683) ||(index >= 10686 && index <= 10687) ||(index >= 10691 && index <= 10720) ||
          (index >= 10724 && index <= 10724) ||(index >= 10728 && index <= 10728) ||(index >= 10732 && index <= 10732) ||
          (index >= 11034 && index <= 11036) ||(index >= 11039 && index <= 11069) ||(index >= 11085 && index <= 11085) ||
          (index >= 11090 && index <= 11090) ||(index >= 11172 && index <= 11174) ||(index >= 11178 && index <= 11179) ||
          (index >= 11183 && index <= 11184) ||(index >= 11220 && index <= 11222) ||(index >= 11225 && index <= 11226) ||
          (index >= 11230 && index <= 11231) ||(index >= 11292 && index <= 11295) ||(index >= 11300 && index <= 11304) ||
          (index >= 11309 && index <= 11313) ||(index >= 11318 && index <= 11321) ||(index >= 11328 && index <= 11331))
          moveSound = SL.WalkWaterL;
        else
          moveSound = SL.WalkWaterL;
      }
      break;
    case 101:
      {
        //Tiles2
        if ((index >= 0 && index <= 74) || (index >= 79 && index <= 83) || (index >= 94 && index <= 204) || (index >= 209 && index <= 213) ||
          (index >= 209 && index <= 213) || (index >= 224 && index <= 249) || (index >= 255 && index <= 258) || (index >= 274 && index <= 329) ||
          (index >= 350 && index <= 404) || (index >= 424 && index <= 449) || (index >= 455 && index <= 459) || (index >= 474 && index <= 504) ||
          (index >= 509 && index <= 513) || (index >= 524 && index <= 549) || (index >= 555 && index <= 559) || (index >= 565 && index <= 573) ||
          (index >= 594 && index <= 704) || (index >= 709 && index <= 713) || (index >= 724 && index <= 748) || (index >= 755 && index <= 759) ||
          (index >= 774 && index <= 904) || (index >= 909 && index <= 923) || (index >= 974 && index <= 1004) || (index >= 1055 && index <= 1058) ||
          (index >= 1064 && index <= 1204) || (index >= 1209 && index <= 1213) || (index >= 1300 && index <= 1373) || (index >= 1574 && index <= 1604) ||
          (index >= 1609 && index <= 1649) || (index >= 1655 && index <= 1659) || (index >= 1674 && index <= 1704) || (index >= 1709 && index <= 1723) ||
          (index >= 1755 && index <= 1758) || (index >= 1765 && index <= 1799) || (index >= 2050 && index <= 2523) || (index >= 2724 && index <= 2849) ||
          (index >= 2950 && index <= 2999) || (index >= 3150 && index <= 3804) || (index >= 3809 && index <= 3849) || (index >= 3855 && index <= 3858) ||
          (index >= 4475 && index <= 4794) || (index >= 5024 && index <= 5054) || (index >= 5059 && index <= 5299))
          moveSound = SL.WalkGroundL;
        else if ((index >= 205 && index <= 208) || (index >= 214 && index <= 223) || (index >= 264 && index <= 273) || (index >= 330 && index <= 349) ||
          (index >= 405 && index <= 423) || (index >= 460 && index <= 474) || (index >= 505 && index <= 508) || (index >= 514 && index <= 523) ||
          (index >= 560 && index <= 564) || (index >= 714 && index <= 723) || (index >= 764 && index <= 773) || (index >= 905 && index <= 908) ||
          (index >= 955 && index <= 973) || (index >= 1009 && index <= 1023) || (index >= 1205 && index <= 1208) || (index >= 5055 && index <= 5058))
          moveSound = SL.WalkLawnL;
        else if ((index >= 250 && index <= 254) || (index >= 259 && index <= 263) || (index >= 450 && index <= 454) || (index >= 550 && index <= 554) ||
          (index >= 574 && index <= 593) || (index >= 705 && index <= 708) || (index >= 749 && index <= 754) || (index >= 924 && index <= 954) ||
          (index >= 1005 && index <= 1008) || (index >= 1024 && index <= 1054) || (index >= 1059 && index <= 1063) || (index >= 760 && index <= 763) ||
          (index >= 1214 && index <= 1299) || (index >= 1374 && index <= 1573) || (index >= 1605 && index <= 1608) || (index >= 1650 && index <= 1654) ||
          (index >= 1660 && index <= 1673) || (index >= 1705 && index <= 1708) || (index >= 1724 && index <= 1754) || (index >= 1759 && index <= 1764) ||
          (index >= 2555 && index <= 2558) || (index >= 2605 && index <= 2608) || (index >= 2655 && index <= 2658) || (index >= 2705 && index <= 2708))
          moveSound = SL.WalkRoughL;
        else if ((index >= 75 && index <= 78) || (index >= 84 && index <= 93) || (index >= 2524 && index <= 2554) || (index >= 2560 && index <= 2604) ||
          (index >= 2609 && index <= 2654) || (index >= 2659 && index <= 2704) || (index >= 2709 && index <= 2723) || (index >= 2850 && index <= 2949) ||
          (index >= 3805 && index <= 3808) || (index >= 3850 && index <= 3854) || (index >= 3859 && index <= 3873) || (index >= 5300 && index <= 5323) ||
          (index >= 6052 && index <= 6118))
          moveSound = SL.WalkStoneL;
        else if ((index >= 4795 && index <= 5023))
          moveSound = SL.WalkCaveL;
        else if ((index >= 1800 && index <= 2049) || (index >= 3000 && index <= 3149))
          moveSound = SL.WalkWaterL;
        else if ((index >= 5324 && index <= 6051) || (index >= 6119 && index <= 6296))
          moveSound = SL.WalkRoomL;
        else moveSound = SL.WalkWaterL;
      }
      break;
    case 102:
      {
        //Tiles3
        if ((index >= 0 && index <= 299) || (index >= 400 && index <= 449) || (index >= 455 && index <= 522) || (index >= 528 && index <= 531) ||
        (index >= 1553 && index <= 1554) || (index >= 1560 && index <= 1561) || (index >= 1565 && index <= 1566) || (index >= 1569 && index <= 1699) ||
        (index >= 1805 && index <= 1809) || (index >= 1850 && index <= 1854) || (index >= 1860 && index <= 1864) || (index >= 1950 && index <= 1954) ||
        (index >= 2000 && index <= 2204) || (index >= 2300 && index <= 2653))
          moveSound = SL.WalkGroundL;
        else if ((index >= 300 && index <= 399) || (index >= 450 && index <= 454) || (index >= 524 && index <= 527) || (index >= 1705 && index <= 1709) ||
          (index >= 1715 && index <= 1799) || (index >= 2205 && index <= 2299))
          moveSound = SL.WalkLawnL;
        else if ((index >= 1700 && index <= 1704) || (index >= 1710 && index <= 1714))
          moveSound = SL.WalkRoughL;
        else if ((index >= 1800 && index <= 1804) || (index >= 1810 && index <= 1849) || (index >= 1855 && index <= 1859) || (index >= 1865 && index <= 1949) ||
          (index >= 1955 && index <= 1999))
          moveSound = SL.WalkStoneL;
        else if ((index >= 1411 && index <= 1550) || (index >= 1555 && index <= 1557))
          moveSound = SL.WalkWoodL;
        else if ((index >= 532 && index <= 1410) || (index >= 1551 && index <= 1552) || (index >= 1558 && index <= 1559) || (index >= 1562 && index <= 1564) ||
          (index >= 1567 && index <= 1568))
          moveSound = SL.WalkWaterL;
        else moveSound = SL.WalkWaterL;
      }
      break;
    case 103:
      {
        //Tiles4
        if ((index >= 0 && index <= 199) || (index >= 205 && index <= 209) || (index >= 250 && index <= 254) || (index >= 260 && index <= 1481) ||
          (index >= 1495 && index <= 1549) || (index >= 1555 && index <= 1559) || (index >= 2500 && index <= 3349) || (index >= 3355 && index <= 3359) ||
          (index >= 3365 && index <= 3399) || (index >= 3500 && index <= 3899) || (index >= 4193 && index <= 4254) || (index >= 4650 && index <= 4849) ||
          (index >= 5000 && index <= 5149) || (index >= 5155 && index <= 5399) || (index >= 5405 && index <= 5409) || (index >= 5415 && index <= 5449) ||
          (index >= 5550 && index <= 5554) || (index >= 5560 && index <= 5564) || (index >= 5605 && index <= 5609) || (index >= 5615 && index <= 5625) ||
          (index >= 5680 && index <= 5749) || (index >= 7400 && index <= 7649) || (index >= 7655 && index <= 7749) || (index >= 7900 && index <= 8099) ||
          (index >= 8166 && index <= 8299) || (index >= 8305 && index <= 8309) || (index >= 8350 && index <= 8354) || (index >= 8360 && index <= 8364) ||
          (index >= 8400 && index <= 8499) || (index >= 8605 && index <= 8609) || (index >= 8615 && index <= 8619) || (index >= 8623 && index <= 8654) ||
          (index >= 8660 && index <= 8754) || (index >= 8805 && index <= 8809) || (index >= 9050 && index <= 9299) || (index >= 9450 && index <= 9549))
          moveSound = SL.WalkGroundL;
        else if ((index >= 200 && index <= 204) || (index >= 210 && index <= 249) || (index >= 255 && index <= 259) || (index >= 1482 && index <= 1494) ||
          (index >= 1560 && index <= 2499) || (index >= 4255 && index <= 4299) || (index >= 4305 && index <= 4349) || (index >= 5555 && index <= 5559) ||
          (index >= 5565 && index <= 5604) || (index >= 5610 && index <= 5614) || (index >= 7650 && index <= 7654) || (index >= 7750 && index <= 7899) ||
          (index >= 8100 && index <= 8165) || (index >= 8300 && index <= 8304) || (index >= 8310 && index <= 8349) || (index >= 8355 && index <= 8359) ||
          (index >= 8365 && index <= 8399) || (index >= 8505 && index <= 8599) || (index >= 8610 && index <= 8614) || (index >= 8620 && index <= 8622) ||
          (index >= 8655 && index <= 8659) || (index >= 8755 && index <= 8799) || (index >= 8810 && index <= 8849) || (index >= 8950 && index <= 8999) ||
          (index >= 9005 && index <= 9049) || (index >= 9550 && index <= 9554))
          moveSound = SL.WalkLawnL;
        else if ((index >= 4300 && index <= 4304) || (index >= 8600 && index <= 8604) || (index >= 1550 && index <= 1554) || (index >= 4131 && index <= 4193) ||
          (index >= 4350 && index <= 4549) || (index >= 5150 && index <= 5154) || (index >= 8500 && index <= 8504) || (index >= 8800 && index <= 8804) ||
          (index >= 8850 && index <= 8949) || (index >= 9000 && index <= 9004))
          moveSound = SL.WalkRoughL;
        else if ((index >= 3350 && index <= 3354) || (index >= 3360 && index <= 3364) || (index >= 3400 && index <= 3499) || (index >= 5400 && index <= 5404) ||
          (index >= 5410 && index <= 5414) || (index >= 5450 && index <= 5549) || (index >= 5626 && index <= 5679) || (index >= 9300 && index <= 9449) ||
          (index >= 9555 && index <= 9749))
          moveSound = SL.WalkStoneL;
        else if ((index >= 3900 && index <= 4130) || (index >= 4550 && index <= 4649) || (index >= 4850 && index <= 4999) || (index >= 5750 && index <= 7399) ||
          (index >= 9750 && index <= 11349))
          moveSound = SL.WalkCaveL;
        else if ((index >= 11350 && index <= 13534))
          moveSound = SL.WalkWoodL;
        else moveSound = SL.WalkWaterL;
      }
      break;
    case 104:
      {
        //Tiles5
        if ((index >= 10 && index <= 14) || (index >= 100 && index <= 104) || (index >= 110 && index <= 114) || (index >= 156 && index <= 159) ||
          (index >= 165 && index <= 204) || (index >= 210 && index <= 214) || (index >= 255 && index <= 259) || (index >= 300 && index <= 304) ||
          (index >= 310 && index <= 314) || (index >= 400 && index <= 404) || (index >= 410 && index <= 414) || (index >= 500 && index <= 504) ||
          (index >= 510 && index <= 514) || (index >= 555 && index <= 559) || (index >= 800 && index <= 849) || (index >= 855 && index <= 859) ||
          (index >= 900 && index <= 904) || (index >= 910 && index <= 914) || (index >= 955 && index <= 959) || (index >= 1000 && index <= 1004) ||
          (index >= 1010 && index <= 1014) || (index >= 1055 && index <= 1059) || (index >= 1400 && index <= 1404) || (index >= 1410 && index <= 1449) ||
          (index >= 1455 && index <= 1459) || (index >= 1465 && index <= 1499) || (index >= 1700 && index <= 1749) || (index >= 1755 && index <= 1759) ||
          (index >= 1765 && index <= 1804) || (index >= 1810 && index <= 1849) || (index >= 1855 && index <= 1859) || (index >= 1900 && index <= 1904) ||
          (index >= 1910 && index <= 1949) || (index >= 1955 && index <= 1959) || (index >= 2000 && index <= 2099) || (index >= 2150 && index <= 2154) ||
          (index >= 2160 && index <= 2164) || (index >= 2250 && index <= 2254) || (index >= 2260 && index <= 2263) || (index >= 2300 && index <= 2399) ||
          (index >= 2500 && index <= 2549) || (index >= 2555 && index <= 2559) || (index >= 2565 && index <= 2699) || (index >= 2750 && index <= 2754) ||
          (index >= 2760 && index <= 2764) || (index >= 2800 && index <= 2804) || (index >= 2810 && index <= 2814) || (index >= 2855 && index <= 2859) ||
          (index >= 2865 && index <= 2904) || (index >= 2910 && index <= 2914) || (index >= 2955 && index <= 2959) || (index >= 2965 && index <= 3004) ||
          (index >= 3010 && index <= 3014) || (index >= 3100 && index <= 3104) || (index >= 3111 && index <= 3114) || (index >= 3200 && index <= 3204) ||
          (index >= 3300 && index <= 3304) || (index >= 3800 && index <= 3804) || (index >= 3900 && index <= 3904) || (index >= 3910 && index <= 3914) ||
          (index >= 4000 && index <= 4004) || (index >= 4010 && index <= 4014) || (index >= 4100 && index <= 4104) || (index >= 4110 && index <= 4114) ||
          (index >= 4200 && index <= 4204) || (index >= 4210 && index <= 4214) || (index >= 4300 && index <= 4304) || (index >= 4310 && index <= 4311) ||
          (index >= 4500 && index <= 4504) || (index >= 4510 && index <= 4514) || (index >= 4600 && index <= 4604) || (index >= 4610 && index <= 4614) ||
          (index >= 4620 && index <= 4621) || (index >= 4655 && index <= 4659) || (index >= 4700 && index <= 4904) || (index >= 4910 && index <= 4949) ||
          (index >= 4955 && index <= 4959) || (index >= 5000 && index <= 5004) || (index >= 5010 && index <= 5014) || (index >= 5100 && index <= 5104) ||
          (index >= 5110 && index <= 5114) || (index >= 5155 && index <= 5159) || (index >= 5165 && index <= 5204) || (index >= 5210 && index <= 5254) ||
          (index >= 5260 && index <= 5264) || (index >= 5305 && index <= 5309) || (index >= 5355 && index <= 5359) || (index >= 5400 && index <= 5599) ||
          (index >= 5650 && index <= 5654) || (index >= 5700 && index <= 5799) || (index >= 5900 && index <= 6049) || (index >= 6100 && index <= 6399) ||
          (index >= 6850 && index <= 6949) || (index >= 7050 && index <= 7349) || (index >= 7750 && index <= 7754) || (index >= 7760 && index <= 7764) ||
          (index >= 7805 && index <= 7809) || (index >= 7815 && index <= 7849) || (index >= 7950 && index <= 7999) || (index >= 8150 && index <= 8399))
          moveSound = SL.WalkGroundL;
        else if ((index >= 710 && index <= 714) || (index >= 860 && index <= 899) || (index >= 905 && index <= 909) || (index >= 915 && index <= 949) ||
          (index >= 960 && index <= 999) || (index >= 1005 && index <= 1009) || (index >= 1015 && index <= 1049) || (index >= 1060 && index <= 1099) ||
          (index >= 1105 && index <= 1109) || (index >= 1160 && index <= 1199) || (index >= 1260 && index <= 1264) || (index >= 1305 && index <= 1309) ||
          (index >= 1360 && index <= 1364) || (index >= 1405 && index <= 1409) || (index >= 1460 && index <= 1464) || (index >= 1500 && index <= 1549) ||
          (index >= 1555 && index <= 1599) || (index >= 1608 && index <= 1609) || (index >= 1660 && index <= 1664) || (index >= 1760 && index <= 1764) ||
          (index >= 1805 && index <= 1809) || (index >= 1860 && index <= 1899) || (index >= 1905 && index <= 1909) || (index >= 1960 && index <= 1999) ||
          (index >= 2400 && index <= 2499) || (index >= 2550 && index <= 2554) || (index >= 2560 && index <= 2564) || (index >= 3400 && index <= 3404) ||
          (index >= 3410 && index <= 3414) || (index >= 3455 && index <= 3459) || (index >= 3465 && index <= 3504) || (index >= 3510 && index <= 3514) ||
          (index >= 3555 && index <= 3559) || (index >= 3565 && index <= 3604) || (index >= 3610 && index <= 3614) || (index >= 3700 && index <= 3704) ||
          (index >= 3710 && index <= 3714) || (index >= 4400 && index <= 4404) || (index >= 4410 && index <= 4414) || (index >= 4904 && index <= 4909) ||
          (index >= 4950 && index <= 4954) || (index >= 4960 && index <= 4999) || (index >= 5005 && index <= 5009) || (index >= 5015 && index <= 5099) ||
          (index >= 5105 && index <= 5109) || (index >= 5115 && index <= 5154) || (index >= 5160 && index <= 5164) || (index >= 5205 && index <= 5209) ||
          (index >= 5225 && index <= 5259) || (index >= 5265 && index <= 5304) || (index >= 5310 && index <= 5354) || (index >= 5360 && index <= 5399) ||
          (index >= 5600 && index <= 5649) || (index >= 5655 && index <= 5699) || (index >= 6050 && index <= 6099) || (index >= 6950 && index <= 7049) ||
          (index >= 7350 && index <= 7449) || (index >= 7855 && index <= 7859) || (index >= 7865 && index <= 7899))
          moveSound = SL.WalkLawnL;
        else if ((index >= 600 && index <= 604) || (index >= 610 && index <= 614) || (index >= 655 && index <= 659) || (index >= 700 && index <= 704) ||
          (index >= 850 && index <= 854) || (index >= 950 && index <= 954) || (index >= 1050 && index <= 1054) || (index >= 1150 && index <= 1154) ||
          (index >= 1250 && index <= 1254) || (index >= 1350 && index <= 1354) || (index >= 1450 && index <= 1454) || (index >= 1550 && index <= 1554) ||
          (index >= 1650 && index <= 1654) || (index >= 1750 && index <= 1754) || (index >= 1850 && index <= 1854) || (index >= 1950 && index <= 1954) ||
          (index >= 5800 && index <= 5899) || (index >= 7450 && index <= 7454) || (index >= 7460 && index <= 7464) || (index >= 7505 && index <= 7509) ||
          (index >= 7515 && index <= 7554) || (index >= 7560 && index <= 7564) || (index >= 7605 && index <= 7609) || (index >= 7615 && index <= 7649) ||
          (index >= 7655 && index <= 7659) || (index >= 7665 && index <= 7704) || (index >= 7755 && index <= 7759) || (index >= 7765 && index <= 7804) ||
          (index >= 7810 && index <= 7814) || (index >= 7850 && index <= 7854) || (index >= 7860 && index <= 7864))
          moveSound = SL.WalkRoughL;
        else if ((index >= 5 && index <= 9) || (index >= 15 && index <= 99) || (index >= 105 && index <= 109) || (index >= 115 && index <= 155) ||
          (index >= 160 && index <= 164) || (index >= 205 && index <= 209) || (index >= 215 && index <= 254) || (index >= 260 && index <= 299) ||
          (index >= 305 && index <= 309) || (index >= 315 && index <= 399) || (index >= 405 && index <= 409) || (index >= 415 && index <= 499) ||
          (index >= 505 && index <= 509) || (index >= 515 && index <= 554) || (index >= 560 && index <= 599) || (index >= 605 && index <= 609) ||
          (index >= 615 && index <= 654) || (index >= 660 && index <= 699) || (index >= 705 && index <= 709) || (index >= 715 && index <= 799) ||
          (index >= 1100 && index <= 1104) || (index >= 1110 && index <= 1149) || (index >= 1155 && index <= 1159) || (index >= 1200 && index <= 1249) ||
          (index >= 1255 && index <= 1259) || (index >= 1265 && index <= 1304) || (index >= 1310 && index <= 1349) || (index >= 1355 && index <= 1359) ||
          (index >= 1365 && index <= 1399) || (index >= 1600 && index <= 1604) || (index >= 1606 && index <= 1607) || (index >= 1610 && index <= 1649) ||
          (index >= 1655 && index <= 1659) || (index >= 1665 && index <= 1699) || (index >= 2100 && index <= 2149) || (index >= 2155 && index <= 2159) ||
          (index >= 2165 && index <= 2249) || (index >= 2255 && index <= 2259) || (index >= 2264 && index <= 2299) || (index >= 2700 && index <= 2749) ||
          (index >= 2755 && index <= 2759) || (index >= 2765 && index <= 2799) || (index >= 2805 && index <= 2809) || (index >= 2815 && index <= 2854) ||
          (index >= 2860 && index <= 2864) || (index >= 2905 && index <= 2909) || (index >= 2915 && index <= 2954) || (index >= 2960 && index <= 2964) ||
          (index >= 3005 && index <= 3009) || (index >= 3015 && index <= 3099) || (index >= 3105 && index <= 3110) || (index >= 3115 && index <= 3199) ||
          (index >= 3205 && index <= 3299) || (index >= 3305 && index <= 3399) || (index >= 3405 && index <= 3409) || (index >= 3415 && index <= 3454) ||
          (index >= 3460 && index <= 3464) || (index >= 3505 && index <= 3509) || (index >= 3515 && index <= 3554) || (index >= 3560 && index <= 3564) ||
          (index >= 3605 && index <= 3609) || (index >= 3615 && index <= 3699) || (index >= 3705 && index <= 3709) || (index >= 3715 && index <= 3799) ||
          (index >= 3805 && index <= 3899) || (index >= 3905 && index <= 3909) || (index >= 3915 && index <= 3999) || (index >= 4005 && index <= 4009) ||
          (index >= 4015 && index <= 4099) || (index >= 4105 && index <= 4109) || (index >= 4115 && index <= 4199) || (index >= 4205 && index <= 4209) ||
          (index >= 4305 && index <= 4309) || (index >= 4312 && index <= 4399) || (index >= 4405 && index <= 4409) || (index >= 4415 && index <= 4499) ||
          (index >= 4505 && index <= 4509) || (index >= 4515 && index <= 4599) || (index >= 4605 && index <= 4609) || (index >= 4615 && index <= 4619) ||
          (index >= 4622 && index <= 4654) || (index >= 4660 && index <= 4699) || (index >= 6400 && index <= 6849) || (index >= 7455 && index <= 7459) ||
          (index >= 7465 && index <= 7504) || (index >= 7510 && index <= 7514) || (index >= 7555 && index <= 7559) || (index >= 7565 && index <= 7604) ||
          (index >= 7610 && index <= 7614) || (index >= 7650 && index <= 7654) || (index >= 7660 && index <= 7664) ||
          (index >= 7705 && index <= 7709) || (index >= 7715 && index <= 7749))
          moveSound = SL.WalkStoneL;
        else if ((index >= 8000 && index <= 8149) || (index >= 8400 && index <= 8481))
          moveSound = SL.WalkCaveL;
        else if ((index >= 7900 && index <= 7949))
          moveSound = SL.WalkWaterL;
        else moveSound = SL.WalkWaterL;
      }
      break;
    case 105:
      {
        //Tiles6
        if (index >= 0 && index <= 1539)
          moveSound = SL.WalkLawnL;
        else if (index >= 1540 && index <= 2368)
          moveSound = SL.WalkRoomL;
        else
          moveSound = SL.WalkWaterL;
      }
      break;
    case 106:
      {
        //Tiles7
        if ((index >= 0 && index <= 19) || (index >= 24 && index <= 53) || (index >= 56 && index <= 69) || (index >= 72 && index <= 74) ||
          (index >= 77 && index <= 93) || (index >= 99 && index <= 115) || (index >= 121 && index <= 124) || (index >= 127 && index <= 132) ||
          (index >= 135 && index <= 141) || (index >= 185 && index <= 190) || (index >= 191 && index <= 200) || (index >= 927 && index <= 940) ||
          (index >= 961 && index <= 1160))
          moveSound = SL.WalkGroundL;
        else if ((index >= 201 && index <= 926) || (index >= 941 && index <= 960) || (index >= 1161 && index <= 3310))
          moveSound = SL.WalkLawnL;
        else if ((index >= 96 && index <= 98))
          moveSound = SL.WalkWoodL;
        else if ((index >= 94 && index <= 95) || (index >= 151 && index <= 184))
          moveSound = SL.WalkStoneL;
        else if ((index >= 20 && index <= 23) || (index >= 54 && index <= 55) || (index >= 70 && index <= 71) || (index >= 75 && index <= 76) ||
          (index >= 116 && index <= 120))
          moveSound = SL.WalkCaveL;
        else if ((index >= 125 && index <= 126) || (index >= 133 && index <= 134) || (index >= 142 && index <= 151))
          moveSound = SL.WalkWaterL;
        else moveSound = SL.WalkWaterL;
      }
      break;
    case 107:
      {
        //Tiles8
        if (index >= 0 && index <= 1215)
          moveSound = SL.WalkCaveL;
        else moveSound = SL.WalkWaterL;
      }
      break;
    default:
      moveSound = SL.WalkWaterL;
      break;
  }
  // #endregion

  index = (m.middleImage[i] & 0x1FFFF) - 1;

  // #region Middle Tiles
  switch (m.middleIndex[i])
  {
    case 114:
      {
        //SMTiles5  iJam Ver
        if ((index >= 3165 && index <= 21311) || (index >= 23775 && index <= 24650))
          moveSound = SL.WalkGroundL;
        else if ((index >= 2773 && index <= 3164) || (index >= 21416 && index <= 21462) || (index >= 21510 && index <= 21511) || (index >= 21528 && index <= 21531) ||
            (index >= 21548 && index <= 21533) || (index >= 21569 && index <= 21575) || (index >= 21591 && index <= 21594) || (index >= 21609 && index <= 21613) ||
            (index >= 21627 && index <= 21632) || (index >= 21646 && index <= 21650) || (index >= 21634 && index <= 21667) || (index >= 22977 && index <= 23316) ||
            (index >= 30473 && index <= 31255))
          moveSound = SL.WalkLawnL;
        else if ((index >= 24651 && index <= 30472))
          moveSound = SL.WalkRoomL;
        else if ((index >= 0 && index <= 2772) || (index >= 21312 && index <= 21415) || (index >= 21463 && index <= 21509) || (index >= 21512 && index <= 21527) ||
          (index >= 21532 && index <= 21547) || (index >= 21534 && index <= 21568) || (index >= 21576 && index <= 21590) || (index >= 21595 && index <= 21608) ||
          (index >= 21614 && index <= 21626) || (index >= 21633 && index <= 21645) || (index >= 21651 && index <= 21633) || (index >= 21668 && index <= 22976) ||
          (index >= 23317 && index <= 23774) || (index >= 31256 && index <= 31302))
          moveSound = SL.WalkStoneL;
      }
      break;
    case 116:
      {
        //SMTiles7  iJam Ver
        if ((index >= 10 && index <= 13) || (index >= 23 && index <= 25) || (index >= 35 && index <= 37) || (index >= 47 && index <= 49) ||
          (index >= 59 && index <= 61) || (index >= 71 && index <= 73) || (index >= 83 && index <= 85) || (index >= 95 && index <= 97) ||
          (index >= 107 && index <= 109) || (index >= 119 && index <= 121) || (index >= 131 && index <= 133) || (index >= 143 && index <= 145) ||
          (index >= 155 && index <= 157) || (index >= 167 && index <= 169) || (index >= 179 && index <= 181) || (index >= 191 && index <= 193) ||
          (index >= 203 && index <= 205) || (index >= 215 && index <= 217) || (index >= 227 && index <= 229) || (index >= 239 && index <= 241) ||
          (index >= 251 && index <= 253) || (index >= 263 && index <= 264) || (index >= 269 && index <= 271) || (index >= 281 && index <= 283) ||
          (index >= 297 && index <= 299) || (index >= 311 && index <= 313) || (index >= 323 && index <= 325) || (index >= 333 && index <= 335) ||
          (index >= 341 && index <= 343) || (index >= 347 && index <= 349) || (index >= 351 && index <= 353) || (index >= 488 && index <= 489) ||
          (index >= 494 && index <= 495) || (index >= 502 && index <= 503) || (index >= 512 && index <= 514) || (index >= 524 && index <= 526) ||
          (index >= 538 && index <= 540) || (index >= 554 && index <= 556) || (index >= 572 && index <= 573) || (index >= 648 && index <= 649) ||
          (index >= 665 && index <= 667) || (index >= 677 && index <= 679) || (index >= 689 && index <= 690) || (index >= 701 && index <= 702) ||
          (index >= 713 && index <= 714) || (index >= 1260 && index <= 1567) || (index >= 2772 && index <= 5869) || (index >= 8887 && index <= 10230))
          moveSound = SL.WalkGroundL;
        else if ((index >= 2013 && index <= 2438) || (index >= 2639 && index <= 2771))
          moveSound = SL.WalkRoughL;
        else if ((index >= 7810 && index <= 7814) || (index >= 7827 && index <= 7832) || (index >= 7847 && index <= 7853) || (index >= 7866 && index <= 7867) ||
          (index >= 7872 && index <= 7874) || (index >= 7884 && index <= 7887) || (index >= 7892 && index <= 7910) || (index >= 7916 && index <= 7923) ||
          (index >= 7929 && index <= 7990))
          moveSound = SL.WalkWoodL;
        else if ((index >= 5870 && index <= 7753) || (index >= 7868 && index <= 7871) || (index >= 7888 && index <= 7891) || (index >= 7991 && index <= 8886) ||
          (index >= 10231 && index <= 10430))
          moveSound = SL.WalkRoomL;
        else if ((index >= 0 && index <= 9) || (index >= 14 && index <= 22) || (index >= 26 && index <= 34) || (index >= 38 && index <= 46) ||
          (index >= 50 && index <= 58) || (index >= 62 && index <= 70) || (index >= 74 && index <= 82) || (index >= 86 && index <= 94) ||
          (index >= 98 && index <= 106) || (index >= 110 && index <= 118) || (index >= 122 && index <= 130) || (index >= 134 && index <= 142) ||
          (index >= 146 && index <= 154) || (index >= 158 && index <= 166) || (index >= 170 && index <= 178) || (index >= 182 && index <= 190) ||
          (index >= 194 && index <= 202) || (index >= 206 && index <= 214) || (index >= 218 && index <= 226) || (index >= 230 && index <= 238) ||
          (index >= 242 && index <= 250) || (index >= 254 && index <= 262) || (index >= 265 && index <= 268) || (index >= 272 && index <= 280) ||
          (index >= 284 && index <= 296) || (index >= 300 && index <= 310) || (index >= 314 && index <= 322) || (index >= 326 && index <= 332) ||
          (index >= 336 && index <= 340) || (index >= 344 && index <= 346) || (index >= 350 && index <= 350) || (index >= 354 && index <= 487) ||
          (index >= 490 && index <= 493) || (index >= 496 && index <= 501) || (index >= 504 && index <= 511) || (index >= 515 && index <= 523) ||
          (index >= 527 && index <= 537) || (index >= 541 && index <= 553) || (index >= 557 && index <= 571) || (index >= 574 && index <= 647) ||
          (index >= 650 && index <= 664) || (index >= 668 && index <= 676) || (index >= 680 && index <= 688) || (index >= 691 && index <= 700) ||
          (index >= 703 && index <= 712) || (index >= 715 && index <= 1259) || (index >= 1568 && index <= 2012) || (index >= 7754 && index <= 7809) ||
          (index >= 7815 && index <= 7826) || (index >= 7833 && index <= 7846) || (index >= 7854 && index <= 7865) || (index >= 7875 && index <= 7883) ||
          (index >= 7911 && index <= 7915) || (index >= 7924 && index <= 7928))
          moveSound = SL.WalkStoneL;
        else if ((index >= 2439 && index <= 2638))
          moveSound = SL.WalkCaveL;
      }
      break;
  }
  // #endregion

  index = (m.middleIndex[i] - 110) * 100000 + index;

  // #region Middle Tiles old code method

  if (index >= 0 && index <= 99999)
  {
    //SMTiles
    if (index >= 0 && index <= 7)
      moveSound = SL.WalkGroundL;
    else if (index >= 8 && index <= 1175)
      moveSound = SL.WalkLawnL;
  }

  else if (index >= 100000 && index <= 199999)
  {
    //SMTiles2
    if (index >= 100000 && index <= 106205)
      moveSound = SL.WalkGroundL;
    else if (index >= 106206 && index <= 109914)
      moveSound = SL.WalkStoneL;
  }

  else if (index >= 200000 && index <= 299999)
  {
    //SMTiles3
    if ((index >= 9119 && index <= 9235) || (index >= 9296 && index <= 209355) || (index >= 9416 && index <= 209475) || (index >= 9536 && index <= 209835) ||
      (index >= 210556 && index <= 210688) || (index >= 210916 && index <= 211035) || (index >= 219200 && index <= 220540) || (index >= 220788 && index <= 221194) ||
      (index >= 229475 && index <= 230959) || (index >= 231378 && index <= 231436))
      moveSound = SL.WalkGroundL;
    else if ((index >= 209236 && index <= 209295) || (index >= 209356 && index <= 209415) || (index >= 209476 && index <= 209535) || (index >= 209836 && index <= 210435) ||
      (index >= 210689 && index <= 210915) || (index >= 223242 && index <= 225299) || (index >= 226252 && index <= 227305) || (index >= 228128 && index <= 228132) ||
      (index >= 228187 && index <= 228192) || (index >= 228245 && index <= 228249) || (index >= 228275 && index <= 228280) || (index >= 228358 && index <= 228454) ||
      (index >= 228974 && index <= 229474) || (index >= 230960 && index <= 231377))
      moveSound = SL.WalkLawnL;
    else if ((index >= 300000 && index <= 209118) || (index >= 220541 && index <= 220787) || (index >= 221195 && index <= 223241) || (index >= 225300 && index <= 226251) ||
      (index >= 227306 && index <= 228127) || (index >= 228133 && index <= 228186) || (index >= 228193 && index <= 228244) || (index >= 228250 && index <= 228274) ||
      (index >= 228281 && index <= 228357) || (index >= 228455 && index <= 228973) || (index >= 231437 && index <= 231703))
      moveSound = SL.WalkStoneL;
    else if ((index >= 211036 && index <= 219199))
      moveSound = SL.WalkRoomL;
    else if ((index >= 210436 && index <= 210555))
      moveSound = SL.WalkRoughL;
  }

  else if (index >= 300000 && index <= 399999)
  {
    //SMTiles4
    if ((index >= 300000 && index <= 300682) || (index >= 300695 && index <= 300699) || (index >= 300714 && index <= 300718) || (index >= 300733 && index <= 300745) ||
      (index >= 300752 && index <= 300829) || (index >= 300833 && index <= 300849) || (index >= 300852 && index <= 300904) || (index >= 300907 && index <= 300920) ||
      (index >= 300923 && index <= 300935) || (index >= 300939 && index <= 301088) || (index >= 301105 && index <= 301106) || (index >= 301112 && index <= 301113) ||
      (index >= 301137 && index <= 301138) || (index >= 301422 && index <= 301423) || (index >= 301441 && index <= 301446) || (index >= 301460 && index <= 301467) ||
      (index >= 301764 && index <= 301767) || (index >= 301772 && index <= 301786) || (index >= 301790 && index <= 302129) || (index >= 302132 && index <= 302291) ||
      (index >= 302492 && index <= 302827) || (index >= 304419 && index <= 304646) || (index >= 306951 && index <= 306955) || (index >= 307027 && index <= 307104) ||
      (index >= 307010 && index <= 307118) || (index >= 307124 && index <= 307133) || (index >= 307138 && index <= 307149) || (index >= 307162 && index <= 307167) ||
      (index >= 307243 && index <= 308983) || (index >= 310892 && index <= 328054) || (index >= 304935 && index <= 305614) || (index >= 305619 && index <= 305627) ||
      (index >= 305633 && index <= 305640) || (index >= 305647 && index <= 305651) || (index >= 305661 && index <= 305563) || (index >= 305675 && index <= 305676) ||
      (index >= 305989 && index <= 306437) || (index >= 306457 && index <= 306474) || (index >= 306484 && index <= 306505) || (index >= 306512 && index <= 306533) ||
      (index >= 306540 && index <= 306625) || (index >= 306636 && index <= 306761) || (index >= 306788 && index <= 306929) || (index >= 306936 && index <= 306942))
      moveSound = SL.WalkGroundL;
    else if ((index >= 301468 && index <= 301515) || (index >= 302130 && index <= 302131) || (index >= 302292 && index <= 302491) || (index >= 302828 && index <= 303063))
      moveSound = SL.WalkRoughL;
    else if ((index >= 301516 && index <= 301555) || (index >= 304647 && index <= 304934) || (index >= 305615 && index <= 305618) || (index >= 305628 && index <= 303632) ||
      (index >= 305641 && index <= 305646) || (index >= 305652 && index <= 305660) || (index >= 305664 && index <= 305674) || (index >= 305677 && index <= 305988) ||
      (index >= 306438 && index <= 306456) || (index >= 306475 && index <= 306483) || (index >= 306506 && index <= 306511) || (index >= 306534 && index <= 306539) ||
      (index >= 306626 && index <= 306635) || (index >= 306762 && index <= 306787) || (index >= 306930 && index <= 306935) || (index >= 306943 && index <= 306950) ||
      (index >= 306956 && index <= 307026) || (index >= 307105 && index <= 307109) || (index >= 307119 && index <= 307123) || (index >= 307134 && index <= 307137) ||
      (index >= 307150 && index <= 307161) || (index >= 307168 && index <= 307242) || (index >= 308983 && index <= 310891))
      moveSound = SL.WalkLawnL;
    else if ((index >= 300691 && index <= 300694) || (index >= 301155 && index <= 301164) || (index >= 301167 && index <= 301173) || (index >= 301181 && index <= 301185) ||
      (index >= 301194 && index <= 301197) || (index >= 301208 && index <= 301211) || (index >= 301223 && index <= 301225) || (index >= 301233 && index <= 301235) ||
      (index >= 301238 && index <= 301239) || (index >= 301247 && index <= 301250) || (index >= 301254 && index <= 301256) || (index >= 301262 && index <= 301265) ||
      (index >= 301271 && index <= 301273) || (index >= 301277 && index <= 301280) || (index >= 301288 && index <= 301295) || (index >= 301305 && index <= 301310) ||
      (index >= 301322 && index <= 301325) || (index >= 301339 && index <= 301341) || (index >= 301350 && index <= 301352) || (index >= 301356 && index <= 301358) ||
      (index >= 301368 && index <= 301375) || (index >= 301386 && index <= 301392) || (index >= 301404 && index <= 301409) || (index >= 301424 && index <= 301427) ||
      (index >= 301587 && index <= 301590) || (index >= 301605 && index <= 301608) || (index >= 301622 && index <= 301625) || (index >= 301638 && index <= 301642) ||
      (index >= 301655 && index <= 301660) || (index >= 301666 && index <= 301678) || (index >= 301683 && index <= 301685) || (index >= 301688 && index <= 301692) ||
      (index >= 301700 && index <= 301709) || (index >= 301718 && index <= 301728) || (index >= 301736 && index <= 301744) || (index >= 301754 && index <= 301763))
      moveSound = SL.WalkWoodL;
    else if ((index >= 303064 && index <= 304418))
      moveSound = SL.WalkRoomL;
    else if ((index >= 328055 && index <= 332912))
      moveSound = SL.WalkCaveL;
    else if ((index >= 300683 && index <= 300690) || (index >= 300700 && index <= 300713) || (index >= 300719 && index <= 300732) || (index >= 300746 && index <= 300751) ||
      (index >= 300830 && index <= 300832) || (index >= 300850 && index <= 300851) || (index >= 300905 && index <= 300906) || (index >= 300921 && index <= 300922) ||
      (index >= 300936 && index <= 300938) || (index >= 301089 && index <= 301104) || (index >= 301107 && index <= 301111) || (index >= 301114 && index <= 301136) ||
      (index >= 301139 && index <= 301154) || (index >= 301165 && index <= 301166) || (index >= 301174 && index <= 301180) || (index >= 301186 && index <= 301193) ||
      (index >= 301198 && index <= 301207) || (index >= 301212 && index <= 301222) || (index >= 301226 && index <= 301232) || (index >= 301236 && index <= 301237) ||
      (index >= 301240 && index <= 301246) || (index >= 301251 && index <= 301253) || (index >= 301258 && index <= 301261) || (index >= 301266 && index <= 301270) ||
      (index >= 301274 && index <= 301276) || (index >= 301281 && index <= 301287) || (index >= 301296 && index <= 301304) || (index >= 301311 && index <= 301321) ||
      (index >= 301326 && index <= 301338) || (index >= 301342 && index <= 301349) || (index >= 301354 && index <= 301355) || (index >= 301359 && index <= 301367) ||
      (index >= 301376 && index <= 301385) || (index >= 301393 && index <= 301403) || (index >= 301410 && index <= 301421) || (index >= 301428 && index <= 301440) ||
      (index >= 301447 && index <= 301459) || (index >= 301556 && index <= 301586) || (index >= 301591 && index <= 301604) || (index >= 301609 && index <= 301621) ||
      (index >= 301626 && index <= 301637) || (index >= 301643 && index <= 301654) || (index >= 301661 && index <= 301665) || (index >= 301679 && index <= 301682) ||
      (index >= 301686 && index <= 301687) || (index >= 301693 && index <= 301699) || (index >= 301710 && index <= 301717) || (index >= 301729 && index <= 301735) ||
      (index >= 301768 && index <= 301771) || (index >= 301787 && index <= 301789) || (index >= 301745 && index <= 301753))
      moveSound = SL.WalkWaterL;
  }

  else if (index >= 500000 && index <= 599999)
  {
    //SMTiles6 (115 image)
    if ((index >= 500000 && index <= 501844) || (index >= 503294 && index <= 509086) || (index >= 509520 && index <= 512812) || (index >= 514854 && index <= 515035) ||
      (index >= 521326 && index <= 522110) || (index >= 526820 && index <= 528871))
      moveSound = SL.WalkGroundL;
    else if ((index >= 501845 && index <= 503293) || (index >= 513557 && index <= 513745))
      moveSound = SL.WalkLawnL;
    else if ((index >= 509087 && index <= 509519) || (index >= 512813 && index <= 512909) || (index >= 516308 && index <= 521325) || (index >= 522111 && index <= 526819) ||
      (index >= 528872 && index <= 530723))
      moveSound = SL.WalkRoomL;
    else if ((index >= 512910 && index <= 513556) || (index >= 513746 && index <= 514853) || (index >= 515037 && index <= 516307))
      moveSound = SL.WalkStoneL;
  }

  else if (index >= 700000 && index <= 799999)
  {
    //SMTiles8
    if (index >= 700000 && index <= 701702)
      moveSound = SL.WalkCaveL;

  }

  // #endregion

  return moveSound;
}

function playWemadeMir3StepSound(m: MapCells, i: number): number
{
  let moveSound: number;
  moveSound = 0;

  let backIndex = (m.backIndex[i]);
  let midIndex = (m.middleIndex[i]);
  let midImage = (m.middleImage[i] & 0x7FFF - 1);

  // #region Shanda & Wemade Mir 3
  let index = (m.backImage[i] & 0x1FFFF) - 1; // Back
  // #region Back 300 Tilesc*!
  if (backIndex == 300 || backIndex == 200)
  {   // Lawn
    if ((index >= 1605 && index <= 1750) || (index >= 1753 && index <= 1759) || (index >= 1763 && index <= 1769)
      || (index >= 1772 && index <= 1794) || (index >= 7246 && index <= 7477))
      moveSound = SL.WalkLawnL;
    // Rough
    else if (index >= 6672 && index <= 6918)
      moveSound = SL.WalkRoughL;
    // Stone
    else if ((index >= 500 && index <= 518) || (index >= 523 && index <= 524) || (index >= 527 && index <= 528)
      || (index >= 907 && index <= 733) || (index >= 1543 && index <= 1564) || (index >= 1561 && index <= 1564)
      || (index == 1589) || (index >= 1591 && index <= 1592) || (index == 1595) || (index >= 1598 && index <= 1600)
      || (index >= 1751 && index <= 1752) || (index >= 1760 && index <= 1762) || (index >= 1770 && index <= 1771)
      || (index >= 1882 && index <= 1883) || (index >= 1891 && index <= 1893) || (index >= 1900 && index <= 1903)
      || (index >= 1910 && index <= 1911) || (index >= 1920 && index <= 1922) || (index >= 1926 && index <= 1929)
      || (index >= 1940 && index <= 1941) || (index == 7138) || (index == 7492) || (index == 7552) || (index == 7557)
      || (index == 7560) || (index == 7932) || (index == 7935) || (index == 7940) || (index == 7943) || (index == 8085)
      || (index == 8110) || (index == 8111) || (index == 8130) || (index == 8352) || (index == 8356) || (index == 8543)
      || (index == 8571) || (index == 8579) || (index == 8680) || (index == 9217) || (index == 9248) || (index == 9249)
      || (index >= 9283 && index <= 9285) || (index >= 9287 && index <= 9288) || (index >= 9300 && index <= 9301)
      || (index >= 9317 && index <= 9318) || (index >= 9321 && index <= 9322) || (index >= 9333 && index <= 9334)
      || (index == 9350) || (index == 9353) || (index >= 9377 && index <= 9378) || (index == 9381)
      || (index >= 9405 && index <= 9406) || (index == 9409) || (index >= 9442 && index <= 9443)
      || (index >= 9471 && index <= 9472) || (index == 9484) || (index == 9517) || (index >= 9553 && index <= 9554)
      || (index >= 9630 && index <= 9631) || (index == 9662))
      moveSound = SL.WalkStoneL;
    // Cave
    else if (index >= 980 && index <= 1498)
      moveSound = SL.WalkCaveL;
    // Wood
    else if ((index >= 1502 && index <= 1533) || (index == 1535) || (index == 1539) || (index == 1541) || (index == 1566)
      || (index == 1569) || (index == 1573) || (index >= 1577 && index <= 1578) || (index >= 1580 && index <= 1587))
      moveSound = SL.WalkWoodL;
    // Room
    else if ((index == 1534) || (index >= 1536 && index <= 1538) || (index == 1540) || (index == 1567)
      || (index >= 1570 && index <= 1572) || (index >= 1574 && index <= 1576) || (index == 1579) || (index == 1590)
      || (index >= 1593 && index <= 1594) || (index >= 1596 && index <= 1597))
      moveSound = SL.WalkRoomL;
    // Water
    else if ((index >= 0 && index <= 3) || (index >= 7 && index <= 11) || (index >= 14 && index <= 17)
      || (index >= 23 && index <= 25) || (index >= 30 && index <= 35) || (index >= 38 && index <= 43)
      || (index >= 48 && index <= 51) || (index >= 55 && index <= 59) || (index >= 62 && index <= 65)
      || (index >= 88 && index <= 91) || (index >= 100 && index <= 104) || (index >= 109 && index <= 117)
      || (index >= 124 && index <= 130) || (index >= 139 && index <= 142) || (index >= 145 && index <= 150)
      || (index >= 153 && index <= 159) || (index >= 164 && index <= 167) || (index >= 172 && index <= 175)
      || (index >= 179 && index <= 182) || (index >= 186 && index <= 192) || (index >= 195 && index <= 200)
      || (index >= 205 && index <= 207) || (index >= 212 && index <= 216) || (index >= 221 && index <= 224)
      || (index >= 228 && index <= 231) || (index >= 236 && index <= 243) || (index >= 250 && index <= 258)
      || (index >= 264 && index <= 268) || (index >= 277 && index <= 279) || (index >= 291 && index <= 296)
      || (index >= 300 && index <= 305) || (index >= 308 && index <= 311) || (index >= 320 && index <= 326)
      || (index >= 333 && index <= 341) || (index >= 350 && index <= 354) || (index >= 363 && index <= 366)
      || (index >= 385 && index <= 388) || (index >= 392 && index <= 396) || (index >= 400 && index <= 403)
      || (index >= 415 && index <= 417) || (index >= 423 && index <= 427) || (index >= 435 && index <= 443)
      || (index >= 449 && index <= 457) || (index >= 5825 && index <= 5829) || (index >= 5835 && index <= 5844)
      || (index >= 5849 && index <= 5859) || (index >= 5865 && index <= 5877) || (index >= 5882 && index <= 5894)
      || (index >= 5900 && index <= 5912) || (index >= 5918 && index <= 5932) || (index >= 5937 && index <= 5951)
      || (index >= 5958 && index <= 5973) || (index >= 5979 && index <= 5993) || (index >= 5999 && index <= 6011)
      || (index >= 6017 && index <= 6029) || (index >= 6034 && index <= 6046) || (index >= 6051 && index <= 6061)
      || (index >= 6067 && index <= 6076) || (index >= 6083 && index <= 6087) || (index >= 6105 && index <= 6110)
      || (index >= 6116 && index <= 6128) || (index >= 6138 && index <= 6152) || (index >= 6159 && index <= 6283)
      || (index >= 6290 && index <= 6303) || (index >= 6313 && index <= 6325) || (index >= 6331 && index <= 6336)
      || (index >= 6340 && index <= 6345) || (index >= 6348 && index <= 6354) || (index >= 6357 && index <= 6363)
      || (index >= 6366 && index <= 6371) || (index >= 6378 && index <= 6383) || (index >= 6386 && index <= 6392)
      || (index >= 6395 && index <= 6400) || (index >= 6404 && index <= 6409) || (index >= 6416 && index <= 6421)
      || (index >= 6424 && index <= 6430) || (index >= 6433 && index <= 6439) || (index >= 6442 && index <= 6448)
      || (index >= 6454 && index <= 6458) || (index >= 6462 && index <= 6467) || (index >= 6471 && index <= 6477)
      || (index >= 6480 && index <= 6486) || (index >= 6493 && index <= 6499) || (index >= 6502 && index <= 6508)
      || (index >= 6511 && index <= 6517) || (index >= 6520 && index <= 6525) || (index >= 6532 && index <= 6537)
      || (index >= 6541 && index <= 6546) || (index >= 6549 && index <= 6555) || (index >= 6558 && index <= 6563)
      || (index >= 6569 && index <= 6575) || (index >= 6578 && index <= 6584) || (index >= 6587 && index <= 6593)
      || (index >= 6597 && index <= 6601) || (index >= 6607 && index <= 6613) || (index >= 6616 && index <= 6622)
      || (index >= 6627 && index <= 6631) || (index >= 6635 && index <= 6639))
      moveSound = SL.WalkWaterL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 315 Wood/Tilesc*
  if (backIndex == 315 || backIndex == 215)
  {   // Stone
    if ((index == 51) || (index >= 57 && index <= 58) || (index >= 64 && index <= 66) || (index == 74)
      || (index == 79) || (index == 87) || (index == 99) || (index == 103) || (index == 116) || (index == 123)
      || (index >= 136 && index <= 140) || (index >= 160 && index <= 166) || (index >= 186 && index <= 196)
      || (index >= 215 && index <= 221) || (index == 225) || (index >= 245 && index <= 250) || (index >= 268 && index <= 271)
      || (index == 282) || (index == 286) || (index == 290) || (index >= 302 && index <= 303) || (index == 322)
      || (index == 345) || (index >= 406 && index <= 409) || (index >= 444 && index <= 448) || (index >= 477 && index <= 481)
      || (index >= 503 && index <= 504) || (index >= 506 && index <= 507) || (index >= 524 && index <= 526)
      || (index >= 528 && index <= 529) || (index >= 544 && index <= 548) || (index >= 563 && index <= 564) || (index >= 566 && index <= 567)
      || (index == 580) || (index >= 586 && index <= 587) || (index >= 589 && index <= 590) || (index == 595) || (index == 602)
      || (index >= 609 && index <= 613) || (index >= 617 && index <= 618) || (index == 626) || (index >= 633 && index <= 634)
      || (index >= 636 && index <= 637) || (index == 642) || (index >= 657 && index == 658) || (index >= 660 && index <= 661)
      || (index >= 666 && index <= 667) || (index >= 681 && index <= 682) || (index == 685) || (index >= 705 && index <= 709)
      || (index >= 730 && index <= 733) || (index >= 753 && index <= 754) || (index == 905) || (index >= 1166 && index <= 1167)
      || (index >= 1191 && index <= 1192) || (index >= 1221 && index <= 1222) || (index == 1326) || (index >= 1383 && index <= 1384)
      || (index == 1427) || (index == 1451) || (index == 1465) || (index == 1488) || (index == 1505) || (index >= 1509 && index <= 1512)
      || (index == 1525) || (index == 1545) || (index >= 1554 && index <= 1558) || (index == 1570) || (index == 1590)
      || (index >= 1598 && index <= 1603) || (index >= 1626 && index <= 1627) || (index == 1634) || (index >= 1641 && index <= 1647)
      || (index == 1673) || (index >= 1687 && index <= 1692) || (index == 1714) || (index >= 1730 && index <= 1733) || (index == 1749)
      || (index >= 1766 && index <= 1767) || (index >= 1786 && index <= 1787) || (index >= 1834 && index <= 1835) || (index == 1942)
      || (index >= 2080 && index <= 2081) || (index >= 2091 && index <= 2092) || (index >= 2623 && index <= 2624) || (index == 2850)
      || (index >= 2862 && index <= 2863) || (index == 2877) || (index >= 2924 && index <= 2925) || (index >= 2939 && index <= 2940)
      || (index == 3057) || (index >= 3913 && index <= 3914))
      moveSound = SL.WalkStoneL;
    // Wood
    else if ((index >= 3157 && index <= 3159) || (index >= 3168 && index <= 3171) || (index >= 3179 && index <= 3182)
      || (index >= 3199 && index <= 3202) || (index >= 3220 && index <= 3222) || (index >= 3243 && index <= 3245)
      || (index >= 3266 && index <= 3268) || (index >= 3286 && index <= 3289) || (index >= 3307 && index <= 3309)
      || (index >= 3329 && index <= 3331) || (index >= 3352 && index <= 3354) || (index >= 3374 && index <= 3376)
      || (index >= 3387 && index <= 3389) || (index >= 3502 && index <= 3503) || (index >= 3522 && index <= 3523)
      || (index >= 3532 && index <= 3538) || (index >= 3545 && index <= 3551) || (index >= 3559 && index <= 3566)
      || (index >= 3581 && index <= 3582) || (index >= 3591 && index <= 3592) || (index >= 3598 && index <= 3600)
      || (index >= 3615 && index <= 3617) || (index >= 3634 && index <= 3636) || (index == 3659) || (index == 3715)
      || (index >= 3727 && index <= 3728) || (index >= 3738 && index <= 3739) || (index == 3745) || (index == 3749)
      || (index == 3750) || (index >= 3754 && index <= 3757) || (index >= 3774 && index <= 3776)
      || (index >= 3797 && index <= 3798) || (index >= 3816 && index <= 3818) || (index >= 3831 && index <= 3832)
      || (index >= 3836 && index <= 3837) || (index >= 3850 && index <= 3853) || (index >= 3866 && index <= 3868))
      moveSound = SL.WalkWoodL;
    // Water
    else if ((index == 3744) || (index >= 3761 && index <= 3769) || (index >= 3782 && index <= 3790)
      || (index >= 3804 && index <= 3809) || (index >= 3824 && index <= 3830) || (index >= 3842 && index <= 3848)
      || (index >= 3860 && index <= 3865) || (index == 3870) || (index >= 3875 && index <= 3881) || (index == 3884)
      || (index >= 3890 && index <= 3894))
      moveSound = SL.WalkWaterL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 330 Sand/Tilesc*
  if (backIndex == 330 || backIndex == 230)
  {   // Stone
    if ((index >= 1029 && index <= 1031) || (index >= 1048 && index <= 1053) || (index >= 1070 && index <= 1075)
      || (index >= 1094 && index <= 1100) || (index >= 1119 && index <= 1120) || (index >= 1124 && index <= 1127)
      || (index >= 1145 && index <= 1146) || (index >= 1150 && index <= 1154) || (index >= 1177 && index <= 1183)
      || (index >= 1185 && index <= 1187) || (index >= 1212 && index <= 1218) || (index >= 1242 && index <= 1246)
      || (index >= 1269 && index <= 1271) || (index >= 1273 && index <= 1274) || (index >= 1301 && index <= 1305)
      || (index >= 1308 && index <= 1310) || (index == 1338) || (index >= 1340 && index <= 1342)
      || (index >= 1345 && index <= 1351) || (index >= 1378 && index <= 1386) || (index >= 1388 && index <= 1393)
      || (index >= 1414 && index <= 1422) || (index >= 1426 && index <= 1429) || (index >= 1444 && index <= 1451)
      || (index >= 1458 && index <= 1459) || (index >= 1475 && index <= 1479) || (index >= 1500 && index <= 1503)
      || (index >= 1520 && index <= 1521) || (index >= 1538 && index <= 1539) || (index >= 1554 && index <= 1557)
      || (index >= 1576 && index <= 1581) || (index >= 1600 && index <= 1603) || (index >= 1616 && index <= 1618)
      || (index >= 1629 && index <= 1631) || (index == 2135) || (index >= 2150 && index <= 2151)
      || (index >= 2163 && index <= 2164) || (index == 2177) || (index >= 2179 && index <= 2180) || (index == 2199)
      || (index == 2201) || (index >= 2205 && index <= 2208) || (index >= 2219 && index <= 2220)
      || (index >= 2227 && index <= 2228) || (index == 2230) || (index == 2249) || (index == 2377) || (index == 2493)
      || (index >= 2600 && index <= 2601) || (index >= 2603 && index <= 2604) || (index >= 2656 && index <= 2659)
      || (index >= 2677 && index <= 2681) || (index >= 2700 && index <= 2703) || (index >= 2717 && index <= 2718)
      || (index >= 2723 && index <= 2726) || (index == 2751) || (index == 2793) || (index >= 3020 && index <= 3022)
      || (index >= 3052 && index <= 3053) || (index >= 3088 && index <= 3089) || (index >= 3115 && index <= 3116)
      || (index >= 3124 && index <= 3125) || (index >= 3131 && index <= 3133) || (index >= 3159 && index <= 3161)
      || (index >= 3168 && index <= 3171) || (index >= 3192 && index <= 3193) || (index >= 3197 && index <= 3199)
      || (index == 3232) || (index >= 4008 && index <= 4009) || (index >= 4030 && index <= 4033)
      || (index >= 4055 && index <= 4056) || (index >= 4078 && index <= 4079) || (index >= 4101 && index <= 4104)
      || (index >= 4121 && index <= 4123) || (index >= 4129 && index <= 4130) || (index >= 4140 && index <= 4141)
      || (index >= 4149 && index <= 4150) || (index >= 4159 && index <= 4160) || (index == 4175) || (index == 4192)
      || (index >= 4212 && index <= 4215) || (index >= 4299 && index <= 4361) || (index >= 4610 && index <= 4734)
      || (index >= 4775 && index <= 4810))
      moveSound = SL.WalkStoneL;
    // Cave
    else if ((index >= 4364 && index <= 4607) || (index >= 4737 && index <= 4772) || (index >= 4813 && index <= 4906))
      moveSound = SL.WalkCaveL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 345 Snow/Tilesc*!
  if (backIndex == 345 || backIndex == 245)
  {   // Stone
    if ((index >= 988 && index <= 1116) || (index == 1721) || (index == 1781) || (index == 1801) || (index >= 3051 && index <= 3282)
      || (index >= 3296 && index <= 3297) || (index >= 3300 && index <= 3301) || (index >= 3994 && index <= 4301)
      || (index >= 6224 && index <= 6291) || (index >= 6293 && index <= 6308) || (index >= 6313 && index <= 6329)
      || (index >= 6336 && index <= 6354) || (index >= 6362 && index <= 6382) || (index >= 6392 && index <= 6414)
      || (index >= 6424 && index <= 6448) || (index >= 6458 && index <= 6484) || (index >= 6494 && index <= 6522)
      || (index >= 6532 && index <= 6561) || (index >= 6571 && index <= 6600) || (index >= 6610 && index <= 6639)
      || (index >= 6649 && index <= 6677) || (index >= 6687 && index <= 6712) || (index >= 6720 && index <= 6747)
      || (index >= 6754 && index <= 6782) || (index >= 6787 && index <= 7941))
      moveSound = SL.WalkStoneL;
    // Snow
    else if ((index >= 2 && index <= 984) || (index >= 2123 && index <= 2453) || (index >= 2456 && index <= 2458)
      || (index >= 2462 && index <= 2465) || (index >= 2468 && index <= 2736) || (index >= 3287 && index <= 3295)
      || (index >= 3298 && index <= 3299) || (index >= 3302 && index <= 3306))
      moveSound = SL.WalkSnowL;
    // Wood
    else if ((index >= 2454 && index <= 2455) || (index >= 2459 && index <= 2461) || (index >= 2466 && index <= 2467)
      || (index >= 4590 && index <= 4593) || (index >= 4611 && index <= 4613) || (index == 4646) || (index == 4655)
      || (index == 4665) || (index == 4668) || (index == 4675) || (index >= 4687 && index <= 4688) || (index >= 4693 && index <= 4694)
      || (index >= 4762 && index <= 4713) || (index >= 4729 && index <= 4732) || (index >= 4750 && index <= 4751))
      moveSound = SL.WalkWoodL;
    // Cave
    else if ((index >= 3311 && index <= 3854) || (index >= 4304 && index <= 4519))
      moveSound = SL.WalkCaveL;
    // Room
    if ((index == 6292) || (index >= 6309 && index <= 6312) || (index >= 6330 && index <= 6335)
      || (index >= 6355 && index <= 6361) || (index >= 6383 && index <= 6391) || (index >= 6415 && index <= 6423)
      || (index >= 6449 && index <= 6457) || (index >= 6485 && index <= 6493) || (index >= 6523 && index <= 6531)
      || (index >= 6562 && index <= 6570) || (index >= 6601 && index <= 6609) || (index >= 6640 && index <= 6648)
      || (index >= 6678 && index <= 6686) || (index >= 6713 && index <= 6719) || (index >= 6748 && index <= 6753)
      || (index >= 6783 && index <= 6785))
      moveSound = SL.WalkRoomL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 360 Forest/Tilesc*!
  if (backIndex == 360 || backIndex == 260)
  {   // Stone
    if ((index >= 120 && index <= 140) || (index >= 141 && index <= 156) || (index >= 158 && index <= 179)
      || (index >= 184 && index <= 195) || (index >= 197 && index <= 198) || (index >= 200 && index <= 205)
      || (index >= 210 && index <= 219) || (index >= 222 && index <= 231) || (index >= 233 && index <= 240)
      || (index >= 248 && index <= 255) || (index >= 257 && index <= 263) || (index >= 271 && index <= 278)
      || (index >= 282 && index <= 286) || (index >= 289 && index <= 290) || (index >= 294 && index <= 299)
      || (index == 306) || (index >= 642 && index <= 646) || (index >= 666 && index <= 670)
      || (index == 677) || (index >= 687 && index <= 692) || (index >= 698 && index <= 700)
      || (index >= 709 && index <= 718) || (index >= 720 && index <= 724) || (index >= 732 && index <= 742)
      || (index >= 744 && index <= 748) || (index >= 755 && index <= 762) || (index >= 768 && index <= 771)
      || (index >= 775 && index <= 790) || (index >= 792 && index <= 812) || (index >= 817 && index <= 833)
      || (index >= 836 && index <= 845) || (index >= 847 && index <= 853) || (index >= 855 && index <= 856)
      || (index >= 1645 && index <= 1786) || (index >= 2041 && index <= 9868))
      moveSound = SL.WalkStoneL;
    // Lawn
    else moveSound = SL.WalkLawnL;
  }
  // #endregion


  // #region Back 301 Tiles30c*
  if (backIndex == 301 || backIndex == 201)
  {   // Lawn
    if ((index == 14) || (index == 19) || (index == 24) || (index == 29) || (index >= 395 && index <= 403)
      || (index >= 410 && index <= 418) || (index >= 425 && index <= 433) || (index >= 440 && index <= 448)
      || (index >= 455 && index <= 463) || (index >= 470 && index <= 478) || (index >= 485 && index <= 493)
      || (index >= 500 && index <= 508) || (index >= 515 && index <= 523) || (index >= 530 && index <= 538)
      || (index >= 605 && index <= 613) || (index >= 620 && index <= 628) || (index >= 635 && index <= 643)
      || (index >= 650 && index <= 658) || (index >= 720 && index <= 724) || (index >= 735 && index <= 748)
      || (index >= 900 && index <= 904) || (index >= 915 && index <= 928) || (index >= 995 && index <= 1003))
      moveSound = SL.WalkLawnL;
    // Stone
    else if ((index >= 30 && index <= 34) || (index >= 45 && index <= 48) || (index >= 120 && index <= 124)
      || (index >= 135 && index <= 138) || (index >= 185 && index <= 193) || (index >= 200 && index <= 208)
      || (index >= 330 && index <= 334) || (index >= 345 && index <= 348) || (index >= 450 && index <= 454)
      || (index >= 465 && index <= 468) || (index >= 1025 && index <= 1033))
      moveSound = SL.WalkStoneL;
    // Rough
    else if ((index >= 600 && index <= 604) || (index >= 615 && index <= 618) || (index >= 780 && index <= 784)
      || (index >= 795 && index <= 814) || (index >= 825 && index <= 844) || (index >= 855 && index <= 868)
      || (index >= 930 && index <= 934) || (index >= 945 && index <= 958))
      moveSound = SL.WalkRoughL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  //316 = wood/Tiles30c
  //331 = sand/Tiles30c
  // #region Back 346 Snow/Tiles30c*
  if (backIndex == 346 || backIndex == 246)
  {   // Snow
    if ((index >= 0 && index <= 58) || (index >= 75 && index <= 94) || (index >= 105 && index <= 118)
      || (index >= 125 && index <= 133) || (index >= 140 && index <= 148) || (index >= 165 && index <= 178))
      moveSound = SL.WalkSnowL;
    // Lawn
    else if ((index >= 210 && index <= 214) || (index >= 225 && index <= 238))
      moveSound = SL.WalkLawnL;
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 361 Forest/Tiles30c*
  if (backIndex == 361 || backIndex == 261)
  {   // Lawn
    if ((index >= 0 && index <= 4) || (index >= 14 && index <= 34) || (index >= 45 && index <= 64)
      || (index >= 75 && index <= 124) || (index >= 135 && index <= 138) || (index >= 150 && index <= 154)
      || (index >= 165 && index <= 179))
      moveSound = SL.WalkLawnL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion


  // #region Back 302 Tiles5c*
  if (backIndex == 302 || backIndex == 202)
  {   // Lawn
    if ((index >= 10330 && index <= 10348) || (index >= 10405 && index <= 10408) || (index >= 10450 && index <= 10454)
      || (index >= 10460 && index <= 10463) || (index >= 10505 && index <= 10508) || (index >= 10550 && index <= 10554)
      || (index >= 10560 && index <= 10563) || (index >= 10575 && index <= 10580) || (index >= 10582 && index <= 10585)
      || (index >= 10588 && index <= 10593) || (index >= 10705 && index <= 10708) || (index >= 10750 && index <= 10754)
      || (index >= 10760 && index <= 10763) || (index >= 10905 && index <= 10908) || (index >= 10915 && index <= 10954)
      || (index >= 10960 && index <= 10973) || (index >= 11205 && index <= 11208) || (index >= 11215 && index <= 11254)
      || (index >= 11260 && index <= 11273) || (index >= 11400 && index <= 11404) || (index >= 11410 && index <= 11423)
      || (index >= 11455 && index <= 11473) || (index >= 11500 && index <= 11523) || (index >= 11550 && index <= 11573)
      || (index >= 12555 && index <= 12558) || (index >= 12565 && index <= 12573) || (index >= 12605 && index <= 12608)
      || (index >= 12615 && index <= 12623) || (index >= 12655 && index <= 12658) || (index >= 12665 && index <= 12673)
      || (index >= 12705 && index <= 12708) || (index >= 12715 && index <= 12723))
      moveSound = SL.WalkLawnL;
    // Rough
    else if ((index >= 10205 && index <= 10208) || (index >= 10215 && index <= 10223) || (index >= 10250 && index <= 10254)
      || (index >= 10260 && index <= 10273) || (index >= 11005 && index <= 11008) || (index >= 11015 && index <= 11054)
      || (index >= 11060 && index <= 11073) || (index >= 11405 && index <= 11408) || (index >= 11450 && index <= 11454))
      moveSound = SL.WalkRoughL;
    // Stone
    else if ((index >= 10 && index <= 14) || (index >= 10085 && index <= 10093) || (index >= 10105 && index <= 10108)
      || (index >= 10150 && index <= 10154) || (index >= 10160 && index <= 10173) || (index >= 10605 && index <= 10608)
      || (index >= 10615 && index <= 10623) || (index >= 10650 && index <= 10654) || (index >= 10660 && index <= 10673)
      || (index >= 11305 && index <= 11308) || (index >= 11315 && index <= 11354) || (index >= 11360 && index <= 11373)
      || (index >= 11479 && index <= 11481) || (index >= 11486 && index <= 11488) || (index >= 11491 && index <= 11493)
      || (index >= 12000 && index <= 12023) || (index >= 12400 && index <= 12423) || (index >= 12550 && index <= 12554)
      || (index >= 12560 && index <= 12563) || (index >= 12600 && index <= 12604) || (index >= 12610 && index <= 12613)
      || (index >= 12650 && index <= 12654) || (index >= 12660 && index <= 12663) || (index >= 12700 && index <= 12704)
      || (index >= 12710 && index <= 12713) || (index >= 12850 && index <= 12874) || (index >= 12995 && index <= 13049)
      || (index >= 14625 && index <= 14678) || (index >= 15045 && index <= 15143) || (index >= 15780 && index <= 15849)
      || (index >= 16075 && index <= 16077))
      moveSound = SL.WalkStoneL;
    // Cave
    else if ((index >= 10040 && index <= 10078) || (index >= 11375 && index <= 11378) || (index >= 11380 && index <= 11383)
      || (index >= 11387 && index <= 11390) || (index >= 11394 && index <= 11396) || (index >= 11425 && index <= 11449)
      || (index >= 11475 && index <= 11478) || (index >= 11482 && index <= 11485) || (index >= 11489 && index <= 11490)
      || (index >= 11494 && index <= 11499) || (index >= 11525 && index <= 11549) || (index >= 11575 && index <= 11973)
      || (index >= 12150 && index <= 12173) || (index >= 12450 && index <= 12473) || (index >= 12495 && index <= 12499)
      || (index >= 12525 && index <= 12549) || (index >= 12575 && index <= 12599) || (index >= 12625 && index <= 12649)
      || (index >= 12675 && index <= 12699) || (index >= 12725 && index <= 12749) || (index >= 12775 && index <= 12799)
      || (index >= 12825 && index <= 12849) || (index >= 12875 && index <= 12899) || (index >= 12925 && index <= 12993)
      || (index >= 13075 && index <= 13098) || (index >= 13125 && index <= 13129) || (index >= 13131 && index <= 13136)
      || (index >= 13139 && index <= 13176) || (index >= 13180 && index <= 13188) || (index >= 13191 && index <= 13229)
      || (index >= 13232 && index <= 13239) || (index >= 13242 && index <= 13245) || (index >= 13247 && index <= 13275)
      || (index >= 13279 && index <= 13281) || (index >= 14825 && index <= 15035) || (index >= 15225 && index <= 15779))
      moveSound = SL.WalkCaveL;
    // Wood
    else if ((index >= 25 && index <= 29) || (index == 11379) || (index >= 11384 && index <= 11386)
      || (index >= 11391 && index <= 11393) || (index >= 11397 && index <= 11399) || (index == 13130)
      || (index >= 13137 && index <= 13138) || (index >= 13177 && index <= 13179) || (index >= 13189 && index <= 13190)
      || (index >= 13230 && index <= 13231) || (index >= 13240 && index <= 13241) || (index == 13246)
      || (index >= 13276 && index <= 13278) || (index == 13282) || (index >= 13780 && index <= 14434)
      || (index >= 15178 && index <= 15179) || (index >= 15182 && index <= 15185) || (index >= 15188 && index <= 15191)
      || (index >= 15194 && index <= 15195) || (index >= 15875 && index <= 16041) || (index >= 16080 && index <= 16083)
      || (index >= 16145 && index <= 16188))
      moveSound = SL.WalkWoodL;
    // Room
    else if ((index >= 12175 && index <= 12199) || (index >= 12232 && index <= 10249) || (index >= 12275 && index <= 12299)
      || (index >= 12325 && index <= 12349) || (index >= 12375 && index <= 12399) || (index >= 12425 && index <= 12449)
      || (index >= 12475 && index <= 12488) || (index >= 12800 && index <= 12824) || (index >= 12900 && index <= 12924)
      || (index >= 15037 && index <= 15040) || (index >= 16085 && index <= 16088))
      moveSound = SL.WalkRoomL;
    // Water
    else if ((index >= 0 && index <= 4) || (index >= 30 && index <= 34))
      moveSound = SL.WalkWaterL;
    // Ground
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 317 Wood/Tiles5c*
  if (backIndex == 317 || backIndex == 217)
  {   // Cave
    if (index >= 0 && index <= 4)
      moveSound = SL.WalkCaveL;
    // Stone
    else moveSound = SL.WalkStoneL;
  }
  // #endregion
  // #region Back 332 Sand/Tiles5c*
  if (backIndex == 332 || backIndex == 232)
  {   // Stone
    if (index >= 5 && index <= 9)
      moveSound = SL.WalkStoneL;
    // Cave
    else if (index >= 0 && index <= 4)
      moveSound = SL.WalkCaveL;
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 347 Snow/Tiles5c*
  if (backIndex == 347 || backIndex == 247)
  {   // Cave
    if ((index >= 21 && index <= 24) || (index >= 35 && index <= 44))
      moveSound = SL.WalkCaveL;
    // Stone
    else if ((index >= 0 && index <= 4) || (index >= 15 && index <= 19))
      moveSound = SL.WalkStoneL;
    // Water
    else if ((index >= 30 && index <= 34))
      moveSound = SL.WalkWaterL;
    // Snow
    else if ((index >= 30 && index <= 34))
      moveSound = SL.WalkSnowL;
    else moveSound = SL.WalkGroundL;
  }
  // #endregion
  // #region Back 362 Forest/Tiles5c*
  if (backIndex == 362 || backIndex == 262)
  {   // Stone
    if (index >= 0 && index <= 36)
      moveSound = SL.WalkStoneL;
    else moveSound = SL.WalkGroundL;
  }
  // #endregion

  // #region Back 304 Housesc || 219 Wood | 234 Sand | 249 Snow | 264 Forest
  if (backIndex == 304 || backIndex == 204)
  {
    moveSound = SL.WalkGroundL;
  }
  // #endregion

  index = (m.middleImage[i] & 0x7FFF) - 1; // Middle
  // #region Middle 310 smObjectsc
  if (midIndex == 310 || backIndex == 210 && midImage != 0)
    // Wood
    if (index >= 7755 && index <= 8300)
      moveSound = SL.WalkWoodL;
    else moveSound = SL.WalkGroundL;
  // #endregion
  // #region Middle 325 Wood/smObjectsc
  if (midIndex == 325 || backIndex == 225 && midImage != 0)
    // Stone
    if ((index >= 480 && index <= 624) || (index >= 2459 && index <= 2519))
      moveSound = SL.WalkStoneL;
    // Room
    else if (index >= 2539 && index <= 2623)
      moveSound = SL.WalkRoomL;
    else moveSound = SL.WalkGroundL;
  // #endregion
  // #region Middle 340 Sand/smObjectsc
  if (midIndex == 340 || backIndex == 240 && midImage != 0)
    // Stone
    if ((index >= 0 && index <= 0) || (index >= 0 && index <= 0))
      moveSound = SL.WalkGroundL;
  // #endregion
  // #region Middle 355 Snow/smObjectsc
  if (midIndex == 355 || backIndex == 255 && midImage != 0)
    // Wood
    if ((index >= 939 && index <= 942) || (index >= 960 && index <= 966) || (index >= 986 && index <= 994)
      || (index >= 1013 && index <= 1021) || (index >= 1044 && index <= 1052) || (index >= 1073 && index <= 1081)
      || (index >= 1104 && index <= 1112) || (index >= 1130 && index <= 1136) || (index >= 1156 && index <= 1161)
      || (index >= 1178 && index <= 1182) || (index >= 1200 && index <= 1203) || (index >= 1219 && index <= 1220)
      || (index >= 1302 && index <= 1303) || (index >= 1319 && index <= 1322) || (index >= 1337 && index <= 1341)
      || (index >= 1358 && index <= 1364) || (index >= 1380 && index <= 1386) || (index >= 1404 && index <= 1412)
      || (index >= 1429 && index <= 1437) || (index >= 1456 && index <= 1464) || (index >= 1480 && index <= 1489)
      || (index >= 1509 && index <= 1516) || (index >= 1532 && index <= 1538) || (index >= 1555 && index <= 1558)
      || (index >= 9681 && index <= 9952))
      moveSound = SL.WalkWoodL;
    // Stone
    else if ((index >= 4596 && index <= 4602) || (index >= 4638 && index <= 4644) || (index >= 4670 && index <= 4676)
      || (index >= 6383 && index <= 6387))
      moveSound = SL.WalkStoneL;
    else moveSound = SL.WalkSnowL;
  // #endregion
  // #region Middle 370 Forest/smObjectsc
  if (midIndex == 370 || backIndex == 270 && midImage != 0)
    // Stone
    moveSound = SL.WalkStoneL;
  // #endregion
  // #endregion Wemade Mir 3
  return moveSound;
}

