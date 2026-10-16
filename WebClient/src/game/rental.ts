// The guest side of an item rental between two players + the rented-items list: GameScene's rental handlers
// (RentedItems, ItemRentalRequest, ItemRentalFee, ItemRentalPeriod, DepositRentalItem, UpdateRentalItem, CancelItemRental,
// ItemRentalLock, ItemRentalPartnerLock, CanConfirmItemRental, ConfirmItemRental) with GuestItemRentDialog /
// GuestItemRentingDialog state and ItemRentingDialog._confirmButton.Enabled. The user's Renting cell, period, fee and locks
// live in ItemChanges (itemChanges.ts, which handles the same packets first). game.ts feeds handle(); the UI follows the
// events: `listed` (ItemRentalDialog.ReceiveRentedItems), `opened` (OpenItemRentalDialog / OpenItemRentDialog),
// `askPeriod` (ItemRentingDialog.InputRentalPeroid after the first deposit), `closed` (the dialogs' Reset; `cancelled`
// = the ItemRentalCancelledFaceOtherParty MirMessageBox) and `changed` (refresh).
// Deliberate difference: the guest sees the sum of the fee packets (the server forwards each added amount).

import type { ItemRentalInformation, ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import type { ItemChanges } from "./itemChanges.ts";
import type { Inventory } from "./inventory.ts";
import { Emitter } from "./emitter.ts";

/** "lend" = S.ItemRentalRequest Renting false (ItemRentingDialog + GuestItemRentDialog); "borrow" = Renting true
 *  (ItemRentDialog + GuestItemRentingDialog). */
export type RentalMode = "lend" | "borrow";

export class Rental {
  /** GameScene.RentedItems: `rentedItems` was replaced. */
  readonly listed = new Emitter<void>();
  /** GameScene.ItemRentalRequest: a deal opened on `mode` (after the state is set). */
  readonly opened = new Emitter<{ mode: RentalMode }>();
  /** GameScene.DepositRentalItem: ask for the rental period (first deposit; sent on a 0 ms timer like before). */
  readonly askPeriod = new Emitter<void>();
  /** GameScene.CancelItemRental / ConfirmItemRental: the deal's dialogs reset (`cancelled` = CancelItemRental). */
  readonly closed = new Emitter<{ cancelled: boolean }>();
  /** A deal packet was applied (last; GetRentedItems has only `listed`). */
  readonly changed = new Emitter<void>();
  private nextListRequest = 0;
  private _mode: RentalMode | null = null;
  private _guestName = "";
  private _guestFee = 0;
  private _guestPeriod = 0;
  private _guestLoanItem: UserItem | null = null;
  private _guestGoldLocked = false;
  private _guestItemLocked = false;
  private _canConfirm = false;
  private _rentedItems: ItemRentalInformation[] = [];

  /**
   * @param requestItem GameScene.Bind (C.RequestItem for a missing ItemInfo).
   * @param now performance.now() clock.
   */
  constructor(private readonly changes: ItemChanges, private readonly inv: Inventory, private readonly send: (f: Uint8Array) => void,
    private readonly requestItem: (index: number) => void, private readonly now: () => number) {}

  /** The open deal's side; null = closed. */
  get mode(): RentalMode | null { return this._mode; }
  /** GuestItemRentDialog / GuestItemRentingDialog state. */
  get guestName(): string { return this._guestName; }
  get guestFee(): number { return this._guestFee; }
  get guestPeriod(): number { return this._guestPeriod; }
  get guestLoanItem(): UserItem | null { return this._guestLoanItem; }
  get guestGoldLocked(): boolean { return this._guestGoldLocked; }
  get guestItemLocked(): boolean { return this._guestItemLocked; }
  /** ItemRentingDialog._confirmButton.Enabled (S.CanConfirmItemRental). */
  get canConfirm(): boolean { return this._canConfirm; }
  /** ItemRentalDialog: the user's rented-out items (S.GetRentedItems). */
  get rentedItems(): readonly ItemRentalInformation[] { return this._rentedItems; }

  /** ItemRentalDialog.RequestRentedItems: C.GetRentedItems at most once a minute. */
  requestList(): void {
    const t = this.now();
    if (t < this.nextListRequest) return;
    this.nextListRequest = t + 60_000;
    this.send(ClientPackets.GetRentedItems());
  }

  /** ItemRentalDialog "Rent Item": C.ItemRentalRequest (the server pairs you with the player you face). */
  request(): void {
    this.send(ClientPackets.ItemRentalRequest());
  }

  /** The deal dialogs' close buttons: C.CancelItemRental. */
  cancel(): void {
    this.send(ClientPackets.CancelItemRental());
  }

  /** ItemRentingDialog._confirmButton: C.ConfirmItemRental. */
  confirm(): void {
    this.send(ClientPackets.ConfirmItemRental());
  }

  /** Server packets (after ItemChanges.handle). Never consumes (the classic chain went on to the other handlers). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "GetRentedItems": // GameScene.RentedItems
        this._rentedItems = p.RentedItems;
        this.listed.emit();
        return false;
      case "ItemRentalRequest":
        // GameScene.ItemRentalRequest: OpenItemRentalDialog / OpenItemRentDialog
        this.resetGuest();
        this._mode = p.Renting ? "borrow" : "lend";
        this._guestName = p.Name;
        this._canConfirm = false;
        this.opened.emit({ mode: this._mode });
        break;
      // GameScene.ItemRentalFee: the server forwards each added amount (PlayerObject.SetItemRentalFee), so it is summed here
      // (deliberate: GuestItemRentDialog.SetGuestFee shows only the last packet)
      case "ItemRentalFee": this._guestFee += p.Amount; break;
      case "ItemRentalPeriod": this._guestPeriod = p.Days; break; // GameScene.ItemRentalPeriod
      case "DepositRentalItem":
        // GameScene.DepositRentalItem: ask for the period after the first deposit
        if (p.Success && this.changes.rentalPeriod === 0 && this.changes.rentalItem) setTimeout(() => this.askPeriod.emit(), 0);
        break;
      case "UpdateRentalItem":
        // GameScene.UpdateRentalItem (+ GameScene.Bind for the item info)
        this._guestLoanItem = p.HasData ? p.LoanItem : null;
        if (this._guestLoanItem && !this.inv.info(this._guestLoanItem)) this.requestItem(this._guestLoanItem.ItemIndex);
        break;
      case "CancelItemRental":
        // GameScene.CancelItemRental: Reset the dialogs + MirMessageBox ItemRentalCancelledFaceOtherParty
        this.close(true);
        break;
      case "ItemRentalLock": break; // GameScene.ItemRentalLock -> ItemRentDialog.Lock / ItemRentingDialog.Lock (flags in ItemChanges)
      case "ItemRentalPartnerLock":
        // GameScene.ItemRentalPartnerLock
        if (p.GoldLocked) this._guestGoldLocked = true;
        else if (p.ItemLocked) this._guestItemLocked = true;
        break;
      case "CanConfirmItemRental": this._canConfirm = true; break; // ItemRentingDialog.EnableConfirmButton
      case "ConfirmItemRental": this.close(false); break; // GameScene.ConfirmItemRental
      default: return false;
    }
    this.changed.emit();
    return false;
  }

  /** ItemRentingDialog.Reset + ItemRentDialog.Reset (the user side is reset by ItemChanges). */
  private close(cancelled: boolean): void {
    this.resetGuest();
    this._mode = null;
    this._canConfirm = false;
    this.closed.emit({ cancelled });
  }

  private resetGuest(): void {
    this._guestName = "";
    this._guestFee = 0;
    this._guestPeriod = 0;
    this._guestLoanItem = null;
    this._guestGoldLocked = false;
    this._guestItemLocked = false;
  }
}
