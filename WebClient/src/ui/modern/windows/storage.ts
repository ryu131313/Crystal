// Ported from play/hudStorage.ts: the new UI's NPC storage (bank) window over the real models. Desktop counterpart:
// StorageDialog (Client/MirScenes/Dialogs/NPCDialogs.cs) + GameScene.NPCStorage / StorageUnlockResult /
// StoragePasswordResult / ResizeStorage; the classic port is src/ui/storageWindow.ts (its open / prompt rules are kept).
//
// Crystal's storage is account storage: Inventory.storage (80 slots, plus a second page of 80 while
// Account.hasExpandedStorage, until expandedStorageExpiryTime). Changes from the mock:
//  - opens only when the server opens it (S.NPCStorage while the NPC page is open) and closes with the NPC session
//    (NpcSession.closed, or a new NPC page = GameScene.NPCResponse); there is no menu entry or hotkey;
//  - the lock screen sends C.UnlockStorage (Settings.RequireStoragePassword); a first password is forced (popups) before
//    the storage opens, closing the NPC window and the bag like StorageDialog.ForceStoragePasswordSetup; set / change
//    (C.SetStoragePassword) and remove (C.RemoveStoragePassword via Account.removeStoragePassword) live in the side
//    column's Protection card; closing the window re-locks it (StorageDialog.Hide);
//  - every cell is a real item cell (core/itemCells.ts, grid "npcStorage" / "inv"): drag, pick, split and right-click go
//    through core/drag.ts and the routes; a click on a bag item in the side column deposits it (a matching stack with
//    room on this page, else this page's first free cell), a double-click on a stored item withdraws it (a matching
//    bag stack, else the first free bag cell). Nothing moves until the server answers (the models lock the cells);
//  - Sort / Auto-stack send C.SortItems through Inventory.sortItems("storage", ...) (the server needs the storage page
//    and the unlock; 2 s cooldown; answered by S.UserStorage);
//  - Rent extra storage = the desktop's Rent button (C.Chat "@ADDSTORAGE" after the question; answered by
//    S.ResizeStorage);
//  - search highlights in place and category chips (ItemInfo.Type) filter in place, so slots never move;
//  - HIDDEN (Crystal keeps no data for them): "stored by" / "when" on the item card, the activity log and the
//    "Shared by <characters>" line. The item card itself is the shared hover tooltip.
// Narrow layout (a layer below 1040 px): the side column (bag + protection) becomes a "Your bag" tab.
//
// S.NPCStorage / S.StorageUnlockResult / S.StoragePasswordResult have no model event, so the UI feeds them: the
// registry entry's watch hook (windowRegistry.ts, run by modernUi.ts at UI start) subscribes watchStorage(host)'s packet
// handler to host.packets (every packet GameUi.handle sees; it never consumes). The
// session state (unlocked, the forced setup) lives in StorageSession, one per UI runtime, so the server can open the
// storage before the (lazily built) window exists.

import type { ItemInfo, ServerPacket, UserItem } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import { BindMode, ItemType } from "../../../protocol/enums.generated.ts";
import { Emitter } from "../../../game/emitter.ts";
import { BELT_IDX, STORAGE_GRID_SIZE } from "../../../game/inventory.ts";
import { dateFromBinary } from "../../../game/itemChanges.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";
import {
  UiWindow, button, chips, fmtDateTime, h, injectCss, later, plural, restartAnim, searchBox, setStyle, setText, svg,
  tabs, typingField, type Btn,
} from "../core/index.ts";
import { routeDrop, type CellRef, type Verdict } from "../core/dragRoutes.ts";
import type { ItemCell } from "../core/itemCells.ts";

/** The window's registry key (no menu entry: the server opens it). */
export const STORAGE_WINDOW = "storage";

const PAGE = STORAGE_GRID_SIZE; // one page (the expanded page is slots 80..159)
const MIN_PASSWORD = 5, MAX_PASSWORD = 15, PASSWORD_CHARS = "A-Za-z0-9"; // Globals.MinPasswordLength / MaxPasswordLength
const RENT_COST = 1_000_000; // NPCScript @ADDSTORAGE (the desktop's question text)

/** ClientTextKeys (English defaults, Client/Localization/English.json), as the classic storageWindow.ts. */
const T = {
  ExtraStorage: "Would you like to rent extra storage for 10 days at a cost of 1,000,000 gold?",
  ExtendYourRentalPeriod: "Would you like to extend your rental period for 10 days at a cost of 1,000,000 gold?",
  StoragePasswordPrompt: "Enter your storage password.",
  StoragePasswordCurrentPrompt: "Enter current storage password.",
  StoragePasswordNewPrompt: "Enter new storage password.",
  StoragePasswordConfirmPrompt: "Confirm new storage password.",
  StoragePasswordMismatch: "Passwords do not match.",
  StoragePasswordSetSuccess: "Storage password set.",
  StoragePasswordChangeSuccess: "Storage password changed.",
  StoragePasswordRemoveSuccess: "Storage password removed.",
  StoragePasswordWrong: "Incorrect storage password.",
  StoragePasswordNotAcceptable: "Password not acceptable.",
  StoragePasswordUnavailable: "Storage is not available.",
  StoragePasswordNoPasswordSet: "No storage password is set.",
  StoragePasswordRules: `Password must be ${MIN_PASSWORD}-${MAX_PASSWORD} characters, letters/numbers only.`,
  StoragePasswordChangePrompt: "Change storage password?",
};

