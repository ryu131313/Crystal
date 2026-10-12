// The hero's item windows with the desktop artwork (Client/MirScenes/Dialogs/HeroDialogs.cs):
//   HeroInventoryDialog: Prguse 1422 at (0, 0), 8 x 5 MirItemCells on hero inventory slots 2.., the LockBar rows
//     (Prguse 1423) over the slots past Hero.Inventory.Length, the auto-pot section (HPButton / MPButton Title 560 / 563,
//     the percent labels, the HPItem / MPItem cells) behind HPLockBar / MPLockBar (Prguse 1428 / 1429) until
//     S.UnlockHeroAutoPot. Ctrl+I toggles it (KeybindOptions.HeroInventory); Escape hides it (GameScene Closeall).
//   HeroBeltDialog: Prguse 1921 (horizontal) / 1943 (vertical, Flip) with the half-transparent 1934 / 1946 backdrop
//     (BeltPanel_BeforeDraw), key labels 7 / 8, two 32 x 32 cells on hero inventory slots 0-1, Rotate and Close; shown
//     when the hero is summoned, like the desktop creates it Visible. Keys 7 / 8 use those cells (Grid[0/1].UseItem).
// The hero's equipment cells sit on the hero CharacterDialog paperdoll (heroCharacterWindow.ts), like the desktop.
// Cells share the inventory window's pick (grids "heroInv" / "heroEq" / "heroHP" / "heroMP"), so items move between the
// user's bag and the hero's bag / equipment as MirItemCell allows (heroItems.ts sends the requests):
//   user bag -> hero bag (C.TransferHeroItem / merge), hero bag -> user bag (C.TakeBackHeroItem / merge), hero bag <->
//   hero bag (C.MoveItem / merge, Ctrl = combine), hero bag -> hero equipment (C.EquipItem), hero equipment -> hero bag
//   (C.RemoveItem), hero bag potion -> auto-pot cell (C.SetAutoPotItem); right-click uses / equips / unequips; a click on
//   a filled auto-pot cell clears it; a picked hero bag item + map click drops it (game.ts calls mapDrop()).
// The windows exist while the game scene does; below Summoned (hero.info = null) they hide, like the desktop disposes them.
// Not ported: the item upgrade grid effect (DisplayItemGridEffect), Shift+right-click chat links and the user's
// RidingMount check in CanRemoveItem for hero cells.

import type { UserItem } from "../protocol/server.ts";
import { MirGridType, Stat, BindMode } from "../protocol/enums.generated.ts";
import type { Hero } from "../game/hero.ts";
import type { HeroItems } from "../game/heroItems.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, keyLabel, matches, matchesAny } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, Lib, closeButton, setPicture, libSize, SCREEN_HEIGHT, SCREEN_WIDTH } from "./mir/controls.ts";
import { messageBox, amountBox } from "./messageBox.ts";
import { placeCell, type Grid, type InventoryWindow } from "./inventoryWindow.ts";

const BAG_CELLS = 8 * 5; // HeroInventoryDialog.Grid

const T = { // ClientTextKeys (English.json)
  EnterValue: "Enter a value",
  YouWantCombineItems: "Do you want to try and combine these items?",
  YouCannotDrop: (name: string) => `You cannot drop ${name}`,
  DropTip: (name: string) => `Are you sure you want to drop ${name}?`,
  DropAmount: "Drop Amount:",
  Rotate: "Rotate",
  CloseKey: (key: string) => `Close (${key})`,
};

type Pick = { grid: Grid; index: number } | null;

/** HeroBeltDialog. */
class HeroBeltWindow {
  readonly win: MirWindow;
  private readonly backdrop: HTMLDivElement;
  private readonly keys: MirLabel[] = [];
  private readonly cells: HTMLElement[] = [];
  private readonly rotateButton: MirButton;
  private readonly closeButton: MirButton;
  private mainX = 0;

