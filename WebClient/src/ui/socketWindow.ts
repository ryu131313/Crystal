// SocketDialog (Client/MirScenes/Dialogs/SocketDialog.cs) with the desktop artwork: Prguse3 20 + (Slots - 1), 6 x 2
// MirItemCells (MirGridType.Socket) of which the first Slots.Length show, the close button at (Size.Width - 23, 3).
// Opened with Ctrl + right-click on an inventory or equipment item that has slots (MirItemCell.OpenItem ->
// SocketDialog.Show), placed centred below the InventoryDialog / CharacterDialog. Right-click a Socket item in the bag to
// put it into the first free socket (MirItemCell.UseSlotItem), right-click a filled socket to take it out
// (MirItemCell.RemoveItem). Cells are persistent inventory-window cells (shared pick / lock state); the model is
// src/game/itemChanges.ts. Like the desktop, Escape does not close it.

import type { ItemChanges } from "../game/itemChanges.ts";
import { SOCKET_GRID_SLOTS } from "../game/itemChanges.ts";
import type { Inventory } from "../game/inventory.ts";
import { gameSignal } from "../game/scope.ts";
import { MirWindow, MirButton, Lib, closeButton, libSize, SCREEN_WIDTH } from "./mir/controls.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";

export class SocketWindow {
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly changes: ItemChanges;
  private readonly requestItem: (index: number) => void;
  private readonly win: MirWindow;
  private readonly close: MirButton;
  private readonly cells: HTMLElement[] = [];
  /** The item the dialog was last shown for (SocketDialog.Show runs once per opened item). */
  private shownFor: bigint | null = null;

  constructor(_stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, changes: ItemChanges, requestItem: (index: number) => void) {
    this.inv = inv;
    this.invWindow = invWindow;
    this.changes = changes;
    this.requestItem = requestItem;
    // SocketDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse3, index: 20, movable: true, sort: true, location: [0, 0], onHide: () => { if (this.changes.socketId !== null) this.changes.closeSockets(); } });
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    for (let x = 0; x < 6; x++) {
      for (let y = 0; y < 2; y++) {
        const idx = 6 * y + x;
        this.cells[idx] = placeCell(invWindow.boundCell("socket", idx), this.win, [x * 36 + 23 + x, y * 33 + 15 + y]);
      }
    }
    this.close = closeButton(this.win, [0, 3], () => this.win.hide());
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  /** SocketDialog.Show (Index, BindGrid, Location) / Hide when the item has gone. */
  render(): void {
    const item = this.changes.socketItem();
    if (!item || item.Slots.length === 0) {
      this.changes.socketId = null;
      this.shownFor = null;
      this.win.hide();
      return;
    }
    // BindGrid: the first Slots.Length cells (at most 6 x 2)
    const count = Math.min(item.Slots.length, SOCKET_GRID_SLOTS);
    for (let i = 0; i < this.cells.length; i++) {
      this.cells[i]!.hidden = i >= count;
      const slot = i < count ? item.Slots[i] : null;
      if (slot && !this.inv.info(slot)) this.requestItem(slot.ItemIndex); // GameScene.Bind
    }
    if (this.shownFor === item.UniqueID && this.win.visible) return;
    this.shownFor = item.UniqueID;
    const index = 20 + (item.Slots.length - 1);
    this.win.index = index;
    const fromBag = this.inv.inventory.some((i) => i?.UniqueID === item.UniqueID);
    // Location: centred under InventoryDialog (Title 196; MirGridType.Inventory) or CharacterDialog (Title 504 at its
    // default (ScreenWidth - 264, 0); MirGridType.Equipment, whose location this window cannot read)
    const root = this.invWindow.root;
    const [ax, ay] = fromBag ? [parseInt(root.style.left, 10) || 0, parseInt(root.style.top, 10) || 0] : [SCREEN_WIDTH - 264, 0];
    void Promise.all([libSize(Lib.Prguse3, index), libSize(Lib.Title, fromBag ? 196 : 504)]).then(([[w], [aw, ah]]) => {
      if (this.shownFor !== item.UniqueID) return;
      this.close.location = [w - 23, 3];
      this.win.location = [ax + Math.trunc((aw - w) / 2), ay + ah + 5];
    });
    this.win.show();
  }
}
