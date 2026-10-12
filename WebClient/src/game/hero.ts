// The user's hero: GameScene.Hero (UserHeroObject) + GameScene.HeroSpawnState / HasHero / HeroStorage / MaximumHeroCount,
// kept in step with the hero packets (GameScene.HeroInformation, UpdateHeroSpawnState, HeroHealthChanged, HeroLevelChanged,
// GainHeroExperience, HeroBaseStatsInfo, UnlockHeroAutoPot, SetAutoPotValue, SetAutoPotItem, SetHeroBehaviour,
// ManageHeroes, ChangeHero, HeroCreateRequest, NewHero; MirScene.NewHeroInfo) and the hero's buffs (AddBuff / RemoveBuff /
// PauseBuff with the hero's ObjectID: HeroBuffsDialog). The hero's items (inventory, equipment, belt) and magic are kept
// by heroItems.ts / magic.ts; windows subscribe with onChange().
// The hero on the map is an ordinary player MapObj with `ownerName` set (S.ObjectHero; desktop HeroObject, Race = Hero).

import type { BaseStats, ClientHeroInformation, ClientMagic, ServerPacket, UserItem } from "../protocol/server.ts";
import { HeroBehaviour, HeroSpawnState, MirGridType, Stat } from "../protocol/enums.generated.ts";
import { userBuff, type UserBuff } from "./buffs.ts";

/** UserObject.HeroBeltIdx: hero belt cells 0-1, bag from 2 (TakeBack/TransferHeroItem indices). */
export const HERO_BELT_IDX = 2;

/** S.HeroInformation (UserHeroObject.Load), plus what later packets change. */
export interface HeroInfo {
  objectId: number; name: string; cls: number; gender: number; level: number; hair: number;
  hp: number; mp: number; experience: bigint; maxExperience: bigint;
  /** UserHeroObject.Inventory (belt first) / Equipment: heroItems.ts keeps these in step with the item packets. */
  inventory: (UserItem | null)[]; equipment: (UserItem | null)[];
  /** S.HeroInformation.Magics as received (magic.ts wraps them for casting). */
  magics: ClientMagic[];
  autoPot: boolean; autoHPPercent: number; autoMPPercent: number;
  /** ItemInfo indexes of the auto-pot potions (UserHeroObject.HPItem / MPItem), 0 = none. */
  hpItemIndex: number; mpItemIndex: number;
}

export class Hero {
  /** UserInformation.HasHero, then S.UpdateHeroSpawnState (GameScene.HasHero = State > None). */
  hasHero = false;
  spawnState: number = HeroSpawnState.None;
  behaviour: number = HeroBehaviour.Attack;
  /** null until S.HeroInformation (sent while the hero is summoned). */
  info: HeroInfo | null = null;
  baseStats: BaseStats | null = null;
  buffs: UserBuff[] = [];
  /** HeroManageDialog: the current hero, the storage list and GameScene.MaximumHeroCount. */
  currentHero: ClientHeroInformation | null = null;
  storage: (ClientHeroInformation | null)[] = [];
  maximumCount = 0;
  /** S.HeroCreateRequest (by MirClass) while the NewHeroDialog should be open, else null. */
  createRequest: boolean[] | null = null;
  /** The last S.NewHero result (NewHeroDialog), or null. */
  newHeroResult: number | null = null;
  /** Set by ManageHeroes (open the dialog) / HeroCreateRequest; windows clear it after opening. */
  openManage = false;

  private listeners = new Set<() => void>();

  constructor(private readonly now: () => number, private readonly chat: (text: string) => void) {}

  /** UpdateHeroSpawnState: the info panel / menu / behaviour panel show while State > Unsummoned. */
  get summoned(): boolean { return this.spawnState > HeroSpawnState.Unsummoned; }
  get dead(): boolean { return this.spawnState === HeroSpawnState.Dead; }

