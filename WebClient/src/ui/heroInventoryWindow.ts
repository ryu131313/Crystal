// The hero's items window: HeroInventoryDialog (bag: 8 x 5 cells, hero inventory slots 2.., cells past the hero's bag
// size locked like the LockBar rows; the auto-pot section: HP / MP potion cells + percent buttons, locked until
// S.UnlockHeroAutoPot like HPLockBar / MPLockBar), HeroBeltDialog (hero inventory slots 0-1, keys 7 / 8) and the
// hero's equipment cells (the desktop shows those in the hero CharacterDialog; the web client shows the user's
// equipment inside the inventory window, so the hero's go here too). DOM edition.
// Cells share the inventory window's pick (grids "heroInv" / "heroEq" / "heroHP" / "heroMP"), so items move between the
// user's bag and the hero's bag / equipment as MirItemCell allows (heroItems.ts sends the requests):
//   user bag -> hero bag (C.TransferHeroItem / merge), hero bag -> user bag (C.TakeBackHeroItem / merge), hero bag <->
//   hero bag (C.MoveItem / merge, Ctrl = combine), hero bag -> hero equipment (C.EquipItem), hero equipment -> hero bag
//   (C.RemoveItem), hero bag potion -> auto-pot cell (C.SetAutoPotItem); right-click uses / equips / unequips; a click on
//   a filled auto-pot cell clears it; a picked hero bag item + map click drops it (game.ts calls mapDrop()).
// Ctrl+I toggles it (KeybindOptions.HeroInventory); shown only while the hero is summoned (hero.info set).
// Not ported: the dialog artwork, the belt rotate button, the item upgrade grid effect, Shift+right-click chat links and
// the user's RidingMount check in CanRemoveItem for hero cells.

import type { UserItem } from "../protocol/server.ts";
import { EquipmentSlotName, MirGridType, Stat, BindMode } from "../protocol/enums.generated.ts";
import type { Hero } from "../game/hero.ts";
import { HERO_BELT_IDX } from "../game/hero.ts";
import type { HeroItems } from "../game/heroItems.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches, matchesAny } from "../game/keybinds.ts";
import { EQUIP_LAYOUT, type Grid, type InventoryWindow } from "./inventoryWindow.ts";
import { el } from "./screens.ts";

const BAG_CELLS = 8 * 5; // HeroInventoryDialog.Grid

const T = { // ClientTextKeys (English.json)
  HeroInventory: "Hero Inventory",
  EnterValue: "Enter a value",
  YouWantCombineItems: "Do you want to try and combine these items?",
  YouCannotDrop: (name: string) => `You cannot drop ${name}`,
  DropTip: (name: string) => `Are you sure you want to drop ${name}?`,
};

type Pick = { grid: Grid; index: number } | null;

export class HeroInventoryWindow {
  /** HeroMenuPanel's inventory button stand-in; hidden while the hero is not summoned. game.ts places it. */
  readonly hudButton: HTMLButtonElement;
  private readonly root = el("div", { class: "window hero-inv-window", hidden: true });
  private readonly title = el("span", {}, T.HeroInventory);
  private readonly equipGrid = el("div", { class: "inv-equip" });
  private readonly beltGrid = el("div", { class: "inv-grid inv-belt" });
  private readonly bagGrid = el("div", { class: "inv-grid" });
  private readonly autoPot = el("div", { class: "hero-autopot" });

