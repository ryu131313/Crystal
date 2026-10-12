// Friends: FriendDialog (Friends / Blacklist tabs, 12 names per page in two columns, online names green, the memo as
// the row tooltip like GameScene.CreateMemoLabel; Add / Remove / Memo / Mail / Whisper) + MemoDialog + GameScene.FriendUpdate.
// Toggle with F (KeybindOptions.Friends) or the HUD Friends button; opening sends C.RefreshFriends.
// MirInputBox / MirMessageBox Yes-No are prompt() / confirm().

import type { ServerPacket, ClientFriend } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { el } from "./screens.ts";

const ROWS = 12; // FriendDialog.Rows
const MAX_MEMO = 200; // PlayerObject.AddMemo refuses longer memos

export class FriendWindow {
  /** FriendDialog.Friends (S.FriendUpdate). */
  friends: ClientFriend[] = [];
  /** The HUD Friends button (MainDialogs FriendButton); game.ts places it. */
  readonly hudButton: HTMLButtonElement;

  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly composeMail: (name: string) => void;
  private readonly root = el("div", { class: "window friend-window", hidden: true });
  private readonly tabs = { friends: el("button", {}, "Friends"), blocked: el("button", {}, "Blacklist") };
  private readonly list = el("div", { class: "friend-list" });
  private readonly pageLabel = el("span", { class: "mail-page" });
  private blockedTab = false;
  private selected: ClientFriend | null = null;
  private page = 0;

