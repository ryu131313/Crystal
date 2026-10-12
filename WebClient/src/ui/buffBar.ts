// Buff bar (BuffDialog): the user's buffs as icons along the top right, newest first, with the time left and a tooltip.

import { BuffTypeName } from "../protocol/enums.generated.ts";
import { libAsync } from "../game/libraries.ts";
import { buffIcon, buffText, type UserBuff } from "../game/buffs.ts";
import { el } from "./screens.ts";

const iconCache = new Map<string, Promise<string | null>>();
export function libIcon(lib: string, index: number): Promise<string | null> {
  const key = `${lib}#${index}`;
  let p = iconCache.get(key);
  if (!p) {
    p = (async () => {
      const l = await libAsync(lib);
      const img = l && (await l.image(index));
      if (!img || !img.width || !img.height) return null;
      const c = document.createElement("canvas");
      c.width = img.width;
      c.height = img.height;
      c.getContext("2d")!.putImageData(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height), 0, 0);
      return c.toDataURL();
    })().catch(() => null);
    iconCache.set(key, p);
  }
  return p;
}

export class BuffBar {
  private readonly root = el("div", { class: "buff-bar" });
  private readonly cells = new Map<number, { root: HTMLElement; time: HTMLElement }>();

  /** `className` adds a class (the hero's buff row, HeroBuffsDialog). */
  constructor(stage: HTMLElement, className?: string) {
    if (className) this.root.classList.add(className);
    stage.append(this.root);
  }

  /** Rebuilds the icon set (add/remove) and refreshes the timers; cheap to call every frame. */
  update(buffs: readonly UserBuff[], now: number): void {
    const live = new Set(buffs.map((b) => b.Type));
    for (const [type, cell] of this.cells) if (!live.has(type)) { cell.root.remove(); this.cells.delete(type); }
    for (let i = buffs.length - 1; i >= 0; i--) {
      const b = buffs[i]!;
      let cell = this.cells.get(b.Type);
      if (!cell) {
        const img = el("img", { alt: "" });
        const [lib, index] = buffIcon(b.Type);
        void libIcon(lib, index).then((url) => { if (url) img.src = url; });
        cell = { root: el("div", { class: "buff-cell" }, img), time: el("span", { class: "buff-time" }) };
        cell.root.append(cell.time);
        this.cells.set(b.Type, cell);
      }
      this.root.append(cell.root); // newest last in the list -> shown first (BuffDialog inserts at 0)
      const left = b.Infinite ? -1 : Math.max(0, b.Paused ? b.expire : b.expire - now);
      const s = Math.ceil(left / 1000);
      cell.time.textContent = left < 0 ? "" : s >= 3600 ? `${Math.floor(s / 3600)}h` : s >= 60 ? `${Math.floor(s / 60)}m` : `${s}s`;
      cell.root.title = buffText(b, BuffTypeName[b.Type] ?? `Buff ${b.Type}`, now);
      cell.root.classList.toggle("paused", b.Paused);
    }
  }
}
