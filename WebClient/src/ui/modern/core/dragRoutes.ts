// Ported from play/hudDrag.ts (DragHost.accepts / verb / quick): the new UI's item routing table, request-based. DOM-free.
//
// A cell is (grid, index) of the real models (itemPick.ts Grid names: "inv" = belt 0-7 + bag, "eq", "trade", "refine",
// "npcStorage", "guild", "socket", "renting", "mount", "fishing", "heroInv" (hero belt 0-1 + bag), "heroEq", "heroHP",
// "heroMP"), plus "npcDrop" (the NPCDropDialog cell) and custom drop-only grids ("x:<name>", registered by a window).
// routeDrop(from, to) looks the pair up in ROUTES and returns a Verdict: what the drop does (the label the drag shows)
// or why not, and `run`, which calls the ONE existing model method that sends the packet and locks the cells
// (Inventory / HeroItems / ItemChanges / NpcDrop: the MirItemCell.MoveItem cases, src/ui/inventoryWindow.ts leftClick).
// Nothing here changes an item: the server answers and the models apply it.
//
// Crystal rules the labels follow:
//  - no cross-grid swaps except equipping (C.EquipItem swaps the worn item out); C.MoveItem swaps within one grid;
//  - store / deposit / take back / transfer into an occupied cell goes to the first free cell (the models redirect);
//  - a matching stack with room merges (C.MergeItem), a full one swaps (same grid) or redirects (cross grid);
//  - a split has no target (C.SplitItem): the server places the new stack;
//  - Ctrl + drop on another item of the same bag = C.CombineItem after a question.
// `run` re-reads the cells when it is called (the drag may be paced or a question answered later): the models refuse a
// request whose cells are empty or locked by then, so a late run never sends a stale move.

import type { ItemInfo, UserItem } from "../../../protocol/server.ts";
import { BindMode, EquipmentSlot, ItemType, MirGridType, PanelType } from "../../../protocol/enums.generated.ts";
import type { Grid, ItemPick } from "../../../game/itemPick.ts";
import { BELT_IDX, type Inventory } from "../../../game/inventory.ts";
import { HERO_BELT_IDX, type Hero } from "../../../game/hero.ts";
import { correctSlot, type HeroItems } from "../../../game/heroItems.ts";
import type { ItemChanges } from "../../../game/itemChanges.ts";
import type { ItemInfos } from "../../../game/items.ts";
import type { NpcSession } from "../../../game/npcSession.ts";
import type { GuildState } from "../../../game/guild.ts";

/** A custom drop-only grid ("x:mail", "x:consign", ...): a window registers its target with DragController.addTarget. */
export type CustomGrid = `x:${string}`;
/** Every cell grid the new UI binds. */
export type CellGrid = Grid | "npcDrop" | CustomGrid;

/** A cell: grid + slot index (items are never keyed by anything else in the UI). */
export interface CellRef {
  readonly grid: CellGrid;
  readonly index: number;
}

/** The models the routes read and call (GameUiContext.models fits this shape). */
export interface ItemModels {
  readonly inventory: Inventory;
  readonly hero: Hero;
  readonly heroItems: HeroItems;
  readonly itemChanges: ItemChanges;
  readonly npcSession: NpcSession;
  readonly guild: GuildState;
  readonly itemInfos: ItemInfos;
  readonly itemPick: ItemPick;
}

/** A custom drop target (a window's own cell: mail attachments, a consign cell, craft / awaken inputs ...). */
export interface CustomTarget {
  /** The item the cell shows (null = empty). */
  item(index: number): UserItem | null;
  /** Whether the cell waits for a server answer. */
  locked?(index: number): boolean;
  /** A drop of `item` from `from`: "" = taken, a reason = refused (shown red), null = not a target for it (no outline). */
  accepts(from: CellRef, item: UserItem, index: number): string | null;
  /** What a drop here is called ("Attach", "Consign"); default "Place". */
  verb?(from: CellRef, item: UserItem, index: number): string;
  /** Sends the request (through a model method that locks the source cell). */
  drop(from: CellRef, item: UserItem, index: number): void;
  /** Right-click on a filled cell (take it back out, ...). */
  use?(index: number): void;
}

