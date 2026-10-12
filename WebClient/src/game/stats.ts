// Port of the desktop client's user stat calculation: UserObject.RefreshStats and everything it calls
// (Client/MirObjects/UserObject.cs), BaseStat.Calculate and the BaseStats(job) default table (Shared/BaseStats.cs),
// the Stats indexer semantics (Shared/Data/Stat.cs: missing = 0, setting 0 removes the key) and the UserItem /
// Awake / ItemSets helpers it uses (Shared/Data/ItemData.cs). Methods keep the C# names, order and structure.
//
// Differences from the requested shape (inputs the C# reads that were not in the brief, all optional):
//  - `ridingMount` (PlayerObject.RidingMount): RefreshSocketStats skips a mount's sockets unless riding. Default false.
//  - `hp` (UserObject.HP): only for PercentHealth. Default 0.
// Not ported: SetLibraries / SetEffects / GameScene.Redraw (presentation). An item whose ItemInfo has not arrived yet
// (C# would throw a NullReferenceException on temp.Info) is skipped and counted in `missing`.
// `baseStats: null` mirrors `CoreStats = new BaseStats(0)`: the WARRIOR table, evaluated with the user's own class,
// until S.BaseStatsInfo replaces it (GameScene.BaseStatsInfo).
// Integer arithmetic wraps like unchecked C# `int` (`| 0`, Math.imul); float->int casts follow .NET 8 x64
// (cvttss2si: out of range / NaN -> int.MinValue).

import type { BaseStat, BaseStats, ItemInfo, UserItem } from "../protocol/server.ts";
import { BuffType, EquipmentSlot, ItemSet, ItemType, MirClass, SpecialItemMode, Spell, Stat, StatFormula } from "../protocol/enums.generated.ts";
import type { ItemInfos } from "./items.ts";
import type { UserBuff } from "./buffs.ts";

export interface StatsInput {
  cls: number; level: number;
  inventory: readonly (UserItem | null)[];
  equipment: readonly (UserItem | null)[];
  infos: ItemInfos;                     // ItemInfo lookup + GetRealItem
  magics: readonly { Spell: number; Level: number }[];
  buffs: readonly UserBuff[];           // the user's buffs (BuffsDialog.Buffs)
  guildBuffStats: Record<number, number> | null; // summed stats of the active guild buffs (GuildDialog), or null
  baseStats: BaseStats | null;          // S.BaseStatsInfo, or null before it arrives
  ridingMount?: boolean;                // PlayerObject.RidingMount (RefreshSocketStats)
  hp?: number;                          // UserObject.HP (PercentHealth)
}

/** ItemSets (Shared/Data/ItemData.cs). */
export interface ItemSets { Set: number; Type: number[]; Count: number }

export interface UserStats {
  stats: Record<number, number>;
  attackSpeed: number;
  currentBagWeight: number;
  currentWearWeight: number;
  currentHandWeight: number;
  light: number;
  weapon: number;
  weaponEffect: number;
  armour: number;
  wingEffect: number;
  mountType: number;
  itemMode: number;         // SpecialItemMode flags
  fastRun: boolean;
  /** RefreshBuffs sets Sprint = true on SwiftFeet but RefreshStats never clears it: OR this into a persisted flag. */
  sprint: boolean;
  transformType: number;
  itemSets: ItemSets[];
  mirSet: number[];         // EquipmentSlot values
  percentHealth: number;
  /** Equipped/inventory items (incl. socketed) skipped because their ItemInfo is unknown. */
  missing: number;
}

const AwakeType = { None: 0, DC: 1, MC: 2, SC: 3, AC: 4, MAC: 5, HPMP: 6 } as const;
const FISHING_ROD_SHAPES = [49, 50]; // Globals.FishingRodShapes
const INT_MAX = 2147483647;

const f = Math.fround;
const i32 = (x: number) => x | 0;
const idiv = (a: number, b: number) => Math.trunc(a / b) | 0;
/** (int)someFloat on .NET 8 x64 (cvttss2si / cvttsd2si). */
const floatToInt = (x: number) => (Number.isNaN(x) || x >= 2147483648 || x < -2147483648) ? -2147483648 : Math.trunc(x);

