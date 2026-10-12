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
