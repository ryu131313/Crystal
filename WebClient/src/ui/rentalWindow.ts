// Item rental between two players: ItemRentalDialog (the rented-items list + "Rent Item"), the lender's
// ItemRentingDialog (Renting cell, rental period, lock, confirm) + GuestItemRentDialog (the borrower's fee), and the
// borrower's ItemRentDialog (fee, lock) + GuestItemRentingDialog (the lender's item and period), with GameScene's
// rental handlers (RentedItems, ItemRentalRequest, ItemRentalFee, ItemRentalPeriod, DepositRentalItem,
// UpdateRentalItem, CancelItemRental, ItemRentalLock, ItemRentalPartnerLock, CanConfirmItemRental, ConfirmItemRental).
// The user's Renting cell, period, fee and locks live in ItemChanges (src/game/itemChanges.ts); the guest side here.
// "Rent Item" sends C.ItemRentalRequest: the server pairs you with the player you face (you lend, they borrow).
// prompt() stands in for MirInputBox (period) / MirAmountBox (fee).

import type { ItemRentalInformation, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, ClientPacketIds } from "../protocol/enums.generated.ts";
import { dateFromBinary, type ItemChanges } from "../game/itemChanges.ts";
import type { Inventory } from "../game/inventory.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

/** C.GetRentedItems (no body; ItemRentalDialog.RequestRentedItems). */
const getRentedItemsFrame = () => ClientPackets.GetRentedItems();

