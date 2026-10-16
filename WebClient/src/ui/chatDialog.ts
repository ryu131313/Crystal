// Chat: port of ChatDialog + ChatControlBar (Client/MirScenes/Dialogs/MainDialogs.cs) with the desktop artwork.
//   ChatDialog      Prguse 2221 / 2224 / 2227 (4 / 7 / 11 lines) at (MainDialog.X + 230, ScreenHeight - 97), drawn at
//                   80 % with Settings.TransparentChat (ChatOptionDialog.UpdateTransparency); fixed 13 px rows of 8pt text with the ChatType back /
//                   fore colours (ReceiveChat), word wrap at 614 px that never splits an item link, Home / Up / Down /
//                   End buttons, the CountBar + draggable PositionBar, mouse wheel and the Up / Down / PageUp /
//                   PageDown / Home / End keys; [Filters] hide lines (Update); clicking a line starts a whisper to its
//                   sender; "<Title/UniqueID>" item links draw blue (red under the mouse) and show the item on click.
//   ChatTextBox     the DarkGray input at (1, 54) (hidden until Enter / '@' / '!' / Space / '/'); Enter sends (C.Chat
//                   with the links still in the text; "@light new|old" stays local; '@LEVELEFFECT' / '@TARGETDEAD' also
//                   flip the local settings), Escape cancels, '/' starts with the last whisper target (LastPM); F1-F12
//                   and Tab still reach the game while typing (ChatTextBox_KeyDown -> CMain_KeyDown).
//   ChatControlBar  Prguse 2034 above the chat: the tab buttons (All / Shout / Whisper / Lover / Mentor / Group / Guild)
//                   pick ChatPrefix, Trade (C.TradeRequest), Size (ChangeSize), Settings (ChatOptionDialog). Report is
//                   hidden like the desktop.
// The messages, LastPM, LinkedItems, ChatPrefix, the chat items and the send logic live in the ChatLog model
// (src/game/chatLog.ts); this window is a view over it (it wraps and colours each `added` message).

import type { UserItem } from "../protocol/server.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, keyLabel, matchesAnyBinding } from "../game/keybinds.ts";
import { gameSettings, setSetting, onSettingChange } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { chatVisible, CHAT_TAB_PREFIX, CHAT_LINK, MAX_CHAT_LENGTH, type ChatLog } from "../game/chatLog.ts";
import { MirWindow, MirButton, MirImage, MirLabel, MirTextBox, Lib, styleTooltip, getUiRoot } from "./mir/controls.ts";
import { pixelDetect, scaleOf } from "./hudCommon.ts";

const CHAT_WIDTH = 614;

/** ChatDialog.ReceiveChat: [back, fore] per ChatType. */
function chatColours(type: number): [string, string] {
  switch (type) {
    case ChatType.Hint: return ["#ffffff", "#006400"];
    case ChatType.Announcement: case ChatType.LineMessage: return ["#0000ff", "#ffffff"];
    case ChatType.Shout: return ["#ffff00", "#000000"];
    case ChatType.Shout2: return ["#008000", "#ffffff"];
    case ChatType.Shout3: return ["#800080", "#ffffff"];
    case ChatType.System: return ["#ff0000", "#ffffff"];
    case ChatType.System2: return ["#8b0000", "#ffffff"];
    case ChatType.Group: return ["#ffffff", "#a52a2a"];
    case ChatType.WhisperOut: return ["#ffffff", "#6495ed"];
    case ChatType.WhisperIn: return ["#ffffff", "#00008b"];
    case ChatType.Guild: return ["#ffffff", "#008000"];
    case ChatType.LevelUp: return ["rgb(225,185,250)", "#0000ff"];
    case ChatType.Relationship: return ["transparent", "#ff69b4"];
    case ChatType.Mentor: return ["#ffffff", "#800080"];
  }
  return ["#ffffff", "#000000"];
}

let measureCtx: CanvasRenderingContext2D | null = null;
function measure(text: string): number {
  measureCtx ??= document.createElement("canvas").getContext("2d")!;
  measureCtx.font = "8pt Arial";
  return measureCtx.measureText(text).width;
}

interface ChatHistory { text: string; back: string; fore: string; type: number }