// Stats indexer
function get(s: Record<number, number>, k: number): number { return s[k] ?? 0; }
function set(s: Record<number, number>, k: number, v: number): void {
  if (v === 0) { delete s[k]; return; }
  s[k] = v;
}
function add(s: Record<number, number>, k: number, v: number): void { set(s, k, i32(get(s, k) + v)); }
// Stats.Add
function addStats(s: Record<number, number>, other: Record<number, number>): void {
  for (const key of Object.keys(other)) { const k = Number(key); add(s, k, other[k]!); }
}

// UserItem.Weight
function itemWeight(item: UserItem, info: ItemInfo): number {
  return (info.Type === ItemType.Amulet || info.Type === ItemType.Bait) ? info.Weight : i32(info.Weight * item.Count);
}
// Awake.GetAwakeValue (byte total, wraps)
function awakeValue(item: UserItem): number {
  let total = 0;
  for (const v of item.AwakeLevels) total = (total + v) & 0xFF;
  return total;
}
// Awake.GetDC / GetMC / GetSC / GetAC / GetMAC / GetHPMP
function awake(item: UserItem, type: number): number { return item.AwakeType === type ? awakeValue(item) : 0; }
// ItemInfo.IsFishingRod
function isFishingRod(info: ItemInfo): boolean { return FISHING_ROD_SHAPES.includes(info.Shape); }

// ItemSets.Amount
function setAmount(s: number): number {
  switch (s) {
    case ItemSet.Mundane: case ItemSet.NokChi: case ItemSet.TaoProtect:
    case ItemSet.Whisker1: case ItemSet.Whisker2: case ItemSet.Whisker3: case ItemSet.Whisker4: case ItemSet.Whisker5:
      return 2;
    case ItemSet.RedOrchid: case ItemSet.RedFlower: case ItemSet.Smash: case ItemSet.HwanDevil: case ItemSet.Purity:
    case ItemSet.FiveString: case ItemSet.Bone: case ItemSet.Bug: case ItemSet.DarkGhost:
      return 3;
    case ItemSet.Recall:
      return 4;
    case ItemSet.Spirit: case ItemSet.WhiteGold: case ItemSet.WhiteGoldH: case ItemSet.RedJade: case ItemSet.RedJadeH:
    case ItemSet.Nephrite: case ItemSet.NephriteH: case ItemSet.Hyeolryong: case ItemSet.Monitor: case ItemSet.Oppressive:
    case ItemSet.Paeok: case ItemSet.Sulgwan: case ItemSet.BlueFrostH: case ItemSet.BlueFrost:
      return 5;
    default:
      return 0;
  }
}
// ItemSets.SetComplete
function setComplete(s: ItemSets): boolean { return s.Count >= setAmount(s.Set); }

// BaseStat.Calculate (float arithmetic, then (int)Math.Min(float, float))
export function calculateBaseStat(stat: BaseStat, job: number, level: number): number {
  const gain = f(stat.Gain), gainRate = f(stat.GainRate), base = f(stat.Base), lv = f(level);
  if (gain === 0) return stat.Base;
  const cap = f(stat.Max > 0 ? stat.Max : INT_MAX);
  let v: number;
  if (stat.FormulaType === StatFormula.Health) {
    switch (job) {
      case MirClass.Warrior: // Base + (level / Gain + GainRate + level / 20F) * level
        v = f(base + f(f(f(f(lv / gain) + gainRate) + f(lv / 20)) * lv));
        break;
      default: // Base + (level / Gain + GainRate) * level
        v = f(base + f(f(f(lv / gain) + gainRate) * lv));
        break;
    }
  } else if (stat.FormulaType === StatFormula.Mana) {
    switch (job) {
      case MirClass.Wizard: // Base + ((level / Gain + 2F) * 2.2F * level) + (level * GainRate)
        v = f(f(base + f(f(f(f(lv / gain) + 2) * f(2.2)) * lv)) + f(lv * gainRate));
        break;
      case MirClass.Taoist: // (Base + level / Gain * 2.2F * level) + (level * GainRate)
        v = f(f(base + f(f(f(lv / gain) * f(2.2)) * lv)) + f(lv * gainRate));
        break;
      default: // Base + (level * Gain) + (level * GainRate)
        v = f(f(base + f(lv * gain)) + f(lv * gainRate));
        break;
    }
  } else {
    switch (stat.FormulaType) {
      case StatFormula.Weight: // Base + ((level / Gain) * level)
        v = f(base + f(f(lv / gain) * lv));
        break;
      default: // Base + (level / Gain)
        v = f(base + f(lv / gain));
        break;
    }
  }
  return floatToInt(Math.min(cap, v));
}