// — The storage session (DOM-free apart from the popups it asks) —

/** What the window shows: the store, the lock screen, the forced first password, or "speak to a storekeeper". */
export type StorageView = "store" | "lock" | "setup" | "closed";

/** StorageDialog's state outside the window: whether the server opened the storage (S.NPCStorage), the unlock, the
 *  forced first-password setup, and the password prompts. One per UI runtime (storageSession). */
export class StorageSession {
  /** The view state changed (opened, unlocked, an unlock error). */
  readonly changed = new Emitter<void>();
  private active = false;            // S.NPCStorage arrived and the session has not ended
  private unlocked = false;          // StorageDialog._storageUnlocked
  private pendingOpenAfterPasswordSet = false;
  private forcingPasswordSetup = false;
  private quietNpcClose = false;     // our own NpcSession.close() (the forced setup) does not end the session
  private _error = "";

  constructor(private readonly host: ModernUiHost) {
    const m = host.ctx.models, sig = host.rt.signal;
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    // NPCDialog.Hide hides the StorageDialog; GameScene.NPCResponse (a new page from any NPC) hides it too.
    on(m.npcSession.closed.on(() => { if (!this.quietNpcClose) this.end(); }));
    on(m.npcSession.page.on(() => this.end()));
  }

  /** The last unlock refusal (the lock screen shows it; "" = none). */
  get error(): string { return this._error; }

  /** What the window shows now. */
  view(): StorageView {
    if (!this.active) return "closed";
    const a = this.host.ctx.models.account;
    if (!a.requireStoragePassword) return "store";
    if (!a.hasStoragePassword) return "setup";
    return this.unlocked ? "store" : "lock";
  }

  /** The storage packets (the Account and Inventory models have already taken their flags and items). Never consumes. */
  handle(p: ServerPacket): void {
    switch (p.type) {
      case "NPCStorage": // GameScene.NPCStorage: only while the NPC dialog is open
        if (!this.host.ctx.models.npcSession.open) return;
        this.active = true;
        this.show();
        return;
      case "StorageUnlockResult": this.unlockResult(p); return;
      case "StoragePasswordResult": this.passwordResult(p); return;
      case "ResizeStorage": this.changed.emit(); return; // RefreshStorage2 (the window re-renders from the models)
    }
  }

  /** StorageDialog.Show: open straight away, force a first password, or open on the lock screen. */
  private show(): void {
    if (!this.active) return;
    const a = this.host.ctx.models.account;
    if (a.requireStoragePassword) {
      if (!a.hasStoragePassword) {
        // ForceStoragePasswordSetup: the NPC window and the bag close, the prompts ask, the storage opens on success
        this.quietNpcClose = true;
        try { this.host.ctx.models.npcSession.close(); } finally { this.quietNpcClose = false; }
        this.host.openWindow("inventory", false);
        this.forcingPasswordSetup = this.pendingOpenAfterPasswordSet = true;
        void this.setPassword(true);
        this.changed.emit();
        return;
      }
    } else this.unlocked = true;
    this._error = "";
    this.changed.emit();
    this.host.openWindow(STORAGE_WINDOW, true); // locked: the window shows its lock screen (PromptStorageUnlock)
  }

  /** The window closed (StorageDialog.Hide): re-lock; the session ends unless the forced setup is still asking. */
  windowClosed(): void {
    this.unlocked = false;
    if (!this.forcingPasswordSetup) this.active = false;
    const pick = this.host.ctx.models.itemPick;
    if (pick.cell?.grid === "npcStorage") pick.clear();
  }

  /** The NPC session ended (or moved to another page): hide the storage. */
  private end(): void {
    this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
    if (!this.active && !this.host.rt.isOpen(STORAGE_WINDOW)) return;
    this.active = false;
    this.unlocked = false;
    this.host.openWindow(STORAGE_WINDOW, false);
    this.changed.emit();
  }

  // — Requests (sent as the classic window does; Account has only removeStoragePassword) —

  /** PromptStorageUnlock's OK: C.UnlockStorage. Returns a refusal ("" = sent). */
  unlock(password: string): string {
    const why = passwordProblem(password);
    if (why) return why;
    this._error = "";
    this.host.ctx.send(ClientPackets.UnlockStorage({ Password: password }));
    return "";
  }

  /** The Rent button: Yes / No, then C.Chat "@ADDSTORAGE" (the server charges and answers with S.ResizeStorage). */
  async rent(): Promise<void> {
    const a = this.host.ctx.models.account;
    const ok = await this.host.rt.popups.ask(a.hasExpandedStorage ? T.ExtendYourRentalPeriod : T.ExtraStorage,
      { title: a.hasExpandedStorage ? "Extend storage rental" : "Rent extra storage", kind: "question", gold: RENT_COST, okCancel: true });
    if (ok && this.active) this.host.ctx.send(ClientPackets.Chat({ Message: "@ADDSTORAGE" }));
  }

