// Mail: the desktop mail dialogs (Client/MirScenes/Dialogs/MailDialogs.cs) with their artwork — MailListDialog (Title 670,
// 10 MailItemRows a page), MailReadLetterDialog (Title 672), MailReadParcelDialog (Title 675), MailComposeLetterDialog
// (Title 671), MailComposeParcelDialog (Title 674: recipient, message, gold, stamp, up to 5 inventory items with the
// mail-locked-item flow and the C.MailCost query) — a view over the mailbox model (src/game/mail.ts: UserObject.Mail,
// GameScene.NewMail and GameScene's mail handlers ReceiveMail / MailLockedItem / MailSendRequest / MailSent /
// ParcelCollected / MailCost, whose prompts and results the window answers) + GameScene.CreateMailLabel (the row hover
// label). The list opens from the HUD Mail button (MiniMapDialog.MailButton); parcels are composed only after
// S.MailSendRequest (an NPC script), like the desktop. Closeall (Escape) hides every mail dialog (GameScene key handling).

import type { UserItem, ClientMail } from "../protocol/server.ts";
import { ChatType, ItemType, BindMode } from "../protocol/enums.generated.ts";
import type { Inventory } from "../game/inventory.ts";
import { type MailBox, isParcel, MAIL_PARCEL_CELLS } from "../game/mail.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { type InventoryWindow, placeCell } from "./inventoryWindow.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirLabel, MirTextBox, closeButton, Lib, libSize, getUiRoot, uiSound, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { messageBox, inputBox, amountBox } from "./messageBox.ts";
import { el } from "./screens.ts";

const ROWS = 10; // MailListDialog.Rows
const PARCEL_CELLS = MAIL_PARCEL_CELLS; // MailComposeParcelDialog._cellCount
const MAX_MESSAGE = 500; // PlayerObject.SendMail refuses longer messages
const MAIL_CELL: readonly [number, number] = [35, 31]; // the mail MirItemCells' Size
const TITLE_FONT = "italic 7pt Arial"; // new Font(Settings.FontName, Settings.FontSize - 1, FontStyle.Italic)
const ENTER_NAME = "Please enter the name of the person you would like to mail."; // ClientTextKeys.EnterMailToName

/** Mail.DateSent.ToString("dd/MM/yy H:mm:ss"). */
function formatDate(v: bigint): string {
  const d = dateFromBinary(v), p = (n: number) => String(n).padStart(2, "0");
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${p(d.getFullYear() % 100)} ${d.getHours()}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}


/** A Title.Lib button with Index / Index + 1 / Index + 2 (the dialogs' common pattern). */
function titleButton(parent: MirControl, index: number, location: [number, number], onClick: () => void, hint?: string, library: string = Lib.Title): MirButton {
  return new MirButton({ parent, library, index, hoverIndex: index + 1, pressedIndex: index + 2, location, sound: SoundList.ButtonA, hint, onClick });
}

/** A single-line label clipped to its Size (MirLabel with DrawFormat = VerticalCenter, no WordBreak). */
function lineLabel(parent: MirControl, location: [number, number], size: [number, number], notControl = true): MirLabel {
  const l = new MirLabel({ parent, location, size, vCenter: true, notControl });
  l.el.style.whiteSpace = "pre";
  l.el.style.overflow = "hidden";
  return l;
}

/** MailItemRow: icon (first item / Prguse 541 gold / Prguse 540 letter), unread / locked / parcel marks, sender, message. */
class MailItemRow {
  readonly control: MirControl;
  private readonly icon: MirImage;
  private readonly unread: MirImage;
  private readonly locked: MirImage;
  private readonly parcel: MirImage;
  private readonly selectedImage: MirImage;
  private readonly sender: MirLabel;
  private readonly message: MirLabel;
  mail: ClientMail | null = null;