// new BaseStats(MirClass job)
export function defaultBaseStats(job: number): BaseStats {
  const s = (Type: number, FormulaType: number, Base: number, Gain: number, GainRate: number, Max = 0): BaseStat =>
    ({ Type, FormulaType, Base, Gain: f(Gain), GainRate: f(GainRate), Max });
  const { Health, Mana, Weight } = StatFormula, St = StatFormula.Stat;
  let stats: BaseStat[] = [];
  switch (job) {
    case MirClass.Warrior:
      stats = [s(Stat.HP, Health, 14, 4, 4.5), s(Stat.MP, Mana, 11, 3.5, 0), s(Stat.BagWeight, Weight, 50, 3, 0),
        s(Stat.WearWeight, Weight, 15, 20, 0), s(Stat.HandWeight, Weight, 12, 13, 0), s(Stat.MinAC, St, 0, 0, 0),
        s(Stat.MaxAC, St, 0, 7, 0), s(Stat.MinDC, St, 0, 5, 0), s(Stat.MaxDC, St, 0, 5, 0), s(Stat.Agility, St, 15, 0, 0),
        s(Stat.Accuracy, St, 5, 0, 0)];
      break;
    case MirClass.Wizard:
      stats = [s(Stat.HP, Health, 14, 15, 1.8), s(Stat.MP, Mana, 13, 5, 0), s(Stat.BagWeight, Weight, 50, 5, 0),
        s(Stat.WearWeight, Weight, 15, 100, 0), s(Stat.HandWeight, Weight, 12, 90, 0), s(Stat.MinDC, St, 0, 7, 0),
        s(Stat.MaxDC, St, 0, 7, 0), s(Stat.MinMC, St, 0, 7, 0), s(Stat.MaxMC, St, 0, 7, 0), s(Stat.Agility, St, 15, 0, 0),
        s(Stat.Accuracy, St, 5, 0, 0)];
      break;
    case MirClass.Taoist:
      stats = [s(Stat.HP, Health, 14, 6, 2.5), s(Stat.MP, Mana, 13, 8, 0), s(Stat.BagWeight, Weight, 50, 4, 0),
        s(Stat.WearWeight, Weight, 15, 50, 0), s(Stat.HandWeight, Weight, 12, 42, 0), s(Stat.MinMAC, St, 0, 12, 0),
        s(Stat.MaxMAC, St, 0, 6, 0), s(Stat.MinDC, St, 0, 7, 0), s(Stat.MaxDC, St, 0, 7, 0), s(Stat.MinSC, St, 0, 7, 0),
        s(Stat.MaxSC, St, 0, 7, 0), s(Stat.Agility, St, 18, 0, 0), s(Stat.Accuracy, St, 5, 0, 0)];
      break;
    case MirClass.Assassin:
      stats = [s(Stat.HP, Health, 14, 4, 3.25), s(Stat.MP, Mana, 11, 5, 0), s(Stat.BagWeight, Weight, 50, 3.5, 0),
        s(Stat.WearWeight, Weight, 15, 33, 0), s(Stat.HandWeight, Weight, 12, 30, 0), s(Stat.MinDC, St, 0, 8, 0),
        s(Stat.MaxDC, St, 0, 8, 0), s(Stat.Agility, St, 20, 0, 0), s(Stat.Accuracy, St, 5, 0, 0)];
      break;
    case MirClass.Archer:
      stats = [s(Stat.HP, Health, 14, 4, 3.25), s(Stat.MP, Mana, 11, 4, 0), s(Stat.BagWeight, Weight, 50, 4, 0),
        s(Stat.WearWeight, Weight, 15, 33, 0), s(Stat.HandWeight, Weight, 12, 30, 0), s(Stat.MinDC, St, 0, 8, 0),
        s(Stat.MaxDC, St, 0, 8, 0), s(Stat.MinMC, St, 0, 8, 0), s(Stat.MaxMC, St, 0, 8, 0), s(Stat.Agility, St, 15, 0, 0),
        s(Stat.Accuracy, St, 8, 0, 0)];
      break;
  }
  const Caps: Record<number, number> = {
    [Stat.MagicResist]: 2, [Stat.PoisonResist]: 6, [Stat.CriticalRate]: 18, [Stat.CriticalDamage]: 10, [Stat.Freezing]: 6,
    [Stat.PoisonAttack]: 6, [Stat.HealthRecovery]: 8, [Stat.SpellRecovery]: 8, [Stat.PoisonRecovery]: 6,
  };
  return { Job: job, Stats: stats, Caps };
}