  /** BeginSetStoragePassword: new, then confirm, then C.SetStoragePassword with an empty current one. `force` = the
   *  forced first setup (cancel closes the storage; a mismatch starts over). */
  async setPassword(force = false): Promise<void> {
    for (;;) {
      const pw = await this.ask(T.StoragePasswordNewPrompt);
      if (pw === null) { if (force) this.cancelSetup(); return; }
      for (;;) {
        const again = await this.ask(T.StoragePasswordConfirmPrompt);
        if (again === null) { if (force) this.cancelSetup(); else this.pendingOpenAfterPasswordSet = false; return; }
        if (again === pw) {
          this.host.ctx.send(ClientPackets.SetStoragePassword({ CurrentPassword: "", NewPassword: pw }));
          return;
        }
        this.system(T.StoragePasswordMismatch);
        if (force) break; // the forced setup asks for a new password again
      }
    }
  }

  /** ManageStoragePassword with a password set: confirm (with the date it was set), then current, new, confirm. */
  async changePassword(): Promise<void> {
    const a = this.host.ctx.models.account;
    let text = T.StoragePasswordChangePrompt;
    if (a.storagePasswordLastSet !== 0n) text += `\nStorage password set: ${fmtDateTime(dateFromBinary(a.storagePasswordLastSet))}`;
    if (!await this.host.rt.popups.ask(text, { title: "Storage password", okCancel: true })) return;
    const current = await this.ask(T.StoragePasswordCurrentPrompt);
    if (current === null) return;
    const pw = await this.ask(T.StoragePasswordNewPrompt);
    if (pw === null) return;
    for (;;) {
      const again = await this.ask(T.StoragePasswordConfirmPrompt);
      if (again === null) return;
      if (again === pw) break;
      this.system(T.StoragePasswordMismatch);
    }
    this.host.ctx.send(ClientPackets.SetStoragePassword({ CurrentPassword: current, NewPassword: pw }));
  }

  /** C.RemoveStoragePassword (Account.removeStoragePassword) after the current password. */
  async removePassword(): Promise<void> {
    const a = this.host.ctx.models.account;
    const warn = a.requireStoragePassword ? "\nThis server requires one: you will set a new password the next time you open your storage." : "";
    if (!await this.host.rt.popups.ask(`Remove your storage password?${warn}`, { title: "Storage password", kind: "warning", okCancel: true })) return;
    const current = await this.ask(T.StoragePasswordCurrentPrompt);
    if (current === null) return;
    if (!a.removeStoragePassword(current)) this.system(T.StoragePasswordNoPasswordSet);
  }

  /** The masked input box with the length / character rules (PromptStoragePassword). */
  private ask(text: string): Promise<string | null> {
    return this.host.rt.popups.input({
      title: "Storage password", text: `${text}\n${T.StoragePasswordRules}`, password: true,
      min: MIN_PASSWORD, max: MAX_PASSWORD, chars: PASSWORD_CHARS,
    });
  }

  /** CancelStoragePasswordSetup. */
  private cancelSetup(): void {
    this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
    this.active = false;
    this.host.openWindow(STORAGE_WINDOW, false);
    this.changed.emit();
  }

  /** StorageDialog.HandleStorageUnlockResult (Account took HasPassword). */
  private unlockResult(p: Extract<ServerPacket, { type: "StorageUnlockResult" }>): void {
    switch (p.Result) {
      case 0: case 4: this.unlocked = true; this._error = ""; this.show(); return;
      case 1: this.refuse(T.StoragePasswordNotAcceptable); return;
      case 2: this.refuse(T.StoragePasswordWrong); return;
      case 3: this.refuse(T.StoragePasswordUnavailable); return;
    }
  }

  private refuse(text: string): void {
    this._error = text;
    this.system(text);
    this.changed.emit();
  }

  /** StorageDialog.HandleStoragePasswordResult (Account took HasPassword / LastSetTime and kept the value before). */
  private passwordResult(p: Extract<ServerPacket, { type: "StoragePasswordResult" }>): void {
    const hadPassword = this.host.ctx.models.account.hadStoragePassword;
    switch (p.Result) {
      case 0: this.system(T.StoragePasswordUnavailable); break;
      case 1: case 3: this.system(T.StoragePasswordNotAcceptable); break;
      case 2: this.system(T.StoragePasswordWrong); break;
      case 5: this.system(T.StoragePasswordNoPasswordSet); break;
      case 4:
        this.unlocked = true; // the server unlocks on set / change / remove
        if (p.Removing) {
          this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
          this.system(T.StoragePasswordRemoveSuccess);
          // Desktop + classic: clear the unlock and hide (a password-required server would otherwise show a dead setup page).
          this.unlocked = false;
          this.active = false;
          this.host.openWindow(STORAGE_WINDOW, false);
          this.changed.emit();
          return;
        }
        this.system(hadPassword ? T.StoragePasswordChangeSuccess : T.StoragePasswordSetSuccess);
        if (this.pendingOpenAfterPasswordSet) {
          this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
          this.show();
        } else this.changed.emit();
        return;
    }
    this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
    this.changed.emit();
  }

  private system(text: string): void { this.host.chat.system(text); }

  dispose(): void { this.active = this.unlocked = false; }
}

/** The client-side password rules (Envir.IsPasswordValid); "" = fine. */
function passwordProblem(v: string): string {
  if (v.length < MIN_PASSWORD || v.length > MAX_PASSWORD || /[^A-Za-z0-9]/.test(v)) return T.StoragePasswordRules;
  return "";
}

