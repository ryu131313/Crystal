// Trade: TradeDialog (the user's 5 x 2 grid, gold, lock/confirm, close = cancel) + GuestTradeDialog (the partner's
// offer) + GameScene's trade handlers (TradeRequest / TradeAccept / TradeGold / TradeItem / TradeConfirm / TradeCancel).
// The user's grid, gold and lock live in Inventory (they move with the item packets); the guest side lives here.
// Request a trade with T (KeybindOptions.Trade) or the Trade button: the server pairs you with the player you face.
// The request asks with confirm() (MirMessageBox Yes/No); the gold amount with prompt() (MirAmountBox).

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { TRADE_SLOTS, type Inventory } from "../game/inventory.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { el } from "./screens.ts";

export class TradeWindow {
  /** GuestTradeDialog.GuestName / GuestGold / GuestItems. */
  guestName = "";
  guestGold = 0;
  guestItems: (UserItem | null)[] = new Array<UserItem | null>(TRADE_SLOTS).fill(null);
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => string;
  private readonly root = el("div", { class: "window trade-window", hidden: true });
  private readonly myName = el("div", { class: "trade-name" });
  private readonly myGrid = el("div", { class: "inv-grid trade-grid" });
  private readonly myGold = el("button", { class: "trade-gold", title: "Add gold" });
  private readonly lockButton = el("button", { class: "trade-lock" });
  private readonly guestLabel = el("div", { class: "trade-name" });
  private readonly guestGrid = el("div", { class: "inv-grid trade-grid" });
  private readonly guestGoldLabel = el("div", { class: "trade-gold" });

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => string) {
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.myGold.onclick = () => this.addGold();
    this.lockButton.onclick = () => { inv.toggleTradeLock(); this.render(); };
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Trade"), el("button", { class: "window-close", title: "Cancel the trade", onclick: () => this.cancel() }, "×")),
      el("div", { class: "trade-sides" },
        el("div", { class: "trade-side" }, this.myName, this.myGrid, el("div", { class: "row" }, this.myGold, this.lockButton)),
        el("div", { class: "trade-side" }, this.guestLabel, this.guestGrid, el("div", { class: "row" }, this.guestGoldLabel))),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    this.me = me;
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  /** KeybindOptions.Trade: ask the player you face. */
  request(): void {
    this.send(ClientPackets.TradeRequest());
  }

  /** TradeDialog.CloseButton: hide both dialogs and tell the server (it hands the items back, then S.TradeCancel). */
  private cancel(): void {
    this.root.hidden = true;
    this.send(ClientPackets.TradeCancel());
  }

  /** TradeDialog.GoldLabel click (MirAmountBox up to GameScene.Gold). */
  private addGold(): void {
    if (this.inv.gold <= 0) return;
    const text = prompt(`Trade Amount: (max ${this.inv.gold.toLocaleString()})`, String(this.inv.gold));
    if (text === null) return;
    const amount = Math.min(this.inv.gold, Math.trunc(Number(text.replace(/[,\s]/g, ""))));
    if (amount > 0) this.inv.offerGold(amount);
  }

  /** Server packets; returns true when handled. Inventory.handle has already updated the user's side. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "TradeRequest": {
        // GameScene.TradeRequest (MirMessageBox Yes/No)
        const yes = confirm(`Player ${p.Name} has requested to trade with you.`);
        this.send(ClientPackets.TradeReply({ AcceptInvite: yes }));
        break;
      }
      case "TradeAccept":
        // GameScene.TradeAccept -> TradeDialog.TradeAccept: open both dialogs and the inventory
        this.guestName = p.Name;
        this.root.hidden = false;
        if (!this.invWindow.visible) this.invWindow.toggle(true);
        break;
      case "TradeGold": this.guestGold = p.Amount; break;
      case "TradeItem": this.guestItems = p.TradeItems.slice(0, TRADE_SLOTS); break;
      case "TradeConfirm": this.reset(); break;
      case "TradeCancel":
        if (p.Unlock) break;
        this.reset();
        this.chat("Deal cancelled. To deal correctly you must face the other party.", ChatType.System);
        break;
      default: return false;
    }
    this.render();
    return true;
  }

  /** GuestTradeDialog.TradeReset + hide (the user's side is reset by Inventory). */
  private reset(): void {
    this.guestItems = new Array<UserItem | null>(TRADE_SLOTS).fill(null);
    this.guestName = "";
    this.guestGold = 0;
    this.root.hidden = true;
  }

  /** TradeDialog.RefreshInterface + GuestTradeDialog.RefreshInterface. Cells are column-major like Grid[2 * x + y]. */
  render(): void {
    if (this.root.hidden) return;
    this.myName.textContent = this.me();
    this.guestLabel.textContent = this.guestName;
    const order = Array.from({ length: TRADE_SLOTS }, (_, i) => 2 * (i % 5) + Math.trunc(i / 5));
    this.myGrid.replaceChildren(...order.map((slot) => this.invWindow.tradeCell(slot)));
    this.guestGrid.replaceChildren(...order.map((slot) => this.invWindow.viewCell(this.guestItems[slot] ?? null)));
    this.myGold.textContent = `Gold ${this.inv.tradeGold.toLocaleString()}`;
    this.guestGoldLabel.textContent = `Gold ${this.guestGold.toLocaleString()}`;
    this.lockButton.textContent = this.inv.tradeLocked ? "Locked ✓" : "Lock";
    this.lockButton.classList.toggle("on", this.inv.tradeLocked);
  }
}