export interface ChatWorld {
  screen: readonly [number, number];
  /** MainDialog.Location.X. */
  mainX: number;
  /** The chat model: messages, LastPM, LinkedItems, ChatPrefix, ChatItemList (S.NewChatItem), C.Chat. */
  log: ChatLog;
  /** The item text for a chat link (GameScene.CreateItemLabel). */
  describe: (item: UserItem) => string;
  /** ChatControlBar.SettingsButton: ChatOptionDialog Show / Hide. */
  chatOptions: () => void;
  /** ChatControlBar.TradeButton. */
  trade: () => void;
  /** After ChangeSize: the top of the chat bar (BeltDialog / HeroBeltDialog follow it on the desktop). */
  onResize?: (chatBarTop: number) => void;
}

export class ChatDialog {
  private readonly w: ChatWorld;
  private readonly win: MirWindow;
  private readonly bar: MirWindow;
  private readonly textBox: MirTextBox;
  /** The chat input (id "chat-input"; other windows prefill it). */
  readonly input: HTMLInputElement;
  private readonly downButton: MirButton;
  private readonly endButton: MirButton;
  private readonly countBar: MirImage;
  private readonly positionBar: MirButton;
  private readonly tabButtons = new Map<string, [MirButton, number]>();
  private lines: MirLabel[] = [];
  /** The log's messages as wrapped rows (ChatDialog.FullHistory). */
  private full: ChatHistory[] = [];
  private history: ChatHistory[] = [];
  private startIndex = 0;
  private lineCount = 4;
  private windowSize = 0;
  private tooltip: HTMLDivElement | null = null;

  constructor(w: ChatWorld) {
    this.w = w;
    const [, SH] = w.screen;
    this.win = new MirWindow({ library: Lib.Prguse, index: 2221, location: [w.mainX + 230, SH - 97], sort: false, visible: true });
    this.win.el.classList.add("mir-hud");
    pixelDetect(this.win);
    this.textBox = new MirTextBox({ parent: this.win, size: [627, 13], location: [1, 54], maxLength: MAX_CHAT_LENGTH,
      backColour: "#a9a9a9", foreColour: "#000000", font: "8pt Arial", onEnter: () => this.sendText(), onEscape: () => this.closeInput() });
    this.input = this.textBox.input as HTMLInputElement;
    this.input.id = "chat-input";
    // ChatTextBox_KeyDown: F1-F12 and Tab go on to the game's key handler
    this.input.addEventListener("keydown", (e) => {
      if (!/^F([1-9]|1[0-2])$/.test(e.key) && e.key !== "Tab") return;
      e.preventDefault();
      window.dispatchEvent(new KeyboardEvent("keydown", e));
    });
    // ChatTextBox.Visible: kept in the layout (invisible, no mouse) so other windows can focus it to prefill a whisper
    this.showTextBox(false);
    this.input.addEventListener("focus", () => this.showTextBox(true));
    this.input.addEventListener("blur", () => { if (!this.input.value) this.showTextBox(false); });
    const b = (index: number, location: [number, number], onClick: () => void) =>
      new MirButton({ parent: this.win, library: Lib.Prguse, index, hoverIndex: index + 1, pressedIndex: index + 2, location, sound: SoundList.ButtonA, onClick });
    b(2018, [618, 1], () => { if (this.startIndex === 0) return; this.startIndex = 0; this.update(); }); // HomeButton
    b(2021, [618, 9], () => { if (this.startIndex === 0) return; this.startIndex--; this.update(); }); // UpButton
    this.endButton = b(2027, [618, 45], () => { if (this.startIndex === this.history.length - 1) return; this.startIndex = this.history.length - 1; this.update(); });
    this.downButton = b(2024, [618, 39], () => { if (this.startIndex === this.history.length - 1) return; this.startIndex++; this.update(); });
    this.countBar = new MirImage({ parent: this.win, library: Lib.Prguse, index: 2012, location: [622, 16] });
    this.positionBar = new MirButton({ parent: this.win, library: Lib.Prguse, index: 2015, hoverIndex: 2016, pressedIndex: 2017, location: [619, 16], sound: 0 });
    this.positionBar.el.addEventListener("pointerdown", (e) => this.dragPositionBar(e));
    this.win.el.addEventListener("wheel", (e) => this.wheel(e));

    // ChatControlBar
    this.bar = new MirWindow({ library: Lib.Prguse, index: 2034, location: [w.mainX + 230, SH - 112], sort: false, visible: true });
    this.bar.el.classList.add("mir-hud");
    const barButton = (index: number, x: number, hint: string, onClick: () => void, sound: number = SoundList.ButtonA, visible = true) =>
      new MirButton({ parent: this.bar, library: Lib.Prguse, index, hoverIndex: index + 1, pressedIndex: index + 2, location: [x, 1], sound, hint, onClick, visible });
    barButton(2057, 574, "Size", () => this.changeSize());
    barButton(2060, 596, "Chat Settings", () => w.chatOptions());
    const tab = (name: keyof typeof CHAT_TAB_PREFIX, index: number, x: number, hint: string, extra?: () => void) => {
      const button = barButton(index, x, hint, () => { extra?.(); this.toggleChatFilter(name); });
      this.tabButtons.set(name, [button, index]);
    };
    tab("All", 2036, 12, "All");
    tab("Shout", 2039, 34, "Shout");
    tab("Whisper", 2042, 56, "Whisper");
    tab("Lover", 2045, 78, "Lover");
    tab("Mentor", 2048, 100, "Mentor");
    tab("Group", 2051, 122, "Group");
    tab("Guild", 2054, 144, "Guild", () => setSetting("ShowGuildChat", !gameSettings.ShowGuildChat));
    new MirButton({ parent: this.bar, library: Lib.Prguse, index: 2004, hoverIndex: 2005, pressedIndex: 2006, location: [166, 1], sound: SoundList.ButtonC,
      hint: `Trade (${keyLabel(KeybindOptions.Trade)})`, onClick: () => w.trade() });
    barButton(2063, 552, "Report", () => {}, SoundList.ButtonA, false); // ReportButton (Visible = false)
    this.toggleChatFilter("All");

    // ChatPanel_KeyPress / ChatPanel_KeyDown while the game has the keyboard
    window.addEventListener("keydown", (e) => this.panelKey(e), { signal: gameSignal() });
    const off = onSettingChange((key) => {
      if (key.startsWith("Filter")) this.update();
      if (key === "TransparentChat") this.updateTransparency();
    });
    // ChatDialog.ReceiveChat: the log's messages (any already there, then each new one)
    for (const m of w.log.messages) this.addRows(m.text, m.type);
    const offAdded = w.log.added.on((m) => this.addRows(m.text, m.type));
    gameSignal().addEventListener("abort", () => { off(); offAdded(); this.win.dispose(); this.bar.dispose(); this.tooltip?.remove(); });
    this.updateTransparency();
    this.update();
  }