/** The runtime's storage session (built on first use). */
export function storageSession(host: ModernUiHost): StorageSession {
  return host.rt.service("storageSession", () => new StorageSession(host));
}

/** Builds the storage session and returns its packet handler (feed it every packet; it never consumes). The window
 *  itself is built on the first S.NPCStorage. Called by this window's watch hook (below) once per game. */
export function watchStorage(host: ModernUiHost): (p: ServerPacket) => void {
  const s = storageSession(host);
  return (p) => s.handle(p);
}

// — The window —

type Cat = "Weapons" | "Armour" | "Jewellery" | "Potions" | "Scrolls" | "Materials" | "Books" | "Other";
const CATS: readonly Cat[] = ["Weapons", "Armour", "Jewellery", "Potions", "Scrolls", "Materials", "Books", "Other"];
/** ItemType -> storage category (everything else is Other). */
const CAT_OF: Readonly<Partial<Record<number, Cat>>> = {
  [ItemType.Weapon]: "Weapons", [ItemType.Armour]: "Armour", [ItemType.Helmet]: "Armour", [ItemType.Boots]: "Armour", [ItemType.Belt]: "Armour",
  [ItemType.Necklace]: "Jewellery", [ItemType.Bracelet]: "Jewellery", [ItemType.Ring]: "Jewellery", [ItemType.Potion]: "Potions",
  [ItemType.Scroll]: "Scrolls", [ItemType.Gem]: "Materials", [ItemType.Ore]: "Materials", [ItemType.CraftingMaterial]: "Materials",
  [ItemType.Meat]: "Materials", [ItemType.Book]: "Books",
};
const catOf = (info: ItemInfo | undefined): Cat => (info && CAT_OF[info.Type]) || "Other";
const dontStore = (_item: UserItem, info: ItemInfo | undefined): string => info && (info.Bind & BindMode.DontStore) ? "Can't be stored" : "";

const CSS = `
.hudm .swin2 { width: 800px; }
.hudm .swin2 .panel { display: flex; flex-direction: column; }
.hudm .swin2 .head { display: flex; align-items: center; gap: 14px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .swin2 .head .t b { display: block; font-size: 15px; color: #fff; } .hudm .swin2 .head .t span { font-size: 11px; color: var(--dim); }
.hudm .swin2 .cap { width: 150px; } .hudm .swin2 .cap .ch { display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); margin-bottom: 3px; }
.hudm .swin2 .cap .ch b { color: var(--tx); }
.hudm .swin2 .bar { height: 6px; border-radius: 3px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .swin2 .bar i { display: block; height: 100%; width: 0; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .swin2 .pages { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .swin2 .pages button { padding: 0 12px; height: 30px; display: flex; flex-direction: column; justify-content: center; cursor: pointer; font: inherit; font-size: 11px; font-weight: 600;
  color: var(--dim); border: 0; background: color-mix(in srgb, var(--pb) 60%, #000); line-height: 1.15; text-align: left; }
.hudm .swin2 .pages button + button { border-left: 1px solid var(--fr); }
.hudm .swin2 .pages button:hover { color: #fff; }
.hudm .swin2 .pages button.on { color: #fff; background: color-mix(in srgb, var(--ac) 26%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .swin2 .pages small { font-size: 9px; font-weight: 400; color: var(--acd); } .hudm .swin2 .pages small.bad { color: var(--bad-soft); }
.hudm .swin2 .head .sp { flex: 1; }
.hudm .swin2 .ntabs { display: none; padding: 0 12px; }
.hudm .swin2 .sbody { display: flex; min-height: 0; }
.hudm .swin2 .store { flex: none; padding: 10px 12px; display: flex; flex-direction: column; gap: 8px; }
.hudm .swin2 .tools { display: flex; gap: 6px; align-items: center; }
.hudm .swin2 .tools .ui-search { width: 190px; } .hudm .swin2 .tools .btn { height: 24px; }
.hudm .swin2 .grid { display: grid; grid-template-columns: repeat(10, 40px); gap: 4px; }
.hudm .swin2 .grid .icell.dim { opacity: .18; filter: grayscale(1); }
.hudm .swin2 .grid .icell.hit { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .swin2 .p2lock { height: 348px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; text-align: center; color: var(--dim);
  border: 1px dashed color-mix(in srgb, var(--fr) 80%, transparent); border-radius: var(--rad); }
.hudm .swin2 .p2lock svg.ic { width: 34px; height: 34px; color: var(--ac); }
.hudm .swin2 .p2lock b { font-size: 14px; color: #fff; }
.hudm .swin2 .hint { font-size: 10px; color: var(--dim); }
.hudm .swin2 .note { font-size: 11px; color: var(--ac); min-height: 14px; }
.hudm .swin2 .side { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 10px; padding: 10px 12px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .swin2 .slbl { display: flex; justify-content: space-between; margin-bottom: 6px; }
.hudm .swin2 .slbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
.hudm .swin2 .bag { display: grid; grid-template-columns: repeat(6, 40px); gap: 4px; max-height: 268px; overflow-y: auto; scrollbar-width: thin; padding-right: 2px; }
.hudm .swin2 .card { padding: 10px 12px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .swin2 .card .ln { font-size: 11px; color: var(--dim); margin-bottom: 8px; }
.hudm .swin2 .card .row { display: flex; gap: 6px; flex-wrap: wrap; }
/* Lock screen */
.hudm .swin2 .lock { height: 420px; display: flex; align-items: center; justify-content: center; }
.hudm .swin2 .lock .card { width: 340px; padding: 24px; text-align: center; }
.hudm .swin2 .lock svg.ic { width: 46px; height: 46px; color: var(--ac); stroke-width: 1.6; }
.hudm .swin2 .lock h3 { margin: 8px 0 4px; font-size: 17px; color: #fff; }
.hudm .swin2 .lock p { margin: 0 0 14px; font-size: 12px; color: var(--dim); white-space: pre-line; }
.hudm .swin2 .lock input.fld { width: 100%; text-align: center; letter-spacing: .3em; height: 32px; }
.hudm .swin2 .lock .btn { width: 100%; height: 32px; margin-top: 8px; }
.hudm .swin2 .lock .err { margin-top: 6px; min-height: 14px; font-size: 11px; color: var(--bad-soft); }
.hudm .swin2 .lock .card.shake { animation: hudm-sshake .3s linear; }
@keyframes hudm-sshake { 20%, 60% { transform: translateX(-6px); } 40%, 80% { transform: translateX(6px); } }
/* Narrow layer: the side column becomes the "Your bag" tab. */
.hudm.narrow .swin2 { width: 470px; }
.hudm.narrow .swin2 .head { flex-wrap: wrap; gap: 8px 12px; }
.hudm.narrow .swin2 .head .t span { display: none; }
.hudm.narrow .swin2 .ntabs { display: block; }
.hudm.narrow .swin2 .side { border-left: 0; }
.hudm.narrow .swin2 .bag { grid-template-columns: repeat(10, 40px); }
.hudm.narrow .swin2:not(.bagview) .side, .hudm.narrow .swin2.bagview .store { display: none; }
.hudm.narrow .swin2 .lock .card { width: 300px; }
`;