  constructor(inv: InventoryWindow) {
    // HeroBeltDialog ctor: Location = (MainDialog.Location.X + 475, ScreenHeight - 150)
    this.win = new MirWindow({ library: Lib.Prguse, index: 1921, movable: true, sort: true, location: [475, SCREEN_HEIGHT - 150] });
    // BeltPanel_BeforeDraw: Prguse 1934 (1946 when flipped) at 50 % under the dialog picture
    this.backdrop = document.createElement("div");
    this.backdrop.className = "mir-pic";
    this.backdrop.style.opacity = "0.5";
    this.win.el.insertBefore(this.backdrop, this.win.el.firstChild);
    for (let i = 0; i < 2; i++) this.keys.push(new MirLabel({ parent: this.win, size: [26, 14], location: [8 + i * 35, 2], text: String(i + 7) }));
    this.rotateButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 1926, hoverIndex: 1927, pressedIndex: 1928, location: [82, 3], sound: SoundList.ButtonA,
      hint: T.Rotate, onClick: () => this.flip() });
    this.closeButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 1923, hoverIndex: 1924, pressedIndex: 1925, location: [82, 19], sound: SoundList.ButtonA,
      hint: T.CloseKey(keyLabel(KeybindOptions.Belt)), onClick: () => this.win.hide() });
    for (let x = 0; x < 2; x++) this.cells.push(placeCell(inv.boundCell("heroInv", x, [32, 32]), this.win, [x * 35 + 12, 3]));
    this.drawBackdrop();
    // MainDialog is centred at the bottom (Prguse 1 on a 1024 screen)
    void libSize(Lib.Prguse, 1).then(([w]) => {
      this.mainX = Math.trunc(SCREEN_WIDTH / 2 - w / 2);
      if (this.win.index === 1921) this.win.location = [this.mainX + 475, SCREEN_HEIGHT - 150];
    });
  }

  private drawBackdrop(): void {
    void setPicture(this.backdrop, Lib.Prguse, this.win.index === 1921 ? 1934 : 1946);
  }

  /** HeroBeltDialog.Flip. */
  flip(): void {
    if (this.win.index === 1921) {
      this.win.index = 1943;
      this.win.location = [0, 446];
      for (let x = 0; x < 2; x++) placeCell(this.cells[x]!, this.win, [3, x * 35 + 12]);
      this.closeButton.setIndices(1935, 1936, 1937);
      this.closeButton.location = [3, 82];
      this.rotateButton.setIndices(1938, 1939, 1940);
      this.rotateButton.location = [19, 82];
    } else {
      this.win.index = 1921;
      this.win.location = [this.mainX + 475, SCREEN_HEIGHT - 150];
      for (let x = 0; x < 2; x++) placeCell(this.cells[x]!, this.win, [x * 35 + 12, 3]);
      this.closeButton.setIndices(1923, 1924, 1925);
      this.closeButton.location = [82, 19];
      this.rotateButton.setIndices(1926, 1927, 1928);
      this.rotateButton.location = [82, 3];
    }
    for (let i = 0; i < this.keys.length; i++) this.keys[i]!.location = this.win.index !== 1921 ? [-1, 11 + i * 35] : [8 + i * 35, 2];
    this.drawBackdrop();
  }
}

export class HeroInventoryWindow {
  private readonly win: MirWindow;
  private readonly belt: HeroBeltWindow;
  private readonly grid: HTMLElement[] = [];
  private readonly lockBars: MirImage[] = [];
  private readonly hpLockBar: MirImage;
  private readonly mpLockBar: MirImage;
  private readonly hpButton: MirButton;
  private readonly mpButton: MirButton;
  private readonly autoHPLabel: MirLabel;
  private readonly autoMPLabel: MirLabel;
  private readonly hpItem: HTMLElement;
  private readonly mpItem: HTMLElement;
  /** The hero state the bound cells last showed (a new HeroInformation replaces the arrays). */
  private shown: { info: unknown; inventory: unknown; equipment: unknown } = { info: null, inventory: null, equipment: null };

