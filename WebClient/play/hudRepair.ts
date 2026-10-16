// Item repair rules for the scene lab's UI tester (2026-10-16): the one place the mock repairs an item (and prices one:
// itemPrice = UserItem.Price, also the merchant's sell price), used by the merchant's Repair tab (worn gear and damaged
// bag items) and Sell tab (hudMerchant.ts). The rules are the game's: UserItem.RepairPrice (the item's full price at full durability minus its price now, x 1.1 per added stat, x the
// NPC's rate), PlayerObject.RepairItem (a normal repair loses (Max - Current) / 30 of the maximum durability; a special
// repair costs 3x and loses none) and the NPC checks (no durability, amulets, potions, scrolls, DontRepair; NoSRepair for
// special repair). Per-item state is the item tooltip's (hudTooltip.ts copyOf / editCopy / setDura), so the tooltip, the
// HUD's durability strip and the repair list agree. Mock only: nothing is sent anywhere.

import { hasBind, info } from "./hudItems.ts";
import { copyOf, editCopy, setDura } from "./hudTooltip.ts";

const NPC_RATE = 1; // NPCInfo.Rate / 100 (no conquest tax here)

/** "" or why the NPC won't repair this item (`special` = special repair). */
export function repairWhy(name: string, special: boolean): string {
  const it = info(name);
  if (!it) return "Unknown item";
  if (!it.dura || it.type === "Amulet" || it.type === "Potion" || it.type === "Scroll") return "Can't be repaired";
  if (hasBind(name, "DontRepair")) return "Can't be repaired";
  if (special && hasBind(name, "NoSRepair")) return "Can't be special repaired";
  return "";
}

/** Durability now / max (thousandths, like UserItem.CurrentDura / MaxDura); null = no durability. */
export function duraOf(name: string): { cur: number; max: number } | null {
  const it = info(name);
  if (!it || !it.dura) return null;
  const cp = copyOf(it);
  return { cur: cp.cur, max: cp.max };
}

/** UserItem.Price() for `count` pieces: items with durability lose value with their max and current durability, added
 *  stats add 10 % each; stackables are the plain price x count. The merchant pays half (NPCObject sell). */
export function itemPrice(name: string, count = 1): number {
  const it = info(name);
  if (!it) return 0;
  if (!it.dura || it.type === "Amulet") return it.price * count;
  const cp = copyOf(it), addedMul = Object.keys(cp.added).length * 0.1 + 1;
  // The price scales with the max durability, and half of it with the current durability.
  const r0 = (it.price / 2) / it.dura;
  let p = Math.floor(cp.max * r0);
  p = Math.floor(p / 2 + (p / 2) * (cp.max > 0 ? cp.cur / cp.max : 0) + it.price / 2);
  return Math.floor(p * addedMul) * count;
}

/** UserItem.RepairPrice x the NPC's rate (x 3 for special repair), for `count` pieces. */
export function repairCost(name: string, special: boolean, count = 1): number {
  const it = info(name);
  if (!it || !it.dura) return 0;
  const cp = copyOf(it), addedMul = Object.keys(cp.added).length * 0.1 + 1;
  // UserItem.RepairPrice(): the price at full current durability minus the price now.
  const full = Math.floor(Math.floor(cp.max * ((it.price / 2) / it.dura) + it.price / 2) * addedMul);
  const one = Math.max(0, full - itemPrice(name));
  return Math.floor(one * count * (special ? 3 : 1) * NPC_RATE);
}

/** How much maximum durability the repair takes off (0 for special repair). */
export function repairLoss(name: string, special: boolean): number {
  const d = duraOf(name);
  return d && !special ? Math.floor((d.max - d.cur) / 30) : 0;
}

/** Repairs the item (PlayerObject.RepairItem): full durability at the new maximum. Returns the new maximum. */
export function applyRepair(name: string, special: boolean): number {
  const it = info(name), d = duraOf(name);
  if (!it || !d) return 0;
  const newMax = d.max - repairLoss(name, special);
  editCopy(name, { max: newMax / it.dura });
  setDura(name, newMax / it.dura);
  return newMax;
}