export interface RouteDeps {
  readonly models: ItemModels;
  /** performance.now() (UseItem throttles). */
  now(): number;
  /** The registered custom targets. */
  custom(grid: CustomGrid): CustomTarget | undefined;
}

/** What a drop (or a right-click) would do. */
export interface Verdict {
  /** True = `run` sends a request. */
  readonly ok: boolean;
  /** The target label: the action ("Move", "Stack", "Swap", "Equip", "Store"...) or the reason it can't ("" = no label). */
  readonly label: string;
  /** Calls the model method (re-reads the cells; safe to call later). */
  readonly run?: () => void;
  /** Ask this Yes / No question before `run` (Ctrl combine). */
  readonly ask?: string;
}

export const NONE: Verdict = { ok: false, label: "" };
const no = (label: string): Verdict => ({ ok: false, label });
const yes = (label: string, run: () => void, ask?: string): Verdict => ({ ok: true, label, run, ...(ask ? { ask } : {}) });

const T = { // the desktop's texts (ClientTextKeys / MirItemCell)
  YouWantCombineItems: "Do you want to try and combine these items?",
  CannotSwap: "You cannot swap items.",
  NoStoreRights: "Insufficient rights to store items.",
  NoRetrieveRights: "Insufficient rights to retrieve items.",
  RentalLocked: "Unable to remove locked item, cancel item rental and try again.",
  NoRoomSplit: "No room to split stack.",
};
const BUSY = "Waiting for the server";
const GUILD_CAN_STORE = 8, GUILD_CAN_RETRIEVE = 16; // GuildRankOptions.CanStoreItem / CanRetrieveItem
const FISHING_ROD_SHAPES = [49, 50]; // Globals.FishingRodShapes
const MOUNT_PARTS: readonly number[] = [ItemType.Reins, ItemType.Bells, ItemType.Saddle, ItemType.Ribbon, ItemType.Mask];
const FISHING_PARTS: readonly number[] = [ItemType.Hook, ItemType.Float, ItemType.Bait, ItemType.Finder, ItemType.Reel];

/** The MirGridType of a cell grid (null for "npcDrop" and custom grids, which have none). */
export const MIR_GRID: Readonly<Record<Grid | "npcDrop", number | null>> = {
  inv: MirGridType.Inventory, eq: MirGridType.Equipment, trade: MirGridType.Trade, guild: MirGridType.GuildStorage,
  refine: MirGridType.Refine, npcStorage: MirGridType.Storage, socket: MirGridType.Socket, renting: MirGridType.Renting,
  mount: MirGridType.Mount, fishing: MirGridType.Fishing, heroInv: MirGridType.HeroInventory, heroEq: MirGridType.HeroEquipment,
  heroHP: MirGridType.HeroHPItem, heroMP: MirGridType.HeroMPItem, npcDrop: null,
};

export const isCustom = (g: CellGrid): g is CustomGrid => g.startsWith("x:");
export const sameCell = (a: CellRef | null | undefined, b: CellRef | null | undefined): boolean => !!a && !!b && a.grid === b.grid && a.index === b.index;
/** "grid:index" (data-cell, map keys). */
export const cellKey = (r: CellRef): string => `${r.grid}:${r.index}`;

// — Reading cells —

