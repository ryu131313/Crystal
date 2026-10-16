// InventoryDialog (Client/MirScenes/Dialogs/InventoryDialog.cs) with the desktop artwork (Title 196), the shared
// MirItemCell (Client/MirControls/MirItemCell.cs) every item window uses, and GameScene's picked item / picked gold.
// The BeltDialog is beltWindow.ts and the equipment cells sit on the CharacterDialog paperdoll (characterWindow.ts),
// like the desktop; both are built from here / from game.ts's existing windows. Toggle with I / F9 (or the Bag button).
//
// Behaviour follows MirItemCell: left-click picks an item (GameScene.SelectedCell; the item then follows the mouse,
// GameScene.DrawControl), left-click on another cell moves / equips / unequips / merges it, right-click uses / equips /
// unequips (UseItem / RemoveItem), Shift+left splits a stack (MirAmountBox), Shift+right links it in chat, Ctrl+right
// opens the sockets, Ctrl+left onto another bag item combines; a picked bag item + a map click drops it (the UI's
// mapMouseDown, src/game/itemDrop.ts). The pick itself (GameScene.SelectedCell / PickedUpGold) lives in the DOM-free
// src/game/itemPick.ts model; this window draws it and reads / writes it through `picks`.
// Hovering a cell shows the item label (GameScene.CreateItemLabel, itemLabel.ts).
//
// ── Cell API (for every window that shows item cells) ──────────────────────────────────────────────────────────────
// A cell is a desktop MirItemCell: a `.mir` element, `position: absolute`, 36 x 32 (Size), no border or background
// (the dialog artwork has the slots); Items[ItemInfo.Image] centred; the count bottom-right in yellow (CountLabel,
// only for stackable items); a lime 1 px border while hovered / picked / locked; a picked or locked item draws
// DimGray at 80 % (DrawControl). The pick, the clicks and the tooltip are shared through this InventoryWindow.
//   placeCell(cell, parent, [x, y])          append a cell to a MirControl (or element) at the desktop Location
//   inv.tradeCell(i) / storageCell(i) / npcStorageCell(i) / refineCell(i) / socketCell(i) / rentingCell()
//   inv.slotItemCell("mount" | "fishing", i) / heroCell(grid, i) / equipCell(slot) / viewCell(item)
//        → a fresh cell showing the current item (call again after `onRender`, or use boundCell)
//   inv.boundCell(grid, i, size?)            a persistent cell for (grid, i), refreshed by every render() — prefer this
//                                            for fixed grids: create once, placeCell() it, it follows the data
//   inv.viewCell(item, size?)                a read-only cell (tooltip only), e.g. GuestTradeDialog / inspect
//   inv.describe(item, info?)                the item label as plain text
//   EQUIP_LAYOUT, Grid, itemIcon(image), CELL_SIZE
// Cells forward their clicks to the pick logic below; nothing else is needed from the caller.

import type { ItemInfo, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ItemType, MirGridType, Stat } from "../protocol/enums.generated.ts";
import { BELT_IDX, type Inventory } from "../game/inventory.ts";
import { dateFromBinary, type ItemChanges } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches, matchesAny } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, Lib, closeButton, setPicture, libPicture, libPictureNow, getUiRoot, uiSound, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { ItemPick, HERO_GRIDS, type Grid, type PickedCell } from "../game/itemPick.ts";
import { askDropGold, type DropPrompts } from "../game/itemDrop.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { showItemLabel, moveItemLabel, hideItemLabel, itemLabelText, type ItemLabelContext } from "./itemLabel.ts";
import { BeltWindow } from "./beltWindow.ts";
import { userWindows, registerUserWindow, gameUser, gameItemInfos } from "./userRefs.ts";

/** The item grids (src/game/itemPick.ts) and the cells that show them: "inv" / "eq" (the bag, belt and paperdoll),
 *  "trade" (tradeCell()), "guild" (storageCell()), "refine" (refineCell()), "npcStorage" (npcStorageCell()), "socket" (socketCell()), "renting"
 *  (rentingCell()), "mount" / "fishing" (slotItemCell(); right-click only, like the desktop), "heroInv" / "heroEq" /
 *  "heroHP" / "heroMP" (heroCell(); the clicks go to heroClick, set by HeroInventoryWindow). */
export type { Grid };
type Pick = PickedCell | null;

/** The classic prompts for the shared drop rules (src/game/itemDrop.ts): MirMessageBox / MirAmountBox. */
export const classicDropPrompts: DropPrompts = {
  confirm: (text, yes) => { messageBox(getUiRoot(), text, [{ label: "Yes", onClick: yes }, { label: "No" }]); },
  amount: (title, image, max, ok) => { amountBox(title, image, max, ok); },
  refuse: (text) => { messageBox(getUiRoot(), text, [{ label: "OK" }]); },
};