  constructor(parent: MirControl, location: [number, number], onClick: (row: MailItemRow) => void, onHover: (row: MailItemRow, e: PointerEvent | null) => void) {
    this.control = new MirControl({ parent, location, size: [290, 33], visible: false });
    this.control.onClick(() => { uiSound(SoundList.ButtonA); onClick(this); });
    this.icon = new MirImage({ parent: this.control, library: Lib.Prguse, index: 540, location: [0, 0], notControl: true });
    this.unread = new MirImage({ parent: this.control, library: Lib.Prguse, index: 550, location: [5, 17], visible: false, notControl: true });
    this.locked = new MirImage({ parent: this.control, library: Lib.Prguse, index: 551, location: [5, 17], visible: false, notControl: true });
    this.parcel = new MirImage({ parent: this.control, library: Lib.Prguse, index: 552, location: [5, 17], visible: false, notControl: true });
    this.selectedImage = new MirImage({ parent: this.control, library: Lib.Prguse, index: 545, location: [-5, -3], visible: false, notControl: true });
    this.sender = lineLabel(this.control, [35, 0], [130, 31]);
    this.message = lineLabel(this.control, [170, 0], [115, 31]);
    // OnMouseEnter / OnMouseLeave: GameScene.CreateMailLabel / DisposeMailLabel
    this.control.el.addEventListener("pointerenter", (e) => onHover(this, e));
    this.control.el.addEventListener("pointermove", (e) => onHover(this, e));
    this.control.el.addEventListener("pointerleave", () => onHover(this, null));
  }

  /** MailItemRow.UpdateInterface (the icon centred in the 34 x 32 IconArea). Returns false while the item info is unknown. */
  update(mail: ClientMail | null, selected: boolean, image: (item: UserItem) => number | undefined): boolean {
    this.mail = mail;
    this.control.visible = mail !== null;
    if (!mail) return true;
    let known = true;
    const first = mail.Items[0];
    if (first) {
      const img = image(first);
      if (img === undefined) { known = false; this.icon.library = null; }
      else { this.icon.library = Lib.Items; this.icon.index = img; }
    } else {
      this.icon.library = Lib.Prguse;
      this.icon.index = mail.Gold > 0 ? 541 : 540;
    }
    const lib = this.icon.library, idx = this.icon.index;
    void this.icon.ready.then(() => {
      if (this.icon.library !== lib || this.icon.index !== idx) return;
      const [w, h] = this.icon.size;
      this.icon.location = [Math.trunc((34 - w) / 2), Math.trunc((32 - h) / 2)];
    });
    this.unread.visible = !mail.Opened;
    this.locked.visible = mail.Locked;
    this.parcel.visible = !mail.Collected;
    // move unread to the second position when a parcel or lock mark takes the first
    this.unread.location = (!mail.Collected || mail.Locked) && !mail.Opened ? [20, 17] : [5, 17];
    this.sender.text = mail.SenderName;
    const text = mail.Message.replace(/\r\n/g, " ");
    this.message.text = mail.Locked ? "[*] " + text : text;
    this.selectedImage.visible = selected;
    return known;
  }
}

export class MailWindow {
  /** MailListDialog.HelpButton (GameScene.HelpDialog.DisplayPage("")); set by game.ts. */
  onHelp: (() => void) | null = null;

  /** UserObject.Mail + the mail packets (src/game/mail.ts). */
  private readonly box: MailBox;
  private readonly inv: Inventory;
  private readonly invWindow: InventoryWindow;
  private readonly chat: (text: string, type: number) => void;
  private readonly stage: HTMLElement;

  // MailListDialog
  private readonly listWin: MirWindow;
  private readonly rows: MailItemRow[] = [];
  private readonly pageLabel: MirLabel;
  private readonly replyButton: MirButton;
  private selected: ClientMail | null = null;
  private startIndex = 0;
  private currentPage = 1;
  private pageCount = 1;
  private mailLabel: HTMLDivElement | null = null;
  private mailLabelFor: ClientMail | null = null;

