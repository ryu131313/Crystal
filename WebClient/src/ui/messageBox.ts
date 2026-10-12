// MirMessageBox / MirInputBox / MirAmountBox with the desktop artwork (Prguse 360 / 660 / 238, Title buttons), all
// non-blocking (confirm() / prompt() would freeze the frame loop). Modal like the desktop: a transparent layer blocks
// the rest of the interface; Enter / Escape follow MirMessageBox.OnKeyPress.

import { MirWindow, MirLabel, MirButton, MirTextBox, MirImage, closeButton, Lib, libPicture } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";

export interface BoxButton { label: string; primary?: boolean; onClick?: () => void }

// Title.Lib button art by caption (MirMessageBox): OK 200, Cancel 203, Yes 206, No 210
const BUTTON_ART: Record<string, number> = { ok: 200, cancel: 203, yes: 206, no: 210 };

/** MirMessageBox on Prguse 360. Buttons are placed like the desktop's OK / OKCancel / YesNo / YesNoCancel layouts
 *  (right-aligned at x 360, 100 px apart); a label without desktop art draws as a text button. Returns a close function. */
export function messageBox(_stage: HTMLElement, text: string, buttons: BoxButton[] = [{ label: "OK" }]): () => void {
  const win = new MirWindow({ library: Lib.Prguse, index: 360, location: "center", modal: true, sort: true });
  new MirLabel({ parent: win, location: [35, 35], size: [390, 110], text });
  const close = () => { win.dispose(); document.removeEventListener("keydown", keys, true); };
  const made = buttons.map((b, i) => {
    const art = BUTTON_ART[b.label.toLowerCase()];
    const x = 360 - (buttons.length - 1 - i) * 100;
    const button = art !== undefined
      ? new MirButton({ parent: win, library: Lib.Title, index: art, hoverIndex: art + 1, pressedIndex: art + 2, location: [x, 157], sound: SoundList.ButtonB })
      : new MirButton({ parent: win, library: Lib.Title, index: 200, hoverIndex: 201, pressedIndex: 202, location: [x, 157], sound: SoundList.ButtonB, text: b.label, drawImage: false, size: [74, 24], className: "mir-text-button" });
    button.onClick(() => { close(); b.onClick?.(); });
    return { b, button };
  });
  // OnKeyPress: Escape = Cancel / No / the only OK, Enter = OK / Yes
  const keys = (e: KeyboardEvent) => {
    if (!win.el.isConnected) return;
    const find = (...names: string[]) => made.find((m) => names.includes(m.b.label.toLowerCase()));
    let hit;
    if (e.key === "Escape") hit = find("cancel", "no") ?? (made.length === 1 ? made[0] : undefined);
    else if (e.key === "Enter") hit = made.find((m) => m.b.primary) ?? find("ok", "yes") ?? (made.length === 1 ? made[0] : undefined);
    else return;
    e.preventDefault();
    e.stopPropagation();
    if (hit) hit.button.el.click();
  };
  document.addEventListener("keydown", keys, true);
  win.show();
  return close;
}

/** MirInputBox on Prguse 660: caption, a lime-bordered text box, OK / Cancel. `onOk` returning false keeps the box
 *  open (the desktop leaves the box up when its OK handler does not dispose it). Returns a close function. */
export function inputBox(_stage: HTMLElement, text: string, initial: string, onOk: (value: string) => boolean | void, onCancel?: () => void, maxLength = 50): () => void {
  const win = new MirWindow({ library: Lib.Prguse, index: 660, location: "center", modal: true });
  new MirLabel({ parent: win, location: [25, 25], size: [235, 40], text });
  const ok = () => { if (onOk(input.text) !== false) close(); };
  const cancel = () => { close(); onCancel?.(); };
  const input = new MirTextBox({ parent: win, location: [23, 86], size: [240, 19], border: true, borderColour: "lime", maxLength, text: initial, onEnter: ok, onEscape: cancel });
  new MirButton({ parent: win, library: Lib.Title, index: 200, hoverIndex: 201, pressedIndex: 202, location: [60, 123], onClick: ok });
  new MirButton({ parent: win, library: Lib.Title, index: 203, hoverIndex: 204, pressedIndex: 205, location: [160, 123], onClick: cancel });
  const close = () => win.dispose();
  win.show();
  input.focus();
  input.input.select();
  return close;
}

/** MirAmountBox on Prguse 238: title, the item picture (Items.Lib `image`, -1 = none) in a 38 x 34 cell, the amount
 *  (min .. max, default = max or `initial`), OK / Cancel / close. `onOk` gets the clamped amount. Returns a close function. */
export function amountBox(title: string, image: number, max: number, onOk: (amount: number) => void, min = 0, initial = 0, onCancel?: () => void): () => void {
  const win = new MirWindow({ library: Lib.Prguse, index: 238, location: "center", modal: true });
  new MirLabel({ parent: win, location: [19, 8], autoSize: true, text: title });
  const cancel = () => { close(); onCancel?.(); };
  closeButton(win, [180, 3], cancel);
  if (image >= 0) {
    // ItemImage: a 38 x 34 MirControl drawing Items[image] centred (MirAmountBox.DrawItem)
    const cell = new MirImage({ parent: win, location: [15, 34], size: [38, 34], notControl: true, autoSize: false });
    libPicture(Lib.Items, image).then((pic) => {
      if (!pic) return;
      const icon = new MirImage({ parent: cell, library: Lib.Items, index: image, notControl: true, autoSize: false });
      icon.location = [Math.floor((38 - pic.width) / 2), Math.floor((34 - pic.height) / 2)];
    });
  }
  const clamp = (v: string) => {
    let n = Math.floor(Number(v.replace(/[^0-9]/g, "")) || 0);
    if (n > max) n = max;
    if (n < min) n = min;
    return n;
  };
  const ok = () => { const n = clamp(input.text); close(); onOk(n); };
  const input = new MirTextBox({ parent: win, location: [58, 43], size: [132, 19], text: String(initial > 0 && initial <= max ? initial : max), onEnter: ok, onEscape: cancel,
    onChange: (v) => { const n = clamp(v); if (v !== "" && String(n) !== v) input.text = String(n); } });
  new MirButton({ parent: win, library: Lib.Title, index: 200, hoverIndex: 201, pressedIndex: 202, location: [23, 76], sound: SoundList.ButtonA, onClick: ok });
  new MirButton({ parent: win, library: Lib.Title, index: 203, hoverIndex: 204, pressedIndex: 205, location: [110, 76], sound: SoundList.ButtonA, onClick: cancel });
  const close = () => win.dispose();
  win.show();
  input.focus();
  input.input.select();
  return close;
}

/** Promise forms for code that used confirm() / prompt(). */
export function confirmBox(text: string, yesNo = false): Promise<boolean> {
  return new Promise((resolve) => messageBox(document.body, text, yesNo
    ? [{ label: "Yes", onClick: () => resolve(true) }, { label: "No", onClick: () => resolve(false) }]
    : [{ label: "OK", onClick: () => resolve(true) }, { label: "Cancel", onClick: () => resolve(false) }]));
}
export function promptBox(text: string, initial = "", maxLength = 50): Promise<string | null> {
  return new Promise((resolve) => inputBox(document.body, text, initial, (v) => { resolve(v); }, () => resolve(null), maxLength));
}
export function amountPrompt(title: string, image: number, max: number, min = 0, initial = 0): Promise<number | null> {
  return new Promise((resolve) => amountBox(title, image, max, (n) => resolve(n), min, initial, () => resolve(null)));
}
