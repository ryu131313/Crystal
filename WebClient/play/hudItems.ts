// The lab mocks' shared bag (2026-10-15): one 40-cell inventory that the Inventory, Storage and Trade windows all read and
// change (before this each kept its own copy), with the item lookups the drag and drop needs (type, stack size, bind
// flags from play/lab-data/items.json). Change a cell, then call bagChanged() so every window showing the bag redraws.

import { findIn, itemData, norm, type Item } from "./hudTooltip.ts";

export interface Stack { name: string; img: number; count: number }
export const BAG_SLOTS = 40;
/** The starting bag (counts within each item's StackSize, so stacks can be topped up and split). */
const START: [string, number, number][] = [
  ["(HP)DrugLarge", 28, 12], ["(MP)DrugLarge", 29, 12], ["SunPotion", 16, 6], ["SunPotion(M)", 312, 4], ["(HP)DrugXL", 813, 6], ["(MP)DrugXL", 814, 6], ["TownTeleport", 402, 5], ["RandomTeleport", 404, 10],
  ["DungeonEscape", 408, 4], ["Amulet", 270, 500], ["GreenPoison", 259, 60], ["RedPoison", 258, 60], ["Apple", 426, 5], ["Dumpling", 797, 1], ["BeefDumpling", 798, 1], ["RepairOil", 3368, 1],
  ["DragonSword", 48, 1], ["HeavenSword", 74, 1], ["BlackTigerArmour(M)", 1030, 1], ["CrystalArmour(M)", 597, 1], ["SpiritNecklace", 249, 1], ["RedOrchidRing", 433, 1], ["SmashWheel", 541, 1], ["BenedictionOil", 26, 1],
  ["HealthStone(M)", 586, 1], ["MagicStone(L)", 582, 1], ["Candle", 130, 1], ["BengalTiger", 2040, 1], ["RecallNecklace", 247, 1], ["Timber", 368, 1], ["(HP)DrugLarge", 28, 8], ["Apple", 426, 3],
];
/** The player's HUD belt (8 cells: keys 1-8 on the 10-slot belt; the 6-slot layout shows the first six): [name, Image,
 *  count]. Desktop Crystal's belt is the bag's first six cells; this HUD keeps it apart (hudMock.ts). */
const START_BELT: [string, number, number][] = [
  ["(HP)DrugLarge", 28, 10], ["(MP)DrugLarge", 29, 10], ["SunPotion", 16, 5], ["SunPotion(M)", 312, 4], ["TownTeleport", 402, 3],
  ["RandomTeleport", 404, 8], ["(HP)DrugXL", 813, 5], ["(MP)DrugXL", 814, 5],
];
export const PLAYER_BELT = 8;
/** The hero's belt (keys 9 and 0 on the 10-slot belt): Crystal's hero Inventory[0..1] (client HeroBeltIdx = 2), used with
 *  C.UseItem { Grid = HeroInventory }; potions only. Not part of the player's bag weight. */
const START_HERO_BELT: [string, number, number][] = [["(HP)DrugLarge", 28, 6], ["(MP)DrugLarge", 29, 6]];
export const HERO_BELT = 2;
/** What a belt potion heals, [HP %, MP %] of the mock maximums (mock amounts). */
export const HEAL: Record<string, [number, number]> = {
  "(HP)DrugLarge": [12, 0], "(MP)DrugLarge": [0, 12], "SunPotion": [20, 20], "SunPotion(M)": [35, 35], "(HP)DrugXL": [25, 0], "(MP)DrugXL": [0, 25],
  "(HP)DrugMedium": [8, 0], "(MP)DrugMedium": [0, 8], "Apple": [5, 0], "Dumpling": [10, 10], "BeefDumpling": [15, 15],
};
const fill = (list: [string, number, number][], n: number): (Stack | null)[] =>
  Array.from({ length: n }, (_, i) => list[i] ? { name: list[i]![0], img: list[i]![1], count: list[i]![2] } : null);
/** The bag: index = cell. */
export const bag: (Stack | null)[] = fill(START, BAG_SLOTS);
export const belt: (Stack | null)[] = fill(START_BELT, PLAYER_BELT);
export const heroBelt: (Stack | null)[] = fill(START_HERO_BELT, HERO_BELT);
/** Back to the starting bag and belt (the lab's reset). */
export function resetItems(): void {
  bag.splice(0, bag.length, ...fill(START, BAG_SLOTS));
  belt.splice(0, belt.length, ...fill(START_BELT, PLAYER_BELT));
  heroBelt.splice(0, heroBelt.length, ...fill(START_HERO_BELT, HERO_BELT));
  bagChanged();
}

const listeners = new Set<() => void>();
/** Runs `fn` whenever the bag changes (and once the item data has loaded). */
export function onBag(fn: () => void): void { listeners.add(fn); }
export function bagChanged(): void { for (const fn of listeners) fn(); }

const items = new Map<string, Item>();
void itemData().then((d) => { if (d) for (const i of d.items) items.set(norm(i.name), i); bagChanged(); });
/** ItemInfo from items.json (undefined until it loads, or for an item it lacks). */
export const info = (name: string): Item | undefined => findIn(items, name);
export const stackMax = (name: string): number => Math.max(1, info(name)?.stack ?? 1);
/** The most one cell holds: StackSize, or no limit for an item items.json lacks (so mock stacks stay whole). */
export const cap = (name: string): number => info(name) ? stackMax(name) : Infinity;
export const hasBind = (name: string, flag: string): boolean => (info(name)?.bind ?? "").split(/,\s*/).includes(flag);

/** Puts a stack in the bag: tops up matching stacks, then the first empty cells. Returns what did not fit. */
export function addToBag(s: Stack): number {
  let left = s.count;
  const max = Math.min(cap(s.name), 1e9);
  if (max > 1) for (const b of bag) {
    if (!left) break;
    if (b && b.name === s.name && b.count < max) { const n = Math.min(left, max - b.count); b.count += n; left -= n; }
  }
  for (let i = 0; i < bag.length && left; i++) if (!bag[i]) { const n = Math.min(left, max); bag[i] = { name: s.name, img: s.img, count: n }; left -= n; }
  return left;
}
export const freeCells = (): number => bag.filter((b) => !b).length;
/** UserItem.Weight: Info.Weight per item, but once per stack for amulets and bait. */
export const weightOf = (s: Stack): number => { const i = info(s.name); return !i ? 1 : i.type === "Amulet" || i.type === "Bait" ? i.weight : i.weight * s.count; };
/** Bag weight limit (mock; Crystal's grows with level). */
export const WEIGHT_MAX = 650;
/** What the bag and belt weigh. */
export const bagWeight = (): number => [...bag, ...belt].reduce((t, s) => t + (s ? weightOf(s) : 0), 0);
