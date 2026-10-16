// The user's mailbox: UserObject.Mail + GameScene.NewMail and GameScene's mail handlers (ReceiveMail, MailLockedItem,
// MailSendRequest, MailSent, ParcelCollected, MailCost; Client/MirScenes/GameScene.cs) + the mail requests the
// MailDialogs send (Client/MirScenes/Dialogs/MailDialogs.cs). The parcel being composed (its cells, gold, stamp) and its
// locks stay in the UI. game.ts feeds handle(); the UI follows `changed` (the list was replaced) and the events
// `sendRequest` (an NPC asks for a parcel recipient), `sent`, `collected` and `cost`.
// Web batch requests (phase 2.1, no desktop sender): C.CollectAllParcels and C.ReadMails (one S.ReceiveMail answers).
// Sent folder (phase 2.6, web only): C.RequestSentMail / C.HideSentMail -> S.SentMail (`sentList`, event `sentChanged`).
// A row shows only while the mail is still in the recipient's box and the sender has not hidden it.

import type { ServerPacket, ClientMail, ClientSentMail } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { Inventory } from "./inventory.ts";
import { dateFromBinary } from "./itemChanges.ts";
import { Emitter } from "./emitter.ts";

/** MailComposeParcelDialog._cellCount (the ItemsIdx length of C.SendMail / C.MailCost). */
export const MAIL_PARCEL_CELLS = 5;
/** PlayerObject.ReadMails cap: the server reads at most this many MailIDs from one C.ReadMails. */
export const MAX_READ_MAILS = 100;
/** PlayerObject.RequestSentMail's cooldown (the server drops faster requests silently). */
export const SENT_MAIL_DELAY = 2000;
/** PlayerObject.HideSentMail's cooldown (the server drops faster requests silently, with no reply). */
export const HIDE_SENT_MAIL_DELAY = 500;

/** A parcel: gold or items (MailListDialog / MailReadParcelDialog). */
export const isParcel = (m: ClientMail): boolean => m.Gold > 0 || m.Items.length > 0;

export class MailBox {
  private _mail: ClientMail[] = [];
  private _newMail = false;
  private _sent: ClientSentMail[] = [];
  private sentTime = 0; // SENT_MAIL_DELAY gate for requestSent()
  private hideTime = 0; // HIDE_SENT_MAIL_DELAY gate for hideSent()
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
  /** S.SentMail: the sent folder was replaced (`sentList`). */
  readonly sentChanged = new Emitter<void>();

  constructor(private readonly send: (f: Uint8Array) => void, private readonly chat: (text: string, type: number) => void,
    private readonly inv: Inventory, private readonly requestItem: (index: number) => void,
    private readonly now: () => number = () => performance.now()) {}

  /** UserObject.Mail (unlocked first, then newest first, like GameScene.ReceiveMail). */
  get list(): readonly ClientMail[] { return this._mail; }

  /** The sent folder (S.SentMail: newest first, at most 50; empty until requestSent() is answered). */
  get sentList(): readonly ClientSentMail[] { return this._sent; }

  /** C.RequestSentMail (web): ask for the sent folder. One request per SENT_MAIL_DELAY; false = too soon, nothing sent.
   *  Answer: `sentChanged`. */
  requestSent(): boolean {
    const t = this.now();
    if (t < this.sentTime) return false;
    this.sentTime = t + SENT_MAIL_DELAY;
    this.send(ClientPackets.RequestSentMail());
    return true;
  }

  /** C.HideSentMail (web): remove one mail this player sent from the sent folder (the recipient keeps it). The row goes
   *  at once; the server answers with a fresh S.SentMail. One request per HIDE_SENT_MAIL_DELAY. False = not in the
   *  folder or too soon, nothing sent. */
  hideSent(mailId: bigint): boolean {
    const t = this.now();
    if (t < this.hideTime) return false;
    const i = this._sent.findIndex((m) => m.MailID === mailId);
    if (i < 0) return false;
    this.hideTime = t + HIDE_SENT_MAIL_DELAY;
    this._sent.splice(i, 1);
    this.sentChanged.emit();
    this.send(ClientPackets.HideSentMail({ MailID: mailId }));
    return true;
  }

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

  /** C.ReadMails (web batch): marks the listed mails read; only unopened mails in the list are sent (repeats dropped, at
   *  most MAX_READ_MAILS). Returns the number sent (0 = nothing sent). */
  readMails(mailIds: readonly bigint[]): number {
    const ids: bigint[] = [];
    for (let i = 0; i < mailIds.length && ids.length < MAX_READ_MAILS; i++) {
      const id = mailIds[i]!;
      if (ids.includes(id)) continue;
      const m = this.find(id);
      if (m && !m.Opened) ids.push(id);
    }
    if (ids.length > 0) this.send(ClientPackets.ReadMails({ MailIDs: ids }));
    return ids.length;
  }

  /** True when a mail holds a parcel that can be collected here (ClientMail.Collected = ready; CollectAllParcels' rule). */
  get hasParcelsToCollect(): boolean {
    for (let i = 0; i < this._mail.length; i++) {
      const m = this._mail[i]!;
      if (m.Collected && isParcel(m)) return true;
    }
    return false;
  }

  /** C.CollectAllParcels (web batch): collect every ready parcel (the server stops at the first full bag, with its chat
   *  line). False = nothing to collect, nothing sent. S.ParcelCollected + one S.ReceiveMail answer. */
  collectAllParcels(): boolean {
    if (!this.hasParcelsToCollect) return false;
    this.send(ClientPackets.CollectAllParcels());
    return true;
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
      case "SentMail": this._sent = p.Mails.slice(); this.sentChanged.emit(); return true; // web (phase 2.6)
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