// UserObject.RefreshStats
export function refreshStats(input: StatsInput): UserStats {
  const { cls: Class, level: Level, infos } = input;
  const CoreStats = input.baseStats ?? defaultBaseStats(0); // UserObject.CoreStats = new BaseStats(0)
  const Stats: Record<number, number> = {}; // Stats.Clear()
  const u: UserStats = {
    stats: Stats, attackSpeed: 0, currentBagWeight: 0, currentWearWeight: 0, currentHandWeight: 0, light: 0,
    weapon: -1, weaponEffect: 0, armour: 0, wingEffect: 0, mountType: -1, itemMode: SpecialItemMode.None, fastRun: false,
    sprint: false, transformType: -1, itemSets: [], mirSet: [], percentHealth: 0, missing: 0,
  };

  // UserObject.RefreshLevelStats
  const refreshLevelStats = () => {
    u.light = 0;
    for (const stat of CoreStats.Stats) set(Stats, stat.Type, calculateBaseStat(stat, Class, Level));
  };

  // UserObject.RefreshBagWeight
  const refreshBagWeight = () => {
    u.currentBagWeight = 0;
    for (let i = 0; i < input.inventory.length; i++) {
      const item = input.inventory[i];
      if (item) {
        const info = infos.get(item.ItemIndex);
        if (!info) { u.missing++; continue; }
        u.currentBagWeight = i32(u.currentBagWeight + itemWeight(item, info));
      }
    }
  };

  // UserObject.RefreshEquipmentStats
  const refreshEquipmentStats = () => {
    u.weapon = -1; u.weaponEffect = 0; u.armour = 0; u.wingEffect = 0; u.mountType = -1;
    u.currentWearWeight = 0; u.currentHandWeight = 0;
    u.itemMode = SpecialItemMode.None; u.fastRun = false;
    u.itemSets = []; u.mirSet = [];

    for (let i = 0; i < input.equipment.length; i++) {
      const temp = input.equipment[i];
      if (!temp) continue;
      const info = infos.get(temp.ItemIndex);
      if (!info) { u.missing++; continue; }

      const realItem = infos.realItem(info, Level, Class);

      if (realItem.Type === ItemType.Weapon || realItem.Type === ItemType.Torch)
        u.currentHandWeight = Math.min(INT_MAX, u.currentHandWeight + itemWeight(temp, info));
      else
        u.currentWearWeight = Math.min(INT_MAX, u.currentWearWeight + itemWeight(temp, info));

      if (temp.CurrentDura === 0 && realItem.Durability > 0) continue;

      if (realItem.Type === ItemType.Armour) { u.armour = realItem.Shape; u.wingEffect = realItem.Effect; }
      if (realItem.Type === ItemType.Weapon) { u.weapon = realItem.Shape; u.weaponEffect = realItem.Effect; }
      if (realItem.Type === ItemType.Mount) u.mountType = realItem.Shape;

      if (isFishingRod(info)) continue;

      addStats(Stats, realItem.Stats);
      addStats(Stats, temp.AddedStats);

      add(Stats, Stat.MinAC, awake(temp, AwakeType.AC));
      add(Stats, Stat.MaxAC, awake(temp, AwakeType.AC));
      add(Stats, Stat.MinMAC, awake(temp, AwakeType.MAC));
      add(Stats, Stat.MaxMAC, awake(temp, AwakeType.MAC));

      add(Stats, Stat.MinDC, awake(temp, AwakeType.DC));
      add(Stats, Stat.MaxDC, awake(temp, AwakeType.DC));
      add(Stats, Stat.MinMC, awake(temp, AwakeType.MC));
      add(Stats, Stat.MaxMC, awake(temp, AwakeType.MC));
      add(Stats, Stat.MinSC, awake(temp, AwakeType.SC));
      add(Stats, Stat.MaxSC, awake(temp, AwakeType.SC));

      add(Stats, Stat.HP, awake(temp, AwakeType.HPMP));
      add(Stats, Stat.MP, awake(temp, AwakeType.HPMP));

      if (realItem.Light > u.light) u.light = realItem.Light;
      if (realItem.Unique !== SpecialItemMode.None) u.itemMode |= realItem.Unique;

      if (realItem.CanFastRun) u.fastRun = true;

      refreshSocketStats(temp, info);

      if (realItem.Set === ItemSet.None) continue;

      const itemSet = u.itemSets.find(s => s.Set === realItem.Set && !s.Type.includes(realItem.Type) && !setComplete(s));
      if (itemSet) {
        itemSet.Type.push(realItem.Type);
        itemSet.Count = (itemSet.Count + 1) & 0xFF;
      } else {
        u.itemSets.push({ Count: 1, Set: realItem.Set, Type: [realItem.Type] });
      }

      //Mir Set
      if (realItem.Set === ItemSet.Mir) {
        if (!u.mirSet.includes(i)) u.mirSet.push(i);
      }
    }

    if ((u.itemMode & SpecialItemMode.Muscle) === SpecialItemMode.Muscle) {
      set(Stats, Stat.BagWeight, Math.imul(get(Stats, Stat.BagWeight), 2));
      set(Stats, Stat.WearWeight, Math.imul(get(Stats, Stat.WearWeight), 2));
      set(Stats, Stat.HandWeight, Math.imul(get(Stats, Stat.HandWeight), 2));
    }
  };

  // UserObject.RefreshSocketStats
  const refreshSocketStats = (equipItem: UserItem, equipInfo: ItemInfo) => {
    if (equipInfo.Type === ItemType.Weapon && isFishingRod(equipInfo)) return;
    if (equipInfo.Type === ItemType.Mount && !input.ridingMount) return;

    for (let i = 0; i < equipItem.Slots.length; i++) {
      const temp = equipItem.Slots[i];
      if (!temp) continue;
      const info = infos.get(temp.ItemIndex);
      if (!info) { u.missing++; continue; }
      const realItem = infos.realItem(info, Level, Class);

      if (realItem.Type === ItemType.Weapon || realItem.Type === ItemType.Torch)
        u.currentHandWeight = Math.min(INT_MAX, u.currentHandWeight + itemWeight(temp, info));
      else
        u.currentWearWeight = Math.min(INT_MAX, u.currentWearWeight + itemWeight(temp, info));

      if (temp.CurrentDura === 0 && realItem.Durability > 0) continue;

      addStats(Stats, realItem.Stats);
      addStats(Stats, temp.AddedStats);

      if (realItem.Light > u.light) u.light = realItem.Light;
      if (realItem.Unique !== SpecialItemMode.None) u.itemMode |= realItem.Unique;
    }
  };

  // UserObject.RefreshItemSetStats
  const refreshItemSetStats = () => {
    let hasSmashSetBonus = false;     // Flag for Smash set AttackSpeed bonus
    let hasPuritySetBonus = false;    // Flag for Purity set Holy bonus
    let hasHwanDevilSetBonus = false; // Flag for HwanDevil set Weight bonuses

    for (const s of u.itemSets) {
      const has = (t: number) => s.Type.includes(t);
      if (s.Set === ItemSet.Smash && has(ItemType.Ring) && has(ItemType.Bracelet)) {
        if (!hasSmashSetBonus) { add(Stats, Stat.AttackSpeed, 2); hasSmashSetBonus = true; }
      }
      if (s.Set === ItemSet.Purity && has(ItemType.Ring) && has(ItemType.Bracelet)) {
        if (!hasPuritySetBonus) { add(Stats, Stat.Holy, 3); hasPuritySetBonus = true; }
      }
      if (s.Set === ItemSet.HwanDevil && has(ItemType.Ring) && has(ItemType.Bracelet)) {
        if (!hasHwanDevilSetBonus) { add(Stats, Stat.WearWeight, 5); add(Stats, Stat.BagWeight, 20); hasHwanDevilSetBonus = true; }
      }
      if (s.Set === ItemSet.DarkGhost && has(ItemType.Necklace) && has(ItemType.Bracelet)) {
        add(Stats, Stat.HP, 25);
      }

      if (!setComplete(s)) continue;

      switch (s.Set) {
        case ItemSet.Mundane: add(Stats, Stat.HP, 50); break;
        case ItemSet.NokChi: add(Stats, Stat.MP, 50); break;
        case ItemSet.TaoProtect: add(Stats, Stat.HP, 30); add(Stats, Stat.MP, 30); break;
        case ItemSet.RedOrchid: add(Stats, Stat.Accuracy, 2); break;
        case ItemSet.RedFlower: add(Stats, Stat.HP, 50); add(Stats, Stat.MP, -50); break;
        case ItemSet.Smash: add(Stats, Stat.MinDC, 1); add(Stats, Stat.MaxDC, 3); break;
        case ItemSet.HwanDevil: add(Stats, Stat.MinMC, 1); add(Stats, Stat.MaxMC, 2); break;
        case ItemSet.Purity: add(Stats, Stat.MinSC, 1); add(Stats, Stat.MaxSC, 2); break;
        case ItemSet.FiveString:
          add(Stats, Stat.HP, floatToInt((get(Stats, Stat.HP) / 100) * 30)); // (int)(((double)HP / 100) * 30)
          add(Stats, Stat.MinAC, 2); add(Stats, Stat.MaxAC, 2);
          break;
        case ItemSet.Spirit: add(Stats, Stat.MinDC, 2); add(Stats, Stat.MaxDC, 5); add(Stats, Stat.AttackSpeed, 2); break;
        case ItemSet.Bone: add(Stats, Stat.MaxAC, 2); add(Stats, Stat.MaxMC, 1); add(Stats, Stat.MaxSC, 1); break;
        case ItemSet.Bug:
          add(Stats, Stat.MaxDC, 1); add(Stats, Stat.MaxMC, 1); add(Stats, Stat.MaxSC, 1); add(Stats, Stat.MaxMAC, 1);
          add(Stats, Stat.PoisonResist, 1);
          break;
        case ItemSet.WhiteGold: add(Stats, Stat.MaxDC, 2); add(Stats, Stat.MaxAC, 2); break;
        case ItemSet.WhiteGoldH: add(Stats, Stat.MaxDC, 3); add(Stats, Stat.HP, 30); add(Stats, Stat.AttackSpeed, 2); break;
        case ItemSet.RedJade: add(Stats, Stat.MaxMC, 2); add(Stats, Stat.MaxMAC, 2); break;
        case ItemSet.RedJadeH: add(Stats, Stat.MaxMC, 2); add(Stats, Stat.MP, 40); add(Stats, Stat.Agility, 2); break;
        case ItemSet.Nephrite: add(Stats, Stat.MaxSC, 2); add(Stats, Stat.MaxAC, 1); add(Stats, Stat.MaxMAC, 1); break;
        case ItemSet.NephriteH:
          add(Stats, Stat.MaxSC, 2); add(Stats, Stat.HP, 15); add(Stats, Stat.MP, 20); add(Stats, Stat.Holy, 1); add(Stats, Stat.Accuracy, 1);
          break;
        case ItemSet.Whisker1: add(Stats, Stat.MaxDC, 1); add(Stats, Stat.BagWeight, 25); break;
        case ItemSet.Whisker2: add(Stats, Stat.MaxMC, 1); add(Stats, Stat.BagWeight, 17); break;
        case ItemSet.Whisker3: add(Stats, Stat.MaxSC, 1); add(Stats, Stat.BagWeight, 17); break;
        case ItemSet.Whisker4: add(Stats, Stat.MaxDC, 1); add(Stats, Stat.BagWeight, 20); break;
        case ItemSet.Whisker5: add(Stats, Stat.MaxDC, 1); add(Stats, Stat.BagWeight, 17); break;
        case ItemSet.Hyeolryong:
          add(Stats, Stat.MaxSC, 2); add(Stats, Stat.HP, 15); add(Stats, Stat.MP, 20); add(Stats, Stat.Holy, 1); add(Stats, Stat.Accuracy, 1);
          break;
        case ItemSet.Monitor: add(Stats, Stat.MagicResist, 1); add(Stats, Stat.PoisonResist, 1); break;
        case ItemSet.Oppressive: add(Stats, Stat.MaxAC, 1); add(Stats, Stat.Agility, 1); break;
        case ItemSet.BlueFrost:
          add(Stats, Stat.MinDC, 1); add(Stats, Stat.MaxDC, 1); add(Stats, Stat.MinMC, 1); add(Stats, Stat.MaxMC, 1);
          add(Stats, Stat.HandWeight, 1); add(Stats, Stat.WearWeight, 2);
          break;
        case ItemSet.BlueFrostH:
          add(Stats, Stat.MinDC, 1); add(Stats, Stat.MaxDC, 2); add(Stats, Stat.MaxMC, 2); add(Stats, Stat.Accuracy, 1); add(Stats, Stat.HP, 50);
          break;
        case ItemSet.DarkGhost: add(Stats, Stat.MP, 25); add(Stats, Stat.AttackSpeed, 2); break;
      }
    }
  };

  // UserObject.RefreshMirSetStats
  const refreshMirSetStats = () => {
    const has = (slot: number) => u.mirSet.includes(slot);
    const E = EquipmentSlot;
    if (u.mirSet.length === 10) {
      add(Stats, Stat.MaxAC, 1); add(Stats, Stat.MaxMAC, 1); add(Stats, Stat.BagWeight, 70); add(Stats, Stat.Luck, 2);
      add(Stats, Stat.AttackSpeed, 2); add(Stats, Stat.HP, 70); add(Stats, Stat.MP, 80); add(Stats, Stat.MagicResist, 6);
      add(Stats, Stat.PoisonResist, 6);
    }
    if (has(E.RingL) && has(E.RingR)) { add(Stats, Stat.MaxMAC, 1); add(Stats, Stat.MaxAC, 1); }
    if (has(E.BraceletL) && has(E.BraceletR)) { add(Stats, Stat.MinAC, 1); add(Stats, Stat.MinMAC, 1); }
    if ((has(E.RingL) || has(E.RingR)) && (has(E.BraceletL) || has(E.BraceletR)) && has(E.Necklace)) {
      add(Stats, Stat.MaxMAC, 1); add(Stats, Stat.MaxAC, 1); add(Stats, Stat.BagWeight, 30); add(Stats, Stat.WearWeight, 17);
    }
    if (has(E.RingL) && has(E.RingR) && has(E.BraceletL) && has(E.BraceletR) && has(E.Necklace)) {
      add(Stats, Stat.MaxMAC, 1); add(Stats, Stat.MaxAC, 1); add(Stats, Stat.BagWeight, 20); add(Stats, Stat.WearWeight, 10);
    }
    if (has(E.Armour) && has(E.Helmet) && has(E.Weapon)) {
      add(Stats, Stat.MaxDC, 2); add(Stats, Stat.MaxMC, 1); add(Stats, Stat.MaxSC, 1); add(Stats, Stat.Agility, 1);
    }
    if (has(E.Armour) && has(E.Boots) && has(E.Belt)) {
      add(Stats, Stat.MaxDC, 1); add(Stats, Stat.MaxMC, 1); add(Stats, Stat.MaxSC, 1); add(Stats, Stat.HandWeight, 17);
    }
    if (has(E.Armour) && has(E.Boots) && has(E.Belt) && has(E.Helmet) && has(E.Weapon)) {
      add(Stats, Stat.MinDC, 1); add(Stats, Stat.MaxDC, 1); add(Stats, Stat.MinMC, 1); add(Stats, Stat.MaxMC, 1);
      add(Stats, Stat.MinSC, 1); add(Stats, Stat.MaxSC, 1); add(Stats, Stat.HandWeight, 17);
    }
  };

  // UserObject.RefreshSkills
  const refreshSkills = () => {
    const spiritSwordLvPlus = [0, 3, 5, 8];
    const slayingLvPlus = [5, 6, 7, 8];
    for (const magic of input.magics) {
      switch (magic.Spell) {
        case Spell.Fencing:
          add(Stats, Stat.Accuracy, magic.Level * 3);
          break;
        case Spell.Slaying:
          add(Stats, Stat.Accuracy, magic.Level);
          add(Stats, Stat.MaxDC, slayingLvPlus[magic.Level] ?? 0); // C# would throw IndexOutOfRange past level 3
          break;
        case Spell.SpiritSword:
          add(Stats, Stat.Accuracy, spiritSwordLvPlus[magic.Level] ?? 0);
          break;
      }
    }
  };

  // UserObject.RefreshBuffs
  const refreshBuffs = () => {
    u.transformType = -1;
    for (const buff of input.buffs) {
      addStats(Stats, buff.Stats);
      switch (buff.Type) {
        case BuffType.SwiftFeet:
          u.sprint = true;
          break;
        case BuffType.Transform:
          if (buff.Paused) continue;
          u.transformType = ((buff.Values[0] ?? 0) << 16) >> 16; // (short)buff.Values[0]
          u.fastRun = true;
          break;
      }
    }
  };

  // UserObject.RefreshGuildBuffs (the caller sums the active GuildDialog.EnabledBuffs' Info.Stats)
  const refreshGuildBuffs = () => {
    if (input.guildBuffStats === null) return;
    addStats(Stats, input.guildBuffStats);
  };

  // UserObject.RefreshStatCaps
  const refreshStatCaps = () => {
    for (const key of Object.keys(CoreStats.Caps)) {
      const k = Number(key);
      set(Stats, k, Math.min(CoreStats.Caps[k]!, get(Stats, k)));
    }

    for (const k of [Stat.HP, Stat.MP, Stat.MinAC, Stat.MaxAC, Stat.MinMAC, Stat.MaxMAC, Stat.MinDC, Stat.MaxDC,
      Stat.MinMC, Stat.MaxMC, Stat.MinSC, Stat.MaxSC]) set(Stats, k, Math.max(0, get(Stats, k)));

    set(Stats, Stat.MinDC, Math.min(get(Stats, Stat.MinDC), get(Stats, Stat.MaxDC)));
    set(Stats, Stat.MinMC, Math.min(get(Stats, Stat.MinMC), get(Stats, Stat.MaxMC)));
    set(Stats, Stat.MinSC, Math.min(get(Stats, Stat.MinSC), get(Stats, Stat.MaxSC)));
  };

  refreshLevelStats();
  refreshBagWeight();
  refreshEquipmentStats();
  refreshItemSetStats();
  refreshMirSetStats();
  refreshSkills();
  refreshBuffs();
  refreshGuildBuffs();

  // SetLibraries(); SetEffects(); -- presentation, not ported

  // Stats[x] += (Stats[x] * Stats[xRatePercent]) / 100;
  const rate = (k: number, pct: number) => add(Stats, k, idiv(Math.imul(get(Stats, k), get(Stats, pct)), 100));
  rate(Stat.HP, Stat.HPRatePercent);
  rate(Stat.MP, Stat.MPRatePercent);
  rate(Stat.MaxAC, Stat.MaxACRatePercent);
  rate(Stat.MaxMAC, Stat.MaxMACRatePercent);

  rate(Stat.MaxDC, Stat.MaxDCRatePercent);
  rate(Stat.MaxMC, Stat.MaxMCRatePercent);
  rate(Stat.MaxSC, Stat.MaxSCRatePercent);
  rate(Stat.AttackSpeed, Stat.AttackSpeedRatePercent);

  refreshStatCaps();

  if (u.light < 3) u.light = 3; // this == User
  u.attackSpeed = i32(1400 - i32(Math.imul(get(Stats, Stat.AttackSpeed), 60) + Math.min(370, Level * 14)));
  if (u.attackSpeed < 550) u.attackSpeed = 550;

  // PercentHealth = (byte)(HP / (float)Stats[Stat.HP] * 100);
  u.percentHealth = floatToInt(f(f(f(input.hp ?? 0) / f(get(Stats, Stat.HP))) * 100)) & 0xFF;

  // GameScene.Scene.Redraw(); -- presentation, not ported
  return u;
}