class StorageWindow extends UiWindow {
  private readonly m: ModernUiHost["ctx"]["models"];
  private page = 0;
  private query = "";
  private cat: Cat | "All" = "All";
  private note = "";
  private shownPage = -1;
  private chipsKey = "";
  private filtered = false;
  /** The bag cell pressed in the side column (a click on it deposits); cleared by any other press. */
  private sidePress = "";

  // — DOM, built once —
  private readonly lockView = h("div", "lock");
  private readonly lockCard = h("div", "card");
  private readonly lockTitle = h("h3");
  private readonly lockText = h("p");
  private readonly lockForm = h("form");
  private readonly lockInput = typingField(h("input", "fld"));
  private readonly lockBtn: Btn;
  private readonly lockErr = h("div", "err");
  private readonly mainView = h("div", "main");
  private readonly usedText = h("b");
  private readonly barFill = h("i");
  private readonly pageBtns: { el: HTMLButtonElement; count: HTMLElement; sub: HTMLElement }[] = [];
  private readonly rentBtn: Btn;
  private readonly lockStoreBtn: Btn;
  private readonly sortBtn: Btn;
  private readonly stackBtn: Btn;
  private readonly chipsBox = h("div");
  private readonly grid = h("div", "grid");
  private readonly p2lock = h("div", "p2lock");
  private readonly noteEl = h("div", "note");
  private readonly bagGrid = h("div", "bag");
  private readonly bagCount = h("b");
  private readonly protCard = h("div", "card");
  private readonly protText = h("div", "ln");
  private readonly protRow = h("div", "row");
  private readonly storeCells: ItemCell[] = [];
  private bagCells: ItemCell[] = [];

