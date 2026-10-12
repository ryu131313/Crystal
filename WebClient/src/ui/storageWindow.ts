// NPC storage (bank): StorageDialog (Client/MirScenes/Dialogs/NPCDialogs.cs) + GameScene.NPCStorage /
// StorageUnlockResult / StoragePasswordResult / ResizeStorage, DOM edition.
//   Two pages of 80 cells (Globals.StorageGridSize): page 1 = slots 0..79, page 2 = 80..159 (the rented expansion; a
//   "locked" label until User.HasExpandedStorage), the Rent button (C.Chat "@ADDSTORAGE" after a Yes/No box), the
//   Protect button (set / change the storage password when the server requires one) and the unlock prompt.
//   Items live in Inventory.storage and the cells share the inventory window's pick (grid "npcStorage"): store / take
//   back / merge / move / split / equip from storage / unequip into storage follow MirItemCell (see inventory.ts).
// The password prompts are a non-blocking box with a masked input (MirInputBox with InputTextBox.Password = true).
// Deliberate differences: a forced first-time setup whose confirmation does not match closes that box before asking
// again (the desktop leaves it open behind the new one); Shift+right-click chat links are inventory-only here.

import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { STORAGE_GRID_SIZE } from "../game/inventory.ts";
import type { InventoryWindow } from "./inventoryWindow.ts";
import { messageBox } from "./messageBox.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";

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

/** What the window needs from the NPC dialog (GameScene.NPCDialog.Visible / Hide). */
export interface StorageNpc { readonly visible: boolean; close(): void }

export class StorageWindow {
  /** UserObject.HasExpandedStorage / ExpandedStorageExpiryTime / HasStoragePassword / RequireStoragePassword /
   *  StoragePasswordLastSet (UserInformation, then the storage packets). */
  hasExpandedStorage = false;
  expandedStorageExpiryTime = 0n;
  hasStoragePassword = false;
  requireStoragePassword = false;
  storagePasswordLastSet = 0n;
  private storageUnlocked = false;        // StorageDialog._storageUnlocked
  private pendingOpenAfterPasswordSet = false;
  private forcingPasswordSetup = false;
  private page: 1 | 2 = 1;
  private readonly stage: HTMLElement;
  private readonly invWindow: InventoryWindow;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly npc: StorageNpc;
  private readonly root = el("div", { class: "window storage-window", hidden: true });
  private readonly grid = el("div", { class: "inv-grid storage-grid" });
  private readonly lockedPage = el("div", { class: "storage-locked", hidden: true });
  private readonly rentalLabel = el("div", { class: "magic-meta storage-rental", hidden: true });
  private readonly page1Button = el("button", { onclick: () => this.refreshStorage(1) }, "Storage 1");
  private readonly page2Button = el("button", { onclick: () => this.refreshStorage(2) }, "Storage 2");
  private readonly rentButton = el("button", { title: "Rent / extend the expanded storage", onclick: () => this.rent() }, "Rent");
  private readonly protectButton = el("button", { title: "Storage password", onclick: () => this.manageStoragePassword() }, "Protect");
  /** Open password boxes (closed with the scene or when the storage closes from elsewhere). */
  private readonly boxes = new Set<() => void>();

  constructor(stage: HTMLElement, invWindow: InventoryWindow, send: (f: Uint8Array) => void,
    chat: (text: string, type: number) => void, npc: StorageNpc) {
    this.stage = stage;
    this.invWindow = invWindow;
    this.send = send;
    this.chat = chat;
    this.npc = npc;
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Storage"), el("button", { class: "window-close", onclick: () => this.hide() }, "×")),
      el("div", { class: "row storage-tabs" }, this.page1Button, this.page2Button, el("span", { class: "storage-spacer" }), this.rentButton, this.protectButton),
      this.grid, this.lockedPage, this.rentalLabel,
    );
    this.lockedPage.textContent = T.ExpandedStorageLocked;
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    this.root.addEventListener("contextmenu", (e) => e.preventDefault());
    stage.append(this.root);
    const prevRender = invWindow.onRender;
    invWindow.onRender = () => { prevRender(); this.render(); };
    // KeybindOptions.Closeall (Escape) hides StorageDialog with the rest
    window.addEventListener("keydown", (e) => {
      if (matches(e, KeybindOptions.Closeall) && !this.root.hidden && !(document.activeElement instanceof HTMLInputElement)) this.hide();
    }, { signal: gameSignal() });
    gameSignal().addEventListener("abort", () => this.closeBoxes());
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  handle(p: ServerPacket): void {
    switch (p.type) {
      case "UserInformation":
        this.hasExpandedStorage = p.HasExpandedStorage;
        this.hasStoragePassword = p.HasStoragePassword;
        this.requireStoragePassword = p.RequireStoragePassword;
        this.storagePasswordLastSet = p.StoragePasswordLastSet;
        this.expandedStorageExpiryTime = p.ExpandedStorageExpiryTime;
        break;
      // GameScene.NPCStorage: only while the NPC dialog is open
      case "NPCStorage": if (this.npc.visible) this.show(); break;
      // GameScene.NPCResponse hides StorageDialog (a new page from any NPC)
      case "NPCResponse": if (this.visible) this.hide(); break;
      case "StorageUnlockResult": this.handleStorageUnlockResult(p); break;
      case "StoragePasswordResult": this.handleStoragePasswordResult(p); break;
      case "ResizeStorage":
        // GameScene.ResizeStorage (Inventory resizes the array): StorageDialog.RefreshStorage2
        this.hasExpandedStorage = p.HasExpandedStorage;
        this.expandedStorageExpiryTime = p.ExpiryTime;
        this.refreshStorage(2);
        break;
    }
  }