  // MailReadLetterDialog
  private readonly letterReadWin: MirWindow;
  private readonly letterSender: MirLabel;
  private readonly letterDate: MirLabel;
  private readonly letterMessage: MirLabel;
  private readingLetter: ClientMail | null = null;

  // MailReadParcelDialog
  private readonly parcelReadWin: MirWindow;
  private readonly parcelSender: MirLabel;
  private readonly parcelDate: MirLabel;
  private readonly parcelMessage: MirLabel;
  private readonly parcelGold: MirLabel;
  private readonly collectButton: MirButton;
  private readCells: HTMLElement[] = [];
  private readingParcel: ClientMail | null = null;

  // MailComposeLetterDialog
  private readonly letterWin: MirWindow;
  private readonly letterTo: MirLabel;
  private readonly letterText: MirTextBox;

  // MailComposeParcelDialog
  private readonly parcelWin: MirWindow;
  private readonly parcelTo: MirLabel;
  private readonly parcelText: MirTextBox;
  private readonly stampButton: MirButton;
  private readonly itemCover: MirImage;
  private readonly costLabel: MirLabel;
  private readonly goldLabel: MirLabel;
  private parcelCells: HTMLElement[] = [];
  /** MailComposeParcelDialog.Items / ItemsIdx / GiftGoldAmount / Stamped. */
  private readonly items: (UserItem | null)[] = new Array<UserItem | null>(PARCEL_CELLS).fill(null);
  private giftGold = 0;
  private stamped = false;
  private retryRender = 0;

