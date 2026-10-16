// The shared item pick: GameScene.SelectedCell (the MirItemCell picked by a left-click, Client/MirControls/MirItemCell.cs)
// and GameScene.PickedUpGold (Client/MirScenes/GameScene.cs). DOM-free: the classic InventoryWindow keeps its cells, the
// floating icon (GameScene.DrawControl) and its redraws, and reads / writes the pick through this model; a modern UI can
// use it without building the classic windows.
//
// The web client keeps two gold flags where the desktop has one PickedUpGold, so the classic UI behaves as before:
//   gold     InventoryDialog.GoldLabel: drawn at the mouse, blocks cell clicks, dropped by the bag window's map click
//   hudGold  MainDialog.GoldLabel: no icon, dropped by the UI's map click (itemDrop.ts)

import { Emitter } from "./emitter.ts";

/** "inv" = the user's belt + bag (UserObject.Inventory), "eq" = equipment; "trade" = the user's TradeDialog grid;
 *  "guild" = GuildDialog.StorageGrid; "refine" = RefineDialog.Grid; "npcStorage" = StorageDialog.Grid; "socket" =
 *  SocketDialog.Grid; "renting" = ItemRentingDialog.ItemCell; "mount" / "fishing" = MountDialog.Grid / FishingDialog.Grid;
 *  "heroInv" / "heroEq" / "heroHP" / "heroMP" = the hero's belt + bag, equipment and auto-pot cells. */
export type Grid = "inv" | "eq" | "trade" | "guild" | "refine" | "npcStorage" | "socket" | "renting" | "mount" | "fishing" | "heroInv" | "heroEq" | "heroHP" | "heroMP";

/** A picked cell: grid + slot index. */
export interface PickedCell {
  readonly grid: Grid;
  readonly index: number;
}

/** The hero grids (HeroInventoryDialog / HeroBeltDialog / HeroDialog / the auto-pot cells). */
export const HERO_GRIDS: ReadonlySet<Grid> = new Set<Grid>(["heroInv", "heroEq", "heroHP", "heroMP"]);

export class ItemPick {
  /** Any change of the picked cell or either gold flag. */
  readonly changed = new Emitter<void>();
  private _cell: PickedCell | null = null;
  private _gold = false;
  private _hudGold = false;

  /** GameScene.SelectedCell (grid + slot), or null. */
  get cell(): PickedCell | null { return this._cell; }
  /** InventoryDialog.GoldLabel's PickedUpGold (the gold icon follows the mouse). */
  get gold(): boolean { return this._gold; }
  /** MainDialog.GoldLabel's PickedUpGold. */
  get hudGold(): boolean { return this._hudGold; }

  /** The picked bag / belt slot, or -1. */
  get inventorySlot(): number {
    return this._cell?.grid === "inv" ? this._cell.index : -1;
  }

  /** The picked hero cell, or null. */
  get heroCell(): PickedCell | null {
    return this._cell && HERO_GRIDS.has(this._cell.grid) ? this._cell : null;
  }

  /** Picks (grid, index), or clears the cell pick with null. */
  set(cell: PickedCell | null): void {
    if (cell === this._cell || (cell && this._cell && cell.grid === this._cell.grid && cell.index === this._cell.index)) { this._cell = cell; return; }
    this._cell = cell;
    this.changed.emit();
  }

  /** Clears the cell pick (the gold flags stay). */
  clear(): void {
    this.set(null);
  }

  setGold(on: boolean): void {
    if (this._gold === on) return;
    this._gold = on;
    this.changed.emit();
  }

  setHudGold(on: boolean): void {
    if (this._hudGold === on) return;
    this._hudGold = on;
    this.changed.emit();
  }

  /** InventoryDialog.GoldLabel.Click: toggles the gold pick while no cell is picked (only with gold to drop). */
  toggleGold(gold: number): void {
    if (this._cell === null) this.setGold(!this._gold && gold > 0);
  }

  /** MainDialog.GoldLabel.Click: toggles the HUD gold pick while no bag slot is picked (only with gold to drop). */
  toggleHudGold(gold: number): void {
    if (this.inventorySlot < 0) this.setHudGold(!this._hudGold && gold > 0);
  }
}
