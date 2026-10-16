// Ported from play/hudMail.ts (MailMock): the new UI's mail window over the real mailbox model (src/game/mail.ts).
// Desktop counterparts: MailListDialog / MailReadLetterDialog / MailReadParcelDialog / MailComposeLetterDialog /
// MailComposeParcelDialog (Client/MirScenes/Dialogs/MailDialogs.cs) + GameScene's mail handlers; the classic port is
// src/ui/mailWindow.ts (its rules are kept). Changes from the mock:
//  - the inbox is MailBox.list (S.ReceiveMail: letters and parcels, newest first with locked mail last, as the server
//    sends and the model sorts); a click reads the mail (C.ReadMail through MailBox.read: the server answers with a fresh
//    list, nothing is marked locally); "Mark all read" is ONE C.ReadMails (MailBox.readMails, at most 100 ids);
//  - Lock / Unlock = C.LockMail (MailBox.toggleLock); Delete = C.DeleteMail after a popup (a parcel still holding gold
//    or items asks ParcelContainsItemsOrGoldDeleteConfirm: they are lost); locked mail can't be deleted;
//  - parcels: ClientMail.Collected = the parcel is READY (a post office NPC's [@COLLECTPARCEL] page marks every parcel
//    ready, or the server auto-sends). A ready parcel is collected with C.CollectParcel, "Collect all" is ONE
//    C.CollectAllParcels (MailBox.collectAllParcels); both wait for the server (the mail and the button stay busy
//    until S.ParcelCollected / S.ReceiveMail). A parcel that is not ready says "Visit a post office" (no button).
//    COLLECT_NEEDS_POST_OFFICE (default false = desktop + server: MailReadParcelDialog enables Collect whenever
//    Mail.Collected, PlayerObject.CollectMail has no NPC check) can gate both buttons on an open post office page;
//  - "New mail" composes a LETTER anywhere (C.SendMail with no gold or items); a PARCEL is composed only when the
//    server asks (S.MailSendRequest from an NPC's [@SENDPARCEL] page, the watch hook below), as the desktop and the
//    classic do. Parcel cells are a custom drop grid ("x:mail", bag items only): a drop sends C.MailLockedItem
//    Locked = true and the cell waits for the server's echo (Inventory.setLocked via the model); a right-click or the
//    "x" sends Locked = false. One cell without a stamp, five with one (a stamp is an ItemType.Nothing Shape 1 item in
//    the bag; PlayerObject.SendMail sends only items[0] without one). Gold is picked with the amount box (read from
//    Inventory.gold). The postage is the server's S.MailCost answer to C.MailCost; the breakdown shows the
//    S.ServerRules numbers (MailCostPer1KGold per whole 1,000 gold, MailItemInsurancePercentage of each item's price,
//    rounded down; free with a stamp when MailFreeWithStamp). Every close path of a parcel composer unlocks its items;
//  - the Sent folder (web, phase 2.6): C.RequestSentMail / S.SentMail (MailBox.sentList: only mail still in the
//    recipient's box, so entries vanish when they delete it), "Remove from Sent" = C.HideSentMail;
//  - composeTo(name, message?) opens a letter to someone (inspect, friends, relationship, territories call it);
//  - the minimap / menu-strip mail badge is the HUD's (MailBox.newMail / unread count), not this window's;
//  - no hotkey (the desktop has no mail key binding): the menu strip, the launcher and the minimap button open it;
//  - HIDDEN: the subject line (ClientMail has none: the first line of the message is shown as the subject), the
//    recipient's mailbox-full precheck (the server refuses with a chat line), the mock's contacts, bag picker,
//    stamp counter, local postage maths, local "take" and its toast.
// Narrow layout (a layer below 1040 px): narrower folder and list columns.

import type { ClientMail, ClientSentMail, UserItem } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { BindMode, ItemType } from "../../../protocol/enums.generated.ts";
import { isParcel, MAIL_PARCEL_CELLS, SENT_MAIL_DELAY, type MailBox } from "../../../game/mail.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import { serverRules, type ServerRules } from "../../../game/serverRules.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, clip, confirmTwice, fmt, fmtAgo, fmtDateTime, h, injectCss, later, plural, searchBox, setStyle, setText,
  svg, toggleSwitch, typingField, type Btn, type IconName, type Kid,
} from "../core/index.ts";
import type { CellRef } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (hud/gameMenu.ts WINDOW_KEYS.mail). */
export const MAIL_WINDOW = "mail";

/** The parcel cells (drop grid) and the read pane's attachment cells (display only). */
const SEND_GRID = "x:mail" as const;
const READ_GRID = "x:mailRead" as const;
/** PlayerObject.SendMail refuses longer messages (CRLF line breaks count 2). */
const MAX_MESSAGE = 500;
/** PlayerObject.NextMailTime: the server drops a second C.SendMail within 10 s silently. */
const SEND_COOLDOWN = 10_000;
/** NPCScript.CollectParcelKey (the post office page; NpcSession.lastPageKey has no brackets). */
const COLLECT_PARCEL_KEY = "@COLLECTPARCEL";
/** True = Collect / Collect all only while a post office page is open (the server does not require it). */
const COLLECT_NEEDS_POST_OFFICE = false;

/** ClientTextKeys (English defaults, Client/Localization/English.json) and the window's own texts. */
const T = {
  NoParcelsToCollect: "No parcels to collect.",
  AllParcelsCollected: "All parcels have been collected.",
  ParcelDeleteConfirm: "This parcel contains items or gold. Are you sure you want to delete it?",
  YouCannotMailItem: "You cannot mail this item.",
  CannotSwap: "You cannot swap items.",
  NeedStamp: "You need a stamp in your bag.",
  VisitPostOffice: "Visit a post office to collect it.",
};

type Folder = "Inbox" | "Unread" | "Attachments" | "Locked" | "Sent";
const FOLDERS: readonly [Folder, IconName][] = [["Inbox", "inbox"], ["Unread", "mail"], ["Attachments", "clip"], ["Locked", "lock"], ["Sent", "send"]];
type Mode = "read" | "letter" | "parcel";
/** A parcel cell: the bag item's UniqueID; `pending` until the server echoes S.MailLockedItem. */
interface Slot { uid: bigint; pending: boolean }

/** The first line of a message (shown as the subject) and the rest (the preview). */
function splitMessage(msg: string): [string, string] {
  const text = msg.replace(/\r\n?/g, "\n").trim();
  const nl = text.indexOf("\n");
  return nl < 0 ? [text, ""] : [text.slice(0, nl).trim(), text.slice(nl + 1).replace(/\n+/g, " ").trim()];
}
/** A sender badge: the first letter (system mail gets the mail icon). */
const badge = (name: string, system: boolean): HTMLElement =>
  h("div", "av" + (system ? " sys" : ""), system ? svg("mail") : (name.trim().charAt(0).toUpperCase() || "?"));
const ago = (binary: bigint): string => fmtAgo(dateFromBinary(binary).getTime());
/** The message as the desktop's multi-line text box sends it (CRLF). */
const crlf = (s: string): string => s.replace(/\r?\n/g, "\r\n");

