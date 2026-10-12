// Mail: MailListDialog (inbox, 10 rows per page, read / reply / delete), MailReadLetterDialog + MailReadParcelDialog
// (one read window here; delete / lock on a letter, collect on a parcel), MailComposeLetterDialog, MailComposeParcelDialog
// (recipient, message, gold, stamp, up to 5 inventory items with the mail-locked-item flow and the C.MailCost query) and
// GameScene's mail handlers (ReceiveMail / MailLockedItem / MailSendRequest / MailSent / ParcelCollected / MailCost).
// The list opens from the HUD Mail button (MiniMapDialog.MailButton); parcels are composed only after S.MailSendRequest
// (an NPC script), like the desktop. MirInputBox / MirAmountBox / MirMessageBox Yes-No are prompt() / confirm();
// the ParcelCollected answers are a non-blocking messageBox.

import type { ServerPacket, UserItem, ClientMail } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType, ItemType, BindMode } from "../protocol/enums.generated.ts";
import type { Inventory } from "../game/inventory.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { itemIcon } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";

const ROWS = 10; // MailListDialog.Rows
const PARCEL_CELLS = 5; // MailComposeParcelDialog._cellCount
const MAX_MESSAGE = 500; // PlayerObject.SendMail refuses longer messages

/** Mail.DateSent.ToString("dd/MM/yy H:mm:ss"). */
function formatDate(v: bigint): string {
  const d = dateFromBinary(v), p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${p(d.getFullYear() % 100)} ${d.getHours()}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

const isParcel = (m: ClientMail) => m.Gold > 0 || m.Items.length > 0;

function windowTitle(text: string, close: () => void): HTMLElement {
  return el("div", { class: "window-title" }, el("span", {}, text), el("button", { class: "window-close", onclick: close }, "×"));
}

export class MailWindow {
  /** UserObject.Mail (sorted like GameScene.ReceiveMail). */
  mail: ClientMail[] = [];
  /** GameScene.NewMail: any unopened mail (MiniMapDialog blinks the NewMail icon). */
  newMail = false;
  /** The HUD Mail button (MiniMapDialog.MailButton); blinks while there is new mail. game.ts places it. */
  readonly hudButton: HTMLButtonElement;

  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly requestItem: (index: number) => void;
  private readonly stage: HTMLElement;

  // MailListDialog
  private readonly listRoot = el("div", { class: "window mail-window", hidden: true });
  private readonly rows = el("div", { class: "mail-rows" });
  private readonly pageLabel = el("span", { class: "mail-page" });
  private readonly replyButton = el("button", { onclick: () => this.reply() }, "Reply");
  private selected: ClientMail | null = null;
  private startIndex = 0;
  private currentPage = 1;
  private pageCount = 1;

  // MailReadLetterDialog / MailReadParcelDialog
  private readonly readRoot = el("div", { class: "window mail-read", hidden: true });
  private readonly readTitle = el("span", {});
  private readonly readSender = el("div", {});
  private readonly readDate = el("div", {});
  private readonly readMessage = el("div", { class: "mail-message" });
  private readonly readParcel = el("div", { class: "mail-parcel" });
  private readonly readGold = el("span", { class: "trade-gold" });
  private readonly readCells = el("div", { class: "inv-grid mail-cells" });
  private readonly readLetterButtons = el("div", { class: "row" });
  private readonly readParcelButtons = el("div", { class: "row" });
  private readonly lockButton = el("button", { onclick: () => this.toggleLock() }, "Lock");
  private readonly collectButton = el("button", { onclick: () => this.collect() }, "Collect");
  private reading: ClientMail | null = null;

  // MailComposeLetterDialog
  private readonly letterRoot = el("div", { class: "window mail-compose", hidden: true });
  private readonly letterTo = el("div", { class: "mail-to" });
  private readonly letterText = el("textarea", { class: "mail-text", rows: 8, maxLength: MAX_MESSAGE }) as HTMLTextAreaElement;

  // MailComposeParcelDialog
  private readonly parcelRoot = el("div", { class: "window mail-compose mail-compose-parcel", hidden: true });
  private readonly parcelTo = el("div", { class: "mail-to" });
  private readonly parcelText = el("textarea", { class: "mail-text", rows: 6, maxLength: MAX_MESSAGE }) as HTMLTextAreaElement;
  private readonly stampButton = el("button", { onclick: () => this.stampParcel() }, "Stamp");
  private readonly costLabel = el("span", { class: "trade-gold" });
  private readonly goldButton = el("button", { class: "trade-gold", title: "Add gold", onclick: () => this.addGold() });
  private readonly parcelCells = el("div", { class: "inv-grid mail-cells" });
  private letterName = "";
  private parcelName = "";
  /** MailComposeParcelDialog.Items / ItemsIdx / GiftGoldAmount / Stamped. */
  private readonly items: (UserItem | null)[] = new Array<UserItem | null>(PARCEL_CELLS).fill(null);
  private giftGold = 0;
  private stamped = false;
  private retryRender = 0;

  constructor(stage: HTMLElement, inv: Inventory, invWindow: InventoryWindow, send: (f: Uint8Array) => void,
    chat: (text: string, type: number) => void, requestItem: (index: number) => void) {
    this.stage = stage;
    this.inv = inv;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.requestItem = requestItem;

    this.hudButton = el("button", { title: "Mail", onclick: () => this.toggle() }, "Mail") as HTMLButtonElement;

    this.listRoot.append(
      windowTitle("Mail", () => this.toggle(false)),
      el("div", { class: "mail-head" }, el("span", {}, "Type"), el("span", {}, "Sender"), el("span", {}, "Message")),
      this.rows,
      el("div", { class: "row mail-pager" },
        el("button", { onclick: () => this.page(-1) }, "‹"), this.pageLabel, el("button", { onclick: () => this.page(1) }, "›")),
      el("div", { class: "row" },
        el("button", { title: "Write a letter", onclick: () => this.sendClick() }, "Send"),
        this.replyButton,
        el("button", { onclick: () => { if (this.selected) this.read(this.selected); } }, "Read"),
        el("button", { onclick: () => this.deleteSelected() }, "Delete")),
    );

    this.readLetterButtons.append(
      el("button", { onclick: () => this.deleteReading() }, "Delete"), this.lockButton,
      el("button", { onclick: () => this.closeRead() }, "Cancel"));
    this.readParcelButtons.append(this.collectButton, el("button", { onclick: () => this.closeRead() }, "Cancel"));
    this.readParcel.append(el("div", { class: "row" }, el("span", {}, "Gold"), this.readGold), this.readCells);
    this.readRoot.append(
      el("div", { class: "window-title" }, this.readTitle, el("button", { class: "window-close", onclick: () => this.closeRead() }, "×")),
      this.readSender, this.readDate, this.readMessage, this.readParcel, this.readLetterButtons, this.readParcelButtons);

    this.letterRoot.append(
      windowTitle("Letter", () => this.hideLetter()), this.letterTo, this.letterText,
      el("div", { class: "row" },
        el("button", { class: "primary", onclick: () => this.sendLetter() }, "Send"),
        el("button", { onclick: () => this.hideLetter() }, "Cancel")));

    this.parcelRoot.append(
      windowTitle("Parcel", () => this.cancelParcel()), this.parcelTo,
      el("div", { class: "row" }, this.stampButton),
      this.parcelText,
      el("div", { class: "row" }, el("span", {}, "Cost"), this.costLabel),
      el("div", { class: "row" }, el("span", {}, "Gold"), this.goldButton),
      this.parcelCells,
      el("div", { class: "magic-meta mail-hint" }, "Pick an item in the bag, then click a slot. A stamp opens slots 2-5."),
      el("div", { class: "row" },
        el("button", { class: "primary", onclick: () => this.sendParcel() }, "Send"),
        el("button", { onclick: () => this.cancelParcel() }, "Cancel")));

    for (const root of [this.listRoot, this.readRoot, this.letterRoot, this.parcelRoot]) {
      root.addEventListener("mousedown", (e) => e.stopPropagation());
      root.addEventListener("keydown", (e) => e.stopPropagation()); // typing in a letter must not trigger game keys
      root.addEventListener("contextmenu", (e) => e.preventDefault());
      stage.append(root);
    }
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.renderParcel(); };
  }

  get visible(): boolean {
    return !this.listRoot.hidden;
  }

  // MailListDialog.Toggle / Show (UpdateInterface) / Hide (SelectedMail = null)
  toggle(show = this.listRoot.hidden): void {
    this.listRoot.hidden = !show;
    if (!show) this.selected = null;
    this.renderList();
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "ReceiveMail": this.receiveMail(p.Mail); return true;
      case "MailLockedItem": {
        // GameScene.MailLockedItem: InventoryDialog.GetCell(UniqueID).Locked = Locked
        const item = this.inv.inventory.find((i) => i?.UniqueID === p.UniqueID);
        if (item) this.inv.setLocked(item, p.Locked);
        return true;
      }
      case "MailSendRequest": this.mailSendRequest(); return true;
      case "MailSent": this.mailSent(p.Result); return true;
      case "ParcelCollected": this.parcelCollected(p.Result); return true;
      case "MailCost":
        // GameScene.MailCost (the gold sound is not played)
        if (!this.parcelRoot.hidden) this.costLabel.textContent = p.Cost.toLocaleString();
        return true;
      default: return false;
    }
  }

  // GameScene.ReceiveMail: replace the list (unlocked first, then newest first), flag new mail, refresh the list
  private receiveMail(list: ClientMail[]): void {
    const time = (m: ClientMail) => dateFromBinary(m.DateSent).getTime();
    this.mail = [...list].sort((a, b) => (Number(!b.Locked) - Number(!a.Locked)) || (time(b) - time(a)));
    for (const m of this.mail) for (const item of m.Items) this.requestItem(item.ItemIndex); // GameScene.Bind
    this.setNewMail(this.mail.some((m) => !m.Opened));
    // the desktop keeps the old ClientMail objects in the open dialogs; re-resolve them by MailID
    if (this.selected) this.selected = this.mail.find((m) => m.MailID === this.selected!.MailID) ?? null;
    if (this.reading) {
      const fresh = this.mail.find((m) => m.MailID === this.reading!.MailID);
      if (fresh) { this.reading = fresh; this.renderRead(); } else this.closeRead();
    }
    this.renderList();
  }

  private setNewMail(on: boolean): void {
    this.newMail = on;
    // MiniMapDialog.Process: the icon blinks (5 s on / 5 s off) ten times, then stays lit
    this.hudButton.classList.remove("mail-new");
    if (on) { void this.hudButton.offsetWidth; this.hudButton.classList.add("mail-new"); }
    this.hudButton.title = on ? "Mail (new mail)" : "Mail";
  }

  // GameScene.MailSendRequest: MirInputBox for the recipient, then MailComposeParcelDialog.ComposeMail + the inventory
  private mailSendRequest(): void {
    const name = prompt("Please enter the name of the person you would like to mail.", "");
    if (!name) return;
    this.composeParcel(name.trim());
    if (!this.invWindow.visible) this.invWindow.toggle(true);
  }

  // GameScene.MailSent: unlock the mailed cells and hide the parcel dialog (Hide -> Reset -> ResetLockedCells)
  private mailSent(result: number): void {
    if (result === -1) {
      // deliberate: the desktop closes the dialog silently; keep it open so the message is not lost
      this.chat("The parcel could not be sent (not enough gold for the postage, or an item cannot be mailed).", ChatType.System);
      return;
    }
    for (const item of this.items) if (item) this.inv.setLocked(item, false);
    this.hideParcel();
  }

  // GameScene.ParcelCollected
  private parcelCollected(result: number): void {
    if (result === -1) messageBox(this.stage, "No parcels to collect.");
    else if (result === 0) messageBox(this.stage, "All parcels have been collected.");
    else if (result === 1 && this.reading && isParcel(this.reading)) this.closeRead();
  }

  // — MailListDialog —

  private page(dir: number): void {
    // PreviousButton / NextButton
    if (dir < 0 ? this.currentPage <= 1 : this.currentPage >= this.pageCount) return;
    this.selected = null;
    this.currentPage += dir;
    this.startIndex += dir * ROWS;
    this.renderList();
  }

  // SendButton: MirInputBox (EnterMailToName) -> MailComposeLetterDialog.ComposeMail
  private sendClick(): void {
    const name = prompt("Please enter the name of the person you would like to mail.", "");
    if (name) this.composeLetter(name.trim());
  }

  // ReplyButton
  private reply(): void {
    if (this.selected) this.composeLetter(this.selected.SenderName);
  }

  // DeleteButton: never a locked mail; a parcel with items or gold asks first
  private deleteSelected(): void {
    const m = this.selected;
    if (!m || m.Locked) return;
    if (isParcel(m) && !confirm("This parcel contains items or gold. Are you sure you want to delete it?")) return;
    this.send(ClientPackets.DeleteMail({ MailID: m.MailID }));
    this.selected = null;
    this.renderList();
  }

  // MailItemRow click: select, a second click on the selected row reads it
  private rowClick(m: ClientMail): void {
    if (this.selected?.MailID !== m.MailID) { this.selected = m; this.renderList(); return; }
    this.read(m);
  }

  // MailListDialog.UpdateInterface + MailItemRow.UpdateInterface (+ GameScene.CreateMailLabel as the row tooltip)
  private renderList(): void {
    if (this.listRoot.hidden) return;
    this.pageCount = Math.max(1, Math.ceil(this.mail.length / ROWS));
    if (this.currentPage > this.pageCount) { this.currentPage = this.pageCount; this.startIndex = (this.pageCount - 1) * ROWS; }
    this.pageLabel.textContent = `${this.currentPage} / ${this.pageCount}`;
    let missing = false;
    const rows: HTMLElement[] = [];
    for (let i = 0; i < ROWS && i + this.startIndex < this.mail.length; i++) {
      const m = this.mail[i + this.startIndex]!;
      const icon = el("div", { class: "mail-icon" });
      const first = m.Items[0];
      const info = first ? this.inv.info(first) : undefined;
      if (first && info) {
        const img = el("img", { alt: info.Name, draggable: false });
        void itemIcon(info.Image).then((url) => { if (url) img.src = url; });
        icon.append(img);
      } else {
        if (first) missing = true;
        icon.append(el("span", {}, m.Gold > 0 || first ? "Parcel" : "Letter"));
      }
      const marks = el("div", { class: "mail-marks" });
      if (!m.Collected) marks.append(el("span", { class: "mail-mark parcel", title: "Parcel to collect" }, "P"));
      if (m.Locked) marks.append(el("span", { class: "mail-mark locked", title: "Locked" }, "L"));
      if (!m.Opened) marks.append(el("span", { class: "mail-mark unread", title: "Unread" }, "N"));
      icon.append(marks);
      const tip = [m.SenderName, `Date Sent : ${formatDate(m.DateSent)}`, ...(m.Gold > 0 ? [`Gold: ${m.Gold}`] : []), m.Opened ? "[Old]" : "[New]"];
      const row = el("div", { class: `mail-row${this.selected?.MailID === m.MailID ? " selected" : ""}${m.Opened ? "" : " unread"}`, title: tip.join("\n") },
        icon, el("span", { class: "mail-sender" }, m.SenderName),
        el("span", { class: "mail-preview" }, (m.Locked ? "[*] " : "") + m.Message.replace(/\r\n/g, " ")));
      row.onclick = () => this.rowClick(m);
      rows.push(row);
    }
    this.rows.replaceChildren(...rows);
    // UpdateRows: Reply only when the selected mail can be replied to
    this.replyButton.hidden = !!this.selected && !this.selected.CanReply;
    this.scheduleRetry(missing);
  }

  /** Item infos arrive after the list (S.NewItemInfo): redraw shortly while any icon is unknown. */
  private scheduleRetry(missing: boolean): void {
    if (!missing || this.retryRender) return;
    this.retryRender = window.setTimeout(() => { this.retryRender = 0; this.renderList(); this.renderRead(); }, 400);
  }

  // — MailReadLetterDialog / MailReadParcelDialog —

  // MailListDialog ReadButton / row: a parcel (gold or items) opens the parcel dialog, else the letter dialog. ReadMail: C.ReadMail when unopened
  private read(m: ClientMail): void {
    this.reading = m;
    if (!m.Opened) this.send(ClientPackets.ReadMail({ MailID: m.MailID }));
    this.readRoot.hidden = false;
    this.renderRead();
  }

  private closeRead(): void {
    this.reading = null;
    this.readRoot.hidden = true;
  }

  private renderRead(): void {
    const m = this.reading;
    if (!m || this.readRoot.hidden) return;
    const parcel = isParcel(m);
    this.readTitle.textContent = parcel ? "Parcel" : "Letter";
    this.readSender.textContent = `From: ${m.SenderName}`;
    this.readDate.textContent = `Sent: ${formatDate(m.DateSent)}`;
    this.readMessage.textContent = m.Message.replace(/\\r\\n/g, "\r\n");
    this.readParcel.hidden = !parcel;
    this.readParcelButtons.hidden = !parcel;
    this.readLetterButtons.hidden = parcel;
    this.lockButton.textContent = m.Locked ? "Unlock" : "Lock";
    if (parcel) {
      this.readGold.textContent = m.Gold.toLocaleString();
      const cells: HTMLElement[] = [];
      for (let i = 0; i < PARCEL_CELLS; i++) cells.push(this.invWindow.viewCell(m.Items[i] ?? null));
      this.readCells.replaceChildren(...cells);
      // Collected = the server delivered it straight to the mailbox; otherwise it waits at a post office NPC
      this.collectButton.disabled = !m.Collected;
      this.collectButton.title = m.Collected ? "" : "Collect this parcel from a post office NPC";
      this.scheduleRetry(m.Items.some((i) => !this.inv.info(i)));
    }
  }

  // MailReadLetterDialog.DeleteButton
  private deleteReading(): void {
    const m = this.reading;
    if (!m || m.Locked) return;
    this.send(ClientPackets.DeleteMail({ MailID: m.MailID }));
    this.closeRead();
  }

  // MailReadLetterDialog.LockButton
  private toggleLock(): void {
    const m = this.reading;
    if (!m) return;
    m.Locked = !m.Locked;
    this.send(ClientPackets.LockMail({ MailID: m.MailID, Lock: m.Locked }));
    this.renderRead();
  }

  // MailReadParcelDialog.CollectButton
  private collect(): void {
    if (this.reading) this.send(ClientPackets.CollectParcel({ MailID: this.reading.MailID }));
  }

  // — MailComposeLetterDialog —

  /** MailComposeLetterDialog.ComposeMail (also the friend list's Mail button). */
  composeLetter(name: string, message = ""): void {
    if (!name) return;
    this.letterName = name;
    this.letterTo.textContent = `To: ${name}`;
    this.letterText.value = message;
    this.letterRoot.hidden = false;
    this.letterText.focus();
  }

  private hideLetter(): void {
    this.letterRoot.hidden = true;
  }

  // MailComposeLetterDialog.SendButton (no gold, no items)
  private sendLetter(): void {
    this.send(ClientPackets.SendMail({ Name: this.letterName, Message: this.messageText(this.letterText), Gold: 0, ItemsIdx: new Array<bigint>(PARCEL_CELLS).fill(0n), Stamped: false }));
    this.hideLetter();
  }

  /** MirTextBox.MultiLine text uses CRLF line breaks. */
  private messageText(t: HTMLTextAreaElement): string {
    return t.value.replace(/\r?\n/g, "\r\n");
  }

  // — MailComposeParcelDialog —

  // ComposeMail
  private composeParcel(name: string): void {
    if (!name) return;
    if (!this.parcelRoot.hidden) this.hideParcel();
    this.parcelName = name;
    this.parcelTo.textContent = `To: ${name}`;
    this.parcelText.value = "";
    this.stamped = false;
    this.giftGold = 0;
    this.costLabel.textContent = "0";
    this.resetLockedCells();
    this.parcelRoot.hidden = false;
    this.renderParcel();
    this.parcelText.focus();
  }

  private itemsIdx(): bigint[] {
    return this.items.map((i) => i?.UniqueID ?? 0n);
  }

  // CalculatePostage: ask the server (S.MailCost answers)
  private calculatePostage(): void {
    this.send(ClientPackets.MailCost({ Gold: this.giftGold, ItemsIdx: this.itemsIdx(), Stamped: this.stamped }));
  }

  // ResetLockedCells: ask the server to unlock each cell item (it echoes S.MailLockedItem), then clear the cells
  private resetLockedCells(): void {
    for (let i = 0; i < PARCEL_CELLS; i++) {
      const item = this.items[i];
      if (item) this.send(ClientPackets.MailLockedItem({ UniqueID: item.UniqueID, Locked: false }));
      this.items[i] = null;
    }
  }

  // Hide -> Reset (GiftGoldAmount, Stamped, ResetLockedCells)
  private hideParcel(): void {
    if (this.parcelRoot.hidden) return;
    this.parcelRoot.hidden = true;
    this.giftGold = 0;
    this.stamped = false;
    this.resetLockedCells();
  }

  // CloseButton / CancelButton
  private cancelParcel(): void {
    this.hideParcel();
  }

  // StampParcel: needs a stamp (ItemType.Nothing, Shape 1) in the bag; toggling clears the cells like the desktop
  private stampParcel(): void {
    if (!this.stamped) {
      const has = this.inv.inventory.some((i) => { const info = i && this.inv.info(i); return !!info && info.Type === ItemType.Nothing && info.Shape === 1; });
      if (has) this.stamped = true;
      else this.chat("You need a stamp in your bag.", ChatType.System); // deliberate: the desktop does nothing
    } else this.stamped = false;
    this.resetLockedCells();
    this.calculatePostage();
    this.renderParcel();
  }

  // GoldSendLabel click: MirAmountBox up to the gold not already put in the parcel
  private addGold(): void {
    const left = this.inv.gold - this.giftGold;
    if (this.invWindow.pickedInventorySlot >= 0 || left <= 0) return;
    const text = prompt(`Send Amount: (max ${left.toLocaleString()})`, String(left));
    if (text === null) return;
    const amount = Math.min(left, Math.trunc(Number(text.replace(/[,\s]/g, ""))));
    if (amount > 0) this.giftGold += amount;
    this.calculatePostage();
    this.renderParcel();
  }

  // MirItemCell "To Mail" (from the inventory only): empty slot, not DontTrade; the inventory cell is locked locally
  private cellClick(slot: number): void {
    const from = this.invWindow.pickedInventorySlot;
    if (from < 0) return; // a mail cell does nothing without a picked item
    if (slot > 0 && !this.stamped) return; // ItemCover: slots 2-5 need a stamp
    const item = this.inv.inventory[from];
    this.invWindow.clearPick();
    if (!item) return;
    if (this.items[slot]) { this.chat("You cannot swap items.", ChatType.System); return; }
    const info = this.inv.info(item);
    // the desktop checks DontTrade; NoMail is also checked here (the server refuses it either way)
    if (info && (info.Bind & (BindMode.DontTrade | BindMode.NoMail)) !== 0) { this.chat("You cannot mail this item.", ChatType.System); return; }
    if (this.items.some((i) => i?.UniqueID === item.UniqueID)) return;
    this.items[slot] = item;
    this.inv.setLocked(item, true);
    this.calculatePostage();
    this.renderParcel();
  }

  // SendButton: the server answers with S.MailSent (and S.LoseGold / S.DeleteItem on success)
  private sendParcel(): void {
    this.send(ClientPackets.SendMail({ Name: this.parcelName, Message: this.messageText(this.parcelText), Gold: this.giftGold, ItemsIdx: this.itemsIdx(), Stamped: this.stamped }));
  }

  private renderParcel(): void {
    if (this.parcelRoot.hidden) return;
    this.stampButton.textContent = this.stamped ? "Stamped ✓" : "Stamp";
    this.stampButton.classList.toggle("on", this.stamped);
    this.goldButton.textContent = this.giftGold.toLocaleString();
    const cells: HTMLElement[] = [];
    for (let i = 0; i < PARCEL_CELLS; i++) {
      const c = this.invWindow.viewCell(this.items[i] ?? null);
      if (i > 0 && !this.stamped) c.classList.add("mail-covered");
      c.onmousedown = (e) => { e.stopPropagation(); if (e.button === 0) this.cellClick(i); };
      cells.push(c);
    }
    this.parcelCells.replaceChildren(...cells);
  }
}
