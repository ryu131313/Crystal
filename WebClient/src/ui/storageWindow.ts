// NPC storage (bank): StorageDialog (Client/MirScenes/Dialogs/NPCDialogs.cs) with the desktop artwork + GameScene.NPCStorage /
// StorageUnlockResult / StoragePasswordResult / ResizeStorage.
//   Prguse 586 at (0, 0), Title 0, the Storage 1 / Storage 2 tab buttons (Title 743-746), Rent (Title 483, C.Chat
//   "@ADDSTORAGE" after an OK/Cancel box), Protect (Title 113: set / change the storage password when the server requires
//   one), the 10 x 8 MirItemCell grid per page (page 2 = slots 80..159, the rented expansion: Prguse 2443 + the red
//   "locked" label until User.HasExpandedStorage) and the rental label.
//   Items live in Inventory.storage and the cells share the inventory window's pick (grid "npcStorage"): store / take
//   back / merge / move / split / equip from storage / unequip into storage follow MirItemCell (see inventory.ts).
// The password prompts are MirInputBox (Prguse 660) with InputTextBox.Password = true and OK disabled while empty.
// Deliberate differences: a forced first-time setup whose confirmation does not match closes that box before asking
// again (the desktop leaves it open behind the new one); the InventoryDialog is not moved beside the storage
// (InventoryWindow has no location API); the expiry date reads "Expires On <date>" with a space.
// The account flags (HasExpandedStorage, the password flags, the expiry) live in the Account model (src/game/account.ts);
// the window reads them and keeps the prompts and the open / close with the NPC.

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { Account } from "../game/account.ts";
import type { Emitter } from "../game/emitter.ts";
import { STORAGE_GRID_SIZE } from "../game/inventory.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirTextBox, Lib, closeButton } from "./mir/controls.ts";

const MIN_PASSWORD = 5, MAX_PASSWORD = 15; // Globals.MinPasswordLength / MaxPasswordLength

/** ClientTextKeys (English defaults from Shared/Language.cs / Client/Localization/English.json). */
const T = {
  ExpandedStorageExpiresOn: "Expanded Storage Expires On",
  ExpandedStorageLocked: "Expanded Storage Locked",
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
  StoragePasswordLastSetLabel: (when: string) => `Storage password set: ${when}`,
  StoragePasswordRules: `Password must be ${MIN_PASSWORD}-${MAX_PASSWORD} characters, letters/numbers only.`,
  StoragePasswordChangePrompt: "Change storage password?",
};

/** DateTime.FromBinary (low 62 bits = ticks since 0001-01-01; kind bits ignored). 0 = DateTime.MinValue. */
function dateFromBinary(v: bigint): Date {
  const ticks = BigInt.asUintN(62, v);
  return new Date(Number((ticks - 621355968000000000n) / 10000n));
}

/** What the window needs from the NPC session (src/game/npcSession.ts: NPCDialog.Visible / Hide; NPCDialog.Hide also
 *  hides the StorageDialog, through `closed`). */
export interface StorageNpc { readonly open: boolean; close(): void; readonly closed: Emitter<void> }

/** MirInputBox (Prguse 660) with InputTextBox.Password = true and OKButton.Enabled following the text. `onSubmit`
 *  returning false keeps the box open (refocused); Cancel closes it, then calls onCancel. Returns a close function. */
function passwordBox(text: string, onSubmit: (value: string) => boolean, onCancel?: () => void): () => void {
  const win = new MirWindow({ library: Lib.Prguse, index: 660, location: "center", modal: true });
  new MirLabel({ parent: win, location: [25, 25], size: [235, 40], text });
  const close = () => win.dispose();
  const ok = () => {
    const value = input.text;
    if (!value) return;
    if (onSubmit(value)) { close(); return; }
    input.focus();
  };
  const cancel = () => { close(); onCancel?.(); };
  const input = new MirTextBox({ parent: win, location: [23, 86], size: [240, 19], border: true, borderColour: "lime", password: true, maxLength: 64,
    onEnter: ok, onEscape: cancel, onChange: (v) => { okButton.enabled = v !== ""; } });
  const okButton = new MirButton({ parent: win, library: Lib.Title, index: 200, hoverIndex: 201, pressedIndex: 202, location: [60, 123], enabled: false, onClick: ok });
  new MirButton({ parent: win, library: Lib.Title, index: 203, hoverIndex: 204, pressedIndex: 205, location: [160, 123], onClick: cancel });
  win.show();
  input.focus();
  return close;
}

