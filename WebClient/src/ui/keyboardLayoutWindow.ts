// Keyboard settings: KeyboardLayoutDialog (+ KeybindRow / KeybindHeadingRow) of Client/MirScenes/Dialogs/KeyboardLayoutDialog.cs.
// Rows are ordered by group, then description (UpdateText). Clicking a row's current-key button waits for the next key
// (WaitingForBind; clicking again cancels); while waiting every game binding is suspended (GameScene_KeyDown returns
// early). CheckNewInput: modifier keys alone are ignored, Delete clears the binding (Keys.None, all "don't care"), any
// other key is taken with the held modifiers as "required" and the rest as "must not be held" (Assign Rule: Strict) or
// "don't care" (Relaxed). Like the desktop, a key already used elsewhere is accepted (its duplicate check is commented
// out); the web dialog only marks rows that now fire together with "!". Reset restores KeyBindSettings.New.
// Toggled by KeybindOptions.Keybind (U); KeybindOptions.Closeall (Escape) hides it.

import { gameSignal } from "../game/scope.ts";
import {
  KeybindOptions, keyBinds, keyLabel, keyName, isModifierKeyName, matches, onKeyBindsChange, optionName, overlapping,
  resetKeyBinds, setCapturing, setKeyBind, tildeHeld, type KeyBind, type KeybindOption,
} from "../game/keybinds.ts";
import { el } from "./screens.ts";
import { messageBox } from "./messageBox.ts";

export class KeyboardLayoutWindow {
  /** HUD stand-in for the desktop's menu entry. */
  readonly hudButton = el("button", { title: "Keyboard settings (U)", onclick: () => this.toggle() }, "Keys");
  private readonly root = el("div", { class: "window keyboard-layout-window", hidden: true });
  private readonly list = el("div", { class: "keybind-list" });
  private readonly enforceBox = el("input", { type: "checkbox", checked: true });
  private readonly enforceLabel = el("span", {}, "Assign Rule: Strict");
  /** KeyboardLayoutDialog.Enforce. */
  private enforce = true;
  /** KeyboardLayoutDialog.WaitingForBind. */
  private waiting: KeybindOption | null = null;

  constructor(private readonly stage: HTMLElement) {
    this.enforceBox.onchange = () => {
      this.enforce = this.enforceBox.checked;
      this.enforceLabel.textContent = this.enforce ? "Assign Rule: Strict" : "Assign Rule: Relaxed";
    };
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Keyboard Settings"), el("button", { class: "window-close", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "keybind-head" }, el("span", {}, "Function"), el("span", {}, "Default"), el("span", {}, "Current")),
      this.list,
      el("div", { class: "row keybind-foot" },
        el("button", { onclick: () => this.reset() }, "Reset"),
        el("label", { class: "keybind-enforce", title: "Strict: modifiers not held must stay released. Relaxed: they are ignored." }, this.enforceBox, this.enforceLabel)),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);

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
      else if (matches(e, KeybindOptions.Closeall) && !this.root.hidden) this.toggle(false);
    }, { signal });
    const off = onKeyBindsChange(() => { if (!this.root.hidden) this.render(); });
    signal.addEventListener("abort", () => { off(); this.cancelWait(); });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (!show) this.cancelWait(); // CloseButton: Save + Hide (saving is immediate here)
    else this.render();
  }

  private cancelWait(): void {
    this.waiting = null;
    setCapturing(false);
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
    this.render();
  }

  /** ResetButton.Click. */
  private reset(): void {
    this.cancelWait();
    resetKeyBinds();
    this.render();
    messageBox(this.stage, "Keyboard settings have been reset back to default.");
  }

  /** UpdateText: heading rows per group, then KeybindRows (description, default, current button). */
  private render(): void {
    const top = this.list.scrollTop;
    this.list.replaceChildren();
    const ordered = [...keyBinds()].sort((a, b) => cmp(a.group, b.group) || cmp(a.description, b.description));
    let group = "";
    for (const b of ordered) {
      if (b.group !== group) {
        group = b.group;
        this.list.append(el("div", { class: "keybind-group" }, group));
      }
      this.list.append(this.row(b));
    }
    this.list.scrollTop = top;
  }

  private row(b: KeyBind): HTMLElement {
    const waiting = this.waiting === b.function;
    const current = keyLabel(b.function);
    const button = el("button", { class: waiting ? "keybind-current waiting" : "keybind-current", title: "Click, then press the new key (Delete clears it)" },
      waiting ? "????" : current);
    button.onclick = () => {
      // KeybindRow.CurrentBindButton.Click: a second click (on any row) cancels the wait
      if (this.waiting !== null) { this.cancelWait(); this.render(); return; }
      this.waiting = b.function;
      setCapturing(true);
      this.render();
    };
    const others = overlapping(b.function);
    const warn = el("span", { class: "keybind-overlap" }, others.length ? "!" : "");
    if (others.length) warn.title = `Also fires: ${others.map((o) => o.description).join(", ")}`;
    return el("div", { class: "keybind-row", title: optionName(b.function) },
      el("span", { class: "keybind-name" }, b.description),
      el("span", { class: "keybind-default" }, keyLabel(b.function, true)),
      warn, button);
  }
}

function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