/** The item a cell shows now (MirItemCell.Item). */
export function itemAt(d: RouteDeps, r: CellRef): UserItem | null {
  const m = d.models, inv = m.inventory, i = r.index;
  if (isCustom(r.grid)) return d.custom(r.grid)?.item(i) ?? null;
  switch (r.grid) {
    case "inv": return inv.inventory[i] ?? null;
    case "eq": return inv.equipment[i] ?? null;
    case "trade": return inv.trade[i] ?? null;
    case "refine": return inv.refine[i] ?? null;
    case "npcStorage": return inv.storage[i] ?? null;
    case "guild": return inv.guildStorage[i] ?? null;
    case "socket": return m.itemChanges.socketItem()?.Slots[i] ?? null;
    case "renting": return m.itemChanges.rentalItem;
    case "mount": return m.itemChanges.slotItems(MirGridType.Mount)?.[i] ?? null;
    case "fishing": return m.itemChanges.slotItems(MirGridType.Fishing)?.[i] ?? null;
    case "heroInv": return m.hero.info?.inventory[i] ?? null;
    case "heroEq": return m.hero.info?.equipment[i] ?? null;
    case "heroHP": return m.heroItems.autoPotItem(MirGridType.HeroHPItem);
    case "heroMP": return m.heroItems.autoPotItem(MirGridType.HeroMPItem);
    case "npcDrop": return m.npcSession.drop.currentItem();
  }
  return null;
}

/** MirItemCell.Locked: the item waits for a server answer (or an empty slot cell waits for an EquipSlotItem answer). */
export function cellLocked(d: RouteDeps, r: CellRef): boolean {
  if (isCustom(r.grid)) return !!d.custom(r.grid)?.locked?.(r.index);
  const c = d.models.itemChanges;
  if (r.grid === "socket" && c.isSocketPending(r.index)) return true;
  if (r.grid === "mount" && c.isSlotPending(MirGridType.Mount, r.index)) return true;
  if (r.grid === "fishing" && c.isSlotPending(MirGridType.Fishing, r.index)) return true;
  if (r.grid === "npcDrop" || r.grid === "heroHP" || r.grid === "heroMP") return false; // display items (the drop target's bag cell is the locked one)
  return d.models.inventory.isLocked(itemAt(d, r));
}

/** Whether a cell may be picked up / dragged (a filled, unlocked cell of a grid with move cases). */
export function canPick(d: RouteDeps, r: CellRef): boolean {
  if (isCustom(r.grid) || r.grid === "npcDrop" || r.grid === "heroHP" || r.grid === "heroMP") return false;
  if ((r.grid === "heroInv" || r.grid === "heroEq") && !heroAlive(d)) return false;
  const item = itemAt(d, r);
  return !!item && !cellLocked(d, r);
}

// — Helpers —

const info = (d: RouteDeps, item: UserItem): ItemInfo | undefined => d.models.itemInfos.get(item.ItemIndex);
const heroAlive = (d: RouteDeps): boolean => !!d.models.hero.info && !d.models.hero.dead;
/** A matching stack with room (the models' canStack: merge instead of move). */
function canStack(d: RouteDeps, a: UserItem, b: UserItem | null): boolean {
  if (!b || a.ItemIndex !== b.ItemIndex) return false;
  const i = info(d, b);
  return !!i && i.StackSize > 1 && b.Count < i.StackSize;
}
const firstFree = (list: readonly (UserItem | null)[], from = 0): number => {
  for (let i = from; i < list.length; i++) if (!list[i]) return i;
  return -1;
};
/** The label for a move within one grid (C.MoveItem swaps; C.MergeItem stacks). */
function sameGridLabel(d: RouteDeps, a: UserItem, b: UserItem | null): string {
  return canStack(d, a, b) ? "Stack" : b ? "Swap" : "Move";
}
/** CorrectSlot + CanWearItem: "" = it can go there, else the reason. */
function wearRefusal(d: RouteDeps, item: UserItem, slot: number, hero: boolean): string {
  const i = info(d, item);
  if (!i) return "";
  if (!correctSlot(i, slot)) return "Doesn't go in this slot";
  const why = hero ? d.models.heroItems.canWear(item, slot) : d.models.inventory.canWear(item, slot);
  return why === null ? "" : why || "Can't wear that";
}

type Route = (d: RouteDeps, a: UserItem, from: number, to: number, ctrl: boolean) => Verdict;

