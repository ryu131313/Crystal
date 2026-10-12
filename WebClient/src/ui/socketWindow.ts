// Socket dialog (SocketDialog): the slots of GameScene.SelectedItem, opened with Ctrl + right-click on an inventory or
// equipment item that has slots (MirItemCell.OpenItem). Right-click a Socket item in the bag to put it into the first
// free socket (MirItemCell.UseSlotItem), right-click a filled socket to take it out (MirItemCell.RemoveItem). Cells
// are drawn by InventoryWindow.socketCell (shared pick / lock state); the model is src/game/itemChanges.ts.

import type { ItemChanges } from "../game/itemChanges.ts";
import { SOCKET_GRID_SLOTS } from "../game/itemChanges.ts";
import type { Inventory } from "../game/inventory.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

export class SocketWindow {
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly changes: ItemChanges;
  private readonly requestItem: (index: number) => void;
  private readonly title = el("span", {}, "Sockets");
  private readonly grid = el("div", { class: "inv-grid socket-grid" });
  private readonly root = el("div", { class: "window socket-window", hidden: true });

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, changes: ItemChanges, requestItem: (index: number) => void) {
    this.inv = inv;
    this.invWindow = invWindow;
    this.changes = changes;
    this.requestItem = requestItem;
    invWindow.changes = changes;
    inv.useSlotItem = (index) => changes.useSlotItem(index);
    this.root.append(
      el("div", { class: "window-title" }, this.title, el("button", { class: "window-close", title: "Close", onclick: () => changes.closeSockets() }, "×")),
      this.grid,
      el("div", { class: "socket-hint" }, "Right-click a socket item in the bag to insert it; right-click a socket to remove it."));
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall) && !this.root.hidden) changes.closeSockets(); }, { signal: gameSignal() });
  }

  /** SocketDialog.Show / BindGrid: one cell per slot (at most 6 x 2); hidden when the item has gone. */
  render(): void {
    const item = this.changes.socketItem();
    if (!item || item.Slots.length === 0) {
      this.changes.socketId = null;
      this.root.hidden = true;
      return;
    }
    this.root.hidden = false;
    const info = this.inv.info(item);
    this.title.textContent = info ? `Sockets: ${info.Name}` : "Sockets";
    const n = Math.min(item.Slots.length, SOCKET_GRID_SLOTS);
    const cells: HTMLElement[] = [];
    for (let i = 0; i < n; i++) {
      const slot = item.Slots[i];
      if (slot && !this.inv.info(slot)) this.requestItem(slot.ItemIndex); // GameScene.Bind
      cells.push(this.invWindow.socketCell(i));
    }
    this.grid.replaceChildren(...cells);
  }
}
