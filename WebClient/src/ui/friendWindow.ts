// Friends: FriendDialog (Client/MirScenes/Dialogs/FriendDialog.cs, Title 199: Friends / Blacklist tab pictures, 12
// FriendRows per page in two columns, online names green, the selected row gray; Add / Remove / Memo / Mail / Whisper)
// + FriendRow + MemoDialog (Title 209) + GameScene.CreateMemoLabel (the memo box that follows the mouse over a row)
// as a view over the friends model (src/game/friends.ts: FriendDialog.Friends + GameScene.FriendUpdate). Toggle with F
// (KeybindOptions.Friends) or the HUD Friends button; opening sends C.RefreshFriends; Esc closes (GameScene Closeall).

import type { ClientFriend } from "../protocol/server.ts";
import type { Friends } from "../game/friends.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { MirWindow, MirImage, MirButton, MirControl, MirLabel, MirTextBox, closeButton, getUiRoot, uiSound, Lib, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { messageBox, inputBox } from "./messageBox.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { el } from "./screens.ts";

const ROWS = 12; // FriendDialog.Rows
const MAX_MEMO = 200; // PlayerObject.AddMemo refuses longer memos

/** Functions.StringOverLines: a line break after every maxWords-th word once the line is over maxLetters. */
function stringOverLines(line: string, maxWordsPerLine: number, maxLettersPerLine: number): string {
  let out = "";
  let lineLength = 0;
  const words = line.split(" ");
  for (let i = 0; i < words.length; i++) {
    lineLength += words[i]!.length + 1;
    out += words[i] + " ";
    if (i > 0 && i % maxWordsPerLine === 0 && lineLength > maxLettersPerLine) {
      lineLength = 0;
      out += "\n";
    }
  }
  return out;
}

/** A mouse position in stage pixels. */
function stagePoint(e: MouseEvent): [number, number] {
  const root = getUiRoot();
  const r = root.getBoundingClientRect();
  const s = r.width / (root.offsetWidth || 1) || 1;
  return [(e.clientX - r.left) / s, (e.clientY - r.top) / s];
}

/** FriendRow: 115 x 17, the name VerticalCenter (Green online, White offline; Gray back colour when selected). */
class FriendRow {
  readonly control: MirControl;
  private readonly nameLabel: MirLabel;
  friend: ClientFriend | null = null;

  constructor(parent: MirControl, location: [number, number]) {
    this.control = new MirControl({ parent, location, size: [115, 17], visible: false });
    this.nameLabel = new MirLabel({ parent: this.control, location: [0, 0], size: [115, 17], vCenter: true, notControl: true });
    this.nameLabel.el.style.whiteSpace = "pre";
  }

  /** FriendRow.UpdateInterface. */
  update(friend: ClientFriend | null, selected: boolean): void {
    this.friend = friend;
    this.control.visible = friend !== null;
    if (!friend) return;
    this.nameLabel.text = friend.Name;
    this.nameLabel.foreColour = friend.Online ? "#008000" : "#ffffff";
    this.nameLabel.el.style.backgroundColor = selected ? "#808080" : "";
  }
}

export class FriendWindow {
  private readonly stage: HTMLElement;
  /** FriendDialog.Friends (S.FriendUpdate; src/game/friends.ts). */
  private readonly model: Friends;
  private readonly chat: (text: string, type: number) => void;
  private readonly composeMail: (name: string) => void;
  /** ChatDialog.ChatTextBox prefill (GameUi.prefillChat). */
  private readonly prefillChat: (text: string) => void;
  private readonly win: MirWindow;
  private readonly friendLabel: MirImage;
  private readonly blacklistLabel: MirImage;
  private readonly pageLabel: MirLabel;
  private readonly rows: FriendRow[] = [];
  private blockedTab = false;
  private selected: ClientFriend | null = null;
  private page = 0;
  /** GameScene.MemoLabel. */
  private memoLabel: MirControl | null = null;

  // MemoDialog
  private readonly memoWin: MirWindow;
  private readonly memoText: MirTextBox;
  private memoFriend: ClientFriend | null = null;