/** (fromGrid > toGrid) -> the model method. Pairs not listed have no MirItemCell case (no drop). */
const ROUTES: Partial<Record<string, Route>> = {
  // — the user's bag / belt / equipment (MirItemCell "To Inventory", "To Equipment") —
  "inv>inv": (d, a, f, t, ctrl) => {
    const m = d.models, b = m.inventory.inventory[t] ?? null;
    if (f === t) return NONE;
    if (ctrl && b) return yes("Combine", () => m.itemChanges.combine(f, t), T.YouWantCombineItems);
    const label = sameGridLabel(d, a, b);
    return yes(label === "Move" && t < BELT_IDX && f >= BELT_IDX ? "Belt" : label, () => m.inventory.move(f, t));
  },
  "inv>eq": (d, a, f, t) => {
    const m = d.models, cur = m.inventory.equipment[t] ?? null;
    const why = wearRefusal(d, a, t, false);
    if (why) return no(why);
    // An amulet onto the worn amulet stack merges (MirItemCell.UseItem's amulet case does exactly that for this slot)
    if (t === EquipmentSlot.Amulet && cur && info(d, a)?.Type === ItemType.Amulet && canStack(d, a, cur)) return yes("Stack", () => m.inventory.use(f, d.now()));
    return yes(cur ? `Equip (swap)` : "Equip", () => m.inventory.equip(f, t));
  },
  "eq>inv": (d, _a, f, t) => {
    const m = d.models, inv = m.inventory.inventory;
    if (!inv[t]) return yes("Unequip", () => m.inventory.unequip(f, t));
    if (firstFree(inv) < 0) return no("Your bag is full");
    return yes("Unequip (free cell)", () => m.inventory.unequip(f)); // MirItemCell.RemoveItem's choice: a stack, else a free cell
  },
  // — trade (TradeDialog grid) —
  "inv>trade": (d, a, f, t) => {
    const m = d.models, b = m.inventory.trade[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.deposit(f, t));
    if (b && firstFree(m.inventory.trade) < 0) return no("The trade is full");
    return yes(b ? "Offer (free cell)" : "Offer", () => m.inventory.deposit(f, t));
  },
  "trade>inv": (d, a, f, t) => {
    const m = d.models, b = m.inventory.inventory[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.retrieve(f, t));
    if (b && firstFree(m.inventory.inventory, BELT_IDX) < 0) return no("Your bag is full");
    return yes(b ? "Take back (free cell)" : "Take back", () => m.inventory.retrieve(f, t));
  },
  "trade>trade": (d, a, f, t) => f === t ? NONE : yes(sameGridLabel(d, a, d.models.inventory.trade[t] ?? null), () => d.models.inventory.tradeMove(f, t)),
  // — refine (RefineDialog grid) —
  "inv>refine": (d, a, f, t) => {
    const m = d.models, b = m.inventory.refine[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.refineDeposit(f, t));
    if (!b) return yes("Refine", () => m.inventory.refineDeposit(f, t));
    if (firstFree(m.inventory.refine) < 0) return no("The refine grid is full");
    return yes("Refine (free cell)", () => { const to = firstFree(m.inventory.refine); if (to >= 0) m.inventory.refineDeposit(f, to); });
  },
  "refine>inv": (d, a, f, t) => {
    const m = d.models, b = m.inventory.inventory[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.refineRetrieve(f, t));
    if (b && firstFree(m.inventory.inventory, BELT_IDX) < 0) return no("Your bag is full");
    return yes(b ? "Take back (free cell)" : "Take back", () => m.inventory.refineRetrieve(f, t));
  },
  "refine>refine": (d, a, f, t) => f === t ? NONE : yes(sameGridLabel(d, a, d.models.inventory.refine[t] ?? null), () => d.models.inventory.refineMove(f, t)),
  // — NPC storage (StorageDialog grid, MirGridType.Storage) —
  "inv>npcStorage": (d, a, f, t) => {
    const m = d.models, b = m.inventory.storage[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.storeItem(f, t));
    if (b && firstFree(m.inventory.storage) < 0) return no("Storage is full");
    return yes(b ? "Store (free cell)" : "Store", () => m.inventory.storeItem(f, t));
  },
  "npcStorage>inv": (d, a, f, t) => {
    const m = d.models, b = m.inventory.inventory[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.takeBackItem(f, t));
    if (b && firstFree(m.inventory.inventory, BELT_IDX) < 0) return no("Your bag is full");
    return yes(b ? "Take out (free cell)" : "Take out", () => m.inventory.takeBackItem(f, t));
  },
  "npcStorage>npcStorage": (d, a, f, t) => f === t ? NONE : yes(sameGridLabel(d, a, d.models.inventory.storage[t] ?? null), () => d.models.inventory.storageMove(f, t)),
  "npcStorage>eq": (d, a, f, t) => {
    const m = d.models, cur = m.inventory.equipment[t] ?? null;
    if (cur && info(d, a)?.Type === ItemType.Amulet && canStack(d, a, cur)) return yes("Stack", () => m.inventory.storageEquip(f, t));
    const why = wearRefusal(d, a, t, false);
    if (why) return no(why);
    return yes(cur ? "Equip (swap)" : "Equip", () => m.inventory.storageEquip(f, t));
  },
  "eq>npcStorage": (d, a, f, t) => {
    const m = d.models, b = m.inventory.storage[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => m.inventory.unequipToStorage(f, t));
    if (firstFree(m.inventory.storage) < 0) return no("Storage is full");
    return yes(b ? "Store (free cell)" : "Store", () => m.inventory.unequipToStorage(f, t));
  },
  // — guild storage (GuildDialog.StorageGrid): no swaps with the bag, the rank's rights —
  "inv>guild": (d, _a, f, t) => {
    const m = d.models;
    if (m.inventory.guildStorage[t]) return no(T.CannotSwap);
    if (!m.guild.can(GUILD_CAN_STORE)) return no(T.NoStoreRights);
    return yes("Store", () => m.inventory.guildStore(f, t));
  },
  "guild>inv": (d, _a, f, t) => {
    const m = d.models;
    if (m.inventory.inventory[t]) return no(T.CannotSwap);
    if (!m.guild.can(GUILD_CAN_RETRIEVE)) return no(T.NoRetrieveRights);
    return yes("Take out", () => m.inventory.guildRetrieve(f, t));
  },
  "guild>guild": (d, _a, f, t) => {
    const m = d.models;
    if (f === t) return NONE;
    if (!m.guild.can(GUILD_CAN_STORE)) return no(T.NoStoreRights);
    return yes(m.inventory.guildStorage[t] ? "Swap" : "Move", () => m.inventory.guildMove(f, t));
  },
  // — the lender's Renting cell (ItemRentingDialog.ItemCell) —
  "inv>renting": (d, _a, f) => {
    const c = d.models.itemChanges;
    if (c.rentalItem) return no("Take the item out first");
    return yes("Lend", () => c.depositRental(f));
  },
  "renting>inv": (d, _a, _f, t) => {
    const c = d.models.itemChanges;
    if (d.models.inventory.inventory[t]) return no(T.CannotSwap);
    if (c.rentalItemLocked) return no(T.RentalLocked);
    return yes("Take back", () => c.retrieveRental(t));
  },
  // — sockets / mount / fishing parts: no MoveItem cases on the desktop (right-click UseSlotItem / RemoveItem). A drop
  //   onto the grid runs the same request: the part goes to its own slot (sockets: the first free one). —
  "inv>socket": (d, a, f) => {
    const c = d.models.itemChanges, target = c.socketItem();
    if (info(d, a)?.Type !== ItemType.Socket) return no("Only gems go in sockets");
    if (!target) return NONE;
    const free = target.Slots.some((s, i) => !s && i < 12 && !c.isSocketPending(i)); // SOCKET_GRID_SLOTS
    return free ? yes("Socket", () => c.useSlotItem(f)) : no("No free socket");
  },
  "inv>mount": (d, a, f) => {
    const t = info(d, a)?.Type ?? -1;
    if (!MOUNT_PARTS.includes(t)) return no("Not a mount part");
    if (!d.models.inventory.equipment[EquipmentSlot.Mount]) return no("No mount equipped");
    return yes("Fit", () => d.models.itemChanges.useSlotItem(f));
  },
  "inv>fishing": (d, a, f) => {
    const t = info(d, a)?.Type ?? -1;
    if (!FISHING_PARTS.includes(t)) return no("Not a fishing part");
    const rod = d.models.inventory.equipment[EquipmentSlot.Weapon], ri = rod ? info(d, rod) : undefined;
    if (!ri || !FISHING_ROD_SHAPES.includes(ri.Shape)) return no("No fishing rod equipped");
    return yes("Fit", () => d.models.itemChanges.useSlotItem(f));
  },
  "socket>inv": (d, _a, f) => freeBag(d) ? yes("Remove", () => d.models.itemChanges.removeSocket(f)) : no("Your bag is full"),
  "mount>inv": (d, _a, f) => freeBag(d) ? yes("Remove", () => d.models.itemChanges.removeSlotItem(MirGridType.Mount, f)) : no("Your bag is full"),
  "fishing>inv": (d, _a, f) => freeBag(d) ? yes("Remove", () => d.models.itemChanges.removeSlotItem(MirGridType.Fishing, f)) : no("Your bag is full"),
  // — the NPC drop panel (Sell / Repair / Consign / Disassemble ...: NPCDropDialog.ItemCell_Click) —
  "inv>npcDrop": (d, _a, f) => {
    const drop = d.models.npcSession.drop;
    if (!drop.open || drop.type === PanelType.Refine) return NONE;
    return yes(DROP_VERB[drop.type] ?? "Place", () => drop.take(f));
  },
  // — the hero (HeroInventoryDialog / HeroBeltDialog / HeroDialog / the auto-pot cells) —
  "inv>heroInv": (d, a, f, t) => {
    const h = d.models.hero.info!, b = h.inventory[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => d.models.heroItems.transfer(f, t));
    if (b && firstFree(h.inventory, HERO_BELT_IDX) < 0) return no("The hero's bag is full");
    return yes(b ? "Give to hero (free cell)" : "Give to hero", () => d.models.heroItems.transfer(f, t));
  },
  "heroInv>inv": (d, a, f, t) => {
    const b = d.models.inventory.inventory[t] ?? null;
    if (canStack(d, a, b)) return yes("Stack", () => d.models.heroItems.takeBack(f, t));
    if (b && firstFree(d.models.inventory.inventory, BELT_IDX) < 0) return no("Your bag is full");
    return yes(b ? "Take back (free cell)" : "Take back", () => d.models.heroItems.takeBack(f, t));
  },
  "heroInv>heroInv": (d, a, f, t, ctrl) => {
    const it = d.models.heroItems, b = d.models.hero.info!.inventory[t] ?? null;
    if (f === t) return NONE;
    if (ctrl && b) return yes("Combine", () => it.combine(f, t), T.YouWantCombineItems);
    return yes(sameGridLabel(d, a, b), () => it.move(f, t));
  },
  "heroInv>heroEq": (d, a, f, t) => {
    const cur = d.models.hero.info!.equipment[t] ?? null;
    if (cur && info(d, a)?.Type === ItemType.Amulet && canStack(d, a, cur)) return yes("Stack", () => d.models.heroItems.equip(f, t));
    const why = wearRefusal(d, a, t, true);
    if (why) return no(why);
    return yes(cur ? "Equip (swap)" : "Equip", () => d.models.heroItems.equip(f, t));
  },
  "heroEq>heroInv": (d, a, f, t) => {
    const h = d.models.hero.info!, b = h.inventory[t] ?? null;
    if (b && info(d, a)?.Type === ItemType.Amulet && canStack(d, a, b)) return yes("Stack", () => d.models.heroItems.unequipTo(f, t));
    if (firstFree(h.inventory) < 0) return no("The hero's bag is full");
    return yes(b ? "Unequip (free cell)" : "Unequip", () => d.models.heroItems.unequipTo(f, t));
  },
  "heroInv>heroHP": (d, a, f) => autoPot(d, a, f, MirGridType.HeroHPItem),
  "heroInv>heroMP": (d, a, f) => autoPot(d, a, f, MirGridType.HeroMPItem),
};