/** MirItemCell.Size. */
export const CELL_SIZE: readonly [number, number] = [36, 32];

const iconCache = new Map<number, Promise<string | null>>();

/** Items[image] as an image URL (for windows that still draw <img> icons). */
export function itemIcon(image: number): Promise<string | null> {
  let p = iconCache.get(image);
  if (!p) {
    p = libPicture(Lib.Items, image).then((pic) => pic?.url ?? null);
    iconCache.set(image, p);
  }
  return p;
}

/** Equipment grid layout (rows of slot indices; -1 = gap) for windows without a paperdoll (inspect / hero). */
export const EQUIP_LAYOUT = [
  [2, 4, 3], // Helmet, Necklace, Torch
  [0, 1, 9], // Weapon, Armour, Amulet
  [6, 5, 10], // BraceletL, BraceletR, Belt
  [8, 7, 11], // RingL, RingR, Boots
  [12, 13, -1], // Stone, Mount
];

/** Appends `cell` to `parent` (a MirControl or element) at the desktop Location. */
export function placeCell(cell: HTMLElement, parent: MirControl | HTMLElement, location: readonly [number, number]): HTMLElement {
  (parent instanceof MirControl ? parent.el : parent).append(cell);
  cell.style.position = "absolute";
  cell.style.left = location[0] + "px";
  cell.style.top = location[1] + "px";
  return cell;
}

/** Client mouse position -> 1024 x 768 stage pixels (the page scales the stage). */
function toStage(clientX: number, clientY: number): [number, number] {
  const root = getUiRoot();
  const r = root.getBoundingClientRect();
  const s = r.width / (root.offsetWidth || 1) || 1;
  return [(clientX - r.left) / s, (clientY - r.top) / s];
}

/** MirItemCell.PlayItemSound. */
function itemSound(type: number): number {
  switch (type) {
    case ItemType.Weapon: return SoundList.ClickWeapon;
    case ItemType.Armour: return SoundList.ClickArmour;
    case ItemType.Helmet: return SoundList.ClickHelmet;
    case ItemType.Necklace: return SoundList.ClickNecklace;
    case ItemType.Bracelet: return SoundList.ClickBracelet;
    case ItemType.Ring: return SoundList.ClickRing;
    case ItemType.Boots: return SoundList.ClickBoots;
    case ItemType.Potion: return SoundList.ClickDrug;
    default: return SoundList.ClickItem;
  }
}

const UI_32BIT = "UI_32bit"; // Libraries.UI_32bit
const INVENTORY_BASE = 46; // UserObject.Inventory.Length before any @ADDINVENTORY
const BAG_PAGE = 40; // cells per bag page

/** One MirItemCell element and what it currently shows. */
interface CellView {
  readonly el: HTMLDivElement;
  readonly icon: HTMLDivElement;
  readonly sealed: HTMLDivElement;
  readonly count: HTMLDivElement;
  grid: Grid;
  index: number;
  item: UserItem | null;
  /** viewCell: tooltip only. */
  readOnly: boolean;
  hovered: boolean;
  /** Extra Locked (a socket / slot waiting for the server). */
  pending: boolean;
  size: readonly [number, number];
}

export class InventoryWindow {
  /** The InventoryDialog element (kept for code that looks things up in it). */
  readonly root: HTMLElement;
  private readonly inv: Inventory;
  private readonly now: () => number;
  /** GameScene.SelectedCell + the bag's PickedUpGold (src/game/itemPick.ts), shared with every cell user. */
  readonly picks: ItemPick;
  private get pick(): Pick { return this.picks.cell; }
  private set pick(v: Pick) { this.picks.set(v); }
  /** While an NPC sell/repair panel is open, a left-click on an inventory item goes to it instead (NPCDropDialog). */
  itemClickHook: ((index: number) => boolean) | null = null;
  /** Shift+right-click on an inventory item: link it in the chat box (MirItemCell -> ChatDialog.LinkedItems). */
  onLink: (item: UserItem) => void = () => {};
  /** A system line for refusals (no room to split, ...). */
  notify: (text: string) => void = () => {};
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
  /** InventoryDialog.QuestButton: opens the quest items (game.ts: `= () => questWindow.toggleItems(true)`). */
  onQuestTab: (() => void) | null = null;
  /** Sends a client packet (the "@ADDINVENTORY" chat line); defaults to the Inventory's own sender. */
  send: (frame: Uint8Array) => void;
  /** The wearer for the item label (MapObject.User): level, class, gender, stats, GetRealItem. Defaults to the user's
   *  CharacterWindow view + the game object; game.ts may replace it. */
  labelContext: () => ItemLabelContext = () => this.defaultLabelContext();