  // MemoDialog
  private readonly memoRoot = el("div", { class: "window friend-memo", hidden: true });
  private readonly memoTitle = el("span", {});
  private readonly memoText = el("textarea", { class: "mail-text", rows: 5, maxLength: MAX_MEMO }) as HTMLTextAreaElement;
  private memoFriend: ClientFriend | null = null;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, composeMail: (name: string) => void) {
    this.send = send;
    this.chat = chat;
    this.composeMail = composeMail;
    this.hudButton = el("button", { title: "Friends (F)", onclick: () => this.toggle() }, "Friends") as HTMLButtonElement;
    this.tabs.friends.onclick = () => this.showTab(false);
    this.tabs.blocked.onclick = () => this.showTab(true);
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Friends"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "row guild-tabs" }, this.tabs.friends, this.tabs.blocked),
      this.list,
      el("div", { class: "row mail-pager" },
        el("button", { onclick: () => this.turnPage(-1) }, "‹"), this.pageLabel, el("button", { onclick: () => this.turnPage(1) }, "›")),
      el("div", { class: "row" },
        el("button", { onclick: () => this.add() }, "Add"),
        el("button", { onclick: () => this.remove() }, "Remove"),
        el("button", { onclick: () => this.memo() }, "Memo"),
        el("button", { onclick: () => this.mail() }, "Mail"),
        el("button", { onclick: () => this.whisper() }, "Whisper")),
    );
    this.memoRoot.append(
      el("div", { class: "window-title" }, this.memoTitle, el("button", { class: "window-close", onclick: () => this.hideMemo() }, "×")),
      this.memoText,
      el("div", { class: "row" },
        el("button", { class: "primary", onclick: () => this.saveMemo() }, "OK"),
        el("button", { onclick: () => this.hideMemo() }, "Cancel")));
    for (const r of [this.root, this.memoRoot]) {
      r.addEventListener("mousedown", (e) => e.stopPropagation());
      r.addEventListener("keydown", (e) => e.stopPropagation());
      stage.append(r);
    }
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (matches(e, KeybindOptions.Friends) && !(a instanceof HTMLInputElement) && !(a instanceof HTMLTextAreaElement)) this.toggle();
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  // FriendDialog.Show (C.RefreshFriends) / Hide (MemoDialog.Hide)
  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (show) { this.send(ClientPackets.RefreshFriends()); this.render(); }
    else this.hideMemo();
  }

  /** Server packets; returns true when handled. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "FriendUpdate") return false;
    // GameScene.FriendUpdate: replace the list; a visible dialog redraws keeping the selection (Update(false))
    this.friends = p.Friends;
    if (this.selected) this.selected = this.friends.find((f) => f.Index === this.selected!.Index && f.Blocked === this.blockedTab) ?? null;
    this.render();
    return true;
  }

  // FriendLabel / BlacklistLabel -> UpdateDisplay: a tab change clears the selection and hides the memo
  private showTab(blocked: boolean): void {
    if (this.blockedTab === blocked) return;
    this.blockedTab = blocked;
    this.selected = null;
    this.page = 0;
    this.hideMemo();
    this.render();
  }

  private filtered(): ClientFriend[] {
    return this.friends.filter((f) => f.Blocked === this.blockedTab);
  }

  // PreviousButton / NextButton (deliberate: the page count uses the shown tab, the desktop counts both lists)
  private turnPage(dir: number): void {
    const maxPage = Math.max(0, Math.ceil(this.filtered().length / ROWS) - 1);
    this.page = Math.min(maxPage, Math.max(0, this.page + dir));
    this.render();
  }

  // AddButton: MirInputBox (FriendEnterAddName / FriendEnterBlockName), C.AddFriend with the tab's Blocked flag
  private add(): void {
    const name = prompt(this.blockedTab ? "Please enter the name of the person you would like to Block." : "Please enter the name of the person you would like to Add.", "");
    if (name && name.trim()) this.send(ClientPackets.AddFriend({ Name: name.trim(), Blocked: this.blockedTab }));
  }

  // RemoveButton (ConfirmRemoveItem)
  private remove(): void {
    const f = this.selected;
    if (!f || !confirm(`Are you sure you wish to remove '${f.Name}'?`)) return;
    this.send(ClientPackets.RemoveFriend({ CharacterIndex: f.Index }));
  }

  // MemoButton -> MemoDialog.Show (the friend's memo, caret at the end)
  private memo(): void {
    const f = this.selected;
    if (!f) return;
    this.memoFriend = f;
    this.memoTitle.textContent = `Memo: ${f.Name}`;
    this.memoText.value = f.Memo;
    this.memoRoot.hidden = false;
    this.memoText.focus();
    this.memoText.setSelectionRange(this.memoText.value.length, this.memoText.value.length);
  }

  // MemoDialog.OKButton (the server ignores an empty memo or one over 200 characters)
  private saveMemo(): void {
    if (this.memoFriend) this.send(ClientPackets.AddMemo({ CharacterIndex: this.memoFriend.Index, Memo: this.memoText.value.replace(/\r?\n/g, "\r\n") }));
    this.hideMemo();
  }

  private hideMemo(): void {
    this.memoRoot.hidden = true;
    this.memoFriend = null;
  }

  // EmailButton -> MailComposeLetterDialog.ComposeMail
  private mail(): void {
    if (this.selected) this.composeMail(this.selected.Name);
  }

  // WhisperButton: only online friends; prefill "/Name " in the chat box
  private whisper(): void {
    const f = this.selected;
    if (!f) return;
    if (!f.Online) { this.chat("Player is not online", ChatType.System); return; }
    const input = document.getElementById("chat-input") as HTMLInputElement | null;
    if (!input) return;
    input.value = `/${f.Name} `;
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }

  // FriendDialog.Update + FriendRow.UpdateInterface (+ GameScene.CreateMemoLabel as the tooltip)
  private render(): void {
    this.tabs.friends.classList.toggle("on", !this.blockedTab);
    this.tabs.blocked.classList.toggle("on", this.blockedTab);
    if (this.root.hidden) return;
    const shown = this.filtered();
    const pages = Math.max(1, Math.ceil(shown.length / ROWS));
    if (this.page >= pages) this.page = pages - 1;
    this.pageLabel.textContent = `${this.page + 1} / ${pages}`;
    const rows = shown.slice(this.page * ROWS, this.page * ROWS + ROWS).map((f) => {
      const row = el("div", { class: `friend-row${f.Online ? " online" : ""}${this.selected?.Index === f.Index ? " selected" : ""}`, title: f.Memo }, f.Name);
      row.onclick = () => { this.selected = f; this.render(); };
      return row;
    });
    this.list.replaceChildren(...rows);
  }
}
