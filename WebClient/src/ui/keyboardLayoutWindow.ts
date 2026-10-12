// Keyboard settings: KeyboardLayoutDialog (+ KeybindRow / KeybindHeadingRow) of Client/MirScenes/Dialogs/KeyboardLayoutDialog.cs
// with the desktop artwork (Title 119, Prguse2 190-192 key buttons, 197-209 scroll buttons / position bar, 201 / 202 group
// bullet and spacer, Title 120-122 Reset, Prguse 1346 / 1347 Assign Rule check box).
// Rows are ordered by group, then description, and laid out from TopLine like UpdateText (18 px per row, 30 px per group
// heading, stop past y 260); the scroll buttons and the draggable position bar move TopLine (the mouse wheel too, a web
// addition). Clicking a row's current-key button waits for the next key (WaitingForBind; clicking again cancels); while
// waiting every game binding is suspended (GameScene_KeyDown returns early). CheckNewInput: modifier keys alone are
// ignored, Delete clears the binding (Keys.None, all "don't care"), any other key is taken with the held modifiers as
// "required" and the rest as "must not be held" (Assign Rule: Strict) or "don't care" (Relaxed). Like the desktop, a key
// already used elsewhere is accepted (its duplicate check is commented out); the web dialog only marks rows that now fire
// together with a red "!" (hint: the other functions). Reset restores KeyBindSettings.New.
// Toggled by KeybindOptions.Keybind (U); KeybindOptions.Closeall (Escape) hides it.

import { gameSignal } from "../game/scope.ts";
import {
  KeybindOptions, keyBinds, keyLabel, keyName, isModifierKeyName, matches, onKeyBindsChange, overlapping,
  resetKeyBinds, setCapturing, setKeyBind, tildeHeld, type KeyBind, type KeybindOption,
} from "../game/keybinds.ts";
import { SoundList } from "../game/audio.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirLabel, closeButton, Lib } from "./mir/controls.ts";
import { el } from "./screens.ts";
import { messageBox } from "./messageBox.ts";

/** KeyboardLayoutDialog.LineCount, PosX, PosMinY, PosMaxY. */
const LINE_COUNT = 16;
const POS_X = 491, POS_MIN_Y = 101, POS_MAX_Y = 344;

export class KeyboardLayoutWindow {
  private readonly win: MirWindow;
  private readonly positionBar: MirButton;
  private readonly enforceChecked: MirImage;
  private readonly enforceLabel: MirLabel;
  /** KeyboardLayoutDialog.Rows. */
  private rows: MirControl[] = [];
  /** KeyboardLayoutDialog.TopLine. */
  private topLine = 0;
  /** KeyboardLayoutDialog.Enforce. */
  private enforce = true;
  /** KeyboardLayoutDialog.WaitingForBind. */
  private waiting: KeybindOption | null = null;