  constructor(_stage: HTMLElement, private readonly hero: Hero, private readonly items: HeroItems,
    private readonly invWindow: InventoryWindow, private readonly now: () => number) {
    invWindow.heroItem = (grid, index) => this.item(grid, index);
    invWindow.heroClick = (pick, grid, index, button, ctrl) => this.click(pick, grid, index, button, ctrl);

    // HeroInventoryDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 1422, movable: true, sort: true, location: [0, 0], onHide: () => this.onHidden() });
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    closeButton(this.win, [299, 2], () => this.toggle(false));
    for (let x = 0; x < 8; x++) {
      for (let y = 0; y < 5; y++) {
        const idx = 8 * y + x;
        this.grid[idx] = placeCell(invWindow.boundCell("heroInv", 2 + idx), this.win, [x * 36 + 14 + x, (y % 5) * 32 + 23 + (y % 5)]);
      }
    }
    for (let i = 0; i < 4; i++) this.lockBars.push(new MirImage({ parent: this.win, library: Lib.Prguse, index: 1423, location: [14, 56 + i * 33], notControl: true, visible: false }));
    this.hpLockBar = new MirImage({ parent: this.win, library: Lib.Prguse, index: 1428, location: [57, 196], notControl: true, visible: false });
    this.mpLockBar = new MirImage({ parent: this.win, library: Lib.Prguse, index: 1429, location: [162, 196], notControl: true, visible: false });
    // HPButton / MPButton at (58 / 206, Size.Height - 60): placed once the dialog picture's size is known
    this.hpButton = new MirButton({ parent: this.win, library: Lib.Title, index: 560, hoverIndex: 561, pressedIndex: 562, size: [60, 25], sound: SoundList.ButtonA, visible: false,
      onClick: () => this.askPercent(Stat.HP) });
    this.mpButton = new MirButton({ parent: this.win, library: Lib.Title, index: 563, hoverIndex: 564, pressedIndex: 565, size: [60, 25], sound: SoundList.ButtonA, visible: false,
      onClick: () => this.askPercent(Stat.MP) });
    this.autoHPLabel = new MirLabel({ parent: this.win, size: [60, 25], align: "center", visible: false });
    this.autoMPLabel = new MirLabel({ parent: this.win, size: [60, 25], align: "center", visible: false });
    this.hpItem = invWindow.boundCell("heroHP", 0);
    this.mpItem = invWindow.boundCell("heroMP", 0);
    void libSize(Lib.Prguse, 1422).then(([, h]) => {
      const y = h - 60;
      this.hpButton.location = [58, y];
      this.mpButton.location = [206, y];
      this.autoHPLabel.location = [58, y + 27];
      this.autoMPLabel.location = [206, y + 27];
      placeCell(this.hpItem, this.win, [58 + 64, y + 5]);
      placeCell(this.mpItem, this.win, [206 - 40, y + 5]);
      this.refreshInterface();
    });

    this.belt = new HeroBeltWindow(invWindow);

    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    // GameScene.UpdateHeroSpawnState: below Summoned the hero dialogs are disposed (hero.info = null); a new hero gets
    // a new HeroBeltDialog (Visible) and a hidden HeroInventoryDialog
    hero.onChange(() => {
      const h = hero.info;
      if (!h) {
        this.win.hide();
        this.belt.win.hide();
        if (invWindow.heroPick) invWindow.clearPick();
      } else if (!this.shown.info) this.belt.win.show();
      if (this.shown.info !== h || this.shown.inventory !== h?.inventory || this.shown.equipment !== h?.equipment) {
        this.shown = { info: h, inventory: h?.inventory, equipment: h?.equipment };
        invWindow.render(); // the bound hero cells follow the new arrays
      } else this.render();
    });

    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      // KeybindOptions.HeroInventory (Ctrl+I); Belt7 / Belt8 (7 / 8, NumPad7 / NumPad8) use the hero belt (HeroBeltDialog.Grid[0/1].UseItem)
      if (matches(e, KeybindOptions.HeroInventory)) { e.preventDefault(); if (hero.info) this.toggle(); return; }
      if (matches(e, KeybindOptions.Closeall)) { this.toggle(false); return; } // HeroInventoryDialog?.Hide()
      if (!hero.info) return;
      if (matchesAny(e, KeybindOptions.Belt7, KeybindOptions.Belt7Alt)) items.use(0, now());
      if (matchesAny(e, KeybindOptions.Belt8, KeybindOptions.Belt8Alt)) items.use(1, now());
    }, { signal });
    signal.addEventListener("abort", () => { this.win.dispose(); this.belt.win.dispose(); });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  toggle(show = !this.win.visible): void {
    if (show && !this.hero.info) return;
    if (show) this.win.show(); else this.win.hide();
    this.render();
  }

  private onHidden(): void {
    if (this.invWindow.heroPick) this.invWindow.clearPick();
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
        const from = pick.index;
        messageBox(document.body, T.YouWantCombineItems, [{ label: "Yes", onClick: () => it.combine(from, index) }, { label: "No" }]);
      } else it.move(pick.index, index);
    } else if (pick.grid === "heroEq" && grid === "heroInv") it.unequipTo(pick.index, index);
    else if (pick.grid === "inv" && grid === "heroInv") it.transfer(pick.index, index);
    else if (pick.grid === "heroInv" && grid === "heroEq") it.equip(pick.index, index);
    else if (pick.grid === "heroInv" && autoGrid >= 0) it.setAutoPotItem(autoGrid, pick.index);
    else if (pick.grid === "heroInv" && grid === "inv") it.takeBack(pick.index, index);
    return null; // every other pair has no MirItemCell case
  }

  /** GameScene map click while a hero belt / bag cell is picked: the YouCannotDrop box for DontDrop, the DropTip Yes/No
   *  for a single item or MirAmountBox (DropAmount) for a stack, then C.DropItem with HeroInventory = true. Returns true
   *  when it used the click. */
  mapDrop(): boolean {
    const pick = this.invWindow.heroPick;
    if (!pick) return false;
    this.invWindow.clearPick();
    if (pick.grid !== "heroInv") return true; // equipment / auto-pot picks: GameScene clears SelectedCell
    const item = this.hero.info?.inventory[pick.index];
    const info = item && this.items.itemInfo(item);
    if (!item) return true;
    const name = info?.Name ?? "this item";
    if (info && (info.Bind & BindMode.DontDrop)) { messageBox(document.body, T.YouCannotDrop(name)); return true; }
    if (item.Count === 1) {
      messageBox(document.body, T.DropTip(name), [{ label: "Yes", onClick: () => this.items.drop(pick.index, 1) }, { label: "No" }]);
      return true;
    }
    amountBox(T.DropAmount, info?.Image ?? -1, item.Count, (n) => { if (n > 0) this.items.drop(pick.index, n); });
    return true;
  }

  /** HeroInventoryDialog.HPButton / MPButton: MirAmountBox (EnterValue, Items 116, max 99) -> C.SetAutoPotValue. */
  private askPercent(stat: typeof Stat.HP | typeof Stat.MP): void {
    amountBox(T.EnterValue, 116, 99, (n) => this.items.setAutoPotValue(stat, n));
  }

  /** HeroInventoryDialog.RefreshInterface. */
  private refreshInterface(): void {
    const h = this.hero.info;
    if (!h) return;
    for (let i = 0; i < this.grid.length; i++) {
      // grid.Enabled = ItemSlot < Hero.Inventory.Length (a disabled cell takes no clicks)
      this.grid[i]!.style.pointerEvents = 2 + i < h.inventory.length ? "" : "none";
    }
    for (let i = 0; i < this.lockBars.length; i++) this.lockBars[i]!.visible = h.inventory.length < 11 + 8 * i;
    const autoPot = h.autoPot;
    this.hpLockBar.visible = !autoPot;
    this.mpLockBar.visible = !autoPot;
    this.hpButton.visible = autoPot;
    this.mpButton.visible = autoPot;
    this.autoHPLabel.visible = autoPot;
    this.autoMPLabel.visible = autoPot;
    this.hpItem.hidden = !autoPot;
    this.mpItem.hidden = !autoPot;
    this.autoHPLabel.text = `${h.autoHPPercent}%`;
    this.autoMPLabel.text = `${h.autoMPPercent}%`;
  }

  render(): void {
    if (!this.hero.info) return;
    this.refreshInterface();
  }
}