  constructor(private readonly ui: ModernUiHost, private readonly session: StorageSession) {
    super(ui.rt, "swin2", 32, 30);
    injectCss("win-storage", CSS);
    this.m = ui.ctx.models;
    const sig = ui.rt.signal;

    // Lock screen (PromptStorageUnlock / the "speak to" and first-password states)
    this.lockInput.type = "password";
    this.lockInput.maxLength = MAX_PASSWORD;
    this.lockInput.placeholder = "••••••";
    this.lockBtn = button("Unlock", () => this.submitUnlock(), { cls: "main big" });
    this.lockForm.append(this.lockInput, this.lockBtn.el);
    // Enter in the field submits the form (one text field: implicit submission); the button's own click submits too.
    this.lockForm.addEventListener("submit", (e) => { e.preventDefault(); this.submitUnlock(); }, { signal: sig });
    this.lockCard.append(svg("lock"), this.lockTitle, this.lockText, this.lockForm, this.lockErr);
    this.lockView.append(this.lockCard);

    // Head: title, capacity, page tabs, rent, lock
    for (let i = 0; i < 2; i++) {
      const count = h("span"), sub = h("small");
      const el = h("button", "", h("span", "", `Page ${i + 1} · `, count), sub);
      el.type = "button";
      el.addEventListener("click", () => { if (this.page !== i) { this.page = i; this.invalidate(); } }, { signal: sig });
      this.pageBtns.push({ el, count, sub });
    }
    this.rentBtn = button("Rent page 2", () => void this.session.rent(), { icon: "coin", title: "Rent the expanded storage page for 10 days (1,000,000 gold)" });
    this.lockStoreBtn = button("Lock", () => this.toggle(false), { icon: "lock", title: "Lock and close the storage (the password is asked again next time)" });
    const head = h("div", "head",
      h("div", "t", h("b", "", "Account storage"), h("span", "", "Shared by every character on your account")),
      h("div", "cap", h("div", "ch", h("span", "", "Used"), this.usedText), h("div", "bar", this.barFill)),
      h("div", "pages", ...this.pageBtns.map((p) => p.el)), h("div", "sp"), this.rentBtn.el, this.lockStoreBtn.el);
    const narrowTabs = tabs(["Storage", "Your bag"], 0, (i) => this.el.classList.toggle("bagview", i === 1));

    // Store column: search, sort / stack, chips, the 80 cells of the page (or the rent panel)
    const search = searchBox("Find an item…", (q) => { this.query = q.toLowerCase(); this.paintFilter(); }, { signal: sig });
    this.sortBtn = button("Sort", () => this.sort("sort"), { icon: "sort", title: "Sort this storage by type (the server sorts it)" });
    this.stackBtn = button("Auto-stack", () => this.sort("stack"), { icon: "repeat", title: "Merge partial stacks of the same item, then close the gaps" });
    for (let i = 0; i < PAGE; i++) {
      const c = ui.cells.cell("npcStorage", i, { size: 40, filter: dontStore });
      this.storeCells.push(c);
      this.grid.append(c.el);
    }
    // Double-click a stored item: withdraw it (the first click picks it, the second puts it back, then this runs).
    this.grid.addEventListener("dblclick", (e) => { const c = ui.cells.cellOf(e.target); if (c) this.withdraw(c.ref.index); }, { signal: sig });
    this.p2lock.append(svg("lock"), h("b", "", "Expanded storage locked"),
      h("div", "", "Rent a second page of 80 slots for 10 days."), button("Rent extra storage", () => void this.session.rent(), { cls: "main", icon: "coin" }).el);
    const store = h("div", "store", h("div", "tools", search.el, this.sortBtn.el, this.stackBtn.el), this.chipsBox, this.grid, this.p2lock,
      h("div", "hint", "Double-click to take an item out · click a bag item to store it · drag either way · search and filters highlight in place."));

    // Side column: the bag (click to deposit), the note, password protection
    this.protCard.append(h("div", "lbl slbl", "Protection"), this.protText, this.protRow);
    const side = h("div", "side", h("div", "", h("div", "lbl slbl", "Your bag", this.bagCount), this.bagGrid), this.noteEl, this.protCard);
    // A press anywhere clears the side-column mark; a press on a side bag cell sets it (capture: root first, then here).
    ui.rt.root.addEventListener("pointerdown", () => { this.sidePress = ""; }, { capture: true, signal: sig });
    this.bagGrid.addEventListener("pointerdown", (e) => {
      const c = e.button === 0 ? ui.cells.cellOf(e.target) : undefined;
      this.sidePress = c ? `${c.ref.grid}:${c.ref.index}` : "";
    }, { capture: true, signal: sig });
    const offClick = ui.drag.addClickHook((ref) => {
      if (!this.open || this.session.view() !== "store" || this.sidePress !== `${ref.grid}:${ref.index}`) return false;
      this.sidePress = "";
      this.deposit(ref.index);
      return true;
    });
    sig.addEventListener("abort", offClick, { once: true });

    this.mainView.append(head, h("div", "ntabs", narrowTabs.el), h("div", "sbody", store, side));
    this.el.append(h("div", "panel", this.titleBar("STORAGE", "Close"), this.lockView, this.mainView));

    // Model events: re-render when shown, else on the next open (dirty-when-closed)
    const on = (off: () => void) => sig.addEventListener("abort", off, { once: true });
    on(this.m.inventory.subscribe(() => this.invalidate()));
    on(this.m.account.changed.on(() => this.invalidate()));
    on(this.session.changed.on(() => this.invalidate()));
    on(ui.events.on((e) => { if (e.kind === "itemInfo") { this.chipsKey = ""; this.invalidate(); } }));
  }

  protected override shown(): void {
    this.note = "";
    if (this.session.view() === "lock") later(() => { if (this.open) this.lockInput.focus({ preventScroll: true }); }, 0, this.signal);
  }

  protected override closed(): void {
    this.lockInput.value = ""; // nothing typed is kept
    this.session.windowClosed();
  }

  // — Render (once a frame at most, only while open) —