const DROP_VERB: Partial<Record<number, string>> = {
  [PanelType.Sell]: "Sell", [PanelType.Repair]: "Repair", [PanelType.SpecialRepair]: "Special repair", [PanelType.Consign]: "Consign",
  [PanelType.CheckRefine]: "Check", [PanelType.Disassemble]: "Disassemble", [PanelType.Downgrade]: "Downgrade", [PanelType.Reset]: "Reset",
  [PanelType.ReplaceWedRing]: "Replace ring",
};

function freeBag(d: RouteDeps): boolean {
  return firstFree(d.models.inventory.inventory) >= 0;
}

/** MirItemCell "To HeroHPItem / HeroMPItem": potions of Shape 0-1 only, with auto-pot unlocked. */
function autoPot(d: RouteDeps, a: UserItem, f: number, grid: number): Verdict {
  const i = info(d, a);
  if (!d.models.hero.info?.autoPot) return no("Auto potions are locked");
  if (!i || i.Type !== ItemType.Potion || i.Shape > 1) return no("HP / MP potions only");
  return yes("Auto potion", () => d.models.heroItems.setAutoPotItem(grid, f));
}

const HERO_ROUTE = /^hero|>hero/;

/** What dropping the item in `from` on `to` does (MirItemCell.MoveItem), or why not. Ctrl = the desktop's Ctrl+place
 *  (combine). The source item is read now: callers check it is still the item they picked up. */