const CSS = `
.hudm .mlwin { width: 900px; }
.hudm .mlwin .panel { height: 560px; display: flex; flex-direction: column; }
.hudm .mlwin .xmain { flex: 1; min-height: 0; display: flex; }
/* Folders */
.hudm .mlwin .nav { width: 160px; flex: none; padding: 10px 8px; display: flex; flex-direction: column; gap: 3px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mlwin .nav .compose { height: 30px; margin-bottom: 8px; }
.hudm .mlwin .nav .it { display: flex; align-items: center; gap: 9px; height: 30px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad);
  font: inherit; font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; background: none; text-align: left; }
.hudm .mlwin .nav .it:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .mlwin .nav .it.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .mlwin .nav .it i { margin-left: auto; font-style: normal; font-size: 10px; padding: 0 6px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .mlwin .nav .it i.hot { background: var(--ac); color: #fff; }
.hudm .mlwin .nav .sp { flex: 1; }
.hudm .mlwin .nav .cap { font-size: 10px; color: var(--dim); padding: 0 6px; }
.hudm .mlwin .nav .cap .bar { height: 4px; margin-top: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .mlwin .nav .cap .bar i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
/* List */
.hudm .mlwin .lst { width: 320px; flex: none; display: flex; flex-direction: column; min-height: 0; padding: 10px 8px 8px 10px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mlwin .bulk { display: flex; gap: 5px; margin: 8px 0 6px; }
.hudm .mlwin .bulk .btn { height: 22px; padding: 0 8px; font-size: 10px; }
.hudm .mlwin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 3px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .mlwin .lhint { font-size: 10px; color: var(--dim); padding-top: 6px; }
.hudm .mlwin .ml { position: relative; display: flex; gap: 9px; padding: 8px 8px 8px 12px; margin-bottom: 3px; cursor: pointer; border-radius: var(--rad); border: 1px solid transparent;
  background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .mlwin .ml:hover { border-color: color-mix(in srgb, var(--ac) 45%, transparent); background: color-mix(in srgb, var(--ac) 7%, transparent); }
.hudm .mlwin .ml.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .mlwin .ml.new::before { content: ""; position: absolute; left: 4px; top: 50%; width: 5px; height: 5px; margin-top: -2.5px; border-radius: 50%; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .mlwin .ml.busy { opacity: .55; }
.hudm .mlwin .av { width: 32px; height: 32px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 800; color: var(--tx);
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 1px solid var(--fr); }
.hudm .mlwin .av.sys { color: var(--ac); border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .mlwin .av svg.ic { width: 15px; height: 15px; }
.hudm .mlwin .ml .mid { flex: 1; min-width: 0; }
.hudm .mlwin .ml .top { display: flex; align-items: baseline; gap: 6px; }
.hudm .mlwin .ml .top b { flex: 1; min-width: 0; font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .mlwin .ml.new .top b, .hudm .mlwin .ml.new .subj { color: #fff; font-weight: 700; }
.hudm .mlwin .ml .top span { font-size: 10px; color: var(--dim); white-space: nowrap; }
.hudm .mlwin .ml .subj { font-size: 12px; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-top: 1px; }
.hudm .mlwin .ml .pv { font-size: 11px; color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-top: 1px; }
.hudm .mlwin .tags { display: flex; flex-wrap: wrap; gap: 5px; margin-top: 4px; }
.hudm .mlwin .tag { display: inline-flex; align-items: center; gap: 3px; font-size: 10px; font-weight: 600; padding: 0 6px; height: 16px; border-radius: 8px; color: var(--acd);
  border: 1px solid color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .mlwin .tag svg.ic { width: 10px; height: 10px; }
.hudm .mlwin .tag.gold { color: var(--gold); border-color: color-mix(in srgb, var(--gold) 45%, transparent); }
.hudm .mlwin .tag.done { color: var(--dim); border-color: color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mlwin .tag.wait { color: var(--warn); border-color: color-mix(in srgb, var(--warn) 45%, transparent); }
/* Reading pane / composer */
.hudm .mlwin .read, .hudm .mlwin .comp { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .mlwin .rscroll { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px 10px; display: flex; flex-direction: column; gap: 12px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .mlwin .rh { display: flex; gap: 12px; align-items: center; }
.hudm .mlwin .rh .av { width: 42px; height: 42px; font-size: 15px; }
.hudm .mlwin .rh h3 { margin: 0; font-size: 15px; color: #fff; overflow-wrap: anywhere; }
.hudm .mlwin .rh .meta { font-size: 11px; color: var(--dim); margin-top: 2px; }
.hudm .mlwin .rh .meta b { color: var(--tx); font-weight: 600; }
.hudm .mlwin .body { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 13px; line-height: 1.6; color: color-mix(in srgb, var(--tx) 92%, var(--dim)); padding: 12px 14px; border-radius: var(--rad);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .mlwin .lbl { display: flex; align-items: center; gap: 6px; margin-bottom: 6px; }
.hudm .mlwin .lbl svg.ic { width: 13px; height: 13px; color: var(--acd); }
.hudm .mlwin .att { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.hudm .mlwin .goldchip { height: 40px; padding: 0 14px; display: inline-flex; align-items: center; gap: 8px; border-radius: var(--rad); font-size: 14px; font-weight: 700; color: var(--gold);
  background: color-mix(in srgb, var(--gold) 8%, transparent); border: 1px solid color-mix(in srgb, var(--gold) 40%, transparent); font-variant-numeric: tabular-nums; }
.hudm .mlwin .goldchip svg.ic { width: 18px; height: 18px; }
.hudm .mlwin .waitnote { font-size: 11px; color: var(--warn); }
.hudm .mlwin .note { font-size: 11px; color: var(--ac); min-height: 14px; }
.hudm .mlwin .actions { display: flex; gap: 5px; align-items: center; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .mlwin .actions .btn { height: 26px; padding: 0 10px; }
.hudm .mlwin .actions .btn.on { color: var(--ac); border-color: var(--ac); }
.hudm .mlwin .actions .sp { flex: 1; }
.hudm .mlwin .actions .hint { font-size: 11px; color: var(--dim); }
.hudm .mlwin .comp input.fld { width: 100%; height: 26px; }
.hudm .mlwin .comp textarea.fld { width: 100%; height: 120px; line-height: 1.5; }
.hudm .mlwin .count { font-size: 10px; color: var(--dim); text-align: right; margin-top: 2px; }
.hudm .mlwin .slots { display: flex; gap: 6px; align-items: center; }
.hudm .mlwin .slot { position: relative; }
.hudm .mlwin .slot.off { opacity: .35; }
.hudm .mlwin .slot .x { position: absolute; right: -5px; top: -5px; z-index: 2; width: 16px; height: 16px; padding: 0; border: 0; border-radius: 50%; display: none; align-items: center; justify-content: center;
  font: inherit; font-size: 11px; line-height: 1; color: #fff; background: var(--bad); cursor: pointer; }
.hudm .mlwin .slot:hover .x.has { display: flex; }
.hudm .mlwin .shint { font-size: 10px; color: var(--dim); margin-top: 4px; }
.hudm .mlwin .row2 { display: flex; gap: 16px; align-items: flex-start; flex-wrap: wrap; }
.hudm .mlwin .row2 .grp { display: flex; align-items: center; gap: 8px; }
.hudm .mlwin .row2 .gamt { min-width: 110px; font-weight: 700; color: var(--gold); font-variant-numeric: tabular-nums; }
.hudm .mlwin .cost { display: grid; grid-template-columns: 1fr auto; gap: 3px 12px; font-size: 11px; padding: 8px 12px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .mlwin .cost span { color: var(--dim); } .hudm .mlwin .cost b { text-align: right; color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .mlwin .cost .tot { font-size: 13px; color: var(--gold); } .hudm .mlwin .cost .free { color: var(--ac); }
.hudm .mlwin .err { font-size: 11px; color: var(--bad-soft); min-height: 14px; }
/* Narrow layer */
.hudm.narrow .mlwin { width: 740px; }
.hudm.narrow .mlwin .nav { width: 124px; padding: 8px 6px; }
.hudm.narrow .mlwin .nav .it { padding: 0 6px; gap: 6px; }
.hudm.narrow .mlwin .lst { width: 250px; }
.hudm.narrow .mlwin .ml .av { display: none; }
.hudm.narrow .mlwin .rscroll { padding: 10px 12px 8px; }
.hudm.narrow .mlwin .actions { padding: 8px 10px 10px; flex-wrap: wrap; }
`;

