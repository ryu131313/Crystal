// Magic window (K): the user's spells with level, experience and key (MagicDialog / MagicButton / AssignKeyPanel),
// and the skill bar over the HUD (SkillBarDialog): F1-F8 slots with the cooldown shaded. Icons are MagIcon2[Icon * 2].
// Assigning a key sends C.MagicKey and clears that key from any other spell, as AssignKeyPanel does. The hero's spells
// (keys 17-24, Shift+F1-F8) have their own window: heroMagicWindow.ts.

import { ClientPackets } from "../protocol/client.ts";
import { libAsync } from "../game/libraries.ts";
import type { Magic, UserMagic } from "../game/magic.ts";
import { el } from "./screens.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matchesAny } from "../game/keybinds.ts";

const iconCache = new Map<number, Promise<string | null>>();
export function magicIcon(index: number): Promise<string | null> {
  let p = iconCache.get(index);
  if (!p) {
    p = (async () => {
      const lib = await libAsync("MagIcon2");
      const img = lib && (await lib.image(index));
      if (!img || !img.width || !img.height) return null;
      const c = document.createElement("canvas");
      c.width = img.width;
      c.height = img.height;
      c.getContext("2d")!.putImageData(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height), 0, 0);
      return c.toDataURL();
    })().catch(() => null);
    iconCache.set(index, p);
  }
  return p;
}

const keyName = (k: number) => (k <= 0 ? "-" : k <= 8 ? `F${k}` : `Ctrl+F${k - 8}`);

export class MagicBar {
  private readonly magic: Magic;
  private readonly send: (f: Uint8Array) => void;
  private readonly window = el("div", { class: "window magic-window", hidden: true });
  private readonly list = el("div", { class: "magic-list" });
  private readonly bar = el("div", { class: "skill-bar" });
  private readonly slots: { root: HTMLElement; img: HTMLImageElement; shade: HTMLElement; icon: number }[] = [];

  constructor(stage: HTMLElement, magic: Magic, send: (f: Uint8Array) => void) {
    this.magic = magic;
    this.send = send;
    this.window.append(el("div", { class: "window-title" }, el("span", {}, "Magic"), el("button", { class: "window-close", onclick: () => this.toggle(false), title: "Close (K)" }, "×")), this.list);
    for (const w of [this.window, this.bar]) w.addEventListener("mousedown", (e) => e.stopPropagation());
    for (let k = 1; k <= 8; k++) {
      const img = el("img", { alt: "" });
      const shade = el("div", { class: "skill-shade" });
      const root = el("div", { class: "skill-slot", title: `F${k}` }, img, shade, el("span", { class: "skill-key" }, `F${k}`));
      this.slots.push({ root, img, shade, icon: -1 });
      this.bar.append(root);
    }
    stage.append(this.window, this.bar);
    magic.onChange = () => this.render();
    window.addEventListener("keydown", (e) => {
      // KeybindOptions.Skills (F11) / Skills2 (S without Ctrl): CharacterDialog.SkillPage, which this window stands in for
      if (matchesAny(e, KeybindOptions.Skills, KeybindOptions.Skills2) && !(document.activeElement instanceof HTMLInputElement)) {
        if (e.key === "F11") e.preventDefault(); // the browser's full screen
        this.toggle();
      }
    }, { signal: gameSignal() });
    this.render();
  }

  toggle(show = this.window.hidden): void {
    this.window.hidden = !show;
    this.render();
  }

  /** AssignKeyPanel: one key per spell; the key is taken from whichever spell had it. */
  private assign(m: UserMagic, key: number): void {
    for (const other of this.magic.magics) if (other !== m && other.Key === key && key !== 0) other.Key = 0;
    this.send(ClientPackets.MagicKey({ Spell: m.Spell, Key: key, OldKey: m.Key }));
    m.Key = key;
    this.render();
  }

  render(): void {
    // Skill bar
    for (let k = 1; k <= 8; k++) {
      const slot = this.slots[k - 1]!;
      const m = this.magic.magics.find((x) => x.Key === k);
      const icon = m ? m.Icon * 2 : -1;
      slot.root.title = m ? `F${k}: ${m.Name} (Lv ${m.Level})` : `F${k}`;
      if (icon !== slot.icon) {
        slot.icon = icon;
        slot.img.removeAttribute("src");
        if (icon >= 0) void magicIcon(icon).then((url) => { if (url && slot.icon === icon) slot.img.src = url; });
      }
    }
    if (this.window.hidden) return;
    const rows = this.magic.magics.map((m) => {
      const img = el("img", { class: "magic-icon", alt: "" });
      void magicIcon(m.Icon * 2).then((url) => { if (url) img.src = url; });
      const need = [m.Need1, m.Need2, m.Need3][m.Level];
      const select = el("select", { title: "Key" }, ...[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16].map((k) => el("option", { value: String(k), selected: k === m.Key }, keyName(k))));
      select.onchange = () => this.assign(m, Number(select.value));
      return el("div", { class: "magic-row" }, img,
        el("div", { class: "magic-name" }, el("div", {}, m.Name), el("div", { class: "magic-meta" }, `Lv ${m.Level}${need ? `   Exp ${m.Experience}/${need}` : ""}   MP ${m.BaseCost + m.LevelCost * m.Level}`)),
        select);
    });
    this.list.replaceChildren(...(rows.length ? rows : [el("div", { class: "magic-meta" }, "You have not learned any magic yet.")]));
  }

  /** Per frame: shade slots still cooling down (MagicButton / SkillBar cooldown). */
  tick(now: number): void {
    for (let k = 1; k <= 8; k++) {
      const m = this.magic.magics.find((x) => x.Key === k);
      const slot = this.slots[k - 1]!;
      const left = m && Number.isFinite(m.castTime) && m.delay > 0 ? m.castTime + m.delay - now : 0;
      const frac = left > 0 ? Math.min(1, left / m!.delay) : 0;
      slot.shade.style.height = `${Math.round(frac * 100)}%`;
    }
  }
}
