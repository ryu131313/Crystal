// The guest side of a trade: GuestTradeDialog.GuestName / GuestGold / GuestItems + GameScene's trade handlers
// (TradeRequest, TradeAccept, TradeGold, TradeItem, TradeConfirm, TradeCancel). The user's grid, gold and lock live in
// Inventory (it handles the same packets first). game.ts feeds handle(); the UI follows the events: `requested` (the
// MirMessageBox Yes/No, answered with reply()), `accepted` (TradeDialog.TradeAccept shows the dialogs), `ended`
// (TradeDialog.TradeReset hides them), `cancelled` (the DealCancelledMustFace MirMessageBox) and `changed` (refresh).

import type { ServerPacket, UserItem } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { TRADE_SLOTS } from "./inventory.ts";
import { Emitter } from "./emitter.ts";

const emptyGuestItems = (): (UserItem | null)[] => new Array<UserItem | null>(TRADE_SLOTS).fill(null);

export class Trade {
  private _guestName = "";
  private _guestGold = 0;
  private _guestItems: (UserItem | null)[] = emptyGuestItems();
  /** GameScene.TradeRequest: `name` asks to trade; answer with reply(). */
  readonly requested = new Emitter<{ name: string }>();
  /** GameScene.TradeAccept: the trade with `name` is open (after guestName is set). */
  readonly accepted = new Emitter<{ name: string }>();
  /** TradeDialog.TradeReset (S.TradeConfirm, or S.TradeCancel without Unlock): the guest side is cleared. */
  readonly ended = new Emitter<void>();
  /** GameScene.TradeCancel without Unlock (after `ended`): the DealCancelledMustFace notice. */
  readonly cancelled = new Emitter<void>();
  /** Any trade packet was applied (last). */
  readonly changed = new Emitter<void>();

  constructor(private readonly send: (f: Uint8Array) => void) {}

  /** GuestTradeDialog.GuestName / GuestGold / GuestItems (a new array on every change). */
  get guestName(): string { return this._guestName; }
  get guestGold(): number { return this._guestGold; }
  get guestItems(): readonly (UserItem | null)[] { return this._guestItems; }

  /** KeybindOptions.Trade / the Trade button: ask the player you face. */
  request(): void {
    this.send(ClientPackets.TradeRequest());
  }

  /** GameScene.TradeRequest's Yes / No. */
  reply(accept: boolean): void {
    this.send(ClientPackets.TradeReply({ AcceptInvite: accept }));
  }

  /** TradeDialog.CloseButton: the server hands the items back, then S.TradeCancel. */
  cancel(): void {
    this.send(ClientPackets.TradeCancel());
  }

  /** Server packets (after Inventory.handle). Never consumes (the classic chain went on to the other handlers). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "TradeRequest": this.requested.emit({ name: p.Name }); break;
      case "TradeAccept":
        this._guestName = p.Name;
        this.accepted.emit({ name: p.Name });
        break;
      case "TradeGold": this._guestGold = p.Amount; break;
      case "TradeItem": this._guestItems = p.TradeItems.slice(0, TRADE_SLOTS); break;
      case "TradeConfirm": this.reset(); break;
      case "TradeCancel":
        if (p.Unlock) break;
        // GameScene.TradeCancel: TradeReset + MirMessageBox (DealCancelledMustFace)
        this.reset();
        this.cancelled.emit();
        break;
      default: return false;
    }
    this.changed.emit();
    return false;
  }

  /** GuestTradeDialog.TradeReset (the user's side is reset by Inventory). */
  private reset(): void {
    this._guestItems = emptyGuestItems();
    this._guestName = "";
    this._guestGold = 0;
    this.ended.emit();
  }
}