export class StorageWindow {
  /** UserObject.HasExpandedStorage / ExpandedStorageExpiryTime / HasStoragePassword / RequireStoragePassword /
   *  StoragePasswordLastSet (UserInformation, then the storage packets): the Account model. */
  private readonly account: Account;
  private storageUnlocked = false;        // StorageDialog._storageUnlocked
  private pendingOpenAfterPasswordSet = false;
  private forcingPasswordSetup = false;
  private readonly stage: HTMLElement;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly npc: StorageNpc;
  private readonly win: MirWindow;
  private readonly lockedPage: MirImage;
  private readonly storage1Button: MirButton;
  private readonly storage2Button: MirButton;
  private readonly rentButton: MirButton;
  private readonly protectButton: MirButton;
  private readonly rentalLabel: MirLabel;
  /** StorageDialog.Grid: 10 x 16 cells (two pages of 80). */
  private readonly grid: HTMLElement[] = [];
  /** Open password boxes (closed with the scene). */
  private readonly boxes = new Set<() => void>();

  constructor(stage: HTMLElement, invWindow: InventoryWindow, send: (f: Uint8Array) => void,
    chat: (text: string, type: number) => void, npc: StorageNpc, account: Account) {
    this.account = account;
    this.stage = stage;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.npc = npc;
    // StorageDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 586, location: [0, 0], sort: true });
    new MirImage({ parent: this.win, library: Lib.Title, index: 0, location: [18, 8] });
    this.lockedPage = new MirImage({ parent: this.win, library: Lib.Prguse, index: 2443, location: [8, 59], visible: false });
    this.storage1Button = new MirButton({ parent: this.win, library: Lib.Title, index: 743, hoverIndex: 743, pressedIndex: 744, location: [8, 36], sound: SoundList.ButtonA, onClick: () => this.refreshStorage(1) });
    this.storage2Button = new MirButton({ parent: this.win, library: Lib.Title, index: 746, hoverIndex: 746, pressedIndex: 746, location: [80, 36], sound: SoundList.ButtonA, onClick: () => this.refreshStorage(2) });
    this.rentButton = new MirButton({ parent: this.win, library: Lib.Title, index: 483, hoverIndex: 484, pressedIndex: 485, location: [283, 33], sound: SoundList.ButtonA, onClick: () => this.rent() });
    this.protectButton = new MirButton({ parent: this.win, library: Lib.Title, index: 113, hoverIndex: 114, pressedIndex: 115, location: [328, 33], sound: SoundList.ButtonA, onClick: () => this.manageStoragePassword() });
    closeButton(this.win, [363, 3], () => this.hide());
    this.rentalLabel = new MirLabel({ parent: this.win, location: [40, 322], autoSize: true, notControl: true, foreColour: "#ff0000", text: T.ExpandedStorageLocked });
    for (let x = 0; x < 10; x++) {
      for (let y = 0; y < 16; y++) {
        const idx = 10 * y + x;
        const cell = placeCell(invWindow.boundCell("npcStorage", idx), this.win, [x * 36 + 9 + x, (y % 8) * 32 + 60 + (y % 8)]);
        cell.hidden = idx >= STORAGE_GRID_SIZE;
        this.grid[idx] = cell;
      }
    }
    // NPCDialog.Hide hides the StorageDialog
    const offClosed = npc.closed.on(() => this.hide());
    // KeybindOptions.Closeall (Escape) hides StorageDialog with the rest
    window.addEventListener("keydown", (e) => {
      if (matches(e, KeybindOptions.Closeall) && this.win.visible && !(document.activeElement instanceof HTMLInputElement)) this.hide();
    }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => { offClosed(); this.closeBoxes(); this.win.dispose(); });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  // the Account flags the window reads
  private get hasExpandedStorage(): boolean { return this.account.hasExpandedStorage; }
  private get expandedStorageExpiryTime(): bigint { return this.account.expandedStorageExpiryTime; }
  private get hasStoragePassword(): boolean { return this.account.hasStoragePassword; }
  private get requireStoragePassword(): boolean { return this.account.requireStoragePassword; }
  private get storagePasswordLastSet(): bigint { return this.account.storagePasswordLastSet; }

  /** The storage packets (the Account model has already taken their flags). */
  handle(p: ServerPacket): void {
    switch (p.type) {
      // GameScene.NPCStorage: only while the NPC dialog is open
      case "NPCStorage": if (this.npc.open) this.show(); break;
      // GameScene.NPCResponse hides StorageDialog (a new page from any NPC)
      case "NPCResponse": if (this.visible) this.hide(); break;
      case "StorageUnlockResult": this.handleStorageUnlockResult(p); break;
      case "StoragePasswordResult": this.handleStoragePasswordResult(p); break;
      case "ResizeStorage":
        // GameScene.ResizeStorage (Inventory resizes the array, Account takes the flags): StorageDialog.RefreshStorage2
        this.refreshStorage(2);
        break;
    }
  }

  /** StorageDialog.Show: open straight away, force a first password, or ask for the password first. */
  show(): void {
    this.protectButton.enabled = this.requireStoragePassword;
    if (this.requireStoragePassword) {
      if (!this.hasStoragePassword) {
        this.npc.close();
        this.invWindow.toggle(false);
        this.forceStoragePasswordSetup();
        return;
      }
      if (!this.storageUnlocked) {
        this.npc.close();
        this.invWindow.toggle(false);
        this.promptStorageUnlock();
        return;
      }
    } else this.storageUnlocked = true;
    this.invWindow.toggle(true);
    this.refreshStorage(1);
    this.win.show();
  }

  /** StorageDialog.Hide (re-lock: the next open asks for the password again). */
  hide(): void {
    this.storageUnlocked = false;
    if (!this.win.visible) return;
    this.win.hide();
    this.invWindow.clearPick();
  }

  /** StorageDialog.HandleStorageUnlockResult. */
  private handleStorageUnlockResult(p: Extract<ServerPacket, { type: "StorageUnlockResult" }>): void {
    // HasStoragePassword = p.HasPassword: Account
    switch (p.Result) {
      case 0: case 4: this.storageUnlocked = true; this.show(); return;
      case 1: this.system(T.StoragePasswordNotAcceptable); return;
      case 2: this.system(T.StoragePasswordWrong); return;
      case 3: this.system(T.StoragePasswordUnavailable); return;
    }
  }

  /** StorageDialog.HandleStoragePasswordResult. */
  private handleStoragePasswordResult(p: Extract<ServerPacket, { type: "StoragePasswordResult" }>): void {
    // HasStoragePassword / StoragePasswordLastSet = the packet's: Account (which keeps the value before it)
    const hadPassword = this.account.hadStoragePassword;
    switch (p.Result) {
      case 0: this.system(T.StoragePasswordUnavailable); return;
      case 1: case 3: this.system(T.StoragePasswordNotAcceptable); return;
      case 2: this.system(T.StoragePasswordWrong); return;
      case 5: this.system(T.StoragePasswordNoPasswordSet); return;
      case 4:
        this.storageUnlocked = true;
        if (p.Removing) {
          this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
          this.system(T.StoragePasswordRemoveSuccess);
          this.hide();
          return;
        }
        this.system(hadPassword ? T.StoragePasswordChangeSuccess : T.StoragePasswordSetSuccess);
        if (this.pendingOpenAfterPasswordSet) {
          this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
          this.show();
        }
        return;
    }
    this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
  }

  private system(text: string): void {
    this.chat(text, ChatType.System);
  }

  /** StorageDialog.RentButton: Yes/No, then C.Chat "@ADDSTORAGE" (the server charges and answers with S.ResizeStorage). */
  private rent(): void {
    messageBox(this.stage, this.hasExpandedStorage ? T.ExtendYourRentalPeriod : T.ExtraStorage, [
      { label: "OK", primary: true, onClick: () => this.send(ClientPackets.Chat({ Message: "@ADDSTORAGE" })) },
      { label: "Cancel" },
    ]);
  }

  /** StorageDialog.ManageStoragePassword (Protect button): set one, or confirm then change it. */
  private manageStoragePassword(): void {
    if (!this.requireStoragePassword) return;
    if (!this.hasStoragePassword) { this.beginSetStoragePassword(); return; }
    let text = T.StoragePasswordChangePrompt;
    if (this.storagePasswordLastSet !== 0n) text += `\n${T.StoragePasswordLastSetLabel(dateFromBinary(this.storagePasswordLastSet).toLocaleString())}`;
    messageBox(this.stage, text, [{ label: "OK", primary: true, onClick: () => this.beginChangeStoragePassword() }, { label: "Cancel" }]);
  }

  /** StorageDialog.ForceStoragePasswordSetup: a first password before the storage opens (cancel = close). */
  private forceStoragePasswordSetup(): void {
    this.forcingPasswordSetup = this.pendingOpenAfterPasswordSet = true;
    this.beginSetStoragePassword(() => this.cancelStoragePasswordSetup(), true);
  }

  /** StorageDialog.CancelStoragePasswordSetup. */
  private cancelStoragePasswordSetup(): void {
    this.pendingOpenAfterPasswordSet = this.forcingPasswordSetup = false;
    this.hide();
  }

  /** StorageDialog.BeginSetStoragePassword: new, then confirm, then C.SetStoragePassword with an empty current one. */
  private beginSetStoragePassword(onCancel?: () => void, force = false): void {
    this.promptStoragePassword(T.StoragePasswordNewPrompt, (newPassword) => {
      this.promptStoragePassword(T.StoragePasswordConfirmPrompt, (confirmPassword) => {
        if (newPassword !== confirmPassword) {
          this.system(T.StoragePasswordMismatch);
          if (force) { this.beginSetStoragePassword(onCancel, true); return true; }
          this.pendingOpenAfterPasswordSet = false;
          return false;
        }
        this.send(ClientPackets.SetStoragePassword({ CurrentPassword: "", NewPassword: newPassword }));
        return true;
      }, onCancel);
      return true;
    }, onCancel);
  }

  /** StorageDialog.BeginChangeStoragePassword: current, new, confirm. */
  private beginChangeStoragePassword(): void {
    this.promptStoragePassword(T.StoragePasswordCurrentPrompt, (currentPassword) => {
      this.promptStoragePassword(T.StoragePasswordNewPrompt, (newPassword) => {
        this.promptStoragePassword(T.StoragePasswordConfirmPrompt, (confirmPassword) => {
          if (newPassword !== confirmPassword) { this.system(T.StoragePasswordMismatch); return false; }
          this.send(ClientPackets.SetStoragePassword({ CurrentPassword: currentPassword, NewPassword: newPassword }));
          return true;
        });
        return true;
      });
      return true;
    });
  }

  /** StorageDialog.PromptStorageUnlock: C.UnlockStorage (cancel = close). */
  private promptStorageUnlock(): void {
    this.promptStoragePassword(T.StoragePasswordPrompt, (password) => {
      this.send(ClientPackets.UnlockStorage({ Password: password }));
      return true;
    }, () => this.hide());
  }

  /** StorageDialog.PromptStoragePassword: the masked MirInputBox with the length rules. */
  private promptStoragePassword(text: string, onSubmit: (value: string) => boolean, onCancel?: () => void): void {
    let close: () => void = () => {};
    const forget = () => { this.boxes.delete(close); };
    close = passwordBox(`${text}\n${T.StoragePasswordRules}`, (v) => { const done = onSubmit(v); if (done) forget(); return done; }, () => { forget(); onCancel?.(); });
    this.boxes.add(close);
  }

  private closeBoxes(): void {
    for (const close of [...this.boxes]) close();
  }

  /** StorageDialog.RefreshStorage1 / RefreshStorage2: the tab pictures, which cells show, Rent, the locked page and
   *  the rental label. */
  private refreshStorage(page: 1 | 2): void {
    const page2 = page === 2;
    this.storage1Button.setIndices(page2 ? 744 : 743, page2 ? 744 : 743, 744);
    this.storage2Button.setIndices(page2 ? 745 : 746, page2 ? 745 : 746, 746);
    this.rentButton.visible = page2;
    this.lockedPage.visible = page2 && !this.hasExpandedStorage;
    this.rentalLabel.visible = page2;
    if (page2) {
      this.rentalLabel.text = this.hasExpandedStorage
        ? `${T.ExpandedStorageExpiresOn} ${dateFromBinary(this.expandedStorageExpiryTime).toLocaleString()}`
        : T.ExpandedStorageLocked;
      this.rentalLabel.foreColour = this.hasExpandedStorage ? "#ffffff" : "#ff0000";
    }
    this.grid.forEach((cell, idx) => {
      cell.hidden = page2 ? idx < STORAGE_GRID_SIZE || !this.hasExpandedStorage : idx >= STORAGE_GRID_SIZE;
    });
  }
}