  /** StorageDialog.Show: open straight away, force a first password, or ask for the password first. */
  show(): void {
    this.protectButton.disabled = !this.requireStoragePassword;
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
    this.root.hidden = false;
    this.refreshStorage(1);
  }

  /** StorageDialog.Hide (re-lock: the next open asks for the password again). */
  hide(): void {
    this.storageUnlocked = false;
    if (this.root.hidden) return;
    this.root.hidden = true;
    this.invWindow.clearPick();
  }

  /** StorageDialog.HandleStorageUnlockResult. */
  private handleStorageUnlockResult(p: Extract<ServerPacket, { type: "StorageUnlockResult" }>): void {
    this.hasStoragePassword = p.HasPassword;
    switch (p.Result) {
      case 0: case 4: this.storageUnlocked = true; this.show(); return;
      case 1: this.system(T.StoragePasswordNotAcceptable); return;
      case 2: this.system(T.StoragePasswordWrong); return;
      case 3: this.system(T.StoragePasswordUnavailable); return;
    }
  }

  /** StorageDialog.HandleStoragePasswordResult. */
  private handleStoragePasswordResult(p: Extract<ServerPacket, { type: "StoragePasswordResult" }>): void {
    const hadPassword = this.hasStoragePassword;
    this.hasStoragePassword = p.HasPassword;
    this.storagePasswordLastSet = p.LastSetTime;
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

  /** StorageDialog.PromptStoragePassword: a masked MirInputBox with the length rules; OK is disabled while empty;
   *  `onSubmit` returning false keeps the box open (refocused). */
  private promptStoragePassword(text: string, onSubmit: (value: string) => boolean, onCancel?: () => void): void {
    const input = el("input", { type: "password", maxLength: 64, class: "storage-password-input" }) as HTMLInputElement;
    const ok = el("button", { class: "primary", disabled: true }, "OK");
    const cancel = el("button", {}, "Cancel");
    const box = el("div", { class: "window message-box storage-password-box" },
      el("div", { class: "message-box-text" }, `${text}\n${T.StoragePasswordRules}`), input, el("div", { class: "row" }, ok, cancel));
    const close = () => { box.remove(); this.boxes.delete(close); };
    this.boxes.add(close);
    const submit = () => {
      const value = input.value;
      if (!value) return;
      if (onSubmit(value)) { close(); return; }
      input.focus();
    };
    input.oninput = () => { ok.disabled = !input.value; };
    ok.onclick = submit;
    cancel.onclick = () => { close(); onCancel?.(); };
    box.addEventListener("mousedown", (e) => e.stopPropagation());
    box.addEventListener("keydown", (e) => {
      e.stopPropagation();
      if (e.key === "Enter") submit();
      else if (e.key === "Escape") cancel.click();
    });
    this.stage.append(box);
    input.focus();
  }

  private closeBoxes(): void {
    for (const close of [...this.boxes]) close();
  }

  /** StorageDialog.RefreshStorage1 / RefreshStorage2: which cells show, the Rent button and the rental label. */
  private refreshStorage(page: 1 | 2): void {
    this.page = page;
    this.render();
  }

  private render(): void {
    if (this.root.hidden) return;
    const page2 = this.page === 2;
    this.page1Button.classList.toggle("primary", !page2);
    this.page2Button.classList.toggle("primary", page2);
    this.rentButton.hidden = !page2;
    this.rentalLabel.hidden = !page2;
    this.lockedPage.hidden = !page2 || this.hasExpandedStorage;
    if (page2) {
      this.rentalLabel.textContent = this.hasExpandedStorage
        ? `${T.ExpandedStorageExpiresOn} ${dateFromBinary(this.expandedStorageExpiryTime).toLocaleString()}`
        : T.ExpandedStorageLocked;
    }
    const showCells = !page2 || this.hasExpandedStorage;
    this.grid.hidden = !showCells;
    if (!showCells) { this.grid.replaceChildren(); return; }
    const start = page2 ? STORAGE_GRID_SIZE : 0;
    this.grid.replaceChildren(...Array.from({ length: STORAGE_GRID_SIZE }, (_, i) => this.invWindow.npcStorageCell(start + i)));
  }
}
