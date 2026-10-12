// MirMessageBox as a non-blocking DOM box (confirm() would freeze the frame loop): text + buttons, removed on any click.
// Used where the game must keep running while the box is open (the revive prompt). Elsewhere confirm() / prompt() remain.

import { el } from "./screens.ts";

export interface BoxButton { label: string; primary?: boolean; onClick?: () => void }

/** Shows a centred box on `stage`; returns a function that closes it without a click. */
export function messageBox(stage: HTMLElement, text: string, buttons: BoxButton[] = [{ label: "OK" }]): () => void {
  const box = el("div", { class: "window message-box" },
    el("div", { class: "message-box-text" }, text),
    el("div", { class: "row" }, ...buttons.map((b) => el("button", { class: b.primary ? "primary" : "", onclick: () => { close(); b.onClick?.(); } }, b.label))));
  box.addEventListener("mousedown", (e) => e.stopPropagation());
  const close = () => box.remove();
  stage.append(box);
  return close;
}

/** MirInputBox as a non-blocking DOM box: text + an input (prefilled) + OK / Cancel. `onOk` returning false keeps the
 *  box open (the desktop leaves the box up when its OK handler does not dispose it). Enter = OK, Escape = Cancel; keys
 *  typed in the input never reach the game's window key handlers. Returns a function that closes it. */
export function inputBox(stage: HTMLElement, text: string, initial: string, onOk: (value: string) => boolean | void, onCancel?: () => void): () => void {
  const input = el("input", { type: "text", value: initial, maxLength: 64 });
  const ok = () => { if (onOk(input.value) !== false) close(); };
  const cancel = () => { close(); onCancel?.(); };
  const box = el("div", { class: "window message-box" },
    el("div", { class: "message-box-text" }, text),
    el("div", { class: "row" }, input),
    el("div", { class: "row" }, el("button", { class: "primary", onclick: ok }, "OK"), el("button", { onclick: cancel }, "Cancel")));
  box.addEventListener("mousedown", (e) => e.stopPropagation());
  input.addEventListener("keydown", (e) => {
    e.stopPropagation();
    if (e.key === "Enter") ok();
    else if (e.key === "Escape") cancel();
  });
  const close = () => box.remove();
  stage.append(box);
  input.focus();
  input.select();
  return close;
}