  private showTextBox(show: boolean): void {
    this.textBox.el.style.opacity = show ? "" : "0";
    this.textBox.el.style.pointerEvents = show ? "" : "none";
  }

  /** Whether the user is typing a message. */
  get typing(): boolean { return document.activeElement === this.input; }

  /** ChatOptionDialog.UpdateTransparency: the dialog draws at 80 % opacity. */
  private updateTransparency(): void {
    this.win.picOpacity = gameSettings.TransparentChat ? 0.8 : 1;
  }

  /** Opens the text box with `text`, the caret at the end (ChatDialog.SetChatText / the whisper prefills). */
  setText(text: string): void {
    this.showTextBox(true);
    this.input.value = text.slice(0, MAX_CHAT_LENGTH);
    this.input.focus();
    this.input.setSelectionRange(this.input.value.length, this.input.value.length);
  }

  /** MirItemCell Shift+right-click: "<Title> " appended + ChatDialog.LinkedItems. False when the message would be too long. */
  addLink(uniqueId: bigint, title: string, grid: number): boolean {
    const text = `<${title}> `;
    if (this.input.value.length + text.length > MAX_CHAT_LENGTH) return false;
    this.w.log.addLink({ UniqueID: uniqueId, Title: title, Grid: grid });
    this.setText(this.input.value + text);
    return true;
  }

  private closeInput(): void {
    this.showTextBox(false);
    this.input.value = "";
    this.w.log.clearLinks();
    this.input.blur();
  }