export class MailWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private readonly box: MailBox;
  private folder: Folder = "Inbox";
  /** The picked mail (resolved by MailID on every render: S.ReceiveMail replaces the objects). */
  private picked: bigint | null = null;
  private pickedSent: bigint | null = null;
  private mode: Mode = "read";
  private query = "";
  private note = "";
  /** Mails waiting for the server's answer (delete / collect): drawn busy, their buttons off. */
  private readonly busy = new Set<bigint>();
  private collectingAll = false;
  private readingAll = false;
  private listKey = "";
  private readKey = "";
  private navKey = "";

  // — Composer state —
  private readonly slots: (Slot | null)[] = new Array<Slot | null>(MAIL_PARCEL_CELLS).fill(null);
  private gold = 0;
  private stamped = false;
  /** The server's postage quote (S.MailCost), null while asked / not asked. */
  private cost: number | null = null;
  private sending = false;
  private sendTo = "";
  private nextSend = 0;
  private stopSendWait: (() => void) | null = null;

  // — DOM, built once —
  private readonly nav = h("div", "nav");
  private readonly folderEls = new Map<Folder, { el: HTMLButtonElement; count: HTMLElement }>();
  private readonly capText = h("span");
  private readonly capFill = h("i");
  private readonly capBox = h("div", "cap");
  private readonly collectAllBtn: Btn;
  private readonly markReadBtn: Btn;
  private readonly refreshSentBtn: Btn;
  private readonly list = h("div", "list");
  private readonly listHint = h("div", "lhint");
  private readonly read = h("div", "read");
  private readonly readCells: ItemCell[] = [];
  private readonly comp = h("div", "comp");
  private readonly compTitle = h("h3");
  private readonly compMeta = h("div", "meta");
  private readonly toInput = typingField(h("input", "fld"));
  private readonly toList = h("datalist");
  private friendsKey = "";
  private readonly bodyInput = typingField(h("textarea", "fld"));
  private readonly countEl = h("div", "count");
  private readonly parcelBox = h("div");
  private readonly slotLbl = h("span");
  private readonly slotWraps: { el: HTMLElement; x: HTMLButtonElement }[] = [];
  private readonly slotHint = h("div", "shint");
  private readonly goldAmt = h("span", "gamt");
  private readonly goldBtn: Btn;
  private readonly stampSw: ReturnType<typeof toggleSwitch>;
  private readonly stampLbl = h("span", "hint");
  private readonly costBox = h("div", "cost");
  private readonly errEl = h("div", "err");
  private readonly sendBtn: Btn;

  constructor(private readonly ui: ModernUiHost) {
    super(ui.rt, "mlwin", 52, 40);
    injectCss("win-mail", CSS);
    this.m = ui.ctx.models;
    this.box = this.m.mail;
    const sig = this.signal;

    // Folders
    const compose = button("New mail", () => this.composeTo(""), { cls: "main compose", icon: "compose", title: "Write a letter (parcels are sent from a post office)" });
    this.nav.append(compose.el);
    for (const [f, icon] of FOLDERS) {
      const count = h("i");
      const el = h("button", "it", svg(icon), f, count);
      el.type = "button";
      el.addEventListener("click", () => this.pickFolder(f), { signal: sig });
      this.folderEls.set(f, { el, count });
      this.nav.append(el);
    }
    this.capBox.append(this.capText, h("div", "bar", this.capFill));
    this.nav.append(h("div", "sp"), this.capBox);

    // List: search, bulk actions, rows (one delegated click listener)
    const search = searchBox("Search mail…", (q) => { this.query = q.toLowerCase(); this.invalidate(); }, { signal: sig });
    this.collectAllBtn = button("Collect all", () => this.collectAll(), { icon: "coin", title: "Collect every ready parcel" });
    this.markReadBtn = button("Mark all read", () => this.markAllRead(), { icon: "mail" });
    this.refreshSentBtn = button("Refresh", () => this.requestSent(), { icon: "repeat", title: "Ask the server for your sent mail again" });
    this.list.addEventListener("click", (e) => {
      const row = (e.target as Element | null)?.closest<HTMLElement>(".ml[data-id]");
      if (row) this.pickRow(BigInt(row.dataset.id!));
    }, { signal: sig });
    const lst = h("div", "lst", search.el, h("div", "bulk", this.collectAllBtn.el, this.markReadBtn.el, this.refreshSentBtn.el), this.list, this.listHint);

    // Read pane attachments: a display-only custom grid over the picked mail's items (tooltips through the cells).
    const offRead = ui.cells.addTarget(READ_GRID, { item: (i) => this.current()?.Items[i] ?? null, accepts: () => null, drop: () => {} });
    sig.addEventListener("abort", offRead, { once: true });

    // Composer (letter or parcel)
    this.toInput.placeholder = "Recipient";
    this.toInput.maxLength = 30;
    this.toList.id = "hudm-mail-to";
    this.toInput.setAttribute("list", this.toList.id);
    this.toInput.addEventListener("input", () => this.invalidate(), { signal: sig });
    this.bodyInput.maxLength = MAX_MESSAGE;
    this.bodyInput.placeholder = "Write your letter…";
    this.bodyInput.addEventListener("input", () => this.invalidate(), { signal: sig });

    const offSend = ui.cells.addTarget(SEND_GRID, {
      item: (i) => this.slotItem(i),
      locked: (i) => !!this.slots[i]?.pending,
      accepts: (from, item, i) => this.accepts(from, item, i),
      verb: () => "Attach",
      drop: (_from, item, i) => this.attach(i, item),
      use: (i) => this.detach(i),
    });
    sig.addEventListener("abort", offSend, { once: true });
    const slots = h("div", "slots");
    for (let i = 0; i < MAIL_PARCEL_CELLS; i++) {
      const cell = ui.cells.cell(SEND_GRID, i, { size: 40, signal: sig, empty: svg("clip") });
      const x = h("button", "x", "×");
      x.type = "button";
      x.title = "Take it out";
      x.addEventListener("click", () => this.detach(i), { signal: sig });
      const el = h("div", "slot", cell.el, x);
      this.slotWraps.push({ el, x });
      slots.append(el);
    }
    this.goldBtn = button("Add gold", () => void this.pickGold(), { icon: "coin", title: "Gold to send with the parcel" });
    this.stampSw = toggleSwitch(false, (on) => this.setStamp(on), { small: true, label: "Mail stamp", title: "A stamp lets you send 5 items" });
    this.parcelBox.append(
      h("div", "", h("div", "lbl", svg("clip"), this.slotLbl), slots, this.slotHint),
      h("div", "row2",
        h("div", "", h("div", "lbl", svg("coin"), "Gold"), h("div", "grp", this.goldAmt, this.goldBtn.el)),
        h("div", "", h("div", "lbl", svg("stamp"), "Mail stamp"), h("div", "grp", this.stampSw.el, this.stampLbl))),
      this.costBox);
    const discard = button("Discard", () => this.discard(), { icon: "trash" });
    this.sendBtn = button("Send", () => this.send(), { cls: "main", icon: "send" });
    this.comp.append(
      h("div", "rscroll",
        h("div", "rh", h("div", "av", svg("compose")), h("div", "", this.compTitle, this.compMeta)),
        h("div", "", h("div", "lbl", "To"), this.toInput, this.toList),
        h("div", "", h("div", "lbl", "Message"), this.bodyInput, this.countEl),
        this.parcelBox, this.errEl),
      h("div", "actions", discard.el, h("div", "sp"), this.sendBtn.el));

    this.el.append(h("div", "panel", this.titleBar("MAIL", "Close"), h("div", "xmain", this.nav, lst, this.read, this.comp)));

    // Model events: re-render while shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(this.box.changed.on(() => {
      // S.ReceiveMail answers read / lock / delete / collect / mark-all-read: the waits end here
      this.busy.clear();
      this.collectingAll = this.readingAll = false;
      this.ui.cells.refresh();
      this.invalidate();
    }));
    on(this.box.sentChanged.on(() => this.invalidate()));
    on(this.box.collected.on(({ result }) => {
      this.busy.clear();
      this.collectingAll = false;
      this.say(result === 1 ? "Collected to your bag." : result === 0 ? T.AllParcelsCollected : T.NoParcelsToCollect);
      this.invalidate();
    }));
    on(this.box.cost.on(({ cost }) => { if (this.mode === "parcel") { this.cost = cost; this.invalidate(); } }));
    on(this.box.sent.on(({ result }) => this.sentResult(result)));
    on(this.m.inventory.subscribe(() => { if (this.mode === "parcel") { this.lockArrived(); this.invalidate(); } }));
    on(this.m.friends.changed.on(() => { if (this.mode !== "read") this.invalidate(); }));
    on(this.m.npcSession.page.on(() => this.invalidate()));
    on(this.m.npcSession.closed.on(() => this.invalidate()));
    on(serverRules(ui.ctx.session).changed.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.listKey = this.readKey = ""; this.invalidate(); } }));
  }

  // — Public API —

  /** MailComposeLetterDialog.ComposeMail(name): opens the window on a letter to `name` ("" = type it). Inspect, friends,
   *  relationship and territories call this (rt.window("mail").composeTo). A parcel being composed is discarded (its
   *  items are unlocked through the server). */
  composeTo(name: string, message = ""): void {
    if (this.mode === "parcel") this.resetParcel();
    this.mode = "letter";
    this.clearDraft(name, message);
    this.toggle(true);
    this.invalidate();
    this.focusDraft();
  }

  /** GameScene.MailSendRequest (an NPC's [@SENDPARCEL] page): opens the parcel composer and the bag. */
  composeParcel(): void {
    if (this.mode === "parcel") this.resetParcel();
    this.mode = "parcel";
    this.clearDraft("", "");
    this.toggle(true);
    this.ui.openWindow("inventory", true); // the desktop shows the inventory with the parcel dialog (drag items from it)
    this.invalidate();
    this.quote();
    this.focusDraft();
  }

  // — Lifecycle —

  protected override shown(): void {
    this.listKey = this.readKey = this.navKey = ""; // the ages ("5 min ago") are redrawn on every open
    this.invalidate();
    if (this.folder === "Sent") this.requestSent();
  }

  /** Every close path: a parcel composer hands its items back (C.MailLockedItem Locked = false, MailComposeParcelDialog.Hide). */
  protected override closed(): void {
    if (this.mode === "parcel") { this.resetParcel(); this.mode = "read"; }
    this.note = "";
  }

  protected override disposed(): void { this.stopSendWait?.(); }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    this.renderNav();
    this.renderBulk();
    this.renderList();
    this.read.hidden = this.mode !== "read";
    this.comp.hidden = this.mode === "read";
    if (this.mode === "read") this.renderRead(); else this.renderComposer();
  }

  private inbox(): readonly ClientMail[] { return this.box.list; }
  private rules(): ServerRules | null { return serverRules(this.ui.ctx.session).rules; }
  /** The picked inbox mail (null in the Sent folder or when it is gone). */
  private current(): ClientMail | null { return this.folder === "Sent" || this.picked === null ? null : this.box.find(this.picked); }
  private currentSent(): ClientSentMail | null {
    if (this.folder !== "Sent" || this.pickedSent === null) return null;
    return this.box.sentList.find((s) => s.MailID === this.pickedSent) ?? null;
  }
  /** Gold or items still in the parcel (collecting empties it). */
  private pending(m: ClientMail): boolean { return isParcel(m); }
  /** A parcel that can be collected now (ClientMail.Collected = it reached you). */
  private ready(m: ClientMail): boolean { return m.Collected && isParcel(m); }
  private postOfficeOpen(): boolean {
    const s = this.m.npcSession;
    return s.open && s.lastPageKey.toUpperCase() === COLLECT_PARCEL_KEY;
  }
  private canCollectHere(): boolean { return !COLLECT_NEEDS_POST_OFFICE || this.postOfficeOpen(); }

  private renderNav(): void {
    const list = this.inbox();
    let unread = 0, att = 0, locked = 0;
    for (const m of list) { if (!m.Opened) unread++; if (this.pending(m)) att++; if (m.Locked) locked++; }
    const counts: Record<Folder, number> = { Inbox: list.length, Unread: unread, Attachments: att, Locked: locked, Sent: this.box.sentList.length };
    const cap = this.rules()?.MailCapacity ?? 0;
    const key = `${this.folder}|${list.length}|${unread}|${att}|${locked}|${counts.Sent}|${cap}`;
    if (key === this.navKey) return;
    this.navKey = key;
    for (const [f, r] of this.folderEls) {
      r.el.classList.toggle("on", f === this.folder);
      setText(r.count, String(counts[f]));
      r.count.classList.toggle("hot", f === "Unread" && counts[f] > 0);
    }
    this.capBox.hidden = cap <= 0;
    if (cap > 0) {
      setText(this.capText, `Mailbox ${list.length} / ${cap}`);
      setStyle(this.capFill, "width", `${Math.min(100, list.length / cap * 100).toFixed(1)}%`);
    }
  }

  private renderBulk(): void {
    const sent = this.folder === "Sent";
    this.collectAllBtn.el.hidden = this.markReadBtn.el.hidden = sent;
    this.refreshSentBtn.el.hidden = !sent;
    if (sent) return;
    let ready = 0, waiting = 0, unread = 0;
    for (const m of this.inbox()) {
      if (this.ready(m)) ready++; else if (this.pending(m)) waiting++;
      if (!m.Opened) unread++;
    }
    this.collectAllBtn.setLabel(ready ? `Collect all (${ready})` : "Collect all");
    this.collectAllBtn.setDisabled(this.collectingAll || !ready || !this.canCollectHere(),
      this.collectingAll ? "Waiting for the server" : !ready ? (waiting ? `${T.VisitPostOffice} (${waiting} waiting)` : T.NoParcelsToCollect) : T.VisitPostOffice);
    this.markReadBtn.setDisabled(this.readingAll || !unread, this.readingAll ? "Waiting for the server" : "Nothing unread");
  }

  private matches(m: ClientMail): boolean {
    switch (this.folder) {
      case "Unread": if (m.Opened) return false; break;
      case "Attachments": if (!this.pending(m)) return false; break;
      case "Locked": if (!m.Locked) return false; break;
    }
    const q = this.query;
    if (!q) return true;
    if (m.SenderName.toLowerCase().includes(q) || m.Message.toLowerCase().includes(q)) return true;
    for (const it of m.Items) if (this.m.itemInfos.get(it.ItemIndex)?.Name.toLowerCase().includes(q)) return true;
    return false;
  }

  private renderList(): void {
    if (this.folder === "Sent") { this.renderSentList(); return; }
    const rows = this.inbox().filter((m) => this.matches(m));
    const sel = this.mode === "read" ? this.picked : null;
    const key = `in|${this.folder}|${this.query}|${String(sel)}|${[...this.busy].join(",")}|`
      + rows.map((m) => `${m.MailID}:${+m.Opened}${+m.Locked}${+m.Collected}${m.Gold}:${m.Items.length}`).join(",");
    if (key === this.listKey) return;
    this.listKey = key;
    const els: HTMLElement[] = [];
    for (const m of rows) {
      const [subj, pv] = splitMessage(m.Message);
      const tags = h("div", "tags");
      if (m.Gold) tags.append(h("span", "tag gold", svg("coin"), fmt(m.Gold)));
      if (m.Items.length) tags.append(h("span", "tag", svg("clip"), plural(m.Items.length, "item")));
      if (this.pending(m) && !m.Collected) tags.append(h("span", "tag wait", "At the post office"));
      if (m.Locked) tags.append(h("span", "tag", svg("lock"), "Locked"));
      if (!m.CanReply) tags.append(h("span", "tag done", "No reply"));
      const row = h("div", "ml" + (m.MailID === sel ? " sel" : "") + (!m.Opened ? " new" : "") + (this.busy.has(m.MailID) ? " busy" : ""),
        badge(m.SenderName, !m.CanReply),
        h("div", "mid", h("div", "top", h("b", "", m.SenderName), h("span", "", ago(m.DateSent))),
          h("div", "subj", subj || "(no message)"), pv ? h("div", "pv", pv) : null, tags.childElementCount ? tags : null));
      row.dataset.id = String(m.MailID);
      els.push(row);
    }
    if (!els.length) els.push(h("div", "empty-note", this.query ? "No mail matches." : this.folder === "Unread" ? "You're all caught up." : "Nothing here."));
    this.list.replaceChildren(...els);
    setText(this.listHint, this.inbox().some((m) => this.pending(m) && !m.Collected) ? "Parcels marked \"At the post office\" are handed over by a post office NPC." : "");
  }

  private renderSentList(): void {
    const q = this.query;
    const rows = this.box.sentList.filter((s) => !q || s.RecipientName.toLowerCase().includes(q) || s.Message.toLowerCase().includes(q));
    const sel = this.mode === "read" ? this.pickedSent : null;
    const key = `sent|${q}|${String(sel)}|` + rows.map((s) => `${s.MailID}:${+s.Opened}${+s.Collected}`).join(",");
    if (key === this.listKey) return;
    this.listKey = key;
    const els: HTMLElement[] = [];
    for (const s of rows) {
      const [subj, pv] = splitMessage(s.Message);
      const tags = h("div", "tags");
      if (s.Gold) tags.append(h("span", "tag gold done", svg("coin"), fmt(s.Gold)));
      if (s.ItemCount) tags.append(h("span", "tag done", svg("clip"), plural(s.ItemCount, "item")));
      tags.append(h("span", "tag" + (s.Opened ? " done" : ""), s.Opened ? "Read" : "Not read yet"));
      const row = h("div", "ml" + (s.MailID === sel ? " sel" : ""),
        badge(s.RecipientName, false),
        h("div", "mid", h("div", "top", h("b", "", `To ${s.RecipientName}`), h("span", "", ago(s.DateSent))),
          h("div", "subj", subj || "(no message)"), pv ? h("div", "pv", pv) : null, tags));
      row.dataset.id = String(s.MailID);
      els.push(row);
    }
    if (!els.length) els.push(h("div", "empty-note", q ? "No mail matches." : "No sent mail in anyone's mailbox."));
    this.list.replaceChildren(...els);
    setText(this.listHint, "Only mail still in the recipient's mailbox is listed: it goes when they delete it.");
  }

  private renderRead(): void {
    if (this.folder === "Sent") { this.renderSentRead(); return; }
    const m = this.current();
    const here = this.canCollectHere();
    const key = m ? `${m.MailID}|${+m.Opened}|${+m.Locked}|${+m.Collected}|${m.Gold}|${m.Items.length}|${+this.busy.has(m.MailID)}|${+here}|${this.note}` : `none|${this.note}`;
    if (key === this.readKey) return;
    this.readKey = key;
    if (!m) {
      this.read.replaceChildren(h("div", "rscroll", h("div", "empty-note", "Pick a mail to read it."), this.note ? h("div", "note", this.note) : null));
      return;
    }
    const [subj] = splitMessage(m.Message);
    const parts: Kid[] = [
      h("div", "rh", badge(m.SenderName, !m.CanReply), h("div", "",
        h("h3", "", clip(subj || "(no message)", 80)),
        h("div", "meta", "From ", h("b", "", m.SenderName), ` · ${fmtDateTime(dateFromBinary(m.DateSent))}${m.CanReply ? "" : " · system mail"}`))),
      h("div", "body", m.Message.replace(/\r\n?/g, "\n")),
    ];
    if (m.Gold || m.Items.length) {
      const att = h("div", "att");
      if (m.Gold) att.append(h("div", "goldchip", svg("coin"), `${fmt(m.Gold)} gold`));
      while (this.readCells.length < m.Items.length) {
        this.readCells.push(this.ui.cells.cell(READ_GRID, this.readCells.length, { size: 40, readOnly: true, signal: this.signal }));
      }
      for (let i = 0; i < m.Items.length; i++) att.append(this.readCells[i]!.el);
      parts.push(h("div", "", h("div", "lbl", svg("clip"), m.Collected ? "Attachments" : "Attachments · waiting at the post office"), att));
      if (!m.Collected) parts.push(h("div", "waitnote", `${T.VisitPostOffice} A post office NPC hands your parcels over.`));
    }
    if (this.note) parts.push(h("div", "note", this.note));
    this.ui.cells.refresh();
    this.read.replaceChildren(h("div", "rscroll", ...parts), this.readActions(m, here));
  }

  private readActions(m: ClientMail, here: boolean): HTMLElement {
    const bar = h("div", "actions");
    const busy = this.busy.has(m.MailID);
    const reply = button("Reply", () => this.reply(m.MailID), { icon: "reply" });
    reply.setDisabled(!m.CanReply, "System mail can't be answered");
    const lock = button(m.Locked ? "Unlock" : "Lock", () => this.toggleLock(m.MailID), { icon: m.Locked ? "unlock" : "lock", title: "Locked mail can't be deleted", cls: m.Locked ? "on" : "" });
    const del = button("Delete", () => void this.remove(m.MailID), { icon: "trash", title: this.pending(m) ? "The gold and items are deleted with the mail" : "" });
    del.setDisabled(m.Locked || busy, m.Locked ? "Unlock it first" : "Waiting for the server");
    bar.append(reply.el, lock.el, del.el, h("div", "sp"));
    if (this.ready(m)) {
      const what = [m.Gold ? `${fmt(m.Gold)} gold` : "", m.Items.length ? plural(m.Items.length, "item") : ""].filter(Boolean).join(" + ");
      const collect = button(`Collect ${what}`, () => this.collect(m.MailID), { cls: "main", icon: "coin" });
      collect.setDisabled(busy || !here, busy ? "Waiting for the server" : T.VisitPostOffice);
      bar.append(collect.el);
    } else if (this.pending(m)) {
      bar.append(h("span", "hint", "Visit a post office"));
    }
    return bar;
  }

  private renderSentRead(): void {
    const s = this.currentSent();
    const key = s ? `sent|${s.MailID}|${+s.Opened}|${+s.Collected}|${this.note}` : `sentnone|${this.note}`;
    if (key === this.readKey) return;
    this.readKey = key;
    if (!s) {
      this.read.replaceChildren(h("div", "rscroll", h("div", "empty-note", "Pick a mail to read it."), this.note ? h("div", "note", this.note) : null));
      return;
    }
    const [subj] = splitMessage(s.Message);
    const parts: Kid[] = [
      h("div", "rh", badge(s.RecipientName, false), h("div", "",
        h("h3", "", clip(subj || "(no message)", 80)),
        h("div", "meta", "To ", h("b", "", s.RecipientName), ` · ${fmtDateTime(dateFromBinary(s.DateSent))}`))),
      h("div", "body", s.Message.replace(/\r\n?/g, "\n")),
    ];
    if (s.Gold || s.ItemCount) {
      const sentWith = [s.Gold ? `${fmt(s.Gold)} gold` : "", s.ItemCount ? plural(s.ItemCount, "item") : ""].filter(Boolean).join(" and ");
      parts.push(h("div", "", h("div", "lbl", svg("clip"), "Sent with this mail"), h("div", "hint", sentWith + (s.Collected ? "" : " · waiting at their post office"))));
    }
    if (this.note) parts.push(h("div", "note", this.note));
    const hide = confirmTwice("Remove from Sent", () => this.hideSent(s.MailID), { icon: "trash", confirmLabel: "Click again to remove", signal: this.signal,
      title: "Remove it from your Sent folder (they keep the mail)" });
    const bar = h("div", "actions", h("span", "hint", s.Opened ? `${s.RecipientName} has read it` : "Not read yet"), h("div", "sp"), hide.el);
    this.read.replaceChildren(h("div", "rscroll", ...parts), bar);
  }

  private renderComposer(): void {
    const parcel = this.mode === "parcel";
    setText(this.compTitle, parcel ? "New parcel" : "New letter");
    setText(this.compMeta, parcel ? "Gold and items cost postage unless you use a stamp." : "Letters are free. Send gold and items from a post office.");
    this.parcelBox.hidden = !parcel;
    this.renderSuggestions();
    const len = crlf(this.bodyInput.value).length;
    setText(this.countEl, `${len} / ${MAX_MESSAGE}`);
    if (parcel) this.renderParcel();
    const why = this.sendProblem();
    this.sendBtn.setLabel(this.sending ? "Sending…" : "Send");
    this.sendBtn.setDisabled(!!why, why);
  }

  /** Recipient suggestions: the friend list (not blocked). Rebuilt only when it changed. */
  private renderSuggestions(): void {
    const names: string[] = [];
    for (const f of this.m.friends.list) if (!f.Blocked) names.push(f.Name);
    const key = names.join("|");
    if (key === this.friendsKey) return;
    this.friendsKey = key;
    this.toList.replaceChildren(...names.map((n) => { const o = h("option"); o.value = n; return o; }));
  }

  private renderParcel(): void {
    const open = this.stamped ? MAIL_PARCEL_CELLS : 1;
    let used = 0;
    for (let i = 0; i < MAIL_PARCEL_CELLS; i++) {
      const s = this.slots[i] ?? null, w = this.slotWraps[i]!;
      if (s) used++;
      w.el.classList.toggle("off", i >= open);
      w.el.title = i >= open ? "Use a mail stamp to send up to 5 items" : s ? "" : "Drag an item here from your bag";
      w.x.classList.toggle("has", !!s && !s.pending);
    }
    setText(this.slotLbl, `Items · ${used} / ${open}${this.stamped ? "" : " (5 with a stamp)"}`);
    setText(this.slotHint, "Drag items here from your bag · right-click a cell to take it out.");
    setText(this.goldAmt, `${fmt(this.gold)} gold`);
    this.goldBtn.setLabel(this.gold ? "Change" : "Add gold");
    const stamps = this.stampCount();
    this.stampSw.set(this.stamped);
    this.stampSw.lock(!this.stamped && stamps <= 0, T.NeedStamp);
    setText(this.stampLbl, stamps > 0 ? `${stamps} in your bag` : "none in your bag");

    // Postage: the server's quote; the split by the S.ServerRules numbers
    const r = this.rules();
    const free = this.stamped && !!r?.MailFreeWithStamp;
    const goldPart = r && !free ? Math.floor(this.gold / 1000) * r.MailCostPer1KGold : null;
    const ins = this.cost !== null && goldPart !== null ? Math.max(0, this.cost - goldPart) : null;
    const kids: HTMLElement[] = [];
    if (r) {
      kids.push(h("span", "", `Gold postage (${fmt(r.MailCostPer1KGold)} per whole 1,000)`), h("b", "", free ? "0" : fmt(goldPart ?? 0)),
        h("span", "", `Item insurance (${r.MailItemInsurancePercentage} % of each item's price)`), h("b", "", free ? "0" : ins === null ? "…" : fmt(ins)));
    }
    kids.push(h("span", "", "Total"), free && this.cost === 0
      ? h("b", "tot free", this.gold ? `${fmt(this.gold)} gold · postage free with a stamp` : "Free with a stamp")
      : h("b", "tot", this.cost === null ? "…" : `${fmt(this.gold + this.cost)} gold`));
    this.costBox.replaceChildren(...kids);
  }

  /** Why Send is off ("" = it may send). */
  private sendProblem(): string {
    if (this.sending) return "Waiting for the server";
    const wait = this.nextSend - this.ui.ctx.now();
    if (wait > 0) return `You can send again in ${Math.ceil(wait / 1000)} s`;
    if (!this.toInput.value.trim()) return "Who is it for?";
    if (crlf(this.bodyInput.value).length > MAX_MESSAGE) return `The message is longer than ${MAX_MESSAGE} characters`;
    if (this.mode === "letter") return this.bodyInput.value.trim() ? "" : "Write something first";
    if (this.slots.some((s) => s?.pending)) return "Waiting for the server";
    if (!this.bodyInput.value.trim() && !this.gold && !this.slots.some(Boolean)) return "Write something, or add gold or items";
    if (this.cost === null) return "Working out the postage…";
    if (this.gold + this.cost > this.m.inventory.gold) return "You don't have enough gold for this and the postage";
    return "";
  }

  // — Actions: list / reading —

  private say(text: string): void {
    this.note = text;
    this.invalidate();
  }

  private pickFolder(f: Folder): void {
    if (this.folder === f && this.mode === "read") return;
    if (this.mode === "parcel") this.resetParcel();
    this.folder = f;
    this.mode = "read";
    this.note = "";
    this.invalidate();
    if (f === "Sent") this.requestSent();
  }

  private pickRow(id: bigint): void {
    if (this.mode === "parcel") this.resetParcel();
    this.mode = "read";
    this.note = "";
    if (this.folder === "Sent") { this.pickedSent = id; this.invalidate(); return; }
    this.picked = id;
    const m = this.box.find(id);
    if (m) this.box.read(m); // C.ReadMail when unopened: the server answers with the list (nothing marked locally)
    this.ui.cells.refresh();
    this.invalidate();
  }

  private markAllRead(): void {
    const ids: bigint[] = [];
    for (const m of this.inbox()) if (!m.Opened) ids.push(m.MailID);
    if (this.box.readMails(ids) > 0) { this.readingAll = true; this.invalidate(); }
  }

  private collectAll(): void {
    if (!this.canCollectHere()) { this.say(T.VisitPostOffice); return; }
    if (this.box.collectAllParcels()) { this.collectingAll = true; this.say(""); }
    else this.say(T.NoParcelsToCollect);
  }

  private collect(id: bigint): void {
    const m = this.box.find(id);
    if (!m || !this.ready(m) || this.busy.has(id)) return;
    if (!this.canCollectHere()) { this.say(T.VisitPostOffice); return; }
    this.busy.add(id);
    this.box.collect(id);
    this.say("");
  }

  private toggleLock(id: bigint): void {
    const m = this.box.find(id);
    if (!m) return;
    this.box.toggleLock(m); // MailReadLetterDialog.LockButton (the server answers with the list)
    this.readKey = this.listKey = "";
    this.invalidate();
  }

  /** DeleteButton: locked mail is protected; a parcel with gold / items asks first (they are lost). */
  private async remove(id: bigint): Promise<void> {
    const m = this.box.find(id);
    if (!m || m.Locked || this.busy.has(id)) return;
    const parcel = this.pending(m);
    const ok = await this.ui.rt.popups.ask(parcel ? T.ParcelDeleteConfirm : `Delete this mail from ${m.SenderName}?`,
      { title: "Delete mail", kind: parcel ? "danger" : "warning", okCancel: true, ...(m.Gold ? { gold: m.Gold } : {}) });
    const now = this.box.find(id);
    if (!ok || !now || now.Locked) return;
    this.busy.add(id);
    this.box.delete(id);
    this.invalidate();
  }

  private reply(id: bigint): void {
    const m = this.box.find(id);
    if (!m || !m.CanReply) return;
    const quote = `\n\n— ${m.SenderName} wrote —\n${m.Message.replace(/\r\n?/g, "\n")}`;
    this.composeTo(m.SenderName, quote.slice(0, MAX_MESSAGE - 20));
    this.bodyInput.setSelectionRange(0, 0);
  }

  private requestSent(): void {
    if (this.box.requestSent()) return;
    // too soon after the last request (the model's SENT_MAIL_DELAY): ask once more when it allows
    later(() => { if (this.open && this.folder === "Sent") this.box.requestSent(); }, SENT_MAIL_DELAY, this.signal);
  }

  private hideSent(id: bigint): void {
    if (!this.box.hideSent(id)) { this.say("Wait a moment and try again."); return; }
    if (this.pickedSent === id) this.pickedSent = null;
    this.invalidate();
  }

  // — Actions: composer —

  private clearDraft(to: string, message: string): void {
    this.toInput.value = to;
    this.bodyInput.value = message;
    this.errEl.textContent = "";
    this.note = "";
    this.friendsKey = "";
  }

  private focusDraft(): void {
    later(() => {
      if (!this.open || this.mode === "read") return;
      (this.toInput.value ? this.bodyInput : this.toInput).focus({ preventScroll: true });
    }, 0, this.signal);
  }

  /** Discard: back to reading (a parcel's items are unlocked). */
  private discard(): void {
    if (this.mode === "parcel") this.resetParcel();
    this.mode = "read";
    this.invalidate();
  }

  private stampCount(): number {
    let n = 0;
    for (const it of this.m.inventory.inventory) {
      const info = it ? this.m.inventory.info(it) : undefined;
      if (it && info && info.Type === ItemType.Nothing && info.Shape === 1) n += it.Count;
    }
    return n;
  }

  /** StampParcel: needs a stamp in the bag; without one only the first cell goes, so cells 2-5 are handed back. */
  private setStamp(on: boolean): void {
    if (this.mode !== "parcel") return;
    if (on && this.stampCount() <= 0) { this.stampSw.set(false); setText(this.errEl, T.NeedStamp); return; }
    this.stamped = on;
    if (!on) for (let i = 1; i < MAIL_PARCEL_CELLS; i++) this.detach(i, false);
    setText(this.errEl, "");
    this.quote();
  }

  /** GoldSendLabel: the amount box up to the gold in the bag (Inventory.gold). */
  private async pickGold(): Promise<void> {
    if (this.mode !== "parcel") return;
    const max = this.m.inventory.gold;
    if (max <= 0 && !this.gold) { setText(this.errEl, "You have no gold."); return; }
    const v = await this.ui.rt.popups.amount({ title: "Send gold", text: "How much gold goes in the parcel?", max, min: 0, value: Math.min(this.gold, max), gold: true });
    if (v === null || this.mode !== "parcel") return;
    this.gold = Math.max(0, Math.min(v, this.m.inventory.gold));
    this.quote();
  }

  private slotItem(i: number): UserItem | null {
    const s = this.slots[i];
    if (!s) return null;
    for (const it of this.m.inventory.inventory) if (it && it.UniqueID === s.uid) return it;
    return null;
  }

  /** MirItemCell "To Mail": from the bag only, an open empty cell, not DontTrade / NoMail (the server's checks). */
  private accepts(from: CellRef, item: UserItem, i: number): string | null {
    if (this.mode !== "parcel" || from.grid !== "inv") return null;
    if (this.sending) return "Waiting for the server";
    if (i > 0 && !this.stamped) return "Needs a mail stamp";
    if (this.slots[i]) return T.CannotSwap;
    if (this.slots.some((s) => s?.uid === item.UniqueID)) return "Already attached";
    const info = this.m.inventory.info(item);
    if (info && (info.Bind & (BindMode.DontTrade | BindMode.NoMail)) !== 0) return T.YouCannotMailItem;
    if (item.Rental && (item.Rental.BindingFlags & BindMode.DontTrade) !== 0) return T.YouCannotMailItem;
    return "";
  }

  /** A drop: the cell waits for the server to lock the bag item (C.MailLockedItem Locked = true, echoed back). */
  private attach(i: number, item: UserItem): void {
    if (this.accepts({ grid: "inv", index: 0 }, item, i) !== "") return;
    this.slots[i] = { uid: item.UniqueID, pending: true };
    this.ui.ctx.send(ClientPackets.MailLockedItem({ UniqueID: item.UniqueID, Locked: true }));
    this.cost = null;
    this.ui.cells.refresh();
    this.invalidate();
  }

  /** Takes an item out of the parcel: the server unlocks the bag cell (MailBox.unlockItem). */
  private detach(i: number, requote = true): void {
    const s = this.slots[i];
    if (!s || this.sending) return;
    this.slots[i] = null;
    this.box.unlockItem(s.uid);
    this.ui.cells.refresh();
    if (requote) this.quote();
    this.invalidate();
  }

  /** ResetLockedCells + Reset: every parcel item is handed back, gold and stamp cleared. */
  private resetParcel(): void {
    for (let i = 0; i < MAIL_PARCEL_CELLS; i++) {
      const s = this.slots[i];
      if (s) this.box.unlockItem(s.uid);
      this.slots[i] = null;
    }
    this.gold = 0;
    this.stamped = false;
    this.cost = null;
    this.ui.cells.refresh();
  }

  /** CalculatePostage: C.MailCost (S.MailCost answers into `cost`). */
  private quote(): void {
    if (this.mode !== "parcel") return;
    this.cost = null;
    this.box.queryCost(this.gold, this.slots.map((s) => s?.uid ?? 0n), this.stamped);
    this.invalidate();
  }

  /** S.MailLockedItem: MailBox.handle consumes the echo and locks the bag cell (Inventory.setLocked fires `changed`),
   *  so a pending parcel cell whose bag item is now locked has its lock; ask for the postage again. */
  private lockArrived(): void {
    let arrived = false;
    for (let i = 0; i < MAIL_PARCEL_CELLS; i++) {
      const s = this.slots[i];
      if (!s || !s.pending) continue;
      const item = this.slotItem(i);
      if (!item || !this.m.inventory.isLocked(item)) continue;
      s.pending = false;
      arrived = true;
    }
    if (!arrived) return;
    this.ui.cells.refresh();
    this.quote();
  }

  private send(): void {
    const why = this.sendProblem();
    if (why) { setText(this.errEl, why); return; }
    const to = this.toInput.value.trim(), msg = crlf(this.bodyInput.value);
    if (this.mode === "parcel") this.box.sendMail(to, msg, this.gold, this.slots.map((s) => s?.uid ?? 0n), this.stamped);
    else this.box.sendMail(to, msg);
    this.sending = true;
    this.sendTo = to;
    this.nextSend = this.ui.ctx.now() + SEND_COOLDOWN;
    setText(this.errEl, "");
    // No S.MailSent comes for a refusal the server only reports in chat (unknown name, mailbox full, blocked):
    // the composer stays and Send comes back once the server's own cooldown has run out.
    this.stopSendWait?.();
    this.stopSendWait = later(() => { this.stopSendWait = null; this.sending = false; this.invalidate(); }, SEND_COOLDOWN, this.signal);
    this.invalidate();
  }

  /** GameScene.MailSent: 1 = delivered (the server took the gold and items), -1 = refused (the model posted why). */
  private sentResult(result: number): void {
    if (!this.sending) return;
    this.sending = false;
    this.stopSendWait?.();
    this.stopSendWait = null;
    if (result === -1) {
      setText(this.errEl, "Not sent: not enough gold for the postage, or an item can't be mailed.");
      this.invalidate();
      return;
    }
    if (this.mode === "parcel") {
      // Items the server did not take (no stamp left: only the first cell goes) are handed back.
      for (let i = 0; i < MAIL_PARCEL_CELLS; i++) {
        const s = this.slots[i];
        if (s && this.slotItem(i)) this.box.unlockItem(s.uid);
        this.slots[i] = null;
      }
      this.gold = 0;
      this.stamped = false;
      this.cost = null;
      this.ui.cells.refresh();
    }
    this.mode = "read";
    this.clearDraft("", "");
    this.note = `Sent to ${this.sendTo}.`;
    if (this.folder === "Sent") this.requestSent();
    // Send stays off until the server's cooldown has run out: redraw then
    later(() => this.invalidate(), Math.max(0, this.nextSend - this.ui.ctx.now()), this.signal);
    this.invalidate();
  }
}