  onChange(fn: () => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  changed(): void {
    for (const fn of this.listeners) fn();
  }

  /** Returns true when the packet changed hero state. */
  handle(p: ServerPacket): boolean {
    const h = this.info;
    switch (p.type) {
      case "UserInformation": this.hasHero = p.HasHero; this.behaviour = p.HeroBehaviour; break;
      case "HeroInformation":
        this.info = {
          objectId: p.ObjectID, name: p.Name, cls: p.Class, gender: p.Gender, level: p.Level, hair: p.Hair, hp: p.HP, mp: p.MP,
          experience: p.Experience, maxExperience: p.MaxExperience, inventory: p.Inventory ?? [], equipment: p.Equipment ?? [],
          magics: p.Magics, autoPot: p.AutoPot, autoHPPercent: p.AutoHPPercent, autoMPPercent: p.AutoMPPercent,
          hpItemIndex: Math.max(0, p.HPItemIndex), mpItemIndex: Math.max(0, p.MPItemIndex),
        };
        this.buffs = [];
        break;
      case "UpdateHeroSpawnState":
        this.spawnState = p.State;
        this.hasHero = p.State > HeroSpawnState.None;
        // GameScene.UpdateHeroSpawnState: below Summoned the hero dialogs are disposed (a new HeroInformation rebuilds them)
        if (p.State < HeroSpawnState.Summoned) { this.info = null; this.buffs = []; }
        break;
      case "HeroHealthChanged": if (!h) return false; h.hp = p.HP; h.mp = p.MP; break;
      case "GainHeroExperience":
        if (!h) return false;
        h.experience += BigInt(p.Amount);
        this.chat(`Hero Experience Gained ${p.Amount}`); // ClientTextKeys.HeroExperienceGained
        break;
      case "HeroLevelChanged":
        if (!h) return false;
        h.level = p.Level; h.experience = p.Experience; h.maxExperience = p.MaxExperience;
        break;
      case "HeroBaseStatsInfo": this.baseStats = p.Stats; break;
      case "UnlockHeroAutoPot": if (!h) return false; h.autoPot = true; break;
      case "SetAutoPotValue":
        if (!h) return false;
        if (p.Stat === Stat.HP) h.autoHPPercent = p.Value; else h.autoMPPercent = p.Value;
        break;
      case "SetAutoPotItem":
        if (!h) return false;
        if (p.Grid === MirGridType.HeroHPItem) h.hpItemIndex = Math.max(0, p.ItemIndex); else h.mpItemIndex = Math.max(0, p.ItemIndex);
        break;
      case "SetHeroBehaviour": this.behaviour = p.Behaviour; break;
      case "NewHeroInfo": // MirScene.NewHeroInfo: GameScene.AddHeroInformation at the storage index
        if (p.StorageIndex < 0) return false;
        while (this.storage.length <= p.StorageIndex) this.storage.push(null);
        this.storage[p.StorageIndex] = p.Info;
        break;
      case "ManageHeroes":
        if (p.Heroes) this.storage = [...p.Heroes];
        this.maximumCount = p.MaximumCount;
        this.currentHero = p.CurrentHero;
        this.openManage = true;
        break;
      case "ChangeHero": { // GameScene.ChangeHero: swap the storage entry with the current hero
        const temp = this.storage[p.FromIndex] ?? null;
        this.storage[p.FromIndex] = this.currentHero;
        this.currentHero = temp;
        break;
      }
      case "HeroCreateRequest": this.createRequest = p.CanCreateClass; this.newHeroResult = null; break;
      case "NewHero":
        this.newHeroResult = p.Result;
        if (p.Result === 0 || p.Result === 4 || p.Result === 6 || p.Result === 10) this.createRequest = null; // NewHeroDialog.Hide
        break;
      case "AddBuff": {
        if (!h || p.Buff.ObjectID !== h.objectId) return false;
        const ub = userBuff(p.Buff, this.now());
        const i = this.buffs.findIndex((x) => x.Type === ub.Type);
        if (i >= 0) this.buffs[i] = ub; else this.buffs.push(ub);
        break;
      }
      case "RemoveBuff":
        if (!h || p.ObjectID !== h.objectId) return false;
        this.buffs = this.buffs.filter((b) => b.Type !== p.Type);
        break;
      case "PauseBuff": {
        if (!h || p.ObjectID !== h.objectId) return false;
        const b = this.buffs.find((x) => x.Type === p.Type);
        if (!b || b.Paused === p.Paused) return false;
        b.Paused = p.Paused;
        b.expire += p.Paused ? -this.now() : this.now(); // as game.ts does for the user's buffs
        break;
      }
      default: return false;
    }
    this.changed();
    return true;
  }
}

/** S.NewHero results (GameScene.NewHero message boxes). */
export const NEW_HERO_TEXT: Record<number, string> = {
  0: "Creating new heroes is currently disabled.",
  1: "Your Hero Name is not acceptable.",
  2: "The gender you selected does not exist.\n Contact a GM for assistance.",
  3: "The class you selected does not exist.\n Contact a GM for assistance.",
  4: "You cannot make anymore Heroes.",
  5: "A Character with this name already exists.",
  6: "No bag space.",
  10: "Hero created successfully.",
};