  private readonly win: MirWindow;
  /** The InventoryDialog's Location (the market moves it beside itself, like the desktop). */
  get location(): readonly [number, number] { return this.win.location; }
  set location(p: readonly [number, number]) { this.win.location = p; }
  get size(): readonly [number, number] { return this.win.size; }
  private readonly itemButton: MirButton;
  private readonly itemButton2: MirButton;
  private readonly questButton: MirButton;
  private readonly addButton: MirButton;
  private readonly goldLabel: MirLabel;
  private readonly weightLabel: MirLabel;
  private readonly weightBar: HTMLDivElement;
  private readonly weightPic: HTMLDivElement;
  private readonly lockBars: MirImage[] = [];
  private readonly grid: CellView[] = [];
  private page: 1 | 2 = 1;
  private readonly bound: CellView[] = [];
  private readonly floating: HTMLDivElement;
  private get goldPicked(): boolean { return this.picks.gold; }
  private set goldPicked(v: boolean) { this.picks.setGold(v); }
  private hoverView: CellView | null = null;
  private lastMouse: [number, number] = [-1000, -1000];
  private lastClient: [number, number] = [0, 0];
  private weightKey = "";
  readonly belt: BeltWindow;

  constructor(stage: HTMLElement, inv: Inventory, now: () => number, picks: ItemPick = new ItemPick()) {
    this.inv = inv;
    this.now = now;
    this.picks = picks;
    this.send = (f) => (inv as unknown as { send(f: Uint8Array): void }).send(f);
    registerUserWindow("inventory", this);

    // InventoryDialog ctor
    this.win = new MirWindow({ library: Lib.Title, index: 196, movable: true, sort: true, location: [0, 0], onHide: () => this.onHidden() });
    this.root = this.win.el;
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    // WeightBar: Prguse 24 at (182, 217), DrawImage = false, drawn clipped in WeightBar_BeforeDraw
    this.weightBar = document.createElement("div");
    this.weightBar.className = "mir";
    Object.assign(this.weightBar.style, { left: "182px", top: "217px", overflow: "hidden", pointerEvents: "none", width: "0px", height: "0px" });
    this.weightPic = document.createElement("div");
    this.weightPic.className = "mir-pic";
    this.weightBar.append(this.weightPic);
    this.root.append(this.weightBar);
    this.itemButton = new MirButton({ parent: this.win, library: Lib.Title, index: 197, location: [6, 7], size: [72, 23], sound: SoundList.ButtonA, onClick: (e) => this.tabClick(1, e) });
    this.itemButton2 = new MirButton({ parent: this.win, library: Lib.Title, index: 738, location: [76, 7], size: [72, 23], sound: SoundList.ButtonA, onClick: (e) => this.tabClick(2, e) });
    this.questButton = new MirButton({ parent: this.win, library: Lib.Title, index: 739, location: [146, 7], size: [72, 23], sound: SoundList.ButtonA, onClick: () => this.onQuestTab?.() });
    this.addButton = new MirButton({ parent: this.win, library: Lib.Title, index: 483, hoverIndex: 484, pressedIndex: 485, location: [235, 5], size: [72, 23], sound: SoundList.ButtonA, visible: false,
      onClick: () => {
        const openLevel = Math.trunc((this.inv.inventory.length - INVENTORY_BASE) / 4);
        const openGold = 1000000 + openLevel * 1000000;
        messageBox(stage, `Are you sure you would like to unlock 4 extra slots? for gold: ${openGold.toLocaleString("en-US")}`,
          [{ label: "OK", onClick: () => this.send(ClientPackets.Chat({ Message: "@ADDINVENTORY" })) }, { label: "Cancel" }]);
      } });
    closeButton(this.win, [289, 3], () => this.toggle(false));
    // DelItemButton: the C.DeleteItem encoder and Inventory.deleteItem(slot, count) exist since phase 2.1, but the classic
    // bin button is deliberately left inert this phase (it shows but does nothing)
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 366, hoverIndex: 367, pressedIndex: 368, location: [291, 212], sound: SoundList.ButtonA });
    this.goldLabel = new MirLabel({ parent: this.win, location: [40, 212], size: [111, 14] });
    this.goldLabel.onClick(() => {
      // GoldLabel.Click (Sound = SoundList.Gold)
      uiSound(SoundList.Gold);
      this.picks.toggleGold(this.inv.gold); // if (SelectedCell == null) PickedUpGold = !PickedUpGold && Gold > 0
      this.drawFloating();
    });
    // Grid: 8 x 10 cells for slots 6..85; rows 5-9 (the second bag) sit on rows 0-4
    for (let x = 0; x < 8; x++) {
      for (let y = 0; y < 10; y++) {
        const idx = 8 * y + x;
        const view = this.makeCell("inv", BELT_IDX + idx);
        placeCell(view.el, this.win, [x * 36 + 9 + x, (y % 5) * 32 + 37 + (y % 5)]);
        this.grid[idx] = view;
      }
    }
    this.weightLabel = new MirLabel({ parent: this.win, location: [268, 212], size: [26, 14] });
    for (let i = 0; i < 10; i++) {
      this.lockBars.push(new MirImage({ parent: this.win, library: Lib.Prguse2, index: 307, location: [9 + (i % 2) * 148, 37 + Math.floor(i / 2) * 33], notControl: true, visible: false }));
    }

    // GameScene.DrawControl: the picked item (or gold, Items 116) follows the mouse
    this.floating = document.createElement("div");
    this.floating.className = "mir-pic";
    Object.assign(this.floating.style, { zIndex: "99990", pointerEvents: "none" });
    this.floating.hidden = true;
    getUiRoot().append(this.floating);

    this.belt = new BeltWindow(this);

    inv.subscribe(() => this.render());
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindOptions.Inventory (F9) / Inventory2 (I without Ctrl; Ctrl+I = HeroInventory)
      if (matchesAny(e, KeybindOptions.Inventory, KeybindOptions.Inventory2)) { if (e.key === "F9") e.preventDefault(); this.toggle(); }
      // KeybindOptions.Closeall: InventoryDialog.Hide (and the pick / picked gold go)
      if (matches(e, KeybindOptions.Closeall)) { this.pick = null; this.goldPicked = false; this.toggle(false); }
    }, { signal });
    window.addEventListener("pointermove", (e) => {
      this.lastMouse = toStage(e.clientX, e.clientY);
      this.lastClient = [e.clientX, e.clientY];
      if (this.pick || this.goldPicked) this.drawFloating();
      // a cell rebuilt under the mouse never gets its pointerleave: drop its label
      if (this.hoverView && (!this.hoverView.el.isConnected || !this.hoverView.el.contains(e.target as Node))) { this.hoverView.hovered = false; this.hoverView = null; hideItemLabel(); }
    }, { signal });
    // GameScene map click with PickedUpGold: MirAmountBox (DropAmount, Items 116) -> C.DropGold
    stage.addEventListener("mousedown", (e) => {
      if (!this.goldPicked || e.button !== 0 || (e.target as Element).closest?.("[data-mir]")) return;
      e.stopPropagation();
      e.preventDefault();
      this.goldPicked = false;
      this.drawFloating();
      askDropGold(this.inv, classicDropPrompts);
    }, { capture: true, signal });
    signal.addEventListener("abort", () => { this.win.dispose(); this.floating.remove(); hideItemLabel(); });
    this.refreshPage();
    this.render();
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** The picked inventory slot, if any (the game drops it on a map click). */
  get pickedInventorySlot(): number {
    return this.picks.inventorySlot;
  }

  /** The picked hero cell, if any (a map click with a hero bag item drops it: itemDrop.ts dropHeroItem). */
  get heroPick(): PickedCell | null {
    return this.picks.heroCell;
  }

  clearPick(): void {
    this.pick = null;
    this.render();
  }

  toggle(show = !this.win.visible): void {
    if (show) { this.win.show(); this.refreshPage(); }
    else this.win.hide();
    this.render();
  }

  private onHidden(): void {
    this.pick = null;
    this.render();
  }

  // — InventoryDialog tabs —

  /** InventoryDialog.Button_Click for ItemButton / ItemButton2. */
  private tabClick(page: 1 | 2, e: MouseEvent): void {
    // Ctrl + left-click with a picked bag item: move it to that bag page (TryMoveSelectedInventoryItem)
    if (e.ctrlKey && this.pick) { this.moveToBag(page === 2); return; }
    if (this.inv.inventory.length === INVENTORY_BASE && page === 2) {
      messageBox(getUiRoot(), "Are you sure you would like to buy 8 extra slots for 1,000,000 gold? Next purchase you can unlock 4 extra slots up to a maximum of 40 slots.",
        [{ label: "OK", onClick: () => this.send(ClientPackets.Chat({ Message: "@ADDINVENTORY" })) }, { label: "Cancel" }]);
      return;
    }
    this.page = page;
    this.refreshPage();
  }

  /** InventoryDialog.TryMoveSelectedInventoryItem. */
  private moveToBag(second: boolean): void {
    const pick = this.pick;
    if (!pick || pick.grid !== "inv") return;
    const item = this.inv.inventory[pick.index];
    if (!item || this.inv.isLocked(item)) return;
    const firstStart = BELT_IDX, secondStart = firstStart + BAG_PAGE, len = this.inv.inventory.length;
    if (second && len <= secondStart) return;
    if (second && pick.index >= secondStart) return;
    if (!second && pick.index >= firstStart && pick.index < secondStart) return;
    const start = second ? secondStart : firstStart, end = second ? len : secondStart;
    let target = -1;
    for (let i = start; i < end; i++) if (!this.inv.inventory[i]) { target = i; break; }
    if (target < 0) return;
    this.pick = null;
    this.inv.move(pick.index, target);
    this.render();
  }

  /** InventoryDialog.RefreshInventory / RefreshInventory2 (button indices, visible cells, LockBars, AddButton). */
  private refreshPage(): void {
    const len = this.inv.inventory.length;
    if (this.page === 2 && len <= INVENTORY_BASE) this.page = 1;
    const two = this.page === 2;
    this.itemButton.index = two ? 737 : 197;
    this.itemButton2.index = two ? 168 : len === INVENTORY_BASE ? 169 : 738;
    this.questButton.index = 739;
    for (const v of this.grid) v.el.hidden = two ? v.index < INVENTORY_BASE || v.index >= len : v.index >= INVENTORY_BASE;
    const openLevel = Math.trunc((len - INVENTORY_BASE) / 4);
    for (let i = 0; i < this.lockBars.length; i++) this.lockBars[i]!.visible = two && i >= openLevel;
    this.addButton.visible = two && openLevel < 10;
  }

  // — Per-frame (InventoryDialog.Process / WeightBar_BeforeDraw); called from the user's CharacterWindow.render —

  process(): void {
    if (!this.win.visible) return;
    const v = userWindows.character?.lastView;
    const st = v?.stats;
    const max = st?.stats[Stat.BagWeight] ?? 0;
    let percent = st && max > 0 ? st.currentBagWeight / max : 0;
    if (percent > 1) percent = 1;
    const key = percent.toFixed(4);
    if (key === this.weightKey) return;
    this.weightKey = key;
    if (percent <= 0) { this.weightBar.style.width = "0px"; return; }
    const [lib, index] = percent <= 0.5 ? [Lib.Prguse, 24] : percent <= 0.75 ? [UI_32BIT, 471] : [UI_32BIT, 470];
    void setPicture(this.weightPic, lib, index).then((pic) => {
      if (!pic) return;
      this.weightBar.style.width = Math.trunc((pic.width - 3) * percent) + "px";
      this.weightBar.style.height = pic.height + "px";
    });
  }

  // — Item label —

  private defaultLabelContext(): ItemLabelContext {
    const v = userWindows.character?.lastView ?? null;
    const infos = gameItemInfos();
    const level = v?.level ?? 0, job = v?.cls ?? 0;
    return {
      info: (item) => this.inv.info(item),
      realItem: (info) => (infos ? infos.realItem(info, level, job) : info),
      level, job,
      gender: gameUser()?.look.gender ?? 0,
      stat: (k) => v?.stats?.stats[k] ?? 0,
    };
  }

  /** The item label as plain text (chat-link hover, title attributes); `info` overrides the ItemInfo table (the game
   *  shop carries its own ItemInfo per listing). */
  describe(item: UserItem, info?: ItemInfo): string {
    return itemLabelText(item, this.labelContext(), info ? { info } : undefined);
  }

  // — Cells —

  private itemAt(grid: Grid, index: number): UserItem | null {
    switch (grid) {
      case "inv": return this.inv.inventory[index] ?? null;
      case "eq": return this.inv.equipment[index] ?? null;
      case "trade": return this.inv.trade[index] ?? null;
      case "refine": return this.inv.refine[index] ?? null;
      case "npcStorage": return this.inv.storage[index] ?? null;
      case "guild": return this.inv.guildStorage[index] ?? null;
      case "socket": return this.changes?.socketItem()?.Slots[index] ?? null;
      case "renting": return this.changes?.rentalItem ?? null;
      case "mount": return this.changes?.slotItems(MirGridType.Mount)?.[index] ?? null;
      case "fishing": return this.changes?.slotItems(MirGridType.Fishing)?.[index] ?? null;
      default: return this.heroItem?.(grid, index) ?? null;
    }
  }

  private pending(grid: Grid, index: number): boolean {
    if (grid === "socket") return !!this.changes?.isSocketPending(index);
    if (grid === "mount") return !!this.changes?.isSlotPending(MirGridType.Mount, index);
    if (grid === "fishing") return !!this.changes?.isSlotPending(MirGridType.Fishing, index);
    return false;
  }

  /** MirItemCell ctor: Size 36 x 32, Library Items, no background (DrawControl draws only the item). */
  private makeCell(grid: Grid, index: number, size: readonly [number, number] = CELL_SIZE, readOnly = false): CellView {
    const el = document.createElement("div");
    el.className = "mir mir-item-cell";
    el.dataset.mir = "";
    el.style.width = size[0] + "px";
    el.style.height = size[1] + "px";
    const icon = document.createElement("div");
    icon.className = "mir-pic";
    const sealed = document.createElement("div");
    sealed.className = "mir-pic";
    // CountLabel: AutoSize, Yellow, no outline, bottom-right
    const count = document.createElement("div");
    count.className = "mir mir-label mir-autosize mir-no-outline";
    Object.assign(count.style, { left: "auto", top: "auto", right: "0px", bottom: "0px", color: "#ffff00", pointerEvents: "none" });
    count.hidden = true;
    el.append(icon, sealed, count);
    const view: CellView = { el, icon, sealed, count, grid, index, item: null, readOnly, hovered: false, pending: false, size };
    el.addEventListener("contextmenu", (e) => e.preventDefault());
    el.addEventListener("mousedown", (e) => {
      e.stopPropagation();
      if (!view.readOnly) this.cellDown(view, e);
    });
    // OnMouseEnter / OnMouseLeave: the item label and the hover border
    el.addEventListener("pointerenter", (e) => {
      view.hovered = true;
      this.hoverView = view;
      this.drawCell(view);
      if (view.item) showItemLabel(view.item, this.labelContext(), e.clientX, e.clientY);
    });
    el.addEventListener("pointermove", (e) => { if (view.item) moveItemLabel(e.clientX, e.clientY); });
    el.addEventListener("pointerleave", () => {
      view.hovered = false;
      if (this.hoverView === view) this.hoverView = null;
      this.drawCell(view);
      hideItemLabel();
    });
    return view;
  }

  /** Points a cell at its current item and redraws it. */
  private setCell(view: CellView, item: UserItem | null): void {
    const changed = view.item !== item;
    view.item = item;
    view.pending = this.pending(view.grid, view.index);
    this.drawCell(view);
    if (changed && view.hovered) {
      if (item) showItemLabel(item, this.labelContext(), this.lastClient[0], this.lastClient[1]); else hideItemLabel();
    }
  }

  /** MirItemCell.DrawControl + Border + CreateDisposeLabel. */
  private drawCell(view: CellView): void {
    const item = view.item;
    const picked = !view.readOnly && this.pick?.grid === view.grid && this.pick.index === view.index;
    const locked = view.pending || this.inv.isLocked(item);
    // Border (lime) while selected, hovered or locked
    view.el.style.outline = picked || view.hovered || locked ? "1px solid #00ff00" : "";
    const info = item ? this.inv.info(item) : undefined;
    const image = item && info ? info.Image : -1;
    if (view.icon.dataset.pic !== (image >= 0 ? Lib.Items + "#" + image : "")) {
      const [w, h] = view.size;
      void setPicture(view.icon, image >= 0 ? Lib.Items : null, image).then((pic) => {
        if (!pic) return;
        // offSet = ((Size.Width - imgSize.Width) / 2, (Size.Height - imgSize.Height) / 2), integer division
        view.icon.style.left = Math.trunc((w - pic.width) / 2) + "px";
        view.icon.style.top = Math.trunc((h - pic.height) / 2) + "px";
      });
    }
    // selected or locked: DimGray at 0.8 opacity
    const dim = !!item && (picked || locked);
    view.icon.style.filter = dim ? "brightness(0.41)" : "";
    view.icon.style.opacity = dim ? "0.8" : "";
    // Item.SealedInfo active: StateItems 3590 at (2, 2)
    const sealedOn = !!item && !dim && !!item.Sealed && dateFromBinary(item.Sealed.ExpiryDate).getTime() > Date.now();
    if (sealedOn !== (view.sealed.dataset.pic === Lib.StateItems + "#3590")) void setPicture(view.sealed, sealedOn ? Lib.StateItems : null, 3590);
    view.sealed.style.transform = "translate(2px, 2px)";
    // CountLabel only for stackable items
    const showCount = !!item && !!info && info.StackSize > 1;
    view.count.hidden = !showCount;
    if (showCount) view.count.textContent = String(item!.Count);
  }

  /** A fresh cell for (grid, index) showing its current item. */
  private cell(grid: Grid, index: number, size: readonly [number, number] = CELL_SIZE): HTMLElement {
    const v = this.makeCell(grid, index, size);
    this.setCell(v, this.itemAt(grid, index));
    return v.el;
  }

  /** A persistent cell for (grid, index): every render() refreshes it. */
  boundCell(grid: Grid, index: number, size: readonly [number, number] = CELL_SIZE): HTMLElement {
    const v = this.makeCell(grid, index, size);
    this.bound.push(v);
    this.setCell(v, this.itemAt(grid, index));
    return v.el;
  }

  /** A cell of the user's trade grid (TradeDialog.Grid), sharing this window's pick. */
  tradeCell(index: number): HTMLElement {
    return this.cell("trade", index);
  }

  /** A cell of the socket dialog (SocketDialog.Grid = GameScene.SelectedItem.Slots); an empty slot waiting for an answer shows locked. */
  socketCell(index: number): HTMLElement {
    return this.cell("socket", index);
  }

  /** A MountDialog / FishingDialog cell (the equipped mount's / rod's Slots); a slot waiting for an answer shows locked.
   *  `_label` is kept for the old callers (the desktop draws no slot names). */
  slotItemCell(grid: "mount" | "fishing", index: number, _label = ""): HTMLElement {
    return this.cell(grid, index);
  }

  /** The lender's Renting cell (ItemRentingDialog.ItemCell), sharing this window's pick. */
  rentingCell(): HTMLElement {
    return this.cell("renting", 0);
  }

  /** A hero cell (HeroInventoryDialog / HeroBeltDialog / HeroDialog / the auto-pot cells), sharing this window's pick. */
  heroCell(grid: "heroInv" | "heroEq" | "heroHP" | "heroMP", index: number, _label = ""): HTMLElement {
    return this.cell(grid, index);
  }

  /** A cell of the refine grid (RefineDialog.Grid), sharing this window's pick. */
  refineCell(index: number): HTMLElement {
    return this.cell("refine", index);
  }

  /** A cell of the guild storage grid (GuildDialog.StorageGrid), sharing this window's pick. */
  storageCell(index: number): HTMLElement {
    return this.cell("guild", index);
  }

  /** A cell of the NPC storage grid (StorageDialog.Grid, MirGridType.Storage), sharing this window's pick. */
  npcStorageCell(index: number): HTMLElement {
    return this.cell("npcStorage", index);
  }

  /** A user equipment cell (CharacterDialog.Grid, MirGridType.Equipment). */
  equipCell(slot: number): HTMLElement {
    return this.cell("eq", slot);
  }

  /** A read-only cell (GuestTradeDialog.GuestGrid, inspect, mail, rewards): the item and its label, no clicks. */
  viewCell(item: UserItem | null, size: readonly [number, number] = CELL_SIZE): HTMLElement {
    const v = this.makeCell("inv", -1, size, true);
    this.setCell(v, item);
    return v.el;
  }

  // — MirItemCell.OnMouseClick —

  private cellDown(view: CellView, e: MouseEvent): void {
    const { grid, index } = view;
    const item = this.itemAt(grid, index);
    hideItemLabel();
    if (this.goldPicked) return; // GameScene.PickedUpGold
    if (this.heroClick && (HERO_GRIDS.has(grid) || (e.button === 0 && this.pick && HERO_GRIDS.has(this.pick.grid)))) {
      if (e.button === 0 && item && !this.pick) uiSound(itemSound(this.inv.info(item)?.Type ?? -1));
      if (e.button === 0 || e.button === 2) this.pick = this.heroClick(this.pick, grid, index, e.button, e.ctrlKey);
      this.render();
      return;
    }
    if (e.shiftKey && grid === "inv" && item && !this.pick) {
      // MirItemCell: Shift+right = link in chat, Shift+left = split the stack (MirAmountBox up to Count - 1)
      if (e.button === 2) { this.onLink(item); return; }
      if (e.button === 0) {
        uiSound(itemSound(this.inv.info(item)?.Type ?? -1));
        if (!this.inv.inventory.some((i) => !i)) { this.notify("No room to split stack."); return; }
        if (item.Count > 1) {
          amountBox("Split Amount:", this.inv.info(item)?.Image ?? -1, item.Count - 1, (n) => {
            if (n === 0 || n >= item.Count) return;
            const err = this.inv.split(index, n);
            if (err) this.notify(err);
          });
        }
        return;
      }
    }
    if (e.shiftKey && grid === "npcStorage" && item && !this.pick && e.button === 0) {
      // MirItemCell Shift+left on a Storage cell: split (no room = no free storage slot)
      if (!this.inv.storage.some((i) => !i)) { this.notify("No room to split stack."); return; }
      if (item.Count > 1) {
        amountBox("Split Amount:", this.inv.info(item)?.Image ?? -1, item.Count - 1, (n) => {
          if (n === 0 || n >= item.Count) return;
          const err = this.inv.storageSplit(index, n);
          if (err) this.notify(err);
        });
      }
      return;
    }
    if (e.ctrlKey && this.ctrlClick(item, grid, index, e.button)) return;
    if (e.button === 2) this.rightClick(grid, index);
    else if (e.button === 0) {
      if (item && !this.pick) uiSound(itemSound(this.inv.info(item)?.Type ?? -1)); // PlayItemSound
      this.leftClick(grid, index);
    }
  }

  private leftClick(grid: Grid, index: number): void {
    if (grid === "inv" && this.itemClickHook && this.inv.inventory[index] && this.itemClickHook(index)) { this.pick = null; this.render(); return; }
    if (grid === "socket" || grid === "renting" || grid === "mount" || grid === "fishing" || this.pick?.grid === "renting") { this.slotClick(grid, index); return; }
    const target = this.itemAt(grid, index);
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
    messageBox(getUiRoot(), "Do you want to try and combine these items?", [{ label: "Yes", onClick: () => c.combine(from, index) }, { label: "No" }]);
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

  private rightClick(grid: Grid, index: number): void {
    this.pick = null;
    if (grid === "inv") this.inv.use(index, this.now());
    else if (grid === "eq") this.inv.unequip(index);
    else if (grid === "socket") this.changes?.removeSocket(index); // MirItemCell.UseItem -> RemoveItem (Socket grid)
    else if (grid === "mount" || grid === "fishing") this.changes?.removeSlotItem(grid === "mount" ? MirGridType.Mount : MirGridType.Fishing, index); // (Mount / Fishing grid)
    else if (grid === "npcStorage") this.inv.storageUse(index);
    this.render();
  }

  /** The user's equipped item in `slot` (the CharacterDialog paperdoll). */
  equipped(slot: number): UserItem | null {
    return this.inv.equipment[slot] ?? null;
  }

  /** UserItem.Info. */
  itemInfo(item: UserItem): ItemInfo | undefined {
    return this.inv.info(item);
  }

  /** Belt key / belt cell use (BeltDialog.Grid[i].UseItem). */
  useBelt(index: number): void {
    this.inv.use(index, this.now());
  }

  // — GameScene.DrawControl: the picked item / gold at the mouse —

  private drawFloating(): void {
    const item = this.pick ? this.itemAt(this.pick.grid, this.pick.index) : null;
    const image = this.goldPicked ? 116 : item ? this.inv.info(item)?.Image ?? -1 : -1;
    if (image < 0) { this.floating.hidden = true; return; }
    this.floating.hidden = false;
    const place = () => {
      const pic = libPictureNow(Lib.Items, image);
      if (!pic) return;
      let x = Math.trunc(this.lastMouse[0] - pic.width / 2), y = Math.trunc(this.lastMouse[1] - pic.height / 2);
      if (x + pic.width >= SCREEN_WIDTH) x = SCREEN_WIDTH - pic.width;
      if (y + pic.height >= SCREEN_HEIGHT) y = SCREEN_HEIGHT - pic.height;
      this.floating.style.left = x + "px";
      this.floating.style.top = y + "px";
    };
    if (this.floating.dataset.pic !== Lib.Items + "#" + image) void setPicture(this.floating, Lib.Items, image).then(place);
    else place();
    this.floating.parentElement?.append(this.floating); // keep on top
  }

  render(): void {
    this.onRender();
    if (this.pick && !this.itemAt(this.pick.grid, this.pick.index)) this.pick = null;
    if (this.goldPicked && this.inv.gold <= 0) this.goldPicked = false;
    for (const v of this.grid) this.setCell(v, this.itemAt("inv", v.index));
    for (let i = this.bound.length - 1; i >= 0; i--) {
      const v = this.bound[i]!;
      this.setCell(v, this.itemAt(v.grid, v.index));
    }
    this.belt.render();
    userWindows.character?.refreshEquipment();
    this.drawFloating();
    if (!this.win.visible) return;
    this.refreshPage();
    // InventoryDialog.Process: free slots and gold
    this.weightLabel.text = String(this.inv.inventory.filter((t) => !t).length);
    this.goldLabel.text = this.inv.gold.toLocaleString("en-US");
    this.weightKey = "";
    this.process();
  }
}