  constructor(stage: HTMLElement, private readonly hero: Hero, private readonly items: HeroItems,
    private readonly invWindow: InventoryWindow, private readonly now: () => number) {
    this.hudButton = el("button", { title: "Hero inventory (Ctrl+I)", hidden: true, onclick: () => this.toggle() }, "Hero Bag") as HTMLButtonElement;
    this.root.append(
      el("div", { class: "window-title" }, this.title, el("button", { class: "window-close", title: "Close (Ctrl+I)", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "inv-label" }, "Equipment"), this.equipGrid,
      el("div", { class: "inv-label" }, "Belt"), this.beltGrid,
      el("div", { class: "inv-label" }, "Bag"), this.bagGrid,
      el("div", { class: "inv-label" }, "Auto potion"), this.autoPot);
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);

    invWindow.heroItem = (grid, index) => this.item(grid, index);
    invWindow.heroClick = (pick, grid, index, button, ctrl) => this.click(pick, grid, index, button, ctrl);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    // GameScene.UpdateHeroSpawnState: below Summoned the hero dialogs are disposed (hero.info = null)
    hero.onChange(() => {
      this.hudButton.hidden = !hero.info;
      if (!hero.info) {
        this.root.hidden = true;
        if (invWindow.heroPick) invWindow.clearPick();
      }
      this.render();
    });

    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindOptions.HeroInventory (Ctrl+I); Belt7 / Belt8 (7 / 8, NumPad7 / NumPad8) use the hero belt (HeroBeltDialog.Grid[0/1].UseItem)
      if (matches(e, KeybindOptions.HeroInventory)) { e.preventDefault(); if (hero.info) this.toggle(); return; }
      if (!hero.info) return;
      if (matchesAny(e, KeybindOptions.Belt7, KeybindOptions.Belt7Alt)) items.use(0, now());
      if (matchesAny(e, KeybindOptions.Belt8, KeybindOptions.Belt8Alt)) items.use(1, now());
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  toggle(show = this.root.hidden): void {
    if (show && !this.hero.info) return;
    this.root.hidden = !show;
    if (!show && this.invWindow.heroPick) this.invWindow.clearPick();
    this.render();
  }

  /** The item a hero cell shows (MirItemCell.ItemArray for the hero grids). */
  private item(grid: Grid, index: number): UserItem | null {
    const h = this.hero.info;
    if (!h) return null;
    if (grid === "heroInv") return h.inventory[index] ?? null;
    if (grid === "heroEq") return h.equipment[index] ?? null;
    if (grid === "heroHP") return this.items.autoPotItem(MirGridType.HeroHPItem);
    if (grid === "heroMP") return this.items.autoPotItem(MirGridType.HeroMPItem);
    return null;
  }

  /** MirItemCell.OnMouseClick / MoveItem / UseItem for a hero cell (or a left-click while a hero cell is picked). */
  private click(pick: Pick, grid: Grid, index: number, button: number, ctrl: boolean): Pick {
    const it = this.items;
    if (button === 2) {
      // Right-click: UseItem (hero belt / bag) or RemoveHeroItem (hero equipment); Ctrl+right = OpenItem (user grids only)
      if (ctrl) return pick;
      if (grid === "heroInv") it.use(index, this.now());
      else if (grid === "heroEq") it.removeHeroItem(index);
      return null;
    }
    const autoGrid = grid === "heroHP" ? MirGridType.HeroHPItem : grid === "heroMP" ? MirGridType.HeroMPItem : -1;
    if (!pick) {
      if (autoGrid >= 0) { it.clearAutoPotItem(autoGrid); return null; } // a filled auto-pot cell, nothing picked
      const item = this.item(grid, index);
      return item && !this.items.isLocked(item) ? { grid, index } : null;
    }
    if (pick.grid === grid && pick.index === index) return null;
    if (pick.grid === "heroInv" && grid === "heroInv") {
      if (ctrl && this.item(grid, index)) {
        // MirItemCell "To HeroInventory / From HeroInventory" with Ctrl: CombineItem after the YouWantCombineItems Yes
        if (confirm(T.YouWantCombineItems)) it.combine(pick.index, index);
      } else it.move(pick.index, index);
    } else if (pick.grid === "heroEq" && grid === "heroInv") it.unequipTo(pick.index, index);
    else if (pick.grid === "inv" && grid === "heroInv") it.transfer(pick.index, index);
    else if (pick.grid === "heroInv" && grid === "heroEq") it.equip(pick.index, index);
    else if (pick.grid === "heroInv" && autoGrid >= 0) it.setAutoPotItem(autoGrid, pick.index);
    else if (pick.grid === "heroInv" && grid === "inv") it.takeBack(pick.index, index);
    return null; // every other pair has no MirItemCell case
  }

  /** GameScene map click while a hero belt / bag cell is picked: DontDrop refusal, the DropTip Yes/No for a single item
   *  or MirAmountBox for a stack, then C.DropItem with HeroInventory = true. Returns true when it used the click. */
  mapDrop(): boolean {
    const pick = this.invWindow.heroPick;
    if (!pick) return false;
    this.invWindow.clearPick();
    if (pick.grid !== "heroInv") return true; // equipment / auto-pot picks: GameScene clears SelectedCell
    const item = this.hero.info?.inventory[pick.index];
    const info = item && this.items.itemInfo(item);
    if (!item) return true;
    const name = info?.Name ?? "this item";
    if (info && (info.Bind & BindMode.DontDrop)) { alert(T.YouCannotDrop(name)); return true; }
    if (item.Count === 1) { if (confirm(T.DropTip(name))) this.items.drop(pick.index, 1); return true; }
    const text = prompt(`Drop how many ${name}? (1-${item.Count})`, String(item.Count));
    if (text !== null && Number(text) > 0) this.items.drop(pick.index, Number(text));
    return true;
  }

  /** HeroInventoryDialog.HPButton / MPButton (MirAmountBox "Enter a value", max 99). */
  private askPercent(stat: typeof Stat.HP | typeof Stat.MP, current: number): void {
    const text = prompt(`${T.EnterValue} (0-99):`, String(current));
    if (text !== null && text.trim() !== "") this.items.setAutoPotValue(stat, Number(text));
  }

  private autoPotCell(grid: "heroHP" | "heroMP", label: string): HTMLElement {
    const c = this.invWindow.heroCell(grid, 0, label);
    const item = this.item(grid, 0);
    if (item) c.append(el("span", { class: "inv-count" }, String(this.items.countItem(item.ItemIndex)))); // HeroAutoPotPreview.CountItem
    return c;
  }

  render(): void {
    const h = this.hero.info;
    if (this.root.hidden || !h) return;
    this.title.textContent = `${T.HeroInventory} - ${h.name}`;
    this.equipGrid.replaceChildren(...EQUIP_LAYOUT.flat().map((slot) =>
      slot < 0 ? el("div", { class: "inv-gap" }) : this.invWindow.heroCell("heroEq", slot, EquipmentSlotName[slot] ?? "")));
    const belt: HTMLElement[] = [];
    for (let i = 0; i < HERO_BELT_IDX; i++) belt.push(this.invWindow.heroCell("heroInv", i, String(i + 7)));
    this.beltGrid.replaceChildren(...belt);
    // HeroInventoryDialog.RefreshInterface: cells past Hero.Inventory.Length are disabled under the LockBar rows
    const bag: HTMLElement[] = [];
    for (let i = 0; i < BAG_CELLS; i++) {
      const slot = HERO_BELT_IDX + i;
      bag.push(slot < h.inventory.length ? this.invWindow.heroCell("heroInv", slot) : el("div", { class: "inv-cell hero-inv-locked", title: "Locked" }));
    }
    this.bagGrid.replaceChildren(...bag);
    if (!h.autoPot) {
      this.autoPot.replaceChildren(el("div", { class: "hero-autopot-locked" }, "Locked")); // HPLockBar / MPLockBar
      return;
    }
    this.autoPot.replaceChildren(
      el("button", { title: "Use the HP potion below this HP %", onclick: () => this.askPercent(Stat.HP, h.autoHPPercent) }, `HP ${h.autoHPPercent}%`),
      this.autoPotCell("heroHP", "HP"),
      this.autoPotCell("heroMP", "MP"),
      el("button", { title: "Use the MP potion below this MP %", onclick: () => this.askPercent(Stat.MP, h.autoMPPercent) }, `MP ${h.autoMPPercent}%`));
  }
}