export function routeDrop(d: RouteDeps, from: CellRef, to: CellRef, mods: { ctrl?: boolean } = {}): Verdict {
  if (sameCell(from, to)) return NONE;
  const a = itemAt(d, from);
  if (!a) return no("It's no longer there");
  if (cellLocked(d, from)) return no(BUSY);
  if (isCustom(to.grid)) {
    const target = d.custom(to.grid);
    const why = target?.accepts(from, a, to.index);
    if (!target || why === null || why === undefined) return NONE;
    if (why) return no(why);
    return yes(target.verb?.(from, a, to.index) ?? "Place", () => { const now = itemAt(d, from); if (now && now.UniqueID === a.UniqueID) target.drop(from, now, to.index); });
  }
  if (isCustom(from.grid)) return NONE;
  const key = `${from.grid}>${to.grid}`;
  const route = ROUTES[key];
  if (!route) return NONE;
  if (HERO_ROUTE.test(key) && !heroAlive(d)) return no("Your hero isn't summoned");
  if (to.grid !== "npcDrop" && cellLocked(d, to)) return no(BUSY);
  return route(d, a, from.index, to.index, !!mods.ctrl);
}

/** Right-click on a cell (MirItemCell.UseItem / RemoveItem / RemoveHeroItem / UseSlotItem): what it does, or NONE. */
export function routeUse(d: RouteDeps, r: CellRef): Verdict {
  const m = d.models, i = r.index;
  if (isCustom(r.grid)) {
    const t = d.custom(r.grid);
    return t?.use && t.item(i) ? yes("Use", () => t.use!(i)) : NONE;
  }
  if (r.grid === "npcDrop") return m.npcSession.drop.currentItem() ? yes("Clear", () => m.npcSession.drop.take(-1)) : NONE;
  const item = itemAt(d, r);
  if (!item || cellLocked(d, r)) return NONE;
  switch (r.grid) {
    case "inv": return yes("Use", () => m.inventory.use(i, d.now()));
    case "eq": return yes("Unequip", () => m.inventory.unequip(i));
    case "socket": return yes("Remove", () => m.itemChanges.removeSocket(i));
    case "mount": return yes("Remove", () => m.itemChanges.removeSlotItem(MirGridType.Mount, i));
    case "fishing": return yes("Remove", () => m.itemChanges.removeSlotItem(MirGridType.Fishing, i));
    case "npcStorage": return yes("Use", () => m.inventory.storageUse(i));
    case "heroInv": return m.hero.info ? yes("Use", () => m.heroItems.use(i, d.now())) : NONE;
    case "heroEq": return heroAlive(d) ? yes("Unequip", () => m.heroItems.removeHeroItem(i)) : NONE;
  }
  return NONE;
}

