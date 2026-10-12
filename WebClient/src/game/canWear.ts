// MirItemCell.CanWearItem: the client-side check before C.EquipItem (gender, class, RequiredType, hand / wear weight,
// fishing and mount parts). Returns the refusal line the desktop prints, or null when the item may be worn.
// Sockets need the socket dialog (GameScene.SelectedItem), which is not ported, so they are refused like the desktop
// does without a selected item.

import type { ItemInfo, UserItem } from "../protocol/server.ts";
import { ItemType, MirClass, MirGender, RequiredClass, RequiredGender, RequiredType, Stat } from "../protocol/enums.generated.ts";
import type { UserStats } from "./stats.ts";

const FISHING_ROD_SHAPES = [49, 50]; // Globals.FishingRodShapes

/** UserItem.Weight: amulets and bait weigh one unit, everything else Info.Weight x Count. */
export function itemWeight(item: UserItem, info: ItemInfo): number {
  return info.Type === ItemType.Amulet || info.Type === ItemType.Bait ? info.Weight : info.Weight * item.Count;
}

export interface Wearer {
  cls: number; gender: number; level: number;
  stats: UserStats;
  /** The equipped weapon's ItemInfo (UserObject.HasFishingRod). */
  weaponInfo: ItemInfo | undefined;
}

/** `current` is the item already in the target slot (MirItemCell.Item), whose weight comes off. */
export function canWearItem(w: Wearer, item: UserItem, info: ItemInfo, current: { item: UserItem; info: ItemInfo } | null): string | null {
  const s = (k: number) => w.stats.stats[k] ?? 0;
  if (w.gender === MirGender.Male && !(info.RequiredGender & RequiredGender.Male)) return "You are not Female.";
  if (w.gender === MirGender.Female && !(info.RequiredGender & RequiredGender.Female)) return "You are not Male.";
  const classes: [number, number, string][] = [
    [MirClass.Warrior, RequiredClass.Warrior, "Warriors cannot use this item."], [MirClass.Wizard, RequiredClass.Wizard, "Wizards cannot use this item."],
    [MirClass.Taoist, RequiredClass.Taoist, "Taoists cannot use this item."], [MirClass.Assassin, RequiredClass.Assassin, "Assassins cannot use this item."],
    [MirClass.Archer, RequiredClass.Archer, "Archers cannot use this item."],
  ];
  for (const [cls, flag, text] of classes) if (w.cls === cls && !(info.RequiredClass & flag)) return text;
  const need = info.RequiredAmount;
  switch (info.RequiredType) {
    case RequiredType.Level: if (w.level < need) return "You are not a high enough level."; break;
    case RequiredType.MaxAC: if (s(Stat.MaxAC) < need) return "You do not have enough AC."; break;
    case RequiredType.MaxMAC: if (s(Stat.MaxMAC) < need) return "You do not have enough MAC."; break;
    case RequiredType.MaxDC: if (s(Stat.MaxDC) < need) return "You do not have enough DC."; break;
    case RequiredType.MaxMC: if (s(Stat.MaxMC) < need) return "You do not have enough MC."; break;
    case RequiredType.MaxSC: if (s(Stat.MaxSC) < need) return "You do not have enough SC."; break;
    case RequiredType.MaxLevel: if (w.level > need) return "You have exceeded the maximum level."; break;
    case RequiredType.MinAC: if (s(Stat.MinAC) < need) return "You do not have enough Base AC."; break;
    case RequiredType.MinMAC: if (s(Stat.MinMAC) < need) return "You do not have enough Base MAC."; break;
    case RequiredType.MinDC: if (s(Stat.MinDC) < need) return "You do not have enough Base DC."; break;
    case RequiredType.MinMC: if (s(Stat.MinMC) < need) return "You do not have enough Base MC."; break;
    case RequiredType.MinSC: if (s(Stat.MinSC) < need) return "You do not have enough Base SC."; break;
  }
  const delta = itemWeight(item, info) - (current ? itemWeight(current.item, current.info) : 0);
  if (info.Type === ItemType.Weapon || info.Type === ItemType.Torch) {
    if (delta + w.stats.currentHandWeight > s(Stat.HandWeight)) return "It is too heavy to Hold.";
  } else if (delta + w.stats.currentWearWeight > s(Stat.WearWeight)) return "It is too heavy to wear.";
  switch (info.Type) {
    case ItemType.Bait: case ItemType.Finder: case ItemType.Hook: case ItemType.Reel: case ItemType.Float: {
      const rod = w.weaponInfo;
      if (!rod || rod.Type !== ItemType.Weapon || !FISHING_ROD_SHAPES.includes(rod.Shape)) return "You are not holding a fishing rod.";
      break;
    }
    case ItemType.Bells: case ItemType.Reins: case ItemType.Ribbon: case ItemType.Saddle:
      if (w.stats.mountType < 0) return "You do not own a mount.";
      break;
    case ItemType.Socket: return "";
  }
  return null;
}