  constructor(stage: HTMLElement, box: MailBox, inv: Inventory, invWindow: InventoryWindow, chat: (text: string, type: number) => void) {
    this.stage = stage;
    this.box = box;
    this.inv = inv;
    this.invWindow = invWindow;
    this.chat = chat;

    // — MailListDialog ctor —
    this.listWin = new MirWindow({ library: Lib.Title, index: 670, movable: true, sort: true, location: [SCREEN_WIDTH - 312 - 150, 5],
      onHide: () => { this.selected = null; this.hideMailLabel(); } });
    const lw = this.listWin;
    new MirImage({ parent: lw, library: Lib.Title, index: 7, location: [18, 9] });
    new MirLabel({ parent: lw, text: "TYPE", font: TITLE_FONT, center: true, size: [37, 19], location: [8, 34] });
    new MirLabel({ parent: lw, text: "SENDER", font: TITLE_FONT, center: true, size: [132, 19], location: [47, 34] });
    new MirLabel({ parent: lw, text: "MESSAGE", font: TITLE_FONT, center: true, size: [122, 19], location: [181, 34] });
    closeButton(lw, [312 - 24, 3], () => this.toggle(false));
    titleButton(lw, 257, [312 - 50, 3], () => this.onHelp?.(), undefined, Lib.Prguse2);
    titleButton(lw, 240, [102, 444 - 55], () => this.page(-1), undefined, Lib.Prguse2);
    this.pageLabel = new MirLabel({ parent: lw, location: [120, 444 - 55], size: [67, 15], center: true });
    titleButton(lw, 243, [192, 444 - 55], () => this.page(1), undefined, Lib.Prguse2);
    titleButton(lw, 563, [75, 414], () => this.sendClick(), "Send", Lib.Prguse);
    this.replyButton = titleButton(lw, 569, [102, 414], () => this.reply(), "Reply", Lib.Prguse);
    titleButton(lw, 572, [129, 414], () => { if (this.selected) this.read(this.selected); }, "Read", Lib.Prguse);
    titleButton(lw, 557, [156, 414], () => this.deleteSelected(), "Delete", Lib.Prguse);
    // BlockListButton / BugReportButton: GrayScale, Enabled = false on the desktop too
    const block = titleButton(lw, 520, [183, 414], () => {}, "Block List", Lib.Prguse);
    block.enabled = false; block.grayScale = true;
    const bug = titleButton(lw, 523, [210, 414], () => {}, "Report Bug", Lib.Prguse);
    bug.enabled = false; bug.grayScale = true;
    for (let i = 0; i < ROWS; i++) {
      this.rows.push(new MailItemRow(lw, [10, 55 + i * 33], (row) => this.rowClick(row), (row, e) => this.mailHover(row, e)));
    }

    // — MailReadLetterDialog ctor —
    this.letterReadWin = new MirWindow({ library: Lib.Title, index: 672, movable: true, sort: true, location: [100, 100], onHide: () => { this.readingLetter = null; } });
    const rl = this.letterReadWin;
    closeButton(rl, [236 - 27, 3], () => rl.hide());
    this.letterSender = new MirLabel({ parent: rl, location: [70, 35], size: [150, 15], notControl: true });
    this.letterDate = new MirLabel({ parent: rl, location: [70, 56], size: [150, 15], notControl: true });
    this.letterMessage = new MirLabel({ parent: rl, location: [15, 92], size: [202, 165] });
    this.letterMessage.el.style.overflow = "hidden";
    titleButton(rl, 540, [12, 265], () => this.deleteReading());
    titleButton(rl, 686, [81, 265], () => this.toggleLock());
    titleButton(rl, 193, [154, 265], () => rl.hide());

    // — MailReadParcelDialog ctor —
    this.parcelReadWin = new MirWindow({ library: Lib.Title, index: 675, movable: true, sort: true, location: [100, 100], onHide: () => { this.readingParcel = null; } });
    const rp = this.parcelReadWin;
    closeButton(rp, [236 - 27, 3], () => rp.hide());
    this.parcelSender = new MirLabel({ parent: rp, location: [70, 35], size: [150, 15], notControl: true });
    this.parcelDate = new MirLabel({ parent: rp, location: [70, 56], size: [150, 15], notControl: true });
    this.parcelMessage = new MirLabel({ parent: rp, location: [15, 98], size: [202, 165] });
    this.parcelMessage.el.style.overflow = "hidden";
    this.parcelGold = lineLabel(rp, [63, 290], [143, 15], false);
    this.collectButton = titleButton(rp, 370, [30, 350], () => this.collect());
    titleButton(rp, 193, [135, 350], () => rp.hide());

    // — MailComposeLetterDialog ctor —
    this.letterWin = new MirWindow({ library: Lib.Title, index: 671, movable: true, sort: true, location: [100, 100] });
    const cl = this.letterWin;
    closeButton(cl, [236 - 27, 3], () => cl.hide());
    this.letterTo = new MirLabel({ parent: cl, location: [70, 35], size: [150, 15], notControl: true });
    this.letterText = new MirTextBox({ parent: cl, location: [15, 92], size: [202, 165], multiline: true, font: "8pt Arial", foreColour: "#ffffff", maxLength: MAX_MESSAGE });
    titleButton(cl, 607, [30, 265], () => this.sendLetter());
    titleButton(cl, 193, [135, 265], () => cl.hide());

    // — MailComposeParcelDialog ctor (Location = (InventoryDialog.Size.Width + 10, 0)) —
    this.parcelWin = new MirWindow({ library: Lib.Title, index: 674, movable: true, sort: true, location: [0, 0], onHide: () => this.resetParcel() });
    const cp = this.parcelWin;
    void libSize(Lib.Title, 196).then(([w]) => { cp.location = [w + 10, 0]; });
    closeButton(cp, [236 - 27, 3], () => cp.hide());
    this.parcelTo = new MirLabel({ parent: cp, location: [70, 35], size: [150, 15], notControl: true });
    this.parcelText = new MirTextBox({ parent: cp, location: [15, 98], size: [202, 165], multiline: true, font: "8pt Arial", foreColour: "#ffffff", maxLength: MAX_MESSAGE });
    this.stampButton = new MirButton({ parent: cp, library: Lib.Prguse2, index: 203, hoverIndex: 203, pressedIndex: 203, location: [73, 56], size: [20, 20], sound: SoundList.ButtonA,
      onClick: () => this.stampParcel() });
    this.itemCover = new MirImage({ parent: cp, library: Lib.Title, index: 676, location: [63, 310], size: [144, 33], notControl: true });
    this.costLabel = lineLabel(cp, [63, 269], [143, 15], false);
    this.goldLabel = lineLabel(cp, [63, 290], [143, 15], false);
    this.goldLabel.onClick(() => { uiSound(SoundList.Gold); this.addGold(); });
    titleButton(cp, 607, [30, 350], () => this.sendParcel());
    titleButton(cp, 193, [135, 350], () => cp.hide());

    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.renderParcel(); };
    // GameScene Closeall: every mail dialog hides
    window.addEventListener("keydown", (e) => {
      if (!matches(e, KeybindOptions.Closeall) || document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement) return;
      for (const w of [this.letterWin, this.parcelWin, this.listWin, this.letterReadWin, this.parcelReadWin]) w.hide();
    }, { signal: gameSignal() });
    // The mailbox's events (GameScene's mail handlers, src/game/mail.ts)
    const offs = [
      box.changed.on(() => this.listChanged()),
      box.sendRequest.on(() => this.mailSendRequest()),
      box.sent.on(({ result }) => this.mailSent(result)),
      box.collected.on(({ result }) => this.parcelCollected(result)),
      // GameScene.MailCost (the gold sound is not played)
      box.cost.on(({ cost }) => { if (this.parcelWin.visible) this.costLabel.text = cost.toLocaleString("en-US"); }),
    ];
    gameSignal().addEventListener("abort", () => {
      for (const off of offs) off();
      this.hideMailLabel();
      for (const w of [this.letterWin, this.parcelWin, this.listWin, this.letterReadWin, this.parcelReadWin]) w.dispose();
    });
  }

  get visible(): boolean {
    return this.listWin.visible;
  }

  /** GameScene.NewMail: any unopened mail (MiniMapDialog blinks the NewMail icon; mail.ts). */
  get newMail(): boolean {
    return this.box.newMail;
  }

  // MailListDialog.Toggle / Show (UpdateInterface) / Hide (SelectedMail = null)
  toggle(show = !this.listWin.visible): void {
    if (show) this.listWin.show(); else this.listWin.hide();
    this.renderList();
  }

  // GameScene.ReceiveMail (the model replaced the list and set NewMail): refresh the list
  private listChanged(): void {
    // the desktop keeps the old ClientMail objects in the open dialogs; re-resolve them by MailID
    const fresh = (m: ClientMail | null) => (m ? this.box.find(m.MailID) : null);
    this.selected = fresh(this.selected);
    if (this.readingLetter) {
      const f = fresh(this.readingLetter);
      if (f) { this.readingLetter = f; this.renderLetterRead(); } else this.letterReadWin.hide();
    }
    if (this.readingParcel) {
      const f = fresh(this.readingParcel);
      if (f) { this.readingParcel = f; this.renderParcelRead(); } else this.parcelReadWin.hide();
    }
    this.renderList();
  }

  // GameScene.MailSendRequest: MirInputBox for the recipient, then MailComposeParcelDialog.ComposeMail + the inventory
  private mailSendRequest(): void {
    inputBox(this.stage, ENTER_NAME, "", (name) => {
      this.composeParcel(name.trim());
      if (!this.invWindow.visible) this.invWindow.toggle(true);
    });
  }

  // GameScene.MailSent: unlock the mailed cells and hide the parcel dialog (Hide -> Reset -> ResetLockedCells)
  private mailSent(result: number): void {
    // -1: deliberate: the desktop closes the dialog silently; it stays open so the model's chat line is not lost
    if (result === -1) return;
    for (const item of this.items) if (item) this.inv.setLocked(item, false);
    this.parcelWin.hide();
  }

  // GameScene.ParcelCollected
  private parcelCollected(result: number): void {
    if (result === -1) messageBox(this.stage, "No parcels to collect.");
    else if (result === 0) messageBox(this.stage, "All parcels have been collected.");
    else if (result === 1) this.parcelReadWin.hide();
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
    inputBox(this.stage, ENTER_NAME, "", (name) => { this.composeLetter(name.trim()); });
  }

  // ReplyButton
  private reply(): void {
    if (this.selected) this.composeLetter(this.selected.SenderName);
  }

  // DeleteButton: never a locked mail; a parcel with items or gold asks first (MirMessageBox YesNo)
  private deleteSelected(): void {
    const m = this.selected;
    if (!m || m.Locked) return;
    const del = () => {
      this.box.delete(m.MailID);
      if (this.selected === m) this.selected = null;
      this.renderList();
    };
    if (isParcel(m)) {
      messageBox(this.stage, "This parcel contains items or gold. Are you sure you want to delete it?",
        [{ label: "Yes", onClick: del }, { label: "No" }]);
    } else del();
  }

  // MailItemRow click: select, a second click on the selected row reads it
  private rowClick(row: MailItemRow): void {
    const m = row.mail;
    if (!m) return;
    if (this.selected?.MailID !== m.MailID) { this.selected = m; this.renderList(); return; }
    this.read(m);
  }

  // MailListDialog.UpdateInterface + UpdateRows
  private renderList(): void {
    if (!this.listWin.visible) return;
    this.pageCount = Math.max(1, Math.ceil(this.box.list.length / ROWS));
    if (this.currentPage > this.pageCount) { this.currentPage = this.pageCount; this.startIndex = (this.pageCount - 1) * ROWS; }
    this.pageLabel.text = `${this.currentPage} / ${this.pageCount}`;
    let missing = false;
    for (let i = 0; i < ROWS; i++) {
      const m = this.box.list[i + this.startIndex] ?? null;
      const known = this.rows[i]!.update(m, !!m && this.selected?.MailID === m.MailID, (item) => this.inv.info(item)?.Image);
      if (!known) missing = true;
    }
    // UpdateRows: Reply only when the selected mail can be replied to
    if (this.selected) this.replyButton.visible = this.selected.CanReply;
    this.scheduleRetry(missing);
  }

  /** Item infos arrive after the list (S.NewItemInfo): redraw shortly while any icon is unknown. */
  private scheduleRetry(missing: boolean): void {
    if (!missing || this.retryRender) return;
    this.retryRender = window.setTimeout(() => { this.retryRender = 0; this.renderList(); this.renderParcelRead(); }, 400);
  }

  // GameScene.CreateMailLabel: (50,50,50) box at 70 %, gray border; sender (yellow), date, gold, [Old] / [New] (red); at mouse + (15, 0)
  private mailHover(row: MailItemRow, e: PointerEvent | null): void {
    const m = row.mail;
    if (!e || !m) { this.hideMailLabel(); return; }
    if (this.mailLabelFor !== m || !this.mailLabel?.isConnected) {
      this.hideMailLabel();
      const box = document.createElement("div");
      box.className = "mir";
      Object.assign(box.style, { position: "absolute", pointerEvents: "none", zIndex: "100000", padding: "4px 4px 0 4px", width: "max-content",
        background: "rgba(50,50,50,0.7)", outline: "1px solid #808080", outlineOffset: "-1px" });
      const line = (text: string, colour: string) => {
        const l = new MirLabel({ parent: box, text, foreColour: colour, autoSize: true });
        l.el.style.position = "relative";
      };
      line(m.SenderName, "#ffff00");
      line(`Date Sent : ${formatDate(m.DateSent)}`, "#ffffff");
      if (m.Gold > 0) line(`Gold: ${m.Gold}`, "#ffffff");
      line(m.Opened ? "[Old]" : "[New]", "#ff0000");
      getUiRoot().append(box);
      this.mailLabel = box;
      this.mailLabelFor = m;
    }
    // GameScene.Process: MailLabel at the mouse + (15, 0), kept on screen
    const root = getUiRoot();
    const r = root.getBoundingClientRect();
    const s = r.width / (root.offsetWidth || 1) || 1;
    const box = this.mailLabel!;
    let x = Math.round((e.clientX - r.left) / s) + 15, y = Math.round((e.clientY - r.top) / s);
    if (x + box.offsetWidth > SCREEN_WIDTH) x = SCREEN_WIDTH - box.offsetWidth;
    if (y + box.offsetHeight > SCREEN_HEIGHT) y = SCREEN_HEIGHT - box.offsetHeight;
    box.style.left = x + "px";
    box.style.top = y + "px";
  }

  private hideMailLabel(): void {
    this.mailLabel?.remove();
    this.mailLabel = null;
    this.mailLabelFor = null;
  }

  // — MailReadLetterDialog / MailReadParcelDialog —

  // MailListDialog ReadButton / row: a parcel (gold or items) opens the parcel dialog, else the letter dialog. ReadMail: C.ReadMail when unopened
  private read(m: ClientMail): void {
    this.box.read(m);
    if (isParcel(m)) {
      this.readingParcel = m;
      this.parcelReadWin.show();
      this.renderParcelRead();
    } else {
      this.readingLetter = m;
      this.letterReadWin.show();
      this.renderLetterRead();
    }
  }

  // MailReadLetterDialog.ReadMail
  private renderLetterRead(): void {
    const m = this.readingLetter;
    if (!m || !this.letterReadWin.visible) return;
    this.letterSender.text = m.SenderName;
    this.letterDate.text = formatDate(m.DateSent);
    this.letterMessage.text = m.Message.replace(/\\r\\n/g, "\r\n");
  }

  // MailReadParcelDialog.ReadMail (cells at (27 + i * 36, 311), CollectButton 680 / 683 by Collected)
  private renderParcelRead(): void {
    const m = this.readingParcel;
    if (!m || !this.parcelReadWin.visible) return;
    this.parcelSender.text = m.SenderName;
    this.parcelDate.text = formatDate(m.DateSent);
    this.parcelMessage.text = m.Message.replace(/\\r\\n/g, "\r\n");
    this.parcelGold.text = m.Gold.toLocaleString("en-US");
    for (const c of this.readCells) c.remove();
    this.readCells = [];
    for (let i = 0; i < PARCEL_CELLS && i < m.Items.length; i++) {
      this.readCells.push(placeCell(this.invWindow.viewCell(m.Items[i]!, MAIL_CELL), this.parcelReadWin, [27 + i * 36, 311]));
    }
    // Collected = the server delivered it straight to the mailbox; otherwise it waits at a post office NPC
    if (!m.Collected) { this.collectButton.setIndices(683, 684, 685); this.collectButton.enabled = false; }
    else { this.collectButton.setIndices(680, 681, 682); this.collectButton.enabled = true; }
    this.scheduleRetry(m.Items.some((i) => !this.inv.info(i)));
  }

  // MailReadLetterDialog.DeleteButton
  private deleteReading(): void {
    const m = this.readingLetter;
    if (!m || m.Locked) return;
    this.box.delete(m.MailID);
    this.letterReadWin.hide();
  }

  // MailReadLetterDialog.LockButton
  private toggleLock(): void {
    const m = this.readingLetter;
    if (!m) return;
    this.box.toggleLock(m);
    this.renderList();
  }

  // MailReadParcelDialog.CollectButton
  private collect(): void {
    if (this.readingParcel) this.box.collect(this.readingParcel.MailID);
  }

  // — MailComposeLetterDialog —

  /** MailComposeLetterDialog.ComposeMail (also the friend list's / relationship's / market's Mail button). */
  composeLetter(name: string, message = ""): void {
    if (!name) return;
    this.letterTo.text = name;
    this.letterText.text = message;
    this.letterWin.show();
    this.letterText.focus();
  }

  // MailComposeLetterDialog.SendButton (no gold, no items)
  private sendLetter(): void {
    this.box.sendMail(this.letterTo.text, this.messageText(this.letterText));
    this.letterWin.hide();
  }

  /** MirTextBox.MultiLine text uses CRLF line breaks. */
  private messageText(t: MirTextBox): string {
    return t.text.replace(/\r?\n/g, "\r\n");
  }

  // — MailComposeParcelDialog —

  // ComposeMail
  private composeParcel(name: string): void {
    if (!name) return;
    if (this.parcelWin.visible) this.parcelWin.hide();
    this.parcelTo.text = name;
    this.parcelText.text = "";
    this.stamped = false;
    this.giftGold = 0;
    this.costLabel.text = "0";
    this.resetLockedCells();
    this.parcelWin.show();
    this.renderParcel();
    this.parcelText.focus();
  }

  private itemsIdx(): bigint[] {
    return this.items.map((i) => i?.UniqueID ?? 0n);
  }

  // CalculatePostage: ask the server (S.MailCost answers)
  private calculatePostage(): void {
    this.box.queryCost(this.giftGold, this.itemsIdx(), this.stamped);
  }

  // ResetLockedCells: ask the server to unlock each cell item (it echoes S.MailLockedItem), then clear the cells
  private resetLockedCells(): void {
    for (let i = 0; i < PARCEL_CELLS; i++) {
      const item = this.items[i];
      if (item) this.box.unlockItem(item.UniqueID);
      this.items[i] = null;
    }
  }

  // Hide -> Reset (GiftGoldAmount, Stamped, ResetLockedCells)
  private resetParcel(): void {
    this.giftGold = 0;
    this.stamped = false;
    this.resetLockedCells();
  }

  // StampParcel: needs a stamp (ItemType.Nothing, Shape 1) in the bag; toggling clears the cells like the desktop
  private stampParcel(): void {
    if (!this.stamped) {
      const has = this.inv.inventory.some((i) => { const info = i && this.inv.info(i); return !!info && info.Type === ItemType.Nothing && info.Shape === 1; });
      if (has) this.stamped = true;
      else this.chat("You need a stamp in your bag.", ChatType.System); // deliberate: the desktop does nothing
    } else this.stamped = false;
    this.calculatePostage();
    this.resetLockedCells();
    this.renderParcel();
  }

  // GoldSendLabel click: MirAmountBox (Items 116) up to the gold not already put in the parcel
  private addGold(): void {
    const left = this.inv.gold - this.giftGold;
    if (this.invWindow.pickedInventorySlot >= 0 || left <= 0) return;
    amountBox("Send Amount:", 116, left, (amount) => {
      if (amount > 0) this.giftGold += Math.min(amount, this.inv.gold - this.giftGold);
      this.calculatePostage();
      this.renderParcel();
    });
  }

  // MirItemCell "To Mail" (from the inventory only): empty slot, not DontTrade; the inventory cell is locked locally
  private cellClick(slot: number): void {
    const from = this.invWindow.pickedInventorySlot;
    if (from < 0) return; // a mail cell does nothing without a picked item
    if (slot > 0 && !this.stamped) return; // Cells[1..4].Enabled = false until stamped
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
    this.box.sendMail(this.parcelTo.text, this.messageText(this.parcelText), this.giftGold, this.itemsIdx(), this.stamped);
  }

  // UpdateParcel (StampButton 203 / 204, ItemCover) + the cells at (27 + i * 36, 311) + GoldSendLabel
  private renderParcel(): void {
    if (!this.parcelWin.visible) return;
    const s = this.stamped ? 204 : 203;
    this.stampButton.setIndices(s, s, s);
    this.itemCover.visible = !this.stamped;
    this.goldLabel.text = this.giftGold.toLocaleString("en-US");
    for (const c of this.parcelCells) c.remove();
    this.parcelCells = [];
    for (let i = 0; i < PARCEL_CELLS; i++) {
      const c = placeCell(this.invWindow.viewCell(this.items[i] ?? null, MAIL_CELL), this.parcelWin, [27 + i * 36, 311]);
      c.addEventListener("mousedown", (e) => { if (e.button === 0) this.cellClick(i); });
      this.parcelCells.push(c);
    }
  }
}