/** Right-click quick move (web-only): a bag item goes to the open guild storage page or NPC storage, a storage item goes
 *  back to the bag. Picks the target cell (a stack with room, else the first free cell) and runs the drag route, so the
 *  rank rights and refusals are the same as a drop. `open(grid)`: whether that grid's cells are on screen. null = no
 *  quick move here (the caller falls back to routeUse). */
export function routeQuickMove(d: RouteDeps, r: CellRef, open: (grid: Grid) => boolean): Verdict | null {
  const inv = d.models.inventory, item = itemAt(d, r);
  if (!item) return null;
  // A stack with room first (not for the guild: its routes never stack), else the first free cell.
  const target = (list: readonly (UserItem | null)[], from: number, stack: boolean): number => {
    if (stack) for (let i = from; i < list.length; i++) if (canStack(d, item, list[i] ?? null)) return i;
    return firstFree(list, from);
  };
  const go = (grid: Grid, list: readonly (UserItem | null)[], from: number, stack: boolean, full: string): Verdict => {
    const t = target(list, from, stack);
    return t < 0 ? no(full) : routeDrop(d, r, { grid, index: t });
  };
  switch (r.grid) {
    case "inv":
      if (open("guild")) return go("guild", inv.guildStorage, 0, false, "The guild storage is full");
      if (open("npcStorage")) return go("npcStorage", inv.storage, 0, true, "Storage is full");
      return null;
    case "guild": return go("inv", inv.inventory, BELT_IDX, false, "Your bag is full");
    case "npcStorage": return go("inv", inv.inventory, BELT_IDX, true, "Your bag is full");
  }
  return null;
}

