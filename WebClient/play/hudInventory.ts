// Inventory window mock for the scene lab's UI tester, in the same window style as the Character window (hudWindow.ts).
// Crystal's bag: 40 cells (the 6 belt cells are the HUD's belt), shown 8 x 5 like the desktop InventoryDialog, then the
// weight bar and the gold / credit line. The bag is the shared one in hudItems.ts (Storage and Trade show it too); its
// cells drag and drop (hudDrag.ts) and right-click moves an item to the open trade or storage, else wears or belts it.
// Items and Image numbers from a copy of the live Server.MirDB (2026-10-15); mock data only, nothing is wired to a game.

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";
import { BAG_SLOTS, WEIGHT_MAX, bag, bagChanged, bagWeight, info, onBag } from "./hudItems.ts";
import { bestCell, bindCell, host, registerHost } from "./hudDrag.ts";

const CREDIT = 350;
/** ItemType -> the HUD belt (consumables) or the character's equipment, for right-click. */
const BELT_TYPES = new Set(["Potion", "Scroll", "Amulet"]);
const WEAR_TYPES = new Set(["Weapon", "Armour", "Helmet", "Torch", "Necklace", "Bracelet", "Ring", "Amulet", "Belt", "Boots", "Stone", "Mount"]);

const CSS = `
.hudm .iwin { width: 408px; }
.hudm .iwin .grid { display: grid; grid-template-columns: repeat(8, 44px); gap: 4px; padding: 12px 14px 10px; }
.hudm .iwin .foot { display: flex; flex-direction: column; gap: 9px; padding: 2px 14px 12px; }
.hudm .iwin .wt { display: flex; align-items: center; gap: 8px; font-size: 11px; }
.hudm .iwin .wt span { color: var(--dim); font-weight: 600; letter-spacing: .04em; }
.hudm .iwin .wt b { color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .iwin .wt .bar { flex: 1; height: 8px; border-radius: 4px; overflow: hidden; background: #04060a; border: 1px solid var(--fr); box-shadow: inset 0 1px 2px #000; }
.hudm .iwin .wt .bar i { display: block; height: 100%; border-radius: 3px;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .iwin .money { display: flex; align-items: center; gap: 16px; font-size: 12px; }
.hudm .iwin .money span { color: var(--dim); font-weight: 600; margin-right: 5px; }
.hudm .iwin .money b { color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .iwin .money .btn { margin-left: auto; }
`;

export class InventoryMock extends HudWindow {
  private readonly grid = h("div", "grid");
  private readonly fill = h("i");
  private readonly wtText = h("b");
  /** The HUD's gold (hudMock.ts calls setGold each frame; the text only changes when the amount does). */
  private readonly goldText = h("b", "", "0");
  private gold = -1;

  constructor() {
    super("iwin", null, 70); // opens 40 px from the right edge of the screen
    injectCss("hudm-inv-css", CSS);
    const wt = h("div", "wt", h("span", "", "Weight"), h("div", "bar", this.fill), this.wtText);
    const sort = h("div", "btn", "Sort");
    sort.title = "Sort by type, then name (stacks merge)";
    sort.addEventListener("click", () => this.sort());
    const money = h("div", "money", h("div", "", h("span", "", "Gold"), this.goldText),
      h("div", "", h("span", "", "Credit"), h("b", "", CREDIT.toLocaleString("en-GB"))), sort);
    this.el.append(h("div", "panel", this.titleBar("INVENTORY", "Close (I)"), this.grid, h("div", "foot", wt, money)));
    registerHost({
      id: "bag", size: BAG_SLOTS, live: () => true, // takes items back (unequip, unbelt, withdraw) even with the window shut
      get: (i) => bag[i] ?? null,
      set: (i, st) => { bag[i] = st; bagChanged(); },
      accepts: () => "",
      verb: (_i, from) => ({ equip: "Unequip", belt: "Off the belt", store: "Withdraw", trade: "Take back" } as Record<string, string>)[from.id] ?? "Move",
      quick: (i) => {
        const st = bag[i];
        if (!st) return null;
        const from = host("bag")!, type = info(st.name)?.type ?? "";
        return bestCell(host("trade"), st, from) ?? bestCell(host("store"), st, from)
          ?? (WEAR_TYPES.has(type) && type !== "Amulet" ? bestCell(host("equip"), st, from, true) : null)
          ?? (BELT_TYPES.has(type) ? bestCell(host("belt"), st, from) : null)
          ?? (type === "Amulet" ? bestCell(host("equip"), st, from, true) : null);
      },
    });
    onBag(() => this.draw());
    this.draw();
  }

  setGold(n: number): void {
    if (n === this.gold) return;
    this.gold = n;
    this.goldText.textContent = n.toLocaleString("en-GB");
  }

  private draw(): void {
    this.grid.replaceChildren(...bag.map((st, i) => {
      const c = st ? itemCell(this.el, st.count > 1 ? `${st.name} (${st.count})` : st.name, st.img, st.count) : itemCell(this.el, "", -1);
      bindCell(c, "bag", i);
      return c;
    }));
    const w = bagWeight();
    this.fill.style.width = Math.min(100, w / WEIGHT_MAX * 100).toFixed(1) + "%";
    this.wtText.textContent = `${w} / ${WEIGHT_MAX}`;
  }

  /** Sort: merge partial stacks, then order by item type and name, empty cells last. */
  private sort(): void {
    const order = ["Weapon", "Armour", "Helmet", "Necklace", "Bracelet", "Ring", "Belt", "Boots", "Stone", "Torch", "Mount", "Potion", "Scroll", "Amulet", "Gem", "Book", "CraftingMaterial", "Ore"];
    const rank = (n: string) => { const k = order.indexOf(info(n)?.type ?? ""); return k < 0 ? order.length : k; };
    const items = bag.filter((x): x is NonNullable<typeof x> => !!x).map((x) => ({ ...x }));
    const merged: typeof items = [];
    for (const x of items) {
      const max = info(x.name)?.stack ?? 1;
      for (const m of merged) if (x.count && m.name === x.name && m.count < max) { const k = Math.min(x.count, max - m.count); m.count += k; x.count -= k; }
      if (x.count) merged.push(x);
    }
    merged.sort((a, b) => rank(a.name) - rank(b.name) || a.name.localeCompare(b.name) || b.count - a.count);
    bag.splice(0, bag.length, ...merged, ...Array<null>(BAG_SLOTS - merged.length).fill(null));
    bagChanged();
  }
}