/** Opens the mail window on a letter to `name` (inspect, friends, relationship, territories). False when no mail window
 *  is registered. */
export function composeMailTo(host: ModernUiHost, name: string, message = ""): boolean {
  const w = host.rt.window<MailWindow>(MAIL_WINDOW);
  if (!w) return false;
  w.composeTo(name, message);
  return true;
}

registerWindow(MAIL_WINDOW, (host) => new MailWindow(host), {
  // No KeybindOptions entry for mail (the desktop opens it from the minimap): menu strip, launcher and minimap only.
  binds: [],
  watch: (host) => {
    const box = host.ctx.models.mail;
    const offs = [
      // GameScene.MailSendRequest: the parcel composer (+ the bag), as the classic window does
      box.sendRequest.on(() => host.rt.window<MailWindow>(MAIL_WINDOW)?.composeParcel()),
      // GameScene.ParcelCollected -1 / 0 (the post office page): the desktop's message box
      box.collected.on(({ result }) => {
        if (result === -1 || result === 0) {
          void host.rt.popups.confirm({ title: "Mail", text: result === -1 ? T.NoParcelsToCollect : T.AllParcelsCollected, kind: "info", buttons: "ok" });
        }
      }),
    ];
    return () => { for (const off of offs) off(); };
  },
});