/** MirItemCell Shift+left: split a stack in the bag or NPC storage (C.SplitItem has no target: the server places the
 *  new stack in a free cell). The verdict's run is not used: the caller asks the amount and calls `split`. */
export function routeSplit(d: RouteDeps, r: CellRef): Verdict {
  if (r.grid !== "inv" && r.grid !== "npcStorage") return NONE;
  const item = itemAt(d, r);
  if (!item || item.Count <= 1 || cellLocked(d, r)) return NONE;
  const list = r.grid === "inv" ? d.models.inventory.inventory : d.models.inventory.storage;
  if (firstFree(list) < 0) return no(T.NoRoomSplit);
  return yes("Split", () => {});
}

/** Sends a split of `count` (1..Count-1) off the stack in `r` (Inventory.split / storageSplit); returns a refusal or null. */
export function split(d: RouteDeps, r: CellRef, count: number): string | null {
  if (r.grid === "inv") return d.models.inventory.split(r.index, count);
  if (r.grid === "npcStorage") return d.models.inventory.storageSplit(r.index, count);
  return null;
}

/** Whether a drop on the world (the map) drops this cell's item: bag / belt and hero bag / belt only (GameScene drops
 *  SelectedCell from those; other picks are only cleared). "" = it can, else the reason; null = not a world drop. */
export function worldDrop(d: RouteDeps, r: CellRef): string | null {
  if (r.grid !== "inv" && r.grid !== "heroInv") return null;
  const item = itemAt(d, r);
  if (!item) return null;
  const i = info(d, item);
  return i && (i.Bind & BindMode.DontDrop) ? "Can't be dropped" : "";
}
