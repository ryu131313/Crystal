// Item rental between two players with the desktop artwork:
//   ItemRentalDialog (Client/MirScenes/Dialogs/ItemRentalDialog.cs): Prguse3 1 (400 x 174, centred), title Prguse3 0, the
//     Rented / Borrowed tabs (Prguse3 2 / 3, disabled), three rows (item, renting player, return date) and "Rent Item"
//     (Prguse3 4-6, C.ItemRentalRequest: the server pairs you with the player you face; you lend, they borrow).
//   ItemRentingDialog + GuestItemRentingDialog (ItemRentingDialog.cs): Prguse 238 at (718, 287): the lender's Renting
//     cell, rental period (MirInputBox), lock (Prguse 250-253) and confirm (Prguse3 10-12); the borrower's copy shows
//     the lender's item and period with every button disabled.
//   ItemRentDialog + GuestItemRentDialog (ItemRentDialog.cs): Prguse 238 at (718, 163): the borrower's fee (Prguse 28
//     button or the label: MirAmountBox) and lock; the lender's copy shows the borrower's fee.
// With GameScene's rental handlers (RentedItems, ItemRentalRequest, ItemRentalFee, ItemRentalPeriod, DepositRentalItem,
// UpdateRentalItem, CancelItemRental, ItemRentalLock, ItemRentalPartnerLock, CanConfirmItemRental, ConfirmItemRental).
// The user's Renting cell, period, fee and locks live in ItemChanges (src/game/itemChanges.ts); the guest side here.
// Escape hides the rented-items list (GameScene Closeall: ItemRentalDialog.Hide()).
// Deliberate difference: the rental fee sends only the amount added (ItemChanges.addRentalFee) and the guest sees the
// sum of the fee packets.

import type { ItemRentalInformation, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { dateFromBinary, type ItemChanges } from "../game/itemChanges.ts";
import type { Inventory } from "../game/inventory.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, Lib, closeButton, SCREEN_HEIGHT, SCREEN_WIDTH } from "./mir/controls.ts";
import { messageBox, inputBox, amountBox } from "./messageBox.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";

const T = { // ClientTextKeys (English.json)
  RentalFee: "Rental fee:",
  RentalFeeGoldAmount: (n: number) => `Rental Fee: ${n.toLocaleString("en-US")}`,
  RentItemDurationQuestion: (item: string, name: string) => `How long would you like to rent ${item} to ${name} for? (1 to 30 days).`,
  RentalPeriodDays: (n: number) => `Rental Period: ${n} Days`,
  ItemRentalCancelledFaceOtherParty: "Item rental cancelled.\nTo complete item rental please face the other party throughout the transaction.",
};

/** The four deal dialogs' size (Size = 204 x 109) and Locations. */
const DEAL_SIZE: [number, number] = [204, 109];
const RENT_Y = DEAL_SIZE[1] + Math.trunc(DEAL_SIZE[1] / 2); // ItemRentDialog / GuestItemRentDialog
const RENTING_Y = DEAL_SIZE[1] * 2 + Math.trunc(DEAL_SIZE[1] / 2) + 15; // ItemRentingDialog / GuestItemRentingDialog
const DEAL_X = SCREEN_WIDTH - DEAL_SIZE[0] - Math.trunc(DEAL_SIZE[0] / 2);

/** DateTime.ToString(CultureInfo.InvariantCulture): "MM/dd/yyyy HH:mm:ss". */
function invariantDate(d: Date): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getMonth() + 1)}/${p(d.getDate())}/${d.getFullYear()} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