  /** ChatTextBox_KeyPress Enter (ChatLog.submit: local commands, C.Chat, LastPM). */
  private sendText(): void {
    this.w.log.submit(this.input.value);
    this.closeInput();
  }

  /** ChatPanel_KeyPress + ChatPanel_KeyDown (the game has the keyboard). */
  private panelKey(e: KeyboardEvent): void {
    const a = document.activeElement;
    if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement || a instanceof HTMLSelectElement) return;
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    switch (e.key) {
      case "@": case "!": case " ": case "Enter":
        e.preventDefault();
        this.setText(this.w.log.prefix !== "" ? this.w.log.prefix : e.key === "!" || e.key === "@" ? e.key : "");
        return;
      case "/":
        e.preventDefault();
        this.setText(this.w.log.lastPM + " ");
        return;
    }
    if (matchesAnyBinding(e)) return;
    const last = this.history.length - 1;
    switch (e.key) {
      case "ArrowUp": if (this.startIndex === 0) return; this.startIndex--; break;
      case "Home": if (this.startIndex === 0) return; this.startIndex = 0; break;
      case "ArrowDown": if (this.startIndex === last) return; this.startIndex++; break;
      case "End": if (this.startIndex === last) return; this.startIndex = last; break;
      case "PageUp": if (this.startIndex === 0) return; this.startIndex -= this.lineCount; break;
      case "PageDown": if (this.startIndex === last) return; this.startIndex += this.lineCount; break;
      default: return;
    }
    e.preventDefault();
    this.update();
  }

  /** ChatPanel_MouseWheel. */
  private wheel(e: WheelEvent): void {
    const count = -Math.sign(e.deltaY);
    if (this.startIndex === 0 && count >= 0) return;
    if (this.startIndex === this.history.length - 1 && count <= 0) return;
    this.startIndex -= count;
    this.update();
  }

  /** PositionBar_OnMoving: drag inside the CountBar. */
  private dragPositionBar(e: PointerEvent): void {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.preventDefault();
    const s = scaleOf(getUiRoot());
    const startY = e.clientY, startTop = this.positionBar.location[1];
    const move = (m: PointerEvent) => {
      const countH = this.countBar.size[1], barH = this.positionBar.size[1] || 14;
      let y = Math.round(startTop + (m.clientY - startY) / s);
      if (y >= 16 + countH - barH) y = 16 + countH - barH;
      if (y < 16) y = 16;
      const h = Math.trunc((y - 16) / ((countH - barH) / (this.history.length - 1)));
      if (Number.isFinite(h) && h !== this.startIndex) { this.startIndex = h; this.update(); }
      this.positionBar.location = [619, y];
    };
    const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  }

  /** ChatDialog.ReceiveChat's view half: colours, word wrap (never inside an item link), auto-scroll at the bottom. */
  private addRows(text: string, type: number): void {
    const [back, fore] = chatColours(type);
    const parts: string[] = [];
    let index = 0;
    for (let i = 1; i < text.length; i++) {
      if (i - index < 0) continue;
      if (measure(text.substring(index, i)) > CHAT_WIDTH) {
        let offset = i - index;
        let newIndex = i - 1;
        for (const m of text.substring(index).matchAll(CHAT_LINK)) {
          if (m.index! < i - index && m.index! + m[0].length > offset - 1) { offset = m.index!; newIndex = index + m.index!; break; }
        }
        parts.push(text.substr(index, Math.max(0, offset - 1)));
        index = newIndex;
      }
    }
    parts.push(text.substring(index));
    if (this.startIndex === this.history.length - this.lineCount) this.startIndex += parts.length;
    for (const p of parts) this.full.push({ text: p, back, fore, type });
    this.update();
  }

  /** ChatDialog.Update: filter, clamp, position bar, the visible rows. */
  update(): void {
    this.history = this.full.filter((h) => chatVisible(h.type));
    for (const l of this.lines) l.dispose();
    this.lines = [];
    this.hideTooltip();
    if (this.startIndex >= this.history.length) this.startIndex = this.history.length - 1;
    if (this.startIndex < 0) this.startIndex = 0;
    if (this.history.length > 1) {
      const h = Math.trunc(((this.countBar.size[1] - (this.positionBar.size[1] || 14)) / (this.history.length - 1)) * this.startIndex);
      this.positionBar.location = [619, 16 + h];
    }
    let y = 1;
    for (let i = this.startIndex; i < this.history.length; i++) {
      const h = this.history[i]!;
      const label = new MirLabel({ parent: this.win, autoSize: true, backColour: h.back, foreColour: h.fore, location: [1, y], outline: false, font: "8pt Arial" });
      // the link labels draw over the line: here inline spans
      let last = 0;
      for (const m of h.text.matchAll(CHAT_LINK)) {
        label.el.append(h.text.slice(last, m.index));
        const item = this.w.log.chatItem(m[2]!);
        if (item) label.el.append(this.chatLink(m[1]!, item));
        else label.el.append(m[1]!);
        last = m.index! + m[0].length;
      }
      label.el.append(h.text.slice(last));
      // click: whisper the sender
      label.onClick(() => {
        const name = (label.el.textContent ?? "").split(/[: ]/)[0]!.replace(/[^A-Za-z0-9]/g, "");
        this.setText(`/${name} `);
      });
      this.lines.push(label);
      y += 13;
      if (i - this.startIndex === this.lineCount - 1) break;
    }
  }

  /** ChatDialog.ChatLink: blue, red under the mouse, ButtonC; a click shows the item (GameScene.CreateItemLabel). */
  private chatLink(title: string, item: UserItem): HTMLElement {
    const span = document.createElement("span");
    span.textContent = title;
    span.style.color = "#0000ff";
    span.addEventListener("pointerenter", () => { span.style.color = "#ff0000"; });
    span.addEventListener("pointerleave", () => { span.style.color = "#0000ff"; this.hideTooltip(); });
    span.addEventListener("pointerdown", () => { span.style.color = "#0000ff"; });
    span.addEventListener("pointerup", () => { span.style.color = "#ff0000"; });
    span.addEventListener("click", (e) => {
      e.stopPropagation();
      this.hideTooltip();
      const box = document.createElement("div");
      styleTooltip(box);
      box.style.cssText += ";position:absolute;z-index:1000;padding:4px 6px;white-space:pre;pointer-events:none";
      box.textContent = this.w.describe(item);
      const root = getUiRoot();
      root.append(box);
      const r = root.getBoundingClientRect(), s = scaleOf(root);
      const x = (e.clientX - r.left) / s, y0 = (e.clientY - r.top) / s;
      box.style.left = Math.max(0, Math.min(root.offsetWidth - box.offsetWidth, x)) + "px";
      box.style.top = Math.max(0, y0 - box.offsetHeight - 8) + "px";
      this.tooltip = box;
    });
    return span;
  }

  private hideTooltip(): void {
    this.tooltip?.remove();
    this.tooltip = null;
  }

  /** ChatControlBar.ToggleChatFilter: the tab's pressed picture + ChatDialog.ChatPrefix. */
  private toggleChatFilter(name: keyof typeof CHAT_TAB_PREFIX): void {
    for (const [n, [button, index]] of this.tabButtons) {
      if (n === name) button.setIndices(index + 2, index + 2, index + 2);
      else button.setIndices(index, index + 1, index + 2);
    }
    this.w.log.selectTab(name);
  }

  /** ChatDialog.ChangeSize (+ ChatControlBar.SizeButton moving the bar): 4 / 7 / 11 lines, the bottom stays put. */
  private changeSize(): void {
    const bottom = this.win.location[1] + [68, 116, 164][this.windowSize]!; // DisplayRectangle.Bottom
    if (++this.windowSize >= 3) this.windowSize = 0;
    const extra = this.windowSize * 48;
    this.lineCount = [4, 7, 11][this.windowSize]!;
    const index = [2221, 2224, 2227][this.windowSize]!;
    const height = [68, 116, 164][this.windowSize]!;
    this.win.index = index;
    this.countBar.index = [2012, 2013, 2014][this.windowSize]!;
    this.downButton.location = [618, 39 + extra];
    this.endButton.location = [618, 45 + extra];
    this.textBox.location = [1, 54 + extra];
    this.win.location = [this.win.location[0], bottom - height];
    this.bar.location = [this.bar.location[0], bottom - height - 16];
    this.w.onResize?.(bottom - height - 16);
    void this.countBar.ready.then(() => this.update());
    this.update();
  }
}