export class RentalWindow {
  /** "lend" = S.ItemRentalRequest Renting false (ItemRentingDialog + GuestItemRentDialog); "borrow" = Renting true
   *  (ItemRentDialog + GuestItemRentingDialog); null = closed. */
  mode: "lend" | "borrow" | null = null;
  /** GuestItemRentDialog / GuestItemRentingDialog state. */
  guestName = "";
  guestFee = 0;
  guestPeriod = 0;
  guestLoanItem: UserItem | null = null;
  guestGoldLocked = false;
  guestItemLocked = false;
  /** ItemRentingDialog._confirmButton.Enabled (S.CanConfirmItemRental). */
  canConfirm = false;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly changes: ItemChanges;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => string;
  private readonly requestItem: (index: number) => void;
  private readonly now: () => number;
  private nextListRequest = 0;
  // Deal window
  private readonly root = el("div", { class: "window rental-window", hidden: true });
  private readonly myPanel = el("div", { class: "trade-side" });
  private readonly guestPanel = el("div", { class: "trade-side" });
  // Rented-items list (ItemRentalDialog)
  private readonly listRoot = el("div", { class: "window rental-list-window", hidden: true });
  private readonly listRows = el("div", { class: "rental-rows" });

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, changes: ItemChanges, send: (f: Uint8Array) => void,
    chat: (text: string, type: number) => void, me: () => string, requestItem: (index: number) => void, now: () => number) {
    this.inv = inv;
    this.invWindow = invWindow;
    this.changes = changes;
    this.send = send;
    this.chat = chat;
    this.me = me;
    this.requestItem = requestItem;
    this.now = now;
    changes.refuse = (text) => chat(text, ChatType.System);
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Item Rental"), el("button", { class: "window-close", title: "Cancel the rental", onclick: () => this.send(ClientPackets.CancelItemRental()) }, "×")),
      el("div", { class: "trade-sides" }, this.myPanel, this.guestPanel));
    this.listRoot.append(
      el("div", { class: "window-title" }, el("span", {}, "Rented Items"), el("button", { class: "window-close", title: "Close", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "rental-row rental-head" }, el("span", {}, "Item"), el("span", {}, "Rented to"), el("span", {}, "Return date")),
      this.listRows,
      el("div", { class: "row" }, el("button", { title: "Lend an item to the player you face", onclick: () => this.send(ClientPackets.ItemRentalRequest()) }, "Rent Item")));
    for (const r of [this.root, this.listRoot]) {
      r.addEventListener("mousedown", (e) => e.stopPropagation());
      r.addEventListener("contextmenu", (e) => e.preventDefault());
    }
    stage.append(this.root, this.listRoot);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    // GameScene Escape: ItemRentalDialog.Hide()
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall)) this.toggle(false); }, { signal: gameSignal() });
    this.receiveRentedItems([]);
  }

  get open(): boolean {
    return !this.root.hidden;
  }

  /** KeybindOptions.Rental -> ItemRentalDialog.Toggle (asks for the list at most once a minute). */
  toggle(show = this.listRoot.hidden): void {
    this.listRoot.hidden = !show;
    if (!show) return;
    // ItemRentalDialog.RequestRentedItems
    const t = this.now();
    if (t < this.nextListRequest) return;
    this.nextListRequest = t + 60_000;
    this.send(getRentedItemsFrame());
  }

  /** ItemRentalDialog.ReceiveRentedItems: three rows (name, renting player, return date). */
  private receiveRentedItems(items: ItemRentalInformation[]): void {
    const rows: HTMLElement[] = [];
    for (let i = 0; i < 3; i++) {
      const r = items[i];
      rows.push(r
        ? el("div", { class: "rental-row" }, el("span", {}, r.ItemName), el("span", {}, r.RentingPlayerName), el("span", {}, dateFromBinary(r.ItemReturnDate).toLocaleString()))
        : el("div", { class: "rental-row empty" }, el("span", {}, "-"), el("span", {}, ""), el("span", {}, "")));
    }
    this.listRows.replaceChildren(...rows);
  }

  /** Server packets; returns true when handled. ItemChanges.handle has already updated the user's side. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "GetRentedItems": this.receiveRentedItems(p.RentedItems); return true; // GameScene.RentedItems
      case "ItemRentalRequest":
        // GameScene.ItemRentalRequest: open the pair of dialogs + the inventory, hide the list
        this.resetGuest();
        this.mode = p.Renting ? "borrow" : "lend";
        this.guestName = p.Name;
        this.canConfirm = false;
        this.root.hidden = false;
        this.listRoot.hidden = true;
        if (!this.invWindow.visible) this.invWindow.toggle(true);
        break;
      // GameScene.ItemRentalFee: the server forwards each added amount (PlayerObject.SetItemRentalFee), so it is summed here
      // (deliberate: GuestItemRentDialog.SetGuestFee shows only the last packet)
      case "ItemRentalFee": this.guestFee += p.Amount; break;
      case "ItemRentalPeriod": this.guestPeriod = p.Days; break; // GameScene.ItemRentalPeriod
      case "DepositRentalItem":
        // GameScene.DepositRentalItem: ask for the period after the first deposit
        if (p.Success && this.changes.rentalPeriod === 0 && this.changes.rentalItem) setTimeout(() => this.inputRentalPeriod(), 0);
        break;
      case "UpdateRentalItem":
        // GameScene.UpdateRentalItem (+ GameScene.Bind for the item info)
        this.guestLoanItem = p.HasData ? p.LoanItem : null;
        if (this.guestLoanItem && !this.inv.info(this.guestLoanItem)) this.requestItem(this.guestLoanItem.ItemIndex);
        break;
      case "CancelItemRental":
        // GameScene.CancelItemRental (MirMessageBox ItemRentalCancelledFaceOtherParty -> a system line)
        this.close();
        this.chat("Item rental cancelled. To complete item rental please face the other party throughout the transaction.", ChatType.System);
        break;
      case "ItemRentalLock": break; // GameScene.ItemRentalLock -> ItemRentDialog.Lock / ItemRentingDialog.Lock (flags in ItemChanges)
      case "ItemRentalPartnerLock":
        // GameScene.ItemRentalPartnerLock
        if (p.GoldLocked) this.guestGoldLocked = true;
        else if (p.ItemLocked) this.guestItemLocked = true;
        break;
      case "CanConfirmItemRental": this.canConfirm = true; break; // ItemRentingDialog.EnableConfirmButton
      case "ConfirmItemRental": this.close(); break; // GameScene.ConfirmItemRental
      default: return false;
    }
    this.render();
    return true;
  }

  /** ItemRentingDialog.Reset + ItemRentDialog.Reset (the user side is reset by ItemChanges). */
  private close(): void {
    this.resetGuest();
    this.mode = null;
    this.canConfirm = false;
    this.root.hidden = true;
  }

  private resetGuest(): void {
    this.guestName = "";
    this.guestFee = 0;
    this.guestPeriod = 0;
    this.guestLoanItem = null;
    this.guestGoldLocked = false;
    this.guestItemLocked = false;
  }

  /** ItemRentingDialog.InputRentalPeroid: MirInputBox kept open until 1..30 is entered or it is cancelled. */
  private inputRentalPeriod(): void {
    if (this.mode !== "lend" || this.changes.rentalItemLocked) return;
    const item = this.changes.rentalItem;
    const name = item ? this.inv.info(item)?.Name ?? "the item" : "the item";
    for (;;) {
      const text = prompt(`How long would you like to rent ${name} to ${this.guestName} for? (1 to 30 days).`, this.changes.rentalPeriod ? String(this.changes.rentalPeriod) : "");
      if (text === null) return;
      if (this.changes.setRentalPeriod(Number(text.trim()))) return;
    }
  }

  /** ItemRentDialog rental price button / label (MirAmountBox "Rental fee:" up to GameScene.Gold). */
  private inputFee(): void {
    if (this.inv.gold <= 0 || this.changes.rentalGoldLocked) return;
    const text = prompt(`Rental fee: (max ${this.inv.gold.toLocaleString()})`, "");
    if (text === null) return;
    this.changes.addRentalFee(Math.min(this.inv.gold, Math.trunc(Number(text.replace(/[,\s]/g, "")))));
  }

  private lockMark(locked: boolean): HTMLElement {
    return el("span", { class: `rental-lock${locked ? " on" : ""}` }, locked ? "Locked ✓" : "Unlocked");
  }

  /** The four dialogs' RefreshInterface. */
  render(): void {
    if (this.root.hidden || !this.mode) return;
    const c = this.changes;
    const fee = (n: number) => `Rental Fee: ${n.toLocaleString()}`;
    const period = (n: number) => `Rental Period: ${n} Days`;
    if (this.mode === "lend") {
      // ItemRentingDialog
      const lock = el("button", { class: `trade-lock${c.rentalItemLocked ? " on" : ""}`, disabled: c.rentalItemLocked, title: "Lock the item and period", onclick: () => c.lockItem() }, c.rentalItemLocked ? "Locked ✓" : "Lock");
      const setPeriod = el("button", { disabled: c.rentalItemLocked, onclick: () => this.inputRentalPeriod() }, "Set Period");
      const confirmBtn = el("button", { disabled: !this.canConfirm, onclick: () => this.send(ClientPackets.ConfirmItemRental()) }, "Confirm");
      this.myPanel.replaceChildren(
        el("div", { class: "trade-name" }, this.me()),
        el("div", { class: "rental-item" }, this.invWindow.rentingCell(), el("span", {}, period(c.rentalPeriod))),
        el("div", { class: "row" }, lock, setPeriod, confirmBtn));
      // GuestItemRentDialog
      this.guestPanel.replaceChildren(
        el("div", { class: "trade-name" }, this.guestName),
        el("div", { class: "trade-gold rental-line" }, fee(this.guestFee)),
        el("div", { class: "row" }, this.lockMark(this.guestGoldLocked)));
    } else {
      // ItemRentDialog
      const lock = el("button", { class: `trade-lock${c.rentalGoldLocked ? " on" : ""}`, disabled: c.rentalGoldLocked, title: "Lock the fee", onclick: () => c.lockFee() }, c.rentalGoldLocked ? "Locked ✓" : "Lock");
      const feeBtn = el("button", { class: "trade-gold", disabled: c.rentalGoldLocked, title: "Add to the rental fee", onclick: () => this.inputFee() }, fee(c.rentalGold));
      this.myPanel.replaceChildren(
        el("div", { class: "trade-name" }, this.me()),
        el("div", { class: "rental-line" }, feeBtn),
        el("div", { class: "row" }, lock));
      // GuestItemRentingDialog
      this.guestPanel.replaceChildren(
        el("div", { class: "trade-name" }, this.guestName),
        el("div", { class: "rental-item" }, this.invWindow.viewCell(this.guestLoanItem), el("span", {}, period(this.guestPeriod))),
        el("div", { class: "row" }, this.lockMark(this.guestItemLocked)));
    }
  }
}