  constructor(private readonly stage: HTMLElement) {
    // KeyboardLayoutDialog ctor (TitleLabel has no Index there, so it draws nothing and is left out)
    const win = this.win = new MirWindow({ library: Lib.Title, index: 119, movable: true, sort: true, location: "center", onShow: () => this.updateText() });
    new MirLabel({ parent: win, location: [135, 34], size: [242, 30], font: "bold 10pt Arial", center: true, text: "Keyboard Settings" }); // PageLabel
    closeButton(win, [489, 3], () => this.toggle(false)); // CloseButton: Save + Hide (saving is immediate here)
    new MirButton({ parent: win, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, size: [16, 14], location: [491, 88], sound: SoundList.ButtonA,
      onClick: () => this.scroll(-1) }); // ScrollUpButton
    new MirButton({ parent: win, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, size: [16, 14], location: [491, 363], sound: SoundList.ButtonA,
      onClick: () => this.scroll(1) }); // ScrollDownButton
    this.positionBar = new MirButton({ parent: win, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [POS_X, POS_MIN_Y], sound: 0 });
    this.dragPositionBar();
    new MirButton({ parent: win, library: Lib.Title, index: 120, hoverIndex: 121, pressedIndex: 122, size: [72, 25], location: [30, 400], onClick: () => this.reset() }); // ResetButton
    new MirButton({ parent: win, library: Lib.Prguse, index: 1346, location: [105, 406], sound: SoundList.ButtonA, onClick: () => this.enforceClick() }); // EnforceButton
    this.enforceChecked = new MirImage({ parent: win, library: Lib.Prguse, index: 1347, location: [105, 406], notControl: true });
    this.enforceLabel = new MirLabel({ parent: win, notControl: true, location: [120, 404], autoSize: true, text: "Assign Rule: Strict" });
    // web addition: the mouse wheel scrolls like the scroll buttons
    win.el.addEventListener("wheel", (e) => { if (e.deltaY) this.scroll(e.deltaY > 0 ? 1 : -1); }, { passive: true });

    const signal = gameSignal();
    // Capture phase: runs before every bubbling game key handler, so a bound key never also triggers its old action.
    window.addEventListener("keydown", (e) => {
      if (this.waiting === null) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      this.checkNewInput(e);
    }, { capture: true, signal });
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Keybind)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false);
    }, { signal });
    const off = onKeyBindsChange(() => { if (this.win.visible) this.updateText(); });
    signal.addEventListener("abort", () => { off(); this.cancelWait(); win.dispose(); });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  toggle(show = !this.win.visible): void {
    if (show) this.win.show();
    else { this.cancelWait(); this.win.hide(); }
  }

  private cancelWait(): void {
    this.waiting = null;
    setCapturing(false);
  }

  /** The rows UpdateText walks: KeyBindSettings.Keylist ordered by Group, then Description. */
  private ordered(): KeyBind[] {
    return [...keyBinds()].sort((a, b) => cmp(a.group, b.group) || cmp(a.description, b.description));
  }

  /** ScrollUpButton / ScrollDownButton.Click (the down limit counts one heading per group like the desktop). */
  private scroll(delta: -1 | 1): void {
    if (delta < 0) {
      if (this.topLine <= 0) return;
    } else {
      const list = keyBinds();
      if (this.topLine + LINE_COUNT >= list.length + new Set(list.map((b) => b.group)).size) return;
    }
    this.topLine += delta;
    this.updateText();
    this.updatePositionBar();
  }

  /** UpdatePositionBar. */
  private updatePositionBar(): void {
    const count = keyBinds().length;
    if (count <= LINE_COUNT) { this.positionBar.visible = false; return; }
    this.positionBar.visible = true;
    const interval = Math.max(1, Math.floor((POS_MAX_Y - POS_MIN_Y) / (count - LINE_COUNT)));
    const y = Math.max(POS_MIN_Y, Math.min(POS_MAX_Y, POS_MIN_Y + this.topLine * interval));
    this.positionBar.location = [POS_X, y];
  }

  /** PositionBar (Movable) + PositionBar_OnMoving: dragged along x = PosX between PosMinY and PosMaxY; TopLine follows. */
  private dragPositionBar(): void {
    const bar = this.positionBar.el;
    bar.addEventListener("pointerdown", (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      const root = this.win.el.getBoundingClientRect();
      const scale = root.width / (this.win.el.offsetWidth || 1) || 1;
      const startY = e.clientY;
      const startTop = this.positionBar.location[1];
      const move = (m: PointerEvent) => {
        const y = Math.max(POS_MIN_Y, Math.min(POS_MAX_Y, Math.round(startTop + (m.clientY - startY) / scale)));
        this.positionBar.location = [POS_X, y];
        const count = keyBinds().length;
        if (count <= LINE_COUNT) return;
        const interval = Math.max(1, Math.floor((POS_MAX_Y - POS_MIN_Y) / (count - LINE_COUNT)));
        // clamped to the last TopLine the scroll-down button can reach (the desktop can scroll past it)
        const maxTop = Math.max(0, count + new Set(keyBinds().map((b) => b.group)).size - LINE_COUNT);
        const top = Math.min(maxTop, Math.floor((y - POS_MIN_Y) / interval));
        if (top !== this.topLine) { this.topLine = top; this.updateText(); }
      };
      const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
    });
  }

  /** EnforceButton_Click. */
  private enforceClick(): void {
    this.enforce = !this.enforce;
    this.enforceChecked.visible = this.enforce;
    this.enforceLabel.text = this.enforce ? "Assign Rule: Strict" : "Assign Rule: Relaxed"; // AssignRuleStrict / AssignRuleRelaxed
  }

  /** KeyboardLayoutDialog.CheckNewInput. */
  private checkNewInput(e: KeyboardEvent): void {
    const opt = this.waiting;
    if (opt === null) return;
    const name = keyName(e);
    if (isModifierKeyName(name)) return;
    this.cancelWait();
    if (name === "Delete") setKeyBind(opt, "", 2, 2, 2, 2);
    else {
      const r = (held: boolean) => (held ? 1 : this.enforce ? 0 : 2);
      setKeyBind(opt, name, r(e.altKey), r(e.shiftKey), r(e.ctrlKey), r(tildeHeld()));
    }
    this.updateText();
  }

  /** ResetButton.Click. */
  private reset(): void {
    this.cancelWait();
    resetKeyBinds();
    this.updateText();
    messageBox(this.stage, "Keyboard settings have been reset back to default."); // KeyboardSettingsResetDefault
  }

  /** UpdateText: from TopLine, a KeybindHeadingRow per new group and a KeybindRow per binding, until y passes 260. */
  private updateText(): void {
    for (const r of this.rows) r.dispose();
    this.rows = [];
    const ordered = this.ordered();
    let currentGroup = "";
    let groupCount = 0;
    for (let i = 0; i < ordered.length; i++) {
      if (i < this.topLine) continue;
      const b = ordered[i]!;
      let y = 18 * (i - this.topLine) + groupCount * 30;
      if (y > 260) break;
      if (currentGroup !== b.group) {
        this.rows.push(this.headingRow(b.group, [15, 90 + y]));
        groupCount++;
        currentGroup = b.group;
      }
      y = 18 * (i - this.topLine) + groupCount * 30;
      if (y > 260) break;
      this.rows.push(this.keybindRow(b, [20, 90 + y]));
    }
  }

  /** KeybindHeadingRow (Size 400 x 40): Prguse2 201 bullet, bold 9pt group name, Prguse2 202 spacer. */
  private headingRow(group: string, location: [number, number]): MirControl {
    const row = new MirControl({ parent: this.win, location, size: [400, 40], notControl: true });
    new MirImage({ parent: row, library: Lib.Prguse2, index: 201, size: [8, 6], location: [10, 10] });
    new MirLabel({ parent: row, location: [20, 5], size: [100, 20], font: "bold 9pt Arial", text: group });
    new MirImage({ parent: row, library: Lib.Prguse2, index: 202, size: [3, 464], location: [0, 25] });
    return row;
  }

  /** KeybindRow (Size 460 x 15): description, default key, and the CurrentBindButton (Prguse2 190 / 191 / 192, text
   *  "  key" top-left like MirButton's non-centred label; "  ????" on 192 while waiting). */
  private keybindRow(b: KeyBind, location: [number, number]): MirControl {
    const row = new MirControl({ parent: this.win, location, size: [460, 15] });
    new MirLabel({ parent: row, location: [0, 0], size: [200, 15], text: b.description });
    new MirLabel({ parent: row, location: [200, 0], size: [100, 15], text: keyLabel(b.function, true) });
    const waiting = this.waiting === b.function;
    const button = new MirButton({ parent: row, library: Lib.Prguse2, location: [340, 0], size: [120, 16],
      index: waiting ? 192 : 190, hoverIndex: waiting ? 192 : 191, pressedIndex: 192 });
    new MirLabel({ parent: button, notControl: true, location: [0, 0], size: [120, 16], text: `  ${waiting ? "????" : keyLabel(b.function)}` });
    button.onClick(() => {
      // KeybindRow.CurrentBindButton.Click: a second click (on any row) cancels the wait
      if (this.waiting !== null) { this.cancelWait(); this.updateText(); return; }
      this.waiting = b.function;
      setCapturing(true);
      this.updateText();
    });
    const others = overlapping(b.function);
    if (others.length) {
      new MirLabel({ parent: row, location: [326, 0], autoSize: true, foreColour: "#ff0000", font: "bold 8pt Arial", text: "!",
        hint: `Also fires: ${others.map((o) => o.description).join(", ")}` });
    }
    return row;
  }
}

function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