  protected override render(): void {
    const view = this.session.view();
    this.lockView.hidden = view === "store";
    this.mainView.hidden = view !== "store";
    if (view !== "store") { this.renderLock(view); return; }
    const a = this.m.account, inv = this.m.inventory;
    const expanded = a.hasExpandedStorage;
    const used = [0, 0];
    for (let i = 0; i < inv.storage.length && i < PAGE * 2; i++) if (inv.storage[i]) used[i < PAGE ? 0 : 1]!++;
    const total = expanded ? PAGE * 2 : PAGE;
    setText(this.usedText, `${used[0]! + used[1]!} / ${total}`);
    setStyle(this.barFill, "width", `${Math.min(100, (used[0]! + used[1]!) / total * 100).toFixed(1)}%`);
    this.pageBtns.forEach((p, i) => {
      p.el.classList.toggle("on", i === this.page);
      setText(p.count, i === 1 && !expanded ? "locked" : `${used[i]}/${PAGE}`);
    });
    const p2 = this.pageBtns[1]!;
    if (expanded) {
      const exp = dateFromBinary(a.expandedStorageExpiryTime);
      const days = Math.max(0, Math.ceil((exp.getTime() - Date.now()) / 86_400_000));
      setText(p2.sub, `expires ${fmtDateTime(exp)} · ${days} ${plural(days, "day")} left`);
      p2.sub.classList.remove("bad");
    } else {
      setText(p2.sub, "not rented");
      p2.sub.classList.add("bad");
    }
    this.rentBtn.setLabel(expanded ? "Extend rental" : "Rent page 2");
    this.lockStoreBtn.el.hidden = !(a.requireStoragePassword && a.hasStoragePassword);

    // The page: cells re-pointed only when the page changed; page 2 without the rental shows the rent panel
    const locked = this.page === 1 && !expanded;
    this.grid.hidden = locked;
    this.p2lock.hidden = !locked;
    if (this.shownPage !== this.page) {
      this.shownPage = this.page;
      const base = this.page * PAGE;
      this.storeCells.forEach((c, i) => c.setRef({ grid: "npcStorage", index: base + i }));
    }
    this.sortBtn.setDisabled(locked, "Rent the page first");
    this.stackBtn.setDisabled(locked, "Rent the page first");
    this.renderChips();
    this.paintFilter();
    this.renderBag();
    this.renderProtection();
    setText(this.noteEl, this.note);
  }

  private renderLock(view: Exclude<StorageView, "store">): void {
    const a = this.m.account;
    if (view === "closed") {
      setText(this.lockTitle, "Storage is closed");
      setText(this.lockText, "Speak to a storekeeper to open your storage.");
    } else if (view === "setup") {
      setText(this.lockTitle, "Set a storage password");
      setText(this.lockText, "This server protects storage with a password. Set one to open your storage.");
    } else {
      setText(this.lockTitle, "Storage is locked");
      setText(this.lockText, `${T.StoragePasswordPrompt} It protects the storage every character on your account shares.`
        + (a.storagePasswordLastSet !== 0n ? `\nSet ${fmtDateTime(dateFromBinary(a.storagePasswordLastSet))}` : ""));
    }
    this.lockForm.hidden = view !== "lock";
    const err = view === "lock" ? this.session.error : "";
    if (err && err !== this.lockErr.textContent) restartAnim(this.lockCard, "shake");
    setText(this.lockErr, err);
  }

  private submitUnlock(): void {
    const why = this.session.unlock(this.lockInput.value);
    this.lockInput.value = "";
    setText(this.lockErr, why);
    if (why) restartAnim(this.lockCard, "shake");
    this.lockInput.focus({ preventScroll: true });
  }

  /** Category chips with this page's counts (rebuilt only when a count changed). */
  private renderChips(): void {
    const counts = new Map<Cat | "All", number>([["All", 0]]);
    const inv = this.m.inventory, base = this.page * PAGE;
    for (let i = base; i < base + PAGE; i++) {
      const item = inv.storage[i];
      if (!item) continue;
      const c = catOf(this.m.itemInfos.get(item.ItemIndex));
      counts.set(c, (counts.get(c) ?? 0) + 1);
      counts.set("All", counts.get("All")! + 1);
    }
    const key = [this.cat, ...["All", ...CATS].map((c) => counts.get(c as Cat) ?? 0)].join(",");
    if (key === this.chipsKey) return;
    this.chipsKey = key;
    const choices = (["All", ...CATS] as (Cat | "All")[]).map((c) => ({ value: c, label: `${c} ${counts.get(c) ?? 0}` }));
    const ch = chips(choices, [this.cat], (sel) => {
      const next = sel.values().next().value ?? "All";
      if (next === this.cat) return;
      this.cat = next;
      this.chipsKey = "";
      this.invalidate();
    }, { multi: false, allowNone: false });
    this.chipsBox.replaceChildren(ch.el);
  }

  /** Search / category: matching cells light, the rest dim (slots never move). Writes only while filtering or to clear. */
  private paintFilter(): void {
    const filtering = this.query !== "" || this.cat !== "All";
    if (!filtering && !this.filtered) return;
    this.filtered = filtering;
    for (const c of this.storeCells) {
      const item = c.item;
      let hit = false, dim = false;
      if (item && filtering) {
        const info = this.m.itemInfos.get(item.ItemIndex), cat = catOf(info);
        const ok = (this.cat === "All" || cat === this.cat)
          && (!this.query || (info?.Name.toLowerCase().includes(this.query) ?? false) || cat.toLowerCase().includes(this.query));
        hit = ok; dim = !ok;
      }
      c.el.classList.toggle("hit", hit);
      c.el.classList.toggle("dim", dim);
    }
  }

