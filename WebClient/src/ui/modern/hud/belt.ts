// Ported from play/hudMock.ts (beltSlot / showBelt / the 10-slot belt rows, ~790-794 and 957-1003): the HUD's belt, two
// rows of five under the HP / MP bars. Cells 1-8 are the player's belt (Inventory[0..BELT_IDX-1], keys
// KeybindOptions.Belt1..Belt8), cells 9 / 0 the hero's belt (the hero's Inventory[0..1], keys HeroBelt1 / HeroBelt2),
// with an H badge, greyed out while no hero is summoned (or it has fallen) and taking potions only (a client-side check;
// the server does not restrict the cell).
//
// The cells are the shared item cells (core/itemCells.ts): they draw themselves from the models, and the drag
// controller (core/drag.ts) gives them the desktop's gestures (click = pick / place, drag = move through the model's
// request, which locks the cell until the server answers; right-click = use). Keys are game.ts's (the gameplay layer of
// the input router); this file only shows their labels and re-reads them when the bindings change. The mock's local
// "use" (healing, a fake 1.5 s per-slot cooldown, wearing gear from the belt) is gone: Crystal's server decides.

import { BELT_IDX } from "../../../game/inventory.ts";
import { HERO_BELT_IDX } from "../../../game/hero.ts";
import { KeybindOptions, keyLabel, onKeyBindsChange, type KeybindOption } from "../../../game/keybinds.ts";
import { h, injectCss, setText } from "../core/dom.ts";
import { itemCells, potionsOnly, type ItemCell } from "../core/itemCells.ts";
import type { UiRuntime } from "../core/runtime.ts";

const CSS = `
.hudm .hm-belt { justify-content: space-between; }
.hudm .hm-belt .icell { --cs: 40px; }
.hudm .hm-belt .icell > kbd { color: var(--tx); font-size: 9px; }
.hudm .hm-belt .icell > .hb { position: absolute; left: 2px; bottom: 2px; z-index: 1; font-size: 8px; font-weight: 800; line-height: 1; padding: 1px 2px; border-radius: 2px;
  color: #000; background: var(--ac); pointer-events: none; }
.hudm .hm-belt .icell.off > .hb { background: var(--dim); }
.hudm.nokeys .hm-belt .icell > kbd { display: none; }
`;

/** A key label short enough for a 40 px slot: "Ctrl + F1" -> "^F1", "Shift + 3" -> "⇧3", "NumPad1" -> "N1". */
export function shortKey(label: string): string {
  return label.replace(/Ctrl \+ /g, "^").replace(/Shift \+ /g, "⇧").replace(/Alt \+ /g, "Alt").replace(/~ \+ /g, "~")
    .replace(/^NumPad/, "N").replace(/^D(\d)$/, "$1");
}

/** The ten belt cells: player 0-7, then hero 0-1. */
interface BeltCell { cell: ItemCell; kbd: HTMLElement | null; opt: KeybindOption }

export class Belt {
  /** Belt row of the left wing (player cells 1-5) and of the right wing (player 6-8, hero 9 / 0). */
  readonly left = h("div", "hm-hrow hm-belt");
  readonly right = h("div", "hm-hrow hm-belt");
  private readonly cells: BeltCell[] = [];

  constructor(rt: UiRuntime) {
    injectCss("hud-belt", CSS);
    const service = itemCells(rt); // built by the integrator (dragController) before the HUD
    const add = (row: HTMLElement, c: ItemCell, opt: KeybindOption) => {
      this.cells.push({ cell: c, kbd: c.el.querySelector(":scope > kbd"), opt });
      row.append(c.el);
    };
    for (let i = 0; i < BELT_IDX; i++) {
      const opt = (KeybindOptions.Belt1 + i * 2) as KeybindOption;
      add(i < 5 ? this.left : this.right, service.cell("inv", i, { size: 40, key: shortKey(keyLabel(opt)) || " " }), opt);
    }
    const heroOpts = [KeybindOptions.HeroBelt1, KeybindOptions.HeroBelt2] as const;
    for (let i = 0; i < HERO_BELT_IDX; i++) {
      const opt = heroOpts[i] ?? KeybindOptions.HeroBelt1;
      // Greyed out (and refusing drops) while no hero is summoned or it has fallen: itemCells' default for hero grids.
      const c = service.cell("heroInv", i, { size: 40, key: shortKey(keyLabel(opt)) || " ", filter: potionsOnly, cls: "hm-hero" });
      c.el.append(h("span", "hb", "H"));
      c.el.title = "Hero belt: potions only";
      add(this.right, c, opt);
    }
    const off = onKeyBindsChange(() => this.relabel());
    rt.signal.addEventListener("abort", off, { once: true });
  }

  /** Every belt cell, player first (layout / tests). */
  get all(): readonly ItemCell[] { return this.cells.map((c) => c.cell); }

  /** Key bindings changed (keybinds.ts): rewrite the key hints. */
  relabel(): void {
    for (const c of this.cells) if (c.kbd) setText(c.kbd, shortKey(keyLabel(c.opt)) || " ");
  }

  dispose(): void {
    for (const c of this.cells) c.cell.dispose();
    this.cells.length = 0;
  }
}
