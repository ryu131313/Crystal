// The user's mailbox: UserObject.Mail + GameScene.NewMail and GameScene's mail handlers (ReceiveMail, MailLockedItem,
// MailSendRequest, MailSent, ParcelCollected, MailCost; Client/MirScenes/GameScene.cs) + the mail requests the
// MailDialogs send (Client/MirScenes/Dialogs/MailDialogs.cs). The parcel being composed (its cells, gold, stamp) and its
// locks stay in the UI. game.ts feeds handle(); the UI follows `changed` (the list was replaced) and the events
// `sendRequest` (an NPC asks for a parcel recipient), `sent`, `collected` and `cost`.

import type { ServerPacket, ClientMail } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { dateFromBinary } from "./itemChanges.ts";
import { Emitter } from "./emitter.ts";

/** MailComposeParcelDialog._cellCount (the ItemsIdx length of C.SendMail / C.MailCost). */
export const MAIL_PARCEL_CELLS = 5;

/** A parcel: gold or items (MailListDialog / MailReadParcelDialog). */
export const isParcel = (m: ClientMail): boolean => m.Gold > 0 || m.Items.length > 0;

export class MailBox {
  private _mail: ClientMail[] = [];
  private _newMail = false;
  /** GameScene.ReceiveMail: the list was replaced (after `newMail`). */
  readonly changed = new Emitter<void>();
  /** GameScene.MailSendRequest: ask for the recipient, then compose a parcel (MailComposeParcelDialog.ComposeMail). */
  readonly sendRequest = new Emitter<void>();
  /** GameScene.MailSent: `result` -1 = refused (the model posted the chat line), else sent (unlock the cells, close). */
  readonly sent = new Emitter<{ result: number }>();
  /** GameScene.ParcelCollected: -1 nothing to collect, 0 all collected, 1 collected (the parcel dialog closes). */
  readonly collected = new Emitter<{ result: number }>();
  /** GameScene.MailCost: the postage of the parcel being composed. */
  readonly cost = new Emitter<{ cost: number }>();

  constructor(private readonly send: (f: Uint8Array) => void, private readonly chat: (text: string, type: number) => void,
    private readonly inv: Inventory, private readonly requestItem: (index: number) => void) {}

  /** UserObject.Mail (unlocked first, then newest first, like GameScene.ReceiveMail). */
  get list(): readonly ClientMail[] { return this._mail; }

  /** GameScene.NewMail: any unopened mail (MiniMapDialog blinks the NewMail icon). */
  get newMail(): boolean { return this._newMail; }

  /** The current mail object with this MailID (the list is replaced on every S.ReceiveMail). */
  find(mailId: bigint): ClientMail | null {
    return this._mail.find((m) => m.MailID === mailId) ?? null;
  }

  /** MailReadLetterDialog / MailReadParcelDialog.ReadMail: C.ReadMail when unopened. */
  read(m: ClientMail): void {
    if (!m.Opened) this.send(ClientPackets.ReadMail({ MailID: m.MailID }));
  }

  /** MailListDialog / MailReadLetterDialog DeleteButton (callers refuse a locked mail). */
  delete(mailId: bigint): void {
    this.send(ClientPackets.DeleteMail({ MailID: mailId }));
  }

  /** MailReadLetterDialog.LockButton: flips the mail's lock locally and tells the server. */
  toggleLock(m: ClientMail): void {
    m.Locked = !m.Locked;
    this.send(ClientPackets.LockMail({ MailID: m.MailID, Lock: m.Locked }));
  }

  /** MailReadParcelDialog.CollectButton. */
  collect(mailId: bigint): void {
    this.send(ClientPackets.CollectParcel({ MailID: mailId }));
  }

  /** MailComposeLetterDialog / MailComposeParcelDialog SendButton (`itemsIdx`: MAIL_PARCEL_CELLS UniqueIDs, 0 = empty). */
  sendMail(name: string, message: string, gold = 0, itemsIdx: bigint[] = new Array<bigint>(MAIL_PARCEL_CELLS).fill(0n), stamped = false): void {
    this.send(ClientPackets.SendMail({ Name: name, Message: message, Gold: gold, ItemsIdx: itemsIdx, Stamped: stamped }));
  }

  /** MailComposeParcelDialog.CalculatePostage (S.MailCost answers). */
  queryCost(gold: number, itemsIdx: bigint[], stamped: boolean): void {
    this.send(ClientPackets.MailCost({ Gold: gold, ItemsIdx: itemsIdx, Stamped: stamped }));
  }

  /** MailComposeParcelDialog.ResetLockedCells: ask the server to unlock a cell item (it echoes S.MailLockedItem). */
  unlockItem(uniqueId: bigint): void {
    this.send(ClientPackets.MailLockedItem({ UniqueID: uniqueId, Locked: false }));
  }

  /** Server packets; true = consumed (the classic chain returned on every mail packet). */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "ReceiveMail": this.receiveMail(p.Mail); return true;
      case "MailLockedItem": {
        // GameScene.MailLockedItem: InventoryDialog.GetCell(UniqueID).Locked = Locked
        const item = this.inv.inventory.find((i) => i?.UniqueID === p.UniqueID);
        if (item) this.inv.setLocked(item, p.Locked);
        return true;
      }
      case "MailSendRequest": this.sendRequest.emit(); return true;
      case "MailSent":
        if (p.Result === -1) {
          // deliberate: the desktop closes the dialog silently; the UI keeps it open so the message is not lost
          this.chat("The parcel could not be sent (not enough gold for the postage, or an item cannot be mailed).", ChatType.System);
        }
        this.sent.emit({ result: p.Result });
        return true;
      case "ParcelCollected": this.collected.emit({ result: p.Result }); return true;
      case "MailCost": this.cost.emit({ cost: p.Cost }); return true; // the gold sound is not played
      default: return false;
    }
  }

  // GameScene.ReceiveMail: replace the list (unlocked first, then newest first), flag new mail, refresh the list
  private receiveMail(list: ClientMail[]): void {
    const time = (m: ClientMail) => dateFromBinary(m.DateSent).getTime();
    this._mail = [...list].sort((a, b) => (Number(!b.Locked) - Number(!a.Locked)) || (time(b) - time(a)));
    for (const m of this._mail) for (const item of m.Items) this.requestItem(item.ItemIndex); // GameScene.Bind
    this._newMail = this._mail.some((m) => !m.Opened);
    this.changed.emit();
  }
}