  /** The bag beside the storage (Inventory.inventory[BELT_IDX..]); cells rebuilt only when the bag size changed. */
  private renderBag(): void {
    const inv = this.m.inventory.inventory;
    const n = Math.max(0, inv.length - BELT_IDX);
    if (this.bagCells.length !== n) {
      for (const c of this.bagCells) c.dispose();
      this.bagCells = [];
      for (let i = 0; i < n; i++) this.bagCells.push(this.ui.cells.cell("inv", BELT_IDX + i, { size: 40 }));
      this.bagGrid.replaceChildren(...this.bagCells.map((c) => c.el));
    }
    let used = 0;
    for (let i = BELT_IDX; i < inv.length; i++) if (inv[i]) used++;
    setText(this.bagCount, `${used} / ${n}`);
  }

  /** Set / change / remove the storage password (only where the server asks for one, or one is set). */
  private renderProtection(): void {
    const a = this.m.account;
    this.protCard.hidden = !a.requireStoragePassword && !a.hasStoragePassword;
    if (this.protCard.hidden) return;
    const text = a.hasStoragePassword
      ? `Password set${a.storagePasswordLastSet !== 0n ? ` ${fmtDateTime(dateFromBinary(a.storagePasswordLastSet))}` : ""}.`
      : "No storage password is set.";
    setText(this.protText, text);
    const key = `${a.hasStoragePassword}|${a.requireStoragePassword}`;
    if (this.protRow.dataset.key === key) return;
    this.protRow.dataset.key = key;
    const btns: HTMLElement[] = [];
    if (!a.hasStoragePassword) { if (a.requireStoragePassword) btns.push(button("Set password", () => void this.session.setPassword(), { icon: "key" }).el); }
    else {
      if (a.requireStoragePassword) btns.push(button("Change", () => void this.session.changePassword(), { icon: "key" }).el);
      btns.push(button("Remove", () => void this.session.removePassword(), { icon: "unlock" }).el);
    }
    this.protRow.replaceChildren(...btns);
  }

  // — Actions (all through the routes / models: nothing moves until the server answers) —

  private say(text: string): void {
    this.note = text;
    setText(this.noteEl, text);
  }

  /** A click on a side bag cell: a matching stack with room on this page, else this page's first free cell (the routes
   *  redirect an occupied target to the first free cell anywhere). */
  private deposit(bagIndex: number): void {
    const inv = this.m.inventory, item = inv.inventory[bagIndex];
    if (!item) return;
    const info = this.m.itemInfos.get(item.ItemIndex);
    if (dontStore(item, info)) { this.ui.drag.toast(`${info?.Name ?? "This item"} can't be stored.`); return; }
    if (this.page === 1 && !this.m.account.hasExpandedStorage) { this.say("Rent the second page first, or switch to page 1."); return; }
    const from: CellRef = { grid: "inv", index: bagIndex };
    const base = this.page * PAGE, end = Math.min(base + PAGE, inv.storage.length);
    let to = -1;
    for (let i = base; i < end && to < 0; i++) {
      const s = inv.storage[i];
      if (s && s.ItemIndex === item.ItemIndex && info && info.StackSize > 1 && s.Count < info.StackSize) to = i;
    }
    if (to < 0) for (let i = base; i < end && to < 0; i++) if (!inv.storage[i]) to = i;
    if (to < 0) to = base; // full page: the route sends it to the first free cell elsewhere, or refuses
    this.run(routeDrop(this.ui.cells.deps, from, { grid: "npcStorage", index: to }), [from, { grid: "npcStorage", index: to }], `Stored ${info?.Name ?? "the item"}.`);
  }

  /** A double-click on a stored item: a matching bag stack with room, else the first free bag cell. */
  private withdraw(index: number): void {
    const inv = this.m.inventory, item = inv.storage[index];
    if (!item) return;
    const info = this.m.itemInfos.get(item.ItemIndex);
    const from: CellRef = { grid: "npcStorage", index };
    let to = -1;
    for (let i = BELT_IDX; i < inv.inventory.length && to < 0; i++) {
      const b = inv.inventory[i];
      if (b && b.ItemIndex === item.ItemIndex && info && info.StackSize > 1 && b.Count < info.StackSize) to = i;
    }
    if (to < 0) for (let i = BELT_IDX; i < inv.inventory.length && to < 0; i++) if (!inv.inventory[i]) to = i;
    if (to < 0) { this.say("Your bag is full."); return; }
    this.run(routeDrop(this.ui.cells.deps, from, { grid: "inv", index: to }), [from, { grid: "inv", index: to }], `Took ${info?.Name ?? "the item"}.`);
  }

  private run(v: Verdict, cells: CellRef[], done: string): void {
    if (!v.ok || !v.run) { if (v.label) this.ui.drag.toast(v.label); return; }
    this.ui.drag.request(v.run, cells);
    this.say(done);
  }

  /** C.SortItems for the storage (Inventory.sortItems: refused while a storage cell waits or within the cooldown). */
  private sort(mode: "sort" | "stack"): void {
    if (this.m.inventory.sortItems("storage", mode, this.ui.ctx.now())) this.say(mode === "sort" ? "Sorting…" : "Stacking…");
    else this.say("Wait a moment: an item is still moving, or you sorted just now.");
  }
}

registerWindow(STORAGE_WINDOW, (host) => new StorageWindow(host, storageSession(host)), {
  binds: [],
  watch: (host) => host.packets.on(watchStorage(host)),
});