const dealWindow = (y: number) => new MirWindow({ library: Lib.Prguse, index: 238, size: DEAL_SIZE, movable: true, sort: true, location: [DEAL_X, y] });
const nameLabel = (win: MirWindow) => new MirLabel({ parent: win, location: [30, 8], size: [150, 14], center: true, notControl: true });

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
  private readonly stage: HTMLElement;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly changes: ItemChanges;
  private readonly send: (f: Uint8Array) => void;
  private readonly me: () => string;
  private readonly requestItem: (index: number) => void;
  private readonly now: () => number;
  private nextListRequest = 0;

  // ItemRentalDialog
  private readonly listWin: MirWindow;
  private readonly rows: { row: MirImage; labels: MirLabel[] }[] = [];
  // ItemRentingDialog
  private readonly renting: { win: MirWindow; name: MirLabel; period: MirLabel; lock: MirButton; setPeriod: MirButton; confirm: MirButton };
  // GuestItemRentingDialog
  private readonly guestRenting: { win: MirWindow; name: MirLabel; period: MirLabel; lock: MirButton; cellLayer: HTMLDivElement };
  /** The guest item cell's item + whether its ItemInfo had arrived (GameScene.Bind), to rebuild the read-only cell. */
  private guestCellKey = "";
  // ItemRentDialog
  private readonly rent: { win: MirWindow; name: MirLabel; price: MirLabel; lock: MirButton; priceButton: MirButton };
  // GuestItemRentDialog
  private readonly guestRent: { win: MirWindow; name: MirLabel; price: MirLabel; lock: MirButton };

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, changes: ItemChanges, send: (f: Uint8Array) => void,
    chat: (text: string, type: number) => void, me: () => string, requestItem: (index: number) => void, now: () => number) {
    this.stage = stage;
    this.inv = inv;
    this.invWindow = invWindow;
    this.changes = changes;
    this.send = send;
    this.me = me;
    this.requestItem = requestItem;
    this.now = now;
    changes.refuse = (text) => chat(text, ChatType.System);
    const cancel = () => this.send(ClientPackets.CancelItemRental());

    // ItemRentalDialog ctor
    this.listWin = new MirWindow({ library: Lib.Prguse3, index: 1, size: [400, 174], movable: true, sort: true,
      location: [Math.trunc((SCREEN_WIDTH - 400) / 2), Math.trunc((SCREEN_HEIGHT - 174) / 2)] });
    new MirImage({ parent: this.listWin, library: Lib.Prguse3, index: 0, location: [22, 8] });
    new MirButton({ parent: this.listWin, library: Lib.Prguse3, index: 2, hoverIndex: 2, pressedIndex: 2, location: [8, 32], size: [72, 23], sound: SoundList.ButtonA, enabled: false });
    new MirButton({ parent: this.listWin, library: Lib.Prguse3, index: 3, location: [81, 32], size: [84, 23], sound: SoundList.ButtonA, enabled: false });
    new MirButton({ parent: this.listWin, library: Lib.Prguse3, index: 4, hoverIndex: 5, pressedIndex: 6, location: [295, 144], size: [85, 29], sound: SoundList.ButtonA,
      onClick: () => this.send(ClientPackets.ItemRentalRequest()) });
    closeButton(this.listWin, [375, 3], () => this.toggle());
    for (let i = 0; i < 3; i++) {
      // ItemRow: three 128 x 20 labels, HorizontalCenter
      const row = new MirImage({ parent: this.listWin, location: [0, 78 + i * 21], size: [383, 21], visible: false });
      const labels = [5, 137, 264].map((x) => new MirLabel({ parent: row, location: [x, 0], size: [128, 20], align: "center", notControl: true }));
      this.rows.push({ row, labels });
    }

    // ItemRentingDialog ctor
    {
      const win = dealWindow(RENTING_Y);
      const confirm = new MirButton({ parent: win, library: Lib.Prguse3, index: 10, hoverIndex: 11, pressedIndex: 12, location: [130, 76], size: [58, 28], sound: SoundList.ButtonA, enabled: false,
        onClick: () => this.send(ClientPackets.ConfirmItemRental()) });
      closeButton(win, [180, 3], cancel);
      const lock = new MirButton({ parent: win, library: Lib.Prguse, index: 250, hoverIndex: 251, pressedIndex: 252, location: [18, 76], size: [28, 25], sound: SoundList.ButtonA,
        onClick: () => this.changes.lockItem() });
      const setPeriod = new MirButton({ parent: win, library: Lib.Prguse3, index: 7, hoverIndex: 8, pressedIndex: 9, location: [46, 76], size: [84, 28], sound: SoundList.ButtonA,
        onClick: () => this.inputRentalPeriod() });
      const name = nameLabel(win);
      const period = new MirLabel({ parent: win, location: [60, 42], size: [150, 14], vCenter: true, notControl: true });
      // ItemCell (MirGridType.Renting, BorderColour Lime)
      placeCell(invWindow.boundCell("renting", 0), win, [16, 35]);
      this.renting = { win, name, period, lock, setPeriod, confirm };
    }
    // GuestItemRentingDialog ctor
    {
      const win = dealWindow(RENTING_Y);
      const lock = new MirButton({ parent: win, library: Lib.Prguse, index: 250, location: [18, 76], size: [28, 25], enabled: false });
      new MirButton({ parent: win, library: Lib.Prguse3, index: 7, location: [46, 76], size: [84, 28], enabled: false });
      new MirButton({ parent: win, library: Lib.Prguse3, index: 10, location: [130, 76], size: [58, 28], enabled: false });
      const name = nameLabel(win);
      const period = new MirLabel({ parent: win, location: [60, 42], size: [150, 14], vCenter: true, notControl: true, text: T.RentalPeriodDays(0) });
      // _guestItemCell (MirGridType.GuestRenting): a read-only cell
      const cellLayer = document.createElement("div");
      win.el.append(cellLayer);
      this.guestRenting = { win, name, period, lock, cellLayer };
    }
    // ItemRentDialog ctor
    {
      const win = dealWindow(RENT_Y);
      closeButton(win, [180, 3], cancel);
      const lock = new MirButton({ parent: win, library: Lib.Prguse, index: 250, hoverIndex: 251, pressedIndex: 252, location: [22, 76], size: [28, 25], sound: SoundList.ButtonA,
        onClick: () => this.changes.lockFee() });
      const priceButton = new MirButton({ parent: win, library: Lib.Prguse, index: 28, location: [18, 46], size: [32, 17], sound: SoundList.Gold, onClick: () => this.inputFee() });
      const name = nameLabel(win);
      // _rentalPriceLabel.Click (left button): the same MirAmountBox
      const price = new MirLabel({ parent: win, location: [60, 42], size: [150, 14], vCenter: true, onClick: () => this.inputFee() });
      this.rent = { win, name, price, lock, priceButton };
    }
    // GuestItemRentDialog ctor
    {
      const win = dealWindow(RENT_Y);
      const lock = new MirButton({ parent: win, library: Lib.Prguse, index: 250, location: [22, 76], size: [28, 25], enabled: false });
      new MirButton({ parent: win, library: Lib.Prguse, index: 28, location: [18, 46], size: [32, 17], enabled: false });
      const name = nameLabel(win);
      const price = new MirLabel({ parent: win, location: [60, 42], size: [150, 14], vCenter: true, notControl: true });
      this.guestRent = { win, name, price, lock };
    }
    for (const w of [this.renting.win, this.guestRenting.win, this.rent.win, this.guestRent.win, this.listWin]) w.el.addEventListener("contextmenu", (e) => e.preventDefault());

    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    const signal = gameSignal();
    // GameScene Escape: ItemRentalDialog.Hide()
    window.addEventListener("keydown", (e) => { if (matches(e, KeybindOptions.Closeall)) this.toggle(false); }, { signal });
    signal.addEventListener("abort", () => {
      for (const w of [this.renting.win, this.guestRenting.win, this.rent.win, this.guestRent.win, this.listWin]) w.dispose();
    });
    this.receiveRentedItems([]);
  }

  get open(): boolean {
    return this.mode !== null;
  }

  /** KeybindOptions.Rental -> ItemRentalDialog.Toggle (asks for the list at most once a minute). */
  toggle(show = !this.listWin.visible): void {
    if (!show) { this.listWin.hide(); return; }
    this.listWin.show();
    // ItemRentalDialog.RequestRentedItems
    const t = this.now();
    if (t < this.nextListRequest) return;
    this.nextListRequest = t + 60_000;
    this.send(ClientPackets.GetRentedItems());
  }

  /** ItemRentalDialog.ReceiveRentedItems: three rows (name, renting player, return date); empty rows hide. */
  private receiveRentedItems(items: ItemRentalInformation[]): void {
    for (let i = 0; i < this.rows.length; i++) {
      const { row, labels } = this.rows[i]!;
      const r = items[i];
      row.visible = !!r;
      labels[0]!.text = r ? r.ItemName : "";
      labels[1]!.text = r ? r.RentingPlayerName : "";
      labels[2]!.text = r ? invariantDate(dateFromBinary(r.ItemReturnDate)) : "";
    }
  }

  /** Server packets; returns true when handled. ItemChanges.handle has already updated the user's side. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "GetRentedItems": this.receiveRentedItems(p.RentedItems); return true; // GameScene.RentedItems
      case "ItemRentalRequest":
        // GameScene.ItemRentalRequest: OpenItemRentalDialog / OpenItemRentDialog (+ the inventory), hide the list
        this.resetGuest();
        this.mode = p.Renting ? "borrow" : "lend";
        this.guestName = p.Name;
        this.canConfirm = false;
        this.listWin.hide();
        if (!this.invWindow.visible) this.invWindow.toggle(true);
        if (this.mode === "lend") { this.renting.win.show(); this.guestRent.win.show(); }
        else { this.rent.win.show(); this.guestRenting.win.show(); }
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
        // GameScene.CancelItemRental: Reset the dialogs + MirMessageBox ItemRentalCancelledFaceOtherParty
        this.close();
        messageBox(this.stage, T.ItemRentalCancelledFaceOtherParty);
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
    for (const w of [this.renting.win, this.guestRenting.win, this.rent.win, this.guestRent.win]) w.hide();
  }

  private resetGuest(): void {
    this.guestName = "";
    this.guestFee = 0;
    this.guestPeriod = 0;
    this.guestLoanItem = null;
    this.guestGoldLocked = false;
    this.guestItemLocked = false;
  }

  /** ItemRentingDialog.InputRentalPeroid: MirInputBox, kept open until 1..30 is entered or it is cancelled. */
  private inputRentalPeriod(): void {
    if (this.mode !== "lend" || this.changes.rentalItemLocked) return;
    const item = this.changes.rentalItem;
    const name = item ? this.inv.info(item)?.Name ?? "" : "";
    inputBox(this.stage, T.RentItemDurationQuestion(name, this.guestName), "", (text) => (this.changes.setRentalPeriod(Number(text.trim())) ? undefined : false));
  }

  /** ItemRentDialog rental price button / label: MirAmountBox (RentalFee, Items 116, up to GameScene.Gold). */
  private inputFee(): void {
    if (this.invWindow.pickedInventorySlot >= 0 || this.invWindow.heroPick || this.inv.gold <= 0 || this.changes.rentalGoldLocked) return;
    amountBox(T.RentalFee, 116, this.inv.gold, (n) => { if (n > 0) this.changes.addRentalFee(n); });
  }

  /** Lock (Prguse 250-252) / locked (253, disabled). */
  private static setLock(b: MirButton, locked: boolean, interactive: boolean): void {
    if (locked) b.setIndices(253);
    else if (interactive) b.setIndices(250, 251, 252);
    else b.setIndices(250);
  }

  /** The four dialogs' RefreshInterface. */
  render(): void {
    if (!this.mode) return;
    const c = this.changes;
    if (this.mode === "lend") {
      // ItemRentingDialog
      const r = this.renting;
      r.name.text = this.me();
      r.period.text = T.RentalPeriodDays(c.rentalPeriod);
      RentalWindow.setLock(r.lock, c.rentalItemLocked, true);
      r.lock.enabled = !c.rentalItemLocked;
      r.setPeriod.enabled = !c.rentalItemLocked;
      r.confirm.enabled = this.canConfirm;
      // GuestItemRentDialog
      const g = this.guestRent;
      g.name.text = this.guestName;
      g.price.text = T.RentalFeeGoldAmount(this.guestFee);
      RentalWindow.setLock(g.lock, this.guestGoldLocked, false);
    } else {
      // ItemRentDialog
      const r = this.rent;
      r.name.text = this.me();
      r.price.text = T.RentalFeeGoldAmount(c.rentalGold);
      RentalWindow.setLock(r.lock, c.rentalGoldLocked, true);
      r.lock.enabled = !c.rentalGoldLocked;
      r.priceButton.enabled = !c.rentalGoldLocked;
      // GuestItemRentingDialog
      const g = this.guestRenting;
      g.name.text = this.guestName;
      g.period.text = T.RentalPeriodDays(this.guestPeriod);
      RentalWindow.setLock(g.lock, this.guestItemLocked, false);
      const item = this.guestLoanItem;
      const key = item ? `${item.UniqueID}:${item.Count}:${!!this.inv.info(item)}` : "-";
      if (key !== this.guestCellKey) {
        this.guestCellKey = key;
        g.cellLayer.replaceChildren();
        placeCell(this.invWindow.viewCell(this.guestLoanItem), g.cellLayer, [16, 35]);
      }
    }
  }
}
