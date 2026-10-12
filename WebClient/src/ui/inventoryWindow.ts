// Inventory + equipment window (DOM over the canvas). Behaviour follows the desktop MirItemCell flow: left-click picks
// an item (GameScene.SelectedCell), left-click on another cell moves/equips/unequips it, right-click uses / equips /
// unequips (MirItemCell.UseItem / RemoveItem), and with an inventory item picked a click on the map drops it.
// Toggle with I (or the Bag button). Icons are Items.Lib images (ItemInfo.Image), decoded once into data URLs.

import type { UserItem } from "../protocol/server.ts";
import { EquipmentSlotName, ItemTypeName, RequiredTypeName, StatName, BindMode, MirGridType } from "../protocol/enums.generated.ts";
import { libAsync } from "../game/libraries.ts";
import { BELT_IDX, type Inventory } from "../game/inventory.ts";
import { itemChangeLines, type ItemChanges } from "../game/itemChanges.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches, matchesAny } from "../game/keybinds.ts";

/** "trade" = the user's TradeDialog grid (cells drawn by TradeWindow through tradeCell()); "guild" = GuildDialog.StorageGrid (storageCell()). */
// "refine" = RefineDialog.Grid (refineCell()); "npcStorage" = StorageDialog.Grid (npcStorageCell())
// "socket" = SocketDialog.Grid (socketCell()); "renting" = ItemRentingDialog.ItemCell (rentingCell())
// "mount" / "fishing" = MountDialog.Grid / FishingDialog.Grid (slotItemCell(); right-click only, like the desktop)
// "heroInv" / "heroEq" / "heroHP" / "heroMP" = the hero's belt + bag, equipment and auto-pot cells (heroCell(); the
// clicks go to heroClick, set by HeroInventoryWindow)
export type Grid = "inv" | "eq" | "trade" | "guild" | "refine" | "npcStorage" | "socket" | "renting" | "mount" | "fishing" | "heroInv" | "heroEq" | "heroHP" | "heroMP";
type Pick = { grid: Grid; index: number } | null;
const HERO_GRIDS: ReadonlySet<Grid> = new Set<Grid>(["heroInv", "heroEq", "heroHP", "heroMP"]);

const iconCache = new Map<number, Promise<string | null>>();

export function itemIcon(image: number): Promise<string | null> {
  let p = iconCache.get(image);
  if (!p) {
    p = (async () => {
      const lib = await libAsync("Items");
      const img = lib && (await lib.image(image));
      if (!img || !img.width || !img.height) return null;
      const canvas = document.createElement("canvas");
      canvas.width = img.width;
      canvas.height = img.height;
      canvas.getContext("2d")!.putImageData(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height), 0, 0);
      return canvas.toDataURL();
    })().catch(() => null);
    iconCache.set(image, p);
  }
  return p;
}

/** Equipment grid layout (rows of slot indices; -1 = gap), roughly the CharacterDialog arrangement. */
export const EQUIP_LAYOUT = [
  [2, 4, 3], // Helmet, Necklace, Torch
  [0, 1, 9], // Weapon, Armour, Amulet
  [6, 5, 10], // BraceletL, BraceletR, Belt
  [8, 7, 11], // RingL, RingR, Boots
  [12, 13, -1], // Stone, Mount
];

export class InventoryWindow {
  readonly root: HTMLElement;
  private readonly inv: Inventory;
  private readonly now: () => number;
  private pick: Pick = null;
  /** While an NPC sell/repair panel is open, a left-click on an inventory item goes to it instead (NPCDropDialog). */
  itemClickHook: ((index: number) => boolean) | null = null;
  private readonly goldLine = el("span", {});
  /** Shift+right-click on an inventory item: link it in the chat box (MirItemCell -> ChatDialog.LinkedItems). */
  onLink: (item: UserItem) => void = () => {};
  /** A system line for refusals (no room to split, ...). */
  notify: (text: string) => void = () => {};
  private readonly equipGrid = el("div", { class: "inv-equip" });
  private readonly beltGrid = el("div", { class: "inv-grid inv-belt" });
  private readonly bagGrid = el("div", { class: "inv-grid" });
  /** Called after every render (the trade window redraws its cells from the same pick state). */
  onRender: () => void = () => {};
  /** GuildDialog.MyOptions (GuildRankOptions) for the storage rights checks, and the chat line for a refusal. */
  guildOptions: () => number = () => 0;
  deny: (text: string) => void = () => {};
  /** Sockets, combine and the Renting cell (src/game/itemChanges.ts); set by SocketWindow. */
  changes: ItemChanges | null = null;
  /** Hero grids (src/ui/heroInventoryWindow.ts): the item a hero cell shows, and the click handler for a click on a
   *  hero cell or a left-click while a hero cell is picked (returns the new pick). */
  heroItem: ((grid: Grid, index: number) => UserItem | null) | null = null;
  heroClick: ((pick: Pick, grid: Grid, index: number, button: number, ctrl: boolean) => Pick) | null = null;

