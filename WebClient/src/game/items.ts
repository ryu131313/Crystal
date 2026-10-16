// Item knowledge the web client needs so far: the ItemInfo table built from S.NewItemInfo (GameScene.ItemInfoList),
// Functions.GetRealItem (class/level variants), and the user's looks + light from equipment
// (UserObject.RefreshEquipmentStats / RefreshStats: Light = brightest equipped item, at least 3).

import type { ItemInfo, UserItem } from "../protocol/server.ts";
import { ItemType, RequiredType } from "../protocol/enums.generated.ts";

export class ItemInfos {
  /** Insertion order matters: GetRealItem scans ItemInfoList in order. */
  private readonly list: ItemInfo[] = [];
  private readonly byIndex = new Map<number, ItemInfo>();

  add(info: ItemInfo): void {
    const old = this.byIndex.get(info.Index);
    if (old) this.list[this.list.indexOf(old)] = info;
    else this.list.push(info);
    this.byIndex.set(info.Index, info);
  }

  get(index: number): ItemInfo | undefined {
    return this.byIndex.get(index);
  }

  /** Functions.GetRealItem. */
  realItem(origin: ItemInfo, level: number, job: number): ItemInfo {
    const startsWith = (info: ItemInfo) => info.Name.startsWith(origin.Name);
    const classBit = 1 << job;
    if (origin.ClassBased && origin.LevelBased) {
      let output = origin;
      for (const info of this.list) {
        if (startsWith(info) && info.RequiredClass === classBit && info.RequiredType === RequiredType.Level && info.RequiredAmount <= level
          && output.RequiredAmount <= info.RequiredAmount && origin.RequiredGender === info.RequiredGender) output = info;
      }
      return output;
    }
    if (origin.ClassBased) {
      for (const info of this.list) {
        if (startsWith(info) && info.RequiredClass === classBit && origin.RequiredGender === info.RequiredGender) return info;
      }
      return origin;
    }
    if (origin.LevelBased) {
      let output = origin;
      for (const info of this.list) {
        if (startsWith(info) && info.RequiredType === RequiredType.Level && info.RequiredAmount <= level
          && output.RequiredAmount < info.RequiredAmount && origin.RequiredGender === info.RequiredGender) output = info;
      }
      return output;
    }
    return origin;
  }
}

export interface EquipmentLooks { armour: number; weapon: number; weaponEffect: number; wingEffect: number; light: number; missing: number }

/** UserObject.RefreshEquipmentStats (looks) + the light rule; `missing` counts equipped items whose ItemInfo has not arrived yet. */
export function equipmentLooks(equipment: (UserItem | null)[] | null, infos: ItemInfos, level: number, job: number): EquipmentLooks {
  const looks: EquipmentLooks = { armour: 0, weapon: -1, weaponEffect: 0, wingEffect: 0, light: 0, missing: 0 };
  for (const item of equipment ?? []) {
    if (!item) continue;
    const info = infos.get(item.ItemIndex);
    if (!info) { looks.missing++; continue; }
    const real = infos.realItem(info, level, job);
    if (item.CurrentDura === 0 && real.Durability > 0) continue;
    if (real.Type === ItemType.Armour) { looks.armour = real.Shape; looks.wingEffect = real.Effect; }
    if (real.Type === ItemType.Weapon) { looks.weapon = real.Shape; looks.weaponEffect = real.Effect; }
    if (real.Light > looks.light) looks.light = real.Light;
  }
  if (looks.light < 3) looks.light = 3;
  return looks;
}

const statCount = (item: UserItem) => Object.values(item.AddedStats).reduce((sum, v) => sum + Math.abs(v), 0); // Stats.Count

/** Stats.Count: the sum of |value| (UserItem.AddedStats.Count in the price formulas). */
export const addedCount = (item: UserItem): number => statCount(item);
/** Awake.GetAwakeLevel. */
export const awakeLevel = (item: UserItem): number => item.AwakeLevels.length;
/** C# float ToString for the NPCDropDialog prices (uint * NPCRate). */
export const floatText = (v: number): string => String(Number(Math.fround(v).toPrecision(7)));

/** UserItem.Price (uint arithmetic: every cast truncates). */
export function itemPrice(item: UserItem, info: ItemInfo | undefined): number {
  if (!info) return 0;
  let p = info.Price;
  if (info.Durability > 0) {
    let r = info.Price / 2 / info.Durability;
    p = Math.trunc(item.MaxDura * r);
    r = item.MaxDura > 0 ? item.CurrentDura / item.MaxDura : 0;
    p = Math.floor(p / 2 + (p / 2) * r + info.Price / 2);
  }
  p = Math.trunc(p * (statCount(item) * 0.1 + 1));
  return p * item.Count;
}

/** UserItem.RepairPrice. */
export function repairPrice(item: UserItem, info: ItemInfo | undefined): number {
  if (!info || info.Durability === 0) return 0;
  let p = Math.floor(item.MaxDura * (info.Price / 2 / info.Durability) + info.Price / 2);
  p = Math.trunc(p * (statCount(item) * 0.1 + 1));
  const cost = (p * item.Count - itemPrice(item, info)) >>> 0; // uint subtraction wraps like the C#
  return item.Rental ? (cost * 2) >>> 0 : cost;
}

// The NPC service prices (NPCDropDialog.NPCDropPanel_BeforeDraw / Confirm, NPCAwakeDialog; UserItem.*Price). Pure:
// the caller passes GameScene.NPCRate and formats (floatText for the float products).

/** NPCDropDialog PanelType.Sell: UserItem.Price() / 2 (uint). */
export function sellPrice(item: UserItem, info: ItemInfo | undefined): number {
  return Math.trunc(itemPrice(item, info) / 2);
}

/** NPCDropDialog PanelType.Repair: UserItem.RepairPrice() * NPCRate (a float). */
export function repairCost(item: UserItem, info: ItemInfo | undefined, rate: number): number {
  return repairPrice(item, info) * rate;
}

/** NPCDropDialog PanelType.SpecialRepair: UserItem.RepairPrice() * 3 * NPCRate (a float). */
export function specialRepairCost(item: UserItem, info: ItemInfo | undefined, rate: number): number {
  return repairPrice(item, info) * 3 * rate;
}

/** NPCDropDialog PanelType.Refine / ReplaceWedRing: ItemInfo.RequiredAmount * 10 * NPCRate (a float). */
export function refineCost(info: ItemInfo | undefined, rate: number): number {
  return (info?.RequiredAmount ?? 0) * 10 * rate;
}

/** UserItem.DisassemblePrice. */
export function disassemblePrice(item: UserItem, info: ItemInfo | undefined): number {
  return Math.trunc(1500 * (info?.Grade ?? 0) * Math.fround((addedCount(item) + awakeLevel(item)) * 0.1 + 1));
}

/** UserItem.DowngradePrice. */
export function downgradePrice(item: UserItem, info: ItemInfo | undefined): number {
  return 3000 * (1 + (awakeLevel(item) + 1) * 2) * (info?.Grade ?? 0);
}

/** UserItem.ResetPrice. */
export function resetPrice(item: UserItem, info: ItemInfo | undefined): number {
  return Math.trunc(3000 * (info?.Grade ?? 0) * Math.fround(addedCount(item) * 0.2 + 1));
}

/** UserItem.AwakeningPrice. */
export function awakeningPrice(item: UserItem, info: ItemInfo | undefined): number {
  return 1500 * (1 + awakeLevel(item) * 2) * (info?.Grade ?? 0);
}