  constructor(stage: HTMLElement, friends: Friends, chat: (text: string, type: number) => void, composeMail: (name: string) => void,
    prefillChat: (text: string) => void) {
    this.stage = stage;
    this.model = friends;
    this.chat = chat;
    this.composeMail = composeMail;
    this.prefillChat = prefillChat;

    // FriendDialog ctor
    this.win = new MirWindow({ library: Lib.Title, index: 199, movable: true, sort: true, location: "center" });
    new MirImage({ parent: this.win, library: Lib.Title, index: 6, location: [18, 8] });
    this.friendLabel = new MirImage({ parent: this.win, library: Lib.Title, index: 163, location: [10, 34], onClick: () => { uiSound(SoundList.ButtonA); this.showTab(false); } });
    this.blacklistLabel = new MirImage({ parent: this.win, library: Lib.Title, index: 167, location: [128, 34], onClick: () => { uiSound(SoundList.ButtonA); this.showTab(true); } });
    this.pageLabel = new MirLabel({ parent: this.win, size: [83, 17], location: [87, 216], center: true });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 240, hoverIndex: 241, pressedIndex: 242, size: [16, 16], location: [70, 218], sound: SoundList.ButtonA, onClick: () => this.turnPage(-1) });
    new MirButton({ parent: this.win, library: Lib.Prguse2, index: 243, hoverIndex: 244, pressedIndex: 245, size: [16, 16], location: [171, 218], sound: SoundList.ButtonA, onClick: () => this.turnPage(1) });
    closeButton(this.win, [237, 3], () => this.toggle(false));
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 554, hoverIndex: 555, pressedIndex: 556, location: [60, 241], sound: SoundList.ButtonA, hint: "Add", onClick: () => this.add() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 557, hoverIndex: 558, pressedIndex: 559, location: [88, 241], sound: SoundList.ButtonA, hint: "Remove", onClick: () => this.remove() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 560, hoverIndex: 561, pressedIndex: 562, location: [116, 241], sound: SoundList.ButtonA, hint: "Memo", onClick: () => this.memo() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 563, hoverIndex: 564, pressedIndex: 565, location: [144, 241], sound: SoundList.ButtonA, hint: "Mail", onClick: () => this.mail() });
    new MirButton({ parent: this.win, library: Lib.Prguse, index: 566, hoverIndex: 567, pressedIndex: 568, location: [172, 241], sound: SoundList.ButtonA, hint: "Whisper", onClick: () => this.whisper() });
    // FriendDialog.Update: Rows[i].Location
    for (let i = 0; i < ROWS; i++) {
      const row = new FriendRow(this.win, [(i % 2) * 115 + 16, 55 + Math.floor(i / 2) * 22]);
      row.control.onClick(() => {
        uiSound(SoundList.ButtonA);
        if (row.friend && row.friend !== this.selected) { this.selected = row.friend; this.render(); }
      });
      // FriendRow.OnMouseEnter / OnMouseLeave: GameScene.CreateMemoLabel / DisposeMemoLabel
      row.control.el.addEventListener("pointerenter", (e) => { if (row.friend && row.friend.Memo.length > 0) this.createMemoLabel(row.friend, e); });
      row.control.el.addEventListener("pointermove", (e) => this.placeMemoLabel(e));
      row.control.el.addEventListener("pointerleave", () => this.disposeMemoLabel());
      this.rows.push(row);
    }

    // MemoDialog ctor
    this.memoWin = new MirWindow({ library: Lib.Title, index: 209, movable: true, sort: true, location: "center" });
    this.memoText = new MirTextBox({ parent: this.memoWin, foreColour: "#ffffff", font: "8pt Arial", location: [15, 30], size: [165, 100], multiline: true, maxLength: MAX_MEMO });
    new MirButton({ parent: this.memoWin, library: Lib.Title, index: 382, hoverIndex: 383, pressedIndex: 384, location: [30, 133], sound: SoundList.ButtonA, onClick: () => this.saveMemo() });
    new MirButton({ parent: this.memoWin, library: Lib.Title, index: 385, hoverIndex: 386, pressedIndex: 387, location: [115, 133], sound: SoundList.ButtonA, onClick: () => this.hideMemo() });
    closeButton(this.memoWin, [168, 3], () => this.hideMemo());

    const signal = gameSignal();
    // GameScene.FriendUpdate (the model replaced the list): a visible dialog redraws keeping the selection (Update(false))
    const offChanged = friends.changed.on(() => {
      if (this.selected) this.selected = friends.list.find((f) => f.Index === this.selected!.Index && f.Blocked === this.blockedTab) ?? null;
      this.render();
    });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Friends)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false); // GameScene Esc hides FriendDialog
    }, { signal });
    signal.addEventListener("abort", () => { offChanged(); this.disposeMemoLabel(); this.win.dispose(); this.memoWin.dispose(); });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  // FriendDialog.Show (C.RefreshFriends) / Hide (MemoDialog.Hide)
  toggle(show = !this.win.visible): void {
    if (show) {
      if (this.win.visible) { this.win.bringToFront(); return; }
      this.win.show();
      this.model.refresh();
      this.render();
    } else {
      this.win.hide();
      this.hideMemo();
      this.disposeMemoLabel();
    }
  }

  // FriendLabel / BlacklistLabel -> UpdateDisplay: a tab change swaps the tab pictures, clears the selection and hides the memo
  private showTab(blocked: boolean): void {
    if (this.blockedTab === blocked) return;
    this.blockedTab = blocked;
    this.selected = null;
    this.page = 0;
    this.hideMemo();
    this.disposeMemoLabel();
    this.render();
  }

  private filtered(): ClientFriend[] {
    return this.model.list.filter((f) => f.Blocked === this.blockedTab);
  }

  // PreviousButton / NextButton (deliberate: the page count uses the shown tab, the desktop counts both lists)
  private turnPage(dir: number): void {
    const maxPage = Math.max(0, Math.ceil(this.filtered().length / ROWS) - 1);
    this.page = Math.min(maxPage, Math.max(0, this.page + dir));
    this.render();
  }

  // AddButton: MirInputBox (FriendEnterAddName / FriendEnterBlockName), C.AddFriend with the tab's Blocked flag
  private add(): void {
    const text = this.blockedTab ? "Please enter the name of the person you would like to Block." : "Please enter the name of the person you would like to Add.";
    const blocked = this.blockedTab;
    inputBox(this.stage, text, "", (name) => { if (name.trim()) this.model.add(name.trim(), blocked); });
  }

  // RemoveButton (ConfirmRemoveItem, YesNo)
  private remove(): void {
    const f = this.selected;
    if (!f) return;
    messageBox(this.stage, `Are you sure you wish to remove '${f.Name}'?`, [
      { label: "Yes", onClick: () => this.model.remove(f.Index) },
      { label: "No" },
    ]);
  }

  // MemoButton -> MemoDialog.Show (the friend's memo, caret at the end)
  private memo(): void {
    const f = this.selected;
    if (!f) return;
    this.memoFriend = f;
    this.memoText.text = f.Memo;
    this.memoWin.show();
    const input = this.memoText.input;
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }

  // MemoDialog.OKButton (the server ignores an empty memo or one over 200 characters)
  private saveMemo(): void {
    if (this.memoFriend) this.model.setMemo(this.memoFriend.Index, this.memoText.text.replace(/\r?\n/g, "\r\n"));
    this.hideMemo();
  }

  private hideMemo(): void {
    this.memoWin.hide();
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
    this.prefillChat(`/${f.Name} `);
  }

  /** GameScene.CreateMemoLabel: (50,50,50) 70 % box, gray border, the memo over lines at (4, 4). */
  private createMemoLabel(friend: ClientFriend, e: MouseEvent): void {
    if (this.memoLabel) return;
    const box = new MirControl({ parent: getUiRoot(), backColour: "rgb(50,50,50)", border: true, borderColour: "#808080", notControl: true, opacity: 0.7 });
    box.el.style.zIndex = "100000";
    const label = new MirLabel({ parent: box, location: [4, 4], autoSize: true, text: stringOverLines(friend.Memo, 5, 20) });
    box.size = [label.el.offsetWidth + 8, label.el.offsetHeight + 8];
    this.memoLabel = box;
    this.placeMemoLabel(e);
  }

  /** GameScene.Process: MemoLabel at the mouse + (15, 0), kept on the screen. */
  private placeMemoLabel(e: MouseEvent): void {
    const box = this.memoLabel;
    if (!box) return;
    const [mx, my] = stagePoint(e);
    let x = Math.round(mx) + 15, y = Math.round(my);
    if (x + box.size[0] > SCREEN_WIDTH) x = SCREEN_WIDTH - box.size[0];
    if (y + box.size[1] > SCREEN_HEIGHT) y = SCREEN_HEIGHT - box.size[1];
    box.location = [x, y];
  }

  /** GameScene.DisposeMemoLabel. */
  private disposeMemoLabel(): void {
    this.memoLabel?.dispose();
    this.memoLabel = null;
  }

  // FriendDialog.Update / UpdateRows + the tab pictures (UpdateDisplay: Friends 163 / 164, Blacklist 167 / 166)
  private render(): void {
    this.friendLabel.index = this.blockedTab ? 164 : 163;
    this.blacklistLabel.index = this.blockedTab ? 166 : 167;
    const shown = this.filtered();
    const pages = Math.max(1, Math.ceil(shown.length / ROWS));
    if (this.page >= pages) this.page = pages - 1;
    this.pageLabel.text = `${this.page + 1} / ${pages}`;
    for (let i = 0; i < ROWS; i++) {
      const f = shown[this.page * ROWS + i] ?? null;
      this.rows[i]!.update(f, f !== null && this.selected?.Index === f.Index);
    }
  }
}