  constructor(stage: HTMLElement, inv: Inventory, now: () => number) {
    this.inv = inv;
    this.now = now;
    this.root = el("div", { class: "window inv-window", hidden: true },
      el("div", { class: "window-title" }, el("span", {}, "Inventory"), el("button", { class: "window-close", onclick: () => this.toggle(false), title: "Close (I)" }, "×")),
      el("div", { class: "inv-label" }, "Equipment"), this.equipGrid,
      el("div", { class: "inv-label" }, "Belt"), this.beltGrid,
      el("div", { class: "inv-label" }, "Bag"), this.bagGrid,
      el("div", { class: "inv-gold" }, this.goldLine, el("button", { title: "Drop gold on the ground", onclick: () => this.dropGold() }, "Drop")));
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    inv.onChange = () => this.render();
    window.addEventListener("keydown", (e) => {
      // KeybindOptions.Inventory (F9) / Inventory2 (I without Ctrl; Ctrl+I = HeroInventory)
      if (matchesAny(e, KeybindOptions.Inventory, KeybindOptions.Inventory2) && !(document.activeElement instanceof HTMLInputElement)) this.toggle();
      if (matches(e, KeybindOptions.Closeall)) { this.pick = null; this.render(); }
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** The picked inventory slot, if any (the game drops it on a map click). */
  get pickedInventorySlot(): number {
    return this.pick?.grid === "inv" ? this.pick.index : -1;
  }

  /** The picked hero cell, if any (a map click with a hero bag item drops it: HeroInventoryWindow.mapDrop). */
  get heroPick(): { grid: Grid; index: number } | null {
    return this.pick && HERO_GRIDS.has(this.pick.grid) ? this.pick : null;
  }

  clearPick(): void {
    this.pick = null;
    this.render();
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (!show) this.pick = null;
    this.render();
  }

  /** `info` overrides the ItemInfo table (the game shop carries its own ItemInfo per listing). */
  describe(item: UserItem, info = this.inv.info(item)): string {
    if (!info) return `Item #${item.ItemIndex}`;
    const lines = [info.Name, ItemTypeName[info.Type] ?? ""];
    if (item.Count > 1) lines.push(`Count ${item.Count}/${info.StackSize}`);
    if (info.Durability > 0) lines.push(`Durability ${Math.round(item.CurrentDura / 1000)}/${Math.round(item.MaxDura / 1000)}`);
    if (info.RequiredAmount > 0) lines.push(`Requires ${RequiredTypeName[info.RequiredType] ?? ""} ${info.RequiredAmount}`);
    // GameScene.CreateItemLabel (condensed): base stats with the item's added stats in brackets, bind flags, tooltip text
    const spaced = (n: string) => n.replace(/([a-z])([A-Z])/g, "$1 $2");
    const keys = new Set([...Object.keys(info.Stats), ...Object.keys(item.AddedStats)].map(Number));
    for (const k of [...keys].sort((x, y) => x - y)) {
      const base = info.Stats[k] ?? 0, added = item.AddedStats[k] ?? 0;
      if (!base && !added) continue;
      const total = base + added;
      lines.push(`${spaced(StatName[k] ?? `Stat ${k}`)} ${total > 0 ? "+" : ""}${total}${added ? ` (${added > 0 ? "+" : ""}${added})` : ""}`);
    }
    const binds = Object.entries(BindMode).filter(([, bit]) => bit !== 0 && (info.Bind & bit) !== 0).map(([n]) => spaced(n));
    if (binds.length) lines.push(binds.join(", "));
    lines.push(...itemChangeLines(item)); // sealed / rental lines
    if (info.ToolTip) lines.push(info.ToolTip);
    return lines.filter(Boolean).join("\n");
  }

  /** A cell of the user's trade grid (TradeDialog.Grid), sharing this window's pick. */
  tradeCell(index: number): HTMLElement {
    return this.cell(this.inv.trade[index] ?? null, "trade", index);
  }

  /** A cell of the socket dialog (SocketDialog.Grid = GameScene.SelectedItem.Slots); an empty slot waiting for an answer shows locked. */
  socketCell(index: number): HTMLElement {
    const c = this.cell(this.changes?.socketItem()?.Slots[index] ?? null, "socket", index);
    if (this.changes?.isSocketPending(index)) c.classList.add("locked");
    return c;
  }

  /** A MountDialog / FishingDialog cell (the equipped mount's / rod's Slots); an empty slot waiting for an answer shows locked. */
  slotItemCell(grid: "mount" | "fishing", index: number, label = ""): HTMLElement {
    const g = grid === "mount" ? MirGridType.Mount : MirGridType.Fishing;
    const c = this.cell(this.changes?.slotItems(g)?.[index] ?? null, grid, index, label);
    if (this.changes?.isSlotPending(g, index)) c.classList.add("locked");
    return c;
  }

  /** The lender's Renting cell (ItemRentingDialog.ItemCell), sharing this window's pick. */
  rentingCell(): HTMLElement {
    return this.cell(this.changes?.rentalItem ?? null, "renting", 0);
  }

  /** A hero cell (HeroInventoryDialog / HeroBeltDialog / HeroDialog / the auto-pot cells), sharing this window's pick. */
  heroCell(grid: "heroInv" | "heroEq" | "heroHP" | "heroMP", index: number, label = ""): HTMLElement {
    return this.cell(this.heroItem?.(grid, index) ?? null, grid, index, label);
  }

  /** A cell of the guild storage grid (GuildDialog.StorageGrid), sharing this window's pick. */
  /** A cell of the refine grid (RefineDialog.Grid), sharing this window's pick. */
  refineCell(index: number): HTMLElement {
    return this.cell(this.inv.refine[index] ?? null, "refine", index);
  }

  storageCell(index: number): HTMLElement {
    return this.cell(this.inv.guildStorage[index] ?? null, "guild", index);
  }

  /** A cell of the NPC storage grid (StorageDialog.Grid, MirGridType.Storage), sharing this window's pick. */
  npcStorageCell(index: number): HTMLElement {
    return this.cell(this.inv.storage[index] ?? null, "npcStorage", index);
  }

  /** A read-only cell (GuestTradeDialog.GuestGrid). */
  viewCell(item: UserItem | null): HTMLElement {
    const c = el("div", { class: "inv-cell", title: item ? this.describe(item) : "" });
    if (item) this.fillCell(c, item);
    return c;
  }

  private fillCell(c: HTMLElement, item: UserItem): void {
    const info = this.inv.info(item);
    if (info) {
      const img = el("img", { alt: info.Name, draggable: false });
      void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
      c.append(img);
    } else c.append(el("span", { class: "inv-slot-label" }, "?"));
    if (item.Count > 1) c.append(el("span", { class: "inv-count" }, String(item.Count)));
  }

  private cell(item: UserItem | null, grid: Grid, index: number, label = ""): HTMLElement {
    const picked = this.pick?.grid === grid && this.pick.index === index;
    const c = el("div", { class: `inv-cell${picked ? " picked" : ""}${this.inv.isLocked(item) ? " locked" : ""}`, title: item ? this.describe(item) : label });
    if (!item && label) c.append(el("span", { class: "inv-slot-label" }, label));
    if (item) this.fillCell(c, item);
    c.onmousedown = (e) => {
      e.stopPropagation();
      if (this.heroClick && (HERO_GRIDS.has(grid) || (e.button === 0 && this.pick && HERO_GRIDS.has(this.pick.grid)))) {
        if (e.button === 0 || e.button === 2) this.pick = this.heroClick(this.pick, grid, index, e.button, e.ctrlKey);
        this.render();
        return;
      }
      if (e.shiftKey && grid === "inv" && item && !this.pick) {
        // MirItemCell: Shift+right = link in chat, Shift+left = split the stack (MirAmountBox up to Count - 1)
        if (e.button === 2) { this.onLink(item); return; }
        if (e.button === 0 && item.Count > 1) {
          const text = prompt(`Split amount (1-${item.Count - 1}):`, String(Math.floor(item.Count / 2)));
          if (text !== null) { const err = this.inv.split(index, Number(text)); if (err) this.notify(err); }
          return;
        }
      }
      if (e.shiftKey && grid === "npcStorage" && item && !this.pick && e.button === 0) {
        // MirItemCell Shift+left on a Storage cell: split (no room = no free storage slot)
        if (!this.inv.storage.some((i) => !i)) { this.notify("No room to split stack."); return; }
        if (item.Count > 1) {
          const text = prompt(`Split amount (1-${item.Count - 1}):`, String(Math.floor(item.Count / 2)));
          if (text !== null) { const err = this.inv.storageSplit(index, Number(text)); if (err) this.notify(err); }
        }
        return;
      }
      if (e.ctrlKey && this.ctrlClick(item, grid, index, e.button)) return;
      if (e.button === 2) this.rightClick(grid, index);
      else if (e.button === 0) this.leftClick(grid, index);
    };
    return c;
  }

  private leftClick(grid: Grid, index: number): void {
    if (grid === "inv" && this.itemClickHook && this.inv.inventory[index] && this.itemClickHook(index)) { this.pick = null; this.render(); return; }
    if (grid === "socket" || grid === "renting" || grid === "mount" || grid === "fishing" || this.pick?.grid === "renting") { this.slotClick(grid, index); return; }
    const target = (grid === "inv" ? this.inv.inventory : grid === "eq" ? this.inv.equipment : grid === "trade" ? this.inv.trade : grid === "refine" ? this.inv.refine : grid === "npcStorage" ? this.inv.storage : this.inv.guildStorage)[index];
    const pick = this.pick;
    if (!pick) {
      if (target && !this.inv.isLocked(target)) this.pick = { grid, index };
      this.render();
      return;
    }
    this.pick = null;
    if (pick.grid === "inv" && grid === "inv") this.inv.move(pick.index, index);
    else if (pick.grid === "inv" && grid === "eq") this.inv.equip(pick.index, index);
    else if (pick.grid === "eq" && grid === "inv") this.inv.unequip(pick.index, index);
    else if (pick.grid === "inv" && grid === "trade") this.inv.deposit(pick.index, index);
    else if (pick.grid === "trade" && grid === "inv") this.inv.retrieve(pick.index, index);
    else if (pick.grid === "trade" && grid === "trade") this.inv.tradeMove(pick.index, index);
    else if (pick.grid === "inv" && grid === "refine") this.inv.refineDeposit(pick.index, index);
    else if (pick.grid === "refine" && grid === "inv") this.inv.refineRetrieve(pick.index, index);
    else if (pick.grid === "refine" && grid === "refine") this.inv.refineMove(pick.index, index);
    else if (grid === "npcStorage" || pick.grid === "npcStorage") this.npcStorageClick(pick, grid, index);
    else if (grid === "guild" || pick.grid === "guild") this.guildClick(pick, grid, index);
    this.render();
  }

  /** MirItemCell.MoveItem's Storage cases: to / from the inventory, the equipment and within the storage grid. */
  private npcStorageClick(pick: { grid: Grid; index: number }, grid: Grid, index: number): void {
    if (pick.grid === "inv" && grid === "npcStorage") this.inv.storeItem(pick.index, index);
    else if (pick.grid === "npcStorage" && grid === "inv") this.inv.takeBackItem(pick.index, index);
    else if (pick.grid === "npcStorage" && grid === "npcStorage") this.inv.storageMove(pick.index, index);
    else if (pick.grid === "npcStorage" && grid === "eq") this.inv.storageEquip(pick.index, index);
    else if (pick.grid === "eq" && grid === "npcStorage") this.inv.unequipToStorage(pick.index, index);
  }

  /** MirItemCell's Renting cases (ItemRentingDialog.ItemCell: deposit into the empty cell, retrieve into an empty
   *  inventory cell) and the socket cells (no move cases on the desktop: right-click only). */
  private slotClick(grid: Grid, index: number): void {
    const c = this.changes, pick = this.pick;
    this.pick = null;
    if (c && grid === "renting" && !pick) { if (c.rentalItem && !this.inv.isLocked(c.rentalItem)) this.pick = { grid, index: 0 }; }
    else if (c && pick?.grid === "inv" && grid === "renting") c.depositRental(pick.index);
    else if (c && pick?.grid === "renting" && grid === "inv") c.retrieveRental(index);
    this.render();
  }

  /** MirItemCell Ctrl+right = OpenItem (the socket dialog for an inventory / equipment item; never uses the item);
   *  Ctrl + placing a picked inventory item onto another inventory item = CombineItem after the YouWantCombineItems question. */
  private ctrlClick(item: UserItem | null, grid: Grid, index: number, button: number): boolean {
    const c = this.changes;
    if (!c) return false;
    if (button === 2) {
      if (!item || (grid !== "inv" && grid !== "eq") || (this.pick?.grid === grid && this.pick.index === index)) return true;
      this.pick = null;
      c.openSockets(item);
      return true;
    }
    if (button !== 0 || this.pick?.grid !== "inv" || grid !== "inv" || !item || this.pick.index === index) return false;
    const from = this.pick.index;
    this.pick = null;
    this.render();
    if (confirm("Do you want to try and combine these items?")) c.combine(from, index);
    return true;
  }

  /** MirItemCell's GuildStorage cases: no swaps with the inventory, and the rank's store / retrieve rights. */
  private guildClick(pick: { grid: Grid; index: number }, grid: Grid, index: number): void {
    const opts = this.guildOptions();
    const CAN_STORE = 8, CAN_RETRIEVE = 16; // GuildRankOptions.CanStoreItem / CanRetrieveItem
    if (pick.grid === "inv" && grid === "guild") {
      if (this.inv.guildStorage[index]) { this.deny("You cannot swap items."); return; }
      if (!(opts & CAN_STORE)) { this.deny("Insufficient rights to store items."); return; }
      this.inv.guildStore(pick.index, index);
    } else if (pick.grid === "guild" && grid === "inv") {
      if (this.inv.inventory[index]) { this.deny("You cannot swap items."); return; }
      if (!(opts & CAN_RETRIEVE)) { this.deny("Insufficient rights to retrieve items."); return; }
      this.inv.guildRetrieve(pick.index, index);
    } else if (pick.grid === "guild" && grid === "guild") {
      if (!(opts & CAN_STORE)) { this.deny("Insufficient rights to store items."); return; }
      this.inv.guildMove(pick.index, index);
    }
  }

  /** Gold drop (the desktop picks the gold up, then a map click asks for the amount). */
  private dropGold(): void {
    if (this.inv.gold <= 0) return;
    const text = prompt(`Drop how much gold? (max ${this.inv.gold.toLocaleString()})`, "");
    if (text) this.inv.dropGold(Number(text.replace(/[,\s]/g, "")));
  }

  private rightClick(grid: Grid, index: number): void {
    this.pick = null;
    if (grid === "inv") this.inv.use(index, this.now());
    else if (grid === "eq") this.inv.unequip(index);
    else if (grid === "socket") this.changes?.removeSocket(index); // MirItemCell.UseItem -> RemoveItem (Socket grid)
    else if (grid === "mount" || grid === "fishing") this.changes?.removeSlotItem(grid === "mount" ? MirGridType.Mount : MirGridType.Fishing, index); // (Mount / Fishing grid)
    else if (grid === "npcStorage") this.inv.storageUse(index);
    this.render();
  }

  render(): void {
    this.onRender();
    if (this.root.hidden) return;
    const { inventory, equipment } = this.inv;
    this.equipGrid.replaceChildren(...EQUIP_LAYOUT.flat().map((slot) =>
      slot < 0 ? el("div", { class: "inv-gap" }) : this.cell(equipment[slot] ?? null, "eq", slot, EquipmentSlotName[slot] ?? "")));
    const belt: HTMLElement[] = [], bag: HTMLElement[] = [];
    for (let i = 0; i < inventory.length; i++) (i < BELT_IDX ? belt : bag).push(this.cell(inventory[i] ?? null, "inv", i, i < BELT_IDX ? String(i + 1) : ""));
    this.beltGrid.replaceChildren(...belt);
    this.bagGrid.replaceChildren(...bag);
    this.goldLine.textContent = `Gold ${this.inv.gold.toLocaleString()}${this.inv.credit ? `   Credit ${this.inv.credit.toLocaleString()}` : ""}`;
  }
}
